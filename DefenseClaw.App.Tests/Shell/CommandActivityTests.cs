using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.Tests.Shell;

/// <summary>
/// What the status strip's Running chip is driven by (CUST-273): how many commands this app has in flight, seen through the one runner every command
/// goes through - any command, not only the scans <see cref="ScanActivity"/> counts for the tray's shield. Which runs count, that a start and an end
/// change the count once however they interleave, that nothing is polled, and that the runner is never hurt by it. Invocations are real
/// <c>CliInvocation</c>s built without a process (<see cref="InvocationFactory"/>); the few tests that run something use <c>cmd.exe</c>, never the real
/// DefenseClaw.
/// </summary>
public sealed class CommandActivityTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly CliRunner _runner;

    public CommandActivityTests()
    {
        _runner = new CliRunner(TestServices.IsolatedPaths(_temp.Path), neutralWorkingDirectory: _temp.File("work"));
    }

    public void Dispose()
    {
        _ = _runner.Shutdown(TimeSpan.FromSeconds(10));
        _runner.Dispose();
        _temp.Dispose();
    }

    private static string CmdPath => Path.Combine(Environment.SystemDirectory, "cmd.exe");

    /// <summary>The tracker over the test runner, posting in place, with the counts it announced recorded in order.</summary>
    private sealed class Watch : IDisposable
    {
        public Watch(CliRunner runner, Action<Action>? post = null)
        {
            Tracker = new CommandActivity(runner, post ?? (action => action()));
            Tracker.Changed += (_, _) =>
            {
                lock (Announced)
                {
                    Announced.Add(Tracker.Count);
                }
            };
        }

        public CommandActivity Tracker { get; }

        public List<int> Announced { get; } = new();

        public int[] Counts
        {
            get
            {
                lock (Announced)
                {
                    return Announced.ToArray();
                }
            }
        }

        public void Dispose() => Tracker.Dispose();
    }

    private static CliInvocation Running(params string[] argv) => InvocationFactory.Create(false, argv);

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = Environment.TickCount64 + 120_000; // a wait, not a bound: CI runners have run up to ~25x slower than a desktop
        while (!condition())
        {
            Assert.True(Environment.TickCount64 < deadline, "Timed out waiting for: " + what);
            await Task.Delay(10);
        }
    }

    [Theory]
    [InlineData("skill", "scan", "--all")]
    [InlineData("skill", "list")]
    [InlineData("doctor")]
    [InlineData("status", "--json")]
    [InlineData("keys", "list", "--json")]
    [InlineData("observability", "plan", "--format", "json")]
    [InlineData("setup", "guardrail")]
    public void Any_command_counts_not_only_a_scan(params string[] argv)
    {
        using var watch = new Watch(_runner);
        var run = Running(argv);

        watch.Tracker.OnStarted(null, run);
        Assert.Equal(1, watch.Tracker.Count);

        InvocationFactory.Finish(run);
        watch.Tracker.OnCompleted(null, run);

        Assert.Equal(0, watch.Tracker.Count);
        Assert.Equal(new[] { 1, 0 }, watch.Counts);
    }

    [Fact]
    public void Commands_at_once_are_counted_and_the_count_is_announced_each_time_it_changes()
    {
        using var watch = new Watch(_runner);
        var doctor = Running("doctor");
        var scan = Running("skill", "scan", "--all");
        var status = Running("status", "--json");

        watch.Tracker.OnStarted(null, doctor);
        watch.Tracker.OnStarted(null, scan);
        watch.Tracker.OnStarted(null, status);
        Assert.Equal(3, watch.Tracker.Count);

        InvocationFactory.Finish(scan);
        watch.Tracker.OnCompleted(null, scan);
        InvocationFactory.Finish(doctor, exitCode: 3);
        watch.Tracker.OnCompleted(null, doctor);
        InvocationFactory.Finish(status);
        watch.Tracker.OnCompleted(null, status);

        Assert.Equal(new[] { 1, 2, 3, 2, 1, 0 }, watch.Counts);
    }

    [Fact]
    public void A_command_that_fails_or_is_stopped_ends_the_count()
    {
        using var watch = new Watch(_runner);
        var timedOut = Running("agent", "discovery", "scan");
        watch.Tracker.OnStarted(null, timedOut);

        InvocationFactory.Fail(timedOut, "timed out after 120 s — process tree killed");
        watch.Tracker.OnCompleted(null, timedOut);

        Assert.Equal(0, watch.Tracker.Count);
        Assert.Equal(new[] { 1, 0 }, watch.Counts);
    }

    [Fact]
    public void A_hand_off_or_a_refusal_born_finished_never_counts()
    {
        using var watch = new Watch(_runner);
        var handOff = Running("keys", "set", "EXAMPLE_KEY");
        InvocationFactory.Finish(handOff);

        watch.Tracker.OnStarted(null, handOff);
        watch.Tracker.OnCompleted(null, handOff);

        Assert.Equal(0, watch.Tracker.Count);
        Assert.Empty(watch.Counts);
    }

    [Fact]
    public void A_completion_for_a_command_never_seen_starting_is_ignored()
    {
        using var watch = new Watch(_runner);
        var running = Running("doctor");
        watch.Tracker.OnStarted(null, running);

        var stranger = Running("status");
        InvocationFactory.Finish(stranger);
        watch.Tracker.OnCompleted(null, stranger);

        Assert.Equal(1, watch.Tracker.Count);
        Assert.Equal(new[] { 1 }, watch.Counts);
    }

    [Fact]
    public void The_same_run_announced_twice_is_one_command()
    {
        using var watch = new Watch(_runner);
        var run = Running("doctor");

        watch.Tracker.OnStarted(null, run);
        watch.Tracker.OnStarted(null, run);
        Assert.Equal(1, watch.Tracker.Count);

        InvocationFactory.Finish(run);
        watch.Tracker.OnCompleted(null, run);
        watch.Tracker.OnCompleted(null, run);

        Assert.Equal(0, watch.Tracker.Count);
        Assert.Equal(new[] { 1, 0 }, watch.Counts);
    }

    [Fact]
    public void A_command_that_begins_and_ends_before_the_UI_thread_looks_announces_nothing()
    {
        var queued = new List<Action>();
        using var watch = new Watch(_runner, queued.Add);
        var run = Running("doctor");

        watch.Tracker.OnStarted(null, run);
        InvocationFactory.Finish(run);
        watch.Tracker.OnCompleted(null, run);
        foreach (var action in queued.ToArray())
        {
            action();
        }

        Assert.Empty(watch.Counts);
        Assert.Equal(0, watch.Tracker.Count);
    }

    [Fact]
    public void The_UI_thread_hears_of_the_count_as_it_is_when_it_looks()
    {
        var queued = new List<Action>();
        using var watch = new Watch(_runner, queued.Add);

        watch.Tracker.OnStarted(null, Running("doctor"));
        watch.Tracker.OnStarted(null, Running("status"));
        foreach (var action in queued.ToArray())
        {
            action();
        }

        Assert.Equal(new[] { 2 }, watch.Counts);
    }

    [Fact]
    public void A_subscriber_that_throws_does_not_stop_the_tracker_or_the_runner()
    {
        using var tracker = new CommandActivity(_runner, action => action());
        tracker.Changed += (_, _) => throw new InvalidOperationException("the strip is gone");
        var run = Running("doctor");

        tracker.OnStarted(null, run);
        InvocationFactory.Finish(run);
        tracker.OnCompleted(null, run);

        Assert.Equal(0, tracker.Count);
    }

    // ---- Through the real runner ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_command_the_runner_runs_counts_on_the_way_in_and_out()
    {
        using var watch = new Watch(_runner);

        var run = await _runner.RunExecutableAsync(CmdPath, new[] { "/c", "exit", "0" });

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(0, watch.Tracker.Count);
        Assert.Equal(new[] { 1, 0 }, watch.Counts);
    }

    [Fact]
    public void A_hand_off_through_the_real_runner_is_born_finished_and_never_counts()
    {
        using var watch = new Watch(_runner);

        _ = _runner.RecordHandOff("defenseclaw", new[] { "keys", "set", "EXAMPLE_KEY" }, "copied for a terminal");
        _ = _runner.RecordRefusal("defenseclaw", new[] { "setup", "guardrail" }, "the list went out of date");

        Assert.Equal(0, watch.Tracker.Count);
        Assert.Empty(watch.Counts);
    }

    [Fact]
    public async Task A_command_already_running_when_the_tracker_is_built_is_picked_up_and_its_end_is_announced()
    {
        CliInvocation? started = null;
        _runner.InvocationStarted += (_, invocation) => started = invocation;
        var run = _runner.RunExecutableAsync(CmdPath, new[] { "/c", "ping -n 60 127.0.0.1 >nul" });
        try
        {
            await WaitUntilAsync(() => started is { IsRunning: true }, "the child to start");

            using var watch = new Watch(_runner);

            Assert.Equal(1, watch.Tracker.Count);

            Assert.True(_runner.Cancel(started!, out _));
            _ = await run.WaitAsync(TimeSpan.FromSeconds(60));

            Assert.Equal(0, watch.Tracker.Count);
            Assert.Equal(new[] { 0 }, watch.Counts);
        }
        finally
        {
            _ = _runner.Shutdown(TimeSpan.FromSeconds(10));
            _ = await run.WaitAsync(TimeSpan.FromSeconds(60));
        }
    }

    [Fact]
    public async Task A_disposed_tracker_has_given_the_runner_its_subscriptions_back()
    {
        var watch = new Watch(_runner);
        watch.Dispose();
        watch.Dispose();

        _ = await _runner.RunExecutableAsync(CmdPath, new[] { "/c", "exit", "0" });

        Assert.Equal(0, watch.Tracker.Count);
        Assert.Empty(watch.Counts);
    }

    [Fact]
    public void A_tracker_needs_a_runner()
    {
        _ = Assert.Throws<ArgumentNullException>(() => new CommandActivity(null!));
    }
}
