//! SQLite `station.db` (data-model.md). Время — мс UTC.

use std::path::Path;

use rusqlite::{Connection, OpenFlags, OptionalExtension, params};
use serde::{Serialize, de::DeserializeOwned};

use crate::clock::{MIN, Ts};
use crate::model::{ReadStatus, Reading};

pub const RETENTION: Ts = 365 * 24 * 60 * MIN;

const SCHEMA: &str = "
CREATE TABLE IF NOT EXISTS readings (
    ts INTEGER PRIMARY KEY,
    soc INTEGER NULL CHECK (soc BETWEEN 0 AND 100),
    ac_in_w INTEGER NULL, ac_out_w INTEGER NULL, dc_in_w INTEGER NULL, dc_out_w INTEGER NULL,
    ac_in_v REAL NULL,
    status TEXT NOT NULL CHECK (status IN ('accepted','rejected','rebased','miss')),
    reason TEXT NULL
);
CREATE TABLE IF NOT EXISTS outages (
    id INTEGER PRIMARY KEY,
    start_ts INTEGER NOT NULL,
    end_ts INTEGER NULL,
    soc_start INTEGER NULL, soc_end INTEGER NULL,
    eco INTEGER NOT NULL DEFAULT 0,
    hibernated INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS outages_start ON outages(start_ts);
CREATE TABLE IF NOT EXISTS events (
    id INTEGER PRIMARY KEY,
    ts INTEGER NOT NULL,
    kind TEXT NOT NULL,
    text TEXT NOT NULL,
    data TEXT NULL
);
CREATE INDEX IF NOT EXISTS events_ts ON events(ts);
CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
";

#[derive(Debug, Clone, PartialEq, Serialize)]
pub struct Event {
    pub ts: Ts,
    pub kind: String,
    pub text: String,
}

#[derive(Debug, Clone, PartialEq, Serialize)]
pub struct Outage {
    pub id: i64,
    pub start_ts: Ts,
    pub end_ts: Option<Ts>,
    pub soc_start: Option<u8>,
    pub soc_end: Option<u8>,
    pub eco: bool,
    pub hibernated: bool,
}

/// Период графиков `/api/history`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Range {
    H6,
    H24,
    D7,
    D30,
}

impl Range {
    pub fn parse(s: &str) -> Option<Self> {
        match s {
            "6h" => Some(Self::H6),
            "24h" => Some(Self::H24),
            "7d" => Some(Self::D7),
            "30d" => Some(Self::D30),
            _ => None,
        }
    }

    pub fn span(self) -> Ts {
        let h = 60 * MIN;
        match self {
            Self::H6 => 6 * h,
            Self::H24 => 24 * h,
            Self::D7 => 7 * 24 * h,
            Self::D30 => 30 * 24 * h,
        }
    }

    /// Шаг точек, с: 6h/24h — сырые показания (опрос раз в минуту), 7d/30d — средние по 5/15 мин.
    pub fn step(self) -> u32 {
        match self {
            Self::H6 | Self::H24 => 60,
            Self::D7 => 300,
            Self::D30 => 900,
        }
    }
}

/// Точка графика: `t` — unix-секунды, `in`/`out` — сумма AC и DC, Вт.
#[derive(Debug, Clone, PartialEq, Serialize)]
pub struct Point {
    pub t: i64,
    pub soc: Option<u8>,
    #[serde(rename = "in")]
    pub inp: Option<u32>,
    pub out: Option<u32>,
}

/// Отключение на графике, unix-секунды.
#[derive(Debug, Clone, PartialEq, Serialize)]
pub struct Span {
    pub start: i64,
    pub end: Option<i64>,
}

#[derive(Debug, Clone, PartialEq, Serialize)]
pub struct History {
    pub step: u32,
    pub points: Vec<Point>,
    pub outages: Vec<Span>,
}

pub struct Store {
    db: Connection,
}

impl Store {
    pub fn open(path: &Path) -> rusqlite::Result<Self> {
        Self::init(Connection::open(path)?)
    }

