//! Время: миллисекунды UTC и часы, которые можно подменить в тестах.

use std::sync::atomic::{AtomicI64, Ordering};

use chrono::{Local, TimeZone};

/// Метка времени: миллисекунды с эпохи Unix, UTC.
pub type Ts = i64;

pub const SEC: Ts = 1000;
pub const MIN: Ts = 60 * SEC;

pub trait Clock: Send + Sync {
    fn now(&self) -> Ts;
}

pub struct SystemClock;

impl Clock for SystemClock {
    fn now(&self) -> Ts {
        chrono::Utc::now().timestamp_millis()
    }
}

/// Часы для тестов: время двигается только вручную.
#[derive(Default)]
pub struct FakeClock(AtomicI64);

impl FakeClock {
    pub fn new(start: Ts) -> Self {
        Self(AtomicI64::new(start))
    }
    pub fn set(&self, t: Ts) {
        self.0.store(t, Ordering::SeqCst);
    }
    pub fn advance(&self, d: Ts) {
        self.0.fetch_add(d, Ordering::SeqCst);
    }
}

impl Clock for FakeClock {
    fn now(&self) -> Ts {
        self.0.load(Ordering::SeqCst)
    }
}

/// ISO 8601 в локальном часовом поясе ПК со смещением, до секунд.
pub fn to_local_iso(t: Ts) -> String {
    match Local.timestamp_millis_opt(t).single() {
        Some(d) => d.format("%Y-%m-%dT%H:%M:%S%:z").to_string(),
        None => String::new(),
    }
}

/// Локальное время для людей: `14.10 21:05`.
pub fn to_local_short(t: Ts) -> String {
    match Local.timestamp_millis_opt(t).single() {
        Some(d) => d.format("%d.%m %H:%M").to_string(),
        None => String::new(),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn fake_clock_moves_only_by_hand() {
        let c = FakeClock::new(1_000);
        assert_eq!(c.now(), 1_000);
        c.advance(MIN);
        assert_eq!(c.now(), 61_000);
        c.set(5);
        assert_eq!(c.now(), 5);
    }

    #[test]
    fn iso_has_offset() {
        let s = to_local_iso(1_760_465_100_000);
        assert_eq!(s.len(), 25, "{s}");
        assert!(s.contains('T'));
    }
}
