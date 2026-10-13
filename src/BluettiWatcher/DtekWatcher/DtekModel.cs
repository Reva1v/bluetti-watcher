namespace BluettiWatcher.DtekWatcher;

/// <summary>How a read ended. A failure is a value, never an exception. Refused = a request or an answer the module will not take.</summary>
internal enum DtekStatus { Ok, Network, Blocked, Format, Refused }

/// <summary>What a half hour of a queue is, as the distributor plans it.</summary>
internal enum DtekSlot { On, Off, Possible, Unknown }

/// <summary>
/// One day of a schedule. Each queue is 24 characters, one per hour cell (hour N = (N-1):00 to N:00):
/// <c>1</c> yes, <c>0</c> no, <c>f</c> first, <c>s</c> second, <c>m</c> maybe, <c>a</c> mfirst, <c>b</c> msecond,
/// <c>?</c> anything else (unknown, never guessed).
/// </summary>
internal sealed class DtekDay
{
    /// <summary>The day's start, Unix seconds (Kyiv midnight).</summary>
    public long Start { get; set; }
    public Dictionary<string, string> Queues { get; set; } = [];
}

/// <summary>The schedule in use: today and tomorrow, with the distributor's own update time and when it was collected.</summary>
internal sealed class DtekSchedule
{
    /// <summary>The distributor's own update time, as written ("07.10.2026 11:43").</summary>
    public string Update { get; set; } = "";
    /// <summary><see cref="Update"/> as a time; null when it cannot be read.</summary>
    public DateTimeOffset? UpdateAt { get; set; }
    /// <summary>When it was collected: the dataset's lastUpdated, or the moment the site was read.</summary>
    public DateTimeOffset CollectedAt { get; set; }
    /// <summary>"dataset" or "site".</summary>
    public string Source { get; set; } = "";
    public List<DtekDay> Days { get; set; } = [];
}

/// <summary>The site's last answer for the house: its queues and, while it has one, the current outage as the site words it.</summary>
internal sealed class DtekHouse
{
    public string[] Queues { get; set; } = [];
    public DateTimeOffset FetchedAt { get; set; }
    public string Kind { get; set; } = "";
    public string StartText { get; set; } = "";
    public string EndText { get; set; } = "";
}

/// <summary>The queue search: a candidate with its matching cuts in a row, and the queue once fixed.</summary>
internal sealed class DtekMatch
{
    public string? Fixed { get; set; }
    public string? Candidate { get; set; }
    public int Count { get; set; }
}

/// <summary>
/// The manual queue as a hint (design.md §11 rule 20): the config value it is about, the cuts in a row that contradicted it for one
/// other queue, and the queue that refuted it. A hint about another config value is a fresh one.
/// </summary>
internal sealed class DtekHint
{
    /// <summary><c>dtekWatcher.group</c> as it was when this was counted.</summary>
    public string Group { get; set; } = "";
    public string? Candidate { get; set; }
    public int Count { get; set; }
    /// <summary>The queue two cuts in a row followed instead; null while the manual queue stands.</summary>
    public string? RefutedBy { get; set; }

    /// <summary>The saved hint when it is about <paramref name="group"/>, else a fresh one.</summary>
    public static DtekHint For(DtekHint? saved, string? group) =>
        group is not null && saved is not null && saved.Group == group ? saved : new DtekHint { Group = group ?? "" };

    /// <summary>A saved hint as one the search can take, or a fresh one: bounded names, a count that fits its candidate.</summary>
    public static DtekHint Sound(DtekHint? h)
    {
        if (h is null || h.Group is null || (h.Group.Length > 0 && !DtekSnapshot.SoundSet(h.Group))) return new DtekHint();
        if (h.RefutedBy is { } by) return by.Length is > 0 and <= DtekSnapshot.MaxName ? new DtekHint { Group = h.Group, RefutedBy = by } : new DtekHint();
        bool counted = h.Candidate is { } running ? running.Length is > 0 and <= DtekSnapshot.MaxName && h.Count is > 0 and < DtekMatcher.Needed : h.Count == 0;
        return counted ? h : new DtekHint();
    }
}

