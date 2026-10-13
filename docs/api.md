# HTTP API

The service listens on `http://127.0.0.1:<port>/` (port 18940 by default) and nowhere else. Answers are JSON (UTF-8)
unless noted. Times are ISO 8601 with the PC's local offset (`2026-01-31T18:05:00+02:00`); query times may be ISO 8601
or Unix milliseconds.

## Guard

- Every request: the `Host` header must be exactly `127.0.0.1:<port>` or `localhost:<port>`; anything else is `403
  forbidden: foreign Host`. This blocks DNS rebinding.
- `POST /api/mode` also needs the header `X-Station: 1` and an `Origin` that is absent or the service's own
  (`http://127.0.0.1:<port>` or `http://localhost:<port>`); else 403 with the reason. A web page on another origin
  cannot send the custom header without a CORS preflight, and the service never sends a CORS header.
- Wrong method: 405 (`POST only` for `/api/mode`, `GET only` elsewhere). Unknown path: 404.

## GET /

The dashboard page (`text/html`). Also `/index.html`.

## GET /api/now

The current state, for the page.

| Field | Meaning |
|---|---|
| `type`, `capacityWh`, `efficiency` | From the configuration. |
| `mode` | The station's output mode, readable (`inverter output`); `modeCode` the raw code (`INVERTER_OUTPUT`, `CLOUD_MAINS`, ...). |
| `eco`, `ecoNote` | The power mode in words ("" when normal) and as a coded note. |
| `manual` | The page's choice: `auto`, `eco`, `max`. `modePending`: a choice not applied yet, or null. |
| `modeError`, `modeErrorNote` | The last refusal or failure of a page choice ("" when none), and as a coded note. |
| `level` | Battery level: `ok`, `low`, `critical`, `hibernating`. |
| `cut` | true while a power cut is confirmed. |
| `grid` | Mains: `on`, `off`, `unknown`. |
| `silent` | true while the station does not answer. |
| `ecoAt` | When eco begins if mains stays off; null when no eco is pending. |
| `hibernateDelayedUntil` | During a cut: the earliest time the rebase rule allows hibernate; else null. |
| `note`, `notes` | Notes for the page: one text joined with ` · `, and the list as `{code, text, args}`. |
| `dryRun` | true in a dry run. |
| `hoursLeft` | Hours of battery to empty at the current load; null on mains or without an estimate. |
| `runtimeSource` | `rate` (measured), `learned`, `config`; null without an estimate. |
| `ratePerHour` | Measured discharge, % per hour; null unless `runtimeSource` is `rate`. |
| `learnedEfficiency` | The learned efficiency, or null. |
| `thresholds` | `emergencyAt`, `stopAt`, `warnAt`, `hibernateAt`. |
| `last` | The last accepted reading, or null: `at`, `soc`, `inAc`, `inDc`, `outAc`, `outDc`, `volt`, `hz`, `mains`. |
| `wake` | The wake timer, or null: `everyMinutes`, `floorPercent`, `armed`, `wakeAt`, `disarmOwed`, `error`. |
| `dtekWatcher` | The DTEK module (below). |

`dtekWatcher` fields:

