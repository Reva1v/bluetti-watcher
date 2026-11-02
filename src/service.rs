//! Цикл сервиса: чтение → решение → действия (eco, уведомления, гибернация) → история → API.

use std::path::PathBuf;
use std::time::Duration;

use tokio::sync::{broadcast, mpsc, watch};

use crate::ble::ac180p::ChargingMode;
use crate::ble::{ReadOptions, ReadOutcome};
use crate::clock::{Clock, MIN, SEC, Ts, to_local_iso};
use crate::config::Config;
use crate::decision::charging::{self, ChargingAuto, Seen, Want, Why};
use crate::decision::{self, Action, DecisionState, Input};
use crate::http::{
    ChargingRequest, ChargingView, CheckRequest, CheckView, ErrorView, HealthView, LevelsView, NowView,
    OutageView, StationView,
};
use crate::model::{EcoLevel, ManualMode, MissReason, ReadStatus, Reading};
use crate::notify::Notifier;
use crate::power::hibernate::Hibernator;
use crate::power::{PowerBackend, PowerControl, PowerRestore};
use crate::store::{self, Store};

const STALE_AFTER: Ts = 5 * MIN;
const TITLE: &str = "Bluetti Watcher";
/// Запись при выключении: укладывается в лимит окна (`shutdown::HOLD`) с запасом на ответ.
const SHUTDOWN_WRITE: Duration = Duration::from_secs(13);
const AUTO_META: &str = "charging_auto";

/// Источник показаний: WinRT в проде, фейк в тестах.
#[allow(async_fn_in_trait)]
pub trait StationReader {
    async fn read(&mut self, encryption_hint: Option<bool>, ts: Ts) -> Result<ReadOutcome, MissReason>;

    /// Записать режим зарядки; `Ok` — станция подтвердила.
    async fn write_charging_mode(
        &mut self,
        encryption_hint: Option<bool>,
        mode: ChargingMode,
    ) -> Result<(), MissReason>;

    /// Видна ли станция в рекламе BLE прямо сейчас; `None` — проверить нельзя.
    async fn advertising(&mut self) -> Option<bool> {
        None
    }
}

#[cfg(windows)]
pub struct WinRtReader {
    address: Option<u64>,
    keys: crate::ble::crypto::Keys,
    unanswered: crate::ble::Unanswered,
}

#[cfg(windows)]
impl WinRtReader {
    pub fn new(address: Option<u64>) -> Self {
        Self { address, keys: crate::ble::crypto::Keys::bluetti(), unanswered: Default::default() }
    }
}

#[cfg(windows)]
impl StationReader for WinRtReader {
    async fn read(&mut self, encryption_hint: Option<bool>, ts: Ts) -> Result<ReadOutcome, MissReason> {
        let address = self.address.ok_or(MissReason::NoAddress)?;
        let mut t = crate::ble::winrt::WinRtTransport::new(address);
        let opts = ReadOptions { encryption_hint, ..ReadOptions::default() };
        crate::ble::read_station_tracked(&mut t, &self.keys, &opts, ts, &mut self.unanswered).await
    }

    async fn write_charging_mode(
        &mut self,
        encryption_hint: Option<bool>,
        mode: ChargingMode,
    ) -> Result<(), MissReason> {
        let address = self.address.ok_or(MissReason::NoAddress)?;
        let mut t = crate::ble::winrt::WinRtTransport::new(address);
        let opts = ReadOptions { encryption_hint, ..ReadOptions::default() };
        crate::ble::write_charging_mode(&mut t, &self.keys, &opts, mode, &mut self.unanswered).await
    }

    async fn advertising(&mut self) -> Option<bool> {
        let address = self.address?;
        match crate::ble::scan::seen(address, Duration::from_secs(8)).await {
            Ok(seen) => Some(seen),
            Err(e) => {
                tracing::warn!("скан BLE для проверки: {e}");
                None
            }
        }
    }
}

/// Начато выключение ПК: `done` разрешает Windows продолжить.
pub struct ShutdownRequest {
    pub done: std::sync::mpsc::Sender<()>,
}

