namespace BluettiWatcher;

internal enum CliAction { Run, CloudLogin, Help, Version, Error }

/// <summary>What the command line asks for; <see cref="Message"/> is the argument error for <see cref="CliAction.Error"/>.</summary>
internal sealed record CliRequest(CliAction Action, string Message = "");

/// <summary>The command line of the exe: which command it is, the usage text, the argument errors.</summary>
internal static class Cli
{
    internal const string ErrorPrefix = "Bluetti Watcher: ";

    internal const string Usage = """
        Bluetti Watcher - reads a BLUETTI power station and switches the PC's power mode.

        Usage:
          BluettiWatcher.exe [options]          run the service
          BluettiWatcher.exe --cloud-login      sign in to the BLUETTI cloud and save the token file
          BluettiWatcher.exe --help | --version

        Options:
          --config <file>        config file (default: <data dir>\config.json)
          --data-dir <dir>       data directory (default: %LOCALAPPDATA%\bluetti-watcher)
          --port <n>             port of the page on 127.0.0.1 (default: 18940)
          --reader <exe>         path of bluetti-read.exe
          --flag <file>          power-mode flag file
          --power-mode <script>  path of power-mode.ps1
          --dry-run              decide and log, never hibernate
          --cloud-login          sign in to the BLUETTI cloud (opens the browser, prints no token)
          --version              print the version
          -h, --help, /?         print this text

        Exit codes: 0 stopped, printed or token saved - 1 the login failed - 2 could not run (bad arguments, port taken, no reader).
        """;

    private static readonly HashSet<string> HelpTokens = new(["--help", "-h", "/?"], StringComparer.OrdinalIgnoreCase);

    /// <summary>The first line of <c>--version</c>.</summary>
    internal static string VersionText()
    {
        var v = typeof(Cli).Assembly.GetName().Version ?? new Version(1, 0, 0);
        return $"Bluetti Watcher {v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}";
    }

    /// <summary>A help token anywhere wins over everything else on the line, whatever follows it.</summary>
    internal static CliRequest Parse(string[] args)
    {
        if (args.Any(HelpTokens.Contains)) return new CliRequest(CliAction.Help);
        try
        {
            var flags = StationConfig.ParseArgs(args);
            if (flags.ContainsKey("version")) return new CliRequest(CliAction.Version);
            return new CliRequest(flags.ContainsKey("cloud-login") ? CliAction.CloudLogin : CliAction.Run);
        }
        catch (ArgumentException e)
        {
            return new CliRequest(CliAction.Error, e.Message);
        }
    }

    /// <summary>
    /// Runs a command that is not the service. <see cref="CliOutcome.Exit"/> is null when the service is to run (with
    /// <see cref="CliOutcome.Config"/>); the login starts only for a line that asks for it and nothing else.
    /// </summary>
    internal static async Task<CliOutcome> ExecuteAsync(string[] args, TextWriter output, TextWriter error, Func<StationConfig, Task<int>> login)
    {
        var request = Parse(args);
        switch (request.Action)
        {
            case CliAction.Help:
                await output.WriteLineAsync(Usage);
                return new CliOutcome(0, null);
            case CliAction.Version:
                await output.WriteLineAsync(VersionText());
                return new CliOutcome(0, null);
            case CliAction.Error:
                await error.WriteLineAsync($"{ErrorPrefix}{request.Message}{Environment.NewLine}Try BluettiWatcher.exe --help");
                return new CliOutcome(2, null);
        }
        StationConfig config;
        try { config = StationConfig.Load(args); }
        catch (Exception e) when (e is ArgumentException or FormatException or OverflowException or System.Text.Json.JsonException or IOException)
        {
            await error.WriteLineAsync($"{ErrorPrefix}COULD NOT RUN: {e.Message}");
            return new CliOutcome(2, null);
        }
        if (request.Action == CliAction.CloudLogin) return new CliOutcome(await login(config), null);
        return new CliOutcome(null, config);
    }
}

internal sealed record CliOutcome(int? Exit, StationConfig? Config);
