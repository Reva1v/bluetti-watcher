namespace BluettiWatcher.Tests;

/// <summary>The command line: help, version, argument errors, and which line starts the cloud login.</summary>
public sealed class CliTests
{
    private static readonly string[] AllFlags = ["--config", "--data-dir", "--port", "--reader", "--flag", "--power-mode", "--dry-run", "--cloud-login", "--version", "--help"];

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("/?")]
    [InlineData("--HELP")]
    public void A_help_token_asks_for_help(string token) =>
        Assert.Equal(CliAction.Help, Cli.Parse([token]).Action);

    [Theory]
    [InlineData("--cloud-login", "--help", "foo")]
    [InlineData("--help", "--cloud-login")]
    [InlineData("--config", "--help")]
    [InlineData("--bogus", "-h")]
    [InlineData("--port", "x", "/?")]
    public void Help_wins_over_everything_else_on_the_line(params string[] args) =>
        Assert.Equal(CliAction.Help, Cli.Parse(args).Action);

    [Fact]
    public void The_plain_commands_are_told_apart()
    {
        Assert.Equal(CliAction.Run, Cli.Parse([]).Action);
        Assert.Equal(CliAction.Run, Cli.Parse(["--dry-run", "--port", "18999"]).Action);
        Assert.Equal(CliAction.Version, Cli.Parse(["--version"]).Action);
        Assert.Equal(CliAction.CloudLogin, Cli.Parse(["--cloud-login", "--data-dir", "x"]).Action);
    }

    [Theory]
    [InlineData("--config")]
    [InlineData("--data-dir")]
    [InlineData("--port")]
    [InlineData("--reader")]
    [InlineData("--flag")]
    [InlineData("--power-mode")]
    public void A_flag_that_takes_a_value_refuses_a_missing_value_or_one_that_is_a_flag(string flag)
    {
        var missing = Cli.Parse([flag]);
        Assert.Equal(CliAction.Error, missing.Action);
        Assert.Equal($"{flag} needs a value", missing.Message);
        var flagValue = Cli.Parse([flag, "--dry-run"]);
        Assert.Equal(CliAction.Error, flagValue.Action);
        Assert.Equal($"{flag} needs a value", flagValue.Message);
    }

    [Theory]
    [InlineData("--bogus", "unknown flag '--bogus'")]
    [InlineData("--import", "unknown flag '--import'")]
    [InlineData("stray", "unknown argument 'stray'")]
    [InlineData("-x", "unknown argument '-x'")]
    public void An_unknown_flag_or_word_is_an_error_naming_it(string arg, string message)
    {
        var request = Cli.Parse([arg]);
        Assert.Equal(CliAction.Error, request.Action);
        Assert.Equal(message, request.Message);
    }

    [Fact]
    public void ParseArgs_keeps_a_value_that_merely_looks_like_a_path()
    {
        var flags = StationConfig.ParseArgs(["--reader", @"C:\a b\-r.exe", "--dry-run"]);
        Assert.Equal(@"C:\a b\-r.exe", flags["reader"]);
        Assert.Equal("1", flags["dry-run"]);
    }

    [Fact]
    public void The_version_line_names_the_product_and_the_project_version()
    {
        Assert.Equal("Bluetti Watcher 1.0.0", Cli.VersionText());
    }

    [Fact]
    public void The_usage_names_every_flag()
    {
        foreach (var flag in AllFlags) Assert.Contains(flag, Cli.Usage, StringComparison.Ordinal);
        Assert.Contains("-h", Cli.Usage, StringComparison.Ordinal);
        Assert.Contains("/?", Cli.Usage, StringComparison.Ordinal);
    }

    private sealed class Run : IDisposable
    {
        public readonly StringWriter Out = new();
        public readonly StringWriter Err = new();
        public int Logins;
        public StationConfig? LoginConfig;
        public CliOutcome? Outcome;

        public void Dispose() { Out.Dispose(); Err.Dispose(); }

        public async Task<Run> Execute(params string[] args)
        {
            Outcome = await Cli.ExecuteAsync(args, Out, Err, cfg => { Logins++; LoginConfig = cfg; return Task.FromResult(0); });
            return this;
        }
    }

    [Theory]
    [InlineData("--cloud-login", "--help", "foo")]
    [InlineData("--help", "--cloud-login")]
    [InlineData("--cloud-login", "-h")]
    public async Task Help_on_a_login_line_prints_the_usage_and_never_starts_the_login(params string[] args)
    {
        using var run = await new Run().Execute(args);
        Assert.Equal(0, run.Outcome!.Exit);
        Assert.Equal(0, run.Logins);
        Assert.StartsWith("Bluetti Watcher - ", run.Out.ToString(), StringComparison.Ordinal);
        Assert.Equal("", run.Err.ToString());
    }

    [Fact]
    public async Task A_bad_login_line_never_starts_the_login()
    {
        using var run = await new Run().Execute("--cloud-login", "foo");
        Assert.Equal(2, run.Outcome!.Exit);
        Assert.Equal(0, run.Logins);
        Assert.Contains("unknown argument 'foo'", run.Err.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_login_line_starts_the_login_once_with_the_loaded_config()
    {
        string dir = Path.Combine(Path.GetTempPath(), "bluetti-cli-test-" + Guid.NewGuid().ToString("N"));
        using var run = await new Run().Execute("--cloud-login", "--data-dir", dir);
        Assert.Equal(0, run.Outcome!.Exit);
        Assert.Equal(1, run.Logins);
        Assert.Equal(dir, run.LoginConfig!.DataDir);
    }

    [Fact]
    public async Task Version_prints_one_line_and_exits_0()
    {
        using var run = await new Run().Execute("--version");
        Assert.Equal(0, run.Outcome!.Exit);
        Assert.Equal("Bluetti Watcher 1.0.0" + Environment.NewLine, run.Out.ToString());
        Assert.Equal(0, run.Logins);
    }

    [Fact]
    public async Task An_unknown_flag_prints_the_message_and_a_pointer_to_help_and_exits_2()
    {
        using var run = await new Run().Execute("--bogus");
        Assert.Equal(2, run.Outcome!.Exit);
        Assert.Equal("", run.Out.ToString());
        Assert.Contains("Bluetti Watcher: unknown flag '--bogus'", run.Err.ToString(), StringComparison.Ordinal);
        Assert.Contains("--help", run.Err.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_config_that_cannot_be_read_exits_2_with_the_reason()
    {
        string dir = Path.Combine(Path.GetTempPath(), "bluetti-cli-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "config.json");
            await File.WriteAllTextAsync(file, "{ not json");
            using var run = await new Run().Execute("--config", file);
            Assert.Equal(2, run.Outcome!.Exit);
            Assert.StartsWith("Bluetti Watcher: COULD NOT RUN: ", run.Err.ToString(), StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task A_plain_line_hands_the_config_to_the_service()
    {
        string dir = Path.Combine(Path.GetTempPath(), "bluetti-cli-test-" + Guid.NewGuid().ToString("N"));
        using var run = await new Run().Execute("--data-dir", dir, "--port", "18999", "--dry-run");
        Assert.Null(run.Outcome!.Exit);
        Assert.Equal(18999, run.Outcome.Config!.Port);
        Assert.True(run.Outcome.Config.DryRun);
        Assert.Equal(0, run.Logins);
        Assert.Equal("", run.Out.ToString() + run.Err);
    }
}
