using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.Tests.Branding;

/// <summary>
/// What the tray's "scanning" shield is driven by: <c>defenseclaw ... scan</c> commands this app has in flight, seen through the one runner
/// every command goes through. Which commands count, that a start and an end flip the state once however they interleave, that nothing
/// is polled, and that the runner is never hurt by it. Invocations are real <c>CliInvocation</c>s built without a process
/// (<see cref="InvocationFactory"/>); the few tests that run something use <c>cmd.exe</c>, never the real DefenseClaw.
/// </summary>
public sealed class ScanActivityTests : IDisposable
{
    private const string CliPath = @"C:\Dev\bin\defenseclaw.exe";

    private readonly TempDirectory _temp = new();
    private readonly CliRunner _runner;

    public ScanActivityTests()
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

    /// <summary>The tracker over the test runner, posting in place, with the flips it announced recorded in order.</summary>
    private sealed class Watch : IDisposable
    {
        public Watch(CliRunner runner, Func<CliInvocation, bool>? isScan = null, Action<Action>? post = null)
        {
            Tracker = new ScanActivity(runner, isScan, post ?? (action => action()));
            Tracker.Changed += (_, _) =>
            {
                lock (Announced)
                {
                    Announced.Add(Tracker.IsScanning);
                }
            };
        }

        public ScanActivity Tracker { get; }

        public List<bool> Announced { get; } = new();

        public bool[] Flips
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

    // ---- Which commands are scans --------------------------------------------------------------------------------------

    [Theory]
    [InlineData("skill", "scan", "--all")]
    [InlineData("skill", "scan", "--", "my-skill")]
    [InlineData("skill", "scan", "my-skill", "--json")]
    [InlineData("mcp", "scan")]
    [InlineData("mcp", "scan", "--all")]
    [InlineData("plugin", "scan", "--", "some-plugin")]
    [InlineData("aibom", "scan", "--json")]
    [InlineData("aibom", "scan")]
    [InlineData("agent", "discovery", "scan")]
    [InlineData("agent", "discovery", "runtime", "scan")]
    [InlineData("agent", "discovery", "runtime", "scan", "--json")]
    [InlineData("agent", "usage", "--refresh")]
    [InlineData("agent", "usage", "--json", "--refresh")]
    [InlineData("SKILL", "SCAN")]
    public void The_CLIs_scans_are_scans(params string[] argv)
    {
        Assert.True(ScanActivity.IsScan(CliPath, argv), string.Join(' ', argv));
    }

    [Theory]
    [InlineData("skill", "list")]
    [InlineData("skill", "info", "scan")]
    [InlineData("skill", "list", "--", "scan")]
    [InlineData("skill", "block", "--", "scan")]
    [InlineData("agent", "discover")]
    [InlineData("agent", "discover", "--refresh", "--no-emit-otel")]
    [InlineData("agent", "discovery", "status")]
    [InlineData("agent", "discovery", "runtime", "status")]
    [InlineData("agent", "discovery", "runtime", "enable")]
    [InlineData("agent", "usage")]
    [InlineData("agent", "usage", "--json")]
    [InlineData("agent", "usage", "--", "--refresh")]
    [InlineData("agent", "scan")]
    [InlineData("scan")]
    [InlineData("doctor")]
    [InlineData("codeguard", "status")]
    [InlineData("alerts", "list")]
    [InlineData("status")]
    public void Other_commands_are_not(params string[] argv)
    {
        Assert.False(ScanActivity.IsScan(CliPath, argv), string.Join(' ', argv));
    }

    [Theory]
    [InlineData("skill", "scan", "--help")]
    [InlineData("skill", "scan", "--dry-run")]
    [InlineData("skill", "scan", "--all", "--dry-run")]
    [InlineData("aibom", "scan", "--help")]
    public void A_preview_or_a_help_screen_is_not_a_scan_in_flight(params string[] argv)
    {
        Assert.False(ScanActivity.IsScan(CliPath, argv), string.Join(' ', argv));
    }

