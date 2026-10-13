using System.Globalization;

namespace BluettiWatcher.Io;

internal sealed record WakeTaskResult(bool Ok, string Detail);

/// <summary>
/// The scheduled task that wakes the hibernated PC (WakeToRun, a no-op action, the user's account, no elevation).
/// install.ps1 registers it disabled; the service only sets its time and turns it on or off.
/// </summary>
internal interface IWakeTask
{
    /// <summary>Sets the task's one trigger to <paramref name="at"/> and enables it. Never throws for a failed command.</summary>
    Task<WakeTaskResult> ArmAsync(DateTimeOffset at, CancellationToken ct);

    /// <summary>
    /// Disables the task; a task that is not registered is nothing to turn off (Ok). Never throws for a failed command.
    /// <paramref name="beforeHibernate"/>: it holds a hibernation, so it gets the arm's short timeout.
    /// </summary>
    Task<WakeTaskResult> DisarmAsync(CancellationToken ct, bool beforeHibernate = false);
}

/// <summary>
/// The task through pwsh's ScheduledTasks module, windowless: <c>Set-ScheduledTask</c> (its trigger time, enabled) and
/// <c>Disable-ScheduledTask</c>. The task's security, which install.ps1 sets, lets the user change it without elevation.
/// A task that does not wake the computer is refused, never armed. Dry run: logged, nothing runs.
/// </summary>
internal sealed class ScheduledWakeTask(StationConfig config, IProcessRunner runner, Action<string> log) : IWakeTask
{
    private static string Quote(string s) => "'" + s.Replace("'", "''", StringComparison.Ordinal) + "'";

    /// <summary>The arm script: the time is an ISO 8601 instant with its offset, the task name quoted; nothing else goes in.</summary>
    public string ArmScript(DateTimeOffset at) => string.Join("; ",
        "$ErrorActionPreference = 'Stop'",
        $"$t = Get-ScheduledTask -TaskPath '\\' -TaskName {Quote(config.WakeTaskName)}",
        "if (-not $t.Settings.WakeToRun) { throw 'the task does not wake the computer: run install.ps1' }",
        $"$t.Triggers[0].StartBoundary = {Quote(at.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture))}",
        "$t.Triggers[0].Enabled = $true",
        "$t.Settings.Enabled = $true",
        "$null = Set-ScheduledTask -InputObject $t",
        $"$b = Get-ScheduledTask -TaskPath '\\' -TaskName {Quote(config.WakeTaskName)}",
        "\"armed $($b.State) $($b.Triggers[0].StartBoundary)\"");

    public string DisarmScript() => string.Join("; ",
        "$ErrorActionPreference = 'Stop'",
        $"$t = Get-ScheduledTask -TaskPath '\\' | Where-Object TaskName -eq {Quote(config.WakeTaskName)}",
        "if (-not $t) { 'not registered: nothing to turn off'; exit 0 }",
        "if (\"$($t.State)\" -ne 'Disabled') { $null = Disable-ScheduledTask -InputObject $t }",
        $"\"disarmed $((Get-ScheduledTask -TaskPath '\\' -TaskName {Quote(config.WakeTaskName)}).State)\"");

    /// <summary>The arm runs right before a hibernation and holds it for at most this long (pwsh and its module start in a few seconds).</summary>
    public static readonly TimeSpan ArmTimeout = TimeSpan.FromSeconds(15);

    public Task<WakeTaskResult> ArmAsync(DateTimeOffset at, CancellationToken ct) =>
        RunAsync($"arm {at:O}", ArmScript(at), ArmTimeout < TimeSpan.FromSeconds(config.PowerModeTimeoutSec) ? ArmTimeout : TimeSpan.FromSeconds(config.PowerModeTimeoutSec), ct);

    public Task<WakeTaskResult> DisarmAsync(CancellationToken ct, bool beforeHibernate = false) =>
        RunAsync("disarm", DisarmScript(), beforeHibernate && ArmTimeout < TimeSpan.FromSeconds(config.PowerModeTimeoutSec) ? ArmTimeout : TimeSpan.FromSeconds(config.PowerModeTimeoutSec), ct);

    private async Task<WakeTaskResult> RunAsync(string what, string script, TimeSpan timeout, CancellationToken ct)
    {
        if (config.DryRun)
        {
            log($"dry run, would {what} the wake task {config.WakeTaskName}");
            return new WakeTaskResult(true, "dry run");
        }
        var r = await runner.RunAsync(config.PwshPath, ["-NoProfile", "-NonInteractive", "-Command", script], timeout, ct);
        string last = r.Lines.Count > 0 ? r.Lines[^1] : r.TimedOut ? "timed out" : $"exit {r.ExitCode}";
        log($"wake task {what} -> exit {r.ExitCode}: {last}");
        return new WakeTaskResult(r.ExitCode == 0 && !r.TimedOut, last);
    }
}
