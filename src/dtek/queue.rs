//! Выбор очереди без ручной настройки (US2): общая сетка, пока кандидаты совпадают; иначе — очередь, с которой
//! дважды подряд однозначно совпали реальные отключения; иначе — первая подходящая.

use serde::{Deserialize, Serialize};

use super::model::Schedule;
use super::windows::off_windows;
use crate::clock::{MIN, Ts};

/// Допуск совпадения границ реального отключения с плановым окном.
pub const TOLERANCE: Ts = 20 * MIN;
/// Сколько однозначных совпадений подряд фиксируют очередь.
pub const STREAK_TO_FIX: u32 = 2;

/// Состояние выбора, хранится в `meta.dtek_queue`.
#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
pub struct QueueState {
    pub fixed: Option<String>,
    pub streak_queue: Option<String>,
    pub streak_count: u32,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "lowercase")]
pub enum QueueStatus {
    Fixed,
    Same,
    Guess,
}

/// Убрать из состояния очереди, которых больше нет среди кандидатов. `true` — что-то изменилось.
pub fn sanitize(state: &mut QueueState, candidates: &[String]) -> bool {
    let known = |q: &Option<String>| q.as_ref().is_none_or(|q| candidates.contains(q));
    let mut changed = false;
    if !known(&state.fixed) {
        state.fixed = None;
        changed = true;
    }
    if !known(&state.streak_queue) {
        state.streak_queue = None;
        state.streak_count = 0;
        changed = true;
    }
    changed
}

/// Очередь для показа на день `today` (и `tomorrow`, если опубликован).
pub fn choose(
    schedule: Option<&Schedule>,
    candidates: &[String],
    state: &QueueState,
    today: Ts,
    tomorrow: Ts,
) -> Option<(String, QueueStatus)> {
    let first = candidates.first()?;
    let fixed = state.fixed.as_ref().filter(|q| candidates.contains(q));
    let Some(s) = schedule else {
        return Some(match fixed {
            Some(q) => (q.clone(), QueueStatus::Fixed),
            None => (first.clone(), QueueStatus::Guess),
        });
    };
    let cells = |q: &String, day: Ts| s.day(q, day).map(|d| d.cells);
    let same_on = |day: Ts, required: bool| {
        let v: Vec<_> = candidates.iter().map(|q| cells(q, day)).collect();
        if v.iter().all(Option::is_none) {
            return !required;
        }
        v.iter().all(|c| c.is_some() && *c == v[0])
    };
    if same_on(today, true) && same_on(tomorrow, false) {
        return Some((fixed.unwrap_or(first).clone(), QueueStatus::Same));
    }
    if let Some(q) = fixed {
        return Some((q.clone(), QueueStatus::Fixed));
    }
    let guess = candidates.iter().find(|q| cells(q, today).is_some()).unwrap_or(first);
    Some((guess.clone(), QueueStatus::Guess))
}

/// Учесть закончившееся реальное отключение `[start, end]`. `true` — состояние изменилось.
pub fn observe(
    schedule: &Schedule,
    candidates: &[String],
    state: &mut QueueState,
    start: Ts,
    end: Ts,
) -> bool {
    let before = state.clone();
    let near = |a: Ts, b: Ts| (a - b).abs() <= TOLERANCE;
    let matched: Vec<&String> = candidates
        .iter()
        .filter(|q| {
            off_windows(schedule.queue_days(q)).iter().any(|w| near(w.start, start) && near(w.end, end))
        })
        .collect();
    match matched.as_slice() {
        [q] => {
            if state.streak_queue.as_ref() == Some(*q) {
                state.streak_count += 1;
            } else {
                state.streak_queue = Some((*q).clone());
                state.streak_count = 1;
            }
            if state.streak_count >= STREAK_TO_FIX {
                state.fixed = Some((*q).clone());
            }
        }
        _ => {
            state.streak_queue = None;
            state.streak_count = 0;
        }
    }
    *state != before
}

#[cfg(test)]
mod tests {
    use std::collections::BTreeMap;

    use super::*;
    use crate::dtek::model::Cell::{self, *};
    use crate::dtek::model::Day;

    const H: Ts = 60 * MIN;
    const D0: Ts = 1_791_925_200_000;
    const D1: Ts = D0 + 24 * H;

    fn cells(off: &[usize]) -> [Cell; 24] {
        let mut c = [Yes; 24];
        for h in off {
            c[h - 1] = No;
        }
        c
    }

    fn sched(q: &[(&str, [Cell; 24])]) -> Schedule {
        let mut days = BTreeMap::new();
        for (name, c) in q {
            days.insert(name.to_string(), vec![Day { start: D0, cells: *c }]);
        }
        Schedule { update: String::new(), collected: 0, days }
    }

    fn cand(q: &[&str]) -> Vec<String> {
        q.iter().map(|s| s.to_string()).collect()
    }

