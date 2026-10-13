<#
wake-task.ps1 - the scheduled task that wakes the hibernated PC during a cut (docs/design.md, section 8). Functions only: install.ps1
dot-sources it; the C# test WakeTests builds the XML through it without registering anything. Dot-sourcing changes
nothing.

The task: "BluettiWatcherWake", the user's account (SID), interactive token (no stored password), least privilege,
WakeToRun, registered DISABLED with one time trigger in the past; the service sets the trigger's time and enables it
just before it hibernates the PC, and disables it on mains. The action is a no-op: System32 cmd /c exit 0 hosted by
System32 conhost --headless (no window). Security: Administrators and SYSTEM full control, the user full control -
the service runs without elevation and must change the trigger; what it can change runs as the user, unelevated, as
anything the user could register.
#>

$script:WatcherWakeTask = 'BluettiWatcherWake'
# The logon task install.ps1 registers: the wake task may never carry its name.
$script:WatcherServiceTask = 'BluettiWatcher'

function New-WatcherWakeTaskXml {
    $sys = [Environment]::GetFolderPath('System')
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $conhost = Join-Path $sys 'conhost.exe'
    $arguments = "--headless `"$(Join-Path $sys 'cmd.exe')`" /d /c exit 0"
    $esc = { param($s) [Security.SecurityElement]::Escape($s) }
    return @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo><Description>Bluetti Watcher: wakes the PC it hibernated during a power cut, to read the station (the action does nothing)</Description></RegistrationInfo>
  <Triggers><TimeTrigger><StartBoundary>2026-01-01T00:00:00</StartBoundary><Enabled>true</Enabled></TimeTrigger></Triggers>
  <Principals><Principal id="Author"><UserId>$sid</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <StartWhenAvailable>false</StartWhenAvailable>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>false</Enabled>
    <Hidden>true</Hidden>
    <WakeToRun>true</WakeToRun>
    <ExecutionTimeLimit>PT1M</ExecutionTimeLimit>
  </Settings>
  <Actions Context="Author"><Exec><Command>$(& $esc $conhost)</Command><Arguments>$(& $esc $arguments)</Arguments></Exec></Actions>
</Task>
"@
}

# Who may touch the wake task: Administrators and SYSTEM, and the user (the unelevated service sets its time).
function Get-WatcherWakeTaskSddl {
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    return "D:P(A;;FA;;;BA)(A;;FA;;;SY)(A;;FA;;;$sid)"
}

# A wake task's XML (as written, or as `schtasks /query /xml` prints it) -> 'ok' | 'foreign: <why>'. ok only for: the
# task disabled, WakeToRun, StartWhenAvailable false (absent = false), one TimeTrigger without a repetition, the user
# (SID or DOMAIN\user) with an interactive token and least privilege (absent = least), exactly one action: the Exec of
# the no-op New-WatcherWakeTaskXml writes.
function Test-WatcherWakeTaskXml([string] $Text) {
    $sys = [Environment]::GetFolderPath('System')
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    try { $x = [xml] $Text } catch { return 'foreign: not XML' }
    $t = $x.Task
    if (-not $t) { return 'foreign: no Task' }
    $s = $t.Settings
    if ("$($s.Enabled)" -cne 'false') { return 'foreign: not disabled' }
    if ("$($s.WakeToRun)" -cne 'true') { return 'foreign: WakeToRun is not true' }
    if ($s.StartWhenAvailable -and "$($s.StartWhenAvailable)" -cne 'false') { return 'foreign: StartWhenAvailable' }
    $triggers = @($t.Triggers.ChildNodes | Where-Object NodeType -eq 'Element')
    if ($triggers.Count -ne 1 -or $triggers[0].LocalName -cne 'TimeTrigger') { return 'foreign: not exactly one TimeTrigger' }
    if ($triggers[0].Repetition) { return 'foreign: a repetition' }
    $p = @($t.Principals.ChildNodes | Where-Object NodeType -eq 'Element')
    if ($p.Count -ne 1) { return 'foreign: not one principal' }
    if ("$($p[0].UserId)" -cne $sid -and "$($p[0].UserId)" -ine "$env:USERDOMAIN\$env:USERNAME") { return 'foreign: another user' }
    if ("$($p[0].LogonType)" -cne 'InteractiveToken') { return 'foreign: not an interactive token' }
    if ($p[0].RunLevel -and "$($p[0].RunLevel)" -cne 'LeastPrivilege') { return 'foreign: elevated' }
    $actions = @($t.Actions.ChildNodes | Where-Object NodeType -eq 'Element')
    if ($actions.Count -ne 1 -or $actions[0].LocalName -cne 'Exec') { return 'foreign: not exactly one Exec action' }
    if ("$($actions[0].Command)".Trim() -ine (Join-Path $sys 'conhost.exe')) { return 'foreign: another command' }
    if ("$($actions[0].Arguments)".Trim() -ine "--headless `"$(Join-Path $sys 'cmd.exe')`" /d /c exit 0") { return 'foreign: other arguments' }
    return 'ok'
}

