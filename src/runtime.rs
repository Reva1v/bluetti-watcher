//! Оценка времени работы от батареи (FR-029). Только информирует: уровни и гибернация её не читают (FR-030).

use serde::Serialize;

use crate::clock::{MIN, Ts};
use crate::config::Config;
use crate::model::Reading;

/// Окно, по которому меряется скорость разряда.
pub const WINDOW: Ts = 60 * MIN;
/// Минимум для скорости: падение на 3 % за 20 мин.
pub const MIN_DROP: f64 = 3.0;
pub const MIN_SPAN: Ts = 20 * MIN;
/// Больше 30 дней не показываем.
pub const MAX_HOURS: f64 = 30.0 * 24.0;
pub const LEARNED_MIN: f64 = 0.4;
pub const LEARNED_MAX: f64 = 0.95;
/// Обученный КПД не выше заданного больше чем на это.
pub const LEARNED_OVER_CONFIG: f64 = 0.05;
/// Каждое новое измерение сдвигает обученный КПД на четверть.
const LEARN_WEIGHT: f64 = 0.25;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "lowercase")]
pub enum Source {
    /// По фактической скорости разряда.
    Rate,
    /// Ёмкость × обученный КПД.
    Learned,
    /// Ёмкость × КПД из конфига.
    Capacity,
}

#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Estimate {
    pub hours: f64,
    pub source: Source,
    /// % в час; только для `Rate`.
    pub rate_per_hour: Option<f64>,
}

/// Измеренный разряд: % в час, средняя нагрузка на отрезке и годится ли окно для обучения.
#[derive(Debug, Clone, Copy, PartialEq)]
struct Measured {
    rate: f64,
    mean_load: f64,
    clean: bool,
}

/// Хвост принятых показаний без сети в окне. Обрывается на показании с сетью, без заряда или на разрыве
/// больше `max_gap_sec`. Заряд меняется целыми процентами, поэтому отрезок — от первого шага заряда до последнего.
/// Рост заряда внутри хвоста — скорости нет. Чистое окно: нет входа DC и ни одного шага больше `soc_jump_floor`.
fn measure(recent: &[Reading], now: Ts, cfg: &Config) -> Option<Measured> {
    let from = now - WINDOW;
    let max_gap = Ts::from(cfg.guards.max_gap_sec) * 1000;
    let mut run: Vec<(&Reading, f64)> = Vec::new();
    for r in recent.iter().rev() {
        if r.ts > now {
            continue;
        }
        if r.ts < from {
            break;
        }
        let Some(soc) = r.soc else { break };
        if r.ac_in_w != Some(0) {
            break;
        }
        if run.last().is_some_and(|(p, _)| p.ts - r.ts > max_gap) {
            break;
        }
        run.push((r, f64::from(soc)));
    }
    run.reverse();
    let (mut first, mut last) = (None, 0);
    for k in 1..run.len() {
        if run[k].1 > run[k - 1].1 {
            return None;
        }
        if run[k].1 < run[k - 1].1 {
            first.get_or_insert(k);
            last = k;
        }
    }
    let first = first.filter(|&f| last > f)?;
    let drop = run[first].1 - run[last].1;
    let span = run[last].0.ts - run[first].0.ts;
    if drop < MIN_DROP || span < MIN_SPAN {
        return None;
    }
    let rate = drop / (span as f64 / 3_600_000.0);
    if !rate.is_finite() || rate <= 0.0 {
        return None;
    }
    let mut load = 0.0;
    let mut clean = true;
    for k in first..=last {
        load += f64::from(run[k].0.load_w().unwrap_or(0));
        if run[k].0.dc_in_w.unwrap_or(0) > 0
            || (k > first && run[k - 1].1 - run[k].1 > f64::from(cfg.guards.soc_jump_floor))
        {
            clean = false;
        }
    }
    Some(Measured { rate, mean_load: load / (last - first + 1) as f64, clean })
}

