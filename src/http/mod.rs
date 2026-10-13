//! Локальный HTTP API и дашборд (contracts/http-api.md). Только `127.0.0.1:<port>`, без лишних потоков:
//! работает на однопоточном рантайме сервиса, SQLite читается своим соединением только для чтения (WAL).

use std::convert::Infallible;
use std::net::{Ipv4Addr, SocketAddr};
use std::path::PathBuf;
use std::sync::{Arc, Mutex};
use std::time::Duration;

use axum::Router;
use axum::body::Bytes;
use axum::extract::{Query, Request, State};
use axum::http::{HeaderMap, StatusCode, header};
use axum::middleware::{self, Next};
use axum::response::sse::{self, KeepAlive, Sse};
use axum::response::{IntoResponse, Json, Response};
use axum::routing::{get, post};
use futures_util::Stream;
use serde::{Deserialize, Serialize};
use tokio::sync::{broadcast, mpsc, watch};

use crate::clock::{Clock, MIN, SystemClock, Ts, to_local_iso};
use crate::model::{EcoLevel, Mains, ManualMode};
use crate::runtime::Source;
use crate::store::{self, Range, Store};

mod assets;

/// Заголовок, без которого `POST /api/mode` отвергается (форма с чужого сайта его не пошлёт).
pub const MODE_HEADER: &str = "x-bluetti-watcher";

/// Тело `/api/now` и `event: now`.
#[derive(Debug, Clone, Default, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct NowView {
    pub ts: Option<String>,
    pub stale: bool,
    pub soc: Option<u8>,
    pub ac_in_w: Option<u32>,
    pub ac_out_w: Option<u32>,
    pub dc_in_w: Option<u32>,
    pub dc_out_w: Option<u32>,
    pub ac_in_v: Option<f32>,
    pub mains: Mains,
    pub outage: Option<OutageView>,
    pub eco_at: Option<String>,
    pub eco: EcoLevel,
    pub manual: ManualMode,
    pub hours_left: Option<f64>,
    pub runtime_source: Option<Source>,
    pub levels: LevelsView,
    pub hibernate_delayed_until: Option<String>,
    pub hibernate_available: bool,
    pub kill_switch: bool,
    pub dry_run: bool,
    pub last_error: Option<ErrorView>,
}

#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct OutageView {
    pub since: String,
    pub soc_start: Option<u8>,
}

#[derive(Debug, Clone, Default, PartialEq, Serialize)]
pub struct LevelsView {
    pub low: bool,
    pub critical: bool,
    pub warn: bool,
}

#[derive(Debug, Clone, PartialEq, Serialize)]
pub struct ErrorView {
    pub ts: String,
    pub text: String,
}

/// Тело `/api/health`. `lastReading` и `readOk24h` обработчик берёт из БД, остальное — от сервиса.
#[derive(Debug, Clone, Default, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct HealthView {
    pub ok: bool,
    pub version: String,
    pub last_reading: Option<String>,
    pub encryption: Option<bool>,
    pub read_ok_24h: Option<f64>,
}

#[derive(Clone)]
pub struct AppState {
    pub port: u16,
    pub db_path: PathBuf,
    /// Сервис публикует после каждого цикла.
    pub now: watch::Receiver<NowView>,
    /// Сервис шлёт сюда новые события.
    pub events: broadcast::Sender<store::Event>,
    pub mode_tx: mpsc::UnboundedSender<ManualMode>,
    pub health: watch::Receiver<HealthView>,
}

struct Shared {
    app: AppState,
    hosts: [String; 2],
    /// Одно соединение на чтение, открывается при первом запросе и сбрасывается при ошибке.
    db: Mutex<Option<Store>>,
}

