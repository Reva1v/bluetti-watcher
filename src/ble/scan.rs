//! Поиск станций BLUETTI по рекламе BLE.

use std::collections::BTreeMap;
use std::sync::{Arc, Mutex};
use std::time::Duration;

use regex::Regex;
use windows::Devices::Bluetooth::Advertisement::{
    BluetoothLEAdvertisementReceivedEventArgs, BluetoothLEAdvertisementWatcher, BluetoothLEScanningMode,
};
use windows::Foundation::TypedEventHandler;
use windows::core::Ref;

/// Имена устройств BLUETTI (`bluetti_bt_lib/devices/__init__.py`).
pub const NAME_PATTERN: &str = r"^(AC2A|AC2P|AC50B|AC60|AC60P|AC70|AC70P|AC180|AC180T|AC180P|AC200L|AC200M|AC200PL|AC300|AC500|AP300|EB3A|EL10|EL100V2|EL30V2|EP500|EP500P|EP600|EP760|EP800|EP2000|Handsfree\s1|PR30V2|PR100V2)(\d+)$";

#[derive(Debug, Clone)]
pub struct Found {
    pub name: String,
    pub address: u64,
    pub rssi: i16,
}

/// Ждать рекламу устройства с адресом `address` не дольше `max`. После сна Windows забывает неспаренную
/// станцию, и `FromBluetoothAddressAsync` сразу отказывает, пока устройство снова не попадётся в эфире.
pub async fn seen(address: u64, max: Duration) -> windows::core::Result<bool> {
    let hit = Arc::new(std::sync::atomic::AtomicBool::new(false));
    let watcher = BluetoothLEAdvertisementWatcher::new()?;
    watcher.SetScanningMode(BluetoothLEScanningMode::Active)?;
    let sink = hit.clone();
    let token = watcher.Received(&TypedEventHandler::new(
        move |_: Ref<BluetoothLEAdvertisementWatcher>,
              args: Ref<BluetoothLEAdvertisementReceivedEventArgs>| {
            if let Some(a) = args.as_ref()
                && a.BluetoothAddress()? == address
            {
                sink.store(true, std::sync::atomic::Ordering::Relaxed);
            }
            Ok(())
        },
    ))?;
    // Скан останавливается и при брошенном future (таймаут чтения), иначе эфир слушался бы дальше.
    let _stop = StopOnDrop(&watcher, token);
    watcher.Start()?;
    let deadline = tokio::time::Instant::now() + max;
    while !hit.load(std::sync::atomic::Ordering::Relaxed) && tokio::time::Instant::now() < deadline {
        tokio::time::sleep(Duration::from_millis(200)).await;
    }
    Ok(hit.load(std::sync::atomic::Ordering::Relaxed))
}

struct StopOnDrop<'a>(&'a BluetoothLEAdvertisementWatcher, i64);

impl Drop for StopOnDrop<'_> {
    fn drop(&mut self) {
        let _ = self.0.Stop();
        let _ = self.0.RemoveReceived(self.1);
    }
}

pub async fn scan(duration: Duration) -> windows::core::Result<Vec<Found>> {
    let re = Regex::new(NAME_PATTERN).expect("regex");
    let found: Arc<Mutex<BTreeMap<u64, Found>>> = Arc::default();
    let watcher = BluetoothLEAdvertisementWatcher::new()?;
    watcher.SetScanningMode(BluetoothLEScanningMode::Active)?;
    let sink = found.clone();
    let token = watcher.Received(&TypedEventHandler::new(
        move |_: Ref<BluetoothLEAdvertisementWatcher>,
              args: Ref<BluetoothLEAdvertisementReceivedEventArgs>| {
            if let Some(a) = args.as_ref() {
                let name = a
                    .Advertisement()
                    .and_then(|ad| ad.LocalName())
                    .map(|h| h.to_string())
                    .unwrap_or_default();
                if re.is_match(&name) {
                    let f = Found { name, address: a.BluetoothAddress()?, rssi: a.RawSignalStrengthInDBm()? };
                    sink.lock().unwrap_or_else(|p| p.into_inner()).insert(f.address, f);
                }
            }
            Ok(())
        },
    ))?;
    watcher.Start()?;
    tokio::time::sleep(duration).await;
    watcher.Stop()?;
    watcher.RemoveReceived(token)?;
    let v = found.lock().unwrap_or_else(|p| p.into_inner()).values().cloned().collect();
    Ok(v)
}
