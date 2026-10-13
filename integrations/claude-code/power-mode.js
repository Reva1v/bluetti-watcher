// Claude Code hook (SessionStart / UserPromptSubmit / SubagentStart) for Bluetti Watcher's power-mode switch: a mode the
// user or the watcher switches (power-mode.ps1 on | off | eco) reaches every live Claude Code session on its next prompt.
// A rules file in CLAUDE.md is read at session start only; this flag is read on every event. README.md beside this file
// shows the settings.json entries.
//
//   flag   env POWER_MODE_FILE, else %ProgramData%\BluettiWatcher\power-mode.json  { "mode": "emergency" | "normal" | "eco",
//          "since": "<ISO 8601 with offset>" [, "until"] [, "source": "station"] [, "battery": { soc, at, level, mains }] };
//          a missing file = normal. An eco with `source: station` has no `until`: it lasts while mains is off. The flag's
//          folder or file being a junction or link, or a folder that cannot be listed, = unreadable. This hook writes no flag.
//   rules  power-mode.rules.md (emergency), power-mode.eco.rules.md, power-mode.stop.rules.md, power-mode.hibernate.rules.md
//          beside this script, read at run time (edit them, no code change); missing = header only.
//   battery  the watcher's station reading, heard while it is younger than 15 minutes: level low -> the emergency rules,
//          critical -> the STOP text, hibernating -> "the PC hibernates at N %: save now"; with mains off the eco and
//          emergency rules end with "Mains is off, battery N %". A session hears each level once (its state file).
//   state  one file per session in TEMP\claude-power-mode\<session>.json (what this session last heard), temp + rename,
//          last writer wins; no lock, no counter shared between sessions. A numeric id counts as its digits, one over 64
//          characters as h<16-hex hash>; SessionStart removes state files older than 14 days (at most 50 a run).
//
// SessionStart (every source): emergency -> header + rules as additionalContext and the header as systemMessage (the user's
//   channel); normal -> nothing; an unreadable flag -> one line naming the file. Records what it said.
// UserPromptSubmit: speaks only when the flag differs from what this session last heard: emergency -> header + rules;
//   emergency -> normal -> "the emergency rules are lifted". No record: normal is recorded silently, emergency speaks.
//   normal -> normal with another `since` (an on and off between two prompts) is recorded silently: nothing to lift here.
//   A missing, empty or non-string session id: silent (no record, so it would repeat on every prompt).
// SubagentStart: emergency -> header + rules; no state.
// An unreadable flag is silent outside SessionStart and never touches the state.
//
// Plain JavaScript for any embedded engine: times in the local zone by the Date getters (no Intl), no child process, stdin
// through fs.readFileSync(0). Fail-open: any error -> nothing on stdout, exit 0. Tests: node --test power-mode.test.js.
'use strict';
const fs = require('fs');
const os = require('os');
const path = require('path');

const HOUR = 3600 * 1000;
const pad = (n) => String(n).padStart(2, '0');
const noBom = (s) => (s.charCodeAt(0) === 0xFEFF ? s.slice(1) : s);


// `since`: YYYY-MM-DDTHH:MM[:SS[.f+]] followed by Z or +HH:MM / -HH:MM, every field in range; the instant in ms, else null.
function parseSince(text) {
  const m = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})(?::(\d{2})(?:\.\d+)?)?(Z|[+-]\d{2}:\d{2})$/.exec(String(text));
  if (!m) return null;
  const [y, mo, d, h, mi, s] = [m[1], m[2], m[3], m[4], m[5], m[6] || '0'].map(Number);
  const t = new Date(Date.UTC(2000, 0, 1));
  t.setUTCFullYear(y, mo - 1, d);
  t.setUTCHours(h, mi, s, 0);
  const ok = t.getUTCFullYear() === y && t.getUTCMonth() === mo - 1 && t.getUTCDate() === d
    && t.getUTCHours() === h && t.getUTCMinutes() === mi && t.getUTCSeconds() === s;
  if (!ok) return null;
  if (m[7] === 'Z') return t.getTime();
  const oh = Number(m[7].slice(1, 3));
  const om = Number(m[7].slice(4, 6));
  if (oh > 14 || om > 59) return null;
  return t.getTime() - (m[7][0] === '-' ? -1 : 1) * (oh * 60 + om) * 60000;
}

