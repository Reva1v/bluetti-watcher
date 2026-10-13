using System.Text.Json;
using BluettiWatcher.Decision;
using BluettiWatcher.DtekWatcher;
using BluettiWatcher.Storage;
using static BluettiWatcher.Tests.DtekWatcher.Dx;

namespace BluettiWatcher.Tests.DtekWatcher;

/// <summary>The house on a set of queues (dtekWatcher.group as an array): the union of their planned windows (design.md §11 rule 28).</summary>
public sealed class DtekWatcherQueueSetTests : IDisposable
{
    private const string Set = "GPV4.1+GPV6.2";
    private static readonly string[] Houses = ["GPV4.1", "GPV6.2", "GPV2.2"];
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bluetti-watcher-test-" + Guid.NewGuid().ToString("N"));
    private readonly StationStore _store;
    private readonly ManualClock _clock = new(At("2026-10-07 12:00"));
    private readonly FakeSource _source = new();

    public DtekWatcherQueueSetTests()
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

    /// <summary>The evening of 07.10.2026: 4.1 17:00-21:00, 6.2 20:30-24:00, 2.2 09:00-12:00; 08.10: 4.1 08:00-12:00, 6.2 04:00-06:00, 2.2 18:00-20:00.</summary>
    private static DtekDay[] Evening() =>
    [
        Day("2026-10-07", ("GPV4.1", "18-21:0"), ("GPV6.2", "21:s 22-24:0"), ("GPV2.2", "10-12:0")),
        Day("2026-10-08", ("GPV4.1", "9-12:0"), ("GPV6.2", "5-6:0"), ("GPV2.2", "19-20:0")),
    ];

    private static DtekSnapshot Snap(DtekOpenCut? open = null) => new()
    {
        Schedule = Schedule(Evening()), House = new DtekHouse { Queues = Houses }, DatasetTriedAt = At("2026-10-07 11:50"), OpenCut = open,
    };

    private static DtekView View(DtekSnapshot snap, string now, bool cut = false, string group = Set) =>
        DtekOutlook.Compute(Config(group: group, queues: Houses), snap, cut, 3, new ManualClock(At(now)), Kyiv, 3);

    // design.md §11 DTEK module, rule 28 (b)
    [Fact]
    public void The_union_merges_overlapping_and_touching_windows_and_keeps_apart_ones_apart()
    {
        var s = Schedule(Evening());
        Assert.Equal(
            [new DtekWindow(At("2026-10-07 17:00"), At("2026-10-08 00:00")), new DtekWindow(At("2026-10-08 04:00"), At("2026-10-08 06:00")), new DtekWindow(At("2026-10-08 08:00"), At("2026-10-08 12:00"))],
            DtekTime.Windows(s, Set, DtekSlot.Off, Kyiv));
        Assert.Equal("17:00-24:00", DtekTime.DayText(s, Set, new DateTime(2026, 10, 7), Kyiv));
        var touching = Schedule(Day("2026-10-07", ("A", "18-19:0"), ("B", "20-21:0")));       // 17:00-19:00 and 19:00-21:00
        Assert.Equal([new DtekWindow(At("2026-10-07 17:00"), At("2026-10-07 21:00"))], DtekTime.Windows(touching, "A+B", DtekSlot.Off, Kyiv));
        // a single queue is read as before
        Assert.Equal([new DtekWindow(At("2026-10-07 17:00"), At("2026-10-07 21:00"))], DtekTime.Windows(s, "GPV4.1", DtekSlot.Off, Kyiv).Take(1));
    }

    // design.md §11 DTEK module, rule 28 (b)
    [Theory]
    [InlineData("10:m", "10:1", "possible 09:00-10:00")]
    [InlineData("10:?", "10:m", "unknown 09:00-10:00")]
    [InlineData("10:0", "10:?", "09:00-10:00")]
    [InlineData("10:f", "10:s", "09:00-10:00")]
    [InlineData("10:a", "10:s", "possible 09:00-09:30, 09:30-10:00")]
    public void A_half_hour_of_the_union_is_off_when_any_member_is_else_unknown_else_possible(string a, string b, string text) =>
        Assert.Equal(text, DtekTime.DayText(Schedule(Day("2026-10-07", ("A", a), ("B", b))), "A+B", new DateTime(2026, 10, 7), Kyiv));

