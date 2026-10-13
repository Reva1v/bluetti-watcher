<#
power-mode.ps1 - the power-mode switch of Bluetti Watcher: emergency (save electricity), eco (save electricity, keep
working) and normal, kept in one flag file that the watcher and any other reader (an agent hook, a script of the
user's) read. README.md beside this script is the user's guide.

  pwsh -NoProfile -File power-mode.ps1 on|off|status|eco|end|battery|prev-plan <plan>|gpu-task install|remove|status [-Until HH:mm | -For <minutes>] [-Source manual|station] [-Path <flag file>]

  on      emergency on;  already on  = `since` kept, said so
  off     back to normal; already off = `since` kept, said so (a missing flag file is normal: nothing is written).
          The flag records who did it: `by` manual | station (-Source).
  status  one line: `power mode: normal` or `power mode: EMERGENCY since DD.MM.YYYY HH:mm` or `power mode: ECO since … until …`;
          with a power plan to return to, a `plan:` line and an `end task:` line (present / absent); with a station
          reading in the flag, a `battery:` line
  eco     save electricity, keep working: the Power saver plan and the GPU cap until the end time. The end time is 4 hours
          from now; -For <minutes> (1-1440) or -Until HH:mm (the next such local wall clock, `24:00` = the coming midnight)
          names another; both together = exit 2. The flag carries `until`; past it every reader sees normal. Refused (2)
          while emergency is on.
          -Source station (the Bluetti Watcher, mains is off): no end time, no end task, never expires on read - it lasts
          until `off`. It never replaces a manual eco that is on (said so, exit 0); -Until / -For with it = exit 2.
  end     an eco flag whose `until` has passed gets its power plan back and is rewritten to normal (power-mode-end.ps1,
          the script the end task runs under Windows PowerShell 5.1). Exit 0.
  battery the watcher's station reading into the flag, everything else kept: -Soc <0-100> -Level ok|low|critical|hibernating
          -Mains on|off [-HibernateAt <percent>]  ->  "battery": { soc, at, level, mains, hibernateAt }.
  eco -Adopt -ReturnPlan balanced|high|<plan GUID>   for an eco that is already on and has no plan to return to: record
          the plan and register the end task; `since` and `until` stay. Exit 2 when no eco is on or the plan is not there.

-Source station on `on` marks an emergency the watcher set (battery low). Over the user's eco it remembers that eco in
the flag (`replaced`: since, until), so the status line still names its end time while on battery. `off` (by hand, or
the watcher's `off -Source station` when mains is back) lifts every mode and drops `replaced`: nothing is put back.

  prev-plan <balanced|high|plan GUID>   rewrites the plan an eco that is ON remembers (`prevPlan`), under the
          flag's lock; nothing else changes and no plan is switched. Exit 1 when no eco is on; 2 when the value is no
          plan or the plan is not on this machine.

Config: power-mode.config.json beside the flag, { "returnPlan": "high" | "balanced" | "previous" | "<plan GUID>",
"gpuCap": true | false }. No file or no key = High performance, GPU cap on; a file or key that cannot be read = the
same plus a `warn:` line.

Power plan (eco): the active Windows plan's GUID goes into the flag as `prevPlan` (a plan is owed; `status` shows it
as `before eco:`) and the built-in Power saver plan is activated. Power saver itself is never recorded: an eco started
while it is already active records the plan `off` will set. Every end of eco or of an emergency over it - `off`, the
watcher's `off -Source station` (mains back), the end task - sets the plan `returnPlan` names:
`high` (the default) = the built-in High performance plan; `balanced` = Balanced plus the Windows 11 overlay Best
performance; `previous` = `prevPlan`, or High performance when that is missing, Power saver or not on this machine; a
GUID = that plan. A plan that is not on this machine = Balanced + Best performance, with a `warn:` line. An eco flag
with no `prevPlan` owes a plan only while Power saver is the active plan. `status` names the plan `off` will set.
prev-plan matters only with `previous`.

GPU power cap (optional; eco, unless "gpuCap": false): the first NVIDIA GPU's power limit goes into the flag as
`prevGpuLimitW` and the minimum limit is set (`nvidia-smi -pl <min>`, `gpuCapW`); a second eco, `on` and `battery` keep
both keys. Every path that ends eco (`off`, the watcher's `off`, the end task) sets `prevGpuLimitW` back and drops the
keys. No nvidia-smi (no NVIDIA card), a failing query or a refused set is a `note:` line, exit unchanged: eco works
without it. A restore not confirmed stays in the flag for the next `off`. A flag with no `prevGpuLimitW` never touches
the GPU on `off` (an eco one says so in a note). While eco is on (or an emergency over it), `battery` and a repeated
`eco` read the live limit once; above the recorded cap they set the cap again, with one note: a driver
re-initialisation can put the default limit back while eco is on. `status` shows the live limit beside the cap, and
names the stuck state (the card at its minimum, nothing recorded to restore) with the elevated command that lifts it.
Nothing else on the GPU is touched.

GPU tasks: `nvidia-smi -pl` needs administrator rights and the watcher has none. `gpu-task install [-RestoreWatts <W>]`
(an elevated shell, once) registers two tasks, the user's, highest run level, no trigger, no window: `<prefix>GpuCap`
runs `<System32>\nvidia-smi.exe -pl <min>` and `<prefix>GpuRestore` runs `<System32>\nvidia-smi.exe -pl <restore>` (the
card's default limit, or -RestoreWatts inside min..max), both values read from the card at install and fixed in the
task: nothing a process that is not elevated writes reaches what runs elevated. install deletes a task of that name
first and registers from the XML text (Task Scheduler COM, no file) with its own security: Administrators and SYSTEM
full control, the user read and run; a task whose action or run level is not exactly that is never run. install and
remove refuse a shell that is not elevated and every POWER_MODE_* test seam (exit 2). `gpu-task status` lists both
tasks with their wattage and the live limit (0 both registered · 1 not). A caller that is not elevated runs
`schtasks /run` on the cap or the restore task and reads the limit back for up to 5 s; an elevated caller runs
nvidia-smi itself. No task, a task that does not start or a limit that does not read back = a `note:` naming
`gpu-task install`, exit unchanged; what is owed stays in the flag (a read-back that timed out included). A recorded
limit other than the restore task's value: the task runs, the debt stays, and an elevated `off` pays it.

The end time runs through a one-shot scheduled task `<prefix>End` that runs power-mode-end.ps1 under Windows
PowerShell 5.1 (a path no PowerShell update moves) and removes itself. The task runs as the user, only while they are
logged on, with no window (conhost.exe --headless) - never as SYSTEM, so registering it needs no elevation;
StartWhenAvailable: an end time missed while the PC was off is made up at the next logon. `eco` while eco is on
replaces the task and keeps the first `prevPlan`; Power saver is never recorded as the plan to return to. `on` over eco
keeps Power saver and `prevPlan` and removes the task: emergency has no end time, the plan returns on `off`. None of
this fails the switch - each is a `warn:` line and exit 0: no Power saver plan on the machine (plan unchanged), a task
that could not be registered (the plan returns on `off` only), a plan that could not be set (`prevPlan` stays in the
flag for the next `off`).

Names (power-mode-flag.ps1 reads them): POWER_MODE_FILE the flag (default %ProgramData%\BluettiWatcher\power-mode.json),
POWER_MODE_MUTEX the writers' lock (default Local\BluettiWatcherPowerMode), POWER_MODE_TASK_PREFIX the scheduled tasks
(default BluettiWatcherEco), POWER_MODE_EXTRA_READERS the accounts that may read the flag folder (default none). The end
task gets no environment: its command line carries all four (-FlagFile, -Mutex, -TaskPrefix, -ExtraReaders) beside
-Path. -Path names another flag file (tests), unguarded: its folder must exist.
Flag file: the configured one has its folder created on the first write with its own ACL (the user Modify, SYSTEM and
Administrators Full, nothing inherited). An existing folder whose ACL is not the user's (inheritance on, the user
without Modify, anyone beyond SYSTEM, Administrators and the read of the POWER_MODE_EXTRA_READERS accounts) refuses
every write, exit 2; the script never changes that ACL. A flag file that cannot be told present (access denied) is
untrusted. Readers check the folder the same way, as the user they run as: one not running as the user (SYSTEM, a
service account) never finds its own Modify there and always reads the flag as untrusted - fail closed by design.
  { "mode": "emergency" | "normal" | "eco", "since": "<ISO 8601 with offset>"
[, "until": "<ISO>"] [, "source": "station"] [, "by": "manual" | "station"] [, "prevPlan": "<plan GUID>"] [, "prevGpuLimitW": <watts>, "gpuCapW": <watts>]
[, "replaced": { … }] [, "battery": { … }] }. Read through power-mode-flag.ps1, the one reader. Written to a temp file in
the same directory and moved over the flag (atomic, LF, no BOM, last writer wins) under the writers' lock. `on` / `off`
replace an unreadable flag file and say so: the switch is the repair path. The replace is retried for under a second
while a reader holds the flag open. A written flag whose local time cannot be formatted prints the raw since and a
`warn:` line, still exit 0 (status: exit by mode).

Test seams (power-mode-flag.ps1): every powercfg call goes through Invoke-PowerCfg, every schtasks call through
Invoke-SchTasks and every nvidia-smi call through Invoke-NvidiaSmi; the env vars POWER_MODE_POWERCFG /
POWER_MODE_SCHTASKS / POWER_MODE_NVIDIASMI name a stand-in script run in their place. With a powercfg or schtasks
stand-in and no nvidia-smi one the GPU is never reached. The real tools come from System32 and are killed after
POWER_MODE_TOOL_TIMEOUT_MS (default 5000): no call holds the flag's lock. POWER_MODE_TEST=1 (set by every test runner):
a call with no stand-in stops the script with exit 2 instead of starting the real tool.

Exit: status 0 normal · 1 emergency or eco · 2 could not read (a corrupt or unreadable flag file is never a silent "normal").
      on / off / eco / end / battery 0 written, or already so · 2 could not run (no directory, the write failed, an
      unknown action, a bad end time, a bad -Source or reading, a bad POWER_MODE_TASK_PREFIX).
      gpu-task install / remove 0 done · 1 a task was not registered as written · 2 not elevated, a test seam set, no answer from the card; gpu-task status 0 both registered · 1 not.
      prev-plan 0 rewritten · 1 no eco is on · 2 could not run (no such plan, an unreadable flag, the write failed).
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)][string] $Action = '',
    [string] $Path = '',
    # eco only: when the mode ends, local wall clock `HH:mm` (today, or tomorrow when that time has passed; `24:00` = the
    # coming midnight). Past it the flag reads as normal (every reader compares the clock on every read).
    [string] $Until = '',
    # eco only: the same as a length in minutes from now (1-1440). Neither given = 240.
    [string] $For = '',
    # end only: the `until` the calling task was registered with. An eco flag that runs longer is not this task's to end.
    [string] $TaskUntil = '',
    # Who switches: the user (manual) or the Bluetti Watcher (station).
    [string] $Source = 'manual',
    # battery only: the station's reading.
    [string] $Soc = '',
    [string] $Level = '',
    [string] $Mains = '',
    [string] $HibernateAt = '',
    # eco only: an eco that is already on gets a plan to return to (-ReturnPlan) and its end task; nothing else changes.
    [switch] $Adopt,
    [string] $ReturnPlan = '',
    # prev-plan only: the plan to return to, `balanced` | `high` | `saver` | a plan GUID.
    [Parameter(Position = 1)][string] $Value = '',
    # gpu-task install only: the limit the restore task sets, inside the card's min..max (default: the card's default limit).
    [string] $RestoreWatts = ''
)
$ErrorActionPreference = 'Stop'
$usage = 'usage: power-mode.ps1 on|off|status|eco|battery|prev-plan <balanced|high|plan GUID>|gpu-task install [-RestoreWatts <W>]|remove|status [-Until HH:mm | -For <minutes>] [-Source manual|station] [-Path <flag file>]'
$defaultEcoMinutes = 240
$saverPlan = 'a1841308-3541-4fab-bc81-f71556f20b4a'

