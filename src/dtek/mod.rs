//! График отключений ДТЭК (feature 002): только показ. Решения (`crate::decision`) этот модуль не видят.
//!
//! Задача [`Refresher`] раз в 30 минут читает публичный набор, хранит сегодня и завтра для очередей-кандидатов в
//! `meta.dtek_cache` и публикует [`DtekView`] для `GET /api/dtek` и `event: dtek`.

pub mod fetch;
pub mod model;
pub mod parse;
pub mod queue;
pub mod windows;

use std::time::Duration;

use serde::{Deserialize, Serialize};
use tokio::sync::{broadcast, mpsc, watch};

use crate::clock::{Clock, MIN, Ts, to_local_iso};
use crate::config;
use crate::notify::Notifier;
use crate::store::{self, Store};
use fetch::{Fetched, Source};
use model::{Cell, Schedule};
use queue::{QueueState, QueueStatus};
use windows::Power;

pub const REFRESH: Duration = Duration::from_secs(30 * 60);
/// Сбор старше — `stale`.
pub const STALE_AFTER: Ts = 90 * MIN;

/// Закончившееся реальное отключение `(start_ts, end_ts)` от сервиса — для выбора очереди.
pub type FinishedOutage = (Ts, Ts);

/// Тело `GET /api/dtek` и `event: dtek` (plan.md, контракт).
#[derive(Debug, Clone, Default, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DtekView {
    pub enabled: bool,
    pub address: Option<String>,
    pub region: Option<String>,
    pub queue: Option<String>,
    pub queue_status: Option<QueueStatus>,
    pub candidates: Vec<String>,
    pub update: Option<String>,
    pub collected: Option<String>,
    pub stale: bool,
    pub error: Option<String>,
    pub now: Option<NowState>,
    pub days: Vec<DayView>,
}

#[derive(Debug, Clone, PartialEq, Serialize)]
pub struct NowState {
    pub power: Power,
    pub until: Option<String>,
}

#[derive(Debug, Clone, PartialEq, Serialize)]
pub struct DayView {
    pub date: String,
    pub label: String,
    pub cells: Option<Vec<Cell>>,
}

impl DtekView {
    /// `[dtek] enabled = false`: остальные поля пустые.
    pub fn disabled() -> Self {
        Self::default()
    }
}

/// Снимок для показа на момент `now`.
pub fn view(
    cfg: &config::Dtek,
    schedule: Option<&Schedule>,
    qs: &QueueState,
    error: Option<&str>,
    now: Ts,
) -> DtekView {
    if !cfg.enabled {
        return DtekView::disabled();
    }
    let today_date = windows::local_date(now);
    let tomorrow_date = today_date.and_then(|d| d.succ_opt());
    let today = today_date.and_then(windows::midnight).unwrap_or(now);
    let tomorrow = tomorrow_date.and_then(windows::midnight).unwrap_or(today + 24 * 60 * MIN);
    let choice = queue::choose(schedule, &cfg.queues, qs, today, tomorrow);
    let queue = choice.as_ref().map(|c| c.0.clone());
    let qdays = match (schedule, &queue) {
        (Some(s), Some(q)) => s.queue_days(q),
        _ => &[],
    };
    let day = |date: Option<chrono::NaiveDate>, start: Ts, label: &str| DayView {
        date: date.map(|d| d.format("%Y-%m-%d").to_string()).unwrap_or_default(),
        label: label.to_string(),
        cells: qdays.iter().find(|d| d.start == start).map(|d| d.cells.to_vec()),
    };
    let (power, until) = windows::current(qdays, now);
    DtekView {
        enabled: true,
        address: Some(cfg.address.clone()).filter(|a| !a.is_empty()),
        region: Some(cfg.region.clone()),
        queue,
        queue_status: choice.map(|c| c.1),
        candidates: cfg.queues.clone(),
        update: schedule.map(|s| s.update.clone()).filter(|u| !u.is_empty()),
        collected: schedule.map(|s| to_local_iso(s.collected)),
        stale: schedule.is_none_or(|s| now - s.collected > STALE_AFTER),
        error: error.map(str::to_string),
        now: Some(NowState { power, until: until.map(to_local_iso) }),
        days: vec![day(today_date, today, "Сегодня"), day(tomorrow_date, tomorrow, "Завтра")],
    }
}

