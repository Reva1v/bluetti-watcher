//! Eco-режим ПК (research.md R3): схема «Bluetti Eco», оверлей энергоэффективности, лимит GPU.
//! Логика — `PowerControl` поверх трейта `PowerBackend`; системные вызовы — `WindowsPower`.

pub mod gpu;
pub mod hibernate;
pub mod scheme;

use serde::{Deserialize, Serialize};

use crate::config::Eco;
use crate::model::EcoLevel;

pub use scheme::WindowsPower;

/// «Сбалансированная».
pub const BALANCED: &str = "381b4222-f694-41f0-9685-ff5bb260df2e";
/// «Экономия энергии».
pub const POWER_SAVER: &str = "a1841308-3541-4fab-bc81-f71556f20b4a";
/// «Высокая производительность».
pub const HIGH_PERFORMANCE: &str = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
/// Схема владельца «Экономия (нет света)» — не трогаем никогда.
pub const OWNER_SCHEME: &str = "b1e771e0-ec00-4c0d-9a11-0000b10e771e";
/// Оверлей «Наилучшая энергоэффективность».
pub const OVERLAY_BEST_EFFICIENCY: &str = "961cc777-2547-4f9d-8174-7d86181b8a7a";
/// Без оверлея (ползунок «Сбалансированный»).
pub const OVERLAY_NONE: &str = "00000000-0000-0000-0000-000000000000";

pub const SUB_PROCESSOR: &str = "54533251-82be-4824-96c1-47b60b740d00";
pub const PROCTHROTTLEMAX: &str = "bc5038f7-23e0-4960-96da-33abaf5935ec";
pub const SUB_VIDEO: &str = "7516b95f-f776-4464-8c53-06167f40cc99";
pub const VIDEOIDLE: &str = "3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e";

pub const ECO_SCHEME_NAME: &str = "Bluetti Eco";

/// Что вернуть при снятии eco; сервис сохраняет это в состоянии до применения уровня.
#[derive(Debug, Clone, Default, Serialize, Deserialize, PartialEq)]
pub struct PowerRestore {
    /// GUID схемы, активной до eco.
    pub prev_scheme: Option<String>,
    /// GUID оверлея до eco.
    pub prev_overlay: Option<String>,
    /// Уровень GPU до eco (`normal`); `Some` — GPU надо вернуть.
    pub gpu_was: Option<String>,
}

impl PowerRestore {
    fn is_empty(&self) -> bool {
        self.prev_scheme.is_none() && self.prev_overlay.is_none() && self.gpu_was.is_none()
    }
}

/// Итог одной части eco — для лога и событий (`eco-part-failed`).
#[derive(Debug, Clone, PartialEq)]
pub struct PartResult {
    /// `scheme` | `cpu` | `monitor` | `overlay` | `gpu`.
    pub part: &'static str,
    pub ok: bool,
    /// По-русски, для событий.
    pub detail: String,
}

impl PartResult {
    fn ok(part: &'static str, detail: impl Into<String>) -> Self {
        Self { part, ok: true, detail: detail.into() }
    }
    fn fail(part: &'static str, detail: impl Into<String>) -> Self {
        Self { part, ok: false, detail: detail.into() }
    }
}

/// Низкоуровневые операции питания. GUID — строки в нижнем регистре без скобок.
pub trait PowerBackend {
    fn active_scheme(&self) -> Result<String, String>;
    fn set_active_scheme(&mut self, guid: &str) -> Result<(), String>;
    /// Все схемы: (GUID, имя).
    fn schemes(&self) -> Result<Vec<(String, String)>, String>;
    /// Копия схемы; возвращает GUID новой.
    fn duplicate_scheme(&mut self, source: &str) -> Result<String, String>;
    fn set_friendly_name(&mut self, guid: &str, name: &str) -> Result<(), String>;
    fn write_ac_value(
        &mut self,
        scheme: &str,
        subgroup: &str,
        setting: &str,
        value: u32,
    ) -> Result<(), String>;
    fn delete_scheme(&mut self, guid: &str) -> Result<(), String>;
    fn overlay(&self) -> Result<String, String>;
    fn set_overlay(&mut self, guid: &str) -> Result<(), String>;
    /// Установлена ли элевированная задача GPU.
    fn gpu_task_installed(&self) -> bool;
    fn gpu_request(&mut self, level: EcoLevel) -> Result<(), String>;
}

/// Схемы, которые программа не меняет и не удаляет.
pub fn is_protected(guid: &str) -> bool {
    [BALANCED, POWER_SAVER, HIGH_PERFORMANCE, OWNER_SCHEME].iter().any(|p| p.eq_ignore_ascii_case(guid))
}

pub struct PowerControl<B: PowerBackend> {
    backend: B,
    cfg: Eco,
    eco_scheme: Option<String>,
    dry_run: bool,
}

impl<B: PowerBackend> PowerControl<B> {
    pub fn new(backend: B, cfg: Eco, eco_scheme: Option<String>, dry_run: bool) -> Self {
        Self { backend, cfg, eco_scheme: eco_scheme.map(|g| g.to_ascii_lowercase()), dry_run }
    }

