//! Сеть и отключение (FR-005, FR-015, FR-018, FR-019, FR-021; design.md §5): таймеры по принятым показаниям,
//! начало и конец отключения, ручной режим и желаемый уровень eco.

use serde_json::json;

use crate::clock::{SEC, Ts};
use crate::config::Config;
use crate::model::{EcoLevel, Mains, ManualMode, Reading};

use super::guards::{self, backoff_due};
use super::{Action, DecisionState, Outage};

const ECO_KEY: &str = "eco";

/// Вход AC > 0 — сеть есть; 0 — батарея (вход DC при нулевом AC тоже батарея); нет значения — неизвестно.
pub fn mains_of(r: &Reading) -> Mains {
    match r.ac_in_w {
        Some(w) if w > 0 => Mains::On,
        Some(_) => Mains::Off,
        None => Mains::Unknown,
    }
}

/// Принятое (не rebase) показание двигает таймеры сети; `step` — время с прошлого принятого, не больше 2 × poll.
pub fn update(
    s: &mut DecisionState,
    m: Mains,
    soc: Option<u8>,
    now: Ts,
    step: Ts,
    cfg: &Config,
    out: &mut Vec<Action>,
) {
    if m != s.mains || s.mains_since.is_none() {
        s.mains = m;
        s.mains_since = Some(now);
        s.mains_since_soc = soc;
        s.mains_run_ms = 0;
    } else {
        s.mains_run_ms += step;
    }
    let since = s.mains_since.unwrap_or(now);
    match m {
        Mains::Off if s.mains_run_ms >= Ts::from(cfg.mains.lost_sec) * SEC => match &mut s.outage {
            // Отключение, найденное снова после пропуска, — не смена сети.
            Some(o) => o.confirmed = true,
            None => {
                s.outage = Some(Outage {
                    start_ts: since,
                    soc_start: s.mains_since_soc,
                    confirmed: true,
                    ..Default::default()
                });
                out.push(Action::OutageStart { start_ts: since, soc: s.mains_since_soc });
                out.push(Action::Event {
                    kind: "outage-start",
                    text: "Сеть пропала: работа от батареи".into(),
                    data: Some(json!({ "soc": s.mains_since_soc })),
                });
                end_manual(s, since, "сеть пропала", out);
            }
        },
        Mains::On => {
            s.level_streaks.clear();
            s.rebase_confirm_ms = 0;
            if s.mains_run_ms >= Ts::from(cfg.mains.back_sec) * SEC {
                // Сеть подтверждена: запомненный rebase больше не действует.
                s.rebase_at = None;
                s.delay_noted = false;
                if s.outage.take().is_some() {
                    out.push(Action::OutageEnd { end_ts: since, soc: s.mains_since_soc });
                    out.push(Action::Event {
                        kind: "outage-end",
                        text: "Сеть вернулась".into(),
                        data: Some(json!({ "soc": s.mains_since_soc })),
                    });
                    end_manual(s, since, "сеть вернулась", out);
                    s.backoff.retain(|k, _| k == ECO_KEY);
                }
            }
        }
        _ => {}
    }
}

/// Подтверждённая смена сети снимает ручной выбор, сделанный до неё.
fn end_manual(s: &mut DecisionState, change_at: Ts, why: &str, out: &mut Vec<Action>) {
    if s.manual != ManualMode::Auto && s.manual_at.is_none_or(|t| t < change_at) {
        s.manual = ManualMode::Auto;
        s.manual_at = None;
        out.push(Action::Event {
            kind: "mode-manual",
            text: format!("Режим «Авто»: {why}"),
            data: Some(json!({ "mode": "auto", "reset": true })),
        });
    }
}

pub fn set_manual(s: &mut DecisionState, m: ManualMode, now: Ts, out: &mut Vec<Action>) {
    s.manual = m;
    s.manual_at = (m != ManualMode::Auto).then_some(now);
    let (code, name) = match m {
        ManualMode::Auto => ("auto", "Авто"),
        ManualMode::Eco => ("eco", "Eco"),
        ManualMode::Max => ("max", "Максимум"),
    };
    out.push(Action::Event {
        kind: "mode-manual",
        text: format!("Выбран режим «{name}»"),
        data: Some(json!({ "mode": code })),
    });
}

/// Уровень eco, который должен стоять сейчас.
pub fn desired(s: &DecisionState) -> EcoLevel {
    let deep = s.outage.as_ref().is_some_and(|o| o.deep);
    match s.manual {
        ManualMode::Max => EcoLevel::None,
        _ if deep => EcoLevel::Deep,
        ManualMode::Eco => EcoLevel::Eco,
        ManualMode::Auto if s.outage.is_some() => EcoLevel::Eco,
        ManualMode::Auto => EcoLevel::None,
    }
}

