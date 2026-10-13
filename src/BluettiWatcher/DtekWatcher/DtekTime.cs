using System.Globalization;
using System.Text;

namespace BluettiWatcher.DtekWatcher;

internal readonly record struct DtekWindow(DateTimeOffset Start, DateTimeOffset End);

/// <summary>
/// Pure: the schedule's cells as instants. A cell is Kyiv wall time (hour N of the day), so each half-hour border is the
/// day's own date plus half hours on the wall clock, turned into an instant by the zone's rules: on a 23 or 25 hour day
/// the evening windows stay where the site shows them and the day ends at the next midnight.
/// </summary>
internal static class DtekTime
{
    /// <summary>The schedule's zone: the source publishes Kyiv wall time, whatever the PC's own zone is.</summary>
    public static TimeZoneInfo Kyiv { get; } = FindKyiv();

    private static TimeZoneInfo FindKyiv()
    {
        foreach (var id in new[] { "FLE Standard Time", "Europe/Kyiv", "Europe/Kiev" })
        {
            if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out var tz)) return tz;
        }
        return TimeZoneInfo.Local;
    }

    /// <summary>A wall time as an instant. A time the clock skips moves to the first one that exists; one that comes twice is the earlier or the later.</summary>
    public static DateTimeOffset Resolve(DateTime wall, TimeZoneInfo tz, bool earlier)
    {
        wall = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);
        for (int k = 0; k < 8 && tz.IsInvalidTime(wall); k++) wall = wall.AddMinutes(30);
        if (!tz.IsAmbiguousTime(wall)) return new DateTimeOffset(wall, tz.GetUtcOffset(wall));
        var offsets = tz.GetAmbiguousTimeOffsets(wall);
        return new DateTimeOffset(wall, earlier ? offsets.Max() : offsets.Min());
    }

    /// <summary>The latest day start any arithmetic here takes: 01.01.2100. A larger (or negative) value is clamped, never thrown on.</summary>
    public const long MaxDayStart = 4102444800;

    /// <summary>The day's date: its Unix start seen in the zone, to the nearest midnight. Total: any long gives a date.</summary>
    public static DateTime DateOf(long dayStart, TimeZoneInfo tz) =>
        TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(Math.Clamp(dayStart, 0, MaxDayStart)), tz).AddHours(12).Date;

    /// <summary>The planned-off half hours of a day's cells as 48 bits: what "the windows" of a queue that day are.</summary>
    public static ulong OffMask(string cells) => OffMask(Expand(cells));

    /// <summary>The planned-off half hours of 48 slots as 48 bits.</summary>
    public static ulong OffMask(DtekSlot[] slots)
    {
        ulong mask = 0;
        for (int i = 0; i < 48; i++) if (slots[i] == DtekSlot.Off) mask |= 1UL << i;
        return mask;
    }

    /// <summary>A set of queues joins its names with this: "GPV1.1+GPV2.1".</summary>
    public const char SetJoin = '+';

    /// <summary>The queues a name stands for: a set's members in their order, without blanks or repeats; a plain name alone.</summary>
    public static IReadOnlyList<string> Members(string queue) =>
        queue.Contains(SetJoin, StringComparison.Ordinal)
            ? [.. queue.Split(SetJoin, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal)]
            : [queue];

    private static DtekSlot[] Expand(string cells)
    {
        var slots = new DtekSlot[48];
        for (int i = 0; i < 48; i++) slots[i] = SlotOf(cells, i);
        return slots;
    }

    /// <summary>How strongly a half hour of one member decides the set's: off, then unknown (never guessed), then possible, then on.</summary>
    private static int Rank(DtekSlot s) => s switch { DtekSlot.Off => 3, DtekSlot.Unknown => 2, DtekSlot.Possible => 1, _ => 0 };

    /// <summary>
    /// A queue's 48 half hours on a day; null when the day has no such queue. A set of queues ("GPV1.1+GPV2.1") is the union
    /// of its members: a half hour is off when any member's is, else unknown when any is, else possible when any is, else
    /// on. A day that lacks one member has no set either: nothing is guessed.
    /// </summary>
    public static DtekSlot[]? Slots(DtekDay day, string queue)
    {
        if (day.Queues.TryGetValue(queue, out var cells)) return Expand(cells);
        var members = Members(queue);
        if (members.Count < 2) return null;
        var union = new DtekSlot[48];
        foreach (var m in members)
        {
            if (!day.Queues.TryGetValue(m, out var c)) return null;
            for (int i = 0; i < 48; i++)
            {
                var s = SlotOf(c, i);
                if (Rank(s) > Rank(union[i])) union[i] = s;
            }
        }
        return union;
    }

    /// <summary>
    /// Why a schedule a source returned (or one saved) cannot be used; null when it can. Each day must begin at a Kyiv
    /// midnight (within 2 hours, for clock changes) no more than 2 days back or 7 ahead; the distributor's update time may
    /// not be more than a day ahead of now (it would outrank every later real schedule).
    /// </summary>
    public static string? Problem(DtekSchedule s, DateTimeOffset now, TimeZoneInfo tz)
    {
        if (s.Days is not { Count: > 0 and <= MaxDays }) return "the schedule has no days or too many";
        var today = TimeZoneInfo.ConvertTime(now, tz).Date;
        foreach (var day in s.Days)
        {
            if (day.Start is < 0 or > MaxDayStart) return $"a day key out of range ({day.Start})";
            var local = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(day.Start), tz);
            var date = local.AddHours(12).Date;
            if ((local.DateTime - date).Duration() > TimeSpan.FromHours(2)) return $"a day key that is no Kyiv day start ({day.Start})";
            if (date < today.AddDays(-2) || date > today.AddDays(7)) return $"a day outside the days around today ({day.Start})";
        }
        return s.UpdateAt is { } at && at > now.AddHours(24) ? $"an update time in the future ({s.Update})" : null;
    }

    public const int MaxDays = 4;
    public const int MaxQueues = 24;

    /// <summary>The border after <paramref name="halfHours"/> half hours of the day's wall clock (0 = midnight, 48 = the next midnight).</summary>
    public static DateTimeOffset Border(long dayStart, int halfHours, TimeZoneInfo tz, bool earlier) =>
        Resolve(DateOf(dayStart, tz).AddMinutes(30 * halfHours), tz, earlier);

    public static DtekSlot SlotOf(string cells, int halfHour)
    {
        var (first, second) = DtekParser.Halves(cells[halfHour / 2]);
        return halfHour % 2 == 0 ? first : second;
    }

    /// <summary>The time the schedule's days cover: from the first midnight to the midnight after the last day.</summary>
    public static DtekWindow? Covered(DtekSchedule? s, TimeZoneInfo tz) => s is not { Days.Count: > 0 }
        ? null
        : new DtekWindow(Border(s.Days[0].Start, 0, tz, true), Border(s.Days[^1].Start, 48, tz, false));

    public static bool Covers(DtekSchedule? s, DateTimeOffset at, TimeZoneInfo tz) => Covered(s, tz) is { } c && at >= c.Start && at < c.End;

    /// <summary>
    /// The stretches of one kind for a queue over all the days, oldest first; half hours that touch are one stretch, across
    /// midnight too. For a set of queues, the stretches of the union (<see cref="Slots"/>): overlapping and touching
    /// windows of its members are one.
    /// </summary>
    public static List<DtekWindow> Windows(DtekSchedule s, string queue, DtekSlot kind, TimeZoneInfo tz)
    {
        var list = new List<DtekWindow>();
        foreach (var day in s.Days)
        {
            if (Slots(day, queue) is not { } slots) continue;
            for (int i = 0; i < 48; i++)
            {
                if (slots[i] != kind) continue;
                var start = Border(day.Start, i, tz, true);
                var end = Border(day.Start, i + 1, tz, false);
                if (end <= start) continue;   // a half hour the clock skips
                if (list.Count > 0 && list[^1].End >= start) list[^1] = list[^1] with { End = end > list[^1].End ? end : list[^1].End };
                else list.Add(new DtekWindow(start, end));
            }
        }
        return list;
    }

    private static string Clock(int halfHour) => string.Create(CultureInfo.InvariantCulture, $"{halfHour / 2:00}:{halfHour % 2 * 30:00}");

    /// <summary>
    /// One day of a queue as the page shows it, on the day's own wall clock: "17:00-21:00, possible 21:00-22:00"; "no cuts"
    /// for a day all on; "" when the schedule has no such day or queue.
    /// </summary>
    public static string DayText(DtekSchedule s, string queue, DateTime date, TimeZoneInfo tz)
    {
        var day = s.Days.Find(d => DateOf(d.Start, tz) == date.Date);
        if (day is null || Slots(day, queue) is not { } slots) return "";
        var text = new StringBuilder();
        for (int i = 0; i < 48;)
        {
            var kind = slots[i];
            int from = i;
            while (i < 48 && slots[i] == kind) i++;
            if (kind == DtekSlot.On) continue;
            if (text.Length > 0) text.Append(", ");
            text.Append(kind switch { DtekSlot.Possible => "possible ", DtekSlot.Unknown => "unknown ", _ => "" }).Append(Clock(from)).Append('-').Append(Clock(i));
        }
        return text.Length == 0 ? "no cuts" : text.ToString();
    }
}