    pub fn backend(&self) -> &B {
        &self.backend
    }

    pub fn eco_scheme(&self) -> Option<&str> {
        self.eco_scheme.as_deref()
    }

    /// Найти или создать «Bluetti Eco»; GUID — для `meta.eco_scheme`.
    pub fn ensure_scheme(&mut self) -> Result<String, String> {
        let base = if self.cfg.efficiency_overlay { BALANCED } else { POWER_SAVER };
        let guid = scheme::ensure(&mut self.backend, self.eco_scheme.as_deref(), base, self.dry_run)?;
        self.eco_scheme = Some(guid.clone());
        Ok(guid)
    }

    /// Удалить «Bluetti Eco» (uninstall); активную схему не удаляем.
    pub fn remove_scheme(&mut self) -> Result<(), String> {
        let removed = scheme::remove(&mut self.backend, self.eco_scheme.as_deref(), self.dry_run)?;
        if removed {
            self.eco_scheme = None;
        }
        Ok(())
    }

    /// Состояние для восстановления: `saved`, дополненное недостающими полями для включённых частей.
    /// Сервис может вызвать это и сохранить результат до `apply`; `apply` делает то же самое сам.
    pub fn capture(&self, saved: Option<PowerRestore>) -> PowerRestore {
        let mut r = saved.unwrap_or_default();
        if self.cfg.power_scheme && r.prev_scheme.is_none() {
            r.prev_scheme = self.backend.active_scheme().ok().map(|g| {
                // Если активна уже наша схема (потеряли состояние), вернуть некуда — берём «Сбалансированную».
                if self.eco_scheme.as_deref().is_some_and(|e| e.eq_ignore_ascii_case(&g)) {
                    BALANCED.to_string()
                } else {
                    g
                }
            });
        }
        if self.cfg.efficiency_overlay && r.prev_overlay.is_none() {
            // Уже стоит наш оверлей (потеряли состояние) — возвращать обычный.
            r.prev_overlay = self.backend.overlay().ok().map(|o| {
                if o.eq_ignore_ascii_case(OVERLAY_BEST_EFFICIENCY) { OVERLAY_NONE.to_string() } else { o }
            });
        }
        if self.cfg.gpu && r.gpu_was.is_none() && self.backend.gpu_task_installed() {
            r.gpu_was = Some("normal".into());
        }
        r
    }

    /// Применить уровень. Возвращает `PowerRestore` для сохранения (`None` — всё возвращено) и итоги частей.
    /// Отказ одной части не мешает остальным (FR-019b).
    pub fn apply(
        &mut self,
        level: EcoLevel,
        saved: Option<PowerRestore>,
    ) -> (Option<PowerRestore>, Vec<PartResult>) {
        match level {
            EcoLevel::None => self.restore(saved),
            EcoLevel::Eco | EcoLevel::Deep => self.enter(level, saved),
        }
    }

    fn enter(
        &mut self,
        level: EcoLevel,
        saved: Option<PowerRestore>,
    ) -> (Option<PowerRestore>, Vec<PartResult>) {
        let restore = self.capture(saved);
        let mut out = Vec::new();
        let dry = self.dry_run;
        let tag = |s: String| if dry { format!("dry run: {s}") } else { s };

        if self.cfg.power_scheme {
            if restore.prev_scheme.is_none() {
                out.push(PartResult::fail("scheme", "не удалось узнать активную схему — схема не меняется"));
            } else {
                self.enter_scheme(level, &mut out, dry);
            }
        }

        if self.cfg.efficiency_overlay {
            let r = if dry { Ok(()) } else { self.backend.set_overlay(OVERLAY_BEST_EFFICIENCY) };
            out.push(match r {
                Ok(()) => PartResult::ok("overlay", tag("оверлей «Наилучшая энергоэффективность»".into())),
                Err(e) => PartResult::fail("overlay", format!("оверлей не включён: {e}")),
            });
        }

        if self.cfg.gpu {
            if !self.backend.gpu_task_installed() {
                out.push(PartResult::fail(
                    "gpu",
                    "задача BluettiWatcherGpu не установлена — GPU не меняется",
                ));
            } else {
                let r = if dry { Ok(()) } else { self.backend.gpu_request(level) };
                out.push(match r {
                    Ok(()) => {
                        PartResult::ok("gpu", tag(format!("запрошен лимит GPU «{}»", gpu::level_word(level))))
                    }
                    Err(e) => PartResult::fail("gpu", e),
                });
            }
        }

        let restore = (!restore.is_empty()).then_some(restore);
        (restore, out)
    }

