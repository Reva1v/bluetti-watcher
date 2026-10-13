'use strict';
// power-mode.js through its real entry: stdin payload in, stdout out; the flag (POWER_MODE_FILE) and the per-session state
// (TEMP) live in a private temp directory per test.
// Run from the repository root: node --test integrations/claude-code/power-mode.test.js
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { spawnSync } = require('child_process');

const HOOK = path.join(__dirname, 'power-mode.js');
const RULES = fs.readFileSync(path.join(__dirname, 'power-mode.rules.md'), 'utf8').replace(/\r\n/g, '\n').trim();
const SINCE = '2026-10-03T01:20:00+03:00';
const HEAD = 'power mode: EMERGENCY since 03.10.2026 01:20';
const FULL = `${HEAD}\n${RULES}`;
const BOM = String.fromCharCode(0xFEFF);
// The configured flag comes from the environment: a runner's own value never reaches a test that does not set it.
delete process.env.POWER_MODE_FILE;

// A private world: its own TEMP (state files) and its own flag file.
function world(t, hook = HOOK) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'power-mode-test-'));
  t.after(() => fs.rmSync(dir, { recursive: true, force: true }));
  const flag = path.join(dir, 'flag', 'power-mode.json');
  const temp = path.join(dir, 'temp');
  fs.mkdirSync(temp);
  fs.mkdirSync(path.dirname(flag));
  const sdir = path.join(temp, 'claude-power-mode');
  const w = {
    dir, flag, temp, sdir, tz: 'Europe/Kyiv',
    set: (mode, since = SINCE) => fs.writeFileSync(flag, JSON.stringify({ mode, since })),
    raw: (text) => fs.writeFileSync(flag, text),
    clear: () => fs.rmSync(flag, { force: true }),
    state: (sid) => { try { return fs.readFileSync(path.join(sdir, `${sid}.json`), 'utf8'); } catch { return null; } },
    files: () => (fs.existsSync(sdir) ? fs.readdirSync(sdir).sort() : []),
    fire(event, sid = 's1', extra = {}) {
      const payload = { hook_event_name: event, ...(sid === null ? {} : { session_id: sid }), ...extra };
      return w.stdin(JSON.stringify(payload));
    },
    stdin(text) {
      // Times print in the PC's zone: TZ pins it, so the expected text does not depend on the runner's.
      const env = { ...process.env, TEMP: temp, TMP: temp, POWER_MODE_FILE: flag, TZ: w.tz };
      const r = spawnSync(process.execPath, [hook], { input: text, encoding: 'utf8', windowsHide: true, env });
      assert.equal(r.status, 0, r.stderr);
      assert.equal(r.stderr, '');
      return r.stdout;
    },
  };
  return w;
}
const parse = (stdout, event) => {
  assert.notEqual(stdout, '', 'the hook said nothing');
  assert.equal(stdout[0], '{', stdout);
  const out = JSON.parse(stdout);
  assert.equal(out.hookSpecificOutput.hookEventName, event);
  assert.deepEqual(Object.keys(out.hookSpecificOutput), ['hookEventName', 'additionalContext']);
  return { context: out.hookSpecificOutput.additionalContext, system: out.systemMessage, keys: Object.keys(out) };
};

// ---- SessionStart
for (const source of ['startup', 'resume', 'clear', 'compact']) {
  test(`SessionStart (${source}), emergency: header + rules in context, the header for the user, recorded`, (t) => {
    const w = world(t);
    w.set('emergency');
    const out = parse(w.fire('SessionStart', 's1', { source }), 'SessionStart');
    assert.equal(out.context, FULL);
    assert.equal(out.system, HEAD);
    assert.deepEqual(JSON.parse(w.state('s1')), { mode: 'emergency', since: SINCE });
  });
}

test('SessionStart, normal: no output at all, for a missing flag and for a normal one; the mode is recorded', (t) => {
  const w = world(t);
  assert.equal(w.fire('SessionStart'), '');
  assert.deepEqual(JSON.parse(w.state('s1')), { mode: 'normal', since: '' });
  w.set('normal');
  assert.equal(w.fire('SessionStart', 's2'), '');
  assert.deepEqual(JSON.parse(w.state('s2')), { mode: 'normal', since: SINCE });
});

const CORRUPT = [
  ['not JSON', '{not json'],
  ['an empty file', ''],
  ['a JSON null', 'null'],
  ['an array', '[{"mode":"emergency","since":"2026-10-03T01:20:00+03:00"}]'],
  ['an unknown mode', '{"mode":"on","since":"2026-10-03T01:20:00+03:00"}'],
  ['a mode in another case', '{"mode":"EMERGENCY","since":"2026-10-03T01:20:00+03:00"}'],
  ['no since', '{"mode":"emergency"}'],
  ['a since without an offset', '{"mode":"emergency","since":"2026-10-03T01:20:00"}'],
  ['a since on a day that does not exist', '{"mode":"emergency","since":"2026-02-30T01:20:00+02:00"}'],
  ['a since that is not a date', '{"mode":"normal","since":"yesterday"}'],
];

