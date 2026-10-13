using System.Text.Json;
using BluettiWatcher.Decision;
using BluettiWatcher.DtekWatcher;
using BluettiWatcher.Http;
using BluettiWatcher.Storage;
using static BluettiWatcher.Tests.DtekWatcher.Dx;

namespace BluettiWatcher.Tests.DtekWatcher;

/// <summary>The manual queue as a hint the real cuts can refute (design.md §11 rules 20-22).</summary>
internal static class Hx
{
    /// <summary>07.10: C 09:00-10:00, B 13:00-15:00, A 17:00-21:00; 08.10: A 08:00-12:00, B 13:00-15:00, C 18:00-20:00.</summary>
    public static DtekDay[] Days() =>
    [
        Day("2026-10-07", ("A", "18-21:0"), ("B", "14-15:0"), ("C", "10:0")),
        Day("2026-10-08", ("A", "9-12:0"), ("B", "14-15:0"), ("C", "19-20:0")),
    ];

    public static readonly DtekCut A1 = Cut("2026-10-07 17:02", "2026-10-07 20:58");
    public static readonly DtekCut B1 = Cut("2026-10-07 13:02", "2026-10-07 14:57");
    public static readonly DtekCut A2 = Cut("2026-10-08 08:05", "2026-10-08 11:55");
    public static readonly DtekCut B2 = Cut("2026-10-08 13:02", "2026-10-08 14:57");
    public static readonly DtekCut C2 = Cut("2026-10-08 18:01", "2026-10-08 19:58");
    public static readonly DtekCut Unplanned = Cut("2026-10-08 03:00", "2026-10-08 05:00");
}

public sealed class DtekWatcherHintMatcherTests
{
    private static DtekHint Feed(string group, DtekSchedule? s, string[] house, params DtekCut[] cuts)
    {
        DtekHint? h = null;
        foreach (var c in cuts) h = DtekMatcher.Hint(h, group, c, s, house, Kyiv);
        return h!;
    }

    private static (string? Candidate, int Count, string? RefutedBy) Of(DtekHint h) => (h.Candidate, h.Count, h.RefutedBy);

    // design.md §11 DTEK module, rule 20
    [Fact]
    public void Two_cuts_in_a_row_for_one_other_queue_refute_the_manual_queue_and_one_alone_does_not()
    {
        Assert.Equal(("B", 1, null), Of(Feed("A", Schedule(Hx.Days()), House, Hx.B1)));
        var h = Feed("A", Schedule(Hx.Days()), House, Hx.B1, Hx.B2);
        Assert.Equal((null, 0, "B", "A"), (h.Candidate, h.Count, h.RefutedBy, h.Group));
    }

    // design.md §11 DTEK module, rule 20
    [Fact]
    public void Two_contradicting_cuts_for_different_queues_refute_nothing()
    {
        Assert.Equal(("C", 1, null), Of(Feed("A", Schedule(Hx.Days()), House, Hx.B1, Hx.C2)));
    }

    // design.md §11 DTEK module, rule 20
    [Fact]
    public void A_cut_that_follows_the_manual_queue_ends_the_row()
    {
        Assert.Equal((null, 0, null), Of(Feed("A", Schedule(Hx.Days()), House, Hx.B1, Hx.A1)));
        Assert.Equal(("B", 1, null), Of(Feed("A", Schedule(Hx.Days()), House, Hx.B1, Hx.A1, Hx.B2)));
    }

    // design.md §11 DTEK module, rule 20
    [Fact]
    public void A_cut_that_matches_no_queue_says_nothing_and_keeps_the_row()
    {
        Assert.Equal(("B", 1, null), Of(Feed("A", Schedule(Hx.Days()), House, Hx.B1, Hx.Unplanned)));
        Assert.Equal("B", Feed("A", Schedule(Hx.Days()), House, Hx.B1, Hx.Unplanned, Hx.B2).RefutedBy);
        Assert.Equal((null, 0, null), Of(Feed("A", Schedule(Hx.Days()), House, Hx.Unplanned)));
    }

    // design.md §11 DTEK module, rules 20, 9
    [Fact]
    public void Queues_with_the_same_window_do_not_count_against_the_manual_queue()
    {
        var twins = Schedule(Day("2026-10-07", ("A", "18-21:0"), ("B", "14-15:0"), ("Twin", "14-15:0")), Day("2026-10-08", ("A", "9-12:0"), ("B", "14-15:0"), ("Twin", "14-15:0")));
        Assert.Equal((null, 0, null), Of(Feed("A", twins, ["A", "B", "Twin"], Hx.B1, Hx.B2)));
        // a twin of the manual queue itself: the cut follows the manual queue
        var own = Schedule(Day("2026-10-07", ("A", "14-15:0"), ("B", "14-15:0")), Day("2026-10-08", ("A", "14-15:0"), ("B", "14-15:0")));
        Assert.Equal((null, 0, null), Of(Feed("A", own, ["A", "B"], Hx.B1, Hx.B2)));
    }

