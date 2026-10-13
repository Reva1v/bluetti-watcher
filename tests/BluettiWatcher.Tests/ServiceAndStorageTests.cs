using BluettiWatcher.Decision;
using BluettiWatcher.Io;
using BluettiWatcher.Storage;
using static BluettiWatcher.Tests.Fx;

namespace BluettiWatcher.Tests;

internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
    /// <summary>The zone people's times are shown in; +03:00 unless a test sets another.</summary>
    public TimeZoneInfo Zone { get; set; } = TimeZoneInfo.CreateCustomTimeZone("t", TimeSpan.FromHours(3), "t", "t");
    public override TimeZoneInfo LocalTimeZone => Zone;
}

internal sealed class QueueReader : IStationReader
{
    public Queue<Reading?> Next { get; } = new();
    public Reading? Default { get; set; }
    public int Reads { get; private set; }

    public Task<Reading?> ReadAsync(CancellationToken ct)
    {
        Reads++;
        return Task.FromResult(Next.Count > 0 ? Next.Dequeue() : Default);
    }
}

/// <summary>A reader whose answer arrives at <see cref="ArriveAt"/>: the clock moves while the station is read.</summary>
internal sealed class SlowReader(ManualClock clock, Reading reading) : IStationReader
{
    public DateTimeOffset ArriveAt { get; set; }

    public Task<Reading?> ReadAsync(CancellationToken ct)
    {
        clock.Now = ArriveAt;
        return Task.FromResult<Reading?>(reading);
    }
}

internal sealed class FakeActions : IPowerModeActions
{
    public List<string> Calls { get; } = [];
    public bool FailEmergency { get; set; }
    public bool FailBattery { get; set; }
    public bool FailEco { get; set; }
    public bool FailLift { get; set; }

    public TimeSpan Delay { get; set; }

    private async Task<ActionResult> R(string name, bool fail)
    {
        if (Delay > TimeSpan.Zero) await Task.Delay(Delay);
        Calls.Add(name);
        return new ActionResult(!fail, fail ? 2 : 0, name, fail ? "COULD NOT RUN" : "ok");
    }

    public Task<ActionResult> StationEcoAsync(CancellationToken ct) => R("eco", FailEco);
    public Task<ActionResult> StationEmergencyAsync(CancellationToken ct) => R("on", FailEmergency);
    public Task<ActionResult> LiftAsync(CancellationToken ct) => R("off", FailLift);
    public Task<ActionResult> BatteryAsync(BatteryReport battery, double hibernateAt, CancellationToken ct) => R($"battery {battery.Level}", FailBattery);
}

/// <summary>Records the command lines of the config's actions; runs nothing.</summary>
internal sealed class FakeLineRunner : ICommandLineRunner
{
    public System.Collections.Concurrent.ConcurrentQueue<string> Lines { get; } = new();

    public Task<ProcessResult> RunLineAsync(string commandLine, TimeSpan timeout, CancellationToken ct, IReadOnlyDictionary<string, string>? env = null)
    {
        Lines.Enqueue(commandLine);
        return Task.FromResult(new ProcessResult(0, ["ok"], false));
    }
}

internal sealed class FakeHibernator(bool ok) : IHibernator
{
    public int Calls { get; private set; }
    public Task<bool> HibernateAsync(CancellationToken ct) { Calls++; return Task.FromResult(ok); }
}

internal sealed class FixedFlag(FlagView view) : IFlagReader
{
    public FlagView View { get; set; } = view;
    /// <summary>The flag cannot be read (a writer holds the lock, the file is torn).</summary>
    public bool Unreadable { get; set; }
    public PowerModeFlag? Read(DateTimeOffset now) => Unreadable ? null : new(View.Mode, View.Source, View.By, View.SinceAt, null, false, null);
}

