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

/// Монитор, отдавший яркость. `id` — постоянная личность монитора (модель и экземпляр устройства,
/// `AUS258A#5&1ba1d4b0&0&UID33029`): имена `\\.\DISPLAYn` Windows раздаёт заново, когда монитор пропадает со связи.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Monitor {
    pub id: String,
    /// Для истории: модель и текущее имя дисплея (`AUS258A (\\.\DISPLAY1)`).
    pub label: String,
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
    /// Мониторы из последнего `monitors()`, которые физически есть, но не отдали яркость и после повторов:
    /// `(id, основной)`. Монитор сразу после сна отвечает по DDC/CI не всегда.
    fn unreadable(&mut self) -> Vec<(String, bool)> {
        Vec::new()
    }
}

/// Сколько раз пробовать команду DDC/CI: монитор, только что вернувшийся из сна, отвечает через раз.
pub const DDC_TRIES: u32 = 3;

/// Вызвать `f` до `tries` раз, между попытками — `pause`; первый успешный результат.
pub fn retry<T>(tries: u32, mut pause: impl FnMut(), mut f: impl FnMut() -> Option<T>) -> Option<T> {
    for i in 0..tries {
        if i > 0 {
            pause();
        }
        if let Some(v) = f() {
            return Some(v);
        }
    }
    None
}

/// Прежняя яркость затемнённых мониторов: сервис хранит её в `meta.dim_restore`.
#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(default)]
pub struct DimRestore {
    pub monitors: BTreeMap<String, u32>,
    /// Сколько попыток подряд сохранённый монитор не нашёлся.
    pub misses: BTreeMap<String, u32>,
    /// Сколько попыток подряд монитор, который нужно затемнить, был на месте, но не отдал яркость.
    pub unanswered: BTreeMap<String, u32>,
}