    // design.md §11 DTEK module, rule 20
    [Fact]
    public void A_cut_that_fits_several_queues_keeps_a_row_it_fits_and_ends_one_it_does_not()
    {
        DtekSchedule Three(string twinOf) => Schedule(
            Day("2026-10-07", ("A", "18-21:0"), ("B", "14-15:0"), ("C", "10:0"), ("T", "1:0")),
            Day("2026-10-08", ("A", "9-12:0"), ("B", "14-15:0"), ("C", "19-20:0"), ("T", twinOf == "B" ? "14-15:0" : "19-20:0")),
            Day("2026-10-09", ("A", "9-12:0"), ("B", "14-15:0"), ("C", "19-20:0"), ("T", "1:0")));
        string[] house = ["A", "B", "C", "T"];
        var b3 = Cut("2026-10-09 13:02", "2026-10-09 14:57");
        Assert.Equal("B", Feed("A", Three("B"), house, Hx.B1, Hx.B2, b3).RefutedBy);                 // B, then B or T, then B
        Assert.Equal(("B", 1, null), Of(Feed("A", Three("C"), house, Hx.B1, Hx.C2, b3)));            // B, then C or T, then B
    }

    // design.md §11 DTEK module, rule 20
    [Fact]
    public void A_manual_queue_that_is_not_among_the_houses_is_compared_all_the_same()
    {
        string[] house = ["B", "C"];
        Assert.Equal((null, 0, null), Of(Feed("A", Schedule(Hx.Days()), house, Hx.B1, Hx.A1)));   // its own window still agrees
        Assert.Equal("B", Feed("A", Schedule(Hx.Days()), house, Hx.B1, Hx.B2).RefutedBy);
        // a queue no schedule names: nothing to compare it with, so the cuts of the house's queues refute it
        Assert.Equal("B", Feed("GPV9.9", Schedule(Hx.Days()), house, Hx.B1, Hx.B2).RefutedBy);
    }

    // design.md §11 DTEK module, rule 21
    [Fact]
    public void What_was_counted_against_another_value_is_dropped_and_a_refuted_one_stays_refuted()
    {
        var one = Feed("A", Schedule(Hx.Days()), House, Hx.B1);
        Assert.Equal(("B", 1, null, "C"), (DtekMatcher.Hint(one, "C", Hx.B2, Schedule(Hx.Days()), House, Kyiv) is var h ? h.Candidate : null, h.Count, h.RefutedBy, h.Group));
        var refuted = Feed("A", Schedule(Hx.Days()), House, Hx.B1, Hx.B2);
        Assert.Equal("B", DtekMatcher.Hint(refuted, "A", Hx.A2, Schedule(Hx.Days()), House, Kyiv).RefutedBy);
        Assert.Null(DtekMatcher.Hint(refuted, "C", Hx.A2, Schedule(Hx.Days()), House, Kyiv).RefutedBy);
    }

    // design.md §11 DTEK module, rule 9
    [Fact]
    public void A_cut_with_no_schedule_for_its_time_tells_nothing()
    {
        var one = Feed("A", Schedule(Hx.Days()), House, Hx.B1);
        Assert.Same(one, DtekMatcher.Hint(one, "A", Hx.B2, null, House, Kyiv));
        Assert.Same(one, DtekMatcher.Hint(one, "A", Hx.B2, Schedule(Day("2026-10-07", ("A", "18-21:0"))), House, Kyiv));
    }

