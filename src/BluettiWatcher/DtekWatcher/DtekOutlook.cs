using System.Globalization;

namespace BluettiWatcher.DtekWatcher;

/// <summary>A planned-off interval and the queues it came from (for a set, the members whose windows make it up).</summary>
internal sealed record DtekInterval(DateTimeOffset Start, DateTimeOffset End, IReadOnlyList<string> Queues)
{
    public bool Equals(DtekInterval? other) => other is not null && Start == other.Start && End == other.End && Queues.SequenceEqual(other.Queues);
    public override int GetHashCode() => HashCode.Combine(Start, End, Queues.Count);
}

/// <summary>One shown queue: its off-windows today and tomorrow as text.</summary>
internal sealed record DtekQueueDays(string Queue, string Today, string Tomorrow)
{
    /// <summary>The planned-off intervals of today and tomorrow with where each came from.</summary>
    public IReadOnlyList<DtekInterval> Intervals { get; init; } = [];

    /// <summary>The texts only: the intervals say the same in times (a value compare stays a value compare).</summary>
    public bool Equals(DtekQueueDays? other) => other is not null && Queue == other.Queue && Today == other.Today && Tomorrow == other.Tomorrow;
    public override int GetHashCode() => HashCode.Combine(Queue, Today, Tomorrow);
}

/// <summary>What /api/now and the page say about the schedule (design.md §11 rule 14). Times are instants; texts are Kyiv wall time.</summary>
internal sealed record DtekView(
    string State, string Note, string Source, string Update, DateTimeOffset? CollectedAt, string DatasetState, string SiteState,
    IReadOnlyList<string> Queues, string QueuesSource, string? Queue, string QueueSource, string QueueOrigin, int QueueContradictions,
    DateTimeOffset? NextStart, DateTimeOffset? NextEnd, string? NextQueue, DateTimeOffset? Return,
    string OutageKind, string OutageStart, string OutageEnd, DateTimeOffset? OutageEndAt, string? BatteryLasts,
    string Today, string Tomorrow, IReadOnlyList<DtekQueueDays> Windows)
{
    /// <summary>During a cut: the planned window it belonged to has ended and no window covers now (design.md §11 rule 24).</summary>
    public bool Overdue { get; init; }
    /// <summary>Whole minutes since that window's end while overdue, else null.</summary>
    public int? OverdueMin { get; init; }
    /// <summary>During a cut: the end of the planned window followed, passed or not; null outside a cut or with none.</summary>
    public DateTimeOffset? PlannedReturn { get; init; }
    /// <summary>During a cut: now plus the battery estimate down to the hibernate level at the current load; null when it is missing, zero or not a number.</summary>
    public DateTimeOffset? BatteryUntil { get; init; }
    /// <summary>During a cut: schedules taken in since it began that moved a shown queue's windows; 0 outside a cut.</summary>
    public int PlanChanges { get; init; }
    /// <summary>The median end delta of the recorded cuts, minutes (positive = back after the plan); null under 3 cuts.</summary>
    public int? LateBackMin { get; init; }
    /// <summary>Recorded cuts the median is taken over.</summary>
    public int LateCuts { get; init; }
    /// <summary>The members of <see cref="Queue"/>: a set's queues, the queue alone, none while no queue is followed.</summary>
    public IReadOnlyList<string> QueueSet { get; init; } = [];
    /// <summary>The queues the next planned outage came from (a set's members whose windows make it up).</summary>
    public IReadOnlyList<string> NextQueues { get; init; } = [];
    /// <summary>The notes <see cref="Note"/> joins, each with the code the page translates it by.</summary>
    public IReadOnlyList<PageNote> Notes { get; init; } = [];

    public static DtekView Off(PageNote? note = null) =>
        new("off", note?.Text ?? "", "", "", null, "off", "off", [], "", null, "", "", 0, null, null, null, null, "", "", "", null, null, "", "", []) { Notes = note is null ? [] : [note] };

    /// <summary>The module is on and cannot say anything: no schedule, and the note says why.</summary>
    public static DtekView Broken(PageNote note) => Off(note) with { State = "none" };
}

