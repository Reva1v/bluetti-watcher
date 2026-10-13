<#
swap.ps1 - the new binaries go live: published into a staging directory while the service runs, swapped in only after
the publish succeeded. Functions only: install.ps1 dot-sources it and passes the real paths; tools/test-swap.ps1
passes temp directories and fake task operations. Dot-sourcing changes nothing.

Rules:
  - the live bin is never a build output: the service runs through the whole publish
  - the task is disabled before it is stopped (restart-on-failure and the 5-minute trigger would start the old exe
    again mid-swap), and the swap waits, bounded, until no process runs from bin and none of its files is held
  - the swap is two directory renames on one volume: bin -> bin.old, bin.new -> bin
  - a bin that has not passed the health check carries the file .unverified; bin.old is the last version that passed
    and is never deleted while bin carries it: the next run puts bin.old back first
  - bin.old is kept until the health check passed; any failure after the first rename puts it back and starts it
  - a task this code disabled is enabled again on every way out; while it is disabled the marker file
    installer-disabled-task lies next to bin, so a run after a hard kill or a power loss enables and starts it first.
    A task disabled by anything else (the user) has no marker and is left as it is
#>

$script:WatcherExe = 'BluettiWatcher.exe'
$script:WatcherUnverified = '.unverified'

function Write-WatcherLog {
    param($Log, [string] $Text)
    # A logger that fails never fails a step: a swap that worked is not undone for its log line.
    if ($Log) { try { $null = & $Log $Text } catch { Write-Warning "install log failed: $($_.Exception.Message)" } }
}

# The task and process operations install.ps1 uses. The tests pass fakes with the same keys.
function Get-WatcherTaskOps {
    @{
        TaskExists  = { param($name) [bool](Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue) }
        TaskEnabled = { param($name) $t = Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue; [bool]$t -and "$($t.State)" -ne 'Disabled' }
        DisableTask = { param($name) $null = Disable-ScheduledTask -TaskName $name }
        EnableTask  = { param($name) $null = Enable-ScheduledTask -TaskName $name }
        StopTask    = { param($name) Stop-ScheduledTask -TaskName $name }
        ProcessesIn = {
            param($dir)
            $prefix = $dir.TrimEnd('\') + '\'
            @(Get-Process | Where-Object { $_.Path -and $_.Path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) } |
                ForEach-Object { "$($_.ProcessName) (pid $($_.Id))" })
        }
        Sleep       = { param($ms) Start-Sleep -Milliseconds $ms }
    }
}

# Until the probe answers true or the time is up. The probe is a script block returning a boolean.
function Wait-WatcherHealth {
    param([Parameter(Mandatory)][scriptblock] $Probe, [int] $TimeoutSec = 30, [int] $PollMs = 1000, [scriptblock] $Sleep = { param($ms) Start-Sleep -Milliseconds $ms })
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSec)
    while ($true) {
        if (& $Probe) { return $true }
        if ([DateTime]::UtcNow -ge $deadline) { return $false }
        $null = & $Sleep $PollMs
    }
}

# Get-WatcherTaskOps plus what only the swap needs. The tests pass fakes with the same keys.
function Get-WatcherSwapOps {
    $ops = Get-WatcherTaskOps
    $ops.StartTask = { param($name) Start-ScheduledTask -TaskName $name }
    $ops.TaskRunning = { param($name) $t = Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue; [bool]$t -and "$($t.State)" -eq 'Running' }
    # The installed exe only: a process started by hand outlives Stop-ScheduledTask.
    $ops.KillIn = {
        param($dir)
        $prefix = $dir.TrimEnd('\') + '\'
        Get-Process | Where-Object { $_.Path -and $_.Path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) } | Stop-Process -Force
    }
    $ops.MoveDir = { param($from, $to) [IO.Directory]::Move($from, $to) }
    $ops
}

# True while the binaries of the directory have not passed the health check.
function Test-WatcherUnverified {
    param([Parameter(Mandatory)][string] $Bin)
    Test-Path -LiteralPath (Join-Path $Bin $script:WatcherUnverified)
}

