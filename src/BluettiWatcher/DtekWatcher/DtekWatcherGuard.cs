using BluettiWatcher.Decision;
using BluettiWatcher.Storage;

namespace BluettiWatcher.DtekWatcher;

/// <summary>What the module keeps and tells through the host's store. A seam, so a failing store is testable.</summary>
internal interface IDtekStore
{
    string? GetMeta(string key);
    void SetMeta(string key, string value);
    void AddEvent(DateTimeOffset at, string kind, string detail);
}

internal sealed class DtekStore(StationStore store) : IDtekStore
{
    public string? GetMeta(string key) => store.GetMeta(key);
    public void SetMeta(string key, string value) => store.SetMeta(key, value);
    public void AddEvent(DateTimeOffset at, string kind, string detail) => store.AddEvent(at, kind, detail);
}

/// <summary>
/// The module's boundary, total: whatever the module does - while it is built, loads its state, hears a poll, is asked
/// for its view or runs - no exception reaches the host. A fault is logged once per distinct message per hour, raises one
/// <c>dtek-watcher-failed</c> event per streak (<c>module: type</c>), and the host carries on; the view is then
/// <c>none</c> with a note.
/// </summary>
internal sealed class DtekWatcherGuard : IDtekWatcher
{
    private static readonly TimeSpan SayAgainAfter = TimeSpan.FromHours(1);
    private const int MaxMessages = 32;

    private readonly StationConfig _config;
    private readonly IDtekStore _store;
    private readonly TimeProvider _time;
    private readonly Action<string> _log;
    private readonly IDtekWatcher? _inner;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, DateTimeOffset> _said = [];
    private bool _faulted;

    public DtekWatcherGuard(StationConfig config, IDtekStore store, TimeProvider time, Action<string> log, Func<IDtekWatcher> make)
    {
        (_config, _store, _time, _log) = (config, store, time, log);
        try { _inner = make(); }
        catch (Exception e) when (e is not OperationCanceledException) { Fault("start", e); }
    }

    /// <summary>The module as the service runs it: its own HTTP source, the service's store.</summary>
    public static DtekWatcherGuard Create(StationConfig config, StationStore store, TimeProvider time, Action<string> log)
    {
        var seam = new DtekStore(store);
        return new DtekWatcherGuard(config, seam, time, log, () => new DtekWatcherService(config, () => new DtekHttpSource(config.DtekWatcher), seam, time, log));
    }

    public void Dispose()
    {
        try { _inner?.Dispose(); }
        catch (Exception e) when (e is not OperationCanceledException) { Fault("stop", e); }
    }

    private void Fault(string where, Exception e)
    {
        string type = e.GetType().Name;
        string message = $"dtek watcher: {where} failed: {type}: {DtekParser.Clean(e.Message, DtekSnapshot.MaxDetail)}";
        bool say, first;
        var now = _time.GetUtcNow();
        lock (_lock)
        {
            say = !_said.TryGetValue(message, out var at) || now - at >= SayAgainAfter || now < at;
            if (say)
            {
                if (_said.Count >= MaxMessages) _said.Clear();
                _said[message] = now;
            }
            first = !_faulted;
            _faulted = true;
        }
        // Telling about a fault may fail too (the same locked database): then it stays untold, never thrown.
        try
        {
            if (say) _log(message);
            if (first) _store.AddEvent(now, "dtek-watcher-failed", $"module: {type}");
        }
        catch (Exception again) when (again is not OperationCanceledException)
        {
            // nothing more to tell it through
        }
    }

    private void Fine()
    {
        lock (_lock) _faulted = false;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (_inner is null) return;
        try { await _inner.RunAsync(ct); }
        catch (Exception e) when (e is not OperationCanceledException) { Fault("run", e); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // stopping with the service
        }
    }

    public void AfterPoll(StationState state)
    {
        if (_inner is null) return;
        try
        {
            _inner.AfterPoll(state);
            Fine();
        }
        catch (Exception e) when (e is not OperationCanceledException) { Fault("after a poll", e); }
    }

    public DtekView View(bool cut, double? hoursLeft, double? hoursToHibernate = null)
    {
        try
        {
            if (_config.DtekWatcher is not { Enabled: true }) return DtekView.Off();
            if (_inner is null) return DtekView.Broken(new PageNote(PageCodes.DtekNotStarted, "the module did not start: see the log"));
            var view = _inner.View(cut, hoursLeft, hoursToHibernate);
            Fine();
            return view;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Fault("view", e);
            return DtekView.Broken(new PageNote(PageCodes.DtekModuleError, $"module error: {e.GetType().Name}", e.GetType().Name));
        }
    }
}
