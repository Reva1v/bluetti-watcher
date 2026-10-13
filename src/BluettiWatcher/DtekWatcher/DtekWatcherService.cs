using System.Text.Json;
using BluettiWatcher.Decision;

namespace BluettiWatcher.DtekWatcher;

/// <summary>
/// The outage schedule beside the station loop (design.md §11 DTEK module): reads the dataset and, best effort, the distributor's
/// site on its own cadence, on its own task, never under the decision gate; keeps the schedule in use on disk; finds the
/// house's queue from the real cuts. It informs only. With <c>dtekWatcher.enabled</c> false it makes no source and no
/// request. The host reaches it only through <see cref="DtekWatcherGuard"/>.
/// </summary>
internal sealed class DtekWatcherService(StationConfig config, Func<IDtekSource> makeSource, IDtekStore store, TimeProvider time, Action<string> log) : IDtekWatcher
{
    public static readonly TimeSpan DatasetEvery = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan SiteEvery = TimeSpan.FromHours(1);
    public static readonly TimeSpan SiteEveryInCut = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan SiteEveryBlocked = TimeSpan.FromHours(6);
    /// <summary>Challenge answers in a row after which the site is tried only every 6 hours.</summary>
    public const int BlockedBackOff = 24;
    /// <summary>A cut's end is known only when the readings around it are at most this far apart.</summary>
    public static readonly TimeSpan EdgeBlind = TimeSpan.FromMinutes(5);
    /// <summary>A collection time further ahead of the clock than this is taken as now.</summary>
    public static readonly TimeSpan ClockSlack = TimeSpan.FromMinutes(10);
    /// <summary>One changed event per queue and day in this time; one failed/ok pair per source in an hour.</summary>
    public static readonly TimeSpan ChangedEvery = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan StreakEvery = TimeSpan.FromHours(1);
    private static readonly TimeSpan Wake = TimeSpan.FromSeconds(60);
    private const string MetaKey = "dtekWatcher";
    private const string LateKey = "dtekWatcherLateness";

    private readonly Lock _lock = new();
    private readonly TimeZoneInfo _tz = DtekTime.Kyiv;
    private readonly Dictionary<string, DateTimeOffset> _changedAt = [];
    private readonly Dictionary<string, (DateTimeOffset FailedAt, bool OwesOk)> _streaks = [];
    private DtekSnapshot _snap = Load(config.DtekWatcher, store, log);
    private IDtekSource? _source;
    private bool _cut;
    private bool _unsaved;
    private bool _lateUnsaved;
    private DateTimeOffset? _lastOnAt;
    private DateTimeOffset? _lastOffAt;

    private DtekConfig Cfg => config.DtekWatcher;

    /// <summary>The module is on and its config can run.</summary>
    public bool Enabled => Cfg.Enabled && Cfg.Problem() is null;

    public void Dispose() => _source?.Dispose();

    /// <summary>
    /// The saved state; anything wrong with the row (unreadable store, damaged or wrong-typed JSON) starts fresh. What was
    /// counted against another <c>dtekWatcher.group</c> than the one set now is dropped: an edited value is a fresh hint.
    /// </summary>
    private static DtekSnapshot Load(DtekConfig cfg, IDtekStore store, Action<string> log)
    {
        try
        {
            var snap = store.GetMeta(MetaKey) is { } json ? DtekSnapshot.Normalize(JsonSerializer.Deserialize(json, StationJson.Default.DtekSnapshot)) : new DtekSnapshot();
            snap.Hint = DtekHint.For(snap.Hint, DtekOutlook.Manual(cfg));
            snap.Late = LoadLate(store, log);
            return snap;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log($"dtek watcher: saved state unreadable, starting fresh: {e.GetType().Name}");
            return new DtekSnapshot { Late = LoadLate(store, log) };
        }
    }

    /// <summary>The lateness list, on its own key: damaged, wrong-typed or unreadable is an empty list, the rest of the state kept.</summary>
    private static List<DtekLate> LoadLate(IDtekStore store, Action<string> log)
    {
        try
        {
            return store.GetMeta(LateKey) is { } json ? DtekLate.Sound(JsonSerializer.Deserialize(json, StationJson.Default.ListDtekLate)) : [];
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log($"dtek watcher: saved lateness unreadable, starting it empty: {e.GetType().Name}");
            return [];
        }
    }