/// <summary>A cut still running: its start, and whether the reading before it was on mains (else the start is unknown).</summary>
internal sealed class DtekOpenCut
{
    public DateTimeOffset Start { get; set; }
    public bool StartKnown { get; set; }
    /// <summary>The house's queues of the schedule held when the cut began: what the finished cut is compared with.</summary>
    public DtekSchedule? Held { get; set; }
    /// <summary>Schedules taken in during this cut that moved a shown queue's planned windows.</summary>
    public int PlanChanges { get; set; }

    /// <summary>The count's bound: a value past it stays at it.</summary>
    public const int MaxPlanChanges = 999;
}

/// <summary>
/// One finished real cut against the planned window it belonged to (design.md §11 rule 25): actual minus planned, in minutes, against
/// the window as it stood when the cut ended and, when the schedule held at its start had one, the window seen then.
/// </summary>
internal sealed class DtekLate
{
    /// <summary>The cut as seen: first reading off mains, mains confirmed back.</summary>
    public DateTimeOffset Start { get; set; }
    public DateTimeOffset End { get; set; }
    public string Queue { get; set; } = "";
    /// <summary>The planned window's Kyiv date, DD.MM.YYYY.</summary>
    public string Date { get; set; } = "";
    public int StartDelta { get; set; }
    public int EndDelta { get; set; }
    public int? FirstStartDelta { get; set; }
    public int? FirstEndDelta { get; set; }
    /// <summary>The cut was read against a set of queues (<see cref="Queue"/> names them joined with +): the union of their windows.</summary>
    public bool Union { get; set; }

    /// <summary>Cuts kept; the oldest go first.</summary>
    public const int Kept = 60;
    /// <summary>A delta past two weeks is no lateness: such an entry is dropped on load.</summary>
    public const int MaxDelta = 14 * 24 * 60;

    private static bool Fits(int? d) => d is null or (>= -MaxDelta and <= MaxDelta);

    /// <summary>A saved list as one the page can take: entries out of bounds dropped, the last <see cref="Kept"/> only.</summary>
    public static List<DtekLate> Sound(List<DtekLate>? saved)
    {
        var list = (saved ?? []).Where(e => e is { Queue: { Length: > 0 } queue, Date: { Length: <= 10 } } && DtekSnapshot.SoundSet(queue) && e.End > e.Start
            && Fits(e.StartDelta) && Fits(e.EndDelta) && Fits(e.FirstStartDelta) && Fits(e.FirstEndDelta)).ToList();
        return list.Count > Kept ? list[^Kept..] : list;
    }
}

/// <summary>Everything the module keeps on disk (meta key <c>dtekWatcher</c>): no schedule history.</summary>
internal sealed class DtekSnapshot
{
    public DtekSchedule? Schedule { get; set; }
    public DtekHouse? House { get; set; }
    public DtekMatch Match { get; set; } = new();
    public DtekHint Hint { get; set; } = new();
    public DtekOpenCut? OpenCut { get; set; }
    /// <summary>The lateness of finished cuts, newest last. Kept under its own meta key, so damage there loses only this list.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public List<DtekLate> Late { get; set; } = [];

    public DateTimeOffset? DatasetTriedAt { get; set; }
    /// <summary>"" or the reason class of the dataset's running failure: network, format, failed.</summary>
    public string DatasetError { get; set; } = "";
    public string DatasetDetail { get; set; } = "";
    /// <summary>The validator of the dataset answer last taken in, sent back to save traffic.</summary>
    public string? DatasetETag { get; set; }

    public DateTimeOffset? SiteTriedAt { get; set; }
    /// <summary>"" or the reason class of the site's running failure: network, blocked, format, failed.</summary>
    public string SiteError { get; set; } = "";
    public string SiteDetail { get; set; } = "";
    /// <summary>Challenge answers in a row; from 24 on the site is tried every 6 hours.</summary>
    public int SiteBlocked { get; set; }

