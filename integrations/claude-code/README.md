# Claude Code hook: power mode

`power-mode.js` tells every running Claude Code session about the power mode Bluetti Watcher (or you, with
[`power-mode.ps1`](../../power-mode/README.md)) switched, on the session's next prompt. A rule in `CLAUDE.md` is read
once at session start; this hook reads the flag file on every event, so a power cut reaches sessions that are already
running.

What a session hears:

| Flag | The session gets |
|---|---|
| normal | nothing; a session that heard a mode gets one line that its rules are lifted |
| eco | `power mode: ECO since … until …` and `power-mode.eco.rules.md`: no local models, no GPU work, no benchmarks; everything else as usual; commit often |
| emergency | `power mode: EMERGENCY since …` and `power-mode.rules.md`: finish the current step, start nothing heavy |
| battery `low` (mains off) | the emergency rules |
| battery `critical` (mains off) | `power mode: STOP` and `power-mode.stop.rules.md`: commit, push what may be pushed, write the state, end the turn |
| battery `hibernating` (mains off) | `the PC hibernates at N %: save now` and `power-mode.hibernate.rules.md` |
| a flag that cannot be read | one line at session start naming the file (treated as normal) |

Each session hears each change once (state per session under `%TEMP%\claude-power-mode\`). A battery reading older
than 15 minutes is ignored. The rules files beside the script are read at run time: edit them to change what your
sessions are told.

## Install

Copy the folder (or point at the repository checkout) and add the hook to `~/.claude/settings.json` (or a project's
`.claude/settings.json`). Node.js must be on PATH; replace the path with yours:

```json
{
  "hooks": {
    "SessionStart": [
      { "hooks": [ { "type": "command", "command": "node \"C:/tools/bluetti-watcher/integrations/claude-code/power-mode.js\"" } ] }
    ],
    "UserPromptSubmit": [
      { "hooks": [ { "type": "command", "command": "node \"C:/tools/bluetti-watcher/integrations/claude-code/power-mode.js\"" } ] }
    ],
    "SubagentStart": [
      { "hooks": [ { "type": "command", "command": "node \"C:/tools/bluetti-watcher/integrations/claude-code/power-mode.js\"" } ] }
    ]
  }
}
```

The hook reads the flag at `%ProgramData%\BluettiWatcher\power-mode.json`. If you moved the flag (the watcher's
`powerMode.flagPath`, the scripts' `POWER_MODE_FILE`), set the same path for Claude Code, for example in the `env`
block of `settings.json`:

```json
{ "env": { "POWER_MODE_FILE": "D:\\power\\power-mode.json" } }
```

The hook never writes the flag. A junction or link in place of the flag's folder or file, or a folder it cannot list,
reads as unreadable. It fails open: any error = no output, exit 0, so a broken hook never blocks a prompt.

## Test

From the repository root, with Node.js 20 or later (the tests that set folder permissions also need `pwsh`):

```powershell
node --test integrations/claude-code/power-mode.test.js
```

Every test runs the hook as Claude Code does (JSON on stdin, JSON on stdout) against a flag and a `%TEMP%` of its own.
To try it by hand:

```powershell
$env:POWER_MODE_FILE = "$env:TEMP\pm-try\power-mode.json"; New-Item -ItemType Directory -Force (Split-Path $env:POWER_MODE_FILE) | Out-Null
'{ "mode": "emergency", "since": "2026-10-13T20:00:00+03:00" }' | Set-Content -NoNewline $env:POWER_MODE_FILE
'{ "hook_event_name": "SessionStart", "session_id": "try" }' | node power-mode.js
```