    [Fact]
    public void Nothing_to_look_at_is_not_a_scan()
    {
        Assert.False(ScanActivity.IsScan(CliPath, Array.Empty<string>()));
        Assert.False(ScanActivity.IsScan(CliPath, new[] { string.Empty, "scan" }));
        Assert.False(ScanActivity.IsScan(string.Empty, new[] { "skill", "scan" }));
    }

    [Theory]
    [InlineData(@"C:\Dev\bin\defenseclaw.exe", true)]
    [InlineData(@"C:\Dev\bin\defenseclaw.cmd", true)]
    [InlineData(@"C:\Dev\bin\DefenseClaw.EXE", true)]
    [InlineData(@"C:\Dev\bin\defenseclaw-gateway.exe", false)]
    [InlineData(@"C:\Windows\System32\cmd.exe", false)]
    [InlineData(@"C:\Dev\bin\defenseclaw-dev.exe", false)]
    public void Only_the_defenseclaw_command_line_tool_is_asked(string executable, bool expected)
    {
        Assert.Equal(expected, ScanActivity.IsScan(executable, new[] { "skill", "scan", "--all" }));
    }

    [Fact]
    public void The_gateways_codeguard_scan_is_a_scan_and_its_other_commands_are_not()
    {
        const string Gateway = @"C:\Dev\bin\defenseclaw-gateway.exe";

        Assert.True(ScanActivity.IsScan(Gateway, new[] { "scan", "code", "--", @"C:\src\project" }));
        Assert.True(ScanActivity.IsScan(Gateway, new[] { "scan", "code" }));
        Assert.False(ScanActivity.IsScan(Gateway, new[] { "status" }));
        Assert.False(ScanActivity.IsScan(Gateway, new[] { "start" }));
        Assert.False(ScanActivity.IsScan(Gateway, new[] { "scan", "--help" }));

        // The verb belongs to the gateway's binary: the Python CLI has no top-level scan.
        Assert.False(ScanActivity.IsScan(CliPath, new[] { "scan", "code", "--", @"C:\src\project" }));
    }

    [Fact]
    public void The_commands_the_app_builds_for_its_own_scans_are_scans()
    {
        // Not strings retyped here: the argv the Overview's "Scan skills" and the Runtime panel's "Poll now" hand to the runner.
        Assert.True(ScanActivity.IsScan(CliPath, DefenseClaw.App.ViewModels.OverviewPanelViewModel.ScanSkillsArgv));
        Assert.True(ScanActivity.IsScan(CliPath, DefenseClaw.Core.AiRuntime.AiRuntimeCommands.PollNow));

        // And the Runtime panel's other commands are not.
        Assert.False(ScanActivity.IsScan(CliPath, DefenseClaw.Core.AiRuntime.AiRuntimeCommands.ReadPermissions));
        Assert.False(ScanActivity.IsScan(CliPath, DefenseClaw.Core.AiRuntime.AiRuntimeCommands.Disable()));
    }

    [Fact]
    public void A_scan_run_in_the_developer_runtimes_container_is_still_a_scan()
    {
        // docker exec [-i] [-e NAME ...] CONTAINER TOOL ARGS: the shape RuntimeLaunch.ContainerArguments builds.
        const string Docker = @"C:\Program Files\Docker\Docker\resources\bin\docker.exe";

        Assert.True(ScanActivity.IsScan(Docker, new[] { "exec", "dc-dev", "defenseclaw", "skill", "scan", "--all" }));
        Assert.True(ScanActivity.IsScan(Docker, new[] { "exec", "dc-dev", "defenseclaw-gateway", "scan", "code", "--", "/src" }));
        Assert.True(ScanActivity.IsScan(Docker, new[] { "exec", "-i", "-e", "DEFENSECLAW_TOKEN", "-e", "OTHER", "dc-dev", "defenseclaw", "agent", "discovery", "runtime", "scan" }));
        Assert.False(ScanActivity.IsScan(Docker, new[] { "exec", "dc-dev", "defenseclaw", "skill", "list" }));
        Assert.False(ScanActivity.IsScan(Docker, new[] { "exec", "dc-dev", "defenseclaw-gateway", "status" }));
        Assert.False(ScanActivity.IsScan(Docker, new[] { "exec", "dc-dev", "python", "skill", "scan" }));
        Assert.False(ScanActivity.IsScan(Docker, new[] { "ps", "--all" }));
        Assert.False(ScanActivity.IsScan(Docker, new[] { "exec" }));
        Assert.False(ScanActivity.IsScan(Docker, new[] { "exec", "dc-dev" }));
        Assert.False(ScanActivity.IsScan(Docker, Array.Empty<string>()));
    }

