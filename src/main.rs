#![windows_subsystem = "windows"]

use bluetti_watcher::{config, console, logging, paths};

use std::process::ExitCode;

use clap::{Parser, Subcommand};

#[derive(Parser)]
#[command(name = "bluetti-watcher", version, about = "Фоновый монитор станции BLUETTI AC180P")]
struct Cli {
    #[command(subcommand)]
    command: Option<Command>,
}

#[derive(Subcommand)]
enum Command {
    /// Сервис: чтение станции, решения, HTTP (по умолчанию)
    Run {
        #[arg(long)]
        dry_run: bool,
    },
    /// Окно дашборда
    Open,
    /// Найти станции BLUETTI поблизости
    Scan,
    /// Одно чтение станции
    ReadOnce {
        #[arg(long)]
        mac: Option<String>,
        /// Печатать каждый кадр (hex, без ключей)
        #[arg(long)]
        trace: bool,
    },
    /// Установить: автозапуск, ярлыки, схема питания
    Install {
        #[arg(long)]
        mac: Option<String>,
        #[arg(long)]
        port: Option<u16>,
        /// Один запрос UAC: включить гибернацию и задачу GPU
        #[arg(long)]
        elevated_setup: bool,
    },
    /// Удалить: задачи, ярлыки, схема питания
    Uninstall {
        /// Удалить и данные
        #[arg(long)]
        purge: bool,
    },
    /// Одна строка состояния
    Status,
    /// Только из задачи BluettiWatcherGpu: применить лимит GPU
    GpuApply,
}

fn main() -> ExitCode {
    let has_console = console::attach_parent();
    let cli = match Cli::try_parse() {
        Ok(c) => c,
        Err(e) => {
            let _ = e.print();
            return ExitCode::from(if e.use_stderr() { 2 } else { 0 });
        }
    };
    let paths = paths::Paths::from_env();
    match cli.command.unwrap_or(Command::Run { dry_run: false }) {
        Command::Run { dry_run } => {
            let _ = paths.ensure();
            logging::init(Some(&paths.logs_dir()), has_console, false);
            block_on(cmd_run(&paths, dry_run))
        }
        Command::Scan => {
            logging::init(None, true, false);
            block_on(cmd_scan())
        }
        Command::ReadOnce { mac, trace } => {
            logging::init(None, true, trace);
            block_on(cmd_read_once(&paths, mac, trace))
        }
        _ => {
            logging::init(None, true, false);
            eprintln!("команда ещё не реализована");
            ExitCode::FAILURE
        }
    }
}

fn block_on<F: std::future::Future<Output = ExitCode>>(f: F) -> ExitCode {
    match tokio::runtime::Builder::new_current_thread().enable_time().build() {
        Ok(rt) => rt.block_on(f),
        Err(e) => {
            eprintln!("не удалось запустить рантайм: {e}");
            ExitCode::FAILURE
        }
    }
}

async fn cmd_run(paths: &paths::Paths, dry_run: bool) -> ExitCode {
    let Some(_guard) = bluetti_watcher::instance::acquire() else {
        tracing::info!("сервис уже запущен");
        return ExitCode::from(3);
    };
    let loaded = config::load(&paths.config());
    for w in &loaded.warnings {
        tracing::warn!("config: {w}");
    }
    let mut cfg = loaded.config;
    cfg.dry_run |= dry_run;
    let store = match bluetti_watcher::store::Store::open(&paths.db()) {
        Ok(s) => s,
        Err(e) => {
            tracing::error!("не открыть {}: {e}", paths.db().display());
            return ExitCode::FAILURE;
        }
    };
    tracing::info!(
        "сервис запущен: станция {}, опрос {} с{}",
        cfg.mac.map_or("не задана".into(), config::format_mac),
        cfg.poll_sec,
        if cfg.dry_run { ", dry run" } else { "" }
    );
    let reader = bluetti_watcher::service::WinRtReader::new(cfg.mac);
    let mut svc =
        bluetti_watcher::service::Service::new(cfg, store, reader, bluetti_watcher::clock::SystemClock);
    svc.run().await;
    ExitCode::SUCCESS
}

async fn cmd_scan() -> ExitCode {
    println!("Ищу станции BLUETTI 10 секунд…");
    match bluetti_watcher::ble::scan::scan(std::time::Duration::from_secs(10)).await {
        Ok(found) if found.is_empty() => {
            println!(
                "Ничего не найдено. Станция включена и рядом? Телефон с приложением BLUETTI не подключён к ней?"
            );
            ExitCode::FAILURE
        }
        Ok(found) => {
            for f in found {
                println!("{:<16} {}  RSSI {} dBm", f.name, config::format_mac(f.address), f.rssi);
            }
            ExitCode::SUCCESS
        }
        Err(e) => {
            eprintln!("Сканирование не удалось (Bluetooth выключен?): {e}");
            ExitCode::FAILURE
        }
    }
}

async fn cmd_read_once(paths: &paths::Paths, mac: Option<String>, trace: bool) -> ExitCode {
    let address = match mac {
        Some(m) => match config::parse_mac(&m) {
            Some(a) => a,
            None => {
                eprintln!("неверный адрес {m}: нужен вид AA:BB:CC:DD:EE:FF");
                return ExitCode::from(2);
            }
        },
        None => match config::load(&paths.config()).config.mac {
            Some(a) => a,
            None => {
                eprintln!("адрес не задан: --mac AA:BB:CC:DD:EE:FF или mac в config.toml");
                return ExitCode::from(2);
            }
        },
    };
    let mut transport = bluetti_watcher::ble::winrt::WinRtTransport::new(address);
    let keys = bluetti_watcher::ble::crypto::Keys::bluetti();
    let opts = bluetti_watcher::ble::ReadOptions::default();
    let started = std::time::Instant::now();
    let now = bluetti_watcher::clock::Clock::now(&bluetti_watcher::clock::SystemClock);
    match bluetti_watcher::ble::read_station(&mut transport, &keys, &opts, now).await {
        Ok(out) => {
            let r = out.reading;
            let f = |v: Option<u32>| v.map_or("—".to_string(), |x| format!("{x} Вт"));
            println!("станция   {}", config::format_mac(address));
            println!("шифрование {}", if out.encrypted { "да" } else { "нет" });
            println!("заряд     {}", r.soc.map_or("—".to_string(), |x| format!("{x} %")));
            println!("вход AC   {}", f(r.ac_in_w));
            println!("выход AC  {}", f(r.ac_out_w));
            println!("вход DC   {}", f(r.dc_in_w));
            println!("выход DC  {}", f(r.dc_out_w));
            println!("напряж.AC {}", r.ac_in_v.map_or("—".to_string(), |x| format!("{x:.1} В")));
            println!("время     {:.1} с", started.elapsed().as_secs_f32());
            if trace {
                println!("(кадры выше в логе, ключи сессии не печатаются)");
            }
            ExitCode::SUCCESS
        }
        Err(e) => {
            eprintln!("нет ответа: {e} ({})", e.code());
            ExitCode::FAILURE
        }
    }
}
