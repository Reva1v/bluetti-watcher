//! Окна отключений по ячейкам: полчаса — шаг, подряд идущие окна (и через полночь) — одно. Время — стена ПК
//! (по допущению спеки ПК живёт по Киеву).

use chrono::{Duration, Local, NaiveDate, TimeZone};
use serde::Serialize;

use super::model::{Cell, Day};
use crate::clock::{MIN, Ts};

/// Состояние получаса.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Half {
    On,
    Off,
    Maybe,
    Unknown,
}

fn halves(c: Cell) -> [Half; 2] {
    use Half::*;
    match c {
        Cell::Yes => [On, On],
        Cell::No => [Off, Off],
        Cell::First => [Off, On],
        Cell::Second => [On, Off],
        Cell::Maybe => [Maybe, Maybe],
        Cell::MaybeFirst => [Maybe, On],
        Cell::MaybeSecond => [On, Maybe],
        Cell::Unknown => [Unknown, Unknown],
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Slot {
    pub start: Ts,
    pub end: Ts,
    pub half: Half,
}

/// Плановое окно без света `[start, end)`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Window {
    pub start: Ts,
    pub end: Ts,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "lowercase")]
pub enum Power {
    On,
    Off,
    Unknown,
}

/// Момент `minutes` минут стенного времени после полуночи `day_start`.
pub fn wall(day_start: Ts, minutes: i64) -> Ts {
    let fallback = day_start + minutes * MIN;
    let Some(base) = Local.timestamp_millis_opt(day_start).single() else { return fallback };
    let naive = base.naive_local() + Duration::minutes(minutes);
    Local.from_local_datetime(&naive).earliest().map_or(fallback, |d| d.timestamp_millis())
}

/// Локальная дата момента.
pub fn local_date(t: Ts) -> Option<NaiveDate> {
    Local.timestamp_millis_opt(t).single().map(|d| d.date_naive())
}

/// Локальная полночь даты.
pub fn midnight(date: NaiveDate) -> Option<Ts> {
    let naive = date.and_hms_opt(0, 0, 0)?;
    Local.from_local_datetime(&naive).earliest().map(|d| d.timestamp_millis())
}

/// 48 получасов каждого дня, по порядку.
pub fn slots(days: &[Day]) -> Vec<Slot> {
    let mut out = Vec::with_capacity(days.len() * 48);
    for d in days {
        for (h, c) in d.cells.iter().enumerate() {
            for (i, half) in halves(*c).into_iter().enumerate() {
                let k = (h * 2 + i) as i64;
                out.push(Slot { start: wall(d.start, k * 30), end: wall(d.start, (k + 1) * 30), half });
            }
        }
    }
    out
}

/// Точные (не «возможно») окна без света; соседние получасы и дни сливаются.
pub fn off_windows(days: &[Day]) -> Vec<Window> {
    let mut out: Vec<Window> = Vec::new();
    for s in slots(days).into_iter().filter(|s| s.half == Half::Off) {
        match out.last_mut() {
            Some(w) if w.end == s.start => w.end = s.end,
            _ => out.push(Window { start: s.start, end: s.end }),
        }
    }
    out
}

