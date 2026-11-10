# Tasks: 006 — «Тихая» зарядка только с 90 %

Вход: `spec.md`, `plan.md`. Начинать после слияния 005 (общие файлы `src/config.rs`, `src/service.rs`).

- [x] T001 [US2] Значения по умолчанию `light_silent_at = 90`, `silent_at = 90` в `src/config.rs`; тесты конфига и
  существующие тесты 004 в `src/decision/charging.rs`, `src/service.rs` переведены на границы 89/90.
- [x] T002 [US1] `Why::Floor` и правило в `update` в `src/decision/charging.rs` с тестами из плана.
- [x] T003 [US1] Тесты сервиса в `src/service.rs`: ручная «Тихая» при сети и 60 % заменяется на следующем цикле;
  `enabled = false` — записи нет.
- [x] T004 Документация: пороги и новое правило в `README.md`, `README.ru.md`, `README.uk.md`,
  `specs/001-rust-station-watcher/contracts/cli-config.md`, пометка в `specs/004-charging-auto/spec.md` о замене порогов.
- [x] T005 Полные проверки: `cargo fmt --check`, `cargo clippy --all-targets -- -D warnings`, `cargo test`.