/// Кэш в `meta.dtek_cache`: `key` — адрес набора и кандидаты, при их смене ETag не используется.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
struct Cache {
    key: String,
    schedule: Schedule,
}

/// Фоновая задача обновления графика.
pub struct Refresher<S: Source, C: Clock> {
    cfg: config::Dtek,
    url: String,
    key: String,
    store: Store,
    source: S,
    clock: C,
    events: broadcast::Sender<store::Event>,
    tx: watch::Sender<DtekView>,
    schedule: Option<Schedule>,
    queue: QueueState,
    error: Option<String>,
    failing: bool,
    notifier: Box<dyn Notifier>,
    /// Начало окна, о котором уже предупредили (`meta.dtek_warned`): после перезапуска не повторять.
    warned: Option<Ts>,
}

impl<S: Source, C: Clock> Refresher<S, C> {
    pub fn new(
        cfg: config::Dtek,
        store: Store,
        source: S,
        clock: C,
        events: broadcast::Sender<store::Event>,
        tx: watch::Sender<DtekView>,
        notifier: Box<dyn Notifier>,
    ) -> Self {
        let url = fetch::dataset_url(&cfg.region, cfg.url.as_deref());
        let key = format!("{url}|{}", cfg.queues.join(","));
        let schedule = store.get_meta::<Cache>("dtek_cache").filter(|c| c.key == key).map(|c| c.schedule);
        let mut queue = store.get_meta::<QueueState>("dtek_queue").unwrap_or_default();
        if queue::sanitize(&mut queue, &cfg.queues) {
            log_db(store.set_meta("dtek_queue", &queue));
        }
        let warned = store.get_meta::<Ts>("dtek_warned");
        Self {
            cfg,
            url,
            key,
            store,
            source,
            clock,
            events,
            tx,
            schedule,
            queue,
            error: None,
            failing: false,
            notifier,
            warned,
        }
    }

    pub fn view(&self) -> DtekView {
        view(&self.cfg, self.schedule.as_ref(), &self.queue, self.error.as_deref(), self.clock.now())
    }

    /// Отдать снимок, если он изменился, и предупредить о близком отключении.
    pub fn publish(&mut self) {
        self.warn();
        let v = self.view();
        self.tx.send_if_modified(|cur| {
            if *cur == v {
                false
            } else {
                *cur = v;
                true
            }
        });
    }

    /// Один запрос набора; ошибка оставляет сохранённое расписание.
    pub async fn refresh(&mut self) {
        let now = self.clock.now();
        match self.fetch_and_parse(now).await {
            Ok(changed) => {
                self.error = None;
                if self.failing {
                    self.failing = false;
                    self.event(now, "dtek-ok", "График ДТЭК снова обновляется".into());
                }
                if changed {
                    tracing::info!("график ДТЭК обновлён");
                }
            }
            Err(e) => {
                tracing::warn!("график ДТЭК: {e}");
                if !self.failing {
                    self.failing = true;
                    self.event(now, "dtek-failed", format!("График ДТЭК не обновлён: {e}"));
                }
                self.error = Some(e);
            }
        }
        self.publish();
    }

    async fn fetch_and_parse(&mut self, now: Ts) -> Result<bool, String> {
        fetch::check_url(&self.url, self.cfg.url.is_some())?;
        let etag = match self.schedule {
            Some(_) => self.store.get_meta::<String>("dtek_etag"),
            None => None,
        };
        match self.source.get(&self.url, etag.as_deref()).await? {
            Fetched::NotModified => Ok(false),
            Fetched::Body { body, etag } => {
                if body.len() > fetch::MAX_BODY {
                    return Err("ответ больше 4 МБ".into());
                }
                let mut s = parse::parse(&body)?;
                let today = windows::local_date(now).and_then(windows::midnight).unwrap_or(now);
                let after = windows::wall(today, 2 * 24 * 60);
                s.retain(&self.cfg.queues, today, after);
                log_db(
                    self.store.set_meta("dtek_cache", &Cache { key: self.key.clone(), schedule: s.clone() }),
                );
                match etag {
                    Some(e) => log_db(self.store.set_meta("dtek_etag", &e)),
                    None => log_db(self.store.delete_meta("dtek_etag")),
                }
                let changed = self.schedule.as_ref() != Some(&s);
                self.schedule = Some(s);
                Ok(changed)
            }
        }
    }