// An instant as `dd.MM.yyyy HH:mm` in the PC's time zone (the process's local zone; TZ overrides it).
function localTime(ms) {
  const k = new Date(ms);
  return `${pad(k.getDate())}.${pad(k.getMonth() + 1)}.${k.getFullYear()} ${pad(k.getHours())}:${pad(k.getMinutes())}`;
}

// { file, untrusted }: the configured flag (POWER_MODE_FILE, else the default); a planted junction or link (its folder or
// the file) is untrusted, and so is a file that cannot be told present or absent (stat fails other than ENOENT). The
// ACL and owner checks live in power-mode-flag.ps1 only: node has no ACL API, and a spawn per prompt costs too much.
const flagPath = () => {
  const file = process.env.POWER_MODE_FILE
    ? path.resolve(process.env.POWER_MODE_FILE)
    : path.join(process.env.ProgramData || process.env.ALLUSERSPROFILE || 'C:\\ProgramData', 'BluettiWatcher', 'power-mode.json');
  const dir = path.dirname(file);
  const linked = (parent, name) => {
    try {
      const e = fs.readdirSync(parent, { withFileTypes: true }).find((d) => d.name.toLowerCase() === name.toLowerCase());
      return !!e && e.isSymbolicLink();
    } catch { return true; } // a folder that cannot be listed cannot be told free of links: untrusted, like a link
  };
  if (linked(path.dirname(dir), path.basename(dir))) return { file, untrusted: true };
  let st;
  try { st = fs.statSync(file, { throwIfNoEntry: false }); } catch { return { file, untrusted: true }; }
  if (st && linked(dir, path.basename(file))) return { file, untrusted: true };
  return { file };
};
// The state files have a directory of their own: TEMP itself can hold half a million entries, too many to list at session start.
const stateDir = () => path.join(os.tmpdir(), 'claude-power-mode');
const stateFile = (key) => path.join(stateDir(), `${key}.json`);
const STATE_RE = /^[A-Za-z0-9_-]{1,64}\.json$/;
const MAX_AGE_MS = 14 * 24 * HOUR;
const MAX_SWEEP = 50;
const MAX_STAT = 100;

// Two 32-bit FNV-1a passes with different offsets: 16 hex characters, the same id always the same.
function shortHash(text) {
  let a = 0x811c9dc5;
  let b = 0x01000193 ^ 0x9e3779b9;
  for (let i = 0; i < text.length; i++) {
    const c = text.charCodeAt(i);
    a = Math.imul(a ^ c, 0x01000193);
    b = Math.imul(b ^ c, 0x01000193);
  }
  return (a >>> 0).toString(16).padStart(8, '0') + (b >>> 0).toString(16).padStart(8, '0');
}

// The state key of a session id: a number as its digits, an id over 64 characters as h<hash>, else the id with every
// character outside [A-Za-z0-9_-] as `_`. null (no state) for a missing, empty or non-string id.
function sessionKey(id) {
  const s = typeof id === 'number' && Number.isFinite(id) ? String(id) : id;
  if (typeof s !== 'string' || s === '') return null;
  if (s.length > 64) return `h${shortHash(s)}`;
  return s.replace(/[^A-Za-z0-9_-]/g, '_');
}

// SessionStart only: remove this hook's state files older than 14 days, at most 50 a run; plain files of the exact name
// shape only, never a link; every error ignored. A stat costs ~1 ms in Jint (2,000 of them blew the deadline, the listing
// itself ~20 ms), so at most MAX_STAT files are looked at per run, from a random start, wrapping: all get their turn.
function sweep(now) {
  try {
    const dir = stateDir(); // a missing directory throws here: no state, no sweep
    const names = fs.readdirSync(dir, { withFileTypes: true }).filter((e) => STATE_RE.test(e.name) && e.isFile() && !e.isSymbolicLink()).map((e) => e.name);
    const start = Math.floor(Math.random() * names.length);
    let removed = 0;
    for (let i = 0; i < Math.min(names.length, MAX_STAT) && removed < MAX_SWEEP; i++) {
      try {
        const p = path.join(dir, names[(start + i) % names.length]);
        if (now - fs.statSync(p).mtimeMs > MAX_AGE_MS) { fs.unlinkSync(p); removed++; }
      } catch { /* the next one */ }
    }
  } catch { /* no sweep */ }
}

