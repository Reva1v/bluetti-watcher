# Bug Assessment: brightness restored to the wrong monitor after Windows renumbers displays

- **Slug**: dim-restore-display-renumber
- **Created**: 2026-11-17
- **Source**: pasted text (owner report) + service events and a live DDC/EnumDisplayDevices probe
- **Verdict**: valid
- **Severity**: high

## Report (verbatim or summarized)

> Яркость у первого монитора поставилась на 0 но не вернулась обратно на 100 когда свет включили

Events (`station.db`, local time):

```
20:58:44 dim-on   Мониторы затемнены (включён Сильный Eco): монитор \\.\DISPLAY2#0: яркость 100 → 0; монитор \\.\DISPLAY1#0: яркость 100 → 0
21:44:49 eco-part-failed монитор \\.\DISPLAY2#0 не отвечает (1 из 5): яркость вернётся при следующей попытке
21:44:49 dim-off  ... монитор \\.\DISPLAY1#0: яркость возвращена (100)
21:45:49 dim-off  ... монитор \\.\DISPLAY2#0: яркость возвращена (100)
```

The service believes both monitors were restored; the ASUS stayed at 0.

## Symptom

After Strong Eco dimmed both monitors and mains came back, the primary ASUS VG259Q5A stayed at brightness 0;
expected: both back to 100.

## Reproduction

1. Two DDC/CI monitors, the ASUS on DisplayPort (it drops off the DP link when the display sleeps).
2. Strong Eco on → both dimmed, `meta.dim_restore` = `{\\.\DISPLAY1#0: 100, \\.\DISPLAY2#0: 100}`.
3. Display sleeps; the ASUS leaves the link, Windows renumbers the GDI displays on its return.
4. Strong Eco off → the restore writes to whatever monitor now holds each `\\.\DISPLAYn` name.

Live probe after the incident: `\\.\DISPLAY1` = `AUS258A…UID33029` (ASUS, primary), `\\.\DISPLAY2` = `ACR0673…UID33024`
(Acer). This morning the ASUS was `\\.\DISPLAY2` (memory note, 2026-11-17).

## Suspected Code Paths

- `src/power/brightness.rs:315` — `WindowsBrightness::monitors` builds the id as `{szDevice}#{i}`
  (`\\.\DISPLAYn#0`), a GDI name Windows reassigns when a monitor leaves and rejoins.
- `src/power/brightness.rs:329` — `set` finds the monitor by the same name.
- `src/power/brightness.rs:136-147` — restore step: a present monitor already at the saved value is counted
  as restored without a write (`m.cur == value`).

## Root Cause Hypothesis

At 21:44:49 only the Acer was on the link and it was called `\\.\DISPLAY1`: the restore wrote 100 to the Acer
and dropped the `DISPLAY1` key. A minute later the ASUS returned as `\\.\DISPLAY1` and the Acer became
`\\.\DISPLAY2`; the remaining `DISPLAY2` key matched the Acer (already 100, so no write) and was cleared. The
ASUS was then not in `dim_restore` and was never touched. Confidence: high (renumbering confirmed live, event
sequence matches exactly).

## Proposed Remediation

**Preferred**: key monitors by a stable identity: the monitor's device interface name from
`EnumDisplayDevicesW(szDevice, i, EDD_GET_DEVICE_INTERFACE_NAME)` (`\\?\DISPLAY#AUS258A#5&…&UID33029#{guid}`),
shortened to `AUS258A#5&…&UID33029`. It does not change when GDI names shift. Fall back to the old
`\\.\DISPLAYn#i` only if the call fails. `set` looks the monitor up by the same id. Messages keep a readable
label (`AUS258A (\\.\DISPLAY1)`).

**Alternatives**:
- On restore, write the saved value to every present monitor whose brightness equals the dim value — guesses,
  and breaks when the owner set a low brightness on purpose.

**Files likely to change**:
- `src/power/brightness.rs`

**Tests to add or update**:
- `apply` with a monitor that changes its GDI label between dim and restore but keeps its id: restored.
- Pure helper that shortens the device interface name: normal path, no `#`, empty string, missing GUID tail.

## Risks & Considerations

- A `dim_restore` saved by the old build with `\\.\DISPLAYn#i` keys would miss 5 times and be dropped; it is
  empty on the owner's machine now.
- Moving a monitor to another port changes its UID → handled by the existing `MAX_MISSES` drop.

## Open Questions

- None.
