using BluettiWatcher.Decision;
using static BluettiWatcher.Tests.Fx;

namespace BluettiWatcher.Tests;

/// <summary>The rules of design.md sections 3, 5 and 6, one named test per rule.</summary>
public class DecisionSpecTests
{
    // design.md §3 Readings
    [Fact]
    public void Service_defaults_poll_once_a_minute_also_on_low_battery()
    {
        var d = Step(Seq(Pre(20), Bat(20)), Station, cfg: new StationConfig());
        Assert.All(d, x => Assert.Equal(60, x.DelaySec));
    }

    // design.md §5 and §3
    [Fact]
    public void Rejected_reading_neither_advances_nor_resets_the_mains_lost_timer()
    {
        // t0, +2, +3 (implausible), +4, +5 min: the cut begins at +5 (300 s after the first), the rejected one changes nothing.
        var d = Step([Bat(80), Bat(80), new S(Bat(0), Dt: 60), new S(Bat(80), Dt: 60), new S(Bat(80), Dt: 60)]);
        Assert.StartsWith("implausible", d[2].Rejected, StringComparison.Ordinal);
        Assert.Equal(T0, d[2].State.OffSince);
        Assert.Equal("", Acts(d[3]));
        Assert.Equal("eco-on", Acts(d[4]));
    }

    // design.md §5 and §3
    [Fact]
    public void Rejected_reading_neither_advances_nor_resets_the_mains_back_timer()
    {
        var d = Step(Seq(Pre(60), Bat(60), Grid(60), new S(Grid(0), Dt: 60), new S(Grid(60), Dt: 60), new S(Grid(60), Dt: 180)), Station);
        Assert.StartsWith("implausible", d[5].Rejected, StringComparison.Ordinal);
        Assert.Equal(T0.AddMinutes(8), d[5].State.OnSince);
        Assert.Equal("mains-back", Acts(d[7]));
    }

    // design.md §3 Readings
    [Fact]
    public void Gap_over_10_minutes_resets_cut_levels_level_timers_and_streaks()
    {
        var d = Step(Seq(Pre(4), Bat(4), Bat(4), Bat(4), new S(Bat(4), Dt: 601)), Station);
        var s = d[^1].State;
        Assert.True(d[^2].State.Cut);
        Assert.False(s.Cut);
        Assert.All(s.Fired, Assert.False);
        Assert.Equal(Level.Ok, s.Level);
        Assert.Null(s.WarnedAt);
        Assert.Equal(d[^1].State.LastAt, s.OffSince);   // the timer restarted at this reading
        Assert.Equal("", Acts(d[^1]));
    }

    // design.md §3 Readings
    [Fact]
    public void Gap_of_exactly_10_minutes_is_no_reset()
    {
        var d = Step(Seq(Pre(80), Bat(80), new S(Bat(80), Dt: 600)), Station);
        Assert.True(d[^1].State.Cut);
    }

    // design.md §5 Mains, eco and manual control
    [Fact]
    public void Gap_reset_keeps_a_lift_still_owed()
    {
        var manual = Flag("eco", "manual");
        var d = Step(Seq(Pre(60), Bat(60), Grid(60), Grid(60), Grid(60), Grid(60), new S(Grid(60), Dt: 900), Grid(60), Grid(60), Grid(60)), manual, [StationAction.MainsBack]);
        Assert.Equal("mains-back", Acts(d[7]));
        Assert.True(d[8].State.OwedLift);
        Assert.Equal("", Acts(d[8]));
        Assert.Equal("mains-back", Acts(d[^1]));
    }

    // ---- design.md §5: the owed lift
    private static object[] CutThenMains() => Seq(Pre(60), Bat(60), Grid(60), Grid(60), Grid(60), Grid(60));   // mains back at d7 = t0+14

    // design.md §5 Mains, eco and manual control
    [Fact]
    public void Repro_a_mains_back_succeeded_a_manual_emergency_60s_later_is_not_lifted()
    {
        var manual = Flag("emergency", "manual", "", T0.AddMinutes(15));
        var d = Step(Seq(CutThenMains(), new S(Grid(60), manual), new S(Grid(60), manual)), Flag("eco", "station", "", T0.AddMinutes(6)));
        Assert.Equal("mains-back", Acts(d[7]));
        Assert.False(d[7].State.OwedLift);
        Assert.Equal("|", AllActs(d, 8, 9));
    }

    // design.md §5 Mains, eco and manual control
    [Fact]
    public void Owed_lift_cleared_only_when_off_succeeded()
    {
        var d = Step(Seq(CutThenMains(), Grid(60), Grid(60)), Flag("eco", "manual", "", T0.AddMinutes(6)), [StationAction.MainsBack]);
        Assert.Equal("mains-back|mains-back|mains-back", AllActs(d, 7, 9));
        Assert.True(d[9].State.OwedLift);
    }