/// <summary>
/// Pure: the next planned outage, the expected return during a cut and whether the battery lasts until it. It informs
/// only: nothing here is read by the decision.
/// </summary>
internal static class DtekOutlook
{
    private static string Day(DateTime d) => d.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);

    internal static string Stamp(DateTimeOffset at, TimeZoneInfo tz) => TimeZoneInfo.ConvertTime(at, tz).ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);

    /// <summary>The house's queues: the configured list, else the ones the site's address answer named (saved), else none.</summary>
    public static (IReadOnlyList<string> Queues, string Source) HouseQueues(DtekConfig config, DtekSnapshot snap)
    {
        string[] configured = [.. (config.Queues ?? []).Where(q => !string.IsNullOrWhiteSpace(q)).Select(q => q.Trim()).Distinct(StringComparer.Ordinal)];
        if (configured.Length > 0) return (configured, "config");
        return snap.House is { Queues.Length: > 0 } h ? (h.Queues, "site") : ([], "");
    }

    /// <summary>
    /// <c>dtekWatcher.group</c> as a queue name; null when it is not set. A set of queues is one name, its members joined
    /// with + in the configured order ("GPV1.1+GPV2.1"): the outlook, the matching and the lateness read it as the union.
    /// </summary>
    public static string? Manual(DtekConfig config) =>
        string.IsNullOrWhiteSpace(config.Group) || DtekTime.Members(config.Group.Trim()) is not { Count: > 0 } members ? null : string.Join(DtekTime.SetJoin, members);

    /// <summary>The members of a queue name the outlook follows: a set's queues, or the queue alone; none for null.</summary>
    public static IReadOnlyList<string> MembersOf(string? queue) => queue is null ? [] : DtekTime.Members(queue);

    /// <summary>The members of <paramref name="queue"/> whose own planned-off windows overlap <paramref name="w"/>: where an interval of a set came from.</summary>
    public static IReadOnlyList<string> From(DtekSchedule s, string queue, DtekWindow w, TimeZoneInfo tz)
    {
        var members = DtekTime.Members(queue);
        return members.Count < 2 ? members : [.. members.Where(m => DtekTime.Windows(s, m, DtekSlot.Off, tz).Exists(x => x.Start < w.End && x.End > w.Start))];
    }

    /// <summary>The queues a real cut is compared with: the house's, and the manual one even when it is not among them.</summary>
    public static IReadOnlyList<string> Compared(DtekConfig config, DtekSnapshot snap)
    {
        var house = HouseQueues(config, snap).Queues;
        return Manual(config) is { } manual && !house.Contains(manual) ? [.. house, manual] : house;
    }

    /// <summary>The queue the real cuts refuted this manual queue for; null while it stands or none is set.</summary>
    public static string? RefutedBy(DtekConfig config, DtekSnapshot snap) =>
        Manual(config) is { } manual && snap.Hint is { RefutedBy: { } by } h && h.Group == manual ? by : null;

    /// <summary>Cuts in a row that contradict the manual queue so far; 0 without one or once it is refuted.</summary>
    public static int Contradictions(DtekConfig config, DtekSnapshot snap) =>
        Manual(config) is { } manual && snap.Hint is { RefutedBy: null } h && h.Group == manual ? h.Count : 0;

    /// <summary>
    /// The queue the outlook follows and where it came from. The manual one while the real cuts have not refuted it
    /// (origin <c>manual</c>); else the matched one, else the house's only queue (origin <c>detected</c>, or
    /// <c>manual-refuted</c> when a manual queue was refuted, with or without a queue found since).
    /// </summary>
    public static (string? Queue, string Source, string Origin) QueueOf(DtekConfig config, DtekSnapshot snap)
    {
        bool refuted = RefutedBy(config, snap) is not null;
        if (Manual(config) is { } manual && !refuted) return (manual, "config", "manual");
        string origin = refuted ? "manual-refuted" : "detected";
        var house = HouseQueues(config, snap).Queues;
        if (snap.Match.Fixed is { } f && house.Contains(f)) return (f, "matched", origin);
        return house is [var only] ? (only, "single", origin) : (null, "", refuted ? origin : "");
    }

    /// <summary>The queues whose windows are shown: the one followed, else all the house's.</summary>
    public static IReadOnlyList<string> Shown(DtekConfig config, DtekSnapshot snap) =>
        QueueOf(config, snap).Queue is { } q ? [q] : HouseQueues(config, snap).Queues;

    public static string SourceState(bool on, DateTimeOffset? triedAt, string error) =>
        !on || triedAt is null ? "off" : error.Length == 0 ? "ok" : error == "blocked" ? "blocked" : "failed";

    /// <summary><paramref name="cut"/>: the service counts a cut as running. <paramref name="hoursLeft"/>: the battery estimate at the current load, null = unknown.</summary>
    public static DtekView Compute(DtekConfig config, DtekSnapshot snap, bool cut, double? hoursLeft, TimeProvider time, TimeZoneInfo tz, double? hoursToHibernate = null)
    {
        if (!config.Enabled) return DtekView.Off();
        if (config.Problem() is { } problem) return DtekView.Off(new PageNote(PageCodes.DtekConfig, problem, problem));
        var now = time.GetUtcNow();
        var (house, houseSource) = HouseQueues(config, snap);
        var (queue, source, origin) = QueueOf(config, snap);
        var shown = Shown(config, snap);
        var schedule = shown.Count > 0 && DtekTime.Covers(snap.Schedule, now, tz) ? snap.Schedule : null;
        bool old = schedule is not null && now - schedule.CollectedAt > TimeSpan.FromMinutes(config.StaleAfterMin);
        string state = schedule is null ? "none" : old ? "stale" : "ok";
        string datasetState = SourceState(true, snap.DatasetTriedAt, snap.DatasetError);
        string siteState = SourceState(config.Site, snap.SiteTriedAt, snap.SiteError);

        var notes = new List<PageNote>();
        if (shown.Count == 0) notes.Add(new(PageCodes.DtekQueuesUnknown, "the house's queues are not known: set dtekWatcher.queues (such as GPV1.1)"));
        else if (schedule is null) notes.Add(new(PageCodes.DtekNoSchedule, "no schedule"));
        else if (old)
        {
            string stamp = Stamp(schedule.CollectedAt, tz), min = config.StaleAfterMin.ToString(CultureInfo.InvariantCulture);
            notes.Add(new(PageCodes.DtekStale, $"stale: last collected {stamp}, over {min} min ago", stamp, min));
        }
        if (shown.Count > 1) notes.Add(new(PageCodes.DtekQueueNotFixed, "the queue is not fixed yet: all the house's queues are shown"));
        if (Manual(config) is { } manual)
        {
            if (RefutedBy(config, snap) is { } by) notes.Add(new(PageCodes.DtekGroupRefuted, $"dtekWatcher.group {manual} does not fit the real cuts: two in a row followed {by}", manual, by));
            else if (house.Count > 0 && DtekTime.Members(manual).Where(m => !house.Contains(m)).ToList() is { Count: > 0 } outside)
            {
                string names = string.Join(DtekTime.SetJoin, outside);
                notes.Add(new(PageCodes.DtekGroupNotHouse, $"dtekWatcher.group {names} is not among the house's queues", names));
            }
        }
        if (schedule is { UpdateAt: null }) notes.Add(new(PageCodes.DtekUpdateUnreadable, "the distributor's update time cannot be read: the schedule was taken by its collection time"));
        if (datasetState == "failed") notes.Add(new(PageCodes.DtekDatasetFailed, $"dataset read failed ({snap.DatasetError}){(snap.DatasetDetail.Length > 0 ? ": " + snap.DatasetDetail : "")}", snap.DatasetError, snap.DatasetDetail.Length > 0 ? ": " + snap.DatasetDetail : ""));
        if (siteState == "blocked") notes.Add(new(PageCodes.DtekSiteBlocked, "the site blocks the read"));
        else if (siteState == "failed") notes.Add(new(PageCodes.DtekSiteFailed, $"site read failed ({snap.SiteError}){(snap.SiteDetail.Length > 0 ? ": " + snap.SiteDetail : "")}", snap.SiteError, snap.SiteDetail.Length > 0 ? ": " + snap.SiteDetail : ""));

        DateTimeOffset? nextStart = null, nextEnd = null, back = null;
        string? nextQueue = null;
        IReadOnlyList<string> nextQueues = [];
        var days = new List<DtekQueueDays>();
        var today = TimeZoneInfo.ConvertTime(now, tz).Date;
        if (schedule is not null)
        {
            foreach (var q in shown)
            {
                var planned = DtekTime.Windows(schedule, q, DtekSlot.Off, tz);
                // The next planned outage: the first window not over yet (one already running starts in the past).
                int n = planned.FindIndex(w => w.End > now);
                if (n >= 0 && (nextStart is null || planned[n].Start < nextStart)) (nextStart, nextEnd, nextQueue, nextQueues) = (planned[n].Start, planned[n].End, q, From(schedule, q, planned[n], tz));
                // During a cut: the window the cut belongs to. Queue unknown: the latest return among the house's queues.
                int r = cut ? planned.FindIndex(w => now >= w.Start - DtekMatcher.Tolerance && now < w.End) : -1;
                if (r >= 0 && (back is null || planned[r].End > back)) back = planned[r].End;
                // Where each planned interval of today and tomorrow came from (a set's members; a queue alone names itself).
                var from = DtekTime.Resolve(today, tz, true);
                var to = DtekTime.Resolve(today.AddDays(2), tz, false);
                var intervals = planned.Where(w => w.End > from && w.Start < to).Select(w => new DtekInterval(w.Start, w.End, From(schedule, q, w, tz))).ToList();
                days.Add(new DtekQueueDays(q, DtekTime.DayText(schedule, q, today, tz), DtekTime.DayText(schedule, q, today.AddDays(1), tz)) { Intervals = intervals });
            }
        }

        // Overdue: no window covers now, and the one the running cut belonged to has ended. The plan no longer gives a return.
        DateTimeOffset? plannedReturn = cut ? back : null;
        int? overdueMin = null;
        if (cut && back is null && shown.Count > 0 && snap.OpenCut is { } running
            && Belonged(Plan(snap.Schedule, running.Held, tz), shown, running.Start, now, tz) is { } was && was.End <= now)
        {
            plannedReturn = was.End;
            overdueMin = (int)Math.Floor(Math.Min((now - was.End).TotalMinutes, DtekLate.MaxDelta));
        }

        // The site's own outage line: only what the site answered during this cut.
        var h = cut && snap.OpenCut is { } open && snap.House is { } answered && answered.FetchedAt >= open.Start ? snap.House : null;
        var outageEndAt = h is { EndText.Length: > 0 } ? DtekParser.ParseDate(h.EndText, tz) : null;
        var outageStartAt = h is { StartText.Length: > 0 } ? DtekParser.ParseDate(h.StartText, tz) : null;
        // The plan first; with no planned window for this cut, the end the site itself gives for the running outage.
        if (cut && back is null && outageEndAt is { } siteEnd && siteEnd > now && (outageStartAt is null || outageStartAt <= now)) back = siteEnd;

        // The estimate bounded: not a number or negative is unknown; past MaxHours is MaxHours.
        double? hours = Bounded(hoursLeft), toHibernate = Bounded(hoursToHibernate);
        string? lasts = !cut ? null : hours is { } left && back is { } until ? (now.AddHours(left) >= until ? "yes" : "no") : "unknown";

        return new DtekView(state, string.Join(" · ", notes.Select(n => n.Text)), snap.Schedule?.Source ?? "", snap.Schedule?.Update ?? "", snap.Schedule?.CollectedAt, datasetState, siteState,
            house, houseSource, queue, source, origin, Contradictions(config, snap), nextStart, nextEnd, nextQueue, cut ? back : null,
            h?.Kind ?? "", outageStartAt is { } os ? Stamp(os, tz) : h?.StartText ?? "", outageEndAt is { } oe ? Stamp(oe, tz) : h?.EndText ?? "", outageEndAt, lasts,
            Day(today), Day(today.AddDays(1)), days)
        {
            Notes = notes,
            Overdue = overdueMin is not null,
            OverdueMin = overdueMin,
            PlannedReturn = plannedReturn,
            BatteryUntil = cut && toHibernate is > 0 ? now.AddHours(toHibernate.Value) : null,
            PlanChanges = cut && snap.OpenCut is { } changing ? changing.PlanChanges : 0,
            LateBackMin = Median(snap.Late),
            LateCuts = snap.Late.Count,
            QueueSet = MembersOf(queue),
            NextQueues = nextQueues,
        };
    }

    private static double? Bounded(double? h) => h is { } raw && double.IsFinite(raw) && raw >= 0 ? Math.Min(raw, MaxHours) : null;

    /// <summary>The battery estimate's bound, hours.</summary>
    public const double MaxHours = 24 * 30;

    /// <summary>Recorded cuts below which no usual lateness is given.</summary>
    public const int MinLate = 3;

    /// <summary>The median end delta, whole minutes (a half rounds away from zero); null under <see cref="MinLate"/> cuts.</summary>
    public static int? Median(IReadOnlyList<DtekLate> late)
    {
        if (late.Count < MinLate) return null;
        int[] d = [.. late.Select(e => e.EndDelta).Order()];
        int m = d.Length / 2;
        return d.Length % 2 == 1 ? d[m] : (int)Math.Round((d[m - 1] + (double)d[m]) / 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// The plan a running or finished cut is read against: the schedule in use, with the days it no longer has (a cut that
    /// began before midnight) taken from the schedule held when the cut began.
    /// </summary>
    public static DtekSchedule? Plan(DtekSchedule? current, DtekSchedule? held, TimeZoneInfo tz)
    {
        if (held is null || current is null) return current ?? held;
        var days = current.Days.ToList();
        days.AddRange(held.Days.Where(d => !current.Days.Exists(c => DtekTime.DateOf(c.Start, tz) == DtekTime.DateOf(d.Start, tz))));
        days.Sort((a, b) => a.Start.CompareTo(b.Start));
        return new DtekSchedule { Days = days };
    }

    /// <summary>
    /// The planned window a cut belongs to, seen at <paramref name="until"/> (its end, or now while it runs): the first
    /// window of a queue that holds the cut's start (from 20 minutes before the window), stretched over the windows of
    /// that queue that begin, less 20 minutes, by <paramref name="until"/>. Among the queues, the one with the latest end.
    /// Null when the cut began in no planned window.
    /// </summary>
    public static (string Queue, DateTimeOffset Start, DateTimeOffset End)? Belonged(DtekSchedule? plan, IReadOnlyList<string> queues, DateTimeOffset start, DateTimeOffset until, TimeZoneInfo tz)
    {
        if (plan is null) return null;
        (string, DateTimeOffset, DateTimeOffset)? best = null;
        foreach (var q in queues)
        {
            var planned = DtekTime.Windows(plan, q, DtekSlot.Off, tz);
            int first = planned.FindIndex(w => start >= w.Start - DtekMatcher.Tolerance && start < w.End);
            if (first < 0) continue;
            var end = planned[first].End;
            for (int k = first + 1; k < planned.Count && planned[k].Start - DtekMatcher.Tolerance <= until; k++) end = planned[k].End;
            if (best is null || end > best.Value.Item3) best = (q, planned[first].Start, end);
        }
        return best;
    }

    private static int Minutes(TimeSpan t) => (int)Math.Round(Math.Clamp(t.TotalMinutes, -DtekLate.MaxDelta, DtekLate.MaxDelta), MidpointRounding.AwayFromZero);

    /// <summary>
    /// A finished cut's lateness (design.md §11 rule 25): against the window it belonged to in the plan as it stood at its end, and
    /// the window it belonged to in the schedule held at its start; null when it began in no planned window then.
    /// </summary>
    public static DtekLate? Lateness(DtekCut seen, DtekSchedule? current, DtekSchedule? held, IReadOnlyList<string> queues, TimeZoneInfo tz)
    {
        if (Belonged(Plan(current, held, tz), queues, seen.Start, seen.End, tz) is not { } end) return null;
        var first = Belonged(held, queues, seen.Start, seen.End, tz);
        return new DtekLate
        {
            Start = seen.Start, End = seen.End, Queue = end.Queue, Date = Day(TimeZoneInfo.ConvertTime(end.Start, tz).Date),
            StartDelta = Minutes(seen.Start - end.Start), EndDelta = Minutes(seen.End - end.End),
            FirstStartDelta = first is { } f ? Minutes(seen.Start - f.Start) : null, FirstEndDelta = first is { } g ? Minutes(seen.End - g.End) : null,
            Union = DtekTime.Members(end.Queue).Count > 1,
        };
    }

    /// <summary>
    /// What moved for the shown queues between two schedules, on the days both have: "GPV1.1 07.10.2026: 17:00-21:00 ->
    /// 16:00-21:00", joined; "" when nothing did.
    /// </summary>
    public static string Changes(DtekSchedule? before, DtekSchedule after, IReadOnlyList<string> shown, TimeZoneInfo tz) =>
        string.Join("; ", Moved(before, after, shown, tz).Select(m => m.Text));

    /// <summary>
    /// The same, one entry per queue and day (the key names both). Compared are the parsed planned-off half hours, never
    /// the texts or the order a source wrote them in: a "possible" or unknown cell changing is no moved plan.
    /// </summary>
    public static List<(string Key, string Text)> Moved(DtekSchedule? before, DtekSchedule after, IReadOnlyList<string> shown, TimeZoneInfo tz)
    {
        var moved = new List<(string, string)>();
        if (before is null) return moved;
        foreach (var day in after.Days)
        {
            var date = DtekTime.DateOf(day.Start, tz);
            var old = before.Days.Find(d => DtekTime.DateOf(d.Start, tz) == date);
            if (old is null) continue;
            foreach (var q in shown)
            {
                if (DtekTime.Slots(old, q) is not { } was || DtekTime.Slots(day, q) is not { } now || DtekTime.OffMask(was) == DtekTime.OffMask(now)) continue;
                moved.Add(($"{q} {Day(date)}", $"{q} {Day(date)}: {DtekTime.DayText(before, q, date, tz)} -> {DtekTime.DayText(after, q, date, tz)}"));
            }
        }
        return moved;
    }

    /// <summary>The planned-off half hours of the queues are the same on every day both schedules have.</summary>
    public static bool SameWindows(DtekSchedule a, DtekSchedule b, IReadOnlyList<string> queues, TimeZoneInfo tz) => Moved(a, b, queues, tz).Count == 0;
}
