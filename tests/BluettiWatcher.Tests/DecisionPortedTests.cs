using System.Text.Json;
using BluettiWatcher.Decision;
using static BluettiWatcher.Tests.Fx;

namespace BluettiWatcher.Tests;

/// <summary>The decision, case by case.</summary>
public class DecisionPortedTests
{
    /// <summary>A reading one minute after the one before.</summary>
    private static S M(Reading r) => new(r, Dt: 60);

    // ---- mains lost: the grid off for mainsLostSec in accepted readings
    // design.md §5 Mains, eco and manual control
    [Fact]
    public void Grid_off_under_180s_no_cut_at_180s_the_cut_begins_with_eco_on()
    {
        var d = Step([M(Bat(80)), M(Bat(80)), M(Bat(80)), M(Bat(80)), M(Bat(80))], cfg: Defaults());
        Assert.Equal("|||eco-on|eco-on", AllActs(d));
        Assert.False(d[2].State.Cut);
        Assert.True(d[3].State.Cut);
        Assert.Equal(T0.AddSeconds(180), d[3].State.CutSince);
    }

    [Fact]
    public void Cut_reading_goes_to_the_flag_as_battery_level_ok_mains_off()
    {
        var d = Step([Bat(80)]);
        Assert.Equal(new BatteryReport(80, Level.Ok, false), d[0].Battery);
    }

    // design.md §5 Mains, eco and manual control
    [Fact]
    public void No_answer_neither_advances_nor_resets_the_mains_timer()
    {
        var d = Step([Bat(80), Miss, Miss, Bat(80)]);
        Assert.Equal("eco-on", Acts(d[3]));
        Assert.Equal(T0, d[3].State.OffSince);
    }

    // design.md §5 Mains, eco and manual control
    [Fact]
    public void Accepted_reading_with_grid_on_restarts_the_mains_lost_timer()
    {
        var d = Step([Bat(80), Bat(80), Grid(80), Bat(80), Bat(80), Bat(80), Bat(80)]);
        Assert.Equal("||||||eco-on", AllActs(d));
    }

    // design.md §3 Readings
    [Fact]
    public void Gap_past_maxGapSec_restarts_the_mains_lost_timer()
    {
        var d = Step([Bat(80), Bat(80), new S(Bat(80), Dt: 660), Bat(80), Bat(80), Bat(80)]);
        Assert.Equal("|||||eco-on", AllActs(d));
    }

    [Fact]
    public void Manual_eco_during_a_cut_is_left_alone() =>
        Assert.Equal("||||", AllActs(Step(Seq(Pre(80), Bat(80), Bat(79)), Flag("eco", "manual"))));

    [Fact]
    public void Manual_emergency_during_a_cut_is_left_alone() =>
        Assert.Equal("", Acts(Step(Seq(Pre(80), Bat(80)), Flag("emergency", "manual"))[3]));

    [Fact]
    public void Its_own_eco_is_not_set_twice() =>
        Assert.Equal("", Acts(Step(Seq(Pre(80), Bat(80)), Station)[3]));

    [Fact]
    public void Unreadable_flag_no_mode_action() =>
        Assert.Equal("", Acts(Step(Seq(Pre(80), Bat(80)), FlagView.Unreadable)[3]));

    // a manual off during the cut holds until mains returns
    [Fact]
    public void Manual_off_during_the_cut_eco_is_not_switched_back_on()
    {
        var offDuring = Flag("normal", "manual", "manual", T0.AddMinutes(7));
        var d = Step(Seq(Pre(80), new S(Bat(80), Flag()), new S(Bat(79), Station), new S(Bat(78), offDuring), new S(Bat(77), offDuring)));
        Assert.Equal("eco-on", Acts(d[3]));
        Assert.Equal("", Acts(d[5]));
        Assert.Equal("", Acts(d[6]));
        Assert.True(d[6].State.Held);
    }

    [Fact]
    public void Manual_off_from_before_the_cut_does_not_hold()
    {
        var d = Step(Seq(Pre(80), new S(Bat(80), Flag("normal", "manual", "manual", T0.AddHours(-5)))));
        Assert.Equal("eco-on", Acts(d[3]));
        Assert.False(d[3].State.Held);
    }

