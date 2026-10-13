using System.Globalization;
using System.Text.RegularExpressions;

namespace BluettiWatcher.Decision;

internal enum Grid { Unknown, On, Off }

internal enum Level { Ok, Low, Critical, Hibernating }

/// <summary>The battery levels in the order they are reached.</summary>
internal enum LevelStep { Emergency = 0, Stop = 1, Warn = 2, Hibernate = 3 }

/// <summary>
/// What the service does after a decision. EcoOn, EmergencyOn, MainsBack and Hibernate run a command whose success is
/// confirmed with <see cref="StationDecision.Complete"/>; Stop and HibernateWarning are events (the flag hears them
/// through the battery write); HibernateDelayed is an event: hibernate held back by the rebase rule, once per rebase;
/// ModeDropped is an event: an observed mains change dropped a page choice whose command had not run.
/// </summary>
internal enum StationAction { EcoOn, EmergencyOn, Stop, HibernateWarning, Hibernate, MainsBack, HibernateDelayed, ModeDropped }

/// <summary>The user's choice on the page; it holds until the next mains change or Auto.</summary>
internal enum ManualMode { Auto, Eco, Max }

/// <summary>One answer of the station. A missing or unparseable field is null.</summary>
internal sealed record Reading(double? Soc, double? InAc, double? OutAc, string? Mode,
    double? InDc = null, double? OutDc = null, double? Volt = null, double? Hz = null, string? Source = null);

/// <summary>The power-mode flag as the decision sees it; mode <c>unreadable</c> when it cannot be trusted.</summary>
internal sealed record FlagView(string Mode, string Source, string By, DateTimeOffset? SinceAt)
{
    public static FlagView Normal { get; } = new("normal", "manual", "", null);
    public static FlagView Unreadable { get; } = new("unreadable", "", "", null);
}

internal sealed record BatteryReport(double Soc, Level Level, bool Mains);

internal sealed record StationDecisionResult(StationState State, IReadOnlyList<StationAction> Actions, int DelaySec, BatteryReport? Battery, string Rejected);

/// <summary>
/// What the service does with one reading. PURE: the reading, the previous state, the flag, the clock and the config go
/// in; the new state, the actions and the next delay come out. It follows design.md sections 3, 5 and 6:
/// the ladder of levels (one new level per poll, each in a later poll) moves on readings and time only - a level is
/// reached when agreed, the warning when it is attempted; an action that failed (emergency-on, the battery write) never
/// holds the ladder back and is proposed again on every poll whose preconditions still hold (the service backs off).
/// </summary>
internal static partial class StationDecision
{
    private static readonly (LevelStep Step, Level Level)[] Steps =
    [
        (LevelStep.Emergency, Level.Low),
        (LevelStep.Stop, Level.Critical),
        (LevelStep.Warn, Level.Hibernating),
        (LevelStep.Hibernate, Level.Hibernating),
    ];

    public static double Threshold(LevelStep step, StationConfig c) => step switch
    {
        LevelStep.Emergency => c.EmergencyAt,
        LevelStep.Stop => c.StopAt,
        LevelStep.Warn => c.WarnAt,
        _ => c.HibernateAt,
    };

    [GeneratedRegex("^[A-Z][A-Z_]*$")]
    private static partial Regex ModePattern();

    /// <summary>AC input above 0 W = on; 0 W with the inverter feeding the sockets = off; else unknown.</summary>
    public static Grid GridOf(Reading r) =>
        r.InAc > 0 ? Grid.On : string.Equals(r.Mode, "INVERTER_OUTPUT", StringComparison.Ordinal) ? Grid.Off : Grid.Unknown;

    private static bool InRange(double? v, double min, double max) => v is { } x && !double.IsNaN(x) && x >= min && x <= max;

