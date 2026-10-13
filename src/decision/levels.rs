//! Уровни батареи и гибернация (FR-022…FR-027; design.md §6). Действуют при любом ручном режиме.

use serde::{Deserialize, Serialize};
use serde_json::json;

use crate::clock::{MIN, SEC, Ts};
use crate::config::Config;
use crate::model::ManualMode;

use super::guards::{backoff_due, backoff_fail, backoff_open};
use super::{Action, DecisionState, Input};

#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum Level {
    Low,
    Critical,
    Warn,
    Hibernate,
}

pub const ALL: [Level; 4] = [Level::Low, Level::Critical, Level::Warn, Level::Hibernate];

/// Серия принятых показаний на батарее не выше уровня: начало и засчитанное время (шаг ≤ 2 × poll).
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Serialize, Deserialize)]
pub struct Streak {
    pub since: Ts,
    pub run_ms: Ts,
}

const HIBERNATE_KEY: &str = "hibernate";
const REFUSED_KEY: &str = "hibernate-refused";
/// Принятое показание позже гибернации на столько — ПК проснулся.
const RESUME_AFTER: Ts = 2 * MIN;

pub fn threshold(l: Level, cfg: &Config) -> u8 {
    match l {
        Level::Low => cfg.levels.low,
        Level::Critical => cfg.levels.critical,
        Level::Warn => cfg.levels.warn,
        Level::Hibernate => cfg.levels.hibernate,
    }
}

/// Скорость разряда с начала отключения, % в минуту; `0` — данных мало (меньше 5 минут или падение < 2 %).
/// Не выше предела правдоподобия: скачок не разгоняет пороги.
pub fn drain_per_min(s: &DecisionState, soc: u8, now: Ts, cfg: &Config) -> f64 {
    let Some(o) = &s.outage else { return 0.0 };
    let (Some(start), elapsed) = (o.soc_start, now - o.start_ts) else { return 0.0 };
    if elapsed < 5 * MIN || start < soc + 2 {
        return 0.0;
    }
    (f64::from(start - soc) / (elapsed as f64 / MIN as f64)).min(cfg.guards.soc_rate_per_min)
}

/// Порог с упреждением по скорости разряда (FR-025a). Пока ждём согласия двух показаний и паузу после
/// предупреждения, быстрый разряд (нагрузка 1800 Вт ≈ 2 %/мин) успевает съесть несколько процентов: порог гибернации
/// поднимается на расход за `agree_sec`, предупреждение — ещё на расход за `hibernate_after_warn_sec + agree_sec`,
/// «мало» и «критично» — не ниже предупреждения.
/// При медленном разряде (< ~0,3 %/мин) пороги равны настроенным.
pub fn effective_threshold(l: Level, rate: f64, cfg: &Config) -> u8 {
    let agree_min = f64::from(cfg.levels.agree_sec) / 60.0;
    let after_warn_min = f64::from(cfg.levels.hibernate_after_warn_sec) / 60.0;
    let hibernate = cfg.levels.hibernate.saturating_add((rate * agree_min).round() as u8).min(100);
    let warn = cfg
        .levels
        .warn
        .max(hibernate.saturating_add((rate * (after_warn_min + agree_min)).round() as u8))
        .min(100);
    // Лестница идёт по одной ступени за цикл: верхние ступени не ниже предупреждения, иначе оно ждёт их.
    match l {
        Level::Hibernate => hibernate,
        Level::Warn => warn,
        Level::Critical => cfg.levels.critical.max(warn),
        Level::Low => cfg.levels.low.max(warn),
    }
}

/// Вид уведомления уровня; по нему `report` узнаёт, какой уровень сработал.
pub fn notify_kind(l: Level) -> Option<&'static str> {
    match l {
        Level::Low => Some("level-low"),
        Level::Critical => Some("level-critical"),
        Level::Warn => Some("hibernate-warn"),
        Level::Hibernate => None,
    }
}

