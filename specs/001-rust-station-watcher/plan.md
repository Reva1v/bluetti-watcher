# Implementation Plan: Фоновый монитор станции BLUETTI AC180P

**Branch**: `001-rust-station-watcher` | **Date**: 2026-10-14 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/001-rust-station-watcher/spec.md`

## Summary

Один exe на Rust заменяет C#-сервис и Python-библиотеку оригинала. Он сам читает AC180P по BLE через WinRT, включая
шифрование IoT v2 ([research R1](research.md#r1-протокол-ac180p)). Решения о сети, eco, уровнях и гибернации —
чистый модуль, портированный с правил оригинала. Eco — своя схема питания «Bluetti Eco» (CPU, монитор, оверлей
энергоэффективности) плюс лимит GPU через элевированную задачу. Дашборд — встроенная страница на `127.0.0.1` в окне
WebView2. Старый код (C#, PowerShell, Python, интеграции) удаляется, история остаётся в `upstream`.

## Technical Context

**Language/Version**: Rust stable 1.97, edition 2024

**Primary Dependencies**: `windows` (WinRT BLE, powrprof, Task Scheduler, мьютекс), `tokio`, `axum`, `rusqlite`
(bundled), `serde`/`toml`/`serde_json`, RustCrypto (`p256` с `ecdsa`/`ecdh`, `aes`, `cbc`, `md-5`, `sha2`),
`nvml-wrapper`, `wry` + `tao`, `tauri-winrt-notification`, `tracing`, `clap`

**Storage**: SQLite `station.db` ([data-model.md](data-model.md))

**Testing**: `cargo test` — unit-тесты в модулях, сценарные тесты решений, тесты протокола на байтовых векторах

**Target Platform**: Windows 10/11 x64, адаптер Bluetooth LE

**Project Type**: desktop-сервис (один бинарник) с локальным веб-дашбордом

**Performance Goals**: цикл чтения ≤ 30 с, ответ `/api/history?range=30d` ≤ 300 мс

**Constraints**: в фоне < 1 % CPU и < 50 МБ памяти; без окна, консоли и трея; HTTP только loopback

**Scale/Scope**: 1 станция, 1 ПК, ~525 тыс. показаний за год

## Constitution Check

| Принцип | Как выполняется | Статус |
|---|---|---|
| I. Только чтение | модуль `ble::modbus` умеет строить только функцию 0x03; тест проверяет все исходящие кадры фейкового транспорта (чтение или `2A2A`-рукопожатие) | ✅ |
| II. Безопасность ПК | гибернация — по двум согласным + свежему показанию, 10 мин после rebase, `no-hibernate`, `--dry-run`; eco обратим возвратом схемы, `power_restore` сохраняется до применения | ✅ |
| III. Невидимый фон | `windows_subsystem = "windows"`, задача при входе без элевации, окно только по `open`, проверка `Host` и заголовок на POST | ✅ |
| IV. Один бинарник | всё в одном exe, дашборд встроен (`include_str!`); внешний процесс один: `powercfg /h on` при установке; задачи планировщика — через COM | ✅ |
| V. Тестируемое ядро | `decision` без I/O, время — параметр; трейты `Transport`, `PowerControl`, `Clock`, `Notifier`, `Store` | ✅ |
| VI. Простота | без облака, ДТЭК, флага, пробуждения по таймеру | ✅ |

Повторная проверка после Phase 1: нарушений нет, Complexity Tracking пуст. Единственная элевированная часть
(GPU-задача) — однократная по согласию владельца и ограничена зажатием ватт ([R3](research.md#r3-eco-режим)).

## Project Structure

### Documentation (this feature)

```text
specs/001-rust-station-watcher/
├── plan.md  research.md  data-model.md  quickstart.md
├── contracts/http-api.md  contracts/cli-config.md
└── tasks.md            # /speckit-tasks
```

### Source Code (repository root)

```text
Cargo.toml
src/
├── main.rs              # clap: run | open | scan | read-once | install | uninstall | status | gpu-apply
├── config.rs            # config.toml, значения по умолчанию, валидация
├── service.rs           # цикл: чтение → решение → действия → БД → SSE
├── ble/
│   ├── mod.rs           # trait Transport, read_station() → Reading
│   ├── winrt.rs         # WinRT GATT: connect, notify, write, scan
│   ├── modbus.rs        # кадры чтения, CRC16, разбор ответа
│   ├── crypto.rs        # рукопожатие и AES-кадры
│   └── ac180p.rs        # карта регистров
├── decision/
│   ├── mod.rs           # decide(state, input, now, cfg) → (state, Vec<Action>)
│   ├── guards.rs        # полнота, правдоподобие, rebase, пропуски, скачки часов
│   ├── mains.rs         # таймеры сети, отключение, ручной режим
│   ├── levels.rs        # уровни, предупреждение, гибернация
│   └── runtime.rs       # оценка времени работы
├── power/
│   ├── mod.rs           # trait PowerControl, apply(Eco|Deep|Normal)
│   ├── scheme.rs        # «Bluetti Eco», powrprof, оверлей
│   ├── gpu.rs           # gpu-request + задача; gpu-apply через NVML
│   └── hibernate.rs     # SetSuspendState, проверка доступности
├── store.rs             # SQLite: схема, запись, агрегаты для графиков, чистка
├── http/
│   ├── mod.rs           # axum, Host-guard, SSE
│   └── web/             # index.html, app.js, uplot.min.js, style.css
├── window.rs            # wry/tao окно дашборда
├── notify.rs            # toast-уведомления
└── install.rs           # install/uninstall, задачи, ярлыки с AUMID
tests/
├── protocol.rs          # векторы CRC/кадров, рукопожатие с фейковой станцией
└── scenarios.rs         # сквозные сценарии решений по spec.md
```

**Structure Decision**: один crate без workspace — один бинарник и около 4–5 тыс. строк. Удаляются `src/BluettiWatcher`,
`tests/BluettiWatcher.Tests`, `power-mode/`, `integrations/`, `tools/`, `install.ps1`, `requirements.lock`, `*.slnx`,
`Directory.*.props`, `global.json`, `nuget.config`, старые `docs/` и `.github/workflows` (заменяются Rust CI).
Лицензия MIT с копирайтом автора оригинала сохраняется, добавляется свой.

## Порядок реализации (для tasks)

1. Каркас crate, CI, удаление старого кода.
2. `ble` + `read-once`/`scan` → **проверка владельцем на станции** (стоп-точка: без неё дальше не идём).
3. `decision` + сценарные тесты (US4 и US3 на фейках).
4. `store` + `service` + `run` + мьютекс + лог.
5. `power` (схема, оверлей, GPU, гибернация) + спайк по энергосбережению.
6. `http` + API + SSE; затем макет в Claude Design → `web/`.
7. `window`, `notify`, `install`/`uninstall`, README.

## Complexity Tracking

Нарушений конституции нет.
