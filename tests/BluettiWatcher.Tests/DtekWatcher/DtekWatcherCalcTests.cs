using BluettiWatcher.DtekWatcher;
using static BluettiWatcher.Tests.DtekWatcher.Dx;

namespace BluettiWatcher.Tests.DtekWatcher;

public sealed class DtekWatcherTimeTests
{
    private static List<DtekWindow> Off(DtekSchedule s, string q) => DtekTime.Windows(s, q, DtekSlot.Off, Kyiv);

    // design.md §11 DTEK module, rule 14
    [Fact]
    public void Hour_n_runs_from_n_minus_1_to_n_and_half_hours_give_borders_on_30()
    {
        var s = Schedule(Day("2026-10-07", ("A", "18-21:0"), ("H", "10:f 11:s 13:0 14:f")));
        Assert.Equal([new DtekWindow(At("2026-10-07 17:00"), At("2026-10-07 21:00"))], Off(s, "A"));
        Assert.Equal(
            [
                new DtekWindow(At("2026-10-07 09:00"), At("2026-10-07 09:30")),
                new DtekWindow(At("2026-10-07 10:30"), At("2026-10-07 11:00")),
                new DtekWindow(At("2026-10-07 12:00"), At("2026-10-07 13:30")),
            ], Off(s, "H"));
    }

    // design.md §11 DTEK module, rule 14
    [Fact]
    public void A_window_crossing_midnight_between_today_and_tomorrow_is_one_outage()
    {
        var w = Off(TwoDays(), "B");
        Assert.Equal(new DtekWindow(At("2026-10-07 22:00"), At("2026-10-08 02:00")), w[0]);
        Assert.Equal(2, w.Count);
        // the page still shows each day on its own clock
        Assert.Equal("22:00-24:00", DtekTime.DayText(TwoDays(), "B", new DateTime(2026, 10, 7), Kyiv));
        Assert.Equal("00:00-02:00, 13:00-15:00", DtekTime.DayText(TwoDays(), "B", new DateTime(2026, 10, 8), Kyiv));
    }

    // design.md §11 DTEK module, rule 14
    [Fact]
    public void Possible_and_unknown_cells_are_shown_as_such_and_are_never_planned_cuts()
    {
        var s = Schedule(Day("2026-10-07", ("A", "10:0 12:m 13:a 14:b 16:?"), ("On", "")));
        Assert.Equal([new DtekWindow(At("2026-10-07 09:00"), At("2026-10-07 10:00"))], Off(s, "A"));
        Assert.Equal("09:00-10:00, possible 11:00-12:30, possible 13:30-14:00, unknown 15:00-16:00", DtekTime.DayText(s, "A", new DateTime(2026, 10, 7), Kyiv));
        Assert.Equal("no cuts", DtekTime.DayText(s, "On", new DateTime(2026, 10, 7), Kyiv));
        Assert.Equal("", DtekTime.DayText(s, "A", new DateTime(2026, 10, 8), Kyiv));
        Assert.Equal("", DtekTime.DayText(s, "Nobody", new DateTime(2026, 10, 7), Kyiv));
    }

