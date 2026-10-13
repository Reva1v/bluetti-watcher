---
description: "Task list for 001-rust-station-watcher"
---

# Tasks: Фоновый монитор станции BLUETTI AC180P

**Input**: `specs/001-rust-station-watcher/` — plan.md, spec.md, research.md, data-model.md, contracts/, quickstart.md

**Tests**: обязательны по конституции (принцип V) для протокола и логики решений; пишутся до реализации модуля.

**Organization**: по историям из spec.md. Порядок: US1 (P1) → US4 (P1) → US2 (P2) → US3 (P3).

## Format: `[ID] [P?] [Story] Description`

- **[P]**: можно параллельно (другие файлы, нет зависимостей от незавершённых задач)
- **[Story]**: US1–US4 из spec.md

---

## Phase 1: Setup

**Purpose**: чистый Rust-проект на месте C#-кода

- [X] T001 Удалить старый код: `src/BluettiWatcher/`, `tests/BluettiWatcher.Tests/`, `tests/.editorconfig`, `power-mode/`, `integrations/`, `tools/`, `install.ps1`, `requirements.lock`, `bluetti-watcher.slnx`, `Directory.Build.props`, `Directory.Packages.props`, `global.json`, `nuget.config`, `docs/api.md`, `docs/configuration.md`, `docs/design.md`, `.github/workflows/*.yml`, `.github/ISSUE_TEMPLATE/`, `.github/PULL_REQUEST_TEMPLATE.md`, `CONTRIBUTING.md`, `SECURITY.md`, `CHANGELOG.md`, `README.uk.md`; оставить `LICENSE` (дописать строку `Copyright (c) 2026 Reva1v` под копирайтом оригинала), `.editorconfig`, `.gitattributes`, `docs/*.png` удалить
- [X] T002 Создать `Cargo.toml` (name `bluetti-watcher`, edition 2024, `[profile.release]` lto = true, opt-level = "s", strip = true, panic = "abort") с зависимостями из plan.md → Technical Context; `src/main.rs` с `#![windows_subsystem = "windows"]` и пустым `clap`-диспетчером команд `run|open|scan|read-once|install|uninstall|status|gpu-apply` (contracts/cli-config.md)
- [X] T003 [P] `.gitignore` для Rust (`/target`), `rustfmt.toml` (max_width = 110), `clippy.toml` при необходимости
- [X] T004 [P] CI `.github/workflows/ci.yml`: windows-latest, `cargo fmt --check`, `cargo clippy --all-targets -- -D warnings`, `cargo test`, `cargo build --release`, артефакт exe

---

## Phase 2: Foundational

**Purpose**: общее для всех историй. **Блокирует все истории.**

