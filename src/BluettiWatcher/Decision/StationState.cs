namespace BluettiWatcher.Decision;

/// <summary>
/// What the decision carries from one poll to the next. Indexed by <see cref="LevelStep"/>: Since (the streak at or under
/// the level), Reached (the ladder), Fired (the action succeeded: emergency on, hibernation ran).
/// </summary>
internal sealed class StationState
{
    public bool Cut { get; set; }
    public DateTimeOffset? CutSince { get; set; }
    public DateTimeOffset? OffSince { get; set; }
    public DateTimeOffset? OnSince { get; set; }
    public bool Held { get; set; }
    public Level Level { get; set; }
    public DateTimeOffset?[] Since { get; set; } = new DateTimeOffset?[4];
    public bool[] Reached { get; set; } = new bool[4];
    public bool[] Fired { get; set; } = new bool[4];
    public DateTimeOffset? WarnedAt { get; set; }
    public int Misses { get; set; }
    public bool Silent { get; set; }
    public DateTimeOffset? SilentSince { get; set; }
    public double? LastSoc { get; set; }
    public DateTimeOffset? LastAt { get; set; }
    public Grid Grid { get; set; }
    public string Rejected { get; set; } = "";
    public DateTimeOffset? RejectedAt { get; set; }
    public bool OwedLift { get; set; }
    /// <summary>When mains counted as back for the owed lift: only modes set before it are lifted.</summary>
    public DateTimeOffset? MainsBackAt { get; set; }
    /// <summary>A cut was on when a gap reset the state: finding it again is no mains change.</summary>
    public bool CutBeforeGap { get; set; }
    public DateTimeOffset? HibernatedAt { get; set; }
    /// <summary>The run of implausible readings (at most the last three).</summary>
    public double[] RejSoc { get; set; } = [];
    public DateTimeOffset[] RejAt { get; set; } = [];
    /// <summary>
    /// When the baseline last moved in this cut (three agreeing rejected readings, or a jump accepted outright after a
    /// silence); cleared when mains is confirmed back. While it is set, hibernate waits the long confirmation
    /// (<see cref="RebaseConfirmedSec"/>): the moved baseline may be garbage.
    /// </summary>
    public DateTimeOffset? RebasedAt { get; set; }
    /// <summary>The hibernate-delayed event of the remembered rebase was given (once per rebase, cleared with RebasedAt).</summary>
    public bool RebaseDelayNoted { get; set; }
    /// <summary>
    /// Seconds of agreeing accepted readings at or under the hibernate level, counted since the later of the streak's start
    /// and the latest rebase, one interval counting at most twice the poll interval. 0 in a state saved before the field.
    /// </summary>
    public double RebaseConfirmedSec { get; set; }
    /// <summary>
    /// When hibernate, held back by the rebase rule, becomes allowed at the earliest; null after any accepted reading that
    /// does not hold it back.
    /// </summary>
    public DateTimeOffset? HibernateDelayedUntil { get; set; }
    public ManualMode Manual { get; set; }
    public DateTimeOffset? ManualAt { get; set; }
    /// <summary>A page choice saved but whose command has not run yet (the service loop applies it).</summary>
    public ManualMode? ModePending { get; set; }
    /// <summary>The last failure or refusal of a page choice, "" when none; shown until the next mode change.</summary>
    public string ModeError { get; set; } = "";

    public StationState Clone()
    {
        var s = (StationState)MemberwiseClone();
        s.Since = (DateTimeOffset?[])Since.Clone();
        s.Reached = (bool[])Reached.Clone();
        s.Fired = (bool[])Fired.Clone();
        return s;
    }

    /// <summary>A state read back from storage, or a damaged one, as a full state.</summary>
    public static StationState Normalize(StationState? s)
    {
        if (s is null) return new StationState();
        if (s.Since is not { Length: 4 }) s.Since = new DateTimeOffset?[4];
        if (s.Reached is not { Length: 4 }) s.Reached = new bool[4];
        if (s.Fired is not { Length: 4 }) s.Fired = new bool[4];
        s.Rejected ??= "";
        s.ModeError ??= "";
        if (s.RejSoc is null || s.RejAt is null || s.RejSoc.Length != s.RejAt.Length) { s.RejSoc = []; s.RejAt = []; }
        if (!Enum.IsDefined(s.Level)) s.Level = Level.Ok;
        if (!Enum.IsDefined(s.Grid)) s.Grid = Grid.Unknown;
        if (!Enum.IsDefined(s.Manual)) s.Manual = ManualMode.Auto;
        if (s.Misses < 0) s.Misses = 0;
        if (!(s.RebaseConfirmedSec >= 0)) s.RebaseConfirmedSec = 0;   // negative or NaN
        return s;
    }
}
