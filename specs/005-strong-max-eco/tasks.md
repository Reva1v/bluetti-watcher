# Tasks: 005 — Сильный и Максимальный Eco с затемнением мониторов

Вход: `spec.md`, `plan.md`. Один крейт, файлы пересекаются, поэтому задачи идут по порядку; `[P]` — только там, где файлы
свои. Тесты пишутся в той же задаче, что и код (конституция V). Проверка в ходе работы: `cargo test <фильтр>`; полные
`cargo fmt --check`, `cargo clippy --all-targets -- -D warnings`, `cargo test` — один раз в конце (T019).

## Phase 1: Основа (блокирует все истории)

- [x] T001 `EcoLevel::Strong` между `Eco` и `Deep` с `PartialOrd, Ord` в `src/model.rs`; все `match` по `EcoLevel` в
  `src/power/mod.rs` (ветка `Eco | Strong | Deep`, процессор `strong_cpu_max_pct`), `src/power/gpu.rs` (`level_word`,
  `parse_level` — `strong`; `limit_for(Strong)`), `src/power/services.rs`, `src/service.rs` (событие `eco-strong`
  «Сильный Eco включён»), `src/install.rs`. Тесты: порядок уровней, процессор и GPU для `Strong`, слова запроса.
- [x] T002 Конфиг в `src/config.rs`: `[levels] strong = 50`, `[eco] strong_cpu_max_pct = 65`, `strong_gpu_pct = 50`,
  `dim = true`, `dim_pct = 0`, `dim_skip_games = true`, `game_processes = []`; проверка `strong > low` вместе с
  существующим порядком уровней. Тесты: разбор, границы, нарушение порядка.

## Phase 2: US1 — Ручные переключатели (P1)

- [x] T003 [US1] `src/decision/boost.rs`: `Boost`, `Switch`, `BoostRequest`, `request`, `level`, `settle`,
  `on_mains_back`; поле `DecisionState.boost` и `Input.boost_requests` в `src/decision/mod.rs`; `guards::reset` не
  сбрасывает `boost`. Тесты из плана (раздел «Тесты», `decision/boost`).
- [x] T004 [US1] `mains::desired` = `max(base, boost::level)`, удалить `Outage.deep` (`src/decision/mains.rs`,
  `src/decision/mod.rs`, `src/decision/guards.rs`); `on_mains_back` при закрытии отключения. Тесты: таблица уровня для
  всех сочетаний, старое состояние с `deep` читается, ручное включение переживает начало отключения и снимается
  возвратом сети.
- [x] T005 [US1] `src/service.rs`: канал `Channels.boosts`, нажатие будит цикл, все нажатия уходят в `Input`; поле
  `boost` в `NowView`. Тест: нажатие применяет `Strong` и не меняет `manual`.
- [x] T006 [US1] `src/http/mod.rs`: `POST /api/boost` (один ключ `strong` | `max` | `dim`, защита как у `/api/mode`),
  поле `boost` в `/api/now`. Тесты: заголовок, неверные тела, доставка в канал, форма `boost`.
- [x] T007 [US1] Дашборд `src/http/web/index.html`, `app.js`, `style.css`: две кнопки-переключателя под сегментом,
  пометка «авто», тексты карточки и статуса для `strong`, «глубокий eco» → «Максимальный Eco» (также `src/notify.rs`).

## Phase 3: US2 — Автоматика по заряду (P1)

- [x] T008 [US2] `Level::Strong` первым в `src/decision/levels.rs`: порог, упреждающий порог, лестница
  `[Strong, Low, Critical, Warn]`, событие `level-strong`, без уведомления Windows; `boost::on_threshold` для `Strong`
  и `Low` с блокировкой при «Максимум». Тесты: границы 51/50 и 21/20, одна ступень за цикл, «Максимум» блокирует,
  выключенный владельцем уровень не включается снова.
- [x] T009 [US2] Сценарии в `tests/scenarios.rs`: 60 → 15 % даёт по одному включению каждого уровня; отключение с 40 %;
  при сети пороги молчат; отвергнутое показание ничего не включает; возврат сети снимает оба. Обновить
  `us3_6_*` под новые правила.

## Phase 4: US3 — Затемнение мониторов (P1)

- [x] T010 [P] [US3] `src/power/brightness.rs`: `Dim`, `Monitor`, `Brightness`, `DimRestore`, `apply` — чистая логика
  с поддельным `Brightness`. Тесты из плана (`power/brightness`, включая переходы с игрой).
- [x] T011 [US3] `WindowsBrightness` в `src/power/brightness.rs` и feature `Win32_Devices_Display` в `Cargo.toml`.
  В тестах не вызывается.
- [x] T012 [US3] Ядро: `DecisionState.dim`, `dim_applied`, `Action::SetDim(Dim)`, `boost::dim_wanted`, `boost::sync_dim`,
  `report` в `src/decision/boost.rs` и `src/decision/mod.rs`; `BoostRequest::Dim`. Тесты: таблица уровень × `dim_off` ×
  игра, `dim_off` возвращается после снятия обоих, бэкофф.
- [x] T013 [US3] `src/service.rs`: `set_dim` (сохранить `dim_restore` до записи, события `dim-on` / `dim-off`,
  `eco-part-failed`), возврат яркости на старте, `[eco] dim = false`, `--dry-run`; поле `dimmed` в `NowView`.
  Тесты из плана (`service`).
- [x] T014 [US3] Дашборд: флажок «Гасить мониторы» и строка о том, что затемнено (`src/http/web/*`).

## Phase 5: US4 — Игра на основном мониторе (P2)

- [x] T015 [P] [US4] `src/power/games.rs`: `Games`, `matches`, `WindowsGames` (реестр Steam `RunningAppID`, процессы из
  `game_processes`). Тесты: `matches`.
- [x] T016 [US4] `src/service.rs`: `Input.game_running` из `Games::running()` на каждом цикле;
  `dim_skip_games = false`. Тесты: запуск и закрытие игры между циклами; уровень включается при запущенной игре —
  основной монитор не получает записи.
- [x] T017 [US4] Дашборд: пометка «игра запущена: основной монитор не затемнён» (`src/http/web/app.js`).

## Phase 6: Завершение

- [x] T018 Документация: новые ключи конфига и переключатели в `README.md`, `README.ru.md`, `README.uk.md`,
  `specs/001-rust-station-watcher/contracts/cli-config.md` и `http-api.md`.
- [x] T019 Полные проверки: `cargo fmt --check`, `cargo clippy --all-targets -- -D warnings`, `cargo test`.
- [x] T020 Вид карточки «Режим ПК» проверен на живом дашборде (скилл `ui-screenshots`, без затемнения мониторов).
- [x] T021 `cargo clean` и уборка временных файлов (CLAUDE.md проекта).

## Зависимости

T001–T002 → US1 (T003–T007) → US2 (T008–T009) → US3 (T010–T014) → US4 (T015–T017) → T018–T021. T010 и T015 не
зависят от историй и могут идти параллельно с ними.
