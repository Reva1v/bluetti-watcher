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
            Self::ModbusError => "modbus-error",
            Self::CryptoError => "crypto-error",
            Self::NoAddress => "no-address",
        }
    }
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
    fn load_sums_ac_and_dc() {
        let r = Reading { ac_out_w: Some(180), dc_out_w: Some(4), ..Default::default() };
        assert_eq!(r.load_w(), Some(184));
        assert_eq!(Reading::default().load_w(), None);
    }
}
