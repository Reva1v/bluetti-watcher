**English** · [Українська](README.uk.md)

# Bluetti Watcher

**Keeps your Windows PC running through power cuts on a BLUETTI power station - and hibernates it safely before the
battery runs out.**

[![CI](https://github.com/vitaliy-shatskiy/bluetti-watcher/actions/workflows/ci.yml/badge.svg)](https://github.com/vitaliy-shatskiy/bluetti-watcher/actions/workflows/ci.yml)
[![Latest release](https://img.shields.io/github/v/release/vitaliy-shatskiy/bluetti-watcher)](https://github.com/vitaliy-shatskiy/bluetti-watcher/releases/latest)
![Windows 10/11 x64](https://img.shields.io/badge/Windows%2010%2F11-x64-0078D6)
[![License: MIT](https://img.shields.io/badge/license-MIT-green)](LICENSE)

<p align="center">
  <img src="docs/screenshot.png" alt="The Bluetti Watcher dashboard during a power cut: battery 64 %, 6 h left at the current load" width="760">
</p>

In Ukraine scheduled and emergency outages are routine, and a desktop on a power station dies quietly when the battery
runs out. Bluetti Watcher reads the station once a minute over Bluetooth LE and, when mains is lost, switches the PC
to an energy-saving mode, raises an emergency at low battery and hibernates the PC before the station is empty. When
mains comes back it puts everything back, and a small dashboard on `127.0.0.1` shows the battery, the load, the time
left and the history.

## How it reacts

| When | What happens |
|---|---|
| Mains lost for 3 minutes | Eco: the Windows Power saver plan, a GPU power cap on NVIDIA cards (if set up), the flag says eco |
| Battery at 20 % | Emergency: the flag tells programs that read it to wind down |
| Battery at 10 % | Stop: the flag's level says critical, a signal to save work and stop |
| Battery at 7 % | Hibernate warning |
| Battery at 5 % | The PC hibernates, after a fresh reading agrees |
| Mains back for 3 minutes | Every mode lifted, the previous power plan back |

Every threshold is configurable. A level acts only after two agreeing readings; the full rules are in
[docs/design.md](docs/design.md).

## Quick start

Needs Windows 10 or 11 x64 with a Bluetooth LE adapter, PowerShell 7 (`pwsh`), Python 3.12, 3.13 or 3.14 on `PATH`
(a regular build, not free-threaded) and Chrome or Edge.

1. Download `bluetti-watcher-<version>-win-x64.zip` and its `.sha256` from
   [the latest release](https://github.com/vitaliy-shatskiy/bluetti-watcher/releases/latest), check the hash and
   extract the zip:

   ```powershell
   (Get-FileHash .\bluetti-watcher-*-win-x64.zip).Hash   # must equal the hash in the .sha256 file
   ```

2. Find the station's Bluetooth address (the BLUETTI app, or Windows Settings > Bluetooth).
3. In PowerShell 7, in the extracted folder:

   ```powershell
   pwsh -NoProfile -File install.ps1 -Mac AA:BB:CC:DD:EE:FF          # dry run: prints every step and what blocks it
   pwsh -NoProfile -File install.ps1 -Mac AA:BB:CC:DD:EE:FF -Apply   # install
   ```

Everything lands in `%LOCALAPPDATA%\bluetti-watcher` (`bin\`, `power-mode\`, `.venv\`, `config.json`, `station.db`,
`service.log`). The installer registers the logon task `BluettiWatcher` (current user, no elevation), starts it, waits
for `/api/health`, registers the wake task (disabled) and creates the `Bluetti Watcher` shortcut on the Desktop and in
the Start menu.

Optional, once, from an elevated PowerShell: the GPU power cap during eco on NVIDIA cards needs two scheduled tasks
that only an administrator can register:

```powershell
pwsh -NoProfile -File "$env:LOCALAPPDATA\bluetti-watcher\power-mode\power-mode.ps1" gpu-task install
```

## Features

- Reads the station over Bluetooth LE, read only; optional fallback to the BLUETTI cloud when Bluetooth is silent.
- Eco, emergency, stop, hibernate warning and hibernation by mains state and battery level, all thresholds configurable.
- Guards against bad readings: implausible jumps rejected, a longer confirmation after the station recalibrates.
- Dashboard in English and Ukrainian: battery, time left at the current load, power cuts, history, events.
- Manual control on the page: Auto, Eco, Max.
- Your own command when a mode begins (`actions`), and a flag file other programs can read.
- Optional wake timer and DTEK planned-outage schedule (Kyiv region).
- One NativeAOT exe, a per-user scheduled task, no window and no tray.

## Screenshots

| English | Українська |
|---|---|
| <img src="docs/screenshot.png" alt="Dashboard in English" width="400"> | <img src="docs/screenshot-uk.png" alt="Dashboard in Ukrainian" width="400"> |

Both were taken during a simulated cut (synthetic readings).

## Supported hardware

- BLUETTI AC200PL: tested.
- Other BLUETTI models that [bluetti-bt-lib](https://pypi.org/project/bluetti-bt-lib/) supports may work (set `type`
  in the configuration); untested. A report helps: [station support](https://github.com/vitaliy-shatskiy/bluetti-watcher/issues/new?template=station_support.yml).

Bluetti Watcher is an independent project, not affiliated with or endorsed by BLUETTI.

## Safety and privacy

- Read only toward the station: the library's writer is deleted from the venv, and the service refuses to start while
  any piece of it is on disk. No setting on the station is ever changed.
- The HTTP API listens on `127.0.0.1` only and refuses any request whose `Host` is not `127.0.0.1:<port>` or
  `localhost:<port>`. The one write route (`POST /api/mode`) needs a custom header that other web pages cannot send.
- Kill switch: an empty file `no-hibernate` in `%LOCALAPPDATA%\bluetti-watcher` turns hibernation off, checked on
  every poll. A dry run (`--dry-run` or `"dryRun": true`) logs every power-mode command and hibernation instead.
- Nothing leaves the PC unless you turn on one of two modules:
  - Cloud fallback: the token refresh to `sso.bluettipower.com` and the device-list read from `gw.bluettipower.com`.
    The token stays in the data directory and is never logged.
  - DTEK module: reads the public dataset on `raw.githubusercontent.com`. Only with `"site": true` it also reads the
    distributor's site (`www.dtek-krem.com.ua`) as a browser would, subject to the site's terms; your address goes
    there only if you set `city`, `street` and `house`.

Reporting a vulnerability: [SECURITY.md](SECURITY.md).

## Optional modules

- **Wake during a cut** (off by default): a PC hibernated in a cut wakes every `wakeEveryMinutes` to check whether
  mains is back; no wake is armed near an empty battery. Run the check below once before turning it on.
  Rules: [docs/design.md](docs/design.md#8-wake-during-a-cut).
- **DTEK module** (Kyiv region, off by default): the next planned outage for your queue, the expected return and
  whether the battery lasts. It never changes a power-mode decision.
  `"dtekWatcher": { "enabled": true, "queues": ["GPV1.1"] }` (add `"site": true` to also read the distributor's website: faster plan edits, subject to its terms); details:
  [docs/design.md](docs/design.md#11-dtek-module-planned-outages-kyiv-region).
- **Cloud fallback** (unofficial, read only): when Bluetooth does not answer, the reading comes from the BLUETTI cloud.
  Sign in once with `& "$env:LOCALAPPDATA\bluetti-watcher\bin\BluettiWatcher.exe" --cloud-login`; it may stop working
  if BLUETTI changes its API. Details: [docs/design.md](docs/design.md#4-cloud-fallback).
- **Claude Code integration**: a hook that tells each Claude Code session on the PC the power mode and battery level,
  so sessions save work and avoid heavy jobs during a cut: [integrations/claude-code](integrations/claude-code/README.md).

<details>
<summary>Wake check: run once before setting <code>wakeEveryMinutes</code></summary>

From a normal (not elevated) PowerShell after the install:

```powershell
$t = Get-ScheduledTask -TaskName BluettiWatcherWake; $t.Triggers[0].StartBoundary = (Get-Date).AddMinutes(4).ToString('s'); $t.Triggers[0].Enabled = $true; $t.Settings.Enabled = $true; Set-ScheduledTask -InputObject $t | Out-Null; powercfg /waketimers; shutdown /h
```

`powercfg /waketimers` must list the task before the PC goes down, and the PC must come back by itself about 4 minutes
later (`powercfg /lastwake` names the source; `Disable-ScheduledTask -TaskName BluettiWatcherWake` turns it off). If it
does not wake, RTC wake from hibernation is off in the firmware or "Allow wake timers" is off in the power plan. If it
does: `"wakeEveryMinutes": 45` in `config.json` and a restart of the task. `install.ps1 -RemoveWake -Apply` removes
the wake task.

</details>

## FAQ

**Windows warns about the download, or PowerShell refuses `install.ps1` as not digitally signed.**
The release is not code-signed. Compare `(Get-FileHash <zip>).Hash` with the `.sha256` file from the same release, then
unblock the extracted folder: `Get-ChildItem -Recurse | Unblock-File`.

**Does it change any setting on the station?**
No. It only reads; the code that could write to the station is deleted from the venv, and the cloud client never sends
a control command.

**My station is not an AC200PL.**
Set `type` to your model; if [bluetti-bt-lib](https://pypi.org/project/bluetti-bt-lib/) supports it, it may work.
Please open a [station support](https://github.com/vitaliy-shatskiy/bluetti-watcher/issues/new?template=station_support.yml)
issue either way.

**Bluetooth does not answer.**
Keep the station in range and close the BLUETTI app on your phone, which may hold the connection. `service.log` in the
data directory names each miss; the cloud fallback covers the gaps.

**`bluetti-read` fails with `DLL load failed ... The filename or extension is too long`.**
The venv sits on too long a path (winrt). `%LOCALAPPDATA%\bluetti-watcher\.venv` is short enough; from source, keep
`-VenvDir` short.

**How do I stop a hibernation right now?**
`New-Item "$env:LOCALAPPDATA\bluetti-watcher\no-hibernate"`. It is checked on every poll; delete the file to turn
hibernation back on.

**Other brands (EcoFlow, Jackery, ...)?**
No. The reader speaks the BLUETTI protocol only.

## Install from source

```powershell
git clone https://github.com/vitaliy-shatskiy/bluetti-watcher.git
cd bluetti-watcher
pwsh -NoProfile -File install.ps1 -Mac AA:BB:CC:DD:EE:FF          # dry run
pwsh -NoProfile -File install.ps1 -Mac AA:BB:CC:DD:EE:FF -Apply   # publishes NativeAOT, then installs as above
```

Needs the .NET SDK named in `global.json` and, for NativeAOT, the MSVC linker (Visual Studio Build Tools, "Desktop
development with C++"); `-Fallback` builds a trimmed single-file exe without it. Development without installing:
[CONTRIBUTING.md](CONTRIBUTING.md).

## Configuration

`%LOCALAPPDATA%\bluetti-watcher\config.json`; only `mac` is required. Every key, its default and an example:
[docs/configuration.md](docs/configuration.md). The rules the service follows: [docs/design.md](docs/design.md). The
HTTP API: [docs/api.md](docs/api.md). Restart the task after an edit:

```powershell
Stop-ScheduledTask BluettiWatcher; Start-ScheduledTask BluettiWatcher
```

## Update and uninstall

To update, run `install.ps1 -Apply` from the new release's folder. `config.json`, the history and the cloud token stay;
the new exe is staged next to the running one and swapped in seconds, and a failed health check puts the previous
version back. What changed: [CHANGELOG.md](CHANGELOG.md).

<details>
<summary>Uninstall</summary>

In this order (the task restarts the exe after any exit until it is disabled, and a running service would set eco
again; `power-mode.ps1` and the GPU task removal need the data directory's scripts, so they run before it is deleted):

```powershell
# 1. stop the service and wait until the exe has exited
Disable-ScheduledTask -TaskName BluettiWatcher | Out-Null
Stop-ScheduledTask -TaskName BluettiWatcher
Get-Process BluettiWatcher -ErrorAction SilentlyContinue | Wait-Process -Timeout 30

# 2. leave eco: puts back the power plan and the GPU power limit eco changed and removes the eco end task
pwsh -NoProfile -File "$env:LOCALAPPDATA\bluetti-watcher\power-mode\power-mode.ps1" off

# 3. only if the GPU tasks were installed: from an elevated PowerShell
pwsh -NoProfile -File "$env:LOCALAPPDATA\bluetti-watcher\power-mode\power-mode.ps1" gpu-task remove

# 4. unregister the tasks (the end task is already gone after step 2; this clears a leftover)
Unregister-ScheduledTask -TaskName BluettiWatcher -Confirm:$false
Unregister-ScheduledTask -TaskName BluettiWatcherEcoEnd -Confirm:$false -ErrorAction SilentlyContinue
pwsh -NoProfile -File install.ps1 -RemoveWake -Apply

# 5. delete the files
Remove-Item -Recurse "$env:LOCALAPPDATA\bluetti-watcher", "$env:ProgramData\BluettiWatcher"
```

Step 2 prints `power mode: normal` and one line per thing it put back, for example `plan: High performance` and
`gpu: power limit back to <W> W`; a line starting with `warn:` or `note:` means something stayed owed, so run it again
before going on. When `config.json` sets `powerMode` names other than the defaults, set `POWER_MODE_FILE`,
`POWER_MODE_MUTEX` and `POWER_MODE_TASK_PREFIX` to them first (and use `<taskPrefix>End` in step 4); the
[power-mode README](power-mode/README.md#configuration) lists them.

Then delete the `Bluetti Watcher` shortcuts.

</details>

---

[Contributing](CONTRIBUTING.md) · [Security](SECURITY.md) · [Changelog](CHANGELOG.md)

MIT, see [LICENSE](LICENSE). Not affiliated with or endorsed by BLUETTI.
