# Data Model: 001-rust-station-watcher

Хранилище: `%LOCALAPPDATA%\bluetti-watcher\station.db` (SQLite, WAL). Время — UTC, `INTEGER` мс с эпохи.

## readings

| Поле | Тип | Правило |
|---|---|---|
| `ts` | INTEGER PK | время получения показания |
| `soc` | INTEGER NULL | 0–100 |
| `ac_in_w`, `ac_out_w`, `dc_in_w`, `dc_out_w` | INTEGER NULL | ≥ 0 |
| `ac_in_v` | REAL NULL | вольты |
| `status` | TEXT | `accepted` / `rejected` / `rebased` / `miss` |
| `reason` | TEXT NULL | причина для `rejected`/`miss` (`incomplete`, `implausible`, `timeout`, `ble-off`, …) |

Хранится 365 дней (чистка раз в сутки). Для графиков 7/30 дней API агрегирует по 5/15 минут.

## outages

| Поле | Тип | Правило |
|---|---|---|
| `id` | INTEGER PK | |
| `start_ts` | INTEGER | время первого показания без входа AC в серии, подтвердившей отключение |
| `end_ts` | INTEGER NULL | время первого показания с входом AC в серии, подтвердившей возврат; NULL — идёт |
| `soc_start`, `soc_end` | INTEGER NULL | |
| `eco` | INTEGER | 0/1 — включался ли eco |
| `hibernated` | INTEGER | 0/1 |

## events

| Поле | Тип | Правило |
|---|---|---|
| `id` | INTEGER PK | |
| `ts` | INTEGER | |
| `kind` | TEXT | `outage-start`, `outage-end`, `eco-on`, `eco-deep`, `eco-off`, `eco-part-failed`, `level-low`, `level-critical`, `hibernate-warn`, `hibernate`, `hibernate-refused`, `hibernate-delayed`, `mode-manual`, `action-failed`, `service-start` |
| `text` | TEXT | человекочитаемо, по-русски |
| `data` | TEXT NULL | JSON с деталями (ватты, проценты, код ошибки) |

## meta (ключ → JSON)

- `decision_state` — `DecisionState` (ниже), пишется после каждого цикла.
- `power_restore` — что вернуть при снятии eco: `prev_scheme` (GUID), `gpu_level_before`; пишется до применения eco.
- `eco_scheme` — GUID схемы «Bluetti Eco».
- `encryption` — `true/false`, определено при первом успешном чтении.
- `learned_efficiency` — число 0.4–0.95.

## DecisionState (в памяти и в `meta`)

```text
last_accepted: Reading?          // база для проверки правдоподобия
rejected_streak: [Reading; ≤3]   // для rebase
mains: On | Off | Unknown
mains_since: ts?                 // начало текущей серии показаний с тем же состоянием сети
outage: { start_ts, rebase_at?, levels_fired: set<Level>, warn_at?, hibernate_at? }?
level_streaks: map<Level, first_ts>
manual: Auto | Eco | Max         // сбрасывается в Auto при подтверждённой смене состояния сети
eco_applied: None | Eco | Deep
backoff: map<Action, { failures, next_try_ts }>
```

## Переходы

```text
               вход AC = 0 ≥ 3 мин                 вход AC > 0 ≥ 3 мин
  [Сеть] ───────────────────────────► [Отключение] ───────────────────► [Сеть]
                                         │  eco (если manual ≠ Max)
                                         ├─ SOC ≤ 20 % ×2 → глубокий eco + уведомление
                                         ├─ SOC ≤ 10 % ×2 → уведомление «сохраните работу»
                                         ├─ SOC ≤ 7 %  ×2 → предупреждение о гибернации
                                         └─ SOC ≤ 5 %  ×2, ≥ 3 мин после предупреждения, свежее показание
                                            подтверждает → гибернация (10 мин подтверждения после rebase)
  Пропуск > 10 мин или часы назад → сброс таймеров, уровней и серий (сохраняются manual и power_restore).
```

## Reading (входные данные решения)

`{ ts, soc?, ac_in_w?, ac_out_w?, dc_in_w?, dc_out_w?, ac_in_v? }`. Полное = `soc` и `ac_in_w` есть.
Правдоподобное = `|soc − last.soc| ≤ max(3, 5 × минуты_с_last)`.
