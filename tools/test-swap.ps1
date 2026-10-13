<#
test-swap.ps1 - the swap rules of tools/swap.ps1 against temp directories, the task and process operations faked
(the file locks are real). Touches nothing outside its own temp directory.

  pwsh -NoProfile -File tools/test-swap.ps1

Exit: 0 every case passed · 1 a case failed · 2 did not run.
#>
$ErrorActionPreference = 'Stop'
$lib = Join-Path $PSScriptRoot 'swap.ps1'
$installer = Join-Path (Split-Path -Parent $PSScriptRoot) 'install.ps1'
foreach ($f in $lib, $installer) { if (-not (Test-Path -LiteralPath $f)) { Write-Output "test-swap: COULD NOT RUN: no $f"; exit 2 } }
try {
    . $lib
    $base = Join-Path ([IO.Path]::GetTempPath()) ('bluetti-watcher-swap-test-' + [Guid]::NewGuid().ToString('N'))
    $null = New-Item -ItemType Directory -Path $base
}
catch { Write-Output "test-swap: COULD NOT RUN: $($_.Exception.Message)"; exit 2 }

$script:failed = 0
$script:passed = 0
$script:n = 0
function Check([string] $name, [bool] $ok, [string] $detail = '') {
    if ($ok) { $script:passed++ } else { $script:failed++; Write-Output "FAIL  $name $detail" }
}
function Throws([scriptblock] $body) { try { $null = & $body; '' } catch { $_.Exception.Message } }
function Names([string] $dir) { if (Test-Path -LiteralPath $dir) { @(Get-ChildItem -LiteralPath $dir -Force | Sort-Object Name | ForEach-Object Name) -join ',' } else { '<none>' } }
function Exe([string] $dir) { $p = Join-Path $dir 'BluettiWatcher.exe'; if (Test-Path -LiteralPath $p) { [IO.File]::ReadAllText($p) } else { '<none>' } }

$T = 'BluettiWatcher'
# A fresh root: bin holds v1 (unless -NoBin; with -Unverified it never passed health), bin.new holds v2 (unless
# -NoStaging), bin.old holds v0 with -Stale.
function New-Case([switch] $NoBin, [switch] $NoStaging, [switch] $Stale, [switch] $Unverified) {
    $script:n++
    $root = Join-Path $base "case$script:n"
    $c = @{ Root = $root; Bin = (Join-Path $root 'bin'); Staging = (Join-Path $root 'bin.new'); Previous = (Join-Path $root 'bin.old'); Marker = (Join-Path $root 'installer-disabled-task') }
    $null = New-Item -ItemType Directory -Path $root
    $fill = { param($dir, $v) $null = New-Item -ItemType Directory -Path $dir; [IO.File]::WriteAllText((Join-Path $dir 'BluettiWatcher.exe'), $v); [IO.File]::WriteAllText((Join-Path $dir 'publish.log'), "log $v") }
    if (-not $NoBin) { & $fill $c.Bin 'v1' }
    if ($Unverified) { [IO.File]::WriteAllText((Join-Path $c.Bin '.unverified'), 'x') }
    if (-not $NoStaging) { & $fill $c.Staging 'v2' }
    if ($Stale) { & $fill $c.Previous 'v0' }
    $c
}

