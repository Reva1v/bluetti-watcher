//! Схема «Bluetti Eco»: поиск/создание/удаление и реализация `PowerBackend` через powrprof.
//! Схемы владельца (в т.ч. «Экономия (нет света)» `b1e771e0-…`) не меняются: пишем только в свою копию.

use super::{ECO_SCHEME_NAME, PowerBackend, is_protected};
use crate::model::EcoLevel;

/// Найти «Bluetti Eco» (по сохранённому GUID или по имени) или создать копией `base`.
pub(crate) fn ensure<B: PowerBackend>(
    b: &mut B,
    known: Option<&str>,
    base: &str,
    dry_run: bool,
) -> Result<String, String> {
    if let Some(g) = find(b, known)? {
        return Ok(g);
    }
    if dry_run {
        return Err(format!("dry run: схема «{ECO_SCHEME_NAME}» не создаётся"));
    }
    let g = b.duplicate_scheme(base).map_err(|e| format!("схема «{ECO_SCHEME_NAME}» не создана: {e}"))?;
    if let Err(e) = b.set_friendly_name(&g, ECO_SCHEME_NAME) {
        let _ = b.delete_scheme(&g);
        return Err(format!("схема «{ECO_SCHEME_NAME}» не названа: {e}"));
    }
    Ok(g)
}

/// Удалить «Bluetti Eco». `Ok(true)` — удалена, `Ok(false)` — её нет (или dry run).
pub(crate) fn remove<B: PowerBackend>(b: &mut B, known: Option<&str>, dry_run: bool) -> Result<bool, String> {
    let Some(g) = find(b, known)? else { return Ok(false) };
    if b.active_scheme().is_ok_and(|a| a.eq_ignore_ascii_case(&g)) {
        return Err(format!("схема «{ECO_SCHEME_NAME}» активна — сначала снимите eco"));
    }
    if dry_run {
        return Ok(false);
    }
    b.delete_scheme(&g).map(|()| true)
}

fn find<B: PowerBackend>(b: &B, known: Option<&str>) -> Result<Option<String>, String> {
    let list = b.schemes()?;
    if let Some(k) = known.filter(|k| !is_protected(k))
        && list.iter().any(|(g, _)| g.eq_ignore_ascii_case(k))
    {
        return Ok(Some(k.to_ascii_lowercase()));
    }
    Ok(list.into_iter().find(|(g, n)| n == ECO_SCHEME_NAME && !is_protected(g)).map(|(g, _)| g))
}

/// Системная реализация: powrprof из crate `windows`, оверлей — недокументированные функции powrprof.dll.
pub struct WindowsPower;

#[cfg(windows)]
mod win {
    use windows::Win32::Foundation::{ERROR_NO_MORE_ITEMS, HLOCAL, LocalFree, WIN32_ERROR};
    use windows::Win32::System::LibraryLoader::{GetProcAddress, LoadLibraryW};
    use windows::Win32::System::Power::{
        ACCESS_SCHEME, PowerDeleteScheme, PowerDuplicateScheme, PowerEnumerate, PowerGetActiveScheme,
        PowerReadFriendlyName, PowerSetActiveScheme, PowerWriteACValueIndex, PowerWriteFriendlyName,
    };
    use windows::core::{GUID, s, w};

    use super::super::{guid_to_string, parse_guid};

    pub fn check(op: &str, e: WIN32_ERROR) -> Result<(), String> {
        if e.0 == 0 {
            Ok(())
        } else {
            let msg = windows::core::Error::from(e.to_hresult()).message();
            Err(format!("{op}: код {} ({})", e.0, msg.trim()))
        }
    }

    /// Забрать GUID, выделенный powrprof через LocalAlloc.
    unsafe fn take(p: *mut GUID) -> String {
        let s = guid_to_string(unsafe { &*p });
        unsafe { LocalFree(Some(HLOCAL(p as _))) };
        s
    }

    pub fn active_scheme() -> Result<String, String> {
        let mut p: *mut GUID = std::ptr::null_mut();
        check("PowerGetActiveScheme", unsafe { PowerGetActiveScheme(None, &mut p) })?;
        Ok(unsafe { take(p) })
    }

    pub fn set_active_scheme(guid: &str) -> Result<(), String> {
        let g = parse_guid(guid)?;
        check("PowerSetActiveScheme", unsafe { PowerSetActiveScheme(None, Some(&g)) })
    }

    pub fn duplicate_scheme(source: &str) -> Result<String, String> {
        let g = parse_guid(source)?;
        let mut p: *mut GUID = std::ptr::null_mut();
        check("PowerDuplicateScheme", unsafe { PowerDuplicateScheme(None, &g, &mut p) })?;
        Ok(unsafe { take(p) })
    }

    pub fn delete_scheme(guid: &str) -> Result<(), String> {
        let g = parse_guid(guid)?;
        check("PowerDeleteScheme", unsafe { PowerDeleteScheme(None, &g) })
    }

