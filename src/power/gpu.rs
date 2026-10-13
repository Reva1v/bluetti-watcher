//! Лимит мощности GPU NVIDIA (research.md R3). Сервис без прав пишет уровень в `gpu-request` и запускает
//! элевированную задачу `BluettiWatcherGpu`; та выполняет `gpu-apply`: останавливает/запускает службы реплея
//! (`services`) и ставит ватты через NVML.

use crate::config::Eco;
use crate::model::EcoLevel;
use crate::paths::Paths;

pub const TASK_NAME: &str = "BluettiWatcherGpu";

pub fn level_word(level: EcoLevel) -> &'static str {
    match level {
        EcoLevel::None => "normal",
        EcoLevel::Eco => "eco",
        EcoLevel::Deep => "deep",
    }
}

pub fn parse_level(s: &str) -> Option<EcoLevel> {
    match s.trim() {
        "normal" => Some(EcoLevel::None),
        "eco" => Some(EcoLevel::Eco),
        "deep" => Some(EcoLevel::Deep),
        _ => None,
    }
}

/// Лимит для уровня (единицы — как у `default`/`min`): normal → default; eco → max(min, default × gpu_pct %);
/// deep → min при `deep_gpu_pct = 0`, иначе max(min, default × pct %); всегда в `[min, default]`.
pub fn limit_for(level: EcoLevel, default: u32, min: u32, cfg: &Eco) -> u32 {
    let pct = |p: u8| (default as u64 * p as u64 / 100) as u32;
    let want = match level {
        EcoLevel::None => default,
        EcoLevel::Eco => pct(cfg.gpu_pct).max(min),
        EcoLevel::Deep if cfg.deep_gpu_pct == 0 => min,
        EcoLevel::Deep => pct(cfg.deep_gpu_pct).max(min),
    };
    want.clamp(min.min(default), default.max(min))
}

#[derive(Debug, thiserror::Error)]
pub enum GpuError {
    #[error("видеокарта NVIDIA не найдена")]
    NoNvidia,
    #[error("запрос gpu-request: {0}")]
    Request(String),
    #[error("NVML: {0}")]
    Nvml(String),
}

impl GpuError {
    /// Код выхода `gpu-apply`: 2 — нет NVIDIA, 1 — прочие ошибки.
    pub fn exit_code(&self) -> i32 {
        match self {
            Self::NoNvidia => 2,
            _ => 1,
        }
    }
}

/// Для элевированной команды `gpu-apply`: прочитать `gpu-request` и поставить лимит всем картам NVIDIA.
/// Возвращает установленные ватты.
pub fn apply_from_request(cfg: &Eco) -> Result<u32, GpuError> {
    use nvml_wrapper::Nvml;
    use nvml_wrapper::error::NvmlError;

    let path = Paths::gpu_request();
    let text =
        std::fs::read_to_string(&path).map_err(|e| GpuError::Request(format!("{}: {e}", path.display())))?;
    let level =
        parse_level(&text).ok_or_else(|| GpuError::Request(format!("неверный уровень «{}»", text.trim())))?;

    // Сначала службы (Windhawk, затем реплей NVIDIA): их не должна блокировать ошибка или отсутствие NVML.
    #[cfg(windows)]
    if cfg.stop_replay {
        let state = Paths::shared_dir().join("services-stopped");
        for line in super::services::apply(level, &state) {
            tracing::info!("{line}");
        }
    }
    if !cfg.gpu {
        return Ok(0);
    }

    let nvml = Nvml::init().map_err(|e| match e {
        NvmlError::LibloadingError(_) | NvmlError::LibraryNotFound | NvmlError::DriverNotLoaded => {
            GpuError::NoNvidia
        }
        e => GpuError::Nvml(e.to_string()),
    })?;
    let count = nvml.device_count().map_err(|e| GpuError::Nvml(e.to_string()))?;
    if count == 0 {
        return Err(GpuError::NoNvidia);
    }
    let mut watts = 0;
    for i in 0..count {
        let nv = |e: NvmlError| GpuError::Nvml(format!("карта {i}: {e}"));
        let mut dev = nvml.device_by_index(i).map_err(nv)?;
        let default = dev.power_management_limit_default().map_err(nv)?;
        let min = dev.power_management_limit_constraints().map_err(nv)?.min_limit;
        let mw = limit_for(level, default, min, cfg);
        dev.set_power_management_limit(mw).map_err(nv)?;
        tracing::info!("GPU {i}: лимит {} Вт (уровень {})", mw / 1000, level_word(level));
        watts = mw / 1000;
    }
    Ok(watts)
}

