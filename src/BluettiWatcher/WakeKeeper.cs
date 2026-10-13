using System.Globalization;
using System.Text.Json;
using BluettiWatcher.Decision;
using BluettiWatcher.Io;
using BluettiWatcher.Storage;

namespace BluettiWatcher;

/// <summary>What the wake keeps on disk (meta key <c>wake</c>): whether the task was armed, for when, and the last hibernation.</summary>
internal sealed class WakeState
{
    public bool Armed { get; set; }
    public DateTimeOffset? WakeAt { get; set; }
    public DateTimeOffset? HibernatedAt { get; set; }
}

/// <summary>The wake as /api/now gives it.</summary>
internal sealed record WakeStatus(int EveryMinutes, double FloorPercent, bool Armed, DateTimeOffset? WakeAt, bool Owed, string Error);

/// <summary>What the service does after a poll, for the wake.</summary>
internal enum WakeStep
{
    None,
    /// <summary>The timer woke the PC and this accepted reading is on battery at or under hibernateAt: hibernate at once.</summary>
    LowOnBattery,
    /// <summary>The timer woke the PC and no reading was accepted for <c>wakeReadSec</c>: hibernate without one.</summary>
    NoReading,
}

/// <summary>
/// Wakes the PC the service hibernated during a cut (design.md §8 Wake during a cut). Just before a hibernation the wake task is armed for
/// <c>wakeEveryMinutes</c> later, unless the battery is at or under <c>wakeFloorPercent</c>; a failure to arm is an event
/// and never holds the hibernation back. Any accepted reading on mains turns the task off; so does the first one after
/// a start (a crash may have left it on), and the first after a failed arm. A failed turn-off is retried with the action
/// backoff and shown until it succeeds. After the timer woke the PC (it resumed within <see cref="Early"/> before and
/// <see cref="Late"/> after the wake time) a hibernation is owed until mains is seen: the first accepted reading on
/// battery at or under hibernateAt asks for it at once, and so does <c>wakeReadSec</c> without an accepted reading,
/// counted from the last one. A wake by hand owes nothing: the decision alone decides.
/// </summary>
internal sealed class WakeKeeper(StationConfig config, IWakeTask task, StationStore store, Action<string> log, TimeZoneInfo? zone = null)
{
    private const string MetaKey = "wake";

    /// <summary>A time people read, in the PC's local zone unless a test gives one.</summary>
    private string Clock(DateTimeOffset at) => FlagFileReader.Clock(at, zone);
    private const string DisarmKey = "wake-disarm";
    /// <summary>A poll this long after the hibernation means the PC resumed (the decision's own rule, design.md §6).</summary>
    public static readonly TimeSpan Resumed = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan Early = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan Late = TimeSpan.FromMinutes(10);

    private readonly Backoff _backoff = new();
    private readonly WakeState _s = Load(store, log);
    private bool _owed = true;
    private string _error = "";
    private DateTimeOffset? _resumedAt;
    private DateTimeOffset? _lastReadAt;
    private bool _afterTimer;

    /// <summary><c>wakeEveryMinutes</c>: 0 = off; anything else between 10 minutes and a day.</summary>
    public int Every => config.WakeEveryMinutes <= 0 ? 0 : Math.Clamp(config.WakeEveryMinutes, 10, 24 * 60);

    /// <summary>
    /// <c>wakeFloorPercent</c> inside 0..hibernateAt (0 = no floor); not set, hibernateAt - 5 (at least 0). At or under it no
    /// wake is armed.
    /// </summary>
    public double Floor
    {
        get
        {
            double top = Math.Max(0, config.HibernateAt);
            double floor = config.WakeFloorPercent ?? top - 5;
            return double.IsFinite(floor) ? Math.Clamp(floor, 0, top) : 0;
        }
    }

    /// <summary><c>wakeReadSec</c>: 0 or less is the default 300; anything else between a minute and an hour.</summary>
    public TimeSpan ReadWithin => TimeSpan.FromSeconds(config.WakeReadSec <= 0 ? 300 : Math.Clamp(config.WakeReadSec, 60, 3600));

    public WakeStatus Status => new(Every, Floor, _s.Armed, _s.Armed ? _s.WakeAt : null, _owed, _error);

    private static WakeState Load(StationStore store, Action<string> log)
    {
        try { return store.GetMeta(MetaKey) is { } json ? JsonSerializer.Deserialize(json, StationJson.Default.WakeState) ?? new WakeState() : new WakeState(); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log($"wake: saved state unreadable, starting fresh: {e.GetType().Name}");
            return new WakeState();
        }
    }