public sealed class ServiceTests : IAsyncLifetime, IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bluetti-watcher-test-" + Guid.NewGuid().ToString("N"));
    private readonly StationStore _store;
    private readonly ManualClock _clock = new(T0);
    private readonly QueueReader _reader = new();
    private readonly FakeActions _actions = new();
    private readonly FixedFlag _flag = new(Station);
    private readonly FakeLineRunner _lines = new();
    private StateActions? _onState;

    /// <summary>One command line per state, so a test sees which state was entered.</summary>
    internal static Dictionary<string, string> EveryAction() => new()
    {
        ["eco"] = "eco-cmd", ["emergency"] = "emergency-cmd", ["stop"] = "stop-cmd", ["hibernate-warn"] = "warn-cmd", ["normal"] = "normal-cmd",
    };

    /// <summary>The action command lines run so far, after every started one finished.</summary>
    private async Task<List<string>> ActionLines()
    {
        if (_onState is not null) await _onState.IdleAsync();
        return [.. _lines.Lines];
    }

    public ServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new StationStore(Path.Combine(_dir, "station.db"));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>The state actions run in the background and write their event to the store: they finish before <see cref="Dispose"/> closes it and deletes its folder.</summary>
    public async Task DisposeAsync()
    {
        if (_onState is not null) await _onState.IdleAsync();
    }

    public void Dispose()
    {
        _store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private StationService Service(IHibernator? h = null, bool dryRun = false)
    {
        var cfg = new StationConfig { DataDir = _dir, DryRun = dryRun, MainsLostSec = 300, MainsBackSec = 300, Actions = EveryAction() };
        _onState = new StateActions(cfg, _lines, _store, _clock, _ => { });
        return new(cfg, _reader, _actions, h ?? new FakeHibernator(true), _flag, _store, _clock, _ => { }, null, _onState);
    }

    /// <summary>One poll per reading, two minutes apart.</summary>
    private async Task Feed(StationService s, params Reading?[] readings)
    {
        foreach (var r in readings)
        {
            _reader.Next.Enqueue(r);
            await s.PollAsync(CancellationToken.None);
            _clock.Now = _clock.Now.AddMinutes(2);
        }
    }

    // spec S4.ai, S4.aj
    [Fact]
    public async Task Repro_b_at_4_with_the_battery_write_failing_every_poll_warn_and_hibernate_still_come()
    {
        var h = new FakeHibernator(true);
        using var s = Service(h);
        _actions.FailBattery = true;
        _reader.Default = Bat(4);
        for (int k = 0; k < 9; k++) { await s.PollAsync(CancellationToken.None); _clock.Now = _clock.Now.AddMinutes(2); }
        var kinds = _store.Events(0, long.MaxValue).Select(e => e.Kind).ToList();
        Assert.Contains("emergency-on", kinds);
        Assert.Contains("hibernate-warning", kinds);
        Assert.Equal(1, h.Calls);
        Assert.True(s.State.Fired[(int)LevelStep.Hibernate]);
        Assert.Contains("action-failed", kinds);
    }

    // spec S4.ak, S4.al
    [Fact]
    public async Task Repro_c_shutdown_keeps_failing_backs_off_1_2_4_8_10_minutes_no_hot_loop()
    {
        var h = new FakeHibernator(false);
        using var s = Service(h);
        _reader.Default = Bat(4);
        var calls = new List<int>();
        for (int minute = 0; minute <= 30; minute++)
        {
            _clock.Now = T0.AddMinutes(minute);
            int before = h.Calls;
            await s.PollAsync(CancellationToken.None);
            if (h.Calls > before) calls.Add(minute);
        }
        // cut at 5 (300 s), emergency 5, stop 6, warning 7, hibernate from 10 (180 s after the warning)
        Assert.Equal([10, 11, 13, 17, 25], calls);
        Assert.Equal(5, _store.Events(0, long.MaxValue).Count(e => e.Kind == "action-failed" && e.Detail.StartsWith("hibernate", StringComparison.Ordinal)));
    }

    // spec S4.al
    [Fact]
    public async Task Failing_emergency_on_backs_off_instead_of_retrying_every_poll()
    {
        using var s = Service();
        _actions.FailEmergency = true;
        _reader.Default = Bat(15);
        for (int minute = 0; minute <= 12; minute++) { _clock.Now = T0.AddMinutes(minute); await s.PollAsync(CancellationToken.None); }
        // first try at 5, then 6, 8, 12
        Assert.Equal(4, _actions.Calls.Count(c => c == "on"));
        Assert.False(s.State.Fired[(int)LevelStep.Emergency]);
    }

    // spec S4.ak, S4.al
    [Fact]
    public async Task Failing_eco_on_backs_off_through_the_service_loop_with_an_event_each_time()
    {
        _flag.View = Flag();
        using var s = Service();
        _actions.FailEco = true;
        _reader.Default = Bat(80);
        for (int minute = 0; minute <= 12; minute++) { _clock.Now = T0.AddMinutes(minute); await s.PollAsync(CancellationToken.None); }
        Assert.Equal(4, _actions.Calls.Count(c => c == "eco"));   // 5, 6, 8, 12
        Assert.Equal(4, _store.Events(0, long.MaxValue).Count(e => e.Kind == "action-failed" && e.Detail.StartsWith("eco-on", StringComparison.Ordinal)));
    }

    // spec S4.v
    [Fact]
    public async Task Failed_emergency_on_is_not_fired()
    {
        using var s = Service();
        _actions.FailEmergency = true;
        await Feed(s, Bat(15), Bat(15), Bat(15), Bat(15));
        Assert.False(s.State.Fired[(int)LevelStep.Emergency]);
    }

    // spec S2.7d
    [Fact]
    public async Task Eco_on_runs_the_eco_action_after_it_succeeded()
    {
        _flag.View = Flag();
        using var s = Service();
        await Feed(s, Bat(80), Bat(80), Bat(80), Bat(80));
        Assert.Equal(["battery Ok", "eco"], _actions.Calls);
        Assert.Equal(["eco-cmd"], await ActionLines());
        _actions.Calls.Clear();
        _actions.FailEco = true;
        await Feed(s, Bat(80));
        Assert.Equal(["eco"], _actions.Calls);
        Assert.Equal(["eco-cmd"], await ActionLines());
    }

    // spec S2.4a, S3.c
    [Fact]
    public async Task Missed_and_rejected_readings_are_stored_with_their_reason()
    {
        using var s = Service();
        await Feed(s, Bat(80), null, Bat(0));
        var ev = _store.Events(0, long.MaxValue);
        Assert.Equal(["missed", "rejected"], ev.Select(e => e.Kind));
        Assert.Equal("no answer", ev[0].Detail);
        Assert.StartsWith("implausible", ev[1].Detail, StringComparison.Ordinal);
        Assert.Equal((3, 0), _store.Counts());
    }

    private async Task<StationService> ToHibernate(IHibernator h)
    {
        var s = Service(h);
        await Feed(s, Bat(4), Bat(4), Bat(4), Bat(4), Bat(4), Bat(4), Bat(4));   // emergency, stop, warning (t0+12), then 2 more polls
        return s;
    }

    // spec S4.u
    [Fact]
    public async Task Hibernate_runs_after_a_fresh_reading_agrees_and_counts_as_fired()
    {
        var h = new FakeHibernator(true);
        using var s = await ToHibernate(h);
        _reader.Next.Enqueue(Bat(4));
        _reader.Next.Enqueue(Bat(4));   // the fresh reading
        await s.PollAsync(CancellationToken.None);
        Assert.Equal(1, h.Calls);
        Assert.True(s.State.Fired[(int)LevelStep.Hibernate]);
    }

    // spec S4.u
    [Fact]
    public async Task No_fresh_reading_no_hibernate()
    {
        var h = new FakeHibernator(true);
        using var s = await ToHibernate(h);
        _reader.Next.Enqueue(Bat(4));   // the poll answers; the fresh reading does not
        await s.PollAsync(CancellationToken.None);
        Assert.Equal(0, h.Calls);
        Assert.False(s.State.Fired[(int)LevelStep.Hibernate]);
    }

    // spec S4.s
    [Fact]
    public async Task Fresh_reading_on_mains_no_hibernate()
    {
        var h = new FakeHibernator(true);
        using var s = await ToHibernate(h);
        _reader.Next.Enqueue(Bat(4));
        _reader.Next.Enqueue(Grid(4));
        await s.PollAsync(CancellationToken.None);
        Assert.Equal(0, h.Calls);
    }

    // spec S4.v, S4.w
    [Fact]
    public async Task Failed_hibernate_goes_back_and_is_retried_on_the_next_qualifying_poll()
    {
        var h = new FakeHibernator(false);
        using var s = await ToHibernate(h);
        _reader.Next.Enqueue(Bat(4));
        _reader.Next.Enqueue(Bat(4));
        await s.PollAsync(CancellationToken.None);
        Assert.Equal(1, h.Calls);
        Assert.False(s.State.Fired[(int)LevelStep.Hibernate]);
        Assert.False(_store.LoadState().Fired[(int)LevelStep.Hibernate]);
        _clock.Now = _clock.Now.AddMinutes(2);
        _reader.Next.Enqueue(Bat(4));
        _reader.Next.Enqueue(Bat(4));
        await s.PollAsync(CancellationToken.None);
        Assert.Equal(2, h.Calls);
    }

    [Fact]
    public async Task Kill_switch_file_stops_hibernate()
    {
        var h = new FakeHibernator(true);
        using var s = await ToHibernate(h);
        await File.WriteAllTextAsync(Path.Combine(_dir, "no-hibernate"), "");
        _reader.Next.Enqueue(Bat(4));
        _reader.Next.Enqueue(Bat(4));
        await s.PollAsync(CancellationToken.None);
        Assert.Equal(0, h.Calls);
    }

    // spec S2.7d, S2.8c
    [Fact]
    public async Task Page_modes_eco_runs_the_station_eco_and_its_action_max_lifts_auto_runs_nothing()
    {
        using var s = Service();
        var eco = await s.SetModeAsync(ManualMode.Eco, CancellationToken.None);
        await s.ApplyPendingAsync(CancellationToken.None);
        var max = await s.SetModeAsync(ManualMode.Max, CancellationToken.None);
        await s.ApplyPendingAsync(CancellationToken.None);
        var auto = await s.SetModeAsync(ManualMode.Auto, CancellationToken.None);
        await s.ApplyPendingAsync(CancellationToken.None);
        Assert.True(eco.Ok && max.Ok && auto.Ok);
        Assert.Equal(202, eco.Status);
        Assert.Equal(["eco", "off"], _actions.Calls);
        Assert.Equal(["eco-cmd", "normal-cmd"], (await ActionLines()).Order(StringComparer.Ordinal));
        Assert.Equal(ManualMode.Auto, _store.LoadState().Manual);
    }

    // spec S4.j
    [Fact]
    public async Task Max_before_20_then_the_emergency_fires_and_Max_never_lifts_it()
    {
        using var s = Service();
        _reader.Default = Bat(25);
        for (int minute = 0; minute <= 5; minute++) { _clock.Now = T0.AddMinutes(minute); await s.PollAsync(CancellationToken.None); }
        Assert.True((await s.SetModeAsync(ManualMode.Max, CancellationToken.None)).Ok);
        await s.ApplyPendingAsync(CancellationToken.None);
        _flag.View = Flag();   // Max lifted the eco
        _reader.Default = Bat(20);
        for (int minute = 6; minute <= 10; minute++) { _clock.Now = T0.AddMinutes(minute); await s.PollAsync(CancellationToken.None); }
        _flag.View = Flag("emergency", "station");
        for (int minute = 11; minute <= 14; minute++) { _clock.Now = T0.AddMinutes(minute); await s.PollAsync(CancellationToken.None); }
        Assert.Equal(1, _actions.Calls.Count(c => c == "off"));   // only the button's own lift
        Assert.Equal(1, _actions.Calls.Count(c => c == "on"));
        Assert.DoesNotContain("eco", _actions.Calls);
        Assert.Equal(ManualMode.Max, s.State.Manual);
    }

    // spec S4.m, S4.n
    [Fact]
    public async Task Max_lift_retried_after_the_emergency_fired_is_refused_and_never_lifts_it()
    {
        using var s = Service();
        _reader.Default = Bat(25);
        for (int minute = 0; minute <= 5; minute++) { _clock.Now = T0.AddMinutes(minute); await s.PollAsync(CancellationToken.None); }
        Assert.True((await s.SetModeAsync(ManualMode.Max, CancellationToken.None)).Ok);
        _actions.FailLift = true;
        await s.ApplyPendingAsync(CancellationToken.None);   // fails, backs off
        _reader.Default = Bat(20);
        for (int minute = 6; minute <= 12; minute++)
        {
            _clock.Now = T0.AddMinutes(minute);
            if (minute == 8) _actions.FailLift = false;
            await s.PollAsync(CancellationToken.None);
            await s.ApplyPendingAsync(CancellationToken.None);   // the service loop: a poll, then a pending choice
        }
        int on = _actions.Calls.IndexOf("on");
        Assert.True(on >= 0);
        Assert.DoesNotContain("off", _actions.Calls.Skip(on));
        Assert.Null(s.State.ModePending);
        Assert.Equal(ManualMode.Auto, s.State.Manual);
        Assert.Equal(StationService.MaxHeldBack, s.ModeError);
        Assert.Contains(_store.Events(0, long.MaxValue), e => e.Kind == "mode-refused");
    }

    // spec S4.ae
    [Fact]
    public async Task Max_refusal_survives_a_restart_until_the_next_mode_change()
    {
        SavePendingMax();
        using (var s = Service())
        {
            _flag.View = Flag("emergency", "station");
            await s.ApplyPendingAsync(CancellationToken.None);
            Assert.Equal(StationService.MaxHeldBack, s.ModeError);
        }
        using var again = Service();
        Assert.Equal(StationService.MaxHeldBack, again.ModeError);
        _flag.View = Station;
        await again.SetModeAsync(ManualMode.Eco, CancellationToken.None);
        Assert.Equal("", again.ModeError);
        Assert.Equal("", _store.LoadState().ModeError);
    }

    /// <summary>A Max saved by the page whose lift has not run yet, as the store holds it.</summary>
    private void SavePendingMax()
    {
        var st = StationDecision.SetManual(_store.LoadState(), ManualMode.Max, _clock.Now);
        st.ModePending = ManualMode.Max;
        _store.SaveState(st);
    }

    // spec S4.y, S4.aa, S4.ac
    [Fact]
    public async Task Recalibration_holds_hibernate_back_10_minutes_one_event_and_the_warning_promises_nothing()
    {
        var h = new FakeHibernator(true);
        using var s = Service(h);
        _reader.Default = Bat(22);
        int minute = 0;
        for (; minute <= 5; minute++) { _clock.Now = T0.AddMinutes(minute); await s.PollAsync(CancellationToken.None); }
        _reader.Default = Bat(4);   // the station recalibrates: 6, 7 rejected, 8 moves the baseline, the streak begins at 9
        for (; minute <= 18; minute++) { _clock.Now = T0.AddMinutes(minute); await s.PollAsync(CancellationToken.None); }
        Assert.Equal(0, h.Calls);
        var events = _store.Events(0, long.MaxValue);
        var delayed = Assert.Single(events, e => e.Kind == "hibernate-delayed");
        Assert.Contains("allowed from 20:19", delayed.Detail, StringComparison.Ordinal);   // the time provider's zone, +03:00
        var warning = Assert.Single(events, e => e.Kind == "hibernate-warning");
        Assert.DoesNotContain("the PC hibernates at 5 %", warning.Detail, StringComparison.Ordinal);
        Assert.Equal(T0.AddMinutes(19), s.State.HibernateDelayedUntil);
        _clock.Now = T0.AddMinutes(19);
        await s.PollAsync(CancellationToken.None);
        Assert.Equal(1, h.Calls);
    }

    // ---- page choices under an emergency, an unreadable flag; the kill switch

    // power-mode.ps1 refuses eco under an emergency (exit 2); the page's Eco is dropped like Max, never retried.
    [Fact]
    public async Task Eco_pressed_during_an_emergency_is_dropped_with_a_saved_error_never_retried()
    {
        _flag.View = Flag("emergency", "station");
        _actions.FailEco = true;
        using var s = Service();
        Assert.Equal(202, (await s.SetModeAsync(ManualMode.Eco, CancellationToken.None)).Status);
        for (int minute = 0; minute <= 20; minute++) { _clock.Now = T0.AddMinutes(minute); await s.ApplyPendingAsync(CancellationToken.None); }
        Assert.DoesNotContain("eco", _actions.Calls);
        Assert.Null(s.State.ModePending);
        Assert.Equal(ManualMode.Auto, s.State.Manual);
        Assert.Equal("Eco held back: battery emergency", s.ModeError);
        Assert.Equal("Eco held back: battery emergency", _store.LoadState().ModeError);
        Assert.Single(_store.Events(0, long.MaxValue), e => e.Kind == "mode-refused");
    }

    // a page Eco whose command keeps failing through a cut never runs after mains is back.
    [Fact]
    public async Task Pending_Eco_is_dropped_when_mains_comes_back_and_never_runs_on_mains()
    {
        using var s = Service();
        _reader.Default = Bat(50);
        int minute = 0;
        for (; minute <= 6; minute++) { _clock.Now = T0.AddMinutes(minute); await s.PollAsync(CancellationToken.None); }
        Assert.True(s.State.Cut);
        _actions.FailEco = true;
        await s.SetModeAsync(ManualMode.Eco, CancellationToken.None);
        _reader.Default = Grid(50);   // mains seen back at 7, confirmed at 12
        for (; minute <= 30; minute++)
        {
            _clock.Now = T0.AddMinutes(minute);
            if (minute == 13) _actions.FailEco = false;
            await s.PollAsync(CancellationToken.None);
            await s.ApplyPendingAsync(CancellationToken.None);
        }
        Assert.DoesNotContain("eco-cmd", await ActionLines());   // eco never succeeded
        Assert.Null(s.State.ModePending);
        Assert.Equal(ManualMode.Auto, s.State.Manual);
        var dropped = Assert.Single(_store.Events(0, long.MaxValue), e => e.Kind == "mode-dropped");
        Assert.Contains("Eco dropped", dropped.Detail, StringComparison.Ordinal);
        Assert.Equal(dropped.Detail, s.ModeError);
    }

    // unknown refuses. With the flag unreadable a pending Max never runs "off"; it waits with backoff and an error.
    [Fact]
    public async Task Pending_Max_with_an_unreadable_flag_is_not_lifted_and_stays_pending_with_backoff()
    {
        SavePendingMax();
        using var s = Service();
        _flag.Unreadable = true;
        await s.ApplyPendingAsync(CancellationToken.None);
        await s.ApplyPendingAsync(CancellationToken.None);   // inside the backoff
        Assert.DoesNotContain("off", _actions.Calls);
        Assert.Equal(ManualMode.Max, s.State.ModePending);
        Assert.Contains("unreadable", s.ModeError, StringComparison.Ordinal);
        Assert.Contains("unreadable", _store.LoadState().ModeError, StringComparison.Ordinal);
        Assert.Single(_store.Events(0, long.MaxValue), e => e.Kind == "action-failed");
        _flag.Unreadable = false;
        _flag.View = Flag("emergency", "station");   // readable again and it holds: dropped, never run
        _clock.Now = _clock.Now.AddMinutes(1);
        await s.ApplyPendingAsync(CancellationToken.None);
        Assert.DoesNotContain("off", _actions.Calls);
        Assert.Equal(StationService.MaxHeldBack, s.ModeError);
    }

    [Fact]
    public async Task Pending_Max_runs_once_the_flag_is_readable_again()
    {
        SavePendingMax();
        using var s = Service();
        _flag.Unreadable = true;
        await s.ApplyPendingAsync(CancellationToken.None);
        _flag.Unreadable = false;
        _clock.Now = _clock.Now.AddMinutes(1);
        await s.ApplyPendingAsync(CancellationToken.None);
        Assert.Equal(["off"], _actions.Calls);
        Assert.Null(s.State.ModePending);
        Assert.Equal("", s.ModeError);
    }

    // the kill switch is checked every poll; its event backs off 1, 2, 4, 8, then 10 minutes.
    [Fact]
    public async Task Kill_switch_refusal_is_logged_with_backoff_and_hibernate_runs_the_poll_after_it_is_removed()
    {
        var h = new FakeHibernator(true);
        using var s = Service(h);
        string kill = Path.Combine(_dir, "no-hibernate");
        await File.WriteAllTextAsync(kill, "");
        _reader.Default = Bat(4);
        for (int minute = 0; minute <= 30; minute++) { _clock.Now = T0.AddMinutes(minute); await s.PollAsync(CancellationToken.None); }
        var refused = _store.Events(0, long.MaxValue).Where(e => e.Kind == "hibernate-refused").Select(e => (e.At - T0.ToUnixTimeSeconds()) / 60).ToList();
        Assert.Equal([10, 11, 13, 17, 25], refused);
        Assert.Equal(0, h.Calls);
        File.Delete(kill);
        _clock.Now = T0.AddMinutes(31);
        await s.PollAsync(CancellationToken.None);
        Assert.Equal(1, h.Calls);   // the switch itself never backs the hibernation off
    }

    [Fact]
    public async Task Dry_run_hibernate_is_skipped_with_backoff()
    {
        var h = new FakeHibernator(false);
        using var s = Service(h, dryRun: true);
        _reader.Default = Bat(4);
        for (int minute = 0; minute <= 30; minute++) { _clock.Now = T0.AddMinutes(minute); await s.PollAsync(CancellationToken.None); }
        Assert.Equal(5, _store.Events(0, long.MaxValue).Count(e => e.Kind == "hibernate-skipped"));
        Assert.Equal(5, h.Calls);
    }

    // spec S4.k, S4.l
    [Fact]
    public async Task Max_pressed_during_an_emergency_is_refused_with_the_page_message()
    {
        _flag.View = Flag("emergency", "station");
        using var s = Service();
        var r = await s.SetModeAsync(ManualMode.Max, CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Equal(409, r.Status);
        Assert.Equal("Max held back: battery emergency", r.Message);
        Assert.Empty(_actions.Calls);
        Assert.Equal(ManualMode.Auto, s.State.Manual);
    }
    // spec S2.8j, S2.8k, S4.al
    [Fact]
    public async Task Choice_is_persisted_even_when_its_command_fails_and_the_failure_backs_off()
    {
        using var s = Service();
        _actions.FailEco = true;
        var r = await s.SetModeAsync(ManualMode.Eco, CancellationToken.None);
        Assert.Equal(202, r.Status);
        Assert.Equal(ManualMode.Eco, _store.LoadState().Manual);
        await s.ApplyPendingAsync(CancellationToken.None);
        await s.ApplyPendingAsync(CancellationToken.None);   // inside the backoff: not run again
        Assert.Equal(["eco"], _actions.Calls);
        Assert.Equal(ManualMode.Eco, _store.LoadState().ModePending);
        Assert.StartsWith("Eco: COULD NOT RUN", s.ModeError, StringComparison.Ordinal);
        Assert.Contains(_store.Events(0, long.MaxValue), e => e.Kind == "action-failed");
    }

    // spec S3.n: a reading's time is when it arrived, taken before any lock (here the gate held by a page command).
    [Fact]
    public async Task Reading_time_is_its_arrival_taken_before_the_lock()
    {
        var reader = new SlowReader(_clock, Bat(80)) { ArriveAt = T0.AddSeconds(30) };
        using var s = new StationService(new StationConfig { DataDir = _dir, MainsLostSec = 300, MainsBackSec = 300 }, reader, _actions, new FakeHibernator(true), _flag, _store, _clock, _ => { });
        await s.SetModeAsync(ManualMode.Max, CancellationToken.None);
        _actions.Delay = TimeSpan.FromMilliseconds(300);
        var apply = s.ApplyPendingAsync(CancellationToken.None);   // holds the gate while "off" runs
        var poll = s.PollAsync(CancellationToken.None);            // read at T0+30 s, then waits for the gate
        _clock.Now = T0.AddSeconds(90);                            // the lock is free only later
        await apply;
        await poll;
        Assert.Equal(T0.AddSeconds(30), s.LastAcceptedAt);
        Assert.Equal(T0.AddSeconds(30), s.LastPollAt);
        Assert.Equal(T0.AddSeconds(30), s.State.LastAt);
        Assert.Equal(T0.AddSeconds(30).ToUnixTimeSeconds(), Assert.Single(_store.History(0, long.MaxValue, 1)).At);
    }

    // spec S4.l: a hold is also the emergency level reached in this cut, with the flag not (yet) saying emergency.
    [Fact]
    public async Task Max_pressed_with_the_emergency_level_reached_but_no_emergency_flag_is_refused_409()
    {
        using var s = Service();
        _actions.FailEmergency = true;   // the flag never gets to say emergency
        _reader.Default = Bat(20);
        for (int minute = 0; minute <= 6; minute++) { _clock.Now = T0.AddMinutes(minute); await s.PollAsync(CancellationToken.None); }
        Assert.True(s.State.Cut);
        Assert.True(s.State.Reached[(int)LevelStep.Emergency]);
        Assert.Equal("eco", _flag.View.Mode);
        var r = await s.SetModeAsync(ManualMode.Max, CancellationToken.None);
        Assert.Equal(409, r.Status);
        Assert.Equal(StationService.MaxHeldBack, r.Message);
        await s.ApplyPendingAsync(CancellationToken.None);
        Assert.DoesNotContain("off", _actions.Calls);
        Assert.Equal(ManualMode.Auto, s.State.Manual);
    }

    // spec S4.m: Auto lifting the page's Eco is a page lift too: a retry after a failure re-checks the hold and is dropped.
    [Fact]
    public async Task Auto_lift_of_the_page_Eco_retried_after_an_emergency_came_on_is_dropped_never_run()
    {
        _flag.View = Flag();
        using var s = Service();
        await s.SetModeAsync(ManualMode.Eco, CancellationToken.None);
        await s.ApplyPendingAsync(CancellationToken.None);
        _flag.View = Flag("eco", "manual", "", _clock.Now);
        Assert.Equal(202, (await s.SetModeAsync(ManualMode.Auto, CancellationToken.None)).Status);
        Assert.Equal(ManualMode.Auto, s.State.ModePending);
        _actions.FailLift = true;
        await s.ApplyPendingAsync(CancellationToken.None);   // "off" fails, backs off a minute
        Assert.Equal(1, _actions.Calls.Count(c => c == "off"));
        _flag.View = Flag("emergency", "manual", "", _clock.Now);
        _actions.FailLift = false;
        _clock.Now = _clock.Now.AddMinutes(2);
        await s.ApplyPendingAsync(CancellationToken.None);
        Assert.Equal(1, _actions.Calls.Count(c => c == "off"));   // the retry never ran
        Assert.Null(s.State.ModePending);
        Assert.Equal(StationService.AutoLiftHeldBack, s.ModeError);
        Assert.Contains(_store.Events(0, long.MaxValue), e => e.Kind == "mode-refused" && e.Detail == StationService.AutoLiftHeldBack);
    }

    // spec S4.ac: the warning says what will happen in both cases: the threshold without a rebase, the confirmation with one.
    [Fact]
    public async Task Warning_without_a_rebase_promises_hibernation_at_5_percent()
    {
        using var s = Service();
        _reader.Default = Bat(4);
        for (int minute = 0; minute <= 8; minute++) { _clock.Now = T0.AddMinutes(minute); await s.PollAsync(CancellationToken.None); }
        var warning = Assert.Single(_store.Events(0, long.MaxValue), e => e.Kind == "hibernate-warning");
        Assert.Equal("battery 4 % - the PC hibernates at 5 %", warning.Detail);
    }

    // spec S4.ac
    [Fact]
    public async Task Warning_after_a_rebase_says_the_PC_hibernates_only_after_the_confirmation()
    {
        using var s = Service();
        _reader.Default = Bat(22);
        int minute = 0;
        for (; minute <= 5; minute++) { _clock.Now = T0.AddMinutes(minute); await s.PollAsync(CancellationToken.None); }
        _reader.Default = Bat(4);   // 6, 7 rejected, 8 rebased
        for (; minute <= 14; minute++) { _clock.Now = T0.AddMinutes(minute); await s.PollAsync(CancellationToken.None); }
        var warning = Assert.Single(_store.Events(0, long.MaxValue), e => e.Kind == "hibernate-warning");
        Assert.Equal("battery 4 % - the baseline moved at 20:08: the PC hibernates only after 10 minutes at or under 5 %", warning.Detail);
    }

    // spec S2.8k: the page's choice runs with the normal power-mode timeout (PowerModeTimeoutSec reaches the process runner).
    [Fact]
    public async Task Page_choice_runs_with_the_configured_action_timeout()
    {
        var runner = new FakeRunner();
        var cfg = new StationConfig { DataDir = _dir, PowerModeTimeoutSec = 17, MainsLostSec = 300, MainsBackSec = 300 };
        using var s = new StationService(cfg, _reader, new PowerModeAdapter(cfg, runner, _ => { }), new FakeHibernator(true), _flag, _store, _clock, _ => { });
        await s.SetModeAsync(ManualMode.Max, CancellationToken.None);
        Assert.Empty(runner.Calls);   // not in the request
        await s.ApplyPendingAsync(CancellationToken.None);
        var call = Assert.Single(runner.Calls);
        Assert.Equal(PowerModeAdapter.LiftArgs, call.Args.Skip(4).Take(3));
        Assert.Equal(TimeSpan.FromSeconds(17), Assert.Single(runner.Timeouts));
    }

    // spec S2.8j
    [Fact]
    public async Task Slow_mode_command_outlives_the_request_and_completes()
    {
        using var s = Service();
        _actions.Delay = TimeSpan.FromMilliseconds(300);
        using var request = new CancellationTokenSource();
        var r = await s.SetModeAsync(ManualMode.Max, request.Token);
        await request.CancelAsync();   // the connection is gone
        Assert.Equal(202, r.Status);
        Assert.Empty(_actions.Calls);
        await s.ApplyPendingAsync(CancellationToken.None);
        Assert.Equal(["off"], _actions.Calls);
        Assert.Null(_store.LoadState().ModePending);
    }
}

public sealed class StorageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bluetti-watcher-test-" + Guid.NewGuid().ToString("N"));
    private readonly StationStore _store;

    public StorageTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new StationStore(Path.Combine(_dir, "station.db"));
    }

    public void Dispose()
    {
        _store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private static readonly DateTimeOffset Now = T0;

    // spec S2.4b
    [Fact]
    public void Raw_rows_older_than_30_days_fold_into_5_minute_aggregates_recent_rows_stay()
    {
        var old = Now.AddDays(-31);
        var bucket = DateTimeOffset.FromUnixTimeSeconds(old.ToUnixTimeSeconds() / 300 * 300);
        _store.AddReading(bucket.AddSeconds(10), Bat(80), ReadingStatus.Accepted, "");
        _store.AddReading(bucket.AddSeconds(70), Bat(78) with { OutAc = 100 }, ReadingStatus.Accepted, "");
        _store.AddReading(bucket.AddSeconds(130), null, ReadingStatus.Missed, "");
        _store.AddReading(bucket.AddSeconds(190), Bat(0), ReadingStatus.Rejected, "implausible");
        _store.AddReading(Now.AddDays(-29), Grid(90), ReadingStatus.Accepted, "");

        Assert.Equal(4, _store.Compact(Now, 30));

        Assert.Equal((1, 1), _store.Counts());
        var rows = _store.History(0, long.MaxValue, 1);
        Assert.Equal(2, rows.Count);
        Assert.Equal(bucket.ToUnixTimeSeconds(), rows[0].At);
        Assert.Equal(79, rows[0].Soc);
        Assert.Equal(160, rows[0].OutAc);
        Assert.Equal(0, rows[0].Mains);
        Assert.Equal(1, rows[1].Mains);
    }

    // spec S2.4b
    [Fact]
    public void Compact_twice_merges_a_late_row_into_its_bucket_by_weight()
    {
        var bucket = DateTimeOffset.FromUnixTimeSeconds(Now.AddDays(-40).ToUnixTimeSeconds() / 300 * 300);
        _store.AddReading(bucket, Bat(80), ReadingStatus.Accepted, "");
        _store.AddReading(bucket.AddSeconds(60), Bat(80), ReadingStatus.Accepted, "");
        _store.Compact(Now, 30);
        _store.AddReading(bucket.AddSeconds(120), Bat(77), ReadingStatus.Accepted, "");
        _store.Compact(Now, 30);
        var rows = _store.History(0, long.MaxValue, 1);
        Assert.Single(rows);
        Assert.Equal(79, rows[0].Soc);
    }

    [Fact]
    public void Compact_twice_merges_every_column_of_a_late_row()
    {
        var bucket = DateTimeOffset.FromUnixTimeSeconds(Now.AddDays(-40).ToUnixTimeSeconds() / 300 * 300);
        var r = Bat(80) with { InDc = 10, OutDc = 20, Volt = 230, Hz = 50 };
        _store.AddReading(bucket, r, ReadingStatus.Accepted, "");
        _store.AddReading(bucket.AddSeconds(60), r, ReadingStatus.Accepted, "");
        _store.Compact(Now, 30);
        _store.AddReading(bucket.AddSeconds(120), r with { InDc = 40, OutDc = 50, Volt = 227, Hz = 49.7 }, ReadingStatus.Accepted, "");
        _store.Compact(Now, 30);
        var row = Assert.Single(_store.History(0, long.MaxValue, 1));
        Assert.Equal(20, row.InDc!.Value, 6);
        Assert.Equal(30, row.OutDc!.Value, 6);
        Assert.Equal(229, row.Volt!.Value, 6);
        Assert.Equal(49.9, row.Hz!.Value, 6);
    }

    // spec S2.4c: 5-minute aggregates are kept forever: later compactions, years on, never drop them.
    [Fact]
    public void Aggregates_survive_every_later_compaction()
    {
        var bucket = DateTimeOffset.FromUnixTimeSeconds(Now.AddDays(-40).ToUnixTimeSeconds() / 300 * 300);
        _store.AddReading(bucket, Bat(80), ReadingStatus.Accepted, "");
        _store.Compact(Now, 30);
        Assert.Equal((0, 1), _store.Counts());
        Assert.Equal(0, _store.Compact(Now.AddDays(400), 30));
        Assert.Equal(0, _store.Compact(Now.AddYears(10), 30));
        Assert.Equal((0, 1), _store.Counts());
        Assert.Equal(80, Assert.Single(_store.History(0, long.MaxValue, 1)).Soc);
    }

    [Fact]
    public void Connection_cache_is_capped_at_512_KB() => Assert.Equal(-512L, _store.CacheSize());

    [Fact]
    public void History_averages_per_step_over_raw_rows_and_skips_missed_and_rejected()
    {
        for (int m = 0; m < 10; m++) _store.AddReading(Now.AddMinutes(m), Bat(90 - m), ReadingStatus.Accepted, "");
        _store.AddReading(Now.AddMinutes(3).AddSeconds(30), Bat(0), ReadingStatus.Rejected, "implausible");
        var rows = _store.History(Now.ToUnixTimeSeconds(), Now.AddMinutes(10).ToUnixTimeSeconds(), 300);
        Assert.Equal(2, rows.Count);
        Assert.Equal(88, rows[0].Soc);
        Assert.Equal(83, rows[1].Soc);
    }

    [Fact]
    public void Damaged_state_loads_fresh()
    {
        _store.SetMeta("state", "{ not json");
        Assert.False(_store.LoadState().Cut);
    }

    [Fact]
    public void State_round_trips()
    {
        var s = new StationState { Cut = true, CutSince = Now, Manual = ManualMode.Max, ManualAt = Now, RebasedAt = Now };
        s.Fired[2] = true;
        _store.SaveState(s);
        var back = _store.LoadState();
        Assert.True(back.Cut);
        Assert.Equal(Now, back.CutSince);
        Assert.Equal(ManualMode.Max, back.Manual);
        Assert.Equal(Now, back.RebasedAt);
        Assert.True(back.Fired[2]);
    }
}
