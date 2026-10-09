using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Gateway.Models;

namespace DefenseClaw.App.Tests.PendingRestart;

/// <summary>
/// The app-wide restart queue (CUST-267) over a fake monitor and a manual clock: what a line is, what applies it (a start time, a binary version,
/// a finished restart, Clear), and that it costs nothing while it is empty. What decides whether a command or a save queues is
/// <see cref="RestartQueueRulesTests"/>; the surfaces that show it are <see cref="RestartQueueSurfacesTests"/>.
/// </summary>
public sealed class RestartQueueTests
{
    private sealed class Rig : IDisposable
    {
        public Rig(CliRunner? cli = null)
        {
            Queue = new RestartQueue(Source, cli, Clock, post: action => action());
            Queue.Changed += (_, _) => Changes++;
        }

        public FakeSnapshotSource Source { get; } = new();

        public ManualClock Clock { get; } = new();

        public RestartQueue Queue { get; }

        public int Changes { get; set; }

        public DateTimeOffset Now => Clock.GetUtcNow();

        /// <summary>A completed poll of a gateway that started at <paramref name="startedAt"/> and runs <paramref name="version"/>.</summary>
        public void PollRunning(DateTimeOffset startedAt, string? version = "0.8.10") =>
            Source.Poll(new GatewaySnapshot
            {
                State = AppGatewayState.Running,
                Health = new GatewayHealth { StartedAt = startedAt, UptimeMs = 60_000 },
                BinaryVersion = version,
                PolledAt = Now,
            });

        public void Dispose() => Queue.Dispose();
    }