    // design.md §5 Mains, eco and manual control
    [Fact]
    public void Mode_set_after_mains_came_back_is_never_lifted_while_a_lift_is_owed()
    {
        var before = Flag("eco", "manual", "", T0.AddMinutes(6));
        var after = Flag("emergency", "manual", "", T0.AddMinutes(15));
        var d = Step(Seq(CutThenMains(), new S(Grid(60), after), new S(Grid(60), after)), before, [StationAction.MainsBack]);
        Assert.Equal("mains-back", Acts(d[7]));
        Assert.Equal("|", AllActs(d, 8, 9));
        Assert.False(d[9].State.OwedLift);
    }

    // design.md §5 Mains, eco and manual control
    [Fact]
    public void Mode_set_before_mains_came_back_is_lifted()
    {
        var d = Step(CutThenMains(), Flag("eco", "manual", "", T0.AddMinutes(7)));
        Assert.Equal("mains-back", Acts(d[7]));
    }

    // design.md §5 Mains, eco and manual control
    [Fact]
    public void Mode_set_during_the_300s_confirmation_stays()
    {
        var d = Step(CutThenMains(), Flag("eco", "manual", "", T0.AddMinutes(9)));   // mains first seen back at t8
        Assert.Equal("", Acts(d[7]));
        Assert.False(d[7].State.OwedLift);
    }

    // design.md §3 Readings
    [Fact]
    public void Backward_clock_jump_counts_as_a_gap()
    {
        var d = Step(Seq(Pre(80), Bat(80), new S(Bat(80), Dt: -600)), Station);
        Assert.True(d[3].State.Cut);
        Assert.False(d[4].State.Cut);
        Assert.Equal(d[4].State.LastAt, d[4].State.OffSince);
    }

    // design.md §5 Mains, eco and manual control
    [Fact]
    public void Max_holds_across_a_gap_that_finds_the_cut_again()
    {
        // cut at t6, Max at t8, 15 minutes silent, then on battery from t23: the cut begins again at t29 - no mains change.
        var d = Step(Seq(Pre(80), Bat(80), Bat(80), new S(Bat(80), Dt: 900), Bat(80), Bat(80), Bat(80), Bat(80)), Flag(), before: At(4, ManualMode.Max));
        Assert.True(d[8].State.Cut);
        Assert.Equal(ManualMode.Max, d[8].State.Manual);
        Assert.Equal(ManualMode.Max, d[^1].State.Manual);
        Assert.Equal("|", AllActs(d, 8, 9));
    }

    // design.md §5 Mains, eco and manual control
    [Fact]
    public void Mains_back_after_a_gap_that_ended_a_cut_ends_Max_and_owes_the_lift()
    {
        var d = Step(Seq(Pre(80), Bat(80), Bat(80), new S(Grid(80), Dt: 900), Grid(80), Grid(80), Grid(80)), Station, before: At(4, ManualMode.Max));
        Assert.Equal(ManualMode.Auto, d[^1].State.Manual);
        Assert.Equal("mains-back", Acts(d[^1]));
    }

    // design.md §6 Battery levels and hibernate
    [Fact]
    public void Resume_within_10_minutes_re_arms_hibernate_with_agreement_and_the_warning()
    {
        // hibernate at t14; the PC is back at t23 (9 minutes, no gap reset), still at 4 % and falling.
        var d = Step(Seq(Pre(4), Bat(4), Bat(4), Bat(4), Bat(4), Bat(4), new S(Bat(4), Dt: 540), Bat(3.5), Bat(3.5), Bat(3)), Station);
        Assert.Equal("hibernate", Acts(d[7]));
        Assert.True(d[8].State.Cut);
        Assert.False(d[8].State.Fired[(int)LevelStep.Hibernate]);
        Assert.Equal("|hibernate-warning||hibernate", AllActs(d, 8, 11));
    }

    // design.md §6 Battery levels and hibernate
    [Fact]
    public void Harness_can_fail_an_action_once_it_is_retried_and_then_done()
    {
        var d = Step(Seq(Pre(20), Bat(20), Bat(20), Bat(20)), Station, failWhen: (i, a) => i == 3 && a == StationAction.EmergencyOn);
        Assert.Equal("emergency-on|emergency-on|", AllActs(d, 3, 5));
        Assert.False(d[3].State.Fired[(int)LevelStep.Emergency]);
        Assert.True(d[4].State.Fired[(int)LevelStep.Emergency]);
    }

    // ---- design.md §6: hibernation on readings and time only
    // design.md §6 Battery levels and hibernate
    [Fact]
    public void Emergency_failing_does_not_hold_back_stop_warn_or_hibernate()
    {
        var d = Step(Seq(Pre(4), Bat(4), Bat(4), Bat(4), Bat(4), Bat(4)), Station, [StationAction.EmergencyOn]);
        Assert.Equal("emergency-on|emergency-on,stop|emergency-on,hibernate-warning|emergency-on|emergency-on,hibernate", AllActs(d, 3, 7));
    }

