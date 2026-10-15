//! Показание станции и его статус (data-model.md).

use serde::{Deserialize, Serialize};

use crate::clock::Ts;

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, Default)]
pub struct Reading {
    pub ts: Ts,
    /// 0–100; значение вне диапазона отбрасывается при разборе.
    pub soc: Option<u8>,
    pub ac_in_w: Option<u32>,
    pub ac_out_w: Option<u32>,
    pub dc_in_w: Option<u32>,
    pub dc_out_w: Option<u32>,
    pub ac_in_v: Option<f32>,
}

impl Reading {
    /// Полное показание: есть заряд и вход AC (FR-004).
    pub fn is_complete(&self) -> bool {
        self.soc.is_some() && self.ac_in_w.is_some()
    }

    pub fn load_w(&self) -> Option<u32> {
        match (self.ac_out_w, self.dc_out_w) {
            (None, None) => None,
            (a, d) => Some(a.unwrap_or(0) + d.unwrap_or(0)),
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum ReadStatus {
    Accepted,
    Rejected,
    Rebased,
    Miss,
}

impl ReadStatus {
    pub fn as_str(self) -> &'static str {
        match self {
            Self::Accepted => "accepted",
            Self::Rejected => "rejected",
            Self::Rebased => "rebased",
            Self::Miss => "miss",
        }
    }
}

/// Почему показания нет или оно отвергнуто.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, thiserror::Error)]
#[serde(rename_all = "kebab-case")]
pub enum MissReason {
    #[error("неполное показание")]
    Incomplete,
    #[error("неправдоподобный скачок заряда")]
    Implausible,
    #[error("станция не ответила вовремя")]
    Timeout,
    #[error("Bluetooth выключен или недоступен")]
    BleOff,
    #[error("станция не найдена")]
    NotFound,
    #[error("станция недоступна по Bluetooth")]
    Unreachable,
    #[error("у устройства нет сервиса BLUETTI")]
    NoService,
    #[error("ошибка Modbus в ответе станции")]
    ModbusError,
    #[error("ошибка защищённого соединения")]
    CryptoError,
    #[error("адрес станции не задан")]
    NoAddress,
}

impl MissReason {
    pub fn code(self) -> &'static str {
        match self {
            Self::Incomplete => "incomplete",
            Self::Implausible => "implausible",
            Self::Timeout => "timeout",
            Self::BleOff => "ble-off",
            Self::NotFound => "not-found",
            Self::Unreachable => "unreachable",
            Self::NoService => "no-service",
            Self::ModbusError => "modbus-error",
            Self::CryptoError => "crypto-error",
            Self::NoAddress => "no-address",
        }
    }

    /// Причины, при которых стоит посмотреть, видна ли станция в эфире.
    pub fn needs_scan(self) -> bool {
        matches!(self, Self::NotFound | Self::Unreachable | Self::Timeout)
    }

    /// Заголовок и подсказка для кнопки «Проверить подключение». `advertising` — видна ли станция
    /// в рекламе BLE (`None` — не проверяли).
    pub fn explain(self, advertising: Option<bool>) -> (&'static str, &'static str) {
        const PHONE: &str = "Станция выключена, далеко от ПК или уже подключена к другому устройству — чаще всего \
            к телефону с приложением BLUETTI. Закройте приложение или выключите Bluetooth на телефоне.";
        if self.needs_scan() && advertising == Some(false) {
            return ("Станция не видна по Bluetooth", PHONE);
        }
        match self {
            Self::NoAddress => (
                "Адрес станции не задан",
                "Найдите станцию командой `bluetti-watcher scan` и укажите mac в config.toml.",
            ),
            Self::BleOff => (
                "Bluetooth на ПК выключен или адаптера нет",
                "Включите Bluetooth в параметрах Windows (Win+A → Bluetooth).",
            ),
            Self::NotFound | Self::Unreachable if advertising == Some(true) => (
                "Станция видна, но не принимает подключение",
                "Станция занята другим подключением или Bluetooth в Windows завис. Закройте приложение BLUETTI \
                 на телефоне; если не помогло — выключите и включите Bluetooth на ПК.",
            ),
            Self::NotFound => ("Windows не находит станцию", PHONE),
            Self::Unreachable => ("Станция недоступна по Bluetooth", PHONE),
            Self::NoService => (
                "По этому адресу не станция BLUETTI",
                "Устройство отвечает, но без сервиса BLUETTI. Проверьте mac в config.toml (`bluetti-watcher scan`).",
            ),
            Self::Timeout => (
                "Станция подключилась, но не ответила вовремя",
                "Связь слабая или станция занята. Поставьте ПК ближе к станции и повторите проверку.",
            ),
            Self::CryptoError => (
                "Не удалось установить защищённое соединение",
                "Повторите проверку. Если ошибка повторяется — перезапустите станцию.",
            ),
            Self::ModbusError => {
                ("Станция ответила с ошибкой", "Повторите проверку; разовые ошибки не страшны.")
            }
            Self::Incomplete | Self::Implausible => ("Показание станции отвергнуто", "Повторите проверку."),
        }
    }
}

/// Уровень eco, который должен быть применён к ПК.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum EcoLevel {
    #[default]
    None,
    Eco,
    Deep,
}

/// Состояние сети по принятым показаниям (FR-005).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum Mains {
    On,
    Off,
    #[default]
    Unknown,
}

/// Выбор режима на дашборде (FR-015).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum ManualMode {
    #[default]
    Auto,
    Eco,
    Max,
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn complete_needs_soc_and_ac_in() {
        let mut r = Reading { soc: Some(50), ..Default::default() };
        assert!(!r.is_complete());
        r.ac_in_w = Some(0);
        assert!(r.is_complete());
    }

    #[test]
    fn explain_uses_advertising() {
        let r = MissReason::Unreachable;
        assert_eq!(r.explain(Some(false)).0, "Станция не видна по Bluetooth");
        assert_eq!(r.explain(Some(true)).0, "Станция видна, но не принимает подключение");
        assert_eq!(r.explain(None).0, "Станция недоступна по Bluetooth");
        // Без связи с эфиром причина не меняется.
        assert_eq!(MissReason::BleOff.explain(Some(false)).0, "Bluetooth на ПК выключен или адаптера нет");
        assert!(MissReason::Timeout.explain(Some(false)).1.contains("телефон"));
    }

    #[test]
    fn load_sums_ac_and_dc() {
        let r = Reading { ac_out_w: Some(180), dc_out_w: Some(4), ..Default::default() };
        assert_eq!(r.load_w(), Some(184));
        assert_eq!(Reading::default().load_w(), None);
    }
}
