<#
test-wake-sddl.ps1 - the access-list read-back of the wake task (Test-WatcherWakeTaskSddl, tools/wake-task.ps1)
against fixed SDDL strings, the wake task's name rules, and its register / remove against a fake Task Scheduler (a task that\nis not ours is never deleted). Registers nothing, touches nothing.

  pwsh -NoProfile -File tools/test-wake-sddl.ps1

Exit: 0 every case passed · 1 a case failed · 2 did not run.
#>
$ErrorActionPreference = 'Stop'
$lib = Join-Path $PSScriptRoot 'wake-task.ps1'
if (-not (Test-Path -LiteralPath $lib)) { Write-Output "test-wake-sddl: COULD NOT RUN: no $lib"; exit 2 }
try {
    . $lib
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
}
catch { Write-Output "test-wake-sddl: COULD NOT RUN: $($_.Exception.Message)"; exit 2 }

$script:failed = 0
$script:passed = 0
function Expect([string] $name, [string] $sddl, [bool] $ok, [string] $names = '') {
    $verdict = Test-WatcherWakeTaskSddl $sddl
    $good = if ($ok) { $verdict -ceq 'ok' } else { $verdict.StartsWith('foreign: ') -and (-not $names -or $verdict.Contains($names)) }
    if ($good) { $script:passed++ } else { $script:failed++; Write-Output "FAIL  $name -> $verdict ($sddl)" }
}