    // design.md §11 DTEK module, rule 28 (b)
    [Fact]
    public void A_day_without_one_member_has_no_union_nothing_is_guessed()
    {
        var s = Schedule(Day("2026-10-07", ("A", "18-21:0")));
        Assert.Equal("", DtekTime.DayText(s, "A+B", new DateTime(2026, 10, 7), Kyiv));
        Assert.Empty(DtekTime.Windows(s, "A+B", DtekSlot.Off, Kyiv));
    }

    // design.md §11 DTEK module, rule 28 (a)
    [Theory]
    [InlineData("\"GPV4.1\"", "GPV4.1", "GPV4.1")]
    [InlineData("[\"GPV4.1\", \"GPV6.2\"]", "GPV4.1+GPV6.2", "GPV4.1+GPV6.2")]
    [InlineData("[\"GPV4.1\"]", "GPV4.1", "GPV4.1")]
    [InlineData("[\" GPV4.1 \", \"GPV6.2\", \"GPV4.1\", \"\"]", "GPV4.1+GPV6.2", "GPV4.1+GPV6.2")]
    [InlineData("[]", null, null)]
    [InlineData("null", null, null)]
    public void Group_takes_a_queue_or_an_array_of_them(string json, string? group, string? manual)
    {
        string file = Path.Combine(_dir, "config.json");
        File.WriteAllText(file, $$"""{ "dtekWatcher": { "enabled": true, "queues": ["GPV4.1", "GPV6.2"], "group": {{json}} } }""");
        var c = StationConfig.Load(["--config", file]).DtekWatcher;
        Assert.Equal((group, manual), (c.Group, DtekOutlook.Manual(c)));
        Assert.Null(c.Problem());
    }

    // design.md §11 DTEK module, rule 28 (a): a wrong value turns the module off with the reason, like any bad module value (design.md §11 rule 8); the service loads
    [Theory]
    [InlineData("[4.1, 6.2]")]
    [InlineData("[\"GPV4.1\", null]")]
    [InlineData("[\"GPV4.1\", [\"GPV6.2\"]]")]
    [InlineData("{ \"a\": 1 }")]
    [InlineData("4.1")]
    [InlineData("true")]
    public void A_group_that_is_neither_a_name_nor_names_turns_the_module_off_not_the_service(string json)
    {
        string file = Path.Combine(_dir, "config.json");
        File.WriteAllText(file, $$"""{ "dtekWatcher": { "enabled": true, "group": {{json}}, "site": false }, "port": 18977 }""");
        var cfg = StationConfig.Load(["--config", file]);
        Assert.Equal(18977, cfg.Port);                                   // the rest of the file is read
        Assert.False(cfg.DtekWatcher.Site);                              // the module's own keys after the group too
        Assert.Equal("dtekWatcher.group must be a queue name or an array of queue names", cfg.DtekWatcher.Problem());
        var v = DtekOutlook.Compute(cfg.DtekWatcher, new DtekSnapshot(), false, null, _clock, Kyiv);
        Assert.Equal(("off", "dtekWatcher.group must be a queue name or an array of queue names"), (v.State, v.Note));
    }
    // design.md §11 DTEK module, rule 28 (a)
    [Fact]
    public void A_set_of_more_than_four_queues_turns_the_module_off_with_the_reason()
    {
        var c = Config(group: "GPV1.1+GPV4.2+GPV2.1+GPV2.2+GPV3.1");
        Assert.Equal("dtekWatcher.group takes at most 4 queues of at most 16 characters", c.Problem());
        Assert.Equal("off", DtekOutlook.Compute(c, new DtekSnapshot(), false, null, _clock, Kyiv).State);
        Assert.Null(Config(group: "GPV1.1+GPV4.2+GPV2.1+GPV2.2").Problem());
    }

