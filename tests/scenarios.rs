//! Сценарии spec.md (US3, US4, Edge Cases) на чистом ядре решений: синтетические показания и явное время,
//! без системных часов. Сервис изображает `Sim`: выполняет действия, сообщает итог и делает свежее чтение.

use bluetti_watcher::clock::{MIN, SEC, Ts};
use bluetti_watcher::config::Config;
use bluetti_watcher::decision::{Action, DecisionState, Input, decide, report, view};
use bluetti_watcher::model::{EcoLevel, Mains, ManualMode, MissReason, ReadStatus, Reading};

const T0: Ts = 1_760_000_000_000;

/// Что вернёт свежее чтение перед гибернацией.
#[derive(Clone, Copy, PartialEq)]
enum Fresh {
    Same,
    Miss,
    Mains,
}

struct Sim {
    s: DecisionState,
    cfg: Config,
    t: Ts,
    kill: bool,
    fresh: Fresh,
    eco_fails: bool,
    /// Время, был ли это свежий вызов, действие.
    log: Vec<(Ts, bool, Action)>,
    statuses: Vec<(Ts, ReadStatus, Option<&'static str>)>,
    applied_eco: EcoLevel,
    hibernations: Vec<(Ts, u8)>,
}

fn reading(t: Ts, soc: Option<u8>, ac_in: Option<u32>) -> Reading {
    Reading {
        ts: t,
        soc,
        ac_in_w: ac_in,
        ac_out_w: Some(180),
        dc_in_w: Some(0),
        dc_out_w: Some(0),
        ac_in_v: ac_in.map(|w| if w > 0 { 230.0 } else { 0.0 }),
    }
}

impl Sim {
    fn new() -> Self {
        Self {
            s: DecisionState::default(),
            cfg: Config::default(),
            t: T0,
            kill: false,
            fresh: Fresh::Same,
            eco_fails: false,
            log: Vec::new(),
            statuses: Vec::new(),
            applied_eco: EcoLevel::None,
            hibernations: Vec::new(),
        }
    }

    /// Один вызов `decide` в момент `self.t` и исполнение действий.
    fn call(
        &mut self,
        r: Result<Reading, MissReason>,
        fresh: bool,
        manual: Option<ManualMode>,
    ) -> Vec<Action> {
        let soc = r.as_ref().ok().and_then(|r| r.soc);
        let input = Input { reading: r, fresh, kill_switch: self.kill, manual_request: manual };
        let d = decide(&mut self.s, &input, self.t, &self.cfg);
        self.statuses.push((self.t, d.status, d.reason));
        for a in &d.actions {
            self.log.push((self.t, fresh, a.clone()));
            match a {
                Action::SetEco(l) => {
                    let ok = !self.eco_fails;
                    if ok {
                        self.applied_eco = *l;
                    }
                    report(&mut self.s, a, ok, self.t);
                }
                Action::Notify { .. } => report(&mut self.s, a, true, self.t),
                Action::Hibernate => {
                    self.hibernations.push((self.t, soc.unwrap_or(255)));
                    report(&mut self.s, a, true, self.t);
                }
                _ => {}
            }
        }
        d.actions
    }

    /// Показание сейчас, при `NeedFreshReading` — свежее через 5 с; затем часы на минуту вперёд.
    fn read_full(&mut self, soc: Option<u8>, ac_in: Option<u32>, manual: Option<ManualMode>) -> Vec<Action> {
        let mut acts = self.call(Ok(reading(self.t, soc, ac_in)), false, manual);
        if acts.contains(&Action::NeedFreshReading) {
            let base = self.t;
            self.t += 5 * SEC;
            let r = match self.fresh {
                Fresh::Same => Ok(reading(self.t, soc, ac_in)),
                Fresh::Miss => Err(MissReason::Timeout),
                Fresh::Mains => Ok(reading(self.t, soc, Some(200))),
            };
            acts.extend(self.call(r, true, None));
            self.t = base;
        }
        self.t += MIN;
        acts
    }

    fn read(&mut self, soc: u8, ac_in: u32) -> Vec<Action> {
        self.read_full(Some(soc), Some(ac_in), None)
    }

