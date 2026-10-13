//! Проверки показаний (FR-004, FR-006): полнота, правдоподобие, rebase, сброс по пропуску и скачкам часов,
//! бэкофф неудачных действий (design.md §3, §6).

use std::collections::BTreeMap;

use serde::{Deserialize, Serialize};

use crate::clock::{MIN, SEC, Ts};
use crate::config::Config;
use crate::model::{MissReason, Reading};

use super::{DecisionState, Outage};

/// Что делать с полным или неполным показанием.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Verdict {
    /// Принято. `jumped` — дальше от прошлого, чем допускает темп за 2 интервала опроса: широкий допуск дал только
    /// пропуск, база сдвинулась как при rebase (правило 10 минут для гибернации), но показание участвует в уровнях.
    Accepted {
        jumped: bool,
    },
    /// Три согласных отвергнутых подряд: новая точка отсчёта, не участвует ни в уровнях, ни в таймерах.
    Rebased,
    Rejected(&'static str),
}

/// Допустимое изменение заряда за `elapsed`: `max(soc_jump_floor, soc_rate_per_min × минуты)`; часы назад — только пол.
pub fn within_rate(soc0: u8, soc1: u8, elapsed: Ts, cfg: &Config) -> bool {
    let minutes = elapsed.max(0) as f64 / MIN as f64;
    let allowed = f64::from(cfg.guards.soc_jump_floor).max(cfg.guards.soc_rate_per_min * minutes);
    f64::from(soc0.abs_diff(soc1)) <= allowed
}

/// Длина одного интервала для таймеров и подтверждений: не больше 2 × poll, скачок часов вперёд — один интервал.
pub fn step_cap(cfg: &Config) -> Ts {
    2 * Ts::from(cfg.poll_sec) * SEC
}

/// Классифицирует полное или неполное показание относительно последнего принятого; ведёт серию отвергнутых.
pub fn classify(s: &mut DecisionState, r: &Reading, now: Ts, cfg: &Config) -> Verdict {
    let Some(soc) = r.soc.filter(|_| r.is_complete()) else {
        s.rejected_streak.clear();
        return Verdict::Rejected(MissReason::Incomplete.code());
    };
    let Some((last_soc, last_ts)) = s.last_accepted.as_ref().and_then(|l| Some((l.soc?, l.ts))) else {
        return Verdict::Accepted { jumped: false };
    };
    let elapsed = now - last_ts;
    if within_rate(last_soc, soc, elapsed, cfg) {
        let jumped = !within_rate(last_soc, soc, elapsed.min(step_cap(cfg)), cfg);
        return Verdict::Accepted { jumped };
    }
    // Неправдоподобное: копим серию из последних трёх; согласные между собой — новая точка отсчёта.
    let mut stored = r.clone();
    stored.ts = now;
    s.rejected_streak.push(stored);
    if s.rejected_streak.len() > 3 {
        s.rejected_streak.remove(0);
    }
    if three_agree(&s.rejected_streak, cfg) {
        Verdict::Rebased
    } else {
        Verdict::Rejected(MissReason::Implausible.code())
    }
}

fn three_agree(streak: &[Reading], cfg: &Config) -> bool {
    streak.len() == 3
        && streak.windows(2).all(|w| match (w[0].soc, w[1].soc) {
            (Some(a), Some(b)) => within_rate(a, b, w[1].ts - w[0].ts, cfg),
            _ => false,
        })
}

/// Принятое в `now` показание начинает решения заново: пропуск больше `max_gap_sec` или часы назад.
pub fn starts_over(s: &DecisionState, now: Ts, cfg: &Config) -> bool {
    s.last_accepted
        .as_ref()
        .is_some_and(|l| now < l.ts || now - l.ts > Ts::from(cfg.guards.max_gap_sec) * SEC)
}