    // design.md §11 DTEK module, rule 28 (c)
    [Fact]
    public void The_set_is_followed_and_its_next_outage_is_the_union_with_the_queues_it_came_from()
    {
        var v = View(Snap(), "2026-10-07 12:00");
        Assert.Equal((Set, "config", "manual"), (v.Queue, v.QueueSource, v.QueueOrigin));
        Assert.Equal(["GPV4.1", "GPV6.2"], v.QueueSet);
        Assert.Equal((At("2026-10-07 17:00"), At("2026-10-08 00:00"), Set), (v.NextStart!.Value, v.NextEnd!.Value, v.NextQueue));
        Assert.Equal(["GPV4.1", "GPV6.2"], v.NextQueues);
        var day = Assert.Single(v.Windows);
        Assert.Equal((Set, "17:00-24:00", "04:00-06:00, 08:00-12:00"), (day.Queue, day.Today, day.Tomorrow));
        Assert.Equal(["GPV4.1", "GPV6.2"], day.Intervals[0].Queues);
        Assert.Equal(["GPV6.2"], day.Intervals[1].Queues);
        Assert.Equal(["GPV4.1"], day.Intervals[2].Queues);
        Assert.DoesNotContain("dtekWatcher.group", v.Note, StringComparison.Ordinal);
        // a single queue names itself
        var one = View(Snap(), "2026-10-07 12:00", group: "GPV4.1");
        Assert.Equal(["GPV4.1"], one.QueueSet);
        Assert.Equal(["GPV4.1"], Assert.Single(one.Windows).Intervals[0].Queues);
    }

    // design.md §11 DTEK module, rule 28 (c)
    [Fact]
    public void A_member_outside_the_house_is_named_in_the_note()
    {
        var v = View(Snap(), "2026-10-07 12:00", group: "GPV4.1+GPV9.9");
        Assert.Contains("dtekWatcher.group GPV9.9 is not among the house's queues", v.Note, StringComparison.Ordinal);
    }

    // design.md §11 DTEK module, rule 28 (d) - the cut of 07.10.2026: from 17:00 to 00:38, past both lines
    [Theory]
    [InlineData("2026-10-07 23:30", false, null)]
    [InlineData("2026-10-08 00:00", true, 0)]
    [InlineData("2026-10-08 00:38", true, 38)]
    public void A_cut_is_overdue_only_once_it_outlasts_the_union(string now, bool overdue, int? minutes)
    {
        var v = View(Snap(new DtekOpenCut { Start = At("2026-10-07 17:02"), StartKnown = true }), now, cut: true);
        Assert.Equal((overdue, minutes, At("2026-10-08 00:00")), (v.Overdue, v.OverdueMin, v.PlannedReturn!.Value));
        Assert.Equal(overdue ? null : At("2026-10-08 00:00"), v.Return);
        // against one queue of the set it would have been overdue at 21:00
        Assert.True(View(Snap(new DtekOpenCut { Start = At("2026-10-07 17:02"), StartKnown = true }), "2026-10-07 23:30", cut: true, group: "GPV4.1").Overdue);
    }

    // design.md §11 DTEK module, rule 28 (e)
    [Fact]
    public void The_set_is_a_hint_like_one_queue_a_cut_that_fits_the_union_ends_the_row_two_for_another_queue_refute_it()
    {
        var s = Schedule(Evening());
        var union = Cut("2026-10-07 17:05", "2026-10-07 23:50");
        var only41 = Cut("2026-10-07 17:05", "2026-10-07 21:05");     // one line of the set alone: the house followed 4.1, not the union
        var other = Cut("2026-10-07 09:05", "2026-10-07 12:10");      // 2.2
        var h = DtekMatcher.Hint(null, Set, union, s, Houses, Kyiv);
        Assert.Equal((Set, null, 0, null), (h.Group, h.Candidate, h.Count, h.RefutedBy));
        h = DtekMatcher.Hint(h, Set, only41, s, Houses, Kyiv);
        Assert.Equal(("GPV4.1", 1), (h.Candidate, h.Count));
        h = DtekMatcher.Hint(h, Set, union, s, Houses, Kyiv);           // ends the row
        Assert.Equal((null, 0), (h.Candidate, h.Count));
        h = DtekMatcher.Hint(DtekMatcher.Hint(h, Set, other, s, Houses, Kyiv), Set, other, s, Houses, Kyiv);
        Assert.Equal("GPV2.2", h.RefutedBy);
        // the 00:38 return fits no window: an unplanned cut says nothing
        Assert.Equal(0, DtekMatcher.Hint(null, Set, Cut("2026-10-07 17:05", "2026-10-08 00:38"), s, Houses, Kyiv).Count);
    }

