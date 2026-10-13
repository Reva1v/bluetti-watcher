using System.Diagnostics;
using System.Text;
using System.Text.Json;
using BluettiWatcher.Decision;
using BluettiWatcher.Http;
using BluettiWatcher.Io;
using BluettiWatcher.Storage;
using static BluettiWatcher.Tests.Fx;

namespace BluettiWatcher.Tests;

/// <summary>A wake task that records what was asked, in one list with the hibernations.</summary>
internal sealed class FakeWakeTask(List<string> log) : IWakeTask
{
    public bool FailArm { get; set; }
    public bool FailDisarm { get; set; }

    public Task<WakeTaskResult> ArmAsync(DateTimeOffset at, CancellationToken ct)
    {
        log.Add($"arm {at:HH:mm}");
        return Task.FromResult(new WakeTaskResult(!FailArm, FailArm ? "Access is denied." : "armed"));
    }

    public Task<WakeTaskResult> DisarmAsync(CancellationToken ct, bool beforeHibernate = false)
    {
        log.Add(beforeHibernate ? "disarm before hibernate" : "disarm");
        return Task.FromResult(new WakeTaskResult(!FailDisarm, FailDisarm ? "Access is denied." : "disarmed"));
    }
}

/// <summary>A reader that answers by the read's place in the poll (0 = the poll's own, 1 = the first fresh reading, ...).</summary>
internal sealed class IndexedReader(Func<int, Reading?> answer) : IStationReader
{
    private int _n;
    public void NextPoll() => _n = 0;
    public Task<Reading?> ReadAsync(CancellationToken ct) => Task.FromResult(answer(_n++));
}

internal sealed class LoggingHibernator(List<string> log, ManualClock clock) : IHibernator
{
    public int Calls { get; private set; }

    public Task<bool> HibernateAsync(CancellationToken ct)
    {
        Calls++;
        log.Add($"hibernate {clock.Now:HH:mm}");
        return Task.FromResult(true);
    }
}

