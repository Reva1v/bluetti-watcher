using BluettiWatcher.DtekWatcher;
using static BluettiWatcher.Tests.DtekWatcher.Dx;

namespace BluettiWatcher.Tests.DtekWatcher;

public sealed class DtekWatcherOutlookTests
{
    /// <summary>A saved state: the schedule in use and the queues the site's address answer once named.</summary>
    private static DtekSnapshot Snap(string? fixedQueue = null, DtekSchedule? s = null, string[]? house = null) => new()
    {
        Schedule = s ?? TwoDays(),
        House = new DtekHouse { Queues = house ?? House },
        Match = new DtekMatch { Fixed = fixedQueue },
        DatasetTriedAt = At("2026-10-07 11:50"),
    };

    private static DtekView View(DtekSnapshot snap, string now, bool cut = false, double? hoursLeft = null, DtekConfig? config = null) =>
        DtekOutlook.Compute(config ?? Config(), snap, cut, hoursLeft, new ManualClock(At(now)), Kyiv);

    // design.md §11 DTEK module, rules 1, 16
    [Fact]
    public void The_next_planned_outage_of_the_fixed_queue_with_both_times_and_the_source()
    {
        var v = View(Snap("A"), "2026-10-07 12:00");
        Assert.Equal("ok", v.State);
        Assert.Equal(("dataset", "07.10.2026 11:43", At("2026-10-07 11:45")), (v.Source, v.Update, v.CollectedAt!.Value));
        Assert.Equal((At("2026-10-07 17:00"), At("2026-10-07 21:00"), "A"), (v.NextStart!.Value, v.NextEnd!.Value, v.NextQueue));
        Assert.Equal(("A", "matched"), (v.Queue, v.QueueSource));
        Assert.Equal([new DtekQueueDays("A", "17:00-21:00", "08:00-12:00")], v.Windows);
        Assert.Equal(("07.10.2026", "08.10.2026"), (v.Today, v.Tomorrow));
        Assert.Null(v.BatteryLasts);   // no cut: nothing to last until
        Assert.Null(v.Return);
        // after today's window the next one is tomorrow's
        Assert.Equal(At("2026-10-08 08:00"), View(Snap("A"), "2026-10-07 21:00").NextStart);
    }

    // design.md §11 DTEK module, rule 3
    [Fact]
    public void Until_the_queue_is_fixed_all_the_houses_queues_are_shown_and_the_earliest_outage_is_next()
    {
        var v = View(Snap(), "2026-10-07 12:00");
        Assert.Null(v.Queue);
        Assert.Equal(House, v.Queues);
        Assert.Equal("site", v.QueuesSource);
        Assert.Equal(["A", "B", "C"], v.Windows.Select(w => w.Queue));
        Assert.Equal((At("2026-10-07 17:00"), "A"), (v.NextStart!.Value, v.NextQueue));
        Assert.Contains("not fixed yet", v.Note, StringComparison.Ordinal);
    }

    // design.md §11 DTEK module, rule 18
    [Fact]
    public void The_queues_come_from_config_first_then_from_a_saved_site_answer()
    {
        var v = View(Snap(), "2026-10-07 12:00", config: Config(queues: ["B", " C ", "B", ""]));
        Assert.Equal(["B", "C"], v.Queues);
        Assert.Equal("config", v.QueuesSource);
        Assert.Equal(["B", "C"], v.Windows.Select(w => w.Queue));
        // a queue matched earlier that the config no longer lists is not followed
        Assert.Null(View(Snap("A"), "2026-10-07 12:00", config: Config(queues: ["B", "C"])).Queue);
        Assert.Equal(("site", "ok"), (View(Snap(), "2026-10-07 12:00").QueuesSource, View(Snap(), "2026-10-07 12:00").State));
    }

    // design.md §11 DTEK module, rule 18
    [Fact]
    public void With_no_queues_from_config_or_site_the_state_is_none_and_the_note_asks_for_them()
    {
        var snap = Snap();
        snap.House = null;
        var v = View(snap, "2026-10-07 12:00");
        Assert.Equal("none", v.State);
        Assert.Empty(v.Queues);
        Assert.Empty(v.Windows);
        Assert.Null(v.NextStart);
        Assert.Contains("set dtekWatcher.queues", v.Note, StringComparison.Ordinal);
        Assert.Equal(("dataset", "07.10.2026 11:43"), (v.Source, v.Update));   // what was read is still named
    }

    // design.md §11 DTEK module, rule 8
    [Fact]
    public void The_configured_queue_wins_over_the_matched_one()
    {
        var v = View(Snap("A"), "2026-10-07 12:00", config: Config(group: "B"));
        Assert.Equal(("B", "config"), (v.Queue, v.QueueSource));
        Assert.Equal(At("2026-10-07 22:00"), v.NextStart);
        Assert.Equal(At("2026-10-08 02:00"), v.NextEnd);   // one outage across midnight
        Assert.Equal(["B"], v.Windows.Select(w => w.Queue));
    }

