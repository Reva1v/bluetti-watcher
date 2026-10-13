<#
power-mode-flag.ps1 (dot-sourced) - the one reader of the power-mode flag and the one place its live names are read,
shared by power-mode.ps1 (the switch), power-mode-end.ps1 (the end task) and any script of the user's that needs the
flag. Runs under Windows PowerShell 5.1 and PowerShell 7 alike: no syntax or type here may need 7 outside the branch
that tests for it.

Configuration (environment, read here only; the Bluetti Watcher passes all four to every power-mode child it starts):
  POWER_MODE_FILE          the flag file          default %ProgramData%\BluettiWatcher\power-mode.json
  POWER_MODE_MUTEX         the flag's writer lock default Local\BluettiWatcherPowerMode
  POWER_MODE_TASK_PREFIX   scheduled-task prefix  default BluettiWatcherEco -> <prefix>End, <prefix>GpuCap, <prefix>GpuRestore
  POWER_MODE_EXTRA_READERS accounts (comma list) that may hold read, never write, on the flag folder; default none
  The mutex is Local\ or Global\ plus a plain name, each reader a plain account name (no domain part): every name also
  goes on the end task's command line. Anything else stops the script (exit 2).

  Get-PowerModeDefaultFlagPath     the configured flag (POWER_MODE_FILE, else the default above), full path. Its folder is
                                   the guarded one: created with its own ACL, checked before every write and every read.
  Get-PowerModeFlagPath [<path>]   the flag file: the argument (any other file, unguarded: tests; the old place in the user profile is refused), else the configured one
  Get-PowerModeNames               @{ flag; mutex; taskPrefix; endTask; gpuCapTask; gpuRestoreTask; extraReaders }
  Initialize-PowerModeFlagDir <f>  before a write: the configured flag's folder is created by the user with its own ACL, an
                                   existing one that is not the user's (Test-PowerModeFlagDirAcl) refuses; another missing one throws
  Read-PowerModeFlag <path>        @{ mode; raw; since; rawSince; at; until; untilAt; expired; prevPlan; source; by; battery }
                                   a missing file = normal · $null = the file is there and cannot be trusted. `mode` is what
                                   is in force now (an eco flag past its `until` reads normal, `expired` set); `raw` is the
                                   mode as written. `source` manual | station (an eco or emergency the Bluetti Watcher set; a
                                   station eco has no `until` and never expires on read). `by` who wrote a normal flag
                                   (manual | station | ''). `battery` $null or @{ soc; at; atAt; level; mains } with level
                                   ok | low | critical | hibernating. A flag without the newer fields reads as before.
  Format-PowerModeFlagFields <h>   the fields of a flag as the text between its braces (mode, since, until, source, by,
                                   prevPlan, replaced, battery)
  Get-StatusLine / Get-StatusLines the status line of a flag
  Format-PowerModeTime             an instant as `dd.MM.yyyy HH:mm` in the PC's time zone
  Invoke-PowerCfg / Invoke-SchTasks / Invoke-NvidiaSmi   the seams to System32 powercfg.exe, schtasks.exe, nvidia-smi.exe:
                                   @{ code; out }, never throw, a child process killed after POWER_MODE_TOOL_TIMEOUT_MS
                                   (default 5000); the env vars POWER_MODE_POWERCFG / POWER_MODE_SCHTASKS /
                                   POWER_MODE_NVIDIASMI name a stand-in run in their place; POWER_MODE_TEST=1 with no
                                   stand-in = exit 2, the real tool is never started
  New-GpuTaskXml / ConvertFrom-GpuTaskXml / Resolve-GpuTaskWatts   the two elevated GPU tasks gpu-task install registers
  New-PowerModeTaskXml             the Task Scheduler XML of a task that runs a script under Windows PowerShell 5.1 as the
                                   interactive user, with no window (conhost.exe --headless), only while they are logged on
#>

# $script:PowerModeDefaultFlagPathTest and the other *Test variables are seams for a dot-sourcing test harness only: no
# environment variable reaches them. They are read through Get-Variable, so a caller under Set-StrictMode with no seam
# set does not throw.
function Get-PowerModeSeam([string] $Name) { Get-Variable -Scope Script -Name $Name -ValueOnly -ErrorAction Ignore }

# The live names: configuration, public defaults neutral. Read here and nowhere else.
$script:PowerModeFlagMutexName = if ($env:POWER_MODE_MUTEX) { $env:POWER_MODE_MUTEX } else { 'Local\BluettiWatcherPowerMode' }
$script:PowerModeTaskPrefix = if ($env:POWER_MODE_TASK_PREFIX) { $env:POWER_MODE_TASK_PREFIX } else { 'BluettiWatcherEco' }
# Every name also travels on the end task's command line (power-mode.ps1 Register-EndTask): plain names only, nothing
# that could quote, split or redirect it. A task name is a path in the Task Scheduler: no folder separator in the prefix.
if ($script:PowerModeTaskPrefix -notmatch '^[A-Za-z0-9_][A-Za-z0-9_.-]{0,99}$') { throw "POWER_MODE_TASK_PREFIX '$($script:PowerModeTaskPrefix)' is not 1-100 letters, digits, '_', '.' or '-', the first a letter, digit or '_'" }
if ($script:PowerModeFlagMutexName -notmatch '^(Local|Global)\\[A-Za-z0-9_.-]{1,100}$') { throw "POWER_MODE_MUTEX '$($script:PowerModeFlagMutexName)' is not Local\ or Global\ plus 1-100 letters, digits, '_', '.' or '-'" }
$script:PowerModeExtraReaders = @("$env:POWER_MODE_EXTRA_READERS" -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
foreach ($reader in $script:PowerModeExtraReaders) {
    if ($reader -notmatch '^[A-Za-z0-9_][A-Za-z0-9_. -]{0,63}$') { throw "POWER_MODE_EXTRA_READERS '$reader' is not 1-64 letters, digits, spaces, '_', '.' or '-', the first a letter, digit or '_'" }
}
$script:EcoEndTask = "$($script:PowerModeTaskPrefix)End"
$script:GpuCapTask = "$($script:PowerModeTaskPrefix)GpuCap"
$script:GpuRestoreTask = "$($script:PowerModeTaskPrefix)GpuRestore"

function Get-PowerModeDefaultFlagPath {
    $seam = Get-PowerModeSeam 'PowerModeDefaultFlagPathTest'
    if ($seam) { return [IO.Path]::GetFullPath($seam) }
    if ($env:POWER_MODE_FILE) { return [IO.Path]::GetFullPath($env:POWER_MODE_FILE) }
    return [IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'BluettiWatcher\power-mode.json'))
}

function Get-PowerModeFlagPath([string] $Path = '') {
    if (-not $Path) { return Get-PowerModeDefaultFlagPath }
    return [IO.Path]::GetFullPath($Path)
}

