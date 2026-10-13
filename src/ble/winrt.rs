//! Транспорт BLE через WinRT (research.md R2): подключение по адресу без сканирования.

use tokio::sync::mpsc::{UnboundedReceiver, unbounded_channel};
use windows::Devices::Bluetooth::GenericAttributeProfile::{
    GattCharacteristic, GattCharacteristicProperties, GattClientCharacteristicConfigurationDescriptorValue,
    GattCommunicationStatus, GattDeviceService, GattValueChangedEventArgs, GattWriteOption,
};
use windows::Devices::Bluetooth::{BluetoothAdapter, BluetoothCacheMode, BluetoothLEDevice};
use windows::Devices::Radios::RadioState;
use windows::Foundation::TypedEventHandler;
use windows::Storage::Streams::{DataReader, DataWriter};
use windows::core::{GUID, Ref};

use super::Transport;
use crate::model::MissReason;

pub const NOTIFY_UUID: GUID = GUID::from_u128(0x0000ff01_0000_1000_8000_00805f9b34fb);
pub const WRITE_UUID: GUID = GUID::from_u128(0x0000ff02_0000_1000_8000_00805f9b34fb);

struct Link {
    device: BluetoothLEDevice,
    services: Vec<GattDeviceService>,
    notify: GattCharacteristic,
    write: GattCharacteristic,
    write_option: GattWriteOption,
    token: i64,
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
    let Ok(op) = BluetoothAdapter::GetDefaultAsync() else { return false };
    let Ok(adapter) = op.await else { return false };
    if !adapter.IsLowEnergySupported().unwrap_or(false) {
        return false;
    }
    let Ok(op) = adapter.GetRadioAsync() else { return false };
    match op.await {
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
        let Ok(op) = s.GetCharacteristicsForUuidWithCacheModeAsync(uuid, mode) else { continue };
        let Ok(r) = op.await else { continue };
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
        let device = BluetoothLEDevice::FromBluetoothAddressAsync(self.address)
            .map_err(|_| MissReason::NotFound)?
            .await
            .map_err(|_| MissReason::NotFound)?;
        let result = device
            .GetGattServicesWithCacheModeAsync(mode)
            .map_err(|_| MissReason::NotFound)?
            .await
            .map_err(|_| MissReason::NotFound)?;
        if result.Status().ok() != Some(GattCommunicationStatus::Success) {
            let _ = device.Close();
            return Err(MissReason::NotFound);
        }
        let services: Vec<GattDeviceService> =
            result.Services().map(|v| v.into_iter().collect()).unwrap_or_default();
        let close = |device: &BluetoothLEDevice, services: &[GattDeviceService]| {
            for s in services {
                let _ = s.Close();
            }
            let _ = device.Close();
        };
        let (Some(notify), Some(write)) = (
            find_characteristic(&services, NOTIFY_UUID, mode).await,
            find_characteristic(&services, WRITE_UUID, mode).await,
        ) else {
            tracing::warn!("у устройства нет характеристик BLUETTI ff01/ff02");
            close(&device, &services);
            return Err(MissReason::NotFound);
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
        let token = match notify.ValueChanged(&handler) {
            Ok(t) => t,
            Err(_) => {
                close(&device, &services);
                return Err(MissReason::NotFound);
            }
        };
        // Служебная запись дескриптора CCCD (конституция, принцип I, v1.0.1).
        let cccd = async {
            notify
                .WriteClientCharacteristicConfigurationDescriptorWithResultAsync(
                    GattClientCharacteristicConfigurationDescriptorValue::Notify,
                )?
                .await?
                .Status()
        }
        .await;
        if cccd.ok() != Some(GattCommunicationStatus::Success) {
            let _ = notify.RemoveValueChanged(token);
            close(&device, &services);
            return Err(MissReason::NotFound);
        }
        Ok((Link { device, services, notify, write, write_option, token }, rx))
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
            link.write.WriteValueWithResultAndOptionAsync(&buf, link.write_option)?.await?.Status()
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
        if let Some(link) = self.link.take() {
            let _ = link.notify.RemoveValueChanged(link.token);
            for s in &link.services {
                let _ = s.Close();
            }
            let _ = link.device.Close();
        }
    }
}