    /// Соединение только для чтения (HTTP-сервер): схему не трогает, БД должна уже существовать.
    pub fn open_read(path: &Path) -> rusqlite::Result<Self> {
        let db = Connection::open_with_flags(
            path,
            OpenFlags::SQLITE_OPEN_READ_ONLY | OpenFlags::SQLITE_OPEN_NO_MUTEX | OpenFlags::SQLITE_OPEN_URI,
        )?;
        db.busy_timeout(std::time::Duration::from_secs(2))?;
        Ok(Self { db })
    }

    pub fn in_memory() -> rusqlite::Result<Self> {
        Self::init(Connection::open_in_memory()?)
    }

    fn init(db: Connection) -> rusqlite::Result<Self> {
        db.pragma_update(None, "journal_mode", "WAL")?;
        db.pragma_update(None, "synchronous", "NORMAL")?;
        let version: i64 = db.pragma_query_value(None, "user_version", |r| r.get(0))?;
        if version < 1 {
            db.execute_batch(SCHEMA)?;
            db.pragma_update(None, "user_version", 1)?;
        }
        Ok(Self { db })
    }

    /// Пропуск без показания пишется с пустыми полями. Совпадение `ts` (повторное чтение в ту же мс) заменяет запись.
    pub fn insert_reading(
        &self,
        r: &Reading,
        status: ReadStatus,
        reason: Option<&str>,
    ) -> rusqlite::Result<()> {
        self.db.execute(
            "INSERT OR REPLACE INTO readings (ts, soc, ac_in_w, ac_out_w, dc_in_w, dc_out_w, ac_in_v, status, reason)
             VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9)",
            params![
                r.ts,
                r.soc,
                r.ac_in_w,
                r.ac_out_w,
                r.dc_in_w,
                r.dc_out_w,
                r.ac_in_v,
                status.as_str(),
                reason
            ],
        )?;
        Ok(())
    }

    pub fn insert_miss(&self, ts: Ts, reason: &str) -> rusqlite::Result<()> {
        self.insert_reading(&Reading { ts, ..Default::default() }, ReadStatus::Miss, Some(reason))
    }

    pub fn insert_event(&self, ts: Ts, kind: &str, text: &str, data: Option<&str>) -> rusqlite::Result<()> {
        self.db.execute(
            "INSERT INTO events (ts, kind, text, data) VALUES (?1, ?2, ?3, ?4)",
            params![ts, kind, text, data],
        )?;
        Ok(())
    }

    pub fn recent_events(&self, limit: u32) -> rusqlite::Result<Vec<Event>> {
        let mut st =
            self.db.prepare_cached("SELECT ts, kind, text FROM events ORDER BY ts DESC, id DESC LIMIT ?1")?;
        st.query_map([limit], |r| Ok(Event { ts: r.get(0)?, kind: r.get(1)?, text: r.get(2)? }))?.collect()
    }

    pub fn start_outage(&self, start_ts: Ts, soc: Option<u8>) -> rusqlite::Result<i64> {
        self.db
            .execute("INSERT INTO outages (start_ts, soc_start) VALUES (?1, ?2)", params![start_ts, soc])?;
        Ok(self.db.last_insert_rowid())
    }

    /// Закрывает последнее открытое отключение.
    pub fn end_outage(&self, end_ts: Ts, soc: Option<u8>) -> rusqlite::Result<()> {
        self.db.execute(
            "UPDATE outages SET end_ts = ?1, soc_end = ?2
             WHERE id = (SELECT id FROM outages WHERE end_ts IS NULL ORDER BY start_ts DESC LIMIT 1)",
            params![end_ts, soc],
        )?;
        Ok(())
    }

    /// Отметить в открытом отключении eco или гибернацию.
    pub fn mark_outage(&self, eco: bool, hibernated: bool) -> rusqlite::Result<()> {
        self.db.execute(
            "UPDATE outages SET eco = MAX(eco, ?1), hibernated = MAX(hibernated, ?2)
             WHERE id = (SELECT id FROM outages WHERE end_ts IS NULL ORDER BY start_ts DESC LIMIT 1)",
            params![eco, hibernated],
        )?;
        Ok(())
    }

    pub fn outages(&self, limit: u32) -> rusqlite::Result<Vec<Outage>> {
        let mut st = self.db.prepare_cached(
            "SELECT id, start_ts, end_ts, soc_start, soc_end, eco, hibernated FROM outages
             ORDER BY start_ts DESC LIMIT ?1",
        )?;
        st.query_map([limit], |r| {
            Ok(Outage {
                id: r.get(0)?,
                start_ts: r.get(1)?,
                end_ts: r.get(2)?,
                soc_start: r.get(3)?,
                soc_end: r.get(4)?,
                eco: r.get(5)?,
                hibernated: r.get(6)?,
            })
        })?
        .collect()
    }

    pub fn get_meta<T: DeserializeOwned>(&self, key: &str) -> Option<T> {
        let v: Option<String> = self
            .db
            .query_row("SELECT value FROM meta WHERE key = ?1", [key], |r| r.get(0))
            .optional()
            .ok()
            .flatten();
        v.and_then(|s| serde_json::from_str(&s).ok())
    }

    pub fn set_meta<T: Serialize>(&self, key: &str, value: &T) -> rusqlite::Result<()> {
        let s = serde_json::to_string(value).expect("json");
        self.db.execute("INSERT OR REPLACE INTO meta (key, value) VALUES (?1, ?2)", params![key, s])?;
        Ok(())
    }

    pub fn delete_meta(&self, key: &str) -> rusqlite::Result<()> {
        self.db.execute("DELETE FROM meta WHERE key = ?1", [key])?;
        Ok(())
    }

    /// Удаляет показания и события старше года.
    pub fn purge(&self, now: Ts) -> rusqlite::Result<usize> {
        let cut = now - RETENTION;
        let a = self.db.execute("DELETE FROM readings WHERE ts < ?1", [cut])?;
        let b = self.db.execute("DELETE FROM events WHERE ts < ?1", [cut])?;
        Ok(a + b)
    }

    /// Последнее принятое показание.
    pub fn last_accepted(&self) -> rusqlite::Result<Option<Reading>> {
        self.db
            .query_row(
                "SELECT ts, soc, ac_in_w, ac_out_w, dc_in_w, dc_out_w, ac_in_v FROM readings
                 WHERE status = 'accepted' ORDER BY ts DESC LIMIT 1",
                [],
                row_to_reading,
            )
            .optional()
    }

    /// Принятые показания за `[from, to]` по возрастанию времени.
    pub fn accepted_between(&self, from: Ts, to: Ts) -> rusqlite::Result<Vec<Reading>> {
        let mut st = self.db.prepare_cached(
            "SELECT ts, soc, ac_in_w, ac_out_w, dc_in_w, dc_out_w, ac_in_v FROM readings
             WHERE status = 'accepted' AND ts BETWEEN ?1 AND ?2 ORDER BY ts",
        )?;
        st.query_map([from, to], row_to_reading)?.collect()
    }

    /// Доля циклов с принятым показанием за `[from, to]` (SC-001); `None` при < 10 циклах.
    pub fn read_ok_ratio(&self, from: Ts, to: Ts) -> rusqlite::Result<Option<f64>> {
        let (all, ok): (i64, i64) = self.db.query_row(
            "SELECT COUNT(*), COALESCE(SUM(status IN ('accepted','rejected','rebased')), 0) FROM readings
             WHERE ts BETWEEN ?1 AND ?2",
            [from, to],
            |r| Ok((r.get(0)?, r.get(1)?)),
        )?;
        Ok((all >= 10).then(|| ok as f64 / all as f64))
    }

    /// История для графиков за `range` до `now`, только принятые показания. Сырые точки для 6h/24h, средние
    /// по корзинам для 7d/30d — всё агрегирует SQLite по диапазону первичного ключа `ts`.
    pub fn history(&self, range: Range, now: Ts) -> rusqlite::Result<History> {
        let from = now - range.span();
        let bucket: Ts = match range {
            Range::H6 | Range::H24 => 1,
            r => Ts::from(r.step()) * 1000,
        };
        let mut st = self.db.prepare_cached(
            "SELECT (ts / ?3) * ?3 AS b,
                    CAST(ROUND(AVG(soc)) AS INTEGER),
                    CAST(ROUND(AVG(COALESCE(ac_in_w, 0) + COALESCE(dc_in_w, 0))) AS INTEGER),
                    CAST(ROUND(AVG(COALESCE(ac_out_w, 0) + COALESCE(dc_out_w, 0))) AS INTEGER)
             FROM readings WHERE ts BETWEEN ?1 AND ?2 AND status = 'accepted'
             GROUP BY b ORDER BY b",
        )?;
        let points = st
            .query_map(params![from, now, bucket], |r| {
                Ok(Point { t: r.get::<_, i64>(0)? / 1000, soc: r.get(1)?, inp: r.get(2)?, out: r.get(3)? })
            })?
            .collect::<rusqlite::Result<_>>()?;
        let mut st = self.db.prepare_cached(
            "SELECT start_ts, end_ts FROM outages
             WHERE start_ts <= ?2 AND (end_ts IS NULL OR end_ts >= ?1) ORDER BY start_ts",
        )?;
        let outages = st
            .query_map([from, now], |r| {
                Ok(Span {
                    start: r.get::<_, i64>(0)? / 1000,
                    end: r.get::<_, Option<i64>>(1)?.map(|e| e / 1000),
                })
            })?
            .collect::<rusqlite::Result<_>>()?;
        Ok(History { step: range.step(), points, outages })
    }

    /// Пакетная вставка в одной транзакции (тесты и импорт).
    pub fn insert_many(&mut self, rows: &[(Reading, ReadStatus)]) -> rusqlite::Result<()> {
        let tx = self.db.transaction()?;
        {
            let mut st = tx.prepare_cached(
                "INSERT OR REPLACE INTO readings (ts, soc, ac_in_w, ac_out_w, dc_in_w, dc_out_w, ac_in_v, status)
                 VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8)",
            )?;
            for (r, status) in rows {
                st.execute(params![
                    r.ts,
                    r.soc,
                    r.ac_in_w,
                    r.ac_out_w,
                    r.dc_in_w,
                    r.dc_out_w,
                    r.ac_in_v,
                    status.as_str()
                ])?;
            }
        }
        tx.commit()
    }
}