/// Каналы к HTTP-серверу и окну сигнала выключения.
pub struct Channels {
    pub now: watch::Sender<NowView>,
    pub health: watch::Sender<HealthView>,
    pub events: broadcast::Sender<store::Event>,
    pub modes: mpsc::UnboundedReceiver<ManualMode>,
    /// Кнопка «Проверить подключение сейчас».
    pub checks: mpsc::UnboundedReceiver<CheckRequest>,
    /// Кнопки режима зарядки станции.
    pub charging: mpsc::UnboundedReceiver<ChargingRequest>,
    /// Сигнал о выключении; отправителя нет, пока автоматика выключена.
    pub shutdown: mpsc::UnboundedReceiver<ShutdownRequest>,
    /// Закончившиеся отключения для выбора очереди ДТЭК; `None` — график выключен.
    pub outages: Option<mpsc::UnboundedSender<crate::dtek::FinishedOutage>>,
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
    /// Последний прочитанный режим зарядки.
    charging_mode: Option<ChargingMode>,
    /// Состояние правил режима зарядки (feature 004).
    auto: ChargingAuto,
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
        let auto = store.get_meta::<ChargingAuto>(AUTO_META).unwrap_or_default();
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
            charging_mode: None,
            auto,
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

    /// Один цикл. `manual` — кнопка на дашборде с прошлого цикла. Возвращает результат первого чтения.
    pub async fn tick(&mut self, manual: Option<ManualMode>) -> Result<Reading, MissReason> {
        let ts = self.clock.now();
        let reading = self.read(ts).await;
        let first = reading.clone();
        let (status, actions) = self.decide(ts, reading, false, manual);
        let fresh_needed = actions.contains(&Action::NeedFreshReading);
        self.run_actions(ts, actions);
        if fresh_needed {
            // Свежее показание прямо перед гибернацией (FR-025).
            let ts = self.clock.now();
            let reading = self.read(ts).await;
            let (_, actions) = self.decide(ts, reading, true, None);
            self.run_actions(ts, actions);
        }
        if status == ReadStatus::Accepted
            && let Some(soc) = first.as_ref().ok().and_then(|r| r.soc)
        {
            self.apply_charging_rules(ts, soc).await;
        }
        log_db(self.store.set_meta("decision_state", &self.state));
        self.learn(ts);
        if ts - self.last_purge > 24 * 60 * MIN {
            self.last_purge = ts;
            log_db(self.store.purge(ts).map(|_| ()));
        }
        self.publish(ts);
        first
    }