// { mode, since, ms } - a missing file is normal with no `since`; null = the file is there and cannot be trusted.
function readFlag(file) {
  if (!fs.existsSync(file)) return { mode: 'normal', since: '', ms: null };
  try {
    const j = JSON.parse(noBom(fs.readFileSync(file, 'utf8'))); // null, a string or an array has no such mode
    if (j.mode !== 'emergency' && j.mode !== 'normal' && j.mode !== 'eco') return null;
    const ms = parseSince(j.since);
    if (ms === null) return null;
    const station = j.source === 'station';
    const battery = readBattery(j.battery);
    // eco carries `until`: past it the flag reads as normal since that instant, whoever is or is not running then.
    // An eco the station set has none: it lasts while mains is off.
    if (j.mode === 'eco') {
      if (station && j.until === undefined) return { mode: 'eco', since: String(j.since), ms, untilMs: null, station, battery };
      const untilMs = parseSince(j.until);
      if (untilMs === null) return null;
      if (Date.now() >= untilMs) return { mode: 'normal', since: String(j.until), ms: untilMs, station: false, battery };
      return { mode: 'eco', since: String(j.since), ms, untilMs, station, battery };
    }
    // The user's eco the station's emergency replaced: its end time is still named while on battery.
    const replacedUntilMs = j.mode === 'emergency' && j.replaced && typeof j.replaced === 'object' ? parseSince(j.replaced.until) : null;
    return { mode: j.mode, since: String(j.since), ms, station, battery, replacedUntilMs };
  } catch { return null; }
}

// The station's battery as the poller wrote it: { soc, level, mains, hibernateAt, ms }, null when absent or malformed.
const LEVELS = ['ok', 'low', 'critical', 'hibernating'];
const BATTERY_FRESH_MS = 15 * 60 * 1000;
function readBattery(b) {
  if (!b || typeof b !== 'object' || typeof b.soc !== 'number' || !(b.soc >= 0 && b.soc <= 100) || LEVELS.indexOf(b.level) < 0) return null;
  const ms = parseSince(b.at);
  if (ms === null) return null;
  const hibernateAt = typeof b.hibernateAt === 'number' && b.hibernateAt >= 0 && b.hibernateAt <= 100 ? b.hibernateAt : null;
  return { soc: b.soc, level: b.level, mains: typeof b.mains === 'boolean' ? b.mains : null, hibernateAt, ms };
}
// A reading older than 15 minutes says nothing: the poller stopped, and a stale STOP must not outlive it.
const freshBattery = (flag) => (flag.battery && Math.abs(Date.now() - flag.battery.ms) <= BATTERY_FRESH_MS ? flag.battery : null);
// Mains on: no battery level is heard, whatever the reading says - STOP and hibernate are for a cut only.
const levelOf = (flag) => { const b = freshBattery(flag); return b && b.mains !== true ? b.level : 'ok'; };
// What this session must hear: the battery level outranks the mode (low = the emergency rules, critical = STOP,
// hibernating = save now); with the battery ok it is the mode.
const VOICE = { low: 'emergency', critical: 'stop', hibernating: 'hibernate' };
const voiceOf = (flag) => VOICE[levelOf(flag)] || flag.mode;
const RULE_FILES = { eco: 'power-mode.eco.rules.md', stop: 'power-mode.stop.rules.md', hibernate: 'power-mode.hibernate.rules.md' };

function readRules(mode) {
  const name = RULE_FILES[mode] || 'power-mode.rules.md';
  try { return fs.readFileSync(path.join(__dirname, name), 'utf8').replace(/\r\n/g, '\n').trim(); } catch { return ''; }
}

function readState(session) {
  try {
    const j = JSON.parse(fs.readFileSync(stateFile(session), 'utf8'));
    return j && typeof j === 'object' && typeof j.mode === 'string' ? { mode: j.mode, since: j.since, level: LEVELS.indexOf(j.level) > 0 ? j.level : 'ok' } : null;
  } catch { return null; }
}

function writeState(session, flag) {
  const file = stateFile(session);
  const tmp = `${file}.${process.pid}.${Date.now()}.tmp`;
  try {
    fs.mkdirSync(stateDir(), { recursive: true });
    const level = levelOf(flag);
    fs.writeFileSync(tmp, JSON.stringify(level === 'ok' ? { mode: flag.mode, since: flag.since } : { mode: flag.mode, since: flag.since, level }));
    fs.renameSync(tmp, file);
  } catch { try { fs.unlinkSync(tmp); } catch { /* gone */ } }
}

