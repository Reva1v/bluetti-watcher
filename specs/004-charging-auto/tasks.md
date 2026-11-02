# Tasks: 004 — Автоматическое переключение режима зарядки

План: [plan.md](plan.md). Тесты обязательны по принципу V. Все задачи идут в одном процессе `cargo`, поэтому порядок последовательный.

## Phase 1: Setup

- [X] T001 Добавить features `Win32_System_Shutdown`, `Win32_Graphics_Gdi` в `Cargo.toml`
- [X] T002 Секция `[charging_auto]` (`enabled`, `turbo_below`, `standard_at`, `silent_at`), значения по умолчанию, проверка порядка порогов и тесты в `src/config.rs`

## Phase 2: Foundational

- [X] T003 `ChargingAuto`, `Want`, `Why`, `Seen`, каркас `update`/`report`/`manual`/`shutdown` в `src/decision/charging.rs`, модуль в `src/decision/mod.rs`
- [X] T004 `decide` в `src/service.rs` возвращает статус показания; поле `auto`, загрузка и сохранение `meta.charging_auto`; `write_auto` пишет режим, ставит событие `charging-mode` с причиной, в `--dry-run` только лог, при ошибке — `fail`

## Phase 3: User Story 1 — зарядка после отключения (P1)

Проверка: «нет сети → сеть подтверждена» при заряде 15/20/30/79/80 даёт нужный режим.

- [X] T005 [US1] Тесты включения света: границы 19/20/79/80, блик сети, совпадение с текущим режимом, неизвестный режим, бэкофф в `src/decision/charging.rs`
- [X] T006 [US1] Правило включения света и повтор по бэкоффу в `update`, `src/decision/charging.rs`
- [X] T007 [US1] Вызов `update` из `tick` при `Accepted` и `enabled`, вызов `manual` из `cycle`, тесты (выключено — нет записи, отвергнутое показание, ошибка и повтор, `--dry-run`, нажатие снимает повтор) в `src/service.rs`

## Phase 4: User Story 2 — смена режима по мере зарядки (P1)

Проверка: 15 → 85 % при сети меняет режим ровно три раза.

- [X] T008 [US2] Тесты порогов: порядок 15 → 50 → 80, качание 79↔80, рост без сети, скачок через оба порога, перезапуск через JSON в `src/decision/charging.rs`
- [X] T009 [US2] Пороги через `high_water` в `update`, `src/decision/charging.rs`

## Phase 5: User Story 3 — тихий режим при выключении ПК (P2)

Проверка: сигнал выключения приводит к записи «Тихая» раньше ответа; автоматикой она не отменяется.

- [X] T010 [US3] Тесты `shutdown`: пауза 2 мин, запуск после выключения даёт правило света, часы назад в `src/decision/charging.rs`
- [X] T011 [US3] `shutdown` и пауза в `update`, `src/decision/charging.rs`
- [X] T012 [US3] `ShutdownRequest`, `Channels.shutdown`, `on_shutdown` (до двух попыток в `timeout` 13 с), `select!` в `run` вокруг ожидания и `cycle`, тесты (пишет, молчит при «Тихая», отвечает при ошибке, обрыв зависшего чтения, следующий цикл не возвращает режим) в `src/service.rs`
- [X] T013 [US3] Скрытое окно, `WM_QUERYENDSESSION` с `lParam == 0`, `SetProcessShutdownParameters`, `ShutdownBlockReasonCreate`/`Destroy`, ожидание 15 с в `src/shutdown.rs`, модуль в `src/lib.rs`
- [X] T014 [US3] Подключение канала, поток только при `enabled` в `src/main.rs`

## Final Phase: Polish

- [X] T015 [P] Контракт `[charging_auto]` в `specs/001-rust-station-watcher/contracts/cli-config.md`; вид события в `specs/001-rust-station-watcher/data-model.md`
- [X] T016 [P] FR-005 и раздел «Дальше» в `specs/003-charging-mode/spec.md` ссылаются на 004; раздел в `README.md`, `README.ru.md`, `README.uk.md`
- [X] T017 Убрать Sync Impact Report из `.specify/memory/constitution.md` перед коммитом
- [X] T018 Гейты: `cargo fmt --check`, `cargo clippy --all-targets -- -D warnings`, `cargo test`

## Зависимости

T001–T004 → US1 → US2 → US3 → Polish. US1 и US2 меняют одну функцию `update`, поэтому идут подряд. MVP: US1 + US2 (автоматика без реакции на выключение).
