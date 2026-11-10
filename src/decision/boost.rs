//! Переключатели «Сильный Eco», «Максимальный Eco» и «Гасить мониторы» (feature 005). Чистый код: без I/O и часов.
//! Переключатели не трогают выбор «Авто / Eco / Максимум»; применённый уровень — `max(по выбору, по переключателям)`.

use serde::{Deserialize, Serialize};
use serde_json::json;

use crate::clock::Ts;
use crate::model::{Dim, EcoLevel, ManualMode};

use super::guards::{self, backoff_due};
use super::{Action, DecisionState};

/// Ключ бэкоффа затемнения (см. `mains::ECO_KEY`).
pub const DIM_KEY: &str = "dim";

/// Один переключатель уровня: включён ли, и порогом ли (`auto`) или владельцем.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(default)]
pub struct Switch {
    pub on: bool,
    pub auto: bool,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(default)]
pub struct Boost {
    pub strong: Switch,
    pub max: Switch,
    /// Владелец выключил «Гасить мониторы»; сбрасывается, когда после действия уровня оба сняты (FR-010).
    pub dim_off: bool,
    /// На последней проверке хотя бы один уровень был включён: только после этого снятие обоих сбрасывает `dim_off`,
    /// иначе выключение «Гасить мониторы» без уровня исчезло бы в том же цикле (сценарий 3.9).
    pub live: bool,
}

/// Нажатие на дашборде.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum BoostRequest {
    Strong(bool),
    Max(bool),
    /// `true` — «Гасить мониторы» включено.
    Dim(bool),
}

/// Какой уровень включает порог.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Which {
    Strong,
    Max,
}

fn name(which: Which) -> &'static str {
    match which {
        Which::Strong => "Сильный Eco",
        Which::Max => "Максимальный Eco",
    }
}

fn event(kind: &'static str, text: String, data: serde_json::Value) -> Action {
    Action::Event { kind, text, data: Some(data) }
}

/// Нажатие владельца: уровень включается или выключается вручную (`auto = false`); выбор Авто / Eco / Максимум
/// не меняется. Нажатие без изменений событие не пишет.
pub fn request(s: &mut DecisionState, req: BoostRequest, out: &mut Vec<Action>) {
    let b = &mut s.boost;
    let mut manual = |sw: &mut Switch, which: Which, on: bool, code: &str| {
        let new = Switch { on, auto: false };
        if *sw == new {
            return;
        }
        *sw = new;
        let text = format!("«{}» {}", name(which), if on { "включён вручную" } else { "выключен" });
        out.push(event("boost-manual", text, json!({ "which": code, "on": on })));
    };
    match req {
        BoostRequest::Strong(on) => manual(&mut b.strong, Which::Strong, on, "strong"),
        BoostRequest::Max(on) => manual(&mut b.max, Which::Max, on, "max"),
        BoostRequest::Dim(on) => {
            if b.dim_off == on {
                b.dim_off = !on;
                let text = format!("Затемнение мониторов {}", if on { "включено" } else { "выключено" });
                out.push(event("boost-manual", text, json!({ "which": "dim", "on": on })));
            }
        }
    }
}

/// Порог заряда в подтверждённом отключении. При «Максимум» уровень сам не включается (FR-006); включённый владельцем
/// остаётся его. Порог действует один раз за отключение, даже если уровни решаются заново после пропуска (FR-007).
/// Возвращает `true`, если уровень включён после вызова.
pub fn on_threshold(s: &mut DecisionState, which: Which) -> bool {
    let sw = match which {
        Which::Strong => &mut s.boost.strong,
        Which::Max => &mut s.boost.max,
    };
    if s.manual == ManualMode::Max {
        return false;
    }
    if let Some(o) = &mut s.outage {
        let fired = match which {
            Which::Strong => &mut o.strong_fired,
            Which::Max => &mut o.max_fired,
        };
        if std::mem::replace(fired, true) {
            return sw.on;
        }
    }
    if !sw.on {
        *sw = Switch { on: true, auto: true };
    }
    true
}