    // design.md §11 DTEK module, rule 14: 25.10.2026 has 25 hours in Kyiv (04:00 summer time becomes 03:00)
    [Fact]
    public void On_the_autumn_clock_change_day_the_evening_window_stays_on_the_wall_clock()
    {
        var s = Schedule(Day("2026-10-25", ("A", "18-21:0"), ("Night", "4:0")));
        var w = Off(s, "A")[0];
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 17, 0, 0, TimeSpan.FromHours(2)), w.Start);
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 21, 0, 0, TimeSpan.FromHours(2)), w.End);
        var covered = DtekTime.Covered(s, Kyiv)!.Value;
        Assert.Equal(TimeSpan.FromHours(25), covered.End - covered.Start);
        // the hour that comes twice is off both times
        Assert.Equal(TimeSpan.FromHours(2), Off(s, "Night")[0].End - Off(s, "Night")[0].Start);
    }

    // design.md §11 DTEK module, rule 14: 29.03.2026 has 23 hours in Kyiv (03:00 becomes 04:00)
    [Fact]
    public void On_the_spring_clock_change_day_the_evening_window_stays_on_the_wall_clock()
    {
        var s = Schedule(Day("2026-03-29", ("A", "18-21:0"), ("Skipped", "4:0"), ("Before", "3:0")));
        var w = Off(s, "A")[0];
        Assert.Equal(new DateTimeOffset(2026, 3, 29, 17, 0, 0, TimeSpan.FromHours(3)), w.Start);
        Assert.Equal(new DateTimeOffset(2026, 3, 29, 21, 0, 0, TimeSpan.FromHours(3)), w.End);
        var covered = DtekTime.Covered(s, Kyiv)!.Value;
        Assert.Equal(TimeSpan.FromHours(23), covered.End - covered.Start);
        Assert.Empty(Off(s, "Skipped"));   // the hour the clock skips has no time
        Assert.Equal(TimeSpan.FromHours(1), Off(s, "Before")[0].End - Off(s, "Before")[0].Start);
    }
}

public sealed class DtekWatcherMatcherTests
{
    private static DtekMatch Feed(DtekMatch m, DtekSchedule? s, string[] house, params DtekCut[] cuts)
    {
        foreach (var c in cuts) m = DtekMatcher.Apply(m, c, s, house, Kyiv);
        return m;
    }

    private static readonly DtekCut A1 = Cut("2026-10-07 17:05", "2026-10-07 20:50");
    private static readonly DtekCut A2 = Cut("2026-10-08 08:10", "2026-10-08 12:15");
    private static readonly DtekCut B2 = Cut("2026-10-08 13:00", "2026-10-08 15:00");
    private static readonly DtekCut C2 = Cut("2026-10-08 18:00", "2026-10-08 20:00");

    // design.md §11 DTEK module, rule 3
    [Fact]
    public void One_matching_cut_names_a_candidate_and_fixes_nothing()
    {
        var m = Feed(new DtekMatch(), TwoDays(), House, A1);
        Assert.Null(m.Fixed);
        Assert.Equal(("A", 1), (m.Candidate, m.Count));
    }

    // design.md §11 DTEK module, rule 3
    [Fact]
    public void Two_matching_cuts_in_a_row_fix_the_queue()
    {
        var m = Feed(new DtekMatch(), TwoDays(), House, A1, A2);
        Assert.Equal("A", m.Fixed);
    }

    // design.md §11 DTEK module, rule 3
    [Fact]
    public void A_cut_of_another_queue_between_them_starts_the_count_over()
    {
        var s = Schedule(Day("2026-10-07", ("A", "18-21:0"), ("B", "23-24:0")), Day("2026-10-08", ("A", "9-12:0"), ("B", "14-15:0")));
        var m = Feed(new DtekMatch(), s, ["A", "B"], A1, B2);
        Assert.Null(m.Fixed);
        Assert.Equal(("B", 1), (m.Candidate, m.Count));
    }

    // design.md §11 DTEK module, rule 9
    [Theory]
    [InlineData("2026-10-07 17:20", "2026-10-07 21:20", true)]
    [InlineData("2026-10-07 16:40", "2026-10-07 20:40", true)]
    [InlineData("2026-10-07 17:21", "2026-10-07 21:00", false)]   // the start is too late
    [InlineData("2026-10-07 17:00", "2026-10-07 20:39", false)]   // the end is too early
    [InlineData("2026-10-07 17:00", "2026-10-07 18:00", false)]   // a short cut inside the window
    public void A_cut_matches_when_both_ends_are_within_20_minutes_of_the_windows_borders(string start, string end, bool matches) =>
        Assert.Equal(matches ? ["A"] : [], DtekMatcher.Matching(Cut(start, end), TwoDays(), House, Kyiv));

