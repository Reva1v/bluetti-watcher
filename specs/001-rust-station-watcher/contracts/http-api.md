# HTTP API

Слушает `127.0.0.1:<port>` (по умолчанию 8765). Любой запрос с `Host`, отличным от `127.0.0.1:<port>` и
`localhost:<port>`, → 403. Время в ответах — ISO 8601 с локальным смещением. Ответы JSON, UTF-8.

## GET /

Встроенный дашборд (HTML + JS + uPlot).

## GET /api/now

```json
{
  "ts": "2026-10-14T21:05:00+03:00",
  "stale": false,
  "soc": 64,
  "acInW": 0, "acOutW": 182, "dcInW": 0, "dcOutW": 4, "acInV": 0.0,
  "mains": "off",
  "outage": { "since": "2026-10-14T19:40:00+03:00", "socStart": 98 },
  "ecoAt": null,
  "eco": "deep",
  "manual": "auto",
  "hoursLeft": 3.6, "runtimeSource": "rate",
  "levels": { "low": true, "critical": false, "warn": false },
  "hibernateDelayedUntil": null,
  "hibernateAvailable": true,
  "killSwitch": false,
  "dryRun": false,
  "lastError": null
}
```

- `stale`: последнее принятое показание старше 5 минут (цифры показываются как «последние известные»).
- `mains`: `on` / `off` / `unknown`. `eco`: `none` / `eco` / `deep`. `manual`: `auto` / `eco` / `max`.
- `ecoAt`: когда включится eco, если сеть не вернётся (только пока сеть пропала, а отключение ещё не началось).
- `runtimeSource`: `rate` / `learned` / `capacity` / null; `hoursLeft` null при нагрузке 0 или на сети.
- `lastError`: `{ "ts", "text" }` последней ошибки чтения или действия, null если всё в порядке.

## GET /api/history?range=6h|24h|7d|30d

```json
{ "step": 60, "points": [ { "t": 1760465100, "soc": 64, "in": 0, "out": 186 } ],
  "outages": [ { "start": 1760458800, "end": null } ] }
```

`t` — unix-секунды; `in` = acIn + dcIn, `out` = acOut + dcOut. Для 7d/30d — средние по 5/15 минут (`step` 300/900).
Только принятые показания.

## GET /api/outages?limit=50

`[{ "start", "end", "durationMin", "socStart", "socEnd", "eco", "hibernated" }]`, новые первыми.

## GET /api/events?limit=100

`[{ "ts", "kind", "text" }]`, новые первыми. `kind` — из data-model.md.

## GET /api/stream

Server-Sent Events: `event: now`, `data: <тело /api/now>` после каждого цикла чтения; `event: event` с новым событием.

## POST /api/mode

Заголовок `X-Bluetti-Watcher: 1` обязателен (иначе 403; простая форма с другого сайта его отправить не может).

Тело: `{ "mode": "auto" | "eco" | "max" }` → 202 `{ "manual": "max" }`; неизвестный режим → 400.
Применяется сервисом в ближайшем цикле. Уровни батареи и гибернация действуют при любом режиме.

## GET /api/health

`{ "ok": true, "version": "0.1.0", "lastReading": "<ts>|null", "encryption": true|null }` — для ярлыка и установщика.
