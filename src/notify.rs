//! Уведомления Windows (toast). Ошибка показа только пишется в лог.

pub const APP_ID: &str = "Reva1v.BluettiWatcher";

pub trait Notifier {
    fn notify(&self, title: &str, text: &str);
}

pub struct ToastNotifier {
    app_id: &'static str,
}

impl ToastNotifier {
    /// `installed` — ярлык «Пуск» с AUMID программы создан; иначе toast идёт от имени PowerShell.
    pub fn new(installed: bool) -> Self {
        let app_id = if installed { APP_ID } else { tauri_winrt_notification::Toast::POWERSHELL_APP_ID };
        Self { app_id }
    }
}

impl Notifier for ToastNotifier {
    fn notify(&self, title: &str, text: &str) {
        tracing::info!("уведомление: {title} — {text}");
        let r = tauri_winrt_notification::Toast::new(self.app_id).title(title).text1(text).show();
        if let Err(e) = r {
            tracing::warn!("уведомление не показано: {e}");
        }
    }
}

/// Тексты уведомлений.
pub mod text {
    pub fn hibernate_soon(minutes: u32) -> String {
        format!("ПК уйдёт в гибернацию примерно через {minutes} мин")
    }
    pub fn save_work(soc: u8) -> String {
        format!("Заряд {soc} % — сохраните работу")
    }
    pub fn deep_eco(soc: u8) -> String {
        format!("Заряд {soc} % — включён глубокий eco")
    }
    pub const NO_HIBERNATION: &str = "Гибернация выключена: при разряде станции ПК не уснёт сам";
}