const pct = (b) => `battery ${Math.round(b.soc * 100) / 100} %`;
// The mains part of a battery text only when the reading says so.
const mainsOff = (b) => (b.mains === false ? ', mains is off' : '');
function emergencyHead(flag) {
  const voice = voiceOf(flag);
  const b = freshBattery(flag);
  if (voice === 'hibernate') return `power mode: ${pct(b)}${mainsOff(b)} - the PC hibernates ${b.hibernateAt === null ? 'soon' : `at ${Math.round(b.hibernateAt * 100) / 100} %`}: save now`;
  if (voice === 'stop') return `power mode: STOP - ${pct(b)}${mainsOff(b)}`;
  if (flag.mode === 'eco' && voice === 'eco') {
    return flag.untilMs === null ? `power mode: ECO since ${localTime(flag.ms)} while mains is off` : `power mode: ECO since ${localTime(flag.ms)} until ${localTime(flag.untilMs)}`;
  }
  if (flag.mode === 'emergency') return `power mode: EMERGENCY since ${localTime(flag.ms)}${flag.replacedUntilMs && flag.replacedUntilMs > Date.now() ? ` (over the user's eco until ${localTime(flag.replacedUntilMs)})` : ''}`;
  return `power mode: EMERGENCY - ${pct(b)}${mainsOff(b)}`; // battery low while the mode itself is not emergency
}
const normalLine = (flag, was) => `power mode: normal${flag.ms === null ? '' : ` since ${localTime(flag.ms)}`} - the ${was === 'eco' ? 'eco' : 'emergency'} rules are lifted`;

function emergencyText(flag) {
  const voice = voiceOf(flag);
  const rules = readRules(voice);
  const b = freshBattery(flag);
  // The eco and emergency rules get the station's reading as one more line; the STOP and hibernate heads carry it.
  const mains = b && b.mains === false && (voice === 'eco' || voice === 'emergency') ? `\n- Mains is off, ${pct(b)}.` : '';
  return rules ? `${emergencyHead(flag)}\n${rules}${mains}` : `${emergencyHead(flag)}${mains}`;
}

function say(event, context, systemMessage) {
  const out = { hookSpecificOutput: { hookEventName: event, additionalContext: context } };
  if (systemMessage) out.systemMessage = systemMessage;
  process.stdout.write(JSON.stringify(out));
}

function main() {
  const payload = JSON.parse(fs.readFileSync(0, 'utf8'));
  const event = payload && payload.hook_event_name;
  if (event !== 'SessionStart' && event !== 'UserPromptSubmit' && event !== 'SubagentStart') return;
  const { file, untrusted } = flagPath();
  const flag = untrusted ? null : readFlag(file);
  if (!flag) {
    if (event === 'SessionStart') {
      const line = `power mode: flag file unreadable (${file}) - treated as normal, tell the user`;
      say(event, line, line);
    }
    return;
  }
  const emergency = voiceOf(flag) !== 'normal'; // emergency, eco, or a battery level: something with rules to say
  if (event === 'SubagentStart') {
    if (emergency) say(event, emergencyText(flag));
    return;
  }
  const key = sessionKey(payload.session_id);
  if (event === 'SessionStart') {
    sweep(Date.now());
    if (key) writeState(key, flag);
    if (emergency) say(event, emergencyText(flag), emergencyHead(flag));
    return;
  }
  if (!key) return; // no record possible: speaking would repeat on every prompt
  const prev = readState(key);
  if (prev && prev.mode === flag.mode && prev.since === flag.since && prev.level === levelOf(flag)) return;
  writeState(key, flag);
  if (emergency) say(event, emergencyText(flag), emergencyHead(flag));
  else if (prev && prev.mode !== 'normal') say(event, normalLine(flag, prev.mode), normalLine(flag, prev.mode));
  else if (prev && prev.level !== 'ok') say(event, normalLine(flag, 'emergency'), normalLine(flag, 'emergency')); // the battery level was the only thing said
}

try { main(); } catch { /* fail-open: nothing */ }
process.exit(0);
