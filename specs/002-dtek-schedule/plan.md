# Plan: 002 — График ДТЭК

## Решения

- **HTTP**: WinRT `Windows.Web.Http.HttpClient` из crate `windows` (системный TLS, без новых зависимостей).
  Фильтр `HttpBaseProtocolFilter`: `AllowAutoRedirect = false`, кэш выключен; `If-None-Match` из `meta.dtek_etag`.
- **Модуль** `src/dtek/`:
  - `model.rs` — `Cell` (`Yes, No, First, Second, Maybe, MaybeFirst, MaybeSecond, Unknown`), `Day { start: Ts, cells: [Cell; 24] }`,
    `Schedule { update: String, collected: Ts, days: BTreeMap<queue, Vec<Day>> }`.
  - `parse.rs` — чистый разбор JSON набора с пределами FR-002; день — Unix-секунды киевской полуночи.
  - `windows.rs` — окна отключений очереди за день (слияние подряд идущих, полчаса — :30), текущее состояние и `until`.
  - `queue.rs` — выбор очереди (US2): `same` (все кандидаты совпадают на день), `fixed` (после двух подряд
    однозначных совпадений реальных отключений), иначе `guess` (первая). Совпадение: начало и конец реального
    отключения в пределах 20 мин от границ планового окна; `maybe` не считается.
  - `fetch.rs` — запрос (FR-001), лимит 4 МБ, 304 → без изменений.
  - `mod.rs` — `DtekView` (контракт ниже) и `fn view(schedule, queue_state, now, cfg)`.
- **Сервис**: отдельная задача tokio — раз в 30 мин `fetch` → `parse` → `meta.dtek_cache` (+ ETag) → `watch::Sender<DtekView>`;
  при старте — из кэша. После каждого `OutageEnd` сервис отдаёт отключение в `queue::observe` (через канал), состояние —
  `meta.dtek_queue`. Ошибки — события `dtek-failed` / `dtek-ok` раз на серию.
- **API**: `GET /api/dtek` → `DtekView` (JSON, camelCase), под тем же Host-guard; в SSE — `event: dtek` при изменении.
- **Дашборд**: карточка `#dtek` перед секцией «Отключения»: шапка (адрес, плашка состояния), вкладки «Сегодня / Завтра»,
  сетка 6×4 (на узком экране 4×6) из кнопок-ячеек `HH:00` со значком молнии, легенда, подвал с временем ДТЭК / сбора и
  очередью. Стиль: «свет есть» — контур карточки; «нет» — заливка `#3B1E0A` + перечёркнутая молния цвета акцента;
  полчаса — заливка половины; «возможно» — штриховка; текущий час — рамка 2 px `#FF6F1A`.

## Контракт `GET /api/dtek`

```json
{ "enabled": true, "address": "вул. Прикладна, 1", "region": "kyiv",
  "queue": "GPV9.1", "queueStatus": "same", "candidates": ["GPV9.1","GPV21.1","GPV33.1","GPV45.1","GPV57.1"],
  "update": "13.10.2026 21:31", "collected": "2026-10-14T02:16:03+03:00", "stale": false, "error": null,
  "now": { "power": "on", "until": "2026-10-14T09:00:00+03:00" },
  "days": [ { "date": "2026-10-14", "label": "Сегодня", "cells": ["yes", "…24…"] },
            { "date": "2026-10-15", "label": "Завтра", "cells": null } ] }
```

## Конституция

I–V без изменений; VI изменён в v1.1.0 (показ графика, единственный GET к GitHub). Решения график не читают — тест:
`src/decision/` не импортирует `crate::dtek`.

## Тесты

- `parse`: реальный фрагмент `kyiv.json` (фикстура `tests/fixtures/kyiv-sample.json`, урезанная до 5 очередей), пределы,
  отказ целиком.
- `windows`: слияние, полчаса, «до HH:MM» на стыке дней.
- `queue`: same / guess / fixed, сброс серии, `maybe` не считается.
- API: форма `/api/dtek`.