    [Fact]
    public void Manual_eco_that_ends_during_the_cut_the_station_takes_over()
    {
        var d = Step(Seq(Pre(80), new S(Bat(80), Flag("eco", "manual")), new S(Bat(79), Flag("normal", "manual", "", T0.AddMinutes(7)))));
        Assert.Equal("eco-on", Acts(d[4]));
    }

    [Fact]
    public void Off_by_the_station_itself_is_no_hold()
    {
        var f = Flag("normal", "manual", "station", T0.AddMinutes(7));
        var d = Step(Seq(Pre(80), new S(Bat(80), f), new S(Bat(79), f)));
        Assert.Equal("eco-on", Acts(d[4]));
    }

    // ---- mains back: the grid on for mainsBackSec, then every mode goes, whoever set it
    // design.md §5 Mains, eco and manual control
    [Fact]
    public void Mains_back_nothing_under_180s_mains_back_at_180s_the_cut_clears()
    {
        var d = Step([M(Bat(60)), M(Bat(60)), M(Bat(60)), M(Bat(60)), M(Grid(60)), M(Grid(60)), M(Grid(61)), M(Grid(61))], Station, cfg: Defaults());
        Assert.Equal("|||mains-back", AllActs(d, 4, 7));
        Assert.False(d[7].State.Cut);
        Assert.Equal(Level.Ok, d[7].State.Level);
    }

    [Fact]
    public void Grid_blip_shorter_than_180s_is_not_mains_back_the_timer_restarts()
    {
        var d = Step([M(Bat(60)), M(Bat(60)), M(Bat(60)), M(Bat(60)), M(Grid(60)), M(Grid(60)), M(Bat(60)), M(Grid(60)), M(Grid(60)), M(Grid(60)), M(Grid(60))], Station, cfg: Defaults());
        Assert.Equal("|||||", AllActs(d, 4, 9));
        Assert.Equal("mains-back", Acts(d[10]));
    }

    // design.md §5 Mains, eco and manual control
    [Fact]
    public void No_answer_neither_advances_nor_resets_the_mains_back_timer()
    {
        var d = Step(Seq(Pre(60), Bat(60), Grid(60), Miss, Grid(60), Grid(60)), Station);
        Assert.Equal("", Acts(d[6]));
        Assert.Equal("mains-back", Acts(d[7]));
        Assert.Equal(T0.AddMinutes(8), d[7].State.OnSince);
    }

    // design.md §5 Mains, eco and manual control
    [Theory]
    [InlineData("eco", "manual")]
    [InlineData("emergency", "manual")]
    [InlineData("emergency", "station")]
    public void Mains_back_lifts_every_mode(string mode, string source)
    {
        var d = Step(Seq(Pre(15), Bat(15), Grid(15), Grid(15), Grid(16), Grid(16)), Flag(mode, source));
        Assert.Equal("mains-back", Acts(d[7]));
    }

    [Fact]
    public void Mains_on_nothing_set_no_action_ever()
    {
        var d = Step([Grid(100), Grid(100), Grid(100), Grid(100)]);
        Assert.Equal("|||", AllActs(d));
        Assert.True(d[3].Battery!.Mains);
    }

    [Fact]
    public void Manual_eco_set_while_mains_is_on_no_cut_is_never_touched() =>
        Assert.Equal("|||", AllActs(Step([Grid(100), Grid(100), Grid(100), Grid(100)], Flag("eco", "manual"))));

    [Fact]
    public void Its_own_eco_with_no_cut_known_goes_when_mains_has_been_on_180s() =>
        Assert.Equal("||mains-back", AllActs(Step([M(Grid(100)), M(Grid(100)), M(Grid(100)), M(Grid(100))], Station, cfg: Defaults()), 1, 3));

    [Fact]
    public void Both_mains_confirmations_default_to_180s()
    {
        Assert.Equal(180, new StationConfig().MainsLostSec);
        Assert.Equal(180, new StationConfig().MainsBackSec);
    }