try {
    . (Join-Path $PSScriptRoot 'power-mode-flag.ps1')
    $Path = Get-PowerModeFlagPath $Path
} catch { Write-Output "power mode: COULD NOT RUN: $($_.Exception.Message)"; exit 2 }
$taskName = $script:EcoEndTask
$cfg = Read-PowerModeConfig $Path

function Read-Flag { Read-PowerModeFlag $Path }
function Get-Plans { Get-PowerPlans }

# The end task's arguments: the flag, then every live name (the Task Scheduler passes no environment; each name is
# plain, power-mode-flag.ps1 checked it), then the `until` it ends.
function Get-EndTaskArguments([string] $untilText) {
    $n = Get-PowerModeNames
    $readers = if ($n.extraReaders.Count) { " -ExtraReaders `"$($n.extraReaders -join ',')`"" } else { '' }
    return "-Path `"$Path`" -FlagFile `"$($n.flag)`" -Mutex $($n.mutex) -TaskPrefix $($n.taskPrefix)$readers -TaskUntil $untilText"
}

# The end task runs power-mode-end.ps1 under Windows PowerShell 5.1 (its path survives every PowerShell 7 update), as
# the user and headless (New-PowerModeTaskXml): no SYSTEM task ever runs these user-writable scripts.
function Register-EndTask([DateTimeOffset] $untilAt, [string] $untilText) {
    $xmlFile = Join-Path ([IO.Path]::GetTempPath()) "power-mode-end-task-$PID-$([guid]::NewGuid().ToString('N')).xml"
    try {
        $xml = New-PowerModeTaskXml -Description 'power-mode.ps1: ends eco mode and brings the power plan back' -Script (Join-Path $PSScriptRoot 'power-mode-end.ps1') `
            -ScriptArguments (Get-EndTaskArguments $untilText) -TriggersXml (New-EcoEndTriggerXml $untilAt)
        [IO.File]::WriteAllText($xmlFile, $xml, [Text.Encoding]::Unicode)
        return Invoke-SchTasks @('/create', '/tn', $taskName, '/xml', $xmlFile, '/f')
    } catch { return @{ code = -1; out = @($_.Exception.Message) } }
    finally { try { if (Test-Path -LiteralPath $xmlFile) { [IO.File]::Delete($xmlFile) } } catch { } }
}
function Remove-EndTask { $null = Invoke-SchTasks @('/delete', '/tn', $taskName, '/f') }
function Test-EndTask { return (Invoke-SchTasks @('/query', '/tn', $taskName)).code -eq 0 }

# $f: @{ mode; until (text); untilAt; source; by; prevPlan; replaced; battery; since (text: written instead of now) }.
function Write-Flag($f) {
    $now = [DateTimeOffset]::Now
    $since = $now.ToString("yyyy-MM-dd'T'HH:mm:sszzz", [Globalization.CultureInfo]::InvariantCulture)
    if ($f.since) { $since = $f.since; $now = [DateTimeOffset]::Parse($f.since, [Globalization.CultureInfo]::InvariantCulture) }
    $untilText = "$($f.until)"
    if ($f.untilAt -and -not $untilText) { $untilText = ([DateTimeOffset] $f.untilAt).ToString("yyyy-MM-dd'T'HH:mm:sszzz", [Globalization.CultureInfo]::InvariantCulture) }
    $fields = @{ mode = $f.mode; since = $since; until = $untilText; source = "$($f.source)"; by = "$($f.by)"; prevPlan = "$($f.prevPlan)"; replaced = $f.replaced; battery = $f.battery
                 prevGpuLimitW = $f.prevGpuLimitW; gpuCapW = $f.gpuCapW }
    Initialize-PowerModeFlagDir $Path
    $tmp = "$Path.$PID.$([guid]::NewGuid().ToString('N')).tmp"
    try {
        [IO.File]::WriteAllText($tmp, "{ $(Format-PowerModeFlagFields $fields) }`n", [Text.UTF8Encoding]::new($false))
        # Every live session reads the flag on every prompt: a reader holding it open makes the replace fail with
        # "access denied" for a moment, so the move is retried (10 tries, 100 ms apart, under 1 s in all).
        $moved = $false
        $last = $null
        for ($try = 1; $try -le 10 -and -not $moved; $try++) {
            try { [IO.File]::Move($tmp, $Path, $true); $moved = $true }
            catch { $last = $_.Exception.Message; if ($try -lt 10) { Start-Sleep -Milliseconds 100 } }
        }
        if (-not $moved) { throw "the flag file could not be replaced in 10 tries (held open by another process?): $last" }
    } catch {
        try { if (Test-Path -LiteralPath $tmp) { [IO.File]::Delete($tmp) } } catch { }
        throw
    }
    return @{ mode = $f.mode; raw = $f.mode; since = $since; rawSince = $since; at = $now; until = $untilText; untilAt = $f.untilAt; expired = $false
              prevPlan = $fields.prevPlan; source = $(if ($fields.source -ceq 'station') { 'station' } else { 'manual' }); by = $fields.by; replaced = $f.replaced; battery = $f.battery
              prevGpuLimitW = $f.prevGpuLimitW; gpuCapW = $f.gpuCapW }
}

# `HH:mm` local wall clock (the PC's time zone) -> the next such instant; `24:00` = the coming midnight. $null = not a time.
function Resolve-Until([string] $text) {
    if ($text -notmatch '^(\d{1,2}):(\d{2})$') { return $null }
    $h = [int] $Matches[1]; $mi = [int] $Matches[2]
    if ($mi -gt 59 -or $h -gt 24 -or ($h -eq 24 -and $mi -ne 0)) { return $null }
    $zone = Get-PowerModeZone
    $nowK = [TimeZoneInfo]::ConvertTime([DateTimeOffset]::Now, $zone)
    $day = $nowK.Date
    $wall = if ($h -eq 24) { $day.AddDays(1) } else { $day.AddHours($h).AddMinutes($mi) }
    if ($wall -le $nowK.DateTime) { $wall = $wall.AddDays(1) }
    return [DateTimeOffset]::new($wall, $zone.GetUtcOffset($wall))
}

if ($Action -cnotin 'on', 'off', 'status', 'eco', 'end', 'battery', 'prev-plan', 'gpu-task') { Write-Output $usage; exit 2 }
if ($Source -cnotin 'manual', 'station') { Write-Output "power mode: COULD NOT RUN: -Source '$Source' is not manual or station"; exit 2 }

if ($Action -ceq 'status') {
    $flag = Read-Flag
    if (-not $flag) { Write-Output "power mode: COULD NOT READ $Path (not a valid flag file)"; exit 2 }
    Get-StatusLines $flag | Write-Output
    $cfg.warn | Write-Output
    if ($flag.prevPlan -or $flag.raw -ceq 'eco') {
        # The plan `off` will set, by name (Resolve-ReturnPlan), and the plan that was active before eco when it is another.
        $plans = Get-Plans
        $owed = Get-OwedPlan $flag $cfg $plans
        if (-not $owed) { Write-Output 'plan: nothing to return to' }
        else {
            $to = Resolve-ReturnPlan $owed $cfg $plans
            $before = if ($flag.prevPlan -and $flag.prevPlan -cne $to.target) { "; before eco: $(Get-PlanLabel $flag.prevPlan $plans)" } else { '' }
            Write-Output "plan: returns to $(Get-PlanLabel $to.target $plans)$before"
        }
        Write-Output "end task: $taskName $(if (Test-EndTask) { 'present' } else { 'absent' })"
    }
    if ($flag.prevGpuLimitW) { Write-Output "gpu cap: $(if ($flag.gpuCapW) { "$(Format-Watts $flag.gpuCapW) W" } else { 'on' }) (returns to $(Format-Watts $flag.prevGpuLimitW) W)" }
    # The limit in force now, read from the card: something other than this script (a driver or display power transition)
    # can put it back to the default while eco is on.
    if ($flag.prevGpuLimitW -and (Test-GpuSeam)) {
        $live = Get-GpuLimitNow
        Write-Output $(if ($null -eq $live) { 'gpu live: no answer from nvidia-smi' }
            elseif ($flag.gpuCapW -and [Math]::Abs($live - $flag.gpuCapW) -ge 0.5) { "gpu live: $(Format-Watts $live) W - MISMATCH, not the cap $(Format-Watts $flag.gpuCapW) W (something else set it)" }
            else { "gpu live: $(Format-Watts $live) W" })
    }
    # The stuck state: the card at its minimum and no limit recorded, so no off will ever lift it. Named, never guessed.
    if (-not $flag.prevGpuLimitW -and $flag.mode -cin 'eco', 'normal' -and (Test-GpuSeam)) {
        $q = Get-GpuLimits
        if (-not $q.error -and $q.limit -le $q.min + 0.5 -and $q.default -gt $q.min + 0.5) {
            Write-Output "gpu: at minimum $(Format-Watts $q.min) W with nothing recorded to restore - run ``nvidia-smi -pl $(Format-Watts $q.default)`` elevated"
        }
    }
    # The tasks a caller that is not elevated needs to set the limit, while eco owes or may set one (never on a simulated machine).
    if ($cfg.gpuCap -and (Test-GpuSeam) -and ($flag.raw -ceq 'eco' -or $flag.prevGpuLimitW)) {
        $c = Get-GpuTaskWatts $script:GpuCapTask; $re = Get-GpuTaskWatts $script:GpuRestoreTask
        Write-Output $(if ($c.state -ceq 'ok' -and $re.state -ceq 'ok') { "gpu helper: $($script:GpuCapTask) $($c.watts) W, $($script:GpuRestoreTask) $($re.watts) W" }
            else { 'gpu helper: not registered (an elevated shell: power-mode.ps1 gpu-task install)' })
    }
    if ($flag.battery) {
        $when = try { Format-PowerModeTime $flag.battery.atAt } catch { $flag.battery.at }
        $grid = if ($flag.battery.mains -is [bool]) { if ($flag.battery.mains) { ', mains on' } else { ', mains off' } } else { '' }
        Write-Output "battery: $(([double] $flag.battery.soc).ToString('0.##', [Globalization.CultureInfo]::InvariantCulture)) % ($($flag.battery.level)$grid) at $when"
    }
    if ($flag.mode -cne 'normal') { exit 1 }
    exit 0
}

if ($Action -ceq 'gpu-task') {
    # The two tasks a caller that is not elevated starts to set the GPU power limit (New-GpuTaskXml). install and
    # remove run only in a really elevated shell and take no test seam: every POWER_MODE_* variable but the names
    # (flag, mutex, task prefix, extra readers) is refused, the tools are System32's, the elevation is the token's.
    $tasks = @($script:GpuCapTask, $script:GpuRestoreTask)
    if ($Value -cnotin 'install', 'remove', 'status') { Write-Output 'usage: power-mode.ps1 gpu-task install [-RestoreWatts <W>]|remove|status'; exit 2 }
    if ($Value -ceq 'status') {
        $all = $true
        foreach ($name in $tasks) {
            $t = Get-GpuTaskWatts $name
            if ($t.state -ceq 'ok') { Write-Output "gpu helper: $name sets $($t.watts) W" }
            else {
                $all = $false
                Write-Output $(if ($t.state -ceq 'missing') { "gpu helper: $name not registered (an elevated shell: power-mode.ps1 gpu-task install)" }
                    else { "gpu helper: $name is registered but its action is not System32 nvidia-smi -pl <watts>: run gpu-task install again" })
            }
        }
        if (Test-GpuSeam) { $live = Get-GpuLimitNow; Write-Output "gpu live: $(if ($null -eq $live) { 'no answer from nvidia-smi' } else { "$(Format-Watts $live) W" })" }
        if ($all) { exit 0 } else { exit 1 }
    }
    if ($env:POWER_MODE_ELEVATED -ceq '0' -or -not (Test-RealElevation)) { Write-Output "power mode: COULD NOT RUN: gpu-task $Value needs an elevated shell"; exit 2 }
    $seams = @([Environment]::GetEnvironmentVariables().Keys | ForEach-Object { "$_" } | Where-Object { $_ -like 'POWER_MODE_*' -and $_ -cnotin 'POWER_MODE_FILE', 'POWER_MODE_MUTEX', 'POWER_MODE_TASK_PREFIX', 'POWER_MODE_EXTRA_READERS' } | Sort-Object)
    if ($seams.Count) { Write-Output "power mode: COULD NOT RUN: gpu-task $Value takes no test seam, unset $($seams -join ', ')"; exit 2 }
    if ($Value -ceq 'remove') {
        foreach ($name in $tasks) {
            $r = Invoke-RealTool 'schtasks.exe' @('/delete', '/tn', $name, '/f')
            Write-Output "gpu helper: $name $(if ($r.code -eq 0) { 'removed' } else { "not removed ($(Get-LastLine $r))" })"
        }
        exit 0
    }
    $q = Invoke-RealTool 'nvidia-smi.exe' @('--query-gpu=power.min_limit,power.max_limit,power.default_limit', '--format=csv,noheader,nounits')
    $cells = @("$(@($q.out)[0])" -split ',' | ForEach-Object { $_.Trim() })
    $nums = @()
    foreach ($c in $cells) { $n = 0.0; if ([double]::TryParse($c, [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref] $n)) { $nums += $n } }
    if ($q.code -ne 0 -or $nums.Count -ne 3) { Write-Output "power mode: COULD NOT RUN: nvidia-smi did not report the power limit range ($(Get-LastLine $q))"; exit 2 }
    $plan = Resolve-GpuTaskWatts @{ min = $nums[0]; max = $nums[1]; default = $nums[2] } $RestoreWatts
    if ($plan.error) { Write-Output "power mode: COULD NOT RUN: gpu-task install: $($plan.error)"; exit 2 }
    $failed = $false
    foreach ($pair in @(@($script:GpuCapTask, $plan.cap), @($script:GpuRestoreTask, $plan.restore))) {
        $name = $pair[0]; $watts = $pair[1]
        # Registered from the XML text in memory (Register-GpuTask): no file a process that is not elevated could swap.
        # A task of that name registered before (by anyone) goes first: its security and principal are never inherited.
        $null = Invoke-RealTool 'schtasks.exe' @('/delete', '/tn', $name, '/f')
        $r = Register-GpuTask $name (New-GpuTaskXml $name $watts)
        # What was registered is read back: a task whose action is not exactly the one written is deleted again.
        $back = Invoke-RealTool 'schtasks.exe' @('/query', '/tn', $name, '/xml')
        $t = if ($back.code -eq 0) { ConvertFrom-GpuTaskXml ($back.out -join "`n") } else { @{ state = 'missing' } }
        if ($r.code -eq 0 -and $t.state -ceq 'ok' -and $t.watts -eq $watts) { Write-Output "gpu helper: $name registered: nvidia-smi -pl $watts (highest run level, on demand)" }
        else {
            $failed = $true
            if ($t.state -ceq 'foreign') { $null = Invoke-RealTool 'schtasks.exe' @('/delete', '/tn', $name, '/f') }
            Write-Output "power mode: gpu-task install: the task $name was not registered as written ($(Get-LastLine $r))"
        }
    }
    if ($failed) { exit 1 }
    exit 0
}

if ($Action -ceq 'end') {
    & (Join-Path $PSScriptRoot 'power-mode-end.ps1') -Path $Path -TaskUntil $TaskUntil
    exit $LASTEXITCODE
}

# Every write from here on holds the flag's lock: read, decide and write happen under it, so a change another writer
# makes meanwhile is read, never overwritten. Released on every exit (finally).
try { $flagLock = Enter-PowerModeFlagLock $Path } catch { Write-Output "power mode: COULD NOT RUN: $($_.Exception.Message)"; exit 2 }
try {

if ($Action -ceq 'battery') {
    try {
        $socValue = 0.0
        if (-not [double]::TryParse($Soc, [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref] $socValue) -or $socValue -lt 0 -or $socValue -gt 100) {
            Write-Output "power mode: COULD NOT RUN: -Soc '$Soc' is not 0-100"; exit 2 }
        if ($Level -cnotin 'ok', 'low', 'critical', 'hibernating') { Write-Output "power mode: COULD NOT RUN: -Level '$Level' is not ok, low, critical or hibernating"; exit 2 }
        if ($Mains -cnotin 'on', 'off') { Write-Output "power mode: COULD NOT RUN: -Mains '$Mains' is not on or off"; exit 2 }
        $hibValue = $null
        if ($HibernateAt) {
            $parsed = 0.0
            if (-not [double]::TryParse($HibernateAt, [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref] $parsed) -or $parsed -lt 0 -or $parsed -gt 100) {
                Write-Output "power mode: COULD NOT RUN: -HibernateAt '$HibernateAt' is not 0-100"; exit 2 }
            $hibValue = $parsed
        }
        $flag = Read-Flag
        if (-not $flag) { Write-Output "power mode: COULD NOT RUN: $Path is not a valid flag file (run on or off to replace it)"; exit 2 }
        # Test seam: hold between the read and the write, so a test can prove another writer waits for the lock.
        if ($env:POWER_MODE_TEST_HOLD_MS -match '^\d{1,5}$') { [IO.File]::WriteAllText("$Path.held", ''); Start-Sleep -Milliseconds ([int] $env:POWER_MODE_TEST_HOLD_MS) }
        $battery = @{ soc = $socValue; at = [DateTimeOffset]::Now.ToString("yyyy-MM-dd'T'HH:mm:sszzz", [Globalization.CultureInfo]::InvariantCulture); level = $Level; mains = ($Mains -ceq 'on'); hibernateAt = $hibValue }
        # Everything else exactly as written: the raw mode and since, so an eco past its end stays that flag.
        $null = Write-Flag @{ mode = $flag.raw; since = $flag.rawSince; until = $flag.until; source = $flag.source; by = $flag.by; prevPlan = $flag.prevPlan; replaced = $flag.replaced; battery = $battery; prevGpuLimitW = $flag.prevGpuLimitW; gpuCapW = $flag.gpuCapW }
    } catch { Write-Output "power mode: COULD NOT RUN: $($_.Exception.Message)"; exit 2 }
    Write-Output "battery: $(([double] $socValue).ToString('0.##', [Globalization.CultureInfo]::InvariantCulture)) % ($Level, mains $Mains)"
    # The station writes its reading every few minutes during a cut: the drift check rides on it (one read; a set only
    # when the live limit is above the recorded cap).
    # Eco, or an emergency over it (the cap stays on then); never a normal flag, whatever debt it still carries.
    if ($flag.mode -cin 'eco', 'emergency') { $recap = @(); Invoke-GpuRecap $flag $cfg ([ref] $recap); $recap | Write-Output }
    exit 0
}

if ($Action -cne 'eco' -and ($Adopt -or $ReturnPlan)) { Write-Output 'power mode: COULD NOT RUN: -Adopt / -ReturnPlan go with eco'; exit 2 }
if ($Action -ceq 'eco' -and ($Adopt -or $ReturnPlan)) {
    # An eco set before it had a plan to return to: the plan is named, the end task registered, nothing else changes.
    $notes = @()
    try {
        if (-not $Adopt -or -not $ReturnPlan) { Write-Output 'power mode: COULD NOT RUN: pass -Adopt with -ReturnPlan balanced|high|<plan GUID>'; exit 2 }
        if ($Until -or $For) { Write-Output 'power mode: COULD NOT RUN: -Adopt keeps the end time (-Until / -For do not go with it)'; exit 2 }
        $plan = if ($ReturnPlan -ceq 'balanced') { '381b4222-f694-41f0-9685-ff5bb260df2e' } elseif ($ReturnPlan -ceq 'high') { '8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c' } else { $ReturnPlan.ToLowerInvariant() }
        if ($plan -notmatch '^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$') { Write-Output "power mode: COULD NOT RUN: -ReturnPlan '$ReturnPlan' is not balanced, high or a plan GUID"; exit 2 }
        if ($plan -ceq $saverPlan) { Write-Output 'power mode: COULD NOT RUN: Power saver is never the plan to return to'; exit 2 }
        $plans = Get-Plans
        if (-not $plans -or $plans.all -cnotcontains $plan) { Write-Output "power mode: COULD NOT RUN: the power plan $plan is not on this machine"; exit 2 }
        $flag = Read-Flag
        if (-not $flag -or $flag.mode -cne 'eco') { Write-Output 'power mode: COULD NOT RUN: -Adopt needs an eco that is on'; exit 2 }
        $written = Write-Flag @{ mode = 'eco'; since = $flag.rawSince; until = $flag.until; untilAt = $flag.untilAt; source = $flag.source; prevPlan = $plan; battery = $flag.battery; prevGpuLimitW = $flag.prevGpuLimitW; gpuCapW = $flag.gpuCapW }
    } catch { Write-Output "power mode: COULD NOT RUN: $($_.Exception.Message)"; exit 2 }
    $notes += "plan: returns to $plan"
    if ($written.untilAt) {
        $r = Register-EndTask $written.untilAt $written.until
        $notes += if ($r.code -eq 0) { "end task: $taskName registered" }
        else { "warn: the end task $taskName could not be registered ($(Get-LastLine $r)): the power plan $plan will not come back at the end time, only on power-mode.ps1 off" }
    }
    Get-StatusLines $written | Write-Output
    $notes | Write-Output
    exit 0
}

if ($Action -ceq 'prev-plan') {
    # The plan an eco that is on returns to, rewritten: nothing else in the flag changes and no plan is switched.
    try {
        $newPlan = Resolve-PlanAlias $Value
        if (-not $newPlan -or $Value -ceq 'saver') { Write-Output "power mode: COULD NOT RUN: prev-plan '$Value' is not balanced, high or a plan GUID"; exit 2 }
        if ($newPlan -ceq $saverPlan) { Write-Output 'power mode: COULD NOT RUN: Power saver is never the plan to return to'; exit 2 }
        $flag = Read-Flag
        if (-not $flag) { Write-Output "power mode: COULD NOT RUN: $Path is not a valid flag file (run on or off to replace it)"; exit 2 }
        if ($flag.mode -cne 'eco') { Write-Output 'power mode: prev-plan: no eco is on, nothing changed'; exit 1 }
        $plans = Get-Plans
        if (-not $plans -or $plans.all -cnotcontains $newPlan) { Write-Output "power mode: COULD NOT RUN: the power plan $newPlan is not on this machine"; exit 2 }
        $written = Write-Flag @{ mode = 'eco'; since = $flag.rawSince; until = $flag.until; untilAt = $flag.untilAt; source = $flag.source; by = $flag.by; replaced = $flag.replaced; battery = $flag.battery
                                 prevGpuLimitW = $flag.prevGpuLimitW; gpuCapW = $flag.gpuCapW; prevPlan = $newPlan }
    } catch { Write-Output "power mode: COULD NOT RUN: $($_.Exception.Message)"; exit 2 }
    # The recorded plan decides only with returnPlan previous; otherwise say what off will set.
    $to = Resolve-ReturnPlan $newPlan $cfg $plans
    $notes = @($cfg.warn) + @($(if ($to.target -ceq $newPlan) { "plan: returns to $(Get-PlanLabel $newPlan $plans)" }
        else { "plan: remembered $(Get-PlanLabel $newPlan $plans); off returns to $(Get-PlanLabel $to.target $plans) (config returnPlan)" }))
    if ($written.untilAt -and -not (Test-EndTask)) {
        $r = Register-EndTask $written.untilAt $written.until
        $notes += if ($r.code -eq 0) { "end task: $taskName registered" }
        else { "warn: the end task $taskName could not be registered ($(Get-LastLine $r)): the power plan will not come back at the end time, only on power-mode.ps1 off" }
    }
    Get-StatusLines $written | Write-Output
    $notes | Write-Output
    exit 0
}

if ($Action -ceq 'eco') {
    $notes = @($cfg.warn)
    $station = $Source -ceq 'station'
    try {
        if ($station -and ($Until -or $For)) { Write-Output 'power mode: COULD NOT RUN: an eco from the station has no end time (-Until / -For)'; exit 2 }
        if ($Until -and $For) { Write-Output 'power mode: COULD NOT RUN: pass -Until or -For, not both'; exit 2 }
        $untilAt = $null
        if ($Until) {
            $untilAt = Resolve-Until $Until
            if (-not $untilAt) { Write-Output "power mode: COULD NOT RUN: -Until '$Until' is not HH:mm"; exit 2 }
        } elseif (-not $station) {
            $minutes = $defaultEcoMinutes
            if ($For) {
                if ($For -notmatch '^\d{1,4}$' -or [int] $For -lt 1 -or [int] $For -gt 1440) { Write-Output "power mode: COULD NOT RUN: -For '$For' is not minutes 1-1440"; exit 2 }
                $minutes = [int] $For
            }
            $untilAt = [DateTimeOffset]::Now.AddMinutes($minutes)
        }
        $flag = Read-Flag
        if ($flag -and $flag.mode -ceq 'emergency') { Write-Output "$(Get-StatusLine $flag) (eco not set: emergency is stricter; run off first)"; exit 2 }
        # The station never replaces an eco that is on: a manual one keeps its end time, its own keeps its since.
        if ($station -and $flag -and $flag.mode -ceq 'eco') {
            Invoke-GpuRecap $flag $cfg ([ref] $notes)
            Get-StatusLines $flag ' (already on, kept)' | Write-Output; $notes | Write-Output; exit 0
        }
        # A plan already recorded (eco on, or ended and not restored yet) stays the plan to return to.
        $prev = if ($flag) { $flag.prevPlan } else { '' }
        $activate = $false
        $plans = Get-Plans
        if (-not $plans) { $notes += 'warn: powercfg did not list the power plans: the power plan is unchanged' }
        elseif ($plans.all -cnotcontains $saverPlan) { $notes += "warn: the Power saver plan ($saverPlan) is not on this machine: the power plan is unchanged" }
        elseif ($plans.active -ceq $saverPlan) {
            # Power saver is never recorded: switched on by hand before eco, the plan off returns to is recorded instead.
            if ($prev) { $notes += "plan: Power saver stays on; returns to $((Resolve-ReturnPlan $prev $cfg $plans).target)" }
            else { $prev = (Resolve-ReturnPlan '' $cfg $plans).target; $notes += "plan: Power saver was already on; returns to $prev" }
        }
        elseif (-not $prev -and -not $plans.active) { $notes += 'warn: the active power plan could not be read: the power plan is unchanged' }
        else { if (-not $prev) { $prev = $plans.active }; $activate = $true }
        # What off will set (config returnPlan), for the lines below.
        $ret = if ($prev) { (Resolve-ReturnPlan $prev $cfg $plans).target } else { '' }
        # The GPU power cap (config gpuCap): the limit in force goes into the flag before the minimum is set, so a crash
        # in between leaves a debt `off` pays. A limit already recorded (eco is on) stays: the limit now reads the cap.
        $gpuPrev = if ($flag) { $flag.prevGpuLimitW } else { $null }
        $gpuCapW = if ($flag) { $flag.gpuCapW } else { $null }
        $gpuSet = $null
        if ($gpuPrev) { $notes += "gpu cap: stays on (returns to $(Format-Watts $gpuPrev) W)"; Invoke-GpuRecap $flag $cfg ([ref] $notes) }
        elseif ($cfg.gpuCap -and (Test-GpuSeam)) {
            $q = Get-GpuLimits
            if ($q.error) { $notes += "note: gpu cap not set ($($q.error))" }
            elseif ($q.limit -le $q.min) { $notes += "gpu cap: the power limit is already the minimum ($(Format-Watts $q.min) W)" }
            else { $gpuPrev = $q.limit; $gpuCapW = $q.min; $gpuSet = $q.min }
        }
        $ecoFlag = @{ mode = 'eco'; untilAt = $untilAt; source = $Source; prevPlan = $prev; battery = $(if ($flag) { $flag.battery } else { $null }); prevGpuLimitW = $gpuPrev; gpuCapW = $gpuCapW }
        $written = Write-Flag $ecoFlag
        if ($gpuSet) {
            $r = Set-GpuLimit $gpuSet 'cap'
            $ecoFlag.since = $written.since; $ecoFlag.until = $written.until
            if ($r.reached) {
                $notes += "gpu cap: $(Format-Watts $r.applied) W (returns to $(Format-Watts $gpuPrev) W)"
                # The cap task sets its own fixed value: that is the cap in force.
                if ([Math]::Abs($r.applied - $gpuSet) -ge 0.5) { $ecoFlag.gpuCapW = $r.applied; $written = Write-Flag $ecoFlag }
            }
            elseif ($r.timedOut) {
                # Not confirmed: the limit may have changed, so the one to return to stays recorded.
                $notes += "note: gpu cap not confirmed ($($r.why)): the limit to return to stays recorded, off puts it back"
            }
            else {
                # Refused: nothing changed, nothing is owed.
                $notes += "note: gpu cap not set ($($r.why))"
                $ecoFlag.prevGpuLimitW = $null; $ecoFlag.gpuCapW = $null
                $written = Write-Flag $ecoFlag
            }
        }
    } catch { Write-Output "power mode: COULD NOT RUN: $($_.Exception.Message)"; exit 2 }
    if ($activate) {
        $r = Invoke-PowerCfg @('/setactive', $saverPlan)
        $notes += if ($r.code -eq 0) { "plan: Power saver on; returns to $ret" } else { "warn: the Power saver plan could not be set ($(Get-LastLine $r)): the power plan is unchanged" }
    }
    if ($prev -and $station) {
        # No end time, so no end task; one a manual eco left behind (ended, plan not restored yet) goes.
        Remove-EndTask
    } elseif ($prev) {
        $r = Register-EndTask $written.untilAt $written.until
        $notes += if ($r.code -eq 0) { "end task: $taskName registered" }
        else { "warn: the end task $taskName could not be registered ($(Get-LastLine $r)): the power plan $ret will not come back at the end time, only on power-mode.ps1 off" }
    }
    Get-StatusLines $written | Write-Output
    $notes | Write-Output
    exit 0
}

# Writing and reporting are apart: a written flag is exit 0 even when its status line cannot be formatted.
$want = if ($Action -ceq 'on') { 'emergency' } else { 'normal' }
$notes = @($cfg.warn)
try {
    $flag = Read-Flag
    $prev = if ($flag) { $flag.prevPlan } else { '' }
    $battery = if ($flag) { $flag.battery } else { $null }
    $gpuPrev = if ($flag) { $flag.prevGpuLimitW } else { $null }
    $gpuCapW = if ($flag) { $flag.gpuCapW } else { $null }
    # Off pays what eco left owed, an emergency over it included: the plan (config returnPlan, High performance unless
    # named; owed while `prevPlan` is in the flag, or by an eco flag without one while Power saver is active) and the
    # GPU power limit. A flag that is not eco and owes nothing touches neither.
    $owed = if ($Action -ceq 'off' -and $flag) { Get-OwedPlan $flag $cfg } else { '' }
    if ($owed -or ($Action -ceq 'off' -and $gpuPrev)) {
        # Off lifts every mode, the station's (mains back) and the user's alike. An eco that ended keeps its `since`.
        $left = Restore-Plan $owed ([ref] $notes) $cfg
        $gpuLeft = Restore-GpuLimit $flag $cfg ([ref] $notes)
        Remove-EndTask
        $written = Write-Flag @{ mode = 'normal'; by = $Source; prevPlan = $left; battery = $battery; since = $(if ($flag.mode -ceq 'normal') { $flag.since } else { '' })
                                 prevGpuLimitW = $gpuLeft; gpuCapW = $(if ($gpuLeft) { $gpuCapW } else { $null }) }
    }
    elseif (-not ($flag -and $flag.mode -ceq $want)) {
        # The station's emergency over the user's eco remembers that eco, so the status line still names its end time.
        $keep = if ($Action -ceq 'on' -and $Source -ceq 'station' -and $flag -and $flag.mode -ceq 'eco' -and $flag.source -ceq 'manual' -and $flag.untilAt) { @{ since = $flag.rawSince; until = $flag.until; untilAt = $flag.untilAt } } else { $null }
        # An eco that owes no plan and recorded no limit: the GPU is left alone (Restore-GpuLimit only says so).
        if ($Action -ceq 'off' -and $flag) { $null = Restore-GpuLimit $flag $cfg ([ref] $notes) }
        $written = Write-Flag @{ mode = $want; prevPlan = $prev; battery = $battery; replaced = $keep; source = $(if ($Action -ceq 'on') { $Source } else { '' }); by = $(if ($Action -ceq 'off') { $Source } else { '' })
                                 prevGpuLimitW = $gpuPrev; gpuCapW = $gpuCapW }
        if ($prev) {
            # Emergency over eco: Power saver stays, the plan to return to stays, the end task goes (no end time).
            Remove-EndTask
            $notes += "plan: Power saver stays on; returns to $((Resolve-ReturnPlan $prev $cfg (Get-Plans)).target) on off"
        }
    }
} catch {
    Write-Output "power mode: COULD NOT RUN: $($_.Exception.Message)"; exit 2
}
if ($written) {
    $note = if ($flag) { '' } else { ' (replaced an unreadable flag file)' }
    Get-StatusLines $written $note | Write-Output
} else {
    $word = if ($Action -ceq 'on') { 'already on' } else { 'already off' }
    Get-StatusLines $flag " ($word, since kept)" | Write-Output
}
$notes | Write-Output
exit 0

} finally { Exit-PowerModeFlagLock $flagLock }
