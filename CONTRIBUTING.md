# Contributing

## Build and run

Requirements: Windows, the .NET SDK named in `global.json`, PowerShell 7, Python 3.12 to 3.14 (regular build), Node.js 22 (for the
power-mode tests).

```powershell
dotnet build bluetti-watcher.slnx
pwsh -NoProfile -File tools/build-venv.ps1 -VenvDir C:\bw-venv                      # keep the path short (winrt)
pwsh -NoProfile -File tools/publish.ps1 -Out C:\bw-out                              # NativeAOT; -Fallback without the MSVC linker
C:\bw-out\BluettiWatcher.exe --dry-run --data-dir C:\bw-data --reader C:\bw-venv\Scripts\bluetti-read.exe --flag $env:TEMP\power-mode-dev.json
```

`--dry-run` logs every power-mode command and hibernation instead of running it. Put a `config.json` with `mac` in the
data directory first ([docs/configuration.md](docs/configuration.md)).

## Tests

```powershell
dotnet test bluetti-watcher.slnx -c Release
node --test power-mode/power-mode.test.js integrations/claude-code/power-mode.test.js
pwsh -NoProfile -File tools/test-swap.ps1
pwsh -NoProfile -File tools/test-wake-sddl.ps1
pwsh -NoProfile -File tools/test-install.ps1
```

The pwsh tests exit 0 when every case passed, 1 when one failed, 2 when they could not run. None of the tests touches
the station, the installed service or a scheduled task. CI runs all of them on every push and pull request.

## Style

- `.editorconfig` holds the formatting and the analyzer settings; the build runs every analyzer and treats warnings as
  errors, so a clean `dotnet build` is the style check.
- Comments state a rule or a reason, not the history of the code.
- The station stays read only: no change may add a write or control path to the station or the cloud.
- Behaviour changes update [docs/design.md](docs/design.md) (and [docs/configuration.md](docs/configuration.md) or
  [docs/api.md](docs/api.md) when a key or a field changes).

## Pull requests

One change per pull request, with tests for the behaviour it adds or fixes, all tests green, and a description of what
changed and how you checked it. Hardware-dependent changes (Bluetooth, hibernation, wake timers): say which station and
Windows version you tried them on.
