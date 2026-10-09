namespace DefenseClaw.App.Tests.TestSupport;

/// <summary>
/// A <see cref="TimeProvider"/> whose time only moves when a test says so, and whose timers fire from <see cref="Advance"/>: a timer due inside the
/// step fires, at the moment it was due, and a periodic one fires once for every period the step covers. So "five seconds later" is a line of code and
/// not five seconds of waiting, and what is asserted is the order things happen in, not a race. The wall clock moves with the monotonic one.
/// <para>
/// <see cref="ManualClock"/> is the same without timers; it is left as it is (<c>Task.Delay(span, clock)</c> would start behaving differently
/// for the tests that use it).
/// </para>
/// </summary>
public sealed class TickingClock : TimeProvider
{
    private readonly List<FakeTimer> _timers = new();
    private long _ticks;
    private DateTimeOffset _wall = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => _ticks;

    public override DateTimeOffset GetUtcNow() => _wall;

    /// <summary>How many timers are armed right now (created and neither disposed nor stopped).</summary>
    public int ActiveTimers
    {
        get
        {
            lock (_timers)
            {
                return _timers.Count(static t => t.IsArmed);
            }
        }
    }

    /// <summary>The period of the armed timers, one entry each (Timeout.InfiniteTimeSpan for a one-shot).</summary>
    public IReadOnlyList<TimeSpan> ActivePeriods
    {
        get
        {
            lock (_timers)
            {
                return _timers.Where(static t => t.IsArmed).Select(static t => t.Period).ToArray();
            }
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new FakeTimer(this, callback, state);
        _ = timer.Change(dueTime, period);
        lock (_timers)
        {
            _timers.Add(timer);
        }

        return timer;
    }

    /// <summary>The wall clock is stepped (negative = backwards); the monotonic clock and the timers do not notice.</summary>
    public void StepWallClock(TimeSpan by) => _wall += by;

    /// <summary>Time passes: every timer due within <paramref name="by"/> fires, in order, at its due time; then the clock is <paramref name="by"/> later.</summary>
    public void Advance(TimeSpan by)
    {
        var target = _ticks + by.Ticks;
        while (true)
        {
            FakeTimer? next;
            lock (_timers)
            {
                next = _timers.Where(t => t.IsArmed && t.DueTicks <= target).OrderBy(static t => t.DueTicks).FirstOrDefault();
            }

            if (next is null)
            {
                break;
            }

            var elapsed = Math.Max(0, next.DueTicks - _ticks);
            _ticks += elapsed;
            _wall += TimeSpan.FromTicks(elapsed);
            next.Fire();
        }

        var rest = target - _ticks;
        _ticks = target;
        _wall += TimeSpan.FromTicks(rest);
    }

    private sealed class FakeTimer : ITimer
    {
        private readonly TickingClock _clock;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private bool _disposed;

        public FakeTimer(TickingClock clock, TimerCallback callback, object? state)
        {
            _clock = clock;
            _callback = callback;
            _state = state;
        }

        public long DueTicks { get; private set; }

        public TimeSpan Period { get; private set; } = Timeout.InfiniteTimeSpan;

        public bool IsArmed { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (_disposed)
            {
                return false;
            }

            Period = period;
            IsArmed = dueTime != Timeout.InfiniteTimeSpan;
            DueTicks = _clock._ticks + dueTime.Ticks;
            return true;
        }

        public void Fire()
        {
            if (Period > TimeSpan.Zero && Period != Timeout.InfiniteTimeSpan)
            {
                DueTicks += Period.Ticks;
            }
            else
            {
                IsArmed = false;
            }

            _callback(_state);
        }

        public void Dispose()
        {
            _disposed = true;
            IsArmed = false;
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
