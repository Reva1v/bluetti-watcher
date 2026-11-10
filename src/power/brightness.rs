//! Яркость подсветки мониторов (feature 005): «Сильный» и «Максимальный Eco» опускают её до `dim_pct`, прежние значения
//! сохраняются до записи (`meta.dim_restore`) и возвращаются. Вся логика — `apply` поверх трейта `Brightness`;
//! настоящие мониторы (DDC/CI через Win32) — только `WindowsBrightness`, в тестах он не вызывается.

use std::collections::{BTreeMap, BTreeSet};

use serde::{Deserialize, Serialize};

use super::PartResult;
pub use crate::model::Dim;

/// Сколько раз подряд сохранённый монитор может не отозваться, прежде чем его значение отбросят: спящий монитор
/// не отвечает по DDC/CI, а при смене раскладки дисплеев он не вернётся совсем.
pub const MAX_MISSES: u32 = 5;

/// Монитор, отдавший яркость. `id` — имя дисплея и номер физического монитора (`\\.\DISPLAY1#0`).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Monitor {
    pub id: String,
    /// Основной монитор Windows.
    pub primary: bool,
    pub min: u32,
    pub cur: u32,
    pub max: u32,
}

pub trait Brightness {
    /// Мониторы, отдавшие яркость; остальные (телевизор, выключен DDC/CI) в список не попадают.
    fn monitors(&mut self) -> Result<Vec<Monitor>, String>;
    fn set(&mut self, id: &str, value: u32) -> Result<(), String>;
}

/// Прежняя яркость затемнённых мониторов: сервис хранит её в `meta.dim_restore`.
#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(default)]
pub struct DimRestore {
    pub monitors: BTreeMap<String, u32>,
    /// Сколько попыток подряд сохранённый монитор не нашёлся.
    pub misses: BTreeMap<String, u32>,
}

/// Яркость затемнения: `min + (max − min) × pct / 100`.
pub fn dim_value(m: &Monitor, pct: u8) -> u32 {
    m.min + (u64::from(m.max.saturating_sub(m.min)) * u64::from(pct.min(100)) / 100) as u32
}

fn should_dim(target: Dim, m: &Monitor) -> bool {
    match target {
        Dim::All => true,
        Dim::Secondary => !m.primary,
        Dim::None => false,
    }
}

