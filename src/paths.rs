//! Каталоги и файлы программы.

use std::path::PathBuf;

pub struct Paths {
    pub data: PathBuf,
}

impl Paths {
    /// `%LOCALAPPDATA%\bluetti-watcher`; переменная `BLUETTI_WATCHER_HOME` подменяет его (тесты, отладка).
    pub fn from_env() -> Self {
        if let Some(home) = std::env::var_os("BLUETTI_WATCHER_HOME") {
            return Self { data: PathBuf::from(home) };
        }
        let base = std::env::var_os("LOCALAPPDATA").map(PathBuf::from).unwrap_or_else(|| PathBuf::from("."));
        Self { data: base.join("bluetti-watcher") }
    }

    pub fn bin_dir(&self) -> PathBuf {
        self.data.join("bin")
    }
    pub fn logs_dir(&self) -> PathBuf {
        self.data.join("logs")
    }
    pub fn config(&self) -> PathBuf {
        self.data.join("config.toml")
    }
    pub fn db(&self) -> PathBuf {
        self.data.join("station.db")
    }
    pub fn kill_switch(&self) -> PathBuf {
        self.data.join("no-hibernate")
    }

    /// `%ProgramData%\bluetti-watcher` — общий с элевированной задачей GPU.
    pub fn shared_dir() -> PathBuf {
        let base = std::env::var_os("ProgramData")
            .map(PathBuf::from)
            .unwrap_or_else(|| PathBuf::from(r"C:\ProgramData"));
        base.join("bluetti-watcher")
    }
    pub fn gpu_request() -> PathBuf {
        Self::shared_dir().join("gpu-request")
    }

    pub fn ensure(&self) -> std::io::Result<()> {
        std::fs::create_dir_all(&self.data)?;
        std::fs::create_dir_all(self.logs_dir())
    }
}
