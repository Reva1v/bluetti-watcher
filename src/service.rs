//! Цикл сервиса: чтение → решение → действия (eco, уведомления, гибернация) → история → API.

use std::path::PathBuf;
use std::time::Duration;

use tokio::sync::{broadcast, mpsc, watch};

use crate::ble::{ReadOptions, ReadOutcome};
use crate::clock::{Clock, MIN, Ts, to_local_iso};
use crate::config::Config;
use crate::decision::{self, Action, DecisionState, Input};
use crate::http::{ErrorView, HealthView, LevelsView, NowView, OutageView, StationView};
use crate::model::{EcoLevel, ManualMode, MissReason, ReadStatus, Reading};
use crate::notify::Notifier;
use crate::power::hibernate::Hibernator;
use crate::power::{PowerBackend, PowerControl, PowerRestore};
use crate::store::{self, Store};

const STALE_AFTER: Ts = 5 * MIN;
const TITLE: &str = "Bluetti Watcher";

/// Источник показаний: WinRT в проде, фейк в тестах.
#[allow(async_fn_in_trait)]
pub trait StationReader {
    async fn read(&mut self, encryption_hint: Option<bool>, ts: Ts) -> Result<ReadOutcome, MissReason>;
}

#[cfg(windows)]
pub struct WinRtReader {
    address: Option<u64>,
    keys: crate::ble::crypto::Keys,
}

#[cfg(windows)]
impl WinRtReader {
    pub fn new(address: Option<u64>) -> Self {
        Self { address, keys: crate::ble::crypto::Keys::bluetti() }
    }
}

#[cfg(windows)]
impl StationReader for WinRtReader {
    async fn read(&mut self, encryption_hint: Option<bool>, ts: Ts) -> Result<ReadOutcome, MissReason> {
        let address = self.address.ok_or(MissReason::NoAddress)?;
        let mut t = crate::ble::winrt::WinRtTransport::new(address);
        let opts = ReadOptions { encryption_hint, ..ReadOptions::default() };
        crate::ble::read_station(&mut t, &self.keys, &opts, ts).await
    }
}

/// Каналы к HTTP-серверу.
pub struct Channels {
    pub now: watch::Sender<NowView>,
    pub health: watch::Sender<HealthView>,
    pub events: broadcast::Sender<store::Event>,
    pub modes: mpsc::UnboundedReceiver<ManualMode>,
}

pub struct Service<R: StationReader, C: Clock, B: PowerBackend> {
    pub cfg: Config,
    pub store: Store,
    pub reader: R,
    pub clock: C,
    pub power: PowerControl<B>,
    pub hibernator: Box<dyn Hibernator>,
    pub notifier: Box<dyn Notifier>,
    pub kill_switch: PathBuf,
    pub state: DecisionState,
    channels: Channels,
    encryption: Option<bool>,
    last_purge: Ts,
    last_error: Option<(Ts, String)>,
    learned: Option<f64>,
    last_learn: Option<Ts>,
}

impl<R: StationReader, C: Clock, B: PowerBackend> Service<R, C, B> {
    #[allow(clippy::too_many_arguments)]
    pub fn new(
        cfg: Config,
        store: Store,
        reader: R,
        clock: C,
        power: PowerControl<B>,
        hibernator: Box<dyn Hibernator>,
        notifier: Box<dyn Notifier>,
        kill_switch: PathBuf,
        channels: Channels,
    ) -> Self {
        let encryption = store.get_meta::<bool>("encryption");
        let state = store.get_meta::<DecisionState>("decision_state").unwrap_or_default();
        let learned = store.get_meta::<f64>("learned_efficiency");
        let last_learn = store.get_meta::<Ts>("learned_at");
        Self {
            cfg,
            store,
            reader,
            clock,
            power,
            hibernator,
            notifier,
            kill_switch,
            state,
            channels,
            encryption,
            last_purge: 0,
            last_error: None,
            learned,
            last_learn,
        }
    }

    /// Подготовка при старте: схема «Bluetti Eco», возврат настроек после сбоя, проверка гибернации.
    pub fn start(&mut self) {
        let now = self.clock.now();
        self.event(now, "service-start", "Сервис запущен".into(), None);
        if self.cfg.eco.power_scheme {
            match self.power.ensure_scheme() {
                Ok(guid) => log_db(self.store.set_meta("eco_scheme", &guid)),
                Err(e) => tracing::warn!("схема «Bluetti Eco»: {e}"),
            }
        }
        // Сервис упал или ПК перезагрузился в eco, а решение уже не хочет eco — вернуть настройки.
        let saved = self.store.get_meta::<PowerRestore>("power_restore");
        if saved.is_some() && self.state.eco == EcoLevel::None {
            tracing::info!("возврат настроек питания после перезапуска");
            self.set_eco(now, EcoLevel::None);
        }
        if !self.hibernator.available() {
            tracing::warn!("гибернация в Windows выключена");
            self.notifier.notify(TITLE, crate::notify::text::NO_HIBERNATION);
        }
        self.publish(now);
    }