    // design.md §6 Battery levels and hibernate
    [Fact]
    public void Resume_still_at_or_under_5_on_battery_hibernates_again_once_the_new_cut_qualifies()
    {
        var steps = new List<object>(Seq(Pre(4), Bat(4), Bat(4), Bat(4), Bat(4), Bat(4)));
        steps.Add(new S(Bat(4), Dt: 3600));
        for (int k = 0; k < 8; k++) steps.Add(Bat(4));
        var d = Step([.. steps], Station);
        Assert.Equal("hibernate", Acts(d[7]));
        Assert.False(d[8].State.Cut);
        Assert.False(d[8].State.Fired[(int)LevelStep.Hibernate]);
        Assert.Equal("emergency-on|stop|hibernate-warning||hibernate", AllActs(d, 11, 15));
    }

    // design.md §6 and §3
    [Fact]
    public void Levels_need_two_readings_at_least_90s_apart()
    {
        var d = Step(Seq(Pre(30), Bat(30), Bat(20), new S(Bat(20), Dt: 89), new S(Bat(20), Dt: 1)), Station);
        Assert.Equal("", Acts(d[5]));
        Assert.Equal("emergency-on", Acts(d[6]));
    }

    // design.md §6 Battery levels and hibernate
    [Fact]
    public void Each_level_fires_in_a_later_poll_than_the_previous_one()
    {
        var d = Step(Seq(Pre(4), Bat(4), Bat(4)), Station);
        Assert.Equal("emergency-on", Acts(d[3]));
        Assert.Equal("stop", Acts(d[4]));
        Assert.Single(d[4].Actions);
    }


    // design.md §6 Battery levels and hibernate
    [Fact]
    public void Warning_from_a_previous_cut_does_not_count_for_hibernate()
    {
        var d = Step(Seq(Pre(5), Bat(5), Bat(5), Bat(5), Grid(5), Grid(5), Grid(5), Grid(5), Bat(5), Bat(5), Bat(5), Bat(5), Bat(5), Bat(5)), Station);
        Assert.Equal("hibernate-warning", Acts(d[5]));
        Assert.Null(d[9].State.WarnedAt);
        Assert.Equal("emergency-on|stop|hibernate-warning", AllActs(d, 13, 15));
    }

    // design.md §6 Battery levels and hibernate
    [Fact]
    public void Hibernate_only_on_battery_never_on_unknown_grid()
    {
        var unknown = new Reading(4, 0, 220, "OTHER");
        var d = Step(Seq(Pre(4), Bat(4), Bat(4), Bat(4), unknown, unknown, unknown), Station);
        Assert.DoesNotContain("hibernate", Flat(d.Skip(7)));
    }

    // ---- manual control
    private static Func<int, DateTimeOffset, StationState, StationState> At(int index, ManualMode m) =>
        (i, clock, s) => i == index ? StationDecision.SetManual(s, m, clock) : s;

    // design.md §5 Mains, eco and manual control
    [Fact]
    public void Max_during_a_cut_no_eco_on()
    {
        var d = Step(Seq(Pre(80), Bat(80), Bat(80)), Flag(), before: At(3, ManualMode.Max));
        Assert.Equal("|", AllActs(d, 3, 4));
    }

    // design.md §6 Battery levels and hibernate
    [Fact]
    public void Levels_apply_under_Max()
    {
        var d = Step(Seq(Pre(4), Bat(4), Bat(4), Bat(4), Bat(4), Bat(4)), Flag(), before: At(3, ManualMode.Max));
        Assert.Equal("emergency-on|stop|hibernate-warning||hibernate", AllActs(d, 3, 7));
    }

    // design.md §5 Mains, eco and manual control
    [Fact]
    public void Auto_after_Max_eco_comes_back()
    {
        var d = Step(Seq(Pre(80), Bat(80), Bat(80), Bat(80)), Flag(), before: (i, c, s) =>
            i == 3 ? StationDecision.SetManual(s, ManualMode.Max, c) : i == 5 ? StationDecision.SetManual(s, ManualMode.Auto, c) : s);
        Assert.Equal("||eco-on", AllActs(d, 3, 5));
    }

    // design.md §5 Mains, eco and manual control
    [Fact]
    public void Manual_choice_from_before_the_cut_ends_when_the_cut_begins()
    {
        var d = Step(Seq(Grid(80), Bat(80), Bat(80), Bat(80), Bat(80)), Flag(), before: At(0, ManualMode.Max));
        Assert.Equal("eco-on", Acts(d[4]));
        Assert.Equal(ManualMode.Auto, d[4].State.Manual);
    }

    // design.md §5 Mains, eco and manual control
    [Fact]
    public void Max_chosen_while_the_grid_is_already_off_holds_when_the_cut_begins()
    {
        var d = Step(Seq(Bat(80), Bat(80), Bat(80), Bat(80)), Flag(), before: At(1, ManualMode.Max));
        Assert.True(d[3].State.Cut);
        Assert.Equal("", Acts(d[3]));
        Assert.Equal(ManualMode.Max, d[3].State.Manual);
    }