for (const [label, text] of CORRUPT) {
  test(`corrupt flag (${label}): SessionStart says one line naming the file, the other events stay silent, no state`, (t) => {
    const w = world(t);
    w.raw(text);
    const out = parse(w.fire('SessionStart'), 'SessionStart');
    const line = `power mode: flag file unreadable (${w.flag}) - treated as normal, tell the user`;
    assert.equal(out.context, line);
    assert.equal(out.system, line);
    assert.equal(w.fire('UserPromptSubmit'), '');
    assert.equal(w.fire('SubagentStart'), '');
    assert.deepEqual(w.files(), []);
  });
}

// ---- UserPromptSubmit
test('UserPromptSubmit, no record, normal: silent and recorded', (t) => {
  const w = world(t);
  assert.equal(w.fire('UserPromptSubmit'), '');
  assert.deepEqual(JSON.parse(w.state('s1')), { mode: 'normal', since: '' });
});

test('UserPromptSubmit, no record, emergency: announces; the same prompt again is silent', (t) => {
  const w = world(t);
  w.set('emergency');
  const out = parse(w.fire('UserPromptSubmit'), 'UserPromptSubmit');
  assert.equal(out.context, FULL);
  assert.equal(out.system, HEAD);
  assert.deepEqual(out.keys, ['hookSpecificOutput', 'systemMessage']);
  assert.equal(w.fire('UserPromptSubmit'), '');
  assert.equal(w.fire('UserPromptSubmit'), '');
});

test('change normal -> emergency reaches a live session on its next prompt, once', (t) => {
  const w = world(t);
  assert.equal(w.fire('SessionStart'), '');
  assert.equal(w.fire('UserPromptSubmit'), '');
  w.set('emergency');
  assert.equal(parse(w.fire('UserPromptSubmit'), 'UserPromptSubmit').context, FULL);
  assert.equal(w.fire('UserPromptSubmit'), '');
});

test('change emergency -> normal: one line that the rules are lifted, in both channels, once', (t) => {
  const w = world(t);
  w.set('emergency');
  parse(w.fire('SessionStart'), 'SessionStart');
  assert.equal(w.fire('UserPromptSubmit'), '');
  w.set('normal', '2026-10-03T09:05:00+03:00');
  const out = parse(w.fire('UserPromptSubmit'), 'UserPromptSubmit');
  const line = 'power mode: normal since 03.10.2026 09:05 - the emergency rules are lifted';
  assert.equal(out.context, line);
  assert.equal(out.system, line);
  assert.equal(w.fire('UserPromptSubmit'), '');
  assert.deepEqual(JSON.parse(w.state('s1')), { mode: 'normal', since: '2026-10-03T09:05:00+03:00' });
});

test('emergency -> the flag file removed: lifted, with no since to name', (t) => {
  const w = world(t);
  w.set('emergency');
  parse(w.fire('UserPromptSubmit'), 'UserPromptSubmit');
  w.clear();
  assert.equal(parse(w.fire('UserPromptSubmit'), 'UserPromptSubmit').context, 'power mode: normal - the emergency rules are lifted');
  assert.equal(w.fire('UserPromptSubmit'), '');
});

test('emergency switched off and on again between two prompts: the new since is announced', (t) => {
  const w = world(t);
  w.set('emergency');
  parse(w.fire('UserPromptSubmit'), 'UserPromptSubmit');
  w.set('emergency', '2026-10-04T18:00:00+03:00');
  assert.equal(parse(w.fire('UserPromptSubmit'), 'UserPromptSubmit').system, 'power mode: EMERGENCY since 04.10.2026 18:00');
  assert.equal(w.fire('UserPromptSubmit'), '');
});

test('normal -> normal with another since (an on and off this session never heard): silent, recorded', (t) => {
  const w = world(t);
  assert.equal(w.fire('UserPromptSubmit'), '');
  w.set('normal');
  assert.equal(w.fire('UserPromptSubmit'), '');
  assert.deepEqual(JSON.parse(w.state('s1')), { mode: 'normal', since: SINCE });
});

test('a hand edit of the mode alone (the same since) is a change', (t) => {
  const w = world(t);
  w.set('emergency');
  parse(w.fire('UserPromptSubmit'), 'UserPromptSubmit');
  w.set('normal');
  assert.equal(parse(w.fire('UserPromptSubmit'), 'UserPromptSubmit').context, 'power mode: normal since 03.10.2026 01:20 - the emergency rules are lifted');
});

test('a corrupt flag between two prompts leaves the record alone: the same emergency after it stays silent', (t) => {
  const w = world(t);
  w.set('emergency');
  parse(w.fire('UserPromptSubmit'), 'UserPromptSubmit');
  const before = w.state('s1');
  w.raw('{not json');
  assert.equal(w.fire('UserPromptSubmit'), '');
  assert.equal(w.state('s1'), before);
  w.set('emergency');
  assert.equal(w.fire('UserPromptSubmit'), '');
});

