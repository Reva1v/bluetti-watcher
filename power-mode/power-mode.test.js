'use strict';
// power-mode.ps1 through pwsh: every exit code, the flag file's bytes, the atomic write, the -Path / POWER_MODE_FILE override,
// eco's end time, power plan and end task (powercfg, schtasks and nvidia-smi are stand-in scripts). Every flag lives in a
// private temp directory; the live flag is never touched.
// Run from the repository root: node --test power-mode/power-mode.test.js
const { test } = require('node:test');
const assert = require('node:assert/strict');
const crypto = require('crypto');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { spawn, spawnSync } = require('child_process');
// The long form of TEMP: CI runners hand an 8.3 short path (RUNNER~1) that PowerShell prints long.
const TMP = fs.realpathSync.native(os.tmpdir());

const CLI = path.join(__dirname, 'power-mode.ps1');
// The live names come from the environment: a runner's own values never reach a test that does not set them.
for (const k of ['POWER_MODE_FILE', 'POWER_MODE_MUTEX', 'POWER_MODE_TASK_PREFIX', 'POWER_MODE_EXTRA_READERS']) delete process.env[k];
const END = path.join(__dirname, 'power-mode-end.ps1');
const FLAG_RE = /^\{ "mode": "(emergency|normal)", "since": "(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}[+-]\d{2}:\d{2})"(?:, "by": "manual")? \}\n$/;
// The zone the tests pin (POWER_MODE_TZ): the IANA id for PowerShell 7, the Windows id for Windows PowerShell 5.1.
const PIN_TZ = 'Europe/Kyiv,FLE Standard Time';
const EMERGENCY_RE = /^power mode: EMERGENCY since \d{2}\.\d{2}\.\d{4} \d{2}:\d{2}$/;

function world(t) {
  const dir = fs.mkdtempSync(path.join(TMP, 'power-mode-cli-'));
  t.after(() => fs.rmSync(dir, { recursive: true, force: true }));
  return { dir, flag: path.join(dir, 'power-mode.json') };
}

// args after the script; env.POWER_MODE_FILE is removed unless given, so the live flag is reachable only by a test bug.
function cli(args, envFlag, extraEnv = {}) {
  const env = { ...process.env, ...extraEnv };
  delete env.POWER_MODE_FILE;
  // Times print in the PC's zone; the tests pin it (POWER_MODE_TZ) so the expected text does not depend on the runner's.
  if (!('POWER_MODE_TZ' in extraEnv)) env.POWER_MODE_TZ = PIN_TZ;
  if (envFlag !== undefined) env.POWER_MODE_FILE = envFlag;
  // Elevated unless a test says otherwise: the answer never depends on the shell the suite runs in.
  if (!('POWER_MODE_ELEVATED' in extraEnv)) env.POWER_MODE_ELEVATED = '1';
  env.POWER_MODE_TEST = '1'; // a call that names no stand-in stops the script: the real tools are never started
  // No test reaches the real powercfg or Task Scheduler: without a simulated machine the seams name a script that does not exist.
  // nvidia-smi needs no entry: with a powercfg stand-in named and no POWER_MODE_NVIDIASMI the script never reaches the GPU.
  for (const seam of ['POWER_MODE_POWERCFG', 'POWER_MODE_SCHTASKS']) if (!(seam in extraEnv)) env[seam] = path.join(os.tmpdir(), 'power-mode-no-such-seam.ps1');
  const r = spawnSync('pwsh', ['-NoProfile', '-NonInteractive', '-File', CLI, ...args], { encoding: 'utf8', windowsHide: true, env });
  assert.equal(r.error, undefined, String(r.error));
  return { status: r.status, out: r.stdout.replace(/\r\n/g, '\n').trimEnd(), err: r.stderr };
}
const read = (file) => fs.readFileSync(file);

test('status: a missing flag file is normal, exit 0', (t) => {
  const w = world(t);
  assert.deepEqual(cli(['status', '-Path', w.flag]), { status: 0, out: 'power mode: normal', err: '' });
  assert.deepEqual(fs.readdirSync(w.dir), []);
});

test('on: writes the flag (LF, no BOM, one line), exit 0; status then exits 1 with the local-time line', (t) => {
  const w = world(t);
  const on = cli(['on', '-Path', w.flag]);
  assert.equal(on.status, 0, on.out);
  assert.match(on.out, EMERGENCY_RE);
  const bytes = read(w.flag);
  assert.notEqual(bytes[0], 0xEF);
  const m = FLAG_RE.exec(bytes.toString('utf8'));
  assert.ok(m, bytes.toString('utf8'));
  assert.equal(m[1], 'emergency');
  assert.ok(Math.abs(Date.parse(m[2]) - Date.now()) < 120000, m[2]);
  assert.deepEqual(fs.readdirSync(w.dir), ['power-mode.json']); // the temp file is gone
  const st = cli(['status', '-Path', w.flag]);
  assert.equal(st.status, 1);
  assert.equal(st.out, on.out);
});

test('on while already on keeps since and says so', (t) => {
  const w = world(t);
  fs.writeFileSync(w.flag, '{ "mode": "emergency", "since": "2026-10-02T22:20:00Z" }\n');
  const before = read(w.flag);
  assert.deepEqual(cli(['on', '-Path', w.flag]), { status: 0, out: 'power mode: EMERGENCY since 03.10.2026 01:20 (already on, since kept)', err: '' });
  assert.deepEqual(read(w.flag), before);
});

test('off: back to normal with a new since, exit 0; status exits 0; off again keeps since and says so', (t) => {
  const w = world(t);
  fs.writeFileSync(w.flag, '{ "mode": "emergency", "since": "2026-10-02T22:20:00Z" }\n');
  assert.deepEqual(cli(['off', '-Path', w.flag]), { status: 0, out: 'power mode: normal', err: '' });
  const m = FLAG_RE.exec(read(w.flag).toString('utf8'));
  assert.ok(m, read(w.flag).toString('utf8'));
  assert.equal(m[1], 'normal');
  assert.notEqual(m[2], '2026-10-02T22:20:00Z');
  assert.deepEqual(fs.readdirSync(w.dir), ['power-mode.json']);
  assert.deepEqual(cli(['status', '-Path', w.flag]), { status: 0, out: 'power mode: normal', err: '' });
  const before = read(w.flag);
  assert.deepEqual(cli(['off', '-Path', w.flag]), { status: 0, out: 'power mode: normal (already off, since kept)', err: '' });
  assert.deepEqual(read(w.flag), before);
});

test('off with no flag file is already off and writes nothing', (t) => {
  const w = world(t);
  assert.deepEqual(cli(['off', '-Path', w.flag]), { status: 0, out: 'power mode: normal (already off, since kept)', err: '' });
  assert.deepEqual(fs.readdirSync(w.dir), []);
});

test('status prints the time in the PC\'s zone (pinned by POWER_MODE_TZ) whatever offset the file carries', (t) => {
  const w = world(t);
  for (const [since, want] of [
    ['2026-10-02T22:20:00Z', '03.10.2026 01:20'],
    ['2026-10-02T18:20:00-04:00', '03.10.2026 01:20'],
    ['2026-12-01T10:00:00+00:00', '01.12.2026 12:00'],
    ['2026-12-31T22:30Z', '01.01.2027 00:30'],
  ]) {
    fs.writeFileSync(w.flag, JSON.stringify({ mode: 'emergency', since }));
    assert.deepEqual(cli(['status', '-Path', w.flag]), { status: 1, out: `power mode: EMERGENCY since ${want}`, err: '' });
  }
});

for (const [label, text] of [
  ['not JSON', '{not json'],
  ['an empty file', ''],
  ['an array', '[1]'],
  ['an unknown mode', '{"mode":"on","since":"2026-10-03T01:20:00+03:00"}'],
  ['a mode in another case', '{"mode":"Emergency","since":"2026-10-03T01:20:00+03:00"}'],
  ['no since', '{"mode":"normal"}'],
  ['a since without an offset', '{"mode":"emergency","since":"2026-10-03T01:20:00"}'],
  ['a since on a day that does not exist', '{"mode":"emergency","since":"2026-02-30T01:20:00+02:00"}'],
]) {
  test(`status on a corrupt flag (${label}) exits 2 and never says normal`, (t) => {
    const w = world(t);
    fs.writeFileSync(w.flag, text);
    const r = cli(['status', '-Path', w.flag]);
    assert.equal(r.status, 2, r.out);
    assert.match(r.out, /^power mode: COULD NOT READ /);
    assert.ok(r.out.includes(w.flag), r.out);
    assert.doesNotMatch(r.out, /normal/);
    assert.equal(fs.readFileSync(w.flag, 'utf8'), text);
  });
}

test('on and off replace a corrupt flag file and say so', (t) => {
  const w = world(t);
  fs.writeFileSync(w.flag, '{not json');
  const on = cli(['on', '-Path', w.flag]);
  assert.equal(on.status, 0, on.out);
  assert.match(on.out, /^power mode: EMERGENCY since .* \(replaced an unreadable flag file\)$/);
  assert.equal(cli(['status', '-Path', w.flag]).status, 1);
  fs.writeFileSync(w.flag, '{not json');
  assert.deepEqual(cli(['off', '-Path', w.flag]), { status: 0, out: 'power mode: normal (replaced an unreadable flag file)', err: '' });
  assert.equal(cli(['status', '-Path', w.flag]).status, 0);
});

test('an unknown action, a wrong-case action and no action exit 2 with usage and write nothing', (t) => {
  const w = world(t);
  for (const args of [['toggle', '-Path', w.flag], ['ON', '-Path', w.flag], ['-Path', w.flag]]) {
    const r = cli(args);
    assert.equal(r.status, 2, r.out);
    assert.match(r.out, /^usage: power-mode\.ps1 on\|off\|status/);
  }
  assert.deepEqual(fs.readdirSync(w.dir), []);
});

test('on into a directory that does not exist exits 2 and creates nothing', (t) => {
  const w = world(t);
  const r = cli(['on', '-Path', path.join(w.dir, 'missing', 'power-mode.json')]);
  assert.equal(r.status, 2, r.out);
  assert.match(r.out, /^power mode: COULD NOT RUN: /);
  assert.deepEqual(fs.readdirSync(w.dir), []);
});

test('on over a path that is a directory exits 2 and leaves no temp file', (t) => {
  const w = world(t);
  fs.mkdirSync(w.flag);
  const r = cli(['on', '-Path', w.flag]);
  assert.equal(r.status, 2, r.out);
  assert.deepEqual(fs.readdirSync(w.dir), ['power-mode.json']);
  assert.deepEqual(fs.readdirSync(w.flag), []);
});

test('POWER_MODE_FILE names the configured flag (its folder created with its own ACL); -Path wins over it', (t) => {
  const w = world(t);
  t.after(() => spawnSync('icacls', [w.dir, '/reset', '/T', '/Q'], { windowsHide: true }));
  const configured = path.join(w.dir, 'pd', 'power-mode.json');
  const other = path.join(w.dir, 'other.json');
  assert.equal(cli(['on'], configured).status, 0);
  assert.deepEqual(fs.readdirSync(w.dir), ['pd']);
  assert.deepEqual(fs.readdirSync(path.dirname(configured)), ['power-mode.json']);
  assert.equal(cli(['status'], configured).status, 1);
  assert.equal(cli(['status', '-Path', other], configured).status, 0);
  assert.equal(cli(['on', '-Path', other], path.join(w.dir, 'never', 'never.json')).status, 0);
  assert.deepEqual(fs.readdirSync(w.dir).sort(), ['other.json', 'pd']);
});

// A child pwsh opens `file` for reading with FileShare.Read (a session reading the flag), prints `open`, holds it `ms`.
function holdOpen(file, ms) {
  const script = "$f = [IO.File]::Open($env:HOLD_FILE, 'Open', 'Read', 'Read'); [Console]::Out.WriteLine('open'); [Console]::Out.Flush(); "
    + 'Start-Sleep -Milliseconds ([int]$env:HOLD_MS); $f.Dispose()';
  const child = spawn('pwsh', ['-NoProfile', '-NonInteractive', '-Command', script],
    { windowsHide: true, env: { ...process.env, HOLD_FILE: file, HOLD_MS: String(ms) } });
  const opened = new Promise((resolve, reject) => {
    child.stdout.on('data', (d) => { if (String(d).includes('open')) resolve(); });
    child.on('exit', (code) => reject(new Error(`holder exited ${code} before opening`)));
  });
  const done = new Promise((resolve) => child.on('exit', resolve));
  return { opened, done, kill: () => { try { child.kill(); } catch { /* gone */ } } };
}

test('on while a reader holds the flag open: retried until the reader lets go, exit 0', async (t) => {
  const w = world(t);
  fs.writeFileSync(w.flag, '{ "mode": "normal", "since": "2026-10-02T22:20:00Z" }\n');
  const h = holdOpen(w.flag, 900);
  t.after(h.kill);
  await h.opened;
  const r = cli(['on', '-Path', w.flag]);
  await h.done;
  assert.equal(r.status, 0, r.out);
  assert.match(r.out, EMERGENCY_RE);
  assert.equal(FLAG_RE.exec(fs.readFileSync(w.flag, 'utf8'))[1], 'emergency');
  assert.deepEqual(fs.readdirSync(w.dir), ['power-mode.json']);
});

test('on while a reader never lets go: exit 2 naming the cause, the temp file removed, the flag unchanged', async (t) => {
  const w = world(t);
  const text = '{ "mode": "normal", "since": "2026-10-02T22:20:00Z" }\n';
  fs.writeFileSync(w.flag, text);
  const h = holdOpen(w.flag, 6000);
  t.after(h.kill);
  await h.opened;
  const r = cli(['on', '-Path', w.flag]);
  h.kill();
  await h.done;
  assert.equal(r.status, 2, r.out);
  assert.match(r.out, /^power mode: COULD NOT RUN: the flag file could not be replaced in 10 tries \(held open by another process\?\): /);
  assert.deepEqual(fs.readdirSync(w.dir), ['power-mode.json']);
  assert.equal(fs.readFileSync(w.flag, 'utf8'), text);
});

test('with no POWER_MODE_TZ the time is the PC\'s own zone; POWER_MODE_TZ names another', (t) => {
  const w = world(t);
  const since = '2026-10-02T22:20:00Z';
  fs.writeFileSync(w.flag, JSON.stringify({ mode: 'emergency', since }));
  const d = new Date(since);
  const pad = (n) => String(n).padStart(2, '0');
  const local = `${pad(d.getDate())}.${pad(d.getMonth() + 1)}.${d.getFullYear()} ${pad(d.getHours())}:${pad(d.getMinutes())}`;
  assert.deepEqual(cli(['status', '-Path', w.flag], undefined, { POWER_MODE_TZ: '' }), { status: 1, out: `power mode: EMERGENCY since ${local}`, err: '' });
  assert.deepEqual(cli(['status', '-Path', w.flag], undefined, { POWER_MODE_TZ: 'America/New_York' }), { status: 1, out: 'power mode: EMERGENCY since 02.10.2026 18:20', err: '' });
});