    // design.md §11 DTEK module, rule 22
    [Fact]
    public void The_view_names_the_queue_its_origin_and_the_cuts_against_it()
    {
        static DtekView View(DtekSnapshot snap, DtekConfig config) => DtekOutlook.Compute(config, snap, false, null, new ManualClock(At("2026-10-07 12:00")), Kyiv);
        DtekSnapshot Snap(DtekHint? hint = null, string? fixedQueue = null) => new() { Schedule = Schedule(Hx.Days()), Match = new DtekMatch { Fixed = fixedQueue }, Hint = hint ?? new DtekHint(), DatasetTriedAt = At("2026-10-07 11:50") };

        var v = View(Snap(), Config(group: "A", queues: House));
        Assert.Equal(("A", "config", "manual", 0), (v.Queue, v.QueueSource, v.QueueOrigin, v.QueueContradictions));
        v = View(Snap(new DtekHint { Group = "A", Candidate = "B", Count = 1 }), Config(group: "A", queues: House));
        Assert.Equal(("A", "manual", 1), (v.Queue, v.QueueOrigin, v.QueueContradictions));
        v = View(Snap(new DtekHint { Group = "A", RefutedBy = "B" }, "B"), Config(group: "A", queues: House));
        Assert.Equal(("B", "matched", "manual-refuted", 0), (v.Queue, v.QueueSource, v.QueueOrigin, v.QueueContradictions));
        Assert.Contains("dtekWatcher.group A does not fit the real cuts", v.Note, StringComparison.Ordinal);
        Assert.Equal(["B"], v.Windows.Select(w => w.Queue));
        // refuted, and the detected queue contradicted since: all the house's queues, the manual one not back
        v = View(Snap(new DtekHint { Group = "A", RefutedBy = "B" }), Config(group: "A", queues: House));
        Assert.Equal((null, "manual-refuted"), (v.Queue, v.QueueOrigin));
        Assert.Equal(House, v.Windows.Select(w => w.Queue));
        // counted against another value: the value set now is a fresh hint
        v = View(Snap(new DtekHint { Group = "A", RefutedBy = "B" }, "B"), Config(group: "C", queues: House));
        Assert.Equal(("C", "manual", 0), (v.Queue, v.QueueOrigin, v.QueueContradictions));
        // no manual queue: as before
        v = View(Snap(new DtekHint { Group = "A", RefutedBy = "B" }, "B"), Config(queues: House));
        Assert.Equal(("B", "matched", "detected", 0), (v.Queue, v.QueueSource, v.QueueOrigin, v.QueueContradictions));
        v = View(Snap(), Config(queues: House));
        Assert.Equal((null, "", "", 0), (v.Queue, v.QueueSource, v.QueueOrigin, v.QueueContradictions));
        Assert.DoesNotContain("dtekWatcher.group", v.Note, StringComparison.Ordinal);
        Assert.Equal(("C", "single", "detected"), (View(Snap(), Config(queues: ["C"])).Queue, View(Snap(), Config(queues: ["C"])).QueueSource, View(Snap(), Config(queues: ["C"])).QueueOrigin));
    }

    // design.md §11 DTEK module, rule 20
    [Fact]
    public void A_manual_queue_that_is_not_among_the_houses_is_followed_and_the_note_says_so()
    {
        var snap = new DtekSnapshot { Schedule = Schedule(Hx.Days()), DatasetTriedAt = At("2026-10-07 11:50") };
        var v = DtekOutlook.Compute(Config(group: "GPV9.9", queues: ["B", "C"]), snap, false, null, new ManualClock(At("2026-10-07 12:00")), Kyiv);
        Assert.Equal(("GPV9.9", "manual", "ok"), (v.Queue, v.QueueOrigin, v.State));
        Assert.Contains("dtekWatcher.group GPV9.9 is not among the house's queues", v.Note, StringComparison.Ordinal);
        Assert.Equal(["B", "C"], v.Queues);
        Assert.Equal(["B", "C", "GPV9.9"], DtekOutlook.Compared(Config(group: "GPV9.9", queues: ["B", "C"]), snap));
        Assert.Equal(["B", "C"], DtekOutlook.Compared(Config(queues: ["B", "C"]), snap));
    }
}

public sealed class DtekWatcherHintServiceTests : IDisposable
{
    private const int Port = 18940;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bluetti-watcher-test-" + Guid.NewGuid().ToString("N"));
    private readonly StationStore _store;
    private readonly ManualClock _clock = new(At("2026-10-07 12:00"));
    private readonly FakeSource _source = new() { Dataset = () => FakeSource.Data(Schedule(Hx.Days())) };

