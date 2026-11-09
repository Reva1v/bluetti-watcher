# Project rules

## Clean up after every task

`target/` grows to 15+ GB (debug builds especially). When a task is finished (after the last build/test and any commit), always:

1. Make sure nothing is running from `target/` (stop the app if you launched it from there).
2. Run `cargo clean`.
3. Remove other leftovers you created: empty `.claude/worktrees/*`, temp logs, screenshots, scratch files outside the scratchpad.

Do not delete tracked files or anything outside the repo. Skip the cleanup only if the user explicitly says they still need the build.

## Rust: best practices and fast agent work

- Stack rules: `~/.claude/toolbox/notes/best-practices.md`, section «11. Rust» (and «13. Tauri 2» for
  Tauri). This repo's own rules win over it.
- Speed without extra tokens (user 2026-11-09), same section, «Скорость разработки с агентами»: an agent
  checks and tests only its own crates (`cargo check -p` / `cargo test -p`), pure logic lives in light
  crates; the full workspace build, clippy and tests run once after the merge; Rust mutation checks and
  device/end-to-end tests (`#[ignore]`) run once after the merge too. 10–15 tasks per agent, bigger work
  goes to 2–3 agents on disjoint crates; no heavy build of your own while agents build.
- Tests are headless: nothing that opens windows, plays sound or records the screen/mic runs without the
  user's consent, and only when they are away from the machine.
