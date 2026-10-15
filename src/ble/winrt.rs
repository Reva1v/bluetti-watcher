//! Транспорт BLE через WinRT (research.md R2): подключение по адресу; скан — только если Windows забыла станцию.

use std::time::Duration;

use tokio::sync::mpsc::{UnboundedReceiver, unbounded_channel};
use windows::Devices::Bluetooth::GenericAttributeProfile::{
    GattCharacteristic, GattCharacteristicProperties, GattClientCharacteristicConfigurationDescriptorValue,
    GattCommunicationStatus, GattDeviceService, GattValueChangedEventArgs, GattWriteOption,
};
use windows::Devices::Bluetooth::{BluetoothAdapter, BluetoothCacheMode, BluetoothLEDevice};
use windows::Devices::Radios::RadioState;
use windows::Foundation::TypedEventHandler;
use windows::Storage::Streams::{DataReader, DataWriter};
use windows::core::{GUID, Interface, Ref, RuntimeType};
use windows_future::{IAsyncInfo, IAsyncOperation};

use super::Transport;
use crate::model::MissReason;

pub const NOTIFY_UUID: GUID = GUID::from_u128(0x0000ff01_0000_1000_8000_00805f9b34fb);
pub const WRITE_UUID: GUID = GUID::from_u128(0x0000ff02_0000_1000_8000_00805f9b34fb);

/// Дождаться WinRT-операции; если future бросили (таймаут чтения), операция отменяется. Сама windows-rs её
/// не отменяет, и брошенное подключение жило бы дальше, мешая следующему опросу.
pub(crate) async fn cancellable<T: RuntimeType + 'static>(
    op: windows::core::Result<IAsyncOperation<T>>,
) -> windows::core::Result<T> {
    struct CancelOnDrop(Option<IAsyncInfo>);
    impl Drop for CancelOnDrop {
        fn drop(&mut self) {
            if let Some(info) = self.0.take() {
                let _ = info.Cancel();
            }
        }
    }
    let op = op?;
    let mut guard = CancelOnDrop(op.cast().ok());
    let r = op.await;
    guard.0 = None;
    r
}

/// Устройство и его сервисы; закрываются при drop — в том числе когда чтение бросили посреди подключения.
struct Conn {
    device: BluetoothLEDevice,
    services: Vec<GattDeviceService>,
}

impl Drop for Conn {
    fn drop(&mut self) {
        for s in &self.services {
            let _ = s.Close();
        }
        let _ = self.device.Close();
    }
}

struct Link {
    notify: GattCharacteristic,
    write: GattCharacteristic,
    write_option: GattWriteOption,
    token: i64,
    /// Поля падают после `Drop::drop`: сначала снимается подписка, потом закрывается устройство.
    _conn: Conn,
}

impl Drop for Link {
    fn drop(&mut self) {
        let _ = self.notify.RemoveValueChanged(self.token);
    }
}

pub struct WinRtTransport {
    address: u64,
    link: Option<Link>,
    /// После ошибки GATT следующий connect идёт без кеша.
    uncached_next: bool,
}

impl WinRtTransport {
    pub fn new(address: u64) -> Self {
        Self { address, link: None, uncached_next: false }
    }
}

/// Адаптер есть и радио включено.
pub async fn adapter_ready() -> bool {
    let Ok(adapter) = cancellable(BluetoothAdapter::GetDefaultAsync()).await else { return false };
    if !adapter.IsLowEnergySupported().unwrap_or(false) {
        return false;
    }
    match cancellable(adapter.GetRadioAsync()).await {
        Ok(radio) => radio.State().map(|s| s == RadioState::On).unwrap_or(false),
        Err(_) => false,
    }
}

fn buffer_bytes(buf: &windows::Storage::Streams::IBuffer) -> windows::core::Result<Vec<u8>> {
    let len = buf.Length()? as usize;
    let reader = DataReader::FromBuffer(buf)?;
    let mut v = vec![0u8; len];
    reader.ReadBytes(&mut v)?;
    Ok(v)
}

async fn find_characteristic(
    services: &[GattDeviceService],
    uuid: GUID,
    mode: BluetoothCacheMode,
) -> Option<GattCharacteristic> {
    for s in services {
        let Ok(r) = cancellable(s.GetCharacteristicsForUuidWithCacheModeAsync(uuid, mode)).await else {
            continue;
        };
        if r.Status().ok() != Some(GattCommunicationStatus::Success) {
            continue;
        }
        if let Ok(list) = r.Characteristics()
            && list.Size().unwrap_or(0) > 0
            && let Ok(c) = list.GetAt(0)
        {
            return Some(c);
        }
    }
    None
}