    /// Один цикл. `manual` — кнопка на дашборде с прошлого цикла.
    pub async fn tick(&mut self, manual: Option<ManualMode>) {
        let ts = self.clock.now();
        let reading = self.read(ts).await;
        let actions = self.decide(ts, reading, false, manual);
        let fresh_needed = actions.contains(&Action::NeedFreshReading);
        self.run_actions(ts, actions);
        if fresh_needed {
            // Свежее показание прямо перед гибернацией (FR-025).
            let ts = self.clock.now();
            let reading = self.read(ts).await;
            let actions = self.decide(ts, reading, true, None);
            self.run_actions(ts, actions);
        }
        log_db(self.store.set_meta("decision_state", &self.state));
        self.learn(ts);
        if ts - self.last_purge > 24 * 60 * MIN {
            self.last_purge = ts;
            log_db(self.store.purge(ts).map(|_| ()));
        }
        self.publish(ts);
    }

    /// Бесконечный цикл с шагом `poll_sec`; кнопка режима запускает цикл сразу.
    pub async fn run(&mut self) {
        self.start();
        let period = Duration::from_secs(u64::from(self.cfg.poll_sec));
        let mut ticker = tokio::time::interval(period);
        ticker.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Delay);
        loop {
            let manual = tokio::select! {
                _ = ticker.tick() => None,
                m = self.channels.modes.recv() => m,
            };
            // Несколько нажатий подряд — берём последнее.
            let mut manual = manual;
            while let Ok(m) = self.channels.modes.try_recv() {
                manual = Some(m);
            }
            self.tick(manual).await;
            if manual.is_some() {
                ticker.reset();
            }
        }
    }

    async fn read(&mut self, ts: Ts) -> Result<Reading, MissReason> {
        match self.reader.read(self.encryption, ts).await {
            Ok(out) => {
                if self.encryption != Some(out.encrypted) {
                    self.encryption = Some(out.encrypted);
                    log_db(self.store.set_meta("encryption", &out.encrypted));
                }
                Ok(out.reading)
            }
            Err(e) => {
                tracing::info!("нет показания: {e}");
                Err(e)
            }
        }
    }

    fn decide(
        &mut self,
        ts: Ts,
        reading: Result<Reading, MissReason>,
        fresh: bool,
        manual: Option<ManualMode>,
    ) -> Vec<Action> {
        let input = Input {
            reading: reading.clone(),
            fresh,
            kill_switch: self.kill_switch.exists(),
            manual_request: manual,
        };
        let d = decision::decide(&mut self.state, &input, ts, &self.cfg);
        match &reading {
            Ok(r) => {
                log_db(self.store.insert_reading(r, d.status, d.reason));
                if d.status == ReadStatus::Accepted {
                    self.last_error = None;
                }
            }
            Err(e) => {
                log_db(self.store.insert_miss(ts, d.reason.unwrap_or(e.code())));
                self.last_error = Some((ts, e.to_string()));
            }
        }
        d.actions
    }

    fn run_actions(&mut self, ts: Ts, actions: Vec<Action>) {
        for a in actions {
            match &a {
                Action::SetEco(level) => {
                    let ok = self.set_eco(ts, *level);
                    decision::report(&mut self.state, &a, ok, ts);
                }
                Action::Notify { text, .. } => {
                    self.notifier.notify(TITLE, text);
                    decision::report(&mut self.state, &a, true, ts);
                }
                Action::Hibernate => {
                    self.event(ts, "hibernate", "ПК уходит в гибернацию".into(), None);
                    log_db(self.store.mark_outage(false, true));
                    // Состояние — до гибернации: после пробуждения решение должно знать, что она была.
                    decision::report(&mut self.state, &a, true, ts);
                    log_db(self.store.set_meta("decision_state", &self.state));
                    if let Err(e) = self.hibernator.hibernate() {
                        decision::report(&mut self.state, &a, false, ts);
                        self.fail(ts, format!("гибернация не удалась: {e}"));
                    }
                }
                Action::Event { kind, text, data } => {
                    let data = data.as_ref().map(|v| v.to_string());
                    self.event(ts, kind, text.clone(), data);
                }
                Action::OutageStart { start_ts, soc } => {
                    log_db(self.store.start_outage(*start_ts, *soc).map(|_| ()))
                }
                Action::OutageEnd { end_ts, soc } => log_db(self.store.end_outage(*end_ts, *soc)),
                Action::NeedFreshReading => {}
            }
        }
    }

    /// Применить уровень eco; `true` — хотя бы одна часть сработала (или частей нет).
    fn set_eco(&mut self, ts: Ts, level: EcoLevel) -> bool {
        let saved = self.store.get_meta::<PowerRestore>("power_restore");
        let saved = if level == EcoLevel::None {
            saved
        } else {
            // Что возвращать — записать до первого изменения (FR-020).
            let pre = self.power.capture(saved);
            log_db(self.store.set_meta("power_restore", &pre));
            Some(pre)
        };
        let (restore, parts) = self.power.apply(level, saved);
        match &restore {
            Some(r) => log_db(self.store.set_meta("power_restore", r)),
            None => log_db(self.store.delete_meta("power_restore")),
        }
        let done: Vec<&str> = parts.iter().filter(|p| p.ok).map(|p| p.detail.as_str()).collect();
        for p in parts.iter().filter(|p| !p.ok) {
            self.event(ts, "eco-part-failed", p.detail.clone(), Some(format!("{{\"part\":\"{}\"}}", p.part)));
        }
        let (kind, title) = match level {
            EcoLevel::None => ("eco-off", "Eco снят"),
            EcoLevel::Eco => ("eco-on", "Eco включён"),
            EcoLevel::Deep => ("eco-deep", "Глубокий eco включён"),
        };
        let ok = parts.is_empty() || !done.is_empty();
        if ok {
            let text =
                if done.is_empty() { title.to_string() } else { format!("{title}: {}", done.join("; ")) };
            self.event(ts, kind, text, None);
            if level != EcoLevel::None {
                log_db(self.store.mark_outage(true, false));
            }
        } else {
            self.fail(ts, format!("{title} — не удалось ни одной части"));
        }
        ok
    }

    fn fail(&mut self, ts: Ts, text: String) {
        tracing::warn!("{text}");
        self.last_error = Some((ts, text.clone()));
        self.event(ts, "action-failed", text, None);
    }

    fn event(&mut self, ts: Ts, kind: &str, text: String, data: Option<String>) {
        log_db(self.store.insert_event(ts, kind, &text, data.as_deref()));
        let _ = self.channels.events.send(store::Event { ts, kind: kind.to_string(), text });
    }

    fn recent(&self, now: Ts) -> Vec<Reading> {
        self.store.accepted_between(now - 70 * MIN, now).unwrap_or_default()
    }

    fn learn(&mut self, now: Ts) {
        let recent = self.recent(now);
        if let Some((eff, at)) = crate::runtime::learn(&recent, self.learned, self.last_learn, now, &self.cfg)
        {
            self.learned = Some(eff);
            self.last_learn = Some(at);
            log_db(self.store.set_meta("learned_efficiency", &eff));
            log_db(self.store.set_meta("learned_at", &at));
        }
    }

    /// Снимок для `/api/now` и `/api/health`.
    pub fn now_view(&self, now: Ts) -> NowView {
        let v = decision::view(&self.state, now, &self.cfg);
        let last = self.state.last_accepted.clone().or_else(|| self.store.last_accepted().ok().flatten());
        let estimate = crate::runtime::estimate(&self.recent(now), now, &self.cfg, self.learned, 0);
        let iso = |t: Option<Ts>| t.map(to_local_iso);
        NowView {
            ts: iso(last.as_ref().map(|r| r.ts)),
            stale: last.as_ref().is_none_or(|r| now - r.ts > STALE_AFTER),
            soc: last.as_ref().and_then(|r| r.soc),
            ac_in_w: last.as_ref().and_then(|r| r.ac_in_w),
            ac_out_w: last.as_ref().and_then(|r| r.ac_out_w),
            dc_in_w: last.as_ref().and_then(|r| r.dc_in_w),
            dc_out_w: last.as_ref().and_then(|r| r.dc_out_w),
            ac_in_v: last.as_ref().and_then(|r| r.ac_in_v),
            mains: v.mains,
            outage: v.outage_since.map(|since| OutageView {
                since: to_local_iso(since),
                soc_start: self.state.outage.as_ref().and_then(|o| o.soc_start),
            }),
            eco_at: iso(v.eco_at),
            eco: v.eco,
            manual: v.manual,
            hours_left: estimate.as_ref().map(|e| e.hours),
            runtime_source: estimate.map(|e| e.source),
            levels: LevelsView { low: v.level_low, critical: v.level_critical, warn: v.level_warn },
            hibernate_delayed_until: iso(v.hibernate_delayed_until),
            hibernate_available: self.hibernator.available(),
            kill_switch: self.kill_switch.exists(),
            dry_run: self.cfg.dry_run,
            last_error: self
                .last_error
                .as_ref()
                .map(|(t, text)| ErrorView { ts: to_local_iso(*t), text: text.clone() }),
            station: StationView {
                mac: self.cfg.mac.map(crate::config::format_mac),
                capacity_wh: self.cfg.station.capacity_wh,
                efficiency: self.learned.unwrap_or(self.cfg.station.efficiency),
                low: self.cfg.levels.low,
                critical: self.cfg.levels.critical,
                warn: self.cfg.levels.warn,
                hibernate: self.cfg.levels.hibernate,
            },
        }
    }

    fn publish(&self, now: Ts) {
        let view = self.now_view(now);
        let health = HealthView {
            ok: true,
            version: env!("CARGO_PKG_VERSION").to_string(),
            last_reading: view.ts.clone(),
            encryption: self.encryption,
            read_ok_24h: None,
        };
        self.channels.now.send_replace(view);
        self.channels.health.send_replace(health);
    }
}