    [Fact]
    public void The_invocation_overload_reads_the_executable_and_argv_the_runner_recorded()
    {
        Assert.True(ScanActivity.IsScan(Running("skill", "scan", "--all")));
        Assert.False(ScanActivity.IsScan(Running("skill", "list")));
        _ = Assert.Throws<ArgumentNullException>(() => ScanActivity.IsScan((CliInvocation)null!));
    }

    // ---- When scanning starts and stops --------------------------------------------------------------------------------

    [Fact]
    public void A_scan_in_flight_is_scanning_until_it_finishes()
    {
        using var watch = new Watch(_runner);
        var scan = Running("skill", "scan", "--all");
        Assert.False(watch.Tracker.IsScanning);

        watch.Tracker.OnStarted(null, scan);

        Assert.True(watch.Tracker.IsScanning);
        Assert.Equal(1, watch.Tracker.Count);
        Assert.Equal(new[] { true }, watch.Flips);

        InvocationFactory.Finish(scan);
        watch.Tracker.OnCompleted(null, scan);

        Assert.False(watch.Tracker.IsScanning);
        Assert.Equal(0, watch.Tracker.Count);
        Assert.Equal(new[] { true, false }, watch.Flips);
    }

    [Fact]
    public void Scans_at_once_are_one_stretch_of_scanning_that_ends_with_the_last()
    {
        using var watch = new Watch(_runner);
        var skills = Running("skill", "scan", "--all");
        var inventory = Running("aibom", "scan", "--json");

        watch.Tracker.OnStarted(null, skills);
        watch.Tracker.OnStarted(null, inventory);

        Assert.Equal(2, watch.Tracker.Count);
        Assert.Equal(new[] { true }, watch.Flips);

        InvocationFactory.Finish(skills);
        watch.Tracker.OnCompleted(null, skills);

        Assert.True(watch.Tracker.IsScanning);
        Assert.Equal(new[] { true }, watch.Flips);

        InvocationFactory.Finish(inventory, exitCode: 3);
        watch.Tracker.OnCompleted(null, inventory);

        Assert.False(watch.Tracker.IsScanning);
        Assert.Equal(new[] { true, false }, watch.Flips);
    }

    [Fact]
    public void A_scan_that_fails_or_is_stopped_still_ends_the_scanning()
    {
        using var watch = new Watch(_runner);
        var timedOut = Running("agent", "discovery", "scan");
        watch.Tracker.OnStarted(null, timedOut);

        InvocationFactory.Fail(timedOut, "timed out after 120 s — process tree killed");
        watch.Tracker.OnCompleted(null, timedOut);

        Assert.False(watch.Tracker.IsScanning);
        Assert.Equal(new[] { true, false }, watch.Flips);
    }

    [Fact]
    public void Commands_that_are_not_scans_leave_the_tracker_alone()
    {
        using var watch = new Watch(_runner);
        var list = Running("skill", "list");
        var doctor = Running("doctor");

        watch.Tracker.OnStarted(null, list);
        watch.Tracker.OnStarted(null, doctor);
        InvocationFactory.Finish(list);
        watch.Tracker.OnCompleted(null, list);

        Assert.False(watch.Tracker.IsScanning);
        Assert.Empty(watch.Flips);
    }