    pub fn friendly_name(g: &GUID) -> Result<String, String> {
        let mut size = 0u32;
        check("PowerReadFriendlyName", unsafe {
            PowerReadFriendlyName(None, Some(g), None, None, None, &mut size)
        })?;
        let mut buf = vec![0u8; size as usize];
        check("PowerReadFriendlyName", unsafe {
            PowerReadFriendlyName(None, Some(g), None, None, Some(buf.as_mut_ptr()), &mut size)
        })?;
        let wide: Vec<u16> = buf.chunks_exact(2).map(|c| u16::from_le_bytes([c[0], c[1]])).collect();
        let end = wide.iter().position(|&c| c == 0).unwrap_or(wide.len());
        Ok(String::from_utf16_lossy(&wide[..end]))
    }

    pub fn schemes() -> Result<Vec<(String, String)>, String> {
        let mut out = Vec::new();
        for index in 0.. {
            let mut g = GUID::zeroed();
            let mut size = std::mem::size_of::<GUID>() as u32;
            let e = unsafe {
                PowerEnumerate(
                    None,
                    None,
                    None,
                    ACCESS_SCHEME,
                    index,
                    Some(&mut g as *mut GUID as *mut u8),
                    &mut size,
                )
            };
            if e == ERROR_NO_MORE_ITEMS {
                break;
            }
            check("PowerEnumerate", e)?;
            out.push((guid_to_string(&g), friendly_name(&g).unwrap_or_default()));
        }
        Ok(out)
    }

    pub fn set_friendly_name(guid: &str, name: &str) -> Result<(), String> {
        let g = parse_guid(guid)?;
        let bytes: Vec<u8> = name.encode_utf16().chain([0]).flat_map(u16::to_le_bytes).collect();
        check("PowerWriteFriendlyName", unsafe { PowerWriteFriendlyName(None, &g, None, None, &bytes) })
    }

    pub fn write_ac_value(scheme: &str, subgroup: &str, setting: &str, value: u32) -> Result<(), String> {
        let (g, sub, set) = (parse_guid(scheme)?, parse_guid(subgroup)?, parse_guid(setting)?);
        check("PowerWriteACValueIndex", unsafe {
            PowerWriteACValueIndex(None, &g, Some(&sub), Some(&set), value)
        })
    }

    type GetOverlay = unsafe extern "system" fn(*mut GUID) -> u32;
    // Win64: структура 16 байт по значению передаётся скрытым указателем — Rust делает так же.
    type SetOverlay = unsafe extern "system" fn(GUID) -> u32;

    fn proc(name: windows::core::PCSTR) -> Result<unsafe extern "system" fn() -> isize, String> {
        let h = unsafe { LoadLibraryW(w!("powrprof.dll")) }.map_err(|e| format!("powrprof.dll: {e}"))?;
        unsafe { GetProcAddress(h, name) }.ok_or_else(|| "в powrprof.dll нет функций оверлея".to_string())
    }

    pub fn overlay() -> Result<String, String> {
        let f: GetOverlay = unsafe { std::mem::transmute(proc(s!("PowerGetActualOverlayScheme"))?) };
        let mut g = GUID::zeroed();
        check("PowerGetActualOverlayScheme", WIN32_ERROR(unsafe { f(&mut g) }))?;
        Ok(guid_to_string(&g))
    }

    pub fn set_overlay(guid: &str) -> Result<(), String> {
        let g = parse_guid(guid)?;
        let f: SetOverlay = unsafe { std::mem::transmute(proc(s!("PowerSetActiveOverlayScheme"))?) };
        check("PowerSetActiveOverlayScheme", WIN32_ERROR(unsafe { f(g) }))
    }
}

#[cfg(windows)]
impl PowerBackend for WindowsPower {
    fn active_scheme(&self) -> Result<String, String> {
        win::active_scheme()
    }
    fn set_active_scheme(&mut self, guid: &str) -> Result<(), String> {
        win::set_active_scheme(guid)
    }
    fn schemes(&self) -> Result<Vec<(String, String)>, String> {
        win::schemes()
    }
    fn duplicate_scheme(&mut self, source: &str) -> Result<String, String> {
        win::duplicate_scheme(source)
    }
    fn set_friendly_name(&mut self, guid: &str, name: &str) -> Result<(), String> {
        if is_protected(guid) {
            return Err(format!("схема {guid} защищена"));
        }
        win::set_friendly_name(guid, name)
    }
    fn write_ac_value(
        &mut self,
        scheme: &str,
        subgroup: &str,
        setting: &str,
        value: u32,
    ) -> Result<(), String> {
        if is_protected(scheme) {
            return Err(format!("схема {scheme} защищена"));
        }
        win::write_ac_value(scheme, subgroup, setting, value)
    }
    fn delete_scheme(&mut self, guid: &str) -> Result<(), String> {
        if is_protected(guid) {
            return Err(format!("схема {guid} защищена"));
        }
        win::delete_scheme(guid)
    }
    fn overlay(&self) -> Result<String, String> {
        win::overlay()
    }
    fn set_overlay(&mut self, guid: &str) -> Result<(), String> {
        win::set_overlay(guid)
    }
    fn gpu_task_installed(&self) -> bool {
        super::gpu::task_installed()
    }
    fn gpu_request(&mut self, level: EcoLevel) -> Result<(), String> {
        super::gpu::request(level)
    }
}