    // design.md §11 DTEK module, rule 9
    [Fact]
    public void Queues_with_the_same_window_that_day_do_not_count()
    {
        var s = Schedule(Day("2026-10-07", ("A", "18-21:0"), ("Twin", "18-21:0")), Day("2026-10-08", ("A", "9-12:0"), ("Twin", "9-12:0")));
        var m = Feed(new DtekMatch(), s, ["A", "Twin"], A1, A2);
        Assert.Null(m.Fixed);
        Assert.Equal(0, m.Count);
        // and they break no count already running
        var one = Feed(new DtekMatch(), TwoDays(), House, A1);
        Assert.Equal(("A", 1), (Feed(one, s, ["A", "Twin"], A2).Candidate, Feed(one, s, ["A", "Twin"], A2).Count));
    }

    // design.md §11 DTEK module, rule 9
    [Fact]
    public void A_cut_with_no_schedule_saved_for_its_time_does_not_count()
    {
        Assert.Equal(0, Feed(new DtekMatch(), null, House, A1).Count);
        var onlyTomorrow = Schedule(Day("2026-10-08", ("A", "9-12:0")));
        Assert.Equal(0, Feed(new DtekMatch(), onlyTomorrow, House, A1).Count);
        // a cut that began before the first saved day
        var night = Schedule(Day("2026-10-08", ("A", "1-2:0")));
        Assert.Equal(0, Feed(new DtekMatch(), night, House, Cut("2026-10-07 23:55", "2026-10-08 02:00")).Count);
    }

    // design.md §11 DTEK module, rule 9
    [Fact]
    public void A_possible_outage_is_never_a_planned_cut_for_matching()
    {
        var s = Schedule(Day("2026-10-07", ("A", "18-21:m")));
        Assert.Empty(DtekMatcher.Matching(A1, s, ["A"], Kyiv));
    }

    // design.md §11 DTEK module, rule 3
    [Fact]
    public void An_unplanned_cut_neither_counts_nor_contradicts()
    {
        var m = Feed(new DtekMatch(), TwoDays(), House, A1, A2, Cut("2026-10-08 03:00", "2026-10-08 05:00"));
        Assert.Equal("A", m.Fixed);
    }

    // design.md §11 DTEK module, rule 3
    [Fact]
    public void A_later_cut_that_follows_another_queue_opens_the_question_again()
    {
        var m = Feed(new DtekMatch(), TwoDays(), House, A1, A2);
        Assert.Equal("A", m.Fixed);
        m = Feed(m, TwoDays(), House, B2);
        Assert.Null(m.Fixed);
        Assert.Equal(("B", 1), (m.Candidate, m.Count));
        // and a second cut of the new queue fixes it
        var again = Schedule(Day("2026-10-08", ("B", "14-15:0")), Day("2026-10-09", ("B", "9-12:0"), ("A", "1-2:0")));
        Assert.Equal("B", Feed(m, again, House, Cut("2026-10-09 08:00", "2026-10-09 12:00")).Fixed);
    }

    // design.md §11 DTEK module, rule 3
    [Fact]
    public void A_cut_that_fits_the_fixed_queue_keeps_it()
    {
        var m = Feed(new DtekMatch(), TwoDays(), House, A1, A2);
        Assert.Same(m, Feed(m, TwoDays(), House, A1));
        Assert.Null(Feed(m, TwoDays(), House, C2).Fixed);
    }

    // design.md §11 DTEK module, rule 3
    [Fact]
    public void A_fixed_queue_that_is_no_longer_the_houses_is_dropped()
    {
        var m = new DtekMatch { Fixed = "A", Candidate = "A", Count = 2 };
        Assert.Same(m, DtekMatcher.Keep(m, ["A", "B"]));
        Assert.Null(DtekMatcher.Keep(m, ["B", "C"]).Fixed);
    }
}