/// Сброс по пропуску или часам назад. Остаются: ручной выбор, eco, бэкофф, база правдоподобия, запомненный rebase
/// (часы назад переносят его на `now`), гибернация для правила пробуждения и сам факт отключения — найти его снова
/// после пропуска не смена сети. Таймеры сети, серии и уровни начинаются заново.
pub fn reset(s: &mut DecisionState, now: Ts) {
    let backward = s.last_accepted.as_ref().is_some_and(|l| now < l.ts);
    let old = std::mem::take(s);
    *s = DecisionState {
        last_accepted: old.last_accepted,
        manual: old.manual,
        manual_at: old.manual_at,
        eco: old.eco,
        eco_applied: old.eco_applied,
        backoff: old.backoff,
        rebase_at: if backward { old.rebase_at.map(|_| now) } else { old.rebase_at },
        delay_noted: old.delay_noted,
        hibernated_at: old.hibernated_at,
        outage: old.outage.map(|o| Outage {
            start_ts: o.start_ts,
            soc_start: o.soc_start,
            deep: o.deep,
            confirmed: false,
            ..Default::default()
        }),
        ..Default::default()
    };
}

/// База сдвинулась (rebase или скачок после пропуска): гибернация ждёт долгое подтверждение заново.
pub fn baseline_moved(s: &mut DecisionState, now: Ts) {
    s.rebase_at = Some(now);
    s.delay_noted = false;
    s.rebase_confirm_ms = 0;
}

/// Неудачи действия одного вида: следующая попытка через 1, 2, 4, 8 мин, затем каждые 10 мин.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Serialize, Deserialize)]
pub struct Backoff {
    pub failures: u32,
    pub next_try_ts: Ts,
}

const DELAYS_MIN: [Ts; 5] = [1, 2, 4, 8, 10];

pub fn backoff_delay(failures: u32) -> Ts {
    DELAYS_MIN[(failures.clamp(1, 5) - 1) as usize] * MIN
}

/// Действие можно повторить: неудач не было или пауза прошла.
pub fn backoff_open(map: &BTreeMap<String, Backoff>, key: &str, now: Ts) -> bool {
    map.get(key).is_none_or(|b| now >= b.next_try_ts)
}

/// Была неудача и пауза прошла: пора повторить.
pub fn backoff_due(map: &BTreeMap<String, Backoff>, key: &str, now: Ts) -> bool {
    map.get(key).is_some_and(|b| now >= b.next_try_ts)
}

pub fn backoff_fail(map: &mut BTreeMap<String, Backoff>, key: &str, now: Ts) -> Ts {
    let failures = map.get(key).map_or(1, |b| b.failures + 1);
    let next_try_ts = now + backoff_delay(failures);
    map.insert(key.to_string(), Backoff { failures, next_try_ts });
    next_try_ts
}

#[cfg(test)]
mod tests {
    use super::*;

    fn cfg() -> Config {
        Config::default()
    }

    fn rd(ts: Ts, soc: u8) -> Reading {
        Reading { ts, soc: Some(soc), ac_in_w: Some(0), ..Default::default() }
    }

    fn with_last(ts: Ts, soc: u8) -> DecisionState {
        DecisionState { last_accepted: Some(rd(ts, soc)), ..Default::default() }
    }

    #[test]
    fn rate_floor_is_three_then_five_per_minute() {
        let c = cfg();
        assert!(within_rate(50, 47, 0, &c));
        assert!(!within_rate(50, 46, 0, &c));
        assert!(within_rate(50, 45, MIN, &c));
        assert!(!within_rate(50, 44, MIN, &c));
        assert!(within_rate(50, 40, 2 * MIN, &c));
        assert!(!within_rate(50, 39, 2 * MIN, &c));
        assert!(within_rate(40, 50, 2 * MIN, &c), "рост заряда проверяется так же");
    }

    #[test]
    fn backward_time_gives_only_the_floor() {
        let c = cfg();
        assert!(within_rate(50, 47, -10 * MIN, &c));
        assert!(!within_rate(50, 40, -10 * MIN, &c));
    }

    #[test]
    fn incomplete_is_rejected_and_breaks_the_rebase_streak() {
        let c = cfg();
        let mut s = with_last(0, 60);
        assert_eq!(classify(&mut s, &rd(MIN, 3), MIN, &c), Verdict::Rejected("implausible"));
        let no_soc = Reading { ts: 2 * MIN, ac_in_w: Some(0), ..Default::default() };
        assert_eq!(classify(&mut s, &no_soc, 2 * MIN, &c), Verdict::Rejected("incomplete"));
        let no_ac = Reading { ts: 2 * MIN, soc: Some(60), ..Default::default() };
        assert_eq!(classify(&mut s, &no_ac, 2 * MIN, &c), Verdict::Rejected("incomplete"));
        assert!(s.rejected_streak.is_empty());
    }