    // ---- battery levels: agreement, one level per poll, the hibernate wait
    // design.md §6 Battery levels and hibernate
    [Fact]
    public void Level_20_one_reading_fires_nothing_60s_apart_no_90s_apart_yes_never_again()
    {
        var d = Step(Seq(Pre(21), Bat(21), Bat(20), new S(Bat(20), Dt: 60), new S(Bat(19.5), Dt: 30), Bat(19.5)), Station);
        Assert.Equal("|||emergency-on|", AllActs(d, 3, 7));
        Assert.Equal(Level.Low, d[6].State.Level);
    }

    // Misses neither break nor count toward agreement.
    // design.md §6 Battery levels and hibernate
    [Fact]
    public void Alternating_accepted_4_and_misses_reach_every_level()
    {
        var d = Step(Seq(Pre(4), Miss, Bat(4), Miss, Bat(4), Miss, Bat(4), Miss, Bat(4)), Station);
        Assert.Equal(["emergency-on", "stop", "hibernate-warning", "hibernate"], Flat(d));
    }

    // design.md §3 Readings
    [Fact]
    public void Gap_past_maxGapSec_starts_everything_from_zero()
    {
        var d = Step(Seq(Pre(21), Bat(21), Bat(20), new S(Bat(20), Dt: 700), Bat(20), Bat(20), Bat(20)), Station);
        Assert.False(d[5].State.Cut);
        Assert.Equal("||", AllActs(d, 5, 7));
        Assert.Equal("emergency-on", Acts(d[8]));
    }

    [Fact]
    public void Level_20_with_an_emergency_already_on_level_low_no_action()
    {
        var d = Step(Seq(Pre(20), Bat(20)), Flag("emergency", "manual"));
        Assert.Equal("", Acts(d[3]));
        Assert.Equal(Level.Low, d[3].State.Level);
        Assert.Equal(Level.Low, d[3].Battery!.Level);
    }

    [Fact]
    public void Level_20_agreed_when_the_cut_begins_emergency_on_replaces_eco_on() =>
        Assert.Equal("emergency-on", Acts(Step(Seq(Pre(20), Bat(20)))[3]));

    // design.md §6 Battery levels and hibernate
    [Fact]
    public void Straight_to_4_one_level_per_poll_in_order_hibernate_3_min_after_warning_each_once()
    {
        var d = Step(Seq(Pre(4), Bat(4), Bat(4), Bat(4), Bat(4), Bat(4), Bat(4), Bat(4)), Station);
        Assert.Equal("emergency-on|stop|hibernate-warning||hibernate||", AllActs(d, 3, 9));
        Assert.Equal(Level.Low, d[3].Battery!.Level);
        Assert.Equal(Level.Critical, d[4].Battery!.Level);
        Assert.Equal(Level.Hibernating, d[5].Battery!.Level);
    }

    // design.md §6 Battery levels and hibernate
    [Fact]
    public void Level_6_the_warning_never_a_hibernate_above_the_threshold()
    {
        var acts = Flat(Step(Seq(Pre(6), Bat(6), Bat(6), Bat(6), Bat(6), Bat(6)), Station)).ToList();
        Assert.Contains("hibernate-warning", acts);
        Assert.DoesNotContain("hibernate", acts);
    }

    // design.md §6 Battery levels and hibernate
    [Fact]
    public void Hibernate_waits_hibernateAfterWarnSec_after_the_warning()
    {
        var d = Step(Seq(Pre(5), Bat(5), Bat(5), Bat(5), new S(Bat(5), Dt: 60), new S(Bat(5), Dt: 60), new S(Bat(5), Dt: 60)), Station);
        Assert.Equal("hibernate-warning", Acts(d[5]));
        Assert.Equal("", Acts(d[6]));
        Assert.Equal("", Acts(d[7]));
        Assert.Equal("hibernate", Acts(d[8]));
    }

