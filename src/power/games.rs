//! Запущена ли игра (feature 005, FR-016/017): пока да, основной монитор не затемняется. Steam сообщает о запущенной
//! игре в `HKCU\Software\Valve\Steam\RunningAppID` (0 — игры нет); игры не из Steam — по именам exe из
//! `[eco] game_processes`. Выбор по имени — чистая `matches`; чтение системы — только `WindowsGames`.

use crate::config::Eco;

pub trait Games {
    /// Запущена ли игра сейчас; ошибка чтения — `false`.
    fn running(&mut self) -> bool;
}

fn normal(name: &str) -> String {
    let n = name.trim().to_lowercase();
    n.strip_suffix(".exe").map_or(n.clone(), str::to_string)
}

/// Есть ли среди запущенных процессов (`running`) один из заданных в конфиге (`names`): без учёта регистра и
/// расширения `.exe`. Пустой список — никогда.
pub fn matches(names: &[String], running: &[String]) -> bool {
    let want: Vec<String> = names.iter().map(|n| normal(n)).filter(|n| !n.is_empty()).collect();
    !want.is_empty() && running.iter().any(|p| want.contains(&normal(p)))
}

/// Определение игр по настройкам `[eco]`.
pub struct WindowsGames {
    /// `[eco] dim_skip_games`: выключено — игра никогда не считается запущенной.
    skip: bool,
    processes: Vec<String>,
}

impl WindowsGames {
    pub fn new(cfg: &Eco) -> Self {
        Self { skip: cfg.dim_skip_games, processes: cfg.game_processes.clone() }
    }
}

impl Games for WindowsGames {
    fn running(&mut self) -> bool {
        if !self.skip {
            return false;
        }
        #[cfg(windows)]
        {
            sys::steam_app_id().is_some_and(|id| id != 0)
                // Снимок процессов — только если в конфиге есть имена.
                || (!self.processes.is_empty() && matches(&self.processes, &sys::process_names()))
        }
        #[cfg(not(windows))]
        false
    }
}

#[cfg(windows)]
mod sys {
    use windows::Win32::Foundation::{CloseHandle, ERROR_SUCCESS};
    use windows::Win32::System::Diagnostics::ToolHelp::{
        CreateToolhelp32Snapshot, PROCESSENTRY32W, Process32FirstW, Process32NextW, TH32CS_SNAPPROCESS,
    };
    use windows::Win32::System::Registry::{HKEY_CURRENT_USER, RRF_RT_REG_DWORD, RegGetValueW};
    use windows::core::w;

    /// `RunningAppID` Steam; `None` — ключа нет или он не читается.
    pub fn steam_app_id() -> Option<u32> {
        let mut data = 0u32;
        let mut size = std::mem::size_of::<u32>() as u32;
        // SAFETY: буфер — одно `u32`, `size` равен его длине; строки — литералы с нулём.
        let err = unsafe {
            RegGetValueW(
                HKEY_CURRENT_USER,
                w!("Software\\Valve\\Steam"),
                w!("RunningAppID"),
                RRF_RT_REG_DWORD,
                None,
                Some(&mut data as *mut u32 as *mut core::ffi::c_void),
                Some(&mut size),
            )
        };
        (err == ERROR_SUCCESS).then_some(data)
    }

    /// Имена exe всех запущенных процессов.
    pub fn process_names() -> Vec<String> {
        let mut names = Vec::new();
        // SAFETY: снимок закрывается в конце; `PROCESSENTRY32W` инициализирован с `dwSize`.
        unsafe {
            let Ok(snap) = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0) else { return names };
            let mut e = PROCESSENTRY32W {
                dwSize: std::mem::size_of::<PROCESSENTRY32W>() as u32,
                ..Default::default()
            };
            let mut ok = Process32FirstW(snap, &mut e).is_ok();
            while ok {
                let len = e.szExeFile.iter().position(|&c| c == 0).unwrap_or(e.szExeFile.len());
                names.push(String::from_utf16_lossy(&e.szExeFile[..len]));
                ok = Process32NextW(snap, &mut e).is_ok();
            }
            let _ = CloseHandle(snap);
        }
        names
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::config::Config;

    fn v(items: &[&str]) -> Vec<String> {
        items.iter().map(|s| s.to_string()).collect()
    }

    #[test]
    fn matching_ignores_case_and_exe_extension() {
        let running = v(&["explorer.exe", "EldenRing.EXE", "chrome.exe"]);
        assert!(matches(&v(&["eldenring.exe"]), &running), "регистр");
        assert!(matches(&v(&["ELDENRING.EXE"]), &running));
        assert!(matches(&v(&["eldenring"]), &running), "в конфиге без .exe");
        assert!(matches(&v(&["eldenring.exe"]), &v(&["eldenring"])), "в списке процессов без .exe");
        assert!(matches(&v(&["  eldenring.exe "]), &running), "пробелы по краям");
        assert!(matches(&v(&["other.exe", "chrome.exe"]), &running), "достаточно одного");
    }

    #[test]
    fn matching_needs_the_whole_name_and_a_non_empty_list() {
        let running = v(&["eldenring.exe", "game.exe.bak"]);
        assert!(!matches(&[], &running), "пустой список — игры нет");
        assert!(!matches(&v(&["elden"]), &running), "часть имени не подходит");
        assert!(!matches(&v(&["ring.exe"]), &running));
        assert!(!matches(&v(&["game.exe"]), &running), "другое расширение");
        assert!(!matches(&v(&[""]), &v(&[""])), "пустое имя не совпадает с пустым");
        assert!(!matches(&v(&[".exe"]), &v(&[".exe"])));
        assert!(!matches(&v(&["eldenring.exe"]), &[]));
    }

    #[test]
    fn disabled_detection_never_reports_a_game() {
        // Определение выключено: ни реестр, ни процессы не читаются, ответ всегда «нет».
        let cfg =
            Eco { dim_skip_games: false, game_processes: v(&["explorer.exe"]), ..Config::default().eco };
        assert!(!WindowsGames::new(&cfg).running());
    }
}