/// Привести мониторы к `target` одной функцией на все переходы. `saved` — что сохранено раньше; `persist` вызывается
/// с новым списком до первой записи (FR-008: прежнее значение сохраняется до изменения). Возвращает остаток для
/// сохранения (`None` — всё возвращено) и итоги частей; отказ одного монитора не мешает другим, остаток повторяется по
/// бэкоффу. Монитор без поддержки яркости — не отказ. `dry` — только описать, ничего не писать и не сохранять.
pub fn apply(
    b: &mut dyn Brightness,
    target: Dim,
    saved: Option<DimRestore>,
    pct: u8,
    dry: bool,
    persist: &mut dyn FnMut(&DimRestore),
) -> (Option<DimRestore>, Vec<PartResult>) {
    let original = saved.clone().filter(|r| !r.monitors.is_empty());
    let mut restore = saved.unwrap_or_default();
    if target == Dim::None && restore.monitors.is_empty() {
        return (None, Vec::new());
    }
    let monitors = match b.monitors() {
        Ok(m) => m,
        Err(e) => {
            return (original, vec![PartResult::fail("dim", format!("яркость мониторов не прочитана: {e}"))]);
        }
    };
    let tag = |s: String| if dry { format!("dry run: {s}") } else { s };
    let mut parts = Vec::new();

    // 1. Запомнить прежнюю яркость тех, кого будем гасить (имеющееся значение не перезаписывается).
    let mut fresh = BTreeSet::new();
    for m in &monitors {
        restore.misses.remove(&m.id);
        if should_dim(target, m) && !restore.monitors.contains_key(&m.id) {
            restore.monitors.insert(m.id.clone(), m.cur);
            fresh.insert(m.id.clone());
        }
    }
    if !fresh.is_empty() && !dry {
        persist(&restore);
    }

    // 2. Записать: погасить, кого нужно, и вернуть тех, кого гасить больше не нужно.
    for m in &monitors {
        let write = |b: &mut dyn Brightness, value: u32| if dry { Ok(()) } else { b.set(&m.id, value) };
        if should_dim(target, m) {
            let value = dim_value(m, pct);
            let before = restore.monitors[&m.id];
            // Уже затемнённый (сохранённый и сдвинутый) повторно не пишем; сохранённый, но не сдвинутый — отказ
            // или сбой в прошлый раз, пишем снова.
            if m.cur != value && (fresh.contains(&m.id) || m.cur == before) {
                parts.push(match write(b, value) {
                    Ok(()) => {
                        PartResult::ok("dim", tag(format!("монитор {}: яркость {} → {value}", m.id, m.cur)))
                    }
                    Err(e) => PartResult::fail("dim", format!("монитор {}: яркость не записана: {e}", m.id)),
                });
            }
        } else if let Some(&prev) = restore.monitors.get(&m.id) {
            let value = if m.max >= m.min { prev.clamp(m.min, m.max) } else { prev };
            let res = if m.cur == value { Ok(()) } else { write(b, value) };
            parts.push(match res {
                Ok(()) => {
                    restore.monitors.remove(&m.id);
                    PartResult::ok("dim", tag(format!("монитор {}: яркость возвращена ({value})", m.id)))
                }
                Err(e) => {
                    PartResult::fail("dim", format!("монитор {}: яркость {value} не возвращена: {e}", m.id))
                }
            });
        }
    }

    // 3. Сохранённые мониторы, которых сейчас нет: немного подождать, затем отбросить.
    let present: BTreeSet<&str> = monitors.iter().map(|m| m.id.as_str()).collect();
    let missing: Vec<String> =
        restore.monitors.keys().filter(|id| !present.contains(id.as_str())).cloned().collect();
    for id in missing {
        let n = restore.misses.entry(id.clone()).or_insert(0);
        *n += 1;
        if *n >= MAX_MISSES {
            let prev = restore.monitors.remove(&id).unwrap_or_default();
            restore.misses.remove(&id);
            parts.push(PartResult::ok(
                "dim",
                format!("монитор {id} не найден: сохранённая яркость {prev} отброшена"),
            ));
        } else {
            parts.push(PartResult::fail(
                "dim",
                format!(
                    "монитор {id} не отвечает ({n} из {MAX_MISSES}): яркость вернётся при следующей попытке"
                ),
            ));
        }
    }
    restore.misses.retain(|id, _| restore.monitors.contains_key(id));

    if dry {
        return (original, parts);
    }
    ((!restore.monitors.is_empty()).then_some(restore), parts)
}

#[cfg(windows)]
pub use win::WindowsBrightness;

/// Яркость через DDC/CI: `EnumDisplayMonitors` → `GetPhysicalMonitorsFromHMONITOR` → `GetMonitorBrightness` /
/// `SetMonitorBrightness` → `DestroyPhysicalMonitors`.
#[cfg(windows)]
mod win {
    use windows::Win32::Devices::Display::{
        DestroyPhysicalMonitors, GetMonitorBrightness, GetNumberOfPhysicalMonitorsFromHMONITOR,
        GetPhysicalMonitorsFromHMONITOR, PHYSICAL_MONITOR, SetMonitorBrightness,
    };
    use windows::Win32::Foundation::{HANDLE, LPARAM, RECT};
    use windows::Win32::Graphics::Gdi::{
        EnumDisplayMonitors, GetMonitorInfoW, HDC, HMONITOR, MONITORINFO, MONITORINFOEXW,
    };
    use windows::core::BOOL;

    use super::{Brightness, Monitor};

