//! Чтение станции по BLE: транспорт, рукопожатие, блоки Modbus → `Reading`.

pub mod ac180p;
pub mod crypto;
pub mod modbus;
#[cfg(windows)]
pub mod scan;
#[cfg(windows)]
pub mod winrt;

use std::time::Duration;

use rand::rngs::OsRng;
use tokio::sync::mpsc::UnboundedReceiver;
use tokio::time::{Instant, timeout_at};

use crate::clock::Ts;
use crate::model::{MissReason, Reading};
use crypto::{Keys, Session, Step};

/// GATT-канал до станции. Реализации: WinRT и фейковая станция в тестах.
#[allow(async_fn_in_trait)]
pub trait Transport {
    /// Подключиться и подписаться на уведомления; уведомления приходят в канал.
    async fn connect(&mut self) -> Result<UnboundedReceiver<Vec<u8>>, MissReason>;
    async fn write(&mut self, data: &[u8]) -> Result<(), MissReason>;
    async fn disconnect(&mut self);
}

#[derive(Debug, Clone)]
pub struct ReadOptions {
    /// Известно ли с прошлого раза, что станция шифрует.
    pub encryption_hint: Option<bool>,
    /// Сколько ждать challenge после подписки.
    pub challenge_wait: Duration,
    /// Сколько ждать завершения рукопожатия.
    pub handshake_timeout: Duration,
    /// Сколько ждать ответа на один блок.
    pub answer_timeout: Duration,
    /// Общий предел цикла.
    pub total_timeout: Duration,
}

impl Default for ReadOptions {
    fn default() -> Self {
        Self {
            encryption_hint: None,
            challenge_wait: Duration::from_secs(3),
            handshake_timeout: Duration::from_secs(10),
            answer_timeout: Duration::from_secs(5),
            total_timeout: Duration::from_secs(30),
        }
    }
}

#[derive(Debug, Clone, PartialEq)]
pub struct ReadOutcome {
    pub reading: Reading,
    pub encrypted: bool,
}

fn hex(b: &[u8]) -> String {
    b.iter().map(|x| format!("{x:02x}")).collect()
}

/// Одно чтение: подключение, рукопожатие (если станция шифрует), три блока, отключение — всегда.
pub async fn read_station<T: Transport>(
    t: &mut T,
    keys: &Keys,
    opts: &ReadOptions,
    ts: Ts,
) -> Result<ReadOutcome, MissReason> {
    let deadline = Instant::now() + opts.total_timeout;
    let r = match timeout_at(deadline, read_inner(t, keys, opts, ts)).await {
        Ok(r) => r,
        Err(_) => Err(MissReason::Timeout),
    };
    t.disconnect().await;
    r
}

async fn read_inner<T: Transport>(
    t: &mut T,
    keys: &Keys,
    opts: &ReadOptions,
    ts: Ts,
) -> Result<ReadOutcome, MissReason> {
    let mut rx = t.connect().await?;
    let mut session = Session::new(keys.clone());
    let mut rng = OsRng;

    // Шифрует ли станция: ждём challenge. Подсказка «не шифрует» сокращает ожидание.
    let wait =
        if opts.encryption_hint == Some(false) { Duration::from_millis(500) } else { opts.challenge_wait };
    let first = match tokio::time::timeout(wait, rx.recv()).await {
        Ok(Some(d)) => Some(d),
        Ok(None) => return Err(MissReason::NotFound),
        Err(_) => None,
    };
    let encrypted = first.as_deref().is_some_and(|d| crypto::kex_body(d).is_some());

    if encrypted {
        let hs_deadline = Instant::now() + opts.handshake_timeout;
        let mut pending = first;
        while !session.is_ready() {
            let data = match pending.take() {
                Some(d) => d,
                None => match timeout_at(hs_deadline, rx.recv()).await {
                    Ok(Some(d)) => d,
                    Ok(None) => return Err(MissReason::NotFound),
                    Err(_) => return Err(MissReason::Timeout),
                },
            };
            tracing::debug!(target: "frames", "<- {}", hex(&data));
            match session.on_notify(&data, &mut rng) {
                Ok(Step::Send(frame)) => {
                    tracing::debug!(target: "frames", "-> {}", hex(&frame));
                    t.write(&frame).await?;
                }
                Ok(_) => {}
                Err(e) => {
                    tracing::warn!("рукопожатие: {e}");
                    return Err(MissReason::CryptoError);
                }
            }
        }
        tracing::debug!("защищённое соединение установлено");
    }

    let mut blocks = Vec::new();
    for (addr, count) in ac180p::BLOCKS {
        let cmd = modbus::read_request(addr, count);
        tracing::debug!(target: "frames", "-> modbus {}", hex(&cmd));
        let frame = if encrypted {
            session.clear_buffer();
            session.encrypt_command(&cmd, &mut rng).map_err(|_| MissReason::CryptoError)?
        } else {
            cmd
        };
        t.write(&frame).await?;
        let resp = receive_response(&mut rx, &mut session, encrypted, count, opts.answer_timeout).await?;
        tracing::debug!(target: "frames", "<- modbus {}", hex(&resp));
        match modbus::parse_read_response(&resp, count) {
            Ok(vals) => blocks.push((addr, vals)),
            Err(modbus::ModbusError::Exception(code)) => {
                tracing::warn!("блок {addr}×{count}: исключение Modbus {code:#04x}, поля останутся пустыми");
            }
            Err(e) => {
                tracing::warn!("блок {addr}×{count}: {e}");
                return Err(MissReason::ModbusError);
            }
        }
    }
    Ok(ReadOutcome { reading: ac180p::to_reading(ts, &blocks), encrypted })
}

async fn receive_response(
    rx: &mut UnboundedReceiver<Vec<u8>>,
    session: &mut Session,
    encrypted: bool,
    count: u16,
    wait: Duration,
) -> Result<Vec<u8>, MissReason> {
    let deadline = Instant::now() + wait;
    let mut buf = Vec::new();
    let mut rng = OsRng;
    loop {
        let data = match timeout_at(deadline, rx.recv()).await {
            Ok(Some(d)) => d,
            Ok(None) => return Err(MissReason::NotFound),
            Err(_) => return Err(MissReason::Timeout),
        };
        if encrypted {
            tracing::debug!(target: "frames", "<- {}", hex(&data));
            match session.on_notify(&data, &mut rng) {
                Ok(Step::Data(plain)) => return Ok(plain),
                Ok(_) => continue,
                Err(e) => {
                    tracing::warn!("расшифровка ответа: {e}");
                    return Err(MissReason::CryptoError);
                }
            }
        }
        buf.extend_from_slice(&data);
        if buf.len() >= modbus::expected_len(&buf, count) {
            return Ok(buf);
        }
    }
}