    /// Закончившееся отключение — в выбор очереди (US2). Короче `min_cut_min` не учитывается: мигание
    /// света не совпадёт ни с одним окном и только сбросит серию.
    pub fn observe(&mut self, start: Ts, end: Ts) {
        let Some(s) = &self.schedule else { return };
        if end - start < Ts::from(self.cfg.min_cut_min) * MIN {
            tracing::debug!(
                "отключение {} мин короче dtek.min_cut_min, в выбор очереди не идёт",
                (end - start) / MIN
            );
            return;
        }
        if queue::observe(s, &self.cfg.queues, &mut self.queue, start, end) {
            log_db(self.store.set_meta("dtek_queue", &self.queue));
            if let Some(q) = &self.queue.fixed {
                tracing::info!("очередь ДТЭК: {q} (серия {})", self.queue.streak_count);
            }
        }
        self.publish();
    }

    /// Уведомление за `warn_min` минут до начала планового окна показанной очереди — один раз на окно.
    fn warn(&mut self) {
        let lead = Ts::from(self.cfg.warn_min) * MIN;
        let Some(s) = self.schedule.as_ref().filter(|_| lead > 0) else { return };
        let now = self.clock.now();
        let today = windows::local_date(now).and_then(windows::midnight).unwrap_or(now);
        let Some((q, _)) =
            queue::choose(Some(s), &self.cfg.queues, &self.queue, today, windows::wall(today, 24 * 60))
        else {
            return;
        };
        let Some(w) = windows::off_windows(s.queue_days(&q)).into_iter().find(|w| w.start > now) else {
            return;
        };
        if w.start - now > lead || self.warned == Some(w.start) {
            return;
        }
        let minutes = (w.start - now + MIN - 1) / MIN;
        let title = format!("Через {minutes} мин отключение света");
        let text = format!(
            "По графику ДТЭК {}–{} (очередь {})",
            hhmm(w.start),
            hhmm(w.end),
            q.trim_start_matches("GPV")
        );
        self.notifier.notify(&title, &text);
        self.event(now, "dtek-warn", format!("{title}: {text}"));
        self.warned = Some(w.start);
        log_db(self.store.set_meta("dtek_warned", &w.start));
    }

    fn event(&self, ts: Ts, kind: &str, text: String) {
        log_db(self.store.insert_event(ts, kind, &text, None));
        let _ = self.events.send(store::Event { ts, kind: kind.to_string(), text });
    }

    /// Сразу — снимок из кэша и запрос; затем запрос раз в 30 минут, снимок раз в минуту (текущий час, «до»).
    pub async fn run(mut self, mut outages: mpsc::UnboundedReceiver<FinishedOutage>) {
        self.publish();
        let mut refresh = tokio::time::interval(REFRESH);
        refresh.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Delay);
        let mut minute = tokio::time::interval(Duration::from_secs(60));
        minute.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Delay);
        let mut outages_open = true;
        loop {
            tokio::select! {
                _ = refresh.tick() => self.refresh().await,
                _ = minute.tick() => self.publish(),
                o = outages.recv(), if outages_open => match o {
                    Some((start, end)) => self.observe(start, end),
                    None => outages_open = false,
                },
            }
        }
    }
}

fn hhmm(t: Ts) -> String {
    use chrono::TimeZone;
    chrono::Local.timestamp_millis_opt(t).single().map(|d| d.format("%H:%M").to_string()).unwrap_or_default()
}

