using System.Globalization;

namespace BluettiWatcher.Decision;

/// <summary>
/// The last threshold, never on trust: the decision must have proposed it, no kill switch, and a FRESH reading taken right
/// before acting must be accepted (complete, plausible next to the last accepted one), on battery and at or under
/// hibernateAt. No fresh reading = no hibernation. Nothing here looks at a flag write or an earlier action.
/// </summary>
internal static class HibernateGuard
{
    private static bool Proposed(StationDecisionResult d) => d.Actions.Contains(StationAction.Hibernate);

    /// <summary>null = go on and take the fresh reading; else why not.</summary>
    public static string? BeforeRead(StationDecisionResult d, bool killSwitch)
    {
        if (!Proposed(d)) return "the decision did not propose it";
        if (killSwitch) return "disabled by the kill switch";
        return null;
    }

    /// <summary>null = hibernate; else why not.</summary>
    public static string? AfterRead(StationDecisionResult d, Reading? fresh, DateTimeOffset now, StationConfig c)
    {
        return Proposed(d) ? Fresh(fresh, d.State, now, c) : "the decision did not propose it";
    }

    /// <summary>The fresh reading right before acting: accepted next to <paramref name="state"/>, on battery, at or under hibernateAt. null = hibernate.</summary>
    public static string? Fresh(Reading? fresh, StationState state, DateTimeOffset now, StationConfig c)
    {
        if (fresh is null) return "no fresh reading";
        string why = StationDecision.Check(fresh, state, now, c);
        if (why.Length > 0) return $"the fresh reading is {why}";
        if (StationDecision.GridOf(fresh) != Grid.Off) return "not on battery";
        if (fresh.Soc > c.HibernateAt)
            return $"battery {fresh.Soc.Value.ToString("0.##", CultureInfo.InvariantCulture)} % is above {c.HibernateAt.ToString("0.##", CultureInfo.InvariantCulture)} %";
        return null;
    }
}

/// <summary>The battery reading the flag last received.</summary>
internal sealed record BatteryWritten(double Soc, Level Level, bool Mains, DateTimeOffset At);

/// <summary>
/// When the battery reading goes into the flag: a pwsh spawn is not free, so only at a change (level, mains, a whole
/// percent) or, on battery, before the hook's 15-minute freshness runs out.
/// </summary>
internal static class BatteryWritePolicy
{
    public static bool ShouldWrite(BatteryWritten? last, BatteryReport now, DateTimeOffset at, StationConfig c)
    {
        if (last is null) return true;
        if (last.Level != now.Level || last.Mains != now.Mains) return true;
        if (Math.Abs(Math.Round(last.Soc) - Math.Round(now.Soc)) >= 1) return true;
        return !now.Mains && (at - last.At).TotalSeconds >= c.BatteryRefreshSec;
    }
}

/// <summary>
/// A failing action backs off: the next try 1, 2, 4, 8 minutes after a failure, then every 10 minutes; a success resets
/// it. One per action kind; never a retry loop at the poll rate.
/// </summary>
internal sealed class Backoff
{
    private static readonly int[] DelaysSec = [60, 120, 240, 480, 600];
    private readonly Dictionary<string, (int Failures, DateTimeOffset NextAt)> _keys = new(StringComparer.Ordinal);

    public static TimeSpan Delay(int failures) => TimeSpan.FromSeconds(DelaysSec[Math.Clamp(failures, 1, DelaysSec.Length) - 1]);

    /// <summary>true = the action may run now.</summary>
    public bool Open(string key, DateTimeOffset now) => !_keys.TryGetValue(key, out var k) || now >= k.NextAt;

    /// <summary>Records a failure; returns the time of the next try.</summary>
    public DateTimeOffset Failed(string key, DateTimeOffset now)
    {
        int failures = _keys.TryGetValue(key, out var k) ? k.Failures + 1 : 1;
        var next = now + Delay(failures);
        _keys[key] = (failures, next);
        return next;
    }

    public void Succeeded(string key) => _keys.Remove(key);
}