# Fake operations: $script:calls records every one; a process leaves bin after $script:exitAfter polls (-1 = never);
# the directory moves are real, but the ones whose numbers are in $script:failMoves throw.
function New-FakeOps([string[]] $Tasks = @(), [int] $ExitAfter = 0, [switch] $Disabled) {
    $script:calls = [Collections.Generic.List[string]]::new()
    $script:tasks = [Collections.Generic.HashSet[string]]::new([string[]]$Tasks)
    $script:exitAfter = $ExitAfter
    $script:polls = 0
    $script:moves = 0
    $script:failMoves = @()
    $script:running = $false
    $script:disabled = [Collections.Generic.HashSet[string]]::new()
    if ($Disabled) { foreach ($t in $Tasks) { $null = $script:disabled.Add($t) } }
    @{
        TaskExists     = { param($n) $script:tasks.Contains($n) }
        TaskEnabled    = { param($n) $script:tasks.Contains($n) -and -not $script:disabled.Contains($n) }
        DisableTask    = { param($n) $script:calls.Add('disable'); $null = $script:disabled.Add($n) }
        EnableTask     = { param($n) $script:calls.Add('enable'); $null = $script:disabled.Remove($n) }
        StopTask       = { param($n) $script:calls.Add('stop') }
        StartTask      = { param($n) $script:calls.Add($(if ($script:disabled.Contains($n)) { 'start-while-disabled' } else { 'start' })) }
        UnregisterTask = { param($n) $script:calls.Add('unregister'); $null = $script:tasks.Remove($n) }
        KillIn         = { param($d) $script:calls.Add('kill') }
        ProcessesIn    = { param($d) $script:polls++; if ($script:exitAfter -lt 0 -or $script:polls -le $script:exitAfter) { 'BluettiWatcher (pid 1)' } }
        Sleep          = { param($ms) $script:calls.Add('sleep') }
        TaskRunning    = { param($n) $script:running }
        MoveDir        = { param($from, $to) $script:moves++; if ($script:failMoves -contains $script:moves) { throw "move $script:moves refused" }; [IO.Directory]::Move($from, $to) }
    }
}
function Unverified([string] $dir) { Test-Path -LiteralPath (Join-Path $dir '.unverified') }
$register = { $script:calls.Add('register'); $null = $script:tasks.Add($T); $null = $script:disabled.Remove($T) }
$healthy = { $script:calls.Add('health'); $true }
$sick = { $script:calls.Add('health'); $false }
$script:lines = [Collections.Generic.List[string]]::new()
$log = { param($t) $script:lines.Add($t) }
function Swap($c, $ops, [scriptblock] $Register = $register, [scriptblock] $Health = $healthy, [int] $TimeoutSec = 5) {
    Install-WatcherBinaries -Bin $c.Bin -Staging $c.Staging -Previous $c.Previous -TaskName $T -Marker $c.Marker -Ops $ops -Register $Register -Health $Health -Log $log -TimeoutSec $TimeoutSec -PollMs 1
}
function Repair($c, $ops, [int] $TimeoutSec = 5) {
    Repair-WatcherInstall -Bin $c.Bin -Previous $c.Previous -TaskName $T -Marker $c.Marker -Ops $ops -Log $log -TimeoutSec $TimeoutSec -PollMs 1
}
function Calls { $script:calls -join '|' }
function Live { $script:tasks.Contains($T) -and -not $script:disabled.Contains($T) }