    /// <summary>"" = accepted; else why not (incomplete / implausible). A reading past maxGapSec is never implausible.</summary>
    public static string Check(Reading r, StationState s, DateTimeOffset now, StationConfig c)
    {
        var missing = new List<string>();
        if (!InRange(r.Soc, 0, 100)) missing.Add("SOC");
        if (!InRange(r.InAc, 0, 1e6)) missing.Add("AC input power");
        if (!InRange(r.OutAc, 0, 1e6)) missing.Add("AC output power");
        if (r.Mode is null || !ModePattern().IsMatch(r.Mode)) missing.Add("output mode");
        if (missing.Count > 0) return $"incomplete (no valid {string.Join(", ", missing)})";
        if (s.LastAt is not { } lastAt || s.LastSoc is not { } lastSoc) return "";
        // Always checked, gaps included: the allowance grows with the elapsed time; a backward clock gives only the floor.
        double gapSec = (now - lastAt).TotalSeconds;
        double soc = r.Soc!.Value;
        if (!WithinRate(lastSoc, lastAt, soc, now, c))
        {
            var inv = CultureInfo.InvariantCulture;
            return $"implausible ({soc.ToString("0.##", inv)} % after {lastSoc.ToString("0.##", inv)} % {(int)gapSec} s earlier)";
        }
        return "";
    }

    private static bool WithinRate(double soc0, DateTimeOffset at0, double soc1, DateTimeOffset at1, StationConfig c) =>
        Math.Abs(soc1 - soc0) <= Math.Max(c.SocJumpFloor, c.SocRatePerMin * Math.Max(0, (at1 - at0).TotalSeconds) / 60);

    /// <summary>Three rejected readings in a row that agree with each other within the rate: the newest becomes the baseline.</summary>
    private static bool ThreeAgree(StationState s, StationConfig c) =>
        s.RejSoc.Length == 3 && WithinRate(s.RejSoc[0], s.RejAt[0], s.RejSoc[1], s.RejAt[1], c) && WithinRate(s.RejSoc[1], s.RejAt[1], s.RejSoc[2], s.RejAt[2], c);

    private static bool Fast(StationState s, StationConfig c) => s.Grid == Grid.Off && s.LastSoc is { } soc && soc < c.FastBelow;

    private static void ResetStreaks(StationState s)
    {
        Array.Clear(s.Since);
        s.RebaseConfirmedSec = 0;
    }

    /// <summary>
    /// A rebase, or a jump accepted outright after a silence: the baseline moved. The latest one in the cut is remembered,
    /// hibernate's long confirmation starts over and its hibernate-delayed event may be given again.
    /// </summary>
    private static void BaselineMoved(StationState s, DateTimeOffset now)
    {
        s.RebasedAt = now;
        s.RebaseDelayNoted = false;
        s.RebaseConfirmedSec = 0;
    }

    /// <summary>An observed mains change ends a page choice whose command has not run: it is never run after the change.</summary>
    private static void DropPending(StationState s, string why, List<StationAction> actions)
    {
        if (s.ModePending is not { } mode) return;
        s.ModePending = null;
        s.ModeError = $"{mode} dropped: {why}";
        actions.Add(StationAction.ModeDropped);
    }

    private static void ClearLevels(StationState s)
    {
        s.Level = Level.Ok; s.WarnedAt = null;
        Array.Clear(s.Reached);
        Array.Clear(s.Fired);
    }

    private static bool Agreed(StationState s, LevelStep step, DateTimeOffset now, StationConfig c) =>
        s.Since[(int)step] is { } st && (now - st).TotalSeconds >= Math.Max(1, c.AgreeSec);