test('two sessions keep separate state', (t) => {
  const w = world(t);
  w.set('emergency');
  assert.equal(parse(w.fire('UserPromptSubmit', 'a'), 'UserPromptSubmit').context, FULL);
  assert.equal(parse(w.fire('UserPromptSubmit', 'b'), 'UserPromptSubmit').context, FULL);
  assert.equal(w.fire('UserPromptSubmit', 'a'), '');
  w.set('normal', '2026-10-03T09:05:00+03:00');
  assert.match(parse(w.fire('UserPromptSubmit', 'b'), 'UserPromptSubmit').context, /rules are lifted$/);
  assert.equal(w.fire('UserPromptSubmit', 'b'), '');
  assert.match(parse(w.fire('UserPromptSubmit', 'a'), 'UserPromptSubmit').context, /rules are lifted$/);
  assert.deepEqual(w.files(), ['a.json', 'b.json']);
});

test('an unreadable state file counts as no record: emergency speaks, normal stays silent', (t) => {
  const w = world(t);
  fs.mkdirSync(w.sdir);
  fs.writeFileSync(path.join(w.sdir, 's1.json'), '{not json');
  assert.equal(w.fire('UserPromptSubmit'), '');
  fs.writeFileSync(path.join(w.sdir, 's1.json'), '{not json');
  w.set('emergency');
  assert.equal(parse(w.fire('UserPromptSubmit'), 'UserPromptSubmit').context, FULL);
});

for (const [label, extra] of [
  ['missing', {}],
  ['empty', { session_id: '' }],
  ['an object', { session_id: { id: 's1' } }],
  ['a boolean', { session_id: true }],
  ['null', { session_id: null }],
]) {
  test(`session id ${label}: UserPromptSubmit stays silent, SessionStart and SubagentStart still speak, nothing recorded`, (t) => {
    const w = world(t);
    w.set('emergency');
    const fire = (event) => w.stdin(JSON.stringify({ hook_event_name: event, ...extra }));
    assert.equal(fire('UserPromptSubmit'), '');
    assert.equal(fire('UserPromptSubmit'), '');
    assert.equal(parse(fire('SessionStart'), 'SessionStart').context, FULL);
    assert.equal(parse(fire('SubagentStart'), 'SubagentStart').context, FULL);
    assert.deepEqual(w.files(), []);
  });
}

test('a numeric session id counts as its digits: one record, a repeat is silent', (t) => {
  const w = world(t);
  w.set('emergency');
  const fire = () => w.stdin(JSON.stringify({ hook_event_name: 'UserPromptSubmit', session_id: 12345 }));
  assert.equal(parse(fire(), 'UserPromptSubmit').context, FULL);
  assert.equal(fire(), '');
  assert.equal(w.stdin(JSON.stringify({ hook_event_name: 'UserPromptSubmit', session_id: '12345' })), '');
  assert.deepEqual(w.files(), ['12345.json']);
});

test('a session id over 64 characters maps to a short hash: the same id the same file, a repeat silent', (t) => {
  const w = world(t);
  w.set('emergency');
  const long1 = 'x'.repeat(300);
  const long2 = `${'x'.repeat(299)}y`;
  const id64 = 'a'.repeat(64);
  assert.equal(parse(w.fire('UserPromptSubmit', long1), 'UserPromptSubmit').context, FULL);
  assert.equal(w.fire('UserPromptSubmit', long1), '');
  const files = w.files();
  assert.equal(files.length, 1);
  assert.match(files[0], /^h[0-9a-f]{16}\.json$/);
  parse(w.fire('UserPromptSubmit', long2), 'UserPromptSubmit');
  assert.equal(w.files().length, 2);
  assert.equal(w.fire('UserPromptSubmit', long2), '');
  parse(w.fire('UserPromptSubmit', id64), 'UserPromptSubmit');
  assert.ok(w.files().includes(`${id64}.json`), w.files().join());
});

// ---- the SessionStart sweep of old state files
const DAY = 24 * 3600 * 1000;
function aged(w, name, days, dir = false, root = w.sdir) {
  fs.mkdirSync(root, { recursive: true });
  const p = path.join(root, name);
  if (dir) fs.mkdirSync(p); else fs.writeFileSync(p, '{"mode":"normal","since":""}');
  const at = new Date(Date.now() - days * DAY);
  fs.utimesSync(p, at, at);
  return p;
}

test('SessionStart removes this hook\'s state files older than 14 days and nothing else', (t) => {
  const w = world(t);
  aged(w, 'old.json', 15);
  aged(w, 'young.json', 13);
  aged(w, 'old.json.bak', 30);
  aged(w, '.json', 30);
  aged(w, 'a.b.json', 30);
  aged(w, 'dir.json', 30, true);
  assert.equal(w.fire('UserPromptSubmit', 'u1'), '');
  assert.ok(w.files().includes('old.json'), 'UserPromptSubmit never sweeps');
  assert.equal(w.fire('SubagentStart'), '');
  assert.ok(w.files().includes('old.json'), 'SubagentStart never sweeps');
  assert.equal(w.fire('SessionStart', 's9'), '');
  assert.deepEqual(w.files(), ['.json', 'a.b.json', 'dir.json', 'old.json.bak', 's9.json', 'u1.json', 'young.json']);
});

