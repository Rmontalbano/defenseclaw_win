using DefenseClaw.Core.Time;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// The refresh gates ride the monotonic clock: a wall-clock step, in either direction, must neither
/// hold a gate shut nor blow it open.
/// </summary>
public class MonotonicStampTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    [Fact]
    public void A_stamp_that_was_never_taken_has_always_elapsed()
    {
        var clock = new ManualClock();

        Assert.True(MonotonicStamp.Never.IsNever);
        Assert.True(default(MonotonicStamp).HasElapsed(Interval, clock));
        Assert.Equal(TimeSpan.MaxValue, MonotonicStamp.Never.Elapsed(clock));
    }

    [Fact]
    public void Elapsed_time_is_measured_on_the_monotonic_clock()
    {
        var clock = new ManualClock();
        var stamp = MonotonicStamp.Now(clock);

        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.False(stamp.HasElapsed(Interval, clock));

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(stamp.HasElapsed(Interval, clock));
        Assert.Equal(Interval, stamp.Elapsed(clock));
    }

    [Fact]
    public void A_backwards_wall_clock_step_does_not_hold_the_gate_shut()
    {
        var clock = new ManualClock();
        var stamp = MonotonicStamp.Now(clock);

        // An hour-long correction backwards, then the interval passes for real. UtcNow - stamp would
        // read minus 59 minutes and keep the gate closed for another hour.
        clock.StepWallClock(TimeSpan.FromHours(-1));
        clock.Advance(Interval);

        Assert.True(stamp.HasElapsed(Interval, clock));
    }

    [Fact]
    public void A_forwards_wall_clock_step_does_not_open_the_gate_early()
    {
        var clock = new ManualClock();
        var stamp = MonotonicStamp.Now(clock);

        clock.StepWallClock(TimeSpan.FromHours(1));
        clock.Advance(TimeSpan.FromSeconds(5));

        Assert.False(stamp.HasElapsed(Interval, clock));
    }

    [Fact]
    public void A_wall_stamp_in_the_future_counts_as_long_ago()
    {
        var clock = new ManualClock();
        var stamp = clock.GetUtcNow();

        // The clock stepped back an hour after the stamp was taken (or the file it came from lies).
        clock.StepWallClock(TimeSpan.FromHours(-1));

        Assert.Equal(TimeSpan.MaxValue, WallClock.Elapsed(stamp, clock));
    }

    [Fact]
    public void A_wall_stamp_in_the_past_measures_the_ordinary_difference()
    {
        var clock = new ManualClock();
        var stamp = clock.GetUtcNow();
        clock.Advance(TimeSpan.FromSeconds(45));

        Assert.Equal(TimeSpan.FromSeconds(45), WallClock.Elapsed(stamp, clock));
        Assert.True(WallClock.Elapsed(DateTimeOffset.MinValue, clock) > TimeSpan.FromDays(365));
    }
}