    public static StationDecisionResult Decide(Reading? reading, StationState? state, FlagView flag, DateTimeOffset now, StationConfig c)
    {
        var s = StationState.Normalize(state).Clone();   // a copy: the caller's state is never changed
        var actions = new List<StationAction>();

        string why = reading is null ? "no answer" : Check(reading, s, now, c);
        bool rebased = false;
        if (why.Length > 0 && reading is not null)
        {
            // A run of implausible readings that agree with each other moves the baseline: a wrong one cannot hold for long.
            if (reading.Soc is { } rs && why.StartsWith("implausible", StringComparison.Ordinal))
            {
                s.RejSoc = [.. s.RejSoc.TakeLast(2), rs];
                s.RejAt = [.. s.RejAt.TakeLast(2), now];
                if (ThreeAgree(s, c)) { why = ""; rebased = true; }
            }
            else { s.RejSoc = []; s.RejAt = []; }
        }
        if (why.Length > 0)
        {
            // Unknown: not "mains is back" and not "the battery is empty". Nothing happens; the level streaks and the
            // mains timers neither advance nor reset.
            s.Misses++;
            if (reading is not null) { s.Rejected = why; s.RejectedAt = now; }
            if (s.Misses >= c.SilentAfter && !s.Silent) { s.Silent = true; s.SilentSince = now; }
            return new StationDecisionResult(s, actions, Fast(s, c) ? c.FastPollSec : c.PollSec, null, reading is null ? "" : why);
        }
        s.RejSoc = []; s.RejAt = [];
        double soc = reading!.Soc!.Value;
        // Accepted outright, yet further from the last accepted reading than the rate allows over jumpWindowSec: only a
        // silence made the allowance that wide. The baseline moved as in a rebase; this reading still counts.
        bool jumped = !rebased && s.LastAt is { } jumpFrom && s.LastSoc is { } jumpSoc
            && !WithinRate(jumpSoc, jumpFrom, soc, jumpFrom.AddSeconds(Math.Min((now - jumpFrom).TotalSeconds, c.JumpWindowSec)), c);
        // What this reading adds to the rebase confirmation: the time since the accepted reading before it, one interval
        // counting at most twice the poll interval, so a forward clock step is not minutes of agreeing readings.
        double confirmStepSec = s.LastAt is { } before ? Math.Clamp((now - before).TotalSeconds, 0, c.ConfirmStepCapSec) : 0;

        // Accepted. After a boot, a resume, a gap past maxGapSec or a backward clock the whole decision state starts over
        // (cut, levels, timers, streaks). The user's manual choice, a lift still owed and whether a cut was on are not
        // decision state: they stay (a cut found again after the gap is no mains change).
        if (StartsOver(s, now, c))
        {
            // A backward clock step would make every new streak look older than a remembered rebase: it moves to now.
            bool backward = s.LastAt is { } la && now < la;
            s = new StationState
            {
                OwedLift = s.OwedLift, MainsBackAt = s.MainsBackAt, Manual = s.Manual, ManualAt = s.ManualAt,
                Rejected = s.Rejected, RejectedAt = s.RejectedAt, CutBeforeGap = s.Cut || s.CutBeforeGap,
                RebasedAt = backward && s.RebasedAt is not null ? now : s.RebasedAt, RebaseDelayNoted = s.RebaseDelayNoted,
                ModePending = s.ModePending, ModeError = s.ModeError,
            };
        }
        s.Misses = 0; s.Silent = false; s.SilentSince = null;
        // Resumed after a hibernation (a reading more than 2 minutes after it): the hibernate level re-arms with every
        // precondition - agreement, the warning 3 minutes before, a fresh reading.
        if (s.Fired[(int)LevelStep.Hibernate] && s.HibernatedAt is { } hib && (now - hib).TotalSeconds > 120)
        {
            s.Fired[(int)LevelStep.Hibernate] = false; s.HibernatedAt = null;
            s.Reached[(int)LevelStep.Warn] = false; s.WarnedAt = null;
            s.Since[(int)LevelStep.Warn] = null; s.Since[(int)LevelStep.Hibernate] = null;
        }
        s.LastSoc = soc; s.LastAt = now;
        s.HibernateDelayedUntil = null;   // set again below only when this poll holds hibernate back
        // A rebased reading only moves the baseline: no level, streak, mains timer or battery report counts it.
        if (rebased)
        {
            BaselineMoved(s, now);
            return new StationDecisionResult(s, actions, Fast(s, c) ? c.FastPollSec : c.PollSec, null, "");
        }
        var grid = GridOf(reading);
        s.Grid = grid;
        // Mains unknown: the reading is not on battery, so it breaks every level's agreement; the mains timers stay.
        if (grid == Grid.Unknown) ResetStreaks(s);

        if (grid == Grid.Off)
        {
            s.OnSince = null;
            s.OffSince ??= now;
            // The levels count on battery from the first reading; a cut begins once the grid has been off for mainsLostSec.
            // The rebase confirmation counts the readings that continue a hibernate streak, never the one that begins it.
            bool streakRuns = s.Since[(int)LevelStep.Hibernate] is not null;
            foreach (var (step, _) in Steps)
            {
                if (soc <= Threshold(step, c)) s.Since[(int)step] ??= now;
                else s.Since[(int)step] = null;
            }
            s.RebaseConfirmedSec = streakRuns && s.Since[(int)LevelStep.Hibernate] is not null ? s.RebaseConfirmedSec + confirmStepSec : 0;
            if (jumped) BaselineMoved(s, now);
            if (!s.Cut && (now - s.OffSince.Value).TotalSeconds >= c.MainsLostSec)
            {
                s.Cut = true; s.CutSince = now; s.Held = false;
                ClearLevels(s);
                if (!s.CutBeforeGap) DropPending(s, "mains was lost before it ran", actions);
                if (ManualEndsAtCut(s)) { s.Manual = ManualMode.Auto; s.ManualAt = null; }
                s.CutBeforeGap = false;
            }
        }

        else if (jumped) BaselineMoved(s, now);   // forgotten again by MainsOn once mains is confirmed back

        if (grid == Grid.Off && s.Cut) CutActions(s, flag, now, c, actions);
        else if (grid == Grid.On) MainsOn(s, flag, now, c, actions);

        return new StationDecisionResult(s, actions, Fast(s, c) ? c.FastPollSec : c.PollSec, new BatteryReport(soc, s.Level, grid == Grid.On), "");
    }