fn log_db<T>(r: rusqlite::Result<T>) {
    if let Err(e) = r {
        tracing::error!("база данных (ДТЭК): {e}");
    }
}

#[cfg(test)]
mod tests {
    use std::cell::RefCell;
    use std::rc::Rc;

    use super::*;
    use crate::clock::FakeClock;

    const FIXTURE: &[u8] = include_bytes!("../../tests/fixtures/kyiv-sample.json");
    const H: Ts = 60 * MIN;
    /// 2026-10-14 00:00 по Киеву.
    const D0: Ts = 1_791_925_200_000;

    fn owner_cfg() -> config::Dtek {
        config::Dtek {
            enabled: true,
            address: "вул. Прикладна, 1".into(),
            queues: ["GPV9.1", "GPV21.1", "GPV33.1", "GPV45.1", "GPV57.1"].map(String::from).to_vec(),
            ..Default::default()
        }
    }

    /// Расписание фикстуры, привязанное к локальной полуночи ПК (тест не зависит от часового пояса машины).
    fn fixture_schedule() -> (Schedule, Ts) {
        let mut s = parse::parse(FIXTURE).unwrap();
        let today = windows::local_date(D0 + 12 * H).and_then(windows::midnight).unwrap();
        for d in s.days.values_mut().flatten() {
            d.start = today;
        }
        (s, today)
    }

    #[test]
    fn decision_does_not_see_dtek() {
        let dir = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("src/decision");
        let mut n = 0;
        for e in std::fs::read_dir(&dir).unwrap() {
            let p = e.unwrap().path();
            let text = std::fs::read_to_string(&p).unwrap().to_lowercase();
            assert!(!text.contains("dtek"), "{} упоминает dtek", p.display());
            n += 1;
        }
        assert!(n >= 4, "в src/decision найдено {n} файлов");
    }

    #[test]
    fn view_has_contract_shape() {
        let (s, today) = fixture_schedule();
        let now = today + 3 * H;
        let v =
            serde_json::to_value(view(&owner_cfg(), Some(&s), &QueueState::default(), None, now)).unwrap();
        let mut keys: Vec<&str> = v.as_object().unwrap().keys().map(String::as_str).collect();
        keys.sort_unstable();
        assert_eq!(
            keys,
            [
                "address",
                "candidates",
                "collected",
                "days",
                "enabled",
                "error",
                "now",
                "queue",
                "queueStatus",
                "region",
                "stale",
                "update"
            ]
        );
        assert_eq!(v["enabled"], true);
        assert_eq!(v["address"], "вул. Прикладна, 1");
        assert_eq!(v["region"], "kyiv");
        assert_eq!(v["queue"], "GPV9.1");
        assert_eq!(v["queueStatus"], "same");
        assert_eq!(v["candidates"].as_array().unwrap().len(), 5);
        assert_eq!(v["update"], "13.10.2026 21:31");
        assert_eq!(v["collected"], to_local_iso(s.collected));
        assert!(v["error"].is_null());
        assert_eq!(v["now"]["power"], "on");
        assert_eq!(v["now"]["until"], to_local_iso(today + 9 * H));
        let days = v["days"].as_array().unwrap();
        assert_eq!(days.len(), 2);
        assert_eq!(days[0]["label"], "Сегодня");
        assert_eq!(days[0]["date"], windows::local_date(today).unwrap().format("%Y-%m-%d").to_string());
        let cells = days[0]["cells"].as_array().unwrap();
        assert_eq!(cells.len(), 24);
        assert_eq!((&cells[8], &cells[9], &cells[12]), (&"yes".into(), &"no".into(), &"first".into()));
        assert_eq!(days[1]["label"], "Завтра");
        assert!(days[1]["cells"].is_null());
        assert_eq!(days[1]["date"].as_str().unwrap().len(), 10);

        let off = view(&owner_cfg(), Some(&s), &QueueState::default(), Some("HTTP 500"), today + 21 * H);
        let v = serde_json::to_value(off).unwrap();
        assert_eq!(
            v["now"],
            serde_json::json!({ "power": "off", "until": to_local_iso(today + 23 * H + 30 * MIN) })
        );
        assert_eq!(v["error"], "HTTP 500");
    }