# missing · disabled · ready · running.
function Get-WatcherTaskState {
    param([Parameter(Mandatory)][string] $TaskName, [Parameter(Mandatory)][hashtable] $Ops)
    if (-not (& $Ops.TaskExists $TaskName)) { 'missing' }
    elseif (-not (& $Ops.TaskEnabled $TaskName)) { 'disabled' }
    elseif (& $Ops.TaskRunning $TaskName) { 'running' }
    else { 'ready' }
}

# The marker first: a run killed right after the disable must find it.
function Disable-WatcherTask {
    param([Parameter(Mandatory)][string] $TaskName, [Parameter(Mandatory)][string] $Marker, [Parameter(Mandatory)][hashtable] $Ops)
    [IO.File]::WriteAllText($Marker, "the installer disabled the task '$TaskName' for a swap; the next install run enables and starts it")
    $null = & $Ops.DisableTask $TaskName
}

# Throws only when the enable itself failed. A marker that will not go is one log line: the task is enabled, and the
# next install run enables it once more and removes the marker.
function Enable-WatcherTask {
    param([Parameter(Mandatory)][string] $TaskName, [Parameter(Mandatory)][string] $Marker, [Parameter(Mandatory)][hashtable] $Ops, $Log)
    $null = & $Ops.EnableTask $TaskName
    try { if (Test-Path -LiteralPath $Marker) { Remove-Item -LiteralPath $Marker -Force } }
    catch { Write-WatcherLog $Log "the task '$TaskName' is enabled; its marker $Marker was not removed ($($_.Exception.Message)), the next install run removes it" }
}

# What bin.old is called: it passed the health check unless it carries the mark itself.
function Get-WatcherPreviousName {
    param([Parameter(Mandatory)][string] $Previous)
    if (Test-WatcherUnverified $Previous) { 'the previous binaries (they never passed the health check either)' } else { 'the last version that passed the health check' }
}

# What an interrupted run left, one line each; reads only. Repair-WatcherInstall puts each right.
function Get-WatcherPending {
    param([Parameter(Mandatory)][string] $Bin, [Parameter(Mandatory)][string] $Previous, [Parameter(Mandatory)][string] $Marker, [Parameter(Mandatory)][string] $TaskName)
    $hasPrevious = Test-Path -LiteralPath $Previous -PathType Container
    if (Test-Path -LiteralPath $Marker) { "an interrupted install left the task '$TaskName' DISABLED ($Marker): -Apply enables and starts it before anything else" }
    if ($hasPrevious -and -not (Test-Path -LiteralPath $Bin)) { "no $Bin, a swap was cut half way: -Apply makes $Previous $Bin again first" }
    elseif ($hasPrevious -and (Test-WatcherUnverified $Bin)) { "$Bin never passed the health check: -Apply puts $(Get-WatcherPreviousName $Previous) ($Previous) back first" }
}

# The files of a directory another process holds open.
function Get-WatcherHeldFile {
    param([Parameter(Mandatory)][string] $Dir)
    if (-not (Test-Path -LiteralPath $Dir -PathType Container)) { return @() }
    @(foreach ($f in Get-ChildItem -LiteralPath $Dir -File -Force) {
            try { $h = [IO.File]::Open($f.FullName, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None); $h.Dispose() }
            catch { $f.Name }
        })
}

# '' once no process runs from the directory and none of its files is held; otherwise, after the wait, what stays.
function Wait-WatcherBinFree {
    param([Parameter(Mandatory)][string] $Dir, [Parameter(Mandatory)][hashtable] $Ops, [int] $TimeoutSec = 30, [int] $PollMs = 500)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSec)
    while ($true) {
        $left = @(& $Ops.ProcessesIn $Dir)
        $held = @(Get-WatcherHeldFile $Dir)
        if ($left.Count -eq 0 -and $held.Count -eq 0) { return '' }
        if ([DateTime]::UtcNow -ge $deadline) {
            return (@($(if ($left.Count) { "running: $($left -join ', ')" }), $(if ($held.Count) { "held open: $($held -join ', ')" })) | Where-Object { $_ }) -join '; '
        }
        $null = & $Ops.Sleep $PollMs
    }
}

