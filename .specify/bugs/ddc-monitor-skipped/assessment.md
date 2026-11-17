# Bug Assessment: monitor that misses one DDC/CI reply is never dimmed

- **Slug**: ddc-monitor-skipped (auto-generated)
- **Created**: 2026-11-17
- **Source**: pasted text (owner report + live probe in session)
- **Verdict**: valid
- **Severity**: medium

## Report (summarized)

Strong/Max Eco does not dim the new primary monitor ASUS VG259Q5A (`\\.\DISPLAY2`, DisplayPort). The 2026-11-17 13:32
`dim-on` event dimmed only `\\.\DISPLAY1#0`. The ASUS had re-enumerated at 13:30:19 after a 3-minute display-off (it
drops the DP link on standby, even in Performance Mode). Live probe: a fast DDC/CI sweep on the ASUS failed on VCP 0x10
(brightness); with an 80 ms delay and retries every declared code read fine; 15 slow brightness reads in a row succeeded.

## Symptom

Observed: a monitor whose `GetMonitorBrightness` fails once is silently left out; Eco is reported as applied and the
monitor stays at full brightness until the next level change. Expected: transient DDC/CI failures are retried, and a
monitor that still does not answer is reported as a failed part so the service retries it on backoff.

## Reproduction

1. Two DDC/CI monitors, one of them flaky right after wake (ASUS VG259Q5A on DisplayPort).
2. Let Windows turn the display off, wake it, enable Strong Eco within a few minutes.
3. History shows `dim-on` for one monitor only, no `eco-part-failed`; the other monitor keeps its brightness.

## Suspected Code Paths

- `src/power/brightness.rs` `WindowsBrightness::monitors()` — one `GetMonitorBrightness` call; on failure the monitor is
  not pushed and nothing is reported.
- `src/power/brightness.rs` `WindowsBrightness::set()` — one `SetMonitorBrightness` call, no retry.
- `src/power/brightness.rs` `apply()` — only sees monitors that answered, so returns all-ok parts.
- `src/service.rs` `set_dim()` — `parts.iter().all(ok)` → `dim_applied` set, no backoff retry.

## Root Cause Hypothesis

DDC/CI on the ASUS is timing-sensitive (fails under rapid commands and shortly after the DP link comes back). A single
failed read drops the monitor from the scan, and the scan has no way to tell "no DDC/CI at all" from "did not answer
this time". Confidence: high (event log + live probe).

## Proposed Remediation

**Preferred**:
1. Retry `GetMonitorBrightness` / `SetMonitorBrightness` up to 3 times with ~100 ms pauses.
2. `Brightness` gets `unreadable()` (default empty): physical monitors that still did not return brightness, with their
   primary flag. `apply()` treats an unreadable monitor that should be dimmed as a failed part ("не ответил по DDC/CI,
   n из MAX_MISSES"), counted in a new `DimRestore.unanswered` map, so the service retries on backoff. After
   `MAX_MISSES` consecutive misses the monitor is treated as unsupported (TV, DDC/CI off) and no longer fails. The
   counter resets when the monitor answers or when dimming is lifted. `DimRestore` is kept while the counter is non-empty.

**Files likely to change**: `src/power/brightness.rs` (only).

**Tests to add**: unreadable monitor fails and is dimmed on the next retry; unreadable monitor stops failing after
`MAX_MISSES`; unreadable primary under `Dim::Secondary` is not a failure; `Dim::None` drops the counter; old
`dim_restore` JSON without `unanswered` still parses.

## Risks & Considerations

- Retries add up to ~0.2–0.3 s blocking per non-DDC monitor per scan (scans happen only on dim transitions/backoff).
- A permanently non-DDC monitor produces up to `MAX_MISSES` `eco-part-failed` events per dim episode, then goes quiet.

## Open Questions

- none
