using BluettiWatcher.Decision;
using BluettiWatcher.Io;
using static BluettiWatcher.Tests.Fx;

namespace BluettiWatcher.Tests;

public class HibernateGuardTests
{
    /// <summary>A decision that proposes hibernate: a cut at 4 %, every earlier level fired, the warning 3 minutes back.</summary>
    private static StationDecisionResult Proposed()
    {
        var d = Step(Seq(Pre(4), Bat(4), Bat(4), Bat(4), Bat(4), Bat(4)), Station, [StationAction.Hibernate]);
        Assert.Contains(StationAction.Hibernate, d[^1].Actions);
        return d[^1];
    }

    private static DateTimeOffset Now => T0.AddMinutes(16).AddSeconds(20);

    [Fact]
    public void Not_proposed_refused() =>
        Assert.Equal("the decision did not propose it", HibernateGuard.BeforeRead(Step([Bat(4)])[0], killSwitch: false));

    [Fact]
    public void Kill_switch_refused() =>
        Assert.Equal("disabled by the kill switch", HibernateGuard.BeforeRead(Proposed(), killSwitch: true));

    [Fact]
    public void Proposed_and_no_kill_switch_goes_on_to_the_fresh_reading() =>
        Assert.Null(HibernateGuard.BeforeRead(Proposed(), killSwitch: false));

    // design.md §6 Battery levels and hibernate
    [Fact]
    public void No_fresh_reading_no_hibernate() =>
        Assert.Equal("no fresh reading", HibernateGuard.AfterRead(Proposed(), null, Now, Cfg()));

    // design.md §6 Battery levels and hibernate
    [Fact]
    public void Fresh_reading_on_mains_refused() =>
        Assert.Equal("not on battery", HibernateGuard.AfterRead(Proposed(), Grid(4), Now, Cfg()));

    // design.md §6 Battery levels and hibernate
    [Fact]
    public void Fresh_reading_with_unknown_grid_refused() =>
        Assert.Equal("not on battery", HibernateGuard.AfterRead(Proposed(), new Reading(4, 0, 220, "OTHER"), Now, Cfg()));

    // design.md §6 Battery levels and hibernate
    [Fact]
    public void Fresh_reading_above_the_threshold_refused() =>
        Assert.StartsWith("battery 6 % is above 5 %", HibernateGuard.AfterRead(Proposed(), Bat(6), Now, Cfg()), StringComparison.Ordinal);

    // design.md §6 Battery levels and hibernate
    [Fact]
    public void Fresh_reading_implausible_refused() =>
        Assert.StartsWith("the fresh reading is implausible", HibernateGuard.AfterRead(Proposed(), Bat(50), Now, Cfg()), StringComparison.Ordinal);

    // design.md §6 Battery levels and hibernate
    [Fact]
    public void Fresh_reading_incomplete_refused() =>
        Assert.StartsWith("the fresh reading is incomplete", HibernateGuard.AfterRead(Proposed(), Bat(4) with { OutAc = null }, Now, Cfg()), StringComparison.Ordinal);

    // design.md §6 Battery levels and hibernate
    [Fact]
    public void Fresh_reading_on_battery_at_the_threshold_hibernates() =>
        Assert.Null(HibernateGuard.AfterRead(Proposed(), Bat(5), Now, Cfg()));
}

public class BatteryWritePolicyTests
{
    private static readonly StationConfig C = new();

    private static BatteryWritten W(double soc, Level level, bool mains) => new(soc, level, mains, T0);

    [Fact]
    public void First_write_always() => Assert.True(BatteryWritePolicy.ShouldWrite(null, new BatteryReport(100, Level.Ok, true), T0, C));

    [Fact]
    public void Same_reading_on_mains_no_write() =>
        Assert.False(BatteryWritePolicy.ShouldWrite(W(100, Level.Ok, true), new BatteryReport(100, Level.Ok, true), T0.AddHours(3), C));

    // design.md §6 Battery levels and hibernate
    [Fact]
    public void Backoff_1_2_4_8_then_every_10_minutes()
    {
        Assert.Equal([1, 2, 4, 8, 10, 10, 10], Enumerable.Range(1, 7).Select(n => Backoff.Delay(n).TotalMinutes));
        var b = new Backoff();
        Assert.True(b.Open("x", T0));
        Assert.Equal(T0.AddMinutes(1), b.Failed("x", T0));
        Assert.False(b.Open("x", T0.AddSeconds(59)));
        Assert.True(b.Open("x", T0.AddMinutes(1)));
        Assert.True(b.Open("y", T0));
        b.Succeeded("x");
        Assert.Equal(T0.AddMinutes(1), b.Failed("x", T0));
    }

