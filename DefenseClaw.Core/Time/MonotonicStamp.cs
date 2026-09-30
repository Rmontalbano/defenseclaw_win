namespace DefenseClaw.Core.Time;

/// <summary>
/// A point on the monotonic clock (<see cref="TimeProvider.GetTimestamp"/>), for every "is it
/// time to refresh?" gate.
/// <para>
/// <b>Why not <c>UtcNow - stamp</c>.</b> The wall clock steps: an NTP correction, a manual
/// change, a VM resuming from a snapshot. After a backwards step of <c>S</c>, a gate written as
/// <c>UtcNow - stamp &gt;= interval</c> stays shut for <c>S</c> more — an hour-long step stops
/// the alert poll (and with it the tray's CRITICAL toasts) for an hour. The monotonic clock does
/// not step, so a gate built on it always opens one interval after it was stamped. Wall time is
/// still what a surface <i>prints</i> ("polled at 14:03:07"); keep a separate wall-clock field
/// for that and never subtract it to decide anything.
/// </para>
/// <para>
/// <c>default</c> is <see cref="Never"/>: nothing has been stamped yet, so any interval has
/// elapsed. That is what a fresh field, and a "force the next one" reset, want.
/// </para>
/// </summary>
public readonly struct MonotonicStamp
{
    private readonly long _timestamp;
    private readonly bool _isSet;

    private MonotonicStamp(long timestamp)
    {
        _timestamp = timestamp;
        _isSet = true;
    }

    /// <summary>Nothing stamped yet; <see cref="HasElapsed"/> is always true.</summary>
    public static MonotonicStamp Never => default;

    public bool IsNever => !_isSet;

    /// <summary>Stamps the current instant on <paramref name="time"/>'s monotonic clock.</summary>
    public static MonotonicStamp Now(TimeProvider? time = null) =>
        new((time ?? TimeProvider.System).GetTimestamp());

    /// <summary>Time since the stamp; <see cref="TimeSpan.MaxValue"/> for <see cref="Never"/>.</summary>
    public TimeSpan Elapsed(TimeProvider? time = null) =>
        _isSet ? (time ?? TimeProvider.System).GetElapsedTime(_timestamp) : TimeSpan.MaxValue;

    /// <summary>True when at least <paramref name="interval"/> has passed since the stamp (or there was none).</summary>
    public bool HasElapsed(TimeSpan interval, TimeProvider? time = null) => Elapsed(time) >= interval;
}

/// <summary>
/// The same guard for a stamp that has to be a wall-clock time — one that is persisted, or that
/// another surface prints — where a <see cref="MonotonicStamp"/> cannot stand in for it.
/// </summary>
public static class WallClock
{
    /// <summary>
    /// <c>now - stamp</c>, except that a stamp in the future — the clock stepped backwards since
    /// it was taken, or the file it came from is bogus — counts as <see cref="TimeSpan.MaxValue"/>
    /// (long ago). A gate on it then opens at once and restamps with the corrected time, instead
    /// of staying shut for the size of the step.
    /// <para>
    /// This is the fallback, not the equal of <see cref="MonotonicStamp"/>: a step smaller than
    /// the time already elapsed still shifts the gate by up to that step, and a forward step
    /// opens it early. It cannot be worse than one extra interval, which is why the panel gates
    /// that already hold a wall stamp use it; new gates hold a <see cref="MonotonicStamp"/>.
    /// </para>
    /// </summary>
    public static TimeSpan Elapsed(DateTimeOffset stamp, TimeProvider? time = null)
    {
        var elapsed = (time ?? TimeProvider.System).GetUtcNow() - stamp;
        return elapsed < TimeSpan.Zero ? TimeSpan.MaxValue : elapsed;
    }
}
