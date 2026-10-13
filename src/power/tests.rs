use std::cell::RefCell;

use super::*;
use crate::config::Config;

/// Фейковый бэкенд: состояние в памяти, журнал вызовов, отказы по имени операции.
#[derive(Default)]
struct Fake {
    active: String,
    schemes: Vec<(String, String)>,
    overlay: String,
    values: Vec<(String, String, u32)>,
    gpu_installed: bool,
    gpu: Vec<EcoLevel>,
    fail: Vec<&'static str>,
    log: RefCell<Vec<String>>,
    next: u32,
}

impl Fake {
    fn new() -> Self {
        Self {
            active: HIGH_PERFORMANCE.into(),
            schemes: [
                (BALANCED, "Сбалансированная"),
                (POWER_SAVER, "Экономия энергии"),
                (HIGH_PERFORMANCE, "Высокая"),
                (OWNER_SCHEME, "Экономия (нет света)"),
            ]
            .iter()
            .map(|(g, n)| (g.to_string(), n.to_string()))
            .collect(),
            overlay: OVERLAY_NONE.into(),
            gpu_installed: true,
            ..Default::default()
        }
    }
    fn op(&self, name: &'static str) -> Result<(), String> {
        self.log.borrow_mut().push(name.into());
        if self.fail.contains(&name) { Err(format!("{name} сломан")) } else { Ok(()) }
    }
    fn writes(&self) -> Vec<String> {
        self.log.borrow().iter().filter(|s| !s.starts_with("get_")).cloned().collect()
    }
}

impl PowerBackend for Fake {
    fn active_scheme(&self) -> Result<String, String> {
        self.op("get_active").map(|()| self.active.clone())
    }
    fn set_active_scheme(&mut self, guid: &str) -> Result<(), String> {
        self.op("set_active")?;
        self.active = guid.into();
        Ok(())
    }
    fn schemes(&self) -> Result<Vec<(String, String)>, String> {
        self.op("get_schemes").map(|()| self.schemes.clone())
    }
    fn duplicate_scheme(&mut self, source: &str) -> Result<String, String> {
        self.op("duplicate")?;
        self.next += 1;
        let g = format!("00000000-0000-0000-0000-{:012}", self.next);
        let name = self.schemes.iter().find(|(s, _)| s == source).unwrap().1.clone();
        self.schemes.push((g.clone(), name));
        Ok(g)
    }
    fn set_friendly_name(&mut self, guid: &str, name: &str) -> Result<(), String> {
        self.op("set_name")?;
        self.schemes.iter_mut().find(|(g, _)| g == guid).unwrap().1 = name.into();
        Ok(())
    }
    fn write_ac_value(&mut self, scheme: &str, _sub: &str, setting: &str, value: u32) -> Result<(), String> {
        self.op(if setting == PROCTHROTTLEMAX { "write_cpu" } else { "write_monitor" })?;
        assert!(!is_protected(scheme), "запись в чужую схему {scheme}");
        self.values.push((scheme.into(), setting.into(), value));
        Ok(())
    }
    fn delete_scheme(&mut self, guid: &str) -> Result<(), String> {
        self.op("delete")?;
        self.schemes.retain(|(g, _)| g != guid);
        Ok(())
    }
    fn overlay(&self) -> Result<String, String> {
        self.op("get_overlay").map(|()| self.overlay.clone())
    }
    fn set_overlay(&mut self, guid: &str) -> Result<(), String> {
        self.op("set_overlay")?;
        self.overlay = guid.into();
        Ok(())
    }
    fn gpu_task_installed(&self) -> bool {
        self.log.borrow_mut().push("get_gpu_task".into());
        self.gpu_installed
    }
    fn gpu_request(&mut self, level: EcoLevel) -> Result<(), String> {
        self.op("gpu")?;
        self.gpu.push(level);
        Ok(())
    }
}

fn control(fake: Fake) -> PowerControl<Fake> {
    PowerControl::new(fake, Config::default().eco, None, false)
}

fn failed(parts: &[PartResult]) -> Vec<&'static str> {
    parts.iter().filter(|p| !p.ok).map(|p| p.part).collect()
}

