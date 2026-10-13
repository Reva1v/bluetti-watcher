//! Данные графика ДТЭК: ячейки часа, дни очереди, расписание набора.

use std::collections::BTreeMap;

use serde::{Deserialize, Serialize};

use crate::clock::Ts;

/// Ячейка часа N (с (N-1):00 до N:00 по Киеву).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum Cell {
    /// Свет есть.
    Yes,
    /// Света нет весь час.
    No,
    /// Света нет первые полчаса.
    First,
    /// Света нет вторые полчаса.
    Second,
    /// Возможно отключение весь час.
    Maybe,
    /// Возможно отключение в первые полчаса.
    #[serde(rename = "mfirst")]
    MaybeFirst,
    /// Возможно отключение во вторые полчаса.
    #[serde(rename = "msecond")]
    MaybeSecond,
    /// Неизвестное значение или пропущенный час.
    Unknown,
}

impl Cell {
    pub fn from_dataset(s: &str) -> Self {
        match s {
            "yes" => Self::Yes,
            "no" => Self::No,
            "first" => Self::First,
            "second" => Self::Second,
            "maybe" => Self::Maybe,
            "mfirst" => Self::MaybeFirst,
            "msecond" => Self::MaybeSecond,
            _ => Self::Unknown,
        }
    }
}

/// День очереди: `start` — киевская (локальная) полночь, мс UTC.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct Day {
    pub start: Ts,
    pub cells: [Cell; 24],
}

/// Разобранный набор: время ДТЭК, время сбора и дни по очередям (по возрастанию `start`).
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct Schedule {
    /// `fact.update` как есть, например `13.10.2026 21:31`.
    pub update: String,
    /// `lastUpdated`, мс UTC.
    pub collected: Ts,
    pub days: BTreeMap<String, Vec<Day>>,
}

impl Schedule {
    /// Оставить только `queues` и дни с началом в `[from, to)`.
    pub fn retain(&mut self, queues: &[String], from: Ts, to: Ts) {
        self.days.retain(|q, _| queues.iter().any(|c| c == q));
        for days in self.days.values_mut() {
            days.retain(|d| d.start >= from && d.start < to);
        }
        self.days.retain(|_, d| !d.is_empty());
    }

    pub fn day(&self, queue: &str, start: Ts) -> Option<&Day> {
        self.days.get(queue)?.iter().find(|d| d.start == start)
    }

    pub fn queue_days(&self, queue: &str) -> &[Day] {
        self.days.get(queue).map_or(&[], Vec::as_slice)
    }
}

/// Имя очереди `GPV<1-2 цифры>.<1-2 цифры>`.
pub fn valid_queue(name: &str) -> bool {
    let Some(rest) = name.strip_prefix("GPV") else { return false };
    let Some((a, b)) = rest.split_once('.') else { return false };
    let digits = |s: &str| (1..=2).contains(&s.len()) && s.bytes().all(|c| c.is_ascii_digit());
    digits(a) && digits(b)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn queue_names() {
        for ok in ["GPV9.1", "GPV21.1", "GPV1.12", "GPV99.99"] {
            assert!(valid_queue(ok), "{ok}");
        }
        for bad in ["GPV123.1", "GPV9", "gpv9.1", "GPV9.1.1", "GPV.1", "GPV9.", "GPVa.1", "9.1", ""] {
            assert!(!valid_queue(bad), "{bad}");
        }
    }

    #[test]
    fn cell_names_round_trip() {
        for s in ["yes", "no", "first", "second", "maybe", "mfirst", "msecond"] {
            let c = Cell::from_dataset(s);
            assert_eq!(serde_json::to_value(c).unwrap(), s);
        }
        assert_eq!(Cell::from_dataset("whatever"), Cell::Unknown);
        assert_eq!(serde_json::to_value(Cell::Unknown).unwrap(), "unknown");
    }
}
