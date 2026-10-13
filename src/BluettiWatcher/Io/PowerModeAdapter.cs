using System.Globalization;
using BluettiWatcher.Decision;

namespace BluettiWatcher.Io;

internal sealed record ActionResult(bool Ok, int ExitCode, string CommandLine, string LastLine)
{
    public bool DryRun => ExitCode == DryRunExit;
    public const int DryRunExit = -2;
}

/// <summary>Every change on this PC goes through the existing tested switch, power-mode.ps1; nothing writes the flag itself.</summary>
internal interface IPowerModeActions
{
    Task<ActionResult> StationEcoAsync(CancellationToken ct);
    Task<ActionResult> StationEmergencyAsync(CancellationToken ct);
    Task<ActionResult> LiftAsync(CancellationToken ct);
    Task<ActionResult> BatteryAsync(BatteryReport battery, double hibernateAt, CancellationToken ct);
}

/// <summary>
/// The command lines, in one place so they can follow power-mode.ps1:
/// station eco = <c>eco -Source station</c> (no end time; lasts until off) · station emergency = <c>on -Source station</c> ·
/// lift (mains back, and the page's Max) = <c>off -Source station</c> (every mode lifted, Balanced + Best performance paid
/// back) · battery = <c>battery -Soc -Level -Mains -HibernateAt</c>.
/// Each runs as <c>pwsh -NoProfile -File &lt;script&gt; ... -Path &lt;flag&gt;</c>, windowless, only at a transition, with
/// POWER_MODE_FILE, POWER_MODE_MUTEX, POWER_MODE_TASK_PREFIX and POWER_MODE_EXTRA_READERS in its environment.
/// </summary>
internal sealed class PowerModeAdapter(StationConfig config, IProcessRunner runner, Action<string> log) : IPowerModeActions
{
    public static IReadOnlyList<string> EcoArgs { get; } = ["eco", "-Source", "station"];
    public static IReadOnlyList<string> EmergencyArgs { get; } = ["on", "-Source", "station"];
    public static IReadOnlyList<string> LiftArgs { get; } = ["off", "-Source", "station"];

    public static IReadOnlyList<string> BatteryArgs(BatteryReport b, double hibernateAt)
    {
        var inv = CultureInfo.InvariantCulture;
        return ["battery", "-Soc", b.Soc.ToString("0.##", inv), "-Level", b.Level.ToString().ToLowerInvariant(),
            "-Mains", b.Mains ? "on" : "off", "-HibernateAt", hibernateAt.ToString("0.##", inv)];
    }

    /// <summary>The full argument list for pwsh.</summary>
    public IReadOnlyList<string> PowerModeCommand(IReadOnlyList<string> args) =>
        ["-NoProfile", "-NonInteractive", "-File", config.PowerModeScript, .. args, "-Path", config.FlagFile];

    public Task<ActionResult> StationEcoAsync(CancellationToken ct) => RunAsync(PowerModeCommand(EcoArgs), ct);
    public Task<ActionResult> StationEmergencyAsync(CancellationToken ct) => RunAsync(PowerModeCommand(EmergencyArgs), ct);
    public Task<ActionResult> LiftAsync(CancellationToken ct) => RunAsync(PowerModeCommand(LiftArgs), ct);
    public Task<ActionResult> BatteryAsync(BatteryReport battery, double hibernateAt, CancellationToken ct) =>
        RunAsync(PowerModeCommand(BatteryArgs(battery, hibernateAt)), ct);

    private async Task<ActionResult> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        string line = config.PwshPath + " " + string.Join(' ', args.Select(a => a.Contains(' ', StringComparison.Ordinal) ? $"\"{a}\"" : a));
        if (config.DryRun)
        {
            log($"dry run, would run: {line}");
            return new ActionResult(false, ActionResult.DryRunExit, line, "dry run");
        }
        var r = await runner.RunAsync(config.PwshPath, args, TimeSpan.FromSeconds(config.PowerModeTimeoutSec), ct, config.PowerModeEnvironment);
        string last = r.Lines.Count > 0 ? r.Lines[^1] : r.TimedOut ? "timed out" : "";
        log($"{line} -> exit {r.ExitCode}: {last}");
        return new ActionResult(r.ExitCode == 0 && !r.TimedOut, r.ExitCode, line, last);
    }
}

internal interface IHibernator
{
    /// <summary>true = the hibernation command succeeded.</summary>
    Task<bool> HibernateAsync(CancellationToken ct);
}

/// <summary><c>shutdown /h</c>, windowless.</summary>
internal sealed class ShutdownHibernator(IProcessRunner runner, Action<string> log) : IHibernator
{
    public async Task<bool> HibernateAsync(CancellationToken ct)
    {
        var r = await runner.RunAsync("shutdown.exe", ["/h"], TimeSpan.FromSeconds(30), ct);
        log($"shutdown /h -> exit {r.ExitCode}");
        return r.ExitCode == 0 && !r.TimedOut;
    }
}

/// <summary>Dry run: logs what it would do and never hibernates.</summary>
internal sealed class DryRunHibernator(Action<string> log) : IHibernator
{
    public Task<bool> HibernateAsync(CancellationToken ct)
    {
        log("dry run, would run: shutdown.exe /h");
        return Task.FromResult(false);
    }
}
