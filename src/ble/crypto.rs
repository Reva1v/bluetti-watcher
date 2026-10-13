//! Шифрование BLUETTI IoT v2 (research.md R1). Порт `bluetti_bt_lib/bluetooth/encryption.py`.
//!
//! Ключи сессии никогда не логируются и не печатаются.

use cbc::cipher::block_padding::NoPadding;
use cbc::cipher::{BlockDecryptMut, BlockEncryptMut, KeyIvInit};
use md5::{Digest, Md5};
use p256::PublicKey;
use p256::ecdh::EphemeralSecret;
use p256::ecdsa::signature::{Signer, Verifier};
use p256::ecdsa::{Signature, SigningKey, VerifyingKey};
use p256::elliptic_curve::rand_core::CryptoRngCore;
use p256::elliptic_curve::sec1::ToEncodedPoint;

pub const KEX_MAGIC: [u8; 2] = *b"**";
pub const AES_BLOCK: usize = 16;

const LOCAL_AES_KEY: [u8; 16] = hex16("459FC535808941F17091E0993EE3E93D");
const PRIVATE_KEY_L1: &str = "4F19A16E3E87BDD9BD24D3E5495B88041511943CBC8B969ADE9641D0F56AF337";
/// Публичный ключ станции K2 (точка P-256 без префикса DER и байта 0x04).
const PUBLIC_KEY_K2: &str = "A73ABF5D2232C8C1C72E68304343C272495E3A8FD6F30EA96DE2F4B3CE60B251\
                             EE21AC667CF8A71E18B46B664EAEFFE3C489F24F695B6411DB7E22CCC85A8594";

const MSG_CHALLENGE: u8 = 1;
const MSG_CHALLENGE_ACCEPTED: u8 = 3;
const MSG_PEER_PUBKEY: u8 = 4;
const MSG_PUBKEY_ACCEPTED: u8 = 6;

#[derive(Debug, Clone, PartialEq, Eq, thiserror::Error)]
pub enum CryptoError {
    #[error("сообщение рукопожатия неверной длины")]
    BadLength,
    #[error("подпись станции не прошла проверку")]
    BadSignature,
    #[error("неверный ключ станции")]
    BadKey,
    #[error("зашифрованные данные не кратны 16 байтам")]
    Unaligned,
    #[error("шифрованное сообщение до установки ключа")]
    NoKey,
    #[error("станция отклонила ключ")]
    KeyRejected,
}

const fn hex16(s: &str) -> [u8; 16] {
    let b = s.as_bytes();
    let mut out = [0u8; 16];
    let mut i = 0;
    while i < 16 {
        out[i] = (nib(b[2 * i]) << 4) | nib(b[2 * i + 1]);
        i += 1;
    }
    out
}

const fn nib(c: u8) -> u8 {
    match c {
        b'0'..=b'9' => c - b'0',
        b'a'..=b'f' => c - b'a' + 10,
        b'A'..=b'F' => c - b'A' + 10,
        _ => panic!("не hex"),
    }
}

fn hex(s: &str) -> Vec<u8> {
    (0..s.len()).step_by(2).map(|i| u8::from_str_radix(&s[i..i + 2], 16).expect("hex")).collect()
}

/// Ключи протокола: проверка подписи станции и подпись нашего ключа.
/// В проде — константы BLUETTI, в тестах — сгенерированная пара для фейковой станции.
#[derive(Clone)]
pub struct Keys {
    pub station_verify: VerifyingKey,
    pub client_sign: SigningKey,
}

impl Keys {
    pub fn bluetti() -> Self {
        let mut k2 = vec![0x04];
        k2.extend(hex(PUBLIC_KEY_K2));
        Self {
            station_verify: VerifyingKey::from_sec1_bytes(&k2).expect("K2"),
            client_sign: SigningKey::from_slice(&hex(PRIVATE_KEY_L1)).expect("L1"),
        }
    }
}

/// Контрольная сумма тела сообщения рукопожатия: сумма байт, u16 BE.
pub fn checksum(body: &[u8]) -> [u8; 2] {
    let sum: u32 = body.iter().map(|&b| u32::from(b)).sum();
    (sum as u16).to_be_bytes()
}