/// Часы до `target_soc` (0 — до пустой) при нагрузке последнего показания. `recent` — принятые показания
/// по возрастанию за последние ~70 мин. `None` на сети, без нагрузки или без заряда.
pub fn estimate(
    recent: &[Reading],
    now: Ts,
    cfg: &Config,
    learned_efficiency: Option<f64>,
    target_soc: u8,
) -> Option<Estimate> {
    let cur = recent.iter().rev().find(|r| r.ts <= now)?;
    let soc = f64::from(cur.soc?);
    if cur.ac_in_w.unwrap_or(0) > 0 {
        return None;
    }
    let load = f64::from(cur.load_w()?);
    if load <= 0.0 {
        return None;
    }
    let left = (soc - f64::from(target_soc)).max(0.0);
    if let Some(m) = measure(recent, now, cfg) {
        // Нагрузка выросла сейчас — время сокращается сейчас.
        let scale = if m.mean_load > 0.0 { m.mean_load / load } else { 1.0 };
        return Some(Estimate {
            hours: cap(left / m.rate * scale)?,
            source: Source::Rate,
            rate_per_hour: Some(round(m.rate, 1)),
        });
    }
    let sound = sound(learned_efficiency, cfg);
    let eff = sound.unwrap_or(cfg.station.efficiency);
    Some(Estimate {
        hours: cap(cfg.station.capacity_wh * left / 100.0 * eff / load)?,
        source: if sound.is_some() { Source::Learned } else { Source::Capacity },
        rate_per_hour: None,
    })
}

/// Новый обученный КПД и время обучения: по чистому окну — средняя нагрузка / (скорость × ёмкость). Первое
/// измерение берётся целиком, следующие сдвигают значение на четверть, не чаще раза в `WINDOW`.
pub fn learn(
    recent: &[Reading],
    prev_learned: Option<f64>,
    last_learn_ts: Option<Ts>,
    now: Ts,
    cfg: &Config,
) -> Option<(f64, Ts)> {
    if last_learn_ts.is_some_and(|t| now - t < WINDOW && t <= now) {
        return None;
    }
    let m = measure(recent, now, cfg).filter(|m| m.clean)?;
    let eff = m.mean_load / (m.rate / 100.0 * cfg.station.capacity_wh);
    if !eff.is_finite() || eff <= 0.0 {
        return None;
    }
    let measured = bound(eff, cfg);
    let next = match sound(prev_learned, cfg) {
        Some(l) => l + LEARN_WEIGHT * (measured - l),
        None => measured,
    };
    Some((round(bound(next, cfg), 3), now))
}

/// КПД в пределах 0.4–0.95 и не выше заданного + 0.05.
fn bound(eff: f64, cfg: &Config) -> f64 {
    let hi = LEARNED_MAX.min(cfg.station.efficiency + LEARNED_OVER_CONFIG).max(LEARNED_MIN);
    eff.clamp(LEARNED_MIN, hi)
}

/// Обученный КПД, если он в пределах; иначе `None`.
fn sound(learned: Option<f64>, cfg: &Config) -> Option<f64> {
    learned.filter(|l| l.is_finite() && (LEARNED_MIN..=LEARNED_MAX).contains(l)).map(|l| bound(l, cfg))
}

fn cap(hours: f64) -> Option<f64> {
    (hours.is_finite() && hours >= 0.0).then(|| round(hours.min(MAX_HOURS), 1))
}

fn round(v: f64, digits: i32) -> f64 {
    let k = 10f64.powi(digits);
    (v * k).round() / k
}

#[cfg(test)]
mod tests {
    use super::*;

    fn r(ts: Ts, soc: u8, ac_in: u32, out: u32) -> Reading {
        Reading {
            ts,
            soc: Some(soc),
            ac_in_w: Some(ac_in),
            ac_out_w: Some(out),
            dc_in_w: Some(0),
            dc_out_w: Some(0),
            ac_in_v: Some(0.0),
        }
    }