$ours = "(A;;FA;;;BA)(A;;FA;;;SY)(A;;FA;;;$sid)"
try {
    # ours, as written and as Windows may give it back
    Expect 'ours as written' (Get-WatcherWakeTaskSddl) $true
    Expect 'ours with owner and group' "O:BAG:SYD:P$ours" $true
    Expect 'ours by SID' "D:P(A;;FA;;;S-1-5-32-544)(A;;FA;;;S-1-5-18)(A;;0x1f01ff;;;$sid)" $true
    Expect 'ours plus a SACL that is never read' "D:P${ours}S:(AU;FA;FA;;;WD)" $true
    # what Task Scheduler adds on top of ours: read-only and inherited entries
    Expect 'real world on top of ours' "D:AI$ours(A;;FR;;;$sid)(A;ID;0x1f019f;;;BA)(A;ID;0x1f019f;;;SY)(A;ID;FA;;;BA)(A;;FR;;;S-1-5-21-1-1001)" $true
    Expect 'read-only grants to others' "D:P$ours(A;;FR;;;WD)(A;;GR;;;AU)(A;;0x120089;;;S-1-5-32-545)(A;;0x80000000;;;IU)" $true
    Expect 'inherited 0x1f019f counts as full' "D:(A;ID;0x1f019f;;;BA)(A;ID;0x1f019f;;;SY)(A;;FA;;;$sid)" $true

    # other entry types and any deny
    Expect 'object allow' "D:P$ours(OA;;FA;;;WD)" $false 'OA;;FA;;;WD'
    Expect 'callback allow' "D:P$ours(XA;;FA;;;WD)" $false 'XA;;FA;;;WD'
    Expect 'lower-case type' "D:P$ours(a;;FA;;;WD)" $false 'a;;FA;;;WD'
    Expect 'lower-case type, read only' "D:P$ours(a;;FR;;;WD)" $false 'a;;FR;;;WD'
    Expect 'object read with a GUID' "D:P$ours(A;;FR;bf967aba-0de6-11d0-a285-00aa003049e2;;WD)" $false 'bf967aba'
    Expect 'audit in the DACL' "D:P$ours(AU;;FA;;;WD)" $false 'AU;;FA;;;WD'
    Expect 'deny Administrators' "D:P(D;;FA;;;BA)$ours" $false 'D;;FA;;;BA'
    Expect 'deny the user' "D:P(D;;FA;;;$sid)$ours" $false "D;;FA;;;$sid"
    Expect 'deny someone else' "D:P$ours(D;;FR;;;WD)" $false 'D;;FR;;;WD'
    # masks and rights
    Expect 'mask over 32 bits' "D:P$ours(A;;0x100120089;;;WD)" $false '0x100120089'
    Expect 'WRITE_DAC' "D:P$ours(A;;WD;;;WD)" $false 'A;;WD;;;WD'
    Expect 'WRITE_OWNER' "D:P$ours(A;;WO;;;AU)" $false 'A;;WO;;;AU'
    Expect 'GENERIC_WRITE' "D:P$ours(A;;GW;;;AU)" $false 'A;;GW;;;AU'
    Expect 'GENERIC_ALL' "D:P$ours(A;;GA;;;AU)" $false 'A;;GA;;;AU'
    Expect 'read plus a write bit' "D:P$ours(A;;0x1200a9;;;AU)" $false '0x1200a9'
    Expect 'read and execute' "D:P$ours(A;;FRFX;;;WD)" $false 'FRFX'
    Expect 'full to everyone' "D:P$ours(A;;FA;;;WD)" $false 'A;;FA;;;WD'
    Expect 'an unknown right' "D:P$ours(A;;CC;;;WD)" $false 'CC'
    Expect 'an unknown flag' "D:P$ours(A;SA;FR;;;WD)" $false 'SA'
    Expect 'a malformed entry' "D:P$ours(A;;FR;;WD)" $false 'A;;FR;;WD'
    # our three, each required with full control on the task itself
    Expect 'no Administrators' "D:P(A;;FA;;;SY)(A;;FA;;;$sid)" $false 'BA'
    Expect 'no SYSTEM' "D:P(A;;FA;;;BA)(A;;FA;;;$sid)" $false 'SY'
    Expect 'no owner' "D:P(A;;FA;;;BA)(A;;FA;;;SY)" $false 'owner'
    Expect 'owner read only' "D:P(A;;FA;;;BA)(A;;FA;;;SY)(A;;FR;;;$sid)" $false 'owner'
    Expect 'owner inherit-only' "D:P(A;;FA;;;BA)(A;;FA;;;SY)(A;IO;FA;;;$sid)" $false 'owner'
    Expect 'the real world alone' 'D:(A;ID;0x1f019f;;;BA)(A;ID;0x1f019f;;;SY)(A;ID;FA;;;BA)(A;;FR;;;S-1-5-21-1-1001)' $false 'owner'
    Expect 'no DACL' 'O:BAG:SY' $false
    Expect 'garbage' 'not an sddl' $false

    # ---- the wake task's name (config wakeTaskName)
    function ExpectName([string] $name, [bool] $ok, [string] $why = '') {
        $v = Test-WatcherWakeTaskName $name
        $good = if ($ok) { $v -ceq 'ok' } else { $v.StartsWith('refused: ') -and (-not $why -or $v.Contains($why)) }
        if ($good) { $script:passed++ } else { $script:failed++; Write-Output "FAIL  name '$name' -> $v" }
    }
    ExpectName 'BluettiWatcherWake' $true
    ExpectName 'My Wake-task_1.2' $true
    ExpectName ('n' * 100) $true
    ExpectName 'BluettiWatcher' $false "service's own task"
    ExpectName 'bluettiwatcher' $false "service's own task"
    ExpectName 'BLUETTIWATCHER' $false "service's own task"
    ExpectName '' $false
    ExpectName ('n' * 101) $false
    ExpectName 'a\b' $false
    ExpectName '\Microsoft\Windows\x' $false
    ExpectName 'x*' $false
    ExpectName 'a;b' $false
    ExpectName 'a"b' $false
    ExpectName "abc`n" $false
    ExpectName ' lead' $false 'starts or ends'
    ExpectName 'trail ' $false 'starts or ends'
    ExpectName 'trail.' $false 'starts or ends'
    ExpectName 'Wake-Ä' $false

    # ---- register / remove against a fake Task Scheduler (never the real one): a foreign task is never deleted
    $ourXml = New-WatcherWakeTaskXml
    $foreignXml = $ourXml.Replace('<WakeToRun>true</WakeToRun>', '<WakeToRun>false</WakeToRun>')
    $otherXml = '<?xml version="1.0"?><Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task"><Actions><Exec><Command>C:\tools\backup.exe</Command></Exec></Actions></Task>'
    # $existing: the XML of the task already there, or $null. After Register the task reads back as $afterRegister.
    function New-FakeOps($existing, $afterRegister, [string] $sddl = (Get-WatcherWakeTaskSddl)) {
        $s = @{ xml = $existing; calls = [Collections.Generic.List[string]]::new() }
        $ops = @{
            Query    = { param($n) $s.calls.Add("query $n"); $s.xml }.GetNewClosure()
            Delete   = { param($n) $s.calls.Add("delete $n"); $s.xml = $null; @{ code = 0; text = '' } }.GetNewClosure()
            Register = { param($n, $x, $d) $s.calls.Add("register $n"); $s.xml = $afterRegister }.GetNewClosure()
            GetSddl  = { param($n) $sddl }.GetNewClosure()
        }
        @{ Ops = $ops; State = $s }
    }
    function Calls($f, [string] $verb) { @($f.State.calls | Where-Object { $_ -like "$verb *" }).Count }
    function Test([string] $name, [bool] $cond, [string] $detail = '') {
        if ($cond) { $script:passed++ } else { $script:failed++; Write-Output "FAIL  $name $detail" }
    }

    $script:WatcherWakeTask = 'BluettiWatcher'
    $f = New-FakeOps $null $ourXml
    $r = Register-WatcherWakeTask -Ops $f.Ops
    Test 'register: the service task name is refused' (-not $r.ok -and $r.text -match "service's own task" -and (Calls $f 'register') -eq 0 -and (Calls $f 'delete') -eq 0 -and (Calls $f 'query') -eq 0) $r.text
    $r = Remove-WatcherWakeTask -Ops $f.Ops
    Test 'remove: the service task name is refused, nothing queried or deleted' (-not $r.ok -and (Calls $f 'delete') -eq 0 -and (Calls $f 'query') -eq 0) $r.text
    $script:WatcherWakeTask = 'bad*name'
    $f = New-FakeOps $null $ourXml
    $r = Register-WatcherWakeTask -Ops $f.Ops
    Test 'register: a bad name is refused' (-not $r.ok -and $r.text -match 'refused' -and (Calls $f 'register') -eq 0) $r.text

    $script:WatcherWakeTask = 'MyWake'
    $f = New-FakeOps $null $ourXml
    $r = Register-WatcherWakeTask -Ops $f.Ops
    Test 'register: no such task -> registered, nothing deleted' ($r.ok -and (Calls $f 'register') -eq 1 -and (Calls $f 'delete') -eq 0) $r.text
    $f = New-FakeOps $ourXml $ourXml
    $r = Register-WatcherWakeTask -Ops $f.Ops
    Test 'register: our own task is deleted, then registered again' ($r.ok -and (Calls $f 'delete') -eq 1 -and (Calls $f 'register') -eq 1) $r.text
    foreach ($case in @(@('a foreign task (WakeToRun false)', $foreignXml), @('an unrelated task', $otherXml), @('a task that is no XML', 'not xml'))) {
        $f = New-FakeOps $case[1] $ourXml
        $r = Register-WatcherWakeTask -Ops $f.Ops
        Test "register: $($case[0]) is not deleted nor replaced" (-not $r.ok -and (Calls $f 'delete') -eq 0 -and (Calls $f 'register') -eq 0 -and $r.text -match "'MyWake'" -and $r.text -match 'not touched') $r.text
        Test "register: $($case[0]) is still there" ($f.State.xml -ceq $case[1])
    }
    $f = New-FakeOps $null $foreignXml
    $r = Register-WatcherWakeTask -Ops $f.Ops
    Test 'register: a read-back that is not ours is deleted again' (-not $r.ok -and (Calls $f 'delete') -eq 1 -and $r.text -match 'not registered as written') $r.text
    $f = New-FakeOps $null $ourXml 'D:P(A;;FA;;;WD)'
    $r = Register-WatcherWakeTask -Ops $f.Ops
    Test 'register: an access list that is not ours is deleted again' (-not $r.ok -and (Calls $f 'delete') -eq 1) $r.text

    $f = New-FakeOps $null $ourXml
    $r = Remove-WatcherWakeTask -Ops $f.Ops
    Test 'remove: no such task is ok, nothing deleted' ($r.ok -and (Calls $f 'delete') -eq 0) $r.text
    $f = New-FakeOps $ourXml $ourXml
    $r = Remove-WatcherWakeTask -Ops $f.Ops
    Test 'remove: our task is deleted' ($r.ok -and (Calls $f 'delete') -eq 1) $r.text
    foreach ($case in @(@('foreign', $foreignXml), @('unrelated', $otherXml))) {
        $f = New-FakeOps $case[1] $ourXml
        $r = Remove-WatcherWakeTask -Ops $f.Ops
        Test "remove: a $($case[0]) task is not deleted" (-not $r.ok -and (Calls $f 'delete') -eq 0 -and $f.State.xml -ceq $case[1] -and $r.text -match "'MyWake'") $r.text
    }
}
catch {
    Write-Output "FAIL  unexpected: $($_.Exception.Message) at $($_.InvocationInfo.ScriptLineNumber)"
    $script:failed++
}
Write-Output "test-wake-sddl: $script:passed passed, $script:failed failed"
if ($script:failed -gt 0) { exit 1 }
if ($script:passed -eq 0) { exit 2 }
exit 0
