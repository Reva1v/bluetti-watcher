//! Протокол BLUETTI против фейковой станции: рукопожатие, шифрованное чтение, склейка фрагментов
//! и инвариант «только чтение» (конституция, принцип I).

use std::collections::HashMap;

use bluetti_watcher::ble::crypto::{
    self, Keys, decrypt_frame, encrypt_frame_fixed, encrypt_frame_seeded, kex_body, kex_frame, md5,
    pubkey_bytes, pubkey_from_bytes,
};
use bluetti_watcher::ble::{ReadOptions, Transport, modbus, read_station};
use bluetti_watcher::model::MissReason;
use p256::ecdh::EphemeralSecret;
use p256::ecdsa::signature::{Signer, Verifier};
use p256::ecdsa::{Signature, SigningKey, VerifyingKey};
use rand::rngs::OsRng;
use tokio::sync::mpsc::{UnboundedReceiver, UnboundedSender, unbounded_channel};

#[derive(PartialEq, Debug)]
enum Phase {
    Plain,
    AwaitChallengeResp,
    AwaitClientKey,
    Ready,
}

/// Что станция получила: сырые байты и, если удалось, расшифрованный открытый текст.
#[derive(Debug)]
struct Outgoing {
    raw: Vec<u8>,
    plain: Option<Vec<u8>>,
}

struct FakeStation {
    encrypted: bool,
    station_sign: SigningKey,
    client_verify: VerifyingKey,
    /// Ломает подпись станции (проверка отказа).
    bad_signature: bool,
    fragment: usize,
    regs: HashMap<u16, u16>,
    phase: Phase,
    challenge: [u8; 4],
    k0: [u8; 16],
    iv0: [u8; 16],
    secret: Option<EphemeralSecret>,
    key: Option<[u8; 32]>,
    tx: Option<UnboundedSender<Vec<u8>>>,
    sent: Vec<Outgoing>,
    disconnected: bool,
}

impl FakeStation {
    fn new(encrypted: bool, station_sign: SigningKey, client_verify: VerifyingKey) -> Self {
        let mut regs = HashMap::new();
        regs.insert(102, 64);
        regs.insert(140, 4);
        regs.insert(142, 182);
        regs.insert(144, 0);
        regs.insert(146, 0);
        regs.insert(1314, 0);
        Self {
            encrypted,
            station_sign,
            client_verify,
            bad_signature: false,
            fragment: 20,
            regs,
            phase: Phase::Plain,
            challenge: [0x11, 0x22, 0x33, 0x44],
            k0: [0; 16],
            iv0: [0; 16],
            secret: None,
            key: None,
            tx: None,
            sent: Vec::new(),
            disconnected: false,
        }
    }

    fn notify(&self, data: &[u8]) {
        let tx = self.tx.as_ref().unwrap();
        for chunk in data.chunks(self.fragment) {
            tx.send(chunk.to_vec()).unwrap();
        }
    }

    fn modbus_response(&self, req: &[u8]) -> Vec<u8> {
        assert!(modbus::is_read_request(req), "станция получила не чтение: {req:02x?}");
        let addr = u16::from_be_bytes([req[2], req[3]]);
        let count = u16::from_be_bytes([req[4], req[5]]);
        let mut r = vec![0x01, 0x03, (count * 2) as u8];
        for a in addr..addr + count {
            r.extend_from_slice(&self.regs.get(&a).copied().unwrap_or(0).to_be_bytes());
        }
        let crc = modbus::crc16(&r);
        r.extend_from_slice(&crc.to_le_bytes());
        r
    }
}

impl Transport for FakeStation {
    async fn connect(&mut self) -> Result<UnboundedReceiver<Vec<u8>>, MissReason> {
        let (tx, rx) = unbounded_channel();
        self.tx = Some(tx);
        if self.encrypted {
            self.phase = Phase::AwaitChallengeResp;
            let mut body = vec![0x01, 0x04];
            body.extend_from_slice(&self.challenge);
            self.notify(&kex_frame(&body));
        }
        Ok(rx)
    }