- [X] T005 `src/config.rs`: структура `Config` по `config.toml` из contracts/cli-config.md со всеми значениями по умолчанию; загрузка из `%LOCALAPPDATA%\bluetti-watcher\config.toml`; невалидный ключ → значение по умолчанию + запись в список предупреждений; пустой/неверный `mac` → `Config.mac = None`; unit-тесты на дефолты, частичный файл, неверные типы и диапазоны (проценты 0–100, `cpu_max_pct` 5–100)
- [X] T006 [P] `src/paths.rs`: каталог данных `%LOCALAPPDATA%\bluetti-watcher` (`bin\`, `logs\`, `station.db`, `config.toml`, `no-hibernate`), `%ProgramData%\bluetti-watcher\gpu-request`; создание каталогов
- [X] T007 [P] `src/logging.rs`: `tracing` в `logs\service.log` с ротацией по размеру 5 МБ × 2 файла (`service.log`, `service.log.1`), FR-009; консольный вывод для команд, подключившихся к консоли родителя (`AttachConsole(ATTACH_PARENT_PROCESS)` в `src/console.rs`)
- [X] T008 [P] `src/clock.rs`: `trait Clock { fn now(&self) -> Timestamp }`, системная и фейковая реализации; тип `Timestamp` (мс UTC) и форматирование в локальное ISO 8601 со смещением
- [X] T009 `src/model.rs`: `Reading { ts, soc: Option<u8>, ac_in_w, ac_out_w, dc_in_w, dc_out_w: Option<u32>, ac_in_v: Option<f32> }`, `ReadStatus` (`accepted|rejected|rebased|miss`) и причины (`incomplete`, `implausible`, `timeout`, `ble-off`, `not-found`, `modbus-error`, `crypto-error`) из data-model.md

**Checkpoint**: каркас собирается, `cargo test` зелёный

---

## Phase 3: User Story 1 — Станция читается в фоне (P1) 🎯 MVP

**Goal**: сервис без окна раз в минуту читает AC180P по BLE и пишет историю.

**Independent Test**: quickstart.md §1 (`scan`, `read-once --trace` у станции), затем `run` 5 мин → ≥ 4 принятых показания в `station.db`.

### Tests for User Story 1

- [X] T010 [P] [US1] `tests/protocol.rs`: CRC16-Modbus (вектор: `01 03 00 66 00 01` → CRC 0x1564, в кадре `64 15`), сборка запроса `01 03 <addr BE> <count BE> <crc LE>`, разбор ответа `01 03 <len> <data> <crc>`, отказ при неверном CRC, при ответе-исключении `01 83 xx`, при длине ≠ `2*count+5`
- [X] T011 [P] [US1] `tests/protocol.rs`: рукопожатие против фейковой станции (`FakeStation` реализует challenge → accepted → peer pubkey → key accepted с тестовой парой ключей вместо констант K2/L1, переданных параметром), затем шифрованный кадр чтения туда и обратно; склейка фрагментированных уведомлений до `6 + ceil(len/16)*16` байт; неверная подпись станции → `crypto-error`
- [X] T012 [P] [US1] `tests/protocol.rs`: инвариант принципа I — фейковый `Transport` записывает все исходящие кадры; после полного цикла чтения каждый кадр либо `2A2A…` рукопожатие, либо (после расшифровки) Modbus с функцией `0x03`
- [X] T013 [P] [US1] Unit-тесты разбора AC180P в `src/ble/ac180p.rs`: блоки `100×10`, `140×10`, `1314×1` → `soc`=рег.102 (вне 0–100 → None), `dc_out`=140, `ac_out`=142, `dc_in`=144, `ac_in`=146, `ac_in_v`=рег.1314/10

### Implementation for User Story 1

- [X] T014 [P] [US1] `src/ble/modbus.rs`: `read_request(addr, count) -> Vec<u8>`, `crc16_modbus`, `parse_read_response(&[u8], count) -> Result<Vec<u16>>`; функции записи (0x06/0x10) не существует (принцип I)
- [X] T015 [P] [US1] `src/ble/crypto.rs`: по research.md R1 — константы `LOCAL_AES_KEY`, `PRIVATE_KEY_L1`, `PUBLIC_KEY_K2`; checksum (сумма байт тела, u16 BE); `Handshake` как конечный автомат: `on_challenge` (IV₀ = MD5(reverse(data)), K₀ = IV₀ ⊕ LOCAL_AES_KEY, ответ `2A2A 0204 IV₀[8..12] cs`), `on_peer_pubkey` (проверка ECDSA-P256-SHA256 подписи над `pub‖IV₀` ключом K2, генерация пары, подпись `my_pub‖IV₀` ключом L1, raw r‖s, кадр `2A2A 0580 …` под AES-128-CBC K₀/IV₀), `on_key_accepted` (ECDH → 32-байтовый ключ); `encrypt_frame`/`decrypt_frame` (`len BE ‖ seed4 ‖ AES-256-CBC(key, MD5(seed))`, нули до 16); буфер склейки фрагментов. Ключи никогда не логируются
- [X] T016 [US1] `src/ble/mod.rs`: `trait Transport { async fn connect; async fn write(&[u8]); fn notifications() -> Stream<Vec<u8>>; async fn disconnect }`; `read_station(transport, encryption_hint) -> Result<Reading, MissReason>`: подписка, ожидание challenge 3 с (есть → шифрованный режим, нет → открытый), чтение трёх блоков с таймаутом 5 с на ответ, общий таймаут цикла 30 с, отключение в `finally`
- [X] T017 [US1] `src/ble/ac180p.rs`: карта регистров и преобразование трёх блоков в `Reading`
- [X] T018 [US1] `src/ble/winrt.rs`: `WinRtTransport` на crate `windows`: адрес `AA:BB:..` → u64, `BluetoothLEDevice::FromBluetoothAddressAsync`, сервис по notify/write UUID (`0000ff01-…`/`0000ff02-…`), включение уведомлений через CCCD, `ValueChanged` → канал tokio, `WriteValueWithResultAsync` (WriteWithoutResponse, если характеристика не поддерживает с ответом), `Close` при отключении; повтор с `BluetoothCacheMode::Uncached` после ошибки GATT; выключенный адаптер → `ble-off`
- [X] T019 [US1] `src/ble/scan.rs` + команда `scan` в `src/main.rs`: `BluetoothLEAdvertisementWatcher` 10 с, фильтр имён по регэкспу из bluetti-bt-lib (`^(AC180P|AC180|AC2A|…)\d+$`), печать имя / адрес / RSSI
- [X] T020 [US1] Команда `read-once [--mac] [--trace]` в `src/main.rs`: одно чтение, печать `encryption: yes|no` и полей; `--trace` печатает каждый кадр hex (без ключей); exit 1 при пропуске с причиной
- [ ] T021 [US1] **СТОП-ТОЧКА ВЛАДЕЛЬЦА**: собрать release, владелец выполняет quickstart.md §1 у своей AC180P; результат (успех, шифрование, совпадение цифр с экраном, прошивка) записать в spec.md → Assumptions. При неудаче — разбор `--trace` до перехода к T022
- [X] T022 [US1] `src/store.rs`: схема `readings`, `outages`, `events`, `meta` строго по data-model.md (`ts INTEGER PK`, `soc INTEGER NULL` 0–100, `status TEXT` одно из `accepted|rejected|rebased|miss`, …), WAL, миграция по `user_version`; `insert_reading`, `insert_event`, `get_meta/set_meta` (JSON), чистка записей старше 365 дней раз в сутки; unit-тесты на временной БД
- [X] T023 [US1] `src/instance.rs`: именованный мьютекс `Local\BluettiWatcher`; второй `run` → exit 3 (FR-008)
- [X] T024 [US1] `src/service.rs` + команда `run [--dry-run]`: цикл каждые `poll_sec` (по умолчанию 60): `read_station` → запись показания или пропуска с причиной (на этом этапе все полные показания `accepted`, решения подключаются в US4) → событие `service-start` при запуске; ошибки чтения не роняют цикл (US1 сценарий 3); подсказка шифрования из `meta.encryption`

**Checkpoint**: `bluetti-watcher run` пишет показания в фоне; пропуски видны с причиной

---

## Phase 4: User Story 4 — Безопасная гибернация до разряда (P1)

**Goal**: проверки показаний, сеть/отключение, уровни батареи, предупреждение и гибернация.

**Independent Test**: `cargo test --test scenarios` (симуляция 25 % → 4 %, скачки, rebase) + quickstart.md §4 с `no-hibernate` и `dry_run`.

### Tests for User Story 4

- [X] T025 [P] [US4] `tests/scenarios.rs`: сценарии spec.md US4 1–6 на фейковых часах и показаниях: предупреждение на 7 % после двух показаний ≥ 90 с; гибернация на 5 % не раньше 3 мин после предупреждения и только после свежего подтверждения; нет свежего показания → нет гибернации; `no-hibernate` → событие `hibernate-refused`; скачок 60 % → 3 % отвергнут, при удержании — 10 мин подтверждения после rebase; повтор после пробуждения
- [X] T026 [P] [US4] `tests/scenarios.rs`: края из spec.md Edge Cases: неполное показание не участвует; пропуск > 600 с сбрасывает состояние; перевод часов назад сбрасывает, вперёд засчитывается одним интервалом (не больше 2×poll); вход DC при нулевом входе AC = работа от батареи
- [X] T027 [P] [US4] Unit-тесты `src/decision/guards.rs`: правдоподобие `|Δsoc| ≤ max(3, 5 × минуты)`; три согласных отвергнутых → rebase, rebase не считается в уровни и таймеры

### Implementation for User Story 4

- [X] T028 [US4] `src/decision/guards.rs`: полнота (`soc` и `ac_in_w` есть), правдоподобие, rebase (FR-004, FR-006), сброс по пропуску > `max_gap_sec`, обработка скачков часов
- [X] T029 [US4] `src/decision/mains.rs`: `mains = On если ac_in_w > 0, Off если = 0` (FR-005); таймеры `lost_sec`/`back_sec` только по принятым показаниям (FR-018); начало/конец отключения; ручной режим `Auto|Eco|Max` сбрасывается в `Auto` при подтверждённой смене сети (FR-015); `eco_at` для отсчёта (FR-021). Действия eco на этом этапе только как `Action::Eco(Level)` без исполнения
- [X] T030 [US4] `src/decision/levels.rs`: уровни `low/critical/warn/hibernate` (по умолчанию 20/10/7/5), согласие двух принятых показаний ≥ `agree_sec`, один уровень за цикл, срабатывание засчитывается только после успешного действия, повтор с бэкоффом 1, 2, 4, 8, затем каждые 10 мин (FR-022–FR-027)
- [X] T031 [US4] `src/decision/mod.rs`: `decide(state, input, now, cfg) -> (DecisionState, Vec<Action>)` где `Action` = `Eco(None|Eco|Deep)`, `Notify(kind, text)`, `Hibernate`, `NeedFreshReading`, `Event(kind, text, data)`; `DecisionState` строго по data-model.md, сериализуется в JSON
- [X] T032 [US4] `src/power/hibernate.rs`: `trait Hibernator`; реализация `SetSuspendState(TRUE, FALSE, FALSE)`; проверка `GetPwrCapabilities().HiberFilePresent` → `hibernateAvailable` в `/api/now` и одно уведомление при старте, если гибернации нет (FR-028a); в `dry_run` — только лог и событие `hibernate` с пометкой
- [X] T033 [P] [US4] `src/notify.rs`: `trait Notifier`; toast через `tauri-winrt-notification` с AUMID `Reva1v.BluettiWatcher` (до установки ярлыка — AUMID PowerShell как запасной); тексты на русском («ПК уйдёт в гибернацию примерно через N минут», «Заряд 10 % — сохраните работу», «Заряд 20 % — включён глубокий eco»)
- [X] T034 [US4] Подключить решения в `src/service.rs`: статус показания из `guards`, запись `DecisionState` в `meta.decision_state` после каждого цикла и загрузка при старте, `NeedFreshReading` → немедленное повторное чтение перед гибернацией, проверка файла `no-hibernate` на каждом цикле, запись `outages` и `events`

**Checkpoint**: гибернация и уведомления работают на фейках и в `dry_run`; eco пока только как событие

---

## Phase 5: User Story 2 — Дашборд по ярлыку (P2)

**Goal**: окно с текущим состоянием, графиками, отключениями и событиями.

**Independent Test**: quickstart.md §2 — ярлык → окно ≤ 3 с, данные ≤ 2 мин, графики за 4 периода, закрытие окна не останавливает сервис.

### Tests for User Story 2

- [X] T035 [P] [US2] Тесты `src/http/mod.rs` (axum `oneshot`): `Host` не `127.0.0.1:<port>`/`localhost:<port>` → 403; `POST /api/mode` без `X-Bluetti-Watcher: 1` → 403, неизвестный режим → 400, верный → 202; форма `/api/now`, `/api/history?range=7d` (`step` 300), `/api/outages`, `/api/events` по contracts/http-api.md

### Implementation for User Story 2

- [X] T036 [US2] `src/store.rs`: запросы для API — последнее принятое показание, история с агрегацией (6h/24h — сырые, 7d — средние по 5 мин, 30d — по 15 мин; только `accepted`), отключения и события с `limit`; доля удачных циклов за 24 ч `readOk24h` (принятые / все циклы, SC-001); запрос 30d ≤ 300 мс на 525 тыс. строк (индекс по `ts`, проверка тестом на синтетике)
- [X] T037 [US2] `src/runtime.rs` + тесты: оценка времени по скорости разряда за `60` мин работы от батареи (минимум падение 3 % за 20 мин), иначе ёмкость 1440 Вт·ч × SOC × КПД / нагрузка; обучаемый КПД 0.4–0.95 в `meta.learned_efficiency`; нагрузка 0 → `None`; максимум 30 дней (FR-029, FR-030); тест SC-007: синтетический разряд 1440 Вт·ч при постоянных 200 Вт и КПД 0.85 — оценка в середине разряда отличается от фактического остатка не больше чем на 20 %
- [X] T038 [US2] `src/http/mod.rs`: axum на `127.0.0.1:port`, middleware `Host`, маршруты из contracts/http-api.md (включая `readOk24h` в `/api/health`), SSE `/api/stream` (`event: now` после каждого цикла, `event: event` на новое событие) через `tokio::sync::broadcast` из `service.rs`; `POST /api/mode` сохраняет выбор для следующего цикла
- [X] T039 [US2] Макет дашборда в Claude Design по contracts/http-api.md (карточки: заряд, источник питания, вход/выход AC/DC, время работы, режим и кнопки Авто/Eco/Максимум; графики заряда и мощности с периодами и полосами отключений; список отключений; журнал событий; состояния «станция не отвечает», «гибернация недоступна», «dry run»); показать владельцу и согласовать
- [X] T040 [US2] `src/http/web/` (`index.html`, `app.js`, `style.css`, `uplot.min.js` 1.6.x, `uplot.min.css`) по согласованному макету, только русский; подключение к `/api/stream`, переключение периодов, кнопки режима с заголовком `X-Bluetti-Watcher: 1`; встраивание через `include_str!`/`include_bytes!` в `src/http/assets.rs`
- [ ] T041 [US2] `src/window.rs` + команда `open`: проверка `/api/health` 3 с; нет ответа → запуск задачи `BluettiWatcher` (до установки T050 — прямой запуск `bluetti-watcher.exe run` отсоединённым процессом) и ожидание до 10 с; окно `tao` + `wry` (WebView2) 1200×800, заголовок «Bluetti Watcher», иконка из ресурса; закрытие окна завершает только процесс `open`
- [ ] T042 [P] [US2] Иконка и ресурсы exe: `assets/icon.ico`, `build.rs` с `winresource` (иконка, версия, описание)

**Checkpoint**: дашборд показывает живые данные и историю

---

## Phase 6: User Story 3 — Eco-режим при отключении света (P3)

**Goal**: eco и глубокий eco применяются и снимаются автоматически и вручную.

**Independent Test**: quickstart.md §3 — выдернуть станцию: через ~3 мин активна «Bluetti Eco», вернуть питание: прежняя схема.

### Tests for User Story 3

- [X] T043 [P] [US3] `tests/scenarios.rs`: US3 1–6 — eco через 3 мин без входа AC; снятие через 3 мин с входом; кратковременный провал 1 мин не включает eco; «Максимум» снимает eco до смены сети или «Авто»; 20 % → `Eco(Deep)`; при «Максимум» глубокий eco не включается, уровни и гибернация действуют — решения (US3 1–4, 6) покрыты в tests/scenarios.rs; часть 5 (применение eco на ПК) — T044–T049
- [X] T044 [P] [US3] Unit-тесты `src/power/mod.rs` на фейковом `PowerBackend`: `power_restore` пишется до применения; частичный отказ (нет NVIDIA, ошибка оверлея) не мешает остальным частям и даёт событие `eco-part-failed` (FR-019b); восстановление после перезапуска по сохранённому `power_restore`

### Implementation for User Story 3

- [X] T045 [US3] Спайк: проверить на ПК владельца `PowerSetActiveOverlayScheme(961cc777-2547-4f9d-8174-7d86181b8a7a)` поверх копии «Сбалансированной» без прав администратора и влияние на потребление/частоты; результат и итоговое решение записать в research.md R3
- [X] T046 [US3] `src/power/scheme.rs`: создать/найти схему «Bluetti Eco» (`PowerDuplicateScheme` от «Сбалансированной» `381b4222-…` при `efficiency_overlay`, иначе от «Экономии энергии» `a1841308-…`; `PowerWriteFriendlyName`), GUID в `meta.eco_scheme`; применить уровень: `PROCTHROTTLEMAX` AC = `cpu_max_pct` / `deep_cpu_max_pct`, `VIDEOIDLE` AC = `monitor_off_sec`, `PowerSetActiveScheme`, оверлей; снять: вернуть `prev_scheme` и оверлей; схему «Экономия (нет света)» `b1e771e0-…` и другие схемы владельца не менять
- [X] T047 [US3] `src/power/gpu.rs`: сервис пишет `normal|eco|deep` в `%ProgramData%\bluetti-watcher\gpu-request` и запускает задачу `BluettiWatcherGpu` (Task Scheduler COM `ITaskService::GetFolder/GetTask/Run`); нет задачи → часть пропускается с событием; команда `gpu-apply`: NVML (`nvml-wrapper`), ватты = `normal` → default, `eco` → `max(min, default × gpu_pct/100)`, `deep` → `deep_gpu_pct = 0 ? min : max(min, default × pct)`, всегда в `[min_limit, default_limit]`; нет NVIDIA → exit 2
- [X] T048 [US3] `src/power/mod.rs`: `trait PowerControl { apply(EcoLevel) -> PartResults }`, порядок: сохранить `power_restore` → схема → оверлей → GPU; бэкофф ошибок общий с T030; `dry_run` → только лог
- [X] T049 [US3] Подключить `Action::Eco` в `src/service.rs`, события `eco-on`, `eco-deep`, `eco-off`, `eco-part-failed`; при старте сервиса — если `power_restore` есть, а отключения нет, вернуть настройки (восстановление после сбоя)

**Checkpoint**: все четыре истории работают

---

## Phase 7: Установка, удаление и полировка

- [ ] T050 `src/install.rs` + команда `install [--mac] [--port] [--elevated-setup]` (без `--mac` — `scan` и единственная найденная станция, несколько → список и exit 1): копия exe в `%LOCALAPPDATA%\bluetti-watcher\bin\`, `config.toml` с `mac`, схема «Bluetti Eco», задача `BluettiWatcher` (при входе текущего пользователя, без элевации, перезапуск при сбое 3 раза через 1 мин, без ограничения по времени, запуск от батареи разрешён), ярлыки на Рабочем столе и в «Пуске» на `bluetti-watcher.exe open` с AUMID `Reva1v.BluettiWatcher` (`IPropertyStore`, `PKEY_AppUserModel_ID`), запуск задачи и ожидание `/api/health` до 15 с
- [ ] T051 `src/install.rs`: `--elevated-setup` перезапускает себя через `ShellExecuteW("runas")` с внутренней командой, которая выполняет `powercfg /h on`, создаёт `%ProgramData%\bluetti-watcher\` с правом записи для пользователя на `gpu-request` и регистрирует задачу `BluettiWatcherGpu` (наивысшие права, запуск по требованию, `bluetti-watcher.exe gpu-apply`)
- [ ] T052 Команды `uninstall [--purge]` и `status` в `src/install.rs`/`src/main.rs`: снять eco (вернуть схему и GPU), остановить и удалить задачи, ярлыки, схему «Bluetti Eco»; `--purge` — каталог данных; `status` — одна строка из `/api/health` + `/api/now`
- [ ] T053 [P] `README.md` на русском: что делает, требования, установка (`scan` → `install`), выключатель `no-hibernate`, `dry_run`, удаление, скриншот дашборда; ссылка на оригинальный проект и MIT
- [ ] T054 Проверка SC-005: сервис 1 ч в фоне — средний CPU < 1 %, память < 50 МБ (`Get-Process`); при превышении — профилирование и правка
- [ ] T055 Пройти quickstart.md §0–§5 целиком на ПК владельца; сверить `readOk24h` ≥ 0.95 за сутки (SC-001) и оценку времени с реальным отключением (SC-007); итоги записать в spec.md, закрыть найденное через `/speckit-bug-*`

---

## Dependencies & Execution Order

- Phase 1 → Phase 2 → US1 (стоп-точка T021) → US4 → US2 → US3 → Phase 7.
- US4 использует чтение и хранилище US1. US2 использует решения US4 для полей `mains/outage/levels`, но API и окно
  можно строить на данных US1. US3 использует `decide` из US4 (Action::Eco уже генерируется в T029).
- Внутри истории: тесты → модули без I/O → I/O-реализации → подключение в `service.rs`.

## Parallel Opportunities

- Phase 1: T003, T004. Phase 2: T006, T007, T008 параллельно после T005.
- US1: тесты T010–T013 параллельно; T014 и T015 параллельно.
- US4: T025–T027 параллельно; T033 параллельно с T028–T032.
- US2: T035 и T042 параллельно; T039 (макет) можно начать сразу после фиксации API — параллельно с T036–T038.
- US3: T043, T044 параллельно; T045 (спайк) — до T046.

```text
# US1 — параллельный старт:
T010 CRC/кадры · T011 рукопожатие · T012 инвариант только чтения · T013 разбор AC180P
T014 modbus.rs  · T015 crypto.rs
```

## Implementation Strategy

1. **MVP**: Phase 1–2 + US1 до T021 — подтверждённое чтение AC180P на Rust. Без этого остальное не начинаем.
2. US1 полностью + US4 — фон, история, защита от разряда (уже безопаснее оригинала для AC180P).
3. US2 — дашборд с дизайном из Claude Design.
4. US3 — eco; затем установка и полировка.
5. Коммит после каждой задачи или логической группы; `cargo fmt/clippy/test` перед каждым коммитом.
