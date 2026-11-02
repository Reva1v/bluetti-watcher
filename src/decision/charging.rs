//! Смена режима зарядки по правилам владельца (feature 004): чистая логика, время — параметром. Запись выполняет сервис.

use serde::{Deserialize, Serialize};

use crate::ble::ac180p::ChargingMode;
use crate::clock::{MIN, Ts};
use crate::config;
use crate::model::Mains;

use super::guards::{Backoff, backoff_delay};

/// После выключения правила молчат: иначе следующий цикл вернул бы режим, пока Windows ещё завершает работу.
pub const SHUTDOWN_PAUSE: Ts = 2 * MIN;

/// Почему выбран режим.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum Why {
    Light { soc: u8 },
    Soc { soc: u8 },
    Shutdown,
}

impl Why {
    pub fn code(self) -> &'static str {
        match self {
            Self::Light { .. } => "light",
            Self::Soc { .. } => "soc",
            Self::Shutdown => "shutdown",
        }
    }

    pub fn text(self) -> String {
        match self {
            Self::Light { soc } => format!("свет включился, заряд {soc} %"),
            Self::Soc { soc } => format!("заряд {soc} %"),
            Self::Shutdown => "выключение ПК".into(),
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub struct Want {
    pub mode: ChargingMode,
    pub why: Why,
}

/// Состояние правил; хранится отдельно от `DecisionState`, потому что переживает его сброс по пропуску.
#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
#[serde(default)]
pub struct ChargingAuto {
    /// Ждём подтверждённую сеть, чтобы выставить режим по заряду: было отключение или выключение ПК.
    pub light_pending: bool,
    /// Наибольший заряд за период с сетью: порог срабатывает при его первом пересечении.
    pub high_water: Option<u8>,
    /// Когда начато выключение ПК.
    pub off_at: Option<Ts>,
    /// Незавершённая запись.
    pub want: Option<Want>,
    pub backoff: Option<Backoff>,
}

/// Что видно по принятому показанию.
#[derive(Debug, Clone, Copy)]
pub struct Seen {
    pub soc: u8,
    pub mains: Mains,
    /// Сеть держится не меньше `mains.back_sec`.
    pub mains_confirmed: bool,
    pub outage: bool,
}

/// Режим при включении света (FR-002).
fn by_level(soc: u8, cfg: &config::ChargingAuto) -> ChargingMode {
    if soc < cfg.turbo_below {
        ChargingMode::Turbo
    } else if soc >= cfg.silent_at {
        ChargingMode::Silent
    } else {
        ChargingMode::Standard
    }
}

/// Режим при росте заряда от `from` до `to` (FR-003): больший порог важнее.
fn by_crossing(from: u8, to: u8, cfg: &config::ChargingAuto) -> Option<ChargingMode> {
    if from < cfg.silent_at && to >= cfg.silent_at {
        Some(ChargingMode::Silent)
    } else if from < cfg.standard_at && to >= cfg.standard_at {
        Some(ChargingMode::Standard)
    } else {
        None
    }
}

fn set_want(a: &mut ChargingAuto, want: Option<Want>) {
    a.want = want;
    a.backoff = None;
}

/// Вызывается только для принятого показания. `Some` — записать этот режим и сообщить итог через [`report`].
pub fn update(
    a: &mut ChargingAuto,
    seen: Seen,
    current: Option<ChargingMode>,
    now: Ts,
    cfg: &config::ChargingAuto,
) -> Option<Want> {
    if let Some(at) = a.off_at {
        if now >= at && now - at < SHUTDOWN_PAUSE {
            return None;
        }
        a.off_at = None;
    }
    if seen.outage {
        a.light_pending = true;
        set_want(a, None);
        return None;
    }
    let on = seen.mains == Mains::On;
    if a.light_pending {
        if on && seen.mains_confirmed {
            a.light_pending = false;
            a.high_water = Some(seen.soc);
            let why = Why::Light { soc: seen.soc };
            set_want(a, Some(Want { mode: by_level(seen.soc, cfg), why }));
        }
    } else if on {
        if let Some(mode) = a.high_water.and_then(|h| by_crossing(h, seen.soc, cfg)) {
            set_want(a, Some(Want { mode, why: Why::Soc { soc: seen.soc } }));
        }
        a.high_water = Some(a.high_water.map_or(seen.soc, |h| h.max(seen.soc)));
    }
    let want = a.want?;
    if current == Some(want.mode) {
        set_want(a, None);
        return None;
    }
    if a.backoff.as_ref().is_some_and(|b| now < b.next_try_ts) {
        return None;
    }
    Some(want)
}

/// Итог записи: успех снимает цель, неудача — повтор через 1, 2, 4, 8, затем каждые 10 мин.
pub fn report(a: &mut ChargingAuto, ok: bool, now: Ts) {
    if ok {
        set_want(a, None);
    } else {
        let failures = a.backoff.as_ref().map_or(1, |b| b.failures + 1);
        a.backoff = Some(Backoff { failures, next_try_ts: now + backoff_delay(failures) });
    }
}

/// Нажатие на чипе важнее незавершённой автоматической записи (FR-009).
pub fn manual(a: &mut ChargingAuto) {
    set_want(a, None);
}

/// Начато выключение ПК. После запуска режим снова выставится по заряду.
pub fn shutdown(a: &mut ChargingAuto, now: Ts) {
    a.off_at = Some(now);
    a.light_pending = true;
    set_want(a, None);
}

#[cfg(test)]
mod tests {
    use super::ChargingMode::{Silent, Standard, Turbo};
    use super::*;

    fn cfg() -> config::ChargingAuto {
        config::ChargingAuto::default()
    }

    fn on(soc: u8) -> Seen {
        Seen { soc, mains: Mains::On, mains_confirmed: true, outage: false }
    }

    fn off(soc: u8) -> Seen {
        Seen { soc, mains: Mains::Off, mains_confirmed: false, outage: true }
    }

    /// Состояние после отключения, сеть уже подтверждена.
    fn after_outage() -> ChargingAuto {
        let mut a = ChargingAuto::default();
        assert_eq!(update(&mut a, off(10), Some(Silent), 0, &cfg()), None);
        assert!(a.light_pending);
        a
    }

    fn mode(w: Option<Want>) -> Option<ChargingMode> {
        w.map(|w| w.mode)
    }

    #[test]
    fn light_sets_mode_by_level_with_exact_bounds() {
        for (soc, want) in [
            (0, Turbo),
            (19, Turbo),
            (20, Standard),
            (30, Standard),
            (31, Standard),
            (79, Standard),
            (80, Silent),
            (100, Silent),
        ] {
            let mut a = after_outage();
            let current = if want == Silent { Standard } else { Silent };
            let w = update(&mut a, on(soc), Some(current), MIN, &cfg());
            assert_eq!(w, Some(Want { mode: want, why: Why::Light { soc } }), "заряд {soc}");
            assert!(!a.light_pending);
        }
    }

    #[test]
    fn light_waits_for_confirmed_mains() {
        let mut a = after_outage();
        let unconfirmed = Seen { mains_confirmed: false, outage: true, ..on(15) };
        assert_eq!(update(&mut a, unconfirmed, Some(Silent), MIN, &cfg()), None);
        // Сеть вернулась, но ещё не подтверждена и отключение уже закрыто быть не могло.
        let waiting = Seen { mains_confirmed: false, ..on(15) };
        assert_eq!(update(&mut a, waiting, Some(Silent), MIN, &cfg()), None);
        assert_eq!(mode(update(&mut a, on(15), Some(Silent), 2 * MIN, &cfg())), Some(Turbo));
    }

    #[test]
    fn mains_blink_without_outage_is_not_light() {
        let mut a = ChargingAuto::default();
        assert_eq!(update(&mut a, on(15), Some(Silent), 0, &cfg()), None);
        let blink = Seen { mains: Mains::Off, mains_confirmed: false, ..on(15) };
        assert_eq!(update(&mut a, blink, Some(Silent), MIN, &cfg()), None);
        assert_eq!(update(&mut a, on(15), Some(Silent), 2 * MIN, &cfg()), None);
    }

    #[test]
    fn same_mode_or_unknown_mode() {
        let mut a = after_outage();
        assert_eq!(update(&mut a, on(15), Some(Turbo), MIN, &cfg()), None);
        assert_eq!(a.want, None, "режим уже стоит: записи нет");

        // Режим не прочитан: правило важнее неизвестного значения.
        let mut a = after_outage();
        assert_eq!(mode(update(&mut a, on(15), None, MIN, &cfg())), Some(Turbo));
    }

    #[test]
    fn full_charge_changes_mode_exactly_three_times() {
        let mut a = after_outage();
        let mut current = Silent;
        let mut seen = Vec::new();
        for (i, soc) in (15..=85).enumerate() {
            if let Some(w) = update(&mut a, on(soc), Some(current), i as Ts * MIN, &cfg()) {
                seen.push((soc, w.mode));
                current = w.mode;
                report(&mut a, true, i as Ts * MIN);
            }
        }
        assert_eq!(seen, [(15, Turbo), (50, Standard), (80, Silent)]);
    }

    #[test]
    fn threshold_fires_once_when_charge_wobbles() {
        let mut a = ChargingAuto::default();
        let mut current = Standard;
        let mut writes = 0;
        for (i, soc) in [79, 80, 79, 80, 79, 81].into_iter().enumerate() {
            if let Some(w) = update(&mut a, on(soc), Some(current), i as Ts * MIN, &cfg()) {
                writes += 1;
                current = w.mode;
                report(&mut a, true, i as Ts * MIN);
            }
        }
        assert_eq!((writes, current), (1, Silent));
    }

    #[test]
    fn manual_choice_holds_until_next_threshold() {
        let mut a = ChargingAuto::default();
        assert_eq!(update(&mut a, on(40), Some(Turbo), 0, &cfg()), None);
        assert_eq!(update(&mut a, on(49), Some(Turbo), MIN, &cfg()), None, "ручной «Турбо» держится");
        assert_eq!(mode(update(&mut a, on(50), Some(Turbo), 2 * MIN, &cfg())), Some(Standard));
    }

    #[test]
    fn no_threshold_without_mains() {
        let mut a = ChargingAuto::default();
        assert_eq!(update(&mut a, on(40), Some(Silent), 0, &cfg()), None);
        let blink = Seen { mains: Mains::Off, mains_confirmed: false, ..on(50) };
        assert_eq!(update(&mut a, blink, Some(Silent), MIN, &cfg()), None);
        assert_eq!(a.high_water, Some(40));
    }

    #[test]
    fn jump_over_both_thresholds_takes_the_higher() {
        let mut a = ChargingAuto::default();
        assert_eq!(update(&mut a, on(40), Some(Turbo), 0, &cfg()), None);
        assert_eq!(mode(update(&mut a, on(85), Some(Turbo), MIN, &cfg())), Some(Silent));
    }

    #[test]
    fn service_restart_is_not_light_and_catches_missed_threshold() {
        let mut a = ChargingAuto::default();
        assert_eq!(update(&mut a, on(45), Some(Turbo), 0, &cfg()), None);
        let text = serde_json::to_string(&a).unwrap();
        let mut back: ChargingAuto = serde_json::from_str(&text).unwrap();
        assert_eq!(back, a);
        // Перезапуск при том же заряде: ручной выбор цел.
        assert_eq!(update(&mut back, on(45), Some(Turbo), MIN, &cfg()), None);
        // Порог пройден за время простоя.
        assert_eq!(mode(update(&mut back, on(60), Some(Turbo), 2 * MIN, &cfg())), Some(Standard));
    }

    #[test]
    fn failed_write_retries_by_backoff() {
        let mut a = after_outage();
        assert_eq!(mode(update(&mut a, on(15), Some(Silent), 0, &cfg())), Some(Turbo));
        report(&mut a, false, 0);
        assert_eq!(update(&mut a, on(15), Some(Silent), MIN - 1, &cfg()), None);
        assert_eq!(mode(update(&mut a, on(15), Some(Silent), MIN, &cfg())), Some(Turbo));
        report(&mut a, false, MIN);
        assert_eq!(update(&mut a, on(15), Some(Silent), 2 * MIN, &cfg()), None);
        assert_eq!(mode(update(&mut a, on(15), Some(Silent), 3 * MIN, &cfg())), Some(Turbo));
        report(&mut a, true, 3 * MIN);
        assert_eq!(update(&mut a, on(15), Some(Silent), 4 * MIN, &cfg()), None);
    }

    #[test]
    fn manual_press_and_outage_cancel_the_retry() {
        let mut a = after_outage();
        assert!(update(&mut a, on(15), Some(Silent), 0, &cfg()).is_some());
        report(&mut a, false, 0);
        manual(&mut a);
        assert_eq!(update(&mut a, on(15), Some(Silent), 10 * MIN, &cfg()), None);

        let mut a = after_outage();
        assert!(update(&mut a, on(15), Some(Silent), 0, &cfg()).is_some());
        report(&mut a, false, 0);
        assert_eq!(update(&mut a, off(15), Some(Silent), 10 * MIN, &cfg()), None);
        assert_eq!((a.want, a.backoff), (None, None));
    }

    #[test]
    fn shutdown_pauses_rules_then_light_applies_after_boot() {
        let mut a = ChargingAuto::default();
        assert_eq!(update(&mut a, on(60), Some(Standard), 0, &cfg()), None);
        shutdown(&mut a, 10 * MIN);
        // Windows ещё завершает работу: режим не возвращаем.
        assert_eq!(update(&mut a, on(60), Some(Silent), 11 * MIN, &cfg()), None);
        assert!(a.off_at.is_some());
        // ПК запущен снова или выключение отменено.
        let w = update(&mut a, on(15), Some(Silent), 10 * MIN + SHUTDOWN_PAUSE, &cfg());
        assert_eq!(w, Some(Want { mode: Turbo, why: Why::Light { soc: 15 } }));
        assert_eq!(a.off_at, None);
    }

    #[test]
    fn shutdown_pause_ends_if_clock_went_back() {
        let mut a = ChargingAuto::default();
        shutdown(&mut a, 10 * MIN);
        assert_eq!(mode(update(&mut a, on(40), Some(Silent), 5 * MIN, &cfg())), Some(Standard));
    }

    #[test]
    fn custom_thresholds_are_used() {
        let c = config::ChargingAuto { enabled: true, turbo_below: 10, standard_at: 40, silent_at: 90 };
        let mut a = after_outage();
        assert_eq!(mode(update(&mut a, on(15), Some(Silent), MIN, &c)), Some(Standard));
        report(&mut a, true, MIN);
        assert_eq!(mode(update(&mut a, on(90), Some(Standard), 2 * MIN, &c)), Some(Silent));
    }
}