test('a written flag whose local time cannot be formatted: the raw since and a warn line, exit 0; status exits by mode', (t) => {
  const w = world(t);
  const tz = { POWER_MODE_TZ: 'No/Such_Zone' };
  const on = cli(['on', '-Path', w.flag], undefined, tz);
  assert.equal(on.status, 0, on.out);
  const since = FLAG_RE.exec(fs.readFileSync(w.flag, 'utf8'))[2];
  const lines = on.out.split('\n');
  assert.equal(lines[0], `power mode: EMERGENCY since ${since}`);
  assert.match(lines[1], /^warn: local time unavailable \(/);
  assert.equal(lines.length, 2);
  const again = cli(['on', '-Path', w.flag], undefined, tz);
  assert.equal(again.status, 0, again.out);
  assert.equal(again.out.split('\n')[0], `power mode: EMERGENCY since ${since} (already on, since kept)`);
  const st = cli(['status', '-Path', w.flag], undefined, tz);
  assert.equal(st.status, 1, st.out);
  assert.equal(st.out.split('\n')[0], `power mode: EMERGENCY since ${since}`);
  assert.deepEqual(cli(['off', '-Path', w.flag], undefined, tz), { status: 0, out: 'power mode: normal', err: '' });
});

// ---- eco: end time, power plan, end task -------------------------------------------------------------------------
const BALANCED = '381b4222-f694-41f0-9685-ff5bb260df2e';
const SAVER = 'a1841308-3541-4fab-bc81-f71556f20b4a';
const TASK_FILE = 'task-BluettiWatcherEcoEnd.xml';
const CAP_TASK = 'BluettiWatcherEcoGpuCap';
const RESTORE_TASK = 'BluettiWatcherEcoGpuRestore';
const SYS32 = path.join(process.env.SystemRoot || 'C:\\Windows', 'System32');
// The action gpu-task install registers (New-GpuTaskXml), as schtasks /query /xml prints it.
const gpuTaskXml = (w) => `<Task><Principals><Principal id="Author"><RunLevel>HighestAvailable</RunLevel></Principal></Principals><Actions Context="Author"><Exec><Command>${SYS32}\\conhost.exe</Command><Arguments>--headless &quot;${SYS32}\\nvidia-smi.exe&quot; -pl ${w}</Arguments></Exec></Actions></Task>`;
const FAKE_POWERCFG = [
  '$d = $env:FAKE_PM_DIR',
  '[IO.File]::AppendAllText("$d\\calls.log", "powercfg $($args -join \' \')`n")',
  '$plans = @(Get-Content -LiteralPath "$d\\plans.txt")',
  '$active = (Get-Content -LiteralPath "$d\\active.txt" -Raw).Trim()',
  "if ($args[0] -eq '/list') { 'Existing Power Schemes (* Active)'; foreach ($p in $plans) { \"Power Scheme GUID: $p  (a plan)$(if ($p -eq $active) { ' *' })\" }; exit 0 }",
  "if ($args[0] -eq '/setactive' -and $plans -contains $args[1] -and $env:FAKE_PM_SETFAIL -ne '1') { [IO.File]::WriteAllText(\"$d\\active.txt\", $args[1]); exit 0 }",
  "if ($args[0] -eq '/overlaysetactive' -and $env:FAKE_PM_OVERLAYFAIL -ne '1') { [IO.File]::WriteAllText(\"$d\\overlay.txt\", $args[1]); exit 0 }",
  "'Invalid Parameters -- try \"/?\" for help'; exit 1",
].join('\n');
const BEST = 'ded574b5-45a0-4f42-8737-46345c09c238';
const HIGH = '8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c';
const GPU_QUERY = 'nvidia-smi --query-gpu=power.limit,power.min_limit,power.default_limit --format=csv,noheader,nounits';
const LIVE_QUERY = 'nvidia-smi --query-gpu=power.limit --format=csv,noheader,nounits';
// gpu.txt = `limit,min,default` in watts; every call logged beside the powercfg ones.
const FAKE_NVIDIASMI = [
  '$d = $env:FAKE_PM_DIR',
  '[IO.File]::AppendAllText("$d\\calls.log", "nvidia-smi $($args -join \' \')`n")',
  '$g = (Get-Content -LiteralPath "$d\\gpu.txt" -Raw).Trim() -split ","',
  "if ($args[0] -like '--query-gpu=*') { if ($env:FAKE_GPU_QUERYFAIL -eq '1') { 'NVIDIA-SMI has failed because it could not communicate with the NVIDIA driver.'; exit 9 }; $map = @{ 'power.limit' = $g[0]; 'power.min_limit' = $g[1]; 'power.default_limit' = $g[2]; 'power.max_limit' = $g[2] }; (($args[0] -replace '^--query-gpu=', '') -split ',' | ForEach-Object { \"$($map[$_]).00\" }) -join ', '; exit 0 }",
  "if ($args[0] -eq '-pl' -and $env:FAKE_GPU_SETFAIL -ne '1') { [IO.File]::WriteAllText(\"$d\\gpu.txt\", \"$($args[1]),$($g[1]),$($g[2])\"); \"Power limit for GPU 00000000:01:00.0 was set to $($args[1]).00 W\"; exit 0 }",
  "'Changing power management limit is not permitted: Insufficient Permissions'; exit 4",
].join('\n');
const FAKE_SCHTASKS = [
  '$d = $env:FAKE_PM_DIR',
  '[IO.File]::AppendAllText("$d\\calls.log", "schtasks $($args -join \' \')`n")',
  // FAKE_PM_MUTEX = a lock name: each call logs whether it is held, seen from another process (this stand-in runs in the caller's).
  "if ($env:FAKE_PM_MUTEX) { $s = & \"$env:SystemRoot\\System32\\WindowsPowerShell\\v1.0\\powershell.exe\" -NoProfile -NonInteractive -Command '$mx = New-Object Threading.Mutex($false, $env:FAKE_PM_MUTEX); try { if ($mx.WaitOne(0)) { $mx.ReleaseMutex(); ''free'' } else { ''held'' } } catch [Threading.AbandonedMutexException] { ''abandoned'' }'; [IO.File]::AppendAllText(\"$d\\calls.log\", \"mutex $($env:FAKE_PM_MUTEX) $s`n\") }",
  "$file =\"$d\\task-$($args[[array]::IndexOf($args, '/tn') + 1]).xml\"",
  "if ($args[0] -eq '/create') { if ($env:FAKE_PM_DENY -eq '1') { 'ERROR: Access is denied.'; exit 1 }; [IO.File]::Copy($args[[array]::IndexOf($args, '/xml') + 1], $file, $true); 'SUCCESS: created'; exit 0 }",
  "if ($args[0] -eq '/delete' -and [IO.File]::Exists($file)) { [IO.File]::Delete($file); exit 0 }",
  "if ($args[0] -eq '/query' -and [IO.File]::Exists($file)) { if ($args -contains '/xml') { [IO.File]::ReadAllText($file) }; exit 0 }",
  // /run of a registered GPU task: what the elevated task would do, nvidia-smi -pl with the wattage fixed in its action
  // (FAKE_PM_HELPER_IDLE = the task starts and does nothing; FAKE_PM_RUNFAIL = it does not start).
  "if ($args[0] -eq '/run' -and [IO.File]::Exists($file) -and $env:FAKE_PM_RUNFAIL -ne '1') { $x = [IO.File]::ReadAllText($file); if ($env:FAKE_PM_HELPER_IDLE -ne '1' -and $x -match '-pl (\\d+)') { $g = (Get-Content -LiteralPath \"$d\\gpu.txt\" -Raw).Trim() -split ','; [IO.File]::WriteAllText(\"$d\\gpu.txt\", \"$($Matches[1]),$($g[1]),$($g[2])\") }; 'SUCCESS: Attempted to run the scheduled task.'; exit 0 }",
  "'ERROR: The system cannot find the file specified.'; exit 1",
].join('\n');

// A flag directory plus a simulated machine: the power plans, the active one, the Task Scheduler, every call logged.
// gpu: [limit, min, default] in watts = a simulated nvidia-smi; config: the object written beside the flag as
// power-mode.config.json - returnPlan balanced unless given (the tool's own default is High performance), null = no file.
function machine(t, { plans = [BALANCED, SAVER], active = BALANCED, gpu = null, config = { returnPlan: 'balanced' } } = {}) {
  const w = world(t);
  const sim = fs.mkdtempSync(path.join(TMP, 'power-mode-sim-'));
  t.after(() => fs.rmSync(sim, { recursive: true, force: true }));
  fs.writeFileSync(path.join(sim, 'plans.txt'), plans.join('\n') + '\n');
  fs.writeFileSync(path.join(sim, 'active.txt'), active);
  fs.writeFileSync(path.join(sim, 'calls.log'), '');
  fs.writeFileSync(path.join(sim, 'fake-powercfg.ps1'), FAKE_POWERCFG);
  fs.writeFileSync(path.join(sim, 'fake-schtasks.ps1'), FAKE_SCHTASKS);
  const env = { FAKE_PM_DIR: sim, POWER_MODE_POWERCFG: path.join(sim, 'fake-powercfg.ps1'), POWER_MODE_SCHTASKS: path.join(sim, 'fake-schtasks.ps1') };
  if (gpu) {
    fs.writeFileSync(path.join(sim, 'gpu.txt'), gpu.join(','));
    fs.writeFileSync(path.join(sim, 'fake-nvidia-smi.ps1'), FAKE_NVIDIASMI);
    env.POWER_MODE_NVIDIASMI = path.join(sim, 'fake-nvidia-smi.ps1');
  }
  if (config) fs.writeFileSync(path.join(w.dir, 'power-mode.config.json'), typeof config === 'string' ? config : JSON.stringify(config));
  const taskFile = path.join(sim, TASK_FILE);
  return {
    ...w, sim, taskFile, env,
    files: () => fs.readdirSync(w.dir).filter((f) => f !== 'power-mode.config.json'),
    registerGpu: (cap, restore) => { fs.writeFileSync(path.join(sim, `task-${CAP_TASK}.xml`), gpuTaskXml(cap)); fs.writeFileSync(path.join(sim, `task-${RESTORE_TASK}.xml`), gpuTaskXml(restore)); },
    gpu: () => Number(fs.readFileSync(path.join(sim, 'gpu.txt'), 'utf8').split(',')[0]),
    gpuCalls: () => fs.readFileSync(path.join(sim, 'calls.log'), 'utf8').split('\n').filter((c) => c.startsWith('nvidia-smi')),
    run: (args, more = {}) => cli([...args, '-Path', w.flag], undefined, { ...env, ...more }),
    active: () => fs.readFileSync(path.join(sim, 'active.txt'), 'utf8').trim(),
    overlay: () => (fs.existsSync(path.join(sim, 'overlay.txt')) ? fs.readFileSync(path.join(sim, 'overlay.txt'), 'utf8').trim() : null),
    calls: () => fs.readFileSync(path.join(sim, 'calls.log'), 'utf8').split('\n').filter(Boolean),
    task: () => (fs.existsSync(taskFile) ? fs.readFileSync(taskFile, 'utf16le').replace(/^﻿/, '') : null),
    json: () => JSON.parse(fs.readFileSync(w.flag, 'utf8')),
  };
}
const iso = (ms) => new Date(ms).toISOString().replace(/\.\d+Z$/, '+00:00');
const ecoFlag = (untilMs, prevPlan) => `{ "mode": "eco", "since": "${iso(untilMs - 3600000)}", "until": "${iso(untilMs)}"${prevPlan ? `, "prevPlan": "${prevPlan}"` : ''} }\n`;
const minutesAhead = (j) => (Date.parse(j.until) - Date.now()) / 60000;

test('eco with no end time lasts 4 hours from now; status exits 1 and names the end', (t) => {
  const m = machine(t);
  const r = m.run(['eco']);
  assert.equal(r.status, 0, r.out);
  const j = m.json();
  assert.equal(j.mode, 'eco');
  assert.ok(Math.abs(minutesAhead(j) - 240) < 2, j.until);
  assert.match(r.out.split('\n')[0], /^power mode: ECO since \d{2}\.\d{2}\.\d{4} \d{2}:\d{2} until \d{2}\.\d{2}\.\d{4} \d{2}:\d{2} \(/);
  assert.equal(m.run(['status']).status, 1);
});

test('eco -For <minutes> is the explicit length; -Until HH:mm stays the next local wall clock', (t) => {
  const m = machine(t);
  assert.equal(m.run(['eco', '-For', '30']).status, 0);
  assert.ok(Math.abs(minutesAhead(m.json()) - 30) < 2, m.json().until);
  assert.equal(m.run(['eco', '-Until', '24:00']).status, 0);
  const until = new Date(m.json().until);
  // POWER_MODE_TZ pins the PC's zone to Europe/Kyiv (cli()).
  const local = new Intl.DateTimeFormat('en-GB', { timeZone: 'Europe/Kyiv', hour: '2-digit', minute: '2-digit', hourCycle: 'h23' }).format(until);
  assert.equal(local, '00:00');
  assert.ok(minutesAhead(m.json()) > 0 && minutesAhead(m.json()) <= 1440, m.json().until);
});

test('eco: -Until with -For, and a -For that is not minutes 1-1440, exit 2 and write nothing', (t) => {
  const m = machine(t);
  for (const args of [['eco', '-Until', '23:00', '-For', '30'], ['eco', '-For', '0'], ['eco', '-For', '1441'], ['eco', '-For', 'soon'], ['eco', '-Until', '25:00']]) {
    const r = m.run(args);
    assert.equal(r.status, 2, `${args.join(' ')}: ${r.out}`);
    assert.match(r.out, /^power mode: COULD NOT RUN: /);
  }
  assert.deepEqual(m.files(), []);
  assert.deepEqual(m.calls(), []);
});

test('eco records the active plan, turns Power saver on and registers the end task; off goes to maximum performance and removes the task', (t) => {
  const m = machine(t);
  const r = m.run(['eco']);
  assert.equal(r.status, 0, r.out);
  const j = m.json();
  assert.equal(j.prevPlan, BALANCED);
  assert.equal(m.active(), SAVER);
  assert.deepEqual(r.out.split('\n').slice(1), [`plan: Power saver on; returns to ${BALANCED}`, 'end task: BluettiWatcherEcoEnd registered']);
  const xml = m.task();
  assert.ok(xml, 'no task registered');
  assert.ok(xml.includes(`<StartBoundary>${j.until}</StartBoundary>`), xml);
  assert.ok(xml.includes('<StartWhenAvailable>true</StartWhenAvailable>'), xml);
  assert.ok(xml.includes('<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>'), xml);
  assert.ok(xml.includes(`<Command>${path.join(process.env.SystemRoot, 'System32', 'conhost.exe')}</Command>`), xml);
  assert.ok(xml.includes(`-ExecutionPolicy Bypass -File &quot;${END}&quot; -Path &quot;${m.flag}&quot; -FlagFile &quot;${path.join(process.env.ProgramData, 'BluettiWatcher', 'power-mode.json')}&quot; -Mutex Local\\BluettiWatcherPowerMode -TaskPrefix BluettiWatcherEco -TaskUntil ${j.until}</Arguments>`), xml);
  const st = m.run(['status']);
  assert.equal(st.status, 1);
  assert.deepEqual(st.out.split('\n').slice(1), [`plan: returns to Balanced (${BALANCED})`, 'end task: BluettiWatcherEcoEnd present']);
  const off = m.run(['off']);
  assert.deepEqual(off, { status: 0, out: `power mode: normal\nplan: Balanced, Best performance`, err: '' });
  assert.equal(m.active(), BALANCED);
  assert.equal(m.overlay(), BEST);
  assert.equal(m.task(), null);
  assert.ok(FLAG_RE.test(fs.readFileSync(m.flag, 'utf8')), fs.readFileSync(m.flag, 'utf8')); // prevPlan is gone
  assert.deepEqual(m.run(['status']), { status: 0, out: 'power mode: normal', err: '' });
});

test('end at the end time: the plan comes back, the flag is normal since `until`, the task removes itself', (t) => {
  for (const untilMs of [Date.now() - 60000, Date.now() + 600000]) { // passed · fired early: equals the task's until
    const m = machine(t, { active: SAVER });
    fs.writeFileSync(m.flag, ecoFlag(untilMs, BALANCED));
    fs.writeFileSync(m.taskFile, 'registered');
    const r = m.run(['end', '-TaskUntil', iso(untilMs)]);
    assert.deepEqual(r, { status: 0, out: `power mode: normal (eco ended)\nplan: Balanced, Best performance`, err: '' });
    assert.equal(m.active(), BALANCED);
    assert.equal(m.overlay(), BEST);
    assert.equal(fs.readFileSync(m.flag, 'utf8'), `{ "mode": "normal", "since": "${iso(untilMs)}" }\n`);
    assert.equal(m.task(), null);
  }
});

test('a second eco while eco is on replaces the task and keeps the first plan to return to', (t) => {
  const m = machine(t);
  assert.equal(m.run(['eco', '-For', '30']).status, 0);
  const first = m.json();
  const r = m.run(['eco', '-For', '90']);
  assert.equal(r.status, 0, r.out);
  const second = m.json();
  assert.equal(second.prevPlan, BALANCED); // never Power saver, the plan active at the second call
  assert.notEqual(second.until, first.until);
  assert.ok(m.task().includes(`-TaskUntil ${second.until}</Arguments>`), m.task());
  assert.equal(m.active(), SAVER);
  assert.ok(r.out.includes(`plan: Power saver stays on; returns to ${BALANCED}`), r.out);
  assert.equal(m.run(['off']).status, 0);
  assert.equal(m.active(), BALANCED);
});

test('eco with Power saver already active and nothing recorded: the plan off returns to is recorded, never Power saver', (t) => {
  const m = machine(t, { active: SAVER });
  const r = m.run(['eco']);
  assert.equal(r.status, 0, r.out);
  assert.equal(m.json().prevPlan, BALANCED); // returnPlan balanced
  assert.ok(m.task(), 'no end task');
  assert.equal(m.active(), SAVER);
  assert.deepEqual(m.calls().filter((c) => c.startsWith('powercfg')), ['powercfg /list']);
  assert.deepEqual(m.run(['status']).out.split('\n').slice(1), [`plan: returns to Balanced (${BALANCED})`, 'end task: BluettiWatcherEcoEnd present']);
  assert.equal(m.run(['off']).status, 0);
  assert.deepEqual({ active: m.active(), overlay: m.overlay() }, { active: BALANCED, overlay: BEST });
});

test("an older task's end does not cut a newer eco short", (t) => {
  const m = machine(t, { active: SAVER });
  const text = ecoFlag(Date.now() + 3600000, BALANCED);
  fs.writeFileSync(m.flag, text);
  fs.writeFileSync(m.taskFile, 'the newer task');
  for (const args of [['end', '-TaskUntil', iso(Date.now() - 60000)], ['end']]) {
    const r = m.run(args);
    assert.equal(r.status, 0, r.out);
    assert.match(r.out, /^power mode: ECO since .* \(end: not due, eco runs on\)$/);
    assert.equal(fs.readFileSync(m.flag, 'utf8'), text);
    assert.equal(m.active(), SAVER);
    assert.equal(fs.readFileSync(m.taskFile, 'utf8'), 'the newer task');
  }
  assert.deepEqual(m.calls(), []);
});

test('end on a flag that is not eco changes nothing; called by a stale task, that task goes', (t) => {
  const m = machine(t, { active: SAVER });
  const text = `{ "mode": "emergency", "since": "2026-10-02T22:20:00Z", "prevPlan": "${BALANCED}" }\n`;
  fs.writeFileSync(m.flag, text);
  fs.writeFileSync(m.taskFile, 'stale');
  const r = m.run(['end', '-TaskUntil', iso(Date.now() - 60000)]);
  assert.equal(r.status, 0, r.out);
  assert.match(r.out, /^power mode: EMERGENCY since .* \(end: no eco to end\)$/);
  assert.equal(fs.readFileSync(m.flag, 'utf8'), text);
  assert.equal(m.active(), SAVER);
  assert.equal(m.task(), null);
});

test('the Task Scheduler refuses: the task cannot be registered, eco is still set, exit 0, a warn line names what will not come back', (t) => {
  const m = machine(t);
  const r = m.run(['eco'], { FAKE_PM_DENY: '1' });
  assert.equal(r.status, 0, r.out);
  assert.equal(m.json().mode, 'eco');
  assert.equal(m.json().prevPlan, BALANCED);
  assert.equal(m.active(), SAVER);
  assert.equal(m.task(), null);
  assert.equal(r.out.split('\n')[2], `warn: the end task BluettiWatcherEcoEnd could not be registered (ERROR: Access is denied.): the power plan ${BALANCED} will not come back at the end time, only on power-mode.ps1 off`);
  assert.deepEqual(m.run(['status']).out.split('\n').slice(1), [`plan: returns to Balanced (${BALANCED})`, 'end task: BluettiWatcherEcoEnd absent']);
  assert.equal(m.run(['off']).status, 0);
  assert.equal(m.active(), BALANCED);
});

test('no Power saver plan on the machine: said so, the plan is left alone, eco is set, exit 0', (t) => {
  const m = machine(t, { plans: [BALANCED] });
  const r = m.run(['eco']);
  assert.equal(r.status, 0, r.out);
  assert.equal(m.json().mode, 'eco');
  assert.equal('prevPlan' in m.json(), false);
  assert.equal(r.out.split('\n')[1], `warn: the Power saver plan (${SAVER}) is not on this machine: the power plan is unchanged`);
  assert.equal(m.active(), BALANCED);
  assert.equal(m.task(), null);
  assert.deepEqual(m.calls(), ['powercfg /list']);
});

test('on over eco keeps Power saver and the plan to return to, removes the end task; off brings the plan back', (t) => {
  const m = machine(t);
  assert.equal(m.run(['eco']).status, 0);
  const on = m.run(['on']);
  assert.equal(on.status, 0, on.out);
  assert.equal(on.out.split('\n')[1], `plan: Power saver stays on; returns to ${BALANCED} on off`);
  assert.deepEqual({ mode: m.json().mode, prevPlan: m.json().prevPlan, until: m.json().until }, { mode: 'emergency', prevPlan: BALANCED, until: undefined });
  assert.equal(m.active(), SAVER);
  assert.equal(m.task(), null);
  assert.equal(m.run(['eco']).status, 2); // emergency is stricter
  assert.equal(m.run(['off']).status, 0);
  assert.equal(m.active(), BALANCED);
  assert.ok(FLAG_RE.test(fs.readFileSync(m.flag, 'utf8')));
});

test('an eco that ended while the PC was off: status is normal and shows the plan still owed; off brings it back and keeps since', (t) => {
  const m = machine(t, { active: SAVER });
  const untilMs = Date.now() - 600000;
  fs.writeFileSync(m.flag, ecoFlag(untilMs, BALANCED));
  const st = m.run(['status']);
  assert.equal(st.status, 0, st.out);
  assert.deepEqual(st.out.split('\n').slice(1), [`plan: returns to Balanced (${BALANCED})`, 'end task: BluettiWatcherEcoEnd absent']);
  assert.equal(m.run(['off']).status, 0);
  assert.equal(m.active(), BALANCED);
  assert.equal(fs.readFileSync(m.flag, 'utf8'), `{ "mode": "normal", "since": "${iso(untilMs)}", "by": "manual" }\n`);
});

test('a plan that cannot be set stays in the flag for the next off', (t) => {
  const m = machine(t, { active: SAVER });
  fs.writeFileSync(m.flag, ecoFlag(Date.now() + 3600000, BALANCED));
  const r = m.run(['off'], { FAKE_PM_SETFAIL: '1' });
  assert.equal(r.status, 0, r.out);
  assert.match(r.out.split('\n')[1], /^warn: the Balanced plan could not be set \(/);
  assert.deepEqual({ mode: m.json().mode, prevPlan: m.json().prevPlan }, { mode: 'normal', prevPlan: BALANCED });
  const o = m.run(['off'], { FAKE_PM_OVERLAYFAIL: '1' });
  assert.match(o.out.split('\n')[1], /^warn: the Best performance power mode could not be set \(/);
  assert.equal(m.json().prevPlan, BALANCED);
  assert.equal(m.run(['off']).status, 0);
  assert.equal(m.active(), BALANCED);
  assert.equal(m.overlay(), BEST);
  assert.equal('prevPlan' in m.json(), false);
});

test('returnPlan previous: off brings back the plan that was active before eco, by name; only Balanced gets the Best performance overlay', (t) => {
  const m = machine(t, { plans: [BALANCED, SAVER, HIGH], active: HIGH, config: { returnPlan: 'previous' } });
  assert.equal(m.run(['eco']).status, 0);
  assert.deepEqual({ prevPlan: m.json().prevPlan, active: m.active() }, { prevPlan: HIGH, active: SAVER });
  assert.equal(m.run(['status']).out.split('\n')[1], `plan: returns to High performance (${HIGH})`);
  assert.deepEqual(m.run(['off']), { status: 0, out: 'power mode: normal\nplan: High performance', err: '' });
  assert.deepEqual({ active: m.active(), overlay: m.overlay() }, { active: HIGH, overlay: null });
});

test('on, off and status on flags without a plan never call powercfg or the Task Scheduler', (t) => {
  const m = machine(t);
  for (const action of ['status', 'off', 'on', 'on', 'status', 'off', 'off', 'end']) assert.notEqual(m.run([action]).status, 2, action);
  assert.deepEqual(m.calls(), []);
});

// ---- the end script under Windows PowerShell 5.1: what the end task runs -----------------------------------------
const PS51 = path.join(process.env.SystemRoot || 'C:\\Windows', 'System32', 'WindowsPowerShell', 'v1.0', 'powershell.exe');
// argv = null: `-Path <m.flag>` then args; else exactly argv after the script (a task's command line).
function end51(m, args, more = {}, argv = null) {
  const env = { ...process.env, FAKE_PM_DIR: m.sim, POWER_MODE_POWERCFG: path.join(m.sim, 'fake-powercfg.ps1'), POWER_MODE_SCHTASKS: path.join(m.sim, 'fake-schtasks.ps1'), ...more };
  delete env.POWER_MODE_FILE;
  // As the Task Scheduler starts it: no PowerShell 7 module path, which keeps 5.1 from loading Get-Acl's module.
  delete env.PSModulePath;
  if (!('POWER_MODE_TZ' in more)) env.POWER_MODE_TZ = PIN_TZ;
  if (!('POWER_MODE_NVIDIASMI' in more)) delete env.POWER_MODE_NVIDIASMI;
  if (!('POWER_MODE_ELEVATED' in more)) env.POWER_MODE_ELEVATED = '1';
  env.POWER_MODE_TEST = '1';
  const r = spawnSync(PS51, ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', END, ...(argv || ['-Path', m.flag, ...args])], { encoding: 'utf8', windowsHide: true, env });
  assert.equal(r.error, undefined, String(r.error));
  return { status: r.status, out: r.stdout.replace(/\r\n/g, '\n').trimEnd(), err: r.stderr };
}

test('power-mode-end.ps1 under Windows PowerShell 5.1: a due eco gets its plan back, the flag is normal, the task is gone', (t) => {
  const m = machine(t, { active: SAVER });
  const untilMs = Date.now() - 60000;
  fs.writeFileSync(m.flag, ecoFlag(untilMs, BALANCED).replace(' }\n', ', "battery": { "soc": 61, "at": "2026-10-06T21:05:06+03:00", "level": "ok", "mains": false } }\n'));
  fs.writeFileSync(m.taskFile, 'registered');
  const r = end51(m, ['-TaskUntil', iso(untilMs)]);
  assert.deepEqual(r, { status: 0, out: `power mode: normal (eco ended)\nplan: Balanced, Best performance`, err: '' });
  assert.equal(m.active(), BALANCED);
  assert.equal(m.overlay(), BEST);
  assert.equal(fs.readFileSync(m.flag, 'utf8'), `{ "mode": "normal", "since": "${iso(untilMs)}", "battery": { "soc": 61, "at": "2026-10-06T21:05:06+03:00", "level": "ok", "mains": false } }\n`);
  assert.equal(m.task(), null);
  assert.deepEqual(m.files(), ['power-mode.json']);
});

test('power-mode-end.ps1 under 5.1: a newer eco, an eco from the station and a corrupt flag are left alone', (t) => {
  const m = machine(t, { active: SAVER });
  const newer = ecoFlag(Date.now() + 3600000, BALANCED);
  fs.writeFileSync(m.flag, newer);
  fs.writeFileSync(m.taskFile, 'the newer task');
  let r = end51(m, ['-TaskUntil', iso(Date.now() - 60000)]);
  assert.equal(r.status, 0, r.out + r.err);
  assert.match(r.out, /^power mode: ECO since .* \(end: not due, eco runs on\)$/);
  assert.equal(fs.readFileSync(m.flag, 'utf8'), newer);
  assert.equal(fs.readFileSync(m.taskFile, 'utf8'), 'the newer task');
  const station = `{ "mode": "eco", "since": "${iso(Date.now() - 3600000)}", "source": "station", "prevPlan": "${BALANCED}" }\n`;
  fs.writeFileSync(m.flag, station);
  r = end51(m, []);
  assert.equal(r.status, 0, r.out + r.err);
  assert.match(r.out, /\(end: no eco to end\)$/);
  assert.equal(fs.readFileSync(m.flag, 'utf8'), station);
  assert.equal(m.active(), SAVER);
  fs.writeFileSync(m.flag, '{not json');
  r = end51(m, ['-TaskUntil', iso(Date.now() - 60000)]);
  assert.equal(r.status, 2, r.out + r.err);
  assert.match(r.out, /^power mode: COULD NOT READ /);
  assert.equal(fs.readFileSync(m.flag, 'utf8'), '{not json');
});

test('power-mode-end.ps1 and the flag reader carry no PowerShell 7 syntax', () => {
  for (const file of [END, path.join(__dirname, 'power-mode-flag.ps1')]) {
    const r = spawnSync(PS51, ['-NoProfile', '-NonInteractive', '-Command',
      '$e = $null; $null = [Management.Automation.Language.Parser]::ParseFile($env:PARSE_FILE, [ref]$null, [ref]$e); $e | ForEach-Object { $_.Message }; exit @($e).Count'],
    { encoding: 'utf8', windowsHide: true, env: { ...process.env, PARSE_FILE: file } });
    assert.equal(r.status, 0, `${file}: ${r.stdout}${r.stderr}`);
  }
});

// ---- the station poller's side of the switch: source, by, battery -------------------------------------------------
test('eco -Source station: no end time, no end task, never expires on read; off -Source station records who', (t) => {
  const m = machine(t);
  const r = m.run(['eco', '-Source', 'station']);
  assert.equal(r.status, 0, r.out);
  const j = m.json();
  assert.deepEqual({ mode: j.mode, source: j.source, until: j.until, prevPlan: j.prevPlan }, { mode: 'eco', source: 'station', until: undefined, prevPlan: BALANCED });
  assert.match(r.out.split('\n')[0], /^power mode: ECO since \d{2}\.\d{2}\.\d{4} \d{2}:\d{2} while mains is off \(station; /);
  assert.equal(m.active(), SAVER);
  assert.equal(m.task(), null);
  fs.writeFileSync(m.flag, fs.readFileSync(m.flag, 'utf8').replace(/"since": "[^"]+"/, '"since": "2020-01-01T00:00:00+00:00"'));
  assert.equal(m.run(['status']).status, 1); // years later: still eco
  const again = m.run(['eco', '-Source', 'station']);
  assert.equal(again.status, 0, again.out);
  assert.match(again.out, /\(already on, kept\)$/);
  assert.equal(m.json().since, '2020-01-01T00:00:00+00:00');
  for (const args of [['eco', '-Source', 'station', '-For', '30'], ['eco', '-Source', 'station', '-Until', '23:00'], ['eco', '-Source', 'phone']]) assert.equal(m.run(args).status, 2, args.join(' '));
  assert.equal(m.run(['off', '-Source', 'station']).status, 0);
  assert.deepEqual({ mode: m.json().mode, by: m.json().by, prevPlan: m.json().prevPlan }, { mode: 'normal', by: 'station', prevPlan: undefined });
  assert.equal(m.active(), BALANCED);
});

test('the station never replaces or shortens a manual eco; a manual off records by: manual', (t) => {
  const m = machine(t);
  assert.equal(m.run(['eco', '-For', '90']).status, 0);
  const before = fs.readFileSync(m.flag, 'utf8');
  const r = m.run(['eco', '-Source', 'station']);
  assert.equal(r.status, 0, r.out);
  assert.match(r.out, /\(already on, kept\)$/);
  assert.equal(fs.readFileSync(m.flag, 'utf8'), before);
  assert.ok(m.task(), 'the manual end task stays');
  assert.equal(m.run(['off']).status, 0);
  assert.equal(m.json().by, 'manual');
});

test('on -Source station marks the emergency as the station\'s; a manual on does not', (t) => {
  const m = machine(t);
  assert.equal(m.run(['eco', '-Source', 'station']).status, 0);
  assert.equal(m.run(['on', '-Source', 'station']).status, 0);
  assert.deepEqual({ mode: m.json().mode, source: m.json().source, prevPlan: m.json().prevPlan }, { mode: 'emergency', source: 'station', prevPlan: BALANCED });
  assert.equal(m.run(['off', '-Source', 'station']).status, 0);
  assert.equal(m.run(['on']).status, 0);
  assert.equal('source' in m.json(), false);
});

test('battery writes the reading into the flag and keeps everything else; every later write keeps the reading', (t) => {
  const m = machine(t);
  assert.deepEqual(m.run(['battery', '-Soc', '87', '-Level', 'ok', '-Mains', 'off']), { status: 0, out: 'battery: 87 % (ok, mains off)', err: '' });
  let j = m.json();
  assert.deepEqual({ mode: j.mode, soc: j.battery.soc, level: j.battery.level, mains: j.battery.mains }, { mode: 'normal', soc: 87, level: 'ok', mains: false });
  assert.ok(Math.abs(Date.parse(j.battery.at) - Date.now()) < 120000, j.battery.at);
  assert.equal(m.run(['eco', '-For', '30']).status, 0);
  const eco = m.json();
  assert.equal(eco.battery.soc, 87);
  assert.equal(m.run(['battery', '-Soc', '18.5', '-Level', 'low', '-Mains', 'off']).status, 0);
  j = m.json();
  assert.deepEqual({ mode: j.mode, since: j.since, until: j.until, prevPlan: j.prevPlan, soc: j.battery.soc, level: j.battery.level },
    { mode: 'eco', since: eco.since, until: eco.until, prevPlan: BALANCED, soc: 18.5, level: 'low' });
  const st = m.run(['status']);
  assert.match(st.out.split('\n').pop(), /^battery: 18\.5 % \(low, mains off\) at \d{2}\.\d{2}\.\d{4} \d{2}:\d{2}$/);
  assert.equal(m.run(['on']).status, 0);
  assert.equal(m.run(['off']).status, 0);
  assert.equal(m.json().battery.soc, 18.5);
  for (const args of [['battery', '-Soc', '101', '-Level', 'ok', '-Mains', 'on'], ['battery', '-Soc', '50', '-Level', 'fine', '-Mains', 'on'], ['battery', '-Soc', '50', '-Level', 'ok', '-Mains', 'maybe'], ['battery']]) {
    assert.equal(m.run(args).status, 2, args.join(' '));
  }
  fs.writeFileSync(m.flag, '{not json');
  assert.equal(m.run(['battery', '-Soc', '50', '-Level', 'ok', '-Mains', 'on']).status, 2);
  assert.equal(fs.readFileSync(m.flag, 'utf8'), '{not json');
});

test('a flag without the new fields reads as before; a malformed battery is no battery, never a corrupt flag', (t) => {
  const m = machine(t);
  fs.writeFileSync(m.flag, '{ "mode": "emergency", "since": "2026-10-02T22:20:00Z", "battery": { "soc": "lots", "at": "x", "level": "ok" } }\n');
  assert.deepEqual(m.run(['status']), { status: 1, out: 'power mode: EMERGENCY since 03.10.2026 01:20', err: '' });
  fs.writeFileSync(m.flag, '{ "mode": "eco", "since": "2026-10-02T22:20:00Z" }\n'); // no until and not the station's: not a flag
  assert.equal(m.run(['status']).status, 2);
});

// ---- the user's task, what the station's emergency replaced, -Adopt ----------------------------------

test('the end task is the user\'s, never SYSTEM: interactive, least privilege, headless, made up at the next logon', (t) => {
  const m = machine(t);
  assert.equal(m.run(['eco']).status, 0);
  const xml = m.task();
  assert.ok(xml, 'no task registered');
  assert.doesNotMatch(xml, /S-1-5-18|HighestAvailable|<LogonType>(Password|S4U|ServiceAccount)/);
  assert.match(xml, /<UserId>S-1-5-21-[\d-]+<\/UserId><LogonType>InteractiveToken<\/LogonType><RunLevel>LeastPrivilege<\/RunLevel>/);
  assert.ok(xml.includes(`<Command>${path.join(process.env.SystemRoot, 'System32', 'conhost.exe')}</Command>`), xml);
  assert.ok(xml.includes(`<Arguments>--headless &quot;${path.join(process.env.SystemRoot, 'System32', 'WindowsPowerShell', 'v1.0', 'powershell.exe')}&quot; -NoProfile -NonInteractive -ExecutionPolicy Bypass -File &quot;${END}&quot; -Path &quot;${m.flag}&quot;`), xml);
  assert.ok(xml.includes('<StartWhenAvailable>true</StartWhenAvailable>'), xml);
  // registered with a plain /create /xml: no /ru, no password, nothing that asks for elevation
  const create = m.calls().find((c) => c.startsWith('schtasks /create'));
  assert.match(create, /^schtasks \/create \/tn BluettiWatcherEcoEnd \/xml \S+ \/f$/);
});

test('the station\'s emergency over a manual eco remembers it and names its end time; mains back (off -Source station) lifts everything to maximum performance', (t) => {
  const m = machine(t);
  assert.equal(m.run(['eco', '-For', '120']).status, 0);
  const eco = m.json();
  const on = m.run(['on', '-Source', 'station']);
  assert.equal(on.status, 0, on.out);
  let j = m.json();
  assert.deepEqual({ mode: j.mode, source: j.source, prevPlan: j.prevPlan, replaced: j.replaced },
    { mode: 'emergency', source: 'station', prevPlan: BALANCED, replaced: { mode: 'eco', since: eco.since, until: eco.until } });
  assert.match(on.out.split('\n')[0], /^power mode: EMERGENCY since \d{2}\.\d{2}\.\d{4} \d{2}:\d{2} \(over the user's eco until \d{2}\.\d{2}\.\d{4} \d{2}:\d{2}\)$/);
  assert.equal(m.task(), null);
  assert.equal(m.run(['battery', '-Soc', '19', '-Level', 'low', '-Mains', 'off', '-HibernateAt', '5']).status, 0);
  assert.deepEqual(m.json().replaced, j.replaced); // the reading keeps it
  assert.equal(m.json().battery.hibernateAt, 5);
  assert.match(m.run(['status']).out, /over the user's eco until/);
  const off = m.run(['off', '-Source', 'station']);
  assert.equal(off.status, 0, off.out);
  j = m.json();
  assert.deepEqual({ mode: j.mode, by: j.by, prevPlan: j.prevPlan, replaced: j.replaced }, { mode: 'normal', by: 'station', prevPlan: undefined, replaced: undefined });
  assert.deepEqual({ active: m.active(), overlay: m.overlay(), task: m.task() }, { active: BALANCED, overlay: BEST, task: null });
  assert.equal(off.out, 'power mode: normal\nplan: Balanced, Best performance');
});

test('a replaced eco is never put back: an off by hand drops it too, end time passed or not', (t) => {
  for (const untilMs of [Date.now() - 60000, Date.now() + 3600000]) {
    const m = machine(t, { active: SAVER });
    fs.writeFileSync(m.flag, `{ "mode": "emergency", "since": "${iso(Date.now() - 600000)}", "source": "station", "prevPlan": "${BALANCED}", "replaced": { "mode": "eco", "since": "${iso(Date.now() - 7200000)}", "until": "${iso(untilMs)}" } }\n`);
    assert.equal(m.run(['off']).status, 0);
    assert.deepEqual({ mode: m.json().mode, by: m.json().by, replaced: m.json().replaced }, { mode: 'normal', by: 'manual', replaced: undefined });
    assert.deepEqual({ active: m.active(), overlay: m.overlay() }, { active: BALANCED, overlay: BEST });
  }
});

test('the station\'s emergency over its own eco, and a manual on, remember nothing', (t) => {
  const m = machine(t);
  assert.equal(m.run(['eco', '-Source', 'station']).status, 0);
  assert.equal(m.run(['on', '-Source', 'station']).status, 0);
  assert.equal('replaced' in m.json(), false);
  assert.equal(m.run(['off', '-Source', 'station']).status, 0);
  assert.equal(m.json().mode, 'normal');
  assert.equal(m.run(['eco']).status, 0);
  assert.equal(m.run(['on']).status, 0);
  assert.equal('replaced' in m.json(), false);
});

test('the flag lock: a manual on made while the battery write sits between its read and its write survives', async (t) => {
  const m = machine(t);
  const env = { ...process.env, FAKE_PM_DIR: m.sim, POWER_MODE_POWERCFG: path.join(m.sim, 'fake-powercfg.ps1'), POWER_MODE_SCHTASKS: path.join(m.sim, 'fake-schtasks.ps1'), POWER_MODE_TEST_HOLD_MS: '1500', POWER_MODE_TEST: '1' };
  delete env.POWER_MODE_FILE;
  const child = spawn('pwsh', ['-NoProfile', '-NonInteractive', '-File', CLI, 'battery', '-Soc', '50', '-Level', 'ok', '-Mains', 'off', '-Path', m.flag], { env, windowsHide: true });
  const exited = new Promise((resolve) => child.on('exit', resolve));
  const held = `${m.flag}.held`;
  for (let i = 0; i < 200 && !fs.existsSync(held); i++) await new Promise((r) => setTimeout(r, 50));
  assert.ok(fs.existsSync(held), 'the battery write never reached its hold');
  const on = m.run(['on']); // the user's on, while the battery write has read the flag and not written it yet
  assert.equal(await exited, 0);
  assert.equal(on.status, 0, on.out);
  const j = m.json();
  assert.deepEqual({ mode: j.mode, soc: j.battery && j.battery.soc }, { mode: 'emergency', soc: 50 });
});

// Another process asks for the flag mutex with no wait: free · held (not released) · abandoned (never released, the
// holding process died holding it). Re-entry by one thread would hide a missing release, so this always crosses processes.
const MUTEX_PROBE = "$m = [Threading.Mutex]::new($false, 'Local\\BluettiWatcherPowerMode'); try { if ($m.WaitOne(0)) { 'free'; $m.ReleaseMutex() } else { 'held' } } catch [Threading.AbandonedMutexException] { 'abandoned'; $m.ReleaseMutex() }";
// A test that probes the mutex runs the script under a mutex name of its own (POWER_MODE_MUTEX): the default name is
// machine-wide, so another process using it (a second test run, an installed watcher) would read as "held" here.
const uniqueMutex = () => `Local\\BWPMTest-${process.pid}-${crypto.randomBytes(6).toString('hex')}`;
const probeMutex = (name) => spawnSync('pwsh', ['-NoProfile', '-NonInteractive', '-Command', MUTEX_PROBE.replace('Local\\BluettiWatcherPowerMode', name)], { encoding: 'utf8', windowsHide: true }).stdout.trim();

test('the flag mutex is released on every exit of power-mode.ps1, seen from another process', async (t) => {
  const m = machine(t);
  const mutex = uniqueMutex();
  const env = { ...process.env, FAKE_PM_DIR: m.sim, POWER_MODE_POWERCFG: path.join(m.sim, 'fake-powercfg.ps1'), POWER_MODE_SCHTASKS: path.join(m.sim, 'fake-schtasks.ps1'), POWER_MODE_TEST: '1', POWER_MODE_MUTEX: mutex };
  delete env.POWER_MODE_FILE;
  // pwsh -File: an exit 2 inside the locked region (a bad -Soc), then an exit 0 at the end
  for (const args of [['battery', '-Soc', 'bad', '-Level', 'ok', '-Mains', 'off'], ['on']]) {
    const r = spawnSync('pwsh', ['-NoProfile', '-NonInteractive', '-File', CLI, ...args, '-Path', m.flag], { encoding: 'utf8', windowsHide: true, env });
    assert.equal(r.status, args[0] === 'on' ? 0 : 2, r.stdout);
    assert.equal(probeMutex(mutex), 'free', `after pwsh -File ${args[0]}`);
  }
  // & power-mode.ps1 in a process that lives on afterwards (as the poller calls it): released before that process ends
  const marker = path.join(m.dir, 'called');
  const host = `& '${CLI}' battery -Soc bad -Level ok -Mains off -Path '${m.flag}' | Out-Null; & '${CLI}' off -Path '${m.flag}' | Out-Null; [IO.File]::WriteAllText('${marker}', 'x'); Start-Sleep -Seconds 6`;
  const child = spawn('pwsh', ['-NoProfile', '-NonInteractive', '-Command', host], { env, windowsHide: true });
  const exited = new Promise((resolve) => child.on('exit', resolve));
  for (let i = 0; i < 200 && !fs.existsSync(marker); i++) await new Promise((r) => setTimeout(r, 50));
  assert.ok(fs.existsSync(marker), 'the host never got past its calls');
  assert.equal(probeMutex(mutex), 'free', 'held by a live process after & power-mode.ps1 returned');
  await exited;
});
test('the user\'s eco end time under the station\'s emergency is dropped from the status line once it has passed', (t) => {
  const m = machine(t);
  const flag = (untilMs) => `{ "mode": "emergency", "since": "${iso(Date.now() - 600000)}", "source": "station", "replaced": { "mode": "eco", "since": "${iso(Date.now() - 7200000)}", "until": "${iso(untilMs)}" } }\n`;
  fs.writeFileSync(m.flag, flag(Date.now() + 3600000));
  assert.match(m.run(['status']).out, /over the user's eco until/);
  fs.writeFileSync(m.flag, flag(Date.now() - 60000));
  assert.match(m.run(['status']).out, /^power mode: EMERGENCY since \d{2}\.\d{2}\.\d{4} \d{2}:\d{2}$/m);
});
test('eco -Adopt: an eco that is on gets a plan to return to and its end task; since and until stay', (t) => {
  const m = machine(t, { plans: [BALANCED, SAVER, HIGH], active: SAVER });
  const untilMs = Date.now() + 3600000;
  fs.writeFileSync(m.flag, ecoFlag(untilMs));
  const r = m.run(['eco', '-Adopt', '-ReturnPlan', 'balanced']);
  assert.equal(r.status, 0, r.out);
  assert.deepEqual(r.out.split('\n').slice(1), [`plan: returns to ${BALANCED}`, 'end task: BluettiWatcherEcoEnd registered']);
  assert.equal(fs.readFileSync(m.flag, 'utf8'), ecoFlag(untilMs, BALANCED));
  assert.ok(m.task().includes(`-TaskUntil ${iso(untilMs)}</Arguments>`), m.task());
  assert.equal(m.active(), SAVER); // the plan itself is not touched
  assert.deepEqual(m.calls().filter((c) => c.startsWith('powercfg')), ['powercfg /list']);
  assert.equal(m.run(['eco', '-Adopt', '-ReturnPlan', 'high']).status, 0);
  assert.equal(m.json().prevPlan, HIGH);
  assert.equal(m.run(['eco', '-Adopt', '-ReturnPlan', BALANCED.toUpperCase()]).status, 0);
  assert.equal(m.json().prevPlan, BALANCED);
  assert.equal(m.run(['off']).status, 0);
  assert.equal(m.active(), BALANCED);
});

test('eco -Adopt refuses: no eco on, Power saver, a plan that is not there, no -ReturnPlan, an end time with it', (t) => {
  const m = machine(t, { plans: [BALANCED, SAVER], active: SAVER });
  assert.equal(m.run(['eco', '-Adopt', '-ReturnPlan', 'balanced']).status, 2); // no flag: no eco
  assert.deepEqual(m.files(), []);
  const text = ecoFlag(Date.now() + 3600000);
  fs.writeFileSync(m.flag, text);
  for (const args of [['eco', '-Adopt'], ['eco', '-ReturnPlan', 'balanced'], ['eco', '-Adopt', '-ReturnPlan', SAVER], ['eco', '-Adopt', '-ReturnPlan', 'high'],
    ['eco', '-Adopt', '-ReturnPlan', 'fast'], ['eco', '-Adopt', '-ReturnPlan', 'balanced', '-For', '30'], ['off', '-Adopt', '-ReturnPlan', 'balanced']]) {
    const r = m.run(args);
    assert.equal(r.status, 2, `${args.join(' ')}: ${r.out}`);
    assert.match(r.out, /^power mode: COULD NOT RUN: /);
  }
  assert.equal(fs.readFileSync(m.flag, 'utf8'), text);
  assert.equal(m.task(), null);
});

// ---- the plan to return to (power-mode.config.json beside the flag), prev-plan, the GPU power cap --------------------
// machine() writes { returnPlan: 'balanced' } unless told otherwise; `config: null` = no config file.
const CUSTOM = '11111111-2222-3333-4444-555555555555';
const GONE = '99999999-8888-7777-6666-555555555555';
const ALL = [BALANCED, SAVER, HIGH, CUSTOM];

test('no config: every exit from eco puts Windows on High performance, whatever plan was active before', (t) => {
  const m = machine(t, { plans: ALL, active: BALANCED, config: null });
  assert.equal(m.run(['eco']).status, 0);
  assert.deepEqual({ prevPlan: m.json().prevPlan, active: m.active() }, { prevPlan: BALANCED, active: SAVER });
  assert.equal(m.run(['status']).out.split('\n')[1], `plan: returns to High performance (${HIGH}); before eco: Balanced (${BALANCED})`);
  assert.deepEqual(m.run(['off']), { status: 0, out: 'power mode: normal\nplan: High performance', err: '' });
  assert.deepEqual({ active: m.active(), overlay: m.overlay(), prevPlan: m.json().prevPlan }, { active: HIGH, overlay: null, prevPlan: undefined });
  // the station: eco, escalated to emergency, lifted by off -Source station (mains back, the page's Max)
  const s = machine(t, { plans: ALL, active: BALANCED, config: null });
  for (const args of [['eco', '-Source', 'station'], ['on', '-Source', 'station'], ['off', '-Source', 'station']]) assert.equal(s.run(args).status, 0, args.join(' '));
  assert.deepEqual({ active: s.active(), mode: s.json().mode, by: s.json().by }, { active: HIGH, mode: 'normal', by: 'station' });
  // the end task
  const e = machine(t, { plans: ALL, active: SAVER, config: null });
  const untilMs = Date.now() - 60000;
  fs.writeFileSync(e.flag, ecoFlag(untilMs, BALANCED));
  assert.deepEqual(end51(e, ['-TaskUntil', iso(untilMs)]), { status: 0, out: 'power mode: normal (eco ended)\nplan: High performance', err: '' });
  assert.equal(e.active(), HIGH);
});

test('no config and no High performance plan on the machine: Balanced + Best performance, with a warn line', (t) => {
  const m = machine(t, { plans: [BALANCED, SAVER], config: null });
  assert.equal(m.run(['eco']).status, 0);
  assert.deepEqual(m.run(['off']), { status: 0, err: '',
    out: `power mode: normal\nwarn: the power plan High performance (${HIGH}) is not on this machine: Balanced is used instead\nplan: Balanced, Best performance` });
  assert.deepEqual({ active: m.active(), overlay: m.overlay() }, { active: BALANCED, overlay: BEST });
});

test('config returnPlan: balanced = Balanced + Best performance; a plan GUID = that plan; a GUID that is not here = Balanced, said so', (t) => {
  for (const [returnPlan, want, overlay, warned] of [['balanced', BALANCED, BEST, false], ['high', HIGH, null, false], [CUSTOM.toUpperCase(), CUSTOM, null, false], [GONE, BALANCED, BEST, true]]) {
    const m = machine(t, { plans: ALL, active: HIGH, config: { returnPlan } });
    assert.equal(m.run(['eco']).status, 0);
    const off = m.run(['off']);
    assert.equal(off.status, 0, off.out);
    assert.equal(/^warn: the power plan .* is not on this machine: Balanced is used instead$/m.test(off.out), warned, off.out);
    assert.deepEqual({ active: m.active(), overlay: m.overlay(), prevPlan: m.json().prevPlan }, { active: want, overlay, prevPlan: undefined });
  }
});

test('config returnPlan previous: the plan active before eco comes back; none recorded, Power saver or a plan that is gone = High performance', (t) => {
  const m = machine(t, { plans: ALL, active: CUSTOM, config: { returnPlan: 'previous' } });
  assert.equal(m.run(['eco']).status, 0);
  assert.equal(m.json().prevPlan, CUSTOM);
  assert.equal(m.run(['status']).out.split('\n')[1], `plan: returns to a plan (${CUSTOM})`);
  assert.equal(m.run(['off']).status, 0);
  assert.deepEqual({ active: m.active(), overlay: m.overlay() }, { active: CUSTOM, overlay: null });
  for (const prev of [undefined, SAVER, GONE]) {
    const n = machine(t, { plans: ALL, active: SAVER, config: { returnPlan: 'previous' } });
    fs.writeFileSync(n.flag, ecoFlag(Date.now() + 3600000, prev));
    assert.match(n.run(['status']).out.split('\n')[1], new RegExp(`^plan: returns to High performance \\(${HIGH}\\)`));
    assert.equal(n.run(['off']).status, 0);
    assert.equal(n.active(), HIGH, String(prev));
  }
});

test('eco started under Power saver never records Power saver: the plan to return to is recorded instead', (t) => {
  for (const [config, want] of [[null, HIGH], [{ returnPlan: 'previous' }, HIGH], [{ returnPlan: CUSTOM }, CUSTOM]]) {
    const m = machine(t, { plans: ALL, active: SAVER, config });
    const r = m.run(['eco']);
    assert.equal(r.status, 0, r.out);
    assert.equal(m.json().prevPlan, want);
    assert.ok(r.out.includes(`plan: Power saver was already on; returns to ${want}`), r.out);
    assert.ok(m.task(), 'no end task');
    assert.equal(m.run(['off']).status, 0);
    assert.equal(m.active(), want);
  }
});

test('an eco flag with no plan recorded (written before): off under Power saver sets the plan to return to; under another plan it changes nothing', (t) => {
  const m = machine(t, { plans: ALL, active: SAVER, config: null });
  fs.writeFileSync(m.flag, ecoFlag(Date.now() + 3600000));
  assert.equal(m.run(['status']).out.split('\n')[1], `plan: returns to High performance (${HIGH})`);
  assert.equal(m.run(['off']).status, 0);
  assert.equal(m.active(), HIGH);
  const n = machine(t, { plans: ALL, active: BALANCED, config: null });
  fs.writeFileSync(n.flag, ecoFlag(Date.now() + 3600000));
  assert.equal(n.run(['status']).out.split('\n')[1], 'plan: nothing to return to');
  assert.deepEqual(n.run(['off']), { status: 0, out: 'power mode: normal', err: '' });
  assert.equal(n.active(), BALANCED);
  assert.deepEqual(n.calls().filter((c) => /setactive/.test(c)), []);
});

test('a config that cannot be read, or a returnPlan that is no plan: a warn line, the defaults apply (High performance, GPU cap on), exit 0', (t) => {
  for (const config of ['{not json', { returnPlan: 'fast' }, { returnPlan: 'saver' }]) {
    const m = machine(t, { plans: ALL, active: BALANCED, config, gpu: [250, 100, 250] });
    const r = m.run(['eco']);
    assert.equal(r.status, 0, r.out);
    assert.match(r.out, /^warn: power-mode\.config\.json/m);
    assert.deepEqual({ prev: m.json().prevGpuLimitW, gpu: m.gpu() }, { prev: 250, gpu: 100 });
    assert.equal(m.run(['off']).status, 0);
    assert.deepEqual({ active: m.active(), gpu: m.gpu() }, { active: HIGH, gpu: 250 });
  }
});

test('prev-plan rewrites the plan an eco that is on remembers; since, until and the active plan stay', (t) => {
  const m = machine(t, { plans: ALL, active: SAVER, config: { returnPlan: 'previous' } });
  const untilMs = Date.now() + 3600000;
  fs.writeFileSync(m.flag, ecoFlag(untilMs, BALANCED).replace(' }\n', ', "prevGpuLimitW": 250, "gpuCapW": 100 }\n'));
  const r = m.run(['prev-plan', 'high']);
  assert.equal(r.status, 0, r.out);
  assert.equal(r.out.split('\n')[1], `plan: returns to High performance (${HIGH})`);
  assert.equal(fs.readFileSync(m.flag, 'utf8'), ecoFlag(untilMs, HIGH).replace(' }\n', ', "prevGpuLimitW": 250, "gpuCapW": 100 }\n'));
  assert.equal(m.active(), SAVER);
  assert.equal(m.run(['status']).out.split('\n')[1], `plan: returns to High performance (${HIGH})`);
  for (const [value, want] of [['balanced', BALANCED], [CUSTOM.toUpperCase(), CUSTOM]]) {
    assert.equal(m.run(['prev-plan', value]).status, 0, value);
    assert.equal(m.json().prevPlan, want);
  }
  assert.deepEqual(m.calls().filter((c) => /setactive/.test(c)), []);
  assert.equal(m.run(['off']).status, 0);
  assert.equal(m.active(), CUSTOM);
});

test('prev-plan with no eco on exits 1 and changes nothing; a value that is no plan, Power saver, or a plan that is not here, exits 2', (t) => {
  const m = machine(t, { plans: [BALANCED, SAVER, HIGH], active: SAVER });
  const none = m.run(['prev-plan', 'high']);
  assert.deepEqual(none, { status: 1, out: 'power mode: prev-plan: no eco is on, nothing changed', err: '' });
  assert.deepEqual(m.files(), []);
  for (const text of ['{ "mode": "normal", "since": "2026-10-02T22:20:00Z" }\n', `{ "mode": "emergency", "since": "2026-10-02T22:20:00Z", "prevPlan": "${BALANCED}" }\n`, ecoFlag(Date.now() - 60000, BALANCED)]) {
    fs.writeFileSync(m.flag, text);
    assert.equal(m.run(['prev-plan', 'high']).status, 1, text);
    assert.equal(fs.readFileSync(m.flag, 'utf8'), text);
  }
  const text = ecoFlag(Date.now() + 3600000, BALANCED);
  fs.writeFileSync(m.flag, text);
  for (const args of [['prev-plan'], ['prev-plan', 'fast'], ['prev-plan', 'saver'], ['prev-plan', SAVER], ['prev-plan', GONE]]) {
    const r = m.run(args);
    assert.equal(r.status, 2, `${args.join(' ')}: ${r.out}`);
    assert.match(r.out, /^power mode: COULD NOT RUN: /);
  }
  assert.equal(fs.readFileSync(m.flag, 'utf8'), text);
});

test('config gpuCap false: eco and off never call nvidia-smi', (t) => {
  for (const config of [{ gpuCap: false }, { gpuCap: false, returnPlan: 'balanced' }]) {
    const m = machine(t, { plans: ALL, gpu: [250, 100, 250], config });
    assert.equal(m.run(['eco']).status, 0);
    assert.equal('prevGpuLimitW' in m.json(), false);
    assert.equal(m.run(['off']).status, 0);
    assert.deepEqual(m.gpuCalls(), []);
    assert.equal(m.gpu(), 250);
  }
});

test('the GPU cap, on unless the config says no: eco sets the minimum power limit and remembers the old one; a second eco, on and battery keep it; off puts it back and forgets it', (t) => {
  const m = machine(t, { gpu: [250, 100, 250] });
  const r = m.run(['eco', '-For', '30']);
  assert.equal(r.status, 0, r.out);
  assert.ok(r.out.split('\n').includes('gpu cap: 100 W (returns to 250 W)'), r.out);
  assert.deepEqual({ prev: m.json().prevGpuLimitW, cap: m.json().gpuCapW, gpu: m.gpu() }, { prev: 250, cap: 100, gpu: 100 });
  assert.deepEqual(m.gpuCalls(), [GPU_QUERY, 'nvidia-smi -pl 100']);
  assert.ok(m.run(['status']).out.split('\n').includes('gpu cap: 100 W (returns to 250 W)'));
  assert.equal(m.run(['eco', '-For', '90']).status, 0); // the limit now reads 100: it must not become the one to return to
  assert.deepEqual({ prev: m.json().prevGpuLimitW, prevPlan: m.json().prevPlan }, { prev: 250, prevPlan: BALANCED });
  assert.equal(m.run(['battery', '-Soc', '60', '-Level', 'ok', '-Mains', 'off']).status, 0);
  assert.equal(m.run(['on']).status, 0); // eco escalated to emergency: the cap and the debt stay
  assert.deepEqual({ mode: m.json().mode, prev: m.json().prevGpuLimitW, cap: m.json().gpuCapW, gpu: m.gpu() }, { mode: 'emergency', prev: 250, cap: 100, gpu: 100 });
  const off = m.run(['off']);
  assert.deepEqual(off, { status: 0, out: 'power mode: normal\nplan: Balanced, Best performance\ngpu: power limit back to 250 W', err: '' });
  assert.equal(m.gpu(), 250);
  assert.deepEqual({ prev: m.json().prevGpuLimitW, cap: m.json().gpuCapW }, { prev: undefined, cap: undefined });
  // LIVE_QUERY: the status above, then the drift checks of the second eco and of the battery reading (the limit read the cap: nothing set)
  assert.deepEqual(m.gpuCalls(), [GPU_QUERY, 'nvidia-smi -pl 100', LIVE_QUERY, LIVE_QUERY, LIVE_QUERY, 'nvidia-smi -pl 250']);
  assert.deepEqual(m.run(['off']), { status: 0, out: 'power mode: normal (already off, since kept)', err: '' });
  assert.equal(m.gpuCalls().length, 6);
});

test('the GPU cap, the station: eco -Source station caps; escalated to emergency and lifted by off -Source station, the limit is back', (t) => {
  const m = machine(t, { gpu: [200, 100, 250] });
  assert.equal(m.run(['eco', '-Source', 'station']).status, 0);
  assert.deepEqual({ prev: m.json().prevGpuLimitW, gpu: m.gpu() }, { prev: 200, gpu: 100 });
  assert.match(m.run(['eco', '-Source', 'station']).out, /\(already on, kept\)$/);
  assert.equal(m.json().prevGpuLimitW, 200);
  assert.equal(m.run(['on', '-Source', 'station']).status, 0);
  assert.deepEqual({ prev: m.json().prevGpuLimitW, gpu: m.gpu() }, { prev: 200, gpu: 100 });
  const off = m.run(['off', '-Source', 'station']);
  assert.equal(off.status, 0, off.out);
  assert.deepEqual({ gpu: m.gpu(), by: m.json().by, prev: m.json().prevGpuLimitW }, { gpu: 200, by: 'station', prev: undefined });
});

test('the end task under Windows PowerShell 5.1 puts the GPU limit back and drops the keys', (t) => {
  const m = machine(t, { active: SAVER, gpu: [100, 100, 250] });
  const untilMs = Date.now() - 60000;
  fs.writeFileSync(m.flag, ecoFlag(untilMs, BALANCED).replace(' }\n', ', "prevGpuLimitW": 250, "gpuCapW": 100 }\n'));
  const r = end51(m, ['-TaskUntil', iso(untilMs)], m.env);
  assert.deepEqual(r, { status: 0, out: 'power mode: normal (eco ended)\nplan: Balanced, Best performance\ngpu: power limit back to 250 W', err: '' });
  assert.equal(m.gpu(), 250);
  assert.equal(fs.readFileSync(m.flag, 'utf8'), `{ "mode": "normal", "since": "${iso(untilMs)}" }\n`);
});

test('the GPU never fails eco or off: no nvidia-smi, a failing query, a refused set are notes, exit 0; a refused restore stays owed for the next off', (t) => {
  let m = machine(t, { gpu: [250, 100, 250] });
  let r = m.run(['eco'], { POWER_MODE_NVIDIASMI: path.join(m.sim, 'no-such-nvidia-smi.ps1') });
  assert.equal(r.status, 0, r.out);
  assert.match(r.out, /^note: gpu cap not set \(/m);
  assert.equal('prevGpuLimitW' in m.json(), false);
  m = machine(t, { gpu: [250, 100, 250] });
  r = m.run(['eco'], { FAKE_GPU_QUERYFAIL: '1' });
  assert.equal(r.status, 0, r.out);
  assert.match(r.out, /^note: gpu cap not set \(NVIDIA-SMI has failed/m);
  assert.deepEqual({ has: 'prevGpuLimitW' in m.json(), gpu: m.gpu(), mode: m.json().mode, plan: m.active() }, { has: false, gpu: 250, mode: 'eco', plan: SAVER });
  m = machine(t, { gpu: [250, 100, 250] });
  r = m.run(['eco'], { FAKE_GPU_SETFAIL: '1' });
  assert.equal(r.status, 0, r.out);
  assert.match(r.out, /^note: gpu cap not set \(Changing power management limit is not permitted: Insufficient Permissions\)$/m);
  assert.deepEqual({ has: 'prevGpuLimitW' in m.json(), cap: 'gpuCapW' in m.json(), gpu: m.gpu(), mode: m.json().mode }, { has: false, cap: false, gpu: 250, mode: 'eco' });
  assert.equal(m.run(['off']).status, 0);
  m = machine(t, { gpu: [250, 100, 250] });
  assert.equal(m.run(['eco']).status, 0);
  r = m.run(['off'], { FAKE_GPU_SETFAIL: '1' });
  assert.equal(r.status, 0, r.out);
  assert.match(r.out, /^note: the GPU power limit could not be set back to 250 W \(.*\): it stays owed in the flag, run off again$/m);
  assert.deepEqual({ mode: m.json().mode, prev: m.json().prevGpuLimitW, gpu: m.gpu(), plan: m.active() }, { mode: 'normal', prev: 250, gpu: 100, plan: BALANCED });
  assert.equal(m.run(['off']).status, 0);
  assert.deepEqual({ prev: m.json().prevGpuLimitW, gpu: m.gpu() }, { prev: undefined, gpu: 250 });
});

test('an eco flag with no GPU limit recorded (written before): off never touches the GPU, one note says so', (t) => {
  for (const gpu of [[100, 100, 250], [200, 100, 250]]) {
    const m = machine(t, { active: SAVER, gpu });
    fs.writeFileSync(m.flag, ecoFlag(Date.now() + 3600000, BALANCED));
    assert.deepEqual(m.run(['off']), { status: 0, err: '', out: 'power mode: normal\nplan: Balanced, Best performance\nnote: gpu: this eco recorded no power limit to return to: the GPU is left as it is' });
    assert.deepEqual({ gpu: m.gpu(), calls: m.gpuCalls() }, { gpu: gpu[0], calls: [] });
  }
});

test('off when eco is not on changes neither the plan nor the GPU', (t) => {
  for (const text of [null, '{ "mode": "normal", "since": "2026-10-02T22:20:00Z" }\n', '{ "mode": "emergency", "since": "2026-10-02T22:20:00Z" }\n']) {
    const m = machine(t, { plans: ALL, active: SAVER, gpu: [100, 100, 250], config: null });
    if (text) fs.writeFileSync(m.flag, text);
    assert.equal(m.run(['off']).status, 0);
    assert.deepEqual(m.calls(), []);
    assert.deepEqual({ active: m.active(), gpu: m.gpu() }, { active: SAVER, gpu: 100 });
  }
});

// ---- the GPU tasks: a caller that is not elevated, the guard, the timeouts, drift -----------------------------------
const NOT_ELEVATED = { POWER_MODE_ELEVATED: '0' };
const OWED = (prev, cap) => `, "prevGpuLimitW": ${prev}, "gpuCapW": ${cap} }\n`;

test('not elevated, both tasks registered: eco runs the cap task, off -Source station runs the restore task; nvidia-smi -pl is never called', (t) => {
  const m = machine(t, { gpu: [250, 100, 250] });
  m.registerGpu(100, 250);
  const eco = m.run(['eco'], NOT_ELEVATED);
  assert.equal(eco.status, 0, eco.out);
  assert.ok(eco.out.split('\n').includes('gpu cap: 100 W (returns to 250 W)'), eco.out);
  assert.deepEqual({ prev: m.json().prevGpuLimitW, cap: m.json().gpuCapW, gpu: m.gpu() }, { prev: 250, cap: 100, gpu: 100 });
  assert.ok(m.calls().includes(`schtasks /run /tn ${CAP_TASK}`), m.calls().join('|'));
  const st = m.run(['status'], NOT_ELEVATED).out.split('\n');
  assert.ok(st.includes(`gpu helper: ${CAP_TASK} 100 W, ${RESTORE_TASK} 250 W`) && st.includes('gpu live: 100 W'), st.join('|'));
  const off = m.run(['off', '-Source', 'station'], NOT_ELEVATED);
  assert.equal(off.status, 0, off.out);
  assert.ok(off.out.split('\n').includes('gpu: power limit back to 250 W'), off.out);
  assert.ok(m.calls().includes(`schtasks /run /tn ${RESTORE_TASK}`), m.calls().join('|'));
  assert.deepEqual({ prev: m.json().prevGpuLimitW, gpu: m.gpu() }, { prev: undefined, gpu: 250 });
  assert.deepEqual(m.gpuCalls().filter((c) => c.includes('-pl')), []);
});

test('not elevated, no tasks: a note names gpu-task install, eco and off exit 0, the restore stays owed; an elevated off pays it', (t) => {
  const m = machine(t, { gpu: [250, 100, 250] });
  const eco = m.run(['eco'], NOT_ELEVATED);
  assert.equal(eco.status, 0, eco.out);
  assert.match(eco.out, new RegExp(`^note: gpu cap not set \\(not elevated and the task ${CAP_TASK} is not registered: run power-mode\\.ps1 gpu-task install in an elevated shell\\)$`, 'm'));
  assert.deepEqual({ has: 'prevGpuLimitW' in m.json(), gpu: m.gpu(), mode: m.json().mode }, { has: false, gpu: 250, mode: 'eco' });
  assert.ok(m.run(['status'], NOT_ELEVATED).out.split('\n').includes('gpu helper: not registered (an elevated shell: power-mode.ps1 gpu-task install)'));
  const n = machine(t, { active: SAVER, gpu: [100, 100, 250] });
  fs.writeFileSync(n.flag, ecoFlag(Date.now() + 3600000, BALANCED).replace(' }\n', OWED(250, 100)));
  const off = n.run(['off', '-Source', 'station'], NOT_ELEVATED);
  assert.equal(off.status, 0, off.out);
  assert.match(off.out, /^note: the GPU power limit could not be set back to 250 W \(not elevated and the task BluettiWatcherEcoGpuRestore is not registered: .*gpu-task install in an elevated shell\): it stays owed in the flag, run off again$/m);
  assert.deepEqual({ mode: n.json().mode, prev: n.json().prevGpuLimitW, gpu: n.gpu(), plan: n.active() }, { mode: 'normal', prev: 250, gpu: 100, plan: BALANCED });
  assert.deepEqual([...m.gpuCalls(), ...n.gpuCalls()].filter((c) => c.includes('-pl')), []);
  assert.equal(n.run(['off']).status, 0);
  assert.deepEqual({ prev: n.json().prevGpuLimitW, gpu: n.gpu() }, { prev: undefined, gpu: 250 });
});

test('a task by that name whose action is not System32 nvidia-smi -pl <watts> is never run', (t) => {
  for (const xml of [`<Task><Actions><Exec><Command>C:\\Users\\Public\\conhost.exe</Command><Arguments>--headless &quot;${SYS32}\\nvidia-smi.exe&quot; -pl 100</Arguments></Exec></Actions></Task>`,
    gpuTaskXml(100).replace(`${SYS32}\\nvidia-smi.exe`, 'C:\\Users\\Public\\nvidia-smi.exe'), gpuTaskXml(100).replace('-pl 100', '-pl 100 -i 1'),
    gpuTaskXml(100).replace('HighestAvailable', 'LeastPrivilege'), gpuTaskXml(100).replace(/<Principals>.*<\/Principals>/, '')]) {
    const m = machine(t, { gpu: [250, 100, 250] });
    fs.writeFileSync(path.join(m.sim, `task-${CAP_TASK}.xml`), xml);
    const eco = m.run(['eco'], NOT_ELEVATED);
    assert.match(eco.out, new RegExp(`^note: gpu cap not set \\(not elevated and the task ${CAP_TASK} is not the one gpu-task install registers: `, 'm'));
    assert.deepEqual({ runs: m.calls().filter((c) => c.includes('/run')), gpu: m.gpu() }, { runs: [], gpu: 250 });
  }
});

test('the restore task\'s fixed value is not the recorded limit: the task runs, the debt stays, an elevated off pays it', (t) => {
  const m = machine(t, { active: SAVER, gpu: [100, 100, 250] });
  m.registerGpu(100, 250);
  fs.writeFileSync(m.flag, ecoFlag(Date.now() + 3600000, BALANCED).replace(' }\n', OWED(200, 100)));
  const off = m.run(['off'], NOT_ELEVATED);
  assert.equal(off.status, 0, off.out);
  assert.ok(off.out.split('\n').includes(`note: the GPU power limit is 250 W (the fixed value of ${RESTORE_TASK}), not the recorded 200 W: it stays owed in the flag; an elevated shell pays it: power-mode.ps1 off`), off.out);
  assert.deepEqual({ mode: m.json().mode, prev: m.json().prevGpuLimitW, gpu: m.gpu() }, { mode: 'normal', prev: 200, gpu: 250 });
  assert.equal(m.run(['off']).status, 0);
  assert.deepEqual({ prev: m.json().prevGpuLimitW, gpu: m.gpu() }, { prev: undefined, gpu: 200 });
});

test('a read-back that times out keeps what is owed: eco keeps the limit to return to, off keeps the debt; a task that does not start owes nothing at eco', (t) => {
  const idle = { ...NOT_ELEVATED, POWER_MODE_GPU_WAIT_MS: '300', FAKE_PM_HELPER_IDLE: '1' };
  const m = machine(t, { gpu: [250, 100, 250] });
  m.registerGpu(100, 250);
  const eco = m.run(['eco'], idle);
  assert.equal(eco.status, 0, eco.out);
  assert.match(eco.out, new RegExp(`^note: gpu cap not confirmed \\(the task ${CAP_TASK} ran but the limit reads 250 W, not 100 W\\): the limit to return to stays recorded, off puts it back$`, 'm'));
  assert.deepEqual({ prev: m.json().prevGpuLimitW, cap: m.json().gpuCapW }, { prev: 250, cap: 100 });
  fs.writeFileSync(path.join(m.sim, 'gpu.txt'), '100,100,250'); // it did apply, late
  const off = m.run(['off'], idle);
  assert.equal(off.status, 0, off.out);
  assert.match(off.out, new RegExp(`^note: the GPU power limit was not confirmed at 250 W \\(the task ${RESTORE_TASK} ran but the limit reads 100 W, not 250 W\\): it stays owed in the flag, run off again$`, 'm'));
  assert.deepEqual({ mode: m.json().mode, prev: m.json().prevGpuLimitW }, { mode: 'normal', prev: 250 });
  assert.equal(m.run(['off'], NOT_ELEVATED).status, 0);
  assert.deepEqual({ prev: m.json().prevGpuLimitW, gpu: m.gpu() }, { prev: undefined, gpu: 250 });
  const n = machine(t, { gpu: [250, 100, 250] });
  n.registerGpu(100, 250);
  const r = n.run(['eco'], { ...NOT_ELEVATED, FAKE_PM_RUNFAIL: '1' });
  assert.match(r.out, new RegExp(`^note: gpu cap not set \\(the task ${CAP_TASK} could not be started: `, 'm'));
  assert.equal('prevGpuLimitW' in n.json(), false);
});

test('a repeated eco re-applies the cap when the live limit drifted above it; no drift = one read, no set', (t) => {
  const m = machine(t, { gpu: [250, 100, 250] });
  assert.equal(m.run(['eco', '-Source', 'station']).status, 0);
  fs.writeFileSync(path.join(m.sim, 'gpu.txt'), '250,100,250'); // a driver re-initialisation put the default back
  const again = m.run(['eco', '-Source', 'station']);
  assert.equal(again.status, 0, again.out);
  assert.deepEqual(again.out.split('\n').slice(1), ['note: gpu cap re-applied: the limit read 250 W, now 100 W']);
  assert.deepEqual({ gpu: m.gpu(), prev: m.json().prevGpuLimitW, cap: m.json().gpuCapW }, { gpu: 100, prev: 250, cap: 100 });
  const before = m.gpuCalls().length;
  assert.equal(m.run(['eco', '-Source', 'station']).out.split('\n').length, 1);
  assert.deepEqual(m.gpuCalls().slice(before), [LIVE_QUERY]);
  // the user's eco, and a caller that is not elevated (the cap task)
  const n = machine(t, { gpu: [250, 100, 250] });
  n.registerGpu(100, 250);
  assert.equal(n.run(['eco'], NOT_ELEVATED).status, 0);
  fs.writeFileSync(path.join(n.sim, 'gpu.txt'), '250,100,250');
  const manual = n.run(['eco', '-For', '60'], NOT_ELEVATED);
  assert.ok(manual.out.split('\n').includes('note: gpu cap re-applied: the limit read 250 W, now 100 W'), manual.out);
  assert.deepEqual({ gpu: n.gpu(), prev: n.json().prevGpuLimitW, runs: n.calls().filter((c) => c.includes('/run')).length }, { gpu: 100, prev: 250, runs: 2 });
});

test('the station\'s battery reading re-applies a drifted cap while eco is on: one read per call, a set only above the cap; never outside eco', (t) => {
  const m = machine(t, { gpu: [250, 100, 250] });
  assert.equal(m.run(['eco', '-Source', 'station']).status, 0);
  const reading = ['battery', '-Soc', '60', '-Level', 'ok', '-Mains', 'off', '-HibernateAt', '5'];
  let before = m.gpuCalls().length;
  assert.deepEqual(m.run(reading), { status: 0, out: 'battery: 60 % (ok, mains off)', err: '' });
  assert.deepEqual(m.gpuCalls().slice(before), [LIVE_QUERY]);
  fs.writeFileSync(path.join(m.sim, 'gpu.txt'), '250,100,250'); // a driver re-initialisation put the default back
  before = m.gpuCalls().length;
  assert.deepEqual(m.run(reading), { status: 0, out: 'battery: 60 % (ok, mains off)\nnote: gpu cap re-applied: the limit read 250 W, now 100 W', err: '' });
  assert.deepEqual({ gpu: m.gpu(), calls: m.gpuCalls().slice(before), prev: m.json().prevGpuLimitW, soc: m.json().battery.soc }, { gpu: 100, calls: [LIVE_QUERY, 'nvidia-smi -pl 100'], prev: 250, soc: 60 });
  // not elevated: the cap task
  const n = machine(t, { gpu: [250, 100, 250] });
  n.registerGpu(100, 250);
  assert.equal(n.run(['eco', '-Source', 'station'], NOT_ELEVATED).status, 0);
  fs.writeFileSync(path.join(n.sim, 'gpu.txt'), '250,100,250');
  assert.ok(n.run(reading, NOT_ELEVATED).out.split('\n').includes('note: gpu cap re-applied: the limit read 250 W, now 100 W'));
  assert.deepEqual({ gpu: n.gpu(), runs: n.calls().filter((c) => c.includes(`/run /tn ${CAP_TASK}`)).length }, { gpu: 100, runs: 2 });
  // an emergency over the eco keeps the cap: the reading re-applies it too
  const e = machine(t, { gpu: [250, 100, 250] });
  assert.equal(e.run(['eco', '-Source', 'station']).status, 0);
  assert.equal(e.run(['on', '-Source', 'station']).status, 0);
  fs.writeFileSync(path.join(e.sim, 'gpu.txt'), '250,100,250');
  assert.deepEqual(e.run(reading).out.split('\n'), ['battery: 60 % (ok, mains off)', 'note: gpu cap re-applied: the limit read 250 W, now 100 W']);
  assert.deepEqual({ gpu: e.gpu(), mode: e.json().mode }, { gpu: 100, mode: 'emergency' });
  // a normal flag still owing a restore (it failed) is never re-capped
  const f = machine(t, { gpu: [250, 100, 250] });
  fs.writeFileSync(f.flag, '{ "mode": "normal", "since": "2026-10-02T22:20:00Z", "prevGpuLimitW": 250, "gpuCapW": 100 }\n');
  assert.deepEqual(f.run(reading), { status: 0, out: 'battery: 60 % (ok, mains off)', err: '' });
  assert.deepEqual({ gpu: f.gpu(), calls: f.gpuCalls() }, { gpu: 250, calls: [] });
  // no eco (normal, emergency without a cap): the reading reads nothing from the GPU
  const o = machine(t, { gpu: [250, 100, 250] });
  assert.equal(o.run(reading).status, 0);
  assert.equal(o.run(['on']).status, 0);
  assert.equal(o.run(reading).status, 0);
  assert.deepEqual(o.gpuCalls(), []);
});

test('install registers from the XML text, never through a file; under a test it registers nothing', (t) => {
  const src = fs.readFileSync(CLI, 'utf8');
  const install = src.slice(src.indexOf("if ($Action -ceq 'gpu-task')"), src.indexOf("if ($Action -ceq 'end')"));
  assert.match(install, /Invoke-RealTool 'schtasks\.exe' @\('\/delete', '\/tn', \$name, '\/f'\)\s*\n\s*\$r = Register-GpuTask \$name \(New-GpuTaskXml \$name \$watts\)/); // a squatted name goes first
  assert.doesNotMatch(install, /WriteAllText|GetTempPath|\/xml', \$|'\/create'/);
  const dir = fs.mkdtempSync(path.join(TMP, 'power-mode-lib-'));
  t.after(() => fs.rmSync(dir, { recursive: true, force: true }));
  const file = path.join(dir, 'probe.ps1');
  fs.writeFileSync(file, `. '${path.join(__dirname, 'power-mode-flag.ps1')}'\nRegister-GpuTask 'BluettiWatcherEcoGpuTestNever' (New-GpuTaskXml 'BluettiWatcherEcoGpuTestNever' 100)\n'not stopped'\n`);
  const r = spawnSync('pwsh', ['-NoProfile', '-NonInteractive', '-File', file], { encoding: 'utf8', windowsHide: true, env: { ...process.env, POWER_MODE_TEST: '1' } });
  assert.equal(r.status, 2, r.stdout + r.stderr);
  assert.equal(r.stdout.trim(), 'power mode: COULD NOT RUN: POWER_MODE_TEST is set: a test never registers a task');
});

test('status names the stuck state: the card at its minimum with nothing recorded to restore (eco or normal), never a guess at a set', (t) => {
  const stuck = 'gpu: at minimum 100 W with nothing recorded to restore - run `nvidia-smi -pl 250` elevated';
  for (const text of [ecoFlag(Date.now() + 3600000, BALANCED), '{ "mode": "normal", "since": "2026-10-02T22:20:00Z" }\n', null]) {
    const m = machine(t, { gpu: [100, 100, 250] });
    if (text) fs.writeFileSync(m.flag, text);
    assert.ok(m.run(['status']).out.split('\n').includes(stuck), String(text));
    assert.deepEqual({ gpu: m.gpu(), sets: m.gpuCalls().filter((c) => c.includes('-pl')) }, { gpu: 100, sets: [] });
  }
  for (const [gpu, text] of [[[200, 100, 250], null], [[100, 100, 250], ecoFlag(Date.now() + 3600000, BALANCED).replace(' }\n', OWED(250, 100))], [[100, 100, 250], '{ "mode": "emergency", "since": "2026-10-02T22:20:00Z" }\n']]) {
    const m = machine(t, { gpu });
    if (text) fs.writeFileSync(m.flag, text);
    assert.doesNotMatch(m.run(['status']).out, /at minimum/);
  }
});

test('status reads the live GPU limit beside the stored cap and flags a limit something else changed', (t) => {
  const m = machine(t, { gpu: [250, 100, 250] });
  assert.equal(m.run(['eco']).status, 0);
  let lines = m.run(['status']).out.split('\n');
  assert.ok(lines.includes('gpu cap: 100 W (returns to 250 W)') && lines.includes('gpu live: 100 W'), lines.join('|'));
  fs.writeFileSync(path.join(m.sim, 'gpu.txt'), '250,100,250');
  lines = m.run(['status']).out.split('\n');
  assert.ok(lines.includes('gpu live: 250 W - MISMATCH, not the cap 100 W (something else set it)'), lines.join('|'));
  assert.equal(m.json().prevGpuLimitW, 250); // status never writes
  assert.ok(m.run(['status'], { FAKE_GPU_QUERYFAIL: '1' }).out.split('\n').includes('gpu live: no answer from nvidia-smi'));
});

test('POWER_MODE_TEST: a call that names no stand-in stops the script, exit 2, the real tool is never started', (t) => {
  const m = machine(t, { gpu: [250, 100, 250] });
  fs.writeFileSync(m.flag, ecoFlag(Date.now() + 3600000, BALANCED).replace(' }\n', OWED(250, 100)));
  const mutex = uniqueMutex();
  const r = cli(['status', '-Path', m.flag], undefined, { POWER_MODE_POWERCFG: '', POWER_MODE_SCHTASKS: '', POWER_MODE_NVIDIASMI: '', POWER_MODE_MUTEX: mutex });
  assert.equal(r.status, 2, r.out);
  assert.match(r.out, /^power mode: COULD NOT RUN: POWER_MODE_TEST is set and no stand-in names powercfg\.exe \(a test never starts the real one\)$/m);
  assert.equal(probeMutex(mutex), 'free');
});

test('a tool that hangs is killed after POWER_MODE_TOOL_TIMEOUT_MS: a note, exit 0, the flag lock released; a child-process stand-in works as the tool', async (t) => {
  const m = machine(t, { gpu: [250, 100, 250] });
  const cmd = path.join(m.sim, 'fake-nvidia-smi.cmd');
  fs.writeFileSync(cmd, ['@echo off', 'if "%FAKE_SLEEP%"=="1" ping -n 11 127.0.0.1 >nul', 'if "%1"=="-pl" (echo %2>"%FAKE_PM_DIR%\\cmd-pl.txt" & exit /b 0)', 'echo 250.00, 100.00, 250.00', ''].join('\r\n'));
  const mutex = uniqueMutex();
  const ok = m.run(['eco'], { POWER_MODE_NVIDIASMI: cmd, POWER_MODE_MUTEX: mutex });
  assert.equal(ok.status, 0, ok.out);
  assert.ok(ok.out.split('\n').includes('gpu cap: 100 W (returns to 250 W)'), ok.out);
  assert.equal(fs.readFileSync(path.join(m.sim, 'cmd-pl.txt'), 'utf8').trim(), '100');
  const n = machine(t, { gpu: [250, 100, 250] });
  const started = Date.now();
  const r = n.run(['eco'], { POWER_MODE_NVIDIASMI: cmd, FAKE_SLEEP: '1', POWER_MODE_TOOL_TIMEOUT_MS: '500', POWER_MODE_MUTEX: mutex });
  const took = Date.now() - started;
  assert.equal(r.status, 0, r.out);
  assert.match(r.out, /^note: gpu cap not set \(fake-nvidia-smi\.cmd timed out after 500 ms \(stopped\)\)$/m);
  assert.ok(took < 8000, `took ${took} ms`);
  assert.equal(n.json().mode, 'eco');
  assert.equal(probeMutex(mutex), 'free');
});

// The library functions, dot-sourced in pwsh: what install registers, and how a registered task is recognised.
function lib(t, code) {
  const dir = fs.mkdtempSync(path.join(TMP, 'power-mode-lib-'));
  t.after(() => fs.rmSync(dir, { recursive: true, force: true }));
  const file = path.join(dir, 'probe.ps1');
  fs.writeFileSync(file, `. '${path.join(__dirname, 'power-mode-flag.ps1')}'\n${code}\n`);
  const r = spawnSync('pwsh', ['-NoProfile', '-NonInteractive', '-File', file], { encoding: 'utf8', windowsHide: true, env: { ...process.env, POWER_MODE_TEST: '1' } });
  assert.equal(r.status, 0, r.stdout + r.stderr);
  return r.stdout.replace(/\r\n/g, '\n').trim();
}

test('the GPU task as registered: System32 conhost hosting System32 nvidia-smi -pl <fixed watts>, the user at the highest run level, on demand, no user-writable path', (t) => {
  const xml = lib(t, "New-GpuTaskXml 'BluettiWatcherEcoGpuCap' 100");
  const sys = lib(t, "[Environment]::GetFolderPath('System')");
  const field = (name) => (xml.match(new RegExp(`<${name}>([^<]*)</${name}>`)) || [])[1];
  assert.equal(field('Command').toLowerCase(), path.join(sys, 'conhost.exe').toLowerCase());
  assert.equal(field('Arguments'), `--headless &quot;${path.join(sys, 'nvidia-smi.exe')}&quot; -pl 100`);
  assert.equal((xml.match(/<Exec>/g) || []).length, 1);
  assert.match(xml, /<UserId>S-1-5-21-[\d-]+<\/UserId><LogonType>InteractiveToken<\/LogonType><RunLevel>HighestAvailable<\/RunLevel>/);
  assert.equal(field('MultipleInstancesPolicy'), 'IgnoreNew');
  assert.equal(field('ExecutionTimeLimit'), 'PT1M');
  assert.equal(field('Hidden'), 'true');
  assert.doesNotMatch(xml, /<Triggers|<WorkingDirectory/i);
  const actions = xml.slice(xml.indexOf('<Actions'));
  assert.doesNotMatch(actions, /power-mode|\.ps1|Users\\|%/i);
  for (const p of actions.match(/[A-Za-z]:\\[^&<"]*/g)) assert.ok(p.toLowerCase().startsWith(sys.toLowerCase() + '\\'), p);
  assert.equal(lib(t, "$x = New-GpuTaskXml 'BluettiWatcherEcoGpuRestore' 250; $r = ConvertFrom-GpuTaskXml $x; \"$($r.state) $($r.watts)\""), 'ok 250');
  assert.equal(lib(t, "$x = (New-GpuTaskXml 'BluettiWatcherEcoGpuCap' 100).Replace('HighestAvailable', 'LeastPrivilege'); (ConvertFrom-GpuTaskXml $x).state"), 'foreign');
  assert.match(lib(t, 'Get-GpuTaskSddl'), /^D:P\(A;;FA;;;BA\)\(A;;FA;;;SY\)\(A;;FRFX;;;S-1-5-21-[\d-]+\)$/);
  assert.match(fs.readFileSync(path.join(__dirname, 'power-mode-flag.ps1'), 'utf8'), /\.RegisterTask\(\$Name, \$Xml, 6, \$null, \$null, 3, \(Get-GpuTaskSddl\)\)/);
  assert.equal(lib(t, "$x = (New-GpuTaskXml 'BluettiWatcherEcoGpuCap' 100).Replace('&quot;', '\"'); $r = ConvertFrom-GpuTaskXml $x; \"$($r.state) $($r.watts)\""), 'ok 100');
});

test('install takes the restore limit from the card, or -RestoreWatts inside its min..max, nothing else', (t) => {
  const out = lib(t, [
    "$l = @{ min = 100.0; max = 250.0; default = 250.0 }",
    "foreach ($w in '', '200', '100', '250', '99', '251', 'abc', '300.5', '-5', '0300', '1e3') { $r = Resolve-GpuTaskWatts $l $w; if ($r.error) { \"$w=error\" } else { \"$w=$($r.cap)/$($r.restore)\" } }",
  ].join('\n'));
  assert.deepEqual(out.split('\n'), ['=100/250', '200=100/200', '100=100/100', '250=100/250', '99=error', '251=error', 'abc=error', '300.5=error', '-5=error', '0300=error', '1e3=error']);
});

const RUNNER_IS_ADMIN = spawnSync('pwsh', ['-NoProfile', '-NonInteractive', '-Command',
  '([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)'],
{ encoding: 'utf8', windowsHide: true }).stdout.trim() === 'True';

test('gpu-task install and remove: refused without real elevation, and refused while any test seam is set; nothing is registered or called', (t) => {
  const m = machine(t, { gpu: [250, 100, 250] });
  for (const verb of ['install', 'remove']) {
    assert.deepEqual(m.run(['gpu-task', verb], NOT_ELEVATED), { status: 2, out: `power mode: COULD NOT RUN: gpu-task ${verb} needs an elevated shell`, err: '' });
    const r = m.run(['gpu-task', verb, '-RestoreWatts', '200']); // POWER_MODE_ELEVATED=1 claims elevation: never taken
    assert.equal(r.status, 2, r.out);
    assert.equal(r.out, RUNNER_IS_ADMIN
      ? `power mode: COULD NOT RUN: gpu-task ${verb} takes no test seam, unset POWER_MODE_ELEVATED, POWER_MODE_NVIDIASMI, POWER_MODE_POWERCFG, POWER_MODE_SCHTASKS, POWER_MODE_TEST, POWER_MODE_TZ`
      : `power mode: COULD NOT RUN: gpu-task ${verb} needs an elevated shell`);
  }
  assert.deepEqual({ calls: m.calls(), files: fs.readdirSync(m.sim).filter((f) => f.startsWith('task-')) }, { calls: [], files: [] });
  assert.equal(m.run(['gpu-task']).status, 2);
});

test('gpu-task status lists both tasks with their wattage and the live limit: 0 both registered, 1 not', (t) => {
  const m = machine(t, { gpu: [100, 100, 250] });
  assert.deepEqual(m.run(['gpu-task', 'status'], NOT_ELEVATED), { status: 1, err: '',
    out: `gpu helper: ${CAP_TASK} not registered (an elevated shell: power-mode.ps1 gpu-task install)\ngpu helper: ${RESTORE_TASK} not registered (an elevated shell: power-mode.ps1 gpu-task install)\ngpu live: 100 W` });
  m.registerGpu(100, 250);
  assert.deepEqual(m.run(['gpu-task', 'status'], NOT_ELEVATED), { status: 0, err: '', out: `gpu helper: ${CAP_TASK} sets 100 W\ngpu helper: ${RESTORE_TASK} sets 250 W\ngpu live: 100 W` });
});

// ---- the configured flag's own folder: created with its own ACL, checked (never changed) before every write and read.
// The library dot-sourced with the configured path pointed into a temp world through a script-scope seam.
const LIB = path.join(__dirname, 'power-mode-flag.ps1');
function flagLib(t, body, seams = true, extraEnv = {}) {
  // Its own world: the ACL cases leave folders the test user cannot delete until their ACL is reset.
  const w = { dir: fs.mkdtempSync(path.join(TMP, 'power-mode-cli-')) };
  t.after(() => {
    spawnSync('icacls', [w.dir, '/reset', '/T', '/Q'], { windowsHide: true });
    fs.rmSync(w.dir, { recursive: true, force: true });
  });
  const fresh = path.join(w.dir, 'pd', 'BluettiWatcher', 'power-mode.json');
  fs.mkdirSync(path.dirname(path.dirname(fresh)), { recursive: true }); // %ProgramData% itself exists
  const seam = seams ? `$script:PowerModeDefaultFlagPathTest = '${fresh}'; ` : '';
  const run = (code = body) => {
    const env = { ...process.env, POWER_MODE_TEST: '1', ...extraEnv };
    const r = spawnSync('pwsh', ['-NoProfile', '-NonInteractive', '-Command', `$ErrorActionPreference = 'Stop'; . '${LIB}'; ${seam}${code}`], { encoding: 'utf8', windowsHide: true, env });
    assert.equal(r.error, undefined, String(r.error));
    return { status: r.status, out: r.stdout.replace(/\r\n/g, '\n').trimEnd(), err: r.stderr };
  };
  return { fresh, run };
}
const flagText = (mode, extra = '') => `{ "mode": "${mode}", "since": "2026-10-09T10:00:00+03:00"${extra} }\n`;
const NAMES = "$n = Get-PowerModeNames; $n.flag; $n.mutex; $n.taskPrefix; $n.endTask; $n.gpuCapTask; $n.gpuRestoreTask; Get-PowerModeFlagPath; [Environment]::GetFolderPath('CommonApplicationData')";

test('the neutral defaults: %ProgramData%\\BluettiWatcher\\power-mode.json, Local\\BluettiWatcherPowerMode, the BluettiWatcherEco tasks', (t) => {
  const r = flagLib(t, NAMES, false).run();
  assert.equal(r.status, 0, r.err);
  const lines = r.out.split('\n');
  const pd = lines.pop();
  assert.deepEqual(lines, [path.join(pd, 'BluettiWatcher', 'power-mode.json'), 'Local\\BluettiWatcherPowerMode', 'BluettiWatcherEco',
    'BluettiWatcherEcoEnd', 'BluettiWatcherEcoGpuCap', 'BluettiWatcherEcoGpuRestore', path.join(pd, 'BluettiWatcher', 'power-mode.json')]);
});

// An install with its own names sets the four variables: every name, path and command is then exactly that install's.
const COMPAT = { POWER_MODE_FILE: 'C:\\ProgramData\\MyPowerMode\\power-mode.json', POWER_MODE_MUTEX: 'Local\\MyPowerModeFlag', POWER_MODE_TASK_PREFIX: 'MyEcoMode', POWER_MODE_EXTRA_READERS: 'mypmreader' };
const NAME_VARS = ['POWER_MODE_FILE', 'POWER_MODE_MUTEX', 'POWER_MODE_TASK_PREFIX', 'POWER_MODE_EXTRA_READERS'];
test('names from the environment: POWER_MODE_FILE / _MUTEX / _TASK_PREFIX / _EXTRA_READERS set to an install\'s values give exactly its names', (t) => {
  // The lock probe (MUTEX_PROBE) under the configured name, for a process the library starts while it holds the lock.
  const probe = MUTEX_PROBE.replace('Local\\BluettiWatcherPowerMode', 'Local\\MyPowerModeFlag');
  assert.notEqual(probe, MUTEX_PROBE);
  const l = flagLib(t, NAMES, false, { ...COMPAT, PROBE: probe });
  const r = l.run();
  assert.equal(r.status, 0, r.err);
  assert.deepEqual(r.out.split('\n').slice(0, 7), ['C:\\ProgramData\\MyPowerMode\\power-mode.json', 'Local\\MyPowerModeFlag', 'MyEcoMode',
    'MyEcoModeEnd', 'MyEcoModeGpuCap', 'MyEcoModeGpuRestore', 'C:\\ProgramData\\MyPowerMode\\power-mode.json']);
  // The lock: held under the configured name while a writer holds it, free after (seen from another process).
  const lock = l.run("$m = Enter-PowerModeFlagLock ''; & pwsh -NoProfile -NonInteractive -Command $env:PROBE; Exit-PowerModeFlagLock $m; & pwsh -NoProfile -NonInteractive -Command $env:PROBE");
  assert.equal(lock.status, 0, lock.err);
  assert.equal(lock.out, 'held\nfree');
  // The GPU task as gpu-task install would register it under the configured prefix.
  assert.match(l.run('New-GpuTaskXml $script:GpuCapTask 100').out, /<Description>power-mode\.ps1: GPU power limit 100 W \(MyEcoModeGpuCap\)<\/Description>/);
  // The switch and the end script against a simulated machine (-Path keeps the configured flag untouched).
  const m = machine(t);
  assert.equal(m.run(['eco'], COMPAT).status, 0);
  assert.match(m.calls().find((c) => c.startsWith('schtasks /create')), /^schtasks \/create \/tn MyEcoModeEnd \/xml \S+ \/f$/);
  assert.ok(m.task() === null && fs.existsSync(path.join(m.sim, 'task-MyEcoModeEnd.xml')));
  // The task's command line carries every name: the Task Scheduler passes no environment (cli() drops POWER_MODE_FILE).
  const endXml = fs.readFileSync(path.join(m.sim, 'task-MyEcoModeEnd.xml'), 'utf16le');
  assert.ok(endXml.includes(
    `-File &quot;${END}&quot; -Path &quot;${m.flag}&quot; -FlagFile &quot;${path.join(process.env.ProgramData, 'BluettiWatcher', 'power-mode.json')}&quot; -Mutex Local\\MyPowerModeFlag -TaskPrefix MyEcoMode -ExtraReaders &quot;mypmreader&quot; -TaskUntil ${m.json().until}</Arguments>`), endXml);
  assert.equal(m.run(['status'], COMPAT).out.split('\n').pop(), 'end task: MyEcoModeEnd present');
  const untilMs = Date.now() - 60000;
  fs.writeFileSync(m.flag, ecoFlag(untilMs, BALANCED));
  const end = end51(m, ['-TaskUntil', iso(untilMs)], COMPAT);
  assert.equal(end.status, 0, end.out + end.err);
  assert.ok(m.calls().includes('schtasks /delete /tn MyEcoModeEnd /f'), m.calls().join('\n'));
});

test('a POWER_MODE_TASK_PREFIX that is not a plain name: exit 2, nothing written', (t) => {
  const w = world(t);
  for (const prefix of ['a\\b', 'x y', '"q']) {
    const r = cli(['on', '-Path', w.flag], undefined, { POWER_MODE_TASK_PREFIX: prefix });
    assert.equal(r.status, 2, r.out);
    assert.match(r.out, /^power mode: COULD NOT RUN: POWER_MODE_TASK_PREFIX /);
  }
  assert.deepEqual(fs.readdirSync(w.dir), []);
});

test('a caller under Set-StrictMode with no seam set gets the default path, no unset-variable error', (t) => {
  const l = flagLib(t, "Set-StrictMode -Version Latest; Get-PowerModeDefaultFlagPath; [Environment]::GetFolderPath('CommonApplicationData'); Get-PowerModeMeSid; [void](Get-PowerModeExtraReaderSids)", false);
  const r = l.run();
  assert.equal(r.status, 0, r.err);
  const [def, pd, me] = r.out.split('\n');
  assert.match(me, /^S-1-5-21-/);
  assert.equal(def, path.join(pd, 'BluettiWatcher', 'power-mode.json'));
});

test('POWER_MODE_EXTRA_READERS: account names, comma-separated; one that does not resolve is left out', (t) => {
  const me = process.env.USERNAME;
  const l = flagLib(t, "(Get-PowerModeExtraReaderSids) -join ','; [Security.Principal.WindowsIdentity]::GetCurrent().User.Value", true,
    { POWER_MODE_EXTRA_READERS: ` no-such-account-bw-test , ${me} ,` });
  const [sids, mine] = l.run().out.split('\n');
  assert.equal(sids, mine);
  const none = flagLib(t, "@(Get-PowerModeExtraReaderSids).Count").run();
  assert.equal(none.out, '0');
});

test('the configured folder is created with its own ACL (the user Modify, SYSTEM and Administrators Full, nothing inherited)', (t) => {
  const body = 'Initialize-PowerModeFlagDir (Get-PowerModeFlagPath); $acl = Get-Acl -LiteralPath (Split-Path (Get-PowerModeFlagPath)); "protected=$($acl.AreAccessRulesProtected)"; ' +
    "$me = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value; " +
    "$acl.Access | ForEach-Object { $s = $_.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value; if ($s -ceq $me) { $s = 'me' }; \"$s|$($_.FileSystemRights)|$($_.InheritanceFlags)|$($_.IsInherited)\" } | Sort-Object";
  const l = flagLib(t, body);
  assert.deepEqual(l.run(), { status: 0, err: '', out: [
    'protected=True',
    'me|Modify, Synchronize|ContainerInherit, ObjectInherit|False',
    'S-1-5-18|FullControl|ContainerInherit, ObjectInherit|False',
    'S-1-5-32-544|FullControl|ContainerInherit, ObjectInherit|False',
  ].join('\n') });
});

test('a missing folder that is not the configured one is never created', (t) => {
  const l = flagLib(t, "Initialize-PowerModeFlagDir (Join-Path (Split-Path (Split-Path (Get-PowerModeFlagPath))) 'other\\power-mode.json')");
  const r = l.run();
  assert.notEqual(r.status, 0);
  assert.match(r.err + r.out, /directory missing/);
  assert.equal(fs.existsSync(path.join(path.dirname(path.dirname(l.fresh)), 'other')), false);
});

// Builds the folder's ACL by hand: protected, the user's rights, plus extra (sid, rights) grants. 'inherit' = a plain mkdir.
const ACL_PS = "function Set-TestAcl([string] $Dir, [string] $OwnerRights, [string[]] $Extra) { " +
  "$s = New-Object Security.AccessControl.DirectorySecurity; $s.SetAccessRuleProtection($true, $false); " +
  "$f = [Security.AccessControl.InheritanceFlags] 'ContainerInherit, ObjectInherit'; " +
  "$add = { param($sid, $r) $s.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier $sid), [Security.AccessControl.FileSystemRights] $r, $f, 'None', 'Allow'))) }; " +
  "& $add 'S-1-5-18' 'FullControl'; & $add 'S-1-5-32-544' 'FullControl'; & $add ([Security.Principal.WindowsIdentity]::GetCurrent().User.Value) $OwnerRights; " +
  "foreach ($e in $Extra) { if ($e) { $sid, $r = $e -split '=', 2; & $add $sid $r } }; Set-Acl -LiteralPath $Dir -AclObject $s }; ";
function aclCase(t, setup) {
  const l = flagLib(t, `${ACL_PS}$d = Split-Path (Get-PowerModeFlagPath); ${setup}; try { Initialize-PowerModeFlagDir (Get-PowerModeFlagPath); 'written' } catch { "refused: $($_.Exception.Message)" }`);
  fs.mkdirSync(path.dirname(l.fresh), { recursive: true });
  return l.run();
}

test('the folder Initialize created passes its own check', (t) => {
  const l = flagLib(t, "Initialize-PowerModeFlagDir (Get-PowerModeFlagPath); Initialize-PowerModeFlagDir (Get-PowerModeFlagPath); 'written'");
  assert.deepEqual(l.run(), { status: 0, out: 'written', err: '' });
});

test('a folder that inherits its ACL refuses the write and keeps its ACL', (t) => {
  const r = aclCase(t, '$null');
  assert.equal(r.status, 0, r.err);
  assert.match(r.out, /^refused: the flag folder .* is not the user's: it inherits its ACL \(inheritance must be off\)/);
});

test('a folder where Users hold read refuses the write', (t) => {
  const r = aclCase(t, "Set-TestAcl $d 'Modify' @('S-1-5-32-545=ReadAndExecute')");
  assert.match(r.out, /^refused: .*S-1-5-32-545 has rights there/);
});

test('an extra reader may read, never write', (t) => {
  const read = aclCase(t, "$script:PowerModeExtraReaderSidsTest = @('S-1-5-32-545'); Set-TestAcl $d 'Modify' @('S-1-5-32-545=ReadAndExecute')");
  assert.equal(read.out, 'written');
  const write = aclCase(t, "$script:PowerModeExtraReaderSidsTest = @('S-1-5-32-545'); Set-TestAcl $d 'Modify' @('S-1-5-32-545=Modify')");
  assert.match(write.out, /^refused: .*the extra reader S-1-5-32-545 may write there/);
});

test('a folder where the user holds only read refuses the write', (t) => {
  const r = aclCase(t, "Set-TestAcl $d 'ReadAndExecute' @()");
  assert.match(r.out, /^refused: .*holds no Modify there/);
});

test('a configured flag that cannot be told present (access denied) is untrusted', (t) => {
  // A deny entry for the running user on the folder (deny wins even elevated); removed before the temp world goes.
  const l = flagLib(t, "$f = Read-PowerModeFlag (Get-PowerModeFlagPath); if ($null -eq $f) { 'untrusted' } else { $f.mode }");
  const dir = path.dirname(l.fresh);
  assert.equal(l.run('Initialize-PowerModeFlagDir (Get-PowerModeFlagPath)').status, 0);
  fs.writeFileSync(l.fresh, flagText('normal'));
  assert.deepEqual(l.run(), { status: 0, out: 'normal', err: '' }); // the positive control: readable before the deny
  const me = spawnSync('pwsh', ['-NoProfile', '-Command', '[Security.Principal.WindowsIdentity]::GetCurrent().User.Value'], { encoding: 'utf8', windowsHide: true }).stdout.trim();
  const icacls = (args) => spawnSync('icacls', [dir, ...args], { encoding: 'utf8', windowsHide: true });
  assert.equal(icacls(['/deny', `*${me}:(OI)(CI)(RD,RA,REA,X)`]).status, 0);
  let r;
  try { r = l.run(); } finally { icacls(['/remove:d', `*${me}`]); } // before the world's own cleanup, which the deny would stop
  assert.deepEqual(r, { status: 0, out: 'untrusted', err: '' });
});

test('a folder owned by someone else refuses the write, however good its DACL', (t) => {
  // The folder's owner set to the real user (allowed without privilege), then the seam makes the running user someone else.
  const r = aclCase(t, "Set-TestAcl $d 'Modify' @(); $o = Get-Acl -LiteralPath $d; $o.SetOwner([Security.Principal.WindowsIdentity]::GetCurrent().User); Set-Acl -LiteralPath $d -AclObject $o; $script:PowerModeMeSidTest = 'S-1-5-21-1-2-3-1001'");
  assert.match(r.out, /^refused: .* is owned by S-1-5-21-/);
});

test('a junction in place of the folder refuses the write', (t) => {
  const r = aclCase(t, "$tgt = Join-Path (Split-Path $d) 'target'; New-Item -ItemType Directory $tgt | Out-Null; Set-TestAcl $tgt 'Modify' @(); New-Item -ItemType Junction -Path $d -Target $tgt | Out-Null");
  assert.match(r.out, /^refused: .* is a reparse point/);
});

test('a flag file carrying an extra entry refuses the write, and readers treat it as untrusted', (t) => {
  const extra = "$fa = Get-Acl -LiteralPath (Get-PowerModeFlagPath); $fa.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier 'S-1-5-32-545'), 'Read', 'Allow'))); Set-Acl -LiteralPath (Get-PowerModeFlagPath) -AclObject $fa";
  const l = flagLib(t, "try { Initialize-PowerModeFlagDir (Get-PowerModeFlagPath); 'written' } catch { \"refused: $($_.Exception.Message)\" }; $f = Read-PowerModeFlag (Get-PowerModeFlagPath); if ($null -eq $f) { 'untrusted' } else { $f.mode }");
  assert.equal(l.run('Initialize-PowerModeFlagDir (Get-PowerModeFlagPath)').status, 0);
  fs.writeFileSync(l.fresh, flagText('normal'));
  assert.equal(l.run(extra).status, 0);
  const lines = l.run().out.split('\n');
  assert.match(lines[0], /^refused: .*S-1-5-32-545 has rights there/);
  assert.deepEqual(lines.slice(1), ['untrusted']);
});

test('a reader refuses a configured folder that is not the user\'s (inheritance on): untrusted', (t) => {
  const l = flagLib(t, "$f = Read-PowerModeFlag (Get-PowerModeFlagPath); if ($null -eq $f) { 'untrusted' } else { $f.mode }");
  fs.mkdirSync(path.dirname(l.fresh), { recursive: true }); // a plain mkdir: inherits the temp folder's ACL
  fs.writeFileSync(l.fresh, flagText('normal'));
  assert.deepEqual(l.run(), { status: 0, out: 'untrusted', err: '' });
});

// ---- the end task's command line: the Task Scheduler passes no environment, so every name travels on it ----------
// The switch registers the task with a configured flag in a guarded temp folder that an extra reader (Users, read) may
// read; the end script then runs that exact command line under 5.1 with none of the four names in its environment.
const taskArgv = (xml) => {
  const args = /<Arguments>([^<]*)<\/Arguments>/.exec(xml)[1].replace(/&quot;/g, '"');
  const rest = args.slice(args.indexOf(`-File "${END}" `) + `-File "${END}" `.length);
  return [...rest.matchAll(/"([^"]*)"|(\S+)/g)].map((x) => (x[1] !== undefined ? x[1] : x[2]));
};
test('the end task under 5.1 with no POWER_MODE_* names in its environment takes them from its command line', (t) => {
  for (const k of NAME_VARS) assert.equal(k in process.env, false, k);
  const m = machine(t, { active: BALANCED, gpu: [250, 100, 250] });
  const guarded = path.join(m.dir, 'guarded');
  fs.mkdirSync(guarded);
  const acl = spawnSync('pwsh', ['-NoProfile', '-NonInteractive', '-Command', `${ACL_PS}Set-TestAcl $env:GUARDED 'Modify' @('S-1-5-32-545=ReadAndExecute')`],
    { encoding: 'utf8', windowsHide: true, env: { ...process.env, GUARDED: guarded } });
  assert.equal(acl.status, 0, acl.stdout + acl.stderr);
  const flag = path.join(guarded, 'power-mode.json');
  fs.writeFileSync(path.join(guarded, 'power-mode.config.json'), JSON.stringify({ returnPlan: 'balanced' }));
  const names = { POWER_MODE_MUTEX: 'Local\\MyPowerModeFlag', POWER_MODE_TASK_PREFIX: 'MyEcoMode', POWER_MODE_EXTRA_READERS: 'mypmreader, Users' };
  // The switch, configured: the guarded folder passes its check only with the extra reader named.
  const eco = cli(['eco'], flag, { ...m.env, ...names });
  assert.equal(eco.status, 0, eco.out);
  assert.equal(cli(['status'], flag, { ...m.env, ...names, POWER_MODE_EXTRA_READERS: '' }).status, 2);
  const argv = taskArgv(fs.readFileSync(path.join(m.sim, 'task-MyEcoModeEnd.xml'), 'utf16le'));
  assert.deepEqual(argv.slice(0, 10), ['-Path', flag, '-FlagFile', flag, '-Mutex', 'Local\\MyPowerModeFlag', '-TaskPrefix', 'MyEcoMode', '-ExtraReaders', 'mypmreader,Users']);
  // The eco is due; the end task's own and the default-named tasks are both there, the GPU restore task under the prefix.
  const j = JSON.parse(fs.readFileSync(flag, 'utf8'));
  assert.equal(j.prevGpuLimitW, 250);
  const untilMs = Date.now() - 60000;
  fs.writeFileSync(flag, JSON.stringify({ ...j, since: iso(untilMs - 3600000), until: iso(untilMs) }));
  argv[argv.indexOf('-TaskUntil') + 1] = iso(untilMs);
  fs.writeFileSync(path.join(m.sim, 'task-BluettiWatcherEcoEnd.xml'), 'not this one');
  fs.writeFileSync(path.join(m.sim, 'task-MyEcoModeGpuRestore.xml'), gpuTaskXml(250));
  fs.writeFileSync(path.join(m.sim, 'calls.log'), '');
  const env = { ...m.env, POWER_MODE_ELEVATED: '0', FAKE_PM_MUTEX: 'Local\\MyPowerModeFlag' };
  // Control: without the extra reader the configured flag's folder is not the user's - the flag is untrusted, nothing changes.
  const noReaders = argv.filter((a, i) => a !== '-ExtraReaders' && argv[i - 1] !== '-ExtraReaders');
  const refused = end51(m, [], env, noReaders);
  assert.equal(refused.status, 2, refused.out + refused.err);
  assert.match(refused.out, /^power mode: COULD NOT READ /);
  // A reader the user's saved environment names is not the task's: with -FlagFile given and no -ExtraReaders, none are trusted.
  const stray = end51(m, [], { ...env, POWER_MODE_EXTRA_READERS: 'Users' }, noReaders);
  assert.equal(stray.status, 2, stray.out + stray.err);
  assert.match(stray.out, /^power mode: COULD NOT READ /);
  const r = end51(m, [], env, argv);
  assert.equal(r.status, 0, r.out + r.err);
  assert.equal(r.out.split('\n')[0], 'power mode: normal (eco ended)');
  assert.match(r.out, /^gpu: power limit back to 250 W$/m);
  const calls = m.calls();
  assert.ok(calls.includes('schtasks /delete /tn MyEcoModeEnd /f'), calls.join('\n'));
  assert.ok(calls.includes('schtasks /run /tn MyEcoModeGpuRestore'), calls.join('\n'));
  assert.ok(!calls.some((c) => c.includes('BluettiWatcherEco')), calls.join('\n'));
  assert.ok(calls.includes('mutex Local\\MyPowerModeFlag held') && !calls.some((c) => / free$/.test(c)), calls.join('\n'));
  assert.equal(fs.existsSync(path.join(m.sim, 'task-MyEcoModeEnd.xml')), false);
  assert.equal(fs.readFileSync(path.join(m.sim, 'task-BluettiWatcherEcoEnd.xml'), 'utf8'), 'not this one');
  assert.equal(m.gpu(), 250);
  assert.equal(JSON.parse(fs.readFileSync(flag, 'utf8')).mode, 'normal');
});

test('the end script refuses a name on its command line that is not plain: exit 2, nothing changed', (t) => {
  const m = machine(t);
  const untilMs = Date.now() - 60000;
  fs.writeFileSync(m.flag, ecoFlag(untilMs, BALANCED));
  fs.writeFileSync(m.taskFile, 'registered');
  for (const bad of [['-Mutex', 'MyPowerModeFlag'], ['-Mutex', 'Local\\a b'], ['-Mutex', 'Local\\a\\b'], ['-TaskPrefix', 'a\\b'], ['-ExtraReaders', 'ok,dom\\user'], ['-ExtraReaders', 'a"b']]) {
    const r = end51(m, [...bad, '-TaskUntil', iso(untilMs)]);
    assert.equal(r.status, 2, `${bad}: ${r.out}${r.err}`);
    assert.match(r.out, /^power mode: COULD NOT RUN: POWER_MODE_(MUTEX|TASK_PREFIX|EXTRA_READERS) '/, bad.join(' '));
  }
  assert.equal(fs.readFileSync(m.flag, 'utf8'), ecoFlag(untilMs, BALANCED));
  assert.equal(fs.readFileSync(m.taskFile, 'utf8'), 'registered');
  assert.deepEqual(m.calls(), []);
});

test('a task prefix or a reader that starts with "-" is refused: under powershell.exe -File it would bind as a parameter name', (t) => {
  const w = world(t);
  const pfx = cli(['on', '-Path', w.flag], undefined, { POWER_MODE_TASK_PREFIX: '-Mutex' });
  assert.equal(pfx.status, 2, pfx.out);
  assert.match(pfx.out, /^power mode: COULD NOT RUN: POWER_MODE_TASK_PREFIX /);
  for (const readers of ['-TaskUntil', 'ok,-x']) {
    const r = cli(['on', '-Path', w.flag], undefined, { POWER_MODE_EXTRA_READERS: readers });
    assert.equal(r.status, 2, r.out);
    assert.match(r.out, /^power mode: COULD NOT RUN: POWER_MODE_EXTRA_READERS /);
  }
  assert.deepEqual(fs.readdirSync(w.dir), []);
});

test('a flag whose local time cannot be formatted: the fallback names the flag\'s own mode (eco, not EMERGENCY)', (t) => {
  const w = world(t);
  const tz = { POWER_MODE_TZ: 'No/Such_Zone' };
  const untilIso = new Date(Date.now() + 3600000).toISOString();
  const sinceIso = new Date(Date.now() - 60000).toISOString();
  fs.writeFileSync(w.flag, JSON.stringify({ mode: 'eco', since: sinceIso, until: untilIso }));
  const st = cli(['status', '-Path', w.flag], undefined, tz);
  const lines = st.out.split('\n');
  assert.equal(lines[0], `power mode: ECO since ${sinceIso} until ${untilIso}`);
  assert.match(lines[1], /^warn: local time unavailable \(/);
  fs.writeFileSync(w.flag, JSON.stringify({ mode: 'normal', since: sinceIso }));
  const nrm = cli(['status', '-Path', w.flag], undefined, tz);
  assert.equal(nrm.out.split('\n')[0], 'power mode: normal');
});