    [Fact]
    public void Levels_fire_only_once_the_cut_has_begun()
    {
        var d = Step(Seq(Pre(3), Bat(3)), Station);
        Assert.Equal("||", AllActs(d, 0, 2));
        Assert.Equal("emergency-on", Acts(d[3]));
    }

    [Fact]
    public void Low_battery_on_mains_charging_no_action_level_ok()
    {
        var d = Step([Grid(3), Grid(3), Grid(3), Grid(4)]);
        Assert.Equal("|||", AllActs(d));
        Assert.Equal(Level.Ok, d[3].State.Level);
    }

    [Fact]
    public void Mains_back_clears_the_cut_the_next_cut_fires_again()
    {
        var d = Step(Seq(Pre(4), Bat(4), Bat(4), Bat(4), Bat(4), Bat(4), Grid(5), Grid(6), Grid(6), Grid(7), Bat(7), Bat(6), Bat(6), Bat(6), Bat(6), Bat(6)),
            Flag("emergency", "station"));
        Assert.Equal("mains-back", Acts(d[11]));
        Assert.Equal(Level.Ok, d[11].State.Level);
        Assert.True(d[16].State.Cut);
        Assert.Contains("stop", Flat(d.Skip(16).Take(2)));
    }

    // ---- a stale cut ends: a gap past maxGapSec resets the WHOLE state
    private static object[] Fall()
    {
        var steps = new List<object> { new S(Bat(90), Dt: 72000) };
        for (int soc = 88; soc >= 2; soc -= 2) steps.Add(Bat(soc));
        for (int k = 0; k < 6; k++) steps.Add(Bat(1));
        return [.. steps];
    }

    // design.md §3 Readings
    [Fact]
    public void Repro_8_polls_at_4_then_20h_later_90_falling_the_old_cut_is_gone()
    {
        var d = Step(Seq(Pre(4), Bat(4), Bat(4), Bat(4), Bat(4), Bat(4), Fall()), Station);
        Assert.True(d[7].State.Cut);
        Assert.False(d[8].State.Cut);
        Assert.Equal(Level.Ok, d[8].Battery!.Level);
        Assert.False(d[8].State.Fired[(int)LevelStep.Hibernate]);
        Assert.False(d[10].State.Cut);
        Assert.True(d[11].State.Cut);
    }

    [Fact]
    public void Repro_the_new_cut_fires_its_levels_again_down_to_hibernate()
    {
        var d = Step(Seq(Pre(4), Bat(4), Bat(4), Bat(4), Bat(4), Bat(4), Fall()), Station);
        var acts = Flat(d.Skip(8)).ToList();
        Assert.Contains("emergency-on", acts);
        Assert.Contains("stop", acts);
        Assert.Contains("hibernate-warning", acts);
        Assert.Contains("hibernate", acts);
    }

    // ---- a level counts only once its action succeeded
    // design.md §6 Battery levels and hibernate
    [Fact]
    public void Failed_emergency_on_is_proposed_again_never_marked_fired()
    {
        var d = Step(Seq(Pre(20), Bat(20), Bat(20)), Station, [StationAction.EmergencyOn]);
        Assert.Equal("emergency-on", Acts(d[3]));
        Assert.Equal("emergency-on", Acts(d[4]));
        Assert.False(d[4].State.Fired[(int)LevelStep.Emergency]);
        // The level is reached on readings and time; only its action is still owed.
        Assert.Equal(Level.Low, d[4].State.Level);
    }

    // design.md §6 Battery levels and hibernate
    [Fact]
    public void Refused_hibernate_is_proposed_again_on_every_qualifying_poll()
    {
        var d = Step(Seq(Pre(4), Bat(4), Bat(4), Bat(4), Bat(4), Bat(4), Bat(4), Bat(4)), Station, [StationAction.Hibernate]);
        Assert.Equal("emergency-on|stop|hibernate-warning||hibernate|hibernate|hibernate", AllActs(d, 3, 9));
    }

