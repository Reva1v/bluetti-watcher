using System.Text.Json;
using BluettiWatcher.Decision;
using BluettiWatcher.DtekWatcher;
using BluettiWatcher.Storage;
using static BluettiWatcher.Tests.DtekWatcher.Dx;

namespace BluettiWatcher.Tests.DtekWatcher;

/// <summary>Overdue cuts, the lateness of finished cuts and plan edits during a cut (design.md §11 rules 24-27).</summary>
public sealed class DtekWatcherOverdueTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bluetti-watcher-test-" + Guid.NewGuid().ToString("N"));
    private readonly StationStore _store;
    private readonly ManualClock _clock = new(At("2026-10-07 12:00"));
    private readonly FakeSource _source = new();

    public DtekWatcherOverdueTests()
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

    // ---- the outlook, pure ----

    /// <summary>Queue A followed; a cut running since <paramref name="cutStart"/>.</summary>
    private static DtekSnapshot Running(string cutStart, DtekSchedule? s = null, DtekSchedule? held = null) => new()
    {
        Schedule = s ?? TwoDays(),
        House = new DtekHouse { Queues = House },
        Match = new DtekMatch { Fixed = "A" },
        DatasetTriedAt = At("2026-10-07 11:50"),
        OpenCut = new DtekOpenCut { Start = At(cutStart), StartKnown = true, Held = held },
    };

    /// <summary><paramref name="toHibernate"/> defaults to <paramref name="hoursLeft"/>.</summary>
    private static DtekView View(DtekSnapshot snap, string now, bool cut = true, double? hoursLeft = 2, double? toHibernate = null) =>
        DtekOutlook.Compute(Config(), snap, cut, hoursLeft, new ManualClock(At(now)), Kyiv, toHibernate ?? hoursLeft);

    // design.md §11 DTEK module, rule 24
    [Fact]
    public void The_battery_time_runs_to_the_hibernate_level_not_to_empty()
    {
        var v = View(Running("2026-10-07 17:06"), "2026-10-07 22:00", hoursLeft: 2, toHibernate: 1.5);
        Assert.Equal(At("2026-10-07 23:30"), v.BatteryUntil);
        Assert.Null(DtekOutlook.Compute(Config(), Running("2026-10-07 17:06"), true, 2, new ManualClock(At("2026-10-07 22:00")), Kyiv).BatteryUntil);   // not given: unknown
    }

    // design.md §11 DTEK module, rule 24
    [Fact]
    public void Once_the_window_the_cut_belonged_to_has_ended_the_cut_is_overdue_and_the_battery_alone_says_how_long()
    {
        var v = View(Running("2026-10-07 17:06"), "2026-10-07 22:00", hoursLeft: 2);
        Assert.True(v.Overdue);
        Assert.Equal(60, v.OverdueMin);
        Assert.Equal(At("2026-10-07 21:00"), v.PlannedReturn);
        Assert.Null(v.Return);                                   // the plan gives no return any more
        Assert.Equal(At("2026-10-08 00:00"), v.BatteryUntil);
        Assert.Equal("unknown", v.BatteryLasts);                 // nothing to compare the battery with
    }

    // design.md §11 DTEK module, rule 24
    [Theory]
    [InlineData("2026-10-07 20:59", false, null)]
    [InlineData("2026-10-07 21:00", true, 0)]
    [InlineData("2026-10-07 21:01", true, 1)]
    public void Overdue_begins_at_the_planned_end_and_not_before(string now, bool overdue, int? minutes)
    {
        var v = View(Running("2026-10-07 17:06"), now);
        Assert.Equal((overdue, minutes), (v.Overdue, v.OverdueMin));
        Assert.Equal(At("2026-10-07 21:00"), v.PlannedReturn);
        Assert.Equal(overdue ? null : At("2026-10-07 21:00"), v.Return);
    }

    // design.md §11 DTEK module, rule 24
    [Fact]
    public void A_window_known_only_from_the_schedule_held_at_the_start_is_not_overdue_before_its_end()
    {
        var tomorrowOnly = Updated("2026-10-07 18:00", "2026-10-07 18:05", "dataset", TwoDayCells()[1]);   // a source that answered 08.10 alone
        Assert.False(View(Running("2026-10-07 17:06", tomorrowOnly, TwoDays()), "2026-10-07 20:00").Overdue);
        Assert.Equal(30, View(Running("2026-10-07 17:06", tomorrowOnly, TwoDays()), "2026-10-07 21:30").OverdueMin);
    }

    // design.md §11 DTEK module, rule 24
    [Fact]
    public void Not_overdue_outside_a_cut_without_a_running_cut_or_for_a_cut_that_began_in_no_window()
    {
        var off = View(Running("2026-10-07 17:06"), "2026-10-07 22:00", cut: false);
        Assert.Equal((false, null, null, null), (off.Overdue, off.OverdueMin, off.PlannedReturn, off.BatteryUntil));
        var noOpen = Running("2026-10-07 17:06");
        noOpen.OpenCut = null;
        Assert.False(View(noOpen, "2026-10-07 22:00").Overdue);
        // began at 13:00, four hours before A's window: an unplanned cut
        Assert.False(View(Running("2026-10-07 13:00"), "2026-10-07 22:00").Overdue);
        // began 20 minutes before the window: still its cut
        Assert.True(View(Running("2026-10-07 16:40"), "2026-10-07 22:00").Overdue);
        Assert.False(View(Running("2026-10-07 16:39"), "2026-10-07 22:00").Overdue);
    }

    // design.md §11 DTEK module, rule 24
    [Fact]
    public void A_new_window_that_covers_now_is_followed_again()
    {
        var s = Schedule(Day("2026-10-07", ("A", "18-21:0 23-24:0")), TwoDayCells()[1]);   // 17:00-21:00, then 22:00-24:00
        var v = View(Running("2026-10-07 17:06", s), "2026-10-07 21:30");
        Assert.Equal((true, 30), (v.Overdue, v.OverdueMin));
        v = View(Running("2026-10-07 17:06", s), "2026-10-07 21:45");                     // 22:00 less 20 minutes has come
        Assert.False(v.Overdue);
        Assert.Equal((At("2026-10-08 00:00"), At("2026-10-08 00:00")), (v.Return!.Value, v.PlannedReturn!.Value));
    }

    // design.md §11 DTEK module, rule 24
    [Theory]
    [InlineData("18-24:0")]                // 17:00-21:00 and 20:30-24:00 touch: one window to 24:00
    [InlineData("21:s 22-24:0")]           // the window moved to 20:30-24:00 and covers now
    public void Touching_or_moved_windows_give_the_later_return(string cells)
    {
        var s = Schedule(Day("2026-10-07", ("A", cells)), TwoDayCells()[1]);
        var v = View(Running("2026-10-07 17:06", s), "2026-10-07 22:00", hoursLeft: 1);
        Assert.False(v.Overdue);
        Assert.Equal(At("2026-10-08 00:00"), v.Return);
        Assert.Equal("no", v.BatteryLasts);
    }

    // design.md §11 DTEK module, rule 24
    [Fact]
    public void A_cut_spanning_midnight_keeps_its_window_from_the_schedule_held_at_its_start()
    {
        var held = Schedule(Day("2026-10-07", ("A", "23-24:0")), Day("2026-10-08", ("A", "1-2:0")));          // 22:00-02:00
        var now = Updated("2026-10-08 00:10", "2026-10-08 00:15", "dataset", Day("2026-10-08", ("A", "1-2:0")), Day("2026-10-09", ("A", "10:0")));
        var v = View(Running("2026-10-07 22:05", now, held), "2026-10-08 01:00");
        Assert.Equal((false, At("2026-10-08 02:00")), (v.Overdue, v.Return!.Value));
        v = View(Running("2026-10-07 22:05", now, held), "2026-10-08 03:00");
        Assert.Equal((true, 60, At("2026-10-08 02:00")), (v.Overdue, v.OverdueMin, v.PlannedReturn!.Value));
    }

    // design.md §11 DTEK module, rule 24
    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void A_missing_zero_or_unreadable_estimate_gives_no_battery_time(double? hoursLeft)
    {
        var v = View(Running("2026-10-07 17:06"), "2026-10-07 22:00", hoursLeft: hoursLeft);
        Assert.True(v.Overdue);
        Assert.Null(v.BatteryUntil);
        // while the window still covers now: an empty battery does not last; any other of these is unknown
        v = View(Running("2026-10-07 17:06"), "2026-10-07 18:00", hoursLeft: hoursLeft);
        Assert.Equal((false, At("2026-10-07 21:00")), (v.Overdue, v.Return!.Value));
        Assert.Equal((hoursLeft == 0 ? "no" : "unknown", (DateTimeOffset?)null), (v.BatteryLasts, v.BatteryUntil));
    }

    // design.md §11 DTEK module, rule 24
    [Fact]
    public void A_huge_estimate_is_bounded_and_never_throws()
    {
        var v = View(Running("2026-10-07 17:06"), "2026-10-07 22:00", hoursLeft: 1e300);
        Assert.Equal(At("2026-10-07 22:00").AddHours(DtekOutlook.MaxHours), v.BatteryUntil);
        Assert.Equal("yes", View(Running("2026-10-07 17:06"), "2026-10-07 18:00", hoursLeft: 1e300).BatteryLasts);
    }

    private static DtekLate Late(int endDelta) => new() { Start = At("2026-10-07 17:00").AddMinutes(endDelta + 100_000), End = At("2026-10-07 21:00").AddMinutes(endDelta + 100_000), Queue = "A", Date = "07.10.2026", EndDelta = endDelta };

    // design.md §11 DTEK module, rule 25
    [Fact]
    public void The_usual_lateness_is_the_median_end_delta_and_only_from_three_cuts()
    {
        var snap = Running("2026-10-07 17:06");
        snap.Late = [Late(10), Late(40)];
        var v = View(snap, "2026-10-07 18:00");
        Assert.Equal((null, 2), (v.LateBackMin, v.LateCuts));
        snap.Late = [Late(10), Late(-5), Late(40)];
        Assert.Equal(10, View(snap, "2026-10-07 18:00").LateBackMin);
        snap.Late = [Late(10), Late(21), Late(30), Late(40)];
        Assert.Equal((26, 4), (View(snap, "2026-10-07 18:00").LateBackMin, View(snap, "2026-10-07 18:00").LateCuts));   // 25.5 away from zero
        snap.Late = [Late(-30), Late(-20), Late(-21)];
        Assert.Equal(-21, View(snap, "2026-10-07 18:00", cut: false).LateBackMin);   // shown outside a cut too
    }

    // ---- the service ----

    private DtekWatcherService Watcher(string[]? queues = null)
    {
        var c = Config(queues: queues ?? ["A"]);
        c.Site = false;
        return new(new StationConfig { DataDir = _dir, DtekWatcher = c }, () => _source, new DtekStore(_store), _clock, _ => { });
    }

    private async Task TickAt(DtekWatcherService w, string wall)
    {
        _clock.Now = At(wall);
        await w.TickAsync(CancellationToken.None);
    }

    private void Plan(string update, params DtekDay[] days) =>
        _source.Datasets.Enqueue(FakeSource.Data(Updated(update, update, "dataset", days), $"\"{update}\""));

    private static StationState On(string at) => new() { LastAt = At(at), OnSince = At(at) };
    private static StationState OffAt(string since, string at) => new() { LastAt = At(at), OffSince = At(since), Cut = true, CutSince = At(since).AddMinutes(5) };
    private static StationState Back(string at) => new() { LastAt = At(at), OnSince = At(at), MainsBackAt = At(at) };

    private static void Begin(DtekWatcherService w, string start)
    {
        w.AfterPoll(On(At(start).AddMinutes(-1).ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)));
        w.AfterPoll(OffAt(start, start));
    }

    private static void End(DtekWatcherService w, string start, string back)
    {
        w.AfterPoll(OffAt(start, At(back).AddMinutes(-1).ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)));
        w.AfterPoll(Back(back));
    }

    private List<DtekLate> Saved() => JsonSerializer.Deserialize(_store.GetMeta("dtekWatcherLateness") ?? "[]", StationJson.Default.ListDtekLate)!;

    // design.md §11 DTEK module, rules 24, 25, 26 - the evening of 07.10.2026
    [Fact]
    public async Task The_window_extended_then_shortened_below_now_is_overdue_at_the_shortened_end_and_counts_its_edits()
    {
        using (var w = Watcher())
        {
            await TickAt(w, "2026-10-07 12:00");                               // A 17:00-21:00
            Begin(w, "2026-10-07 17:06");
            Assert.Equal(0, w.View(true, 3).PlanChanges);
            Plan("2026-10-07 18:00", Day("2026-10-07", ("A", "18-24:0")), TwoDayCells()[1]);   // 17:00-24:00
            await TickAt(w, "2026-10-07 18:00");
            _clock.Now = At("2026-10-07 21:30");
            Assert.False(w.View(true, 3).Overdue);
            Plan("2026-10-07 21:35", TwoDayCells());                            // back to 17:00-21:00 with the cut still on
            await TickAt(w, "2026-10-07 21:40");
            var v = w.View(true, 3);
            Assert.Equal((true, 40, At("2026-10-07 21:00"), 2), (v.Overdue, v.OverdueMin, v.PlannedReturn!.Value, v.PlanChanges));
            Assert.Equal(2, _store.Events(0, long.MaxValue).Count(e => e.Kind == "dtek-watcher-changed"));
        }
        // a restart during the cut: overdue recomputed, the count kept
        using (var again = Watcher())
        {
            _clock.Now = At("2026-10-07 22:00");
            var v = again.View(true, 3);
            Assert.Equal((true, 60, 2), (v.Overdue, v.OverdueMin, v.PlanChanges));
            End(again, "2026-10-07 17:06", "2026-10-07 22:30");
            Assert.Equal(0, again.View(false, null).PlanChanges);
            End(again, "2026-10-07 17:06", "2026-10-07 22:30");                 // the same return heard twice
        }
        var late = Assert.Single(Saved());
        Assert.Equal(("A", "07.10.2026", 6, 90, 6, 90), (late.Queue, late.Date, late.StartDelta, late.EndDelta, late.FirstStartDelta, late.FirstEndDelta));
        using var third = Watcher();
        Assert.Equal(1, third.View(false, null).LateCuts);
    }

    // design.md §11 DTEK module, rule 25
    [Fact]
    public async Task Both_windows_are_kept_the_one_at_the_end_and_the_one_seen_at_the_start()
    {
        using var w = Watcher();
        Plan("2026-10-07 11:50", Day("2026-10-07", ("A", "18-20:0")), TwoDayCells()[1]);   // 17:00-20:00
        await TickAt(w, "2026-10-07 12:00");
        Begin(w, "2026-10-07 16:55");
        Plan("2026-10-07 18:00", TwoDayCells());                                            // 17:00-21:00
        await TickAt(w, "2026-10-07 18:00");
        End(w, "2026-10-07 16:55", "2026-10-07 22:30");
        var late = Assert.Single(Saved());
        Assert.Equal((-5, 90, -5, 150), (late.StartDelta, late.EndDelta, late.FirstStartDelta, late.FirstEndDelta));
    }

    // design.md §11 DTEK module, rule 25
    [Fact]
    public async Task A_cut_that_ends_before_the_plan_has_a_negative_delta_and_an_unplanned_cut_is_not_counted()
    {
        using var w = Watcher();
        await TickAt(w, "2026-10-07 12:00");
        Begin(w, "2026-10-07 13:00");
        End(w, "2026-10-07 13:00", "2026-10-07 14:00");                         // no window at 13:00
        Assert.Empty(Saved());
        Begin(w, "2026-10-07 17:02");
        End(w, "2026-10-07 17:02", "2026-10-07 19:30");
        var late = Assert.Single(Saved());
        Assert.Equal((2, -90), (late.StartDelta, late.EndDelta));
    }

    // design.md §11 DTEK module, rule 25
    [Fact]
    public async Task A_cut_closed_twice_with_a_known_start_is_one_entry()
    {
        using (var w = Watcher())
        {
            await TickAt(w, "2026-10-07 12:00");
            Begin(w, "2026-10-07 17:06");
        }
        string running = _store.GetMeta("dtekWatcher")!;                       // the open cut, its start known
        using (var w = Watcher()) End(w, "2026-10-07 17:06", "2026-10-07 22:30");
        Assert.Single(Saved());
        _store.SetMeta("dtekWatcher", running);                                // the open cut back, as if its clearing never landed
        using (var again = Watcher()) End(again, "2026-10-07 17:06", "2026-10-07 22:30");
        Assert.Single(Saved());
    }

    // design.md §11 DTEK module, rule 25
    [Fact]
    public async Task With_several_queues_shown_and_none_fixed_no_lateness_is_recorded()
    {
        using var w = Watcher(House);
        await TickAt(w, "2026-10-07 12:00");
        Begin(w, "2026-10-07 17:06");
        End(w, "2026-10-07 17:06", "2026-10-07 21:30");
        Assert.Empty(Saved());
    }

    // design.md §11 DTEK module, rule 25
    [Fact]
    public async Task A_cut_whose_end_fell_in_a_gap_of_readings_is_not_counted()
    {
        using var w = Watcher();
        await TickAt(w, "2026-10-07 12:00");
        Begin(w, "2026-10-07 17:02");
        w.AfterPoll(OffAt("2026-10-07 17:02", "2026-10-07 19:00"));
        w.AfterPoll(Back("2026-10-07 22:00"));                                   // three hours unseen
        Assert.Empty(Saved());
    }

    // design.md §11 DTEK module, rules 25, 24
    [Fact]
    public async Task A_cut_spanning_midnight_is_counted_against_its_window_across_midnight()
    {
        using var w = Watcher();
        Plan("2026-10-07 11:50", Day("2026-10-07", ("A", "23-24:0")), Day("2026-10-08", ("A", "1-2:0")));   // 22:00-02:00
        await TickAt(w, "2026-10-07 12:00");
        Begin(w, "2026-10-07 22:03");
        Plan("2026-10-08 00:10", Day("2026-10-08", ("A", "1-2:0")), Day("2026-10-09", ("A", "10:0")));    // the day before is gone
        await TickAt(w, "2026-10-08 00:20");
        _clock.Now = At("2026-10-08 02:10");
        Assert.Equal((true, 10), (w.View(true, 1).Overdue, w.View(true, 1).OverdueMin));
        End(w, "2026-10-07 22:03", "2026-10-08 02:20");
        var late = Assert.Single(Saved());
        Assert.Equal(("07.10.2026", 3, 20, 3, 20), (late.Date, late.StartDelta, late.EndDelta, late.FirstStartDelta, late.FirstEndDelta));
    }

    // design.md §11 DTEK module, rule 25
    [Fact]
    public async Task The_history_keeps_the_last_60_cuts()
    {
        using var w = Watcher();
        await TickAt(w, "2026-10-07 12:00");
        var first = At("2026-10-07 17:00");
        for (int i = 0; i < 61; i++)
        {
            var s = first.AddMinutes(2 * i);
            w.AfterPoll(new StationState { LastAt = s.AddMinutes(-1), OnSince = s.AddMinutes(-1) });
            w.AfterPoll(new StationState { LastAt = s, OffSince = s, Cut = true });
            w.AfterPoll(new StationState { LastAt = s.AddMinutes(1), OnSince = s.AddMinutes(1), MainsBackAt = s.AddMinutes(1) });
        }
        var saved = Saved();
        Assert.Equal(60, saved.Count);
        Assert.Equal(first.AddMinutes(2), saved[0].Start);                       // the oldest went
        Assert.Equal(60, w.View(false, null).LateCuts);
    }

    // design.md §11 DTEK module, rule 25
    [Theory]
    [InlineData("{not json")]
    [InlineData("\"an older format\"")]
    [InlineData("{\"cuts\":[]}")]
    [InlineData("[{\"queue\":5}]")]
    public async Task A_damaged_history_starts_empty_and_the_rest_of_the_state_is_kept(string json)
    {
        using (var w = Watcher()) await TickAt(w, "2026-10-07 12:00");
        _store.SetMeta("dtekWatcherLateness", json);
        using var again = Watcher();
        var v = again.View(false, null);
        Assert.Equal(("ok", 0), (v.State, v.LateCuts));
        Assert.Equal(At("2026-10-07 17:00"), v.NextStart);
    }

    // design.md §11 DTEK module, rule 25
    [Fact]
    public async Task Saved_entries_out_of_bounds_are_dropped_and_over_60_cut_to_the_last()
    {
        using (var w = Watcher()) await TickAt(w, "2026-10-07 12:00");
        var list = Enumerable.Range(0, 70).Select(i => Late(i)).ToList();
        list[69].EndDelta = DtekLate.MaxDelta + 1;
        list[68].Queue = new string('Q', 40);
        list[67].End = list[67].Start;
        _store.SetMeta("dtekWatcherLateness", JsonSerializer.Serialize(list, StationJson.Default.ListDtekLate));
        using var again = Watcher();
        Assert.Equal(60, again.View(false, null).LateCuts);
    }

    // design.md §11 DTEK module, rule 26
    [Fact]
    public async Task Plan_changes_are_counted_only_during_a_cut()
    {
        using var w = Watcher();
        await TickAt(w, "2026-10-07 12:00");
        Plan("2026-10-07 12:10", Day("2026-10-07", ("A", "18-22:0")), TwoDayCells()[1]);    // a change on mains
        await TickAt(w, "2026-10-07 12:30");
        Begin(w, "2026-10-07 17:01");
        Assert.Equal(0, w.View(true, 1).PlanChanges);
        Plan("2026-10-07 17:20", TwoDayCells());
        await TickAt(w, "2026-10-07 17:30");
        Assert.Equal(1, w.View(true, 1).PlanChanges);
        Assert.Equal(0, w.View(false, 1).PlanChanges);
    }
}