    /// Разряд `capacity_wh` при постоянной нагрузке `load` и КПД `eff`: показание раз в минуту, заряд целым числом
    /// (станция округляет вверх). Возвращает показания до минуты `upto` и полное время разряда в часах.
    fn drain(cfg: &Config, load: u32, upto: i64) -> (Vec<Reading>, f64) {
        let total_h = cfg.station.capacity_wh * cfg.station.efficiency / f64::from(load);
        let v = (0..=upto)
            .map(|m| {
                let left = 100.0 * (1.0 - (m as f64 / 60.0) / total_h);
                r(m * MIN, left.ceil().clamp(0.0, 100.0) as u8, 0, load)
            })
            .collect();
        (v, total_h)
    }

    #[test]
    fn sc007_mid_drain_within_20_percent() {
        let cfg = Config::default();
        assert_eq!((cfg.station.capacity_wh, cfg.station.efficiency), (1152.0, 0.85));
        let total_h = cfg.station.capacity_wh * cfg.station.efficiency / 200.0;
        let mid = (total_h * 60.0 / 2.0) as i64;
        let (all, _) = drain(&cfg, 200, mid);
        let now = mid * MIN;
        let actual = total_h - mid as f64 / 60.0;

        let recent: Vec<_> = all.iter().filter(|x| x.ts >= now - 70 * MIN).cloned().collect();
        let e = estimate(&recent, now, &cfg, None, 0).unwrap();
        assert_eq!(e.source, Source::Rate);
        assert!((e.hours - actual).abs() / actual <= 0.2, "rate {} vs {actual}", e.hours);

        // Только начало отключения: оценка по ёмкости.
        let e = estimate(&recent[recent.len() - 3..], now, &cfg, None, 0).unwrap();
        assert_eq!(e.source, Source::Capacity);
        assert!((e.hours - actual).abs() / actual <= 0.2, "capacity {} vs {actual}", e.hours);
    }

    #[test]
    fn mains_or_no_load_gives_none() {
        let cfg = Config::default();
        assert_eq!(estimate(&[r(0, 50, 300, 200)], 0, &cfg, None, 0), None);
        assert_eq!(estimate(&[r(0, 50, 0, 0)], 0, &cfg, None, 0), None);
        assert_eq!(estimate(&[], 0, &cfg, None, 0), None);
    }

    #[test]
    fn capacity_formula_and_learned() {
        let cfg = Config::default();
        let e = estimate(&[r(0, 50, 0, 200)], 0, &cfg, None, 0).unwrap();
        // 1152 × 0.5 × 0.85 / 200 = 2.448
        assert_eq!(e, Estimate { hours: 2.4, source: Source::Capacity, rate_per_hour: None });
        let e = estimate(&[r(0, 50, 0, 200)], 0, &cfg, Some(0.7), 0).unwrap();
        assert_eq!((e.hours, e.source), (2.0, Source::Learned));
        // Вне пределов — не учитывается.
        assert_eq!(estimate(&[r(0, 50, 0, 200)], 0, &cfg, Some(0.2), 0).unwrap().source, Source::Capacity);
        // До уровня гибернации.
        assert_eq!(estimate(&[r(0, 50, 0, 200)], 0, &cfg, None, 5).unwrap().hours, 2.2);
    }

    /// Показание раз в минуту до `points.last()`: заряд — ступенькой по точкам `(минута, заряд)`.
    fn series(points: &[(i64, u8)], load: u32) -> Vec<Reading> {
        let end = points.last().unwrap().0;
        (0..=end)
            .map(|m| {
                let soc = points.iter().rev().find(|p| p.0 <= m).unwrap().1;
                r(m * MIN, soc, 0, load)
            })
            .collect()
    }