pub fn level_of_kind(kind: &str) -> Option<Level> {
    ALL.into_iter().find(|&l| notify_kind(l) == Some(kind))
}

fn notify_key(kind: &str) -> String {
    format!("notify:{kind}")
}

/// Принятое показание на батарее: серии уровней начинаются, продолжаются или рвутся.
/// Подтверждение после rebase считает показания, продолжающие серию гибернации, но не начинающие её.
pub fn update_streaks(s: &mut DecisionState, soc: u8, now: Ts, step: Ts, cfg: &Config) {
    let hib_ran = s.level_streaks.contains_key(&Level::Hibernate);
    let rate = drain_per_min(s, soc, now, cfg);
    for l in ALL {
        if soc <= effective_threshold(l, rate, cfg) {
            s.level_streaks
                .entry(l)
                .and_modify(|st| st.run_ms += step)
                .or_insert(Streak { since: now, run_ms: 0 });
        } else {
            s.level_streaks.remove(&l);
        }
    }
    let hib_runs = s.level_streaks.contains_key(&Level::Hibernate);
    s.rebase_confirm_ms = if hib_ran && hib_runs { s.rebase_confirm_ms + step } else { 0 };
}

/// Два согласных принятых показания с разницей не меньше `agree_sec`.
pub fn agreed(s: &DecisionState, l: Level, cfg: &Config) -> bool {
    s.level_streaks.get(&l).is_some_and(|st| st.run_ms >= Ts::from(cfg.levels.agree_sec.max(1)) * SEC)
}

/// Проснулись после гибернации: уровень гибернации взводится заново со всеми условиями (согласие, предупреждение
/// за 3 минуты, свежее показание).
pub fn rearm_after_resume(s: &mut DecisionState, now: Ts) {
    if s.hibernated_at.is_some_and(|h| now - h > RESUME_AFTER) {
        s.hibernated_at = None;
        if let Some(o) = &mut s.outage {
            o.levels_reached.retain(|&l| l < Level::Warn);
            o.levels_fired.retain(|&l| l < Level::Warn);
            o.warn_at = None;
            o.hibernate_at = None;
        }
        s.level_streaks.remove(&Level::Warn);
        s.level_streaks.remove(&Level::Hibernate);
        s.backoff.remove(HIBERNATE_KEY);
        s.backoff.remove(&notify_key("hibernate-warn"));
    }
}

/// Лестница уровней на принятом показании от батареи в подтверждённом отключении: один новый уровень за цикл.
pub fn ladder(
    s: &mut DecisionState,
    input: &Input,
    fresh_ok: bool,
    soc: u8,
    now: Ts,
    cfg: &Config,
    out: &mut Vec<Action>,
) {
    retry_notifications(s, soc, now, cfg, out);
    for l in [Level::Low, Level::Critical, Level::Warn] {
        if reached(s, l) {
            continue;
        }
        if agreed(s, l, cfg) {
            reach(s, l, soc, now, cfg, out);
        }
        return;
    }
    hibernate_step(s, input, fresh_ok, now, cfg, out);
}

fn reached(s: &DecisionState, l: Level) -> bool {
    s.outage.as_ref().is_some_and(|o| o.levels_reached.contains(&l))
}

fn fired(s: &DecisionState, l: Level) -> bool {
    s.outage.as_ref().is_some_and(|o| o.levels_fired.contains(&l))
}