    // design.md §11 DTEK module, rule 28 (e)
    [Fact]
    public void A_saved_set_hint_survives_a_restart_and_an_edited_set_is_a_fresh_hint()
    {
        var hint = DtekHint.Sound(new DtekHint { Group = "GPV4.1+GPV6.2+GPV2.2", Candidate = "GPV1.1", Count = 1 });
        Assert.Equal(("GPV4.1+GPV6.2+GPV2.2", "GPV1.1", 1), (hint.Group, hint.Candidate, hint.Count));
        Assert.Equal("", DtekHint.Sound(new DtekHint { Group = new string('Q', 17) + "+GPV6.2", Candidate = "GPV1.1", Count = 1 }).Group);
        Assert.Equal(0, DtekHint.For(hint, "GPV4.1+GPV6.2").Count);
    }

    // ---- the service: lateness against the union ----

    private DtekWatcherService Watcher(string group)
    {
        var c = Config(group: group, queues: Houses);
        c.Site = false;
        return new(new StationConfig { DataDir = _dir, DtekWatcher = c }, () => _source, new DtekStore(_store), _clock, _ => { });
    }

    private static void Through(DtekWatcherService w, string start, string back)
    {
        w.AfterPoll(new StationState { LastAt = At(start).AddMinutes(-1), OnSince = At(start).AddMinutes(-1) });
        w.AfterPoll(new StationState { LastAt = At(start), OffSince = At(start), Cut = true, CutSince = At(start).AddMinutes(5) });
        w.AfterPoll(new StationState { LastAt = At(back).AddMinutes(-1), OffSince = At(start), Cut = true, CutSince = At(start).AddMinutes(5) });
        w.AfterPoll(new StationState { LastAt = At(back), OnSince = At(back), MainsBackAt = At(back) });
    }

    private List<DtekLate> Saved() => JsonSerializer.Deserialize(_store.GetMeta("dtekWatcherLateness") ?? "[]", StationJson.Default.ListDtekLate)!;

    // design.md §11 DTEK module, rule 28 (f)
    [Fact]
    public async Task With_a_set_the_lateness_is_recorded_against_the_union_and_says_so()
    {
        _source.Datasets.Enqueue(FakeSource.Data(Schedule(Evening())));
        using (var w = Watcher(Set))
        {
            await w.TickAsync(CancellationToken.None);
            Through(w, "2026-10-07 17:06", "2026-10-08 00:38");
        }
        var late = Assert.Single(Saved());
        Assert.Equal((Set, true, "07.10.2026", 6, 38), (late.Queue, late.Union, late.Date, late.StartDelta, late.EndDelta));
        using var again = Watcher(Set);
        Assert.Equal(1, again.View(false, null).LateCuts);   // the entry loads back
    }

    // design.md §11 DTEK module, rule 28 (f)
    [Fact]
    public async Task With_one_queue_the_record_is_as_before_and_not_a_union()
    {
        _source.Datasets.Enqueue(FakeSource.Data(Schedule(Evening())));
        using var w = Watcher("GPV4.1");
        await w.TickAsync(CancellationToken.None);
        Through(w, "2026-10-07 17:06", "2026-10-07 21:30");
        var late = Assert.Single(Saved());
        Assert.Equal(("GPV4.1", false, 30), (late.Queue, late.Union, late.EndDelta));
    }
}
