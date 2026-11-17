# Bug Fix: monitor that misses one DDC/CI reply is never dimmed

- **Slug**: ddc-monitor-skipped
- **Fixed**: 2026-11-17
- **Assessment**: ./assessment.md
- **Status**: applied

## Summary

DDC/CI brightness reads and writes are retried (3 tries, 100 ms apart). A monitor that is physically present but still
does not return its brightness is now a failed `dim` part, so the service retries it on backoff; after `MAX_MISSES`
consecutive misses it is treated as a monitor without brightness support and goes quiet.

## Changes

| File | Change | Notes |
|------|--------|-------|
| `src/power/brightness.rs` | modified | `retry()`, `DDC_TRIES`, `Brightness::unreadable()` (default empty), `DimRestore.unanswered`, step 4 in `apply()`, retries + unreadable list in `WindowsBrightness` |
| `src/power/brightness.rs` | added tests | 9 new tests |
| `src/main.rs` | modified | `WindowsBrightness::default()` (struct now holds the unreadable list) |

## Tests Added or Updated

- `silent_monitor_fails_and_is_dimmed_on_the_next_attempt` — the reported scenario: miss → failure + counter, next try dims it and resets the counter, restore returns both.
- `silent_monitor_stops_failing_after_max_misses` — TV without DDC/CI: 4 failures, one ok note, then silence; counter capped; nothing written.
- `silent_primary_during_a_game_is_not_a_failure` — `Dim::Secondary` ignores an unreadable primary.
- `lifting_the_dim_drops_the_silent_counter` — `Dim::None` clears counters, also when only counters are stored.
- `silent_counter_survives_an_unreadable_list` — counters kept when the monitor list itself fails.
- `silent_monitor_that_disappears_loses_its_counter`
- `dry_run_reports_a_silent_monitor_but_saves_nothing`
- `old_restore_json_reads_without_the_silent_counter`, `restore_state_round_trips_and_reads_without_misses` (now with `unanswered`)
- `retry_stops_at_first_success_and_pauses_between_tries` — call/pause counts, zero tries.

## Local Verification

- `cargo test --lib brightness` → 30 passed.
- `cargo fmt --check`, `cargo clippy --all-targets` → clean; `cargo test` → 252 + 9 + 40 passed, 4 ignored (device tests).
- Live: not yet — needs a dim episode right after the ASUS wakes (see test.md).

## Deviations from Assessment

- `src/main.rs` changed too: one-line constructor update, because `WindowsBrightness` now carries state.

## Follow-ups

- Verify live on the next Strong/Max Eco after the ASUS wakes from display-off.