    /// <summary>The state to disk. A store that fails (a locked database) keeps the state in memory; the next change or look saves it.</summary>
    private void Save()
    {
        try
        {
            // The open cut is cleared before its lateness is written: a crash between the two loses the entry, never doubles it.
            store.SetMeta(MetaKey, JsonSerializer.Serialize(_snap, StationJson.Default.DtekSnapshot));
            if (_lateUnsaved)
            {
                store.SetMeta(LateKey, JsonSerializer.Serialize(_snap.Late, StationJson.Default.ListDtekLate));
                _lateUnsaved = false;
            }
            _unsaved = false;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            if (!_unsaved) log($"dtek watcher: state not saved, kept in memory: {e.GetType().Name}: {e.Message}");
            _unsaved = true;
        }
    }

    /// <summary>An event for the page's list; a store that fails loses the event, never the module.</summary>
    private void Tell(DateTimeOffset now, string kind, string detail)
    {
        try { store.AddEvent(now, kind, detail); }
        catch (Exception e) when (e is not OperationCanceledException) { log($"dtek watcher: event {kind} not stored: {e.GetType().Name}"); }
    }

    private static bool Due(DateTimeOffset? last, DateTimeOffset now, TimeSpan every) => last is not { } at || now - at >= every || now < at;

    /// <summary>The loop: one look a minute, a read only when one is due. Ends with the token; returns at once when off.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        if (!Cfg.Enabled) return;
        if (Cfg.Problem() is { } problem) { log($"dtek watcher: off - {problem}"); return; }
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try { await TickAsync(ct); }
                catch (Exception e) when (e is not OperationCanceledException) { log($"dtek watcher: read failed: {e.GetType().Name}: {e.Message}"); }
                await Task.Delay(Wake, time, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // stopping with the service
        }
    }

    /// <summary>
    /// One look: the dataset when 30 minutes passed; the site when its hour passed (15 minutes in a cut, 6 hours after 24
    /// challenge answers in a row). Every outcome of a read, an exception nobody expected included, records the attempt,
    /// so a failed read comes again on the same cadence, never sooner. true = a read ran.
    /// </summary>
    public async Task<bool> TickAsync(CancellationToken ct)
    {
        if (!Enabled) return false;
        var now = time.GetUtcNow();
        bool dataset, site;
        string? etag;
        lock (_lock)
        {
            if (_unsaved) Save();
            dataset = Due(_snap.DatasetTriedAt, now, DatasetEvery);
            site = Cfg.Site && Due(_snap.SiteTriedAt, now, _snap.SiteBlocked >= BlockedBackOff ? SiteEveryBlocked : _cut ? SiteEveryInCut : SiteEvery);
            // The validator stands only for a schedule still held: without one the whole answer is asked for.
            etag = _snap.Schedule is null ? null : _snap.DatasetETag;
        }
        if (!dataset && !site) return false;
        if (dataset)
        {
            DtekDatasetResult answer;
            try { answer = await (_source ??= makeSource()).DatasetAsync(etag, ct); }
            catch (Exception e) when (!ct.IsCancellationRequested) { answer = new(DtekStatus.Refused, null, null, false, Unexpected(e)); }
            lock (_lock) Apply(answer, time.GetUtcNow());
        }
        if (site)
        {
            DtekFetch answer;
            try { answer = await (_source ??= makeSource()).SiteAsync(Cfg.HasAddress, ct); }
            catch (Exception e) when (!ct.IsCancellationRequested) { answer = new(new(DtekStatus.Refused, null, null, Unexpected(e)), null); }
            lock (_lock) Apply(answer, time.GetUtcNow());
        }
        return true;
    }

    private static string Unexpected(Exception e) => DtekParser.Clean($"{e.GetType().Name}: {e.Message}", DtekSnapshot.MaxDetail);

    private static string Class(DtekStatus s) => s switch
    {
        DtekStatus.Network => "network",
        DtekStatus.Blocked => "blocked",
        DtekStatus.Format => "format",
        DtekStatus.Refused => "failed",
        _ => "",
    };

    /// <summary>One event per failure streak of a source and one on its recovery; at most one such pair per source in an hour.</summary>
    private void Streak(string source, string before, string after, string detail, DateTimeOffset now)
    {
        _streaks.TryGetValue(source, out var last);
        if (after.Length > 0 && before.Length == 0)
        {
            log($"dtek watcher: {source} read failed ({after}): {detail}");
            if (last.FailedAt != default && !Due(last.FailedAt, now, StreakEvery)) return;
            _streaks[source] = (now, true);
            Tell(now, "dtek-watcher-failed", $"{source}: {after}: {detail}");
        }
        else if (after.Length == 0 && before.Length > 0)
        {
            log($"dtek watcher: {source} is read again");
            if (!last.OwesOk && last.FailedAt != default) return;
            _streaks[source] = (last.FailedAt, false);
            Tell(now, "dtek-watcher-ok", $"{source}: read again");
        }
    }

    private static bool SameCells(DtekSchedule a, DtekSchedule b) =>
        a.Days.Count == b.Days.Count && a.Days.Zip(b.Days).All(p => p.First.Start == p.Second.Start && p.First.Queues.Count == p.Second.Queues.Count
            && p.First.Queues.All(q => p.Second.Queues.TryGetValue(q.Key, out var cells) && cells == q.Value));

    /// <summary>
    /// The schedule in use is the one with the newest update time of the distributor among what the sources returned. The
    /// same update collected again only moves the collection time. A candidate whose update time cannot be read, against
    /// a held one that can, is taken only when its content differs and it was collected later (the view's note says so).
    /// A change of a shown queue's planned windows is an event, at most one per queue and day in 10 minutes.
    /// </summary>
    private void TakeIn(DtekSchedule candidate, DateTimeOffset now)
    {
        var current = _snap.Schedule;
        if (current is not null)
        {
            if (candidate.UpdateAt is null && current.UpdateAt is not null)
            {
                if (candidate.CollectedAt <= current.CollectedAt || SameCells(current, candidate)) return;
            }
            else
            {
                var mine = current.UpdateAt ?? DateTimeOffset.MinValue;
                var theirs = candidate.UpdateAt ?? DateTimeOffset.MinValue;
                if (theirs < mine) return;
                if (theirs == mine && current.Update == candidate.Update && SameCells(current, candidate))
                {
                    if (candidate.CollectedAt > current.CollectedAt) { current.CollectedAt = candidate.CollectedAt; current.Source = candidate.Source; }
                    return;
                }
                if (theirs == mine && candidate.CollectedAt <= current.CollectedAt) return;   // nothing newer by either time
            }
        }
        var all = DtekOutlook.Moved(current, candidate, DtekOutlook.Shown(Cfg, _snap), _tz);
        var moved = all.Where(m => Due(_changedAt.GetValueOrDefault(m.Key, DateTimeOffset.MinValue), now, ChangedEvery)).ToList();
        _snap.Schedule = candidate;   // the schedule in use only: no history
        // Every moved plan during a cut counts, the ones the event's 10 minutes hold back too.
        if (all.Count > 0 && _snap.OpenCut is { PlanChanges: < DtekOpenCut.MaxPlanChanges } open) open.PlanChanges++;
        if (moved.Count == 0) return;
        if (_changedAt.Count > 256) _changedAt.Clear();
        foreach (var m in moved) _changedAt[m.Key] = now;
        string text = string.Join("; ", moved.Select(m => m.Text));
        Tell(now, "dtek-watcher-changed", text);
        log($"dtek watcher: the plan moved: {text}");
    }

    /// <summary>A schedule a source returned, bounded against the clock; null with the reason when it cannot be used.</summary>
    private DtekSchedule? Accept(DtekSchedule schedule, DateTimeOffset now, out string problem)
    {
        if (schedule.CollectedAt > now + ClockSlack) schedule.CollectedAt = now;   // a source's clock ahead of ours never makes a schedule fresh for long
        problem = DtekTime.Problem(schedule, now, _tz) ?? "";
        return problem.Length == 0 ? schedule : null;
    }

    private void Apply(DtekDatasetResult answer, DateTimeOffset now)
    {
        string before = _snap.DatasetError;
        _snap.DatasetTriedAt = now;
        var status = answer.Status;
        string detail = answer.Detail;
        if (answer is { Status: DtekStatus.Ok, NotModified: false, Schedule: { } schedule })
        {
            if (Accept(schedule, now, out string problem) is { } sound)
            {
                _snap.DatasetETag = DtekParser.Clean(answer.ETag, 200) is { Length: > 0 } tag ? tag : null;   // only for a body taken in
                TakeIn(sound, now);
            }
            else (status, detail) = (DtekStatus.Format, problem);
        }
        // Not modified: collected by the dataset no later than before, so the collection time stays the dataset's own.
        _snap.DatasetError = Class(status);
        _snap.DatasetDetail = status == DtekStatus.Ok ? "" : DtekParser.Clean(detail, DtekSnapshot.MaxDetail);
        Streak("dataset", before, _snap.DatasetError, _snap.DatasetDetail, now);
        Save();
    }

    private void Apply(DtekFetch answer, DateTimeOffset now)
    {
        string before = _snap.SiteError;
        _snap.SiteTriedAt = now;
        _snap.SiteBlocked = answer.Page.Status == DtekStatus.Blocked ? _snap.SiteBlocked + 1 : 0;
        string error = Class(answer.Page.Status), detail = answer.Page.Detail;
        if (answer.Page is { Status: DtekStatus.Ok, Schedule: { } schedule })
        {
            schedule.CollectedAt = now;
            if (Accept(schedule, now, out string problem) is { } sound)
            {
                TakeIn(sound, now);
                if (answer.House is { Status: DtekStatus.Ok, House: { } house })
                {
                    house.FetchedAt = now;
                    _snap.House = house;
                    _snap.Match = DtekMatcher.Keep(_snap.Match, DtekOutlook.HouseQueues(Cfg, _snap).Queues);
                }
                else if (answer.House is { } failed) (error, detail) = (Class(failed.Status), failed.Detail);
            }
            else (error, detail) = ("format", problem);
        }
        _snap.SiteError = error;
        _snap.SiteDetail = error.Length > 0 ? DtekParser.Clean(detail, DtekSnapshot.MaxDetail) : "";
        Streak("site", before, error, _snap.SiteDetail, now);
        Save();
    }

    /// <summary>The house's queues of a schedule, alone: what a cut that begins now will be compared with. A set keeps its members.</summary>
    private static DtekSchedule? Held(DtekSchedule? s, IReadOnlyList<string> queues)
    {
        if (s is null || queues.Count == 0) return null;
        var keep = queues.SelectMany(DtekTime.Members).ToHashSet(StringComparer.Ordinal);
        return new DtekSchedule
        {
            Update = s.Update, UpdateAt = s.UpdateAt, CollectedAt = s.CollectedAt, Source = s.Source,
            Days = [.. s.Days.Select(d => new DtekDay { Start = d.Start, Queues = d.Queues.Where(q => keep.Contains(q.Key)).ToDictionary(q => q.Key, q => q.Value) })],
        };
    }

    /// <summary>
    /// After a poll: follows the cut the decision recorded, and when mains is confirmed back hands the finished cut to the
    /// matcher, against the schedule held when the cut began. A plan that changed during the cut tells nothing: such a
    /// cut neither counts nor contradicts. Reads the state, changes nothing in it.
    /// </summary>
    public void AfterPoll(StationState state)
    {
        if (!Enabled || state is not { } s) return;
        lock (_lock)
        {
            if (s.LastAt is { } at)
            {
                if (s.OnSince is not null) _lastOnAt = at;
                if (s.OffSince is not null) _lastOffAt = at;
            }
            _cut = s.Cut;
            var queues = DtekOutlook.HouseQueues(Cfg, _snap).Queues;
            var compared = DtekOutlook.Compared(Cfg, _snap);
            if (s.Cut && _snap.OpenCut is null)
            {
                var start = s.OffSince ?? s.CutSince ?? time.GetUtcNow();
                bool known = _lastOnAt is { } on && start >= on && start - on <= EdgeBlind;
                _snap.OpenCut = new DtekOpenCut { Start = start, StartKnown = known, Held = Held(_snap.Schedule, compared) };
                Save();
            }
            else if (_snap.OpenCut is { } open && !s.Cut && !s.CutBeforeGap)
            {
                // Mains is confirmed back. A cut whose end fell in a gap of readings (a hibernated PC) has no known end.
                bool edges = open.StartKnown && s.MainsBackAt is { } back0 && back0 > open.Start && _lastOffAt is { } off && back0 >= off && back0 - off <= EdgeBlind;
                // Lateness: against the plan as it stands now, edits during the cut included (design.md §11 rule 25).
                // Only with one queue in play (fixed, manual or the only one): several shown queues say nothing of one's lateness.
                // A manual set of queues is one in play: the house loses power for their union, recorded as such (Union).
                if (edges && DtekOutlook.Shown(Cfg, _snap) is [_] one && DtekOutlook.Lateness(new DtekCut(open.Start, s.MainsBackAt!.Value), _snap.Schedule, open.Held, one, _tz) is { } late
                    && !_snap.Late.Exists(e => e.Start == late.Start))
                {
                    _snap.Late.Add(late);
                    if (_snap.Late.Count > DtekLate.Kept) _snap.Late.RemoveRange(0, _snap.Late.Count - DtekLate.Kept);
                    _lateUnsaved = true;
                }
                if (edges && open.Held is { } held && _snap.Schedule is { } current && DtekOutlook.SameWindows(held, current, compared, _tz))
                {
                    var seen = new DtekCut(open.Start, s.MainsBackAt!.Value);
                    var match = DtekMatcher.Apply(_snap.Match, seen, held, queues, _tz);
                    if (!ReferenceEquals(match, _snap.Match))
                    {
                        if (match.Fixed != _snap.Match.Fixed) log($"dtek watcher: queue {(match.Fixed is { } f ? $"fixed: {f}" : $"open again (was {_snap.Match.Fixed})")}");
                        _snap.Match = match;
                    }
                    if (DtekOutlook.Manual(Cfg) is { } manual) Hint(manual, seen, held, queues);
                }
                _snap.OpenCut = null;
                Save();
            }
        }
    }

    /// <summary>
    /// The manual queue against a finished cut (design.md §11 rule 20). Two cuts in a row for one other queue refute it: one event,
    /// and the queue followed from then on is the detected one, as if the search had fixed it.
    /// </summary>
    private void Hint(string manual, DtekCut seen, DtekSchedule held, IReadOnlyList<string> queues)
    {
        bool stood = DtekOutlook.RefutedBy(Cfg, _snap) is null;
        _snap.Hint = DtekMatcher.Hint(_snap.Hint, manual, seen, held, queues, _tz);
        if (!stood || _snap.Hint.RefutedBy is not { } by) return;
        _snap.Match = new DtekMatch { Fixed = by, Candidate = by, Count = DtekMatcher.Needed };
        Tell(time.GetUtcNow(), "dtek-watcher-queue-changed", $"{manual} -> {by}: two cuts in a row followed {by}, not dtekWatcher.group {manual}");
        log($"dtek watcher: dtekWatcher.group {manual} refuted by two cuts in a row: {by} is followed");
    }

    /// <summary>What the page and /api/now show now.</summary>
    public DtekView View(bool cut, double? hoursLeft, double? hoursToHibernate = null)
    {
        lock (_lock) return DtekOutlook.Compute(Cfg, _snap, cut, hoursLeft, time, _tz, hoursToHibernate);
    }

    /// <summary>The queue search as saved, for tests.</summary>
    internal DtekMatch Match { get { lock (_lock) return _snap.Match; } }

    /// <summary>Challenge answers of the site in a row, for tests.</summary>
    internal int SiteBlocked { get { lock (_lock) return _snap.SiteBlocked; } }

    /// <summary>The dataset's validator as held, for tests.</summary>
    internal string? DatasetETag { get { lock (_lock) return _snap.DatasetETag; } }
}