try {
    # an upgrade over a running install
    $c = New-Case
    $ops = New-FakeOps -Tasks $T -ExitAfter 2
    Swap $c $ops
    Check 'upgrade: disable, stop, wait, swap, enable, register, start, health' ((Calls) -eq 'disable|stop|kill|sleep|sleep|enable|register|start|health') (Calls)
    Check 'upgrade: the new exe is live, staging and previous gone' ((Exe $c.Bin) -eq 'v2' -and (Names $c.Root) -eq 'bin') (Names $c.Root)
    Check 'upgrade: the task is enabled' (Live)
    Check 'upgrade: bin is marked as passed' (-not (Unverified $c.Bin))

    # a first install: no task, no bin
    $c = New-Case -NoBin
    $ops = New-FakeOps
    Swap $c $ops
    Check 'first install: register, start, health only' ((Calls) -eq 'register|start|health') (Calls)
    Check 'first install: the exe is live' ((Exe $c.Bin) -eq 'v2' -and (Names $c.Root) -eq 'bin' -and (Live)) (Names $c.Root)

    # nothing published: nothing touched
    $c = New-Case -NoStaging
    $ops = New-FakeOps -Tasks $T
    $err = Throws { Swap $c $ops }
    Check 'no staging exe: refused before the task is touched' ($err -match 'nothing was changed' -and $script:calls.Count -eq 0 -and (Exe $c.Bin) -eq 'v1') "$err $(Calls)"

    # the process does not exit within the wait
    $c = New-Case
    $ops = New-FakeOps -Tasks $T -ExitAfter -1
    $err = Throws { Swap $c $ops -TimeoutSec 0 }
    Check 'process stays: reported in one line' ($err -match 'still in use' -and $err -match 'running: BluettiWatcher \(pid 1\)' -and $err -match 'nothing was swapped' -and $err -notmatch "`n") $err
    Check 'process stays: the old install untouched, staging kept' ((Exe $c.Bin) -eq 'v1' -and (Exe $c.Staging) -eq 'v2' -and (Names $c.Root) -eq 'bin,bin.new') (Names $c.Root)
    Check 'process stays: the task enabled and started again' ((Live) -and (Calls) -eq 'disable|stop|kill|enable|start') (Calls)

    # the exe still held open after the process list is empty
    $c = New-Case
    $ops = New-FakeOps -Tasks $T
    $held = [IO.File]::Open((Join-Path $c.Bin 'BluettiWatcher.exe'), [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try { $err = Throws { Swap $c $ops -TimeoutSec 0 } } finally { $held.Dispose() }
    Check 'exe locked: reported by name, nothing swapped' ($err -match 'held open: BluettiWatcher\.exe' -and (Exe $c.Bin) -eq 'v1' -and (Names $c.Root) -eq 'bin,bin.new') $err
    Check 'exe locked: the task enabled and started again' ((Live) -and $script:calls[-1] -eq 'start') (Calls)

    # the swap cut half way: bin is already bin.old when the staging directory refuses to move
    $c = New-Case
    $ops = New-FakeOps -Tasks $T
    $held = [IO.File]::Open((Join-Path $c.Staging 'publish.log'), [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
    try { $err = Throws { Swap $c $ops } } finally { $held.Dispose() }
    Check 'half swap: reported' ($err -match 'could not be moved' -and $err -match 'nothing was swapped') $err
    Check 'half swap: the previous binaries are bin again' ((Exe $c.Bin) -eq 'v1' -and (Names $c.Root) -eq 'bin,bin.new') (Names $c.Root)
    Check 'half swap: the task enabled and started again, nothing registered' ((Live) -and (Calls) -eq 'disable|stop|kill|enable|start') (Calls)

    # stopping the task fails right after it was disabled
    $c = New-Case
    $ops = New-FakeOps -Tasks $T
    $ops.StopTask = { param($n) $script:calls.Add('stop'); throw 'the scheduler said no' }
    $err = Throws { Swap $c $ops }
    Check 'stop fails: the task is not left disabled' ($err -match 'the scheduler said no' -and (Live) -and (Exe $c.Bin) -eq 'v1' -and (Calls) -eq 'disable|stop|enable|start') "$err $(Calls)"

    # the start after the failure fails too: the task is still enabled
    $c = New-Case
    $ops = New-FakeOps -Tasks $T -ExitAfter -1
    $ops.StartTask = { param($n) $script:calls.Add('start'); throw 'no start' }
    $err = Throws { Swap $c $ops -TimeoutSec 0 }
    Check 'start fails after a failure: enabled all the same, said' ((Live) -and $err -match 'could not be started \(no start\)') "$err $(Calls)"

    # the last net: the enable on the failure path itself fails (a terminating error between disable and enable);
    # the finally enables the task all the same
    $c = New-Case
    $ops = New-FakeOps -Tasks $T -ExitAfter -1
    $script:enableFails = 1
    $ops.EnableTask = { param($n) $script:calls.Add('enable'); if ($script:enableFails-- -gt 0) { throw 'the scheduler is busy' }; $null = $script:disabled.Remove($n) }
    $err = Throws { Swap $c $ops -TimeoutSec 0 }
    Check 'last net: a failed enable on the failure path is retried on the way out' ((Live) -and -not (Test-Path -LiteralPath $c.Marker) -and $err -match 'could not be started \(the scheduler is busy\)' -and (Calls).StartsWith('disable|stop|kill|enable|enable')) "$err $(Calls)"

    Check 'last net: the task it enabled is started too' ((Calls) -eq 'disable|stop|kill|enable|enable|start') (Calls)

    # a double fault in the swap: bin is already bin.old, bin.new will not move, and bin.old will not go back
    $c = New-Case
    $ops = New-FakeOps -Tasks $T
    $script:failMoves = 2, 3
    $err = Throws { Swap $c $ops }
    Check 'double fault in the swap: the message states the real state and the repair' ($err -match 'NO runnable exe is at' -and $err -match 'could not be put back either' -and $err -match "Move-Item -LiteralPath '.+bin\.old' -Destination '.+bin'" -and $err -notmatch 'the previous version is in' -and $err -notmatch "`n") $err
    Check 'double fault in the swap: the task enabled, not started on nothing' ((Live) -and -not (Test-Path -LiteralPath $c.Marker) -and $err -match 'enabled and has nothing to run' -and -not ($script:calls -match 'start')) (Calls)
    $ops = New-FakeOps -Tasks $T
    Check 'double fault in the swap: the next run puts the previous binaries back' ((Repair $c $ops) -and (Exe $c.Bin) -eq 'v1' -and (Names $c.Root) -eq 'bin,bin.new') (Names $c.Root)

    # a double fault on the way back: the new bin is already bin.failed, bin.old will not move, bin.failed will not return
    $c = New-Case
    $ops = New-FakeOps -Tasks $T
    $script:failMoves = 4, 5
    $err = Throws { Swap $c $ops -Health $sick }
    Check 'double fault on the way back: the message states the real state and the repair' ($err -match 'the way back failed too' -and $err -match 'NO runnable exe is at' -and $err -match 'run the installer with -Apply again' -and $err -notmatch 'the new version stays in' -and $err -notmatch "`n") $err
    Check 'double fault on the way back: the task enabled, not started on nothing' ((Live) -and -not (Test-Path -LiteralPath $c.Marker) -and $script:calls[-1] -eq 'enable') (Calls)
    $ops = New-FakeOps -Tasks $T
    Check 'double fault on the way back: the next run puts the previous binaries back and clears the rest' ((Repair $c $ops) -and (Exe $c.Bin) -eq 'v1' -and (Names $c.Root) -eq 'bin') (Names $c.Root)

    # a single fault on the way back: the new version does stay in bin, and is started
    $c = New-Case
    $ops = New-FakeOps -Tasks $T
    $script:failMoves = @(4)
    $err = Throws { Swap $c $ops -Health $sick }
    Check 'single fault on the way back: the new version stays and runs, the previous one kept' ($err -match 'the new version stays in' -and $err -notmatch 'NO runnable' -and (Exe $c.Bin) -eq 'v2' -and (Unverified $c.Bin) -and (Exe $c.Previous) -eq 'v1' -and (Live) -and $script:calls[-1] -eq 'start') "$err $(Calls)"

    # a first install that fails before the swap claims no previous version
    $c = New-Case -NoBin
    $ops = New-FakeOps
    $script:failMoves = @(1)
    $err = Throws { Swap $c $ops }
    Check 'first install, the swap fails: nothing claimed about a previous version' ($err -match 'nothing was installed' -and $err -notmatch 'previous version is in|NO runnable') $err

    # the new version does not answer health: the previous binaries back, started
    $c = New-Case
    $ops = New-FakeOps -Tasks $T
    $err = Throws { Swap $c $ops -Health $sick }
    Check 'health fails: one line saying what runs now' ($err -match 'did not answer the health check' -and $err -match 'the previous binaries are back in' -and $err -match 'enabled and started' -and $err -notmatch "`n") $err
    Check 'health fails: the previous exe is live, nothing else left' ((Exe $c.Bin) -eq 'v1' -and (Names $c.Root) -eq 'bin') (Names $c.Root)
    Check 'health fails: new task off for the way back, then enabled and started' ((Live) -and (Calls) -eq 'disable|stop|kill|enable|register|start|health|disable|stop|kill|enable|start') (Calls)

    # the previous binaries are kept until health answered
    $c = New-Case
    $ops = New-FakeOps -Tasks $T
    $script:seen = ''
    Swap $c $ops -Health { $script:seen = "$(Exe $c.Previous)/$(Exe $c.Bin)/$(Unverified $c.Bin)"; $true }
    Check 'previous binaries: there during the health check (bin not yet passed), removed after it' ($script:seen -eq 'v1/v2/True' -and -not (Test-Path -LiteralPath $c.Previous)) $script:seen

    # health fails and the new exe will not go away: the task still enabled, the previous binaries named
    $c = New-Case
    $ops = New-FakeOps -Tasks $T
    $err = Throws { Swap $c $ops -Health { $script:exitAfter = -1; $false } -TimeoutSec 0 }
    Check 'way back blocked: said, previous binaries kept, bin not passed' ($err -match 'the way back failed too' -and $err -match 'still in use' -and (Exe $c.Previous) -eq 'v1' -and (Exe $c.Bin) -eq 'v2' -and (Unverified $c.Bin)) $err
    # ... and the next run, whose new version fails health again, still ends on the last version that passed
    $null = New-Item -ItemType Directory -Path $c.Staging
    [IO.File]::WriteAllText((Join-Path $c.Staging 'BluettiWatcher.exe'), 'v3')
    $ops = New-FakeOps -Tasks $T
    $err = Throws { Swap $c $ops -Health $sick }
    Check 'rerun after a blocked way back: the last version that passed is live, never the one that failed' ((Exe $c.Bin) -eq 'v1' -and -not (Unverified $c.Bin) -and (Names $c.Root) -eq 'bin' -and (Live)) "$(Exe $c.Bin) $(Names $c.Root) $err"
    Check 'way back blocked: the task enabled and started' ((Live) -and $script:calls[-1] -eq 'start' -and $script:calls[-2] -eq 'enable') (Calls)

    # health fails on a first install: nothing to go back to
    $c = New-Case -NoBin
    $ops = New-FakeOps
    $err = Throws { Swap $c $ops -Health $sick }
    Check 'first install, health fails: the new one stays, task enabled' ($err -match 'no previous version to put back' -and (Exe $c.Bin) -eq 'v2' -and (Live) -and -not ($script:calls -contains 'disable')) "$err $(Calls)"

    # the registration fails after the swap
    $c = New-Case
    $ops = New-FakeOps -Tasks $T
    $err = Throws { Swap $c $ops -Register { $script:calls.Add('register'); throw 'access denied' } }
    Check 'register fails: previous binaries back, task enabled and started' ($err -match 'access denied' -and (Exe $c.Bin) -eq 'v1' -and (Names $c.Root) -eq 'bin' -and (Live) -and $script:calls[-1] -eq 'start') "$err $(Calls)"

    $c = New-Case
    $ops = New-FakeOps
    $err = Throws { Swap $c $ops -Register { $script:calls.Add('register'); throw 'access denied' } }
    Check 'register fails, no task before: previous binaries back, nothing to start' ((Exe $c.Bin) -eq 'v1' -and (Names $c.Root) -eq 'bin' -and (Calls) -eq 'kill|register|kill') "$err $(Calls)"

    # a task found disabled (an undone install): stays so on a failure before the swap, enabled by a good run
    $c = New-Case
    $ops = New-FakeOps -Tasks $T -ExitAfter -1 -Disabled
    $err = Throws { Swap $c $ops -TimeoutSec 0 }
    Check 'found disabled: a failure before the swap neither enables nor starts it' ($err -match 'nothing was swapped' -and -not (Live) -and -not ($script:calls -match 'enable|start')) (Calls)
    $ops = New-FakeOps -Tasks $T -Disabled
    Swap $c $ops
    Check 'found disabled: a good run registers and starts it enabled' ((Live) -and (Exe $c.Bin) -eq 'v2' -and -not ($script:calls -contains 'start-while-disabled')) (Calls)

    # bin passed health, bin.old is a leftover: bin is the version to go back to
    $c = New-Case -Stale
    $ops = New-FakeOps -Tasks $T
    $err = Throws { Swap $c $ops -Health $sick }
    Check 'bin passed, bin.old left over: the way back restores bin' ((Exe $c.Bin) -eq 'v1' -and (Names $c.Root) -eq 'bin') "$(Exe $c.Bin) $(Names $c.Root)"

    # bin never passed health (a run cut during the health wait), bin.old is the last version that passed
    $c = New-Case -Stale -Unverified
    $ops = New-FakeOps -Tasks $T
    $script:seen = ''
    $err = Throws { Swap $c $ops -Health { $script:seen = "$(Exe $c.Previous)/$(Exe $c.Bin)"; $false } }
    Check 'bin never passed: bin.old is not deleted for it' ($script:seen -eq 'v0/v2') $script:seen
    Check 'bin never passed: a failed health check ends on the last version that passed' ((Exe $c.Bin) -eq 'v0' -and -not (Unverified $c.Bin) -and (Names $c.Root) -eq 'bin' -and (Live)) "$(Exe $c.Bin) $(Names $c.Root)"
    $c = New-Case -Stale -Unverified
    $ops = New-FakeOps -Tasks $T
    Swap $c $ops
    Check 'bin never passed: a good run leaves only the new version' ((Exe $c.Bin) -eq 'v2' -and -not (Unverified $c.Bin) -and (Names $c.Root) -eq 'bin')  (Names $c.Root)

    # the staging directory an earlier failed run left
    $c = New-Case
    [IO.File]::WriteAllText((Join-Path $c.Staging 'half.obj'), 'x')
    Clear-WatcherStaging -Staging $c.Staging -Log $log
    Check 'stale staging: removed, bin untouched' (-not (Test-Path -LiteralPath $c.Staging) -and (Exe $c.Bin) -eq 'v1')
    Clear-WatcherStaging -Staging $c.Staging -Log $log
    Check 'stale staging: none is fine' (-not (Test-Path -LiteralPath $c.Staging))

    # a swap cut between its two renames (the PC went dark): no bin, the previous binaries in bin.old
    $c = New-Case -NoBin -Stale
    $ops = New-FakeOps -Tasks $T
    Check 'repair: bin.old is bin again' ((Repair $c $ops) -and (Exe $c.Bin) -eq 'v0' -and -not (Test-Path -LiteralPath $c.Previous)) (Names $c.Root)
    $c = New-Case -Stale
    $ops = New-FakeOps -Tasks $T
    Check 'repair: a bin that passed is left alone, the task untouched' (-not (Repair $c $ops) -and (Exe $c.Bin) -eq 'v1' -and (Exe $c.Previous) -eq 'v0' -and $script:calls.Count -eq 0) (Calls)
    $c = New-Case -NoBin
    $ops = New-FakeOps
    Check 'repair: a first install has nothing to repair' (-not (Repair $c $ops) -and -not (Test-Path -LiteralPath $c.Bin))

    # a run cut during the health wait: bin never passed, bin.old did - put back before anything else
    $c = New-Case -Stale -Unverified
    $ops = New-FakeOps -Tasks $T
    Check 'repair: a bin that never passed gives way to bin.old' ((Repair $c $ops) -and (Exe $c.Bin) -eq 'v0' -and (Names $c.Root) -eq 'bin,bin.new') "$(Exe $c.Bin) $(Names $c.Root)"
    Check 'repair: with the task disabled for it, then enabled and started' ((Live) -and (Calls) -eq 'disable|stop|kill|enable|start') (Calls)
    $c = New-Case -Unverified
    $ops = New-FakeOps -Tasks $T
    Check 'repair: a bin that never passed with nothing to go back to stays' (-not (Repair $c $ops) -and (Exe $c.Bin) -eq 'v1' -and $script:calls.Count -eq 0) (Calls)
    $c = New-Case -Stale -Unverified
    $ops = New-FakeOps -Tasks $T -ExitAfter -1
    $err = Throws { Repair $c $ops -TimeoutSec 0 }
    Check 'repair: a bin in use is not moved, the task enabled again' ($err -match 'still in use' -and (Exe $c.Bin) -eq 'v1' -and (Exe $c.Previous) -eq 'v0' -and (Live)) "$err $(Calls)"

    # the marker of a task the installer disabled: there through the disabled window, gone once it is enabled
    $c = New-Case
    $ops = New-FakeOps -Tasks $T
    $script:seen = ''
    $ops.StopTask = { param($n) $script:calls.Add('stop'); $script:seen += "$(Test-Path -LiteralPath $c.Marker)" }
    Swap $c $ops
    Check 'marker: written before the task is stopped, removed after the enable' ($script:seen -eq 'True' -and -not (Test-Path -LiteralPath $c.Marker)) $script:seen

    # a run that died in the disabled window (here: nothing could enable the task): the marker stays, the next run heals
    $c = New-Case
    $ops = New-FakeOps -Tasks $T -ExitAfter -1
    $ops.EnableTask = { param($n) $script:calls.Add('enable'); throw 'killed' }
    $err = Throws { Swap $c $ops -TimeoutSec 0 } 3>$null
    Check 'marker: stays while the task is disabled' (-not (Live) -and (Test-Path -LiteralPath $c.Marker)) "$err $(Calls)"
    Check 'pending: the dry run names the disabled task' ([bool](@(Get-WatcherPending -Bin $c.Bin -Previous $c.Previous -Marker $c.Marker -TaskName $T) -match "left the task '$T' DISABLED"))
    $keep = $script:disabled
    $ops = New-FakeOps -Tasks $T
    $script:disabled = $keep
    Check 'state: disabled' ((Get-WatcherTaskState -TaskName $T -Ops $ops) -eq 'disabled')
    Check 'repair: the marker enables and starts the task, and goes' ((Repair $c $ops) -and (Live) -and (Calls) -eq 'enable|start' -and -not (Test-Path -LiteralPath $c.Marker)) (Calls)
    Check 'state: ready, then running' ((Get-WatcherTaskState -TaskName $T -Ops $ops) -eq 'ready' -and $(. { $script:running = $true; (Get-WatcherTaskState -TaskName $T -Ops $ops) -eq 'running' }))
    Check 'state: missing' ((Get-WatcherTaskState -TaskName $T -Ops (New-FakeOps)) -eq 'missing')
    Check 'pending: nothing after the repair' (@(Get-WatcherPending -Bin $c.Bin -Previous $c.Previous -Marker $c.Marker -TaskName $T).Count -eq 0)

    # a task disabled by the user has no marker: never enabled
    $c = New-Case
    $ops = New-FakeOps -Tasks $T -Disabled
    Check 'repair: a task disabled by someone else stays disabled' (-not (Repair $c $ops) -and -not (Live) -and $script:calls.Count -eq 0) (Calls)

    # a marker with no task left: just removed
    $c = New-Case
    [IO.File]::WriteAllText($c.Marker, 'x')
    $ops = New-FakeOps
    Check 'repair: a marker without a task is removed' ((Repair $c $ops) -and $script:calls.Count -eq 0 -and -not (Test-Path -LiteralPath $c.Marker)) (Calls)

    # what the dry run reports about the binaries
    $c = New-Case -Stale -Unverified
    Check 'pending: a bin that never passed is named' ([bool](@(Get-WatcherPending -Bin $c.Bin -Previous $c.Previous -Marker $c.Marker -TaskName $T) -match 'never passed the health check'))
    $c = New-Case -NoBin -Stale
    Check 'pending: a swap cut half way is named' ([bool](@(Get-WatcherPending -Bin $c.Bin -Previous $c.Previous -Marker $c.Marker -TaskName $T) -match 'cut half way'))
    Check 'pending: reading only' ((Names $c.Root) -eq 'bin.new,bin.old') (Names $c.Root)

    # the marker cannot be deleted (held open without delete sharing through the disabled window): the enable
    # worked, so the swap goes on to the start and nothing claims the task is disabled
    $c = New-Case
    $ops = New-FakeOps -Tasks $T
    $script:hold = $null
    $ops.StopTask = { param($n) $script:calls.Add('stop'); $script:hold = [IO.File]::Open($c.Marker, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read) }
    $script:lines.Clear()
    try { $warn = @(Swap $c $ops 3>&1); $err = '' } catch { $err = $_.Exception.Message } finally { if ($script:hold) { $script:hold.Dispose() } }
    Check 'marker held: the swap completes and the task is started' ($err -eq '' -and (Live) -and (Exe $c.Bin) -eq 'v2' -and (Calls) -eq 'disable|stop|kill|enable|register|start|health') "$err $(Calls)"
    Check 'marker held: no warning, one log line, the marker left for the next run' ($warn.Count -eq 0 -and @($script:lines -match 'its marker .+ was not removed').Count -eq 1 -and (Test-Path -LiteralPath $c.Marker)) "$warn"
    $ops = New-FakeOps -Tasks $T
    Check 'marker held: the next run removes it' ((Repair $c $ops) -and (Live) -and -not (Test-Path -LiteralPath $c.Marker))

    # ... the same on the way back after a failed health check: the previous binaries are started, not "could not be started"
    $c = New-Case
    $ops = New-FakeOps -Tasks $T
    [IO.File]::WriteAllText($c.Marker, 'x')
    $script:hold = [IO.File]::Open($c.Marker, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::ReadWrite)
    try { $err = Throws { Swap $c $ops -Health $sick } } finally { $script:hold.Dispose() }
    Check 'marker held on the way back: the previous binaries started, said so' ((Live) -and (Exe $c.Bin) -eq 'v1' -and $err -match 'enabled and started' -and $err -notmatch 'could not be started' -and $script:calls[-1] -eq 'start') "$err $(Calls)"

    # bin answered health but cannot be marked as passed: bin.old is kept
    $c = New-Case
    $ops = New-FakeOps -Tasks $T
    $script:hold = $null
    $script:lines.Clear()
    try { Swap $c $ops -Health { $script:hold = [IO.File]::Open((Join-Path $c.Bin '.unverified'), [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read); $true } } finally { if ($script:hold) { $script:hold.Dispose() } }
    Check 'bin not marked as passed: bin.old is kept, said' ((Unverified $c.Bin) -and (Exe $c.Previous) -eq 'v1' -and (Exe $c.Bin) -eq 'v2' -and [bool]($script:lines -match 'could not be marked as passed') -and -not ($script:lines -match 'removed the previous binaries')) (Names $c.Root)

    # the repair says "started" only when it started something
    $c = New-Case -NoBin
    [IO.File]::WriteAllText($c.Marker, 'x')
    $ops = New-FakeOps -Tasks $T -Disabled
    $script:lines.Clear()
    Check 'repair wording: enabled with no exe is not "started"' ((Repair $c $ops) -and (Calls) -eq 'enable' -and [bool]($script:lines -match 'is enabled, not started') -and -not ($script:lines -match 'enabled and started')) "$(Calls) $($script:lines -join ' / ')"

    # a bin.old that never passed health is not called "the last version that passed"
    $c = New-Case -Unverified
    $ops = New-FakeOps -Tasks $T
    $script:lines.Clear()
    Swap $c $ops
    Check 'log wording: an unverified previous version is called that' ([bool]($script:lines -match 'never passed the health check either') -and -not ($script:lines -match 'last version that passed')) ($script:lines -join ' / ')
    $c = New-Case
    $ops = New-FakeOps -Tasks $T
    $script:lines.Clear()
    Swap $c $ops
    Check 'log wording: a previous version that passed is called that' ([bool]($script:lines -match 'the last version that passed the health check wait')) ($script:lines -join ' / ')
    $c = New-Case -Unverified
    $null = New-Item -ItemType Directory -Path $c.Previous
    [IO.File]::WriteAllText((Join-Path $c.Previous '.unverified'), 'x')
    Check 'pending wording: the same for the dry run' ([bool](@(Get-WatcherPending -Bin $c.Bin -Previous $c.Previous -Marker $c.Marker -TaskName $T) -match 'never passed the health check either'))

    # the wait itself
    $c = New-Case
    $ops = New-FakeOps -ExitAfter 3
    Check 'wait: free once the process left' ((Wait-WatcherBinFree -Dir $c.Bin -Ops $ops -TimeoutSec 5 -PollMs 1) -eq '' -and (Calls) -eq 'sleep|sleep|sleep') (Calls)
    Check 'wait: a missing directory is free' ((Wait-WatcherBinFree -Dir (Join-Path $c.Root 'nope') -Ops (New-FakeOps) -TimeoutSec 0 -PollMs 1) -eq '')

    # the health wait
    $script:probes = 0
    Check 'health: true once the probe answers' (Wait-WatcherHealth -TimeoutSec 5 -PollMs 1 -Probe { $script:probes++; $script:probes -ge 3 } -Sleep { param($ms) })
    Check 'health: false when the time is up' (-not (Wait-WatcherHealth -TimeoutSec 0 -PollMs 1 -Probe { $false } -Sleep { param($ms) }))

    # the real operations and the installer's wiring
    $real = Get-WatcherSwapOps
    Check 'ops: every key the swap calls is there' (-not ('TaskExists', 'TaskEnabled', 'DisableTask', 'EnableTask', 'StopTask', 'StartTask', 'KillIn', 'MoveDir', 'ProcessesIn', 'Sleep', 'TaskRunning' | Where-Object { $real[$_] -isnot [scriptblock] }))
    $text = Get-Content -Raw -LiteralPath $installer
    Check 'installer: publishes into the staging directory, never into bin' ($text -match "'-Out', \`$staging\)" -and $text -notmatch "'-Out', \`$bin\)")
    Check 'installer: stops the task nowhere but in the swap' ($text -notmatch 'Stop-ScheduledTask|Stop-Process|Disable-ScheduledTask' -and $text -match 'Install-WatcherBinaries -Bin \$bin -Staging \$staging -Previous \$previous')
    $at = { param($s) $text.IndexOf($s) }
    Check 'installer: the repair of an interrupted run comes before the venv and the publish' ($text -match 'if \(\$Apply\) \{\s+try \{ \$null = Repair-WatcherInstall -Bin' -and (& $at 'Repair-WatcherInstall -Bin') -lt (& $at '# ---- preconditions') -and (& $at 'Repair-WatcherInstall -Bin') -lt (& $at "'tools\build-venv.ps1'") -and (& $at "'tools\build-venv.ps1'") -lt (& $at "'-Out', `$staging)"))
    Check 'installer: both modes print what is pending and the state of the task before the preconditions' ((& $at 'Get-WatcherPending -Bin') -gt 0 -and (& $at 'Get-WatcherPending -Bin') -lt (& $at "Write-Output `"task '`$task': `$(Get-WatcherTaskState") -and (& $at "Write-Output `"task '`$task': `$(Get-WatcherTaskState") -lt (& $at '# ---- preconditions'))
    Check 'installer: a failed publish reports the state it read' ($text -match 'the publish failed \(exit \$code\); \$bin was not touched; the task ''\$task'' is \$\(Get-WatcherTaskState' -and $text -notmatch 'keeps running"')
    Check 'installer: staging and previous sit next to bin' ($text -match "\`$staging = Join-Path \`$root 'bin\.new'" -and $text -match "\`$previous = Join-Path \`$root 'bin\.old'")
}
catch {
    Write-Output "FAIL  unexpected: $($_.Exception.Message) at $($_.InvocationInfo.ScriptLineNumber)"
    $script:failed++
}
finally {
    Remove-Item -LiteralPath $base -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Output "test-swap: $script:passed passed, $script:failed failed"
if ($script:failed -gt 0) { exit 1 }
if ($script:passed -eq 0) { exit 2 }
exit 0
