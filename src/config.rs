//! `config.toml` (contracts/cli-config.md). Отсутствующий или неверный ключ — значение по умолчанию и предупреждение.

use std::path::Path;

use toml::{Table, Value};

#[derive(Debug, Clone, PartialEq)]
pub struct Config {
    /// Адрес станции; `None` — не задан или неверен.
    pub mac: Option<u64>,
    pub port: u16,
    pub poll_sec: u32,
    pub dry_run: bool,
    pub station: Station,
    pub mains: Mains,
    pub levels: Levels,
    pub eco: Eco,
    pub guards: Guards,
}

#[derive(Debug, Clone, PartialEq)]
pub struct Station {
    pub capacity_wh: f64,
    pub efficiency: f64,
}

#[derive(Debug, Clone, PartialEq)]
pub struct Mains {
    pub lost_sec: u32,
    pub back_sec: u32,
}

#[derive(Debug, Clone, PartialEq)]
pub struct Levels {
    pub low: u8,
    pub critical: u8,
    pub warn: u8,
    pub hibernate: u8,
    pub agree_sec: u32,
    pub hibernate_after_warn_sec: u32,
    pub rebase_confirm_sec: u32,
}

#[derive(Debug, Clone, PartialEq)]
pub struct Eco {
    pub power_scheme: bool,
    pub cpu_max_pct: u8,
    pub deep_cpu_max_pct: u8,
    pub efficiency_overlay: bool,
    pub monitor_off_sec: u32,
    pub gpu: bool,
    pub gpu_pct: u8,
    pub deep_gpu_pct: u8,
}

#[derive(Debug, Clone, PartialEq)]
pub struct Guards {
    pub soc_rate_per_min: f64,
    pub soc_jump_floor: u8,
    pub max_gap_sec: u32,
}

impl Default for Config {
    fn default() -> Self {
        Self {
            mac: None,
            port: 8765,
            poll_sec: 60,
            dry_run: false,
            station: Station { capacity_wh: 1152.0, efficiency: 0.85 },
            mains: Mains { lost_sec: 180, back_sec: 180 },
            levels: Levels {
                low: 20,
                critical: 10,
                warn: 7,
                hibernate: 5,
                agree_sec: 90,
                hibernate_after_warn_sec: 180,
                rebase_confirm_sec: 600,
            },
            eco: Eco {
                power_scheme: true,
                cpu_max_pct: 80,
                deep_cpu_max_pct: 50,
                efficiency_overlay: true,
                monitor_off_sec: 180,
                gpu: true,
                gpu_pct: 70,
                deep_gpu_pct: 0,
            },
            guards: Guards { soc_rate_per_min: 5.0, soc_jump_floor: 3, max_gap_sec: 600 },
        }
    }
}

/// Результат загрузки: конфиг и список предупреждений для лога и событий.
pub struct Loaded {
    pub config: Config,
    pub warnings: Vec<String>,
}

pub fn load(path: &Path) -> Loaded {
    match std::fs::read_to_string(path) {
        Ok(text) => parse(&text),
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => Loaded {
            config: Config::default(),
            warnings: vec![format!("{} не найден: адрес станции не задан", path.display())],
        },
        Err(e) => Loaded {
            config: Config::default(),
            warnings: vec![format!("{} не читается: {e}", path.display())],
        },
    }
}