    /// <summary>An observed mains change ends a manual choice made before it; a cut found again after a gap is none.</summary>
    private static bool ManualEndsAtCut(StationState s) =>
        !s.CutBeforeGap && s.Manual != ManualMode.Auto && s.ManualAt < s.OffSince;

    /// <summary>
    /// When the cut begins with eco on if mains stays off: the first-off time plus mainsLostSec, from the same state the cut
    /// reads (the switch itself comes with the first accepted reading at or after it). Null when no eco is pending: mains
    /// on or unknown, the cut already begun, the station silent, the flag not normal (eco or emergency on, unreadable), Max
    /// chosen during this outage (it outlives the cut), a 20 % streak running (whether the cut brings eco or the emergency
    /// depends on when the reading after it comes), or the next accepted reading at <paramref name="now"/> would start the
    /// whole state over (gap past maxGapSec, backward clock): the restarted time is not known yet.
    /// </summary>
    public static DateTimeOffset? EcoAt(StationState state, FlagView flag, DateTimeOffset now, StationConfig c)
    {
        var s = StationState.Normalize(state.Clone());
        if (s.Grid != Grid.Off || s.Cut || s.Silent || s.OffSince is not { } off || flag.Mode != "normal") return null;
        if (StartsOver(s, now, c)) return null;
        if (s.Manual == ManualMode.Max && !ManualEndsAtCut(s)) return null;
        if (s.Since[(int)LevelStep.Emergency] is not null) return null;
        return off.AddSeconds(c.MainsLostSec);
    }

    /// <summary>A reading accepted at <paramref name="now"/> starts the decision state over: a boot, a gap past maxGapSec, a backward clock.</summary>
    private static bool StartsOver(StationState s, DateTimeOffset now, StationConfig c) =>
        s.LastAt is not { } lastAt || (now - lastAt).TotalSeconds > c.MaxGapSec || now < lastAt;