/// Подтверждённый возврат сети выключает оба уровня, включённые автоматикой или вручную (FR-007).
pub fn on_mains_back(s: &mut DecisionState, out: &mut Vec<Action>) {
    let (strong, max) = (s.boost.strong.on, s.boost.max.on);
    if !strong && !max {
        return;
    }
    s.boost.strong = Switch::default();
    s.boost.max = Switch::default();
    let text = match (strong, max) {
        (true, true) => "«Сильный Eco» и «Максимальный Eco» выключены: сеть вернулась".to_string(),
        (true, false) => "«Сильный Eco» выключен: сеть вернулась".to_string(),
        _ => "«Максимальный Eco» выключен: сеть вернулась".to_string(),
    };
    out.push(event("boost-off", text, json!({ "strong": strong, "max": max, "reason": "mains" })));
}

/// Уровень, который требуют переключатели.
pub fn level(s: &DecisionState) -> EcoLevel {
    if s.boost.max.on {
        EcoLevel::Deep
    } else if s.boost.strong.on {
        EcoLevel::Strong
    } else {
        EcoLevel::None
    }
}

/// После всех изменений цикла: когда действие уровня закончилось, «Гасить мониторы» возвращается во «включено».
pub fn settle(s: &mut DecisionState) {
    let b = &mut s.boost;
    if b.strong.on || b.max.on {
        b.live = true;
    } else if b.live {
        b.live = false;
        b.dim_off = false;
    }
}

/// Что должно быть затемнено: ничего, если уровень ниже сильного или владелец выключил затемнение; иначе все
/// мониторы, а при запущенной игре — все, кроме основного.
pub fn dim_wanted(s: &DecisionState, game: bool) -> Dim {
    if level(s) < EcoLevel::Strong || s.boost.dim_off {
        Dim::None
    } else if game {
        Dim::Secondary
    } else {
        Dim::All
    }
}

/// Новое желаемое затемнение — `SetDim` сразу; неприменённое после ошибки — повтор по бэкоффу (как `sync_eco`).
pub fn sync_dim(s: &mut DecisionState, game: bool, now: Ts, out: &mut Vec<Action>) {
    let want = dim_wanted(s, game);
    if want != s.dim {
        s.dim = want;
        s.backoff.remove(DIM_KEY);
        if want != s.dim_applied {
            out.push(Action::SetDim(want));
        }
    } else if s.dim != s.dim_applied && backoff_due(&s.backoff, DIM_KEY, now) {
        out.push(Action::SetDim(s.dim));
    }
}