| Field | Meaning |
|---|---|
| `state` | `off` module off · `ok` the schedule in use covers now and is fresh · `stale` it covers now and is older than `staleAfterMin` · `none` no schedule covers now, or the house's queues are not known. |
| `note`, `notes` | Text for the page, and coded notes. |
| `source` | Where the schedule in use came from: `dataset` or `site`. |
| `update` | The distributor's own update time of the schedule in use, as written. |
| `collectedAt` | When that schedule was collected. |
| `datasetState`, `siteState` | Per source: `ok`, `failed`, `off`; the site also `blocked`. |
| `queues`, `queuesSource` | The house's queues and where they came from (`config`, `site`, ""). |
| `queue`, `queueSource` | The queue followed, or null, and how it was found (`config`, `matched`, `single`, ""). |
| `queueOrigin`, `queueContradictions` | `manual`, `detected`, `manual-refuted` or ""; cuts in the running row against the manual queue. |
| `queueSet` | Members of the followed queue (one for a single queue; none while no queue is followed). |
| `nextStart`, `nextEnd`, `nextQueue`, `nextQueues` | The first planned outage not over yet among the shown queues, and the members it came from. |
| `return` | During a cut: the expected return (the planned window's end, else the site's); else null. |
| `outageKind`, `outageStart`, `outageEnd`, `outageEndAt` | The site's current outage (texts as written; `outageEndAt` as a time or null). |
| `batteryLasts` | During a cut: `yes`, `no`, `unknown`; else null. |
| `overdue`, `overdueMin` | The cut outlasted its planned window; whole minutes since its end, or null. |
| `plannedReturn` | During a cut: the end of the planned window followed; else null. |
| `batteryUntil` | During a cut: when the battery reaches `hibernateAt` at the current load; else null. |
| `planChanges` | Plan edits seen during this cut (0 outside a cut). |
| `lateBackMin`, `lateCuts` | Median minutes the power came back after the plan (null under 3 recorded cuts), and the cuts recorded. |
| `today`, `tomorrow` | Dates `DD.MM.YYYY` (Kyiv). |
| `windows` | Per shown queue: `queue`, `today`, `tomorrow` (text such as `17:00-21:00, possible 21:00-22:00`, `no cuts`, or ""), `intervals` (`start`, `end`, `queues`). |

## GET /api/history?from&to&step

Stored readings. `to` defaults to now, `from` to 26 hours before `to`; `step` seconds, default 60. A step of 60 or
less returns each stored row (raw minutes, and 5-minute aggregates for older times); a larger step groups them.

```json
{ "step": 60, "rows": [ { "at": "...", "soc": 87, "inAc": 0, "inDc": 0, "outAc": 142, "outDc": 0, "volt": 52.1, "hz": 50.0, "mains": false } ] }
```

400 when a time cannot be read, `step` < 1 or `from` >= `to`.

## GET /api/events?from&to

Stored events, same time defaults: `{ "events": [ { "at", "kind", "detail" } ] }`. Kinds include `eco-on`,
`emergency-on`, `mains-back`, `stop`, `hibernate-warning`, `hibernate`, `hibernate-refused`, `hibernate-skipped`,
`hibernate-delayed`, `mode-dropped`, `mode-refused`, action failures, `wake-*` and `dtek-watcher-*`.

## GET /api/health

| Field | Meaning |
|---|---|
| `ok` | true when `problem` is null; false while a problem is set (for example when `mac` is missing). |
| `problem` | What keeps the service from working (for example the missing `mac`), or null. |
| `version` | The service version. |
| `uptimeSec` | Seconds since start. |
| `dryRun` | true in a dry run. |
| `lastPollAt`, `lastAcceptedAt` | Times of the last poll and the last accepted reading, or null. |
| `lastSource` | `bluetooth` or `cloud`: the source of the last accepted reading, or null. |
| `misses` | Misses in a row. |
| `silent` | true while the station does not answer. |
| `rawRows`, `aggregateRows` | Rows in the database. |

## POST /api/mode

Body `{"mode": "auto" | "eco" | "max"}`, headers per the guard above.

```powershell
Invoke-RestMethod -Method Post -Uri http://127.0.0.1:18940/api/mode -Headers @{ 'X-Station' = '1' } -ContentType 'application/json' -Body '{"mode":"eco"}'
```

The page's Eco fires the `eco` action and Max (or Auto lifting the page's Eco) fires `normal`, when `actions` define
them: an executable with arguments, no shell, logged only in a dry run ([configuration.md](configuration.md), "Power
mode and actions"; the power-mode commands have their own limit, `powerModeTimeoutSec`).

Answer: `{ "ok", "accepted", "manual", "message", "code" }`. 202 = saved; the service loop applies it. 409 = Max refused
during a battery emergency (`code` names it). 400 = a body that is not JSON or a mode that is not one of the three.