    /// Бесконечный цикл с шагом `poll_sec`; кнопки режима, зарядки и проверки запускают цикл сразу.
    pub async fn run(&mut self) {
        self.start();
        let (_, closed) = mpsc::unbounded_channel();
        let mut shutdown = std::mem::replace(&mut self.channels.shutdown, closed);
        let period = Duration::from_secs(u64::from(self.cfg.poll_sec));
        let mut ticker = tokio::time::interval(period);
        ticker.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Delay);
        loop {
            let mut manual = None;
            let mut checks = Vec::new();
            let mut charging = Vec::new();
            let early = tokio::select! {
                biased;
                Some(r) = shutdown.recv() => {
                    self.on_shutdown(r).await;
                    continue;
                }
                _ = ticker.tick() => false,
                Some(m) = self.channels.modes.recv() => { manual = Some(m); true }
                Some(c) = self.channels.checks.recv() => { checks.push(c); true }
                Some(c) = self.channels.charging.recv() => { charging.push(c); true }
            };
            // Несколько нажатий подряд — берём последнее.
            while let Ok(m) = self.channels.modes.try_recv() {
                manual = Some(m);
            }
            while let Ok(c) = self.channels.checks.try_recv() {
                checks.push(c);
            }
            while let Ok(c) = self.channels.charging.try_recv() {
                charging.push(c);
            }
            // Выключение не ждёт зависшего чтения. Бросить цикл безопасно: между его `await` код синхронный.
            tokio::select! {
                biased;
                Some(r) = shutdown.recv() => self.on_shutdown(r).await,
                () = self.cycle(manual, checks, charging) => {}
            }
            if early {
                ticker.reset();
            }
            crate::memory::trim();
        }
    }

    /// Цикл и ответ всем, кто ждёт проверку подключения или смену режима зарядки. Режим зарядки пишется до
    /// чтения, чтобы чтение сразу показало, что стоит на станции; из нескольких нажатий — последнее.
    pub async fn cycle(
        &mut self,
        manual: Option<ManualMode>,
        checks: Vec<CheckRequest>,
        charging: Vec<ChargingRequest>,
    ) {
        let started = std::time::Instant::now();
        let written = match charging.last() {
            Some(c) => {
                charging::manual(&mut self.auto);
                Some((c.mode, self.set_charging_mode(c.mode).await))
            }
            None => None,
        };
        let first = self.tick(manual).await;
        if let Some((mode, result)) = written {
            let view = self.charging_view(mode, result);
            for c in charging {
                let _ = c.reply.send(view.clone());
            }
        }
        if checks.is_empty() {
            return;
        }
        let view = self.check_view(first, started).await;
        for c in checks {
            let _ = c.send(view.clone());
        }
    }

    async fn check_view(
        &mut self,
        first: Result<Reading, MissReason>,
        started: std::time::Instant,
    ) -> CheckView {
        let ts = self.clock.now();
        let elapsed_ms = started.elapsed().as_millis() as u64;
        let view = match first {
            Ok(r) => {
                let soc = r.soc.map_or(String::new(), |s| format!("заряд {s} %, "));
                let secs = format!("{:.1}", elapsed_ms as f64 / 1000.0).replace('.', ",");
                CheckView {
                    ok: true,
                    title: "Связь со станцией есть".into(),
                    detail: format!("Станция ответила: {soc}чтение заняло {secs} с."),
                    ..Default::default()
                }
            }
            Err(e) => {
                let advertising = if e.needs_scan() { self.reader.advertising().await } else { None };
                let (title, hint) = e.explain(advertising);
                CheckView {
                    ok: false,
                    title: title.into(),
                    detail: hint.into(),
                    reason: Some(e.code()),
                    advertising,
                    ..Default::default()
                }
            }
        };
        let kind = if view.ok { "check-ok" } else { "check-failed" };
        self.event(ts, kind, format!("Проверка подключения: {}", view.title), None);
        CheckView { ts: to_local_iso(ts), elapsed_ms, ..view }
    }

    /// Запись режима зарядки по кнопке. В `--dry-run` станция не меняется.
    async fn set_charging_mode(&mut self, mode: ChargingMode) -> Result<(), String> {
        let ts = self.clock.now();
        if self.cfg.dry_run {
            tracing::info!("dry run: режим зарядки «{}» не записан", mode.title());
            return Err("режим --dry-run, станция не менялась".into());
        }
        match self.reader.write_charging_mode(self.encryption, mode).await {
            Ok(()) => {
                self.charging_mode = Some(mode);
                let data = serde_json::json!({ "mode": mode }).to_string();
                self.event(ts, "charging-mode", format!("Режим зарядки: {}", mode.title()), Some(data));
                Ok(())
            }
            Err(e) => {
                self.fail(ts, format!("режим зарядки «{}» не установлен: {e}", mode.title()));
                Err(e.to_string())
            }
        }
    }

    /// Правила владельца по принятому показанию (feature 004).
    async fn apply_charging_rules(&mut self, ts: Ts, soc: u8) {
        if !self.cfg.charging_auto.enabled {
            return;
        }
        let seen = Seen {
            soc,
            mains: self.state.mains,
            mains_confirmed: self.state.mains_run_ms >= Ts::from(self.cfg.mains.back_sec) * SEC,
            outage: self.state.outage.is_some(),
        };
        let want = charging::update(&mut self.auto, seen, self.charging_mode, ts, &self.cfg.charging_auto);
        if let Some(w) = want {
            let ok = self.write_by_rule(ts, w).await;
            charging::report(&mut self.auto, ok, ts);
        }
        log_db(self.store.set_meta(AUTO_META, &self.auto));
    }

    /// Запись по правилу; `true` — станция подтвердила. В `--dry-run` только лог.
    async fn write_by_rule(&mut self, ts: Ts, w: Want) -> bool {
        let why = w.why.text();
        if self.cfg.dry_run {
            tracing::info!("dry run: режим зарядки «{}» не записан ({why})", w.mode.title());
            return true;
        }
        match self.reader.write_charging_mode(self.encryption, w.mode).await {
            Ok(()) => {
                self.charging_mode = Some(w.mode);
                let data = serde_json::json!({ "mode": w.mode, "reason": w.why.code() }).to_string();
                self.event(
                    ts,
                    "charging-mode",
                    format!("Режим зарядки: {} ({why})", w.mode.title()),
                    Some(data),
                );
                true
            }
            Err(e) => {
                self.fail(ts, format!("режим зарядки «{}» не установлен ({why}): {e}", w.mode.title()));
                false
            }
        }
    }

    /// Выключение или перезагрузка ПК: «Тихая» при любом заряде, затем Windows продолжает.
    pub async fn on_shutdown(&mut self, req: ShutdownRequest) {
        let ts = self.clock.now();
        // Состояние — до записи: Windows может завершить процесс в любой момент.
        charging::shutdown(&mut self.auto, ts);
        log_db(self.store.set_meta(AUTO_META, &self.auto));
        if self.charging_mode != Some(ChargingMode::Silent) {
            let want = Want { mode: ChargingMode::Silent, why: Why::Shutdown };
            let write = async {
                for _ in 0..2 {
                    if self.write_by_rule(ts, want).await {
                        break;
                    }
                }
            };
            if tokio::time::timeout(SHUTDOWN_WRITE, write).await.is_err() {
                self.fail(
                    ts,
                    "режим зарядки при выключении ПК не установлен: станция не ответила вовремя".into(),
                );
            }
        }
        let _ = req.done.send(());
    }

    /// Ответ кнопке: итог записи и что станция показала при чтении сразу после неё.
    fn charging_view(&self, mode: ChargingMode, result: Result<(), String>) -> ChargingView {
        let (ok, text) = match result {
            Ok(()) if self.charging_mode == Some(mode) => (true, format!("Режим зарядки: {}", mode.title())),
            Ok(()) => (false, "Станция приняла команду, но показывает другой режим".to_string()),
            Err(e) => (false, format!("Режим зарядки не изменён: {e}")),
        };
        ChargingView { ok, text, charging_mode: self.charging_mode }
    }

    async fn read(&mut self, ts: Ts) -> Result<Reading, MissReason> {
        match self.reader.read(self.encryption, ts).await {
            Ok(out) => {
                if self.encryption != Some(out.encrypted) {
                    self.encryption = Some(out.encrypted);
                    log_db(self.store.set_meta("encryption", &out.encrypted));
                }
                if out.charging_mode.is_some() {
                    self.charging_mode = out.charging_mode;
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
    ) -> (ReadStatus, Vec<Action>) {
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
        (d.status, d.actions)
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
                Action::OutageEnd { end_ts, soc } => {
                    let open = self
                        .store
                        .outages(1)
                        .ok()
                        .and_then(|v| v.into_iter().next())
                        .filter(|o| o.end_ts.is_none());
                    log_db(self.store.end_outage(*end_ts, *soc));
                    if let (Some(tx), Some(o)) = (&self.channels.outages, open) {
                        let _ = tx.send((o.start_ts, *end_ts));
                    }
                }
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
        // Средняя нагрузка вместо мгновенной: в отключении — с его начала, иначе — за всю историю.
        let outage_start = self.state.outage.as_ref().map(|o| o.start_ts);
        let (avg_from, scope) = match outage_start {
            Some(t) => (t, "outage"),
            None => (0, "history"),
        };
        let avg = self.store.avg_load(avg_from, now).ok().flatten().map(|(a, _)| a);
        let estimate =
            crate::runtime::estimate_with_load(&self.recent(now), now, &self.cfg, self.learned, 0, avg);
        let hours_if_outage = match (outage_start, last.as_ref().and_then(|r| r.soc), avg) {
            (None, Some(soc), Some(a)) => crate::runtime::if_outage(soc, a, &self.cfg, self.learned),
            _ => None,
        };
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
            charging_mode: self.charging_mode,
            hours_left: estimate.as_ref().map(|e| e.hours),
            runtime_source: estimate.map(|e| e.source),
            avg_load_w: avg.map(|a| a.round() as u32),
            avg_load_scope: avg.map(|_| scope),
            hours_if_outage,
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

    /// Показания по очереди (последнее повторяется) и режим зарядки, который меняет запись.
    struct Fake {
        reads: Vec<Result<ReadOutcome, MissReason>>,
        charging: Option<ChargingMode>,
        write_err: Option<MissReason>,
        writes: u32,
        /// Чтение зависает навсегда.
        hang: bool,
    }

    impl StationReader for Fake {
        async fn read(&mut self, _: Option<bool>, ts: Ts) -> Result<ReadOutcome, MissReason> {
            if self.hang {
                std::future::pending::<()>().await;
            }
            let next = if self.reads.len() > 1 { self.reads.remove(0) } else { self.reads[0].clone() };
            next.map(|mut o| {
                o.reading.ts = ts;
                o.charging_mode = self.charging;
                o
            })
        }

        async fn write_charging_mode(
            &mut self,
            _: Option<bool>,
            mode: ChargingMode,
        ) -> Result<(), MissReason> {
            self.writes += 1;
            match self.write_err {
                Some(e) => Err(e),
                None => {
                    self.charging = Some(mode);
                    Ok(())
                }
            }
        }

        async fn advertising(&mut self) -> Option<bool> {
            Some(false)
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
            charging_mode: None,
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
        outages: mpsc::UnboundedReceiver<crate::dtek::FinishedOutage>,
        shutdown: mpsc::UnboundedSender<ShutdownRequest>,
    }

    fn harness(reads: Vec<Result<ReadOutcome, MissReason>>) -> Harness {
        let (now, _) = watch::channel(NowView::default());
        let (health, _) = watch::channel(HealthView::default());
        let (events, _) = broadcast::channel(16);
        let (_tx, modes) = mpsc::unbounded_channel();
        let (_check_tx, checks) = mpsc::unbounded_channel();
        let (_charging_tx, charging) = mpsc::unbounded_channel();
        let (shutdown_tx, shutdown) = mpsc::unbounded_channel();
        let (outages_tx, outages_rx) = mpsc::unbounded_channel();
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
            Fake { reads, charging: Some(ChargingMode::Standard), write_err: None, writes: 0, hang: false },
            FakeClock::new(1_000 * MIN),
            power,
            Box::new(FakeHib(hibernations.clone())),
            Box::new(FakeNotify(notes.clone())),
            PathBuf::from("Z:/nonexistent/no-hibernate"),
            Channels { now, health, events, modes, checks, charging, shutdown, outages: Some(outages_tx) },
        );
        Harness { svc, hibernations, notes, outages: outages_rx, shutdown: shutdown_tx }
    }

    #[tokio::test]
    async fn stores_readings_and_misses() {
        let mut h = harness(vec![ok(80, 100), Err(MissReason::Timeout), ok(80, 100)]);
        h.svc.start();
        for _ in 0..3 {
            let _ = h.svc.tick(None).await;
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
            let _ = h.svc.tick(None).await;
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
        // Отключение уходит в выбор очереди ДТЭК.
        assert_eq!(h.outages.try_recv().unwrap(), (o.start_ts, o.end_ts.unwrap()));
        assert!(h.outages.try_recv().is_err());
        let kinds: Vec<String> = h.svc.store.recent_events(50).unwrap().into_iter().map(|e| e.kind).collect();
        assert!(kinds.contains(&"eco-on".to_string()) && kinds.contains(&"eco-off".to_string()), "{kinds:?}");
    }

    #[tokio::test]
    async fn check_answers_with_reading_or_reason() {
        let mut h = harness(vec![ok(70, 0), Err(MissReason::Unreachable)]);
        h.svc.start();
        let (tx, mut rx) = tokio::sync::oneshot::channel();
        h.svc.cycle(None, vec![tx], vec![]).await;
        let v = rx.try_recv().unwrap();
        assert!(v.ok && v.detail.contains("заряд 70 %"), "{v:?}");
        assert_eq!(h.svc.store.last_accepted().unwrap().unwrap().soc, Some(70));

        h.svc.clock.advance(MIN);
        let (tx, mut rx) = tokio::sync::oneshot::channel();
        h.svc.cycle(None, vec![tx], vec![]).await;
        let v = rx.try_recv().unwrap();
        assert!(!v.ok);
        assert_eq!((v.reason, v.advertising), (Some("unreachable"), Some(false)));
        assert_eq!(v.title, "Станция не видна по Bluetooth");
        let kinds: Vec<String> = h.svc.store.recent_events(10).unwrap().into_iter().map(|e| e.kind).collect();
        assert!(kinds.contains(&"check-ok".to_string()) && kinds.contains(&"check-failed".to_string()));
    }

    fn charging_request(
        mode: ChargingMode,
    ) -> (ChargingRequest, tokio::sync::oneshot::Receiver<ChargingView>) {
        let (reply, rx) = tokio::sync::oneshot::channel();
        (ChargingRequest { mode, reply }, rx)
    }

    #[tokio::test]
    async fn charging_mode_written_then_read_back() {
        let mut h = harness(vec![ok(70, 300)]);
        h.svc.start();
        h.svc.tick(None).await.unwrap();
        assert_eq!(h.svc.now_view(h.svc.clock.now()).charging_mode, Some(ChargingMode::Standard));

        // Из двух нажатий подряд пишется последнее, ответ получают оба.
        let (a, mut ra) = charging_request(ChargingMode::Turbo);
        let (b, mut rb) = charging_request(ChargingMode::Silent);
        h.svc.cycle(None, vec![], vec![a, b]).await;
        assert_eq!(h.svc.reader.writes, 1);
        for v in [ra.try_recv().unwrap(), rb.try_recv().unwrap()] {
            assert!(v.ok, "{v:?}");
            assert_eq!(v.charging_mode, Some(ChargingMode::Silent));
        }
        assert_eq!(h.svc.now_view(h.svc.clock.now()).charging_mode, Some(ChargingMode::Silent));
        let kinds: Vec<String> = h.svc.store.recent_events(10).unwrap().into_iter().map(|e| e.kind).collect();
        assert!(kinds.contains(&"charging-mode".to_string()));
    }

    #[tokio::test]
    async fn charging_mode_failure_and_dry_run_leave_station_alone() {
        let mut h = harness(vec![ok(70, 300)]);
        h.svc.reader.write_err = Some(MissReason::Timeout);
        let (r, mut rx) = charging_request(ChargingMode::Turbo);
        h.svc.cycle(None, vec![], vec![r]).await;
        let v = rx.try_recv().unwrap();
        assert!(!v.ok && v.text.contains("не изменён"), "{v:?}");
        assert_eq!(v.charging_mode, Some(ChargingMode::Standard));
        let kinds: Vec<String> = h.svc.store.recent_events(10).unwrap().into_iter().map(|e| e.kind).collect();
        assert!(kinds.contains(&"action-failed".to_string()));

        h.svc.reader.write_err = None;
        h.svc.cfg.dry_run = true;
        let (r, mut rx) = charging_request(ChargingMode::Turbo);
        h.svc.cycle(None, vec![], vec![r]).await;
        assert!(!rx.try_recv().unwrap().ok);
        assert_eq!(h.svc.reader.writes, 1);
        assert_eq!(h.svc.reader.charging, Some(ChargingMode::Standard));
    }

    /// `n` циклов по минуте; возвращает число записей за них.
    async fn ticks(h: &mut Harness, n: usize) -> u32 {
        let before = h.svc.reader.writes;
        for _ in 0..n {
            let _ = h.svc.tick(None).await;
            h.svc.clock.advance(MIN);
        }
        h.svc.reader.writes - before
    }

    fn event_texts(h: &Harness, kind: &str) -> Vec<String> {
        let events = h.svc.store.recent_events(50).unwrap();
        events.into_iter().filter(|e| e.kind == kind).map(|e| e.text).collect()
    }

    /// Отключение (8 мин) и возврат сети: заряд `soc` без изменений.
    fn outage_then_light(soc: u8) -> Vec<Result<ReadOutcome, MissReason>> {
        let mut reads = vec![ok(soc, 300); 2];
        reads.extend(vec![ok(soc, 0); 8]);
        reads.extend(vec![ok(soc, 300); 8]);
        reads
    }

    #[tokio::test]
    async fn light_sets_turbo_and_reports_the_reason() {
        let mut h = harness(outage_then_light(15));
        h.svc.cfg.charging_auto.enabled = true;
        h.svc.start();
        assert_eq!(ticks(&mut h, 18).await, 1);
        assert_eq!(h.svc.reader.charging, Some(ChargingMode::Turbo));
        let texts = event_texts(&h, "charging-mode");
        assert!(texts[0].contains("турбо") && texts[0].contains("свет включился, заряд 15 %"), "{texts:?}");
        // Состояние переживает перезапуск.
        assert!(h.svc.store.get_meta::<ChargingAuto>(AUTO_META).is_some_and(|a| !a.light_pending));
    }

    #[tokio::test]
    async fn rules_write_nothing_when_disabled() {
        let mut h = harness(outage_then_light(15));
        h.svc.start();
        assert_eq!(ticks(&mut h, 18).await, 0);
        assert_eq!(h.svc.reader.charging, Some(ChargingMode::Standard));
    }

    #[tokio::test]
    async fn threshold_written_once_while_charging() {
        let mut h = harness(vec![ok(49, 300), ok(49, 300), ok(50, 300), ok(50, 300), ok(50, 300)]);
        h.svc.reader.charging = Some(ChargingMode::Turbo);
        h.svc.cfg.charging_auto.enabled = true;
        h.svc.start();
        assert_eq!(ticks(&mut h, 5).await, 1);
        assert_eq!(h.svc.reader.charging, Some(ChargingMode::Standard));
    }

    #[tokio::test]
    async fn rejected_or_missing_reading_does_not_fire_rules() {
        // Скачок 49 → 90 отвергается проверкой правдоподобия, пропуск не показание.
        let mut h = harness(vec![ok(49, 300), ok(49, 300), ok(90, 300), Err(MissReason::Timeout)]);
        h.svc.reader.charging = Some(ChargingMode::Turbo);
        h.svc.cfg.charging_auto.enabled = true;
        h.svc.start();
        assert_eq!(ticks(&mut h, 4).await, 0);
    }

    #[tokio::test]
    async fn failed_rule_write_retries_and_dry_run_never_writes() {
        let mut h = harness(outage_then_light(15));
        h.svc.cfg.charging_auto.enabled = true;
        h.svc.reader.write_err = Some(MissReason::Timeout);
        h.svc.start();
        let failed = ticks(&mut h, 18).await;
        assert!(failed >= 2, "повтор по бэкоффу: {failed}");
        assert!(!event_texts(&h, "action-failed").is_empty());
        h.svc.reader.write_err = None;
        h.svc.clock.advance(20 * MIN);
        let _ = ticks(&mut h, 1).await;
        assert_eq!(h.svc.reader.charging, Some(ChargingMode::Turbo));

        let mut h = harness(outage_then_light(15));
        h.svc.cfg.charging_auto.enabled = true;
        h.svc.cfg.dry_run = true;
        h.svc.start();
        assert_eq!(ticks(&mut h, 18).await, 0);
        assert_eq!(h.svc.reader.charging, Some(ChargingMode::Standard));
    }

    #[tokio::test]
    async fn manual_press_cancels_rule_retry() {
        let mut h = harness(outage_then_light(15));
        h.svc.cfg.charging_auto.enabled = true;
        h.svc.reader.write_err = Some(MissReason::Timeout);
        h.svc.start();
        assert!(ticks(&mut h, 18).await > 0);
        assert!(h.svc.auto.want.is_some());
        h.svc.reader.write_err = None;
        let (r, _rx) = charging_request(ChargingMode::Silent);
        h.svc.cycle(None, vec![], vec![r]).await;
        assert_eq!(h.svc.auto.want, None);
        assert_eq!(h.svc.reader.charging, Some(ChargingMode::Silent));
    }

    #[tokio::test]
    async fn shutdown_writes_silent_and_next_cycles_keep_it() {
        let mut h = harness(vec![ok(60, 300)]);
        h.svc.cfg.charging_auto.enabled = true;
        h.svc.start();
        assert_eq!(ticks(&mut h, 5).await, 0);
        let (done, wait) = std::sync::mpsc::channel();
        h.svc.on_shutdown(ShutdownRequest { done }).await;
        assert!(wait.try_recv().is_ok());
        assert_eq!(h.svc.reader.charging, Some(ChargingMode::Silent));
        assert!(event_texts(&h, "charging-mode")[0].contains("выключение ПК"));

        // Пока Windows завершает работу, режим не возвращается; выключение отменено — режим по заряду.
        assert_eq!(ticks(&mut h, 2).await, 0);
        assert_eq!(ticks(&mut h, 1).await, 1);
        assert_eq!(h.svc.reader.charging, Some(ChargingMode::Standard));
    }

    #[tokio::test]
    async fn shutdown_no_write_when_silent_answers_on_failure_and_dry_run() {
        let mut h = harness(vec![ok(60, 300)]);
        h.svc.reader.charging = Some(ChargingMode::Silent);
        h.svc.start();
        let _ = h.svc.tick(None).await;
        let (done, wait) = std::sync::mpsc::channel();
        h.svc.on_shutdown(ShutdownRequest { done }).await;
        assert!(wait.try_recv().is_ok());
        assert_eq!(h.svc.reader.writes, 0);

        let mut h = harness(vec![ok(60, 300)]);
        h.svc.reader.write_err = Some(MissReason::Timeout);
        h.svc.start();
        let (done, wait) = std::sync::mpsc::channel();
        h.svc.on_shutdown(ShutdownRequest { done }).await;
        assert!(wait.try_recv().is_ok());
        assert_eq!(h.svc.reader.writes, 2);
        assert!(!event_texts(&h, "action-failed").is_empty());

        let mut h = harness(vec![ok(60, 300)]);
        h.svc.cfg.dry_run = true;
        let (done, wait) = std::sync::mpsc::channel();
        h.svc.on_shutdown(ShutdownRequest { done }).await;
        assert!(wait.try_recv().is_ok());
        assert_eq!(h.svc.reader.writes, 0);
    }

    #[tokio::test]
    async fn shutdown_interrupts_a_hung_read() {
        let mut h = harness(vec![ok(60, 300)]);
        h.svc.reader.hang = true;
        let tx = h.shutdown.clone();
        let (done, wait) = std::sync::mpsc::channel();
        tokio::select! {
            () = h.svc.run() => unreachable!(),
            () = async {
                tokio::time::sleep(Duration::from_millis(50)).await;
                tx.send(ShutdownRequest { done }).unwrap();
                tokio::time::sleep(Duration::from_millis(100)).await;
            } => {}
        }
        assert!(wait.try_recv().is_ok(), "зависшее чтение задержало ответ");
        assert_eq!(h.svc.reader.charging, Some(ChargingMode::Silent));
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
            let _ = h.svc.tick(None).await;
            h.svc.clock.advance(MIN);
        }
        assert_eq!(*h.hibernations.borrow(), 1);
        assert!(h.notes.borrow().iter().any(|t| t.contains("гибернац")), "{:?}", h.notes.borrow());
        assert!(h.svc.store.outages(1).unwrap()[0].hibernated);
    }
}