    fn enter_scheme(&mut self, level: EcoLevel, out: &mut Vec<PartResult>, dry: bool) {
        let guid = match self.ensure_scheme() {
            Ok(g) => g,
            Err(_) if dry => {
                out.push(PartResult::ok(
                    "scheme",
                    format!("dry run: схема «{ECO_SCHEME_NAME}» была бы создана"),
                ));
                return;
            }
            Err(e) => {
                out.push(PartResult::fail("scheme", e));
                return;
            }
        };
        let cpu = if level == EcoLevel::Deep { self.cfg.deep_cpu_max_pct } else { self.cfg.cpu_max_pct };
        let tag = |s: String| if dry { format!("dry run: {s}") } else { s };

        let r = if dry {
            Ok(())
        } else {
            self.backend.write_ac_value(&guid, SUB_PROCESSOR, PROCTHROTTLEMAX, cpu as u32)
        };
        out.push(match r {
            Ok(()) => PartResult::ok("cpu", tag(format!("процессор не выше {cpu} %"))),
            Err(e) => PartResult::fail("cpu", format!("предел процессора не записан: {e}")),
        });

        let sec = self.cfg.monitor_off_sec;
        if sec > 0 {
            let r = if dry { Ok(()) } else { self.backend.write_ac_value(&guid, SUB_VIDEO, VIDEOIDLE, sec) };
            out.push(match r {
                Ok(()) => PartResult::ok("monitor", tag(format!("монитор гаснет через {sec} с"))),
                Err(e) => PartResult::fail("monitor", format!("тайм-аут монитора не записан: {e}")),
            });
        }

        let r = if dry { Ok(()) } else { self.backend.set_active_scheme(&guid) };
        out.push(match r {
            Ok(()) => PartResult::ok("scheme", tag(format!("активна схема «{ECO_SCHEME_NAME}»"))),
            Err(e) => PartResult::fail("scheme", format!("схема «{ECO_SCHEME_NAME}» не включена: {e}")),
        });
    }

    fn restore(&mut self, saved: Option<PowerRestore>) -> (Option<PowerRestore>, Vec<PartResult>) {
        let Some(mut r) = saved else { return (None, Vec::new()) };
        let mut out = Vec::new();
        let dry = self.dry_run;
        let tag = |s: String| if dry { format!("dry run: {s}") } else { s };

        if let Some(prev) = r.prev_scheme.clone() {
            // Прежней схемы больше нет — «Сбалансированная».
            let exists = match self.backend.schemes() {
                Ok(list) => list.iter().any(|(g, _)| g.eq_ignore_ascii_case(&prev)),
                Err(_) => true,
            };
            let target = if exists { prev } else { BALANCED.to_string() };
            let res = if dry { Ok(()) } else { self.backend.set_active_scheme(&target) };
            out.push(match res {
                Ok(()) => {
                    r.prev_scheme = None;
                    PartResult::ok("scheme", tag(format!("возвращена схема {target}")))
                }
                Err(e) => PartResult::fail("scheme", format!("схема {target} не возвращена: {e}")),
            });
        }

        if let Some(prev) = r.prev_overlay.clone() {
            let res = if dry { Ok(()) } else { self.backend.set_overlay(&prev) };
            out.push(match res {
                Ok(()) => {
                    r.prev_overlay = None;
                    PartResult::ok("overlay", tag(format!("возвращён оверлей {prev}")))
                }
                Err(e) => PartResult::fail("overlay", format!("оверлей {prev} не возвращён: {e}")),
            });
        }

        if r.gpu_was.is_some() {
            if !self.backend.gpu_task_installed() {
                // Задачу удалили — повторять бесполезно.
                r.gpu_was = None;
                out.push(PartResult::fail(
                    "gpu",
                    "задача BluettiWatcherGpu не установлена — лимит GPU не возвращён",
                ));
            } else {
                let res = if dry { Ok(()) } else { self.backend.gpu_request(EcoLevel::None) };
                out.push(match res {
                    Ok(()) => {
                        r.gpu_was = None;
                        PartResult::ok("gpu", tag("запрошен обычный лимит GPU".into()))
                    }
                    Err(e) => PartResult::fail("gpu", e),
                });
            }
        }

        ((!r.is_empty()).then_some(r), out)
    }
}

/// GUID → строка `xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx` в нижнем регистре.
pub fn guid_to_string(g: &windows::core::GUID) -> String {
    let h = format!("{:032x}", g.to_u128());
    format!("{}-{}-{}-{}-{}", &h[0..8], &h[8..12], &h[12..16], &h[16..20], &h[20..32])
}

/// Строка (со скобками или без) → GUID.
pub fn parse_guid(s: &str) -> Result<windows::core::GUID, String> {
    let t = s.trim().trim_start_matches('{').trim_end_matches('}');
    let parts: Vec<&str> = t.split('-').collect();
    let lens: Vec<usize> = parts.iter().map(|p| p.len()).collect();
    if lens != [8, 4, 4, 4, 12] {
        return Err(format!("неверный GUID «{s}»"));
    }
    u128::from_str_radix(&parts.concat(), 16)
        .map(windows::core::GUID::from_u128)
        .map_err(|_| format!("неверный GUID «{s}»"))
}

#[cfg(test)]
mod tests;