    private static void CutActions(StationState s, FlagView flag, DateTimeOffset now, StationConfig c, List<StationAction> actions)
    {
        // The user's own off (power-mode.ps1 off by hand) during this cut holds until mains returns.
        if (flag.Mode == "normal" && flag.By == "manual" && flag.SinceAt is { } since && since >= s.CutSince) s.Held = true;
        bool eco = flag.Mode == "normal" && !s.Held && s.Manual != ManualMode.Max;
        var events = new List<StationAction>();

        // The ladder: one new level per poll, the first one not yet reached, only when agreed - readings and time only.
        // It applies in every manual mode, Max included.
        foreach (var (step, level) in Steps)
        {
            if (step == LevelStep.Hibernate)
            {
                // Proposed on every qualifying poll until the hibernation ran; 3 minutes after the warning of this cut.
                if (s.Fired[(int)step] || !Agreed(s, step, now, c) || s.WarnedAt is not { } warned
                    || (now - warned).TotalSeconds < c.HibernateAfterWarnSec)
                    break;
                // While a baseline move is remembered in this cut, the streak hibernates only once the readings counted
                // since the later of its start and the latest move have lasted the long confirmation, whenever the streak
                // began; until then it is held back, said once per move.
                if (s.RebasedAt is not null && s.RebaseConfirmedSec < c.RebaseHibernateConfirmSec)
                {
                    s.HibernateDelayedUntil = now.AddSeconds(c.RebaseHibernateConfirmSec - s.RebaseConfirmedSec);
                    if (!s.RebaseDelayNoted) { s.RebaseDelayNoted = true; events.Add(StationAction.HibernateDelayed); }
                }
                else events.Add(StationAction.Hibernate);
                break;
            }
            if (s.Reached[(int)step]) continue;
            if (Agreed(s, step, now, c))
            {
                s.Reached[(int)step] = true;
                s.Level = level;
                if (step == LevelStep.Stop) events.Add(StationAction.Stop);
                if (step == LevelStep.Warn) { s.WarnedAt = now; events.Add(StationAction.HibernateWarning); }
            }
            break;
        }

        // The emergency's own action, owed until it succeeded (or the flag already says emergency): proposed again on every
        // poll at which 20 % is still agreed. Eco gives way to the stricter mode.
        bool emergency = false;
        if (s.Reached[(int)LevelStep.Emergency] && !s.Fired[(int)LevelStep.Emergency])
        {
            if (flag.Mode == "emergency") s.Fired[(int)LevelStep.Emergency] = true;
            else if (Agreed(s, LevelStep.Emergency, now, c)) emergency = true;
        }
        if (emergency) actions.Add(StationAction.EmergencyOn);
        else if (eco) actions.Add(StationAction.EcoOn);
        actions.AddRange(events);
    }

    private static void MainsOn(StationState s, FlagView flag, DateTimeOffset now, StationConfig c, List<StationAction> actions)
    {
        s.OffSince = null;
        s.OnSince ??= now;
        ResetStreaks(s);
        if ((now - s.OnSince.Value).TotalSeconds < c.MainsBackSec) return;
        // Mains is back: every power mode set before mains was first seen back goes, whoever set it; one set during the
        // mainsBackSec confirmation stays. The lift stays owed until off exits 0.
        if (s.Cut || s.CutBeforeGap)
        {
            s.OwedLift = true;
            s.MainsBackAt = s.OnSince;
            if (s.Manual != ManualMode.Auto && s.ManualAt < s.OnSince) { s.Manual = ManualMode.Auto; s.ManualAt = null; }
            DropPending(s, "mains came back before it ran", actions);
        }
        s.CutBeforeGap = false;
        bool modeOn = flag.Mode is "eco" or "emergency";
        var liftBefore = s.OwedLift ? s.MainsBackAt ?? s.OnSince.Value : s.OnSince.Value;
        bool setBefore = flag.SinceAt is not { } since || since < liftBefore;   // a mode set after mains came back stays
        if (s.Manual == ManualMode.Eco) s.OwedLift = false;   // the user's eco chosen after mains came back holds
        else if (modeOn && setBefore && (s.OwedLift || flag.Source == "station")) actions.Add(StationAction.MainsBack);
        else if (flag.Mode != "unreadable") s.OwedLift = false;   // nothing of before is on: nothing owed
        s.Cut = false; s.CutSince = null; s.Held = false; s.RebasedAt = null; s.RebaseDelayNoted = false;
        ClearLevels(s);
    }

    /// <summary>
    /// An action's command succeeded: the emergency is on, the hibernation ran, the owed lift is paid.
    /// </summary>
    public static StationState Complete(StationState state, StationAction action, DateTimeOffset now)
    {
        var s = StationState.Normalize(state).Clone();
        switch (action)
        {
            case StationAction.EmergencyOn: s.Fired[(int)LevelStep.Emergency] = true; break;
            case StationAction.Hibernate: s.Fired[(int)LevelStep.Hibernate] = true; s.HibernatedAt = now; break;
            case StationAction.MainsBack: s.OwedLift = false; break;
            default: break;
        }
        return s;
    }

    /// <summary>The user's choice from the page.</summary>
    public static StationState SetManual(StationState state, ManualMode mode, DateTimeOffset now)
    {
        var s = StationState.Normalize(state).Clone();
        s.Manual = mode;
        s.ManualAt = mode == ManualMode.Auto ? null : now;
        return s;
    }
}