fn reach(s: &mut DecisionState, l: Level, soc: u8, now: Ts, cfg: &Config, out: &mut Vec<Action>) {
    let Some(o) = &mut s.outage else { return };
    o.levels_reached.push(l);
    match l {
        Level::Low => {
            o.deep = true;
            out.push(Action::Event {
                kind: "level-low",
                text: format!("Заряд {soc} % — мало"),
                data: Some(json!({ "soc": soc, "deep": s.manual != ManualMode::Max })),
            });
        }
        Level::Critical => out.push(Action::Event {
            kind: "level-critical",
            text: format!("Заряд {soc} % — критично"),
            data: Some(json!({ "soc": soc })),
        }),
        Level::Warn => {
            o.warn_at = Some(now);
            let minutes = warn_minutes(s, soc, now, cfg);
            out.push(Action::Event {
                kind: "hibernate-warn",
                text: format!("Заряд {soc} %: гибернация примерно через {minutes} мин"),
                data: Some(json!({ "soc": soc, "minutes": minutes, "rebase": s.rebase_at.is_some() })),
            });
        }
        Level::Hibernate => {}
    }
    if let Some(n) = notify(s, l, soc, now, cfg) {
        out.push(n);
    }
}

fn notify(s: &DecisionState, l: Level, soc: u8, now: Ts, cfg: &Config) -> Option<Action> {
    let kind = notify_kind(l)?;
    let text = match l {
        Level::Low if s.manual == ManualMode::Max => {
            format!("Заряд {soc} % — мало. Выбран «Максимум»: глубокий eco не включён")
        }
        Level::Low => format!("Заряд {soc} % — включён глубокий eco"),
        Level::Critical => format!("Заряд {soc} % — сохраните работу"),
        _ => {
            let n = warn_minutes(s, soc, now, cfg);
            let tail = if s.rebase_at.is_some() {
                " (после подтверждения заряда)"
            } else {
                ""
            };
            format!("ПК уйдёт в гибернацию примерно через {n} {}{tail}", minutes_word(n))
        }
    };
    Some(Action::Notify { kind, text })
}

/// Уведомление уровня не дошло: повтор по бэкоффу, пока уровень ещё подтверждён.
fn retry_notifications(s: &mut DecisionState, soc: u8, now: Ts, cfg: &Config, out: &mut Vec<Action>) {
    for l in [Level::Low, Level::Critical, Level::Warn] {
        let Some(kind) = notify_kind(l) else { continue };
        if reached(s, l)
            && !fired(s, l)
            && agreed(s, l, cfg)
            && backoff_due(&s.backoff, &notify_key(kind), now)
            && let Some(n) = notify(s, l, soc, now, cfg)
        {
            out.push(n);
        }
    }
}

fn hibernate_step(
    s: &mut DecisionState,
    input: &Input,
    fresh_ok: bool,
    now: Ts,
    cfg: &Config,
    out: &mut Vec<Action>,
) {
    let Some(warn_at) = s.outage.as_ref().and_then(|o| o.warn_at) else { return };
    if fired(s, Level::Hibernate)
        || !agreed(s, Level::Hibernate, cfg)
        || now - warn_at < Ts::from(cfg.levels.hibernate_after_warn_sec) * SEC
    {
        return;
    }
    // После rebase в этом отключении — долгое подтверждение согласными показаниями не выше порога.
    let confirm = Ts::from(cfg.levels.rebase_confirm_sec) * SEC;
    if s.rebase_at.is_some() && s.rebase_confirm_ms < confirm {
        let until = now + confirm - s.rebase_confirm_ms;
        s.hibernate_delayed_until = Some(until);
        if !s.delay_noted {
            s.delay_noted = true;
            let minutes = (confirm - s.rebase_confirm_ms + MIN - 1) / MIN;
            out.push(Action::Event {
                kind: "hibernate-delayed",
                text: format!(
                    "Гибернация отложена примерно на {minutes} мин: заряд после скачка подтверждается {} мин",
                    confirm / MIN
                ),
                data: Some(json!({ "until": until })),
            });
        }
        return;
    }
    if input.kill_switch {
        if backoff_open(&s.backoff, REFUSED_KEY, now) {
            backoff_fail(&mut s.backoff, REFUSED_KEY, now);
            out.push(Action::Event {
                kind: "hibernate-refused",
                text: "Гибернация отменена выключателем no-hibernate".into(),
                data: None,
            });
        }
        return;
    }
    s.backoff.remove(REFUSED_KEY);
    if !backoff_open(&s.backoff, HIBERNATE_KEY, now) {
        return;
    }
    if fresh_ok {
        if let Some(o) = &mut s.outage {
            o.hibernate_at = Some(now);
        }
        out.push(Action::Hibernate);
    } else if !input.fresh {
        s.fresh_pending = true;
        out.push(Action::NeedFreshReading);
    }
}