type St = State<Arc<Shared>>;
type DbError = (StatusCode, &'static str);

impl Shared {
    fn with_store<T>(&self, f: impl FnOnce(&Store) -> rusqlite::Result<T>) -> Result<T, DbError> {
        let mut g = self.db.lock().unwrap_or_else(|p| p.into_inner());
        if g.is_none() {
            match Store::open_read(&self.app.db_path) {
                Ok(s) => *g = Some(s),
                Err(e) => {
                    tracing::warn!("http: БД не открывается: {e}");
                    return Err((StatusCode::SERVICE_UNAVAILABLE, "база недоступна"));
                }
            }
        }
        let res = f(g.as_ref().expect("store"));
        res.map_err(|e| {
            tracing::warn!("http: ошибка запроса к БД: {e}");
            *g = None;
            (StatusCode::INTERNAL_SERVER_ERROR, "ошибка базы")
        })
    }
}

pub fn router(state: AppState) -> Router {
    let port = state.port;
    let shared = Arc::new(Shared {
        app: state,
        hosts: [format!("127.0.0.1:{port}"), format!("localhost:{port}")],
        db: Mutex::new(None),
    });
    Router::new()
        .merge(assets::routes())
        .route("/api/now", get(now))
        .route("/api/history", get(history))
        .route("/api/outages", get(outages))
        .route("/api/events", get(events))
        .route("/api/stream", get(stream))
        .route("/api/mode", post(mode))
        .route("/api/health", get(health))
        .layer(middleware::from_fn_with_state(shared.clone(), host_guard))
        .with_state(shared)
}

/// Слушает только `127.0.0.1:port`.
pub async fn serve(state: AppState) -> std::io::Result<()> {
    let addr = SocketAddr::from((Ipv4Addr::LOCALHOST, state.port));
    let listener = tokio::net::TcpListener::bind(addr).await?;
    axum::serve(listener, router(state)).await
}

/// Защита от DNS rebinding: `Host` только `127.0.0.1:<port>` или `localhost:<port>`.
async fn host_guard(State(s): St, req: Request, next: Next) -> Response {
    let ok = req
        .headers()
        .get(header::HOST)
        .and_then(|h| h.to_str().ok())
        .is_some_and(|h| s.hosts.iter().any(|a| a.eq_ignore_ascii_case(h)));
    if ok { next.run(req).await } else { StatusCode::FORBIDDEN.into_response() }
}

fn now_ms() -> Ts {
    SystemClock.now()
}

async fn now(State(s): St) -> Json<NowView> {
    Json(s.app.now.borrow().clone())
}

#[derive(Deserialize)]
struct RangeQuery {
    range: Option<String>,
}

async fn history(State(s): St, Query(q): Query<RangeQuery>) -> Response {
    let Some(range) = q.range.as_deref().and_then(Range::parse) else {
        return (StatusCode::BAD_REQUEST, "range: 6h | 24h | 7d | 30d").into_response();
    };
    match s.with_store(|st| st.history(range, now_ms())) {
        Ok(h) => Json(h).into_response(),
        Err(r) => r.into_response(),
    }
}

#[derive(Deserialize)]
struct LimitQuery {
    limit: Option<u32>,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct OutageItem {
    start: String,
    end: Option<String>,
    duration_min: i64,
    soc_start: Option<u8>,
    soc_end: Option<u8>,
    eco: bool,
    hibernated: bool,
}

async fn outages(State(s): St, Query(q): Query<LimitQuery>) -> Response {
    let limit = q.limit.unwrap_or(50).min(1000);
    let now = now_ms();
    match s.with_store(|st| st.outages(limit)) {
        Ok(v) => Json(
            v.into_iter()
                .map(|o| OutageItem {
                    start: to_local_iso(o.start_ts),
                    end: o.end_ts.map(to_local_iso),
                    duration_min: (o.end_ts.unwrap_or(now) - o.start_ts).max(0) / MIN,
                    soc_start: o.soc_start,
                    soc_end: o.soc_end,
                    eco: o.eco,
                    hibernated: o.hibernated,
                })
                .collect::<Vec<_>>(),
        )
        .into_response(),
        Err(r) => r.into_response(),
    }
}

#[derive(Debug, Serialize)]
struct EventView {
    ts: String,
    kind: String,
    text: String,
}

impl From<store::Event> for EventView {
    fn from(e: store::Event) -> Self {
        Self { ts: to_local_iso(e.ts), kind: e.kind, text: e.text }
    }
}

async fn events(State(s): St, Query(q): Query<LimitQuery>) -> Response {
    let limit = q.limit.unwrap_or(100).min(1000);
    match s.with_store(|st| st.recent_events(limit)) {
        Ok(v) => Json(v.into_iter().map(EventView::from).collect::<Vec<_>>()).into_response(),
        Err(r) => r.into_response(),
    }
}

/// SSE: текущее состояние сразу, затем `event: now` на каждое обновление и `event: event` на каждое событие.
async fn stream(State(s): St) -> Sse<impl Stream<Item = Result<sse::Event, Infallible>>> {
    let mut now_rx = s.app.now.clone();
    now_rx.mark_changed();
    let ev_rx = s.app.events.subscribe();
    let st = futures_util::stream::unfold((now_rx, ev_rx), |(mut now_rx, mut ev_rx)| async move {
        loop {
            tokio::select! {
                r = now_rx.changed() => {
                    r.ok()?;
                    let v = now_rx.borrow_and_update().clone();
                    let e = sse::Event::default().event("now").json_data(v).ok()?;
                    return Some((Ok(e), (now_rx, ev_rx)));
                }
                r = ev_rx.recv() => match r {
                    Ok(ev) => {
                        let e = sse::Event::default().event("event").json_data(EventView::from(ev)).ok()?;
                        return Some((Ok(e), (now_rx, ev_rx)));
                    }
                    Err(broadcast::error::RecvError::Lagged(_)) => continue,
                    Err(broadcast::error::RecvError::Closed) => return None,
                },
            }
        }
    });
    Sse::new(st).keep_alive(KeepAlive::new().interval(Duration::from_secs(30)))
}

#[derive(Deserialize)]
struct ModeBody {
    mode: ManualMode,
}

#[derive(Serialize)]
struct ModeReply {
    manual: ManualMode,
}

async fn mode(State(s): St, headers: HeaderMap, body: Bytes) -> Response {
    if headers.get(MODE_HEADER).is_none_or(|v| v.as_bytes() != b"1") {
        return StatusCode::FORBIDDEN.into_response();
    }
    let Ok(ModeBody { mode }) = serde_json::from_slice::<ModeBody>(&body) else {
        return (StatusCode::BAD_REQUEST, "mode: auto | eco | max").into_response();
    };
    if s.app.mode_tx.send(mode).is_err() {
        return (StatusCode::SERVICE_UNAVAILABLE, "сервис не принимает команды").into_response();
    }
    (StatusCode::ACCEPTED, Json(ModeReply { manual: mode })).into_response()
}

async fn health(State(s): St) -> Json<HealthView> {
    let mut h = s.app.health.borrow().clone();
    let now = now_ms();
    if let Ok((last, ratio)) =
        s.with_store(|st| Ok((st.last_accepted()?, st.read_ok_ratio(now - 24 * 60 * MIN, now)?)))
    {
        h.last_reading = last.map(|r| to_local_iso(r.ts));
        h.read_ok_24h = ratio.map(|x| (x * 1000.0).round() / 1000.0);
    }
    Json(h)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::model::{ReadStatus, Reading};
    use axum::body::{Body, to_bytes};
    use axum::http::{Method, Request};
    use serde_json::{Value, json};
    use tower::ServiceExt;

    const PORT: u16 = 8765;

    struct Env {
        app: Router,
        mode_rx: mpsc::UnboundedReceiver<ManualMode>,
        now_tx: watch::Sender<NowView>,
        events: broadcast::Sender<store::Event>,
        _health_tx: watch::Sender<HealthView>,
        dir: PathBuf,
    }

    impl Drop for Env {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.dir);
        }
    }

    /// Временная БД с показаниями, отключениями и событиями вокруг текущего времени.
    fn env(name: &str) -> Env {
        let dir = std::env::temp_dir().join(format!("bw-http-{}-{name}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();
        let db_path = dir.join("station.db");
        let mut s = Store::open(&db_path).unwrap();
        let now = now_ms();
        let rows: Vec<_> = (0..20)
            .map(|i| {
                let r = Reading {
                    ts: now - (20 - i) * MIN,
                    soc: Some(80),
                    ac_in_w: Some(0),
                    ac_out_w: Some(180),
                    dc_in_w: Some(0),
                    dc_out_w: Some(4),
                    ac_in_v: Some(0.0),
                };
                (r, if i < 2 { ReadStatus::Miss } else { ReadStatus::Accepted })
            })
            .collect();
        s.insert_many(&rows).unwrap();
        s.start_outage(now - 3 * 60 * MIN, Some(100)).unwrap();
        s.end_outage(now - 2 * 60 * MIN, Some(90)).unwrap();
        s.start_outage(now - 30 * MIN, Some(85)).unwrap();
        s.insert_event(now - 2 * MIN, "mains-lost", "Сеть пропала", None).unwrap();
        s.insert_event(now - MIN, "eco-on", "Eco включён", None).unwrap();
        drop(s);

        let (now_tx, now_rx) = watch::channel(NowView { soc: Some(80), dry_run: true, ..Default::default() });
        let (health_tx, health_rx) =
            watch::channel(HealthView { ok: true, version: "0.1.0".into(), ..Default::default() });
        let (events, _) = broadcast::channel(16);
        let (mode_tx, mode_rx) = mpsc::unbounded_channel();
        let app = router(AppState {
            port: PORT,
            db_path,
            now: now_rx,
            events: events.clone(),
            mode_tx,
            health: health_rx,
        });
        Env { app, mode_rx, now_tx, events, _health_tx: health_tx, dir }
    }

    fn req(method: Method, uri: &str, host: Option<&str>) -> axum::http::request::Builder {
        let b = Request::builder().method(method).uri(uri);
        match host {
            Some(h) => b.header(header::HOST, h),
            None => b,
        }
    }

    async fn call(app: &Router, r: Request<Body>) -> (StatusCode, Value) {
        let resp = app.clone().oneshot(r).await.unwrap();
        let status = resp.status();
        let b = to_bytes(resp.into_body(), 1 << 20).await.unwrap();
        (status, serde_json::from_slice(&b).unwrap_or(Value::Null))
    }

    async fn get_json(app: &Router, uri: &str) -> (StatusCode, Value) {
        call(app, req(Method::GET, uri, Some("127.0.0.1:8765")).body(Body::empty()).unwrap()).await
    }

    #[tokio::test]
    async fn host_guard_allows_only_loopback_names() {
        let e = env("host");
        for (host, want) in [
            (Some("127.0.0.1:8765"), StatusCode::OK),
            (Some("localhost:8765"), StatusCode::OK),
            (Some("evil.example:8765"), StatusCode::FORBIDDEN),
            (Some("127.0.0.1:9999"), StatusCode::FORBIDDEN),
            (Some("localhost"), StatusCode::FORBIDDEN),
            (None, StatusCode::FORBIDDEN),
        ] {
            let r = req(Method::GET, "/api/now", host).body(Body::empty()).unwrap();
            assert_eq!(e.app.clone().oneshot(r).await.unwrap().status(), want, "{host:?}");
        }
    }

    #[tokio::test]
    async fn assets_have_types_and_no_cache() {
        let e = env("assets");
        for (uri, ctype, needle) in [
            ("/", "text/html; charset=utf-8", "/app.js"),
            ("/app.js", "text/javascript; charset=utf-8", "/api/stream"),
            ("/style.css", "text/css; charset=utf-8", "--accent"),
            ("/uplot.min.js", "text/javascript; charset=utf-8", "uPlot (v1.6"),
            ("/uplot.min.css", "text/css; charset=utf-8", ".uplot"),
        ] {
            let r = req(Method::GET, uri, Some("localhost:8765")).body(Body::empty()).unwrap();
            let resp = e.app.clone().oneshot(r).await.unwrap();
            assert_eq!(resp.status(), StatusCode::OK, "{uri}");
            assert_eq!(resp.headers()[header::CONTENT_TYPE], ctype, "{uri}");
            assert_eq!(resp.headers()[header::CACHE_CONTROL], "no-cache", "{uri}");
            let b = to_bytes(resp.into_body(), 1 << 20).await.unwrap();
            let text = String::from_utf8_lossy(&b);
            assert!(text.contains(needle), "{uri} lacks {needle}");
            if uri == "/" {
                assert!(text.contains("Bluetti Watcher") && !text.contains("https://"), "{uri}");
            }
        }
        let r = req(Method::GET, "/app.js", Some("evil.example:8765")).body(Body::empty()).unwrap();
        assert_eq!(e.app.clone().oneshot(r).await.unwrap().status(), StatusCode::FORBIDDEN);
    }

    #[tokio::test]
    async fn mode_needs_header_and_valid_body() {
        let mut e = env("mode");
        let post = |hdr: bool, body: &str| {
            let mut b = req(Method::POST, "/api/mode", Some("127.0.0.1:8765"))
                .header(header::CONTENT_TYPE, "application/json");
            if hdr {
                b = b.header("X-Bluetti-Watcher", "1");
            }
            b.body(Body::from(body.to_string())).unwrap()
        };
        assert_eq!(call(&e.app, post(false, r#"{"mode":"max"}"#)).await.0, StatusCode::FORBIDDEN);
        assert_eq!(call(&e.app, post(true, r#"{"mode":"turbo"}"#)).await.0, StatusCode::BAD_REQUEST);
        assert_eq!(call(&e.app, post(true, "not json")).await.0, StatusCode::BAD_REQUEST);
        assert!(e.mode_rx.try_recv().is_err());
        let (st, v) = call(&e.app, post(true, r#"{"mode":"max"}"#)).await;
        assert_eq!((st, v), (StatusCode::ACCEPTED, json!({ "manual": "max" })));
        assert_eq!(e.mode_rx.try_recv().unwrap(), ManualMode::Max);
    }

    #[tokio::test]
    async fn now_has_contract_shape() {
        let e = env("now");
        let (st, v) = get_json(&e.app, "/api/now").await;
        assert_eq!(st, StatusCode::OK);
        let keys: Vec<&str> = v.as_object().unwrap().keys().map(String::as_str).collect();
        let mut want = vec![
            "ts",
            "stale",
            "soc",
            "acInW",
            "acOutW",
            "dcInW",
            "dcOutW",
            "acInV",
            "mains",
            "outage",
            "ecoAt",
            "eco",
            "manual",
            "hoursLeft",
            "runtimeSource",
            "levels",
            "hibernateDelayedUntil",
            "hibernateAvailable",
            "killSwitch",
            "dryRun",
            "lastError",
        ];
        want.sort_unstable();
        let mut got = keys.clone();
        got.sort_unstable();
        assert_eq!(got, want);
        assert_eq!(v["soc"], 80);
        assert_eq!(v["mains"], "unknown");
        assert_eq!(v["eco"], "none");
        assert_eq!(v["manual"], "auto");
        assert_eq!(v["dryRun"], true);
        assert_eq!(v["levels"], json!({ "low": false, "critical": false, "warn": false }));

        let full = NowView {
            outage: Some(OutageView { since: to_local_iso(0), soc_start: Some(98) }),
            runtime_source: Some(Source::Rate),
            last_error: Some(ErrorView { ts: to_local_iso(0), text: "x".into() }),
            ..Default::default()
        };
        let v = serde_json::to_value(full).unwrap();
        assert_eq!(v["outage"]["socStart"], 98);
        assert_eq!(v["runtimeSource"], "rate");
        assert_eq!(v["lastError"]["text"], "x");
    }

    #[tokio::test]
    async fn history_ranges() {
        let e = env("history");
        let (st, v) = get_json(&e.app, "/api/history?range=6h").await;
        assert_eq!(st, StatusCode::OK);
        assert_eq!(v["step"], 60);
        assert_eq!(v["points"].as_array().unwrap().len(), 18);
        assert_eq!(v["points"][0]["in"], 0);
        assert_eq!(v["points"][0]["out"], 184);
        assert_eq!(v["outages"].as_array().unwrap().len(), 2);
        assert!(v["outages"][1]["end"].is_null());

        let (st, v) = get_json(&e.app, "/api/history?range=7d").await;
        assert_eq!((st, &v["step"]), (StatusCode::OK, &json!(300)));
        let pts = v["points"].as_array().unwrap();
        assert!(!pts.is_empty() && pts.len() <= 5, "{}", pts.len());
        assert!(pts[0]["t"].is_i64() && pts[0]["soc"] == 80);

        assert_eq!(get_json(&e.app, "/api/history?range=30d").await.1["step"], 900);
        assert_eq!(get_json(&e.app, "/api/history?range=1y").await.0, StatusCode::BAD_REQUEST);
        assert_eq!(get_json(&e.app, "/api/history").await.0, StatusCode::BAD_REQUEST);
    }

    #[tokio::test]
    async fn outages_and_events_newest_first() {
        let e = env("lists");
        let (st, v) = get_json(&e.app, "/api/outages?limit=10").await;
        assert_eq!(st, StatusCode::OK);
        let a = v.as_array().unwrap();
        assert_eq!(a.len(), 2);
        assert!(a[0]["end"].is_null());
        assert_eq!(a[0]["socStart"], 85);
        assert_eq!(a[0]["durationMin"], 30);
        assert_eq!((a[1]["durationMin"].as_i64(), a[1]["socEnd"].as_i64()), (Some(60), Some(90)));
        assert_eq!(a[1]["start"].as_str().unwrap().len(), 25);
        for k in ["start", "end", "durationMin", "socStart", "socEnd", "eco", "hibernated"] {
            assert!(a[1].get(k).is_some(), "{k}");
        }
        assert_eq!(get_json(&e.app, "/api/outages?limit=1").await.1.as_array().unwrap().len(), 1);

        let (_, v) = get_json(&e.app, "/api/events").await;
        let a = v.as_array().unwrap();
        assert_eq!(
            a.iter().map(|x| x["kind"].as_str().unwrap()).collect::<Vec<_>>(),
            ["eco-on", "mains-lost"]
        );
        assert_eq!(a[0]["text"], "Eco включён");
        assert_eq!(a[0]["ts"].as_str().unwrap().len(), 25);
        assert_eq!(get_json(&e.app, "/api/events?limit=x").await.0, StatusCode::BAD_REQUEST);
    }

    #[tokio::test]
    async fn health_reports_read_ratio() {
        let e = env("health");
        let (st, v) = get_json(&e.app, "/api/health").await;
        assert_eq!(st, StatusCode::OK);
        assert_eq!(v["ok"], true);
        assert_eq!(v["version"], "0.1.0");
        assert!(v["encryption"].is_null());
        // 18 принятых из 20 циклов.
        assert_eq!(v["readOk24h"], 0.9);
        assert_eq!(v["lastReading"].as_str().unwrap().len(), 25);
    }

    #[tokio::test]
    async fn stream_sends_now_and_events() {
        use futures_util::StreamExt;
        let e = env("stream");
        let r = req(Method::GET, "/api/stream", Some("127.0.0.1:8765")).body(Body::empty()).unwrap();
        let resp = e.app.clone().oneshot(r).await.unwrap();
        assert_eq!(resp.headers()[header::CONTENT_TYPE], "text/event-stream");
        let mut body = resp.into_body().into_data_stream();
        let mut chunks = Vec::new();
        let first = body.next().await.unwrap().unwrap();
        chunks.push(String::from_utf8(first.to_vec()).unwrap());
        assert!(
            chunks[0].starts_with(
                "event: now
data: {"
            ) && chunks[0].contains("\"soc\":80"),
            "{}",
            chunks[0]
        );
        e.now_tx.send_modify(|v| v.soc = Some(79));
        let c = body.next().await.unwrap().unwrap();
        assert!(String::from_utf8_lossy(&c).contains("\"soc\":79"));
        e.events.send(store::Event { ts: 0, kind: "eco-on".into(), text: "Eco".into() }).unwrap();
        let c = body.next().await.unwrap().unwrap();
        let c = String::from_utf8_lossy(&c);
        assert!(
            c.starts_with(
                "event: event
data: {"
            ) && c.contains("\"kind\":\"eco-on\""),
            "{c}"
        );
    }
}
