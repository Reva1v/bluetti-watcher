<#
test-install.ps1 - install.ps1's dry run against a temp data directory (LOCALAPPDATA pointed at a temp folder for the
child only): the station MAC it requires, the config it would write, a release zip told from a checkout. Never runs
-Apply; registers nothing, writes nothing outside its own temp directory.

  pwsh -NoProfile -File tools/test-install.ps1

Exit: 0 every case passed · 1 a case failed · 2 did not run.
#>
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$installer = Join-Path $repo 'install.ps1'
if (-not (Test-Path -LiteralPath $installer)) { Write-Output "test-install: COULD NOT RUN: no $installer"; exit 2 }
try {
    $base = Join-Path ([IO.Path]::GetTempPath()) ('bluetti-watcher-install-test-' + [Guid]::NewGuid().ToString('N'))
    $null = New-Item -ItemType Directory -Path $base
}
catch { Write-Output "test-install: COULD NOT RUN: $($_.Exception.Message)"; exit 2 }

$script:failed = 0
$script:passed = 0
$script:n = 0
function Check([string] $name, [bool] $ok, [string] $detail = '') {
    if ($ok) { $script:passed++ } else { $script:failed++; Write-Output "FAIL  $name $detail" }
}
# A free port: the dry run reports a taken one as a problem.
function Get-FreePort { $l = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0); $l.Start(); $p = $l.LocalEndpoint.Port; $l.Stop(); $p }

# The dry run in a child pwsh whose LOCALAPPDATA is a fresh temp folder. Config: $null = none, else its text.
function Invoke-DryRun([string] $Script = $installer, $Config = $null, [string[]] $Extra = @(), [hashtable] $Env = @{}) {
    $script:n++
    $local = Join-Path $base "local$script:n"
    $null = New-Item -ItemType Directory -Path $local
    if ($null -ne $Config) {
        $null = New-Item -ItemType Directory -Path (Join-Path $local 'bluetti-watcher')
        [IO.File]::WriteAllText((Join-Path $local 'bluetti-watcher\config.json'), $Config)
    }
    # Only the data directory: pwsh itself may write its caches under LOCALAPPDATA.
    $data = Join-Path $local 'bluetti-watcher'
    $list = { if (Test-Path -LiteralPath $data) { @($data) + @(Get-ChildItem -LiteralPath $data -Recurse -Force | ForEach-Object FullName) } else { @() } }
    $before = & $list
    $saved = $env:LOCALAPPDATA
    $savedEnv = @{}
    try {
        $env:LOCALAPPDATA = $local
        foreach ($k in $Env.Keys) { $savedEnv[$k] = [Environment]::GetEnvironmentVariable($k); [Environment]::SetEnvironmentVariable($k, $Env[$k]) }
        $out = @(& pwsh -NoProfile -NonInteractive -File $Script -Port (Get-FreePort) @Extra 2>&1 | ForEach-Object { "$_" })
        $code = $LASTEXITCODE
    }
    finally {
        $env:LOCALAPPDATA = $saved
        foreach ($k in $savedEnv.Keys) { [Environment]::SetEnvironmentVariable($k, $savedEnv[$k]) }
    }
    $after = & $list
    @{ Out = $out; Text = ($out -join "`n"); Code = $code; Unchanged = (($before -join '|') -eq ($after -join '|')) }
}

