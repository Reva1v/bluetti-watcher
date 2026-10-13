namespace BluettiWatcher.DtekWatcher;

/// <summary>A cut the service saw from its first reading off mains to the first reading back on.</summary>
internal sealed record DtekCut(DateTimeOffset Start, DateTimeOffset End);

/// <summary>
/// Pure: which of the house's queues the real cuts follow (design.md §11 rules 3, 9). A cut matches a queue when both its ends fall
/// within 20 minutes of one planned window's borders. Only a cut that matches exactly one of the house's queues counts:
/// queues with the same window that day do not tell each other apart. Two counted cuts in a row for one queue fix it; a
/// later cut that matches other queues and not the fixed one opens the question again.
/// </summary>
internal static class DtekMatcher
{
    public static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(20);
    public const int Needed = 2;

    private static bool Near(DateTimeOffset a, DateTimeOffset b) => (a - b).Duration() <= Tolerance;

    /// <summary>The house's queues with a planned (never a "possible") window the cut matches.</summary>
    public static List<string> Matching(DtekCut cut, DtekSchedule schedule, IReadOnlyList<string> queues, TimeZoneInfo tz) =>
        [.. queues.Where(q => DtekTime.Windows(schedule, q, DtekSlot.Off, tz).Exists(w => Near(cut.Start, w.Start) && Near(cut.End, w.End)))];

    public static DtekMatch Apply(DtekMatch m, DtekCut cut, DtekSchedule? schedule, IReadOnlyList<string> queues, TimeZoneInfo tz)
    {
        // No schedule saved for the cut's time: the cut does not count.
        if (schedule is null || queues.Count == 0 || !DtekTime.Covers(schedule, cut.Start, tz) || !DtekTime.Covers(schedule, cut.End, tz)) return m;
        var matched = Matching(cut, schedule, queues, tz);
        if (matched.Count == 0) return m;   // an unplanned cut says nothing about the queue
        string? only = matched.Count == 1 ? matched[0] : null;
        if (m.Fixed is { } fixedQueue)
        {
            if (matched.Contains(fixedQueue)) return m;
            return new DtekMatch { Fixed = null, Candidate = only, Count = only is null ? 0 : 1 };   // contradicted
        }
        if (only is null) return m;   // several queues fit: nothing told apart
        int count = m.Candidate == only ? m.Count + 1 : 1;
        return new DtekMatch { Fixed = count >= Needed ? only : null, Candidate = only, Count = count };
    }

    /// <summary>
    /// The manual queue against one more real cut (design.md §11 rule 20). A cut that matches exactly one queue, and not the manual
    /// one, contradicts it; two such cuts in a row for the same queue refute it. A cut that matches the manual queue
    /// ends the row, and so does one that fits several queues without the one being counted. An unplanned cut (it matches
    /// no queue) and a cut with no schedule for its time tell nothing. The manual queue is compared even when it is not among the house's queues.
    /// </summary>
    public static DtekHint Hint(DtekHint? saved, string group, DtekCut cut, DtekSchedule? schedule, IReadOnlyList<string> queues, TimeZoneInfo tz)
    {
        var h = DtekHint.For(saved, group);
        if (h.RefutedBy is not null) return h;
        if (schedule is null || !DtekTime.Covers(schedule, cut.Start, tz) || !DtekTime.Covers(schedule, cut.End, tz)) return h;
        var matched = Matching(cut, schedule, queues.Contains(group) ? queues : [.. queues, group], tz);
        if (matched.Count == 0) return h;   // an unplanned cut says nothing about the queue
        if (matched.Contains(group)) return new DtekHint { Group = group };
        if (matched.Count > 1) return h.Candidate is { } running && matched.Contains(running) ? h : new DtekHint { Group = group };
        string only = matched[0];
        int count = h.Candidate == only ? h.Count + 1 : 1;
        return count >= Needed ? new DtekHint { Group = group, RefutedBy = only } : new DtekHint { Group = group, Candidate = only, Count = count };
    }

    /// <summary>The house's queues changed: a fixed or candidate queue that is no longer the house's is dropped.</summary>
    public static DtekMatch Keep(DtekMatch m, IReadOnlyList<string> queues) =>
        (m.Fixed is null || queues.Contains(m.Fixed)) && (m.Candidate is null || queues.Contains(m.Candidate)) ? m : new DtekMatch();
}