#[test]
fn eco_captures_previous_state_before_changing_it() {
    let mut pc = control(Fake::new());
    let (restore, parts) = pc.apply(EcoLevel::Eco, None);
    let r = restore.unwrap();
    assert_eq!(r.prev_scheme.as_deref(), Some(HIGH_PERFORMANCE));
    assert_eq!(r.prev_overlay.as_deref(), Some(OVERLAY_NONE));
    assert_eq!(r.gpu_was.as_deref(), Some("normal"));
    assert!(failed(&parts).is_empty(), "{parts:?}");

    // Чтения прежнего состояния идут раньше первого изменения.
    let log = pc.backend().log.borrow().clone();
    let first_write = log.iter().position(|s| !s.starts_with("get_")).unwrap();
    assert!(log[..first_write].contains(&"get_active".to_string()));
    assert!(log[..first_write].contains(&"get_overlay".to_string()));

    let b = pc.backend();
    let eco = pc.eco_scheme().unwrap();
    assert_eq!(b.active, eco);
    assert_eq!(b.overlay, OVERLAY_BEST_EFFICIENCY);
    assert_eq!(b.gpu, vec![EcoLevel::Eco]);
    assert!(b.values.contains(&(eco.to_string(), PROCTHROTTLEMAX.to_string(), 80)));
    assert!(b.values.contains(&(eco.to_string(), VIDEOIDLE.to_string(), 180)));
    assert!(b.schemes.iter().any(|(g, n)| g == eco && n == ECO_SCHEME_NAME));
}

#[test]
fn eco_to_deep_keeps_restore_and_lowers_cpu() {
    let mut pc = control(Fake::new());
    let (r1, _) = pc.apply(EcoLevel::Eco, None);
    let (r2, parts) = pc.apply(EcoLevel::Deep, r1.clone());
    assert_eq!(r1, r2);
    assert!(failed(&parts).is_empty());
    let b = pc.backend();
    assert_eq!(b.values.iter().rfind(|(_, s, _)| s == PROCTHROTTLEMAX).unwrap().2, 50);
    assert_eq!(b.gpu, vec![EcoLevel::Eco, EcoLevel::Deep]);
    // Схема создана один раз.
    assert_eq!(b.schemes.iter().filter(|(_, n)| n == ECO_SCHEME_NAME).count(), 1);
}

#[test]
fn restore_returns_everything_and_clears_state() {
    let mut pc = control(Fake::new());
    let (r, _) = pc.apply(EcoLevel::Eco, None);
    let (after, parts) = pc.apply(EcoLevel::None, r);
    assert_eq!(after, None);
    assert!(failed(&parts).is_empty());
    let b = pc.backend();
    assert_eq!(b.active, HIGH_PERFORMANCE);
    assert_eq!(b.overlay, OVERLAY_NONE);
    assert_eq!(b.gpu.last(), Some(&EcoLevel::None));
}

#[test]
fn restore_after_restart_uses_saved_state() {
    let saved = PowerRestore {
        prev_scheme: Some(HIGH_PERFORMANCE.into()),
        prev_overlay: Some(OVERLAY_NONE.into()),
        gpu_was: Some("normal".into()),
    };
    let mut fake = Fake::new();
    fake.active = "00000000-0000-0000-0000-000000000077".into();
    fake.schemes.push((fake.active.clone(), ECO_SCHEME_NAME.into()));
    fake.overlay = OVERLAY_BEST_EFFICIENCY.into();
    // Новый процесс: тот же GUID схемы из meta.
    let mut pc = PowerControl::new(
        fake,
        Config::default().eco,
        Some(String::from("00000000-0000-0000-0000-000000000077")),
        false,
    );
    let (after, parts) = pc.apply(EcoLevel::None, Some(saved));
    assert_eq!(after, None);
    assert!(failed(&parts).is_empty());
    assert_eq!(pc.backend().active, HIGH_PERFORMANCE);
    assert_eq!(pc.backend().overlay, OVERLAY_NONE);
}

#[test]
fn missing_previous_scheme_falls_back_to_balanced() {
    let saved = PowerRestore {
        prev_scheme: Some("11111111-1111-1111-1111-111111111111".into()),
        ..Default::default()
    };
    let mut pc = control(Fake::new());
    let (after, _) = pc.apply(EcoLevel::None, Some(saved));
    assert_eq!(after, None);
    assert_eq!(pc.backend().active, BALANCED);
}

#[test]
fn lost_state_with_eco_already_active_restores_balanced() {
    let mut pc = control(Fake::new());
    let (r, _) = pc.apply(EcoLevel::Eco, None);
    let eco = pc.eco_scheme().unwrap().to_string();
    assert_eq!(pc.backend().active, eco);
    drop(r);
    // Состояние потеряно: захват не должен запомнить саму «Bluetti Eco» и наш оверлей.
    let (r, _) = pc.apply(EcoLevel::Eco, None);
    let r = r.unwrap();
    assert_eq!(r.prev_scheme.as_deref(), Some(BALANCED));
    assert_eq!(r.prev_overlay.as_deref(), Some(OVERLAY_NONE));
}

