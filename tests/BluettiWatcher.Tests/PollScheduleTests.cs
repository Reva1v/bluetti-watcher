using static BluettiWatcher.Tests.Fx;

namespace BluettiWatcher.Tests;

/// <summary>A clock whose wall time can be stepped without the monotonic one moving.</summary>
internal sealed class SteppingClock(DateTimeOffset start) : TimeProvider
{
    private long _ticks;

    public DateTimeOffset Wall { get; set; } = start;

    /// <summary>Real time passes: both clocks move.</summary>
    public void Advance(TimeSpan real)
    {
        _ticks += real.Ticks;
        Wall += real;
    }

    public override DateTimeOffset GetUtcNow() => Wall.ToUniversalTime();
    public override long GetTimestamp() => _ticks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
}

/// <summary>The poll loop's schedule never follows a wall-clock step.</summary>
public sealed class PollScheduleTests
{
    private readonly SteppingClock _clock = new(T0);

    [Fact]
    public void First_poll_is_due_at_once()
    {
        var s = new PollSchedule(_clock);
        Assert.True(s.Due());
    }

    [Fact]
    public void Backward_wall_clock_step_never_delays_a_poll()
    {
        var s = new PollSchedule(_clock);
        Assert.True(s.Due());
        s.Next(60);
        _clock.Wall = _clock.Wall.AddHours(-1);
        _clock.Advance(TimeSpan.FromSeconds(59));
        Assert.False(s.Due());
        Assert.Equal(TimeSpan.FromSeconds(1), s.Wait);
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(s.Due());
    }

    [Fact]
    public void Forward_wall_clock_step_never_brings_a_poll_early()
    {
        var s = new PollSchedule(_clock);
        Assert.True(s.Due());
        s.Next(60);
        _clock.Wall = _clock.Wall.AddHours(1);
        Assert.False(s.Due());
        Assert.Equal(TimeSpan.FromSeconds(60), s.Wait);
        _clock.Advance(TimeSpan.FromSeconds(60));
        Assert.True(s.Due());
    }

    [Fact]
    public void Delay_counts_from_the_moment_the_poll_began()
    {
        var s = new PollSchedule(_clock);
        Assert.True(s.Due());
        _clock.Advance(TimeSpan.FromSeconds(20));   // the poll took 20 s
        s.Next(60);
        Assert.Equal(TimeSpan.FromSeconds(40), s.Wait);
        _clock.Advance(TimeSpan.FromSeconds(39));
        Assert.False(s.Due());
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(s.Due());
        s.Next(60);
        Assert.Equal(TimeSpan.FromSeconds(60), s.Wait);   // counted from the second poll, not from the first
    }
}
