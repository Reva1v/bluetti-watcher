//! Службы, которые мешают экономии: Windhawk (его мод не даёт выключить реплей NVIDIA) и контейнер NVIDIA,
//! который держит Instant Replay (NVENC пишет постоянно). Останавливаются в eco и запускаются после него.
//! Работает только в элевированной задаче `BluettiWatcherGpu` (`gpu-apply`).

use std::path::Path;
use std::time::{Duration, Instant};

use crate::model::EcoLevel;

/// Служба и процессы, которые остаются после её остановки. Порядок важен: сначала Windhawk, потом NVIDIA.
pub const MANAGED: [(&str, &[&str]); 2] = [
    ("Windhawk", &["windhawk.exe"]),
    ("NvContainerLocalSystem", &["NVIDIA Overlay.exe", "NVIDIA Share.exe"]),
];

/// Что сделать со службами для уровня; `stopped` — службы, которые программа остановила раньше.
/// Eco/Deep: остановить запущенные (по порядку `MANAGED`). Normal: запустить только остановленные нами (в обратном
/// порядке: NVIDIA, затем Windhawk, чтобы мод снова подхватил процесс).
pub fn plan(level: EcoLevel, running: &[&str], stopped: &[String]) -> Vec<(String, bool)> {
    match level {
        EcoLevel::None => MANAGED
            .iter()
            .rev()
            .filter(|(s, _)| stopped.iter().any(|x| x == s))
            .map(|(s, _)| ((*s).to_string(), true))
            .collect(),
        EcoLevel::Eco | EcoLevel::Deep => MANAGED
            .iter()
            .filter(|(s, _)| running.contains(s))
            .map(|(s, _)| ((*s).to_string(), false))
            .collect(),
    }
}

/// Список остановленных нами служб в `services-stopped` (по строке на службу).
pub fn read_stopped(file: &Path) -> Vec<String> {
    std::fs::read_to_string(file)
        .map(|t| t.lines().map(str::trim).filter(|l| !l.is_empty()).map(String::from).collect())
        .unwrap_or_default()
}

/// Применить уровень. Возвращает строки для лога; ошибка одной службы не мешает остальным.
#[cfg(windows)]
pub fn apply(level: EcoLevel, state_file: &Path) -> Vec<String> {
    let stopped_before = read_stopped(state_file);
    let running: Vec<&str> = MANAGED.iter().map(|(s, _)| *s).filter(|s| scm::is_running(s)).collect();
    let mut log = Vec::new();
    let mut stopped_now: Vec<String> =
        if level == EcoLevel::None { Vec::new() } else { stopped_before.clone() };
    for (name, start) in plan(level, &running, &stopped_before) {
        if start {
            match scm::start(&name) {
                Ok(()) => log.push(format!("служба {name} запущена")),
                Err(e) => log.push(format!("служба {name} не запущена: {e}")),
            }
        } else {
            match scm::stop(&name, Duration::from_secs(15)) {
                Ok(()) => {
                    let procs = MANAGED.iter().find(|(s, _)| *s == name).map(|(_, p)| *p).unwrap_or(&[]);
                    let killed = procs.iter().map(|p| scm::kill_by_name(p)).sum::<usize>();
                    log.push(format!("служба {name} остановлена (закрыто процессов: {killed})"));
                    if !stopped_now.contains(&name) {
                        stopped_now.push(name);
                    }
                }
                Err(e) => log.push(format!("служба {name} не остановлена: {e}")),
            }
        }
    }
    if stopped_now.is_empty() {
        let _ = std::fs::remove_file(state_file);
    } else {
        let _ = std::fs::write(state_file, stopped_now.join("\n"));
    }
    log
}

#[cfg(windows)]
mod scm {
    use super::*;
    use windows::Win32::Foundation::CloseHandle;
    use windows::Win32::System::Diagnostics::ToolHelp::{
        CreateToolhelp32Snapshot, PROCESSENTRY32W, Process32FirstW, Process32NextW, TH32CS_SNAPPROCESS,
    };
    use windows::Win32::System::Services::{
        CloseServiceHandle, ControlService, OpenSCManagerW, OpenServiceW, QueryServiceStatus, SC_HANDLE,
        SC_MANAGER_CONNECT, SERVICE_CONTROL_STOP, SERVICE_QUERY_STATUS, SERVICE_RUNNING, SERVICE_START,
        SERVICE_STATUS, SERVICE_STOP, SERVICE_STOPPED, StartServiceW,
    };
    use windows::Win32::System::Threading::{OpenProcess, PROCESS_TERMINATE, TerminateProcess};
    use windows::core::PCWSTR;

    struct H(SC_HANDLE);
    impl Drop for H {
        fn drop(&mut self) {
            // SAFETY: хэндл открыт OpenSCManager/OpenService и закрывается один раз.
            unsafe {
                let _ = CloseServiceHandle(self.0);
            }
        }
    }

    fn wide(s: &str) -> Vec<u16> {
        s.encode_utf16().chain(std::iter::once(0)).collect()
    }