    // design.md §6 Battery levels and hibernate
    [Fact]
    public void Hibernate_retried_needs_its_agreement_again()
    {
        var d = Step(Seq(Pre(4), Bat(4), Bat(4), Bat(4), Bat(4), Bat(4), Bat(6), Bat(4), Bat(4)), Station, [StationAction.Hibernate]);
        Assert.Equal("hibernate", Acts(d[7]));
        Assert.Equal("", Acts(d[8]));
        Assert.Equal("", Acts(d[9]));
        Assert.Equal("hibernate", Acts(d[10]));
    }

    // ---- a lift owed after mains back stays owed until the flag shows it done
    // design.md §5 Mains, eco and manual control
    [Fact]
    public void Failed_mains_back_off_is_tried_again_done_once_normal_a_later_manual_eco_is_left_alone()
    {
        var manual = Flag("eco", "manual");
        var d = Step(Seq(Pre(60), new S(Bat(60), manual), new S(Grid(60), manual), new S(Grid(60), manual), new S(Grid(60), manual), new S(Grid(60), manual),
            new S(Grid(60), manual), new S(Grid(60), Flag()), new S(Grid(60), manual)), fail: [StationAction.MainsBack]);
        Assert.Equal("mains-back|mains-back||", AllActs(d, 7, 10));
        Assert.True(d[8].State.OwedLift);
        Assert.False(d[9].State.OwedLift);
    }

    // ---- plausibility
    // design.md §3 Readings
    [Fact]
    public void Incident_0_twice_after_80_is_implausible_no_level_no_battery_a_miss()
    {
        var d = Step(Seq(Pre(80), Bat(80), new S(Bat(0), Dt: 30), new S(Bat(0), Dt: 30)));
        Assert.Equal("|", AllActs(d, 4, 5));
        Assert.Null(d[5].Battery);
        Assert.StartsWith("implausible (0 % after 80 % 60 s earlier)", d[5].Rejected, StringComparison.Ordinal);
        Assert.Equal(2, d[5].State.Misses);
        Assert.Equal(Level.Ok, d[5].State.Level);
        Assert.StartsWith("implausible", d[5].State.Rejected, StringComparison.Ordinal);
    }

    // design.md §3 Readings
    [Fact]
    public void Plausible_3_points_within_a_minute_5_a_minute_beyond_more_rejected()
    {
        var d = Step([Bat(50), new S(Bat(47), Dt: 30), new S(Bat(43), Dt: 30), new S(Bat(36), Dt: 120), new S(Bat(22), Dt: 120)]);
        Assert.Equal("", d[1].Rejected);
        Assert.StartsWith("implausible", d[2].Rejected, StringComparison.Ordinal);
        Assert.Equal("", d[3].Rejected);
        Assert.StartsWith("implausible", d[4].Rejected, StringComparison.Ordinal);
    }

    // A gap never resets the baseline; three agreeing rejected readings move it.
    // design.md §3 Readings
    [Fact]
    public void Three_rejected_readings_that_agree_become_the_baseline()
    {
        var d = Step([Bat(80), new S(Bat(0), Dt: 30), new S(Bat(0), Dt: 300), new S(Bat(0), Dt: 400)]);
        Assert.StartsWith("implausible", d[1].Rejected, StringComparison.Ordinal);
        Assert.StartsWith("implausible", d[2].Rejected, StringComparison.Ordinal);
        Assert.Equal("", d[3].Rejected);
        Assert.Equal(0, d[3].State.LastSoc);
    }

    // design.md §3 Readings
    [Fact]
    public void Plausibility_holds_across_a_gap_0_after_11_minutes_of_silence_is_rejected()
    {
        var d = Step([Bat(80), Miss, Miss, Miss, Miss, Miss, new S(Bat(0), Dt: 60), new S(Bat(80), Dt: 60), Bat(80)]);
        Assert.StartsWith("implausible (0 % after 80 % 660 s earlier)", d[6].Rejected, StringComparison.Ordinal);
        Assert.Equal("", d[7].Rejected);
        Assert.Equal("", d[8].Rejected);
        Assert.Equal(80, d[8].State.LastSoc);
    }

