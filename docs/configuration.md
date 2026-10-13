# Configuration

The service reads its defaults, then `config.json` in the data directory (`%LOCALAPPDATA%\bluetti-watcher` unless
`--data-dir` or `--config` says otherwise), then the command-line flags. Keys are camelCase and case-sensitive (`MAC`
is not `mac`; the installer refuses it too); comments (`//`) are allowed; a key left out keeps its default. A change takes effect at the next start of the service
(`Stop-ScheduledTask BluettiWatcher; Start-ScheduledTask BluettiWatcher`).

The rules these values feed are in [design.md](design.md).

## Minimal example

```json
{
  "mac": "AA:BB:CC:DD:EE:FF"
}
```

`mac` is the station's Bluetooth address (the BLUETTI app or Windows Bluetooth settings show it). Without it the
service starts, serves the page and `/api/health` with a problem naming the missing `mac`, and polls nothing.

## Full example

Every key with its default, except `mac`, `actions` and the DTEK address, which have none.

```json
{
  "mac": "AA:BB:CC:DD:EE:FF",
  "type": "AC200PL",
  "capacityWh": 2304,
  "efficiency": 0.85,

  "pollSec": 60,
  "fastPollSec": 60,
  "fastBelow": 25,
  "readerTimeoutSec": 30,

  "emergencyAt": 20,
  "stopAt": 10,
  "warnAt": 7,
  "hibernateAt": 5,
  "agreeSec": 90,
  "hibernateAfterWarnSec": 180,
  "rebaseHibernateConfirmSec": 600,
  "jumpWindowSec": 120,
  "mainsLostSec": 180,
  "mainsBackSec": 180,
  "maxGapSec": 600,
  "socRatePerMin": 5,
  "socJumpFloor": 3,
  "silentAfter": 3,
  "batteryRefreshSec": 600,

  "rateWindowMin": 60,
  "rateMinDrop": 3,
  "rateMinSpanMin": 20,

  "powerMode": {
    "script": "<exe dir>\\..\\power-mode\\power-mode.ps1",
    "flagPath": "C:\\ProgramData\\BluettiWatcher\\power-mode.json",
    "mutex": "Local\\BluettiWatcherPowerMode",
    "taskPrefix": "BluettiWatcherEco",
    "extraReaders": []
  },
  "pwshPath": "pwsh.exe",
  "powerModeTimeoutSec": 60,
  "actionTimeoutSec": 30,
  "actions": {
    "eco": "pwsh -NoProfile -File C:\\scripts\\on-eco.ps1",
    "normal": "pwsh -NoProfile -File C:\\scripts\\on-normal.ps1"
  },

  "wakeEveryMinutes": 0,
  "wakeReadSec": 300,
  "wakeFloorPercent": null,
  "wakeTaskName": "BluettiWatcherWake",

  "cloudEnabled": null,
  "cloudTokenPath": null,
  "staleLoadW": 100,
  "staleAfterMin": 30,

  "port": 18940,
  "lockWaitSec": 30,
  "dataDir": "%LOCALAPPDATA%\\bluetti-watcher",
  "readerPath": "%LOCALAPPDATA%\\bluetti-watcher\\.venv\\Scripts\\bluetti-read.exe",
  "rawRetentionDays": 30,
  "dryRun": false,

  "dtekWatcher": {
    "enabled": false,
    "queues": ["GPV1.1", "GPV2.1"],
    "group": "GPV1.1",
    "datasetUrl": "https://raw.githubusercontent.com/Baskerville42/outage-data-ua/main/data/kyiv-region.json",
    "staleAfterMin": 90,
    "site": false,
    "host": "www.dtek-krem.com.ua",
    "city": "",
    "street": "",
    "house": ""
  }
}
```

`<exe dir>` and `%LOCALAPPDATA%` above stand for the real paths: write an absolute path in a real file, or leave the
key out for its default. JSON strings need `\\` for each backslash.

## Station

| Key | Type | Default | Meaning |
|---|---|---|---|
| `mac` | string | none (required) | The station's Bluetooth address. |
| `type` | string | `AC200PL` | The model name passed to `bluetti-read` and matched against the cloud's device model. |
| `capacityWh` | number | 2304 | Battery capacity, Wh, for the battery-time formula. |
| `efficiency` | number | 0.85 | Inverter efficiency for the formula, until a learned value exists. |

## Polling and readings

| Key | Type | Default | Meaning |
|---|---|---|---|
| `pollSec` | int | 60 | Seconds between readings. |
| `fastPollSec` | int | 60 | Seconds between readings on battery under `fastBelow`. |
| `fastBelow` | number | 25 | Battery percent under which `fastPollSec` applies. |
| `readerTimeoutSec` | int | 30 | Time limit of one `bluetti-read` run. |
| `maxGapSec` | int | 600 | A gap between accepted readings over this resets the decision state. |
| `socRatePerMin` | number | 5 | Largest plausible SOC change, points per minute. |
| `socJumpFloor` | number | 3 | The allowed SOC change is never under this many points. |
| `jumpWindowSec` | int | 120 | A reading accepted after a silence that jumps more than the rate allows over this many seconds counts as a rebase for hibernate. |
| `silentAfter` | int | 3 | Misses in a row before the station is reported silent. |