    #[test]
    fn view_without_data_and_disabled() {
        let v = serde_json::to_value(view(&owner_cfg(), None, &QueueState::default(), None, D0)).unwrap();
        assert_eq!(v["now"], serde_json::json!({ "power": "unknown", "until": null }));
        assert_eq!(v["queueStatus"], "guess");
        assert_eq!(v["stale"], true);
        assert!(v["days"][0]["cells"].is_null() && v["update"].is_null() && v["collected"].is_null());

        let mut cfg = owner_cfg();
        cfg.enabled = false;
        let v = serde_json::to_value(view(&cfg, None, &QueueState::default(), None, D0)).unwrap();
        assert_eq!(
            v,
            serde_json::json!({
                "enabled": false, "address": null, "region": null, "queue": null, "queueStatus": null,
                "candidates": [], "update": null, "collected": null, "stale": false, "error": null,
                "now": null, "days": []
            })
        );
    }

    type Notes = Rc<RefCell<Vec<String>>>;

    struct FakeNotify(Notes);
    impl Notifier for FakeNotify {
        fn notify(&self, title: &str, text: &str) {
            self.0.borrow_mut().push(format!("{title} | {text}"));
        }
    }

    fn notes() -> (Box<dyn Notifier>, Notes) {
        let n = Notes::default();
        (Box::new(FakeNotify(n.clone())), n)
    }

    /// Фейковый источник: очередь ответов, запоминает присланный ETag.
    struct Fake {
        replies: Vec<Result<Fetched, String>>,
        etags: Rc<RefCell<Vec<Option<String>>>>,
    }

    impl Source for Fake {
        async fn get(&mut self, url: &str, etag: Option<&str>) -> Result<Fetched, String> {
            assert!(url.starts_with("https://raw.githubusercontent.com/"));
            self.etags.borrow_mut().push(etag.map(str::to_string));
            self.replies.remove(0)
        }
    }

    fn body(today: Ts) -> Vec<u8> {
        // Фикстура с ключом дня = локальная полночь ПК.
        let text = std::str::from_utf8(FIXTURE).unwrap().replace("1791925200", &(today / 1000).to_string());
        text.into_bytes()
    }

