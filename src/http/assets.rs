//! Встроенный дашборд: HTML, JS, CSS и uPlot 1.6.32 (MIT) зашиты в бинарь, внешних запросов нет.

use axum::Router;
use axum::http::header;
use axum::response::IntoResponse;
use axum::routing::get;

const INDEX_HTML: &str = include_str!("web/index.html");
const APP_JS: &str = include_str!("web/app.js");
const STYLE_CSS: &str = include_str!("web/style.css");
const UPLOT_JS: &[u8] = include_bytes!("web/uplot.min.js");
const UPLOT_CSS: &[u8] = include_bytes!("web/uplot.min.css");

const HTML: &str = "text/html; charset=utf-8";
const JS: &str = "text/javascript; charset=utf-8";
const CSS: &str = "text/css; charset=utf-8";

fn asset(content_type: &'static str, body: &'static [u8]) -> impl IntoResponse {
    ([(header::CONTENT_TYPE, content_type), (header::CACHE_CONTROL, "no-cache")], body)
}

/// Маршруты статики; состояние и охрана `Host` навешиваются в `super::router`.
pub fn routes<S: Clone + Send + Sync + 'static>() -> Router<S> {
    Router::new()
        .route("/", get(|| async { asset(HTML, INDEX_HTML.as_bytes()) }))
        .route("/app.js", get(|| async { asset(JS, APP_JS.as_bytes()) }))
        .route("/style.css", get(|| async { asset(CSS, STYLE_CSS.as_bytes()) }))
        .route("/uplot.min.js", get(|| async { asset(JS, UPLOT_JS) }))
        .route("/uplot.min.css", get(|| async { asset(CSS, UPLOT_CSS) }))
}