#[test]
fn failing_part_does_not_block_others() {
    let mut fake = Fake::new();
    fake.fail = vec!["set_overlay", "write_cpu"];
    fake.gpu_installed = false;
    let mut pc = control(fake);
    let (r, parts) = pc.apply(EcoLevel::Eco, None);
    let mut f = failed(&parts);
    f.sort();
    assert_eq!(f, vec!["cpu", "gpu", "overlay"]);
    for p in parts.iter().filter(|p| !p.ok) {
        assert!(!p.detail.is_empty());
    }
    let b = pc.backend();
    assert_eq!(b.active, pc.eco_scheme().unwrap(), "схема включена несмотря на отказы");
    assert!(b.values.iter().any(|(_, s, _)| s == VIDEOIDLE));
    // Без задачи GPU возвращать нечего.
    assert_eq!(r.unwrap().gpu_was, None);
}

#[test]
fn failed_restore_part_stays_for_retry() {
    let mut pc = control(Fake::new());
    let (r, _) = pc.apply(EcoLevel::Eco, None);
    pc.backend.fail = vec!["set_overlay"];
    let (left, parts) = pc.apply(EcoLevel::None, r);
    assert_eq!(failed(&parts), vec!["overlay"]);
    let left = left.unwrap();
    assert_eq!(left, PowerRestore { prev_overlay: Some(OVERLAY_NONE.into()), ..Default::default() });
    assert_eq!(pc.backend().active, HIGH_PERFORMANCE);
    pc.backend.fail.clear();
    assert_eq!(pc.apply(EcoLevel::None, Some(left)).0, None);
}

#[test]
fn unreadable_active_scheme_skips_scheme_but_not_overlay() {
    let mut fake = Fake::new();
    fake.fail = vec!["get_active"];
    let mut pc = control(fake);
    let (r, parts) = pc.apply(EcoLevel::Eco, None);
    assert_eq!(failed(&parts), vec!["scheme"]);
    assert_eq!(pc.backend().active, HIGH_PERFORMANCE);
    assert_eq!(pc.backend().overlay, OVERLAY_BEST_EFFICIENCY);
    assert_eq!(r.unwrap().prev_scheme, None);
}

#[test]
fn disabled_parts_are_not_touched() {
    let cfg = Eco { power_scheme: false, efficiency_overlay: false, gpu: false, ..Config::default().eco };
    let mut pc = PowerControl::new(Fake::new(), cfg, None, false);
    let (r, parts) = pc.apply(EcoLevel::Deep, None);
    assert_eq!(r, None);
    assert!(parts.is_empty());
    assert!(pc.backend().writes().is_empty());
}

#[test]
fn monitor_zero_is_not_written() {
    let cfg = Eco { monitor_off_sec: 0, ..Config::default().eco };
    let mut pc = PowerControl::new(Fake::new(), cfg, None, false);
    let (_, parts) = pc.apply(EcoLevel::Eco, None);
    assert!(parts.iter().all(|p| p.part != "monitor"));
    assert!(pc.backend().values.iter().all(|(_, s, _)| s != VIDEOIDLE));
}

#[test]
fn dry_run_changes_nothing() {
    let mut pc = PowerControl::new(Fake::new(), Config::default().eco, None, true);
    let (r, parts) = pc.apply(EcoLevel::Eco, None);
    assert!(r.is_some());
    assert!(failed(&parts).is_empty(), "{parts:?}");
    assert!(parts.iter().all(|p| p.detail.contains("dry run")), "{parts:?}");
    let (r, parts) = pc.apply(EcoLevel::None, r);
    assert_eq!(r, None);
    assert!(parts.iter().all(|p| p.detail.contains("dry run")));
    assert!(pc.ensure_scheme().is_err());
    pc.remove_scheme().unwrap();
    assert!(pc.backend().writes().is_empty(), "{:?}", pc.backend().writes());
}

#[test]
fn scheme_base_follows_overlay_setting() {
    let mut pc = control(Fake::new());
    let g = pc.ensure_scheme().unwrap();
    assert_eq!(pc.ensure_scheme().unwrap(), g, "повторно не создаётся");
    assert_eq!(pc.backend().log.borrow().iter().filter(|s| *s == "duplicate").count(), 1);

    let cfg = Eco { efficiency_overlay: false, ..Config::default().eco };
    let mut fake = Fake::new();
    fake.fail = vec!["set_name"];
    let mut pc = PowerControl::new(fake, cfg.clone(), None, false);
    assert!(pc.ensure_scheme().is_err());
    assert_eq!(pc.backend().schemes.len(), 4, "безымянная копия удалена");

    let mut pc = PowerControl::new(Fake::new(), cfg, None, false);
    pc.ensure_scheme().unwrap();
    assert!(pc.backend().log.borrow().contains(&"duplicate".to_string()));
    // Копия «Экономии энергии» — до переименования у неё было её имя (см. Fake::duplicate_scheme).
}