/// Постоянная личность монитора из имени его интерфейса (`\\?\DISPLAY#AUS258A#5&…&UID33029#{guid}` →
/// `AUS258A#5&…&UID33029`); `None` — имя не того вида.
pub fn stable_id(interface: &str) -> Option<String> {
    let mut parts = interface.strip_prefix(r"\\?\")?.split('#');
    let (_, model, instance) = (parts.next()?, parts.next()?, parts.next()?);
    (!model.is_empty() && !instance.is_empty()).then(|| format!("{model}#{instance}"))
}

/// `id` и `label` физического монитора `index` дисплея `display`; `interface` — имя его интерфейса, если Windows его
/// отдала. Без него — прежний `\\.\DISPLAYn#i`.
pub fn identify(display: &str, index: usize, interface: Option<&str>) -> (String, String) {
    match interface.and_then(stable_id) {
        Some(id) => {
            let model = id.split('#').next().unwrap_or_default();
            let label = format!("{model} ({display})");
            (id, label)
        }
        None => {
            let id = format!("{display}#{index}");
            (id.clone(), id)
        }
    }
}

/// Яркость затемнения: `min + (max − min) × pct / 100`.
pub fn dim_value(m: &Monitor, pct: u8) -> u32 {
    m.min + (u64::from(m.max.saturating_sub(m.min)) * u64::from(pct.min(100)) / 100) as u32
}

fn should_dim(target: Dim, primary: bool) -> bool {
    match target {
        Dim::All => true,
        Dim::Secondary => !primary,
        Dim::None => false,
    }
}

/// Привести мониторы к `target` одной функцией на все переходы. `saved` — что сохранено раньше; `persist` вызывается
/// с новым списком до первой записи (FR-008: прежнее значение сохраняется до изменения). Возвращает остаток для
/// сохранения (`None` — всё возвращено) и итоги частей; отказ одного монитора не мешает другим, остаток повторяется по
/// бэкоффу. Монитор, который есть, но не отдал яркость, — отказ до `MAX_MISSES` попыток подряд, дальше его считают
/// монитором без поддержки яркости (телевизор, выключен DDC/CI) и не трогают. `dry` — только описать, ничего не писать и не сохранять.
pub fn apply(
    b: &mut dyn Brightness,
    target: Dim,
    saved: Option<DimRestore>,
    pct: u8,
    dry: bool,
    persist: &mut dyn FnMut(&DimRestore),
) -> (Option<DimRestore>, Vec<PartResult>) {
    let original = saved.clone().filter(|r| !r.monitors.is_empty() || !r.unanswered.is_empty());
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
    let unreadable = b.unreadable();
    let tag = |s: String| if dry { format!("dry run: {s}") } else { s };
    let mut parts = Vec::new();

    // 1. Запомнить прежнюю яркость тех, кого будем гасить (имеющееся значение не перезаписывается).
    let mut fresh = BTreeSet::new();
    for m in &monitors {
        restore.misses.remove(&m.id);
        restore.unanswered.remove(&m.id);
        if should_dim(target, m.primary) && !restore.monitors.contains_key(&m.id) {
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
        if should_dim(target, m.primary) {
            let value = dim_value(m, pct);
            let before = restore.monitors[&m.id];
            // Уже затемнённый (сохранённый и сдвинутый) повторно не пишем; сохранённый, но не сдвинутый — отказ
            // или сбой в прошлый раз, пишем снова.
            if m.cur != value && (fresh.contains(&m.id) || m.cur == before) {
                parts.push(match write(b, value) {
                    Ok(()) => PartResult::ok(
                        "dim",
                        tag(format!("монитор {}: яркость {} → {value}", m.label, m.cur)),
                    ),
                    Err(e) => {
                        PartResult::fail("dim", format!("монитор {}: яркость не записана: {e}", m.label))
                    }
                });
            }
        } else if let Some(&prev) = restore.monitors.get(&m.id) {
            let value = if m.max >= m.min { prev.clamp(m.min, m.max) } else { prev };
            let res = if m.cur == value { Ok(()) } else { write(b, value) };
            parts.push(match res {
                Ok(()) => {
                    restore.monitors.remove(&m.id);
                    PartResult::ok("dim", tag(format!("монитор {}: яркость возвращена ({value})", m.label)))
                }
                Err(e) => PartResult::fail(
                    "dim",
                    format!("монитор {}: яркость {value} не возвращена: {e}", m.label),
                ),
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

    // 4. Мониторы на месте, но без ответа по DDC/CI: отказ, чтобы сервис повторил по бэкоффу; после `MAX_MISSES`
    // попыток подряд — монитор без поддержки яркости. Сохранённых среди них уже учёл шаг 3.
    let silent: BTreeSet<&str> = unreadable.iter().map(|(id, _)| id.as_str()).collect();
    restore.unanswered.retain(|id, _| target != Dim::None && silent.contains(id.as_str()));
    for (id, primary) in &unreadable {
        if !should_dim(target, *primary) || restore.monitors.contains_key(id) {
            continue;
        }
        let n = restore.unanswered.entry(id.clone()).or_insert(0);
        if *n >= MAX_MISSES {
            continue;
        }
        *n += 1;
        parts.push(if *n < MAX_MISSES {
            PartResult::fail(
                "dim",
                format!(
                    "монитор {id} не ответил по DDC/CI ({n} из {MAX_MISSES}): затемню при следующей попытке"
                ),
            )
        } else {
            PartResult::ok("dim", format!("монитор {id} не отвечает по DDC/CI: его яркость не меняется"))
        });
    }

    if dry {
        return (original, parts);
    }
    let keep = !restore.monitors.is_empty() || !restore.unanswered.is_empty();
    (keep.then_some(restore), parts)
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
        DISPLAY_DEVICE_ACTIVE, DISPLAY_DEVICEW, EnumDisplayDevicesW, EnumDisplayMonitors, GetMonitorInfoW,
        HDC, HMONITOR, MONITORINFO, MONITORINFOEXW,
    };
    use windows::core::{BOOL, PCWSTR};

    use super::{Brightness, DDC_TRIES, Monitor, identify, retry};

    /// `EDD_GET_DEVICE_INTERFACE_NAME`: `DeviceID` — имя интерфейса монитора, а не его PnP-код.
    const EDD_GET_DEVICE_INTERFACE_NAME: u32 = 1;

    /// Имя интерфейса `index`-го активного монитора дисплея `display` (`\\?\DISPLAY#AUS258A#…#{guid}`).
    fn interface(display: &str, index: usize) -> Option<String> {
        let wide: Vec<u16> = display.encode_utf16().chain(std::iter::once(0)).collect();
        let mut active = 0usize;
        for n in 0..16u32 {
            let mut dd =
                DISPLAY_DEVICEW { cb: std::mem::size_of::<DISPLAY_DEVICEW>() as u32, ..Default::default() };
            // SAFETY: `wide` оканчивается нулём и живёт до конца вызова; `cb` указывает размер структуры.
            let ok = unsafe {
                EnumDisplayDevicesW(PCWSTR(wide.as_ptr()), n, &mut dd, EDD_GET_DEVICE_INTERFACE_NAME)
            };
            if !ok.as_bool() {
                return None;
            }
            if dd.StateFlags & DISPLAY_DEVICE_ACTIVE == DISPLAY_DEVICE_ACTIVE {
                if active == index {
                    let len = dd.DeviceID.iter().position(|&c| c == 0).unwrap_or(dd.DeviceID.len());
                    return Some(String::from_utf16_lossy(&dd.DeviceID[..len]));
                }
                active += 1;
            }
        }
        None
    }

    /// `id` и `label` физического монитора.
    fn ident(display: &str, index: usize) -> (String, String) {
        identify(display, index, interface(display, index).as_deref())
    }

    /// Пауза между повторами команды DDC/CI.
    const DDC_PAUSE: std::time::Duration = std::time::Duration::from_millis(100);

    fn pause() {
        std::thread::sleep(DDC_PAUSE);
    }

    /// `MONITORINFOF_PRIMARY` (в крейте `windows` константы нет).
    const MONITORINFOF_PRIMARY: u32 = 1;

    #[derive(Default)]
    pub struct WindowsBrightness {
        /// Не отдавшие яркость в последнем `monitors()`.
        unreadable: Vec<(String, bool)>,
    }

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
            let mut silent = Vec::new();
            walk(|name, primary, i, handle| {
                let read = retry(DDC_TRIES, pause, || {
                    let (mut min, mut cur, mut max) = (0u32, 0u32, 0u32);
                    // SAFETY: дескриптор действителен внутри `walk`.
                    let ok = unsafe { GetMonitorBrightness(handle, &mut min, &mut cur, &mut max) };
                    (ok != 0 && max >= min).then_some((min, cur, max))
                });
                let (id, label) = ident(name, i);
                match read {
                    Some((min, cur, max)) => out.push(Monitor { id, label, primary, min, cur, max }),
                    None => silent.push((id, primary)),
                }
                false
            });
            self.unreadable = silent;
            Ok(out)
        }

        fn set(&mut self, id: &str, value: u32) -> Result<(), String> {
            let mut result = Err(format!("монитор {id} не найден"));
            walk(|name, _, i, handle| {
                if ident(name, i).0 != id {
                    return false;
                }
                // SAFETY: дескриптор действителен внутри `walk`.
                let ok = retry(DDC_TRIES, pause, || {
                    (unsafe { SetMonitorBrightness(handle, value) } != 0).then_some(())
                });
                result = if ok.is_some() {
                    Ok(())
                } else {
                    Err(format!("монитор {id} не принял яркость {value}"))
                };
                true
            });
            result
        }

        fn unreadable(&mut self) -> Vec<(String, bool)> {
            self.unreadable.clone()
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
        /// Мониторы на месте, но без ответа по DDC/CI: `(id, основной)`.
        silent: Vec<(String, bool)>,
    }

    fn mon(id: &str, primary: bool, cur: u32) -> Monitor {
        Monitor { id: id.into(), label: id.into(), primary, min: 0, cur, max: 100 }
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
        fn unreadable(&mut self) -> Vec<(String, bool)> {
            self.silent.clone()
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
        let m = Monitor { id: "A".into(), label: "A".into(), primary: true, min: 10, cur: 80, max: 90 };
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
            unanswered: [("\\\\.\\DISPLAY2#0".to_string(), 1)].into(),
        };
        assert_eq!(serde_json::from_str::<DimRestore>(&serde_json::to_string(&r).unwrap()).unwrap(), r);
        let old: DimRestore = serde_json::from_str(r#"{"monitors":{"X#0":5}}"#).unwrap();
        assert_eq!(old, restore(&[("X#0", 5)]));
    }

    /// Сценарий бага: основной монитор после сна не ответил по DDC/CI — это отказ (повтор по бэкоффу), и следующая
    /// попытка, когда он ответил, его затемняет.
    #[test]
    fn silent_monitor_fails_and_is_dimmed_on_the_next_attempt() {
        let mut f = Fake { monitors: vec![mon("B", false, 70)], ..Default::default() };
        f.silent = vec![("A".into(), true)];
        let (saved, parts) = run(&mut f, Dim::All, None, 0);
        assert_eq!(failed(&parts), 1, "{parts:?}");
        assert!(
            parts.iter().any(|p| !p.ok && p.detail.contains("A") && p.detail.contains("1 из")),
            "{parts:?}"
        );
        assert_eq!(f.cur("B"), 0, "ответивший затемнён сразу");
        let saved = saved.expect("счётчик сохранён");
        assert_eq!(saved.unanswered.get("A"), Some(&1));
        // Монитор ответил.
        f.silent.clear();
        f.monitors.insert(0, mon("A", true, 100));
        let (saved, parts) = run(&mut f, Dim::All, Some(saved), 0);
        assert_eq!(failed(&parts), 0, "{parts:?}");
        assert_eq!((f.cur("A"), f.cur("B")), (0, 0));
        let saved = saved.unwrap();
        assert!(saved.unanswered.is_empty(), "счётчик сброшен");
        assert_eq!(saved.monitors, restore(&[("A", 100), ("B", 70)]).monitors);
        let (left, _) = run(&mut f, Dim::None, Some(saved), 0);
        assert_eq!((f.cur("A"), f.cur("B"), left), (100, 70, None));
    }

    #[test]
    fn silent_monitor_stops_failing_after_max_misses() {
        let mut f = Fake { silent: vec![("TV".into(), false)], ..Default::default() };
        let mut saved = None;
        for attempt in 1..MAX_MISSES {
            let (left, parts) = run(&mut f, Dim::All, saved, 0);
            assert_eq!(failed(&parts), 1, "попытка {attempt}");
            saved = left;
        }
        let (left, parts) = run(&mut f, Dim::All, saved, 0);
        assert!(parts.len() == 1 && parts[0].ok && parts[0].detail.contains("не отвечает"), "{parts:?}");
        let (again, parts) = run(&mut f, Dim::All, left.clone(), 0);
        assert!(parts.is_empty(), "дальше молчим: {parts:?}");
        assert_eq!(again, left, "счётчик не растёт");
        assert_eq!(again.unwrap().unanswered.get("TV"), Some(&MAX_MISSES));
        assert!(f.log.is_empty(), "в молчащий монитор ничего не пишется");
    }

    #[test]
    fn silent_primary_during_a_game_is_not_a_failure() {
        let mut f = Fake { monitors: vec![mon("B", false, 70)], ..Default::default() };
        f.silent = vec![("A".into(), true)];
        let (saved, parts) = run(&mut f, Dim::Secondary, None, 0);
        assert_eq!(failed(&parts), 0, "{parts:?}");
        assert_eq!(saved, Some(restore(&[("B", 70)])), "основной не считается");
    }

    #[test]
    fn lifting_the_dim_drops_the_silent_counter() {
        let mut f = Fake { monitors: vec![mon("B", false, 70)], ..Default::default() };
        f.silent = vec![("A".into(), true)];
        let (saved, _) = run(&mut f, Dim::All, None, 0);
        let (left, parts) = run(&mut f, Dim::None, saved, 0);
        assert_eq!((left, failed(&parts), f.cur("B")), (None, 0, 70));
        // Только счётчик, без сохранённой яркости: снятие ничего не опрашивает и всё стирает.
        let only = DimRestore { unanswered: [("A".to_string(), 2)].into(), ..Default::default() };
        let (left, parts) = run(&mut f, Dim::None, Some(only), 0);
        assert_eq!((left, parts.len()), (None, 0));
    }

    #[test]
    fn silent_counter_survives_an_unreadable_list() {
        let mut f = Fake { fail_list: true, ..Default::default() };
        let only = Some(DimRestore { unanswered: [("A".to_string(), 2)].into(), ..Default::default() });
        let (left, parts) = run(&mut f, Dim::All, only.clone(), 0);
        assert_eq!((left, failed(&parts)), (only, 1));
    }

    #[test]
    fn silent_monitor_that_disappears_loses_its_counter() {
        let mut f = Fake { silent: vec![("A".into(), true)], ..Default::default() };
        let (saved, _) = run(&mut f, Dim::All, None, 0);
        f.silent.clear();
        let (left, parts) = run(&mut f, Dim::All, saved, 0);
        assert_eq!((left, parts.len()), (None, 0));
    }

    #[test]
    fn dry_run_reports_a_silent_monitor_but_saves_nothing() {
        let mut f = Fake { silent: vec![("A".into(), true)], ..Default::default() };
        let (saved, parts) = apply(&mut f, Dim::All, None, 0, true, &mut |_| panic!("persist в dry run"));
        assert_eq!((saved, failed(&parts)), (None, 1));
    }

    #[test]
    fn old_restore_json_reads_without_the_silent_counter() {
        let old: DimRestore = serde_json::from_str(r#"{"monitors":{"X#0":5},"misses":{"X#0":1}}"#).unwrap();
        assert!(old.unanswered.is_empty());
        assert_eq!(old.monitors.get("X#0"), Some(&5));
    }

    #[test]
    fn retry_stops_at_first_success_and_pauses_between_tries() {
        let (mut calls, mut pauses) = (0, 0);
        let got = retry(
            3,
            || pauses += 1,
            || {
                calls += 1;
                (calls == 2).then_some(calls)
            },
        );
        assert_eq!((got, calls, pauses), (Some(2), 2, 1));
        let (mut calls, mut pauses) = (0, 0);
        let got: Option<()> = retry(
            3,
            || pauses += 1,
            || {
                calls += 1;
                None
            },
        );
        assert_eq!((got, calls, pauses), (None, 3, 2), "без паузы после последней попытки");
        let got = retry(0, || panic!("пауза"), || Some(1));
        assert_eq!(got, None, "ноль попыток — ни одного вызова");
    }

    #[test]
    fn stable_id_takes_model_and_instance() {
        let iface = r"\\?\DISPLAY#AUS258A#5&1ba1d4b0&0&UID33029#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
        assert_eq!(stable_id(iface).as_deref(), Some("AUS258A#5&1ba1d4b0&0&UID33029"));
        assert_eq!(
            stable_id(r"\\?\DISPLAY#AUS258A#UID1").as_deref(),
            Some("AUS258A#UID1"),
            "без хвоста GUID"
        );
        assert_eq!(stable_id(""), None);
        assert_eq!(stable_id(r"\\.\DISPLAY1"), None, "имя дисплея, а не интерфейса");
        assert_eq!(stable_id(r"\\?\DISPLAY"), None, "без модели");
        assert_eq!(stable_id(r"\\?\DISPLAY#AUS258A"), None, "без экземпляра");
        assert_eq!(stable_id(r"\\?\DISPLAY##UID1#x"), None, "пустая модель");
        assert_eq!(stable_id(r"\\?\DISPLAY#AUS258A##x"), None, "пустой экземпляр");
    }

    #[test]
    fn identify_prefers_the_interface_and_falls_back_to_the_display_name() {
        let iface = r"\\?\DISPLAY#AUS258A#5&1&UID33029#{guid}";
        assert_eq!(
            identify(r"\\.\DISPLAY1", 0, Some(iface)),
            ("AUS258A#5&1&UID33029".to_string(), r"AUS258A (\\.\DISPLAY1)".to_string())
        );
        // Тот же монитор под другим именем дисплея — тот же id.
        assert_eq!(identify(r"\\.\DISPLAY2", 0, Some(iface)).0, "AUS258A#5&1&UID33029");
        let old = r"\\.\DISPLAY2#1".to_string();
        assert_eq!(identify(r"\\.\DISPLAY2", 1, None), (old.clone(), old.clone()));
        assert_eq!(identify(r"\\.\DISPLAY2", 1, Some("мусор")), (old.clone(), old));
    }

    #[test]
    fn restore_follows_the_monitor_when_windows_renumbers_displays() {
        // Случай 2026-11-17: ASUS был DISPLAY2, вернулся со связи как DISPLAY1, Acer сдвинулся на DISPLAY2.
        let mut asus = mon("AUS", true, 100);
        asus.label = r"AUS (\\.\DISPLAY2)".into();
        let mut acer = mon("ACR", false, 80);
        acer.label = r"ACR (\\.\DISPLAY1)".into();
        let mut f = Fake { monitors: vec![asus, acer], ..Default::default() };
        let (saved, _) = run(&mut f, Dim::All, None, 0);
        assert_eq!((f.cur("AUS"), f.cur("ACR")), (0, 0));

        // ASUS пропал со связи: Acer возвращён, ASUS ждёт.
        let asus = f.monitors.remove(0);
        let (saved, parts) = run(&mut f, Dim::None, saved, 0);
        assert_eq!(f.cur("ACR"), 80);
        assert_eq!(failed(&parts), 1, "ASUS не найден — повтор");
        let saved = saved.expect("яркость ASUS ещё сохранена");
        assert_eq!(saved.monitors.get("AUS"), Some(&100));

        // ASUS вернулся под другим именем дисплея: id тот же, яркость возвращена ему, Acer не тронут.
        let mut asus = asus;
        asus.label = r"AUS (\\.\DISPLAY1)".into();
        f.monitors.push(asus);
        f.log.clear();
        let (saved, parts) = run(&mut f, Dim::None, Some(saved), 0);
        assert_eq!((f.cur("AUS"), f.cur("ACR")), (100, 80));
        assert_eq!(f.writes(), vec!["set AUS 100".to_string()]);
        assert_eq!(failed(&parts), 0);
        assert!(saved.is_none());
        assert!(
            parts[0].detail.contains(r"AUS (\\.\DISPLAY1)"),
            "в истории текущее имя: {}",
            parts[0].detail
        );
    }
}