    // design.md §5 Mains, eco and manual control
    [Fact]
    public void Max_ends_at_mains_back_and_the_mode_is_lifted()
    {
        var d = Step(Seq(Pre(60), Bat(60), Grid(60), Grid(60), Grid(60), Grid(60)), Station, before: At(3, ManualMode.Max));
        Assert.Equal("mains-back", Acts(d[^1]));
        Assert.Equal(ManualMode.Auto, d[^1].State.Manual);
    }

    // design.md §5 Mains, eco and manual control
    [Fact]
    public void Manual_eco_on_mains_is_not_lifted_until_Auto()
    {
        var d = Step([Grid(100), Grid(100), Grid(100), Grid(100), Grid(100), Grid(100)], Flag("eco", "station", "", T0.AddMinutes(-1)), before: (i, c, s) =>
            i == 0 ? StationDecision.SetManual(s, ManualMode.Eco, c) : i == 4 ? StationDecision.SetManual(s, ManualMode.Auto, c) : s);
        Assert.Equal("||||mains-back|mains-back", AllActs(d));
    }

    // design.md §5 Mains, eco and manual control
    [Fact]
    public void Manual_eco_chosen_after_mains_came_back_is_not_lifted_by_the_owed_lift()
    {
        var d = Step(Seq(Pre(60), Bat(60), Grid(60), Grid(60), Grid(60), Grid(60), Grid(60)), Station, before: At(5, ManualMode.Eco));
        Assert.Equal("||", AllActs(d, 6, 8));
        Assert.False(d[^1].State.OwedLift);
        Assert.Equal(ManualMode.Eco, d[^1].State.Manual);
    }

    [Fact]
    public void Manual_eco_during_a_cut_asks_for_eco_while_the_flag_is_normal()
    {
        var d = Step(Seq(Pre(80), Bat(80)), Flag(), before: At(3, ManualMode.Eco));
        Assert.Equal("eco-on", Acts(d[3]));
    }

    // design.md §5 Mains, eco and manual control
    [Fact]
    public void Gap_reset_keeps_the_manual_choice()
    {
        var d = Step(Seq(Pre(80), Bat(80), new S(Bat(80), Dt: 900)), Flag(), before: At(3, ManualMode.Max));
        Assert.Equal(ManualMode.Max, d[^1].State.Manual);
    }

    // ---- rebased readings, unknown mains
    private static object[] Every60(Reading r, int n) => Enumerable.Range(0, n).Select(_ => (object)new S(r, Dt: 60)).ToArray();

    /// <summary>Hibernate comes, and at the first poll whose streak (begun after the rebase) has lasted the confirmation.</summary>
    private static void HibernatesAfterTheConfirmation(List<StationDecisionResult> d, int confirmSec = 600, int stepSec = 60)
    {
        int i = d.FindIndex(x => x.Actions.Contains(StationAction.Hibernate));
        Assert.True(i >= 0, "no hibernate");
        var st = d[i].State;
        double lasted = (st.LastAt!.Value - st.Since[(int)LevelStep.Hibernate]!.Value).TotalSeconds;
        Assert.InRange(lasted, confirmSec, confirmSec + stepSec - 1);
        Assert.Equal(st.Since[(int)LevelStep.Hibernate]!.Value.AddSeconds(confirmSec), d[i - 1].State.HibernateDelayedUntil);
    }