    #[test]
    fn same_when_all_candidates_match() {
        let c = cand(&["GPV9.1", "GPV21.1"]);
        let s =
            sched(&[("GPV9.1", cells(&[10, 11])), ("GPV21.1", cells(&[10, 11])), ("GPV1.1", cells(&[1]))]);
        let st = QueueState::default();
        assert_eq!(choose(Some(&s), &c, &st, D0, D1), Some(("GPV9.1".into(), QueueStatus::Same)));
        // Завтра есть только у одной — уже не «совпадают».
        let mut s2 = s.clone();
        s2.days.get_mut("GPV21.1").unwrap().push(Day { start: D1, cells: cells(&[]) });
        assert_eq!(choose(Some(&s2), &c, &st, D0, D1), Some(("GPV9.1".into(), QueueStatus::Guess)));
    }

    #[test]
    fn guess_is_first_with_cells() {
        let c = cand(&["GPV5.1", "GPV9.1", "GPV21.1"]);
        let s = sched(&[("GPV9.1", cells(&[10])), ("GPV21.1", cells(&[11]))]);
        let st = QueueState::default();
        assert_eq!(choose(Some(&s), &c, &st, D0, D1), Some(("GPV9.1".into(), QueueStatus::Guess)));
        assert_eq!(choose(None, &c, &st, D0, D1), Some(("GPV5.1".into(), QueueStatus::Guess)));
        assert_eq!(choose(Some(&s), &[], &st, D0, D1), None);
    }

    #[test]
    fn two_unique_matches_in_a_row_fix_the_queue() {
        let c = cand(&["GPV9.1", "GPV21.1"]);
        // 9.1: 09–11 и 15–16; 21.1: 09–11 и 18–19.
        let s = sched(&[("GPV9.1", cells(&[10, 11, 16])), ("GPV21.1", cells(&[10, 11, 19]))]);
        let mut st = QueueState::default();
        // Совпало с обеими — не считается.
        assert!(!observe(&s, &c, &mut st, D0 + 9 * H + 5 * MIN, D0 + 11 * H - 10 * MIN));
        assert_eq!(st, QueueState::default());
        // Только 9.1, в пределах 20 минут.
        assert!(observe(&s, &c, &mut st, D0 + 15 * H - 19 * MIN, D0 + 16 * H + 19 * MIN));
        assert_eq!((st.streak_queue.as_deref(), st.streak_count, &st.fixed), (Some("GPV9.1"), 1, &None));
        assert_eq!(choose(Some(&s), &c, &st, D0, D1).unwrap().1, QueueStatus::Guess);
        // Ещё раз 9.1 — фиксируется.
        observe(&s, &c, &mut st, D0 + 15 * H, D0 + 16 * H);
        assert_eq!(st.fixed.as_deref(), Some("GPV9.1"));
        assert_eq!(choose(Some(&s), &c, &st, D0, D1), Some(("GPV9.1".into(), QueueStatus::Fixed)));
    }

    #[test]
    fn ambiguous_or_unmatched_outage_resets_the_streak() {
        let c = cand(&["GPV9.1", "GPV21.1"]);
        let s = sched(&[("GPV9.1", cells(&[10, 11, 16])), ("GPV21.1", cells(&[10, 11, 19]))]);
        let mut st = QueueState::default();
        observe(&s, &c, &mut st, D0 + 15 * H, D0 + 16 * H);
        // Вне допуска (21 минута) — ни с одной.
        observe(&s, &c, &mut st, D0 + 15 * H + 21 * MIN, D0 + 16 * H);
        assert_eq!(st.streak_count, 0);
        observe(&s, &c, &mut st, D0 + 15 * H, D0 + 16 * H);
        assert_eq!(st.fixed, None);
        // Другая очередь начинает серию заново.
        observe(&s, &c, &mut st, D0 + 18 * H, D0 + 19 * H);
        assert_eq!((st.streak_queue.as_deref(), st.streak_count, &st.fixed), (Some("GPV21.1"), 1, &None));
    }

    #[test]
    fn maybe_cells_never_match() {
        let c = cand(&["GPV9.1", "GPV21.1"]);
        let mut maybe = cells(&[]);
        maybe[15] = Maybe;
        let s = sched(&[("GPV9.1", maybe), ("GPV21.1", cells(&[19]))]);
        let mut st = QueueState::default();
        observe(&s, &c, &mut st, D0 + 15 * H, D0 + 16 * H);
        observe(&s, &c, &mut st, D0 + 15 * H, D0 + 16 * H);
        assert_eq!(st, QueueState::default());
    }

    #[test]
    fn dropped_candidate_is_forgotten() {
        let mut st =
            QueueState { fixed: Some("GPV9.1".into()), streak_queue: Some("GPV9.1".into()), streak_count: 2 };
        let c = cand(&["GPV21.1", "GPV33.1"]);
        assert!(sanitize(&mut st, &c));
        assert_eq!(st, QueueState::default());
        assert!(!sanitize(&mut st, &c));
        // Даже без sanitize фиксированная очередь вне кандидатов не показывается.
        let st = QueueState { fixed: Some("GPV9.1".into()), ..Default::default() };
        let s = sched(&[("GPV21.1", cells(&[10])), ("GPV33.1", cells(&[11]))]);
        assert_eq!(choose(Some(&s), &c, &st, D0, D1), Some(("GPV21.1".into(), QueueStatus::Guess)));
    }
}