    [Fact]
    public void A_hand_off_that_is_born_finished_never_counts()
    {
        // CliRunner.RecordHandOff raises the started event for an entry that is already over: nothing is running, and the completion that
        // follows would only undo a state the tray had no time to show.
        using var watch = new Watch(_runner);
        var handOff = Running("skill", "scan", "--all");
        InvocationFactory.Finish(handOff);

        watch.Tracker.OnStarted(null, handOff);
        watch.Tracker.OnCompleted(null, handOff);

        Assert.False(watch.Tracker.IsScanning);
        Assert.Empty(watch.Flips);
    }

    [Fact]
    public void A_completion_for_a_command_never_seen_starting_is_ignored()
    {
        using var watch = new Watch(_runner);
        var running = Running("skill", "scan", "--all");
        watch.Tracker.OnStarted(null, running);

        var stranger = Running("skill", "scan", "--all");
        InvocationFactory.Finish(stranger);
        watch.Tracker.OnCompleted(null, stranger);

        Assert.True(watch.Tracker.IsScanning);
        Assert.Equal(1, watch.Tracker.Count);
        Assert.Equal(new[] { true }, watch.Flips);
    }

    [Fact]
    public void The_same_run_announced_twice_is_one_scan()
    {
        using var watch = new Watch(_runner);
        var scan = Running("mcp", "scan");

        watch.Tracker.OnStarted(null, scan);
        watch.Tracker.OnStarted(null, scan);

        Assert.Equal(1, watch.Tracker.Count);
        Assert.Equal(new[] { true }, watch.Flips);

        InvocationFactory.Finish(scan);
        watch.Tracker.OnCompleted(null, scan);
        watch.Tracker.OnCompleted(null, scan);

        Assert.False(watch.Tracker.IsScanning);
        Assert.Equal(new[] { true, false }, watch.Flips);
    }

    [Fact]
    public void A_scan_that_begins_and_ends_before_the_UI_thread_looks_raises_nothing()
    {
        // The flips are posted to the UI thread and read there; a start and an end that cancel out before it runs announce nothing.
        var queued = new List<Action>();
        using var watch = new Watch(_runner, post: queued.Add);
        var scan = Running("skill", "scan", "--all");

        watch.Tracker.OnStarted(null, scan);
        InvocationFactory.Finish(scan);
        watch.Tracker.OnCompleted(null, scan);
        foreach (var action in queued.ToArray())
        {
            action();
        }

        Assert.Empty(watch.Flips);
        Assert.False(watch.Tracker.IsScanning);
    }

    [Fact]
    public void The_UI_thread_hears_of_the_state_as_it_is_when_it_looks()
    {
        var queued = new List<Action>();
        using var watch = new Watch(_runner, post: queued.Add);
        var scan = Running("skill", "scan", "--all");

        watch.Tracker.OnStarted(null, scan);
        foreach (var action in queued.ToArray())
        {
            action();
        }

        Assert.Equal(new[] { true }, watch.Flips);
    }

    [Fact]
    public void Posting_happens_only_when_the_state_flips_so_an_idle_runner_costs_nothing()
    {
        var posts = 0;
        using var watch = new Watch(_runner, post: action =>
        {
            posts++;
            action();
        });

        // Forty commands of every kind, and two scans overlapping: the state flips twice.
        for (var i = 0; i < 40; i++)
        {
            var other = Running(i % 2 == 0 ? "skill" : "alerts", "list");
            watch.Tracker.OnStarted(null, other);
            InvocationFactory.Finish(other);
            watch.Tracker.OnCompleted(null, other);
        }

        var first = Running("skill", "scan", "--all");
        var second = Running("mcp", "scan");
        watch.Tracker.OnStarted(null, first);
        watch.Tracker.OnStarted(null, second);
        InvocationFactory.Finish(first);
        watch.Tracker.OnCompleted(null, first);
        InvocationFactory.Finish(second);
        watch.Tracker.OnCompleted(null, second);

        Assert.Equal(2, posts);
        Assert.Equal(new[] { true, false }, watch.Flips);
    }

