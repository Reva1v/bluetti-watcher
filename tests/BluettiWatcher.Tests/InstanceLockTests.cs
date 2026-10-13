using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BluettiWatcher.Io;

namespace BluettiWatcher.Tests;

/// <summary>A task restart starts the new instance while the old one still holds the port's lock.</summary>
public sealed class InstanceLockTests : IDisposable
{
    // Only the lock's name comes from it: nothing here binds a port.
    private readonly int _port = System.Security.Cryptography.RandomNumberGenerator.GetInt32(40000, 60000);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bluetti-watcher-test-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _log = [];

    public InstanceLockTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>Another thread holds the lock (a mutex belongs to its thread) for <paramref name="hold"/>, then lets go or dies holding it.</summary>
    private Thread Hold(TimeSpan hold, bool release = true)
    {
        using var held = new ManualResetEventSlim();
        var t = new Thread(() =>
        {
            var m = new Mutex(false, InstanceLock.Name(_port), out _);
            m.WaitOne();
            held.Set();
            Thread.Sleep(hold);
            if (release) m.ReleaseMutex();
            m.Dispose();
        }) { IsBackground = true };
        t.Start();
        held.Wait();
        return t;
    }

    [Fact]
    public void Lock_name_is_the_one_the_running_watcher_takes()
    {
        Assert.Equal($"Local\\BluettiWatcher-{_port}", InstanceLock.Name(_port));
    }

    [Fact]
    public void Free_lock_is_taken_at_once()
    {
        using var m = InstanceLock.Take(_port, TimeSpan.FromSeconds(5), _log.Add);
        Assert.NotNull(m);
        Assert.Empty(_log);
    }

    [Fact]
    public void Lock_released_within_the_window_is_taken()
    {
        var holder = Hold(TimeSpan.FromMilliseconds(500));
        var sw = Stopwatch.StartNew();
        using var m = InstanceLock.Take(_port, TimeSpan.FromSeconds(10), _log.Add);
        Assert.NotNull(m);
        Assert.InRange(sw.Elapsed, TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(9));
        Assert.Contains(_log, l => l.StartsWith("another instance holds this port's lock - waiting up to 10 s", StringComparison.Ordinal));
        Assert.Contains("the port's lock was released - starting", _log);
        holder.Join();
    }

    [Fact]
    public void Lock_abandoned_by_a_killed_holder_is_taken()
    {
        var holder = Hold(TimeSpan.FromMilliseconds(300), release: false);
        using var m = InstanceLock.Take(_port, TimeSpan.FromSeconds(10), _log.Add);
        Assert.NotNull(m);
        holder.Join();
    }

    [Fact]
    public void Lock_held_past_the_window_gives_up()
    {
        var holder = Hold(TimeSpan.FromSeconds(3));
        var sw = Stopwatch.StartNew();
        var m = InstanceLock.Take(_port, TimeSpan.FromMilliseconds(500), _log.Add);
        Assert.Null(m);
        Assert.InRange(sw.Elapsed, TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(2.5));
        holder.Join();
    }