fn log_db<T>(r: rusqlite::Result<T>) {
    if let Err(e) = r {
        tracing::error!("база данных: {e}");
    }
}

#[cfg(test)]
mod tests {
    use std::cell::RefCell;
    use std::collections::HashMap;
    use std::rc::Rc;

    use super::*;
    use crate::clock::FakeClock;

    struct Fake(Vec<Result<ReadOutcome, MissReason>>);

    impl StationReader for Fake {
        async fn read(&mut self, _: Option<bool>, ts: Ts) -> Result<ReadOutcome, MissReason> {
            let next = if self.0.len() > 1 { self.0.remove(0) } else { self.0[0].clone() };
            next.map(|mut o| {
                o.reading.ts = ts;
                o
            })
        }
    }

    fn ok(soc: u8, ac_in: u32) -> Result<ReadOutcome, MissReason> {
        Ok(ReadOutcome {
            reading: Reading {
                soc: Some(soc),
                ac_in_w: Some(ac_in),
                ac_out_w: Some(180),
                dc_in_w: Some(0),
                dc_out_w: Some(0),
                ..Default::default()
            },
            encrypted: false,
        })
    }

    /// Минимальная схема питания в памяти.
    #[derive(Default)]
    struct FakePower {
        active: String,
        schemes: HashMap<String, String>,
        overlay: String,
        next: u32,
    }

