#![windows_subsystem = "windows"]

use bluetti_watcher::{config, console, install, logging, paths};

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
    /// Внутренняя: элевированная часть `install --elevated-setup` (запускается через UAC)
    #[command(hide = true)]
    ElevatedSetup {
        #[arg(long)]
        user_sid: String,
        #[arg(long)]
        bin_exe: std::path::PathBuf,
        #[arg(long)]
        report: std::path::PathBuf,
    },
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
            // LocalSet: WinRT-объекты задачи графика ДТЭК не `Send`, она идёт через `spawn_local` на том же потоке.
            block_on(tokio::task::LocalSet::new().run_until(cmd_run(&paths, dry_run)))
        }
        Command::Open => {
            let _ = paths.ensure();
            logging::init(Some(&paths.logs_dir()), false, false);
            cmd_open(&paths)
        }
        Command::Scan => {
            logging::init(None, true, false);
            block_on(cmd_scan())
        }
        Command::ReadOnce { mac, trace } => {
            logging::init(None, true, trace);
            block_on(cmd_read_once(&paths, mac, trace))
        }
        Command::GpuApply => {
            let _ = paths.ensure();
            logging::init(Some(&paths.logs_dir()), has_console, false);
            cmd_gpu_apply(&paths)
        }
        Command::Install { mac, port, elevated_setup } => {
            logging::init(None, true, false);
            ExitCode::from(install::cmd_install(paths, mac, port, elevated_setup))
        }
        Command::Uninstall { purge } => {
            logging::init(None, true, false);
            ExitCode::from(install::cmd_uninstall(paths, purge))
        }
        Command::Status => ExitCode::from(install::cmd_status(&paths)),
        Command::ElevatedSetup { user_sid, bin_exe, report } => {
            ExitCode::from(install::cmd_elevated_setup(&user_sid, &bin_exe, &report))
        }
    }
}

fn cmd_open(paths: &paths::Paths) -> ExitCode {
    let port = config::load(&paths.config()).config.port;
    if !bluetti_watcher::window::ensure_service(port) {
        tracing::error!("сервис не ответил на порту {port} за 13 с");
    }
    match bluetti_watcher::window::open(port, paths.data.join("webview")) {
        Ok(()) => ExitCode::SUCCESS,
        Err(e) => {
            tracing::error!("окно дашборда: {e}");
            ExitCode::FAILURE
        }
    }
}

fn block_on<F: std::future::Future<Output = ExitCode>>(f: F) -> ExitCode {
    match tokio::runtime::Builder::new_current_thread().enable_all().build() {
        Ok(rt) => rt.block_on(f),
        Err(e) => {
            eprintln!("не удалось запустить рантайм: {e}");
            ExitCode::FAILURE
        }
    }
}

async fn cmd_run(paths: &paths::Paths, dry_run: bool) -> ExitCode {
    let mutex = bluetti_watcher::instance::mutex_name(&paths.data, &paths::Paths::default_data());
    let Some(_guard) = bluetti_watcher::instance::acquire(&mutex) else {
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
    use bluetti_watcher::{http, notify, power, service};
    let (now_tx, now_rx) = tokio::sync::watch::channel(http::NowView::default());
    let (health_tx, health_rx) = tokio::sync::watch::channel(http::HealthView::default());
    let (events_tx, _) = tokio::sync::broadcast::channel(64);
    let (mode_tx, mode_rx) = tokio::sync::mpsc::unbounded_channel();
    let (check_tx, check_rx) = tokio::sync::mpsc::unbounded_channel();
    let (dtek_tx, dtek_rx) = tokio::sync::watch::channel(bluetti_watcher::dtek::DtekView::disabled());
    let outages_tx = spawn_dtek(&cfg, paths, events_tx.clone(), dtek_tx);
    let app = http::AppState {
        port: cfg.port,
        db_path: paths.db(),
        now: now_rx,
        events: events_tx.clone(),
        mode_tx,
        check_tx,
        health: health_rx,
        dtek: dtek_rx,
    };
    let port = cfg.port;
    tokio::spawn(async move {
        if let Err(e) = http::serve(app).await {
            tracing::error!("HTTP на 127.0.0.1:{port} не запущен: {e}");
        }
    });
    let eco_scheme = store.get_meta::<String>("eco_scheme");
    let power =
        power::PowerControl::new(power::scheme::WindowsPower, cfg.eco.clone(), eco_scheme, cfg.dry_run);
    let hibernator = Box::new(power::hibernate::WindowsHibernator { dry_run: cfg.dry_run });
    let installed = paths.bin_dir().join("bluetti-watcher.exe").exists();
    let notifier = Box::new(notify::ToastNotifier::new(installed));
    let channels = service::Channels {
        now: now_tx,
        health: health_tx,
        events: events_tx,
        modes: mode_rx,
        checks: check_rx,
        outages: outages_tx,
    };
    bluetti_watcher::memory::spawn_trimmer(std::time::Duration::from_secs(10));
    let reader = service::WinRtReader::new(cfg.mac);
    let mut svc = service::Service::new(
        cfg,
        store,
        reader,
        bluetti_watcher::clock::SystemClock,
        power,
        hibernator,
        notifier,
        paths.kill_switch(),
        channels,
    );
    svc.run().await;
    ExitCode::SUCCESS
}

/// Задача графика ДТЭК (только при `[dtek] enabled = true`); канал — для закончившихся отключений.
fn spawn_dtek(
    cfg: &config::Config,
    paths: &paths::Paths,
    events: tokio::sync::broadcast::Sender<bluetti_watcher::store::Event>,
    view: tokio::sync::watch::Sender<bluetti_watcher::dtek::DtekView>,
) -> Option<tokio::sync::mpsc::UnboundedSender<bluetti_watcher::dtek::FinishedOutage>> {
    use bluetti_watcher::dtek;
    if !cfg.dtek.enabled {
        // Снимок «выключено» остаётся в канале, `/api/dtek` отдаёт его.
        return None;
    }
    let store = match bluetti_watcher::store::Store::open(&paths.db()) {
        Ok(s) => s,
        Err(e) => {
            tracing::error!("график ДТЭК: не открыть {}: {e}", paths.db().display());
            let now = bluetti_watcher::clock::Clock::now(&bluetti_watcher::clock::SystemClock);
            view.send_replace(dtek::view(&cfg.dtek, None, &Default::default(), Some("база недоступна"), now));
            return None;
        }
    };
    let (tx, rx) = tokio::sync::mpsc::unbounded_channel();
    let refresher = dtek::Refresher::new(
        cfg.dtek.clone(),
        store,
        dtek::fetch::WinRtSource,
        bluetti_watcher::clock::SystemClock,
        events,
        view,
        Box::new(bluetti_watcher::notify::ToastNotifier::new(
            paths.bin_dir().join("bluetti-watcher.exe").exists(),
        )),
    );
    tokio::task::spawn_local(refresher.run(rx));
    Some(tx)
}

fn cmd_gpu_apply(paths: &paths::Paths) -> ExitCode {
    let cfg = config::load(&paths.config()).config;
    match bluetti_watcher::power::gpu::apply_from_request(&cfg.eco) {
        Ok(w) => {
            tracing::info!("лимит GPU {w} Вт");
            ExitCode::SUCCESS
        }
        Err(e) => {
            tracing::error!("gpu-apply: {e}");
            ExitCode::from(e.exit_code() as u8)
        }
    }
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
