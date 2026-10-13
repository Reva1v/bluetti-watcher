<#
power-mode-end.ps1 - what the one-shot task `<prefix>End` (POWER_MODE_TASK_PREFIX, default BluettiWatcherEco) runs at the end time of an eco mode, and what
`power-mode.ps1 end` runs. Written for Windows PowerShell 5.1 (always on the machine, at a path no update moves):

  %SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe -NoProfile -ExecutionPolicy Bypass -File power-mode-end.ps1 -Path "<flag>" -FlagFile "<configured flag>" -Mutex <mutex> -TaskPrefix <prefix> [-ExtraReaders "<a,b>"] -TaskUntil <ISO>

The Task Scheduler passes no environment: with -FlagFile given, -FlagFile, -Mutex, -TaskPrefix and -ExtraReaders set
POWER_MODE_FILE, _MUTEX, _TASK_PREFIX and _EXTRA_READERS (an absent one = empty, whatever the saved environment says)
before power-mode-flag.ps1 is dot-sourced, which checks them
(a name that is not plain = exit 2). A -Path equal to the configured flag is the configured flag: its folder is checked.
No PowerShell 7 syntax or type in this file or in power-mode-flag.ps1, which it dot-sources.

An eco flag whose `until` has passed, or equals -TaskUntil (the `until` the calling task was registered with), pays its
owed plan (the config's `returnPlan`: High performance unless named; a plan that is not on this machine = Balanced with
the Best performance overlay) and its GPU power limit (`prevGpuLimitW`), is rewritten to normal since that `until`, and the task is removed. An eco flag that runs
longer is not this call's to end: a later eco replaced the task. A flag that is not eco (`on` / `off` took the plan
over, or the station set an eco with no end time) is left alone; a task that still calls is stale and goes. A plan that
cannot be set stays in the flag for the next `off`.

Exit 0 done or nothing to do · 2 the flag file cannot be trusted, or the write failed.
#>
param(
    [string] $Path = '',
    [string] $TaskUntil = '',
    [string] $FlagFile = '',
    [string] $Mutex = '',
    [string] $TaskPrefix = '',
    [string] $ExtraReaders = ''
)
$ErrorActionPreference = 'Stop'
# As the end task (-FlagFile given) the four names come from the arguments alone: an absent one is empty (the default),
# never a value the user's saved environment carries, which would widen the trusted readers.
if ($FlagFile) {
    $env:POWER_MODE_FILE = $FlagFile
    $env:POWER_MODE_MUTEX = $Mutex
    $env:POWER_MODE_TASK_PREFIX = $TaskPrefix
    $env:POWER_MODE_EXTRA_READERS = $ExtraReaders
} else {
    if ($Mutex) { $env:POWER_MODE_MUTEX = $Mutex }
    if ($TaskPrefix) { $env:POWER_MODE_TASK_PREFIX = $TaskPrefix }
    if ($ExtraReaders) { $env:POWER_MODE_EXTRA_READERS = $ExtraReaders }
}
try {
    . (Join-Path $PSScriptRoot 'power-mode-flag.ps1')
    $Path = Get-PowerModeFlagPath $Path
} catch { Write-Output "power mode: COULD NOT RUN: $($_.Exception.Message)"; exit 2 }
$taskName = $script:EcoEndTask

# Temp file in the flag's directory, then over the flag: retried for under a second while a reader holds it open.
function Write-EndFlag([string] $text) {
    $tmp = "$Path.$PID.$([guid]::NewGuid().ToString('N')).tmp"
    try {
        Initialize-PowerModeFlagDir $Path
        [IO.File]::WriteAllText($tmp, $text, (New-Object Text.UTF8Encoding $false))
        $done = $false
        $last = ''
        for ($try = 1; $try -le 10 -and -not $done; $try++) {
            try {
                if ([IO.File]::Exists($Path)) { [IO.File]::Replace($tmp, $Path, [NullString]::Value) } else { [IO.File]::Move($tmp, $Path) }
                $done = $true
            } catch { $last = $_.Exception.Message; if ($try -lt 10) { Start-Sleep -Milliseconds 100 } }
        }
        if (-not $done) { throw "the flag file could not be replaced in 10 tries: $last" }
    } catch {
        try { if ([IO.File]::Exists($tmp)) { [IO.File]::Delete($tmp) } } catch { }
        throw
    }
}

$notes = @()
# The flag's lock (power-mode-flag.ps1), held from the read to the write.
try { $flagLock = Enter-PowerModeFlagLock $Path } catch { Write-Output "power mode: COULD NOT RUN: $($_.Exception.Message)"; exit 2 }
try {
    $flag = Read-PowerModeFlag $Path
    if (-not $flag) { Write-Output "power mode: COULD NOT READ $Path (not a valid flag file)"; exit 2 }
    $taskAt = [DateTimeOffset]::MinValue
    $fromTask = [DateTimeOffset]::TryParse($TaskUntil, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::None, [ref] $taskAt)
    if ($flag.raw -cne 'eco' -or -not $flag.untilAt) {
        if ($fromTask) { $null = Invoke-SchTasks @('/delete', '/tn', $taskName, '/f') }
        Get-StatusLines $flag ' (end: no eco to end)' | Write-Output
        exit 0
    }
    # A later eco replaced the task this call came from, or it is a manual call ahead of time: not due.
    $due = $flag.expired -or ($fromTask -and $taskAt -eq $flag.untilAt)
    if (-not $due) { Get-StatusLines $flag ' (end: not due, eco runs on)' | Write-Output; exit 0 }
    $cfg = Read-PowerModeConfig $Path
    $notes += $cfg.warn
    $left = Restore-Plan (Get-OwedPlan $flag $cfg) ([ref] $notes) $cfg
    $gpuLeft = Restore-GpuLimit $flag $cfg ([ref] $notes)
    $capLeft = $null
    if ($gpuLeft) { $capLeft = $flag.gpuCapW }
    $fields = @{ mode = 'normal'; since = $flag.until; until = ''; source = ''; by = ''; prevPlan = $left; battery = $flag.battery; prevGpuLimitW = $gpuLeft; gpuCapW = $capLeft }
    Write-EndFlag ("{ $(Format-PowerModeFlagFields $fields) }`n")
    $null = Invoke-SchTasks @('/delete', '/tn', $taskName, '/f')
} catch { Write-Output "power mode: COULD NOT RUN: $($_.Exception.Message)"; exit 2 }
finally { Exit-PowerModeFlagLock $flagLock }
Write-Output 'power mode: normal (eco ended)'
$notes | Write-Output
exit 0