    /// `MONITORINFOF_PRIMARY` (в крейте `windows` константы нет).
    const MONITORINFOF_PRIMARY: u32 = 1;

    pub struct WindowsBrightness;

    unsafe extern "system" fn collect(h: HMONITOR, _: HDC, _: *mut RECT, data: LPARAM) -> BOOL {
        // SAFETY: `data` — указатель на `Vec<HMONITOR>` из `monitors_handles`, живущий до конца перечисления.
        let list = unsafe { &mut *(data.0 as *mut Vec<HMONITOR>) };
        list.push(h);
        BOOL::from(true)
    }

    fn monitors_handles() -> Vec<HMONITOR> {
        let mut list: Vec<HMONITOR> = Vec::new();
        // SAFETY: обратный вызов только дописывает в `list`, который живёт дольше вызова.
        unsafe {
            let _ = EnumDisplayMonitors(None, None, Some(collect), LPARAM(&mut list as *mut _ as isize));
        }
        list
    }

    /// Имя дисплея и признак основного.
    fn info(h: HMONITOR) -> Option<(String, bool)> {
        let mut mi = MONITORINFOEXW::default();
        mi.monitorInfo.cbSize = std::mem::size_of::<MONITORINFOEXW>() as u32;
        // SAFETY: `MONITORINFOEXW` начинается с `MONITORINFO`, `cbSize` указывает полный размер.
        let ok = unsafe { GetMonitorInfoW(h, &mut mi as *mut MONITORINFOEXW as *mut MONITORINFO) };
        if !ok.as_bool() {
            return None;
        }
        let len = mi.szDevice.iter().position(|&c| c == 0).unwrap_or(mi.szDevice.len());
        let primary = mi.monitorInfo.dwFlags & MONITORINFOF_PRIMARY != 0;
        Some((String::from_utf16_lossy(&mi.szDevice[..len]), primary))
    }

    /// Обход физических мониторов; `visit` получает имя дисплея, признак основного, номер и дескриптор. `true` — стоп.
    fn walk(mut visit: impl FnMut(&str, bool, usize, HANDLE) -> bool) {
        for h in monitors_handles() {
            let Some((name, primary)) = info(h) else { continue };
            let mut n = 0u32;
            // SAFETY: `h` получен от EnumDisplayMonitors; массив нужной длины; дескрипторы закрываются ниже.
            unsafe {
                if GetNumberOfPhysicalMonitorsFromHMONITOR(h, &mut n).is_err() || n == 0 {
                    continue;
                }
                let mut phys = vec![PHYSICAL_MONITOR::default(); n as usize];
                if GetPhysicalMonitorsFromHMONITOR(h, &mut phys).is_err() {
                    continue;
                }
                let mut stop = false;
                for (i, p) in phys.iter().enumerate() {
                    // Структура `packed`: поле копируется, ссылка на него не берётся.
                    let handle = p.hPhysicalMonitor;
                    if visit(&name, primary, i, handle) {
                        stop = true;
                        break;
                    }
                }
                let _ = DestroyPhysicalMonitors(&phys);
                if stop {
                    return;
                }
            }
        }
    }

    impl Brightness for WindowsBrightness {
        fn monitors(&mut self) -> Result<Vec<Monitor>, String> {
            let mut out = Vec::new();
            walk(|name, primary, i, handle| {
                let (mut min, mut cur, mut max) = (0u32, 0u32, 0u32);
                // SAFETY: дескриптор действителен внутри `walk`.
                let ok = unsafe { GetMonitorBrightness(handle, &mut min, &mut cur, &mut max) };
                if ok != 0 && max >= min {
                    out.push(Monitor { id: format!("{name}#{i}"), primary, min, cur, max });
                }
                false
            });
            Ok(out)
        }

