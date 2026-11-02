# Project rules

## Clean up after every task

`target/` grows to 15+ GB (debug builds especially). When a task is finished (after the last build/test and any commit), always:

1. Make sure nothing is running from `target/` (stop the app if you launched it from there).
2. Run `cargo clean`.
3. Remove other leftovers you created: empty `.claude/worktrees/*`, temp logs, screenshots, scratch files outside the scratchpad.

Do not delete tracked files or anything outside the repo. Skip the cleanup only if the user explicitly says they still need the build.
