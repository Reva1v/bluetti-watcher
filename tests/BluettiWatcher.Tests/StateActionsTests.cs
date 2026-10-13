using System.Text.Json;
using BluettiWatcher.Decision;
using BluettiWatcher.Http;
using BluettiWatcher.Io;
using BluettiWatcher.Storage;
using static BluettiWatcher.Tests.Fx;

namespace BluettiWatcher.Tests;

/// <summary>A line runner that answers with set output and records the environment it was given.</summary>
internal sealed class EchoLineRunner(string output) : ICommandLineRunner
{
    public List<IReadOnlyDictionary<string, string>?> Envs { get; } = [];

    public Task<ProcessResult> RunLineAsync(string commandLine, TimeSpan timeout, CancellationToken ct, IReadOnlyDictionary<string, string>? env = null)
    {
        lock (Envs) Envs.Add(env);
        return Task.FromResult(new ProcessResult(3, [output], false));
    }
}

/// <summary>The config's <c>actions</c>: once per entry, never blocking the decision, every run an event.</summary>
public sealed class StateActionsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bluetti-actions-test-" + Guid.NewGuid().ToString("N"));
    private readonly StationStore _store;
    private readonly ManualClock _clock = new(T0);

    public StateActionsTests()
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

    private List<string> ActionEvents() => [.. _store.Events(0, long.MaxValue).Where(e => e.Kind == "action").Select(e => e.Detail)];

    [Fact]
    public async Task Each_state_runs_its_command_once_per_entry()
    {
        var lines = new FakeLineRunner();
        var cfg = new StationConfig { DataDir = _dir, Actions = ServiceTests.EveryAction() };
        var a = new StateActions(cfg, lines, _store, _clock, _ => { });
        foreach (var s in new[] { "eco", "eco", "emergency", "emergency", "stop", "hibernate-warn", "hibernate-warn", "normal", "normal", "eco" }) a.Enter(s);
        await a.IdleAsync();
        Assert.Equal(["eco-cmd", "eco-cmd", "emergency-cmd", "normal-cmd", "stop-cmd", "warn-cmd"], lines.Lines.Order(StringComparer.Ordinal));   // they run concurrently
        Assert.Equal(6, ActionEvents().Count);
    }

    [Fact]
    public async Task A_new_cut_enters_its_states_again_even_when_the_last_entry_was_the_same()
    {
        var lines = new FakeLineRunner();
        var cfg = new StationConfig { DataDir = _dir, Actions = ServiceTests.EveryAction() };
        var a = new StateActions(cfg, lines, _store, _clock, _ => { });
        a.Enter("eco");
        a.Enter("eco");   // two entries within one cut
        await a.IdleAsync();
        Assert.Equal(["eco-cmd"], lines.Lines);
        a.CutEnded();
        a.Enter("eco");
        await a.IdleAsync();
        Assert.Equal(["eco-cmd", "eco-cmd"], lines.Lines);
    }

    // Cut 1: eco on, the user switches it off by hand, mains returns with nothing to lift (no normal entered); cut 2: eco on again.
    [Fact]
    public async Task Eco_in_the_second_cut_runs_its_action_when_the_first_cut_ended_without_a_lift()
    {
        var lines = new FakeLineRunner();
        var actions = new FakeActions();
        var cfg = new StationConfig { DataDir = _dir, MainsLostSec = 300, MainsBackSec = 300, Actions = ServiceTests.EveryAction() };
        var onState = new StateActions(cfg, lines, _store, _clock, _ => { });
        var reader = new QueueReader();
        using var s = new StationService(cfg, reader, actions, new FakeHibernator(true), new FixedFlag(Flag()), _store, _clock, _ => { }, null, onState);
        async Task Polls(Reading r, int n)
        {
            reader.Default = r;
            for (int k = 0; k < n; k++) { await s.PollAsync(CancellationToken.None); _clock.Now = _clock.Now.AddMinutes(2); }
        }
        await Polls(Bat(80), 6);    // cut 1: eco proposed on several polls (the flag stays normal), its action runs once
        await onState.IdleAsync();
        Assert.Equal(["eco-cmd"], lines.Lines);
        await Polls(Grid(80), 6);   // mains back, the flag already normal: nothing to lift
        Assert.DoesNotContain("off", actions.Calls);
        Assert.False(s.State.Cut);
        await Polls(Bat(80), 6);    // cut 2
        await onState.IdleAsync();
        Assert.Equal(["eco-cmd", "eco-cmd"], lines.Lines);
    }

    [Fact]
    public async Task A_lift_runs_the_normal_action_once_and_the_next_cut_enters_eco_again()
    {
        var lines = new FakeLineRunner();
        var actions = new FakeActions();
        var cfg = new StationConfig { DataDir = _dir, MainsLostSec = 300, MainsBackSec = 300, Actions = ServiceTests.EveryAction() };
        var onState = new StateActions(cfg, lines, _store, _clock, _ => { });
        var reader = new QueueReader();
        var flag = new FixedFlag(Flag());
        using var s = new StationService(cfg, reader, actions, new FakeHibernator(true), flag, _store, _clock, _ => { }, null, onState);
        async Task Polls(Reading r, int n)
        {
            reader.Default = r;
            for (int k = 0; k < n; k++) { await s.PollAsync(CancellationToken.None); _clock.Now = _clock.Now.AddMinutes(2); }
        }
        await Polls(Bat(80), 6);
        flag.View = Station;   // eco is on when mains comes back: the lift runs
        await Polls(Grid(80), 8);
        Assert.Contains("off", actions.Calls);
        flag.View = Flag();
        await Polls(Bat(80), 6);
        await onState.IdleAsync();
        Assert.Equal(["eco-cmd", "eco-cmd", "normal-cmd"], lines.Lines.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_state_without_a_command_runs_nothing_and_still_counts_as_entered()
    {
        var lines = new FakeLineRunner();
        var cfg = new StationConfig { DataDir = _dir, Actions = new() { ["eco"] = "eco-cmd" } };
        var a = new StateActions(cfg, lines, _store, _clock, _ => { });
        a.Enter("normal");
        a.Enter("eco");
        a.Enter("normal");
        a.Enter("eco");
        await a.IdleAsync();
        Assert.Equal(["eco-cmd", "eco-cmd"], lines.Lines);
    }

    [Fact]
    public async Task Exit_code_and_the_first_300_characters_are_the_event_and_the_power_mode_names_reach_the_child()
    {
        var runner = new EchoLineRunner(new string('x', 500));
        var cfg = new StationConfig { DataDir = _dir, Actions = new() { ["stop"] = "notify.exe --stop" }, PowerMode = new() { Mutex = @"Local\Custom", TaskPrefix = "CustomEco" } };
        var a = new StateActions(cfg, runner, _store, _clock, _ => { });
        a.Enter("stop");
        await a.IdleAsync();
        Assert.Equal("stop: notify.exe --stop -> exit 3: " + new string('x', 300), Assert.Single(ActionEvents()));
        var env = Assert.Single(runner.Envs)!;
        Assert.Equal(@"Local\Custom", env["POWER_MODE_MUTEX"]);
        Assert.Equal("CustomEco", env["POWER_MODE_TASK_PREFIX"]);
        Assert.Equal(cfg.FlagFile, env["POWER_MODE_FILE"]);
    }

    [Fact]
    public async Task A_missing_executable_is_an_event_and_nothing_else()
    {
        string missing = Path.Combine(_dir, "no-such-tool.exe");
        var cfg = new StationConfig { DataDir = _dir, Actions = new() { ["eco"] = $"\"{missing}\" --now" } };
        var log = new List<string>();
        var a = new StateActions(cfg, new ProcessRunner(), _store, _clock, l => { lock (log) log.Add(l); });
        a.Enter("eco");
        await a.IdleAsync().WaitAsync(TimeSpan.FromSeconds(30));
        string e = Assert.Single(ActionEvents());
        Assert.StartsWith($"eco: \"{missing}\" --now -> could not run: Win32Exception", e, StringComparison.Ordinal);
        Assert.Contains(log, l => l.Contains("could not run", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Dry_run_runs_no_action()
    {
        var lines = new FakeLineRunner();
        var cfg = new StationConfig { DataDir = _dir, DryRun = true, Actions = new() { ["eco"] = "eco-cmd" } };
        var a = new StateActions(cfg, lines, _store, _clock, _ => { });
        a.Enter("eco");
        await a.IdleAsync();
        Assert.Empty(lines.Lines);
        Assert.Equal("eco: dry run, would run: eco-cmd", Assert.Single(ActionEvents()));
    }

    [Fact]
    public void Command_line_splits_into_the_executable_and_the_rest()
    {
        Assert.Equal(("ping", "-n 3 127.0.0.1"), ProcessRunner.SplitCommandLine("  ping -n 3 127.0.0.1 "));
        Assert.Equal((@"C:\Program Files\x\t.exe", "\"a b\" c"), ProcessRunner.SplitCommandLine("\"C:\\Program Files\\x\\t.exe\" \"a b\" c"));
        Assert.Equal(("tool.exe", ""), ProcessRunner.SplitCommandLine("tool.exe"));
        Assert.Throws<ArgumentException>(() => ProcessRunner.SplitCommandLine("\"unclosed"));
    }

    // A hanging command is killed at actionTimeoutSec; the polls go on meanwhile and the decision is unchanged.
    [WindowsFact]
    public async Task A_hanging_command_is_killed_at_the_timeout_and_the_poll_goes_on()
    {
        var actions = new FakeActions();
        var cfg = new StationConfig { DataDir = _dir, MainsLostSec = 300, MainsBackSec = 300, ActionTimeoutSec = 3, Actions = new() { ["eco"] = "ping -n 60 127.0.0.1" } };
        var onState = new StateActions(cfg, new ProcessRunner(), _store, _clock, _ => { });
        using var s = new StationService(cfg, new QueueReader { Default = Bat(80) }, actions, new FakeHibernator(true), new FixedFlag(Flag()), _store, _clock, _ => { }, null, onState);
        for (int k = 0; k < 4; k++) { await s.PollAsync(CancellationToken.None); _clock.Now = _clock.Now.AddMinutes(2); }
        Assert.Contains("eco", actions.Calls);
        var idle = onState.IdleAsync();
        Assert.False(idle.IsCompleted);   // the poll returned while the command runs
        await s.PollAsync(CancellationToken.None);   // and the next one runs too
        await idle.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.StartsWith("eco: ping -n 60 127.0.0.1 -> killed after 3 s: ", Assert.Single(ActionEvents()), StringComparison.Ordinal);
        Assert.True(s.State.Cut);
    }
}

/// <summary>No default station address: the service serves the page and says what is missing.</summary>
public sealed class MissingMacTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bluetti-mac-test-" + Guid.NewGuid().ToString("N"));
    private readonly StationStore _store;

    public MissingMacTests()
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

    private async Task<(int Status, JsonElement Body)> Get(StationConfig cfg, string path)
    {
        var clock = new ManualClock(T0);
        using var service = new StationService(cfg, new QueueReader(), new FakeActions(), new FakeHibernator(true), new FixedFlag(FlagView.Normal), _store, clock, _ => { });
        var api = new StationApi(cfg, service, _store, clock, T0);
        var r = await api.HandleAsync(new HttpRequest("GET", path, new Dictionary<string, string>(), new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Host"] = "127.0.0.1:18940" }, []), CancellationToken.None);
        return (r.Status, path == "/" ? default : JsonDocument.Parse(r.Body).RootElement.Clone());
    }

    [Fact]
    public async Task Without_a_mac_health_names_the_problem_and_the_page_is_served()
    {
        var cfg = new StationConfig { DataDir = _dir };
        var (status, health) = await Get(cfg, "/api/health");
        Assert.Equal(200, status);
        Assert.False(health.GetProperty("ok").GetBoolean());
        Assert.Contains("mac", health.GetProperty("problem").GetString(), StringComparison.Ordinal);
        Assert.Equal(200, (await Get(cfg, "/")).Status);
    }

    [Theory]
    [InlineData("""{ }""")]
    [InlineData("""{ "mac": "  " }""")]
    public void A_config_without_a_mac_has_the_problem(string json)
    {
        string file = Path.Combine(_dir, "config.json");
        File.WriteAllText(file, json);
        Assert.NotNull(StationConfig.Load(["--config", file]).MacProblem);
    }

    [Fact]
    public async Task With_a_mac_health_is_ok_with_no_problems()
    {
        var (_, health) = await Get(new StationConfig { DataDir = _dir, Mac = "00:11:22:33:44:55" }, "/api/health");
        Assert.True(health.GetProperty("ok").GetBoolean());
        Assert.Equal(JsonValueKind.Null, health.GetProperty("problem").ValueKind);
    }
}

/// <summary>Times people read are in the PC's local zone (a test gives the zone).</summary>
public sealed class LocalClockTests
{
    private static readonly TimeZoneInfo Utc5 = TimeZoneInfo.CreateCustomTimeZone("u5", TimeSpan.FromHours(5), "u5", "u5");

    [Fact]
    public void Clock_is_in_the_given_zone_and_local_when_none()
    {
        var at = DateTimeOffset.Parse("2026-10-06T17:08:00Z", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal("22:08", FlagFileReader.Clock(at, Utc5));
        Assert.Equal(TimeZoneInfo.ConvertTime(at, TimeZoneInfo.Local).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture), FlagFileReader.Clock(at));
    }

    [Fact]
    public void Eco_text_shows_the_until_time_in_the_given_zone()
    {
        var now = DateTimeOffset.Parse("2026-10-06T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var flag = new PowerModeFlag("eco", "manual", "", now, now.AddHours(2), false, null);
        Assert.Equal("Eco mode until 17:00", FlagFileReader.EcoText(flag, now, Utc5));
        Assert.Equal("17:00", FlagFileReader.EcoNote(flag, now, Utc5)!.Args[0]);
    }
}