    fn open(name: &str, access: u32) -> Result<(H, H), String> {
        // SAFETY: строки с нулём живут до конца вызова; хэндлы закрывает H.
        unsafe {
            let mgr = H(OpenSCManagerW(PCWSTR::null(), PCWSTR::null(), SC_MANAGER_CONNECT)
                .map_err(|e| e.message())?);
            let n = wide(name);
            let svc = H(OpenServiceW(mgr.0, PCWSTR(n.as_ptr()), access).map_err(|e| e.message())?);
            Ok((mgr, svc))
        }
    }

    fn state(svc: &H) -> Option<u32> {
        let mut st = SERVICE_STATUS::default();
        // SAFETY: хэндл открыт с SERVICE_QUERY_STATUS.
        unsafe { QueryServiceStatus(svc.0, &mut st).ok()? };
        Some(st.dwCurrentState.0)
    }

    pub fn is_running(name: &str) -> bool {
        open(name, SERVICE_QUERY_STATUS).ok().and_then(|(_m, s)| state(&s)) == Some(SERVICE_RUNNING.0)
    }

    pub fn stop(name: &str, wait: Duration) -> Result<(), String> {
        let (_m, svc) = open(name, SERVICE_STOP | SERVICE_QUERY_STATUS)?;
        let mut st = SERVICE_STATUS::default();
        // SAFETY: хэндл открыт с SERVICE_STOP.
        unsafe { ControlService(svc.0, SERVICE_CONTROL_STOP, &mut st) }.map_err(|e| e.message())?;
        let deadline = Instant::now() + wait;
        while Instant::now() < deadline {
            if state(&svc) == Some(SERVICE_STOPPED.0) {
                return Ok(());
            }
            std::thread::sleep(Duration::from_millis(300));
        }
        Err("не остановилась за 15 с".into())
    }

    pub fn start(name: &str) -> Result<(), String> {
        let (_m, svc) = open(name, SERVICE_START | SERVICE_QUERY_STATUS)?;
        if state(&svc) == Some(SERVICE_RUNNING.0) {
            return Ok(());
        }
        // SAFETY: хэндл открыт с SERVICE_START, аргументов нет.
        unsafe { StartServiceW(svc.0, None) }.map_err(|e| e.message())
    }

    /// Закрыть все процессы с этим именем exe; возвращает, сколько закрыто.
    pub fn kill_by_name(exe: &str) -> usize {
        let mut killed = 0;
        // SAFETY: снимок процессов закрывается в конце; PROCESSENTRY32W инициализирован с dwSize.
        unsafe {
            let Ok(snap) = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0) else { return 0 };
            let mut e = PROCESSENTRY32W {
                dwSize: std::mem::size_of::<PROCESSENTRY32W>() as u32,
                ..Default::default()
            };
            let mut ok = Process32FirstW(snap, &mut e).is_ok();
            while ok {
                let len = e.szExeFile.iter().position(|&c| c == 0).unwrap_or(e.szExeFile.len());
                let name = String::from_utf16_lossy(&e.szExeFile[..len]);
                if name.eq_ignore_ascii_case(exe)
                    && let Ok(h) = OpenProcess(PROCESS_TERMINATE, false, e.th32ProcessID)
                {
                    if TerminateProcess(h, 1).is_ok() {
                        killed += 1;
                    }
                    let _ = CloseHandle(h);
                }
                ok = Process32NextW(snap, &mut e).is_ok();
            }
            let _ = CloseHandle(snap);
        }
        killed
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn eco_stops_running_windhawk_first() {
        let p = plan(EcoLevel::Eco, &["NvContainerLocalSystem", "Windhawk"], &[]);
        assert_eq!(p, vec![("Windhawk".into(), false), ("NvContainerLocalSystem".into(), false)]);
        assert_eq!(
            plan(EcoLevel::Deep, &["NvContainerLocalSystem"], &[]),
            vec![("NvContainerLocalSystem".into(), false)]
        );
    }

    #[test]
    fn normal_starts_only_what_we_stopped_nvidia_first() {
        let stopped = vec!["Windhawk".to_string(), "NvContainerLocalSystem".to_string()];
        assert_eq!(
            plan(EcoLevel::None, &[], &stopped),
            vec![("NvContainerLocalSystem".into(), true), ("Windhawk".into(), true)]
        );
        assert!(plan(EcoLevel::None, &[], &[]).is_empty(), "не трогаем службы, которые остановил не я");
    }

    #[test]
    fn stopped_file_round_trip() {
        let f = std::env::temp_dir().join(format!("bw-svc-{}", std::process::id()));
        std::fs::write(&f, "Windhawk\n\nNvContainerLocalSystem\n").unwrap();
        assert_eq!(read_stopped(&f), vec!["Windhawk", "NvContainerLocalSystem"]);
        std::fs::remove_file(&f).unwrap();
        assert!(read_stopped(&f).is_empty());
    }
}