test('the sweep lists only its own directory, never the TEMP root', (t) => {
  const w = world(t);
  aged(w, 'old.json', 30);
  aged(w, 'old.json', 30, false, w.temp);
  aged(w, 'claude-power-mode-old.json', 30, false, w.temp);
  w.fire('SessionStart', null);
  assert.deepEqual(w.files(), []);
  assert.deepEqual(fs.readdirSync(w.temp).sort(), ['claude-power-mode', 'claude-power-mode-old.json', 'old.json']);
});

test('no state directory: SessionStart sweeps nothing and creates it only to write its record', (t) => {
  const w = world(t);
  w.set('emergency');
  parse(w.fire('SessionStart', null), 'SessionStart');
  assert.deepEqual(fs.readdirSync(w.temp), []);
  parse(w.fire('SessionStart', 's1'), 'SessionStart');
  assert.deepEqual(w.files(), ['s1.json']);
});

test('the sweep removes at most 50 files a run', (t) => {
  const w = world(t);
  for (let i = 0; i < 60; i++) aged(w, `old${i}.json`, 20);
  w.fire('SessionStart', null);
  assert.equal(w.files().length, 10);
  w.fire('SessionStart', null);
  assert.equal(w.files().length, 0);
});

test('the sweep never follows or removes a link', (t) => {
  const w = world(t);
  const target = path.join(w.dir, 'target.json');
  fs.writeFileSync(target, 'keep');
  fs.mkdirSync(w.sdir);
  const link = path.join(w.sdir, 'link.json');
  try { fs.symlinkSync(target, link, 'file'); } catch (e) { t.skip(`no symlink here: ${e.code}`); return; }
  const at = new Date(Date.now() - 30 * DAY);
  fs.lutimesSync(link, at, at);
  fs.utimesSync(target, at, at);
  w.fire('SessionStart', null);
  assert.deepEqual(w.files(), ['link.json']);
  assert.equal(fs.readFileSync(target, 'utf8'), 'keep');
});

test('a session id with path characters cannot leave TEMP', (t) => {
  const w = world(t);
  w.set('emergency');
  parse(w.fire('UserPromptSubmit', '..\\..\\x/y'), 'UserPromptSubmit');
  assert.deepEqual(w.files(), ['______x_y.json']);
});

// ---- SubagentStart
test('SubagentStart: emergency -> header + rules as context only; normal -> nothing; never any state', (t) => {
  const w = world(t);
  assert.equal(w.fire('SubagentStart', 's1', { agent_id: 'a1', agent_type: 'Explore' }), '');
  w.set('normal');
  assert.equal(w.fire('SubagentStart'), '');
  w.set('emergency');
  for (let i = 0; i < 2; i++) {
    const out = parse(w.fire('SubagentStart'), 'SubagentStart');
    assert.equal(out.context, FULL);
    assert.deepEqual(out.keys, ['hookSpecificOutput']);
  }
  assert.deepEqual(w.files(), []);
});

// ---- the rules file, the time, the size
test('a missing rules file leaves the header alone, on every event', (t) => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'power-mode-norules-'));
  t.after(() => fs.rmSync(dir, { recursive: true, force: true }));
  const copy = path.join(dir, 'power-mode.js');
  fs.copyFileSync(HOOK, copy);
  const w = world(t, copy);
  w.set('emergency');
  for (const event of ['SessionStart', 'UserPromptSubmit', 'SubagentStart']) {
    assert.equal(parse(w.fire(event, `s-${event}`), event).context, HEAD);
  }
});

test('the rules file is read at run time, CRLF and a BOM tolerated', (t) => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'power-mode-rules-'));
  t.after(() => fs.rmSync(dir, { recursive: true, force: true }));
  const copy = path.join(dir, 'power-mode.js');
  fs.copyFileSync(HOOK, copy);
  fs.writeFileSync(path.join(dir, 'power-mode.rules.md'), `${BOM}- one\r\n- two\r\n\r\n`);
  const w = world(t, copy);
  w.set('emergency');
  assert.equal(parse(w.fire('SubagentStart'), 'SubagentStart').context, `${HEAD}\n- one\n- two`);
});

for (const [since, want] of [
  ['2026-10-02T22:20:00Z', '03.10.2026 01:20'],          // UTC, summer: +3, the local date a day ahead
  ['2026-10-02T18:20:00-04:00', '03.10.2026 01:20'],     // a negative offset
  ['2026-10-03T03:50:00+05:30', '03.10.2026 01:20'],     // an offset with minutes
  ['2026-12-01T10:00:00+00:00', '01.12.2026 12:00'],     // winter: +2
  ['2026-10-25T00:59:59Z', '25.10.2026 03:59'],          // the last second of summer time
  ['2026-10-25T01:00:00Z', '25.10.2026 03:00'],          // the first of winter time
  ['2026-12-31T22:30Z', '01.01.2027 00:30'],             // no seconds, a year rollover
  ['2026-10-03T01:20:00.123+03:00', '03.10.2026 01:20'], // fractional seconds
]) {
  test(`since ${since} prints ${want} in the pinned zone`, (t) => {
    const w = world(t);
    w.set('emergency', since);
    assert.equal(parse(w.fire('SessionStart'), 'SessionStart').system, `power mode: EMERGENCY since ${want}`);
  });
}