pub fn parse(text: &str) -> Loaded {
    let mut w = Vec::new();
    let table = match text.parse::<Table>() {
        Ok(t) => t,
        Err(e) => {
            w.push(format!("config.toml не разобран, всё по умолчанию: {e}"));
            Table::new()
        }
    };
    let d = Config::default();
    let mut r = Reader { root: &table, warnings: &mut w };

    let mac = match table.get("mac") {
        None => {
            r.warnings.push("mac не задан: станция не читается".into());
            None
        }
        Some(Value::String(s)) => match parse_mac(s) {
            Some(m) => Some(m),
            None => {
                r.warnings.push(format!("mac = \"{s}\" — неверный адрес, нужен вид AA:BB:CC:DD:EE:FF"));
                None
            }
        },
        Some(_) => {
            r.warnings.push("mac должен быть строкой".into());
            None
        }
    };

    let config = Config {
        mac,
        port: r.int(None, "port", d.port as i64, 1024, 65535) as u16,
        poll_sec: r.int(None, "poll_sec", d.poll_sec as i64, 15, 600) as u32,
        dry_run: r.boolean(None, "dry_run", d.dry_run),
        station: Station {
            capacity_wh: r.float(Some("station"), "capacity_wh", d.station.capacity_wh, 100.0, 100_000.0),
            efficiency: r.float(Some("station"), "efficiency", d.station.efficiency, 0.4, 1.0),
        },
        mains: Mains {
            lost_sec: r.int(Some("mains"), "lost_sec", d.mains.lost_sec as i64, 30, 3600) as u32,
            back_sec: r.int(Some("mains"), "back_sec", d.mains.back_sec as i64, 30, 3600) as u32,
        },
        levels: Levels {
            low: r.int(Some("levels"), "low", d.levels.low as i64, 1, 100) as u8,
            critical: r.int(Some("levels"), "critical", d.levels.critical as i64, 1, 100) as u8,
            warn: r.int(Some("levels"), "warn", d.levels.warn as i64, 1, 100) as u8,
            hibernate: r.int(Some("levels"), "hibernate", d.levels.hibernate as i64, 1, 100) as u8,
            agree_sec: r.int(Some("levels"), "agree_sec", d.levels.agree_sec as i64, 30, 3600) as u32,
            hibernate_after_warn_sec: r.int(
                Some("levels"),
                "hibernate_after_warn_sec",
                d.levels.hibernate_after_warn_sec as i64,
                0,
                3600,
            ) as u32,
            rebase_confirm_sec: r.int(
                Some("levels"),
                "rebase_confirm_sec",
                d.levels.rebase_confirm_sec as i64,
                60,
                7200,
            ) as u32,
        },
        eco: Eco {
            power_scheme: r.boolean(Some("eco"), "power_scheme", d.eco.power_scheme),
            cpu_max_pct: r.int(Some("eco"), "cpu_max_pct", d.eco.cpu_max_pct as i64, 5, 100) as u8,
            deep_cpu_max_pct: r.int(Some("eco"), "deep_cpu_max_pct", d.eco.deep_cpu_max_pct as i64, 5, 100)
                as u8,
            efficiency_overlay: r.boolean(Some("eco"), "efficiency_overlay", d.eco.efficiency_overlay),
            monitor_off_sec: r.int(Some("eco"), "monitor_off_sec", d.eco.monitor_off_sec as i64, 0, 7200)
                as u32,
            gpu: r.boolean(Some("eco"), "gpu", d.eco.gpu),
            gpu_pct: r.int(Some("eco"), "gpu_pct", d.eco.gpu_pct as i64, 0, 100) as u8,
            deep_gpu_pct: r.int(Some("eco"), "deep_gpu_pct", d.eco.deep_gpu_pct as i64, 0, 100) as u8,
        },
        guards: Guards {
            soc_rate_per_min: r.float(
                Some("guards"),
                "soc_rate_per_min",
                d.guards.soc_rate_per_min,
                0.5,
                50.0,
            ),
            soc_jump_floor: r.int(Some("guards"), "soc_jump_floor", d.guards.soc_jump_floor as i64, 1, 50)
                as u8,
            max_gap_sec: r.int(Some("guards"), "max_gap_sec", d.guards.max_gap_sec as i64, 120, 7200) as u32,
        },
    };

    let mut config = config;
    let l = &config.levels;
    if !(l.low >= l.critical && l.critical >= l.warn && l.warn > l.hibernate) {
        w.push(format!(
            "уровни должны идти low ≥ critical ≥ warn > hibernate (сейчас {}/{}/{}/{}): взяты значения по умолчанию",
            l.low, l.critical, l.warn, l.hibernate
        ));
        config.levels = d.levels;
    }
    Loaded { config, warnings: w }
}

/// `AA:BB:CC:DD:EE:FF` (или через `-`) → 48-битный адрес.
pub fn parse_mac(s: &str) -> Option<u64> {
    let parts: Vec<&str> = s.trim().split([':', '-']).collect();
    if parts.len() != 6 {
        return None;
    }
    let mut v = 0u64;
    for p in parts {
        if p.len() != 2 {
            return None;
        }
        v = (v << 8) | u64::from(u8::from_str_radix(p, 16).ok()?);
    }
    Some(v)
}

pub fn format_mac(v: u64) -> String {
    (0..6).rev().map(|i| format!("{:02X}", (v >> (i * 8)) & 0xFF)).collect::<Vec<_>>().join(":")
}