    impl PowerBackend for FakePower {
        fn active_scheme(&self) -> Result<String, String> {
            Ok(self.active.clone())
        }
        fn set_active_scheme(&mut self, guid: &str) -> Result<(), String> {
            self.active = guid.to_string();
            Ok(())
        }
        fn schemes(&self) -> Result<Vec<(String, String)>, String> {
            Ok(self.schemes.iter().map(|(g, n)| (g.clone(), n.clone())).collect())
        }
        fn duplicate_scheme(&mut self, _: &str) -> Result<String, String> {
            self.next += 1;
            let g = format!("00000000-0000-0000-0000-{:012}", self.next);
            self.schemes.insert(g.clone(), String::new());
            Ok(g)
        }
        fn set_friendly_name(&mut self, guid: &str, name: &str) -> Result<(), String> {
            self.schemes.insert(guid.to_string(), name.to_string());
            Ok(())
        }
        fn write_ac_value(&mut self, _: &str, _: &str, _: &str, _: u32) -> Result<(), String> {
            Ok(())
        }
        fn delete_scheme(&mut self, guid: &str) -> Result<(), String> {
            self.schemes.remove(guid);
            Ok(())
        }
        fn overlay(&self) -> Result<String, String> {
            Ok(self.overlay.clone())
        }
        fn set_overlay(&mut self, guid: &str) -> Result<(), String> {
            self.overlay = guid.to_string();
            Ok(())
        }
        fn gpu_task_installed(&self) -> bool {
            false
        }
        fn gpu_request(&mut self, _: EcoLevel) -> Result<(), String> {
            Ok(())
        }
    }

    struct FakeHib(Rc<RefCell<u32>>);
    impl Hibernator for FakeHib {
        fn available(&self) -> bool {
            true
        }
        fn hibernate(&self) -> Result<(), String> {
            *self.0.borrow_mut() += 1;
            Ok(())
        }
    }

