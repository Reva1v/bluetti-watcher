//! Один экземпляр сервиса (FR-008): именованный мьютекс на сессию пользователя.

use windows::Win32::Foundation::{CloseHandle, ERROR_ALREADY_EXISTS, GetLastError, HANDLE};
use windows::Win32::System::Threading::CreateMutexW;
use windows::core::w;

pub struct InstanceGuard(HANDLE);

impl Drop for InstanceGuard {
    fn drop(&mut self) {
        // SAFETY: хэндл получен из CreateMutexW и закрывается один раз.
        unsafe {
            let _ = CloseHandle(self.0);
        }
    }
}

/// `None` — сервис уже запущен.
pub fn acquire() -> Option<InstanceGuard> {
    // SAFETY: имя — статическая широкая строка; хэндл хранится в guard.
    unsafe {
        let h = CreateMutexW(None, true, w!("Local\\BluettiWatcher")).ok()?;
        if GetLastError() == ERROR_ALREADY_EXISTS {
            let _ = CloseHandle(h);
            return None;
        }
        Some(InstanceGuard(h))
    }
}