    // design.md §11 DTEK module, rule 8
    [Fact]
    public void A_house_with_one_queue_needs_no_matching()
    {
        Assert.Equal(("C", "single"), (View(Snap(house: ["C"]), "2026-10-07 08:00").Queue, View(Snap(house: ["C"]), "2026-10-07 08:00").QueueSource));
        Assert.Equal("single", View(Snap(), "2026-10-07 08:00", config: Config(queues: ["A"])).QueueSource);
    }

    // design.md §11 DTEK module, rule 11
    [Theory]
    [InlineData(4.0, "yes")]    // 17:30 + 4 h reaches 21:00
    [InlineData(3.5, "yes")]
    [InlineData(3.4, "no")]
    public void During_a_cut_the_battery_lasts_or_not_until_the_fixed_queues_return(double hoursLeft, string lasts)
    {
        var v = View(Snap("A"), "2026-10-07 17:30", cut: true, hoursLeft: hoursLeft);
        Assert.Equal(At("2026-10-07 21:00"), v.Return);
        Assert.Equal(lasts, v.BatteryLasts);
    }

    // design.md §11 DTEK module, rule 11
    [Fact]
    public void While_the_queue_is_unknown_the_latest_return_among_the_houses_queues_is_used()
    {
        var s = Schedule(Day("2026-10-07", ("A", "18-19:0"), ("B", "18-21:0"), ("C", "10:0")));
        var v = View(Snap(s: s), "2026-10-07 17:30", cut: true, hoursLeft: 2.5);
        Assert.Equal(At("2026-10-07 21:00"), v.Return);   // B's, later than A's 19:00
        Assert.Equal("no", v.BatteryLasts);
        Assert.Equal("yes", View(Snap("A", s), "2026-10-07 17:30", cut: true, hoursLeft: 2.5).BatteryLasts);
    }

    // design.md §11 DTEK module, rule 11
    [Fact]
    public void Unknown_when_either_side_is_unknown()
    {
        Assert.Equal("unknown", View(Snap("A"), "2026-10-07 17:30", cut: true, hoursLeft: null).BatteryLasts);
        // a cut outside every planned window: no return known
        var v = View(Snap("A"), "2026-10-07 13:00", cut: true, hoursLeft: 5);
        Assert.Null(v.Return);
        Assert.Equal("unknown", v.BatteryLasts);
        // no schedule at all
        Assert.Equal("unknown", View(new DtekSnapshot(), "2026-10-07 17:30", cut: true, hoursLeft: 5).BatteryLasts);
    }

    private static DtekSnapshot WithOutage(string cutStart, string answeredAt, string end = "15:00 07.10.2026")
    {
        var snap = Snap("A");
        snap.OpenCut = new DtekOpenCut { Start = At(cutStart), StartKnown = true };
        snap.House = new DtekHouse { Queues = House, FetchedAt = At(answeredAt), Kind = "Emergency repair", StartText = "12:20 07.10.2026", EndText = end };
        return snap;
    }

    // design.md §11 DTEK module, rule 7
    [Fact]
    public void The_sites_own_outage_is_shown_and_its_end_serves_when_the_plan_has_no_window()
    {
        var v = View(WithOutage("2026-10-07 12:21", "2026-10-07 12:40"), "2026-10-07 13:00", cut: true, hoursLeft: 1);
        Assert.Equal(("Emergency repair", "07.10.2026 12:20", "07.10.2026 15:00"), (v.OutageKind, v.OutageStart, v.OutageEnd));
        Assert.Equal(At("2026-10-07 15:00"), v.OutageEndAt);
        Assert.Equal(At("2026-10-07 15:00"), v.Return);
        Assert.Equal("no", v.BatteryLasts);
        // a date the service cannot read is shown as written and decides nothing
        v = View(WithOutage("2026-10-07 12:21", "2026-10-07 12:40", "by the evening"), "2026-10-07 13:00", cut: true, hoursLeft: 1);
        Assert.Equal("by the evening", v.OutageEnd);
        Assert.Null(v.OutageEndAt);
        Assert.Equal("unknown", v.BatteryLasts);
    }

    // design.md §11 DTEK module, rule 18
    [Fact]
    public void The_outage_line_appears_only_when_the_site_answered_during_this_cut()
    {
        // answered before the cut began: an older outage, not this one
        var v = View(WithOutage("2026-10-07 12:21", "2026-10-07 12:00"), "2026-10-07 13:00", cut: true, hoursLeft: 1);
        Assert.Equal(("", "", ""), (v.OutageKind, v.OutageStart, v.OutageEnd));
        Assert.Null(v.Return);
        // and nothing outside a cut
        Assert.Equal("", View(WithOutage("2026-10-07 12:21", "2026-10-07 12:40"), "2026-10-07 13:00").OutageKind);
    }

