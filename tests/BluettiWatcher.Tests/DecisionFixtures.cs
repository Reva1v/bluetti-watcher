using System.Globalization;
using BluettiWatcher.Decision;

namespace BluettiWatcher.Tests;

/// <summary>The decision fixtures: readings two minutes apart, threaded state.</summary>
internal static class Fx
{
    public static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-10-06T20:00:00+03:00", CultureInfo.InvariantCulture);

    /// <summary>the config the decision cases run on (poll 120 s, fast 30 s); every threshold as the service's defaults.</summary>
    /// <remarks>The mains confirmations (300 s both ways) are pinned here: the cases count readings two minutes apart.</remarks>
    public static StationConfig Cfg() => new() { PollSec = 120, FastPollSec = 30, MainsLostSec = 300, MainsBackSec = 300 };

    /// <summary>The service's own defaults, mains confirmations included.</summary>
    public static StationConfig Defaults() => new();

    public static Reading Bat(double soc) => new(soc, 0, 220, "INVERTER_OUTPUT");
    public static Reading Grid(double soc) => new(soc, 350, 220, "BYPASS_OUTPUT");
    public static FlagView Flag(string mode = "normal", string source = "manual", string by = "", DateTimeOffset? since = null) => new(mode, source, by, since);
    public static readonly FlagView Station = Flag("eco", "station");

    public const string Miss = "miss";

    /// <summary>A step with its own flag and/or delay after the one before.</summary>
    public sealed record S(Reading? R, FlagView? F = null, int Dt = 120);

    public static object[] Pre(double soc) => [Bat(soc), Bat(soc), Bat(soc)];

    public static object[] Seq(params object[] parts) => parts.SelectMany(p => p is object[] a ? a : [p]).ToArray();

    /// <summary>
    /// Feeds the steps, threading the state; every confirmed action (emergency-on, hibernate, mains-back) succeeds unless it is in
    /// <paramref name="fail"/>. <paramref name="before"/> runs on the state before each step (index, state) - the page's buttons.
    /// </summary>
    public static List<StationDecisionResult> Step(object[] steps, FlagView? flag = null, StationAction[]? fail = null, StationState? state = null,
        StationConfig? cfg = null, Func<int, DateTimeOffset, StationState, StationState>? before = null, Func<int, StationAction, bool>? failWhen = null)
    {
        cfg ??= Cfg();
        flag ??= Flag();
        fail ??= [];
        var outList = new List<StationDecisionResult>();
        var clock = T0;
        bool first = true;
        int i = 0;
        foreach (var st in steps)
        {
            Reading? reading; var f = flag; int dt = 120;
            switch (st)
            {
                case S s: reading = s.R; f = s.F ?? flag; dt = s.Dt; break;
                case string m when m == Miss: reading = null; break;
                case Reading r: reading = r; break;
                default: throw new ArgumentException("bad step");
            }
            if (!first) clock = clock.AddSeconds(dt);
            first = false;
            if (before is not null) state = before(i, clock, state ?? new StationState());
            var d = StationDecision.Decide(reading, state, f, clock, cfg);
            foreach (var a in d.Actions.Where(a => !fail.Contains(a) && failWhen?.Invoke(i, a) != true)) d = d with { State = StationDecision.Complete(d.State, a, clock) };
            state = d.State;
            outList.Add(d);
            i++;
        }
        return outList;
    }

    public static string Name(StationAction a) => a switch
    {
        StationAction.EcoOn => "eco-on",
        StationAction.EmergencyOn => "emergency-on",
        StationAction.Stop => "stop",
        StationAction.HibernateWarning => "hibernate-warning",
        StationAction.Hibernate => "hibernate",
        StationAction.HibernateDelayed => "hibernate-delayed",
        StationAction.ModeDropped => "mode-dropped",
        _ => "mains-back",
    };

    public static string Acts(StationDecisionResult d) => string.Join(",", d.Actions.Select(Name));

    public static string AllActs(IEnumerable<StationDecisionResult> ds) => string.Join("|", ds.Select(Acts));

    public static string AllActs(List<StationDecisionResult> ds, int from, int to) => AllActs(ds.Skip(from).Take(to - from + 1));

    public static IEnumerable<string> Flat(IEnumerable<StationDecisionResult> ds) => ds.SelectMany(d => d.Actions.Select(Name));
}