        fn set(&mut self, id: &str, value: u32) -> Result<(), String> {
            let mut result = Err(format!("монитор {id} не найден"));
            walk(|name, _, i, handle| {
                if format!("{name}#{i}") != id {
                    return false;
                }
                // SAFETY: дескриптор действителен внутри `walk`.
                let ok = unsafe { SetMonitorBrightness(handle, value) };
                result = if ok != 0 {
                    Ok(())
                } else {
                    Err(format!("монитор {id} не принял яркость {value}"))
                };
                true
            });
            result
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Поддельные мониторы; запись меняет `cur`, как настоящий монитор.
    #[derive(Default)]
    struct Fake {
        monitors: Vec<Monitor>,
        /// Журнал: `set id value` и `persist`.
        log: Vec<String>,
        fail_set: Vec<&'static str>,
        fail_list: bool,
    }

    fn mon(id: &str, primary: bool, cur: u32) -> Monitor {
        Monitor { id: id.into(), primary, min: 0, cur, max: 100 }
    }

    impl Fake {
        fn two(a: u32, b: u32) -> Self {
            Self { monitors: vec![mon("A", true, a), mon("B", false, b)], ..Default::default() }
        }
        fn cur(&self, id: &str) -> u32 {
            self.monitors.iter().find(|m| m.id == id).unwrap().cur
        }
        fn writes(&self) -> Vec<String> {
            self.log.iter().filter(|l| l.starts_with("set")).cloned().collect()
        }
    }

    impl Brightness for Fake {
        fn monitors(&mut self) -> Result<Vec<Monitor>, String> {
            if self.fail_list {
                return Err("нет связи".into());
            }
            Ok(self.monitors.clone())
        }
        fn set(&mut self, id: &str, value: u32) -> Result<(), String> {
            self.log.push(format!("set {id} {value}"));
            if self.fail_set.contains(&id) {
                return Err("отказ".into());
            }
            self.monitors.iter_mut().find(|m| m.id == id).ok_or("нет")?.cur = value;
            Ok(())
        }
    }

    fn run(
        f: &mut Fake,
        target: Dim,
        saved: Option<DimRestore>,
        pct: u8,
    ) -> (Option<DimRestore>, Vec<PartResult>) {
        apply(f, target, saved, pct, false, &mut |_| {})
    }

    fn failed(parts: &[PartResult]) -> usize {
        parts.iter().filter(|p| !p.ok).count()
    }

    fn restore(pairs: &[(&str, u32)]) -> DimRestore {
        DimRestore {
            monitors: pairs.iter().map(|(k, v)| (k.to_string(), *v)).collect(),
            ..Default::default()
        }
    }

    #[test]
    fn dims_all_and_restores_exact_values() {
        let mut f = Fake::two(100, 70);
        let (saved, parts) = run(&mut f, Dim::All, None, 0);
        assert_eq!((f.cur("A"), f.cur("B")), (0, 0));
        assert_eq!(saved, Some(restore(&[("A", 100), ("B", 70)])));
        assert_eq!(failed(&parts), 0, "{parts:?}");
        let (saved, parts) = run(&mut f, Dim::None, saved, 0);
        assert_eq!((f.cur("A"), f.cur("B")), (100, 70));
        assert_eq!(saved, None, "всё возвращено");
        assert_eq!(failed(&parts), 0);
    }

    /// Общий журнал записей и сохранений: так видно, что сохранение идёт раньше первой записи.
    struct Logged {
        inner: Fake,
        log: std::rc::Rc<std::cell::RefCell<Vec<String>>>,
    }

    impl Brightness for Logged {
        fn monitors(&mut self) -> Result<Vec<Monitor>, String> {
            self.inner.monitors()
        }
        fn set(&mut self, id: &str, value: u32) -> Result<(), String> {
            self.log.borrow_mut().push(format!("set {id} {value}"));
            self.inner.set(id, value)
        }
    }

    #[test]
    fn saved_values_go_to_persist_before_the_first_write() {
        let log = std::rc::Rc::new(std::cell::RefCell::new(Vec::new()));
        let mut b = Logged { inner: Fake::two(100, 70), log: log.clone() };
        let persisted = std::cell::RefCell::new(Vec::new());
        let (saved, _) = apply(&mut b, Dim::All, None, 0, false, &mut |r| {
            log.borrow_mut().push("persist".to_string());
            persisted.borrow_mut().push(r.clone());
        });
        assert_eq!(*log.borrow(), ["persist", "set A 0", "set B 0"]);
        assert_eq!(
            *persisted.borrow(),
            [restore(&[("A", 100), ("B", 70)])],
            "сохранены прежние значения, не нули"
        );
        assert!(saved.is_some());
        // Возврат ничего нового не сохраняет заранее.
        log.borrow_mut().clear();
        apply(&mut b, Dim::None, saved, 0, false, &mut |_| log.borrow_mut().push("persist".to_string()));
        assert_eq!(*log.borrow(), ["set A 100", "set B 70"]);
    }

    #[test]
    fn repeated_dim_does_not_overwrite_the_saved_value() {
        let mut f = Fake::two(100, 70);
        let (saved, _) = run(&mut f, Dim::All, None, 0);
        f.log.clear();
        let (again, parts) = run(&mut f, Dim::All, saved.clone(), 0);
        assert_eq!(again, saved, "в сохранённом остались 100 и 70, а не 0");
        assert!(f.writes().is_empty(), "повторной записи нет: {:?}", f.log);
        assert!(parts.is_empty());
        // Сильный → максимальный: SetDim(All) тот же, ничего не меняется.
        let (done, _) = run(&mut f, Dim::None, again, 0);
        assert_eq!((f.cur("A"), f.cur("B"), done), (100, 70, None));
    }

    #[test]
    fn saved_zero_stays_zero() {
        let mut f = Fake::two(0, 50);
        let (saved, _) = run(&mut f, Dim::All, None, 0);
        assert_eq!(saved, Some(restore(&[("A", 0), ("B", 50)])));
        assert!(!f.writes().iter().any(|w| w.starts_with("set A")), "уже на нуле — запись не нужна");
        run(&mut f, Dim::None, saved, 0);
        assert_eq!((f.cur("A"), f.cur("B")), (0, 50), "нулевая яркость до уровня остаётся нулевой");
    }

    #[test]
    fn failing_monitor_does_not_block_the_other_and_is_retried() {
        let mut f = Fake::two(100, 70);
        f.fail_set = vec!["A"];
        let (saved, parts) = run(&mut f, Dim::All, None, 0);
        assert_eq!((f.cur("A"), f.cur("B")), (100, 0), "второй затемнён несмотря на отказ первого");
        assert_eq!(failed(&parts), 1);
        assert_eq!(saved, Some(restore(&[("A", 100), ("B", 70)])), "прежние значения сохранены");
        // Повтор: первый ещё не сдвинут — пишется снова, второй не перезаписывается.
        f.fail_set.clear();
        f.log.clear();
        let (saved, parts) = run(&mut f, Dim::All, saved, 0);
        assert_eq!(f.writes(), ["set A 0"]);
        assert_eq!(failed(&parts), 0);
        assert_eq!(saved, Some(restore(&[("A", 100), ("B", 70)])));
    }

    #[test]
    fn failing_restore_keeps_the_remainder() {
        let mut f = Fake::two(100, 70);
        let (saved, _) = run(&mut f, Dim::All, None, 0);
        f.fail_set = vec!["B"];
        let (left, parts) = run(&mut f, Dim::None, saved, 0);
        assert_eq!((f.cur("A"), f.cur("B")), (100, 0));
        assert_eq!(left, Some(restore(&[("B", 70)])), "остаток — только неудавшийся");
        assert_eq!(failed(&parts), 1);
        f.fail_set.clear();
        let (left, _) = run(&mut f, Dim::None, left, 0);
        assert_eq!((f.cur("B"), left), (70, None));
    }

    #[test]
    fn unreadable_list_keeps_saved_and_reports_failure() {
        let mut f = Fake::two(100, 70);
        f.fail_list = true;
        let saved = Some(restore(&[("A", 100)]));
        let (left, parts) = run(&mut f, Dim::None, saved.clone(), 0);
        assert_eq!(left, saved);
        assert_eq!(failed(&parts), 1);
        assert!(f.log.is_empty());
        // Нечего возвращать — монитор вообще не опрашивается.
        let (left, parts) = run(&mut f, Dim::None, None, 0);
        assert_eq!((left, parts.len()), (None, 0));
    }

    #[test]
    fn missing_monitor_is_kept_for_a_while_then_dropped() {
        let mut f = Fake::two(100, 70);
        let (mut saved, _) = run(&mut f, Dim::All, None, 0);
        f.monitors.retain(|m| m.id != "B");
        for attempt in 1..MAX_MISSES {
            let (left, parts) = run(&mut f, Dim::None, saved, 0);
            assert_eq!(failed(&parts), 1, "попытка {attempt}: ждём монитор");
            assert_eq!(left.as_ref().unwrap().monitors.keys().collect::<Vec<_>>(), ["B"]);
            saved = left;
        }
        let (left, parts) = run(&mut f, Dim::None, saved, 0);
        assert_eq!(left, None, "после {MAX_MISSES} попыток значение отброшено");
        assert!(
            parts.iter().all(|p| p.ok) && parts.iter().any(|p| p.detail.contains("отброшена")),
            "{parts:?}"
        );
        assert_eq!(f.cur("A"), 100, "найденный монитор вернулся сразу");
    }

    #[test]
    fn monitor_that_comes_back_is_restored_and_its_misses_reset() {
        let mut f = Fake::two(100, 70);
        let (saved, _) = run(&mut f, Dim::All, None, 0);
        let b = f.monitors.remove(1);
        let (saved, _) = run(&mut f, Dim::None, saved, 0);
        assert_eq!(saved.as_ref().unwrap().misses.get("B"), Some(&1));
        f.monitors.push(Monitor { cur: 0, ..b });
        let (saved, parts) = run(&mut f, Dim::None, saved, 0);
        assert_eq!((saved, f.cur("B"), failed(&parts)), (None, 70, 0));
    }

    #[test]
    fn dim_pct_and_nonzero_min_are_honoured() {
        let m = Monitor { id: "A".into(), primary: true, min: 10, cur: 80, max: 90 };
        assert_eq!((dim_value(&m, 0), dim_value(&m, 50), dim_value(&m, 100)), (10, 50, 90));
        let mut f = Fake { monitors: vec![m], ..Default::default() };
        let (saved, _) = run(&mut f, Dim::All, None, 25);
        assert_eq!(f.cur("A"), 30);
        run(&mut f, Dim::None, saved, 25);
        assert_eq!(f.cur("A"), 80);
        // Сохранённое вне диапазона монитора зажимается в него.
        let mut f = Fake { monitors: vec![mon("A", true, 0)], ..Default::default() };
        run(&mut f, Dim::None, Some(restore(&[("A", 250)])), 0);
        assert_eq!(f.cur("A"), 100);
    }

    #[test]
    fn dry_run_writes_and_saves_nothing() {
        let mut f = Fake::two(100, 70);
        let mut persisted = 0;
        let (saved, parts) = apply(&mut f, Dim::All, None, 0, true, &mut |_| persisted += 1);
        assert!(f.writes().is_empty());
        assert_eq!((persisted, saved), (0, None));
        assert!(
            parts.iter().all(|p| p.ok && p.detail.starts_with("dry run")) && parts.len() == 2,
            "{parts:?}"
        );
        let have = Some(restore(&[("A", 100), ("B", 70)]));
        let (saved, parts) = apply(&mut f, Dim::None, have.clone(), 0, true, &mut |_| persisted += 1);
        assert!(f.writes().is_empty());
        assert_eq!((persisted, saved), (0, have), "сохранённое в dry run не меняется");
        assert!(parts.iter().all(|p| p.detail.starts_with("dry run")));
    }

    fn game_setup() -> Fake {
        Fake::two(100, 70)
    }

    #[test]
    fn secondary_dims_only_the_non_primary() {
        let mut f = game_setup();
        let (saved, _) = run(&mut f, Dim::Secondary, None, 0);
        assert_eq!((f.cur("A"), f.cur("B")), (100, 0));
        assert_eq!(saved, Some(restore(&[("B", 70)])), "основной не запоминается: его не трогали");
    }

    #[test]
    fn all_to_secondary_restores_primary_and_leaves_the_other_alone() {
        let mut f = game_setup();
        let (saved, _) = run(&mut f, Dim::All, None, 0);
        f.log.clear();
        let (saved, parts) = run(&mut f, Dim::Secondary, saved, 0);
        assert_eq!(f.writes(), ["set A 100"], "второй не перезаписывается");
        assert_eq!((f.cur("A"), f.cur("B")), (100, 0));
        assert_eq!(saved, Some(restore(&[("B", 70)])));
        assert_eq!(failed(&parts), 0);
    }

    #[test]
    fn secondary_to_all_dims_the_primary() {
        let mut f = game_setup();
        let (saved, _) = run(&mut f, Dim::Secondary, None, 0);
        f.log.clear();
        let (saved, _) = run(&mut f, Dim::All, saved, 0);
        assert_eq!(f.writes(), ["set A 0"]);
        assert_eq!((f.cur("A"), f.cur("B")), (0, 0));
        assert_eq!(saved, Some(restore(&[("A", 100), ("B", 70)])));
    }

    #[test]
    fn secondary_to_none_restores_the_second() {
        let mut f = game_setup();
        let (saved, _) = run(&mut f, Dim::Secondary, None, 0);
        let (saved, _) = run(&mut f, Dim::None, saved, 0);
        assert_eq!((f.cur("A"), f.cur("B"), saved), (100, 70, None));
        assert_eq!(f.writes(), ["set B 0", "set B 70"], "основной не трогался ни разу");
    }

    #[test]
    fn single_monitor_is_not_dimmed_during_a_game() {
        let mut f = Fake { monitors: vec![mon("A", true, 100)], ..Default::default() };
        let (saved, parts) = run(&mut f, Dim::Secondary, None, 0);
        assert!(f.log.is_empty() && saved.is_none() && parts.is_empty());
        // Без основного в списке (он не отдал яркость) гасятся все найденные.
        let mut f = Fake { monitors: vec![mon("B", false, 70)], ..Default::default() };
        run(&mut f, Dim::Secondary, None, 0);
        assert_eq!(f.cur("B"), 0);
    }

    #[test]
    fn owner_changing_brightness_is_not_overwritten_by_a_repeat() {
        let mut f = Fake::two(100, 70);
        let (saved, _) = run(&mut f, Dim::All, None, 0);
        f.monitors[1].cur = 40; // владелец поднял второй монитор вручную
        f.log.clear();
        let (again, _) = run(&mut f, Dim::All, saved.clone(), 0);
        assert!(f.writes().is_empty(), "повторное SetDim не опускает яркость заново");
        assert_eq!(again, saved);
        run(&mut f, Dim::None, again, 0);
        assert_eq!(f.cur("B"), 70, "при снятии возвращается сохранённое значение");
    }

    #[test]
    fn restore_state_round_trips_and_reads_without_misses() {
        let r = DimRestore {
            monitors: [("\\\\.\\DISPLAY1#0".to_string(), 100)].into(),
            misses: [("\\\\.\\DISPLAY1#0".to_string(), 2)].into(),
        };
        assert_eq!(serde_json::from_str::<DimRestore>(&serde_json::to_string(&r).unwrap()).unwrap(), r);
        let old: DimRestore = serde_json::from_str(r#"{"monitors":{"X#0":5}}"#).unwrap();
        assert_eq!(old, restore(&[("X#0", 5)]));
    }
}
