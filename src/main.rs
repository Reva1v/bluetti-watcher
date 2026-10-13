#![windows_subsystem = "windows"]
// Модули подключаются по мере реализации задач; снять перед T055.
#![allow(dead_code)]

mod clock;
mod config;
mod console;
mod logging;
mod model;
mod paths;

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
        Command::Run { .. } => {
            let _ = paths.ensure();
            logging::init(Some(&paths.logs_dir()), has_console, false);
            tracing::info!("сервис ещё не реализован");
            ExitCode::SUCCESS
        }
        _ => {
            logging::init(None, true, false);
            eprintln!("команда ещё не реализована");
            ExitCode::FAILURE
        }
    }
}