    [Fact]
    public void A_subscriber_that_throws_does_not_stop_the_tracker_or_the_runner()
    {
        using var tracker = new ScanActivity(_runner, post: action => action());
        tracker.Changed += (_, _) => throw new InvalidOperationException("the tray is gone");
        var scan = Running("skill", "scan", "--all");

        tracker.OnStarted(null, scan);
        InvocationFactory.Finish(scan);
        tracker.OnCompleted(null, scan);

        Assert.False(tracker.IsScanning);
    }

    [Fact]
    public void A_predicate_that_throws_is_traced_not_thrown_into_the_runner()
    {
        using var watch = new Watch(_runner, isScan: _ => throw new InvalidOperationException("broken"));

        // Neither handler lets it out: a fault here must never be why a command does not start or finish.
        watch.Tracker.OnStarted(null, Running("skill", "scan"));
        watch.Tracker.OnCompleted(null, Running("skill", "scan"));

        Assert.False(watch.Tracker.IsScanning);
        Assert.Empty(watch.Flips);
    }

    // ---- Through the real runner ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_command_the_runner_runs_flips_the_tracker_on_the_way_in_and_out()
    {
        // The default predicate asks for defenseclaw, so a cmd.exe run is told to count: this proves the two subscriptions, not the verbs.
        using var watch = new Watch(_runner, isScan: invocation => invocation.Argv.Contains("/c"));

        var run = await _runner.RunExecutableAsync(CmdPath, new[] { "/c", "exit", "0" });

        Assert.Equal(0, run.ExitCode);
        Assert.False(watch.Tracker.IsScanning);
        Assert.Equal(new[] { true, false }, watch.Flips);
    }

    [Fact]
    public async Task A_command_that_is_not_a_scan_is_invisible_to_the_default_predicate()
    {
        using var watch = new Watch(_runner);

        _ = await _runner.RunExecutableAsync(CmdPath, new[] { "/c", "exit", "0" });
        _ = _runner.RecordHandOff("defenseclaw", new[] { "skill", "scan", "--all" }, "copied for a terminal");

        Assert.Empty(watch.Flips);
        Assert.False(watch.Tracker.IsScanning);
    }

    [Fact]
    public async Task A_scan_already_running_when_the_tracker_is_built_is_picked_up()
    {
        CliInvocation? started = null;
        _runner.InvocationStarted += (_, invocation) => started = invocation;
        var run = _runner.RunExecutableAsync(CmdPath, new[] { "/c", "ping -n 60 127.0.0.1 >nul" });
        try
        {
            await WaitUntilAsync(() => started is { IsRunning: true }, "the child to start");

            using var watch = new Watch(_runner, isScan: invocation => invocation.Argv.Contains("/c"));

            Assert.True(watch.Tracker.IsScanning);
            Assert.Equal(1, watch.Tracker.Count);

            Assert.True(_runner.Cancel(started!, out _));
            _ = await run.WaitAsync(TimeSpan.FromSeconds(60));

            Assert.False(watch.Tracker.IsScanning);
            Assert.Equal(new[] { false }, watch.Flips);
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
        var watch = new Watch(_runner, isScan: _ => true);
        watch.Dispose();
        watch.Dispose();

        _ = await _runner.RunExecutableAsync(CmdPath, new[] { "/c", "exit", "0" });

        Assert.False(watch.Tracker.IsScanning);
        Assert.Empty(watch.Flips);
    }

    [Fact]
    public void A_tracker_needs_a_runner()
    {
        _ = Assert.Throws<ArgumentNullException>(() => new ScanActivity(null!));
    }
}
