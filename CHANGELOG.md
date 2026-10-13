# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses [Semantic Versioning](https://semver.org/).
The release notes on GitHub are this file's section for the tag.

## [Unreleased]

## [1.0.0] - 2026-10-14

First public release: a Windows service that keeps a PC running through power cuts on a BLUETTI power station and
hibernates it safely before the battery runs out.

### Download

| File | What it is |
|---|---|
| `bluetti-watcher-1.0.0-win-x64.zip` | The service, the power-mode scripts, the installer and the docs |
| `bluetti-watcher-1.0.0-win-x64.zip.sha256` | Its SHA-256; check with `(Get-FileHash bluetti-watcher-1.0.0-win-x64.zip).Hash` |

Install: extract the zip, then `pwsh -NoProfile -File install.ps1 -Mac <station address>` (a dry run) and the same
with `-Apply`. Requirements and details: [README](https://github.com/vitaliy-shatskiy/bluetti-watcher#quick-start).

### Added

- Reads the station once a minute over Bluetooth LE, read only; the library's writer is deleted from the venv and the
  service refuses to start while any piece of it is on disk.
- Power cut detected after 3 minutes without mains: eco (Windows Power saver, optional GPU power cap on NVIDIA cards);
  emergency at 20 %, stop at 10 %, hibernate warning at 7 %, hibernation at 5 %; mains back for 3 minutes lifts every
  mode. All thresholds configurable.
- Guards against bad readings: implausible jumps rejected, two agreeing readings before any level acts, a fresh reading
  right before a hibernation, a longer confirmation after the station recalibrates.
- Dashboard on `127.0.0.1` in English and Ukrainian: battery, time left at the current load, power cuts, history
  chart, events; manual control Auto / Eco / Max.
- The power-mode switch (`power-mode/`) and its flag file, readable by other programs.
- Optional `actions`: your own command when eco, emergency, stop, hibernate warning or normal begins.
- Optional wake timer: a PC hibernated in a cut wakes periodically to check whether mains is back.
- Optional DTEK module (Kyiv region): planned outages for your queue, the expected return, and whether the battery
  lasts until then.
- Optional cloud fallback (unofficial, read only) with `BluettiWatcher.exe --cloud-login`.
- Optional hook for AI coding sessions (`integrations/`) that tells running sessions the power mode.
- Installer with a dry run by default; in-place upgrade with a staged exe and a rollback when the health check fails.

### Known issues

- Only the AC200PL is tested; other BLUETTI models may work through `type`.
- A station that keeps recalibrating for a whole cut never lets the PC hibernate: every recalibration restarts the
  long confirmation. The page shows a `hibernate-delayed` note when this happens.
- The optional wake timer (`wakeEveryMinutes`, off by default): if Bluetooth never answers after a timer wake, the PC
  goes wake, hibernate, wake every interval until the station is empty. Verify that the PC wakes from hibernation on
  the RTC timer first (the README explains how) and leave the option off if you cannot watch the first cycles.
- The cloud fallback uses an unofficial API and may stop working if BLUETTI changes it.
- The release is not code-signed; check the `.sha256` before running it.

Source at this tag: [v1.0.0](https://github.com/vitaliy-shatskiy/bluetti-watcher/tree/v1.0.0)

[Unreleased]: https://github.com/vitaliy-shatskiy/bluetti-watcher/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/vitaliy-shatskiy/bluetti-watcher/releases/tag/v1.0.0