    private void Save()
    {
        try { store.SetMeta(MetaKey, JsonSerializer.Serialize(_s, StationJson.Default.WakeState)); }
        catch (Exception e) when (e is not OperationCanceledException) { log($"wake: state not saved: {e.GetType().Name}: {e.Message}"); }
    }

    private void Tell(DateTimeOffset now, string kind, string detail)
    {
        log($"{kind}: {detail}");
        try { store.AddEvent(now, kind, detail); }
        catch (Exception e) when (e is not OperationCanceledException) { log($"wake: event {kind} not stored: {e.GetType().Name}"); }
    }

    private static async Task<WakeTaskResult> Safe(Func<Task<WakeTaskResult>> run)
    {
        try { return await run(); }
        catch (Exception e) when (e is not OperationCanceledException) { return new WakeTaskResult(false, $"{e.GetType().Name}: {e.Message}"); }
    }

    private static string Pct(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>
    /// Just before a hibernation: the wake armed for <see cref="Every"/> minutes later, unless <paramref name="soc"/> (the
    /// battery the hibernation acts on; null = unknown) is at or under <see cref="Floor"/>. Never throws; a failure is an
    /// event, and a wake that may have been armed all the same is turned off at the next reading on mains.
    /// </summary>
    public async Task BeforeHibernateAsync(DateTimeOffset now, double? soc, CancellationToken ct)
    {
        if (Every == 0) return;
        if (Floor > 0 && soc is { } s && s <= Floor)
        {
            // An earlier wake may still be ahead (the PC was woken by hand before it): it goes, or is owed.
            if (_s.Armed && !(await Safe(() => task.DisarmAsync(ct, beforeHibernate: true))).Ok) _owed = true;   // 15 s at most, as the arm
            (_s.Armed, _s.WakeAt) = (false, null);
            Tell(now, "wake-not-armed", $"wake not armed: battery {Pct(s)} % at or under the floor {Pct(Floor)} %");
            Save();
            return;
        }
        var at = now.AddMinutes(Every);
        var r = await Safe(() => task.ArmAsync(at, ct));
        if (r.Ok)
        {
            (_s.Armed, _s.WakeAt) = (true, at);
            Tell(now, "wake-armed", $"the PC wakes at {Clock(at)} to read the station");
        }
        else
        {
            _owed = true;   // the arm may have landed before it failed or timed out
            Tell(now, "wake-arm-failed", $"{r.Detail} - the PC hibernates without a wake");
        }
        Save();
    }

    /// <summary>The hibernation command succeeded: the next poll more than 2 minutes later is a resume.</summary>
    public void Hibernated(DateTimeOffset now)
    {
        (_s.HibernatedAt, _resumedAt, _lastReadAt, _afterTimer) = (now, null, null, false);
        Save();
    }

    /// <summary>
    /// After each poll, with its reading and whether it was accepted. Turns the wake off on an accepted reading on mains
    /// while it is (or may be) on; says when a timer wake owes a hibernation (<see cref="WakeStep"/>).
    /// </summary>
    public async Task<WakeStep> AfterPollAsync(DateTimeOffset now, Reading? reading, bool accepted, CancellationToken ct)
    {
        if (_s.HibernatedAt is { } h && _resumedAt is null && now - h > Resumed)
        {
            _resumedAt = now;
            _lastReadAt = null;
            _afterTimer = _s.Armed && _s.WakeAt is { } w && now >= w - Early && now <= w + Late;
        }
        var grid = accepted && reading is not null ? StationDecision.GridOf(reading) : Grid.Unknown;
        if (accepted) _lastReadAt = now;
        if (grid == Grid.On)
        {
            _afterTimer = false;
            if (_s.Armed || _owed) await DisarmAsync(now, ct);
            return WakeStep.None;
        }
        if (!_afterTimer) return WakeStep.None;
        if (grid == Grid.Off && reading!.Soc <= config.HibernateAt) return WakeStep.LowOnBattery;
        return now - (_lastReadAt ?? _resumedAt!.Value) >= ReadWithin ? WakeStep.NoReading : WakeStep.None;
    }

    private async Task DisarmAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (!_backoff.Open(DisarmKey, now)) return;
        var r = await Safe(() => task.DisarmAsync(ct));
        if (r.Ok)
        {
            _backoff.Succeeded(DisarmKey);
            bool was = _s.Armed;
            (_s.Armed, _s.WakeAt, _owed, _error) = (false, null, false, "");
            Save();
            if (was) Tell(now, "wake-disarmed", $"mains is on: {r.Detail}");
            return;
        }
        _error = r.Detail;
        var next = _backoff.Failed(DisarmKey, now);
        Tell(now, "wake-disarm-failed", $"{r.Detail} - next try {Clock(next)}");
    }
}