    #[test]
    fn first_reading_is_accepted() {
        let mut s = DecisionState::default();
        assert_eq!(classify(&mut s, &rd(0, 3), 0, &cfg()), Verdict::Accepted { jumped: false });
    }

    #[test]
    fn three_agreeing_rejected_rebase() {
        let c = cfg();
        let mut s = with_last(0, 60);
        assert_eq!(classify(&mut s, &rd(MIN, 3), MIN, &c), Verdict::Rejected("implausible"));
        assert_eq!(classify(&mut s, &rd(2 * MIN, 3), 2 * MIN, &c), Verdict::Rejected("implausible"));
        assert_eq!(classify(&mut s, &rd(3 * MIN, 2), 3 * MIN, &c), Verdict::Rebased);
    }

    #[test]
    fn disagreeing_rejected_do_not_rebase() {
        let c = cfg();
        let mut s = with_last(0, 60);
        classify(&mut s, &rd(MIN, 3), MIN, &c);
        classify(&mut s, &rd(2 * MIN, 95), 2 * MIN, &c);
        assert_eq!(classify(&mut s, &rd(3 * MIN, 3), 3 * MIN, &c), Verdict::Rejected("implausible"));
        // Последние три: 95, 3, 3 — первая пара не согласна.
        assert_eq!(classify(&mut s, &rd(4 * MIN, 3), 4 * MIN, &c), Verdict::Rejected("implausible"));
        assert_eq!(s.rejected_streak.len(), 3);
        assert_eq!(classify(&mut s, &rd(5 * MIN, 3), 5 * MIN, &c), Verdict::Rebased);
    }

    #[test]
    fn jump_after_silence_is_accepted_but_moves_the_baseline() {
        let c = cfg();
        // 8 минут пропусков: допуск 40 %, но за 2 интервала только 10 %.
        let mut s = with_last(0, 50);
        assert_eq!(classify(&mut s, &rd(8 * MIN, 20), 8 * MIN, &c), Verdict::Accepted { jumped: true });
        let mut s = with_last(0, 50);
        assert_eq!(classify(&mut s, &rd(8 * MIN, 42), 8 * MIN, &c), Verdict::Accepted { jumped: false });
        let mut s = with_last(0, 50);
        assert_eq!(classify(&mut s, &rd(MIN, 45), MIN, &c), Verdict::Accepted { jumped: false });
    }

    #[test]
    fn gap_and_backward_clock_start_over() {
        let c = cfg();
        let s = with_last(100 * MIN, 50);
        assert!(!starts_over(&s, 110 * MIN, &c), "ровно 600 с — ещё не пропуск");
        assert!(starts_over(&s, 110 * MIN + 1, &c));
        assert!(starts_over(&s, 100 * MIN - 1, &c));
        assert!(!starts_over(&DecisionState::default(), 0, &c));
    }

    #[test]
    fn reset_keeps_manual_eco_and_outage_but_not_timers() {
        let mut s = with_last(10 * MIN, 50);
        s.manual = crate::model::ManualMode::Max;
        s.manual_at = Some(5 * MIN);
        s.mains_run_ms = 5 * MIN;
        s.rebase_at = Some(9 * MIN);
        s.outage = Some(Outage { start_ts: MIN, confirmed: true, deep: true, ..Default::default() });
        reset(&mut s, 5 * MIN);
        assert_eq!(s.manual, crate::model::ManualMode::Max);
        assert_eq!(s.mains_run_ms, 0);
        assert_eq!(s.rebase_at, Some(5 * MIN), "часы назад переносят rebase на now");
        let o = s.outage.unwrap();
        assert!(!o.confirmed && o.deep && o.start_ts == MIN);
    }

    #[test]
    fn step_is_capped_at_two_polls() {
        assert_eq!(step_cap(&cfg()), 2 * MIN);
    }

    #[test]
    fn backoff_1_2_4_8_then_10() {
        let mut m = BTreeMap::new();
        let delays: Vec<Ts> = (0..7)
            .map(|_| {
                let next = backoff_fail(&mut m, "x", 0);
                next / MIN
            })
            .collect();
        assert_eq!(delays, [1, 2, 4, 8, 10, 10, 10]);
        assert!(!backoff_open(&m, "x", 9 * MIN));
        assert!(backoff_open(&m, "x", 10 * MIN));
        assert!(backoff_open(&m, "y", 0));
        assert!(!backoff_due(&m, "y", 0));
    }
}