/// <summary>Waking the hibernated PC during a cut (design.md §8 Wake during a cut): the wake task armed before a hibernation, disarmed on mains.</summary>
public sealed class WakeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bluetti-watcher-test-" + Guid.NewGuid().ToString("N"));
    private readonly StationStore _store;
    private readonly ManualClock _clock = new(T0);
    private readonly QueueReader _reader = new();
    private readonly List<string> _log = [];
    private readonly FakeWakeTask _task;
    private readonly LoggingHibernator _hibernator;

    public WakeTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new StationStore(Path.Combine(_dir, "station.db"));
        _task = new FakeWakeTask(_log);
        _hibernator = new LoggingHibernator(_log, _clock);
    }

    public void Dispose()
    {
        _store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    /// <summary>No floor unless named: the readings here sit at 4 %, under the default floor.</summary>
    /// <summary>The scenarios count minutes against mains confirmations of 300 s both ways.</summary>
    private StationConfig Config(int every = 45, double? floor = 0) => new() { DataDir = _dir, WakeEveryMinutes = every, WakeFloorPercent = floor, MainsLostSec = 300, MainsBackSec = 300 };

    private (StationService Service, WakeKeeper Keeper) Service(int every = 45, double? floor = 0)
    {
        var cfg = Config(every, floor);
        var keeper = new WakeKeeper(cfg, _task, _store, _ => { });
        return (new StationService(cfg, _reader, new FakeActions(), _hibernator, new FixedFlag(Station), _store, _clock, _ => { }, keeper), keeper);
    }

    private async Task PollAt(StationService s, int minute, Reading? reading)
    {
        _clock.Now = T0.AddMinutes(minute);
        _reader.Next.Enqueue(reading);
        await s.PollAsync(CancellationToken.None);
    }

    /// <summary>On battery at 4 % every two minutes from T0 until the decision hibernates (T0+14, 20:14).</summary>
    private async Task Hibernated(StationService s)
    {
        _reader.Default = Bat(4);   // the fresh reading right before acting
        for (int m = 0; m <= 14; m += 2) await PollAt(s, m, Bat(4));
        Assert.Equal(1, _hibernator.Calls);
        _reader.Default = null;
    }

    private List<string> Kinds() => [.. _store.Events(0, long.MaxValue).Select(e => e.Kind)];

    // design.md §8 Wake during a cut (a)
    [Fact]
    public async Task The_wake_is_armed_every_minutes_ahead_just_before_the_hibernation()
    {
        var (s, keeper) = Service();
        using (s)
        {
            await Hibernated(s);
            Assert.Equal(["arm 20:59", "hibernate 20:14"], _log);
            Assert.Equal((true, T0.AddMinutes(59)), (keeper.Status.Armed, keeper.Status.WakeAt!.Value));
            Assert.Contains("wake-armed", Kinds());
        }
    }

    // design.md §8 Wake during a cut (b)
    [Fact]
    public async Task A_wake_that_cannot_be_armed_never_holds_the_hibernation_back()
    {
        _task.FailArm = true;
        var (s, keeper) = Service();
        using (s) await Hibernated(s);
        Assert.Equal(["arm 20:59", "hibernate 20:14"], _log);
        Assert.False(keeper.Status.Armed);
        Assert.Contains("wake-arm-failed", Kinds());
    }

    // design.md §8 Wake during a cut (a)
    [Fact]
    public async Task With_every_minutes_0_nothing_is_armed()
    {
        var (s, _) = Service(every: 0);
        using (s) await Hibernated(s);
        Assert.Equal(["hibernate 20:14"], _log);
    }

    // design.md §8 Wake during a cut (c)
    [Fact]
    public async Task Mains_back_during_the_wake_keeps_the_PC_awake_and_turns_the_wake_off()
    {
        var (s, keeper) = Service();
        using (s)
        {
            await Hibernated(s);
            await PollAt(s, 59, Grid(4));        // the timer woke the PC: mains is back
            for (int m = 60; m <= 75; m++) await PollAt(s, m, Grid(5));
        }
        Assert.Equal(["arm 20:59", "hibernate 20:14", "disarm"], _log);
        Assert.False(keeper.Status.Armed);
        Assert.Contains("wake-disarmed", Kinds());
    }

    // design.md §8 Wake during a cut (d)
    [Fact]
    public async Task Still_a_cut_and_low_after_the_timer_wake_the_first_reading_hibernates_at_once_and_arms_the_next_wake()
    {
        var (s, keeper) = Service();
        using (s)
        {
            await Hibernated(s);
            _reader.Default = Bat(4);                 // the fresh reading right before acting
            await PollAt(s, 59, Bat(4));
        }
        Assert.Equal(["arm 20:59", "hibernate 20:14", "arm 21:44", "hibernate 20:59"], _log);
        Assert.Contains(_store.Events(0, long.MaxValue), e => e.Kind == "hibernate" && e.Detail.Contains("after the wake timer", StringComparison.Ordinal));
        Assert.True(keeper.Status.Armed);
    }

    // design.md §8 Wake during a cut (d): the re-hibernation takes its own fresh reading; mains on there refuses it and ends what the wake owed
    [Fact]
    public async Task After_the_timer_wake_a_fresh_reading_on_mains_refuses_the_rehibernation_and_ends_the_debt()
    {
        var (s, keeper) = Service();
        using (s)
        {
            await Hibernated(s);
            _reader.Next.Enqueue(Bat(4));
            _reader.Next.Enqueue(Grid(4));            // the fresh reading right before acting: mains is back
            _clock.Now = T0.AddMinutes(59);
            await s.PollAsync(CancellationToken.None);
            for (int m = 60; m <= 75; m++) await PollAt(s, m, null);   // the reader dies: nothing is owed any more
        }
        Assert.Equal(["arm 20:59", "hibernate 20:14", "disarm"], _log);
        Assert.False(keeper.Status.Armed);
        Assert.Contains(_store.Events(0, long.MaxValue), e => e.Kind == "hibernate-refused" && e.Detail.StartsWith("not on battery", StringComparison.Ordinal));
    }

    // design.md §8 Wake during a cut (d): a hibernation the decision proposed and its fresh reading refused is never followed, in the same poll,
    // by the re-hibernation on the older poll reading
    [Fact]
    public async Task A_hibernation_refused_by_its_fresh_reading_is_not_followed_by_one_on_the_older_reading()
    {
        bool woken = false;
        // before the wake: 4 % everywhere; after it: the poll 4 %, the first fresh reading 6 % (over the level), a third read 4 %
        var reader = new IndexedReader(i => !woken ? Bat(4) : i == 1 ? Bat(6) : Bat(4));
        var cfg = Config();
        var keeper = new WakeKeeper(cfg, _task, _store, _ => { });
        using (var s = new StationService(cfg, reader, new FakeActions(), _hibernator, new FixedFlag(Station), _store, _clock, _ => { }, keeper))
        {
            for (int m = 0; m <= 14; m += 2) { _clock.Now = T0.AddMinutes(m); reader.NextPoll(); await s.PollAsync(CancellationToken.None); }
            Assert.Equal(1, _hibernator.Calls);
            woken = true;
            for (int m = 59; m <= 80; m++) { _clock.Now = T0.AddMinutes(m); reader.NextPoll(); await s.PollAsync(CancellationToken.None); }
        }
        Assert.Equal(1, _hibernator.Calls);
        // the decision did propose and its own fresh reading refused it (the wake's refusals say "after the wake timer")
        Assert.Contains(_store.Events(0, long.MaxValue), e => e.Kind == "hibernate-refused" && !e.Detail.Contains("after the wake timer", StringComparison.Ordinal));
    }

    // design.md §8 Wake during a cut (d): a fresh reading the decision's hibernation took and rejected says nothing; the poll's accepted reading
    // still counts as read, so the no-reading rule never fires while readings arrive
    [Fact]
    public async Task A_rejected_fresh_reading_leaves_the_polls_accepted_reading_heard()
    {
        bool woken = false;
        // after the wake: the poll 4 %, every fresh reading 90 % (implausible next to 4 %: rejected)
        var reader = new IndexedReader(i => !woken ? Bat(4) : i == 0 ? Bat(4) : Bat(90));
        var cfg = Config();
        var keeper = new WakeKeeper(cfg, _task, _store, _ => { });
        using (var s = new StationService(cfg, reader, new FakeActions(), _hibernator, new FixedFlag(Station), _store, _clock, _ => { }, keeper))
        {
            for (int m = 0; m <= 14; m += 2) { _clock.Now = T0.AddMinutes(m); reader.NextPoll(); await s.PollAsync(CancellationToken.None); }
            Assert.Equal(1, _hibernator.Calls);
            woken = true;
            for (int m = 59; m <= 85; m++) { _clock.Now = T0.AddMinutes(m); reader.NextPoll(); await s.PollAsync(CancellationToken.None); }
        }
        Assert.Equal(1, _hibernator.Calls);
        var refused = _store.Events(0, long.MaxValue).Where(e => e.Kind == "hibernate-refused").ToList();
        Assert.Contains(refused, e => !e.Detail.Contains("after the wake timer", StringComparison.Ordinal));   // the decision proposed and its fresh reading was rejected
        Assert.DoesNotContain(_store.Events(0, long.MaxValue), e => e.Kind == "hibernate" && e.Detail.Contains("no reading", StringComparison.Ordinal));
    }
    // design.md §8 Wake during a cut (d)
    [Fact]
    public async Task Still_a_cut_and_low_after_a_wake_by_hand_the_decision_keeps_its_resume_delays()
    {
        var (s, _) = Service();
        using (s)
        {
            await Hibernated(s);
            _reader.Default = Bat(4);
            for (int m = 30; m <= 34; m++) await PollAt(s, m, Bat(4));   // woken by hand at 20:30
            Assert.Equal(1, _hibernator.Calls);
            for (int m = 35; m <= 50 && _hibernator.Calls < 2; m++) await PollAt(s, m, Bat(4));
        }
        Assert.Equal(2, _hibernator.Calls);   // later, by the decision
    }

    // design.md §8 Wake during a cut (a)
    [Fact]
    public async Task At_or_under_the_floor_no_wake_is_armed_and_an_earlier_one_is_turned_off()
    {
        var (s, keeper) = Service(floor: 4);
        using (s) await Hibernated(s);
        Assert.Equal(["hibernate 20:14"], _log);
        Assert.False(keeper.Status.Armed);
        Assert.Contains(_store.Events(0, long.MaxValue), e => e.Kind == "wake-not-armed" && e.Detail.StartsWith("wake not armed: battery 4 % at or under the floor 4 %", StringComparison.Ordinal));
        // bounded to 0..hibernateAt (5 by default); 0 = no floor
        Assert.Equal(5, new WakeKeeper(Config(floor: 50), _task, _store, _ => { }).Floor);
        Assert.Equal(0, new WakeKeeper(Config(floor: -3), _task, _store, _ => { }).Floor);
        Assert.Equal(0, new WakeKeeper(new StationConfig { DataDir = _dir }, _task, _store, _ => { }).Floor);                    // not set: hibernateAt 5 - 5
        Assert.Equal(10, new WakeKeeper(new StationConfig { DataDir = _dir, HibernateAt = 15 }, _task, _store, _ => { }).Floor);  // the live thresholds
        Assert.Equal(15, new WakeKeeper(new StationConfig { DataDir = _dir, HibernateAt = 15, WakeFloorPercent = 40 }, _task, _store, _ => { }).Floor);
    }

    // design.md §8 Wake during a cut (a): with the defaults (no floor set) the wake arms
    [Fact]
    public async Task With_the_default_floor_the_wake_arms()
    {
        var (s, keeper) = Service(floor: null);
        using (s) await Hibernated(s);
        Assert.Equal(["arm 20:59", "hibernate 20:14"], _log);
        Assert.Equal((0, true), (keeper.Floor, keeper.Status.Armed));
    }

    // design.md §8 Wake during a cut (a): a wake still ahead goes before a hibernation under the floor, held 15 s at most
    [Fact]
    public async Task Under_the_floor_a_wake_still_ahead_is_turned_off_before_the_hibernation()
    {
        var (s, keeper) = Service(floor: 3);
        using (s)
        {
            await Hibernated(s);                      // 4 %: over the floor, armed
            _reader.Default = Bat(3);
            await PollAt(s, 59, Bat(3));              // the timer woke it at 3 %
        }
        Assert.Equal(["arm 20:59", "hibernate 20:14", "disarm before hibernate", "hibernate 20:59"], _log);
        Assert.False(keeper.Status.Armed);
    }

    // design.md §8 Wake during a cut (e)
    [Theory]
    [InlineData(0, 300)]
    [InlineData(-5, 300)]
    [InlineData(5, 60)]
    [InlineData(600, 600)]
    [InlineData(999999, 3600)]
    public void The_read_bound_is_a_minute_to_an_hour(int configured, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), new WakeKeeper(new StationConfig { DataDir = _dir, WakeReadSec = configured }, _task, _store, _ => { }).ReadWithin);

    // design.md §8 Wake during a cut (c) - the normal path: the service has run on mains (nothing owed), then a cut, a wake, mains back
    [Fact]
    public async Task After_days_on_mains_a_cut_arms_the_wake_and_mains_back_turns_it_off()
    {
        var (s, keeper) = Service();
        using (s)
        {
            for (int m = -3 * 24 * 60; m < -3 * 24 * 60 + 5; m++) await PollAt(s, m, Grid(80));   // three days ago, on mains
            await PollAt(s, -60, Grid(30));
            Assert.Equal(["disarm"], _log);                                                         // the one owed by the start
            Assert.False(keeper.Status.Owed);
            await Hibernated(s);
            await PollAt(s, 59, Grid(4));
        }
        Assert.Equal(["disarm", "arm 20:59", "hibernate 20:14", "disarm"], _log);
        Assert.False(keeper.Status.Armed);
    }

    // design.md §8 Wake during a cut (b) - an arm that failed may have landed: it is turned off on mains all the same
    [Fact]
    public async Task A_failed_arm_owes_the_turn_off()
    {
        var (s, keeper) = Service();
        using (s)
        {
            await PollAt(s, -60, Grid(30));
            _task.FailArm = true;
            await Hibernated(s);
            Assert.True(keeper.Status.Owed);
            await PollAt(s, 30, Grid(4));
        }
        Assert.Equal(["disarm", "arm 20:59", "hibernate 20:14", "disarm"], _log);
    }

    // design.md §8 Wake during a cut (e) - readings that stop: the bound runs from the last one
    [Fact]
    public async Task After_a_timer_wake_the_no_reading_bound_runs_from_the_last_accepted_reading()
    {
        var (s, _) = Service();
        using (s)
        {
            await Hibernated(s);
            for (int m = 59; m <= 62; m++) await PollAt(s, m, Bat(6));   // above the level: the PC stays up
            for (int m = 63; m <= 66; m++) await PollAt(s, m, null);     // the reader dies
            Assert.Equal(1, _hibernator.Calls);
            await PollAt(s, 67, null);                                    // 5 minutes after the last reading
            Assert.Equal(2, _hibernator.Calls);
        }
    }

    // design.md §8 Wake during a cut (e)
    [Fact]
    public async Task A_wake_by_hand_after_the_timer_window_is_never_hibernated_without_a_reading()
    {
        var (s, _) = Service();
        using (s)
        {
            await Hibernated(s);
            for (int m = 75; m <= 100; m++) await PollAt(s, m, null);   // past 20:59 + 10 minutes: a wake by hand
        }
        Assert.Equal(1, _hibernator.Calls);
    }

    // design.md §8 Wake during a cut (e)
    [Fact]
    public async Task No_reading_within_the_bound_after_the_timer_woke_the_PC_hibernates_it_again_and_arms_the_next_wake()
    {
        var (s, keeper) = Service();
        using (s)
        {
            await Hibernated(s);
            for (int m = 59; m <= 63; m++) await PollAt(s, m, null);
            Assert.Equal(1, _hibernator.Calls);   // under 5 minutes: not yet
            await PollAt(s, 64, null);
            Assert.Equal(2, _hibernator.Calls);
            for (int m = 65; m <= 70; m++) await PollAt(s, m, null);   // the PC is gone; no second try at the poll rate
        }
        Assert.Equal(["arm 20:59", "hibernate 20:14", "arm 21:49", "hibernate 21:04"], _log);
        Assert.Contains(_store.Events(0, long.MaxValue), e => e.Kind == "hibernate" && e.Detail.Contains("no reading", StringComparison.Ordinal));
        Assert.True(keeper.Status.Armed);
    }

    // design.md §8 Wake during a cut (e)
    [Fact]
    public async Task A_wake_by_hand_long_before_the_timer_is_never_hibernated_without_a_reading()
    {
        var (s, _) = Service();
        using (s)
        {
            await Hibernated(s);
            for (int m = 30; m <= 45; m++) await PollAt(s, m, null);   // woken by hand at 20:30, the station silent
        }
        Assert.Equal(1, _hibernator.Calls);
    }

    // design.md §8 Wake during a cut (e)
    [Fact]
    public async Task The_kill_switch_holds_the_rehibernation_too()
    {
        var (s, _) = Service();
        using (s)
        {
            await Hibernated(s);
            await File.WriteAllTextAsync(Path.Combine(_dir, "no-hibernate"), "");
            for (int m = 59; m <= 66; m++) await PollAt(s, m, null);
        }
        Assert.Equal(1, _hibernator.Calls);
        Assert.Contains("hibernate-refused", Kinds());
    }

    // design.md §8 Wake during a cut (f)
    [Fact]
    public async Task A_restart_with_the_wake_armed_turns_it_off_at_the_first_reading_on_mains()
    {
        var (s, _) = Service();
        using (s) await Hibernated(s);
        _log.Clear();
        var (again, keeper) = Service();
        using (again)
        {
            Assert.True(keeper.Status.Armed);                     // read back from the store
            await PollAt(again, 70, Bat(4));                      // still a cut: kept
            Assert.Empty(_log);
            await PollAt(again, 71, Grid(4));
            await PollAt(again, 72, Grid(4));
        }
        Assert.Equal(["disarm"], _log);
        Assert.False(keeper.Status.Armed);
    }

    // design.md §8 Wake during a cut (f)
    [Fact]
    public async Task A_start_with_nothing_saved_still_turns_the_wake_off_once_a_crash_may_have_left_it_on()
    {
        var (s, keeper) = Service(every: 0);
        using (s)
        {
            await PollAt(s, 0, Grid(80));
            await PollAt(s, 1, Grid(80));
        }
        Assert.Equal(["disarm"], _log);
        Assert.False(keeper.Status.Owed);
    }

    // design.md §8 Wake during a cut (g)
    [Fact]
    public async Task A_failed_disarm_is_retried_with_the_backoff_and_shown_until_it_succeeds()
    {
        _task.FailDisarm = true;
        var (s, keeper) = Service();
        using (s)
        {
            for (int m = 0; m <= 7; m++) await PollAt(s, m, Grid(80));
            Assert.Equal(4, _log.Count(c => c == "disarm"));      // 0, 1, 3, 7
            Assert.Equal("Access is denied.", keeper.Status.Error);
            Assert.True(keeper.Status.Owed);
            var api = new StationApi(Config(), s, _store, _clock, T0);
            var now = await api.HandleAsync(new HttpRequest("GET", "/api/now", new Dictionary<string, string>(), new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Host"] = "127.0.0.1:18940" }, []), CancellationToken.None);
            using var doc = JsonDocument.Parse(now.Body);
            var wake = doc.RootElement.GetProperty("wake");
            Assert.Equal((45, false, true, "Access is denied."), (wake.GetProperty("everyMinutes").GetInt32(), wake.GetProperty("armed").GetBoolean(), wake.GetProperty("disarmOwed").GetBoolean(), wake.GetProperty("error").GetString()));
            Assert.Contains(doc.RootElement.GetProperty("notes").EnumerateArray(), n => n.GetProperty("code").GetString() == PageCodes.WakeDisarmFailed);
            _task.FailDisarm = false;
            for (int m = 8; m <= 20; m++) await PollAt(s, m, Grid(80));
        }
        Assert.Equal(5, _log.Count(c => c == "disarm"));          // 15: succeeded, then never again
        Assert.Equal(("", false), (keeper.Status.Error, keeper.Status.Owed));
    }

    // ---- the task operations behind the interface ----

    // design.md §8 Wake during a cut (h)
    [Fact]
    public async Task The_task_operations_run_pwsh_on_the_installed_task_and_a_dry_run_runs_nothing()
    {
        var runner = new FakeRunner { Lines = ["armed Ready"] };
        var task = new ScheduledWakeTask(new StationConfig(), runner, _ => { });
        var at = DateTimeOffset.Parse("2026-10-08T01:30:00+03:00", System.Globalization.CultureInfo.InvariantCulture);
        Assert.True((await task.ArmAsync(at, CancellationToken.None)).Ok);
        Assert.Equal(TimeSpan.FromSeconds(15), runner.Timeouts[0]);   // the arm holds a hibernation 15 s at most
        var (exe, args) = Assert.Single(runner.Calls);
        string script = args[^1];
        Assert.Equal(("pwsh.exe", "-Command"), (exe, args[^2]));
        Assert.Contains("'BluettiWatcherWake'", script, StringComparison.Ordinal);
        Assert.Contains("WakeToRun", script, StringComparison.Ordinal);
        Assert.Contains("'2026-10-08T01:30:00+03:00'", script, StringComparison.Ordinal);
        Assert.Contains("Set-ScheduledTask", script, StringComparison.Ordinal);
        await task.DisarmAsync(CancellationToken.None);
        Assert.Contains("Disable-ScheduledTask", runner.Calls[1].Args[^1], StringComparison.Ordinal);
        Assert.Equal(TimeSpan.FromSeconds(60), runner.Timeouts[1]);
        await task.DisarmAsync(CancellationToken.None, beforeHibernate: true);
        Assert.Equal(TimeSpan.FromSeconds(15), runner.Timeouts[2]);
        runner.Exit = 1;
        runner.Lines = ["Access is denied."];
        Assert.Equal((false, "Access is denied."), (await task.DisarmAsync(CancellationToken.None)) is var r ? (r.Ok, r.Detail) : default);
        var dry = new FakeRunner();
        Assert.True((await new ScheduledWakeTask(new StationConfig { DryRun = true }, dry, _ => { }).ArmAsync(at, CancellationToken.None)).Ok);
        Assert.Empty(dry.Calls);
    }

    private static string Repo([System.Runtime.CompilerServices.CallerFilePath] string here = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));

    /// <summary>The task install.ps1 registers, fixed here: {SID} the user, {SYS} System32.</summary>
    private const string ExpectedTask = """
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo><Description>Bluetti Watcher: wakes the PC it hibernated during a power cut, to read the station (the action does nothing)</Description></RegistrationInfo>
          <Triggers><TimeTrigger><StartBoundary>2026-01-01T00:00:00</StartBoundary><Enabled>true</Enabled></TimeTrigger></Triggers>
          <Principals><Principal id="Author"><UserId>{SID}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
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
          <Actions Context="Author"><Exec><Command>{SYS}\conhost.exe</Command><Arguments>--headless &quot;{SYS}\cmd.exe&quot; /d /c exit 0</Arguments></Exec></Actions>
        </Task>
        """;

    // design.md §8 Wake during a cut (i): the XML install.ps1 registers against a fixed one, and its read-back checks against fixed variants; nothing registered
    [WindowsFact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void The_installer_writes_a_disabled_wake_to_run_task_as_the_current_user_without_elevation_and_reads_it_back_strictly()
    {
        string sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string user = $"{Environment.UserDomainName}\\{Environment.UserName}";
        string expected = ExpectedTask.Replace("{SID}", sid, StringComparison.Ordinal).Replace("{SYS}", sys, StringComparison.Ordinal);
        string exec = $"<Exec><Command>{sys}\\conhost.exe</Command><Arguments>--headless &quot;{sys}\\cmd.exe&quot; /d /c exit 0</Arguments></Exec>";
        var xmls = new Dictionary<string, string>
        {
            ["a-as-written"] = expected,
            ["b-read-back-form"] = expected.Replace($"<UserId>{sid}</UserId>", $"<UserId>{user}</UserId>", StringComparison.Ordinal).Replace("<RunLevel>LeastPrivilege</RunLevel>", "", StringComparison.Ordinal).Replace("<StartWhenAvailable>false</StartWhenAvailable>", "", StringComparison.Ordinal),
            ["c-elevated"] = expected.Replace("LeastPrivilege", "HighestAvailable", StringComparison.Ordinal),
            ["d-no-wake"] = expected.Replace("<WakeToRun>true", "<WakeToRun>false", StringComparison.Ordinal),
            ["e-enabled"] = expected.Replace("<Enabled>false</Enabled>", "<Enabled>true</Enabled>", StringComparison.Ordinal),
            ["f-two-actions"] = expected.Replace(exec, exec + exec, StringComparison.Ordinal),
            ["g-repetition"] = expected.Replace("<Enabled>true</Enabled></TimeTrigger>", "<Enabled>true</Enabled><Repetition><Interval>PT5M</Interval></Repetition></TimeTrigger>", StringComparison.Ordinal),
            ["h-start-when-available"] = expected.Replace("<StartWhenAvailable>false", "<StartWhenAvailable>true", StringComparison.Ordinal),
            ["i-other-user"] = expected.Replace(sid, "S-1-5-18", StringComparison.Ordinal),
            ["j-other-action"] = expected.Replace("/d /c exit 0", "/d /c del x", StringComparison.Ordinal),
        };
        var sddls = new Dictionary<string, string>
        {
            ["a-ok"] = $"D:P(A;;FA;;;BA)(A;;FA;;;SY)(A;;FA;;;{sid})",
            ["b-mask"] = $"D:P(A;;0x1f01ff;;;BA)(A;;0x1f01ff;;;SY)(A;;0x1f01ff;;;{sid})",
            ["c-everyone"] = $"D:P(A;;FA;;;BA)(A;;FA;;;SY)(A;;FA;;;{sid})(A;;FA;;;WD)",
            ["f-real-world"] = $"D:(A;ID;0x1f019f;;;BA)(A;ID;0x1f019f;;;SY)(A;ID;FA;;;BA)(A;;FR;;;S-1-5-21-1-1001)(A;;FA;;;BA)(A;;FA;;;SY)(A;;FA;;;{sid})",
            ["g-read-only-extras"] = $"D:P(A;;FA;;;BA)(A;;FA;;;SY)(A;;FA;;;{sid})(A;;FR;;;WD)(A;;0x120089;;;AU)(A;;GR;;;S-1-5-32-545)",
            ["h-write-extra"] = $"D:P(A;;FA;;;BA)(A;;FA;;;SY)(A;;FA;;;{sid})(A;;0x1f019f;;;AU)",
            ["i-execute-extra"] = $"D:P(A;;FA;;;BA)(A;;FA;;;SY)(A;;FA;;;{sid})(A;;FRFX;;;WD)",
            ["j-real-world-alone"] = "D:(A;ID;0x1f019f;;;BA)(A;ID;0x1f019f;;;SY)(A;ID;FA;;;BA)(A;;FR;;;S-1-5-21-1-1001)",
            ["d-no-system"] = $"D:P(A;;FA;;;BA)(A;;FA;;;{sid})",
            ["e-user-read-only"] = $"D:P(A;;FA;;;BA)(A;;FA;;;SY)(A;;FRFX;;;{sid})",
        };
        string dir = Path.Combine(_dir, "wake-xml");
        Directory.CreateDirectory(dir);
        foreach (var (name, text) in xmls) File.WriteAllText(Path.Combine(dir, name + ".xml"), text);
        foreach (var (name, text) in sddls) File.WriteAllText(Path.Combine(dir, name + ".sddl"), text);
        string script = Path.Combine(Repo(), "tools", "wake-task.ps1");
        var psi = new ProcessStartInfo("pwsh.exe") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8 };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-Command",
            $". '{script}'; Get-ChildItem '{dir}' -Filter *.xml | Sort-Object Name | ForEach-Object {{ \"$($_.BaseName)=$(Test-WatcherWakeTaskXml (Get-Content -Raw $_.FullName))\" }}; " +
            $"Get-ChildItem '{dir}' -Filter *.sddl | Sort-Object Name | ForEach-Object {{ \"sddl $($_.BaseName)=$(Test-WatcherWakeTaskSddl (Get-Content -Raw $_.FullName))\" }}; " +
            "Write-Output \"sddl written=$(Test-WatcherWakeTaskSddl (Get-WatcherWakeTaskSddl))\"; Write-Output '---'; New-WatcherWakeTaskXml" })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd();
        string errors = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, errors);
        var parts = output.Split("---", 2);
        static string Norm(string s) => string.Join('\n', s.Replace("\r", "", StringComparison.Ordinal).Trim().Split('\n').Select(l => l.TrimEnd()));
        Assert.Equal(Norm(expected), Norm(parts[1]));
        var verdicts = parts[0].Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(l => l.Split('=', 2)).ToDictionary(kv => kv[0], kv => kv[1]);
        Assert.Equal(("ok", "ok"), (verdicts["a-as-written"], verdicts["b-read-back-form"]));
        foreach (var bad in new[] { "c-elevated", "d-no-wake", "e-enabled", "f-two-actions", "g-repetition", "h-start-when-available", "i-other-user", "j-other-action" })
            Assert.StartsWith("foreign", verdicts[bad], StringComparison.Ordinal);
        Assert.Equal(("ok", "ok", "ok", "ok", "ok"), (verdicts["sddl a-ok"], verdicts["sddl b-mask"], verdicts["sddl written"], verdicts["sddl f-real-world"], verdicts["sddl g-read-only-extras"]));
        foreach (var bad in new[] { "sddl c-everyone", "sddl d-no-system", "sddl e-user-read-only", "sddl h-write-extra", "sddl i-execute-extra", "sddl j-real-world-alone" })
            Assert.StartsWith("foreign", verdicts[bad], StringComparison.Ordinal);
    }
}