struct Reader<'a> {
    root: &'a Table,
    warnings: &'a mut Vec<String>,
}

impl Reader<'_> {
    fn get(&mut self, section: Option<&str>, key: &str) -> Option<&Value> {
        match section {
            None => self.root.get(key),
            Some(s) => match self.root.get(s) {
                None => None,
                Some(Value::Table(t)) => t.get(key),
                Some(_) => None,
            },
        }
    }

    fn name(section: Option<&str>, key: &str) -> String {
        section.map_or_else(|| key.to_string(), |s| format!("{s}.{key}"))
    }

    fn int(&mut self, section: Option<&str>, key: &str, default: i64, min: i64, max: i64) -> i64 {
        let v = match self.get(section, key) {
            None => return default,
            Some(Value::Integer(i)) => Some(*i),
            Some(_) => None,
        };
        match v {
            Some(i) if (min..=max).contains(&i) => i,
            _ => {
                let n = Self::name(section, key);
                self.warnings.push(format!("{n}: нужно целое {min}..{max}, взято {default}"));
                default
            }
        }
    }

    fn float(&mut self, section: Option<&str>, key: &str, default: f64, min: f64, max: f64) -> f64 {
        let v = match self.get(section, key) {
            None => return default,
            Some(Value::Float(f)) => Some(*f),
            Some(Value::Integer(i)) => Some(*i as f64),
            Some(_) => None,
        };
        match v {
            Some(f) if f >= min && f <= max => f,
            _ => {
                let n = Self::name(section, key);
                self.warnings.push(format!("{n}: нужно число {min}..{max}, взято {default}"));
                default
            }
        }
    }

    fn boolean(&mut self, section: Option<&str>, key: &str, default: bool) -> bool {
        match self.get(section, key) {
            None => default,
            Some(Value::Boolean(b)) => *b,
            Some(_) => {
                let n = Self::name(section, key);
                self.warnings.push(format!("{n}: нужно true/false, взято {default}"));
                default
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn empty_file_gives_defaults_and_warns_about_mac() {
        let l = parse("");
        assert_eq!(l.config, Config::default());
        assert_eq!(l.warnings.len(), 1);
    }

    #[test]
    fn partial_file_keeps_other_defaults() {
        let l = parse("mac = \"aa:bb:cc:dd:ee:0f\"\n[eco]\ncpu_max_pct = 70\n");
        assert!(l.warnings.is_empty(), "{:?}", l.warnings);
        assert_eq!(l.config.mac, Some(0xAABB_CCDD_EE0F));
        assert_eq!(l.config.eco.cpu_max_pct, 70);
        assert_eq!(l.config.eco.deep_cpu_max_pct, 50);
        assert_eq!(l.config.port, 8765);
    }

    #[test]
    fn wrong_type_or_range_falls_back_per_key() {
        let l = parse(
            "mac = \"AA:BB:CC:DD:EE:FF\"\nport = \"x\"\n[eco]\ncpu_max_pct = 0\ngpu = 1\n[station]\nefficiency = 0.9\n",
        );
        assert_eq!(l.config.port, 8765);
        assert_eq!(l.config.eco.cpu_max_pct, 80);
        assert!(l.config.eco.gpu);
        assert_eq!(l.config.station.efficiency, 0.9);
        assert_eq!(l.warnings.len(), 3, "{:?}", l.warnings);
    }

    #[test]
    fn bad_mac_is_none() {
        assert_eq!(parse("mac = \"AA:BB\"").config.mac, None);
        assert_eq!(parse("mac = 5").config.mac, None);
    }

    #[test]
    fn levels_out_of_order_reset() {
        let l = parse("mac = \"AA:BB:CC:DD:EE:FF\"\n[levels]\nwarn = 3\nhibernate = 5\n");
        assert_eq!(l.config.levels, Config::default().levels);
        assert_eq!(l.warnings.len(), 1);
    }

    #[test]
    fn broken_toml_is_defaults() {
        let l = parse("mac = ");
        assert_eq!(l.config, Config::default());
        assert!(l.warnings[0].contains("не разобран"));
    }

    #[test]
    fn mac_round_trip() {
        let m = parse_mac("01-23-45-67-89-ab").unwrap();
        assert_eq!(format_mac(m), "01:23:45:67:89:AB");
    }
}