test('the emergency output stays under 700 characters, with the shipped rules', (t) => {
  const w = world(t);
  w.set('emergency');
  const out = parse(w.fire('SessionStart'), 'SessionStart');
  assert.equal(RULES.split('\n').length, 4);
  assert.ok(out.context.length + out.system.length < 700, String(out.context.length + out.system.length));
});

test('a flag file with a BOM reads', (t) => {
  const w = world(t);
  w.raw(`${BOM}{ "mode": "emergency", "since": "${SINCE}" }\n`);
  assert.equal(parse(w.fire('SubagentStart'), 'SubagentStart').context, FULL);
});

test('no temp file is left behind', (t) => {
  const w = world(t);
  w.set('emergency');
  w.fire('SessionStart');
  w.fire('UserPromptSubmit');
  w.set('normal');
  w.fire('UserPromptSubmit');
  assert.deepEqual(w.files(), ['s1.json']);
  assert.deepEqual(fs.readdirSync(w.temp), ['claude-power-mode']);
  assert.deepEqual(fs.readdirSync(w.dir).sort(), ['flag', 'temp']);
  assert.deepEqual(fs.readdirSync(path.dirname(w.flag)), ['power-mode.json']);
});

// ---- fail-open
for (const [label, stdin] of [
  ['empty stdin', ''],
  ['garbage stdin', '{not json'],
  ['a JSON null payload', 'null'],
  ['no event name', JSON.stringify({ session_id: 's1', prompt: 'x' })],
  ['another event', JSON.stringify({ session_id: 's1', hook_event_name: 'PreToolUse' })],
]) {
  test(`fail-open, silent, no state: ${label}`, (t) => {
    const w = world(t);
    w.set('emergency');
    assert.equal(w.stdin(stdin), '');
    assert.deepEqual(w.files(), []);
  });
}

// The local-time getters are allowed: times print in the PC's zone, and an embedded engine's Date uses the host's zone too.
test('Jint-ready: no Intl, no locale formatting, no child process, no process.stdin', () => {
  const src = fs.readFileSync(HOOK, 'utf8').replace(/^\s*\/\/.*$/gm, ''); // code only, not the header's prose
  for (const banned of [/\bIntl\b/, /toLocale/, /child_process/, /process\.stdin/, /getTimezoneOffset/, /Date\.parse/]) {
    assert.doesNotMatch(src, banned);
  }
  assert.deepEqual(src.match(/require\([^)]*\)/g), ["require('fs')", "require('os')", "require('path')"]);
  assert.deepEqual([...new Set(src.match(/\bfs\.\w+/g))].sort(), ['fs.existsSync', 'fs.mkdirSync', 'fs.readFileSync', 'fs.readdirSync', 'fs.renameSync', 'fs.statSync', 'fs.unlinkSync', 'fs.writeFileSync']);
});

// ---- the battery station: an eco with no end time, and the battery levels ----------------------------------------
const ECO_RULES = fs.readFileSync(path.join(__dirname, 'power-mode.eco.rules.md'), 'utf8').replace(/\r\n/g, '\n').trim();
const STOP_RULES = fs.readFileSync(path.join(__dirname, 'power-mode.stop.rules.md'), 'utf8').replace(/\r\n/g, '\n').trim();
const HIBERNATE_RULES = fs.readFileSync(path.join(__dirname, 'power-mode.hibernate.rules.md'), 'utf8').replace(/\r\n/g, '\n').trim();
const isoNow = (agoMs = 0) => new Date(Date.now() - agoMs).toISOString().replace(/\.\d+Z$/, '+00:00');
// A flag as the switch writes it for the poller: mode, the station as its source, the reading.
const stationFlag = (mode, soc, level, { mains = false, agoMs = 0, source = 'station', hibernateAt = 5, extra = {} } = {}) => JSON.stringify({
  mode, since: SINCE, ...(source ? { source } : {}), ...extra,
  battery: { soc, at: isoNow(agoMs), level, ...(mains === null ? {} : { mains }), ...(hibernateAt === null ? {} : { hibernateAt }) },
});
const ECO_HEAD = 'power mode: ECO since 03.10.2026 01:20 while mains is off';