    [Fact]
    public void Mains_or_level_change_writes()
    {
        Assert.True(BatteryWritePolicy.ShouldWrite(W(80, Level.Ok, true), new BatteryReport(80, Level.Ok, false), T0.AddMinutes(1), C));
        Assert.True(BatteryWritePolicy.ShouldWrite(W(20, Level.Ok, false), new BatteryReport(20, Level.Low, false), T0.AddMinutes(1), C));
    }

    [Fact]
    public void Whole_percent_change_writes() =>
        Assert.True(BatteryWritePolicy.ShouldWrite(W(80, Level.Ok, false), new BatteryReport(79, Level.Ok, false), T0.AddMinutes(1), C));

    [Fact]
    public void On_battery_refreshed_before_the_hooks_15_minutes_run_out()
    {
        Assert.False(BatteryWritePolicy.ShouldWrite(W(80, Level.Ok, false), new BatteryReport(80, Level.Ok, false), T0.AddSeconds(599), C));
        Assert.True(BatteryWritePolicy.ShouldWrite(W(80, Level.Ok, false), new BatteryReport(80, Level.Ok, false), T0.AddSeconds(600), C));
    }
}

public class ReaderParseTests
{
    [Fact]
    public void Parses_field_lines_strips_units_and_the_output_mode_prefix()
    {
        var r = BluettiReader.Parse([
            "connecting...", "FieldName.BATTERY_SOC: 92.0 %", "FieldName.AC_INPUT_POWER: 0 W", "FieldName.AC_OUTPUT_POWER: 189",
            "FieldName.DC_OUTPUT_POWER: 0.0", "FieldName.INTERNAL_AC_VOLTAGE: 229.0V", "FieldName.INTERNAL_AC_FREQUENCY: 49.9 Hz",
            "FieldName.AC_OUTPUT_MODE: OutputMode.INVERTER_OUTPUT"]);
        Assert.NotNull(r);
        Assert.Equal(92, r.Soc);
        Assert.Equal(0, r.InAc);
        Assert.Equal(189, r.OutAc);
        Assert.Equal(229, r.Volt);
        Assert.Equal(49.9, r.Hz);
        Assert.Equal("INVERTER_OUTPUT", r.Mode);
        Assert.Null(r.InDc);
    }

    [Fact]
    public void No_field_lines_is_no_answer() => Assert.Null(BluettiReader.Parse(["Error: device not found", ""]));

    // design.md §3 Readings
    [Fact]
    public void Unparseable_value_stays_null_and_the_decision_calls_it_incomplete()
    {
        var r = BluettiReader.Parse(["FieldName.BATTERY_SOC: n/a", "FieldName.AC_INPUT_POWER: 0", "FieldName.AC_OUTPUT_POWER: 10", "FieldName.AC_OUTPUT_MODE: OutputMode.INVERTER_OUTPUT"]);
        Assert.Null(r!.Soc);
        Assert.StartsWith("incomplete (no valid SOC)", StationDecision.Check(r, new StationState(), T0, Cfg()), StringComparison.Ordinal);
    }
}

