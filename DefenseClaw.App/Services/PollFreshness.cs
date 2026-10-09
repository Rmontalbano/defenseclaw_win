namespace DefenseClaw.App.Services;

/// <summary>
/// How fresh the gateway monitor's picture is: what the status strip's Stale chip reads. <see cref="GatewayMonitor"/> is the only
/// implementation; a test passes its own.
/// </summary>
internal interface IPollFreshness
{
    /// <summary>
    /// How long ago the last poll that finished without a fault finished, on the monotonic clock (a wall-clock step cannot make the data look
    /// old or new); null while no such poll has finished yet. A poll that is still running, or that threw, does not count: it brought nothing.
    /// </summary>
    TimeSpan? SinceLastGoodPoll { get; }

    /// <summary>How long the poll loop waits between polls right now: the Settings page's health pulse, or the slow lane after repeated unreachable polls.</summary>
    TimeSpan Cadence { get; }

    /// <summary>True while the operator has paused monitoring: nothing is polled by choice, so nothing is stale by neglect.</summary>
    bool IsPaused { get; }
}

/// <summary>The rule behind the Stale chip, in one place so the chip, its tooltip and the tests cannot disagree about it.</summary>
internal static class PollFreshness
{
    /// <summary>The data is stale once the last good poll is older than this many polling intervals (the TUI's strip says "stale" at three).</summary>
    public const int IntervalsBeforeStale = 3;

    /// <summary>The longest the strip waits between looks at a monitor that has stopped reporting, however slow the cadence is.</summary>
    public static readonly TimeSpan LongestLook = TimeSpan.FromSeconds(10);

    /// <summary>The shortest, so a fast pulse does not make the strip look every other moment.</summary>
    public static readonly TimeSpan ShortestLook = TimeSpan.FromSeconds(1);

    /// <summary>
    /// True when the last good poll is more than <see cref="IntervalsBeforeStale"/> intervals old. Strictly more: a poll exactly three intervals
    /// ago is the one the next tick is about to replace. Nothing is stale before a first good poll (the strip says "Checking…" then), and not
    /// while the operator has paused monitoring (the strip says so).
    /// </summary>
    public static bool IsStale(TimeSpan? sinceLastGoodPoll, TimeSpan cadence, bool paused = false) =>
        !paused && sinceLastGoodPoll is { } since && since > cadence * IntervalsBeforeStale;

    /// <summary>
    /// How often to look while the strip is on screen: once per polling interval, so a monitor that stops reporting is called stale within
    /// one interval of crossing the line, bounded to <see cref="ShortestLook"/> .. <see cref="LongestLook"/> (a 60 s pulse is looked at every
    /// 10 s, which is still inside one interval).
    /// </summary>
    public static TimeSpan LookEvery(TimeSpan cadence) =>
        cadence < ShortestLook ? ShortestLook : cadence > LongestLook ? LongestLook : cadence;
}