fn row_to_reading(r: &rusqlite::Row<'_>) -> rusqlite::Result<Reading> {
    Ok(Reading {
        ts: r.get(0)?,
        soc: r.get(1)?,
        ac_in_w: r.get(2)?,
        ac_out_w: r.get(3)?,
        dc_in_w: r.get(4)?,
        dc_out_w: r.get(5)?,
        ac_in_v: r.get(6)?,
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    fn reading(ts: Ts, soc: u8) -> Reading {
        Reading { ts, soc: Some(soc), ac_in_w: Some(0), ac_out_w: Some(150), ..Default::default() }
    }

    #[test]
    fn readings_and_ratio() {
        let s = Store::in_memory().unwrap();
        for i in 0..9 {
            s.insert_reading(&reading(i * MIN, 50), ReadStatus::Accepted, None).unwrap();
        }
        assert_eq!(s.read_ok_ratio(0, 100 * MIN).unwrap(), None);
        s.insert_miss(9 * MIN, "timeout").unwrap();
        assert_eq!(s.read_ok_ratio(0, 100 * MIN).unwrap(), Some(0.9));
        assert_eq!(s.last_accepted().unwrap().unwrap().ts, 8 * MIN);
        assert_eq!(s.accepted_between(2 * MIN, 4 * MIN).unwrap().len(), 3);
    }

    #[test]
    fn soc_constraint_enforced() {
        let s = Store::in_memory().unwrap();
        let bad = Reading { ts: 1, soc: Some(101), ..Default::default() };
        assert!(s.insert_reading(&bad, ReadStatus::Rejected, None).is_err());
    }

    #[test]
    fn outage_lifecycle() {
        let s = Store::in_memory().unwrap();
        s.start_outage(10, Some(98)).unwrap();
        s.mark_outage(true, false).unwrap();
        s.end_outage(20, Some(80)).unwrap();
        let o = &s.outages(10).unwrap()[0];
        assert_eq!(
            (o.start_ts, o.end_ts, o.soc_start, o.soc_end, o.eco, o.hibernated),
            (10, Some(20), Some(98), Some(80), true, false)
        );
    }

    #[test]
    fn events_newest_first_and_meta() {
        let s = Store::in_memory().unwrap();
        s.insert_event(1, "eco-on", "Eco включён", None).unwrap();
        s.insert_event(2, "eco-off", "Eco снят", None).unwrap();
        assert_eq!(s.recent_events(10).unwrap()[0].kind, "eco-off");
        s.set_meta("x", &vec![1, 2]).unwrap();
        assert_eq!(s.get_meta::<Vec<i32>>("x"), Some(vec![1, 2]));
        assert_eq!(s.get_meta::<Vec<i32>>("y"), None);
    }

    #[test]
    fn history_raw_and_buckets() {
        let mut s = Store::in_memory().unwrap();
        let now = 1_000 * 900 * 1000; // кратно 15 мин
        let rows: Vec<_> = (0..30)
            .map(|i| {
                let r = Reading {
                    ts: now - (29 - i) * MIN,
                    soc: Some(50 + (i % 2) as u8),
                    ac_in_w: Some(10),
                    dc_in_w: Some(5),
                    ac_out_w: Some(100),
                    dc_out_w: Some(i as u32 % 2),
                    ..Default::default()
                };
                (r, if i == 0 { ReadStatus::Rejected } else { ReadStatus::Accepted })
            })
            .collect();
        s.insert_many(&rows).unwrap();
        s.start_outage(now - 10 * MIN, Some(51)).unwrap();

        let h = s.history(Range::H6, now).unwrap();
        assert_eq!((h.step, h.points.len()), (60, 29));
        assert_eq!(
            h.points[0],
            Point { t: (now - 28 * MIN) / 1000, soc: Some(51), inp: Some(15), out: Some(101) }
        );
        assert_eq!(h.outages, vec![Span { start: (now - 10 * MIN) / 1000, end: None }]);

        let h = s.history(Range::D7, now).unwrap();
        assert_eq!(h.step, 300);
        // Корзины по 5 мин: последняя — одна точка ровно в `now`.
        assert_eq!(h.points.len(), 7);
        assert!(h.points.windows(2).all(|w| w[1].t - w[0].t == 300));
        assert_eq!(s.history(Range::D30, now).unwrap().step, 900);
        assert_eq!(Range::parse("1y"), None);
    }

    /// 30d на годе показаний раз в минуту ≤ 300 мс (T036): ~20 мс в release, ~35 мс в debug.
    #[test]
    fn history_30d_fast_on_a_year() {
        let dir = std::env::temp_dir().join(format!("bw-hist-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let path = dir.join("station.db");
        let _ = std::fs::remove_file(&path);
        let mut s = Store::open(&path).unwrap();
        let now: Ts = 1_760_000_000_000;
        let rows: Vec<_> = (0..525_600i64)
            .map(|i| {
                let r = Reading {
                    ts: now - i * MIN,
                    soc: Some((i % 100) as u8),
                    ac_in_w: Some(0),
                    ac_out_w: Some(150),
                    dc_in_w: Some(0),
                    dc_out_w: Some(4),
                    ac_in_v: Some(0.0),
                };
                (r, ReadStatus::Accepted)
            })
            .collect();
        s.insert_many(&rows).unwrap();
        drop(s);
        let s = Store::open_read(&path).unwrap();
        let t = std::time::Instant::now();
        let h = s.history(Range::D30, now).unwrap();
        let took = t.elapsed();
        eprintln!("30d: {} точек за {took:?}", h.points.len());
        assert_eq!(h.points.len(), 30 * 24 * 4 + 1);
        assert!(took.as_millis() < 300, "{took:?}");
        drop(s);
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn purge_drops_old_rows() {
        let s = Store::in_memory().unwrap();
        s.insert_reading(&reading(0, 50), ReadStatus::Accepted, None).unwrap();
        s.insert_event(0, "service-start", "старт", None).unwrap();
        s.insert_reading(&reading(RETENTION + 10, 50), ReadStatus::Accepted, None).unwrap();
        assert_eq!(s.purge(RETENTION + 20).unwrap(), 2);
    }
}
