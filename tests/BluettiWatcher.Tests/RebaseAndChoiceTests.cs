using BluettiWatcher.Decision;
using static BluettiWatcher.Tests.Fx;

namespace BluettiWatcher.Tests;

/// <summary>Rebases and hibernate, page choices across mains changes, the held-back time.</summary>
public sealed class RebaseAndChoiceTests
{
    private static object[] Every60(Reading? r, int n) => Enumerable.Range(0, n).Select(_ => (object)new S(r, Dt: 60)).ToArray();

    private static int FirstHibernate(List<StationDecisionResult> d) => d.FindIndex(x => x.Actions.Contains(StationAction.Hibernate));

    // Really 80 %, a silence of 20 minutes, one 0 % accepted (it anchors the streak), then
    // 80 x3 (rebase), 0 x3 (rebase), then zeros: hibernate waits the confirmation counted from the LATEST rebase.
    [Fact]
    public void One_garbage_reading_before_two_rebases_does_not_keep_the_normal_rule()
    {
        var d = Step(Seq(Bat(80), new S(Bat(0), Dt: 1200), Every60(Bat(80), 3), Every60(Bat(0), 3), Every60(Bat(0), 12)), Station, cfg: new StationConfig());
        Assert.Equal("", d[1].Rejected);   // accepted outright: the allowance grew with the silence
        Assert.Equal("", d[4].Rejected);   // the first rebase
        Assert.Equal("", d[7].Rejected);   // the second rebase
        var latest = d[7].State.LastAt!.Value;
        int i = FirstHibernate(d);
        Assert.Equal(17, i);
        Assert.Equal(600, (d[i].State.LastAt!.Value - latest).TotalSeconds);
    }

    // suspicion (a): a forward clock step under maxGapSec must not count as 9 minutes of agreeing readings.
    [Fact]
    public void Forward_clock_step_does_not_shorten_the_rebase_confirmation()
    {
        // rebase at 6, the streak from 7, emergency 9, stop 10, the step and the warning at 11.
        var d = Step(Seq(Pre(80), Bat(80), Every60(Bat(0), 3), Every60(Bat(0), 4), new S(Bat(0), Dt: 540), Every60(Bat(0), 8)), Station, cfg: new StationConfig());
        Assert.Equal("hibernate-warning", Acts(d[11]));
        // counted: 3 x 60 s, the step as 120 s (twice the poll interval), then 5 x 60 s = 600 s at 16.
        Assert.Equal(16, FirstHibernate(d));
    }

    // suspicion (b): no rebase at all. 8 minutes of misses, then 0 % from a real 40 % is within the grown allowance.
    [Fact]
    public void Garbage_0_accepted_outright_after_a_silence_waits_the_long_confirmation()
    {
        var d = Step(Seq(Pre(40), Bat(40), Every60(null, 7), new S(Bat(0), Dt: 60), Every60(Bat(0), 12)), Station, cfg: new StationConfig());
        Assert.Equal("", d[11].Rejected);
        Assert.Equal(21, FirstHibernate(d));   // 600 s after the jump, not the 7 minutes of the normal rule
        Assert.Single(Flat(d), "hibernate-delayed");
    }

    // suspicion (b), the other side: a real drain seen again after the same silence keeps the normal rule.
    [Fact]
    public void Real_drain_seen_after_a_silence_keeps_the_normal_rule()
    {
        var d = Step(Seq(Pre(8), Bat(8), Every60(null, 7), new S(Bat(5), Dt: 60), Every60(Bat(5), 8)), Station, cfg: new StationConfig());
        Assert.Null(d[^1].State.RebasedAt);
        Assert.DoesNotContain("hibernate-delayed", Flat(d));
        Assert.InRange(FirstHibernate(d), 16, 17);   // stop 11, warning 13, hibernate 180 s later
    }

    // one hibernate-delayed event per rebase, not per cut.
    [Fact]
    public void Each_rebase_that_holds_hibernate_back_gives_its_own_event()
    {
        // 4 % real with the warning given; 40 x3 and back (two rebases, one held-back poll after them), then again.
        var d = Step(Seq(Pre(4), Bat(4), Bat(4), Bat(4), Every60(Bat(40), 3), Every60(Bat(4), 3), Every60(Bat(4), 2),
            Every60(Bat(40), 3), Every60(Bat(4), 3), Every60(Bat(4), 12)), Station);
        Assert.Equal(2, Flat(d).Count(a => a == "hibernate-delayed"));
        Assert.Contains("hibernate", Flat(d));
    }

    // an observed mains change drops a page choice whose command has not run.
    [Fact]
    public void Cut_beginning_drops_a_pending_page_choice()
    {
        var d = Step(Seq(Pre(60), Bat(60)), Flag(), before: (i, _, s) => { if (i == 0) s.ModePending = ManualMode.Auto; return s; });
        Assert.Equal(ManualMode.Auto, d[2].State.ModePending);
        Assert.True(d[3].State.Cut);
        Assert.Null(d[3].State.ModePending);
        Assert.Contains("mode-dropped", Flat([d[3]]));
        Assert.Contains("dropped", d[3].State.ModeError, StringComparison.Ordinal);
    }