/// `** <body> <checksum>`.
pub fn kex_frame(body: &[u8]) -> Vec<u8> {
    let mut f = KEX_MAGIC.to_vec();
    f.extend_from_slice(body);
    f.extend_from_slice(&checksum(body));
    f
}

/// Тело сообщения рукопожатия (`body[0]` — тип, `body[1]` — длина данных, дальше данные), если кадр им является.
pub fn kex_body(frame: &[u8]) -> Option<&[u8]> {
    if frame.len() >= 6 && frame[..2] == KEX_MAGIC { Some(&frame[2..frame.len() - 2]) } else { None }
}

pub fn md5(data: &[u8]) -> [u8; 16] {
    Md5::digest(data).into()
}

/// AES-CBC без паддинга; длина ключа выбирает AES-128 или AES-256. `data` кратно 16.
pub fn aes_cbc_encrypt(key: &[u8], iv: &[u8; 16], data: &[u8]) -> Vec<u8> {
    match key.len() {
        16 => cbc::Encryptor::<aes::Aes128>::new_from_slices(key, iv)
            .expect("key")
            .encrypt_padded_vec_mut::<NoPadding>(data),
        32 => cbc::Encryptor::<aes::Aes256>::new_from_slices(key, iv)
            .expect("key")
            .encrypt_padded_vec_mut::<NoPadding>(data),
        _ => unreachable!("ключ AES 16 или 32 байта"),
    }
}

pub fn aes_cbc_decrypt(key: &[u8], iv: &[u8; 16], data: &[u8]) -> Result<Vec<u8>, CryptoError> {
    if !data.len().is_multiple_of(AES_BLOCK) {
        return Err(CryptoError::Unaligned);
    }
    let r = match key.len() {
        16 => cbc::Decryptor::<aes::Aes128>::new_from_slices(key, iv)
            .expect("key")
            .decrypt_padded_vec_mut::<NoPadding>(data),
        32 => cbc::Decryptor::<aes::Aes256>::new_from_slices(key, iv)
            .expect("key")
            .decrypt_padded_vec_mut::<NoPadding>(data),
        _ => unreachable!("ключ AES 16 или 32 байта"),
    };
    r.map_err(|_| CryptoError::Unaligned)
}

fn pad(data: &[u8]) -> Vec<u8> {
    let mut v = data.to_vec();
    v.resize(data.len().div_ceil(AES_BLOCK) * AES_BLOCK, 0);
    v
}

/// Кадр с фиксированным IV (рукопожатие): `len BE ‖ AES(key, iv, data + нули)`.
pub fn encrypt_frame_fixed(data: &[u8], key: &[u8], iv: &[u8; 16]) -> Vec<u8> {
    let mut f = (data.len() as u16).to_be_bytes().to_vec();
    f.extend(aes_cbc_encrypt(key, iv, &pad(data)));
    f
}

/// Кадр сессии: `len BE ‖ seed4 ‖ AES(key, MD5(seed), data + нули)`.
pub fn encrypt_frame_seeded(data: &[u8], key: &[u8], seed: [u8; 4]) -> Vec<u8> {
    let mut f = (data.len() as u16).to_be_bytes().to_vec();
    f.extend_from_slice(&seed);
    f.extend(aes_cbc_encrypt(key, &md5(&seed), &pad(data)));
    f
}

/// Обратное к `encrypt_frame_fixed` (`iv = Some`) или `encrypt_frame_seeded` (`iv = None`).
pub fn decrypt_frame(frame: &[u8], key: &[u8], iv: Option<&[u8; 16]>) -> Result<Vec<u8>, CryptoError> {
    if frame.len() < 2 {
        return Err(CryptoError::BadLength);
    }
    let len = u16::from_be_bytes([frame[0], frame[1]]) as usize;
    let (iv, body) = match iv {
        Some(iv) => (*iv, &frame[2..]),
        None => {
            if frame.len() < 6 {
                return Err(CryptoError::BadLength);
            }
            (md5(&frame[2..6]), &frame[6..])
        }
    };
    let mut plain = aes_cbc_decrypt(key, &iv, body)?;
    if plain.len() < len {
        return Err(CryptoError::BadLength);
    }
    plain.truncate(len);
    Ok(plain)
}

