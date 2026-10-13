//! Modbus RTU поверх BLE. Только чтение (функция 0x03): функции записи здесь нет и не будет (конституция, принцип I).

pub const UNIT: u8 = 0x01;
pub const FN_READ: u8 = 0x03;

#[derive(Debug, Clone, PartialEq, Eq, thiserror::Error)]
pub enum ModbusError {
    #[error("ответ короче ожидаемого: {got} из {want} байт")]
    Short { got: usize, want: usize },
    #[error("неверный CRC")]
    Crc,
    #[error("станция вернула исключение Modbus {0:#04x}")]
    Exception(u8),
    #[error("неожиданный ответ: {0}")]
    Unexpected(&'static str),
}

/// CRC16-Modbus (полином 0xA001, начальное 0xFFFF).
pub fn crc16(data: &[u8]) -> u16 {
    let mut crc: u16 = 0xFFFF;
    for &b in data {
        crc ^= u16::from(b);
        for _ in 0..8 {
            crc = if crc & 1 != 0 { (crc >> 1) ^ 0xA001 } else { crc >> 1 };
        }
    }
    crc
}

/// `01 03 <addr BE> <count BE> <crc LE>`.
pub fn read_request(addr: u16, count: u16) -> Vec<u8> {
    let mut f = vec![UNIT, FN_READ];
    f.extend_from_slice(&addr.to_be_bytes());
    f.extend_from_slice(&count.to_be_bytes());
    let crc = crc16(&f);
    f.extend_from_slice(&crc.to_le_bytes());
    f
}

/// Длина полного ответа на чтение `count` регистров.
pub fn response_len(count: u16) -> usize {
    2 * count as usize + 5
}

/// Сколько байт ответа ещё ждать: ответ-исключение короче обычного (5 байт).
pub fn expected_len(buf: &[u8], count: u16) -> usize {
    if buf.len() >= 2 && buf[1] == FN_READ | 0x80 { 5 } else { response_len(count) }
}

/// `01 03 <len> <data…> <crc LE>` → значения регистров (u16 BE).
pub fn parse_read_response(resp: &[u8], count: u16) -> Result<Vec<u16>, ModbusError> {
    if resp.len() >= 5 && resp[1] == FN_READ | 0x80 {
        if crc16(&resp[..3]).to_le_bytes() != [resp[3], resp[4]] {
            return Err(ModbusError::Crc);
        }
        return Err(ModbusError::Exception(resp[2]));
    }
    let want = response_len(count);
    if resp.len() < want {
        return Err(ModbusError::Short { got: resp.len(), want });
    }
    let resp = &resp[..want];
    if crc16(&resp[..want - 2]).to_le_bytes() != [resp[want - 2], resp[want - 1]] {
        return Err(ModbusError::Crc);
    }
    if resp[0] != UNIT || resp[1] != FN_READ {
        return Err(ModbusError::Unexpected("не ответ на чтение"));
    }
    if resp[2] as usize != 2 * count as usize {
        return Err(ModbusError::Unexpected("длина данных не совпадает с запросом"));
    }
    Ok(resp[3..want - 2].chunks_exact(2).map(|c| u16::from_be_bytes([c[0], c[1]])).collect())
}

/// Кадр — корректный запрос чтения (для проверки инварианта «только чтение»).
pub fn is_read_request(frame: &[u8]) -> bool {
    frame.len() == 8
        && frame[0] == UNIT
        && frame[1] == FN_READ
        && crc16(&frame[..6]).to_le_bytes() == [frame[6], frame[7]]
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn crc_matches_reference_vector() {
        assert_eq!(crc16(&[0x01, 0x03, 0x00, 0x66, 0x00, 0x01]), 0x1564);
    }

    #[test]
    fn builds_read_request() {
        assert_eq!(read_request(102, 1), vec![0x01, 0x03, 0x00, 0x66, 0x00, 0x01, 0x64, 0x15]);
        assert!(is_read_request(&read_request(140, 10)));
    }

    fn response(values: &[u16]) -> Vec<u8> {
        let mut r = vec![UNIT, FN_READ, (values.len() * 2) as u8];
        for v in values {
            r.extend_from_slice(&v.to_be_bytes());
        }
        let crc = crc16(&r);
        r.extend_from_slice(&crc.to_le_bytes());
        r
    }

    #[test]
    fn parses_response() {
        let r = response(&[64, 182]);
        assert_eq!(parse_read_response(&r, 2).unwrap(), vec![64, 182]);
    }

    #[test]
    fn rejects_bad_crc_short_and_exception() {
        let mut r = response(&[1, 2]);
        let n = r.len();
        r[n - 1] ^= 0xFF;
        assert_eq!(parse_read_response(&r, 2), Err(ModbusError::Crc));
        assert!(matches!(parse_read_response(&response(&[1]), 2), Err(ModbusError::Short { .. })));
        let mut ex = vec![UNIT, 0x83, 0x02];
        let crc = crc16(&ex);
        ex.extend_from_slice(&crc.to_le_bytes());
        assert_eq!(expected_len(&ex, 10), 5);
        assert_eq!(parse_read_response(&ex, 10), Err(ModbusError::Exception(2)));
    }
}
