using BluettiWatcher.Decision;
using BluettiWatcher.Io;

namespace BluettiWatcher.Tests;

/// <summary>
/// A custom install's names (flag file, mutex, task prefix) set in the config give the exact command lines the switch has
/// always received, and the same names reach every power-mode child's environment.
/// </summary>
public sealed class PowerModeGoldenTests : IDisposable
{
    // A custom install's names.
    private const string Script = @"C:\pm\power-mode.ps1";
    private const string Flag = @"C:\ProgramData\MyPowerMode\power-mode.json";
    private const string Mutex = @"Local\MyPowerModeFlag";
    private const string Prefix = "MyEcoMode";
    private const string Reader = "mypmreader";

    private static readonly string[] Golden =
    [
        @"pwsh.exe|-NoProfile -NonInteractive -File C:\pm\power-mode.ps1 eco -Source station -Path C:\ProgramData\MyPowerMode\power-mode.json",
        @"pwsh.exe|-NoProfile -NonInteractive -File C:\pm\power-mode.ps1 on -Source station -Path C:\ProgramData\MyPowerMode\power-mode.json",
        @"pwsh.exe|-NoProfile -NonInteractive -File C:\pm\power-mode.ps1 off -Source station -Path C:\ProgramData\MyPowerMode\power-mode.json",
        @"pwsh.exe|-NoProfile -NonInteractive -File C:\pm\power-mode.ps1 battery -Soc 19.5 -Level low -Mains off -HibernateAt 5 -Path C:\ProgramData\MyPowerMode\power-mode.json",
    ];

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bluetti-watcher-golden-" + Guid.NewGuid().ToString("N"));

    public PowerModeGoldenTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private StationConfig Load(string json)
    {
        string file = Path.Combine(_dir, "config.json");
        File.WriteAllText(file, json);
        return StationConfig.Load(["--config", file, "--data-dir", _dir]);
    }

    private static async Task<List<string>> Lines(StationConfig cfg, FakeRunner runner)
    {
        var a = new PowerModeAdapter(cfg, runner, _ => { });
        await a.StationEcoAsync(CancellationToken.None);
        await a.StationEmergencyAsync(CancellationToken.None);
        await a.LiftAsync(CancellationToken.None);
        await a.BatteryAsync(new BatteryReport(19.5, Level.Low, false), 5, CancellationToken.None);
        return [.. runner.Calls.Select(c => c.Exe + "|" + string.Join(' ', c.Args))];
    }

    private static void AssertEnvironment(FakeRunner runner)
    {
        Assert.Equal(4, runner.Envs.Count);
        foreach (var env in runner.Envs)
        {
            Assert.NotNull(env);
            Assert.Equal(Flag, env["POWER_MODE_FILE"]);
            Assert.Equal(Mutex, env["POWER_MODE_MUTEX"]);
            Assert.Equal(Prefix, env["POWER_MODE_TASK_PREFIX"]);
            Assert.Equal(Reader, env["POWER_MODE_EXTRA_READERS"]);
        }
    }

    // The live config shape: the top-level flagPath (the alias) beside the powerMode section.
    [Fact]
    public async Task Custom_install_names_through_the_flagPath_alias_give_the_same_command_lines_and_environment()
    {
        var cfg = Load($$"""{ "flagPath": {{Json(Flag)}}, "powerMode": { "script": {{Json(Script)}}, "mutex": {{Json(Mutex)}}, "taskPrefix": "{{Prefix}}", "extraReaders": ["{{Reader}}"] } }""");
        var runner = new FakeRunner();
        Assert.Equal(Golden, await Lines(cfg, runner));
        AssertEnvironment(runner);
        Assert.Equal(Mutex, cfg.PowerModeMutex);
    }

    [Fact]
    public async Task Custom_install_names_in_the_powerMode_section_give_the_same_command_lines_and_environment()
    {
        var cfg = Load($$"""{ "powerMode": { "script": {{Json(Script)}}, "flagPath": {{Json(Flag)}}, "mutex": {{Json(Mutex)}}, "taskPrefix": "{{Prefix}}", "extraReaders": ["{{Reader}}"] } }""");
        var runner = new FakeRunner();
        Assert.Equal(Golden, await Lines(cfg, runner));
        AssertEnvironment(runner);
    }

    [Fact]
    public async Task Flags_power_mode_and_flag_still_set_the_script_and_the_flag_file()
    {
        string file = Path.Combine(_dir, "config.json");
        await File.WriteAllTextAsync(file, $$"""{ "flagPath": "C:\\elsewhere.json", "powerMode": { "mutex": {{Json(Mutex)}}, "taskPrefix": "{{Prefix}}", "extraReaders": ["{{Reader}}"] } }""");
        var cfg = StationConfig.Load(["--config", file, "--power-mode", Script, "--flag", Flag]);
        var runner = new FakeRunner();
        Assert.Equal(Golden, await Lines(cfg, runner));
        AssertEnvironment(runner);
    }