#[test]
fn scheme_found_by_name_when_meta_is_lost() {
    let mut fake = Fake::new();
    fake.schemes.push(("00000000-0000-0000-0000-000000000042".into(), ECO_SCHEME_NAME.into()));
    let mut pc = control(fake);
    assert_eq!(pc.ensure_scheme().unwrap(), "00000000-0000-0000-0000-000000000042");
    assert!(!pc.backend().log.borrow().contains(&"duplicate".to_string()));
}

#[test]
fn remove_scheme_refuses_active_and_protected() {
    let mut pc = control(Fake::new());
    pc.apply(EcoLevel::Eco, None);
    assert!(pc.remove_scheme().is_err());
    pc.backend.active = HIGH_PERFORMANCE.into();
    pc.remove_scheme().unwrap();
    assert!(pc.backend().schemes.iter().all(|(_, n)| n != ECO_SCHEME_NAME));
    assert_eq!(pc.eco_scheme(), None);

    // GUID из meta указывает на схему владельца — её не трогаем.
    let mut pc = PowerControl::new(Fake::new(), Config::default().eco, Some(OWNER_SCHEME.into()), false);
    pc.remove_scheme().unwrap();
    assert!(pc.backend().schemes.iter().any(|(g, _)| g == OWNER_SCHEME));
}

#[test]
fn guid_roundtrip() {
    for s in [BALANCED, OWNER_SCHEME, OVERLAY_NONE, "{8C5E7FDA-E8BF-4A96-9A85-A6E23A8C635C}"] {
        let g = parse_guid(s).unwrap();
        let back = guid_to_string(&g);
        assert_eq!(back, s.trim_matches(['{', '}']).to_ascii_lowercase());
    }
    assert!(parse_guid("8c5e7fda").is_err());
}

/// Спайк T045 на реальной системе: `cargo test -- --ignored overlay_spike`.
/// Копия «Сбалансированной» → активна → оверлей → проверка → всё вернуть и удалить копию.
#[cfg(windows)]
#[test]
#[ignore]
fn overlay_spike() {
    struct Guard {
        active: String,
        overlay: Option<String>,
        temp: Option<String>,
    }
    impl Drop for Guard {
        fn drop(&mut self) {
            let mut w = WindowsPower;
            println!("restore active {} → {:?}", self.active, w.set_active_scheme(&self.active));
            if let Some(o) = &self.overlay {
                println!("restore overlay {o} → {:?}", w.set_overlay(o));
            }
            if let Some(t) = &self.temp {
                println!("delete temp {t} → {:?}", w.delete_scheme(t));
            }
            println!("final: active {:?}, overlay {:?}", w.active_scheme(), w.overlay());
        }
    }

    let mut w = WindowsPower;
    let active = w.active_scheme().expect("active scheme");
    let overlay = w.overlay();
    println!("before: active {active}, overlay {overlay:?}");
    let mut guard = Guard { active, overlay: overlay.ok(), temp: None };

    let temp = w.duplicate_scheme(BALANCED).expect("duplicate");
    assert!(!is_protected(&temp));
    guard.temp = Some(temp.clone());
    w.set_friendly_name(&temp, "Bluetti Spike").expect("name");
    println!("temp scheme {temp}");
    w.set_active_scheme(&temp).expect("activate");
    let set = w.set_overlay(OVERLAY_BEST_EFFICIENCY);
    println!("set overlay → {set:?}");
    let now_active = w.active_scheme();
    let now_overlay = w.overlay();
    println!("after: active {now_active:?}, overlay {now_overlay:?}");
    assert_eq!(now_active.as_deref(), Ok(temp.as_str()));
    set.expect("set overlay");
    assert_eq!(now_overlay.as_deref(), Ok(OVERLAY_BEST_EFFICIENCY));
    drop(guard);
}

/// Только чтение системы: `cargo test -- --ignored windows_reads`.
#[cfg(windows)]
#[test]
#[ignore]
fn windows_reads() {
    use super::hibernate::{Hibernator, WindowsHibernator};
    let w = WindowsPower;
    let list = w.schemes().expect("schemes");
    println!("schemes: {list:?}");
    assert!(list.iter().any(|(g, _)| g == BALANCED));
    println!("gpu task installed: {}", gpu::task_installed());
    println!("hibernate available: {}", WindowsHibernator { dry_run: true }.available());
}