    public static DtekSnapshot Normalize(DtekSnapshot? s)
    {
        if (s is null) return new DtekSnapshot();
        s.Match ??= new DtekMatch();
        s.Hint = DtekHint.Sound(s.Hint);
        s.DatasetError ??= "";
        s.DatasetDetail ??= "";
        s.SiteError ??= "";
        s.SiteDetail ??= "";
        if (s.SiteBlocked < 0) s.SiteBlocked = 0;
        s.DatasetDetail = DtekParser.Clean(s.DatasetDetail, MaxDetail);
        s.SiteDetail = DtekParser.Clean(s.SiteDetail, MaxDetail);
        s.Schedule = Sound(s.Schedule);
        if (s.OpenCut is { } cut)
        {
            cut.Held = Sound(cut.Held);
            cut.PlanChanges = Math.Clamp(cut.PlanChanges, 0, DtekOpenCut.MaxPlanChanges);
        }
        if (s.House is { } h)
        {
            h.Queues = [.. (h.Queues ?? []).Where(q => q is { Length: > 0 and <= MaxName }).Take(DtekTime.MaxQueues)];
            h.Kind = DtekParser.Clean(h.Kind, DtekParser.MaxSiteText);
            h.StartText = DtekParser.Clean(h.StartText, DtekParser.MaxSiteText);
            h.EndText = DtekParser.Clean(h.EndText, DtekParser.MaxSiteText);
        }
        if (s.Match.Fixed is { Length: > MaxName }) s.Match = new DtekMatch();
        return s;
    }

    public const int MaxDetail = 300;
    internal const int MaxName = 16;
    /// <summary>A queue or a set of them as one name (dtekWatcher.group, a lateness entry): at most <see cref="DtekConfig.MaxSet"/> names of at most <see cref="MaxName"/> characters.</summary>
    internal static bool SoundSet(string name) => name.Length <= DtekConfig.MaxSet * (MaxName + 1) && DtekTime.Members(name) is { Count: > 0 and <= DtekConfig.MaxSet } m && m.All(q => q.Length is > 0 and <= MaxName);

    /// <summary>A saved schedule as one the arithmetic can take, or null: day keys in range, 24 cells a queue, bounded counts.</summary>
    private static DtekSchedule? Sound(DtekSchedule? sc)
    {
        if (sc is null) return null;
        sc.Update = DtekParser.Clean(sc.Update, DtekParser.MaxUpdateText);
        sc.Source = DtekParser.Clean(sc.Source, MaxName);
        bool sound = sc.Days is { Count: > 0 and <= DtekTime.MaxDays } && sc.Days.TrueForAll(d => d is { Start: >= 0 and <= DtekTime.MaxDayStart, Queues: { Count: <= DtekTime.MaxQueues } q }
            && q.All(kv => kv.Key is { Length: > 0 and <= MaxName } && kv.Value is { Length: 24 }));
        if (!sound) return null;
        sc.Days.Sort((a, b) => a.Start.CompareTo(b.Start));
        return sc;
    }
}

internal sealed record DtekPageResult(DtekStatus Status, DtekSchedule? Schedule, string? Csrf, string Detail);

internal sealed record DtekHouseResult(DtekStatus Status, DtekHouse? House, string Detail);

/// <summary>One read of the site: the page always, the house when it was asked for and the page gave a session.</summary>
internal sealed record DtekFetch(DtekPageResult Page, DtekHouseResult? House);

/// <summary>One read of the dataset. NotModified = the host says the answer last taken in still stands.</summary>
internal sealed record DtekDatasetResult(DtekStatus Status, DtekSchedule? Schedule, string? ETag, bool NotModified, string Detail);

internal interface IDtekSource : IDisposable
{
    /// <summary>Reads the dataset, sending <paramref name="etag"/> back when there is one. Never throws for a failed read.</summary>
    Task<DtekDatasetResult> DatasetAsync(string? etag, CancellationToken ct);

    /// <summary>Reads the site's schedule page and, with <paramref name="withHouse"/>, the configured address. Never throws for a failed read.</summary>
    Task<DtekFetch> SiteAsync(bool withHouse, CancellationToken ct);
}