    public DtekWatcherHintServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new StationStore(Path.Combine(_dir, "station.db"));
    }

    public void Dispose()
    {
        _source.Dispose();
        _store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private static DtekConfig Cfg(string? group, string[]? queues = null)
    {
        var c = Config(group, queues ?? House);
        c.Site = false;
        return c;
    }

    private DtekWatcherService Watcher(string? group = "A", string[]? queues = null) =>
        new(new StationConfig { DataDir = _dir, DtekWatcher = Cfg(group, queues) }, () => _source, new DtekStore(_store), _clock, _ => { });

    private async Task<DtekWatcherService> Started(string? group = "A", string[]? queues = null)
    {
        var w = Watcher(group, queues);
        await w.TickAsync(CancellationToken.None);
        return w;
    }

    private List<EventRow> Changed() => _store.Events(0, long.MaxValue).Where(e => e.Kind == "dtek-watcher-queue-changed").ToList();

    /// <summary>A whole cut as the polls see it: on mains a minute before, off, a last reading off a minute before mains is back.</summary>
    private static void Run(DtekWatcherService w, DtekCut cut)
    {
        w.AfterPoll(new StationState { LastAt = cut.Start.AddMinutes(-1), OnSince = cut.Start.AddMinutes(-1) });
        w.AfterPoll(new StationState { LastAt = cut.Start, OffSince = cut.Start });
        w.AfterPoll(new StationState { LastAt = cut.End.AddMinutes(-1), OffSince = cut.Start, Cut = true, CutSince = cut.Start.AddMinutes(5) });
        w.AfterPoll(new StationState { LastAt = cut.End, OnSince = cut.End, MainsBackAt = cut.End });
    }

    private static (string? Queue, string Origin, int Against) Of(DtekWatcherService w) => (w.View(false, null).Queue, w.View(false, null).QueueOrigin, w.View(false, null).QueueContradictions);

    // design.md §11 DTEK module, rule 20
    [Fact]
    public async Task The_manual_queue_stands_until_two_cuts_in_a_row_follow_another_then_one_event_and_the_detected_queue()
    {
        using var w = await Started();
        Assert.Equal(("A", "manual", 0), Of(w));
        Run(w, Hx.B1);
        Assert.Equal(("A", "manual", 1), Of(w));
        Assert.Empty(Changed());
        Run(w, Hx.B2);
        Assert.Equal(("B", "manual-refuted", 0), Of(w));
        Assert.Equal("B", w.Match.Fixed);
        Assert.Equal("A -> B: two cuts in a row followed B, not dtekWatcher.group A", Assert.Single(Changed()).Detail);
        Assert.Equal(["B"], w.View(false, null).Windows.Select(q => q.Queue));
        Run(w, Hx.C2);   // the detected queue is the search's from here on: a cut of another opens the question, with no second event
        Assert.Equal((null, "manual-refuted", 0), Of(w));
        Assert.Single(Changed());
    }

    // design.md §11 DTEK module, rule 20
    [Fact]
    public async Task Cuts_that_disagree_or_an_agreeing_cut_between_them_change_nothing()
    {
        using (var w = await Started())
        {
            Run(w, Hx.B1);
            Run(w, Hx.C2);
            Assert.Equal(("A", "manual", 1), Of(w));
        }
        _store.SetMeta("dtekWatcher", "{}");
        using var again = await Started();
        Run(again, Hx.B1);
        Run(again, Hx.A1);
        Assert.Equal(("A", "manual", 0), Of(again));
        Run(again, Hx.B2);
        Assert.Equal(("A", "manual", 1), Of(again));
        Assert.Empty(Changed());
    }

    // design.md §11 DTEK module, rule 21
    [Fact]
    public async Task A_restart_between_the_two_cuts_keeps_the_first_one_counted()
    {
        using (var w = await Started()) Run(w, Hx.B1);
        using var again = Watcher();
        Assert.Equal(("A", "manual", 1), Of(again));
        Run(again, Hx.B2);
        Assert.Equal(("B", "manual-refuted", 0), Of(again));
        Assert.Single(Changed());
    }

    // design.md §11 DTEK module, rule 21
    [Fact]
    public async Task The_refutation_survives_a_restart_for_the_same_value_and_an_edited_value_is_a_fresh_hint()
    {
        using (var w = await Started())
        {
            Run(w, Hx.B1);
            Run(w, Hx.B2);
        }
        using (var same = Watcher()) Assert.Equal(("B", "manual-refuted", 0), Of(same));
        using (var edited = await Started("C"))
        {
            Assert.Equal(("C", "manual", 0), Of(edited));
            _clock.Now = At("2026-10-07 12:30");
            await edited.TickAsync(CancellationToken.None);   // saved with the new value: the old refutation is gone
        }
        using var back = Watcher();
        Assert.Equal(("A", "manual", 0), Of(back));
        Assert.Single(Changed());
    }

    // design.md §11 DTEK module, rule 21
    [Fact]
    public async Task One_cut_counted_against_a_value_does_not_count_against_the_next_one()
    {
        using (var w = await Started()) Run(w, Hx.B1);
        using var edited = Watcher("C");
        Assert.Equal(("C", "manual", 0), Of(edited));
        Run(edited, Hx.B2);
        Assert.Equal(("C", "manual", 1), Of(edited));
        Assert.Empty(Changed());
    }

    // design.md §11 DTEK module, rule 20
    [Fact]
    public async Task A_manual_queue_outside_the_houses_queues_is_followed_agreed_with_and_refuted_like_any_other()
    {
        using var w = await Started("A", ["B", "C"]);
        Assert.Equal(("A", "manual", 0), Of(w));
        Run(w, Hx.B1);
        Run(w, Hx.A1);   // its own window: the row ends
        Assert.Equal(("A", "manual", 0), Of(w));
        Run(w, Hx.A2);
        Run(w, Hx.B2);
        Assert.Equal(("A", "manual", 1), Of(w));
        Run(w, Hx.C2);
        Assert.Equal(("A", "manual", 1), Of(w));
        Assert.Empty(Changed());
    }

    // design.md §11 DTEK module, rule 20
    [Fact]
    public async Task Without_a_manual_queue_nothing_is_counted_and_the_search_is_as_before()
    {
        using var w = await Started(group: null);
        Run(w, Hx.B1);
        Assert.Equal((null, "", 0), Of(w));
        Run(w, Hx.B2);
        Assert.Equal(("B", "detected", 0), Of(w));
        Assert.Equal("matched", w.View(false, null).QueueSource);
        Assert.Empty(Changed());
        using var doc = JsonDocument.Parse(_store.GetMeta("dtekWatcher")!);
        var hint = doc.RootElement.GetProperty("hint");
        Assert.Equal(("", 0, JsonValueKind.Null), (hint.GetProperty("group").GetString(), hint.GetProperty("count").GetInt32(), hint.GetProperty("refutedBy").ValueKind));
    }

    // design.md §11 DTEK module, rule 21
    [Theory]
    [InlineData("""{"hint":null}""")]
    [InlineData("""{"hint":{}}""")]
    [InlineData("""{"hint":{"group":"A","candidate":"B","count":-3}}""")]
    [InlineData("""{"hint":{"group":"A","candidate":"B","count":2}}""")]
    [InlineData("""{"hint":{"group":"A","candidate":null,"count":1}}""")]
    [InlineData("""{"hint":{"group":"A","refutedBy":""}}""")]
    [InlineData("""{"hint":{"group":"A","refutedBy":"QQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQ"}}""")]
    [InlineData("""{"hint":{"group":null,"refutedBy":"B"}}""")]
    [InlineData("""{"hint":7}""")]
    [InlineData("""{"hint":{"group":"A","count":"many"}}""")]
    [InlineData("""{"hint":{"group":"A","refutedBy":"B"}""")]
    public void A_damaged_or_missing_saved_refutation_falls_back_to_the_manual_queue(string saved)
    {
        _store.SetMeta("dtekWatcher", saved);
        using var w = Watcher();
        Assert.Equal(("A", "manual", 0), Of(w));
        Run(w, Hx.B1);   // and it takes cuts from there without a throw
        Assert.Equal("A", w.View(false, null).Queue);
    }

    // design.md §11 DTEK module, rule 22
    [Fact]
    public async Task Api_now_carries_the_queue_its_origin_and_the_cuts_against_it()
    {
        var cfg = new StationConfig { DataDir = _dir, Port = Port, DtekWatcher = Cfg("A") };
        using var watcher = new DtekWatcherService(cfg, () => _source, new DtekStore(_store), _clock, _ => { });
        await watcher.TickAsync(CancellationToken.None);
        Run(watcher, Hx.B1);
        async Task<(string?, string?, int)> Now()
        {
            using var service = new StationService(cfg, new QueueReader(), new FakeActions(), new FakeHibernator(true), new FixedFlag(Fx.Station), _store, _clock, _ => { });
            var api = new StationApi(cfg, service, _store, _clock, _clock.Now, watcher);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Host"] = $"127.0.0.1:{Port}" };
            var r = await api.HandleAsync(new HttpRequest("GET", "/api/now", new Dictionary<string, string>(), headers, []), CancellationToken.None);
            using var doc = JsonDocument.Parse(r.Body);
            var w = doc.RootElement.GetProperty("dtekWatcher");
            return (w.GetProperty("queue").GetString(), w.GetProperty("queueOrigin").GetString(), w.GetProperty("queueContradictions").GetInt32());
        }
        Assert.Equal(("A", "manual", 1), await Now());
        Run(watcher, Hx.B2);
        Assert.Equal(("B", "manual-refuted", 0), await Now());
    }
}