/// Новый желаемый уровень — `SetEco` сразу; неприменённый после ошибки — повтор по бэкоффу.
pub fn sync_eco(s: &mut DecisionState, now: Ts, out: &mut Vec<Action>) {
    let want = desired(s);
    if want != s.eco {
        s.eco = want;
        s.backoff.remove(ECO_KEY);
        if want != s.eco_applied {
            out.push(Action::SetEco(want));
        }
    } else if s.eco != s.eco_applied && backoff_due(&s.backoff, ECO_KEY, now) {
        out.push(Action::SetEco(s.eco));
    }
}

pub fn report_eco(s: &mut DecisionState, level: EcoLevel, ok: bool, now: Ts) {
    if ok {
        s.eco_applied = level;
        s.backoff.remove(ECO_KEY);
    } else {
        guards::backoff_fail(&mut s.backoff, ECO_KEY, now);
    }
}

/// Последнее принятое показание старше `max_gap_sec` или впереди часов: о сети ничего не известно.
pub fn stale(s: &DecisionState, now: Ts, cfg: &Config) -> bool {
    s.last_accepted.is_none() || guards::starts_over(s, now, cfg)
}

/// Отсчёт до eco (FR-021): сеть пропала, отключение ещё не началось, eco не стоит, «Максимум» не выбран в этот раз.
/// Время — первое показание без входа + `lost_sec`; eco придёт с первым принятым показанием в этот момент или позже.
pub fn eco_at(s: &DecisionState, now: Ts, cfg: &Config) -> Option<Ts> {
    if s.mains != Mains::Off || s.outage.is_some() || s.eco != EcoLevel::None || stale(s, now, cfg) {
        return None;
    }
    let since = s.mains_since?;
    let max_survives = s.manual_at.is_some_and(|t| t >= since);
    if s.manual == ManualMode::Max && max_survives {
        return None;
    }
    let last = s.last_accepted.as_ref()?.ts;
    Some(last + (Ts::from(cfg.mains.lost_sec) * SEC - s.mains_run_ms).max(0))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::clock::MIN;

    fn r(ac: Option<u32>, dc: Option<u32>) -> Reading {
        Reading { soc: Some(50), ac_in_w: ac, dc_in_w: dc, ..Default::default() }
    }

    #[test]
    fn mains_from_ac_input_only() {
        assert_eq!(mains_of(&r(Some(172), None)), Mains::On);
        assert_eq!(mains_of(&r(Some(0), None)), Mains::Off);
        assert_eq!(mains_of(&r(Some(0), Some(300))), Mains::Off, "солнце на DC — это батарея");
        assert_eq!(mains_of(&r(None, Some(300))), Mains::Unknown);
    }

    #[test]
    fn desired_eco_table() {
        let mut s = DecisionState::default();
        assert_eq!(desired(&s), EcoLevel::None);
        s.manual = ManualMode::Eco;
        assert_eq!(desired(&s), EcoLevel::Eco);
        s.outage = Some(Outage::default());
        s.manual = ManualMode::Auto;
        assert_eq!(desired(&s), EcoLevel::Eco);
        s.manual = ManualMode::Max;
        assert_eq!(desired(&s), EcoLevel::None);
        s.outage.as_mut().unwrap().deep = true;
        assert_eq!(desired(&s), EcoLevel::None, "«Максимум» не пускает глубокий eco");
        s.manual = ManualMode::Eco;
        assert_eq!(desired(&s), EcoLevel::Deep);
    }

    #[test]
    fn lost_timer_counts_accepted_steps() {
        let c = Config::default();
        let mut s = DecisionState::default();
        let mut out = Vec::new();
        for i in 0..3 {
            update(&mut s, Mains::Off, Some(50), i * MIN, if i == 0 { 0 } else { MIN }, &c, &mut out);
        }
        assert!(s.outage.is_none());
        update(&mut s, Mains::Off, Some(49), 3 * MIN, MIN, &c, &mut out);
        let o = s.outage.as_ref().unwrap();
        assert_eq!((o.start_ts, o.soc_start), (0, Some(50)));
        assert!(out.contains(&Action::OutageStart { start_ts: 0, soc: Some(50) }));
    }

    #[test]
    fn eco_failure_retries_on_backoff() {
        let mut s = DecisionState { outage: Some(Outage::default()), ..Default::default() };
        let mut out = Vec::new();
        sync_eco(&mut s, 0, &mut out);
        assert_eq!(out, [Action::SetEco(EcoLevel::Eco)]);
        report_eco(&mut s, EcoLevel::Eco, false, 0);
        out.clear();
        sync_eco(&mut s, 30 * SEC, &mut out);
        assert!(out.is_empty());
        sync_eco(&mut s, MIN, &mut out);
        assert_eq!(out, [Action::SetEco(EcoLevel::Eco)]);
        report_eco(&mut s, EcoLevel::Eco, true, MIN);
        out.clear();
        sync_eco(&mut s, 20 * MIN, &mut out);
        assert!(out.is_empty());
    }
}
