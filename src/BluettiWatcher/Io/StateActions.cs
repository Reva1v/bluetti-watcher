using System.Globalization;
using BluettiWatcher.Storage;

namespace BluettiWatcher.Io;

/// <summary>
/// The config's <c>actions</c>: a command run when the watcher enters a state, once per entry (a repeat of the last state within one cut is not an entry). Fire and forget: the
/// decision never waits for it, and a missing, failing or hanging command changes nothing but its event. Each run is
/// windowless, killed at <c>actionTimeoutSec</c>, and logged as an event with its exit code and the first 300 characters
/// of its output.
/// </summary>
internal sealed class StateActions(StationConfig config, ICommandLineRunner runner, StationStore store, TimeProvider time, Action<string> log)
{
    public const string Eco = "eco";
    public const string Emergency = "emergency";
    public const string Stop = "stop";
    public const string HibernateWarn = "hibernate-warn";
    public const string Normal = "normal";
    public const int OutputChars = 300;

    private readonly Lock _lock = new();
    private readonly List<Task> _running = [];
    private string? _entered;

    /// <summary>A cut ended (mains back confirmed): the next cut's entries start their commands again, whatever the last one was.</summary>
    public void CutEnded()
    {
        lock (_lock) _entered = null;
    }

    /// <summary>The watcher entered <paramref name="state"/>: its command starts unless this state was the last one entered in this cut.</summary>
    public void Enter(string state)
    {
        lock (_lock)
        {
            if (_entered == state) return;
            _entered = state;
        }
        if (config.Actions is not { } actions || !actions.TryGetValue(state, out var line) || string.IsNullOrWhiteSpace(line)) return;
        var run = Task.Run(() => RunAsync(state, line));
        lock (_lock)
        {
            _running.RemoveAll(t => t.IsCompleted);
            _running.Add(run);
        }
    }

    /// <summary>Completes when every command started so far has finished (tests, shutdown).</summary>
    public Task IdleAsync()
    {
        lock (_lock) return Task.WhenAll([.. _running]);
    }

    private async Task RunAsync(string state, string line)
    {
        string detail;
        if (config.DryRun) detail = $"dry run, would run: {line}";
        else
        {
            int sec = config.ActionTimeoutSec;
            try
            {
                var r = await runner.RunLineAsync(line, TimeSpan.FromSeconds(sec), CancellationToken.None, config.PowerModeEnvironment);
                string output = string.Join('\n', r.Lines);
                if (output.Length > OutputChars) output = output[..OutputChars];
                detail = r.TimedOut
                    ? string.Create(CultureInfo.InvariantCulture, $"{line} -> killed after {sec} s: {output}")
                    : string.Create(CultureInfo.InvariantCulture, $"{line} -> exit {r.ExitCode}: {output}");
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                detail = $"{line} -> could not run: {e.GetType().Name}: {e.Message}";
            }
        }
        string text = $"{state}: {detail}";
        log($"action {text}");
        try { store.AddEvent(time.GetLocalNow(), "action", text); }
        catch (Exception e) when (e is ObjectDisposedException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            log($"action event not saved: {e.GetType().Name}: {e.Message}");
        }
    }
}
