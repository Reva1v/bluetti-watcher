namespace BluettiWatcher;

/// <summary>
/// When the next poll is due: the delay the last poll asked for, counted from the moment that poll began. Counted on the
/// monotonic clock (<see cref="TimeProvider.GetTimestamp"/>), so a wall-clock step, backward or forward, neither delays a
/// poll nor brings one early. The decision still receives wall time.
/// </summary>
internal sealed class PollSchedule(TimeProvider time)
{
    private long _started = time.GetTimestamp();
    private TimeSpan _delay = TimeSpan.Zero;

    /// <summary>true = poll now; the poll counts as begun at this moment.</summary>
    public bool Due()
    {
        if (time.GetElapsedTime(_started) < _delay) return false;
        _started = time.GetTimestamp();
        return true;
    }

    /// <summary>The next poll comes <paramref name="delaySec"/> after the last one began.</summary>
    public void Next(int delaySec) => _delay = TimeSpan.FromSeconds(delaySec);

    /// <summary>How long until the next poll is due; zero or less when it is.</summary>
    public TimeSpan Wait => _delay - time.GetElapsedTime(_started);
}