# What an interrupted run left, put right before anything else. True when something was repaired.
#   - no bin, bin.old there (a swap cut between its two renames): bin.old is bin again
#   - bin never passed the health check and bin.old is there: bin.old is bin again
#   - the marker of a task this installer disabled: the task enabled and started
function Repair-WatcherInstall {
    param(
        [Parameter(Mandatory)][string] $Bin,
        [Parameter(Mandatory)][string] $Previous,
        [Parameter(Mandatory)][string] $TaskName,
        [Parameter(Mandatory)][string] $Marker,
        [Parameter(Mandatory)][hashtable] $Ops,
        $Log,
        [int] $TimeoutSec = 30,
        [int] $PollMs = 500
    )
    $did = $false
    $exists = [bool](& $Ops.TaskExists $TaskName)
    try {
        if (-not (Test-Path -LiteralPath $Previous -PathType Container)) { }
        elseif (-not (Test-Path -LiteralPath $Bin)) {
            $null = & $Ops.MoveDir $Previous $Bin
            Write-WatcherLog $Log "an interrupted swap is repaired: $Previous is $Bin again"
            if (Test-Path -LiteralPath "$Bin.failed") { try { Remove-Item -LiteralPath "$Bin.failed" -Recurse -Force } catch { Write-WatcherLog $Log "$Bin.failed was not removed: $($_.Exception.Message)" } }
            $did = $true
        }
        elseif (Test-WatcherUnverified $Bin) {
            if ($exists -and (& $Ops.TaskEnabled $TaskName)) { Disable-WatcherTask -TaskName $TaskName -Marker $Marker -Ops $Ops }
            if ($exists) { $null = & $Ops.StopTask $TaskName }
            $null = & $Ops.KillIn $Bin
            $busy = Wait-WatcherBinFree -Dir $Bin -Ops $Ops -TimeoutSec $TimeoutSec -PollMs $PollMs
            $what = Get-WatcherPreviousName $Previous
            if ($busy) { throw "$Bin never passed the health check and is still in use after $TimeoutSec s ($busy); $what stay(s) in $Previous" }
            $failed = "$Bin.failed"
            if (Test-Path -LiteralPath $failed) { Remove-Item -LiteralPath $failed -Recurse -Force }
            $null = & $Ops.MoveDir $Bin $failed
            try { $null = & $Ops.MoveDir $Previous $Bin }
            catch { $e = $_; $null = & $Ops.MoveDir $failed $Bin; throw $e }
            try { Remove-Item -LiteralPath $failed -Recurse -Force } catch { Write-WatcherLog $Log "$failed was not removed: $($_.Exception.Message)" }
            Write-WatcherLog $Log "$Bin had never passed the health check: $what ($Previous) is $Bin again"
            $did = $true
        }
    }
    finally {
        if (Test-Path -LiteralPath $Marker) {
            if ($exists) {
                Enable-WatcherTask -TaskName $TaskName -Marker $Marker -Ops $Ops -Log $Log
                if (Test-Path -LiteralPath (Join-Path $Bin $script:WatcherExe)) {
                    $null = & $Ops.StartTask $TaskName
                    Write-WatcherLog $Log "the task '$TaskName' is enabled and started: an install run had left it disabled"
                }
                else { Write-WatcherLog $Log "the task '$TaskName' is enabled, not started: an install run had left it disabled, and there is no exe in $Bin to start" }
            }
            else { Remove-Item -LiteralPath $Marker -Force }
            $did = $true
        }
    }
    $did
}