/// Полная длина зашифрованного кадра по заголовку, если заголовок уже пришёл.
pub fn frame_len(buf: &[u8], seeded: bool) -> Option<usize> {
    if buf.len() < 2 {
        return None;
    }
    let len = u16::from_be_bytes([buf[0], buf[1]]) as usize;
    Some(if seeded { 6 } else { 2 } + len.div_ceil(AES_BLOCK) * AES_BLOCK)
}

/// Публичный ключ P-256 как 64 байта `x ‖ y`.
pub fn pubkey_bytes(pk: &PublicKey) -> [u8; 64] {
    let p = pk.to_encoded_point(false);
    let mut out = [0u8; 64];
    out.copy_from_slice(&p.as_bytes()[1..65]);
    out
}

pub fn pubkey_from_bytes(xy: &[u8]) -> Result<PublicKey, CryptoError> {
    let mut sec1 = vec![0x04];
    sec1.extend_from_slice(xy);
    PublicKey::from_sec1_bytes(&sec1).map_err(|_| CryptoError::BadKey)
}

/// Что сделать с уведомлением станции.
#[derive(Debug, PartialEq, Eq)]
pub enum Step {
    /// Отправить станции этот кадр (шаг рукопожатия).
    Send(Vec<u8>),
    /// Служебное сообщение обработано, отправлять нечего.
    Nothing,
    /// Кадр ещё не собран полностью.
    Partial,
    /// Расшифрованные данные приложения (ответ Modbus).
    Data(Vec<u8>),
}

/// Сессия шифрования одного подключения.
pub struct Session {
    keys: Keys,
    iv0: Option<[u8; 16]>,
    k0: Option<[u8; 16]>,
    secret: Option<EphemeralSecret>,
    peer: Option<PublicKey>,
    key: Option<[u8; 32]>,
    buf: Vec<u8>,
    /// Был ли challenge — то есть станция шифрует.
    pub started: bool,
}

impl Session {
    pub fn new(keys: Keys) -> Self {
        Self {
            keys,
            iv0: None,
            k0: None,
            secret: None,
            peer: None,
            key: None,
            buf: Vec::new(),
            started: false,
        }
    }

    pub fn is_ready(&self) -> bool {
        self.key.is_some()
    }

    /// Сбросить недособранный кадр (перед новой командой).
    pub fn clear_buffer(&mut self) {
        self.buf.clear();
    }

    pub fn encrypt_command(&self, cmd: &[u8], rng: &mut impl CryptoRngCore) -> Result<Vec<u8>, CryptoError> {
        let key = self.key.ok_or(CryptoError::NoKey)?;
        let mut seed = [0u8; 4];
        rng.fill_bytes(&mut seed);
        Ok(encrypt_frame_seeded(cmd, &key, seed))
    }

    pub fn on_notify(&mut self, data: &[u8], rng: &mut impl CryptoRngCore) -> Result<Step, CryptoError> {
        if let Some(body) = kex_body(data) {
            // Открытое сообщение рукопожатия.
            return match body.first() {
                Some(&MSG_CHALLENGE) => self.on_challenge(&body[2..]),
                Some(&MSG_CHALLENGE_ACCEPTED) => Ok(Step::Nothing),
                _ => Ok(Step::Nothing),
            };
        }
        let (k0, iv0) = match (self.k0, self.iv0) {
            (Some(k), Some(iv)) => (k, iv),
            _ => return Err(CryptoError::NoKey),
        };
        self.buf.extend_from_slice(data);
        let seeded = self.key.is_some();
        let need = match frame_len(&self.buf, seeded) {
            Some(n) => n,
            None => return Ok(Step::Partial),
        };
        if self.buf.len() < need {
            return Ok(Step::Partial);
        }
        let frame: Vec<u8> = self.buf.drain(..need).collect();
        let plain = match self.key {
            Some(key) => decrypt_frame(&frame, &key, None),
            None => decrypt_frame(&frame, &k0, Some(&iv0)),
        };
        let plain = match plain {
            Ok(p) => p,
            Err(e) => {
                self.buf.clear();
                return Err(e);
            }
        };
        if let Some(body) = kex_body(&plain) {
            return match body.first() {
                Some(&MSG_PEER_PUBKEY) => self.on_peer_pubkey(&body[2..], rng),
                Some(&MSG_PUBKEY_ACCEPTED) => self.on_key_accepted(&body[2..]),
                _ => Ok(Step::Nothing),
            };
        }
        Ok(Step::Data(plain))
    }

