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

use modbus::FrameStart;

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

/// Запросы, ответ на которые ещё не пришёл, — в том числе из прошлых чтений: переподключение может
/// застать тот же радиоканал, и поздний ответ придёт уже в следующем опросе. Блоки 100×10 и 140×10 отвечают
/// кадрами одной длины, так что без этого поздний ответ на один блок прошёл бы CRC и лёг в поля другого.
#[derive(Debug, Default)]
pub struct Unanswered(Vec<(u16, u16, Instant)>);

/// Чей ответ пришёл, пока ждали запрос `addr`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum Claim {
    /// Подходит только к нашему запросу.
    Ours,
    /// Мог прийти и на старый запрос другого блока той же длины: старый вычеркнут, наш надо повторить.
    Ambiguous,
    /// Нашему запросу не подходит: поздний ответ на старый (вычеркнут) или чужой кадр.
    Stray,
}

impl Unanswered {
    /// Дольше этого поздний ответ не ждём.
    const KEEP: Duration = Duration::from_secs(120);
    const MAX: usize = 8;

    fn sent(&mut self, addr: u16, count: u16) {
        let now = Instant::now();
        self.0.retain(|e| now.duration_since(e.2) < Self::KEEP);
        if self.0.len() >= Self::MAX {
            self.0.remove(0);
        }
        self.0.push((addr, count, now));
    }

    fn take(&mut self, pick: impl Fn(&(u16, u16, Instant)) -> bool) -> bool {
        match self.0.iter().position(pick) {
            Some(i) => {
                self.0.remove(i);
                true
            }
            None => false,
        }
    }

    fn claim(&mut self, frame: &[u8], addr: u16, count: u16) -> Claim {
        let exception = modbus::is_exception(frame);
        let fits = |c: u16| exception || frame.len() == modbus::response_len(c);
        if !fits(count) {
            self.take(|e| fits(e.1));
            return Claim::Stray;
        }
        if self.take(|e| e.0 != addr && fits(e.1)) {
            return Claim::Ambiguous;
        }
        self.take(|e| e.0 == addr && e.1 == count);
        Claim::Ours
    }
}

/// Сколько раз переспросить блок, если ответ мог быть поздним ответом на другой.
const MAX_REASK: u32 = 2;

/// Одно чтение: подключение, рукопожатие (если станция шифрует), три блока, отключение — всегда.
pub async fn read_station<T: Transport>(
    t: &mut T,
    keys: &Keys,
    opts: &ReadOptions,
    ts: Ts,
) -> Result<ReadOutcome, MissReason> {
    read_station_tracked(t, keys, opts, ts, &mut Unanswered::default()).await
}

/// То же, но с журналом неотвеченных запросов между чтениями (сервис держит его всё время работы).
pub async fn read_station_tracked<T: Transport>(
    t: &mut T,
    keys: &Keys,
    opts: &ReadOptions,
    ts: Ts,
    unanswered: &mut Unanswered,
) -> Result<ReadOutcome, MissReason> {
    let deadline = Instant::now() + opts.total_timeout;
    let r = match timeout_at(deadline, read_inner(t, keys, opts, ts, unanswered)).await {
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
    unanswered: &mut Unanswered,
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
    let mut buf = Vec::new();
    for (addr, count) in ac180p::BLOCKS {
        let mut asks = 0;
        let resp = 'block: loop {
            let cmd = modbus::read_request(addr, count);
            tracing::debug!(target: "frames", "-> modbus {}", hex(&cmd));
            let frame = if encrypted {
                session.clear_buffer();
                session.encrypt_command(&cmd, &mut rng).map_err(|_| MissReason::CryptoError)?
            } else {
                cmd
            };
            t.write(&frame).await?;
            unanswered.sent(addr, count);
            let deadline = Instant::now() + opts.answer_timeout;
            loop {
                let resp = next_frame(&mut rx, &mut session, encrypted, &mut buf, deadline).await?;
                tracing::debug!(target: "frames", "<- modbus {}", hex(&resp));
                match unanswered.claim(&resp, addr, count) {
                    Claim::Ours => break 'block resp,
                    Claim::Stray => tracing::debug!("блок {addr}×{count}: лишний кадр пропущен"),
                    Claim::Ambiguous if asks < MAX_REASK => {
                        asks += 1;
                        tracing::info!(
                            "блок {addr}×{count}: ответ мог быть поздним ответом на другой блок, повтор"
                        );
                        continue 'block;
                    }
                    Claim::Ambiguous => {
                        tracing::warn!(
                            "блок {addr}×{count}: ответы остаются неоднозначными, чтение не принято"
                        );
                        return Err(MissReason::ModbusError);
                    }
                }
            }
        };
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

/// Следующий кадр-ответ из уведомлений: длина по заголовку, хвост остаётся в `buf` до следующего вызова.
async fn next_frame(
    rx: &mut UnboundedReceiver<Vec<u8>>,
    session: &mut Session,
    encrypted: bool,
    buf: &mut Vec<u8>,
    deadline: Instant,
) -> Result<Vec<u8>, MissReason> {
    let mut rng = OsRng;
    loop {
        match modbus::frame_start(buf) {
            FrameStart::Len(n) if buf.len() >= n => return Ok(buf.drain(..n).collect()),
            FrameStart::Junk => {
                tracing::debug!(target: "frames", "<- не ответ, пропущено: {}", hex(buf));
                buf.clear();
            }
            _ => {}
        }
        let data = match timeout_at(deadline, rx.recv()).await {
            Ok(Some(d)) => d,
            Ok(None) => return Err(MissReason::NotFound),
            Err(_) => return Err(MissReason::Timeout),
        };
        if encrypted {
            tracing::debug!(target: "frames", "<- {}", hex(&data));
            match session.on_notify(&data, &mut rng) {
                Ok(Step::Data(plain)) => buf.extend_from_slice(&plain),
                Ok(_) => {}
                Err(e) => {
                    tracing::warn!("расшифровка ответа: {e}");
                    return Err(MissReason::CryptoError);
                }
            }
        } else {
            buf.extend_from_slice(&data);
        }
    }
}