/// Свет сейчас и до какого момента: конец текущего окна без света или начало следующего.
pub fn current(days: &[Day], now: Ts) -> (Power, Option<Ts>) {
    let all = slots(days);
    let Some(slot) = all.iter().find(|s| s.start <= now && now < s.end) else {
        return (Power::Unknown, None);
    };
    let windows = off_windows(days);
    match slot.half {
        Half::Off => (Power::Off, windows.iter().find(|w| w.start <= now && now < w.end).map(|w| w.end)),
        Half::On | Half::Maybe => (Power::On, windows.iter().find(|w| w.start > now).map(|w| w.start)),
        Half::Unknown => (Power::Unknown, None),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::dtek::model::Cell::*;

    const H: Ts = 60 * MIN;
    /// 2026-10-14 00:00 по Киеву, мс.
    const D0: Ts = 1_791_925_200_000;

    fn owner_day(start: Ts) -> Day {
        let mut cells = [Yes; 24];
        for h in [10, 11, 12, 21, 22, 23] {
            cells[h - 1] = No;
        }
        cells[12] = First;
        cells[23] = First;
        Day { start, cells }
    }

    /// Начало дня через 24 стенных часа (вне перехода на зимнее время это ровно 24 ч).
    fn next(start: Ts) -> Ts {
        wall(start, 24 * 60)
    }

    #[test]
    fn merges_hours_and_half_hours() {
        let d = owner_day(D0);
        let w = off_windows(std::slice::from_ref(&d));
        let rel: Vec<(Ts, Ts)> = w.iter().map(|w| ((w.start - D0) / MIN, (w.end - D0) / MIN)).collect();
        assert_eq!(rel, [(9 * 60, 12 * 60 + 30), (20 * 60, 23 * 60 + 30)]);
    }

    #[test]
    fn second_half_and_maybe() {
        let mut cells = [Yes; 24];
        cells[0] = Second; // 00:30–01:00
        cells[1] = First; // 01:00–01:30
        cells[5] = Maybe;
        cells[6] = MaybeFirst;
        let d = Day { start: D0, cells };
        let w = off_windows(std::slice::from_ref(&d));
        assert_eq!(w, [Window { start: D0 + 30 * MIN, end: D0 + 90 * MIN }]);
        // «Возможно» — свет есть, до следующего точного окна (его нет).
        assert_eq!(current(&[d], D0 + 5 * H + 10 * MIN), (Power::On, None));
    }

    #[test]
    fn now_and_until() {
        let d = owner_day(D0);
        let days = [d];
        assert_eq!(current(&days, D0 + 3 * H), (Power::On, Some(D0 + 9 * H)));
        assert_eq!(current(&days, D0 + 10 * H), (Power::Off, Some(D0 + 12 * H + 30 * MIN)));
        assert_eq!(current(&days, D0 + 12 * H + 30 * MIN), (Power::On, Some(D0 + 20 * H)));
        assert_eq!(current(&days, D0 + 23 * H + 40 * MIN), (Power::On, None));
        assert_eq!(current(&days, D0 - MIN), (Power::Unknown, None));
        assert_eq!(current(&days, next(D0)), (Power::Unknown, None));
        let mut u = owner_day(D0);
        u.cells[2] = Unknown;
        assert_eq!(current(&[u], D0 + 2 * H + MIN), (Power::Unknown, None));
    }

    #[test]
    fn until_crosses_midnight() {
        let mut today = owner_day(D0);
        today.cells[23] = No; // 23:00–24:00
        let mut tomorrow = Day { start: next(D0), cells: [Yes; 24] };
        tomorrow.cells[0] = No;
        tomorrow.cells[1] = First; // до 01:30 завтра
        let days = [today.clone(), tomorrow.clone()];
        let end = tomorrow.start + 90 * MIN;
        assert_eq!(current(&days, D0 + 21 * H), (Power::Off, Some(end)));
        assert_eq!(current(&days, tomorrow.start + 10 * MIN), (Power::Off, Some(end)));
        assert_eq!(off_windows(&days).len(), 2);
        // Без завтрашних данных окно заканчивается в полночь.
        assert_eq!(current(&[today], D0 + 21 * H), (Power::Off, Some(tomorrow.start)));
        // Свет есть, следующее окно — уже завтра.
        let mut quiet = Day { start: D0, cells: [Yes; 24] };
        quiet.cells[0] = Unknown;
        assert_eq!(current(&[quiet, tomorrow.clone()], D0 + 22 * H), (Power::On, Some(tomorrow.start)));
    }

    #[test]
    fn local_midnight_round_trip() {
        let date = local_date(D0 + 5 * H).unwrap();
        let m = midnight(date).unwrap();
        assert_eq!(local_date(m), Some(date));
        assert!(m <= D0 + 5 * H && D0 + 5 * H - m < 24 * H);
    }
}