    /// <summary>Runs the watcher exe with the lock waiting <paramref name="lockWaitSec"/> and no reader: it never gets to the port.</summary>
    private (int Exit, string Log) RunWatcher(int lockWaitSec)
    {
        string config = Path.Combine(_dir, "config.json");
        string reader = Path.Combine(_dir, "no-venv", "Scripts", "bluetti-read.exe").Replace("\\", "\\\\", StringComparison.Ordinal);
        File.WriteAllText(config, $$"""{"port": {{_port}}, "lockWaitSec": {{lockWaitSec}}, "mac": "00:11:22:33:44:55", "readerPath": "{{reader}}"}""");
        var psi = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "BluettiWatcher.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "--config", config, "--data-dir", _dir, "--port", _port.ToString(System.Globalization.CultureInfo.InvariantCulture) },
        };
        using var p = Process.Start(psi)!;
        if (!p.WaitForExit(TimeSpan.FromSeconds(60))) { p.Kill(); Assert.Fail("the watcher did not exit"); }
        return (p.ExitCode, File.ReadAllText(Path.Combine(_dir, "service.log")));
    }

    [WindowsFact]
    public void Watcher_held_past_the_window_exits_non_zero()
    {
        var holder = Hold(TimeSpan.FromSeconds(4));
        var (exit, log) = RunWatcher(lockWaitSec: 1);
        Assert.Equal(2, exit);
        Assert.Contains("another instance holds this port's lock - waiting up to 1 s", log, StringComparison.Ordinal);
        Assert.Contains("DID NOT RUN - another instance still holds this port's lock after 1 s", log, StringComparison.Ordinal);
        holder.Join();
    }

    [WindowsFact]
    public void Watcher_waits_for_the_old_instance_and_goes_on()
    {
        var holder = Hold(TimeSpan.FromSeconds(1));
        var (_, log) = RunWatcher(lockWaitSec: 20);
        Assert.Contains("the port's lock was released - starting", log, StringComparison.Ordinal);
        Assert.DoesNotContain("still holds this port's lock", log, StringComparison.Ordinal);
        // Past the lock it stops at the next check: no reader in the temp dir.
        Assert.Contains("DID NOT RUN - no reader at", log, StringComparison.Ordinal);
        holder.Join();
    }

    [WindowsFact]
    public void Watcher_with_a_negative_wait_gives_up_at_once()
    {
        var holder = Hold(TimeSpan.FromSeconds(3));
        var (exit, log) = RunWatcher(lockWaitSec: -5);
        Assert.Equal(2, exit);
        Assert.Contains("DID NOT RUN - another instance still holds this port's lock after 0 s", log, StringComparison.Ordinal);
        holder.Join();
    }
}

