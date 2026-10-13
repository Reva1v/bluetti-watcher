//! Exe собран без консоли; команды, которые печатают текст, подключаются к консоли родителя.

use windows::Win32::System::Console::{ATTACH_PARENT_PROCESS, AttachConsole};

/// true — консоль родителя есть, `println!` будет виден.
pub fn attach_parent() -> bool {
    // SAFETY: простой вызов WinAPI без указателей.
    unsafe { AttachConsole(ATTACH_PARENT_PROCESS).is_ok() }
}