    fn on_challenge(&mut self, data: &[u8]) -> Result<Step, CryptoError> {
        if data.len() != 4 {
            return Err(CryptoError::BadLength);
        }
        let rev: Vec<u8> = data.iter().rev().copied().collect();
        let iv0 = md5(&rev);
        let mut k0 = [0u8; 16];
        for i in 0..16 {
            k0[i] = iv0[i] ^ LOCAL_AES_KEY[i];
        }
        self.iv0 = Some(iv0);
        self.k0 = Some(k0);
        self.key = None;
        self.started = true;
        let mut body = vec![0x02, 0x04];
        body.extend_from_slice(&iv0[8..12]);
        Ok(Step::Send(kex_frame(&body)))
    }

    fn on_peer_pubkey(&mut self, data: &[u8], rng: &mut impl CryptoRngCore) -> Result<Step, CryptoError> {
        if data.len() != 128 {
            return Err(CryptoError::BadLength);
        }
        let (k0, iv0) = (self.k0.ok_or(CryptoError::NoKey)?, self.iv0.ok_or(CryptoError::NoKey)?);
        let (peer_xy, sig) = data.split_at(64);
        let mut signed = peer_xy.to_vec();
        signed.extend_from_slice(&iv0);
        let sig = Signature::from_slice(sig).map_err(|_| CryptoError::BadSignature)?;
        self.keys.station_verify.verify(&signed, &sig).map_err(|_| CryptoError::BadSignature)?;
        self.peer = Some(pubkey_from_bytes(peer_xy)?);

        let secret = EphemeralSecret::random(rng);
        let my_xy = pubkey_bytes(&secret.public_key());
        self.secret = Some(secret);
        let mut to_sign = my_xy.to_vec();
        to_sign.extend_from_slice(&iv0);
        let my_sig: Signature = self.keys.client_sign.sign(&to_sign);

        let mut body = vec![0x05, 0x80];
        body.extend_from_slice(&my_xy);
        body.extend_from_slice(&my_sig.to_bytes());
        Ok(Step::Send(encrypt_frame_fixed(&kex_frame(&body), &k0, &iv0)))
    }

    fn on_key_accepted(&mut self, data: &[u8]) -> Result<Step, CryptoError> {
        if data.len() != 1 {
            return Err(CryptoError::BadLength);
        }
        if data[0] != 0 {
            return Err(CryptoError::KeyRejected);
        }
        let (secret, peer) = match (self.secret.take(), self.peer.as_ref()) {
            (Some(s), Some(p)) => (s, p),
            _ => return Err(CryptoError::NoKey),
        };
        let shared = secret.diffie_hellman(peer);
        let mut key = [0u8; 32];
        key.copy_from_slice(shared.raw_secret_bytes());
        self.key = Some(key);
        Ok(Step::Nothing)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn bluetti_keys_parse() {
        let _ = Keys::bluetti();
    }

    #[test]
    fn checksum_is_byte_sum_be() {
        assert_eq!(checksum(&[0x02, 0x04, 0xFF, 0xFF]), [0x02, 0x04]);
    }

    #[test]
    fn seeded_frame_round_trip() {
        let key = [7u8; 32];
        let f = encrypt_frame_seeded(b"hello modbus", &key, [1, 2, 3, 4]);
        assert_eq!(f.len(), 2 + 4 + 16);
        assert_eq!(frame_len(&f, true), Some(f.len()));
        assert_eq!(decrypt_frame(&f, &key, None).unwrap(), b"hello modbus");
    }

    #[test]
    fn fixed_frame_round_trip() {
        let key = [9u8; 16];
        let iv = [3u8; 16];
        let f = encrypt_frame_fixed(&[0xAB; 33], &key, &iv);
        assert_eq!(frame_len(&f, false), Some(2 + 48));
        assert_eq!(decrypt_frame(&f, &key, Some(&iv)).unwrap(), vec![0xAB; 33]);
    }
}