# The access list read back (SDDL; only its DACL is read) -> 'ok' | 'foreign: <why>', a whitelist over every entry of the
# DACL. Allowed are only:
#   (a) type A (exactly, case-sensitive) granting full control - FA, GA, or a mask holding 0x1f019f (Windows writes
#       0x1f01ff and 0x1f019f) - to Administrators (BA or S-1-5-32-544), SYSTEM (SY or S-1-5-18) or the user's SID;
#       all three are required, each by an entry that applies to the task itself (not inherit-only);
#   (b) type A granting reading only - FR, GR, or a mask inside 0x80120089 - to any account.
# Flags: ID (inherited) is taken as any other entry; IO (inherit-only) applies to no task, so it never counts as our
# full control, and it is still held to (a) or (b); CI, OI, NP are inheritance only. Any other type (D deny, OA, XA,
# a, AU audit, ...), any other flag, any right outside FA FR FW FX GA GR GW GX RC SD WD WO or a mask over 32 bits, a
# grant beyond reading to an account not ours, or a malformed entry refuses the task, the message naming the entry.
# A deny is refused too: one against our accounts would keep the service from disarming the wake.
function Test-WatcherWakeTaskSddl([string] $Sddl) {
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    # Case-sensitive: SDDL writes its aliases in capitals.
    $alias = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    $alias['BA'] = 'BA'; $alias['S-1-5-32-544'] = 'BA'; $alias['SY'] = 'SY'; $alias['S-1-5-18'] = 'SY'; $alias[$sid] = 'owner'
    $codes = @{ FA = 0x1F01FFL; FR = 0x120089L; FW = 0x120116L; FX = 0x1200A0L; GA = 0x10000000L; GR = 0x80000000L; GW = 0x40000000L; GX = 0x20000000L
        RC = 0x20000L; SD = 0x10000L; WD = 0x40000L; WO = 0x80000L }
    $readOnly = 0x80120089L
    # O: and G: (a SID or an alias), then D: with its flags and entries, then an optional S: that is never read.
    if ($Sddl.Trim() -cnotmatch '^(?:O:(?:S-[0-9-]+|[A-Z]{2}))?(?:G:(?:S-[0-9-]+|[A-Z]{2}))?D:[A-Z]*((?:\([^()]*\))*)(?:S:.*)?$') { return "foreign: not an access list that can be read ($Sddl)" }
    $dacl = $Matches[1]
    $full = @{}
    foreach ($m in [regex]::Matches($dacl, '\(([^()]*)\)')) {
        $ace = $m.Groups[1].Value
        $f = $ace.Split(';')
        if ($f.Count -ne 6) { return "foreign: a malformed entry ($ace)" }
        if ($f[0] -cne 'A') { return "foreign: an entry of type $($f[0]) ($ace)" }
        if ($f[3] -or $f[4]) { return "foreign: an object entry ($ace)" }
        $flags = @([regex]::Matches($f[1], '..') | ForEach-Object Value)
        if ($f[1].Length % 2 -ne 0 -or @($flags | Where-Object { $_ -cnotin @('ID', 'IO', 'CI', 'OI', 'NP') }).Count) { return "foreign: flags $($f[1]) ($ace)" }
        $rights = $f[2]
        [long] $mask = 0
        if ($rights -cmatch '^0x[0-9a-fA-F]{1,8}$') { $mask = [Convert]::ToInt64($rights.Substring(2), 16) }
        elseif ($rights -cmatch '^([A-Z]{2})+$') {
            foreach ($c in [regex]::Matches($rights, '..')) {
                if (-not $codes.ContainsKey($c.Value)) { return "foreign: the right $($c.Value) ($ace)" }
                $mask = $mask -bor $codes[$c.Value]
            }
        }
        else { return "foreign: rights $rights ($ace)" }
        $who = if ($alias.ContainsKey($f[5])) { $alias[$f[5]] } else { $null }
        if ($who) {
            $isFull = ($mask -band 0x10000000L) -ne 0 -or ($mask -band 0x1F019FL) -eq 0x1F019FL
            if ($isFull -and $flags -cnotcontains 'IO') { $full[$who] = $true }
            continue
        }
        if (($mask -band (0xFFFFFFFFL -bxor $readOnly)) -ne 0) { return "foreign: $($f[5]) may $rights ($ace)" }
    }
    $missing = @('BA', 'SY', 'owner') | Where-Object { -not $full[$_] }
    if ($missing) { return "foreign: no full control for $($missing -join ', ') in $Sddl" }
    return 'ok'
}