test('an eco from the station: no end time in the header, the eco rules, and one line with the battery', (t) => {
  const w = world(t);
  w.raw(stationFlag('eco', 87, 'ok'));
  const s = parse(w.fire('SessionStart'), 'SessionStart');
  assert.equal(s.context, `${ECO_HEAD}\n${ECO_RULES}\n- Mains is off, battery 87 %.`);
  assert.equal(s.system, ECO_HEAD);
  assert.equal(parse(w.fire('SubagentStart'), 'SubagentStart').context, s.context);
  w.raw(JSON.stringify({ mode: 'eco', since: SINCE, source: 'station' })); // no reading yet
  assert.equal(parse(w.fire('SessionStart', 's2'), 'SessionStart').context, `${ECO_HEAD}\n${ECO_RULES}`);
});

test('battery low: the emergency rules, whatever the mode; critical: the STOP text; hibernating: save now', (t) => {
  const w = world(t);
  w.raw(stationFlag('emergency', 18, 'low'));
  assert.equal(parse(w.fire('SessionStart'), 'SessionStart').context, `${HEAD}\n${RULES}\n- Mains is off, battery 18 %.`);
  w.raw(stationFlag('normal', 18.5, 'low', { source: '' })); // the user switched eco off during the cut
  assert.equal(parse(w.fire('SessionStart'), 'SessionStart').context, `power mode: EMERGENCY - battery 18.5 %, mains is off\n${RULES}\n- Mains is off, battery 18.5 %.`);
  w.raw(stationFlag('emergency', 9, 'critical'));
  const stop = parse(w.fire('SessionStart'), 'SessionStart');
  assert.equal(stop.context, `power mode: STOP - battery 9 %, mains is off\n${STOP_RULES}`);
  assert.equal(stop.system, 'power mode: STOP - battery 9 %, mains is off');
  assert.equal(parse(w.fire('SubagentStart'), 'SubagentStart').context, stop.context);
  w.raw(stationFlag('emergency', 7, 'hibernating'));
  assert.equal(parse(w.fire('SessionStart'), 'SessionStart').context, `power mode: battery 7 %, mains is off - the PC hibernates at 5 %: save now\n${HIBERNATE_RULES}`);
});

test('the battery texts take mains and the hibernate threshold from the reading; mains on silences every level', (t) => {
  const w = world(t);
  w.raw(stationFlag('emergency', 4.5, 'hibernating', { hibernateAt: 4 }));
  assert.equal(parse(w.fire('SessionStart'), 'SessionStart').system, 'power mode: battery 4.5 %, mains is off - the PC hibernates at 4 %: save now');
  w.raw(stationFlag('emergency', 4.5, 'hibernating', { hibernateAt: null, mains: null }));
  assert.equal(parse(w.fire('SessionStart', 's2'), 'SessionStart').system, 'power mode: battery 4.5 % - the PC hibernates soon: save now');
  w.raw(stationFlag('emergency', 9, 'critical', { mains: null }));
  assert.equal(parse(w.fire('SessionStart', 's3'), 'SessionStart').system, 'power mode: STOP - battery 9 %');
  for (const level of ['critical', 'hibernating']) {
    w.raw(stationFlag('emergency', 9, level, { mains: true }));
    const s = parse(w.fire('SessionStart', `s4-${level}`), 'SessionStart');
    assert.equal(s.system, HEAD, level); // the mode alone: no STOP, no hibernate text while mains is on
    assert.doesNotMatch(s.context, /STOP|hibernat|[Mm]ains is off/);
  }
});