function Get-PowerModeNames {
    return @{ flag = (Get-PowerModeDefaultFlagPath); mutex = $script:PowerModeFlagMutexName; taskPrefix = $script:PowerModeTaskPrefix
              endTask = $script:EcoEndTask; gpuCapTask = $script:GpuCapTask; gpuRestoreTask = $script:GpuRestoreTask
              extraReaders = $script:PowerModeExtraReaders }
}

# The SIDs of POWER_MODE_EXTRA_READERS (account names, comma-separated; one that does not resolve is left out) · @() = none.
function Get-PowerModeExtraReaderSids {
    $seam = Get-PowerModeSeam 'PowerModeExtraReaderSidsTest'
    if ($seam) { return @($seam) }
    $sids = @()
    foreach ($name in $script:PowerModeExtraReaders) {
        try { $sids += (New-Object Security.Principal.NTAccount $name).Translate([Security.Principal.SecurityIdentifier]).Value } catch { $null = $_ }
    }
    return $sids
}

# The user the flag folder belongs to: the one running this. $script:PowerModeMeSidTest is a test seam (a dot-sourcing
# harness only) that makes a folder the real user owns read as someone else's.
function Get-PowerModeMeSid {
    $seam = Get-PowerModeSeam 'PowerModeMeSidTest'
    if ($seam) { return $seam }
    return [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
}

# '' = one item of the flag (the folder or power-mode.json) is the user's, else the reason. Never changes anything:
# not a reparse point (a planted junction or link), owned by the user, Administrators or SYSTEM (an item's owner can
# rewrite the DACL at will), the folder protected (inheritance off), the user holding Modify (on the folder: for its
# files too), and every other allow entry SYSTEM, Administrators or an extra reader (POWER_MODE_EXTRA_READERS) without
# write. "The user" is the account running this: a reader running as anyone else (SYSTEM, a service account) never finds
# its own Modify and always gets a reason - untrusted, fail closed by design.
function Test-PowerModeAclOf([string] $Path, [bool] $IsDir) {
    try { $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop } catch { return "$Path cannot be read ($($_.Exception.Message))" }
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { return "$Path is a reparse point (a junction or a link)" }
    try { $acl = Get-Acl -LiteralPath $Path } catch { return "the ACL of $Path cannot be read ($($_.Exception.Message))" }
    $me = Get-PowerModeMeSid
    $owner = try { $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value } catch { '' }
    if ($owner -cnotin $me, 'S-1-5-32-544', 'S-1-5-18') { return "$Path is owned by $owner (only the user, Administrators or SYSTEM may own it)" }
    if ($IsDir -and -not $acl.AreAccessRulesProtected) { return 'it inherits its ACL (inheritance must be off)' }
    $readers = @(Get-PowerModeExtraReaderSids)
    # WriteData, AppendData, WriteExtendedAttributes, DeleteSubdirectoriesAndFiles, WriteAttributes, Delete, ChangePermissions,
    # TakeOwnership, GenericAll, GenericWrite
    $write = [long] (0x2 -bor 0x4 -bor 0x10 -bor 0x40 -bor 0x100 -bor 0x10000 -bor 0x40000 -bor 0x80000 -bor 0x10000000 -bor 0x40000000)
    $modify = [long] [Security.AccessControl.FileSystemRights]::Modify
    $userOk = $false
    foreach ($r in $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
        if ("$($r.AccessControlType)" -cne 'Allow') { continue }
        $sid = $r.IdentityReference.Value
        $rights = [long] $r.FileSystemRights
        if ($sid -ceq 'S-1-5-18' -or $sid -ceq 'S-1-5-32-544') { continue }
        if ($sid -ceq $me) {
            $reaches = -not $IsDir -or (($r.InheritanceFlags -band [Security.AccessControl.InheritanceFlags]::ObjectInherit) -and "$($r.PropagationFlags)" -ceq 'None')
            if (($rights -band $modify) -eq $modify -and $reaches) { $userOk = $true }
            continue
        }
        if ($readers -ccontains $sid) { if ($rights -band $write) { return "the extra reader $sid may write there" }; continue }
        return "$sid has rights there (only the user, SYSTEM, Administrators and the extra readers' read may)"
    }
    if (-not $userOk) { return "the user $me holds no Modify there" }
    return ''
}

# '' = the flag folder and the flag file in it (when there) are the user's (Test-PowerModeAclOf), else the reason.
function Test-PowerModeFlagDirAcl([string] $Dir) {
    $why = Test-PowerModeAclOf $Dir $true
    if ($why) { return $why }
    $file = Join-Path $Dir ([IO.Path]::GetFileName((Get-PowerModeDefaultFlagPath)))
    if (Get-Item -LiteralPath $file -Force -ErrorAction SilentlyContinue) { return (Test-PowerModeAclOf $file $false) }   # a dangling link too
    return ''
}

# Before a write: the flag's directory exists, or - for the configured flag only - is created by the user with its own
# ACL: the user Modify, SYSTEM and Administrators Full control, protected (nothing inherited from %ProgramData%, whose
# Users entries would let any account write the flag). An existing configured folder is checked, never changed: one that
# is not the user's (Test-PowerModeFlagDirAcl) refuses the write. Throws 'directory missing' / 'not the user's'.
function Initialize-PowerModeFlagDir([string] $FlagPath) {
    $full = [IO.Path]::GetFullPath($FlagPath)
    $dir = [IO.Path]::GetDirectoryName($full)
    $isDefault = $full -ieq (Get-PowerModeDefaultFlagPath)
    if (Test-Path -LiteralPath $dir -PathType Container) {
        if ($isDefault) {
            $why = Test-PowerModeFlagDirAcl $dir
            if ($why) { throw "the flag folder $dir is not the user's: $why; nothing written (fix the ACL by hand)" }
        }
        return
    }
    if (-not $isDefault) { throw "directory missing: $dir" }
    $me = [Security.Principal.WindowsIdentity]::GetCurrent().User
    if ($me.Value -cin 'S-1-5-18', 'S-1-5-19', 'S-1-5-20' -or @(Get-PowerModeExtraReaderSids) -ccontains $me.Value) { throw "the flag folder $dir is created by the user only, not by $($me.Value)" }
    $inherit = [Security.AccessControl.InheritanceFlags] 'ContainerInherit, ObjectInherit'
    $sec = New-Object Security.AccessControl.DirectorySecurity
    $sec.SetAccessRuleProtection($true, $false)
    $grants = @(
        @((New-Object Security.Principal.SecurityIdentifier 'S-1-5-18'), [Security.AccessControl.FileSystemRights]::FullControl),
        @((New-Object Security.Principal.SecurityIdentifier 'S-1-5-32-544'), [Security.AccessControl.FileSystemRights]::FullControl),
        @($me, [Security.AccessControl.FileSystemRights]::Modify))
    foreach ($g in $grants) {
        $sec.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($g[0], $g[1], $inherit, [Security.AccessControl.PropagationFlags]::None, [Security.AccessControl.AccessControlType]::Allow)))
    }
    # Created WITH the ACL, so the folder is never open under the inherited one, not even for a moment.
    if ($PSVersionTable.PSVersion.Major -ge 6) { [IO.FileSystemAclExtensions]::Create((New-Object IO.DirectoryInfo $dir), $sec) }
    else { $null = [IO.Directory]::CreateDirectory($dir, $sec) }
}

