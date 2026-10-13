//! Цикл сервиса: чтение → запись в историю (решения подключаются в US4).

use std::time::Duration;

use crate::ble::{ReadOptions, ReadOutcome};
use crate::clock::{Clock, MIN, Ts};
use crate::config::Config;
use crate::model::{MissReason, ReadStatus};
use crate::store::Store;

/// Источник показаний: WinRT в проде, фейк в тестах.
#[allow(async_fn_in_trait)]
pub trait StationReader {
    async fn read(&mut self, encryption_hint: Option<bool>, ts: Ts) -> Result<ReadOutcome, MissReason>;
}

#[cfg(windows)]
pub struct WinRtReader {
    address: Option<u64>,
    keys: crate::ble::crypto::Keys,
}

#[cfg(windows)]
impl WinRtReader {
    pub fn new(address: Option<u64>) -> Self {
        Self { address, keys: crate::ble::crypto::Keys::bluetti() }
    }
}

#[cfg(windows)]
impl StationReader for WinRtReader {
    async fn read(&mut self, encryption_hint: Option<bool>, ts: Ts) -> Result<ReadOutcome, MissReason> {
        let address = self.address.ok_or(MissReason::NoAddress)?;
        let mut t = crate::ble::winrt::WinRtTransport::new(address);
        let opts = ReadOptions { encryption_hint, ..ReadOptions::default() };
        crate::ble::read_station(&mut t, &self.keys, &opts, ts).await
    }
}

pub struct Service<R: StationReader, C: Clock> {
    pub cfg: Config,
    pub store: Store,
    pub reader: R,
    pub clock: C,
    encryption: Option<bool>,
    last_purge: Ts,
}

impl<R: StationReader, C: Clock> Service<R, C> {
    pub fn new(cfg: Config, store: Store, reader: R, clock: C) -> Self {
        let encryption = store.get_meta::<bool>("encryption");
        Self { cfg, store, reader, clock, encryption, last_purge: 0 }
    }

    /// Один цикл: чтение и запись. Ошибки базы пишутся в лог, цикл не падает.
    pub async fn tick(&mut self) {
        let ts = self.clock.now();
        match self.reader.read(self.encryption, ts).await {
            Ok(out) => {
                if self.encryption != Some(out.encrypted) {
                    self.encryption = Some(out.encrypted);
                    log_db(self.store.set_meta("encryption", &out.encrypted));
                }
                let r = out.reading;
                let (status, reason) = if r.is_complete() {
                    (ReadStatus::Accepted, None)
                } else {
                    (ReadStatus::Rejected, Some("incomplete"))
                };
                tracing::debug!(?r, ?status, "показание");
                log_db(self.store.insert_reading(&r, status, reason));
            }
            Err(e) => {
                tracing::info!("нет показания: {e}");
                log_db(self.store.insert_miss(ts, e.code()));
            }
        }
        if ts - self.last_purge > 24 * 60 * MIN {
            self.last_purge = ts;
            log_db(self.store.purge(ts).map(|_| ()));
        }
    }

    /// Бесконечный цикл с шагом `poll_sec` без накопления сдвига.
    pub async fn run(&mut self) {
        log_db(self.store.insert_event(self.clock.now(), "service-start", "Сервис запущен", None));
        let period = Duration::from_secs(u64::from(self.cfg.poll_sec));
        let mut ticker = tokio::time::interval(period);
        ticker.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Delay);
        loop {
            ticker.tick().await;
            self.tick().await;
        }
    }
}

fn log_db<T>(r: rusqlite::Result<T>) {
    if let Err(e) = r {
        tracing::error!("база данных: {e}");
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::clock::FakeClock;
    use crate::model::Reading;

    struct Fake(Vec<Result<ReadOutcome, MissReason>>);

    impl StationReader for Fake {
        async fn read(&mut self, _: Option<bool>, ts: Ts) -> Result<ReadOutcome, MissReason> {
            self.0.remove(0).map(|mut o| {
                o.reading.ts = ts;
                o
            })
        }
    }

    fn ok(soc: u8) -> Result<ReadOutcome, MissReason> {
        Ok(ReadOutcome {
            reading: Reading { soc: Some(soc), ac_in_w: Some(100), ac_out_w: Some(90), ..Default::default() },
            encrypted: false,
        })
    }

    #[tokio::test]
    async fn ticks_store_readings_and_misses() {
        let reader = Fake(vec![ok(80), Err(MissReason::Timeout), ok(79)]);
        let mut s = Service::new(Config::default(), Store::in_memory().unwrap(), reader, FakeClock::new(MIN));
        for _ in 0..3 {
            s.tick().await;
            s.clock.advance(MIN);
        }
        assert_eq!(s.store.last_accepted().unwrap().unwrap().soc, Some(79));
        assert_eq!(s.store.get_meta::<bool>("encryption"), Some(false));
        assert_eq!(s.store.accepted_between(0, 10 * MIN).unwrap().len(), 2);
    }
}