test("the station's emergency over the user's eco names that eco's end time", (t) => {
  const w = world(t);
  const flag = (until) => stationFlag('emergency', 19, 'ok', { extra: { replaced: { mode: 'eco', since: SINCE, until } } });
  w.raw(flag(isoNow(-3600000)));
  assert.match(parse(w.fire('SessionStart'), 'SessionStart').system, /^power mode: EMERGENCY since 03\.10\.2026 01:20 \(over the user's eco until \d{2}\.\d{2}\.\d{4} \d{2}:\d{2}\)$/);
  w.raw(flag(isoNow(60000))); // its end time has passed
  assert.equal(parse(w.fire('SessionStart', 's2'), 'SessionStart').system, HEAD);
});
test('UserPromptSubmit speaks once per level change, not per reading; mains back lifts it', (t) => {
  const w = world(t);
  const prompt = () => w.fire('UserPromptSubmit');
  w.raw(stationFlag('eco', 60, 'ok'));
  assert.match(parse(prompt(), 'UserPromptSubmit').context, /^power mode: ECO since .* while mains is off\n/);
  assert.equal(prompt(), '');
  w.raw(stationFlag('eco', 41, 'ok')); // another reading, the same level
  assert.equal(prompt(), '');
  w.raw(stationFlag('eco', 20, 'low'));
  assert.equal(parse(prompt(), 'UserPromptSubmit').context, `power mode: EMERGENCY - battery 20 %, mains is off\n${RULES}\n- Mains is off, battery 20 %.`);
  w.raw(stationFlag('eco', 19, 'low'));
  assert.equal(prompt(), '');
  w.raw(stationFlag('eco', 10, 'critical'));
  assert.match(parse(prompt(), 'UserPromptSubmit').context, /^power mode: STOP - battery 10 %, mains is off\n/);
  assert.equal(prompt(), '');
  w.raw(stationFlag('eco', 7, 'hibernating'));
  assert.match(parse(prompt(), 'UserPromptSubmit').context, /^power mode: battery 7 %, mains is off - the PC hibernates at 5 %: save now\n/);
  assert.equal(prompt(), '');
  w.raw(stationFlag('normal', 30, 'ok', { mains: true, source: '' }));
  assert.match(parse(prompt(), 'UserPromptSubmit').context, /^power mode: normal since .* - the eco rules are lifted$/);
  assert.equal(prompt(), '');
});

test('a battery level heard in normal mode is lifted when the level is ok again', (t) => {
  const w = world(t);
  w.raw(stationFlag('normal', 15, 'low', { source: '' }));
  assert.match(parse(w.fire('UserPromptSubmit'), 'UserPromptSubmit').context, /^power mode: EMERGENCY - battery 15 %, mains is off\n/);
  w.raw(stationFlag('normal', 40, 'ok', { mains: true, source: '' }));
  assert.match(parse(w.fire('UserPromptSubmit'), 'UserPromptSubmit').context, /^power mode: normal since .* - the emergency rules are lifted$/);
  assert.equal(w.fire('UserPromptSubmit'), '');
});

test('a reading older than 15 minutes says nothing: the mode speaks alone', (t) => {
  const w = world(t);
  w.raw(stationFlag('eco', 9, 'critical', { agoMs: 16 * 60000 }));
  assert.equal(parse(w.fire('SessionStart'), 'SessionStart').context, `${ECO_HEAD}\n${ECO_RULES}`);
  w.raw(stationFlag('normal', 9, 'critical', { agoMs: 16 * 60000, source: '' }));
  assert.equal(w.fire('SessionStart', 's2'), '');
});

test('a malformed battery is no battery; an eco with no end time that the station did not set is not a flag', (t) => {
  const w = world(t);
  w.raw(JSON.stringify({ mode: 'emergency', since: SINCE, battery: { soc: 'lots', at: 'x', level: 'critical' } }));
  assert.equal(parse(w.fire('SessionStart'), 'SessionStart').context, FULL);
  w.raw(JSON.stringify({ mode: 'eco', since: SINCE }));
  assert.match(parse(w.fire('SessionStart', 's2'), 'SessionStart').context, /^power mode: flag file unreadable /);
});

// With no POWER_MODE_FILE the flag is %ProgramData%\BluettiWatcher\power-mode.json; the hook writes no flag. A junction
// or link in its place, or a folder that cannot be listed, is unreadable - for a POWER_MODE_FILE path too.
function defaultWorld(t) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'power-mode-test-'));
  t.after(() => fs.rmSync(dir, { recursive: true, force: true }));
  const pd = path.join(dir, 'pd');
  const temp = path.join(dir, 'temp');
  for (const d of [path.join(pd, 'BluettiWatcher'), temp]) fs.mkdirSync(d, { recursive: true });
  const fresh = path.join(pd, 'BluettiWatcher', 'power-mode.json');
  const fire = (sid, flagEnv) => {
    const env = { ...process.env, TEMP: temp, TMP: temp };
    for (const k of Object.keys(env)) if (/^(POWER_MODE_FILE|PROGRAMDATA|ALLUSERSPROFILE)$/i.test(k)) delete env[k];
    env.ProgramData = pd;
    if (flagEnv) env.POWER_MODE_FILE = flagEnv;
    const r = spawnSync(process.execPath, [HOOK], { input: JSON.stringify({ hook_event_name: 'SessionStart', session_id: sid, source: 'startup' }), encoding: 'utf8', windowsHide: true, env });
    assert.equal(r.status, 0, r.stderr);
    return r.stdout;
  };
  return { dir, pd, fresh, fire, set: (file, mode) => fs.writeFileSync(file, JSON.stringify({ mode, since: SINCE })) };
}
const userSid = () => spawnSync('pwsh', ['-NoProfile', '-Command', '[Security.Principal.WindowsIdentity]::GetCurrent().User.Value'], { encoding: 'utf8', windowsHide: true }).stdout.trim();

test('no POWER_MODE_FILE: the %ProgramData%\\BluettiWatcher flag is read, and nothing is written there', (t) => {
  const w = defaultWorld(t);
  assert.equal(w.fire('s0'), '');
  assert.deepEqual(fs.readdirSync(path.dirname(w.fresh)), []);
  w.set(w.fresh, 'emergency');
  assert.equal(parse(w.fire('s1'), 'SessionStart').system, HEAD);
  assert.deepEqual(fs.readdirSync(path.dirname(w.fresh)), ['power-mode.json']);
});

