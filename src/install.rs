//! Команды `install`, `uninstall`, `status` (T050–T052).
//!
//! Логика шагов — над трейтом [`Sys`] (задачи планировщика, ярлыки, процессы, UAC-часть), чтобы её можно было
//! проверить без изменений в системе; системная реализация — [`WinSys`] (COM: Task Scheduler, IShellLinkW).

use std::path::{Path, PathBuf};
use std::time::{Duration, Instant};

use crate::ble::scan::Found;
use crate::config;
use crate::model::EcoLevel;
use crate::paths::Paths;
use crate::power::{PowerBackend, PowerControl, PowerRestore, gpu};
use crate::store::Store;

/// Задача автозапуска сервиса.
pub const TASK_NAME: &str = "BluettiWatcher";
pub const SHORTCUT_FILE: &str = "Bluetti Watcher.lnk";
pub const SHORTCUT_DESC: &str = "Дашборд станции BLUETTI";
pub const EXE_NAME: &str = "bluetti-watcher.exe";
/// Сколько ждать `/api/health` после запуска задачи.
pub const HEALTH_WAIT: Duration = Duration::from_secs(15);

// ---------------------------------------------------------------------------------------------------------------
// Чистые части
// ---------------------------------------------------------------------------------------------------------------

#[derive(Debug, PartialEq)]
pub enum PickError {
    None,
    Many(Vec<String>),
}

/// Выбор станции по результатам `scan`: ровно одна — её адрес; несколько — список строк для печати.
pub fn pick_station(found: &[Found]) -> Result<u64, PickError> {
    match found {
        [] => Err(PickError::None),
        [one] => Ok(one.address),
        many => Err(PickError::Many(
            many.iter()
                .map(|f| format!("{:<16} {}  RSSI {} dBm", f.name, config::format_mac(f.address), f.rssi))
                .collect(),
        )),
    }
}

/// Записать `mac` (и `port`, если задан) в текст `config.toml`, сохранив остальные ключи. Комментарии теряются.
/// `Err` — существующий файл не разбирается как TOML.
pub fn merge_config(existing: Option<&str>, mac: u64, port: Option<u16>) -> Result<String, String> {
    let mut table = match existing.map(str::trim).filter(|t| !t.is_empty()) {
        Some(text) => text.parse::<toml::Table>().map_err(|e| e.to_string())?,
        None => toml::Table::new(),
    };
    table.insert("mac".into(), toml::Value::String(config::format_mac(mac)));
    if let Some(p) = port {
        table.insert("port".into(), toml::Value::Integer(p as i64));
    }
    toml::to_string(&table).map_err(|e| e.to_string())
}

fn xml_escape(s: &str) -> String {
    s.replace('&', "&amp;").replace('<', "&lt;").replace('>', "&gt;").replace('"', "&quot;")
}

/// Описание задачи планировщика (Task Scheduler 1.2 XML).
pub struct TaskSpec<'a> {
    pub description: &'a str,
    pub exe: &'a Path,
    pub args: &'a str,
    pub user_sid: &'a str,
    /// Триггер «при входе этого пользователя»; `false` — только запуск по требованию.
    pub logon_trigger: bool,
    /// Наивысшие права (`HighestAvailable`), иначе `LeastPrivilege`.
    pub highest: bool,
    /// Перезапуск при сбое 3 раза через 1 мин.
    pub restart_on_failure: bool,
}

impl TaskSpec<'_> {
    /// Задача `BluettiWatcher`: при входе пользователя, без элевации, `bin\bluetti-watcher.exe run`.
    pub fn autostart<'a>(exe: &'a Path, user_sid: &'a str) -> TaskSpec<'a> {
        TaskSpec {
            description: "Bluetti Watcher: фоновый монитор станции BLUETTI",
            exe,
            args: "run",
            user_sid,
            logon_trigger: true,
            highest: false,
            restart_on_failure: true,
        }
    }

    /// Задача `BluettiWatcherGpu`: по требованию, наивысшие права, `gpu-apply`.
    pub fn gpu<'a>(exe: &'a Path, user_sid: &'a str) -> TaskSpec<'a> {
        TaskSpec {
            description: "Bluetti Watcher: лимит мощности GPU в eco-режиме",
            exe,
            args: "gpu-apply",
            user_sid,
            logon_trigger: false,
            highest: true,
            restart_on_failure: false,
        }
    }

    pub fn to_xml(&self) -> String {
        let sid = xml_escape(self.user_sid);
        let exe = xml_escape(&self.exe.display().to_string());
        let dir = xml_escape(&self.exe.parent().map(|p| p.display().to_string()).unwrap_or_default());
        let triggers = if self.logon_trigger {
            format!("<LogonTrigger><Enabled>true</Enabled><UserId>{sid}</UserId></LogonTrigger>")
        } else {
            String::new()
        };
        let run_level = if self.highest { "HighestAvailable" } else { "LeastPrivilege" };
        let restart = if self.restart_on_failure {
            "<RestartOnFailure><Interval>PT1M</Interval><Count>3</Count></RestartOnFailure>"
        } else {
            ""
        };
        format!(
            r#"<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo><Author>Bluetti Watcher</Author><Description>{desc}</Description></RegistrationInfo>
  <Triggers>{triggers}</Triggers>
  <Principals>
    <Principal id="Author"><UserId>{sid}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>{run_level}</RunLevel></Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>true</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    {restart}
  </Settings>
  <Actions Context="Author">
    <Exec><Command>"{exe}"</Command><Arguments>{args}</Arguments><WorkingDirectory>{dir}</WorkingDirectory></Exec>
  </Actions>
</Task>
"#,
            desc = xml_escape(self.description),
            args = xml_escape(self.args),
        )
    }
}

/// Права на задачу GPU: администраторы и SYSTEM — полный доступ, установивший пользователь — чтение и запуск
/// (удалить её без администратора он не может).
pub fn gpu_task_sddl(user_sid: &str) -> String {
    format!("D:P(A;;FA;;;BA)(A;;FA;;;SY)(A;;GRGX;;;{user_sid})")
}

/// Ярлык `bluetti-watcher.exe open`.
#[derive(Debug, Clone, PartialEq)]
pub struct Shortcut {
    pub target: PathBuf,
    pub args: String,
    pub workdir: PathBuf,
    pub description: String,
    /// `PKEY_AppUserModel_ID` (нужен ярлыку в «Пуске» для уведомлений).
    pub app_id: Option<String>,
}

impl Shortcut {
    pub fn open(exe: &Path, app_id: Option<&str>) -> Self {
        Self {
            target: exe.to_path_buf(),
            args: "open".into(),
            workdir: exe.parent().map(Path::to_path_buf).unwrap_or_default(),
            description: SHORTCUT_DESC.into(),
            app_id: app_id.map(str::to_string),
        }
    }
}

/// Строка `status` по телам `/api/health` и `/api/now` (`None` — сервис не ответил).
pub fn status_line(health: Option<&serde_json::Value>, now: Option<&serde_json::Value>) -> String {
    let Some(_) = health else { return "сервис: не запущен".into() };
    let mut parts = vec!["сервис: работает".to_string()];
    let Some(now) = now else {
        parts.push("состояние недоступно".into());
        return parts.join(" | ");
    };
    let mains = match now["mains"].as_str() {
        Some("on") => "есть",
        Some("off") => "нет",
        _ => "неизвестно",
    };
    parts.push(format!("сеть: {mains}"));
    parts.push(match now["soc"].as_u64() {
        Some(s) => format!("заряд {s} %"),
        None => "заряд —".into(),
    });
    let eco = match now["eco"].as_str() {
        Some("eco") => "да",
        Some("deep") => "глубокий",
        _ => "нет",
    };
    parts.push(format!("eco: {eco}"));
    match now["manual"].as_str() {
        Some("max") => parts.push("режим: максимум".into()),
        Some("eco") => parts.push("режим: eco вручную".into()),
        _ => {}
    }
    match now["ts"].as_str().and_then(|ts| ts.get(11..16)) {
        Some(hm) if now["stale"].as_bool() == Some(true) => parts.push(format!("показание {hm} (устарело)")),
        Some(hm) => parts.push(format!("показание {hm}")),
        None => parts.push("показаний нет".into()),
    }
    if now["dryRun"].as_bool() == Some(true) {
        parts.push("dry run".into());
    }
    parts.join(" | ")
}