pub fn report_hibernate(s: &mut DecisionState, ok: bool, now: Ts) {
    if ok {
        let at = s.outage.as_ref().and_then(|o| o.hibernate_at).unwrap_or(now).min(now);
        s.hibernated_at = Some(at);
        if let Some(o) = &mut s.outage
            && !o.levels_fired.contains(&Level::Hibernate)
        {
            o.levels_fired.push(Level::Hibernate);
        }
        s.backoff.remove(HIBERNATE_KEY);
    } else {
        backoff_fail(&mut s.backoff, HIBERNATE_KEY, now);
    }
}

pub fn report_notify(s: &mut DecisionState, kind: &str, ok: bool, now: Ts) {
    let Some(l) = level_of_kind(kind) else { return };
    if ok {
        s.backoff.remove(&notify_key(kind));
        if let Some(o) = &mut s.outage
            && o.levels_reached.contains(&l)
            && !o.levels_fired.contains(&l)
        {
            o.levels_fired.push(l);
        }
    } else {
        backoff_fail(&mut s.backoff, &notify_key(kind), now);
    }
}

/// Оценка для предупреждения: по скорости разряда с начала отключения, не меньше паузы после предупреждения,
/// после rebase — не меньше оставшегося подтверждения.
fn warn_minutes(s: &DecisionState, soc: u8, now: Ts, cfg: &Config) -> i64 {
    let floor = Ts::from(cfg.levels.hibernate_after_warn_sec) * SEC;
    let mut ms = floor;
    if let Some(o) = &s.outage
        && let Some(start_soc) = o.soc_start
        && start_soc > soc
        && now > o.start_ts
    {
        let per_ms = f64::from(start_soc - soc) / (now - o.start_ts) as f64;
        let left = f64::from(soc.saturating_sub(cfg.levels.hibernate)) / per_ms;
        ms = ms.max(left as Ts);
    }
    if s.rebase_at.is_some() {
        ms = ms.max(Ts::from(cfg.levels.rebase_confirm_sec) * SEC - s.rebase_confirm_ms);
    }
    (ms + MIN - 1) / MIN
}