    #[test]
    fn needs_three_percent_over_twenty_minutes() {
        let cfg = Config::default();
        // 2 % за 30 мин — мало.
        let v = series(&[(0, 52), (1, 51), (31, 50)], 200);
        assert_eq!(estimate(&v, 31 * MIN, &cfg, None, 0).unwrap().source, Source::Capacity);
        // 4 % за 10 мин — коротко.
        let v = series(&[(0, 54), (1, 53), (11, 50)], 200);
        assert_eq!(estimate(&v, 11 * MIN, &cfg, None, 0).unwrap().source, Source::Capacity);
        // 4 % за 30 мин: 8 %/ч, 49 % → 6.1 ч.
        let v = series(&[(0, 54), (1, 53), (31, 49), (32, 49)], 200);
        let e = estimate(&v, 32 * MIN, &cfg, None, 0).unwrap();
        assert_eq!((e.source, e.rate_per_hour, e.hours), (Source::Rate, Some(8.0), 6.1));
    }

    #[test]
    fn run_breaks_on_mains_gap_and_rise() {
        let cfg = Config::default();
        let base = series(&[(0, 60), (1, 59), (40, 50)], 200);
        assert_eq!(estimate(&base, 40 * MIN, &cfg, None, 0).unwrap().source, Source::Rate);
        // Сеть посреди окна обрывает хвост.
        let mut v = base.clone();
        v[30].ac_in_w = Some(300);
        assert_eq!(estimate(&v, 40 * MIN, &cfg, None, 0).unwrap().source, Source::Capacity);
        // Разрыв больше max_gap_sec.
        let v: Vec<_> = base.iter().filter(|x| !(10 * MIN..=25 * MIN).contains(&x.ts)).cloned().collect();
        assert_eq!(estimate(&v, 40 * MIN, &cfg, None, 0).unwrap().source, Source::Capacity);
        // Рост заряда без сети — скорости нет.
        let mut v = base.clone();
        v[5].soc = Some(61);
        assert_eq!(estimate(&v, 40 * MIN, &cfg, None, 0).unwrap().source, Source::Capacity);
    }

    #[test]
    fn higher_load_now_shortens_rate_estimate() {
        let cfg = Config::default();
        let mut v = series(&[(0, 54), (1, 53), (31, 49), (32, 49)], 200);
        v.last_mut().unwrap().ac_out_w = Some(400);
        let e = estimate(&v, 32 * MIN, &cfg, None, 0).unwrap();
        assert_eq!(e.hours, 3.1);
    }

    #[test]
    fn capped_at_30_days() {
        let cfg = Config::default();
        assert_eq!(estimate(&[r(0, 100, 0, 1)], 0, &cfg, None, 0).unwrap().hours, MAX_HOURS);
    }

    #[test]
    fn learning_bounded_and_throttled() {
        let cfg = Config::default();
        let (v, _) = drain(&cfg, 200, 120);
        let now = 120 * MIN;
        let (eff, ts) = learn(&v, None, None, now, &cfg).unwrap();
        assert!((eff - 0.85).abs() < 0.1, "{eff}");
        assert_eq!(ts, now);
        // Не чаще раза в час.
        assert_eq!(learn(&v, Some(eff), Some(now - 30 * MIN), now, &cfg), None);
        // Сдвиг на четверть от прежнего.
        let (next, _) = learn(&v, Some(0.6), Some(now - 61 * MIN), now, &cfg).unwrap();
        assert!((next - (0.6 + 0.25 * (eff - 0.6))).abs() < 0.002, "{next}");
        // Не выше заданного + 0.05 и не выше 0.95.
        let small = Config {
            station: crate::config::Station { capacity_wh: 600.0, efficiency: 0.85 },
            ..cfg.clone()
        };
        assert_eq!(learn(&v, None, None, now, &small).unwrap().0, 0.9);
        // Вход DC — окно не чистое.
        let dirty: Vec<_> = v.iter().cloned().map(|mut x| (x.dc_in_w = Some(50), x).1).collect();
        assert_eq!(learn(&dirty, None, None, now, &cfg), None);
    }
}
