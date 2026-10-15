//! Память сервиса между циклами. Сервис почти всё время спит, поэтому после цикла его страницы
//! отдаются Windows (`SetProcessWorkingSetSize(-1, -1)`): в Диспетчере задач остаётся ~1 МБ, а на следующем
//! цикле нужные страницы подгружаются обратно из памяти ожидания.

/// Отдать Windows рабочий набор процесса.
#[cfg(windows)]
pub fn trim() {
    use windows::Win32::System::Threading::{GetCurrentProcess, SetProcessWorkingSetSize};
    // SAFETY: псевдо-дескриптор своего процесса; `usize::MAX` дважды — документированная просьба сбросить набор.
    if let Err(e) = unsafe { SetProcessWorkingSetSize(GetCurrentProcess(), usize::MAX, usize::MAX) } {
        tracing::debug!("сброс рабочего набора: {e}");
    }
}

#[cfg(not(windows))]
pub fn trim() {}

/// Сброс ещё и по таймеру: после BLE-сессии WinRT дочищает свои объекты в фоне секундами позже.
pub fn spawn_trimmer(period: std::time::Duration) {
    tokio::spawn(async move {
        let mut t = tokio::time::interval(period);
        t.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Delay);
        loop {
            t.tick().await;
            trim();
        }
    });
}
