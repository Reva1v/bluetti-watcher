//! Сигнал о выключении Windows (feature 004). Приходит только окнам верхнего уровня, поэтому у сервиса есть скрытое
//! окно на своём потоке. Создаётся лишь при включённой автоматике режима зарядки (конституция III).

use std::sync::{OnceLock, mpsc};
use std::time::Duration;

use tokio::sync::mpsc::UnboundedSender;
use windows::Win32::Foundation::{HWND, LPARAM, LRESULT, WPARAM};
use windows::Win32::System::LibraryLoader::GetModuleHandleW;
use windows::Win32::System::Shutdown::{ShutdownBlockReasonCreate, ShutdownBlockReasonDestroy};
use windows::Win32::System::Threading::SetProcessShutdownParameters;
use windows::Win32::UI::WindowsAndMessaging::{
    CreateWindowExW, DefWindowProcW, DispatchMessageW, GetMessageW, MSG, RegisterClassW, WM_QUERYENDSESSION,
    WNDCLASSW, WS_OVERLAPPED,
};
use windows::core::{PCWSTR, w};

use crate::service::ShutdownRequest;

/// Дольше выключение не придерживаем (конституция III).
pub const HOLD: Duration = Duration::from_secs(15);
/// Сигнал среди первых приложений; выше этого уровня без прав администратора нельзя.
const SHUTDOWN_LEVEL: u32 = 0x3FF;
const CLASS: PCWSTR = w!("BluettiWatcherShutdown");

static REQUESTS: OnceLock<UnboundedSender<ShutdownRequest>> = OnceLock::new();

/// Запустить поток окна. Повторный вызов ничего не делает.
pub fn spawn(requests: UnboundedSender<ShutdownRequest>) {
    if REQUESTS.set(requests).is_err() {
        return;
    }
    if let Err(e) = std::thread::Builder::new().name("shutdown-window".into()).spawn(window_thread) {
        tracing::warn!("окно сигнала выключения не запущено: {e}");
    }
}

/// Выключение или перезагрузка; остальные значения — выход из сеанса, закрытие приложения установщиком,
/// критическое завершение.
fn is_power_off(lparam: LPARAM) -> bool {
    lparam.0 == 0
}

fn window_thread() {
    // SAFETY: параметры — константы; класс и окно живут до конца процесса, сообщения разбираются в этом же потоке.
    unsafe {
        if let Err(e) = SetProcessShutdownParameters(SHUTDOWN_LEVEL, 0) {
            tracing::warn!("приоритет сигнала выключения: {e}");
        }
        let instance = match GetModuleHandleW(None) {
            Ok(h) => h,
            Err(e) => {
                tracing::warn!("окно сигнала выключения: {e}");
                return;
            }
        };
        let class = WNDCLASSW {
            lpfnWndProc: Some(window_proc),
            hInstance: instance.into(),
            lpszClassName: CLASS,
            ..Default::default()
        };
        if RegisterClassW(&raw const class) == 0 {
            tracing::warn!("окно сигнала выключения: класс не зарегистрирован");
            return;
        }
        // Без WS_VISIBLE: окно никогда не показывается. Message-only окно сигнал не получило бы.
        if let Err(e) = CreateWindowExW(
            Default::default(),
            CLASS,
            w!("Bluetti Watcher"),
            WS_OVERLAPPED,
            0,
            0,
            0,
            0,
            None,
            None,
            Some(instance.into()),
            None,
        ) {
            tracing::warn!("окно сигнала выключения: {e}");
            return;
        }
        let mut msg = MSG::default();
        while GetMessageW(&raw mut msg, None, 0, 0).as_bool() {
            DispatchMessageW(&raw const msg);
        }
    }
}

unsafe extern "system" fn window_proc(hwnd: HWND, msg: u32, wparam: WPARAM, lparam: LPARAM) -> LRESULT {
    if msg == WM_QUERYENDSESSION {
        if is_power_off(lparam) {
            hold_for_write(hwnd);
        }
        // Выключению не препятствуем никогда.
        return LRESULT(1);
    }
    // SAFETY: аргументы получены от Windows без изменений.
    unsafe { DefWindowProcW(hwnd, msg, wparam, lparam) }
}

/// Подождать, пока сервис запишет режим. Причина видна на экране Windows, если выключение затянется.
fn hold_for_write(hwnd: HWND) {
    let Some(requests) = REQUESTS.get() else { return };
    let (done, wait) = mpsc::channel();
    if requests.send(ShutdownRequest { done }).is_err() {
        return;
    }
    // SAFETY: `hwnd` — живое окно этого потока, строка статическая.
    let held = unsafe {
        ShutdownBlockReasonCreate(hwnd, w!("Ставлю тихий режим зарядки станции"))
    };
    let _ = wait.recv_timeout(HOLD);
    if held.is_ok() {
        // SAFETY: то же окно.
        let _ = unsafe { ShutdownBlockReasonDestroy(hwnd) };
    }
}

#[cfg(test)]
mod tests {
    use windows::Win32::UI::WindowsAndMessaging::{FindWindowW, SendMessageW};

    use super::*;

    /// Настоящее окно и настоящее сообщение; завершение работы не начинается, потому что шлём его сами.
    #[test]
    fn window_asks_service_only_on_power_off() {
        const ENDSESSION_LOGOFF: isize = 0x8000_0000;
        let (tx, mut rx) = tokio::sync::mpsc::unbounded_channel::<ShutdownRequest>();
        spawn(tx);

        let mut hwnd = None;
        for _ in 0..100 {
            // SAFETY: имя класса — статическая строка.
            hwnd = unsafe { FindWindowW(CLASS, None) }.ok();
            if hwnd.is_some() {
                break;
            }
            std::thread::sleep(Duration::from_millis(20));
        }
        let hwnd = hwnd.expect("окно не появилось");
        let raw = hwnd.0 as isize;
        let send = move |lparam: isize| {
            // SAFETY: окно живёт до конца процесса.
            unsafe {
                SendMessageW(HWND(raw as *mut _), WM_QUERYENDSESSION, Some(WPARAM(0)), Some(LPARAM(lparam)))
            }
        };

        // Выход из сеанса: ответ сразу, сервис не спрашивают.
        assert_eq!(send(ENDSESSION_LOGOFF), LRESULT(1));
        assert!(rx.try_recv().is_err());

        // Выключение: окно ждёт, пока сервис ответит.
        let sender = std::thread::spawn(move || send(0));
        let req = rx.blocking_recv().expect("нет запроса от окна");
        req.done.send(()).unwrap();
        assert_eq!(sender.join().unwrap(), LRESULT(1));
    }
}