    #[tokio::test]
    async fn refresher_caches_reports_failures_once_and_observes() {
        let (_, today) = fixture_schedule();
        let store = Store::in_memory().unwrap();
        let (events, mut ev_rx) = broadcast::channel(16);
        let (tx, rx) = watch::channel(DtekView::disabled());
        let etags = Rc::new(RefCell::new(Vec::new()));
        let fake = Fake {
            replies: vec![
                Ok(Fetched::Body { body: body(today), etag: Some("\"abc\"".into()) }),
                Err("HTTP 500".into()),
                Err("таймаут".into()),
                Ok(Fetched::NotModified),
            ],
            etags: etags.clone(),
        };
        let mut r =
            Refresher::new(owner_cfg(), store, fake, FakeClock::new(today + 3 * H), events, tx, notes().0);
        r.publish();
        assert_eq!(rx.borrow().queue_status, Some(QueueStatus::Guess));
        r.refresh().await;
        assert_eq!(rx.borrow().queue_status, Some(QueueStatus::Same));
        assert!(rx.borrow().days[0].cells.is_some());
        assert_eq!(r.store.get_meta::<String>("dtek_etag").as_deref(), Some("\"abc\""));
        let cached = r.store.get_meta::<Cache>("dtek_cache").unwrap().schedule;
        assert_eq!(cached.days.len(), 5, "только кандидаты");
        r.refresh().await;
        r.refresh().await;
        assert_eq!(rx.borrow().error.as_deref(), Some("таймаут"));
        assert!(rx.borrow().days[0].cells.is_some(), "кэш остаётся");
        r.refresh().await;
        assert!(rx.borrow().error.is_none());
        assert_eq!(
            *etags.borrow(),
            [None, Some("\"abc\"".into()), Some("\"abc\"".into()), Some("\"abc\"".into())]
        );
        let kinds: Vec<String> = std::iter::from_fn(|| ev_rx.try_recv().ok()).map(|e| e.kind).collect();
        assert_eq!(kinds, ["dtek-failed", "dtek-ok"]);
        assert!(r.store.recent_events(5).unwrap()[1].text.contains("HTTP 500"));

        // Отключение, совпавшее со всеми пятью, серию не начинает.
        r.observe(today + 9 * H, today + 12 * H + 30 * MIN);
        assert_eq!(r.store.get_meta::<QueueState>("dtek_queue"), None);
        // Короткое (14 мин) не сбрасывает серию; 15 мин уже учитывается.
        r.queue = QueueState { fixed: None, streak_queue: Some("GPV9.1".into()), streak_count: 1 };
        r.observe(today + 20 * H, today + 20 * H + 14 * MIN);
        assert_eq!(r.queue.streak_count, 1);
        r.observe(today + 20 * H, today + 20 * H + 15 * MIN);
        assert_eq!(r.queue.streak_count, 0);

        // Новый запуск: кэш сразу, без запроса.
        let store = std::mem::replace(&mut r.store, Store::in_memory().unwrap());
        let (events, _) = broadcast::channel(16);
        let (tx, rx) = watch::channel(DtekView::disabled());
        let fake = Fake { replies: vec![], etags: Rc::new(RefCell::new(Vec::new())) };
        let mut r2 =
            Refresher::new(owner_cfg(), store, fake, FakeClock::new(today + 10 * H), events, tx, notes().0);
        r2.publish();
        assert_eq!(rx.borrow().now.as_ref().unwrap().power, Power::Off);
    }

    #[tokio::test]
    async fn warns_once_before_each_planned_cut() {
        let (_, today) = fixture_schedule();
        let (events, mut ev_rx) = broadcast::channel(16);
        let (tx, _rx) = watch::channel(DtekView::disabled());
        let fake = Fake {
            replies: vec![Ok(Fetched::Body { body: body(today), etag: None })],
            etags: Rc::new(RefCell::new(Vec::new())),
        };
        let (n, seen) = notes();
        let clock = FakeClock::new(today + 7 * H);
        let mut r = Refresher::new(owner_cfg(), Store::in_memory().unwrap(), fake, clock, events, tx, n);
        r.refresh().await;
        assert!(seen.borrow().is_empty(), "за 2 часа рано");
        r.clock.set(today + 8 * H + 10 * MIN);
        r.publish();
        r.clock.set(today + 8 * H + 30 * MIN);
        r.publish();
        assert_eq!(
            *seen.borrow(),
            ["Через 50 мин отключение света | По графику ДТЭК 09:00–12:30 (очередь 9.1)"]
        );
        assert_eq!(ev_rx.try_recv().unwrap().kind, "dtek-warn");
        // Во время окна — тишина; следующее окно — своё уведомление.
        r.clock.set(today + 10 * H);
        r.publish();
        r.clock.set(today + 19 * H);
        r.publish();
        assert_eq!(seen.borrow().len(), 2);
        assert!(seen.borrow()[1].starts_with("Через 60 мин"));

        // Перезапуск: о том же окне не повторяет; warn_min = 0 — молчит.
        let store = std::mem::replace(&mut r.store, Store::in_memory().unwrap());
        let (events, _) = broadcast::channel(16);
        let (tx, _rx) = watch::channel(DtekView::disabled());
        let fake = Fake { replies: vec![], etags: Rc::new(RefCell::new(Vec::new())) };
        let (n, seen) = notes();
        let mut r2 = Refresher::new(
            owner_cfg(),
            store,
            fake,
            FakeClock::new(today + 19 * H + 30 * MIN),
            events,
            tx,
            n,
        );
        r2.publish();
        assert!(seen.borrow().is_empty());
        r2.cfg.warn_min = 0;
        r2.warned = None;
        r2.publish();
        assert!(seen.borrow().is_empty());
    }
}
