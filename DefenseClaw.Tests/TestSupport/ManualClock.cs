namespace DefenseClaw.Tests.TestSupport;

/// <summary>
/// A <see cref="TimeProvider"/> whose wall clock and monotonic clock are moved separately, which is
/// the whole point: <see cref="Advance"/> is real time passing (both move), <see cref="StepWallClock"/>
/// is an NTP correction or a manual clock change (only the wall clock moves).
/// </summary>
public sealed class ManualClock : TimeProvider
{
    private long _ticks;
    private DateTimeOffset _wall = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => _ticks;

    public override DateTimeOffset GetUtcNow() => _wall;

    /// <summary>Time passes: both clocks move forward.</summary>
    public void Advance(TimeSpan by)
    {
        _ticks += by.Ticks;
        _wall += by;
    }

    /// <summary>The wall clock is stepped (negative = backwards); the monotonic clock does not notice.</summary>
    public void StepWallClock(TimeSpan by) => _wall += by;
}
