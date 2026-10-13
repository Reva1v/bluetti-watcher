//! Окно дашборда (`open`): WebView2 через wry/tao. Живёт только в этом процессе, сервис браузер не держит.

use std::io::{Read, Write};
use std::net::{SocketAddr, TcpStream};
use std::path::PathBuf;
use std::time::{Duration, Instant};

use tao::dpi::LogicalSize;
use tao::event::{Event, WindowEvent};
use tao::event_loop::{ControlFlow, EventLoop};
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

/// Открыть окно и крутить цикл событий до закрытия. Не возвращается при успехе.
pub fn open(port: u16, webview_data: PathBuf) -> Result<(), String> {
    let event_loop = EventLoop::new();
    let window = WindowBuilder::new()
        .with_title("Bluetti Watcher")
        .with_inner_size(LogicalSize::new(1200.0, 820.0))
        .with_min_inner_size(LogicalSize::new(420.0, 480.0))
        .build(&event_loop)
        .map_err(|e| format!("окно: {e}"))?;
    let mut ctx = WebContext::new(Some(webview_data));
    let webview = WebViewBuilder::new_with_web_context(&mut ctx)
        .with_url(format!("http://127.0.0.1:{port}/"))
        .with_background_color((17, 17, 17, 255))
        .build(&window)
        .map_err(|e| format!("WebView2: {e}"))?;
    event_loop.run(move |event, _, control_flow| {
        *control_flow = ControlFlow::Wait;
        let _keep = (&webview, &ctx);
        if let Event::WindowEvent { event: WindowEvent::CloseRequested, .. } = event {
            *control_flow = ControlFlow::Exit;
        }
    })
}
