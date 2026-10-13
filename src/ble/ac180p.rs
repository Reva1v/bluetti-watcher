//! Карта регистров AC180P (`bluetti_bt_lib/devices/ac180p.py`, база `BaseDeviceV2`).

use crate::clock::Ts;
use crate::model::Reading;

/// Блоки чтения: (адрес, количество). Протокол v2 принимает блоки по 10 регистров.
pub const BLOCKS: [(u16, u16); 3] = [(100, 10), (140, 10), (1314, 1)];

pub const REG_SOC: u16 = 102;
pub const REG_DC_OUT: u16 = 140;
pub const REG_AC_OUT: u16 = 142;
pub const REG_DC_IN: u16 = 144;
pub const REG_AC_IN: u16 = 146;
pub const REG_AC_IN_V: u16 = 1314;

/// Регистры из прочитанных блоков → показание. Отсутствующий блок оставляет поля `None`.
pub fn to_reading(ts: Ts, blocks: &[(u16, Vec<u16>)]) -> Reading {
    let get = |addr: u16| -> Option<u16> {
        blocks.iter().find_map(|(start, vals)| {
            let i = addr.checked_sub(*start)? as usize;
            vals.get(i).copied()
        })
    };
    Reading {
        ts,
        soc: get(REG_SOC).filter(|&v| v <= 100).map(|v| v as u8),
        dc_out_w: get(REG_DC_OUT).map(u32::from),
        ac_out_w: get(REG_AC_OUT).map(u32::from),
        dc_in_w: get(REG_DC_IN).map(u32::from),
        ac_in_w: get(REG_AC_IN).map(u32::from),
        ac_in_v: get(REG_AC_IN_V).map(|v| f32::from(v) / 10.0),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn blocks(soc: u16) -> Vec<(u16, Vec<u16>)> {
        let mut b100 = vec![0u16; 10];
        b100[2] = soc;
        let b140 = vec![4, 0, 182, 0, 0, 0, 230, 0, 0, 0];
        vec![(100, b100), (140, b140), (1314, vec![2301])]
    }

    #[test]
    fn maps_registers() {
        let r = to_reading(5, &blocks(64));
        assert_eq!(r.ts, 5);
        assert_eq!(r.soc, Some(64));
        assert_eq!(r.dc_out_w, Some(4));
        assert_eq!(r.ac_out_w, Some(182));
        assert_eq!(r.dc_in_w, Some(0));
        assert_eq!(r.ac_in_w, Some(230));
        assert_eq!(r.ac_in_v, Some(230.1));
    }

    #[test]
    fn soc_out_of_range_is_none() {
        assert_eq!(to_reading(0, &blocks(101)).soc, None);
    }

    #[test]
    fn missing_block_leaves_fields_empty() {
        let r = to_reading(0, &blocks(50)[..1]);
        assert_eq!(r.soc, Some(50));
        assert_eq!(r.ac_in_w, None);
        assert!(!r.is_complete());
    }
}