    async fn write(&mut self, data: &[u8]) -> Result<(), MissReason> {
        match self.phase {
            Phase::Plain => {
                self.sent.push(Outgoing { raw: data.to_vec(), plain: Some(data.to_vec()) });
                let resp = self.modbus_response(data);
                self.notify(&resp);
            }
            Phase::AwaitChallengeResp => {
                self.sent.push(Outgoing { raw: data.to_vec(), plain: None });
                let rev: Vec<u8> = self.challenge.iter().rev().copied().collect();
                self.iv0 = md5(&rev);
                let local = [
                    0x45, 0x9F, 0xC5, 0x35, 0x80, 0x89, 0x41, 0xF1, 0x70, 0x91, 0xE0, 0x99, 0x3E, 0xE3, 0xE9,
                    0x3D,
                ];
                for ((k, v), l) in self.k0.iter_mut().zip(self.iv0).zip(local) {
                    *k = v ^ l;
                }
                let body = kex_body(data).expect("ответ на challenge — открытое сообщение");
                assert_eq!(body, [&[0x02, 0x04][..], &self.iv0[8..12]].concat());
                self.notify(&kex_frame(&[0x03, 0x00]));

                let secret = EphemeralSecret::random(&mut OsRng);
                let xy = pubkey_bytes(&secret.public_key());
                self.secret = Some(secret);
                let mut signed = xy.to_vec();
                signed.extend_from_slice(&self.iv0);
                let sig: Signature = self.station_sign.sign(&signed);
                let mut sig = sig.to_bytes().to_vec();
                if self.bad_signature {
                    sig[10] ^= 1;
                }
                let mut body = vec![0x04, 0x80];
                body.extend_from_slice(&xy);
                body.extend_from_slice(&sig);
                let frame = encrypt_frame_fixed(&kex_frame(&body), &self.k0, &self.iv0);
                self.phase = Phase::AwaitClientKey;
                self.notify(&frame);
            }
            Phase::AwaitClientKey => {
                let plain = decrypt_frame(data, &self.k0, Some(&self.iv0)).expect("ключ клиента");
                self.sent.push(Outgoing { raw: data.to_vec(), plain: Some(plain.clone()) });
                let body = kex_body(&plain).expect("ключ клиента — сообщение рукопожатия");
                assert_eq!(&body[..2], &[0x05, 0x80]);
                let (xy, sig) = body[2..].split_at(64);
                let mut signed = xy.to_vec();
                signed.extend_from_slice(&self.iv0);
                self.client_verify
                    .verify(&signed, &Signature::from_slice(sig).unwrap())
                    .expect("подпись клиента");
                let peer = pubkey_from_bytes(xy).unwrap();
                let shared = self.secret.take().unwrap().diffie_hellman(&peer);
                let mut key = [0u8; 32];
                key.copy_from_slice(shared.raw_secret_bytes());
                self.key = Some(key);
                self.phase = Phase::Ready;
                let frame = encrypt_frame_fixed(&kex_frame(&[0x06, 0x01, 0x00]), &self.k0, &self.iv0);
                self.notify(&frame);
            }
            Phase::Ready => {
                let key = self.key.unwrap();
                let req = decrypt_frame(data, &key, None).expect("команда");
                self.sent.push(Outgoing { raw: data.to_vec(), plain: Some(req.clone()) });
                let resp = self.modbus_response(&req);
                let frame = encrypt_frame_seeded(&resp, &key, [9, 8, 7, 6]);
                self.notify(&frame);
            }
        }
        Ok(())
    }

    async fn disconnect(&mut self) {
        self.disconnected = true;
        self.tx = None;
    }
}

fn test_keys() -> (Keys, SigningKey, VerifyingKey) {
    let station = SigningKey::random(&mut OsRng);
    let client = SigningKey::random(&mut OsRng);
    let keys = Keys { station_verify: *station.verifying_key(), client_sign: client.clone() };
    (keys, station, *client.verifying_key())
}

fn fast_opts() -> ReadOptions {
    ReadOptions { challenge_wait: std::time::Duration::from_millis(200), ..ReadOptions::default() }
}

#[tokio::test]
async fn encrypted_read_end_to_end() {
    let (keys, station_sign, client_verify) = test_keys();
    let mut st = FakeStation::new(true, station_sign, client_verify);
    let out = read_station(&mut st, &keys, &fast_opts(), 42).await.expect("чтение");
    assert!(out.encrypted);
    assert_eq!(out.reading.ts, 42);
    assert_eq!(out.reading.soc, Some(64));
    assert_eq!(out.reading.ac_out_w, Some(182));
    assert_eq!(out.reading.dc_out_w, Some(4));
    assert_eq!(out.reading.ac_in_w, Some(0));
    assert!(st.disconnected);
}

#[tokio::test]
async fn plain_read_end_to_end() {
    let (keys, station_sign, client_verify) = test_keys();
    let mut st = FakeStation::new(false, station_sign, client_verify);
    st.fragment = 7;
    let out = read_station(&mut st, &keys, &fast_opts(), 1).await.expect("чтение");
    assert!(!out.encrypted);
    assert_eq!(out.reading.soc, Some(64));
    assert!(st.disconnected);
}

#[tokio::test]
async fn bad_station_signature_is_crypto_error() {
    let (keys, station_sign, client_verify) = test_keys();
    let mut st = FakeStation::new(true, station_sign, client_verify);
    st.bad_signature = true;
    let r = read_station(&mut st, &keys, &fast_opts(), 1).await;
    assert_eq!(r, Err(MissReason::CryptoError));
    assert!(st.disconnected);
}

#[tokio::test]
async fn real_bluetti_constants_reject_foreign_station() {
    let (_, station_sign, client_verify) = test_keys();
    let mut st = FakeStation::new(true, station_sign, client_verify);
    let r = read_station(&mut st, &Keys::bluetti(), &fast_opts(), 1).await;
    assert_eq!(r, Err(MissReason::CryptoError));
}

/// Принцип I: станция получает только шаги рукопожатия и запросы чтения 0x03.
#[tokio::test]
async fn only_handshake_and_read_frames_go_out() {
    for encrypted in [true, false] {
        let (keys, station_sign, client_verify) = test_keys();
        let mut st = FakeStation::new(encrypted, station_sign, client_verify);
        read_station(&mut st, &keys, &fast_opts(), 1).await.expect("чтение");
        assert!(!st.sent.is_empty());
        for o in &st.sent {
            let ok = crypto::kex_body(&o.raw).is_some()
                || o.plain
                    .as_deref()
                    .is_some_and(|p| crypto::kex_body(p).is_some() || modbus::is_read_request(p));
            assert!(ok, "запрещённый кадр: {:02x?}", o.raw);
        }
    }
}
