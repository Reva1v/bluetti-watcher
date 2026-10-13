# power-mode: the power-mode switch

Three PowerShell scripts that switch the PC between **normal**, **eco** (save electricity, keep working) and
**emergency** (save electricity, wind down), and keep the mode in one flag file. Bluetti Watcher runs them when the
battery station's state changes; you can run them by hand; any other program (a script, an agent hook from [`integrations/`](../integrations/)) can read the flag.

| File | What it is |
|---|---|
| `power-mode.ps1` | the switch (PowerShell 7, `pwsh`) |
| `power-mode-flag.ps1` | the library both scripts dot-source: the one reader of the flag and the one place its names are read. Dot-source it from your own scripts to read the flag (`Read-PowerModeFlag (Get-PowerModeFlagPath)`). |
| `power-mode-end.ps1` | what the eco end task runs at the end time (Windows PowerShell 5.1) |

Installed beside the watcher at `<data dir>\power-mode\`; the watcher's default `powerMode.script` points there.

## Commands

```powershell
pwsh -NoProfile -File power-mode.ps1 <command> [options]
```

| Command | What it does |
|---|---|
| `status` | one line: `power mode: normal`, `power mode: EMERGENCY since …` or `power mode: ECO since … until …`; then the plan it returns to, the end task, the GPU cap and the last battery reading when there are any. Exit 0 normal, 1 eco or emergency, 2 the flag cannot be read. |
| `on` | emergency on. Already on: `since` kept. |
| `off` | back to normal: the power plan and the GPU limit eco changed are put back, the end task removed. Already off: `since` kept. |
| `eco [-For <minutes> \| -Until HH:mm]` | eco for 4 hours, or `-For` 1-1440 minutes, or until the next `HH:mm` (the PC's local time; `24:00` = midnight). Activates the Power saver plan, caps the GPU (optional, below) and registers a one-shot task that ends eco at that time. Refused while emergency is on. |
| `eco -Source station` | eco with no end time, as the watcher sets it while mains is off; lasts until `off`. Never replaces a manual eco that is on. |
| `eco -Adopt -ReturnPlan balanced\|high\|<GUID>` | an eco that is on and has no plan to return to gets one, and its end task. |
| `end` | an eco whose end time has passed gets its plan back and the flag reads normal (what the end task runs). |
| `battery -Soc <0-100> -Level ok\|low\|critical\|hibernating -Mains on\|off [-HibernateAt <percent>]` | the watcher's station reading into the flag; nothing else changes. |
| `prev-plan balanced\|high\|<GUID>` | rewrites the plan an eco that is on returns to. Exit 1 when no eco is on. |
| `gpu-task install [-RestoreWatts <W>]` / `remove` / `status` | the two elevated GPU tasks (below). |

Common options: `-Path <flag file>` uses another flag file (tests; its folder must exist), `-Source manual|station`
records who switched (`on`, `off`, `eco`).

Exit codes: `0` done or already so · `1` (status) eco or emergency, (gpu-task install) a task was not registered as
written, (gpu-task status) a task is missing, (prev-plan) no eco is on · `2` could not run: bad arguments, a flag file
that cannot be trusted, a write that failed, a missing elevation. A corrupt flag is never read as a silent "normal".

## Configuration

Environment variables, read in `power-mode-flag.ps1` only. Bluetti Watcher passes all four to every power-mode
script it starts (its config section `powerMode`: `flagPath`, `mutex`, `taskPrefix`, `extraReaders`); set them yourself
when you run the scripts by hand with non-default values. The eco end task runs from the Task Scheduler with no
environment: the switch writes all four on its command line. A value outside the forms below stops every script (exit 2).

| Variable | Default | What it names |
|---|---|---|
| `POWER_MODE_FILE` | `%ProgramData%\BluettiWatcher\power-mode.json` | the flag file |
| `POWER_MODE_MUTEX` | `Local\BluettiWatcherPowerMode` | the named mutex every writer of the flag holds (the watcher takes it too); `Local\` or `Global\` plus letters, digits, `_ . -` |
| `POWER_MODE_TASK_PREFIX` | `BluettiWatcherEco` | the scheduled tasks `<prefix>End`, `<prefix>GpuCap`, `<prefix>GpuRestore`; letters, digits, `_ . -` only |
| `POWER_MODE_EXTRA_READERS` | none | accounts (comma list) that may hold read, never write, on the flag folder; plain account names (letters, digits, space, `_ . -`), no domain part |

`power-mode.config.json`, beside the flag file (optional):

```json
{ "returnPlan": "high", "gpuCap": true }
```

- `returnPlan`: the plan every end of eco sets. `high` (default) = High performance; `balanced` = Balanced with the
  Windows 11 power mode "Best performance"; `previous` = the plan that was active before eco (High performance when
  that is unknown, Power saver or gone); or a plan GUID (`powercfg /list`). A plan that is not on this machine =
  Balanced, with a `warn:` line. Power saver is never a plan to return to.
- `gpuCap`: `false` = eco never touches the GPU.

A missing file or key = the defaults; one that cannot be read = the defaults plus a `warn:` line, never a failure.

## The flag file

One line of JSON, UTF-8 without BOM, written to a temp file and moved over the flag under the mutex:

```json
{ "mode": "eco", "since": "2026-10-13T20:08:00+03:00", "until": "2026-10-14T00:08:00+03:00", "prevPlan": "381b4222-f694-41f0-9685-ff5bb260df2e", "prevGpuLimitW": 250, "gpuCapW": 100 }
```

| Field | Meaning |
|---|---|
| `mode` | `normal`, `eco` or `emergency` |
| `since` | when the mode began, ISO 8601 with offset |
| `until` | eco only: its end. Past it every reader sees normal, whether or not the end task ran. An eco with `"source": "station"` has none. |
| `source` | `station` when the watcher set the mode; absent = by hand |
| `by` | who wrote a normal flag: `manual` or `station` |
| `prevPlan` | the power plan eco owes back |
| `prevGpuLimitW`, `gpuCapW` | the GPU limit eco owes back and the cap it set, watts |
| `replaced` | `{ mode, since, until }` of a manual eco the watcher's emergency replaced (named in the status line) |
| `battery` | `{ soc, at, level, mains, hibernateAt }`, the watcher's last station reading; `level` is `ok`, `low`, `critical` or `hibernating` |

A missing file is normal. The configured flag's folder is created on the first write with its own ACL: you Modify,
SYSTEM and Administrators Full control, nothing inherited. An existing folder with any other entry (beyond the read of
`POWER_MODE_EXTRA_READERS`), inheritance on, or a junction in its place refuses every write and reads as untrusted;
the scripts never change that ACL, fix it by hand. A reader running as another account (a service, SYSTEM) therefore
always reads the flag as untrusted: run the readers as the user.

## Power plan and GPU

Eco records the active Windows power plan and activates the built-in Power saver plan; every end of eco (`off`, the
end task, the watcher's `off` when mains is back) sets the plan `returnPlan` names. A plan that cannot be set stays
owed in the flag for the next `off`.

The GPU cap is optional. With an NVIDIA card and `nvidia-smi` in System32, eco records the power limit and sets the
card's minimum; the end of eco puts the recorded limit back. No NVIDIA card or no `nvidia-smi` = a `note:` line and
eco works without the cap.

### What needs administrator rights

Only setting the GPU power limit. An elevated shell runs `nvidia-smi -pl` itself. For everything else (the watcher,
a normal shell) run once, in an elevated PowerShell 7:

```powershell
pwsh -NoProfile -File power-mode.ps1 gpu-task install            # -RestoreWatts <W> to restore to another limit than the card's default
```

It registers two on-demand tasks, `<prefix>GpuCap` (`nvidia-smi -pl <card minimum>`) and `<prefix>GpuRestore`
(`nvidia-smi -pl <default or -RestoreWatts>`), run at the highest level as you, with the wattage fixed at install:
nothing a process without elevation writes reaches what runs elevated. `gpu-task status` lists them; `gpu-task
remove` (elevated) deletes them. The eco end task and the power plan need no elevation.

## Tests

From the repository root, on Windows with PowerShell 7 (`pwsh`) on PATH and Node.js 20 or later:

```powershell
node --test power-mode/power-mode.test.js
```

The tests never touch the real flag, power plan, Task Scheduler or GPU: every flag lives in a temp folder, and
`powercfg`, `schtasks` and `nvidia-smi` are replaced by stand-in scripts through `POWER_MODE_POWERCFG`,
`POWER_MODE_SCHTASKS` and `POWER_MODE_NVIDIASMI`. With `POWER_MODE_TEST=1` (every test sets it) a call that names no
stand-in stops the script with exit 2 instead of starting the real tool. `gpu-task install` and `remove` refuse to run
while any of these test variables is set.