/// <summary>
/// install.ps1 stops what runs from bin (by its path, so no name can drift from the exe) and waits for it before the swap;
/// its logon task comes back after any exit.
/// </summary>
public partial class InstallScriptTests
{
    private static string Repo([System.Runtime.CompilerServices.CallerFilePath] string here = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));

    private static string Text(params string[] path) => File.ReadAllText(Path.Combine([Repo(), .. path]));

    [GeneratedRegex(@"<AssemblyName>([^<]+)</AssemblyName>")]
    private static partial Regex AssemblyName();

    [GeneratedRegex(@"^\$exe = Join-Path \$bin '([^']+)'\r?$", RegexOptions.Multiline)]
    private static partial Regex InstallExe();

    [GeneratedRegex(@"^\$exe = Join-Path \$Out '([^']+)'\r?$", RegexOptions.Multiline)]
    private static partial Regex PublishExe();

    [GeneratedRegex(@"^\$script:WatcherExe = '([^']+)'\r?$", RegexOptions.Multiline)]
    private static partial Regex SwapExe();

    [Fact]
    public void Install_publish_and_swap_name_the_exe_the_project_builds()
    {
        string name = AssemblyName().Match(Text("src", "BluettiWatcher", "BluettiWatcher.csproj")).Groups[1].Value;
        Assert.NotEmpty(name);
        Assert.Equal(name + ".exe", InstallExe().Match(Text("install.ps1")).Groups[1].Value);
        Assert.Equal(name + ".exe", PublishExe().Match(Text("tools", "publish.ps1")).Groups[1].Value);
        Assert.Equal(name + ".exe", SwapExe().Match(Text("tools", "swap.ps1")).Groups[1].Value);
    }

    [Fact]
    public void Install_stops_and_waits_for_what_runs_from_bin_before_the_swap()
    {
        // No process is looked up by a name that can drift from the exe: by the directory it runs from only.
        foreach (string f in new[] { "install.ps1", Path.Combine("tools", "swap.ps1") })
            Assert.DoesNotContain("Get-Process -Name", Text(f), StringComparison.Ordinal);

        string swap = Text("tools", "swap.ps1");
        int fn = swap.IndexOf("function Install-WatcherBinaries", StringComparison.Ordinal);
        Assert.True(fn > 0);
        Assert.Contains("[int] $TimeoutSec = 30", swap[fn..], StringComparison.Ordinal);
        int stop = swap.IndexOf("& $Ops.StopTask $TaskName", fn, StringComparison.Ordinal);
        int kill = swap.IndexOf("& $Ops.KillIn $Bin", stop, StringComparison.Ordinal);
        int wait = swap.IndexOf("Wait-WatcherBinFree -Dir $Bin", kill, StringComparison.Ordinal);
        int refuse = swap.IndexOf("if ($busy) { throw \"$Bin is still in use after $TimeoutSec s ($busy)\" }", wait, StringComparison.Ordinal);
        int move = swap.IndexOf("& $Ops.MoveDir $Bin $aside", refuse, StringComparison.Ordinal);
        Assert.True(stop > fn && kill > stop && wait > kill && refuse > wait && move > refuse, "stop, kill, wait, refuse, then the swap");

        // The publish goes to staging, never into the live bin; a failed swap is exit 1.
        string install = Text("install.ps1");
        int publish = install.IndexOf("'-Out', $staging)", StringComparison.Ordinal);
        int binaries = install.IndexOf("Install-WatcherBinaries -Bin $bin", StringComparison.Ordinal);
        Assert.True(publish > 0 && binaries > publish, "publish to staging, then the swap");
        Assert.Contains("catch { Stop-Install $_.Exception.Message }", install[binaries..], StringComparison.Ordinal);
        int stopInstall = install.IndexOf("function Stop-Install", StringComparison.Ordinal);
        Assert.Contains("    exit 1\n}", install[stopInstall..binaries].Replace("\r", "", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void Install_registers_the_task_swap_defines()
    {
        string install = Text("install.ps1");
        Assert.Contains("$def = New-WatcherTaskDefinition -Exe $exe -Port $Port", install, StringComparison.Ordinal);
        Assert.Contains("-Action $def.Action -Trigger $def.Trigger -Principal $def.Principal -Settings $def.Settings", install, StringComparison.Ordinal);
        Assert.DoesNotContain("New-ScheduledTaskTrigger", install, StringComparison.Ordinal);
    }

    /// <summary>The definition built for real (ScheduledTasks, in memory: New-ScheduledTask registers nothing).</summary>
    [WindowsFact]
    public void Task_comes_back_every_5_minutes_beside_the_logon_trigger_one_instance()
    {
        string swap = Path.Combine(Repo(), "tools", "swap.ps1");
        var psi = new ProcessStartInfo("pwsh.exe") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8 };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-Command",
            $". '{swap}'; $d = New-WatcherTaskDefinition -Exe 'C:\\x\\BluettiWatcher.exe' -Port 18999 -WorkingDirectory 'C:\\x' -User \"$env:USERDOMAIN\\$env:USERNAME\"; " +
            "$t = New-ScheduledTask -Action $d.Action -Trigger $d.Trigger -Principal $d.Principal -Settings $d.Settings; " +
            "[pscustomobject]@{ " +
            "Triggers = @($t.Triggers | ForEach-Object { [pscustomobject]@{ Kind = $_.CimClass.CimClassName; Enabled = $_.Enabled; Interval = \"$($_.Repetition.Interval)\"; Duration = \"$($_.Repetition.Duration)\"; Stop = $_.Repetition.StopAtDurationEnd } }); " +
            "Multiple = \"$($t.Settings.MultipleInstances)\"; RestartCount = $t.Settings.RestartCount; RestartInterval = \"$($t.Settings.RestartInterval)\"; Limit = \"$($t.Settings.ExecutionTimeLimit)\"; " +
            "Exe = $t.Actions[0].Execute; Args = $t.Actions[0].Arguments } | ConvertTo-Json -Depth 4 -Compress" })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd();
        string errors = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, errors);
        using var json = JsonDocument.Parse(output);
        var root = json.RootElement;
        var triggers = root.GetProperty("Triggers").EnumerateArray().ToList();
        Assert.Equal(2, triggers.Count);
        Assert.Equal("MSFT_TaskLogonTrigger", triggers[0].GetProperty("Kind").GetString());
        var repeat = triggers[1];
        Assert.Equal("MSFT_TaskTimeTrigger", repeat.GetProperty("Kind").GetString());
        Assert.True(repeat.GetProperty("Enabled").GetBoolean());
        Assert.Equal("PT5M", repeat.GetProperty("Interval").GetString());
        // No duration: it repeats indefinitely.
        Assert.Equal("", repeat.GetProperty("Duration").GetString());
        Assert.Equal("IgnoreNew", root.GetProperty("Multiple").GetString());
        Assert.Equal(999, root.GetProperty("RestartCount").GetInt32());
        Assert.Equal("PT1M", root.GetProperty("RestartInterval").GetString());
        Assert.Equal("PT0S", root.GetProperty("Limit").GetString());
        Assert.Equal(@"C:\x\BluettiWatcher.exe", root.GetProperty("Exe").GetString());
        Assert.Equal("--port 18999", root.GetProperty("Args").GetString());
    }
}
