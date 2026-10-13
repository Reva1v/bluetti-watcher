//! Окно дашборда (`open`): WebView2 через wry/tao. Живёт только в этом процессе, сервис браузер не держит.

use std::io::{Read, Write};
use std::net::{SocketAddr, TcpStream};
use std::path::PathBuf;
use std::time::{Duration, Instant};

use tao::dpi::LogicalSize;
use tao::event::{Event, WindowEvent};
use tao::event_loop::ControlFlow;
use tao::window::WindowBuilder;
use wry::{WebContext, WebViewBuilder};

/// `GET /api/health` отвечает 200.
pub fn service_alive(port: u16, timeout: Duration) -> bool {
    let addr = SocketAddr::from(([127, 0, 0, 1], port));
    let Ok(mut s) = TcpStream::connect_timeout(&addr, timeout) else { return false };
    let _ = s.set_read_timeout(Some(timeout));
    let req = format!("GET /api/health HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nConnection: close\r\n\r\n");
    if s.write_all(req.as_bytes()).is_err() {
        return false;
    }
    let mut head = [0u8; 12];
    s.read_exact(&mut head).is_ok() && head.starts_with(b"HTTP/1.1 200")
}

/// Запустить сервис отдельным процессом без окна (если он уже идёт — второй экземпляр сразу выйдет).
pub fn spawn_service() -> std::io::Result<()> {
    use std::os::windows::process::CommandExt;
    const DETACHED_PROCESS: u32 = 0x0000_0008;
    const CREATE_NO_WINDOW: u32 = 0x0800_0000;
    let exe = std::env::current_exe()?;
    std::process::Command::new(exe)
        .arg("run")
        .creation_flags(DETACHED_PROCESS | CREATE_NO_WINDOW)
        .stdin(std::process::Stdio::null())
        .stdout(std::process::Stdio::null())
        .stderr(std::process::Stdio::null())
        .spawn()
        .map(|_| ())
}

/// Ждать сервис: 3 с как есть, потом запустить и ждать до 10 с (SC-002).
pub fn ensure_service(port: u16) -> bool {
    if service_alive(port, Duration::from_millis(500)) {
        return true;
    }
    let deadline = Instant::now() + Duration::from_secs(3);
    while Instant::now() < deadline {
        std::thread::sleep(Duration::from_millis(300));
        if service_alive(port, Duration::from_millis(500)) {
            return true;
        }
    }
    if let Err(e) = spawn_service() {
        tracing::error!("не удалось запустить сервис: {e}");
        return false;
    }
    let deadline = Instant::now() + Duration::from_secs(10);
    while Instant::now() < deadline {
        std::thread::sleep(Duration::from_millis(300));
        if service_alive(port, Duration::from_millis(500)) {
            return true;
        }
    }
    false
}

/// Команды верхней панели страницы (IPC из WebView2).
#[derive(Debug, Clone, Copy)]
enum UiEvent {
    Drag,
    Minimize,
    ToggleMaximize,
    Close,
}

/// Скруглённые углы Windows 11 у окна без системной рамки.
fn round_corners(hwnd: isize) {
    use windows::Win32::Foundation::HWND;
    use windows::Win32::Graphics::Dwm::{
        DWMWA_WINDOW_CORNER_PREFERENCE, DWMWCP_ROUND, DwmSetWindowAttribute,
    };
    let pref = DWMWCP_ROUND;
    // SAFETY: HWND живого окна tao, атрибут — 4-байтовое значение перечисления.
    unsafe {
        let _ = DwmSetWindowAttribute(
            HWND(hwnd as *mut _),
            DWMWA_WINDOW_CORNER_PREFERENCE,
            std::ptr::from_ref(&pref).cast(),
            std::mem::size_of_val(&pref) as u32,
        );
    }
}

/// Открыть окно и крутить цикл событий до закрытия. Не возвращается при успехе.
/// Системной рамки нет: верхняя панель — часть страницы, её кнопки приходят сюда через IPC.
pub fn open(port: u16, webview_data: PathBuf) -> Result<(), String> {
    use tao::event_loop::EventLoopBuilder;
    use tao::platform::windows::{WindowBuilderExtWindows, WindowExtWindows};

    let event_loop = EventLoopBuilder::<UiEvent>::with_user_event().build();
    let proxy = event_loop.create_proxy();
    let window = WindowBuilder::new()
        .with_title("Bluetti Watcher")
        .with_decorations(false)
        .with_undecorated_shadow(true)
        .with_inner_size(LogicalSize::new(1240.0, 860.0))
        .with_min_inner_size(LogicalSize::new(420.0, 480.0))
        .build(&event_loop)
        .map_err(|e| format!("окно: {e}"))?;
    round_corners(window.hwnd());
    let mut ctx = WebContext::new(Some(webview_data));
    let webview = WebViewBuilder::new_with_web_context(&mut ctx)
        .with_url(format!("http://127.0.0.1:{port}/"))
        .with_background_color((17, 17, 17, 255))
        .with_ipc_handler(move |req| {
            let ev = match req.body().as_str() {
                "drag" => UiEvent::Drag,
                "min" => UiEvent::Minimize,
                "max" => UiEvent::ToggleMaximize,
                "close" => UiEvent::Close,
                _ => return,
            };
            let _ = proxy.send_event(ev);
        })
        .build(&window)
        .map_err(|e| format!("WebView2: {e}"))?;
    let mut maximized = window.is_maximized();
    event_loop.run(move |event, _, control_flow| {
        *control_flow = ControlFlow::Wait;
        let _keep = &ctx;
        match event {
            Event::UserEvent(UiEvent::Drag) => {
                let _ = window.drag_window();
            }
            Event::UserEvent(UiEvent::Minimize) => window.set_minimized(true),
            Event::UserEvent(UiEvent::ToggleMaximize) => window.set_maximized(!window.is_maximized()),
            Event::UserEvent(UiEvent::Close)
            | Event::WindowEvent { event: WindowEvent::CloseRequested, .. } => {
                *control_flow = ControlFlow::Exit;
            }
            Event::WindowEvent { event: WindowEvent::Resized(_), .. } => {
                let now = window.is_maximized();
                if now != maximized {
                    maximized = now;
                    let _ = webview
                        .evaluate_script(&format!("window.__setMaximized && window.__setMaximized({now})"));
                }
            }
            _ => {}
        }
    })
}