    fn batt(&mut self, soc: u8) -> Vec<Action> {
        self.read(soc, 0)
    }

    fn miss(&mut self) -> Vec<Action> {
        let a = self.call(Err(MissReason::Timeout), false, None);
        self.t += MIN;
        a
    }

    /// Сеть, затем 4 показания без входа: отключение подтверждено на 4-м (через 180 с).
    fn start_outage(&mut self, soc: u8) {
        self.read(soc, 150);
        self.read(soc, 150);
        for _ in 0..3 {
            assert!(self.batt(soc).iter().all(|a| !matches!(a, Action::OutageStart { .. })));
        }
        let a = self.batt(soc);
        assert!(a.iter().any(|a| matches!(a, Action::OutageStart { .. })), "{a:?}");
    }

    fn view(&self) -> bluetti_watcher::decision::View {
        view(&self.s, self.t, &self.cfg)
    }

    fn times(&self, pred: impl Fn(&Action) -> bool) -> Vec<Ts> {
        self.log.iter().filter(|(_, _, a)| pred(a)).map(|(t, _, _)| *t).collect()
    }

    fn event_times(&self, kind: &str) -> Vec<Ts> {
        self.times(|a| matches!(a, Action::Event { kind: k, .. } if *k == kind))
    }
}

fn is_event(a: &Action, kind: &str) -> bool {
    matches!(a, Action::Event { kind: k, .. } if *k == kind)
}

fn has_event(acts: &[Action], kind: &str) -> bool {
    acts.iter().any(|a| is_event(a, kind))
}

fn has_notify(acts: &[Action], kind: &str) -> bool {
    acts.iter().any(|a| matches!(a, Action::Notify { kind: k, .. } if *k == kind))
}

fn hib_or_fresh(acts: &[Action]) -> bool {
    acts.iter().any(|a| matches!(a, Action::Hibernate | Action::NeedFreshReading))
}

/// Отключение на 9 %: «мало» и «критично» уже пройдены, следующий уровень — предупреждение.
fn outage_past_critical() -> Sim {
    let mut sim = Sim::new();
    sim.start_outage(9);
    sim.batt(9);
    assert!(sim.view().level_low && sim.view().level_critical);
    sim
}

// ---------- US4: гибернация ----------

#[test]
fn us4_1_warning_at_7_after_two_readings_90s_apart() {
    let mut sim = outage_past_critical();
    assert!(!has_event(&sim.batt(7), "hibernate-warn"));
    assert!(!has_event(&sim.batt(7), "hibernate-warn"), "60 с — ещё не согласие");
    let a = sim.batt(7);
    assert!(has_event(&a, "hibernate-warn"));
    let text = a
        .iter()
        .find_map(|a| match a {
            Action::Notify { kind: "hibernate-warn", text } => Some(text.clone()),
            _ => None,
        })
        .expect("уведомление о гибернации");
    assert!(text.starts_with("ПК уйдёт в гибернацию примерно через "), "{text}");
    assert!(sim.view().level_warn);
}

#[test]
fn us4_2_hibernate_at_5_not_before_3_min_after_warning_and_only_on_fresh() {
    let mut sim = outage_past_critical();
    sim.batt(7);
    sim.batt(6);
    let a = sim.batt(5); // предупреждение; серия 5 % начинается здесь
    assert!(has_event(&a, "hibernate-warn"));
    let warn_at = sim.event_times("hibernate-warn")[0];
    assert!(!hib_or_fresh(&sim.batt(5)));
    assert!(!hib_or_fresh(&sim.batt(5)), "согласие есть, но после предупреждения только 120 с");
    let a = sim.batt(5);
    assert_eq!(sim.hibernations.len(), 1, "{a:?}");
    let need = sim.times(|a| *a == Action::NeedFreshReading);
    let hib: Vec<_> = sim.log.iter().filter(|(_, _, a)| *a == Action::Hibernate).collect();
    assert_eq!(need.len(), 1);
    assert!(hib[0].1, "гибернация только в ответ на свежее показание");
    assert!(hib[0].0 > need[0]);
    assert!(hib[0].0 - warn_at >= 180 * SEC);
}

#[test]
fn us4_3_no_fresh_reading_no_hibernate_retry_next_reading() {
    let mut sim = outage_past_critical();
    sim.fresh = Fresh::Miss;
    for soc in [7, 6, 5, 5, 5, 5] {
        sim.batt(soc);
    }
    assert!(sim.hibernations.is_empty());
    let need = sim.times(|a| *a == Action::NeedFreshReading);
    assert_eq!(need.len(), 1);
    assert!(sim.batt(5).contains(&Action::NeedFreshReading), "повтор на следующем показании");
    assert!(sim.hibernations.is_empty());

    // Свежее показание видит сеть: гибернации нет, повтор позже.
    sim.fresh = Fresh::Mains;
    sim.batt(5);
    assert!(sim.hibernations.is_empty());
    sim.fresh = Fresh::Same;
    for _ in 0..4 {
        sim.batt(5);
    }
    assert_eq!(sim.hibernations.len(), 1);
}

#[test]
fn us4_3_fresh_flag_without_request_never_hibernates() {
    let mut sim = outage_past_critical();
    for soc in [7, 6, 5, 5] {
        sim.batt(soc);
    }
    // Сервис сам прислал fresh=true без запроса: ни гибернации, ни нового запроса.
    let t = sim.t;
    let a = sim.call(Ok(reading(t, Some(5), Some(0))), true, None);
    assert!(!hib_or_fresh(&a), "{a:?}");
}

#[test]
fn us4_4_kill_switch_refuses_with_backoff() {
    let mut sim = outage_past_critical();
    sim.kill = true;
    for soc in [7, 6, 5, 5, 5] {
        sim.batt(soc);
    }
    for _ in 0..10 {
        sim.batt(5);
    }
    assert!(sim.hibernations.is_empty());
    assert!(sim.times(|a| *a == Action::NeedFreshReading).is_empty());
    let refused = sim.event_times("hibernate-refused");
    // Через 0, 1, 3, 7 мин после первого отказа (бэкофф 1, 2, 4).
    let rel: Vec<Ts> = refused.iter().map(|t| (t - refused[0]) / MIN).collect();
    assert_eq!(rel, [0, 1, 3, 7]);
    // Файл убран — слышно на следующем цикле.
    sim.kill = false;
    sim.batt(5);
    assert_eq!(sim.hibernations.len(), 1);
}

#[test]
fn us4_5_jump_60_to_3_rejected_then_long_confirmation_after_rebase() {
    let mut sim = Sim::new();
    sim.start_outage(60);
    sim.batt(60);
    let rej = sim.statuses.len();
    sim.batt(3);
    sim.batt(3);
    sim.batt(3);
    let st: Vec<_> = sim.statuses[rej..].iter().map(|(_, s, r)| (*s, *r)).collect();
    assert_eq!(
        st,
        [
            (ReadStatus::Rejected, Some("implausible")),
            (ReadStatus::Rejected, Some("implausible")),
            (ReadStatus::Rebased, None)
        ]
    );
    let first_counted = sim.t;
    let mut delayed_seen = false;
    for _ in 0..15 {
        sim.batt(3);
        if sim.hibernations.is_empty() && sim.view().hibernate_delayed_until.is_some() {
            delayed_seen = true;
        }
    }
    assert!(delayed_seen);
    assert_eq!(sim.event_times("hibernate-delayed").len(), 1, "одно событие на rebase");
    assert_eq!(sim.hibernations.len(), 1);
    let warn_at = sim.event_times("hibernate-warn")[0];
    let hib_at = sim.hibernations[0].0;
    assert!(hib_at - warn_at >= 180 * SEC);
    assert!(hib_at - first_counted >= 10 * MIN, "{}", (hib_at - first_counted) / SEC);
    assert!(hib_at - first_counted < 11 * MIN);
}

#[test]
fn us4_5_single_bogus_reading_never_hibernates() {
    let mut sim = Sim::new();
    sim.start_outage(60);
    for i in 0..30 {
        let soc = if i % 7 == 3 { 2 } else { 60 };
        sim.batt(soc);
    }
    assert!(sim.hibernations.is_empty());
    assert!(sim.event_times("hibernate-warn").is_empty());
    assert!(sim.event_times("level-low").is_empty());
}

#[test]
fn us4_6_hibernates_again_after_resume() {
    let mut sim = outage_past_critical();
    for soc in [7, 6, 5, 5, 5, 5] {
        sim.batt(soc);
    }
    assert_eq!(sim.hibernations.len(), 1);
    // ПК спал 40 минут; станция всё ещё на батарее, 4 %.
    sim.t += 40 * MIN;
    let resumed = sim.t;
    for _ in 0..12 {
        sim.batt(4);
    }
    assert_eq!(sim.hibernations.len(), 2);
    let warns = sim.event_times("hibernate-warn");
    assert_eq!(warns.len(), 2, "то же предупреждение");
    assert!(warns[1] > resumed);
    assert!(sim.hibernations[1].0 - warns[1] >= 180 * SEC);
    assert_eq!(sim.times(|a| matches!(a, Action::OutageStart { .. })).len(), 1, "отключение продолжается");
}

#[test]
fn us4_6_no_rehibernate_within_two_minutes() {
    let mut sim = outage_past_critical();
    for soc in [7, 6, 5, 5, 5, 5] {
        sim.batt(soc);
    }
    assert_eq!(sim.hibernations.len(), 1);
    sim.batt(5); // через минуту: ещё не пробуждение
    sim.batt(5);
    assert_eq!(sim.hibernations.len(), 1);
}

/// SC-004: разряд 25 % → 4 %, одно показание в минуту; гибернация ровно одна, после предупреждения, до < 3 %,
/// ложное показание 0 % и неполное по пути ничего не запускают.
fn drain(minutes_per_percent: u32) -> Sim {
    drain_rate(25, 1, minutes_per_percent)
}

/// Разряд от `start` % по `percent` процентов за `minutes` минут (2/1 — полная нагрузка 1800 Вт).
fn drain_rate(start: u8, percent: u32, minutes: u32) -> Sim {
    let mut sim = Sim::new();
    sim.read(start, 150);
    sim.read(start, 150);
    let mut i = 0u32;
    loop {
        let soc = i32::from(start) - (i * percent / minutes) as i32;
        let soc = soc.max(0) as u8;
        if soc < 4 {
            break;
        }
        i += 1;
        if !sim.hibernations.is_empty() {
            break; // ПК спит
        }
        if i == 30 {
            sim.batt(0); // ложное показание
            continue;
        }
        if i == 31 {
            sim.read_full(None, Some(0), None);
            continue;
        }
        sim.batt(soc);
    }
    sim
}

#[test]
fn sc004_drain_hibernates_exactly_once_before_3_percent() {
    for mpp in [2, 3, 4] {
        let sim = drain(mpp);
        assert_eq!(sim.hibernations.len(), 1, "{mpp} мин на процент");
        let (hib_at, soc) = sim.hibernations[0];
        assert!((3..=5).contains(&soc), "гибернация на {soc} %");
        let warns = sim.event_times("hibernate-warn");
        assert_eq!(warns.len(), 1);
        assert!(hib_at - warns[0] >= 180 * SEC);
        assert_eq!(sim.event_times("level-low").len(), 1);
        assert_eq!(sim.event_times("level-critical").len(), 1);
        assert!(sim.statuses.iter().any(|(_, s, r)| *s == ReadStatus::Rejected && *r == Some("implausible")));
        assert!(sim.statuses.iter().any(|(_, s, r)| *s == ReadStatus::Rejected && *r == Some("incomplete")));
        assert!(sim.event_times("hibernate-delayed").is_empty(), "одиночное ложное показание — не rebase");
    }
}

/// Быстрый разряд (FR-025a): пороги поднимаются заранее, гибернация не позже 3 %.
#[test]
fn sc004_fast_drain_hibernates_before_3_percent() {
    for (start, pct, min) in [(25, 1, 1), (50, 2, 1)] {
        let sim = drain_rate(start, pct, min);
        assert_eq!(sim.hibernations.len(), 1, "{pct} % за {min} мин");
        let (hib_at, soc) = sim.hibernations[0];
        assert!(soc >= 3, "{pct} %/мин: гибернация на {soc} %");
        let warns = sim.event_times("hibernate-warn");
        assert_eq!(warns.len(), 1);
        assert!(hib_at - warns[0] >= 180 * SEC);
    }
}

#[test]
fn sc004_drain_with_kill_switch_never_hibernates() {
    let mut sim = Sim::new();
    sim.kill = true;
    sim.read(25, 150);
    for i in 0..70u32 {
        sim.batt(25 - (i / 3) as u8);
    }
    assert!(sim.hibernations.is_empty());
    assert!(!sim.event_times("hibernate-refused").is_empty());
}

// ---------- US3: eco ----------

#[test]
fn us3_1_eco_after_3_minutes_without_ac_input() {
    let mut sim = Sim::new();
    sim.read(80, 150);
    let off_at = sim.t;
    for k in 0..3 {
        let a = sim.batt(80);
        assert!(!a.iter().any(|a| matches!(a, Action::SetEco(_))));
        if k == 0 {
            assert_eq!(view(&sim.s, off_at + 30 * SEC, &sim.cfg).eco_at, Some(off_at + 180 * SEC));
        }
    }
    let a = sim.batt(80);
    assert!(a.contains(&Action::SetEco(EcoLevel::Eco)));
    assert!(a.contains(&Action::OutageStart { start_ts: off_at, soc: Some(80) }));
    assert!(has_event(&a, "outage-start"));
    assert_eq!(sim.applied_eco, EcoLevel::Eco);
    assert_eq!(sim.view().outage_since, Some(off_at));
    assert_eq!(sim.view().eco_at, None);
}

#[test]
fn us3_2_eco_lifted_after_3_minutes_with_ac_input() {
    let mut sim = Sim::new();
    sim.start_outage(80);
    let back_at = sim.t;
    for _ in 0..3 {
        assert!(!sim.read(79, 150).iter().any(|a| matches!(a, Action::SetEco(_) | Action::OutageEnd { .. })));
    }
    let a = sim.read(79, 150);
    assert!(a.contains(&Action::SetEco(EcoLevel::None)));
    assert!(a.contains(&Action::OutageEnd { end_ts: back_at, soc: Some(79) }));
    assert!(has_event(&a, "outage-end"));
    assert_eq!(sim.applied_eco, EcoLevel::None);
    assert_eq!(sim.view().outage_since, None);
}

#[test]
fn us3_3_one_minute_dip_does_not_start_eco() {
    let mut sim = Sim::new();
    for ac in [150, 150, 0, 150, 150, 150, 150, 0, 0, 150, 150] {
        let a = sim.read(80, ac);
        assert!(!a.iter().any(|a| matches!(a, Action::SetEco(_) | Action::OutageStart { .. })), "{a:?}");
    }
}

#[test]
fn us3_4_max_lifts_eco_until_mains_change_or_auto() {
    let mut sim = Sim::new();
    sim.start_outage(80);
    let a = sim.read_full(Some(80), Some(0), Some(ManualMode::Max));
    assert!(a.contains(&Action::SetEco(EcoLevel::None)));
    assert!(has_event(&a, "mode-manual"));
    for _ in 0..5 {
        assert!(!sim.batt(79).iter().any(|a| matches!(a, Action::SetEco(_))));
    }
    assert_eq!(sim.view().manual, ManualMode::Max);
    let a = sim.read_full(Some(79), Some(0), Some(ManualMode::Auto));
    assert!(a.contains(&Action::SetEco(EcoLevel::Eco)));
    // «Максимум» снова, затем сеть вернулась — выбор сброшен в «Авто».
    sim.read_full(Some(79), Some(0), Some(ManualMode::Max));
    assert_eq!(sim.applied_eco, EcoLevel::None);
    for _ in 0..4 {
        sim.read(79, 150);
    }
    assert_eq!(sim.view().manual, ManualMode::Auto);
    assert_eq!(sim.applied_eco, EcoLevel::None);
    // Следующее отключение — снова eco.
    for _ in 0..4 {
        sim.batt(78);
    }
    assert_eq!(sim.applied_eco, EcoLevel::Eco);
}

#[test]
fn us3_4_manual_applies_on_a_miss_cycle_too() {
    let mut sim = Sim::new();
    sim.start_outage(80);
    let t = sim.t;
    let a = sim.call(Err(MissReason::BleOff), false, Some(ManualMode::Max));
    assert!(a.contains(&Action::SetEco(EcoLevel::None)));
    assert_eq!(sim.statuses.last().unwrap(), &(t, ReadStatus::Miss, Some("ble-off")));
}

#[test]
fn us3_manual_eco_on_mains_holds_until_mains_change() {
    let mut sim = Sim::new();
    sim.read(90, 150);
    let a = sim.read_full(Some(90), Some(150), Some(ManualMode::Eco));
    assert!(a.contains(&Action::SetEco(EcoLevel::Eco)));
    for _ in 0..4 {
        sim.batt(90);
    }
    assert_eq!(sim.view().manual, ManualMode::Auto, "смена сети сбрасывает ручной выбор");
    assert_eq!(sim.applied_eco, EcoLevel::Eco, "eco остаётся — идёт отключение");
    assert_eq!(sim.view().eco_at, None);
    for _ in 0..4 {
        sim.read(90, 150);
    }
    assert_eq!(sim.applied_eco, EcoLevel::None);
}

#[test]
fn us3_6_deep_eco_and_notification_at_20() {
    let mut sim = Sim::new();
    sim.start_outage(22);
    sim.batt(21);
    assert!(!sim.batt(20).iter().any(|a| matches!(a, Action::SetEco(EcoLevel::Deep))));
    sim.batt(20);
    let a = sim.batt(20);
    assert!(a.contains(&Action::SetEco(EcoLevel::Deep)));
    assert!(has_notify(&a, "level-low"));
    assert!(has_event(&a, "level-low"));
    assert_eq!(sim.applied_eco, EcoLevel::Deep);
    assert!(sim.view().level_low);
}

#[test]
fn us3_6_max_blocks_deep_eco_but_levels_and_hibernate_act() {
    let mut sim = Sim::new();
    sim.start_outage(22);
    sim.read_full(Some(22), Some(0), Some(ManualMode::Max));
    for i in 0..60u32 {
        if !sim.hibernations.is_empty() {
            break;
        }
        sim.batt(21 - (i / 3).min(17) as u8);
    }
    assert!(!sim.log.iter().any(|(_, _, a)| *a == Action::SetEco(EcoLevel::Deep)));
    assert_eq!(sim.applied_eco, EcoLevel::None);
    let low = sim
        .log
        .iter()
        .find_map(|(_, _, a)| match a {
            Action::Notify { kind: "level-low", text } => Some(text.clone()),
            _ => None,
        })
        .unwrap();
    assert!(low.contains("Максимум"), "{low}");
    assert_eq!(sim.event_times("level-critical").len(), 1);
    assert_eq!(sim.event_times("hibernate-warn").len(), 1);
    assert_eq!(sim.hibernations.len(), 1);
}

#[test]
fn us3_eco_failure_backs_off() {
    let mut sim = Sim::new();
    sim.eco_fails = true;
    sim.start_outage(80);
    for _ in 0..16 {
        sim.batt(80);
    }
    let tries = sim.times(|a| *a == Action::SetEco(EcoLevel::Eco));
    let rel: Vec<Ts> = tries.iter().map(|t| (t - tries[0]) / MIN).collect();
    assert_eq!(rel, [0, 1, 3, 7, 15]);
    sim.eco_fails = false;
    for _ in 0..10 {
        sim.batt(80);
    }
    assert_eq!(sim.applied_eco, EcoLevel::Eco);
}

// ---------- Edge Cases ----------

#[test]
fn edge_incomplete_reading_takes_no_part() {
    let mut sim = Sim::new();
    sim.read(80, 150);
    sim.batt(80);
    sim.batt(80);
    // Без заряда, со входом AC: не сбрасывает таймер отключения.
    sim.read_full(None, Some(150), None);
    assert_eq!(sim.statuses.last().unwrap().1, ReadStatus::Rejected);
    assert_eq!(sim.statuses.last().unwrap().2, Some("incomplete"));
    // Без входа AC — тоже неполное.
    sim.read_full(Some(80), None, None);
    assert_eq!(sim.statuses.last().unwrap().2, Some("incomplete"));
    let a = sim.batt(80);
    assert!(a.iter().any(|a| matches!(a, Action::OutageStart { .. })), "{a:?}");
}

#[test]
fn edge_miss_neither_advances_nor_resets_timers() {
    let mut sim = Sim::new();
    sim.read(80, 150);
    sim.batt(80);
    sim.miss();
    assert_eq!(sim.statuses.last().unwrap().1, ReadStatus::Miss);
    sim.batt(80); // шаг 120 с (2 интервала): 60 + 120
    let a = sim.batt(80);
    assert!(a.iter().any(|a| matches!(a, Action::OutageStart { .. })));
    assert_eq!(sim.view().mains, Mains::Off);
}

#[test]
fn edge_gap_over_600s_resets_state() {
    let mut sim = Sim::new();
    sim.start_outage(18);
    sim.batt(18);
    assert!(sim.view().level_low);
    sim.t += 11 * MIN;
    assert_eq!(sim.view().mains, Mains::Unknown, "станция давно молчит");
    let a = sim.batt(18);
    assert!(!sim.view().level_low, "уровни сброшены");
    assert!(!a.iter().any(|a| matches!(a, Action::SetEco(_))), "eco не дёргается: {a:?}");
    for _ in 0..3 {
        sim.batt(18);
    }
    assert_eq!(sim.times(|a| matches!(a, Action::OutageStart { .. })).len(), 1, "отключение то же");
    assert_eq!(sim.event_times("level-low").len(), 2, "уровень решается заново");
    assert_eq!(sim.applied_eco, EcoLevel::Deep);
}

#[test]
fn edge_gap_before_outage_restarts_timer() {
    let mut sim = Sim::new();
    sim.read(80, 150);
    sim.batt(80);
    sim.batt(80);
    sim.t += 11 * MIN;
    sim.batt(80);
    assert!(sim.s.outage.is_none());
    sim.batt(80);
    sim.batt(80);
    assert!(sim.s.outage.is_none());
    sim.batt(80);
    assert!(sim.s.outage.is_some());
}

#[test]
fn edge_clock_back_resets_forward_counts_one_interval() {
    let mut sim = Sim::new();
    sim.read(80, 150);
    sim.batt(80);
    sim.batt(80);
    sim.batt(80);
    sim.t -= 60 * MIN; // часы назад
    let a = sim.batt(80);
    assert_eq!(sim.statuses.last().unwrap().1, ReadStatus::Accepted);
    assert!(!a.iter().any(|a| matches!(a, Action::OutageStart { .. })), "часы назад — сброс таймеров");
    sim.batt(80);
    sim.batt(80);
    assert!(sim.s.outage.is_none());
    sim.batt(80);
    assert!(sim.s.outage.is_some());

    // Вперёд на 5 минут: засчитано 120 с, а не 300.
    let mut sim = Sim::new();
    sim.read(80, 150);
    sim.batt(80);
    sim.t += 5 * MIN;
    let a = sim.batt(80);
    assert!(!a.iter().any(|a| matches!(a, Action::OutageStart { .. })));
    let a = sim.batt(80);
    assert!(a.iter().any(|a| matches!(a, Action::OutageStart { .. })));
}

#[test]
fn edge_dc_input_with_zero_ac_is_battery() {
    let mut sim = Sim::new();
    sim.read(80, 150);
    for _ in 0..4 {
        let t = sim.t;
        let mut r = reading(t, Some(80), Some(0));
        r.dc_in_w = Some(300);
        sim.call(Ok(r), false, None);
        sim.t += MIN;
    }
    assert!(sim.s.outage.is_some());
    assert_eq!(sim.view().mains, Mains::Off);
    assert_eq!(sim.applied_eco, EcoLevel::Eco);
}
