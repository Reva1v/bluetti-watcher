//! Разбор `data/<region>.json` набора outage-data-ua (FR-002). Чистая функция: ответ вне пределов отвергается целиком.

use std::collections::BTreeMap;

use serde_json::Value;

use super::model::{Cell, Day, Schedule, valid_queue};

pub const MAX_DAYS: usize = 4;
pub const MAX_QUEUES: usize = 100;

/// `fact.data[unixDaySeconds][queue][hour "1".."24"] = cell`, `fact.update`, `lastUpdated` (ISO UTC).
pub fn parse(body: &[u8]) -> Result<Schedule, String> {
    let v: Value = serde_json::from_slice(body).map_err(|e| format!("ответ не JSON: {e}"))?;
    let fact = v.get("fact").and_then(Value::as_object).ok_or("нет fact")?;
    let data = fact.get("data").and_then(Value::as_object).ok_or("нет fact.data")?;
    if data.len() > MAX_DAYS {
        return Err(format!("дней {} > {MAX_DAYS}", data.len()));
    }
    let update = match fact.get("update") {
        None | Some(Value::Null) => String::new(),
        Some(Value::String(s)) if s.chars().count() <= 40 => s.clone(),
        Some(_) => return Err("fact.update не строка до 40 символов".into()),
    };
    let collected = v
        .get("lastUpdated")
        .and_then(Value::as_str)
        .and_then(|s| chrono::DateTime::parse_from_rfc3339(s).ok())
        .map(|d| d.timestamp_millis())
        .ok_or("нет lastUpdated")?;

    let mut days: BTreeMap<String, Vec<Day>> = BTreeMap::new();
    for (key, queues) in data {
        let secs: i64 = key.parse().map_err(|_| format!("день «{key}» не число"))?;
        // 2001..2286 годы — грубая защита от мусора.
        if !(1_000_000_000..10_000_000_000).contains(&secs) {
            return Err(format!("день {secs} вне пределов"));
        }
        let queues = queues.as_object().ok_or_else(|| format!("день {key}: не объект"))?;
        if queues.len() > MAX_QUEUES {
            return Err(format!("день {key}: очередей {} > {MAX_QUEUES}", queues.len()));
        }
        for (name, hours) in queues {
            if !valid_queue(name) {
                return Err(format!("очередь «{name}» не вида GPV9.1"));
            }
            let hours = hours.as_object().ok_or_else(|| format!("{name}: не объект"))?;
            let mut cells = [Cell::Unknown; 24];
            for (h, c) in hours {
                let n = h
                    .parse::<usize>()
                    .ok()
                    .filter(|n| (1..=24).contains(n))
                    .ok_or_else(|| format!("{name}: час «{h}» вне 1..24"))?;
                cells[n - 1] = c.as_str().map_or(Cell::Unknown, Cell::from_dataset);
            }
            days.entry(name.clone()).or_default().push(Day { start: secs * 1000, cells });
        }
    }
    for d in days.values_mut() {
        d.sort_by_key(|d| d.start);
    }
    Ok(Schedule { update, collected, days })
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::dtek::model::Cell::*;

    const FIXTURE: &[u8] = include_bytes!("../../tests/fixtures/kyiv-sample.json");
    /// 2026-10-14 00:00 по Киеву.
    const DAY: i64 = 1_791_925_200;

    fn day_json(queues: usize, hours: &str) -> String {
        let q: Vec<String> =
            (0..queues).map(|i| format!("\"GPV{}.{}\": {hours}", i / 10 + 1, i % 10 + 1)).collect();
        format!("{{{}}}", q.join(","))
    }

    fn doc(days: &[(i64, String)]) -> Vec<u8> {
        let d: Vec<String> = days.iter().map(|(k, v)| format!("\"{k}\": {v}")).collect();
        format!(
            "{{\"lastUpdated\": \"2026-10-13T23:16:03.550Z\", \"fact\": {{\"update\": \"13.10.2026 21:31\", \
             \"today\": {DAY}, \"data\": {{{}}}}}}}",
            d.join(",")
        )
        .into_bytes()
    }

    #[test]
    fn fixture_today_for_owner_queue() {
        let s = parse(FIXTURE).unwrap();
        assert_eq!(s.update, "13.10.2026 21:31");
        assert_eq!(s.collected, 1_791_933_363_550);
        assert_eq!(s.days.len(), 7);
        let d = &s.days["GPV9.1"];
        assert_eq!(d.len(), 1);
        assert_eq!(d[0].start, DAY * 1000);
        let mut want = [Yes; 24];
        // Нет света 09:00–12:30 и 20:00–23:30: часы 10–12 и 21–23 целиком, 13 и 24 — первые полчаса.
        for h in [10, 11, 12, 21, 22, 23] {
            want[h - 1] = No;
        }
        want[12] = First;
        want[23] = First;
        assert_eq!(d[0].cells, want);
        for q in ["GPV21.1", "GPV33.1", "GPV45.1", "GPV57.1"] {
            assert_eq!(s.days[q][0].cells, want, "{q}");
        }
        assert_ne!(s.days["GPV1.1"][0].cells, want);
    }

    #[test]
    fn unknown_cells_and_missing_hours_are_unknown() {
        let s =
            parse(&doc(&[(DAY, "{\"GPV9.1\": {\"1\": \"no\", \"2\": \"zzz\", \"3\": 5}}".into())])).unwrap();
        let c = s.days["GPV9.1"][0].cells;
        assert_eq!((c[0], c[1], c[2], c[23]), (No, Unknown, Unknown, Unknown));
    }

    #[test]
    fn limits_reject_the_whole_answer() {
        let ok = "{\"1\": \"yes\"}";
        assert!(parse(&doc(&[(DAY, day_json(MAX_QUEUES, ok))])).is_ok());
        assert!(parse(&doc(&[(DAY, day_json(MAX_QUEUES + 1, ok))])).is_err());
        let four: Vec<_> = (0..4).map(|i| (DAY + i * 86_400, day_json(1, ok))).collect();
        assert!(parse(&doc(&four)).is_ok());
        let five: Vec<_> = (0..5).map(|i| (DAY + i * 86_400, day_json(1, ok))).collect();
        assert!(parse(&doc(&five)).is_err());
        for bad in [
            "{\"GPV123.1\": {\"1\": \"yes\"}}",
            "{\"GPV9.1\": {\"1\": \"yes\"}, \"X\": {\"1\": \"yes\"}}",
            "{\"GPV9.1\": {\"0\": \"yes\"}}",
            "{\"GPV9.1\": {\"25\": \"yes\"}}",
            "{\"GPV9.1\": {\"x\": \"yes\"}}",
            "{\"GPV9.1\": [1, 2]}",
            "[]",
        ] {
            assert!(parse(&doc(&[(DAY, bad.to_string())])).is_err(), "{bad}");
        }
        assert!(parse(&doc(&[(-5, day_json(1, ok))])).is_err());
        assert!(parse(b"not json").is_err());
        assert!(parse(b"{\"fact\": {}}").is_err());
        assert!(parse(b"{\"fact\": {\"data\": {}}}").is_err(), "без lastUpdated");
    }
}