pub fn report_dim(s: &mut DecisionState, target: Dim, ok: bool, now: Ts) {
    if ok {
        s.dim_applied = target;
        s.backoff.remove(DIM_KEY);
    } else {
        guards::backoff_fail(&mut s.backoff, DIM_KEY, now);
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::clock::{MIN, SEC};

    fn req(s: &mut DecisionState, r: BoostRequest) -> Vec<Action> {
        let mut out = Vec::new();
        request(s, r, &mut out);
        out
    }

    fn texts(out: &[Action]) -> Vec<&str> {
        out.iter()
            .filter_map(|a| match a {
                Action::Event { text, .. } => Some(text.as_str()),
                _ => None,
            })
            .collect()
    }

    #[test]
    fn manual_press_sets_the_switch_and_never_touches_the_selection() {
        for manual in [ManualMode::Auto, ManualMode::Eco, ManualMode::Max] {
            let mut s = DecisionState { manual, manual_at: Some(5), ..Default::default() };
            let out = req(&mut s, BoostRequest::Strong(true));
            assert_eq!(texts(&out), ["«Сильный Eco» включён вручную"]);
            assert_eq!(s.boost.strong, Switch { on: true, auto: false });
            req(&mut s, BoostRequest::Max(true));
            req(&mut s, BoostRequest::Max(false));
            req(&mut s, BoostRequest::Strong(false));
            assert_eq!((s.manual, s.manual_at), (manual, Some(5)), "выбор режима не меняется");
            assert_eq!(s.boost.strong, Switch::default());
        }
    }

    #[test]
    fn manual_press_takes_over_an_automatic_switch_and_repeat_is_silent() {
        let mut s = DecisionState::default();
        assert!(on_threshold(&mut s, Which::Strong));
        assert_eq!(s.boost.strong, Switch { on: true, auto: true });
        let out = req(&mut s, BoostRequest::Strong(true));
        assert_eq!(s.boost.strong, Switch { on: true, auto: false }, "нажатие делает уровень ручным");
        assert_eq!(out.len(), 1);
        assert!(req(&mut s, BoostRequest::Strong(true)).is_empty());
        assert!(req(&mut s, BoostRequest::Max(false)).is_empty(), "выключенный уровень выключить нечем");
        assert_eq!(texts(&req(&mut s, BoostRequest::Strong(false))), ["«Сильный Eco» выключен"]);
    }

    #[test]
    fn dim_press_sets_dim_off_and_reports_once() {
        let mut s = DecisionState::default();
        assert!(req(&mut s, BoostRequest::Dim(true)).is_empty(), "по умолчанию затемнение включено");
        assert_eq!(texts(&req(&mut s, BoostRequest::Dim(false))), ["Затемнение мониторов выключено"]);
        assert!(s.boost.dim_off);
        assert!(req(&mut s, BoostRequest::Dim(false)).is_empty());
        assert_eq!(texts(&req(&mut s, BoostRequest::Dim(true))), ["Затемнение мониторов включено"]);
        assert!(!s.boost.dim_off);
    }

    #[test]
    fn level_follows_the_switches() {
        let mut s = DecisionState::default();
        assert_eq!(level(&s), EcoLevel::None);
        req(&mut s, BoostRequest::Strong(true));
        assert_eq!(level(&s), EcoLevel::Strong);
        req(&mut s, BoostRequest::Max(true));
        assert_eq!(level(&s), EcoLevel::Deep, "оба включены — действует максимальный");
        req(&mut s, BoostRequest::Strong(false));
        assert_eq!(level(&s), EcoLevel::Deep);
        req(&mut s, BoostRequest::Max(false));
        assert_eq!(level(&s), EcoLevel::None);
        req(&mut s, BoostRequest::Max(true));
        assert_eq!(level(&s), EcoLevel::Deep, "максимальный и без сильного");
    }

    #[test]
    fn threshold_is_blocked_by_max_but_not_by_eco_or_auto() {
        for (manual, on) in [(ManualMode::Auto, true), (ManualMode::Eco, true), (ManualMode::Max, false)] {
            let mut s = DecisionState { manual, ..Default::default() };
            assert_eq!(on_threshold(&mut s, Which::Strong), on, "{manual:?}");
            assert_eq!(on_threshold(&mut s, Which::Max), on, "{manual:?}");
            assert_eq!((s.boost.strong.on, s.boost.max.on), (on, on));
            assert_eq!((s.boost.strong.auto, s.boost.max.auto), (on, on));
        }
        // Включённый владельцем остаётся его, даже если порог дошёл.
        let mut s = DecisionState::default();
        req(&mut s, BoostRequest::Max(true));
        assert!(on_threshold(&mut s, Which::Max));
        assert_eq!(s.boost.max, Switch { on: true, auto: false });
        // «Максимум» не выключает уже включённое вручную.
        let mut s = DecisionState { manual: ManualMode::Max, ..Default::default() };
        req(&mut s, BoostRequest::Strong(true));
        assert!(!on_threshold(&mut s, Which::Strong), "при «Максимум» порог ничего не делает");
        assert!(s.boost.strong.on);
    }

    #[test]
    fn mains_back_clears_both_kinds_of_switch() {
        let mut s = DecisionState::default();
        let mut out = Vec::new();
        on_mains_back(&mut s, &mut out);
        assert!(out.is_empty(), "нечего снимать — тихо");
        on_threshold(&mut s, Which::Strong);
        req(&mut s, BoostRequest::Max(true));
        on_mains_back(&mut s, &mut out);
        assert_eq!((s.boost.strong, s.boost.max), (Switch::default(), Switch::default()));
        assert_eq!(texts(&out), ["«Сильный Eco» и «Максимальный Eco» выключены: сеть вернулась"]);
        req(&mut s, BoostRequest::Strong(true));
        out.clear();
        on_mains_back(&mut s, &mut out);
        assert_eq!(texts(&out), ["«Сильный Eco» выключен: сеть вернулась"]);
    }

    #[test]
    fn dim_off_returns_after_both_levels_are_off_and_holds_while_one_is_on() {
        let mut s = DecisionState::default();
        req(&mut s, BoostRequest::Strong(true));
        req(&mut s, BoostRequest::Max(true));
        settle(&mut s);
        req(&mut s, BoostRequest::Dim(false));
        settle(&mut s);
        assert!(s.boost.dim_off);
        req(&mut s, BoostRequest::Strong(false));
        settle(&mut s);
        assert!(s.boost.dim_off, "один уровень ещё действует");
        req(&mut s, BoostRequest::Max(false));
        settle(&mut s);
        assert!(!s.boost.dim_off, "оба сняты — затемнение снова включено");
        // Следующее включение уровня затемняет.
        req(&mut s, BoostRequest::Strong(true));
        settle(&mut s);
        assert_eq!(dim_wanted(&s, false), Dim::All);
    }

    #[test]
    fn dim_off_set_without_a_level_holds_until_the_next_level_ends() {
        let mut s = DecisionState::default();
        req(&mut s, BoostRequest::Dim(false));
        settle(&mut s);
        settle(&mut s);
        assert!(s.boost.dim_off, "сценарий 3.9: выключение держится до конца ближайшего действия уровня");
        req(&mut s, BoostRequest::Strong(true));
        settle(&mut s);
        assert_eq!(dim_wanted(&s, false), Dim::None, "уровень включился, затемнение выключено владельцем");
        req(&mut s, BoostRequest::Strong(false));
        settle(&mut s);
        assert!(!s.boost.dim_off);
        // Включили и выключили в одном цикле — уровня «не было», выключение остаётся.
        let mut s = DecisionState::default();
        req(&mut s, BoostRequest::Dim(false));
        req(&mut s, BoostRequest::Strong(true));
        req(&mut s, BoostRequest::Strong(false));
        settle(&mut s);
        assert!(s.boost.dim_off);
    }

    #[test]
    fn dim_wanted_table() {
        // (strong, max, dim_off, game) -> Dim
        let cases = [
            (false, false, false, false, Dim::None),
            (false, false, false, true, Dim::None),
            (true, false, false, false, Dim::All),
            (true, false, false, true, Dim::Secondary),
            (false, true, false, false, Dim::All),
            (false, true, false, true, Dim::Secondary),
            (true, true, false, false, Dim::All),
            (true, false, true, false, Dim::None),
            (true, false, true, true, Dim::None),
            (false, true, true, false, Dim::None),
            (false, false, true, true, Dim::None),
        ];
        for (strong, max, dim_off, game, want) in cases {
            let mut s = DecisionState::default();
            s.boost.strong.on = strong;
            s.boost.max.on = max;
            s.boost.dim_off = dim_off;
            assert_eq!(dim_wanted(&s, game), want, "strong={strong} max={max} dim_off={dim_off} game={game}");
        }
        // Обычный Eco по выбору яркость не трогает.
        let s = DecisionState { manual: ManualMode::Eco, eco: EcoLevel::Eco, ..Default::default() };
        assert_eq!(dim_wanted(&s, false), Dim::None);
    }

    #[test]
    fn game_change_between_cycles_sends_set_dim_once() {
        let mut s = DecisionState::default();
        s.boost.strong.on = true;
        let mut out = Vec::new();
        sync_dim(&mut s, false, 0, &mut out);
        assert_eq!(out, [Action::SetDim(Dim::All)]);
        report_dim(&mut s, Dim::All, true, 0);
        out.clear();
        sync_dim(&mut s, false, MIN, &mut out);
        assert!(out.is_empty(), "ничего не изменилось");
        sync_dim(&mut s, true, 2 * MIN, &mut out);
        assert_eq!(out, [Action::SetDim(Dim::Secondary)]);
        report_dim(&mut s, Dim::Secondary, true, 2 * MIN);
        out.clear();
        sync_dim(&mut s, true, 3 * MIN, &mut out);
        assert!(out.is_empty(), "игра идёт — второй записи нет");
        sync_dim(&mut s, false, 4 * MIN, &mut out);
        assert_eq!(out, [Action::SetDim(Dim::All)]);
        report_dim(&mut s, Dim::All, true, 4 * MIN);
        // Уровень снят.
        s.boost.strong = Switch::default();
        out.clear();
        sync_dim(&mut s, true, 5 * MIN, &mut out);
        assert_eq!(out, [Action::SetDim(Dim::None)]);
    }

    #[test]
    fn dim_failure_retries_on_backoff_and_a_new_wish_resets_it() {
        let mut s = DecisionState::default();
        s.boost.max.on = true;
        let mut out = Vec::new();
        sync_dim(&mut s, false, 0, &mut out);
        report_dim(&mut s, Dim::All, false, 0);
        out.clear();
        sync_dim(&mut s, false, 30 * SEC, &mut out);
        assert!(out.is_empty());
        sync_dim(&mut s, false, MIN, &mut out);
        assert_eq!(out, [Action::SetDim(Dim::All)]);
        report_dim(&mut s, Dim::All, false, MIN);
        out.clear();
        sync_dim(&mut s, false, MIN + 90 * SEC, &mut out);
        assert!(out.is_empty(), "вторая пауза — 2 минуты");
        // Желание изменилось (игра) — сразу, без ожидания паузы.
        sync_dim(&mut s, true, MIN + 91 * SEC, &mut out);
        assert_eq!(out, [Action::SetDim(Dim::Secondary)]);
        report_dim(&mut s, Dim::Secondary, true, MIN + 91 * SEC);
        assert!(!s.backoff.contains_key(DIM_KEY));
        out.clear();
        sync_dim(&mut s, true, 30 * MIN, &mut out);
        assert!(out.is_empty());
    }

    #[test]
    fn boost_round_trips_through_json_and_old_state_has_defaults() {
        let mut s = DecisionState::default();
        req(&mut s, BoostRequest::Strong(true));
        on_threshold(&mut s, Which::Max);
        req(&mut s, BoostRequest::Dim(false));
        settle(&mut s);
        s.dim = Dim::Secondary;
        s.dim_applied = Dim::All;
        let back: DecisionState = serde_json::from_str(&serde_json::to_string(&s).unwrap()).unwrap();
        assert_eq!(back, s);
        let old: DecisionState = serde_json::from_str(r#"{"manual":"eco"}"#).unwrap();
        assert_eq!((old.boost, old.dim, old.dim_applied), (Boost::default(), Dim::None, Dim::None));
        let partial: DecisionState = serde_json::from_str(r#"{"boost":{"max":{"on":true}}}"#).unwrap();
        assert_eq!(partial.boost.max, Switch { on: true, auto: false });
        assert!(!partial.boost.strong.on);
    }
}
