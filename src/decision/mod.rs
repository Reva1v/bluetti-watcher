//! Чистое ядро решений (конституция II, V): показание, состояние, время и конфиг на входе — статус показания,
//! действия и новое состояние на выходе. Без I/O и системных часов; `now` — время получения показания.

pub mod guards;
pub mod levels;
pub mod mains;

use std::collections::BTreeMap;

use serde::{Deserialize, Serialize};

use crate::clock::Ts;
use crate::config::Config;
use crate::model::{EcoLevel, Mains, ManualMode, MissReason, ReadStatus, Reading};

pub use guards::{Backoff, Verdict};
pub use levels::{Level, Streak};

pub struct Input {
    /// Результат чтения в этом цикле.
    pub reading: Result<Reading, MissReason>,
    /// Чтение запрошено действием `NeedFreshReading`.
    pub fresh: bool,
    /// Есть файл `no-hibernate`.
    pub kill_switch: bool,
    /// Кнопка на дашборде с прошлого цикла.
    pub manual_request: Option<ManualMode>,
}

/// Текущее отключение (data-model.md).
#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
#[serde(default)]
pub struct Outage {
    /// Первое показание без входа AC в серии, подтвердившей отключение.
    pub start_ts: Ts,
    pub soc_start: Option<u8>,
    /// `false` после сброса по пропуску, пока сеть не подтверждена заново: уровни не срабатывают.
    pub confirmed: bool,
    /// Был уровень «мало»: глубокий eco до конца отключения (переживает сброс по пропуску).
    pub deep: bool,
    /// Уровни, до которых дошла лестница.
    pub levels_reached: Vec<Level>,
    /// Уровни, чьё действие выполнено успешно.
    pub levels_fired: Vec<Level>,
    pub warn_at: Option<Ts>,
    /// Когда отправлена команда гибернации.
    pub hibernate_at: Option<Ts>,
}

#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
#[serde(default)]
pub struct DecisionState {
    /// База для проверки правдоподобия.
    pub last_accepted: Option<Reading>,
    /// Последние отвергнутые как неправдоподобные (≤ 3) — для rebase.
    pub rejected_streak: Vec<Reading>,
    pub mains: Mains,
    /// Начало текущей серии показаний с тем же состоянием сети.
    pub mains_since: Option<Ts>,
    pub mains_since_soc: Option<u8>,
    /// Засчитанное время серии (шаг ≤ 2 × poll).
    pub mains_run_ms: Ts,
    pub outage: Option<Outage>,
    pub level_streaks: BTreeMap<Level, Streak>,
    pub manual: ManualMode,
    pub manual_at: Option<Ts>,
    /// Желаемый уровень eco.
    pub eco: EcoLevel,
    /// Последний успешно применённый уровень eco.
    pub eco_applied: EcoLevel,
    pub backoff: BTreeMap<String, Backoff>,
    /// Последний rebase (или скачок после пропуска) до подтверждённого возврата сети.
    pub rebase_at: Option<Ts>,
    /// Время согласных показаний не выше уровня гибернации после rebase.
    pub rebase_confirm_ms: Ts,
    /// Событие `hibernate-delayed` для запомненного rebase уже было.
    pub delay_noted: bool,
    pub hibernate_delayed_until: Option<Ts>,
    /// Гибернация выполнена; принятое показание позже чем через 2 мин — ПК проснулся.
    pub hibernated_at: Option<Ts>,
    /// Ждём свежее показание перед гибернацией.
    pub fresh_pending: bool,
}

#[derive(Debug, Clone, PartialEq)]
pub struct Decision {
    /// Как сохранить показание (`Miss`, если чтения нет).
    pub status: ReadStatus,
    /// Код причины для `rejected`/`miss`.
    pub reason: Option<&'static str>,
    pub actions: Vec<Action>,
}

#[derive(Debug, Clone, PartialEq)]
pub enum Action {
    /// Желаемый уровень eco изменился (или повтор после ошибки).
    SetEco(EcoLevel),
    /// Уведомление Windows, текст по-русски.
    Notify {
        kind: &'static str,
        text: String,
    },
    Hibernate,
    /// Прочитать станцию сразу ещё раз и вызвать `decide` с `fresh = true`.
    NeedFreshReading,
    /// Событие в историю; виды из data-model.md.
    Event {
        kind: &'static str,
        text: String,
        data: Option<serde_json::Value>,
    },
    OutageStart {
        start_ts: Ts,
        soc: Option<u8>,
    },
    OutageEnd {
        end_ts: Ts,
        soc: Option<u8>,
    },
}

pub fn decide(state: &mut DecisionState, input: &Input, now: Ts, cfg: &Config) -> Decision {
    let s = state;
    let mut out = Vec::new();
    if let Some(m) = input.manual_request {
        mains::set_manual(s, m, now, &mut out);
    }
    // Свежее показание засчитывается только в ответ на `NeedFreshReading`; запрос живёт один цикл.
    let fresh_ok = input.fresh && s.fresh_pending;
    s.fresh_pending = false;

    let (status, reason) = match &input.reading {
        Err(e) => (ReadStatus::Miss, Some(e.code())),
        Ok(r) => match guards::classify(s, r, now, cfg) {
            Verdict::Rejected(why) => (ReadStatus::Rejected, Some(why)),
            Verdict::Rebased => {
                accept(s, r, now, cfg);
                guards::baseline_moved(s, now);
                (ReadStatus::Rebased, None)
            }
            Verdict::Accepted { jumped } => {
                let step = accept(s, r, now, cfg);
                let m = mains::mains_of(r);
                mains::update(s, m, r.soc, now, step, cfg, &mut out);
                if let (Mains::Off, Some(soc)) = (m, r.soc) {
                    levels::update_streaks(s, soc, now, step, cfg);
                    if jumped {
                        guards::baseline_moved(s, now);
                    }
                    if s.outage.as_ref().is_some_and(|o| o.confirmed) {
                        levels::ladder(s, input, fresh_ok, soc, now, cfg, &mut out);
                    }
                } else if jumped {
                    guards::baseline_moved(s, now);
                }
                (ReadStatus::Accepted, None)
            }
        },
    };
    mains::sync_eco(s, now, &mut out);
    Decision { status, reason, actions: out }
}