test('a flag that cannot be told present (access denied) is unreadable', (t) => {
  const w = defaultWorld(t);
  w.set(w.fresh, 'emergency');
  const dir = path.dirname(w.fresh);
  const icacls = (args) => spawnSync('icacls', [dir, ...args], { encoding: 'utf8', windowsHide: true });
  assert.equal(icacls(['/deny', `*${userSid()}:(OI)(CI)(RD,RA,REA,X)`]).status, 0);
  let out;
  try { out = w.fire('s1'); } finally { icacls(['/remove:d', `*${userSid()}`]); } // before the world's own cleanup
  assert.match(parse(out, 'SessionStart').system, /^power mode: flag file unreadable \(.*BluettiWatcher.power-mode\.json\)/);
});

test('a junction in place of the flag folder is unreadable, the default one and a POWER_MODE_FILE one alike', (t) => {
  const w = defaultWorld(t);
  const dir = path.dirname(w.fresh);
  const target = `${dir}-target`;
  fs.rmSync(dir, { recursive: true, force: true });
  fs.mkdirSync(target);
  fs.symlinkSync(target, dir, 'junction');
  w.set(w.fresh, 'normal');
  assert.match(parse(w.fire('s1'), 'SessionStart').system, /^power mode: flag file unreadable \(/);
  const other = path.join(w.dir, 'mine');
  fs.mkdirSync(`${other}-target`);
  fs.symlinkSync(`${other}-target`, other, 'junction');
  w.set(path.join(other, 'power-mode.json'), 'normal');
  assert.match(parse(w.fire('s2', path.join(other, 'power-mode.json')), 'SessionStart').system, /^power mode: flag file unreadable \(.*mine.power-mode\.json\)/);
  fs.rmSync(other); // the positive control: the same file in a plain folder reads
  fs.renameSync(`${other}-target`, other);
  assert.equal(w.fire('s3', path.join(other, 'power-mode.json')), '');
});

test('a %ProgramData% that cannot be listed cannot be told free of links: unreadable', (t) => {
  const w = defaultWorld(t);
  w.set(w.fresh, 'normal');
  assert.equal(w.fire('s0'), ''); // the positive control
  const icacls = (args) => spawnSync('icacls', [w.pd, ...args], { encoding: 'utf8', windowsHide: true });
  assert.equal(icacls(['/deny', `*${userSid()}:(RD)`]).status, 0); // list only, on the folder itself: the flag stays reachable
  let out;
  try { out = w.fire('s1'); } finally { icacls(['/remove:d', `*${userSid()}`]); } // before the world's own cleanup
  assert.match(parse(out, 'SessionStart').system, /^power mode: flag file unreadable \(/);
});

test('times print in the process\'s local zone: another TZ, another wall clock, no zone label', (t) => {
  const w = world(t);
  w.tz = 'America/New_York';
  w.set('emergency', '2026-10-02T22:20:00Z');
  assert.equal(parse(w.fire('SessionStart'), 'SessionStart').system, 'power mode: EMERGENCY since 02.10.2026 18:20');
  w.tz = 'Asia/Tokyo';
  assert.equal(parse(w.fire('SessionStart', 's2'), 'SessionStart').system, 'power mode: EMERGENCY since 03.10.2026 07:20');
});
// The switch (power-mode/power-mode.ps1, through pwsh) writes the flag, the hook reads it. powercfg and schtasks name stand-ins
// that do not exist and POWER_MODE_TEST=1 refuses any real tool: the switch's `on` and `off` need none.
const SWITCH = path.join(__dirname, '..', '..', 'power-mode', 'power-mode.ps1');
function switchCli(args, flag) {
  // -Path names the flag, not the configured one: no POWER_MODE_FILE here (the configured flag's folder must be the user's).
  const env = { ...process.env, POWER_MODE_TEST: '1', POWER_MODE_ELEVATED: '1', TZ: 'Europe/Kyiv', POWER_MODE_TZ: 'Europe/Kyiv,FLE Standard Time',
    POWER_MODE_POWERCFG: path.join(os.tmpdir(), 'power-mode-no-such-seam.ps1'), POWER_MODE_SCHTASKS: path.join(os.tmpdir(), 'power-mode-no-such-seam.ps1') };
  const r = spawnSync('pwsh', ['-NoProfile', '-NonInteractive', '-File', SWITCH, ...args, '-Path', flag], { encoding: 'utf8', windowsHide: true, env });
  assert.equal(r.error, undefined, String(r.error));
  return { status: r.status, out: r.stdout.replace(/\r\n/g, '\n').trimEnd() };
}

test('the hook reads what the switch wrote: on announces, off lifts', (t) => {
  const w = world(t);
  const prompt = () => {
    const out = w.fire('UserPromptSubmit');
    return out ? JSON.parse(out).systemMessage : '';
  };
  assert.equal(prompt(), '');
  const on = switchCli(['on'], w.flag);
  assert.equal(on.status, 0, on.out);
  assert.equal(prompt(), on.out);
  assert.equal(prompt(), '');
  assert.equal(switchCli(['off'], w.flag).status, 0);
  assert.match(prompt(), /^power mode: normal since \d{2}\.\d{2}\.\d{4} \d{2}:\d{2} - the emergency rules are lifted$/);
  assert.equal(prompt(), '');
});