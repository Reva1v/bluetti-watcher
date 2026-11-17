# Bug Fix: brightness restored to the wrong monitor after Windows renumbers displays

- **Slug**: dim-restore-display-renumber
- **Fixed**: 2026-11-17
- **Assessment**: ./assessment.md
- **Status**: applied

## Summary

Monitors are now keyed by their device interface identity (`AUS258A#5&…&UID33029`) instead of the GDI name
`\\.\DISPLAYn#i`, which Windows reassigns when the ASUS leaves and rejoins the DisplayPort link. The saved
brightness now follows the physical monitor.

## Changes

| File | Change | Notes |
|------|--------|-------|
| `src/power/brightness.rs` | modified | `Monitor.label`; `stable_id`, `identify`; `WindowsBrightness` reads the interface name via `EnumDisplayDevicesW(…, EDD_GET_DEVICE_INTERFACE_NAME)` for both read and write; history text uses `label` |
| `src/power/brightness.rs` | added tests | see below |
| `src/service.rs` | modified | test constructors get `label` |

## Tests Added or Updated

- `stable_id_takes_model_and_instance` — normal name, no GUID tail, empty, display name, missing/empty model or instance.
- `identify_prefers_the_interface_and_falls_back_to_the_display_name` — same id under another display name; fallback to `\\.\DISPLAYn#i`.
- `restore_follows_the_monitor_when_windows_renumbers_displays` — the 2026-11-17 sequence: ASUS missing on restore, returns under another display name, gets its brightness back, the Acer is not touched.

## Local Verification

- `cargo test --lib brightness` → 33 passed.
- `cargo clippy --all-targets` → clean; `cargo test` → 255 + 9 + 40 passed, 4 ignored (device tests).
- Live probe (PowerShell, read-only): `EnumDisplayDevicesW` with flag 1 returns `\\?\DISPLAY#AUS258A#…UID33029#{…}` for
  `\\.\DISPLAY1` and `\\?\DISPLAY#ACR0673#…UID33024#{…}` for `\\.\DISPLAY2`, both active.
- Live dim/restore on the real monitors: not yet.

## Deviations from Assessment

- `src/service.rs` test constructors updated (new `Monitor.label` field).

## Follow-ups

- Verify on the next Strong/Max Eco that spans a display-off.