/// Общее для принятого и rebase: сброс по пропуску или часам, пробуждение, новая база. Возвращает шаг таймеров.
fn accept(s: &mut DecisionState, r: &Reading, now: Ts, cfg: &Config) -> Ts {
    let step = if guards::starts_over(s, now, cfg) {
        guards::reset(s, now);
        0
    } else {
        s.last_accepted.as_ref().map_or(0, |l| (now - l.ts).clamp(0, guards::step_cap(cfg)))
    };
    levels::rearm_after_resume(s, now);
    let mut base = r.clone();
    base.ts = now;
    s.last_accepted = Some(base);
    s.rejected_streak.clear();
    s.hibernate_delayed_until = None;
    step
}

/// Итог выполнения действия сервисом. Уровень засчитывается только после успеха; неудачи — бэкофф
/// 1, 2, 4, 8 мин, затем каждые 10 мин.
pub fn report(state: &mut DecisionState, action: &Action, ok: bool, now: Ts) {
    match action {
        Action::SetEco(l) => mains::report_eco(state, *l, ok, now),
        Action::Hibernate => levels::report_hibernate(state, ok, now),
        Action::Notify { kind, .. } => levels::report_notify(state, kind, ok, now),
        _ => {}
    }
}

#[derive(Debug, Clone, Default, Serialize)]
pub struct View {
    pub mains: Mains,
    pub outage_since: Option<Ts>,
    /// Отсчёт до eco (FR-021).
    pub eco_at: Option<Ts>,
    /// Желаемый уровень eco.
    pub eco: EcoLevel,
    pub manual: ManualMode,
    pub level_low: bool,
    pub level_critical: bool,
    pub level_warn: bool,
    pub hibernate_delayed_until: Option<Ts>,
}

pub fn view(state: &DecisionState, now: Ts, cfg: &Config) -> View {
    let reached = |l: Level| state.outage.as_ref().is_some_and(|o| o.levels_reached.contains(&l));
    View {
        mains: if mains::stale(state, now, cfg) { Mains::Unknown } else { state.mains },
        outage_since: state.outage.as_ref().map(|o| o.start_ts),
        eco_at: mains::eco_at(state, now, cfg),
        eco: state.eco,
        manual: state.manual,
        level_low: reached(Level::Low),
        level_critical: reached(Level::Critical),
        level_warn: reached(Level::Warn),
        hibernate_delayed_until: state.hibernate_delayed_until,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::clock::MIN;

    #[test]
    fn state_round_trips_through_json() {
        let mut s = DecisionState {
            outage: Some(Outage { start_ts: 5, levels_reached: vec![Level::Low], ..Default::default() }),
            ..Default::default()
        };
        s.level_streaks.insert(Level::Warn, Streak { since: 1, run_ms: 2 });
        guards::backoff_fail(&mut s.backoff, "eco", 0);
        let text = serde_json::to_string(&s).unwrap();
        let back: DecisionState = serde_json::from_str(&text).unwrap();
        assert_eq!(back, s);
        // Старое или неполное состояние читается с умолчаниями.
        let partial: DecisionState = serde_json::from_str(r#"{"manual":"max"}"#).unwrap();
        assert_eq!(partial.manual, ManualMode::Max);
    }

    #[test]
    fn rebased_reading_moves_only_the_baseline() {
        let c = Config::default();
        let mut s = DecisionState::default();
        let rd = |ts: Ts, soc: u8| Input {
            reading: Ok(Reading { ts, soc: Some(soc), ac_in_w: Some(0), ..Default::default() }),
            fresh: false,
            kill_switch: false,
            manual_request: None,
        };
        decide(&mut s, &rd(0, 60), 0, &c);
        decide(&mut s, &rd(MIN, 60), MIN, &c);
        let run = s.mains_run_ms;
        let streaks = s.level_streaks.clone();
        assert_eq!(decide(&mut s, &rd(2 * MIN, 4), 2 * MIN, &c).status, ReadStatus::Rejected);
        assert_eq!(decide(&mut s, &rd(3 * MIN, 4), 3 * MIN, &c).status, ReadStatus::Rejected);
        let d = decide(&mut s, &rd(4 * MIN, 4), 4 * MIN, &c);
        assert_eq!(d.status, ReadStatus::Rebased);
        assert_eq!(s.mains_run_ms, run, "rebase не двигает таймер сети");
        assert_eq!(s.level_streaks, streaks, "rebase не начинает серию уровня");
        assert_eq!(s.rebase_at, Some(4 * MIN));
        assert_eq!(s.last_accepted.as_ref().unwrap().soc, Some(4));
    }
}