    // design.md §6 Battery levels and hibernate
    [Theory]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(9)]
    public void Station_reporting_0_for_3_to_9_minutes_after_80_never_hibernates(int minutes)
    {
        var d = Step(Seq(Pre(80), Bat(80), Every60(Bat(0), minutes)), Station);
        Assert.DoesNotContain("hibernate", Flat(d));
    }

    // design.md §6 Battery levels and hibernate
    [Fact]
    public void Station_reporting_the_same_0_for_10_minutes_after_the_rebase_is_treated_as_real()
    {
        var d = Step(Seq(Pre(80), Bat(80), Every60(Bat(0), 40)), Station);
        HibernatesAfterTheConfirmation(d);
    }

    // design.md §6 Battery levels and hibernate
    [Fact]
    public void Recalibration_22_to_4_hibernates_after_10_minutes_at_4_and_not_before()
    {
        var d = Step(Seq(Pre(22), Bat(22), Every60(Bat(4), 3), Every60(Bat(4), 20)), Station);
        HibernatesAfterTheConfirmation(d);
        Assert.Single(Flat(d), "hibernate-delayed");
    }

    // design.md §6 Battery levels and hibernate
    [Fact]
    public void Garbage_40_between_real_8_then_a_real_drain_to_4_hibernates_after_10_minutes()
    {
        var d = Step(Seq(Pre(8), Bat(8), Every60(Bat(40), 3), Every60(Bat(8), 3), Every60(Bat(6), 1), Every60(Bat(4), 20)), Station);
        HibernatesAfterTheConfirmation(d);
    }

    /// <summary>A confirmation longer than the warning's wait, so the two cannot end at the same poll.</summary>
    private static StationConfig LongConfirm()
    {
        var c = Cfg();
        c.RebaseHibernateConfirmSec = 1200;
        return c;
    }

    // design.md §6 Battery levels and hibernate
    [Fact]
    public void Backward_clock_step_moves_the_rebase_to_the_new_now()
    {
        var d = Step(Seq(Pre(80), Bat(80), Every60(Bat(4), 5), new S(Bat(4), Dt: -3600), Every60(Bat(4), 30)), Station, cfg: LongConfirm());
        var back = d[9].State;
        Assert.Equal(back.LastAt, back.RebasedAt);
        HibernatesAfterTheConfirmation(d, 1200);
    }

    // design.md §5 Mains, eco and manual control
    [Fact]
    public void Gap_reset_keeps_a_page_choice_still_pending()
    {
        var d = Step(Seq(Pre(80), Bat(80), new S(Bat(80), Dt: 900)), Flag(),
            before: (i, clock, s) => { if (i == 4) s.ModePending = ManualMode.Max; return s; });   // pressed in the cut (it began at 3)
        Assert.Equal(ManualMode.Max, d[^1].State.ModePending);
    }

    // design.md §3 and §6
    [Fact]
    public void Rebased_reading_never_counts_toward_a_level()
    {
        // 0, 0 rejected, the third 0 moves the baseline; the next 0 is the first reading that counts: no agreement yet.
        var d = Step(Seq(Pre(25), Bat(25), Every60(Bat(0), 3), new S(Bat(0), Dt: 90), new S(Bat(0), Dt: 90)), Station);
        Assert.Equal("", d[6].Rejected);
        Assert.Null(d[6].State.Since[(int)LevelStep.Emergency]);
        Assert.DoesNotContain(StationAction.EmergencyOn, d[7].Actions);
        Assert.Contains(StationAction.EmergencyOn, d[8].Actions);
    }

    // design.md §3 and §6
    [Fact]
    public void Hibernate_streak_from_before_a_rebase_waits_the_confirmation_from_the_latest_rebase()
    {
        // The battery really is at 4 %, the warning given; one wild reading run moves the baseline (8) and back (11). The
        // streak of 4 % is older than both, and still waits 600 s of counted readings after the latest rebase.
        var d = Step(Seq(Pre(4), Bat(4), Bat(4), Bat(4), Every60(Bat(40), 3), Every60(Bat(4), 3), Every60(Bat(4), 12)), Station);
        Assert.Equal(d[11].State.LastAt, d[11].State.RebasedAt);
        Assert.True(d[12].State.Since[(int)LevelStep.Hibernate] < d[8].State.RebasedAt);
        Assert.Equal(21, d.FindIndex(x => x.Actions.Contains(StationAction.Hibernate)));
        Assert.Equal(d[11].State.LastAt!.Value.AddSeconds(600), d[20].State.HibernateDelayedUntil);
        Assert.Single(Flat(d), "hibernate-delayed");
    }

    // design.md §3 Readings
    [Fact]
    public void Rebase_survives_a_gap_in_the_same_cut_hibernate_still_waits_10_minutes()
    {
        var d = Step(Seq(Pre(80), Bat(80), Every60(Bat(0), 3), Bat(0), new S(Bat(0), Dt: 700), Every60(Bat(0), 30)), Station, cfg: LongConfirm());
        Assert.NotNull(d[8].State.RebasedAt);
        HibernatesAfterTheConfirmation(d, 1200);
    }

    // design.md §6 Battery levels and hibernate
    [Fact]
    public void Accepted_reading_with_unknown_mains_breaks_agreement_for_a_level()
    {
        var unknown = new Reading(23, 0, 220, "OTHER");
        var d = Step(Seq(Pre(25), Bat(25), Bat(20), unknown, Bat(20), Bat(20)), Station);
        Assert.Equal("", d[5].Rejected);
        Assert.DoesNotContain(StationAction.EmergencyOn, d[6].Actions);
        Assert.Contains(StationAction.EmergencyOn, d[7].Actions);
    }

    // design.md §3 Readings
    [Fact]
    public void Rebase_is_forgotten_once_mains_is_confirmed_back_the_next_cut_hibernates()
    {
        var d = Step(Seq(Pre(80), Bat(80), Every60(Bat(10), 3), Grid(10), Grid(10), Grid(10), Grid(10),
            Bat(4), Bat(4), Bat(4), Bat(4), Bat(4), Bat(4), Bat(4), Bat(4), Bat(4)), Station);
        Assert.NotNull(d[6].State.RebasedAt);
        Assert.Null(d[10].State.RebasedAt);
        Assert.Contains("hibernate", Flat(d.Skip(11)));
    }

    // ---- one named test per rule of design.md sections 3, 5 and 6

    // design.md §5 Mains, eco and manual control: an incomplete reading neither advances nor resets the mains-lost timer, even when it shows AC input.
    [Fact]
    public void Incomplete_reading_with_input_keeps_the_mains_lost_timer()
    {
        var incomplete = new Reading(80, 350, null, "BYPASS_OUTPUT");
        var d = Step([Bat(80), incomplete, Bat(80), Bat(80)]);
        Assert.StartsWith("incomplete", d[1].Rejected, StringComparison.Ordinal);
        Assert.Equal(T0, d[1].State.OffSince);
        Assert.Null(d[1].State.OnSince);
        Assert.Equal("eco-on", Acts(d[3]));   // the cut begins 300 s after the first reading, not after the incomplete one
    }

    // design.md §5 Mains, eco and manual control: an incomplete reading neither advances nor resets the mains-back timer.
    [Fact]
    public void Incomplete_reading_on_battery_keeps_the_mains_back_timer()
    {
        var incomplete = new Reading(60, 0, null, "INVERTER_OUTPUT");
        var d = Step(Seq(Pre(60), Bat(60), Grid(60), incomplete, Grid(60), Grid(60)), Station);
        Assert.StartsWith("incomplete", d[5].Rejected, StringComparison.Ordinal);
        Assert.Equal(T0.AddMinutes(8), d[5].State.OnSince);
        Assert.Null(d[5].State.OffSince);
        Assert.Equal("mains-back", Acts(d[7]));
    }

    // design.md §6 Battery levels and hibernate: the stop level is 10 %: 11 % never gives it, 10 % does.
    [Fact]
    public void Stop_comes_at_10_percent_and_not_at_11()
    {
        var at11 = Flat(Step(Seq(Pre(11), Bat(11), Bat(11), Bat(11), Bat(11)), Station)).ToList();
        Assert.Contains("emergency-on", at11);
        Assert.DoesNotContain("stop", at11);
        var at10 = Flat(Step(Seq(Pre(10), Bat(10), Bat(10), Bat(10), Bat(10)), Station)).ToList();
        Assert.Contains("stop", at10);
        Assert.DoesNotContain("hibernate-warning", at10);
    }

    // design.md §6 Battery levels and hibernate: the warning level is 7 %: 8 % never gives it, 7 % does (and never a hibernate above 5 %).
    [Fact]
    public void Warning_comes_at_7_percent_and_not_at_8()
    {
        var at8 = Flat(Step(Seq(Pre(8), Bat(8), Bat(8), Bat(8), Bat(8)), Station)).ToList();
        Assert.Contains("stop", at8);
        Assert.DoesNotContain("hibernate-warning", at8);
        var at7 = Flat(Step(Seq(Pre(7), Bat(7), Bat(7), Bat(7), Bat(7), Bat(7), Bat(7)), Station)).ToList();
        Assert.Contains("hibernate-warning", at7);
        Assert.DoesNotContain("hibernate", at7);
    }

    // design.md §3 Readings: the output mode must be present AND parseable: a label or a raw enum text is incomplete.
    [Theory]
    [InlineData("inverter output")]
    [InlineData("OutputMode.INVERTER_OUTPUT")]
    [InlineData("INVERTER OUTPUT")]
    public void Unparseable_output_mode_is_incomplete_no_level_no_battery(string mode)
    {
        var r = new Reading(50, 0, 220, mode);
        Assert.StartsWith("incomplete (no valid output mode)", StationDecision.Check(r, new StationState(), T0, Cfg()), StringComparison.Ordinal);
        var d = Step(Seq(Pre(50), Bat(50), r));
        Assert.StartsWith("incomplete", d[4].Rejected, StringComparison.Ordinal);
        Assert.Null(d[4].Battery);
        Assert.Equal(1, d[4].State.Misses);
    }

    // design.md §3 Readings: the rate and the floor are the configured ones, not fixed numbers.
    [Fact]
    public void Plausibility_uses_the_configured_rate_and_floor()
    {
        var c = Cfg();
        c.SocRatePerMin = 2;
        c.SocJumpFloor = 1;
        var s = new StationState { LastSoc = 50, LastAt = T0 };
        Assert.Equal("", StationDecision.Check(Bat(49), s, T0.AddSeconds(30), c));                                       // the floor: 1 point
        Assert.StartsWith("implausible", StationDecision.Check(Bat(48.9), s, T0.AddSeconds(30), c), StringComparison.Ordinal);
        Assert.Equal("", StationDecision.Check(Bat(46), s, T0.AddSeconds(120), c));                                      // 2 a minute
        Assert.StartsWith("implausible", StationDecision.Check(Bat(45.9), s, T0.AddSeconds(120), c), StringComparison.Ordinal);
    }

    // design.md §3 Readings: three rejected readings that do not agree with each other within the rate move nothing.
    [Fact]
    public void Three_rejected_readings_that_disagree_do_not_move_the_baseline()
    {
        var d = Step([Bat(80), new S(Bat(0), Dt: 60), new S(Bat(40), Dt: 60), new S(Bat(0), Dt: 60), new S(Bat(0), Dt: 60), new S(Bat(0), Dt: 60)]);
        Assert.All(d.Skip(1).Take(4), x => Assert.StartsWith("implausible", x.Rejected, StringComparison.Ordinal));
        Assert.Equal(80, d[4].State.LastSoc);
        Assert.Equal("", d[5].Rejected);   // 0, 0, 0: now three agree
        Assert.Equal(0, d[5].State.LastSoc);
    }

    // design.md §3 Readings: "three in a row": an accepted reading in the run starts the count over.
    [Fact]
    public void Rejected_run_broken_by_an_accepted_reading_starts_over()
    {
        var d = Step([Bat(80), new S(Bat(0), Dt: 60), new S(Bat(0), Dt: 60), new S(Bat(80), Dt: 60), new S(Bat(0), Dt: 60), new S(Bat(0), Dt: 60), new S(Bat(0), Dt: 60)]);
        Assert.Equal("", d[3].Rejected);
        Assert.StartsWith("implausible", d[5].Rejected, StringComparison.Ordinal);   // only two since the accepted one
        Assert.Equal(80, d[5].State.LastSoc);
        Assert.Equal("", d[6].Rejected);
        Assert.Equal(0, d[6].State.LastSoc);
    }

    // design.md §3 and §6: a rebased reading neither continues nor breaks an agreement already running.
    [Fact]
    public void Rebased_reading_neither_continues_nor_breaks_a_running_agreement()
    {
        // 20 % at t8 starts the emergency streak; 0, 0 rejected, the third 0 (t11) is rebased; the 0 at t12 is accepted.
        var d = Step(Seq(Pre(25), Bat(25), Bat(20), Every60(Bat(0), 3), new S(Bat(0), Dt: 60)), Station);
        Assert.Equal("", d[7].Rejected);
        Assert.Equal(T0.AddMinutes(8), d[7].State.Since[(int)LevelStep.Emergency]);
        Assert.DoesNotContain(StationAction.EmergencyOn, d[7].Actions);   // the rebased reading did not complete it
        Assert.Contains(StationAction.EmergencyOn, d[8].Actions);         // nor break it: the 20 % from t8 still counts
    }

    // design.md §3 Readings: a rebased reading advances no mains timer (on battery: no mains-lost start; with input: no mains-back start).
    [Fact]
    public void Rebased_reading_advances_no_mains_timer()
    {
        var off = Step(Seq(Grid(80), Every60(Bat(0), 3), new S(Bat(0), Dt: 60)));
        Assert.Equal("", off[3].Rejected);
        Assert.Null(off[3].State.OffSince);
        Assert.Equal(T0, off[3].State.OnSince);
        Assert.Equal(off[4].State.LastAt, off[4].State.OffSince);   // the timer starts at the first reading that counts

        var on = Step(Seq(Pre(80), Bat(80), Every60(Grid(0), 3), new S(Grid(0), Dt: 60)), Station);
        Assert.Equal("", on[6].Rejected);
        Assert.Null(on[6].State.OnSince);
        Assert.Equal(T0, on[6].State.OffSince);
        Assert.True(on[6].State.Cut);
        Assert.Equal(on[7].State.LastAt, on[7].State.OnSince);
    }

    // design.md §3 Readings: a rebased reading writes no battery reading to the flag.
    [Fact]
    public void Rebased_reading_gives_no_battery_report()
    {
        var d = Step(Seq(Pre(80), Bat(80), Every60(Bat(0), 3), new S(Bat(0), Dt: 60)), Station);
        Assert.Equal("", d[6].Rejected);
        Assert.Null(d[6].Battery);
        Assert.NotNull(d[7].Battery);
    }

    // design.md §3 Readings: the LATEST rebase of the cut is remembered; a second one moves the time.
    [Fact]
    public void Second_rebase_in_a_cut_moves_the_remembered_time_to_the_latest()
    {
        var d = Step(Seq(Pre(80), Bat(80), Every60(Bat(0), 3), Every60(Bat(40), 3)), Station);
        var first = d[6].State.LastAt;
        Assert.Equal(first, d[6].State.RebasedAt);
        Assert.Equal("", d[9].Rejected);
        Assert.Equal(40, d[9].State.LastSoc);   // the second rebase moved the baseline
        Assert.Equal(first!.Value.AddMinutes(3), d[9].State.RebasedAt);
    }

    // design.md §3 Readings: emergency, stop and warning may fire on readings that follow a rebase; only hibernate waits.
    [Fact]
    public void Lower_levels_fire_on_readings_after_a_rebase()
    {
        var d = Step(Seq(Pre(80), Bat(80), Every60(Bat(4), 3), Every60(Bat(4), 6)), Station);
        Assert.NotNull(d[6].State.RebasedAt);
        Assert.Equal(["emergency-on", "stop", "hibernate-warning"], Flat(d));
    }

    // design.md §6 Battery levels and hibernate: a rejected reading between two 20 % readings neither breaks nor counts toward the agreement.
    [Fact]
    public void Rejected_reading_between_two_20_percent_readings_does_not_break_the_agreement()
    {
        var d = Step(Seq(Pre(25), Bat(25), Bat(20), new S(Bat(0), Dt: 30), new S(Bat(20), Dt: 60)), Station);
        Assert.StartsWith("implausible", d[5].Rejected, StringComparison.Ordinal);
        Assert.Equal(T0.AddMinutes(8), d[5].State.Since[(int)LevelStep.Emergency]);
        Assert.Contains(StationAction.EmergencyOn, d[6].Actions);   // 90 s after the first 20 %
    }

    // design.md §6 Battery levels and hibernate: an accepted reading with unknown mains leaves the mains-back timer as it is.
    [Fact]
    public void Accepted_reading_with_unknown_mains_keeps_the_mains_back_timer()
    {
        var unknown = new Reading(60, 0, 220, "OTHER");
        var d = Step(Seq(Pre(60), Bat(60), Grid(60), unknown, Grid(60), Grid(60)), Station);
        Assert.Equal("", d[5].Rejected);
        Assert.Equal(T0.AddMinutes(8), d[5].State.OnSince);
        Assert.Equal("mains-back", Acts(d[7]));
    }

    // design.md §5 Mains, eco and manual control: mains on, a gap, mains on again is no mains change: the page's Eco holds.
    [Fact]
    public void Gap_that_finds_mains_still_on_keeps_a_manual_Eco()
    {
        var d = Step([Grid(100), Grid(100), new S(Grid(100), Dt: 900), Grid(100), Grid(100), Grid(100), Grid(100)],
            Flag("eco", "manual", "", T0), before: At(1, ManualMode.Eco));
        Assert.Equal(ManualMode.Eco, d[^1].State.Manual);
        Assert.DoesNotContain("mains-back", Flat(d));
    }

    // design.md §6 Battery levels and hibernate: "more than 2 minutes" after the hibernate: 120 s does not re-arm it, 121 s does.
    [Theory]
    [InlineData(120, true)]
    [InlineData(121, false)]
    public void Hibernate_re_arms_only_more_than_120s_after_it(int afterSec, bool stillFired)
    {
        var d = Step(Seq(Pre(4), Bat(4), Bat(4), Bat(4), Bat(4), Bat(4), new S(Bat(4), Dt: afterSec)), Station);
        Assert.Equal("hibernate", Acts(d[7]));
        Assert.Equal(stillFired, d[8].State.Fired[(int)LevelStep.Hibernate]);
        Assert.DoesNotContain(StationAction.Hibernate, d[8].Actions);
    }

    // design.md §6 Battery levels and hibernate: after a rebase the confirmation is one more precondition, never a replacement: confirmation over but the
    // warning less than 180 s back = no hibernate.
    [Fact]
    public void Rebase_confirmation_over_but_the_warning_under_180s_back_no_hibernate()
    {
        var c = Cfg();
        c.RebaseHibernateConfirmSec = 120;
        // rebase at t9, streak from t10, emergency t12, stop t13, warning t14; the confirmation is over from t12.
        var d = Step(Seq(Pre(80), Bat(80), Every60(Bat(0), 3), Every60(Bat(0), 8)), Station, cfg: c);
        Assert.Equal("hibernate-warning", Acts(d[11]));
        Assert.Equal("|", AllActs(d, 12, 13));
        Assert.Null(d[13].State.HibernateDelayedUntil);   // not held back by the rebase: the warning's wait holds it
        Assert.Equal("hibernate", Acts(d[14]));
        Assert.Equal(180, (d[14].State.LastAt!.Value - d[14].State.WarnedAt!.Value).TotalSeconds);
    }

    // design.md §6 Battery levels and hibernate: one hibernate-delayed event per cut, remembered with the rebase through a gap reset.
    [Fact]
    public void One_hibernate_delayed_event_across_a_gap_in_the_same_cut()
    {
        var d = Step(Seq(Pre(80), Bat(80), Every60(Bat(0), 3), Every60(Bat(0), 8), new S(Bat(0), Dt: 700), Every60(Bat(0), 30)), Station, cfg: LongConfirm());
        int first = d.FindIndex(x => x.Actions.Contains(StationAction.HibernateDelayed));
        Assert.True(first >= 0 && first < 15, "held back before the gap");
        Assert.False(d[15].State.Cut);   // the gap reset the cut
        Assert.Contains(d.Skip(16), x => x.State.HibernateDelayedUntil is not null);   // held back again after the gap, no second event
        Assert.Single(Flat(d), "hibernate-delayed");
        Assert.Contains("hibernate", Flat(d.Skip(16)));
    }
}