    [Fact]
    public void Powermode_section_flagPath_wins_over_the_alias()
    {
        var cfg = Load("""{ "flagPath": "C:\\alias.json", "powerMode": { "flagPath": "C:\\section.json" } }""");
        Assert.Equal(@"C:\section.json", cfg.FlagFile);
    }

    [Fact]
    public void Defaults_are_neutral()
    {
        var cfg = new StationConfig();
        Assert.Equal(@"Local\BluettiWatcherPowerMode", cfg.PowerModeMutex);
        Assert.Equal("BluettiWatcherEco", cfg.PowerModeTaskPrefix);
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BluettiWatcher", "power-mode.json"), cfg.FlagFile);
        Assert.Equal(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "power-mode", "power-mode.ps1")), cfg.PowerModeScript);
        Assert.Equal(60, cfg.PowerModeTimeoutSec);
        Assert.Equal(30, cfg.ActionTimeoutSec);
        Assert.Null(cfg.Mac);
    }

    [Theory]
    [InlineData("""{ "flagPath": "C:\\f.json" }""", true)]
    [InlineData("""{ "powerMode": { "flagPath": "C:\\f.json" } }""", true)]
    [InlineData("""{ "flagPath": "C:\\f.json", "powerMode": { "mutex": "Local\\MyMutex" } }""", false)]
    [InlineData("""{ "powerMode": { "mutex": "Local\\MyMutex" } }""", false)]
    [InlineData("""{ }""", false)]
    public void A_flag_path_with_the_default_mutex_warns(string json, bool warns)
    {
        var w = Load(json).MutexWarning;
        if (warns) Assert.Equal("flagPath set but powerMode.mutex is the default; a power-mode switch installed elsewhere must use the same mutex", w);
        else Assert.Null(w);
    }

    [Fact]
    public async Task Several_extra_readers_are_joined_with_commas_and_none_gives_an_empty_value()
    {
        var two = Load("""{ "powerMode": { "extraReaders": ["mypmreader", "My Reader-2.x_y"] } }""");
        var runner = new FakeRunner();
        await new PowerModeAdapter(two, runner, _ => { }).LiftAsync(CancellationToken.None);
        Assert.Equal("mypmreader,My Reader-2.x_y", runner.Envs[0]!["POWER_MODE_EXTRA_READERS"]);
        Assert.Null(two.ExtraReadersProblem);

        var none = Load("""{ }""");
        Assert.Empty(none.ExtraReaders);
        Assert.Equal("", none.PowerModeEnvironment["POWER_MODE_EXTRA_READERS"]);
        Assert.Null(none.ExtraReadersProblem);
    }

    [Theory]
    [InlineData("bad;name")]
    [InlineData("a,b")]
    [InlineData("")]
    [InlineData("DOMAIN\\user")]
    [InlineData("name\n")]
    public async Task An_invalid_extra_reader_name_is_a_health_problem_naming_it_and_nothing_is_passed(string bad)
    {
        var cfg = Load($$"""{ "mac": "AA:BB:CC:DD:EE:FF", "powerMode": { "extraReaders": ["mypmreader", {{Json(bad)}}] } }""");
        Assert.NotNull(cfg.ExtraReadersProblem);
        Assert.Contains("powerMode.extraReaders", cfg.ConfigProblem, StringComparison.Ordinal);
        Assert.Contains($"\"{bad}\"", cfg.ConfigProblem, StringComparison.Ordinal);
        Assert.Empty(cfg.ExtraReaders);
        var runner = new FakeRunner();
        await new PowerModeAdapter(cfg, runner, _ => { }).LiftAsync(CancellationToken.None);
        Assert.Equal("", runner.Envs[0]!["POWER_MODE_EXTRA_READERS"]);
    }

    [Fact]
    public void A_name_over_64_characters_is_invalid_and_64_is_valid()
    {
        Assert.Null(Load($$"""{ "powerMode": { "extraReaders": ["{{new string('a', 64)}}"] } }""").ExtraReadersProblem);
        Assert.NotNull(Load($$"""{ "powerMode": { "extraReaders": ["{{new string('a', 65)}}"] } }""").ExtraReadersProblem);
    }

    [Fact]
    public void The_extra_reader_problem_joins_the_missing_mac_problem()
    {
        var cfg = Load("""{ "powerMode": { "extraReaders": ["bad;name"] } }""");
        Assert.Contains("mac is not set", cfg.ConfigProblem, StringComparison.Ordinal);
        Assert.Contains("bad;name", cfg.ConfigProblem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(45, 45)]
    [InlineData(100000, 600)]
    public void Timeouts_stay_within_1_and_600_seconds(int set, int expected)
    {
        var cfg = Load($$"""{ "actionTimeoutSec": {{set}}, "powerModeTimeoutSec": {{set}} }""");
        Assert.Equal(expected, cfg.ActionTimeoutSec);
        Assert.Equal(expected, cfg.PowerModeTimeoutSec);
    }

    private static string Json(string s) => System.Text.Json.JsonSerializer.Serialize(s, StationJsonStrings.Default.String);
}

[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
internal sealed partial class StationJsonStrings : System.Text.Json.Serialization.JsonSerializerContext;
