//! Один экземпляр сервиса (FR-008): именованный мьютекс на сессию пользователя.

use std::path::Path;

use windows::Win32::Foundation::{CloseHandle, ERROR_ALREADY_EXISTS, GetLastError, HANDLE};
use windows::Win32::System::Threading::CreateMutexW;
use windows::core::PCWSTR;

pub struct InstanceGuard(HANDLE);

impl Drop for InstanceGuard {
    fn drop(&mut self) {
        // SAFETY: хэндл получен из CreateMutexW и закрывается один раз.
        unsafe {
            let _ = CloseHandle(self.0);
        }
    }
}

/// Имя мьютекса: каталог данных по умолчанию — `Local\BluettiWatcher`; другой (`BLUETTI_WATCHER_HOME`) — со своим
/// суффиксом, чтобы тестовый экземпляр не конфликтовал с установленным.
pub fn mutex_name(data_dir: &Path, default_dir: &Path) -> String {
    if data_dir == default_dir {
        return "Local\\BluettiWatcher".into();
    }
    // FNV-1a от пути в нижнем регистре.
    let mut h: u64 = 0xcbf2_9ce4_8422_2325;
    for b in data_dir.to_string_lossy().to_lowercase().bytes() {
        h = (h ^ u64::from(b)).wrapping_mul(0x0100_0000_01b3);
    }
    format!("Local\\BluettiWatcher-{h:016x}")
}

/// `None` — сервис с этим каталогом данных уже запущен.
pub fn acquire(name: &str) -> Option<InstanceGuard> {
    let wide: Vec<u16> = name.encode_utf16().chain(std::iter::once(0)).collect();
    // SAFETY: строка заканчивается нулём и живёт до конца вызова; хэндл хранится в guard.
    unsafe {
        let h = CreateMutexW(None, true, PCWSTR(wide.as_ptr())).ok()?;
        if GetLastError() == ERROR_ALREADY_EXISTS {
            let _ = CloseHandle(h);
            return None;
        }
        Some(InstanceGuard(h))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn default_dir_keeps_the_plain_name() {
        let d = Path::new("C:/Users/x/AppData/Local/bluetti-watcher");
        assert_eq!(mutex_name(d, d), "Local\\BluettiWatcher");
        let other = mutex_name(Path::new("C:/tmp/demo"), d);
        assert!(other.starts_with("Local\\BluettiWatcher-"));
        assert_eq!(other.len(), "Local\\BluettiWatcher-".len() + 16);
    }
}