/// Путь без префикса `\\?\` для сравнения.
fn normalize(p: &Path) -> String {
    let c = std::fs::canonicalize(p).unwrap_or_else(|_| p.to_path_buf());
    let s = c.display().to_string();
    s.strip_prefix(r"\\?\").unwrap_or(&s).to_lowercase()
}

pub fn same_file(a: &Path, b: &Path) -> bool {
    normalize(a) == normalize(b)
}

/// `inner` лежит внутри `dir`.
pub fn is_inside(inner: &Path, dir: &Path) -> bool {
    let (i, d) = (normalize(inner), normalize(dir));
    i.strip_prefix(&d).is_some_and(|rest| rest.starts_with('\\') || rest.starts_with('/'))
}

// ---------------------------------------------------------------------------------------------------------------
// Отчёт о шагах
// ---------------------------------------------------------------------------------------------------------------

/// Итоги шагов: печатает по-русски по ходу и помнит, был ли критический сбой.
#[derive(Default)]
pub struct Report {
    pub lines: Vec<String>,
    pub failed: bool,
    pub echo: bool,
}

impl Report {
    pub fn printing() -> Self {
        Self { echo: true, ..Self::default() }
    }
    fn push(&mut self, line: String) {
        if self.echo {
            println!("{line}");
        }
        self.lines.push(line);
    }
    pub fn ok(&mut self, msg: impl AsRef<str>) {
        self.push(format!("  ок        {}", msg.as_ref()));
    }
    /// Некритичный сбой: установка продолжается и считается успешной.
    pub fn warn(&mut self, msg: impl AsRef<str>) {
        self.push(format!("  внимание  {}", msg.as_ref()));
    }
    /// Критичный сбой: exit 1.
    pub fn fail(&mut self, msg: impl AsRef<str>) {
        self.failed = true;
        self.push(format!("  ОШИБКА    {}", msg.as_ref()));
    }
    pub fn note(&mut self, msg: impl AsRef<str>) {
        self.push(msg.as_ref().to_string());
    }
}

// ---------------------------------------------------------------------------------------------------------------
// Побочные эффекты
// ---------------------------------------------------------------------------------------------------------------

#[derive(Debug, PartialEq)]
pub enum TaskDelete {
    Deleted,
    Missing,
    AccessDenied,
}

/// Операции, меняющие систему. Файлы в каталогах программы шаги трогают сами (в тестах — временные каталоги).
pub trait Sys {
    /// Завершить процессы, запущенные из `exe` (кроме текущего). Возвращает число завершённых.
    fn stop_processes(&mut self, exe: &Path) -> Result<usize, String>;
    fn register_task(
        &mut self,
        name: &str,
        xml: &str,
        user_sid: &str,
        sddl: Option<&str>,
    ) -> Result<(), String>;
    fn run_task(&mut self, name: &str) -> Result<(), String>;
    fn delete_task(&mut self, name: &str) -> Result<TaskDelete, String>;
    fn create_shortcut(&mut self, path: &Path, link: &Shortcut) -> Result<(), String>;
    fn service_alive(&mut self, port: u16) -> bool;
    fn sleep(&mut self, d: Duration);
    /// `powercfg /h on` (элевированно).
    fn hibernate_on(&mut self) -> Result<(), String>;
    /// Дать пользователю `sid` право «Изменение» на файл (элевированно).
    fn grant_modify(&mut self, path: &Path, sid: &str) -> Result<(), String>;
    /// Удалить каталог после выхода текущего процесса (exe лежит в нём).
    fn remove_dir_later(&mut self, dir: &Path) -> Result<(), String>;
}

/// Куда ставить.
pub struct Targets {
    pub paths: Paths,
    /// Exe, из которого запущена установка.
    pub current_exe: PathBuf,
    pub user_sid: String,
    /// Рабочий стол и «Пуск\Программы»; `None` — папку узнать не удалось.
    pub desktop: Option<PathBuf>,
    pub programs: Option<PathBuf>,
    /// `%ProgramData%\bluetti-watcher`.
    pub shared: PathBuf,
}

impl Targets {
    pub fn bin_exe(&self) -> PathBuf {
        self.paths.bin_dir().join(EXE_NAME)
    }
    fn shortcuts(&self) -> Vec<(PathBuf, Option<&'static str>, &'static str)> {
        let mut v = Vec::new();
        if let Some(d) = &self.desktop {
            v.push((d.join(SHORTCUT_FILE), None, "Рабочий стол"));
        }
        if let Some(p) = &self.programs {
            v.push((p.join(SHORTCUT_FILE), Some(crate::notify::APP_ID), "меню «Пуск»"));
        }
        v
    }
}

/// Шаги `install` после выбора станции (без элевированной части). `port` — итоговый порт сервиса.
pub fn install<S: Sys, B: PowerBackend>(
    sys: &mut S,
    t: &Targets,
    mac: u64,
    port_arg: Option<u16>,
    power: impl FnOnce(&config::Config, Option<String>) -> PowerControl<B>,
    rep: &mut Report,
) {
    let bin_exe = t.bin_exe();

    // 1. Копия exe.
    if let Err(e) = std::fs::create_dir_all(t.paths.bin_dir()).and_then(|()| t.paths.ensure()) {
        rep.fail(format!("каталог {}: {e}", t.paths.data.display()));
        return;
    }
    if same_file(&t.current_exe, &bin_exe) {
        rep.ok(format!("exe уже на месте: {}", bin_exe.display()));
    } else {
        match sys.stop_processes(&bin_exe) {
            Ok(0) => {}
            Ok(n) => rep.ok(format!("остановлен прежний сервис ({n} проц.)")),
            Err(e) => rep.warn(format!("прежний сервис не остановлен: {e}")),
        }
        let mut last = None;
        for attempt in 0..10 {
            if attempt > 0 {
                sys.sleep(Duration::from_millis(500));
            }
            match std::fs::copy(&t.current_exe, &bin_exe) {
                Ok(_) => {
                    last = None;
                    break;
                }
                Err(e) => last = Some(e),
            }
        }
        match last {
            None => rep.ok(format!("exe скопирован в {}", bin_exe.display())),
            Some(e) => {
                rep.fail(format!("exe не скопирован в {}: {e}", bin_exe.display()));
                return;
            }
        }
    }

    // 2. config.toml.
    let cfg_path = t.paths.config();
    let existing = std::fs::read_to_string(&cfg_path).ok();
    let text = match merge_config(existing.as_deref(), mac, port_arg) {
        Ok(s) => s,
        Err(e) => {
            let bak = cfg_path.with_extension("toml.bak");
            let _ = std::fs::rename(&cfg_path, &bak);
            rep.warn(format!("config.toml не разобран ({e}) — старый сохранён как {}", bak.display()));
            merge_config(None, mac, port_arg).unwrap_or_default()
        }
    };
    if let Err(e) = std::fs::write(&cfg_path, text) {
        rep.fail(format!("{}: {e}", cfg_path.display()));
        return;
    }
    rep.ok(format!("config.toml: станция {}", config::format_mac(mac)));
    let loaded = config::load(&cfg_path);
    for w in &loaded.warnings {
        rep.warn(format!("config.toml: {w}"));
    }
    let cfg = loaded.config;

    // 3. Схема «Bluetti Eco».
    match Store::open(&t.paths.db()) {
        Ok(store) => {
            let mut pc = power(&cfg, store.get_meta::<String>("eco_scheme"));
            match pc.ensure_scheme() {
                Ok(guid) => match store.set_meta("eco_scheme", &guid) {
                    Ok(()) => rep.ok(format!("схема питания «{}» {guid}", crate::power::ECO_SCHEME_NAME)),
                    Err(e) => rep.warn(format!("GUID схемы не сохранён: {e}")),
                },
                Err(e) => rep.warn(e),
            }
        }
        Err(e) => rep.warn(format!("{}: {e} — схема питания будет создана сервисом", t.paths.db().display())),
    }

    // 4. Задача автозапуска.
    let xml = TaskSpec::autostart(&bin_exe, &t.user_sid).to_xml();
    match sys.register_task(TASK_NAME, &xml, &t.user_sid, None) {
        Ok(()) => rep.ok(format!("задача автозапуска {TASK_NAME} (при входе в Windows)")),
        Err(e) => rep.fail(format!("задача {TASK_NAME}: {e}")),
    }

    // 5. Ярлыки.
    for (path, app_id, place) in t.shortcuts() {
        match sys.create_shortcut(&path, &Shortcut::open(&bin_exe, app_id)) {
            Ok(()) => rep.ok(format!("ярлык: {place}")),
            Err(e) => rep.warn(format!("ярлык ({place}) не создан: {e}")),
        }
    }
    if t.programs.is_none() {
        rep.warn("папка меню «Пуск» не найдена — уведомления пойдут от имени PowerShell");
    }

    // 6. Запуск сервиса.
    if rep.failed {
        return;
    }
    if let Err(e) = sys.run_task(TASK_NAME) {
        rep.fail(format!("задача {TASK_NAME} не запущена: {e}"));
        return;
    }
    let deadline = Instant::now() + HEALTH_WAIT;
    loop {
        if sys.service_alive(cfg.port) {
            rep.ok(format!("сервис запущен: http://127.0.0.1:{}/", cfg.port));
            break;
        }
        if Instant::now() >= deadline {
            rep.fail(format!(
                "сервис не ответил на порту {} за {} с — см. {}",
                cfg.port,
                HEALTH_WAIT.as_secs(),
                t.paths.logs_dir().join("service.log").display()
            ));
            break;
        }
        sys.sleep(Duration::from_millis(500));
    }
}

/// Элевированная часть (`--elevated-setup`): гибернация, `gpu-request` с правом записи, задача GPU.
/// Отказ одного шага не останавливает остальные.
pub fn elevated_setup<S: Sys>(sys: &mut S, shared: &Path, user_sid: &str, bin_exe: &Path, rep: &mut Report) {
    match sys.hibernate_on() {
        Ok(()) => rep.ok("гибернация включена (powercfg /h on)"),
        Err(e) => rep.fail(format!("гибернация не включена: {e}")),
    }

    let file = shared.join("gpu-request");
    let made = std::fs::create_dir_all(shared).and_then(|()| {
        if file.exists() { Ok(()) } else { std::fs::write(&file, gpu::level_word(EcoLevel::None)) }
    });
    match made.map_err(|e| e.to_string()).and_then(|()| sys.grant_modify(&file, user_sid)) {
        Ok(()) => rep.ok(format!("{} (запись разрешена пользователю)", file.display())),
        Err(e) => rep.fail(format!("{}: {e}", file.display())),
    }

    let xml = TaskSpec::gpu(bin_exe, user_sid).to_xml();
    match sys.register_task(gpu::TASK_NAME, &xml, user_sid, Some(&gpu_task_sddl(user_sid))) {
        Ok(()) => rep.ok(format!("задача {} (лимит GPU, наивысшие права)", gpu::TASK_NAME)),
        Err(e) => rep.fail(format!("задача {}: {e}", gpu::TASK_NAME)),
    }
}

/// Шаги `uninstall`. `power` строится по сохранённой конфигурации и GUID схемы.
pub fn uninstall<S: Sys, B: PowerBackend>(
    sys: &mut S,
    t: &Targets,
    purge: bool,
    power: impl FnOnce(&config::Config, Option<String>) -> PowerControl<B>,
    rep: &mut Report,
) {
    let bin_exe = t.bin_exe();

    // 1. Автозапуск — первым, чтобы сервис не перезапустился.
    delete_task(sys, TASK_NAME, rep);

    // 2. Остановить сервис.
    match sys.stop_processes(&bin_exe) {
        Ok(0) => rep.ok("сервис не был запущен"),
        Ok(n) => rep.ok(format!("сервис остановлен ({n} проц.)")),
        Err(e) => rep.fail(format!("сервис не остановлен: {e}")),
    }

    // 3. Снять eco и удалить схему (GPU возвращается через задачу GPU, поэтому до её удаления).
    let cfg = config::load(&t.paths.config()).config;
    let store = if t.paths.db().exists() { Store::open(&t.paths.db()).ok() } else { None };
    let eco_scheme = store.as_ref().and_then(|s| s.get_meta::<String>("eco_scheme"));
    let mut pc = power(&cfg, eco_scheme);
    let saved = store.as_ref().and_then(|s| s.get_meta::<PowerRestore>("power_restore"));
    if let Some(saved) = saved {
        let gpu_back = saved.gpu_was.is_some();
        let (left, parts) = pc.apply(EcoLevel::None, Some(saved));
        for p in parts {
            if p.ok {
                rep.ok(format!("eco снят: {}", p.detail));
            } else {
                rep.warn(format!("eco: {}", p.detail));
            }
        }
        if let Some(s) = &store {
            let _ = match &left {
                Some(r) => s.set_meta("power_restore", r),
                None => s.delete_meta("power_restore"),
            };
        }
        if gpu_back {
            sys.sleep(Duration::from_secs(3)); // дать gpu-apply отработать до удаления задачи
        }
    }
    match pc.remove_scheme() {
        Ok(()) => {
            rep.ok(format!("схема питания «{}» удалена", crate::power::ECO_SCHEME_NAME));
            if let Some(s) = &store {
                let _ = s.delete_meta("eco_scheme");
            }
        }
        Err(e) => rep.warn(e),
    }
    drop(store);

    // 4. Задача GPU (нужен администратор).
    delete_task(sys, gpu::TASK_NAME, rep);

    // 5. Ярлыки.
    for (path, _, place) in t.shortcuts() {
        match std::fs::remove_file(&path) {
            Ok(()) => rep.ok(format!("ярлык удалён: {place}")),
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => {}
            Err(e) => rep.warn(format!("ярлык ({place}) не удалён: {e}")),
        }
    }

    // 6. Данные.
    if purge {
        let shared_file = t.shared.join("gpu-request");
        if shared_file.exists() || t.shared.exists() {
            let r = std::fs::remove_file(&shared_file)
                .or_else(|e| if e.kind() == std::io::ErrorKind::NotFound { Ok(()) } else { Err(e) })
                .and_then(|()| std::fs::remove_dir(&t.shared));
            match r {
                Ok(()) => rep.ok(format!("удалён {}", t.shared.display())),
                Err(e) => rep.warn(format!(
                    "{} не удалён ({e}) — удалите вручную из консоли администратора",
                    t.shared.display()
                )),
            }
        }
        let data = &t.paths.data;
        if !data.exists() {
            return;
        }
        if is_inside(&t.current_exe, data) {
            match sys.remove_dir_later(data) {
                Ok(()) => rep.ok(format!("{} будет удалён через несколько секунд", data.display())),
                Err(e) => rep.fail(format!("{} не удалён: {e}", data.display())),
            }
        } else {
            match std::fs::remove_dir_all(data) {
                Ok(()) => rep.ok(format!("данные удалены: {}", data.display())),
                Err(e) => rep.fail(format!("{} не удалён: {e}", data.display())),
            }
        }
    }
}

fn delete_task<S: Sys>(sys: &mut S, name: &str, rep: &mut Report) {
    match sys.delete_task(name) {
        Ok(TaskDelete::Deleted) => rep.ok(format!("задача {name} удалена")),
        Ok(TaskDelete::Missing) => rep.ok(format!("задачи {name} нет")),
        Ok(TaskDelete::AccessDenied) => rep.warn(format!(
            "задачу {name} может удалить только администратор: schtasks /delete /tn {name} /f"
        )),
        Err(e) => rep.fail(format!("задача {name}: {e}")),
    }
}

// ---------------------------------------------------------------------------------------------------------------
// Команды (main.rs)
// ---------------------------------------------------------------------------------------------------------------

/// `install`. Возвращает код выхода.
pub fn cmd_install(paths: Paths, mac: Option<String>, port: Option<u16>, elevated: bool) -> u8 {
    let mac = match mac {
        Some(m) => match config::parse_mac(&m) {
            Some(a) => a,
            None => {
                eprintln!("неверный адрес {m}: нужен вид AA:BB:CC:DD:EE:FF");
                return 1;
            }
        },
        None => match find_station() {
            Some(a) => a,
            None => return 1,
        },
    };
    let mut sys = WinSys::new();
    let targets = match win::targets(paths) {
        Ok(t) => t,
        Err(e) => {
            eprintln!("установка невозможна: {e}");
            return 1;
        }
    };
    println!("Установка Bluetti Watcher (станция {}):", config::format_mac(mac));
    let mut rep = Report::printing();
    install(&mut sys, &targets, mac, port, windows_power, &mut rep);
    if elevated {
        println!("Шаг с правами администратора (запрос UAC):");
        match win::run_elevated_setup(&targets) {
            Ok((code, lines)) => {
                for l in lines {
                    println!("{l}");
                }
                if code != 0 {
                    rep.warn("часть шагов администратора не выполнена (см. выше)");
                }
            }
            Err(e) => rep.warn(format!("шаг администратора не выполнен: {e}")),
        }
    }
    if rep.failed {
        println!("Установка завершена с ошибками.");
        1
    } else {
        println!("Готово. Дашборд — ярлык «Bluetti Watcher» на Рабочем столе.");
        0
    }
}

fn windows_power(
    cfg: &config::Config,
    eco_scheme: Option<String>,
) -> PowerControl<crate::power::WindowsPower> {
    PowerControl::new(crate::power::WindowsPower, cfg.eco.clone(), eco_scheme, false)
}

fn find_station() -> Option<u64> {
    println!("Адрес не указан — ищу станции BLUETTI 10 секунд…");
    let rt = tokio::runtime::Builder::new_current_thread().enable_all().build().ok()?;
    let found = match rt.block_on(crate::ble::scan::scan(Duration::from_secs(10))) {
        Ok(f) => f,
        Err(e) => {
            eprintln!("Сканирование не удалось (Bluetooth выключен?): {e}");
            return None;
        }
    };
    match pick_station(&found) {
        Ok(a) => {
            println!("Найдена станция {}", config::format_mac(a));
            Some(a)
        }
        Err(PickError::None) => {
            eprintln!(
                "Станция не найдена. Включите её, отключите от телефона и повторите, \
                 или укажите адрес: install --mac AA:BB:CC:DD:EE:FF"
            );
            None
        }
        Err(PickError::Many(list)) => {
            eprintln!("Найдено несколько станций — выберите одну: install --mac <адрес>");
            for l in list {
                eprintln!("  {l}");
            }
            None
        }
    }
}

/// Скрытая команда `elevated-setup`, запускается из `install --elevated-setup` через UAC.
/// Консоли у неё нет — отчёт пишется в файл `report`, который читает родитель.
pub fn cmd_elevated_setup(user_sid: &str, bin_exe: &Path, report: &Path) -> u8 {
    let mut rep = Report::default();
    elevated_setup(&mut WinSys::new(), &Paths::shared_dir(), user_sid, bin_exe, &mut rep);
    let _ = std::fs::write(report, rep.lines.join("\n"));
    u8::from(rep.failed)
}

/// `uninstall`.
pub fn cmd_uninstall(paths: Paths, purge: bool) -> u8 {
    let mut sys = WinSys::new();
    let targets = match win::targets(paths) {
        Ok(t) => t,
        Err(e) => {
            eprintln!("удаление невозможно: {e}");
            return 1;
        }
    };
    println!("Удаление Bluetti Watcher:");
    let mut rep = Report::printing();
    uninstall(&mut sys, &targets, purge, windows_power, &mut rep);
    if rep.failed {
        println!("Удаление завершено с ошибками.");
        1
    } else {
        println!("Готово.");
        0
    }
}

/// `status`: всегда exit 0.
pub fn cmd_status(paths: &Paths) -> u8 {
    let port = config::load(&paths.config()).config.port;
    let t = Duration::from_secs(2);
    let health = http_get_json(port, "/api/health", t);
    let now = health.as_ref().and_then(|_| http_get_json(port, "/api/now", t));
    println!("{}", status_line(health.as_ref(), now.as_ref()));
    0
}

/// Минимальный `GET` на `127.0.0.1:port` (HTTP/1.0, без chunked) → JSON тела при 200.
pub fn http_get_json(port: u16, path: &str, timeout: Duration) -> Option<serde_json::Value> {
    use std::io::{Read, Write};
    use std::net::{SocketAddr, TcpStream};
    let addr = SocketAddr::from(([127, 0, 0, 1], port));
    let mut s = TcpStream::connect_timeout(&addr, timeout).ok()?;
    s.set_read_timeout(Some(timeout)).ok()?;
    let req = format!("GET {path} HTTP/1.0\r\nHost: 127.0.0.1:{port}\r\nConnection: close\r\n\r\n");
    s.write_all(req.as_bytes()).ok()?;
    let mut buf = Vec::new();
    s.read_to_end(&mut buf).ok()?;
    parse_http_json(&buf)
}

fn parse_http_json(resp: &[u8]) -> Option<serde_json::Value> {
    let split = resp.windows(4).position(|w| w == b"\r\n\r\n")?;
    let head = std::str::from_utf8(&resp[..split]).ok()?;
    let status = head.lines().next()?;
    if status.split_whitespace().nth(1) != Some("200") {
        return None;
    }
    serde_json::from_slice(&resp[split + 4..]).ok()
}

// ---------------------------------------------------------------------------------------------------------------
// Системная реализация
// ---------------------------------------------------------------------------------------------------------------

pub use win::WinSys;

#[cfg(windows)]
pub mod win {
    use std::mem::ManuallyDrop;
    use std::path::{Path, PathBuf};
    use std::time::Duration;

    use windows::Win32::Foundation::PROPERTYKEY;
    use windows::Win32::Foundation::{
        CloseHandle, E_ACCESSDENIED, HANDLE, HLOCAL, LocalFree, RPC_E_CHANGED_MODE,
    };
    use windows::Win32::Security::Authorization::{
        ConvertSidToStringSidW, ConvertStringSecurityDescriptorToSecurityDescriptorW, SDDL_REVISION_1,
        SE_FILE_OBJECT, SetNamedSecurityInfoW,
    };
    use windows::Win32::Security::{
        ACL, DACL_SECURITY_INFORMATION, GetSecurityDescriptorDacl, GetTokenInformation, PSECURITY_DESCRIPTOR,
        TOKEN_QUERY, TOKEN_USER, TokenUser,
    };
    use windows::Win32::System::Com::StructuredStorage::{PROPVARIANT, PropVariantClear};
    use windows::Win32::System::Com::{
        CLSCTX_INPROC_SERVER, COINIT_APARTMENTTHREADED, CoCreateInstance, CoInitializeEx, CoTaskMemAlloc,
        CoTaskMemFree, CoUninitialize, IPersistFile, STGM_READ,
    };
    use windows::Win32::System::Diagnostics::ToolHelp::{
        CreateToolhelp32Snapshot, PROCESSENTRY32W, Process32FirstW, Process32NextW, TH32CS_SNAPPROCESS,
    };
    use windows::Win32::System::TaskScheduler::{
        ITaskService, TASK_CREATE_OR_UPDATE, TASK_LOGON_INTERACTIVE_TOKEN, TaskScheduler,
    };
    use windows::Win32::System::Threading::{
        GetCurrentProcess, GetCurrentProcessId, GetExitCodeProcess, INFINITE, OpenProcess, OpenProcessToken,
        PROCESS_NAME_WIN32, PROCESS_QUERY_LIMITED_INFORMATION, PROCESS_SYNCHRONIZE, PROCESS_TERMINATE,
        QueryFullProcessImageNameW, TerminateProcess, WaitForSingleObject,
    };
    use windows::Win32::System::Variant::VT_LPWSTR;
    use windows::Win32::System::Variant::{VARIANT, VT_BSTR, VariantClear};
    use windows::Win32::UI::Shell::PropertiesSystem::IPropertyStore;
    use windows::Win32::UI::Shell::{
        FOLDERID_Desktop, FOLDERID_Programs, IShellLinkW, KF_FLAG_DEFAULT, SEE_MASK_NOASYNC,
        SEE_MASK_NOCLOSEPROCESS, SHELLEXECUTEINFOW, SHGetKnownFolderPath, ShellExecuteExW, ShellLink,
    };
    use windows::core::{BSTR, GUID, HSTRING, Interface, PCWSTR, PWSTR};

    use super::{Shortcut, Sys, Targets, TaskDelete};
    use crate::paths::Paths;

    /// `PKEY_AppUserModel_ID` = {9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3}, 5.
    pub const PKEY_APP_USER_MODEL_ID: PROPERTYKEY =
        PROPERTYKEY { fmtid: GUID::from_u128(0x9f4c2855_9f79_4b39_a8d0_e1d42de1d5f3), pid: 5 };
    const HR_NOT_FOUND: i32 = 0x8007_0002_u32 as i32;

    fn err(op: &str, e: windows::core::Error) -> String {
        format!("{op}: {} (0x{:08X})", e.message().trim(), e.code().0)
    }

    /// COM в текущем потоке на время операции.
    pub struct Com(bool);
    impl Com {
        pub fn init() -> Result<Self, String> {
            let hr = unsafe { CoInitializeEx(None, COINIT_APARTMENTTHREADED) };
            if hr.is_err() && hr != RPC_E_CHANGED_MODE {
                return Err(err("COM", hr.into()));
            }
            Ok(Self(hr.is_ok()))
        }
    }
    impl Drop for Com {
        fn drop(&mut self) {
            if self.0 {
                unsafe { CoUninitialize() };
            }
        }
    }

    /// `VT_LPWSTR` со строкой в CoTaskMem (как `InitPropVariantFromString`); освобождать `PropVariantClear`.
    fn lpwstr_variant(s: &str) -> Result<PROPVARIANT, String> {
        let wide: Vec<u16> = s.encode_utf16().chain(Some(0)).collect();
        let mut pv = PROPVARIANT::default();
        unsafe {
            let p = CoTaskMemAlloc(wide.len() * 2) as *mut u16;
            if p.is_null() {
                return Err("нет памяти".into());
            }
            std::ptr::copy_nonoverlapping(wide.as_ptr(), p, wide.len());
            let inner = &mut *pv.Anonymous.Anonymous;
            inner.vt = VT_LPWSTR;
            inner.Anonymous.pwszVal = PWSTR(p);
        }
        Ok(pv)
    }

    fn bstr_variant(s: &str) -> VARIANT {
        let mut v = VARIANT::default();
        unsafe {
            let inner = &mut *v.Anonymous.Anonymous;
            inner.vt = VT_BSTR;
            inner.Anonymous.bstrVal = ManuallyDrop::new(BSTR::from(s));
        }
        v
    }

    fn scheduler() -> Result<ITaskService, String> {
        let svc: ITaskService = unsafe { CoCreateInstance(&TaskScheduler, None, CLSCTX_INPROC_SERVER) }
            .map_err(|e| err("планировщик заданий", e))?;
        let empty = VARIANT::default();
        unsafe { svc.Connect(&empty, &empty, &empty, &empty) }.map_err(|e| err("планировщик заданий", e))?;
        Ok(svc)
    }

    pub fn known_folder(id: &GUID) -> Result<PathBuf, String> {
        unsafe {
            let p = SHGetKnownFolderPath(id, KF_FLAG_DEFAULT, None).map_err(|e| err("папка", e))?;
            let s = p.to_string();
            CoTaskMemFree(Some(p.0 as _));
            s.map(PathBuf::from).map_err(|e| e.to_string())
        }
    }

    pub fn current_user_sid() -> Result<String, String> {
        unsafe {
            let mut tok = HANDLE::default();
            OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &mut tok).map_err(|e| err("токен", e))?;
            let mut len = 0u32;
            let _ = GetTokenInformation(tok, TokenUser, None, 0, &mut len);
            let mut buf = vec![0u64; (len as usize).div_ceil(8)];
            let r = GetTokenInformation(tok, TokenUser, Some(buf.as_mut_ptr() as _), len, &mut len);
            let _ = CloseHandle(tok);
            r.map_err(|e| err("токен", e))?;
            let tu = &*(buf.as_ptr() as *const TOKEN_USER);
            let mut s = PWSTR::null();
            ConvertSidToStringSidW(tu.User.Sid, &mut s).map_err(|e| err("SID", e))?;
            let out = s.to_string().map_err(|e| e.to_string());
            LocalFree(Some(HLOCAL(s.0 as _)));
            out
        }
    }

    pub fn targets(paths: Paths) -> Result<Targets, String> {
        let _com = Com::init()?;
        Ok(Targets {
            paths,
            current_exe: std::env::current_exe().map_err(|e| format!("путь exe: {e}"))?,
            user_sid: current_user_sid()?,
            desktop: known_folder(&FOLDERID_Desktop).ok(),
            programs: known_folder(&FOLDERID_Programs).ok(),
            shared: Paths::shared_dir(),
        })
    }

    /// Создать ярлык `path` (перезаписывает).
    pub fn create_shortcut(path: &Path, link: &Shortcut) -> Result<(), String> {
        let _com = Com::init()?;
        unsafe {
            let sl: IShellLinkW = CoCreateInstance(&ShellLink, None, CLSCTX_INPROC_SERVER)
                .map_err(|e| err("IShellLinkW", e))?;
            let target = HSTRING::from(link.target.as_os_str());
            sl.SetPath(&target).map_err(|e| err("SetPath", e))?;
            sl.SetArguments(&HSTRING::from(link.args.as_str())).map_err(|e| err("SetArguments", e))?;
            sl.SetWorkingDirectory(&HSTRING::from(link.workdir.as_os_str()))
                .map_err(|e| err("SetWorkingDirectory", e))?;
            sl.SetIconLocation(&target, 0).map_err(|e| err("SetIconLocation", e))?;
            sl.SetDescription(&HSTRING::from(link.description.as_str()))
                .map_err(|e| err("SetDescription", e))?;
            if let Some(id) = &link.app_id {
                let store: IPropertyStore = sl.cast().map_err(|e| err("IPropertyStore", e))?;
                let mut pv = lpwstr_variant(id)?;
                let r = store.SetValue(&PKEY_APP_USER_MODEL_ID, &pv);
                let _ = PropVariantClear(&mut pv);
                r.map_err(|e| err("AppUserModel_ID", e))?;
                store.Commit().map_err(|e| err("IPropertyStore::Commit", e))?;
            }
            if let Some(dir) = path.parent() {
                std::fs::create_dir_all(dir).map_err(|e| format!("{}: {e}", dir.display()))?;
            }
            let file: IPersistFile = sl.cast().map_err(|e| err("IPersistFile", e))?;
            file.Save(&HSTRING::from(path.as_os_str()), true).map_err(|e| err("сохранение ярлыка", e))?;
        }
        Ok(())
    }

    /// Прочитать ярлык: (цель, аргументы, AUMID). Для тестов и проверки установки.
    pub fn read_shortcut(path: &Path) -> Result<(String, String, Option<String>), String> {
        let _com = Com::init()?;
        unsafe {
            let sl: IShellLinkW = CoCreateInstance(&ShellLink, None, CLSCTX_INPROC_SERVER)
                .map_err(|e| err("IShellLinkW", e))?;
            let file: IPersistFile = sl.cast().map_err(|e| err("IPersistFile", e))?;
            file.Load(&HSTRING::from(path.as_os_str()), STGM_READ).map_err(|e| err("чтение ярлыка", e))?;
            let mut buf = [0u16; 1024];
            sl.GetPath(&mut buf, std::ptr::null_mut(), 0).map_err(|e| err("GetPath", e))?;
            let target = from_wide(&buf);
            let mut buf = [0u16; 1024];
            sl.GetArguments(&mut buf).map_err(|e| err("GetArguments", e))?;
            let args = from_wide(&buf);
            let store: IPropertyStore = sl.cast().map_err(|e| err("IPropertyStore", e))?;
            let mut pv = store.GetValue(&PKEY_APP_USER_MODEL_ID).map_err(|e| err("AppUserModel_ID", e))?;
            let app_id = {
                let inner = &*pv.Anonymous.Anonymous;
                if inner.vt == VT_LPWSTR { inner.Anonymous.pwszVal.to_string().ok() } else { None }
            };
            let _ = PropVariantClear(&mut pv);
            Ok((target, args, app_id))
        }
    }

    fn from_wide(buf: &[u16]) -> String {
        let end = buf.iter().position(|&c| c == 0).unwrap_or(buf.len());
        String::from_utf16_lossy(&buf[..end])
    }

    /// Запустить `elevated-setup` через UAC и дождаться: (код выхода, строки отчёта).
    pub fn run_elevated_setup(t: &Targets) -> Result<(u32, Vec<String>), String> {
        let report =
            std::env::temp_dir().join(format!("bluetti-watcher-elevated-{}.txt", std::process::id()));
        let _ = std::fs::remove_file(&report);
        let params = format!(
            "elevated-setup --user-sid {} --bin-exe \"{}\" --report \"{}\"",
            t.user_sid,
            t.bin_exe().display(),
            report.display()
        );
        let exe = HSTRING::from(t.current_exe.as_os_str());
        let params = HSTRING::from(params);
        let verb = HSTRING::from("runas");
        let mut info = SHELLEXECUTEINFOW {
            cbSize: std::mem::size_of::<SHELLEXECUTEINFOW>() as u32,
            fMask: SEE_MASK_NOCLOSEPROCESS | SEE_MASK_NOASYNC,
            lpVerb: PCWSTR(verb.as_ptr()),
            lpFile: PCWSTR(exe.as_ptr()),
            lpParameters: PCWSTR(params.as_ptr()),
            nShow: 0, // SW_HIDE
            ..Default::default()
        };
        unsafe {
            ShellExecuteExW(&mut info).map_err(|e| err("запуск с правами администратора", e))?;
            if info.hProcess.is_invalid() {
                return Err("процесс не запущен".into());
            }
            WaitForSingleObject(info.hProcess, INFINITE);
            let mut code = 1u32;
            let _ = GetExitCodeProcess(info.hProcess, &mut code);
            let _ = CloseHandle(info.hProcess);
            let lines = std::fs::read_to_string(&report)
                .map(|s| s.lines().map(str::to_string).collect())
                .unwrap_or_else(|_| vec!["  (отчёт шага администратора не найден)".to_string()]);
            let _ = std::fs::remove_file(&report);
            Ok((code, lines))
        }
    }

    pub struct WinSys;

    impl WinSys {
        pub fn new() -> Self {
            Self
        }
    }

    impl Default for WinSys {
        fn default() -> Self {
            Self::new()
        }
    }

    impl Sys for WinSys {
        fn stop_processes(&mut self, exe: &Path) -> Result<usize, String> {
            if !exe.exists() {
                return Ok(0);
            }
            let me = unsafe { GetCurrentProcessId() };
            let mut stopped = 0;
            unsafe {
                let snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0)
                    .map_err(|e| err("список процессов", e))?;
                let mut entry = PROCESSENTRY32W {
                    dwSize: std::mem::size_of::<PROCESSENTRY32W>() as u32,
                    ..Default::default()
                };
                let mut more = Process32FirstW(snap, &mut entry).is_ok();
                let mut result = Ok(());
                while more {
                    let pid = entry.th32ProcessID;
                    if pid != me && pid != 0 && process_name(&entry).eq_ignore_ascii_case(super::EXE_NAME) {
                        let access =
                            PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_TERMINATE | PROCESS_SYNCHRONIZE;
                        if let Ok(h) = OpenProcess(access, false, pid) {
                            let mut buf = [0u16; 1024];
                            let mut len = buf.len() as u32;
                            let image = QueryFullProcessImageNameW(
                                h,
                                PROCESS_NAME_WIN32,
                                PWSTR(buf.as_mut_ptr()),
                                &mut len,
                            )
                            .ok()
                            .map(|()| PathBuf::from(String::from_utf16_lossy(&buf[..len as usize])));
                            if image.is_some_and(|p| super::same_file(&p, exe)) {
                                match TerminateProcess(h, 1) {
                                    Ok(()) => {
                                        WaitForSingleObject(h, 5000);
                                        stopped += 1;
                                    }
                                    Err(e) => result = Err(err(&format!("процесс {pid}"), e)),
                                }
                            }
                            let _ = CloseHandle(h);
                        }
                    }
                    more = Process32NextW(snap, &mut entry).is_ok();
                }
                let _ = CloseHandle(snap);
                result.map(|()| stopped)
            }
        }

        fn register_task(
            &mut self,
            name: &str,
            xml: &str,
            user_sid: &str,
            sddl: Option<&str>,
        ) -> Result<(), String> {
            let _com = Com::init()?;
            let svc = scheduler()?;
            let folder = unsafe { svc.GetFolder(&BSTR::from("\\")) }.map_err(|e| err("папка задач", e))?;
            let mut user = bstr_variant(user_sid);
            let empty = VARIANT::default();
            let mut sd = sddl.map(bstr_variant).unwrap_or_default();
            let r = unsafe {
                folder.RegisterTask(
                    &BSTR::from(name),
                    &BSTR::from(xml),
                    TASK_CREATE_OR_UPDATE.0,
                    &user,
                    &empty,
                    TASK_LOGON_INTERACTIVE_TOKEN,
                    &sd,
                )
            };
            unsafe {
                let _ = VariantClear(&mut user);
                let _ = VariantClear(&mut sd);
            }
            r.map(|_| ()).map_err(|e| err("регистрация", e))
        }

        fn run_task(&mut self, name: &str) -> Result<(), String> {
            let _com = Com::init()?;
            let svc = scheduler()?;
            unsafe {
                let task = svc
                    .GetFolder(&BSTR::from("\\"))
                    .and_then(|f| f.GetTask(&BSTR::from(name)))
                    .map_err(|e| err("задача", e))?;
                task.Run(&VARIANT::default()).map(|_| ()).map_err(|e| err("запуск", e))
            }
        }

        fn delete_task(&mut self, name: &str) -> Result<TaskDelete, String> {
            let _com = Com::init()?;
            let svc = scheduler()?;
            let folder = unsafe { svc.GetFolder(&BSTR::from("\\")) }.map_err(|e| err("папка задач", e))?;
            match unsafe { folder.DeleteTask(&BSTR::from(name), 0) } {
                Ok(()) => Ok(TaskDelete::Deleted),
                Err(e) if e.code().0 == HR_NOT_FOUND => Ok(TaskDelete::Missing),
                Err(e) if e.code() == E_ACCESSDENIED => Ok(TaskDelete::AccessDenied),
                Err(e) => Err(err("удаление", e)),
            }
        }

        fn create_shortcut(&mut self, path: &Path, link: &Shortcut) -> Result<(), String> {
            create_shortcut(path, link)
        }

        fn service_alive(&mut self, port: u16) -> bool {
            crate::window::service_alive(port, Duration::from_millis(500))
        }

        fn sleep(&mut self, d: Duration) {
            std::thread::sleep(d);
        }

        fn hibernate_on(&mut self) -> Result<(), String> {
            use std::os::windows::process::CommandExt;
            const CREATE_NO_WINDOW: u32 = 0x0800_0000;
            let out = std::process::Command::new("powercfg")
                .args(["/h", "on"])
                .creation_flags(CREATE_NO_WINDOW)
                .output()
                .map_err(|e| format!("powercfg: {e}"))?;
            if out.status.success() {
                Ok(())
            } else {
                Err(format!("powercfg /h on: код {}", out.status.code().unwrap_or(-1)))
            }
        }

        fn grant_modify(&mut self, path: &Path, sid: &str) -> Result<(), String> {
            // 0x1301bf = «Изменение» (чтение, запись, выполнение, удаление); наследуемые ACE каталога остаются.
            let sddl = HSTRING::from(format!("D:(A;;0x1301bf;;;{sid})"));
            unsafe {
                let mut psd = PSECURITY_DESCRIPTOR::default();
                ConvertStringSecurityDescriptorToSecurityDescriptorW(&sddl, SDDL_REVISION_1, &mut psd, None)
                    .map_err(|e| err("SDDL", e))?;
                let mut present = windows::core::BOOL(0);
                let mut defaulted = windows::core::BOOL(0);
                let mut dacl: *mut ACL = std::ptr::null_mut();
                let r = GetSecurityDescriptorDacl(psd, &mut present, &mut dacl, &mut defaulted)
                    .map_err(|e| err("DACL", e))
                    .and_then(|()| {
                        let e = SetNamedSecurityInfoW(
                            &HSTRING::from(path.as_os_str()),
                            SE_FILE_OBJECT,
                            DACL_SECURITY_INFORMATION,
                            None,
                            None,
                            Some(dacl),
                            None,
                        );
                        if e.0 == 0 {
                            Ok(())
                        } else {
                            Err(err("SetNamedSecurityInfoW", e.to_hresult().into()))
                        }
                    });
                LocalFree(Some(HLOCAL(psd.0)));
                r
            }
        }

        fn remove_dir_later(&mut self, dir: &Path) -> Result<(), String> {
            // Единственное место с cmd: exe, который удаляется, — это мы сами; ждём выхода ~2 с и удаляем каталог.
            use std::os::windows::process::CommandExt;
            const DETACHED_PROCESS: u32 = 0x0000_0008;
            const CREATE_NO_WINDOW: u32 = 0x0800_0000;
            let cmd = format!("ping -n 3 127.0.0.1 >nul & rmdir /s /q \"{}\"", dir.display());
            std::process::Command::new("cmd")
                .arg("/c")
                .raw_arg(cmd)
                .creation_flags(DETACHED_PROCESS | CREATE_NO_WINDOW)
                .stdin(std::process::Stdio::null())
                .stdout(std::process::Stdio::null())
                .stderr(std::process::Stdio::null())
                .spawn()
                .map(|_| ())
                .map_err(|e| format!("cmd: {e}"))
        }
    }

    fn process_name(e: &PROCESSENTRY32W) -> String {
        from_wide(&e.szExeFile)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::power::{BALANCED, ECO_SCHEME_NAME, OVERLAY_NONE};

    fn found(name: &str, address: u64) -> Found {
        Found { name: name.into(), address, rssi: -60 }
    }

    #[test]
    fn pick_one_none_many() {
        assert_eq!(pick_station(&[]), Err(PickError::None));
        assert_eq!(pick_station(&[found("AC180P2435", 0xAABBCCDDEEFF)]), Ok(0xAABBCCDDEEFF));
        match pick_station(&[found("AC180P1", 1), found("EB3A2", 0x0102030405)]) {
            Err(PickError::Many(list)) => {
                assert_eq!(list.len(), 2);
                assert!(list[1].contains("00:01:02:03:04:05"), "{list:?}");
                assert!(list[0].starts_with("AC180P1"));
            }
            other => panic!("{other:?}"),
        }
    }

    #[test]
    fn merge_config_keeps_other_keys() {
        let old = "mac = \"11:22:33:44:55:66\"\npoll_sec = 30 # часто\n\n[levels]\nlow = 25\n";
        let new = merge_config(Some(old), 0xAABBCCDDEEFF, Some(9000)).unwrap();
        let t: toml::Table = new.parse().unwrap();
        assert_eq!(t["mac"].as_str(), Some("AA:BB:CC:DD:EE:FF"));
        assert_eq!(t["port"].as_integer(), Some(9000));
        assert_eq!(t["poll_sec"].as_integer(), Some(30));
        assert_eq!(t["levels"]["low"].as_integer(), Some(25));
        // Результат читается загрузчиком конфигурации.
        let loaded = config::parse(&new);
        assert!(loaded.warnings.is_empty(), "{:?}", loaded.warnings);
        assert_eq!(loaded.config.mac, Some(0xAABBCCDDEEFF));
        assert_eq!(loaded.config.port, 9000);
        assert_eq!(loaded.config.poll_sec, 30);
    }

    #[test]
    fn merge_config_without_port_keeps_existing_port() {
        let new = merge_config(Some("port = 8800\n"), 1, None).unwrap();
        let t: toml::Table = new.parse().unwrap();
        assert_eq!(t["port"].as_integer(), Some(8800));
        assert_eq!(t["mac"].as_str(), Some("00:00:00:00:00:01"));
        let fresh = merge_config(None, 1, None).unwrap();
        assert_eq!(fresh.trim(), "mac = \"00:00:00:00:00:01\"");
        assert!(merge_config(Some("mac = = ="), 1, None).is_err());
    }

    #[test]
    fn autostart_task_xml() {
        let exe = Path::new(r"C:\Users\Me & Co\AppData\Local\bluetti-watcher\bin\bluetti-watcher.exe");
        let x = TaskSpec::autostart(exe, "S-1-5-21-1-2-3-1001").to_xml();
        for want in [
            "<LogonTrigger><Enabled>true</Enabled><UserId>S-1-5-21-1-2-3-1001</UserId></LogonTrigger>",
            "<LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel>",
            "<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>",
            "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>",
            "<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>",
            "<RestartOnFailure><Interval>PT1M</Interval><Count>3</Count></RestartOnFailure>",
            "<MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>",
            "<Hidden>true</Hidden>",
            "<StartWhenAvailable>true</StartWhenAvailable>",
            r#"<Command>"C:\Users\Me &amp; Co\AppData\Local\bluetti-watcher\bin\bluetti-watcher.exe"</Command>"#,
            "<Arguments>run</Arguments>",
            r"<WorkingDirectory>C:\Users\Me &amp; Co\AppData\Local\bluetti-watcher\bin</WorkingDirectory>",
        ] {
            assert!(x.contains(want), "нет {want}\n{x}");
        }
        assert!(!x.contains("HighestAvailable"));
    }

    #[test]
    fn gpu_task_xml_and_sddl() {
        let exe = Path::new(r"C:\x\bin\bluetti-watcher.exe");
        let x = TaskSpec::gpu(exe, "S-1-5-21-9").to_xml();
        assert!(x.contains("<Triggers></Triggers>"), "{x}");
        assert!(x.contains("<RunLevel>HighestAvailable</RunLevel>"));
        assert!(x.contains("<AllowStartOnDemand>true</AllowStartOnDemand>"));
        assert!(x.contains("<Hidden>true</Hidden>"));
        assert!(x.contains("<Arguments>gpu-apply</Arguments>"));
        assert!(!x.contains("RestartOnFailure"));
        assert_eq!(gpu_task_sddl("S-1-5-21-9"), "D:P(A;;FA;;;BA)(A;;FA;;;SY)(A;;GRGX;;;S-1-5-21-9)");
    }

    #[test]
    fn status_lines() {
        use serde_json::json;
        assert_eq!(status_line(None, None), "сервис: не запущен");
        let h = json!({"ok": true});
        let now = json!({"ts": "2026-10-14T21:05:00+03:00", "stale": false, "soc": 87, "mains": "on",
                         "eco": "none", "manual": "auto", "dryRun": false});
        assert_eq!(
            status_line(Some(&h), Some(&now)),
            "сервис: работает | сеть: есть | заряд 87 % | eco: нет | показание 21:05"
        );
        let now = json!({"ts": "2026-10-14T19:00:00+03:00", "stale": true, "soc": 40, "mains": "off",
                         "eco": "deep", "manual": "max", "dryRun": true});
        assert_eq!(
            status_line(Some(&h), Some(&now)),
            "сервис: работает | сеть: нет | заряд 40 % | eco: глубокий | режим: максимум | показание 19:00 (устарело) | dry run"
        );
        let now = json!({"ts": null, "soc": null, "mains": "unknown", "eco": "none"});
        assert_eq!(
            status_line(Some(&h), Some(&now)),
            "сервис: работает | сеть: неизвестно | заряд — | eco: нет | показаний нет"
        );
    }

    #[test]
    fn http_response_parsing() {
        let ok = b"HTTP/1.1 200 OK\r\ncontent-type: application/json\r\n\r\n{\"ok\":true}";
        assert_eq!(parse_http_json(ok), Some(serde_json::json!({"ok": true})));
        assert_eq!(parse_http_json(b"HTTP/1.1 403 Forbidden\r\n\r\n{}"), None);
        assert_eq!(parse_http_json(b"garbage"), None);
    }

    #[test]
    fn status_against_dead_port_is_not_running() {
        // Порт, на котором точно никто не слушает: заняли и сразу освободили.
        let port = std::net::TcpListener::bind("127.0.0.1:0").unwrap().local_addr().unwrap().port();
        assert!(http_get_json(port, "/api/health", Duration::from_millis(300)).is_none());
    }

    // ---- оркестрация на фейках ----

    #[derive(Default)]
    struct FakeSys {
        calls: Vec<String>,
        alive: bool,
        fail_task: bool,
        deny_gpu_delete: bool,
        shortcuts: Vec<(PathBuf, Shortcut)>,
        granted: Vec<(PathBuf, String)>,
        tasks: Vec<(String, String, Option<String>)>,
    }

    impl Sys for FakeSys {
        fn stop_processes(&mut self, exe: &Path) -> Result<usize, String> {
            self.calls.push(format!("stop {}", exe.file_name().unwrap().to_string_lossy()));
            Ok(0)
        }
        fn register_task(
            &mut self,
            name: &str,
            xml: &str,
            _sid: &str,
            sddl: Option<&str>,
        ) -> Result<(), String> {
            self.calls.push(format!("register {name}"));
            if self.fail_task {
                return Err("доступ запрещён".into());
            }
            self.tasks.push((name.into(), xml.into(), sddl.map(str::to_string)));
            Ok(())
        }
        fn run_task(&mut self, name: &str) -> Result<(), String> {
            self.calls.push(format!("run {name}"));
            Ok(())
        }
        fn delete_task(&mut self, name: &str) -> Result<TaskDelete, String> {
            self.calls.push(format!("delete {name}"));
            Ok(if name == gpu::TASK_NAME && self.deny_gpu_delete {
                TaskDelete::AccessDenied
            } else {
                TaskDelete::Deleted
            })
        }
        fn create_shortcut(&mut self, path: &Path, link: &Shortcut) -> Result<(), String> {
            self.calls.push("shortcut".into());
            std::fs::write(path, b"lnk").map_err(|e| e.to_string())?;
            self.shortcuts.push((path.to_path_buf(), link.clone()));
            Ok(())
        }
        fn service_alive(&mut self, _port: u16) -> bool {
            self.alive
        }
        fn sleep(&mut self, _d: Duration) {}
        fn hibernate_on(&mut self) -> Result<(), String> {
            self.calls.push("hibernate".into());
            Err("нет прав".into())
        }
        fn grant_modify(&mut self, path: &Path, sid: &str) -> Result<(), String> {
            self.granted.push((path.to_path_buf(), sid.into()));
            Ok(())
        }
        fn remove_dir_later(&mut self, dir: &Path) -> Result<(), String> {
            self.calls.push(format!("remove-later {}", dir.display()));
            Ok(())
        }
    }

    /// Минимальный `PowerBackend` в памяти.
    #[derive(Default)]
    struct FakePower {
        active: String,
        schemes: Vec<(String, String)>,
        next: u32,
        log: std::rc::Rc<std::cell::RefCell<Vec<String>>>,
    }

    impl PowerBackend for FakePower {
        fn active_scheme(&self) -> Result<String, String> {
            Ok(self.active.clone())
        }
        fn set_active_scheme(&mut self, guid: &str) -> Result<(), String> {
            self.log.borrow_mut().push(format!("active {guid}"));
            self.active = guid.into();
            Ok(())
        }
        fn schemes(&self) -> Result<Vec<(String, String)>, String> {
            Ok(self.schemes.clone())
        }
        fn duplicate_scheme(&mut self, _source: &str) -> Result<String, String> {
            self.next += 1;
            let g = format!("00000000-0000-0000-0000-{:012}", self.next);
            self.schemes.push((g.clone(), String::new()));
            Ok(g)
        }
        fn set_friendly_name(&mut self, guid: &str, name: &str) -> Result<(), String> {
            for s in &mut self.schemes {
                if s.0 == guid {
                    s.1 = name.into();
                }
            }
            Ok(())
        }
        fn write_ac_value(&mut self, _: &str, _: &str, _: &str, _: u32) -> Result<(), String> {
            Ok(())
        }
        fn delete_scheme(&mut self, guid: &str) -> Result<(), String> {
            self.log.borrow_mut().push(format!("delete {guid}"));
            self.schemes.retain(|s| s.0 != guid);
            Ok(())
        }
        fn overlay(&self) -> Result<String, String> {
            Ok(OVERLAY_NONE.into())
        }
        fn set_overlay(&mut self, guid: &str) -> Result<(), String> {
            self.log.borrow_mut().push(format!("overlay {guid}"));
            Ok(())
        }
        fn gpu_task_installed(&self) -> bool {
            false
        }
        fn gpu_request(&mut self, _level: EcoLevel) -> Result<(), String> {
            Ok(())
        }
    }

    struct Tmp(PathBuf);
    impl Tmp {
        fn new(tag: &str) -> Self {
            let base = std::env::temp_dir().join(format!("bw-install-{tag}-{}", std::process::id()));
            let _ = std::fs::remove_dir_all(&base);
            std::fs::create_dir_all(&base).unwrap();
            Self(base)
        }
    }
    impl Drop for Tmp {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }

    fn targets(tmp: &Path) -> Targets {
        let src = tmp.join("src");
        std::fs::create_dir_all(&src).unwrap();
        let exe = src.join(EXE_NAME);
        std::fs::write(&exe, b"MZ fake exe").unwrap();
        for d in ["desktop", "programs"] {
            std::fs::create_dir_all(tmp.join(d)).unwrap();
        }
        Targets {
            paths: Paths { data: tmp.join("data") },
            current_exe: exe,
            user_sid: "S-1-5-21-7".into(),
            desktop: Some(tmp.join("desktop")),
            programs: Some(tmp.join("programs")),
            shared: tmp.join("shared"),
        }
    }

    fn power_factory(
        backend: FakePower,
    ) -> impl FnOnce(&config::Config, Option<String>) -> PowerControl<FakePower> {
        move |cfg, scheme| PowerControl::new(backend, cfg.eco.clone(), scheme, false)
    }

    #[test]
    fn install_full_flow() {
        let tmp = Tmp::new("full");
        let t = targets(&tmp.0);
        std::fs::create_dir_all(&t.paths.data).unwrap();
        std::fs::write(t.paths.config(), "poll_sec = 45\n").unwrap();
        let mut sys = FakeSys { alive: true, ..Default::default() };
        let mut rep = Report::default();
        let backend = FakePower { active: BALANCED.into(), ..Default::default() };
        install(&mut sys, &t, 0xAABBCCDDEEFF, Some(8899), power_factory(backend), &mut rep);
        assert!(!rep.failed, "{:#?}", rep.lines);

        assert_eq!(std::fs::read(t.bin_exe()).unwrap(), b"MZ fake exe");
        let cfg = config::load(&t.paths.config()).config;
        assert_eq!((cfg.mac, cfg.port, cfg.poll_sec), (Some(0xAABBCCDDEEFF), 8899, 45));
        let store = Store::open(&t.paths.db()).unwrap();
        assert!(store.get_meta::<String>("eco_scheme").is_some());

        assert_eq!(
            sys.calls,
            [
                "stop bluetti-watcher.exe",
                "register BluettiWatcher",
                "shortcut",
                "shortcut",
                "run BluettiWatcher"
            ]
        );
        let (_, xml, sddl) = &sys.tasks[0];
        assert!(xml.contains(&t.bin_exe().display().to_string()) && sddl.is_none());
        let start = &sys.shortcuts[1];
        assert_eq!(start.0, tmp.0.join("programs").join(SHORTCUT_FILE));
        assert_eq!(start.1.app_id.as_deref(), Some(crate::notify::APP_ID));
        assert_eq!(start.1.args, "open");
        assert_eq!(start.1.target, t.bin_exe());
        assert_eq!(start.1.workdir, t.paths.bin_dir());
        assert_eq!(sys.shortcuts[0].1.app_id, None);
        assert!(
            rep.lines.last().unwrap().contains("сервис запущен: http://127.0.0.1:8899/"),
            "{:#?}",
            rep.lines
        );
    }

    #[test]
    fn install_reuses_existing_scheme_and_stops_on_task_failure() {
        let tmp = Tmp::new("taskfail");
        let t = targets(&tmp.0);
        let mut sys = FakeSys { fail_task: true, ..Default::default() };
        let mut rep = Report::default();
        let existing = "11111111-2222-3333-4444-555555555555".to_string();
        let backend = FakePower {
            active: BALANCED.into(),
            schemes: vec![(existing.clone(), ECO_SCHEME_NAME.into())],
            ..Default::default()
        };
        install(&mut sys, &t, 1, None, power_factory(backend), &mut rep);
        assert!(rep.failed);
        let store = Store::open(&t.paths.db()).unwrap();
        assert_eq!(store.get_meta::<String>("eco_scheme"), Some(existing));
        // Ярлыки создаются, но сервис не запускается.
        assert!(sys.calls.contains(&"shortcut".to_string()));
        assert!(!sys.calls.iter().any(|c| c.starts_with("run")), "{:?}", sys.calls);
    }

    #[test]
    fn elevated_steps_continue_after_failure() {
        let tmp = Tmp::new("elev");
        let shared = tmp.0.join("ProgramData").join("bluetti-watcher");
        let mut sys = FakeSys::default();
        let mut rep = Report::default();
        elevated_setup(&mut sys, &shared, "S-1-5-21-7", Path::new(r"C:\b\bluetti-watcher.exe"), &mut rep);
        assert!(rep.failed, "гибернация упала в фейке");
        assert_eq!(std::fs::read_to_string(shared.join("gpu-request")).unwrap(), "normal");
        assert_eq!(sys.granted, [(shared.join("gpu-request"), "S-1-5-21-7".to_string())]);
        let (name, xml, sddl) = &sys.tasks[0];
        assert_eq!(name, gpu::TASK_NAME);
        assert!(xml.contains("HighestAvailable"));
        assert_eq!(sddl.as_deref(), Some("D:P(A;;FA;;;BA)(A;;FA;;;SY)(A;;GRGX;;;S-1-5-21-7)"));
        assert_eq!(rep.lines.len(), 3);
    }

    #[test]
    fn uninstall_restores_power_and_purges() {
        let tmp = Tmp::new("uninst");
        let t = targets(&tmp.0);
        t.paths.ensure().unwrap();
        std::fs::write(t.paths.config(), "mac = \"AA:BB:CC:DD:EE:FF\"\n").unwrap();
        let eco = "00000000-0000-0000-0000-000000000009".to_string();
        {
            let store = Store::open(&t.paths.db()).unwrap();
            store.set_meta("eco_scheme", &eco).unwrap();
            let r = PowerRestore {
                prev_scheme: Some(BALANCED.into()),
                prev_overlay: Some(OVERLAY_NONE.into()),
                gpu_was: None,
            };
            store.set_meta("power_restore", &r).unwrap();
        }
        for d in ["desktop", "programs"] {
            std::fs::write(tmp.0.join(d).join(SHORTCUT_FILE), b"lnk").unwrap();
        }
        let log = std::rc::Rc::new(std::cell::RefCell::new(Vec::new()));
        let backend = FakePower {
            active: eco.clone(),
            schemes: vec![
                (BALANCED.into(), "Сбалансированная".into()),
                (eco.clone(), ECO_SCHEME_NAME.into()),
            ],
            log: log.clone(),
            ..Default::default()
        };
        let mut sys = FakeSys { deny_gpu_delete: true, ..Default::default() };
        let mut rep = Report::default();
        uninstall(&mut sys, &t, true, power_factory(backend), &mut rep);
        assert!(!rep.failed, "{:#?}", rep.lines);
        assert_eq!(
            sys.calls,
            ["delete BluettiWatcher", "stop bluetti-watcher.exe", "delete BluettiWatcherGpu"]
        );
        let log = log.borrow();
        assert_eq!(log.first().map(String::as_str), Some(format!("active {BALANCED}").as_str()), "{log:?}");
        assert!(log.contains(&format!("delete {eco}")), "{log:?}");
        assert!(
            rep.lines.iter().any(|l| l.contains("schtasks /delete /tn BluettiWatcherGpu")),
            "{:#?}",
            rep.lines
        );
        assert!(!tmp.0.join("desktop").join(SHORTCUT_FILE).exists());
        assert!(!tmp.0.join("programs").join(SHORTCUT_FILE).exists());
        assert!(!t.paths.data.exists(), "--purge удаляет данные");
    }

    #[test]
    fn uninstall_purge_from_bin_schedules_removal() {
        let tmp = Tmp::new("selfpurge");
        let mut t = targets(&tmp.0);
        std::fs::create_dir_all(t.paths.bin_dir()).unwrap();
        std::fs::write(t.bin_exe(), b"MZ").unwrap();
        t.current_exe = t.bin_exe();
        let mut sys = FakeSys::default();
        let mut rep = Report::default();
        uninstall(&mut sys, &t, true, power_factory(FakePower::default()), &mut rep);
        assert!(sys.calls.iter().any(|c| c.starts_with("remove-later")), "{:?}", sys.calls);
        assert!(t.paths.data.exists());
    }

    #[test]
    fn inside_and_same_file() {
        let tmp = Tmp::new("paths");
        let a = tmp.0.join("data").join("bin");
        std::fs::create_dir_all(&a).unwrap();
        assert!(is_inside(&a.join("x.exe"), &tmp.0.join("data")));
        assert!(!is_inside(&tmp.0.join("database"), &tmp.0.join("data")));
        assert!(same_file(&a, &tmp.0.join("data").join(".").join("bin")));
    }

    /// Создаёт ярлык во временной папке и читает его назад (реальный COM, без изменений вне temp).
    #[test]
    #[ignore]
    fn shortcut_roundtrip_in_temp() {
        let tmp = Tmp::new("lnk");
        let exe = std::env::current_exe().unwrap();
        let path = tmp.0.join(SHORTCUT_FILE);
        win::create_shortcut(&path, &Shortcut::open(&exe, Some(crate::notify::APP_ID))).unwrap();
        let (target, args, app_id) = win::read_shortcut(&path).unwrap();
        assert!(same_file(Path::new(&target), &exe), "{target}");
        assert_eq!(args, "open");
        assert_eq!(app_id.as_deref(), Some(crate::notify::APP_ID));
        let plain = tmp.0.join("plain.lnk");
        win::create_shortcut(&plain, &Shortcut::open(&exe, None)).unwrap();
        assert_eq!(win::read_shortcut(&plain).unwrap().2, None);
    }
}