    private static CliInvocation Finished(string tool, string commandLine, int exitCode = 0, DateTimeOffset? startedAt = null)
    {
        var invocation = InvocationFactory.CreateFor(tool, commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries), startedAt);
        InvocationFactory.Finish(invocation, exitCode);
        return invocation;
    }

    // ------------------------------------------------------------------ lines

    [Fact]
    public void Nothing_is_queued_at_first_and_the_monitors_poll_is_not_listened_to()
    {
        using var rig = new Rig();

        Assert.False(rig.Queue.IsPending);
        Assert.Equal(string.Empty, rig.Queue.Reason);
        Assert.Null(rig.Queue.QueuedAt);
        Assert.Empty(rig.Queue.Entries);
        Assert.Equal(0, rig.Source.PollSubscribers);
    }

    [Fact]
    public void A_reason_is_queued_with_its_time_and_the_poll_is_listened_to_from_then_on()
    {
        using var rig = new Rig();

        Assert.True(rig.Queue.Queue("guardrail hilt ran with --no-restart"));

        var entry = Assert.Single(rig.Queue.Entries);
        Assert.Equal(new RestartQueueEntry("guardrail hilt ran with --no-restart", rig.Now), entry);
        Assert.True(rig.Queue.IsPending);
        Assert.Equal("guardrail hilt ran with --no-restart", rig.Queue.Reason);
        Assert.Equal(rig.Now, rig.Queue.QueuedAt);
        Assert.Equal(1, rig.Source.PollSubscribers);
        Assert.Equal(1, rig.Changes);
    }

    [Fact]
    public void Reasons_are_joined_oldest_first_and_a_repeat_is_one_line_with_the_later_time()
    {
        using var rig = new Rig();
        var first = rig.Now;

        _ = rig.Queue.Queue("a");
        rig.Clock.Advance(TimeSpan.FromMinutes(1));
        _ = rig.Queue.Queue("b");
        rig.Clock.Advance(TimeSpan.FromMinutes(1));
        var again = rig.Queue.Queue("a");

        Assert.False(again);
        Assert.Equal("a; b", rig.Queue.Reason);
        Assert.Equal(new[] { first.AddMinutes(2), first.AddMinutes(1) }, rig.Queue.Entries.Select(e => e.QueuedAt));

        // The oldest line still queued is the TUI's queued_at, and a line queued again counts from the later time.
        Assert.Equal(first.AddMinutes(1), rig.Queue.QueuedAt);

        // The repeat added nothing to look at: one change for each of the two lines.
        Assert.Equal(2, rig.Changes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void A_blank_reason_queues_nothing(string? reason)
    {
        using var rig = new Rig();

        Assert.False(rig.Queue.Queue(reason));

        Assert.False(rig.Queue.IsPending);
        Assert.Equal(0, rig.Changes);
        Assert.Equal(0, rig.Source.PollSubscribers);
    }

    [Fact]
    public void A_reason_is_kept_as_one_line_cut_to_its_length()
    {
        using var rig = new Rig();

        _ = rig.Queue.Queue("first line\nsecond line " + new string('x', 400));

        var reason = Assert.Single(rig.Queue.Entries).Reason;
        Assert.DoesNotContain('\n', reason);
        Assert.StartsWith("first line\\nsecond line", reason, StringComparison.Ordinal);
        Assert.Equal(RestartQueueRules.MaxReasonLength, reason.Length);
    }

    [Fact]
    public void The_oldest_line_goes_when_more_than_the_most_are_queued()
    {
        using var rig = new Rig();

        for (var i = 0; i < RestartQueue.MaxEntries + 5; i++)
        {
            _ = rig.Queue.Queue("line " + i);
        }

        Assert.Equal(RestartQueue.MaxEntries, rig.Queue.Entries.Count);
        Assert.Equal("line 5", rig.Queue.Entries[0].Reason);
        Assert.Equal("line " + (RestartQueue.MaxEntries + 4), rig.Queue.Entries[^1].Reason);
    }

    [Fact]
    public void Clear_empties_the_queue_stops_listening_and_says_so_once()
    {
        using var rig = new Rig();
        _ = rig.Queue.Queue("a");
        _ = rig.Queue.Queue("b");
        rig.Changes = 0;

        Assert.True(rig.Queue.Clear());
        Assert.False(rig.Queue.Clear());

        Assert.False(rig.Queue.IsPending);
        Assert.Empty(rig.Queue.Entries);
        Assert.Equal(string.Empty, rig.Queue.Reason);
        Assert.Null(rig.Queue.QueuedAt);
        Assert.Equal(0, rig.Source.PollSubscribers);
        Assert.Equal(1, rig.Changes);
    }

    [Fact]
    public void A_handler_that_throws_costs_only_its_own_notice()
    {
        using var rig = new Rig();
        var heard = 0;
        rig.Queue.Changed += (_, _) => throw new InvalidOperationException("a panel that cannot redraw");
        rig.Queue.Changed += (_, _) => heard++;

        Assert.True(rig.Queue.Queue("a"));

        Assert.Equal(1, heard);
        Assert.True(rig.Queue.IsPending);
    }

    // ------------------------------------------------------------------ the gateway restarted (the monitor's poll)

    [Fact]
    public void A_start_after_the_line_was_queued_applies_it_whoever_restarted_the_gateway()
    {
        using var rig = new Rig();
        var before = rig.Now.AddHours(-3);
        _ = rig.Queue.Queue("config.yaml saved in the config editor (guardrail)");
        rig.Changes = 0;

        // The gateway has been up since before the save: nothing changed, nothing is said.
        rig.PollRunning(before);
        Assert.True(rig.Queue.IsPending);
        Assert.Equal(0, rig.Changes);

        // A restart from anywhere - the tray, the palette, another app, a terminal - is only a new start time.
        rig.Clock.Advance(TimeSpan.FromMinutes(2));
        rig.PollRunning(rig.Now.AddSeconds(-20));

        Assert.False(rig.Queue.IsPending);
        Assert.Equal(1, rig.Changes);
        Assert.Equal(0, rig.Source.PollSubscribers);
    }

    [Fact]
    public void A_start_from_before_the_line_was_queued_does_not_apply_it_however_new_it_looks_to_the_monitor()
    {
        // The monitor's last poll can be one interval old: a restart just before a save is seen by the first poll after it, and the save is not in it.
        using var rig = new Rig();
        var restartedJustBefore = rig.Now.AddSeconds(-2);
        _ = rig.Queue.Queue("a");

        rig.PollRunning(restartedJustBefore);
        rig.PollRunning(restartedJustBefore);

        Assert.True(rig.Queue.IsPending);
    }

    [Fact]
    public void Each_line_is_applied_by_the_first_start_that_followed_it()
    {
        using var rig = new Rig();
        _ = rig.Queue.Queue("a");
        rig.Clock.Advance(TimeSpan.FromMinutes(10));
        var restartedAt = rig.Now;
        rig.Clock.Advance(TimeSpan.FromMinutes(10));
        _ = rig.Queue.Queue("b");
        rig.Changes = 0;

        rig.PollRunning(restartedAt);

        Assert.Equal("b", rig.Queue.Reason);
        Assert.Equal(1, rig.Changes);
        Assert.Equal(1, rig.Source.PollSubscribers);

        // ... and the queued-at is now b's.
        Assert.Equal(rig.Now, rig.Queue.QueuedAt);
    }

    [Fact]
    public void The_uptime_stands_in_for_a_start_time_the_gateway_does_not_report()
    {
        using var rig = new Rig();
        _ = rig.Queue.Queue("a");
        rig.Clock.Advance(TimeSpan.FromMinutes(5));

        // Up for 10 minutes at this poll: it started before the line was queued.
        rig.Source.Poll(new GatewaySnapshot { State = AppGatewayState.Running, Health = new GatewayHealth { UptimeMs = 10 * 60_000 }, PolledAt = rig.Now });
        Assert.True(rig.Queue.IsPending);

        // Up for 1 minute at a poll 5 minutes later: it started after.
        rig.Clock.Advance(TimeSpan.FromMinutes(5));
        rig.Source.Poll(new GatewaySnapshot { State = AppGatewayState.Running, Health = new GatewayHealth { UptimeMs = 60_000 }, PolledAt = rig.Now });
        Assert.False(rig.Queue.IsPending);
    }

    [Fact]
    public void A_gateway_that_is_not_answering_applies_nothing()
    {
        using var rig = new Rig();
        _ = rig.Queue.Queue("a");

        rig.Source.Poll(new GatewaySnapshot { State = AppGatewayState.GatewayStopped, PolledAt = rig.Now.AddHours(1) });
        rig.Source.Poll(new GatewaySnapshot { State = AppGatewayState.Degraded, Health = new GatewayHealth(), PolledAt = rig.Now.AddHours(1) });

        Assert.True(rig.Queue.IsPending);
    }

    [Fact]
    public void A_gateway_started_after_a_line_was_queued_while_it_was_down_applies_it()
    {
        using var rig = new Rig();
        rig.Source.Current = new GatewaySnapshot { State = AppGatewayState.GatewayStopped, PolledAt = rig.Now };
        _ = rig.Queue.Queue("a");

        rig.Clock.Advance(TimeSpan.FromMinutes(1));
        rig.PollRunning(rig.Now);

        Assert.False(rig.Queue.IsPending);
    }

    // ------------------------------------------------------------------ the binary changed

    [Fact]
    public void A_different_binary_version_applies_everything_even_when_no_start_time_is_reported()
    {
        using var rig = new Rig();
        rig.Source.Current = new GatewaySnapshot { BinaryVersion = "0.8.10" };
        _ = rig.Queue.Queue("a");
        _ = rig.Queue.Queue("b");

        rig.Source.Poll(new GatewaySnapshot { State = AppGatewayState.Running, Health = new GatewayHealth(), BinaryVersion = "0.8.10", PolledAt = rig.Now });
        Assert.True(rig.Queue.IsPending);

        rig.Source.Poll(new GatewaySnapshot { State = AppGatewayState.Running, Health = new GatewayHealth(), BinaryVersion = "0.8.11", PolledAt = rig.Now });

        Assert.False(rig.Queue.IsPending);
        Assert.Equal(0, rig.Source.PollSubscribers);
    }

    [Fact]
    public void The_same_version_in_another_case_or_padded_is_the_same_binary()
    {
        using var rig = new Rig();
        rig.Source.Current = new GatewaySnapshot { BinaryVersion = "1.0.0-RC1" };
        _ = rig.Queue.Queue("a");

        rig.Source.Poll(new GatewaySnapshot { State = AppGatewayState.Running, Health = new GatewayHealth(), BinaryVersion = " 1.0.0-rc1 ", PolledAt = rig.Now });

        Assert.True(rig.Queue.IsPending);
    }

    [Fact]
    public void A_version_that_was_not_known_when_the_line_was_queued_is_learned_by_the_next_poll_that_has_one()
    {
        using var rig = new Rig();
        _ = rig.Queue.Queue("a");

        rig.Source.Poll(new GatewaySnapshot { State = AppGatewayState.Running, Health = new GatewayHealth(), BinaryVersion = "0.8.10", PolledAt = rig.Now });
        Assert.True(rig.Queue.IsPending);

        rig.Source.Poll(new GatewaySnapshot { State = AppGatewayState.Running, Health = new GatewayHealth(), BinaryVersion = "0.8.11", PolledAt = rig.Now });
        Assert.False(rig.Queue.IsPending);
    }

    [Fact]
    public void A_poll_with_no_version_says_nothing_about_the_binary()
    {
        using var rig = new Rig();
        rig.Source.Current = new GatewaySnapshot { BinaryVersion = "0.8.10" };
        _ = rig.Queue.Queue("a");

        rig.Source.Poll(new GatewaySnapshot { State = AppGatewayState.GatewayStopped, BinaryVersion = null, PolledAt = rig.Now });
        rig.Source.Poll(new GatewaySnapshot { State = AppGatewayState.GatewayStopped, BinaryVersion = "  ", PolledAt = rig.Now });

        Assert.True(rig.Queue.IsPending);
    }

    // ------------------------------------------------------------------ the app's own commands (the runner's finished runs)

    [Fact]
    public void A_wizard_run_told_not_to_restart_queues_a_line_when_it_finishes()
    {
        using var rig = new Rig();

        var changed = rig.Queue.Note(Finished("defenseclaw", "setup claudecode --yes --mode observe --no-restart"));

        Assert.True(changed);
        Assert.Equal("setup claudecode ran with --no-restart", rig.Queue.Reason);
    }

    [Theory]
    [InlineData("guardrail enable --yes --no-restart", "guardrail enable ran with --no-restart")]
    [InlineData("guardrail hilt on --yes --no-restart", "guardrail hilt ran with --no-restart")]
    [InlineData("agent discovery enable --yes --no-restart --no-scan", "agent discovery ran with --no-restart")]
    public void The_guardrail_and_discovery_runs_told_not_to_restart_queue_too(string commandLine, string expected)
    {
        using var rig = new Rig();

        Assert.True(rig.Queue.Note(Finished("defenseclaw", commandLine)));

        Assert.Equal(expected, rig.Queue.Reason);
    }

    [Theory]
    [InlineData("defenseclaw", "guardrail enable --yes --no-restart", 1)]   // it failed
    [InlineData("defenseclaw", "guardrail enable --yes", 0)]                // it restarted
    [InlineData("defenseclaw", "guardrail status", 0)]                      // a read
    [InlineData("defenseclaw", "setup claudecode --dry-run --no-restart", 0)] // a preview
    [InlineData("defenseclaw-gateway", "status", 0)]
    public void A_run_that_saved_nothing_that_is_waiting_queues_nothing(string tool, string commandLine, int exitCode)
    {
        using var rig = new Rig();

        Assert.False(rig.Queue.Note(Finished(tool, commandLine, exitCode)));

        Assert.False(rig.Queue.IsPending);
    }

    [Fact]
    public void A_run_that_is_still_running_or_is_not_defenseclaws_is_nothing()
    {
        using var rig = new Rig();

        var running = InvocationFactory.CreateFor("defenseclaw", new[] { "setup", "claudecode", "--yes", "--no-restart" });
        var installer = Finished(@"C:\tools\cosign.exe", "setup claudecode --yes --no-restart");

        Assert.False(rig.Queue.Note(running));
        Assert.False(rig.Queue.Note(installer));
        Assert.False(rig.Queue.IsPending);
    }

    [Fact]
    public void A_run_the_runner_refused_or_handed_to_a_terminal_never_ran_and_queues_nothing()
    {
        using var temp = new TempDirectory();
        var cli = new CliRunner(TestServices.IsolatedPaths(temp.Path), neutralWorkingDirectory: temp.File("cwd"));
        using var rig = new Rig(cli);

        // The runner's own records, raised the way a run's completion is: no exit code, nothing started.
        var argv = new[] { "setup", "claudecode", "--yes", "--no-restart" };
        _ = cli.RecordRefusal("defenseclaw", argv, "this installation is read only");
        _ = cli.RecordHandOff("defenseclaw", argv, "Handed to a console window.");

        Assert.False(rig.Queue.IsPending);
    }

    [Fact]
    public void A_finished_restart_applies_the_lines_queued_before_it_began_without_waiting_for_a_poll()
    {
        using var rig = new Rig();
        _ = rig.Queue.Queue("a");
        rig.Clock.Advance(TimeSpan.FromSeconds(30));
        _ = rig.Queue.Queue("b");
        rig.Clock.Advance(TimeSpan.FromSeconds(30));
        rig.Changes = 0;

        // The tray's, the palette's and Restart now's run is the gateway's own verb ...
        Assert.True(rig.Queue.Note(Finished("defenseclaw-gateway", "restart", startedAt: rig.Now)));

        Assert.False(rig.Queue.IsPending);
        Assert.Equal(1, rig.Changes);
        Assert.Equal(0, rig.Source.PollSubscribers);
    }

    [Fact]
    public void A_setup_or_guardrail_run_that_restarted_the_gateway_applies_them_too()
    {
        using var rig = new Rig();
        _ = rig.Queue.Queue("a");
        rig.Clock.Advance(TimeSpan.FromSeconds(1));

        Assert.True(rig.Queue.Note(Finished("defenseclaw", "guardrail enable --yes", startedAt: rig.Now)));

        Assert.False(rig.Queue.IsPending);
    }

    [Fact]
    public void A_run_the_reviews_rule_calls_a_restart_applies_the_queue_even_though_it_carries_the_flag()
    {
        // setup local-observability up restarts through the setup group's callback and has no --no-restart of its own: the rule that states the
        // restart before the run says it restarts, so to the queue it is a restart and not a line.
        using var rig = new Rig();
        _ = rig.Queue.Queue("a");
        rig.Clock.Advance(TimeSpan.FromSeconds(1));

        Assert.True(rig.Queue.Note(Finished("defenseclaw", "setup local-observability up --no-restart", startedAt: rig.Now)));

        Assert.False(rig.Queue.IsPending);
    }

    [Fact]
    public void A_line_queued_while_a_restart_ran_is_left_to_the_monitor_to_settle()
    {
        using var rig = new Rig();
        _ = rig.Queue.Queue("before the restart");
        rig.Clock.Advance(TimeSpan.FromSeconds(10));
        var began = rig.Now;
        rig.Clock.Advance(TimeSpan.FromSeconds(5));
        _ = rig.Queue.Queue("while it ran");

        Assert.True(rig.Queue.Note(Finished("defenseclaw-gateway", "restart", startedAt: began)));

        Assert.Equal("while it ran", rig.Queue.Reason);

        // The restarted gateway reports when it started. Early in the run - before the line was queued - it did not read that line ...
        rig.PollRunning(began.AddSeconds(2));
        Assert.True(rig.Queue.IsPending);

        // ... and a start after the line was queued did.
        rig.PollRunning(began.AddSeconds(20));
        Assert.False(rig.Queue.IsPending);
    }

    [Fact]
    public void A_restart_that_failed_or_was_stopped_applies_nothing()
    {
        using var rig = new Rig();
        _ = rig.Queue.Queue("a");
        rig.Clock.Advance(TimeSpan.FromSeconds(1));

        var failed = Finished("defenseclaw-gateway", "restart", exitCode: 1, startedAt: rig.Now);
        var cancelled = InvocationFactory.CreateFor("defenseclaw-gateway", new[] { "restart" }, rig.Now);
        InvocationFactory.Fail(cancelled, "cancelled — process tree killed");

        Assert.False(rig.Queue.Note(failed));
        Assert.False(rig.Queue.Note(cancelled));
        Assert.True(rig.Queue.IsPending);
    }

    [Fact]
    public void Start_and_stop_are_not_restarts_here_the_monitor_sees_a_gateway_that_came_up()
    {
        using var rig = new Rig();
        _ = rig.Queue.Queue("a");
        rig.Clock.Advance(TimeSpan.FromSeconds(1));

        Assert.False(rig.Queue.Note(Finished("defenseclaw-gateway", "start", startedAt: rig.Now)));
        Assert.False(rig.Queue.Note(Finished("defenseclaw-gateway", "stop", startedAt: rig.Now)));

        Assert.True(rig.Queue.IsPending);
    }

    // ------------------------------------------------------------------ wired to the real runner and monitor

    [Fact]
    public async Task The_runner_tells_the_queue_when_a_command_finishes_and_the_composition_wires_it()
    {
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp);

        // A copy of cmd.exe called defenseclaw.exe (the technique InstallationGateTests uses): a real child, a real exit 0, in a scratch folder.
        var fake = temp.File("defenseclaw.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), fake);

        var invocation = await services.Cli.RunExecutableAsync(fake, new[] { "/c", "echo", "setup", "claudecode", "--yes", "--no-restart" });

        Assert.Equal(0, invocation.ExitCode);
        Assert.True(services.RestartQueue.IsPending);
        Assert.EndsWith("ran with --no-restart", services.RestartQueue.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void The_composition_hands_the_queue_the_monitor_and_nothing_is_delivered_while_it_is_empty()
    {
        using var scene = OverviewScene.Create(seedAudit: false);
        var services = scene.Services;

        // Nothing queued: the monitor has no one to deliver the poll to on the queue's behalf.
        var idle = services.Monitor.PollCompletedSubscriberCount;
        scene.Publish(OverviewScene.Snapshot());

        _ = services.RestartQueue.Queue("config.yaml saved in the config editor (llm)");
        Assert.Equal(idle + 1, services.Monitor.PollCompletedSubscriberCount);

        // The scene's gateway has been up for hours: that start precedes the line, so a poll leaves it.
        scene.Publish(OverviewScene.Snapshot());
        Assert.True(services.RestartQueue.IsPending);

        // A restart, by whatever route, is a poll that reports a start after the line was queued.
        var restarted = OverviewScene.Snapshot(health: new GatewayHealth { StartedAt = DateTimeOffset.UtcNow.AddSeconds(1), UptimeMs = 1_000 }) with
        {
            PolledAt = DateTimeOffset.UtcNow.AddSeconds(2),
        };
        scene.Publish(restarted);

        Assert.False(services.RestartQueue.IsPending);
        Assert.Equal(idle, services.Monitor.PollCompletedSubscriberCount);
    }

    [Fact]
    public void A_disposed_queue_ignores_the_runner_and_the_monitor_and_raises_nothing()
    {
        using var rig = new Rig();
        _ = rig.Queue.Queue("a");
        rig.Changes = 0;

        rig.Queue.Dispose();

        Assert.False(rig.Queue.Queue("b"));
        Assert.Equal(0, rig.Source.PollSubscribers);
        Assert.Equal(0, rig.Changes);
        Assert.False(rig.Queue.IsPending);
    }
}