public sealed class WriterGuardTests : IDisposable
{
    private readonly string _venv = Path.Combine(Path.GetTempPath(), "bluetti-watcher-test-venv-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => Directory.Delete(_venv, recursive: true);

    private string Lib(params string[] rel) => Path.Combine([_venv, "Lib", "site-packages", "bluetti_bt_lib", .. rel]);

    // design.md §1 Goals and limits
    [Fact]
    public void Removes_entry_points_modules_compiled_copies_and_strips_the_imports()
    {
        Directory.CreateDirectory(Path.Combine(_venv, "Scripts"));
        Directory.CreateDirectory(Lib("scripts"));
        Directory.CreateDirectory(Lib("bluetooth", "__pycache__"));
        File.WriteAllText(Path.Combine(_venv, "Scripts", "bluetti-write.exe"), "x");
        File.WriteAllText(Path.Combine(_venv, "Scripts", "bluetti-read.exe"), "x");
        File.WriteAllText(Lib("scripts", "bluetti_write.py"), "x");
        File.WriteAllText(Lib("bluetooth", "device_writer.py"), "x");
        File.WriteAllText(Lib("bluetooth", "__pycache__", "device_writer.cpython-314.pyc"), "x");
        File.WriteAllText(Lib("bluetooth", "__pycache__", "device_reader.cpython-314.pyc"), "x");
        File.WriteAllText(Lib("bluetooth", "__init__.py"), "from .device_reader import *\nfrom .device_writer import *\n");
        File.WriteAllText(Lib("__init__.py"), "from .x import (\n    DeviceReader,\n    DeviceWriter,\n)\n");
        Assert.Equal(6, WriterGuard.Left(_venv).Count);

        var left = WriterGuard.Remove(_venv, _ => { });

        Assert.Empty(left);
        Assert.True(File.Exists(Path.Combine(_venv, "Scripts", "bluetti-read.exe")));
        Assert.True(File.Exists(Lib("bluetooth", "__pycache__", "device_reader.cpython-314.pyc")));
        Assert.Equal("from .device_reader import *\n", File.ReadAllText(Lib("bluetooth", "__init__.py")));
        Assert.Equal("from .x import (\n    DeviceReader,\n)\n", File.ReadAllText(Lib("__init__.py")));
    }

    // design.md §1 Goals and limits
    [WindowsFact]
    public void Writer_that_cannot_be_removed_is_reported()
    {
        Directory.CreateDirectory(Path.Combine(_venv, "Scripts"));
        string w = Path.Combine(_venv, "Scripts", "bluetti-write.exe");
        File.WriteAllText(w, "x");
        using (new FileStream(w, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Single(WriterGuard.Remove(_venv, _ => { }));
    }

    private BluettiReader Reader(FakeRunner runner, List<string> log) =>
        new(new StationConfig { ReaderPath = Path.Combine(_venv, "Scripts", "bluetti-read.exe") }, runner, log.Add);

    // design.md §1 and §3
    [Fact]
    public async Task Writer_reappearing_before_a_read_is_removed_and_the_read_runs()
    {
        Directory.CreateDirectory(Path.Combine(_venv, "Scripts"));
        string w = Path.Combine(_venv, "Scripts", "bluetti-write.exe");
        await File.WriteAllTextAsync(w, "x");
        var runner = new FakeRunner();
        await Reader(runner, []).ReadAsync(CancellationToken.None);
        Assert.False(File.Exists(w));
        Assert.Single(runner.Calls);
    }

    // design.md §1 Goals and limits
    [WindowsFact]
    public async Task Writer_that_cannot_be_removed_refuses_the_read_as_a_miss()
    {
        Directory.CreateDirectory(Path.Combine(_venv, "Scripts"));
        string w = Path.Combine(_venv, "Scripts", "bluetti-write.exe");
        await File.WriteAllTextAsync(w, "x");
        var runner = new FakeRunner();
        var log = new List<string>();
        Decision.Reading? r;
        using (new FileStream(w, FileMode.Open, FileAccess.Read, FileShare.None))
            r = await Reader(runner, log).ReadAsync(CancellationToken.None);
        Assert.Null(r);
        Assert.Empty(runner.Calls);
        Assert.Contains(log, l => l.Contains("read refused", StringComparison.Ordinal));
    }
}

public class FlagParseTests
{
    private static readonly DateTimeOffset Now = T0;

    [Fact]
    public void Station_eco_without_until_is_eco()
    {
        var f = FlagFileReader.Parse("""{ "mode": "eco", "since": "2026-10-06T19:00:00+03:00", "source": "station" }""", Now);
        Assert.Equal(new FlagView("eco", "station", "", T0.AddHours(-1)), f!.View);
        Assert.Equal("Eco mode while mains is off", FlagFileReader.EcoText(f, Now));
    }

    [Fact]
    public void Manual_eco_past_until_reads_normal()
    {
        var f = FlagFileReader.Parse("""{ "mode": "eco", "since": "2026-10-06T15:00:00+03:00", "until": "2026-10-06T19:00:00+03:00" }""", Now);
        Assert.Equal("normal", f!.Mode);
        Assert.True(f.Expired);
    }

    [Fact]
    public void Manual_off_carries_by()
    {
        var f = FlagFileReader.Parse("""{ "mode": "normal", "since": "2026-10-06T19:30:00+03:00", "by": "manual" }""", Now);
        Assert.Equal("manual", f!.By);
    }

    [Theory]
    [InlineData("""{ "mode": "turbo", "since": "2026-10-06T19:00:00+03:00" }""")]
    [InlineData("""{ "mode": "eco", "since": "yesterday" }""")]
    [InlineData("""{ "mode": "eco", "since": "2026-10-06T19:00:00+03:00" }""")]
    [InlineData("not json")]
    public void Invalid_flag_is_unreadable(string text) => Assert.Null(FlagFileReader.Parse(text, Now));

    // design.md §2 Architecture
    [Fact]
    public void Reader_takes_the_mutex_it_is_given() => Assert.Equal("Local\\X", new FlagFileReader("f.json", _ => { }, "Local\\X").MutexName);

    // design.md §2 Architecture: the flag is read under the writers' named mutex; a writer holding it makes the flag unreadable this poll.
    [Fact]
    public void Flag_whose_mutex_a_writer_holds_reads_null_and_the_log_says_so()
    {
        string name = "Local\\BluettiWatcherTest-" + Guid.NewGuid().ToString("N");   // never a live mutex
        string path = Path.Combine(Path.GetTempPath(), "power-mode-test-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, """{ "mode": "eco", "since": "2026-10-06T19:00:00+03:00", "source": "station" }""");
        try
        {
            var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
            var reader = new FlagFileReader(path, log.Enqueue, name, TimeSpan.FromMilliseconds(200));
            PowerModeFlag? ReadOnAnotherThread()
            {
                PowerModeFlag? f = null;
                var t = new Thread(() => f = reader.Read(Now));
                t.Start();
                t.Join();
                return f;
            }
            using var writer = new Mutex(true, name);   // a writer holds the lock on this thread
            Assert.Null(ReadOnAnotherThread());
            Assert.Contains(log, l => l.Contains("holds the lock", StringComparison.Ordinal));
            writer.ReleaseMutex();
            Assert.Equal("eco", ReadOnAnotherThread()!.Mode);   // released: readable again
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Missing_flag_file_reads_normal()
    {
        var r = new FlagFileReader(Path.Combine(Path.GetTempPath(), "power-mode-test-missing-" + Guid.NewGuid().ToString("N") + ".json"), _ => { }, "Local\\BluettiWatcherTest-" + Guid.NewGuid().ToString("N"));
        Assert.Equal("normal", r.Read(Now)!.Mode);
    }
}

public class PowerModeAdapterTests
{
    // design.md §5 and §2
    [Fact]
    public void Command_lines_are_the_switch_with_source_station_and_the_flag_path()
    {
        var cfg = new StationConfig { PowerMode = new() { Script = @"C:\t\power-mode.ps1" }, FlagPath = @"C:\t\flag.json" };
        var a = new PowerModeAdapter(cfg, new FakeRunner(), _ => { });
        Assert.Equal(["-NoProfile", "-NonInteractive", "-File", @"C:\t\power-mode.ps1", "eco", "-Source", "station", "-Path", @"C:\t\flag.json"], a.PowerModeCommand(PowerModeAdapter.EcoArgs));
        Assert.Equal(["on", "-Source", "station"], PowerModeAdapter.EmergencyArgs);
        Assert.Equal(["off", "-Source", "station"], PowerModeAdapter.LiftArgs);
        Assert.Equal(["battery", "-Soc", "19.5", "-Level", "low", "-Mains", "off", "-HibernateAt", "5"], PowerModeAdapter.BatteryArgs(new BatteryReport(19.5, Level.Low, false), 5));
    }

    [Fact]
    public async Task Dry_run_runs_nothing_and_reports_not_done()
    {
        var runner = new FakeRunner();
        var a = new PowerModeAdapter(new StationConfig { DryRun = true }, runner, _ => { });
        var r = await a.StationEcoAsync(CancellationToken.None);
        Assert.False(r.Ok);
        Assert.True(r.DryRun);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Exit_2_is_a_failed_action()
    {
        var runner = new FakeRunner { Exit = 2 };
        var r = await new PowerModeAdapter(new StationConfig(), runner, _ => { }).LiftAsync(CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task Last_line_is_the_last_output_line()
    {
        var runner = new FakeRunner { Lines = ["first", "middle", "power mode: eco"] };
        var r = await new PowerModeAdapter(new StationConfig(), runner, _ => { }).StationEcoAsync(CancellationToken.None);
        Assert.Equal("power mode: eco", r.LastLine);
    }
}

internal sealed class FakeRunner : IProcessRunner
{
    public int Exit { get; set; }
    public IReadOnlyList<string> Lines { get; set; } = ["power mode: fake"];
    public List<(string Exe, IReadOnlyList<string> Args)> Calls { get; } = [];
    public List<TimeSpan> Timeouts { get; } = [];
    public List<IReadOnlyDictionary<string, string>?> Envs { get; } = [];

    public Task<ProcessResult> RunAsync(string exe, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, IReadOnlyDictionary<string, string>? env = null)
    {
        Calls.Add((exe, args));
        Envs.Add(env);
        Timeouts.Add(timeout);
        return Task.FromResult(new ProcessResult(Exit, Lines, false));
    }
}