try {
    # AC5: no config and no -Mac: the dry run names the missing MAC, runs nothing, exits 1
    $r = Invoke-DryRun
    Check 'no mac: named' ($r.Text -match 'blocked: no station MAC: pass -Mac') $r.Text
    Check 'no mac: exit 1' ($r.Code -eq 1) "exit $($r.Code)"
    Check 'no mac: nothing written' $r.Unchanged
    Check 'no mac: says nothing changed' ($r.Text -match 'dry run: nothing changed')

    # -Mac: the template would carry it
    $r = Invoke-DryRun -Extra @('-Mac', 'AA:BB:CC:DD:EE:01')
    Check '-Mac: no mac problem' ($r.Text -notmatch 'no station MAC') $r.Text
    Check '-Mac: the config would be written with it' ($r.Text -match 'would\s+config: write .+config\.json from the template with mac AA:BB:CC:DD:EE:01') $r.Text
    Check '-Mac: nothing written' $r.Unchanged

    # a malformed -Mac
    $r = Invoke-DryRun -Extra @('-Mac', 'not-a-mac')
    Check 'bad -Mac: refused' ($r.Text -match 'blocked: -Mac is not a Bluetooth address: not-a-mac' -and $r.Code -eq 1) $r.Text

    # an existing config without mac: named, never changed
    $r = Invoke-DryRun -Config "{`n  // a comment`n  `"port`": 18940`n}" -Extra @('-Mac', 'AA:BB:CC:DD:EE:02')
    Check 'config without mac: names the file' ($r.Text -match 'blocked: no station MAC: add "mac": "AA:BB:CC:DD:EE:FF" to .+config\.json' -and $r.Code -eq 1) $r.Text
    Check 'config without mac: kept, -Mac only for a new one' ($r.Text -match 'keep .+config\.json as it is \(-Mac is used only for a new config\.json\)') $r.Text
    Check 'config without mac: unchanged' $r.Unchanged

    # an existing config with mac: no mac problem; its wake task name is used
    $r = Invoke-DryRun -Config '{ "mac": "AA:BB:CC:DD:EE:03", "wakeTaskName": "MyWakeTask" }'
    Check 'config with mac: no mac problem' ($r.Text -notmatch 'no station MAC|not a Bluetooth address') $r.Text
    Check 'config with mac: the wake task name from the config' ($r.Text -match "register the wake task 'MyWakeTask'") $r.Text

    # a config that is no JSON
    $r = Invoke-DryRun -Config '{ "mac": '
    Check 'broken config: named' ($r.Text -match 'blocked: .+config\.json cannot be read as JSON' -and $r.Code -eq 1) $r.Text

    # a release layout: the exe is copied, no dotnet needed; the power-mode switch found beside it
    $rel = Join-Path $base 'release'
    foreach ($d in 'bin', 'tools', 'power-mode') { $null = New-Item -ItemType Directory -Path (Join-Path $rel $d) }
    Copy-Item -LiteralPath $installer -Destination $rel
    foreach ($f in 'swap.ps1', 'wake-task.ps1', 'build-venv.ps1') { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $f) -Destination (Join-Path $rel 'tools') }
    [IO.File]::WriteAllText((Join-Path $rel 'bin\BluettiWatcher.exe'), 'stand-in')
    [IO.File]::WriteAllText((Join-Path $rel 'power-mode\power-mode.ps1'), '# stand-in')
    $r = Invoke-DryRun -Script (Join-Path $rel 'install.ps1') -Extra @('-Mac', 'AA:BB:CC:DD:EE:04')
    Check 'release: copies the exe' ($r.Text -match "would\s+copy the release's .+\\bin to the staging directory") $r.Text
    Check 'release: no publish, no dotnet' ($r.Text -notmatch 'publish NativeAOT|no dotnet') $r.Text
    Check 'release: the power-mode switch is copied' ($r.Text -match 'would\s+copy the power-mode switch .+power-mode\\\*\.ps1 to .+bluetti-watcher\\power-mode') $r.Text
    Check 'release: no power-mode problem' ($r.Text -notmatch 'no .+power-mode\.ps1') $r.Text
    Check 'release: exit 1 only with a blocked line' (($r.Code -eq 1) -eq ($r.Text -match 'blocked:') -and $r.Code -in 0, 1) "exit $($r.Code)"
    Check 'release: nothing written' $r.Unchanged

    # a checkout: published
    $r = Invoke-DryRun -Extra @('-Mac', 'AA:BB:CC:DD:EE:05')
    Check 'checkout: publishes into the staging directory' ($r.Text -match 'would\s+publish NativeAOT to the staging directory') $r.Text
    $r = Invoke-DryRun -Extra @('-Mac', 'AA:BB:CC:DD:EE:05', '-Fallback')
    Check 'checkout: -Fallback is the single file' ($r.Text -match 'would\s+publish trimmed self-contained single file') $r.Text

    # -RemoveWake without -Apply: a dry run
    $r = Invoke-DryRun -Extra @('-RemoveWake')
    Check 'remove wake: dry run' ($r.Code -eq 0 -and $r.Text -match "would\s+remove the wake task 'BluettiWatcherWake'" -and $r.Unchanged) $r.Text
    # the wake task name from the config: never the service's own task, only plain characters
    $mac = '"mac": "AA:BB:CC:DD:EE:06"'
    foreach ($bad in 'BluettiWatcher', 'bluettiwatcher', 'a\\b', 'x*', 'a;b', 'a\"b', 'abc\n', ' lead', 'trail.', ('n' * 101)) {
        $r = Invoke-DryRun -Config "{ $mac, `"wakeTaskName`": `"$bad`" }"
        Check "wake name '$bad': blocked" ($r.Code -eq 1 -and $r.Text -match 'blocked: wakeTaskName in .+config\.json: refused: the wake task name') $r.Text
        Check "wake name '$bad': nothing written" $r.Unchanged
    }
    $r = Invoke-DryRun -Config "{ $mac, `"wakeTaskName`": `"BluettiWatcher`" }"
    Check 'wake name = service task: says so' ($r.Text -match "is the service's own task") $r.Text
    $r = Invoke-DryRun -Config "{ $mac, `"wakeTaskName`": `"My Wake-task_1.2`" }"
    Check 'wake name with every allowed character: accepted' ($r.Text -notmatch 'wakeTaskName' -and $r.Text -match "register the wake task 'My Wake-task_1\.2'") $r.Text
    $r = Invoke-DryRun -Config "{ $mac, `"wakeTaskName`": `"BluettiWatcher`" }" -Extra @('-RemoveWake')
    Check '-RemoveWake with the service task name: refused, exit 1' ($r.Code -eq 1 -and $r.Text -match 'blocked: wakeTaskName' -and $r.Text -notmatch 'would\s+remove the wake task') $r.Text
    $r = Invoke-DryRun -Config "{ $mac, `"WakeTaskName`": `"Other`" }"
    Check 'wake name key in the wrong case: blocked' ($r.Code -eq 1 -and $r.Text -match 'blocked: the key WakeTaskName in .+config\.json must be written exactly "wakeTaskName"') $r.Text

    # the service reads keys case-sensitively: "MAC" is a missing mac
    $r = Invoke-DryRun -Config '{ "MAC": "AA:BB:CC:DD:EE:07" }'
    Check 'MAC key: named as a missing mac' ($r.Code -eq 1 -and $r.Text -match 'blocked: no station MAC: the key MAC in .+config\.json must be written exactly "mac"') $r.Text
    $r = Invoke-DryRun -Config '{ "mac": "AA:BB:CC:DD:EE:07", "MAC": "x" }'
    Check 'mac key present: a second MAC key is no problem' ($r.Text -notmatch 'no station MAC') $r.Text

    # Python 3.12-3.14, a regular build: a stand-in python first on PATH
    $fake = Join-Path $base 'fakepython'
    $null = New-Item -ItemType Directory -Path $fake
    [IO.File]::WriteAllText((Join-Path $fake 'python.cmd'), "@echo %BW_FAKE_PYTHON%`r`n")
    foreach ($case in @(@('3 12 0', $true), @('3 13 0', $true), @('3 14 0', $true), @('3 11 0', $false), @('3 15 0', $false), @('2 7 0', $false), @('3 13 1', $false), @('3 14 1', $false), @('garbage', $false))) {
        $r = Invoke-DryRun -Extra @('-Mac', 'AA:BB:CC:DD:EE:08') -Env @{ PATH = "$fake;$env:PATH"; BW_FAKE_PYTHON = $case[0] }
        $blocked = $r.Text -match 'blocked: python on PATH'
        Check "python '$($case[0])': $(if ($case[1]) { 'accepted' } else { 'blocked' })" ($blocked -ne $case[1]) $r.Text
    }
    $r = Invoke-DryRun -Extra @('-Mac', 'AA:BB:CC:DD:EE:08') -Env @{ PATH = "$fake;$env:PATH"; BW_FAKE_PYTHON = '3 13 1' }
    Check 'free-threaded python: named, exit 1' ($r.Code -eq 1 -and $r.Text -match 'blocked: python on PATH is a free-threaded build \(3\.13t\)') $r.Text
    $r = Invoke-DryRun -Extra @('-Mac', 'AA:BB:CC:DD:EE:08') -Env @{ PATH = "$fake;$env:PATH"; BW_FAKE_PYTHON = '3 15 0' }
    Check 'python 3.15: named' ($r.Text -match 'blocked: python on PATH is 3\.15; the venv needs 3\.12, 3\.13 or 3\.14') $r.Text
}
catch {
    Write-Output "FAIL  unexpected: $($_.Exception.Message) at $($_.InvocationInfo.ScriptLineNumber)"
    $script:failed++
}
finally {
    Remove-Item -LiteralPath $base -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Output "test-install: $script:passed passed, $script:failed failed"
if ($script:failed -gt 0) { exit 1 }
if ($script:passed -eq 0) { exit 2 }
exit 0