## Mains and battery levels

| Key | Type | Default | Meaning |
|---|---|---|---|
| `mainsLostSec` | int | 180 | No AC input this long -> the cut begins, eco on. |
| `mainsBackSec` | int | 180 | AC input held this long -> every mode lifted. |
| `emergencyAt` | number | 20 | Percent: station emergency. |
| `stopAt` | number | 10 | Percent: stop (the flag's level says critical). |
| `warnAt` | number | 7 | Percent: hibernate warning. |
| `hibernateAt` | number | 5 | Percent: the PC hibernates. |
| `agreeSec` | int | 90 | Two agreeing readings at least this far apart confirm a level. |
| `hibernateAfterWarnSec` | int | 180 | Hibernate waits at least this long after the warning. |
| `rebaseHibernateConfirmSec` | int | 600 | After a rebase in the cut, hibernate needs this long at or under `hibernateAt`. |
| `batteryRefreshSec` | int | 600 | On battery, the reading goes into the flag at least this often. |

## Battery time

| Key | Type | Default | Meaning |
|---|---|---|---|
| `rateWindowMin` | int | 60 | Minutes of readings off mains the measured discharge rate looks back over. |
| `rateMinDrop` | number | 3 | Least SOC drop, points, the measured rate needs. |
| `rateMinSpanMin` | int | 20 | Least span, minutes, the measured rate needs. |

## Power mode and actions

| Key | Type | Default | Meaning |
|---|---|---|---|
| `powerMode.script` | string | `<exe dir>\..\power-mode\power-mode.ps1` | The power-mode switch the service runs. |
| `powerMode.flagPath` | string | `%ProgramData%\BluettiWatcher\power-mode.json` | The flag file; passed to the switch as `-Path` and `POWER_MODE_FILE`. |
| `powerMode.mutex` | string | `Local\BluettiWatcherPowerMode` | The named mutex guarding the flag; passed as `POWER_MODE_MUTEX`. |
| `powerMode.taskPrefix` | string | `BluettiWatcherEco` | Prefix of the switch's scheduled tasks (`<prefix>End`, `<prefix>GpuCap`, `<prefix>GpuRestore`); passed as `POWER_MODE_TASK_PREFIX`. |
| `powerMode.extraReaders` | string[] | `[]` | Local accounts allowed to read the flag folder besides the user, SYSTEM and Administrators (e.g. a service that shows the mode); passed as `POWER_MODE_EXTRA_READERS`, comma-joined. Each name `A-Z a-z 0-9 _ . - space`, 1-64 chars; an invalid one is reported in `/api/health` `problem` and none are passed. |
| `flagPath` | string | - | Older name of `powerMode.flagPath`; still read. |
| `pwshPath` | string | `pwsh.exe` | PowerShell 7 used to run the switch and the wake-task commands. |
| `powerModeTimeoutSec` | int | 60 | 1-600: time limit of each power-mode command and each wake-task command. A command over it is killed and counts as failed. |
| `actionTimeoutSec` | int | 30 | 1-600: time limit of each action (only actions). |
| `actions` | object | none | Commands run when the service enters a state: keys `eco`, `emergency`, `stop`, `hibernate-warn`, `normal`. See below. |
| `dryRun` | bool | false | Log every power-mode command, action and hibernation instead of running it (also `--dry-run`). |

An action is an executable followed by its arguments, started directly with no shell: a pipe, a redirect or a shell
built-in needs `cmd /c ...`. It runs windowless, with the environment values `POWER_MODE_FILE`, `POWER_MODE_MUTEX`,
`POWER_MODE_TASK_PREFIX` and `POWER_MODE_EXTRA_READERS` set, within `actionTimeoutSec`. Its exit code and the first 300 characters of its output are
logged as an event; a missing, failing or hanging command never changes a decision. It fires once per entry into a
state per power cut (the next cut fires again; `normal` fires once per lift). The page's Eco fires `eco`; its Max and
Auto-lift fire `normal`. A dry run logs the actions and their environment instead of running them.

A flag path set while `powerMode.mutex` is left at its default is reported at start (log and event `config-warning`):
set both, or neither.

Hibernation off: create an empty file `no-hibernate` in the data directory. It is read on every poll.

## Wake during a cut

| Key | Type | Default | Meaning |
|---|---|---|---|
| `wakeEveryMinutes` | int | 0 | 10-1440: a hibernation during a cut arms the wake task this many minutes ahead. 0 = off. Turn it on only after the check in the README passes. |
| `wakeReadSec` | int | 300 | 60-3600: woken by the timer with no accepted reading for this long, the PC hibernates again. |
| `wakeFloorPercent` | number or null | null | At or under this percent no wake is armed. null = `hibernateAt` - 5 (at least 0); bounded to 0..`hibernateAt`; 0 = no floor. |
| `wakeTaskName` | string | `BluettiWatcherWake` | The wake task `install.ps1` registers: 1-100 characters of letters, digits, space, `.`, `_`, `-`; never `BluettiWatcher` (the service's own task). A task of that name that is not the wake task the installer registers is never replaced or removed. |

## Cloud fallback

| Key | Type | Default | Meaning |
|---|---|---|---|
| `cloudEnabled` | bool or null | null | null = on when the token file exists at start; `true` on; `false` off. |
| `cloudTokenPath` | string or null | null | The token file; null = `cloud-token.json` in the data directory. `BluettiWatcher.exe --cloud-login` writes it. |
| `staleLoadW` | number | 100 | A cloud reading off mains with at least this AC load whose SOC has not moved for `staleAfterMin` is stale (a miss). |
| `staleAfterMin` | int | 30 | Minutes for the stale check; 0 or less = no check. |

## Service

| Key | Type | Default | Meaning |
|---|---|---|---|
| `port` | int | 18940 | HTTP port on 127.0.0.1 (also `--port`). |
| `lockWaitSec` | int | 30 | 0-600: a start waits this long for an older instance to release the port's lock. |
| `dataDir` | string | `%LOCALAPPDATA%\bluetti-watcher` | Database, log, config and kill switch (also `--data-dir`). |
| `readerPath` | string | `<data dir>\.venv\Scripts\bluetti-read.exe` | The Bluetooth reader (also `--reader`). |
| `rawRetentionDays` | int | 30 | Raw minute rows kept this long, then folded into 5-minute aggregates. |

## DTEK module (`dtekWatcher`)

Off by default; it makes no request while off. `null` = off.

| Key | Type | Default | Meaning |
|---|---|---|---|
| `enabled` | bool | false | Turns the module on. |
| `queues` | string[] | none | The house's queues, such as `GPV1.1`. Without them (and with `site` off) the module cannot tell which queue is yours. |
| `group` | string, string[] or null | null | The queue to follow, a hint real cuts can refute; an array = a set whose planned outage is the union (at most 4). |
| `datasetUrl` | string | the Baskerville42/outage-data-ua Kyiv region file | Primary schedule source; must be plain https. |
| `staleAfterMin` | int | 90 | The schedule is stale when its newest collection is older. |
| `site` | bool | false | Opt-in. false = the distributor's site is never asked; only the public dataset is read. true = the watcher also reads the distributor's website the way a browser does, which picks up plan edits sooner and, with `city`, `street` and `house`, names the house's queues. Use of the site is subject to its own terms; you turn it on at your own responsibility. |
| `host` | string | `www.dtek-krem.com.ua` | The site's host; with the dataset's, the only hosts a request may go to. |
| `city`, `street`, `house` | string | "" | Optional address, sent to the site only, to learn the house's queues and current outage. |

## Command line

`BluettiWatcher.exe [--config <file>] [--data-dir <dir>] [--port <n>] [--reader <bluetti-read.exe>] [--flag <file>]
[--power-mode <power-mode.ps1>] [--dry-run]` - flags override config.json. `--flag` sets `powerMode.flagPath`,
`--power-mode` sets `powerMode.script`.

`BluettiWatcher.exe --cloud-login [--data-dir <dir>]` - signs in to the BLUETTI cloud in the browser and writes the
token file, then exits. An answer without a valid `expires_in` is refused and nothing is written.

`BluettiWatcher.exe --help` (`-h`, `/?`) prints the usage and `--version` prints `Bluetti Watcher <version>`; both exit 0
and win over every other word on the line (`--cloud-login --help` only prints the usage). A flag that takes a value
refuses a missing value or one that starts with `--`; an unknown flag or word prints a message and exits 2. The exe has no
console of its own: these commands print into the terminal they were started from (nothing, when started by the logon
task). PowerShell does not wait for such an exe, so `$LASTEXITCODE` is not set after a plain `& .\BluettiWatcher.exe`;
pipe it (`& .\BluettiWatcher.exe --help | Out-Host`) or use `Start-Process -NoNewWindow -Wait -PassThru`.

## Environment of the power-mode scripts

`power-mode/*.ps1` read `POWER_MODE_FILE`, `POWER_MODE_MUTEX`, `POWER_MODE_TASK_PREFIX` and `POWER_MODE_EXTRA_READERS`,
with the defaults above (no extra readers). The service sets all four from `powerMode` for every child it starts; set them yourself when you run the scripts by
hand with non-default names.