# The zone times are shown in: the PC's own.
function Get-PowerModeZone {
    # POWER_MODE_TZ (test seam): the zone ids to try instead, comma-separated.
    if (-not $env:POWER_MODE_TZ) { return [TimeZoneInfo]::Local }
    foreach ($id in ($env:POWER_MODE_TZ -split ',')) {
        try { return [TimeZoneInfo]::FindSystemTimeZoneById($id) } catch { }
    }
    throw "no time zone $($env:POWER_MODE_TZ) on this machine"
}

function Format-PowerModeTime([DateTimeOffset] $at) {
    [TimeZoneInfo]::ConvertTime($at, (Get-PowerModeZone)).ToString('dd.MM.yyyy HH:mm', [Globalization.CultureInfo]::InvariantCulture)
}

# A JSON value as plain PowerShell: object -> hashtable, string -> string, number -> double, true/false -> bool, the
# rest -> $null. Strings stay text (ConvertFrom-Json in 7 turns an ISO string into a DateTime and loses its offset).
function ConvertFrom-PowerModeElement($el) {
    $kind = "$($el.ValueKind)"
    if ($kind -ceq 'Object') {
        $h = @{}
        foreach ($p in $el.EnumerateObject()) { $h[$p.Name] = ConvertFrom-PowerModeElement $p.Value }
        return $h
    }
    if ($kind -ceq 'String') { return $el.GetString() }
    if ($kind -ceq 'Number') { return $el.GetDouble() }
    if ($kind -ceq 'True') { return $true }
    if ($kind -ceq 'False') { return $false }
    return $null
}
function ConvertFrom-PowerModeValue($v) {
    if ($v -is [Collections.IDictionary]) {
        $h = @{}
        foreach ($k in @($v.Keys)) { $h["$k"] = ConvertFrom-PowerModeValue $v[$k] }
        return $h
    }
    if ($v -is [string] -or $v -is [bool]) { return $v }
    if ($v -is [int] -or $v -is [long] -or $v -is [decimal] -or $v -is [double]) { return [double] $v }
    return $null
}
# The top-level object of a flag file as a hashtable · $null = not a JSON object.
function ConvertFrom-PowerModeJson([string] $Text) {
    try {
        if ($PSVersionTable.PSVersion.Major -ge 6) {
            $doc = [Text.Json.JsonDocument]::Parse($Text)
            if ("$($doc.RootElement.ValueKind)" -cne 'Object') { return $null }
            return (ConvertFrom-PowerModeElement $doc.RootElement)
        }
        Add-Type -AssemblyName System.Web.Extensions
        $o = (New-Object System.Web.Script.Serialization.JavaScriptSerializer).DeserializeObject($Text)
        if ($o -isnot [Collections.IDictionary]) { return $null }
        return (ConvertFrom-PowerModeValue $o)
    } catch { return $null }
}

# A flag file's text, read the way a writer is never blocked and never blocks it: FileShare ReadWrite|Delete (the
# writers replace the file by a move). A BOM is dropped.
function Read-PowerModeFlagText([string] $Path) {
    $fs = New-Object IO.FileStream($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    try { $reader = New-Object IO.StreamReader($fs, (New-Object Text.UTF8Encoding $false), $true); return $reader.ReadToEnd() }
    finally { $fs.Dispose() }
}

# One writer at a time, across processes, both PowerShell editions and the Bluetti Watcher (C#), which takes the same
# named mutex (POWER_MODE_MUTEX, read at the top of this file): every write path takes it, re-reads the flag under it and
# writes atomically.
function Enter-PowerModeFlagLock([string] $Path, [int] $TimeoutMs = 10000) {
    $m = New-Object Threading.Mutex($false, $script:PowerModeFlagMutexName)
    $owned = $false
    try { $owned = $m.WaitOne($TimeoutMs) } catch [Threading.AbandonedMutexException] { $owned = $true }   # a writer that died: ours now
    if (-not $owned) { $m.Dispose(); throw "another writer holds the power-mode flag lock ($([int] ($TimeoutMs / 1000)) s)" }
    return $m
}
function Exit-PowerModeFlagLock($m) { if ($m) { try { $m.ReleaseMutex() } catch { $null = $_ }; $m.Dispose() } }

function Read-PowerModeFlag([string] $Path) {
    $sinceRe = '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d+)?)?(Z|[+-]\d{2}:\d{2})$'
    $inv = [Globalization.CultureInfo]::InvariantCulture
    $none = [Globalization.DateTimeStyles]::None
    $flag = @{ mode = 'normal'; raw = 'normal'; since = ''; rawSince = ''; at = $null; until = ''; untilAt = $null; expired = $false
               prevPlan = ''; source = 'manual'; by = ''; battery = $null; replaced = $null; prevGpuLimitW = $null; gpuCapW = $null }
    # The configured flag is read only while its folder is the user's: a folder or file someone else could have written
    # is untrusted, and so is a file that cannot be told present or absent (access denied).
    if ($Path -and [IO.Path]::GetFullPath($Path) -ieq (Get-PowerModeDefaultFlagPath)) {
        try { $null = [IO.File]::GetAttributes($Path) }
        catch [IO.FileNotFoundException], [IO.DirectoryNotFoundException] { $null = $_ }
        catch { return $null }
        $flagDir = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($Path))
        if ((Test-Path -LiteralPath $flagDir) -and (Test-PowerModeFlagDirAcl $flagDir)) { return $null }
    }
    if (-not (Test-Path -LiteralPath $Path)) { return $flag }
    try {
        $j = ConvertFrom-PowerModeJson (Read-PowerModeFlagText $Path)
        if ($null -eq $j) { return $null }
        if ($j['mode'] -isnot [string] -or $j['since'] -isnot [string]) { return $null }
        if ($j['mode'] -cnotin 'emergency', 'normal', 'eco') { return $null }
        $since = $j['since']
        if ($since -notmatch $sinceRe) { return $null }
        $at = [DateTimeOffset]::MinValue
        if (-not [DateTimeOffset]::TryParse($since, $inv, $none, [ref] $at)) { return $null }
        $flag.mode = $j['mode']; $flag.raw = $j['mode']; $flag.since = $since; $flag.rawSince = $since; $flag.at = $at
        # The power plan to return to (power-mode.ps1 eco). Anything that is not a GUID is no plan.
        if ($j['prevPlan'] -is [string] -and $j['prevPlan'] -match '^[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}$') { $flag.prevPlan = $j['prevPlan'].ToLowerInvariant() }
        # The GPU power limit to return to and the cap eco set (watts). Anything that is not a positive number is none.
        if ($j['prevGpuLimitW'] -is [double] -and $j['prevGpuLimitW'] -gt 0) {
            $flag.prevGpuLimitW = $j['prevGpuLimitW']
            if ($j['gpuCapW'] -is [double] -and $j['gpuCapW'] -gt 0) { $flag.gpuCapW = $j['gpuCapW'] }
        }
        if ($j['source'] -is [string] -and $j['source'] -ceq 'station') { $flag.source = 'station' }
        if ($j['by'] -is [string] -and $j['by'] -cin 'manual', 'station') { $flag.by = $j['by'] }
        # The station's battery, written by the Bluetti Watcher. A malformed one is no battery, never a corrupt flag.
        $b = $j['battery']
        if ($b -is [hashtable] -and $b['soc'] -is [double] -and $b['soc'] -ge 0 -and $b['soc'] -le 100 -and
            $b['at'] -is [string] -and $b['at'] -match $sinceRe -and $b['level'] -is [string] -and $b['level'] -cin 'ok', 'low', 'critical', 'hibernating') {
            $batAt = [DateTimeOffset]::MinValue
            if ([DateTimeOffset]::TryParse($b['at'], $inv, $none, [ref] $batAt)) {
                $flag.battery = @{ soc = $b['soc']; at = $b['at']; atAt = $batAt; level = $b['level']; mains = $(if ($b['mains'] -is [bool]) { $b['mains'] } else { $null })
                                   hibernateAt = $(if ($b['hibernateAt'] -is [double]) { $b['hibernateAt'] } else { $null }) }
            }
        }
        # What an emergency from the station replaced: the user's eco, named in the status line while it would still run.
        $r = $j['replaced']
        if ($r -is [hashtable] -and $r['since'] -is [string] -and $r['since'] -match $sinceRe -and $r['until'] -is [string] -and $r['until'] -match $sinceRe) {
            $rUntil = [DateTimeOffset]::MinValue
            if ([DateTimeOffset]::TryParse($r['until'], $inv, $none, [ref] $rUntil)) { $flag.replaced = @{ since = $r['since']; until = $r['until']; untilAt = $rUntil } }
        }
        if ($j['mode'] -ceq 'eco') {
            # An eco the station set lasts while mains is off: no `until`, nothing to expire.
            if ($null -eq $j['until'] -and $flag.source -ceq 'station') { return $flag }
            if ($j['until'] -isnot [string]) { return $null }
            $untilText = $j['until']
            if ($untilText -notmatch $sinceRe) { return $null }
            $untilAt = [DateTimeOffset]::MinValue
            if (-not [DateTimeOffset]::TryParse($untilText, $inv, $none, [ref] $untilAt)) { return $null }
            $flag.until = $untilText; $flag.untilAt = $untilAt
            # Past `until` the flag is normal since that instant: no process has to be alive to switch it.
            if ([DateTimeOffset]::Now -ge $untilAt) { $flag.mode = 'normal'; $flag.since = $untilText; $flag.at = $untilAt; $flag.expired = $true }
        }
        return $flag
    } catch { return $null }
}