    private static readonly Reading[] IncompleteReadings =
    [
        new(50, null, 220, "INVERTER_OUTPUT"),   // no AC input power
        new(50, 0, null, "INVERTER_OUTPUT"),     // no AC output power
        new(50, 0, 220, ""),                     // no output mode
        new(120, 0, 220, "INVERTER_OUTPUT"),     // a SOC over 100
        new(null, 0, 220, "INVERTER_OUTPUT"),    // a null SOC
    ];

    // design.md §3 Readings
    [Theory]
    [InlineData(0, "no AC input power")]
    [InlineData(1, "no AC output power")]
    [InlineData(2, "no output mode")]
    [InlineData(3, "a SOC over 100")]
    [InlineData(4, "a null SOC")]
    public void Incomplete_unknown_no_action_no_battery_the_cut_as_it_was(int index, string why)
    {
        var r = IncompleteReadings[index];
        var d = Step(Seq(Pre(50), Bat(50), r, r));
        Assert.Equal("|", AllActs(d, 4, 5));
        Assert.Null(d[5].Battery);
        Assert.StartsWith("incomplete", d[5].Rejected, StringComparison.Ordinal);
        Assert.True(d[5].State.Cut, why);
    }

    // design.md §6 Battery levels and hibernate
    [Fact]
    public void No_AC_input_and_no_inverter_output_unknown_the_mains_timer_untouched()
    {
        var other = new Reading(50, 0, 220, "OTHER");
        var d = Step([Bat(50), Bat(50), other, other, Bat(50)]);
        Assert.Equal("|||", AllActs(d, 0, 3));
        Assert.False(d[3].State.Cut);
        Assert.Equal("eco-on", Acts(d[4]));
    }

    // ---- no answer
    [Fact]
    public void Misses_no_action_three_in_a_row_silent_the_next_answer_clears_it()
    {
        var d = Step(Seq(Pre(60), Bat(60), Miss, Miss, Miss, Bat(59)), Station);
        Assert.Equal("", Acts(d[4]));
        Assert.Null(d[4].Battery);
        Assert.True(d[4].State.Cut);
        Assert.Equal(1, d[4].State.Misses);
        Assert.False(d[4].State.Silent);
        Assert.Equal(2, d[5].State.Misses);
        Assert.False(d[5].State.Silent);
        Assert.True(d[6].State.Silent);
        Assert.Equal(T0.AddMinutes(12), d[6].State.SilentSince);
        Assert.False(d[7].State.Silent);
        Assert.Equal(0, d[7].State.Misses);
    }

    // ---- the next poll
    [Fact]
    public void Next_poll_120s_30s_on_battery_under_25_a_miss_there_included_120s_on_mains()
    {
        var d = Step(Seq(Pre(25), Bat(25), Bat(24), Miss, Grid(24)), Station);
        Assert.Equal([120, 30, 30, 120], d.Skip(3).Select(x => x.DelaySec));
    }

    // ---- pure
    [Fact]
    public void The_state_passed_in_is_never_changed()
    {
        var before = new StationState();
        string json = JsonSerializer.Serialize(before, StationJson.Default.StationState);
        StationDecision.Decide(Bat(4), before, Flag(), T0, Cfg());
        Assert.Equal(json, JsonSerializer.Serialize(before, StationJson.Default.StationState));
    }

    [Fact]
    public void State_read_back_from_JSON_carries_the_timers_and_the_streak()
    {
        var d = Step(Pre(20), Station);
        var round = JsonSerializer.Deserialize(JsonSerializer.Serialize(d[2].State, StationJson.Default.StationState), StationJson.Default.StationState);
        Assert.Equal("emergency-on", Acts(StationDecision.Decide(Bat(20), round, Station, T0.AddMinutes(6), Cfg())));
    }

    [Fact]
    public void Damaged_state_starts_fresh()
    {
        var damaged = new StationState { Since = new DateTimeOffset?[1], Fired = null!, Rejected = null! };
        var d = StationDecision.Decide(Bat(80), damaged, Flag(), T0, Cfg());
        Assert.Equal("", Acts(d));
        Assert.Equal(T0, d.State.OffSince);
    }
}