# The name config wakeTaskName may give the wake task -> 'ok' | 'refused: <why>'. Only letters, digits, space, dot,
# underscore and hyphen (no folder, no wildcard, no quote), 1 to 100 characters, no space or dot at either end, and
# never the service's own task: registering over it would replace the watcher with the no-op.
function Test-WatcherWakeTaskName([string] $Name, [string] $ServiceTask = $script:WatcherServiceTask) {
    if ($Name -cnotmatch '^[A-Za-z0-9 ._-]{1,100}\z') { return "refused: the wake task name '$Name' is not 1 to 100 characters of letters, digits, space, '.', '_' and '-'" }
    if ($Name -cne $Name.Trim(' ', '.')) { return "refused: the wake task name '$Name' starts or ends with a space or a dot" }
    if ($Name -ieq $ServiceTask) { return "refused: the wake task name '$Name' is the service's own task" }
    return 'ok'
}

# The Task Scheduler operations of the wake task. The tests pass fakes with the same keys.
#   Query name -> the task's XML text, or $null when there is no such task
#   Delete name -> @{ code; text }
#   Register name xml sddl -> registers (create or update, interactive token, its own security); throws on failure
#   GetSddl name -> the DACL of the registered task as SDDL
function Get-WatcherWakeTaskOps {
    @{
        Query    = { param($name) $r = (& schtasks.exe /query /tn $name /xml 2>&1) -join "`n"; if ($LASTEXITCODE -eq 0) { $r } else { $null } }
        Delete   = { param($name) $r = & schtasks.exe /delete /tn $name /f 2>&1; @{ code = $LASTEXITCODE; text = "$(@($r)[-1])" } }
        # 6 = TASK_CREATE_OR_UPDATE, 3 = TASK_LOGON_INTERACTIVE_TOKEN
        Register = { param($name, $xml, $sddl) $svc = New-Object -ComObject 'Schedule.Service'; $svc.Connect(); $null = $svc.GetFolder('\').RegisterTask($name, $xml, 6, $null, $null, 3, $sddl) }
        # 4 = DACL_SECURITY_INFORMATION
        GetSddl  = { param($name) $svc = New-Object -ComObject 'Schedule.Service'; $svc.Connect(); $svc.GetFolder('\').GetTask($name).GetSecurityDescriptor(4) }
    }
}

# A task of this name that is not ours is never replaced or deleted. -> $null when the name is free (no such task, or
# the one this installer registered: Test-WatcherWakeTaskXml says ok), else the message that names it.
function Get-WatcherWakeTaskHold([string] $Name, [hashtable] $Ops) {
    $xml = & $Ops.Query $Name
    if ($null -eq $xml) { return $null }
    $v = Test-WatcherWakeTaskXml $xml
    if ($v -ceq 'ok') { return $null }
    return "a task named '$Name' exists and is not the wake task this installer registers ($v); it was not touched. Set wakeTaskName in config.json to another name, or remove that task yourself"
}

# Registers the wake task from its XML text through the Task Scheduler's COM API (create or update, interactive token,
# its own security): no file on disk. The name is checked first (Test-WatcherWakeTaskName); a task of that name goes
# first (its security is never inherited) only when it is ours, a foreign one stops the registration; what was
# registered is read back, the XML and the access list: a task that is not exactly ours is deleted again.
# @{ ok; text }, never throws.
function Register-WatcherWakeTask([hashtable] $Ops = $null) {
    if (-not $Ops) { $Ops = Get-WatcherWakeTaskOps }
    $name = $script:WatcherWakeTask
    try {
        $bad = Test-WatcherWakeTaskName $name
        if ($bad -cne 'ok') { return @{ ok = $false; text = "wake task not registered: $bad" } }
        $held = Get-WatcherWakeTaskHold $name $Ops
        if ($held) { return @{ ok = $false; text = "wake task not registered: $held" } }
        if ($null -ne (& $Ops.Query $name)) {
            $d = & $Ops.Delete $name
            if ($d.code -ne 0) { return @{ ok = $false; text = "$name not registered: the old one could not be deleted ($($d.text))" } }
        }
        $null = & $Ops.Register $name (New-WatcherWakeTaskXml) (Get-WatcherWakeTaskSddl)
        $back = & $Ops.Query $name
        $xml = Test-WatcherWakeTaskXml "$back"
        $sddl = & $Ops.GetSddl $name
        $acl = Test-WatcherWakeTaskSddl $sddl
        if ($xml -ceq 'ok' -and $acl -ceq 'ok') {
            return @{ ok = $true; text = "registered ${name}: disabled, WakeToRun, one TimeTrigger, $env:USERDOMAIN\$env:USERNAME, LeastPrivilege, one no-op action; access $sddl" }
        }
        $null = & $Ops.Delete $name
        return @{ ok = $false; text = "$name was not registered as written (read back: XML $xml, access $acl); removed" }
    } catch { return @{ ok = $false; text = "$name not registered: $($_.Exception.Message)" } }
}
# Removes the wake task, only one that is ours (a foreign task of that name stays). @{ ok; text }, never throws; a task
# that is not there is ok.
function Remove-WatcherWakeTask([hashtable] $Ops = $null) {
    if (-not $Ops) { $Ops = Get-WatcherWakeTaskOps }
    $name = $script:WatcherWakeTask
    try {
        $bad = Test-WatcherWakeTaskName $name
        if ($bad -cne 'ok') { return @{ ok = $false; text = "$name not removed: $bad" } }
        if ($null -eq (& $Ops.Query $name)) { return @{ ok = $true; text = "$name not registered" } }
        $held = Get-WatcherWakeTaskHold $name $Ops
        if ($held) { return @{ ok = $false; text = "$name not removed: $held" } }
        $r = & $Ops.Delete $name
        if ($r.code -eq 0) { return @{ ok = $true; text = "$name removed" } }
        return @{ ok = $false; text = "$name not removed: $($r.text)" }
    } catch { return @{ ok = $false; text = "$name not removed: $($_.Exception.Message)" } }
}
