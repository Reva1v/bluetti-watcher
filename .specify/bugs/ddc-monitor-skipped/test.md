# Bug Verification: monitor that misses one DDC/CI reply is never dimmed

- **Slug**: ddc-monitor-skipped
- **Verified**: 2026-11-17
- **Fix**: ./fix.md (commit 1b36bfb)
- **Status**: partially verified — automated checks pass, live check pending

## Automated

- `cargo test` → 252 + 9 + 40 passed, 4 ignored; the 9 new brightness tests cover the reported scenario.
- `cargo clippy --workspace --all-targets -- -D warnings`, `cargo fmt --check` → clean.

## Installed

- Release build installed via `bluetti-watcher install` on 2026-11-17; installed exe hash equals the build; service
  answers on http://127.0.0.1:8765/.

## Live check (pending)

Turn Strong Eco on within a few minutes after the ASUS VG259Q5A wakes from display-off. Expected: both
`\\.\DISPLAY1#0` and `\\.\DISPLAY2#0` in the `dim-on` event, or an `eco-part-failed` "не ответил по DDC/CI (1 из 5)"
followed by a later `dim-on` for `\\.\DISPLAY2#0`.