/// Записать уровень в `gpu-request` и запустить задачу `BluettiWatcherGpu`.
pub fn request(level: EcoLevel) -> Result<(), String> {
    let path = Paths::gpu_request();
    if let Some(dir) = path.parent() {
        std::fs::create_dir_all(dir).map_err(|e| format!("{}: {e}", dir.display()))?;
    }
    std::fs::write(&path, level_word(level)).map_err(|e| format!("{}: {e}", path.display()))?;
    task::run()
}

/// Есть ли задача `BluettiWatcherGpu` в планировщике.
pub fn task_installed() -> bool {
    task::installed()
}

#[cfg(windows)]
mod task {
    use windows::Win32::Foundation::RPC_E_CHANGED_MODE;
    use windows::Win32::System::Com::{
        CLSCTX_INPROC_SERVER, COINIT_MULTITHREADED, CoCreateInstance, CoInitializeEx, CoUninitialize,
    };
    use windows::Win32::System::TaskScheduler::{IRegisteredTask, ITaskService, TaskScheduler};
    use windows::Win32::System::Variant::VARIANT;
    use windows::core::BSTR;

    use super::TASK_NAME;

    struct Com(bool);
    impl Drop for Com {
        fn drop(&mut self) {
            if self.0 {
                unsafe { CoUninitialize() };
            }
        }
    }

    fn with_task<T>(f: impl FnOnce(IRegisteredTask) -> windows::core::Result<T>) -> Result<T, String> {
        let hr = unsafe { CoInitializeEx(None, COINIT_MULTITHREADED) };
        if hr.is_err() && hr != RPC_E_CHANGED_MODE {
            return Err(format!("COM: {}", windows::core::Error::from(hr).message()));
        }
        let _com = Com(hr.is_ok());
        let svc: ITaskService = unsafe { CoCreateInstance(&TaskScheduler, None, CLSCTX_INPROC_SERVER) }
            .map_err(|e| format!("планировщик заданий: {}", e.message()))?;
        let empty = VARIANT::default();
        unsafe { svc.Connect(&empty, &empty, &empty, &empty) }
            .map_err(|e| format!("планировщик заданий: {}", e.message()))?;
        let task =
            unsafe { svc.GetFolder(&BSTR::from("\\")).and_then(|f| f.GetTask(&BSTR::from(TASK_NAME))) }
                .map_err(|_| {
                    format!("задача {TASK_NAME} не установлена — запустите install --elevated-setup")
                })?;
        f(task).map_err(|e| format!("задача {TASK_NAME} не запущена: {}", e.message()))
    }

    pub fn installed() -> bool {
        with_task(|_| Ok(())).is_ok()
    }

    pub fn run() -> Result<(), String> {
        with_task(|t| unsafe { t.Run(&VARIANT::default()) }.map(|_| ()))
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::config::Config;

    fn eco(gpu_pct: u8, deep_gpu_pct: u8) -> Eco {
        Eco { gpu_pct, deep_gpu_pct, ..Config::default().eco }
    }

    #[test]
    fn rtx3060_defaults() {
        let c = eco(70, 0);
        assert_eq!(limit_for(EcoLevel::None, 170, 100, &c), 170);
        assert_eq!(limit_for(EcoLevel::Eco, 170, 100, &c), 119);
        assert_eq!(limit_for(EcoLevel::Deep, 170, 100, &c), 100);
    }

    #[test]
    fn percent_below_min_is_raised_to_min() {
        let c = eco(50, 35);
        assert_eq!(limit_for(EcoLevel::Eco, 170, 100, &c), 100);
        assert_eq!(limit_for(EcoLevel::Deep, 170, 100, &c), 100);
        assert_eq!(limit_for(EcoLevel::Deep, 170_000, 100_000, &eco(70, 80)), 136_000);
    }

    #[test]
    fn always_within_min_and_default() {
        for p in [0u8, 1, 50, 99, 100] {
            for lvl in [EcoLevel::None, EcoLevel::Eco, EcoLevel::Deep] {
                let w = limit_for(lvl, 170_000, 100_000, &eco(p, p));
                assert!((100_000..=170_000).contains(&w), "{lvl:?} {p}% → {w}");
            }
        }
        // Странные лимиты карты не роняют clamp.
        assert_eq!(limit_for(EcoLevel::None, 90, 100, &eco(70, 0)), 90);
    }

    #[test]
    fn request_words_roundtrip() {
        for lvl in [EcoLevel::None, EcoLevel::Eco, EcoLevel::Deep] {
            assert_eq!(parse_level(&format!("{}\r\n", level_word(lvl))), Some(lvl));
        }
        assert_eq!(parse_level("max"), None);
    }
}