# The logon task's definition, nothing registered: install.ps1 registers it, the tests read it.
# Restart-on-failure covers a run that fails to start, not a process that ran and exited non-zero: the second trigger
# repeats every 5 minutes, indefinitely (no -RepetitionDuration), and with IgnoreNew a running instance is left alone,
# so a process that exited with any code is back within 5 minutes.
function New-WatcherTaskDefinition {
    param(
        [Parameter(Mandatory)][string] $Exe,
        [Parameter(Mandatory)][int] $Port,
        [Parameter(Mandatory)][string] $WorkingDirectory,
        [Parameter(Mandatory)][string] $User
    )
    @{
        Action    = New-ScheduledTaskAction -Execute $Exe -Argument "--port $Port" -WorkingDirectory $WorkingDirectory
        Trigger   = @(
            New-ScheduledTaskTrigger -AtLogOn -User $User
            New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes 5)
        )
        Principal = New-ScheduledTaskPrincipal -UserId $User -LogonType Interactive -RunLevel Limited
        Settings  = New-ScheduledTaskSettingsSet -Hidden -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable `
            -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1) -MultipleInstances IgnoreNew
    }
}

# What an earlier failed run left in the staging directory never reaches bin.
function Clear-WatcherStaging {
    param([Parameter(Mandatory)][string] $Staging, $Log)
    if (-not (Test-Path -LiteralPath $Staging)) { return }
    Remove-Item -LiteralPath $Staging -Recurse -Force
    Write-WatcherLog $Log "removed the staging directory an earlier run left: $Staging"
}

# The swap, the registration, the start and the health check. Throws one line that says what failed and what runs now.
# Register: a script block that registers the task (enabled). Health: a script block, true when /api/health answered.
function Install-WatcherBinaries {
    param(
        [Parameter(Mandatory)][string] $Bin,
        [Parameter(Mandatory)][string] $Staging,
        [Parameter(Mandatory)][string] $Previous,
        [Parameter(Mandatory)][string] $TaskName,
        [Parameter(Mandatory)][string] $Marker,
        [Parameter(Mandatory)][hashtable] $Ops,
        [Parameter(Mandatory)][scriptblock] $Register,
        [Parameter(Mandatory)][scriptblock] $Health,
        $Log,
        [int] $TimeoutSec = 30,
        [int] $PollMs = 500
    )
    if (-not (Test-Path -LiteralPath (Join-Path $Staging $script:WatcherExe))) { throw "no $script:WatcherExe in $Staging; nothing was changed" }
    $hadTask = [bool](& $Ops.TaskExists $TaskName)
    $hadBin = Test-Path -LiteralPath $Bin -PathType Container
    $failed = "$Bin.failed"
    # A task found disabled (an install that was undone) stays disabled unless this run registers it.
    $mustEnable = $hadTask -and [bool](& $Ops.TaskEnabled $TaskName)
    $isDisabled = $false
    $swapped = $false
    try {
        try {
            if ($hadTask) {
                if ($mustEnable) { $isDisabled = $true; Disable-WatcherTask -TaskName $TaskName -Marker $Marker -Ops $Ops }
                $null = & $Ops.StopTask $TaskName
                Write-WatcherLog $Log "task '$TaskName' disabled and stopped: the downtime starts"
            }
            if ($hadBin) {
                $null = & $Ops.KillIn $Bin
                $busy = Wait-WatcherBinFree -Dir $Bin -Ops $Ops -TimeoutSec $TimeoutSec -PollMs $PollMs
                if ($busy) { throw "$Bin is still in use after $TimeoutSec s ($busy)" }
            }
            # A bin that never passed the health check is thrown away; bin.old, the last one that passed, stays.
            $keep = $hadBin -and (Test-WatcherUnverified $Bin) -and (Test-Path -LiteralPath $Previous -PathType Container)
            $aside = if ($keep) { $failed } else { $Previous }
            [IO.File]::WriteAllText((Join-Path $Staging $script:WatcherUnverified), 'these binaries have not passed the health check yet')
            if ($hadBin) {
                if (Test-Path -LiteralPath $aside) { Remove-Item -LiteralPath $aside -Recurse -Force }
                $null = & $Ops.MoveDir $Bin $aside
            }
            try { $null = & $Ops.MoveDir $Staging $Bin }
            catch {
                $why = $_.Exception.Message
                if ($hadBin) { try { $null = & $Ops.MoveDir $aside $Bin } catch { $why += "; $aside could not be put back either: $($_.Exception.Message)" } }
                throw "$Staging could not be moved to $Bin ($why)"
            }
            $swapped = $true
            if ($keep) { try { Remove-Item -LiteralPath $failed -Recurse -Force } catch { Write-WatcherLog $Log "$failed was not removed: $($_.Exception.Message)" } }
            Write-WatcherLog $Log "swapped: $Staging is $Bin$(if (Test-Path -LiteralPath $Previous) { ", $(Get-WatcherPreviousName $Previous) wait(s) in $Previous" })"
            if ($isDisabled) { Enable-WatcherTask -TaskName $TaskName -Marker $Marker -Ops $Ops -Log $Log; $isDisabled = $false }
            $null = & $Register
            $mustEnable = $true
            $null = & $Ops.StartTask $TaskName
            if (-not (& $Health)) { throw 'the new version did not answer the health check' }
        }
        catch {
            $why = $_.Exception.Message
            $exists = [bool](& $Ops.TaskExists $TaskName)
            $wayBack = ''
            $now = if (-not $swapped) { if ($hadBin) { "nothing was swapped, the previous version is in $Bin" } else { 'nothing was installed' } }
            elseif (-not (Test-Path -LiteralPath $Previous -PathType Container)) { "there was no previous version to put back, the new one stays in $Bin" }
            else {
                try {
                    if ($exists -and (& $Ops.TaskEnabled $TaskName)) { $isDisabled = $true; $mustEnable = $true; Disable-WatcherTask -TaskName $TaskName -Marker $Marker -Ops $Ops }
                    if ($exists) { $null = & $Ops.StopTask $TaskName }
                    $null = & $Ops.KillIn $Bin
                    $busy = Wait-WatcherBinFree -Dir $Bin -Ops $Ops -TimeoutSec $TimeoutSec -PollMs $PollMs
                    if ($busy) { throw "$Bin is still in use after $TimeoutSec s ($busy)" }
                    if (Test-Path -LiteralPath $failed) { Remove-Item -LiteralPath $failed -Recurse -Force }
                    $null = & $Ops.MoveDir $Bin $failed
                    try { $null = & $Ops.MoveDir $Previous $Bin }
                    catch { $e = $_; $null = & $Ops.MoveDir $failed $Bin; throw $e }
                    try { Remove-Item -LiteralPath $failed -Recurse -Force } catch { Write-WatcherLog $Log "$failed was not removed: $($_.Exception.Message)" }
                    "the previous binaries are back in $Bin"
                }
                catch { $wayBack = $_.Exception.Message; "the way back failed too ($wayBack): the new version stays in $Bin, the previous binaries are in $Previous" }
            }
            # What is on disk has the last word: a second fault can leave no bin at all.
            $runnable = Test-Path -LiteralPath (Join-Path $Bin $script:WatcherExe)
            if (-not $runnable -and ($hadBin -or $swapped)) {
                $now = "$(if ($wayBack) { "the way back failed too ($wayBack): " })NO runnable exe is at $Bin; $(if (Test-Path -LiteralPath $Previous -PathType Container) { "the last version that ran is in ${Previous}: run the installer with -Apply again, it puts it back first (by hand: Move-Item -LiteralPath '$Previous' -Destination '$Bin')" } else { 'run the installer with -Apply again' })"
            }
            $run = ''
            if ($exists -and $mustEnable) {
                try {
                    if ($isDisabled) { Enable-WatcherTask -TaskName $TaskName -Marker $Marker -Ops $Ops -Log $Log; $isDisabled = $false }
                    if ($runnable) { $null = & $Ops.StartTask $TaskName; $run = "; the task '$TaskName' is enabled and started" }
                    else { $run = "; the task '$TaskName' is enabled and has nothing to run until then" }
                }
                catch { $run = "; the task '$TaskName' could not be started ($($_.Exception.Message))" }
            }
            throw "$why; $now$run"
        }
    }
    finally {
        # The last net: a watcher left disabled is dead through a power cut.
        if ($isDisabled -and $mustEnable) {
            # Started too: an interrupted run would otherwise leave up to 5 minutes without a watcher.
            try { Enable-WatcherTask -TaskName $TaskName -Marker $Marker -Ops $Ops -Log $Log; if (Test-Path -LiteralPath (Join-Path $Bin $script:WatcherExe)) { $null = & $Ops.StartTask $TaskName } } catch { Write-Warning "the task '$TaskName' is still DISABLED: $($_.Exception.Message). Run: Enable-ScheduledTask -TaskName '$TaskName' (the next install run does it: $Marker)" }
        }
    }
    # Passed: only now is bin the version to keep, and bin.old goes.
    try { Remove-Item -LiteralPath (Join-Path $Bin $script:WatcherUnverified) -Force }
    catch { Write-WatcherLog $Log "$Bin could not be marked as passed ($($_.Exception.Message)); $Previous is kept and the next install puts it back"; return }
    if (Test-Path -LiteralPath $Previous) {
        try { Remove-Item -LiteralPath $Previous -Recurse -Force; Write-WatcherLog $Log "removed the previous binaries $Previous" }
        catch { Write-WatcherLog $Log "the previous binaries were not removed ($($_.Exception.Message)); the next install removes $Previous" }
    }
}