    [Fact]
    public void Mains_back_drops_a_pending_page_choice()
    {
        var d = Step(Seq(Pre(60), Bat(60), Grid(60), Grid(60), Grid(60), Grid(60)), Station,
            before: (i, at, s) =>
            {
                if (i != 4) return s;
                s = StationDecision.SetManual(s, ManualMode.Eco, at.AddSeconds(-30));   // pressed during the cut
                s.ModePending = ManualMode.Eco;
                return s;
            });
        Assert.Equal(ManualMode.Eco, d[6].State.ModePending);   // mains seen back, not yet confirmed
        Assert.Null(d[7].State.ModePending);
        Assert.Equal(ManualMode.Auto, d[7].State.Manual);
        Assert.Contains("mode-dropped", Flat([d[7]]));
        Assert.Contains("Eco dropped", d[7].State.ModeError, StringComparison.Ordinal);
    }

    // a cut found again after a gap is no mains change: the pending choice stays (design.md §5 Mains, eco and manual control).
    [Fact]
    public void Cut_found_again_after_a_gap_keeps_a_pending_page_choice()
    {
        var d = Step(Seq(Pre(60), Bat(60), new S(Bat(60), Dt: 900), Bat(60), Bat(60), Bat(60)), Station,
            before: (i, _, s) => { if (i == 4) s.ModePending = ManualMode.Max; return s; });
        Assert.True(d[^1].State.Cut);
        Assert.Equal(ManualMode.Max, d[^1].State.ModePending);
    }

    // the held-back time goes as soon as a poll no longer holds hibernate back (here: mains seen back, not confirmed).
    [Fact]
    public void Hibernate_delayed_until_is_cleared_when_hibernate_is_no_longer_held_back()
    {
        // rebase at 6, the streak from 7, the warning at 11, held back from 14.
        var d = Step(Seq(Pre(22), Bat(22), Every60(Bat(4), 3), Every60(Bat(4), 9), Every60(Grid(4), 1)), Station);
        Assert.NotNull(d[15].State.HibernateDelayedUntil);
        Assert.True(d[16].State.Cut);
        Assert.Null(d[16].State.HibernateDelayedUntil);
    }

    // The state the installed version (779c22b) and 6f5f23f saved still loads; what they do not carry defaults safely.
    [Theory]
    [InlineData("""{"cut":true,"cutSince":"2026-10-06T20:06:00+03:00","offSince":"2026-10-06T20:00:00+03:00","onSince":null,"held":false,"level":"Low","since":["2026-10-06T20:00:00+03:00",null,null,null],"reached":[true,false,false,false],"fired":[true,false,false,false],"warnedAt":null,"misses":0,"silent":false,"silentSince":null,"lastSoc":18,"lastAt":"2026-10-06T20:08:00+03:00","grid":"Off","rejected":"","rejectedAt":null,"owedLift":false,"mainsBackAt":null,"manual":"Max","manualAt":"2026-10-06T20:07:00+03:00"}""", false)]
    [InlineData("""{"cut":true,"cutSince":"2026-10-06T20:06:00+03:00","offSince":"2026-10-06T20:00:00+03:00","onSince":null,"held":false,"level":"Low","since":["2026-10-06T20:00:00+03:00",null,null,null],"reached":[true,false,false,false],"fired":[true,false,false,false],"warnedAt":null,"misses":0,"silent":false,"silentSince":null,"lastSoc":18,"lastAt":"2026-10-06T20:08:00+03:00","grid":"Off","rejected":"","rejectedAt":null,"owedLift":false,"mainsBackAt":null,"cutBeforeGap":false,"hibernatedAt":null,"rejSoc":[],"rejAt":[],"rebasedAt":"2026-10-06T20:05:00+03:00","rebaseDelayNoted":true,"hibernateDelayedUntil":null,"manual":"Max","manualAt":"2026-10-06T20:07:00+03:00","modePending":"Max","modeError":""}""", true)]
    public void State_saved_by_an_older_version_still_loads(string json, bool rebased)
    {
        var s = StationState.Normalize(System.Text.Json.JsonSerializer.Deserialize(json, StationJson.Default.StationState));
        Assert.True(s.Cut);
        Assert.Equal(Level.Low, s.Level);
        Assert.Equal(ManualMode.Max, s.Manual);
        Assert.Equal(18, s.LastSoc);
        Assert.True(s.Fired[(int)LevelStep.Emergency]);
        Assert.Equal(rebased, s.RebasedAt is not null);
        Assert.Equal(0, s.RebaseConfirmedSec);   // a confirmation under way starts over: later, never earlier
        Assert.Empty(s.RejSoc);
        Assert.Equal("", s.ModeError);
        var d = StationDecision.Decide(Bat(18), s, Station, T0.AddMinutes(9), new StationConfig());
        Assert.Equal("", d.Rejected);
        Assert.True(d.State.Cut);
        Assert.DoesNotContain(StationAction.Hibernate, d.Actions);
    }
}