function Format-PowerModeBattery($battery) {
    $inv = [Globalization.CultureInfo]::InvariantCulture
    $mains = if ($battery.mains -is [bool]) { ", `"mains`": $(if ($battery.mains) { 'true' } else { 'false' })" } else { '' }
    $hib = if ($null -ne $battery.hibernateAt) { ", `"hibernateAt`": $(([double] $battery.hibernateAt).ToString('0.##', $inv))" } else { '' }
    return "{ `"soc`": $(([double] $battery.soc).ToString('0.##', $inv)), `"at`": `"$($battery.at)`", `"level`": `"$($battery.level)`"$mains$hib }"
}

# @{ mode; since; until; source; by; prevPlan; battery } -> the text between the braces; an empty field is left out, and
# `source` is written only when it is the station (a manual flag keeps its old shape).
function Format-PowerModeFlagFields($f) {
    $parts = @("`"mode`": `"$($f.mode)`"", "`"since`": `"$($f.since)`"")
    if ($f.until) { $parts += "`"until`": `"$($f.until)`"" }
    if ($f.source -ceq 'station') { $parts += '"source": "station"' }
    if ($f.by) { $parts += "`"by`": `"$($f.by)`"" }
    if ($f.prevPlan) { $parts += "`"prevPlan`": `"$($f.prevPlan)`"" }
    if ($f.prevGpuLimitW) {
        $parts += "`"prevGpuLimitW`": $(Format-Watts $f.prevGpuLimitW)"
        if ($f.gpuCapW) { $parts += "`"gpuCapW`": $(Format-Watts $f.gpuCapW)" }
    }
    if ($f.replaced) { $parts += "`"replaced`": { `"mode`": `"eco`", `"since`": `"$($f.replaced.since)`", `"until`": `"$($f.replaced.until)`" }" }
    if ($f.battery) { $parts += "`"battery`": $(Format-PowerModeBattery $f.battery)" }
    return ($parts -join ', ')
}

function Get-StatusLine($flag) {
    if ($flag.mode -ceq 'emergency') { return "power mode: EMERGENCY since $(Format-PowerModeTime $flag.at)$(if ($flag.replaced -and $flag.replaced.untilAt -gt [DateTimeOffset]::Now) { " (over the user's eco until $(Format-PowerModeTime $flag.replaced.untilAt))" })" }
    if ($flag.mode -ceq 'eco' -and -not $flag.untilAt) { return "power mode: ECO since $(Format-PowerModeTime $flag.at) while mains is off (station; everything else as usual)" }
    if ($flag.mode -ceq 'eco') { return "power mode: ECO since $(Format-PowerModeTime $flag.at) until $(Format-PowerModeTime $flag.untilAt) (everything else as usual)" }
    if ($flag.expired) { return "power mode: normal (eco ended $(Format-PowerModeTime $flag.at))" }
    return 'power mode: normal'
}

# The status line, or - when the local time cannot be formatted - the raw since plus a warn: line. Never throws.
function Get-StatusLines($flag, [string] $suffix = '') {
    try { return @("$(Get-StatusLine $flag)$suffix") }
    catch {
        $raw = if ($flag.mode -ceq 'emergency') { "power mode: EMERGENCY since $($flag.since)" }
               elseif ($flag.mode -ceq 'eco') { "power mode: ECO since $($flag.since)$(if ($flag.until) { " until $($flag.until)" })" }
               elseif ($flag.expired) { "power mode: normal (eco ended $($flag.since))" }
               else { 'power mode: normal' }
        return @("$raw$suffix", "warn: local time unavailable ($($_.Exception.Message))")
    }
}

# The seams: nothing reaches powercfg, the Task Scheduler or nvidia-smi any other way. @{ code; out }, never throws.
# A stand-in script (*.ps1, tests) runs in this process; anything else runs as a child process with no window, killed
# after POWER_MODE_TOOL_TIMEOUT_MS (default 5000) so no call holds the flag's lock: code -2 = timed out. The real tools
# are taken from System32 by the OS folder, never by PATH. Under POWER_MODE_TEST=1 (every test runner sets it) a call
# with no stand-in stops the script, exit 2: a test that forgot a stand-in never reaches the real machine.
function ConvertTo-ProcessArgument([string] $a) {
    if ($a -notmatch '[\s"]' -and $a) { return $a }
    return '"' + ($a -replace '"', '\"') + '"'
}
function Get-ToolTimeoutMs {
    if ($env:POWER_MODE_TOOL_TIMEOUT_MS -match '^\d{1,6}$') { return [int] $env:POWER_MODE_TOOL_TIMEOUT_MS }
    return 5000
}
function Invoke-TimedTool([string] $File, [string[]] $List, [int] $TimeoutMs) {
    $name = [IO.Path]::GetFileName($File)
    $psi = New-Object Diagnostics.ProcessStartInfo
    $psi.FileName = $File
    $psi.Arguments = (@($List | ForEach-Object { ConvertTo-ProcessArgument $_ }) -join ' ')
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    try { $p = [Diagnostics.Process]::Start($psi) } catch { return @{ code = -1; out = @("$name could not start: $($_.Exception.Message)") } }
    try {
        $o = $p.StandardOutput.ReadToEndAsync()
        $e = $p.StandardError.ReadToEndAsync()
        if (-not $p.WaitForExit($TimeoutMs)) {
            # The whole tree goes: a tool's own children must not outlive the call either.
            try {
                $k = New-Object Diagnostics.ProcessStartInfo
                $k.FileName = Join-Path ([Environment]::GetFolderPath('System')) 'taskkill.exe'
                $k.Arguments = "/T /F /PID $($p.Id)"
                $k.UseShellExecute = $false; $k.CreateNoWindow = $true; $k.RedirectStandardOutput = $true; $k.RedirectStandardError = $true
                $kp = [Diagnostics.Process]::Start($k); $null = $kp.WaitForExit(3000); $kp.Dispose()
            } catch { $null = $_ }
            try { if (-not $p.HasExited) { $p.Kill() } } catch { $null = $_ }
            return @{ code = -2; out = @("$name timed out after $TimeoutMs ms (stopped)") }
        }
        $p.WaitForExit()
        $text = "$($o.Result)`n$($e.Result)"
        return @{ code = $p.ExitCode; out = @($text -split "`r?`n" | Where-Object { $_.Trim() }) }
    } finally { $p.Dispose() }
}
# A real System32 tool: the guard, then a timed child process.
function Invoke-RealTool([string] $exeName, [string[]] $list) {
    if ($env:POWER_MODE_TEST -ceq '1') {
        [Console]::Out.WriteLine("power mode: COULD NOT RUN: POWER_MODE_TEST is set and no stand-in names $exeName (a test never starts the real one)")
        exit 2
    }
    return Invoke-TimedTool (Join-Path ([Environment]::GetFolderPath('System')) $exeName) $list (Get-ToolTimeoutMs)
}
function Invoke-PowerModeSeam([string] $standIn, [string] $exe, [string[]] $list) {
    if (-not $standIn) { return Invoke-RealTool $exe $list }
    if ($standIn -notlike '*.ps1') { return Invoke-TimedTool $standIn $list (Get-ToolTimeoutMs) }
    try {
        $global:LASTEXITCODE = 0
        $out = & $standIn @list 2>&1
        return @{ code = $LASTEXITCODE; out = @($out | ForEach-Object { "$_" } | Where-Object { $_.Trim() }) }
    } catch { return @{ code = -1; out = @($_.Exception.Message) } }
}
function Invoke-PowerCfg([string[]] $list) { Invoke-PowerModeSeam $env:POWER_MODE_POWERCFG 'powercfg.exe' $list }
function Invoke-SchTasks([string[]] $list) { Invoke-PowerModeSeam $env:POWER_MODE_SCHTASKS 'schtasks.exe' $list }
function Get-LastLine($result) { if ($result.out.Count) { $result.out[-1] } else { "exit $($result.code)" } }
# The third seam, nvidia-smi. A simulated machine (a powercfg or schtasks stand-in is named) that names no nvidia-smi
# stand-in never reaches the real GPU: Test-GpuSeam is false there and the callers skip the GPU without a word.
function Test-GpuSeam { return [bool] $env:POWER_MODE_NVIDIASMI -or -not ($env:POWER_MODE_POWERCFG -or $env:POWER_MODE_SCHTASKS) }
function Invoke-NvidiaSmi([string[]] $list) {
    if (-not (Test-GpuSeam)) { return @{ code = -1; out = @('nvidia-smi is not reached from a simulated machine') } }
    Invoke-PowerModeSeam $env:POWER_MODE_NVIDIASMI 'nvidia-smi.exe' $list
}
function Format-Watts($w) { return ([double] $w).ToString('0.##', [Globalization.CultureInfo]::InvariantCulture) }

# Setting the GPU power limit needs administrator rights. An elevated caller runs `nvidia-smi -pl` itself; any other
# caller (the station service) starts one of two tasks `gpu-task install` registered in an elevated shell, each of
# which runs System32 nvidia-smi with a wattage fixed at install - nothing a caller writes reaches them - and reads the
# limit back. POWER_MODE_ELEVATED = 1 | 0 (test seam) answers the elevation question here; install never takes it. The
# task names ($script:GpuCapTask, $script:GpuRestoreTask) come from POWER_MODE_TASK_PREFIX at the top of this file.
function Test-RealElevation {
    try { return (New-Object Security.Principal.WindowsPrincipal ([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) }
    catch { return $false }
}
function Test-PowerModeElevated {
    if ($env:POWER_MODE_ELEVATED -ceq '1') { return $true }
    if ($env:POWER_MODE_ELEVATED -ceq '0') { return $false }
    return (Test-RealElevation)
}
# A GPU task as registered: @{ state = ok | missing | foreign; watts }. ok only for the action install writes.
function Get-GpuTaskWatts([string] $name) {
    $r = Invoke-SchTasks @('/query', '/tn', $name, '/xml')
    if ($r.code -ne 0) { return @{ state = 'missing'; watts = $null } }
    return (ConvertFrom-GpuTaskXml ($r.out -join "`n"))
}
# The power limit in force, watts · $null = no answer. Needs no elevation.
function Get-GpuLimitNow {
    $r = Invoke-NvidiaSmi @('--query-gpu=power.limit', '--format=csv,noheader,nounits')
    if ($r.code -ne 0) { return $null }
    $n = 0.0
    if (-not [double]::TryParse("$(@($r.out)[0])".Trim(), [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref] $n)) { return $null }
    return $n
}
# $Kind cap | restore -> @{ reached; applied; timedOut; why }. Elevated: `nvidia-smi -pl $watts`. Not elevated: the
# cap or restore task runs its own fixed wattage (applied), confirmed by a read for up to 5 s (POWER_MODE_GPU_WAIT_MS,
# test seam). timedOut = the set may or may not have happened: a caller keeps what is owed.
function Set-GpuLimit($watts, [string] $Kind) {
    if (Test-PowerModeElevated) {
        $r = Invoke-NvidiaSmi @('-pl', (Format-Watts $watts))
        if ($r.code -eq 0) { return @{ reached = $true; applied = [double] $watts; timedOut = $false; why = '' } }
        return @{ reached = $false; applied = $null; timedOut = ($r.code -eq -2); why = (Get-LastLine $r) }
    }
    $task = if ($Kind -ceq 'cap') { $script:GpuCapTask } else { $script:GpuRestoreTask }
    $t = Get-GpuTaskWatts $task
    if ($t.state -cne 'ok') {
        $what = if ($t.state -ceq 'missing') { 'is not registered' } else { 'is not the one gpu-task install registers' }
        return @{ reached = $false; applied = $null; timedOut = $false; why = "not elevated and the task $task ${what}: run power-mode.ps1 gpu-task install in an elevated shell" } }
    $r = Invoke-SchTasks @('/run', '/tn', $task)
    if ($r.code -ne 0) { return @{ reached = $false; applied = $null; timedOut = ($r.code -eq -2); why = "the task $task could not be started: $(Get-LastLine $r)" } }
    $waitMs = 5000
    if ($env:POWER_MODE_GPU_WAIT_MS -match '^\d{1,5}$') { $waitMs = [int] $env:POWER_MODE_GPU_WAIT_MS }
    $deadline = [DateTime]::UtcNow.AddMilliseconds($waitMs)
    $seen = $null
    do {
        $seen = Get-GpuLimitNow
        if ($null -ne $seen -and [Math]::Abs($seen - $t.watts) -lt 0.5) { return @{ reached = $true; applied = [double] $t.watts; timedOut = $false; why = '' } }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    $reads = 'nothing'
    if ($null -ne $seen) { $reads = "$(Format-Watts $seen) W" }
    return @{ reached = $false; applied = $null; timedOut = $true; why = "the task $task ran but the limit reads $reads, not $($t.watts) W" }
}

# The power plans: the three built-in ones by name, and the plan the machine returns to when none was remembered.
$script:BalancedPlan = '381b4222-f694-41f0-9685-ff5bb260df2e'
$script:HighPlan = '8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c'
$script:SaverPlan = 'a1841308-3541-4fab-bc81-f71556f20b4a'
$script:BestPerformanceOverlay = 'ded574b5-45a0-4f42-8737-46345c09c238'
$script:PlanGuidRe = '^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$'

# `balanced` | `high` | `saver` | a plan GUID -> the GUID in lower case · '' = neither.
function Resolve-PlanAlias([string] $text) {
    if ($text -ceq 'balanced') { return $script:BalancedPlan }
    if ($text -ceq 'high') { return $script:HighPlan }
    if ($text -ceq 'saver') { return $script:SaverPlan }
    $g = $text.ToLowerInvariant()
    if ($g -match $script:PlanGuidRe) { return $g }
    return ''
}
# A plan's name: the built-in three in English, another plan as powercfg lists it, else its GUID.
function Get-PlanName([string] $plan, $plans = $null) {
    if ($plan -ceq $script:BalancedPlan) { return 'Balanced' }
    if ($plan -ceq $script:HighPlan) { return 'High performance' }
    if ($plan -ceq $script:SaverPlan) { return 'Power saver' }
    if ($plans -and $plans.names -and $plans.names[$plan]) { return $plans.names[$plan] }
    return $plan
}
function Get-PlanLabel([string] $plan, $plans = $null) {
    $name = Get-PlanName $plan $plans
    if ($name -ceq $plan) { return $plan }
    return "$name ($plan)"
}

# @{ all; active; names } from `powercfg /list` (the active plan's line ends with `*` in every UI language) · $null = no answer.
function Get-PowerPlans {
    $r = Invoke-PowerCfg @('/list')
    if ($r.code -ne 0) { return $null }
    $all = @(); $active = ''; $names = @{}
    foreach ($line in $r.out) {
        if ($line -notmatch '([0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12})\s*(\(([^)]*)\))?') { continue }
        $g = $Matches[1].ToLowerInvariant()
        $all += $g
        if ($Matches[4]) { $names[$g] = $Matches[4].Trim() }
        if ($line.TrimEnd().EndsWith('*')) { $active = $g }
    }
    return @{ all = $all; active = $active; names = $names }
}

# power-mode.config.json beside the flag: { "returnPlan": "high" | "balanced" | "previous" | "<plan GUID>", "gpuCap": true | false }.
# -> @{ returnPlan (a GUID, or `previous`; High performance when not named); gpuCap (true when not named); warn (lines) }.
# A missing file is the defaults; a file or a key that cannot be read is the default plus a `warn:` line, never a failure.
function Read-PowerModeConfig([string] $FlagPath) {
    $cfg = @{ returnPlan = $script:HighPlan; gpuCap = $true; warn = @() }
    $file = Join-Path ([IO.Path]::GetDirectoryName($FlagPath)) 'power-mode.config.json'
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { return $cfg }
    $j = $null
    try { $j = ConvertFrom-PowerModeJson (Read-PowerModeFlagText $file) } catch { $j = $null }
    if ($null -eq $j) { $cfg.warn += "warn: power-mode.config.json is not a JSON object: the defaults apply (High performance, GPU cap on)"; return $cfg }
    if ($j.ContainsKey('returnPlan')) {
        $plan = ''
        if ($j['returnPlan'] -is [string]) { if ($j['returnPlan'] -ceq 'previous') { $plan = 'previous' } else { $plan = Resolve-PlanAlias $j['returnPlan'] } }
        if ($plan -and $plan -cne $script:SaverPlan) { $cfg.returnPlan = $plan }
        else { $cfg.warn += "warn: power-mode.config.json: returnPlan is not high, balanced, previous or a plan GUID (never Power saver): High performance applies" }
    }
    if ($j.ContainsKey('gpuCap')) {
        if ($j['gpuCap'] -is [bool]) { $cfg.gpuCap = $j['gpuCap'] } else { $cfg.warn += "warn: power-mode.config.json: gpuCap is not true or false: the GPU cap stays on" }
    }
    return $cfg
}

# The plan the end of eco sets: @{ target; warn }. The config's returnPlan - High performance unless named; `previous` =
# $prev, the plan recorded before eco (`prevPlan`), or High performance when that is missing, Power saver or not on this
# machine. A target that is not on this machine = Balanced, with a `warn:` line. $plans = Get-PowerPlans ($null = no
# answer: the target is tried as is).
function Resolve-ReturnPlan([string] $prev, $cfg, $plans) {
    $want = $cfg.returnPlan
    if ($want -ceq 'previous') {
        if ($prev -and $prev -cne $script:SaverPlan -and (-not $plans -or $plans.all -ccontains $prev)) { $want = $prev } else { $want = $script:HighPlan }
    }
    if (-not $plans -or $plans.all -ccontains $want) { return @{ target = $want; warn = '' } }
    return @{ target = $script:BalancedPlan; warn = "warn: the power plan $(Get-PlanLabel $want $plans) is not on this machine: Balanced is used instead" }
}

# Whether a flag owes a plan, as the marker Restore-Plan takes: `prevPlan`; for an eco flag that carries none (written
# before eco always recorded one) High performance, but only while Power saver is the active plan - eco never switched
# anything else on. '' = nothing owed.
function Get-OwedPlan($flag, $cfg, $plans = $null) {
    if ($flag.prevPlan) { return $flag.prevPlan }
    if ($flag.raw -cne 'eco') { return '' }
    if (-not $plans) { $plans = Get-PowerPlans }
    if ($plans -and $plans.active -ceq $script:SaverPlan) { return $script:HighPlan }
    return ''
}

# The owed plan paid: the plan Resolve-ReturnPlan names is set; Balanced also gets the Windows 11 power-mode overlay
# "Best performance" (both work as the interactive user, no elevation). $plan = the plan recorded before eco, '' =
# nothing owed, nothing done. Returns '' = done · the plan = a step failed and the debt stays in the flag for the
# next `off`.
function Restore-Plan([string] $plan, [ref] $notes, $cfg = $null) {
    if (-not $plan) { return '' }
    if (-not $cfg) { $cfg = @{ returnPlan = $script:HighPlan } }
    $plans = Get-PowerPlans
    $to = Resolve-ReturnPlan $plan $cfg $plans
    if ($to.warn) { $notes.Value += $to.warn }
    $name = Get-PlanName $to.target $plans
    $r = Invoke-PowerCfg @('/setactive', $to.target)
    if ($r.code -ne 0) { $notes.Value += "warn: the $name plan could not be set ($(Get-LastLine $r)): it stays owed in the flag, run off again"; return $plan }
    if ($to.target -cne $script:BalancedPlan) { $notes.Value += "plan: $name"; return '' }
    $r = Invoke-PowerCfg @('/overlaysetactive', $script:BestPerformanceOverlay)
    if ($r.code -ne 0) { $notes.Value += "warn: the Best performance power mode could not be set ($(Get-LastLine $r)): it stays owed in the flag, run off again"; return $plan }
    $notes.Value += 'plan: Balanced, Best performance'
    return ''
}

# The GPU's power limits in watts, first GPU: @{ limit; min; default } · @{ error } when nvidia-smi is missing or fails.
function Get-GpuLimits {
    $r = Invoke-NvidiaSmi @('--query-gpu=power.limit,power.min_limit,power.default_limit', '--format=csv,noheader,nounits')
    if ($r.code -ne 0) { return @{ error = (Get-LastLine $r) } }
    $cells = @("$(@($r.out)[0])" -split ',' | ForEach-Object { $_.Trim() })
    $v = @()
    foreach ($c in $cells) {
        $n = 0.0
        if (-not [double]::TryParse($c, [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref] $n) -or $n -le 0) { return @{ error = "nvidia-smi did not report the power limits: $(@($r.out)[0])" } }
        $v += $n
    }
    if ($v.Count -ne 3) { return @{ error = "nvidia-smi did not report the power limits: $(@($r.out)[0])" } }
    return @{ limit = $v[0]; min = $v[1]; default = $v[2] }
}

# The GPU power limit back when eco ends: `prevGpuLimitW` of the flag. A flag with none recorded never touches the GPU
# (an eco flag says so in a note). Nothing else on the GPU is ever touched. Returns $null = nothing owed any more · the
# watts = still owed (refused, not confirmed, or the restore task's fixed value is another), kept for the next `off`.
function Restore-GpuLimit($flag, $cfg, [ref] $notes) {
    $target = $flag.prevGpuLimitW
    if (-not $target) {
        if ($flag.raw -ceq 'eco' -and $cfg.gpuCap -and (Test-GpuSeam)) { $notes.Value += 'note: gpu: this eco recorded no power limit to return to: the GPU is left as it is' }
        return $null
    }
    if (-not (Test-GpuSeam)) { return $target }
    $r = Set-GpuLimit $target 'restore'
    if ($r.reached -and [Math]::Abs($r.applied - $target) -lt 0.5) { $notes.Value += "gpu: power limit back to $(Format-Watts $target) W"; return $null }
    if ($r.reached) {
        $notes.Value += "note: the GPU power limit is $(Format-Watts $r.applied) W (the fixed value of $($script:GpuRestoreTask)), not the recorded $(Format-Watts $target) W: it stays owed in the flag; an elevated shell pays it: power-mode.ps1 off"
        return $target
    }
    if ($r.timedOut) { $notes.Value += "note: the GPU power limit was not confirmed at $(Format-Watts $target) W ($($r.why)): it stays owed in the flag, run off again"; return $target }
    $notes.Value += "note: the GPU power limit could not be set back to $(Format-Watts $target) W ($($r.why)): it stays owed in the flag, run off again"
    return $target
}

# The cap again when the limit drifted above it while eco is on (a driver re-initialisation puts the default back):
# one read; a set and a note only when the live limit is above the recorded cap. The flag is not written.
function Invoke-GpuRecap($flag, $cfg, [ref] $notes) {
    if (-not $flag.prevGpuLimitW -or -not $flag.gpuCapW -or -not $cfg.gpuCap -or -not (Test-GpuSeam)) { return }
    $live = Get-GpuLimitNow
    if ($null -eq $live -or $live -le $flag.gpuCapW + 0.5) { return }
    $r = Set-GpuLimit $flag.gpuCapW 'cap'
    if ($r.reached) { $notes.Value += "note: gpu cap re-applied: the limit read $(Format-Watts $live) W, now $(Format-Watts $r.applied) W" }
    else { $notes.Value += "note: gpu cap: the limit reads $(Format-Watts $live) W, above the cap $(Format-Watts $flag.gpuCapW) W, and could not be set again ($($r.why))" }
}

# A task that runs a script under Windows PowerShell 5.1 as the interactive user, only while they are logged on - never
# as SYSTEM: the scripts and the config are the user's to write. No window: conhost.exe
# --headless hosts the console. A run missed while they were logged off (or the PC off) starts at the next logon. Battery
# power never holds it back: a PC on a battery station may report as on battery. Registering it needs no elevation.
#   -Script / -ScriptArguments   what powershell.exe runs (-File <Script> <ScriptArguments>)
#   -TriggersXml                 the content of <Triggers>
function New-PowerModeTaskXml([string] $Description, [string] $Script, [string] $ScriptArguments, [string] $TriggersXml) {
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $conhost = Join-Path $env:SystemRoot 'System32\conhost.exe'
    $ps51 = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $arguments = "--headless `"$ps51`" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$Script`" $ScriptArguments".TrimEnd()
    return @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo><Description>$([Security.SecurityElement]::Escape($Description))</Description></RegistrationInfo>
  <Triggers>$TriggersXml</Triggers>
  <Principals><Principal id="Author"><UserId>$sid</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <StartWhenAvailable>true</StartWhenAvailable>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>true</Hidden>
    <ExecutionTimeLimit>PT5M</ExecutionTimeLimit>
  </Settings>
  <Actions Context="Author"><Exec><Command>$([Security.SecurityElement]::Escape($conhost))</Command><Arguments>$([Security.SecurityElement]::Escape($arguments))</Arguments></Exec></Actions>
</Task>
"@
}

# The one-shot trigger of the eco end task.
function New-EcoEndTriggerXml([DateTimeOffset] $At) {
    return "<TimeTrigger><StartBoundary>$($At.ToString("yyyy-MM-dd'T'HH:mm:sszzz", [Globalization.CultureInfo]::InvariantCulture))</StartBoundary><Enabled>true</Enabled></TimeTrigger>"
}

# The two GPU tasks (gpu-task install). The action is System32 nvidia-smi with a wattage fixed at install, hosted by
# System32 conhost.exe --headless (no window): no path, argument or file a non-elevated process can write. The user's
# account, highest run level, no trigger (started on demand by `schtasks /run`), a second start while one runs is
# ignored, one minute at most. Both paths come from the OS folder, never from an environment variable.
function New-GpuTaskXml([string] $Name, [int] $Watts) {
    $sys = [Environment]::GetFolderPath('System')
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $conhost = Join-Path $sys 'conhost.exe'
    $arguments = "--headless `"$(Join-Path $sys 'nvidia-smi.exe')`" -pl $Watts"
    return @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo><Description>$([Security.SecurityElement]::Escape("power-mode.ps1: GPU power limit $Watts W ($Name)"))</Description></RegistrationInfo>
  <Principals><Principal id="Author"><UserId>$sid</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>true</Hidden>
    <ExecutionTimeLimit>PT1M</ExecutionTimeLimit>
  </Settings>
  <Actions Context="Author"><Exec><Command>$([Security.SecurityElement]::Escape($conhost))</Command><Arguments>$([Security.SecurityElement]::Escape($arguments))</Arguments></Exec></Actions>
</Task>
"@
}
# Who may touch a GPU task: Administrators and SYSTEM full control, the user read and run (schtasks /run) - never
# change or delete it from a process that is not elevated.
function Get-GpuTaskSddl {
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    return "D:P(A;;FA;;;BA)(A;;FA;;;SY)(A;;FRFX;;;$sid)"
}
# Registers a GPU task from its XML text through the Task Scheduler's COM API (Schedule.Service RegisterTask, create
# or update, interactive token): no file on disk, no PowerShell module. @{ code; out }, never throws. Never under a
# test (POWER_MODE_TEST): there is no stand-in for it.
function Register-GpuTask([string] $Name, [string] $Xml) {
    if ($env:POWER_MODE_TEST -ceq '1') {
        [Console]::Out.WriteLine('power mode: COULD NOT RUN: POWER_MODE_TEST is set: a test never registers a task')
        exit 2
    }
    try {
        $svc = New-Object -ComObject 'Schedule.Service'
        $svc.Connect()
        # 6 = TASK_CREATE_OR_UPDATE, 3 = TASK_LOGON_INTERACTIVE_TOKEN
        $null = $svc.GetFolder('\').RegisterTask($Name, $Xml, 6, $null, $null, 3, (Get-GpuTaskSddl))
        return @{ code = 0; out = @("registered $Name") }
    } catch { return @{ code = -1; out = @($_.Exception.Message) } }
}
# A GPU task's XML (as written, or as `schtasks /query /xml` prints it) -> @{ state = ok | foreign; watts }: ok only
# for exactly the action New-GpuTaskXml writes.
function ConvertFrom-GpuTaskXml([string] $Text) {
    $un = { param($s) $s.Replace('&quot;', '"').Replace('&apos;', "'").Replace('&lt;', '<').Replace('&gt;', '>').Replace('&amp;', '&') }
    $sys = [Environment]::GetFolderPath('System')
    if ($Text -notmatch '<Command>([^<]*)</Command>') { return @{ state = 'foreign'; watts = $null } }
    $cmd = (& $un $Matches[1]).Trim()
    if ($Text -notmatch '<Arguments>([^<]*)</Arguments>') { return @{ state = 'foreign'; watts = $null } }
    $arg = (& $un $Matches[1]).Trim()
    # A task that would run without elevation (a name squatted before install) is not ours either.
    if ($Text -notmatch '<RunLevel>HighestAvailable</RunLevel>') { return @{ state = 'foreign'; watts = $null } }
    if ($cmd -ine (Join-Path $sys 'conhost.exe') -or $arg -notmatch '^--headless "([^"]+)" -pl ([1-9][0-9]{0,3})$' -or $Matches[1] -ine (Join-Path $sys 'nvidia-smi.exe')) {
        return @{ state = 'foreign'; watts = $null } }
    return @{ state = 'ok'; watts = [int] $Matches[2] }
}
# What install registers: @{ cap; restore } from the card's limits @{ min; max; default } and an optional -RestoreWatts
# (an integer inside min..max) · @{ error }.
function Resolve-GpuTaskWatts($Limits, [string] $RestoreWatts) {
    $min = [int] [Math]::Ceiling([double] $Limits.min); $max = [int] [Math]::Floor([double] $Limits.max)
    $restore = [int] [Math]::Round([double] $Limits.default)
    if ($RestoreWatts) {
        if ($RestoreWatts -notmatch '^[1-9][0-9]{0,3}$') { return @{ error = "-RestoreWatts '$RestoreWatts' is not a whole number of watts" } }
        $restore = [int] $RestoreWatts
    }
    if ($min -le 0 -or $max -lt $min) { return @{ error = "the card reported no usable power limit range ($($Limits.min)..$($Limits.max) W)" } }
    if ($restore -lt $min -or $restore -gt $max) { return @{ error = "the restore limit $restore W is outside the card's $min..$max W" } }
    return @{ cap = $min; restore = $restore }
}