fn minutes_word(n: i64) -> &'static str {
    match (n % 10, n % 100) {
        (1, r) if r != 11 => "минуту",
        (2..=4, r) if !(12..=14).contains(&r) => "минуты",
        _ => "минут",
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::decision::Outage;

    fn cfg() -> Config {
        Config::default()
    }

    fn in_outage() -> DecisionState {
        DecisionState { outage: Some(Outage { confirmed: true, ..Default::default() }), ..Default::default() }
    }

    fn input() -> Input {
        Input {
            reading: Err(crate::model::MissReason::Timeout),
            fresh: false,
            kill_switch: false,
            manual_request: None,
        }
    }

    #[test]
    fn agreement_needs_two_readings_90s_apart() {
        let c = cfg();
        let mut s = in_outage();
        update_streaks(&mut s, 7, 0, 0, &c);
        assert!(!agreed(&s, Level::Warn, &c));
        update_streaks(&mut s, 7, 60 * SEC, 60 * SEC, &c);
        assert!(!agreed(&s, Level::Warn, &c));
        update_streaks(&mut s, 6, 120 * SEC, 60 * SEC, &c);
        assert!(agreed(&s, Level::Warn, &c));
        assert!(agreed(&s, Level::Low, &c));
        assert!(!agreed(&s, Level::Hibernate, &c));
        update_streaks(&mut s, 8, 180 * SEC, 60 * SEC, &c);
        assert!(!agreed(&s, Level::Warn, &c), "показание выше уровня рвёт серию");
    }

    #[test]
    fn one_level_per_cycle() {
        let c = cfg();
        let mut s = in_outage();
        for (i, soc) in [4u8, 4, 4].into_iter().enumerate() {
            update_streaks(&mut s, soc, i as Ts * MIN, if i == 0 { 0 } else { MIN }, &c);
        }
        let mut seen = Vec::new();
        for k in 0..3 {
            let mut out = Vec::new();
            ladder(&mut s, &input(), false, 4, (2 + k) * MIN, &c, &mut out);
            seen.push(out.iter().filter(|a| matches!(a, Action::Event { .. })).count());
        }
        assert_eq!(seen, [1, 1, 1]);
        assert_eq!(s.outage.as_ref().unwrap().levels_reached, [Level::Low, Level::Critical, Level::Warn]);
    }

    #[test]
    fn notify_counts_only_after_success_and_retries_on_backoff() {
        let c = cfg();
        let mut s = in_outage();
        update_streaks(&mut s, 15, 0, 0, &c);
        update_streaks(&mut s, 15, 2 * MIN, 2 * MIN, &c);
        let mut out = Vec::new();
        ladder(&mut s, &input(), false, 15, 2 * MIN, &c, &mut out);
        assert!(out.iter().any(|a| matches!(a, Action::Notify { kind: "level-low", .. })));
        report_notify(&mut s, "level-low", false, 2 * MIN);
        assert!(!fired(&s, Level::Low));
        out.clear();
        ladder(&mut s, &input(), false, 15, 2 * MIN + 30 * SEC, &c, &mut out);
        assert!(out.is_empty());
        ladder(&mut s, &input(), false, 15, 3 * MIN, &c, &mut out);
        assert!(out.iter().any(|a| matches!(a, Action::Notify { kind: "level-low", .. })));
        report_notify(&mut s, "level-low", true, 3 * MIN);
        assert!(fired(&s, Level::Low));
    }

    #[test]
    fn rebase_confirmation_counts_continuing_readings() {
        let c = cfg();
        let mut s = in_outage();
        update_streaks(&mut s, 4, 0, 0, &c);
        assert_eq!(s.rebase_confirm_ms, 0);
        update_streaks(&mut s, 4, MIN, MIN, &c);
        update_streaks(&mut s, 4, 2 * MIN, MIN, &c);
        assert_eq!(s.rebase_confirm_ms, 2 * MIN);
        update_streaks(&mut s, 6, 3 * MIN, MIN, &c);
        assert_eq!(s.rebase_confirm_ms, 0);
    }

    #[test]
    fn thresholds_follow_drain_rate() {
        let c = cfg();
        assert_eq!(effective_threshold(Level::Hibernate, 0.0, &c), 5);
        assert_eq!(effective_threshold(Level::Warn, 0.0, &c), 7);
        assert_eq!(effective_threshold(Level::Warn, 0.25, &c), 7, "медленный разряд — без упреждения");
        assert_eq!(effective_threshold(Level::Hibernate, 1.0, &c), 7);
        assert_eq!(effective_threshold(Level::Warn, 1.0, &c), 12);
        assert_eq!(effective_threshold(Level::Hibernate, 2.0, &c), 8);
        assert_eq!(effective_threshold(Level::Warn, 2.0, &c), 17);
        assert_eq!(effective_threshold(Level::Critical, 2.0, &c), 17, "критично не ниже предупреждения");
        assert_eq!(effective_threshold(Level::Critical, 0.0, &c), 10);
        assert_eq!(effective_threshold(Level::Low, 2.0, &c), 20);
    }

    #[test]
    fn russian_minutes() {
        assert_eq!(minutes_word(1), "минуту");
        assert_eq!(minutes_word(3), "минуты");
        assert_eq!(minutes_word(5), "минут");
        assert_eq!(minutes_word(12), "минут");
        assert_eq!(minutes_word(21), "минуту");
    }
}