    struct FakeNotify(Rc<RefCell<Vec<String>>>);
    impl Notifier for FakeNotify {
        fn notify(&self, _: &str, text: &str) {
            self.0.borrow_mut().push(text.to_string());
        }
    }

    struct Harness {
        svc: Service<Fake, FakeClock, FakePower>,
        hibernations: Rc<RefCell<u32>>,
        notes: Rc<RefCell<Vec<String>>>,
    }

    fn harness(reads: Vec<Result<ReadOutcome, MissReason>>) -> Harness {
        let (now, _) = watch::channel(NowView::default());
        let (health, _) = watch::channel(HealthView::default());
        let (events, _) = broadcast::channel(16);
        let (_tx, modes) = mpsc::unbounded_channel();
        let mut backend = FakePower {
            active: crate::power::HIGH_PERFORMANCE.into(),
            overlay: crate::power::OVERLAY_NONE.into(),
            ..Default::default()
        };
        for (g, n) in [
            (crate::power::HIGH_PERFORMANCE, "Высокая производительность"),
            (crate::power::BALANCED, "Сбалансированная"),
        ] {
            backend.schemes.insert(g.into(), n.into());
        }
        let cfg = Config::default();
        let power = PowerControl::new(backend, cfg.eco.clone(), None, false);
        let hibernations = Rc::new(RefCell::new(0));
        let notes = Rc::new(RefCell::new(Vec::new()));
        let svc = Service::new(
            cfg,
            Store::in_memory().unwrap(),
            Fake(reads),
            FakeClock::new(1_000 * MIN),
            power,
            Box::new(FakeHib(hibernations.clone())),
            Box::new(FakeNotify(notes.clone())),
            PathBuf::from("Z:/nonexistent/no-hibernate"),
            Channels { now, health, events, modes },
        );
        Harness { svc, hibernations, notes }
    }

    #[tokio::test]
    async fn stores_readings_and_misses() {
        let mut h = harness(vec![ok(80, 100), Err(MissReason::Timeout), ok(80, 100)]);
        h.svc.start();
        for _ in 0..3 {
            h.svc.tick(None).await;
            h.svc.clock.advance(MIN);
        }
        assert_eq!(h.svc.store.last_accepted().unwrap().unwrap().soc, Some(80));
        assert_eq!(h.svc.store.get_meta::<bool>("encryption"), Some(false));
        assert!(h.svc.store.get_meta::<String>("eco_scheme").is_some());
    }

    #[tokio::test]
    async fn outage_turns_eco_on_and_back_off() {
        let mut reads = vec![ok(90, 300), ok(90, 300)];
        reads.extend((0..8).map(|_| ok(89, 0)));
        reads.extend((0..8).map(|_| ok(89, 300)));
        let mut h = harness(reads);
        h.svc.start();
        let mut saw_eco = false;
        for _ in 0..18 {
            h.svc.tick(None).await;
            h.svc.clock.advance(MIN);
            if h.svc.power.backend().active != crate::power::HIGH_PERFORMANCE {
                saw_eco = true;
                assert!(h.svc.store.get_meta::<PowerRestore>("power_restore").is_some());
            }
        }
        assert!(saw_eco, "eco так и не включился");
        assert_eq!(h.svc.power.backend().active, crate::power::HIGH_PERFORMANCE);
        assert!(h.svc.store.get_meta::<PowerRestore>("power_restore").is_none());
        let o = &h.svc.store.outages(5).unwrap()[0];
        assert!(o.end_ts.is_some() && o.eco);
        let kinds: Vec<String> = h.svc.store.recent_events(50).unwrap().into_iter().map(|e| e.kind).collect();
        assert!(kinds.contains(&"eco-on".to_string()) && kinds.contains(&"eco-off".to_string()), "{kinds:?}");
    }

    #[tokio::test]
    async fn drain_hibernates_once_with_warning() {
        let mut reads = vec![ok(25, 300), ok(25, 300)];
        // 1 % за 3 минуты от 25 до 3.
        for soc in (3..=25).rev() {
            reads.extend((0..3).map(|_| ok(soc, 0)));
        }
        let n = reads.len();
        let mut h = harness(reads);
        h.svc.start();
        for _ in 0..n {
            h.svc.tick(None).await;
            h.svc.clock.advance(MIN);
        }
        assert_eq!(*h.hibernations.borrow(), 1);
        assert!(h.notes.borrow().iter().any(|t| t.contains("гибернац")), "{:?}", h.notes.borrow());
        assert!(h.svc.store.outages(1).unwrap()[0].hibernated);
    }
}
