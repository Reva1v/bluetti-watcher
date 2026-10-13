using System.Globalization;
using BluettiWatcher.DtekWatcher;

namespace BluettiWatcher.Tests.DtekWatcher;

/// <summary>
/// Fixtures of the DTEK Watcher tests. dataset.json is the public dataset as captured on 07.10.2026 with its <c>preset</c>
/// cut down; blocked.html is the site's own challenge answer; schedule-page.html, format-changed.html and house.json are
/// hand-made to the site's contract (invented houses, no real address).
/// </summary>
internal static class Dx
{
    public static readonly TimeZoneInfo Kyiv = DtekTime.Kyiv;

    public static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "DtekWatcher", "Fixtures", name));

    /// <summary>Kyiv wall time as an instant: "2026-10-07 17:00".</summary>
    public static DateTimeOffset At(string wall) =>
        DtekTime.Resolve(DateTime.ParseExact(wall, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), Kyiv, earlier: true);

    public static long DayStart(string date) => At(date + " 00:00").ToUnixTimeSeconds();

    /// <summary>24 hour cells, all on except "18-21:0 10:f" (hour or range : cell code).</summary>
    public static string Cells(string spec = "")
    {
        var cells = new string('1', 24).ToCharArray();
        foreach (var part in spec.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split(':');
            var range = kv[0].Split('-');
            int from = int.Parse(range[0], CultureInfo.InvariantCulture), to = int.Parse(range[^1], CultureInfo.InvariantCulture);
            for (int n = from; n <= to; n++) cells[n - 1] = kv[1][0];
        }
        return new string(cells);
    }

    public static DtekDay Day(string date, params (string Queue, string Spec)[] queues) =>
        new() { Start = DayStart(date), Queues = queues.ToDictionary(q => q.Queue, q => Cells(q.Spec)) };

    /// <summary>A schedule the distributor updated at 11:43 on 07.10, collected at 11:45.</summary>
    public static DtekSchedule Schedule(params DtekDay[] days) => Updated("2026-10-07 11:43", "2026-10-07 11:45", "dataset", days);

    public static DtekSchedule Updated(string update, string collected, string source, params DtekDay[] days) => new()
    {
        Update = At(update).ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture), UpdateAt = At(update), CollectedAt = At(collected), Source = source, Days = [.. days],
    };

    /// <summary>07.10: A 17:00-21:00, B 22:00-24:00, C 09:00-09:30; 08.10: A 08:00-12:00, B 00:00-02:00 and 13:00-15:00, C 18:00-20:00.</summary>
    public static DtekDay[] TwoDayCells() =>
    [
        Day("2026-10-07", ("A", "18-21:0"), ("B", "23-24:0"), ("C", "10:f")),
        Day("2026-10-08", ("A", "9-12:0"), ("B", "1-2:0 14-15:0"), ("C", "19-20:0")),
    ];

    public static DtekSchedule TwoDays() => Schedule(TwoDayCells());

    public static readonly string[] House = ["A", "B", "C"];

    public static DtekCut Cut(string start, string end) => new(At(start), At(end));

    /// <summary>The module on, the site opted in with an address, no queues in config.</summary>
    public static DtekConfig Config(string? group = null, string[]? queues = null) =>
        new() { Enabled = true, Site = true, City = "Test City", Street = "Test Street", House = "1", Group = group, Queues = queues };
}

/// <summary>A source that answers from queues and records what was asked.</summary>
internal sealed class FakeSource : IDtekSource
{
    public Queue<DtekDatasetResult> Datasets { get; } = new();
    public Queue<DtekFetch> Sites { get; } = new();
    public Func<DtekDatasetResult> Dataset { get; set; } = () => Data(Dx.TwoDays());
    public Func<DtekFetch> Site { get; set; } = () => Blocked();
    /// <summary>"dataset" with the validator sent, or "site" / "site+house".</summary>
    public List<string> Asked { get; } = [];

    public Task<DtekDatasetResult> DatasetAsync(string? etag, CancellationToken ct)
    {
        Asked.Add(etag is null ? "dataset" : $"dataset {etag}");
        return Task.FromResult(Datasets.Count > 0 ? Datasets.Dequeue() : Dataset());
    }

    public Task<DtekFetch> SiteAsync(bool withHouse, CancellationToken ct)
    {
        Asked.Add(withHouse ? "site+house" : "site");
        return Task.FromResult(Sites.Count > 0 ? Sites.Dequeue() : Site());
    }

    public void Dispose() { }

    public static DtekDatasetResult Data(DtekSchedule schedule, string etag = "\"v1\"") => new(DtekStatus.Ok, schedule, etag, false, "");
    public static DtekDatasetResult NotModified(string etag = "\"v1\"") => new(DtekStatus.Ok, null, etag, true, "");
    public static DtekDatasetResult NoData(DtekStatus status = DtekStatus.Network, string detail = "why") => new(status, null, null, false, detail);

    public static DtekFetch Answer(DtekSchedule? schedule = null, params string[] queues) => new(
        new DtekPageResult(DtekStatus.Ok, schedule ?? Dx.Updated("2026-10-07 11:43", "2026-10-07 11:45", "site", Dx.TwoDayCells()), "token", ""),
        new DtekHouseResult(DtekStatus.Ok, new DtekHouse { Queues = queues.Length > 0 ? queues : Dx.House }, ""));

    public static DtekFetch Blocked() => new(new DtekPageResult(DtekStatus.Blocked, null, null, "a challenge page instead of the schedule"), null);
    public static DtekFetch Down(DtekStatus status = DtekStatus.Network, string detail = "why") => new(new DtekPageResult(status, null, null, detail), null);
}