    // design.md §11 DTEK module, rules 16, 6
    [Fact]
    public void Stale_when_the_newest_collection_is_over_90_minutes_old_and_none_after_the_days_it_covers()
    {
        var snap = Snap("A");   // collected at 11:45
        Assert.Equal("ok", View(snap, "2026-10-07 13:15").State);
        var v = View(snap, "2026-10-07 13:16");
        Assert.Equal("stale", v.State);
        Assert.Contains("stale: last collected 07.10.2026 11:45, over 90 min ago", v.Note, StringComparison.Ordinal);
        Assert.Equal(At("2026-10-07 17:00"), v.NextStart);   // the covered days are still shown
        var config = Config();
        config.StaleAfterMin = 30;
        Assert.Equal("stale", View(snap, "2026-10-07 12:16", config: config).State);
        v = View(snap, "2026-10-08 23:59");
        Assert.Equal([new DtekQueueDays("A", "08:00-12:00", "")], v.Windows);   // yesterday's "tomorrow" is today
        v = View(snap, "2026-10-09 00:00");
        Assert.Equal("none", v.State);
        Assert.Empty(v.Windows);
        Assert.Null(v.NextStart);
        Assert.Contains("no schedule", v.Note, StringComparison.Ordinal);
    }

    // design.md §11 DTEK module, rule 17
    [Fact]
    public void A_blocked_site_is_the_sites_own_state_and_never_the_modules()
    {
        var snap = Snap("A");
        snap.SiteTriedAt = At("2026-10-07 11:55");
        snap.SiteError = "blocked";
        var v = View(snap, "2026-10-07 12:00");
        Assert.Equal(("ok", "ok", "blocked"), (v.State, v.DatasetState, v.SiteState));
        Assert.Contains("the site blocks the read", v.Note, StringComparison.Ordinal);
        snap.SiteError = "network";
        Assert.Equal("failed", View(snap, "2026-10-07 12:00").SiteState);
        snap.SiteError = "";
        Assert.Equal("ok", View(snap, "2026-10-07 12:00").SiteState);
        var noSite = Config();
        noSite.Site = false;
        Assert.Equal("off", View(snap, "2026-10-07 12:00", config: noSite).SiteState);
        snap.DatasetError = "network";
        Assert.Equal(("ok", "failed"), (View(snap, "2026-10-07 12:00").State, View(snap, "2026-10-07 12:00").DatasetState));
    }

    // design.md §11 DTEK module, rule 8
    [Fact]
    public void Off_without_enabled_or_with_a_source_address_that_cannot_be_used()
    {
        var clock = new ManualClock(At("2026-10-07 12:00"));
        Assert.Equal("off", DtekOutlook.Compute(new DtekConfig(), Snap("A"), false, null, clock, Kyiv).State);
        // the address is optional: the dataset needs none
        Assert.Null(new DtekConfig { Enabled = true }.Problem());
        Assert.NotNull(new DtekConfig { Enabled = true, Host = "evil.example/path" }.Problem());
        foreach (string url in new[] { "http://raw.githubusercontent.com/x.json", "raw.githubusercontent.com/x.json", "https://host.example:8443/x.json", "https://user@host.example/x.json", "" })
        {
            var bad = new DtekConfig { Enabled = true, DatasetUrl = url };
            Assert.NotNull(bad.Problem());
            Assert.Equal("off", DtekOutlook.Compute(bad, Snap("A"), false, null, clock, Kyiv).State);
        }
    }

    // design.md §11 DTEK module, rule 16
    [Fact]
    public void Changes_name_the_queue_the_day_and_both_window_texts()
    {
        var before = TwoDays();
        var after = Schedule(Day("2026-10-07", ("A", "17-21:0"), ("B", "23-24:0")), Day("2026-10-08", ("A", "9-12:0"), ("B", "14-15:0")), Day("2026-10-09", ("A", "1:0")));
        Assert.Equal("A 07.10.2026: 17:00-21:00 -> 16:00-21:00", DtekOutlook.Changes(before, after, ["A"], Kyiv));
        Assert.Equal("A 07.10.2026: 17:00-21:00 -> 16:00-21:00; B 08.10.2026: 00:00-02:00, 13:00-15:00 -> 13:00-15:00", DtekOutlook.Changes(before, after, House, Kyiv));
        Assert.Equal("", DtekOutlook.Changes(before, after, ["C"], Kyiv));   // C is not in the new answer: nothing to compare
        Assert.Equal("", DtekOutlook.Changes(null, after, House, Kyiv));
        Assert.Equal("", DtekOutlook.Changes(before, TwoDays(), House, Kyiv));
    }
}
