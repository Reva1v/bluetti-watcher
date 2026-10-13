//! Гибернация ПК (research.md R4).

pub trait Hibernator {
    /// Гибернация включена (`powercfg /h on`) и поддерживается.
    fn available(&self) -> bool;
    fn hibernate(&self) -> Result<(), String>;
}

pub struct WindowsHibernator {
    /// Только лог, без гибернации.
    pub dry_run: bool,
}

#[cfg(windows)]
impl Hibernator for WindowsHibernator {
    fn available(&self) -> bool {
        use windows::Win32::System::Power::{GetPwrCapabilities, SYSTEM_POWER_CAPABILITIES};
        let mut caps = SYSTEM_POWER_CAPABILITIES::default();
        let ok = unsafe { GetPwrCapabilities(&mut caps) };
        ok && caps.SystemS4 && caps.HiberFilePresent
    }

    fn hibernate(&self) -> Result<(), String> {
        if self.dry_run {
            tracing::warn!("dry run: гибернация пропущена");
            return Ok(());
        }
        tracing::warn!("гибернация");
        // hibernate=TRUE, force=FALSE, wakeup_events_disabled=FALSE.
        if unsafe { windows::Win32::System::Power::SetSuspendState(true, false, false) } {
            Ok(())
        } else {
            Err(format!("SetSuspendState: {}", windows::core::Error::from_thread().message()))
        }
    }
}
