using BluettiWatcher.Decision;

namespace BluettiWatcher.DtekWatcher;

/// <summary>
/// All the host knows of the DTEK Watcher module: start it, tell it what a poll saw, ask what to show. Nothing comes back
/// into the station's decision; taking the module out means dropping these three calls.
/// </summary>
internal interface IDtekWatcher : IDisposable
{
    /// <summary>Runs until the token stops it; returns at once when the module is off.</summary>
    Task RunAsync(CancellationToken ct);

    /// <summary>The hook for "a cut began" and "mains is back": the state as the poll left it, read and never changed.</summary>
    void AfterPoll(StationState state);

    /// <summary>The module's object for /api/now. <paramref name="hoursToHibernate"/>: the battery estimate down to the hibernate level, null = unknown.</summary>
    DtekView View(bool cut, double? hoursLeft, double? hoursToHibernate = null);
}