impl WinRtTransport {
    async fn open(
        &mut self,
        mode: BluetoothCacheMode,
    ) -> Result<(Link, UnboundedReceiver<Vec<u8>>), MissReason> {
        // `NotFound` — только когда Windows не знает устройство; всё дальше — `Unreachable`.
        let device =
            cancellable(BluetoothLEDevice::FromBluetoothAddressAsync(self.address)).await.map_err(|e| {
                tracing::debug!("устройство по адресу: {e}");
                MissReason::NotFound
            })?;
        let mut conn = Conn { device, services: Vec::new() };
        let result = match cancellable(conn.device.GetGattServicesWithCacheModeAsync(mode)).await {
            Ok(r) => r,
            Err(e) => {
                tracing::debug!("GATT-сервисы: {e}");
                return Err(MissReason::Unreachable);
            }
        };
        let status = result.Status().ok();
        if status != Some(GattCommunicationStatus::Success) {
            tracing::debug!("GATT-сервисы: {status:?}");
            return Err(MissReason::Unreachable);
        }
        conn.services = result.Services().map(|v| v.into_iter().collect()).unwrap_or_default();
        let (Some(notify), Some(write)) = (
            find_characteristic(&conn.services, NOTIFY_UUID, mode).await,
            find_characteristic(&conn.services, WRITE_UUID, mode).await,
        ) else {
            tracing::warn!("у устройства нет характеристик BLUETTI ff01/ff02");
            return Err(MissReason::NoService);
        };

        let props = write.CharacteristicProperties().unwrap_or_default();
        let write_option = if props.contains(GattCharacteristicProperties::Write) {
            GattWriteOption::WriteWithResponse
        } else {
            GattWriteOption::WriteWithoutResponse
        };

        let (tx, rx) = unbounded_channel();
        let handler = TypedEventHandler::new(
            move |_: Ref<GattCharacteristic>, args: Ref<GattValueChangedEventArgs>| {
                if let Some(args) = args.as_ref()
                    && let Ok(buf) = args.CharacteristicValue()
                    && let Ok(bytes) = buffer_bytes(&buf)
                {
                    let _ = tx.send(bytes);
                }
                Ok(())
            },
        );
        let Ok(token) = notify.ValueChanged(&handler) else { return Err(MissReason::NotFound) };
        let link = Link { notify, write, write_option, token, _conn: conn };
        // Служебная запись дескриптора CCCD (конституция, принцип I, v1.0.1).
        let cccd = cancellable(link.notify.WriteClientCharacteristicConfigurationDescriptorWithResultAsync(
            GattClientCharacteristicConfigurationDescriptorValue::Notify,
        ))
        .await
        .and_then(|r| r.Status());
        if cccd.ok() != Some(GattCommunicationStatus::Success) {
            return Err(MissReason::Unreachable);
        }
        Ok((link, rx))
    }
}

impl Transport for WinRtTransport {
    async fn connect(&mut self) -> Result<UnboundedReceiver<Vec<u8>>, MissReason> {
        if !adapter_ready().await {
            return Err(MissReason::BleOff);
        }
        let first =
            if self.uncached_next { BluetoothCacheMode::Uncached } else { BluetoothCacheMode::Cached };
        let r = match self.open(first).await {
            Ok(r) => Ok(r),
            // Windows забыла станцию (бывает после сна): дождаться её рекламы и повторить.
            Err(MissReason::NotFound) => {
                match super::scan::seen(self.address, Duration::from_secs(8)).await {
                    Ok(true) => {
                        tracing::info!("станция снова в эфире, повтор подключения");
                        self.open(BluetoothCacheMode::Uncached).await
                    }
                    Ok(false) => Err(MissReason::NotFound),
                    Err(e) => {
                        tracing::debug!("скан BLE: {e}");
                        Err(MissReason::NotFound)
                    }
                }
            }
            Err(_) if first == BluetoothCacheMode::Cached => {
                tracing::debug!("повтор подключения без кеша GATT");
                self.open(BluetoothCacheMode::Uncached).await
            }
            Err(e) => Err(e),
        };
        match r {
            Ok((link, rx)) => {
                self.uncached_next = false;
                self.link = Some(link);
                Ok(rx)
            }
            Err(e) => {
                self.uncached_next = true;
                Err(e)
            }
        }
    }

    async fn write(&mut self, data: &[u8]) -> Result<(), MissReason> {
        let link = self.link.as_ref().ok_or(MissReason::NotFound)?;
        let status = async {
            let w = DataWriter::new()?;
            w.WriteBytes(data)?;
            let buf = w.DetachBuffer()?;
            cancellable(link.write.WriteValueWithResultAndOptionAsync(&buf, link.write_option))
                .await?
                .Status()
        }
        .await;
        match status {
            Ok(GattCommunicationStatus::Success) => Ok(()),
            other => {
                tracing::warn!("запись в станцию не удалась: {other:?}");
                self.uncached_next = true;
                Err(MissReason::Timeout)
            }
        }
    }

    async fn disconnect(&mut self) {
        self.link = None;
    }
}
