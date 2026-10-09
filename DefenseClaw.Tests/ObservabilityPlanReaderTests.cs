using System.Text.Json;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Observability;
using DefenseClaw.Core.Paths;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// <see cref="ObservabilityPlanReader"/> (CUST-272): what it runs, that it parses what comes back, that a hung, failing or garbled command is a
/// sentence and never a hang or a throw, and that the answer is reused between polls - until it is five minutes old, until the configuration it was
/// compiled from is replaced, or until somebody asks again. The command is a fake that returns the fixtures; no process is started.
/// </summary>
public class ObservabilityPlanReaderTests
{
    private static readonly string FreshPlan = FixtureFiles.ReadText("runtime-95159fd/cli/observability-plan.json");
    private static readonly string FullPlan = FixtureFiles.ReadText("runtime-0.8.10/cli/observability-plan.destinations.synthetic.json");

    /// <summary>A command that answers from a queue of canned outputs (the last one repeats) and records every run.</summary>
    private sealed class FakeCommand
    {
        private readonly Queue<Func<CancellationToken, Task<PlanCommandOutput>>> _answers = new();
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public List<string> Asked { get; } = new();

        public List<TimeSpan> Timeouts { get; } = new();

        public FakeCommand Then(string stdout, int exitCode = 0) => Then(_ => Task.FromResult(new PlanCommandOutput(exitCode, null, stdout)));

        public FakeCommand Then(Func<CancellationToken, Task<PlanCommandOutput>> answer)
        {
            _answers.Enqueue(answer);
            return this;
        }

        public Task<PlanCommandOutput> Run(IReadOnlyList<string> argv, TimeSpan timeout, CancellationToken token)
        {
            _ = Interlocked.Increment(ref _calls);
            lock (Asked)
            {
                Asked.Add(string.Join(' ', argv));
                Timeouts.Add(timeout);
            }

            Func<CancellationToken, Task<PlanCommandOutput>> answer;
            lock (_answers)
            {
                answer = _answers.Count > 1 ? _answers.Dequeue() : _answers.Peek();
            }

            return answer(token);
        }
    }

    private static ObservabilityPlanReader Reader(FakeCommand command, Func<object?>? token = null, TimeProvider? time = null) =>
        new(command.Run, token, time);

    // ---- what it runs ----

    [Fact]
    public async Task It_runs_observability_plan_format_json_and_nothing_else_and_that_is_a_read()
    {
        var command = new FakeCommand().Then(FreshPlan);

        var read = await Reader(command).GetAsync();

        Assert.True(read.IsOk, read.Message);
        Assert.Equal(new[] { "observability plan --format json" }, command.Asked);
        Assert.Equal(new[] { "observability", "plan", "--format", "json" }, ObservabilityPlanReader.Argv);
        Assert.Equal(CommandTier.ReadOnly, CommandTiers.Classify(ObservabilityPlanReader.Argv));
        Assert.Equal(ObservabilityPlanReader.DefaultTimeout, Assert.Single(command.Timeouts));
    }

    [Theory]
    [InlineData("cli-tree-0.8.10.json")]
    [InlineData("runtime-95159fd/cli/cli-tree.json")]
    public void Both_runtimes_command_trees_have_that_command_with_that_option(string tree)
    {
        using var document = JsonDocument.Parse(FixtureFiles.ReadText(tree));

        var plan = document.RootElement.GetProperty("commands").EnumerateArray().Single(c => c.GetProperty("path").GetString() == "observability plan");

        Assert.False(plan.GetProperty("group").GetBoolean());
        Assert.Contains(plan.GetProperty("options").EnumerateArray(), o => o.GetProperty("names").EnumerateArray().Any(n => n.GetString() == "--format"));
    }

    [Fact]
    public async Task The_plan_it_reads_is_the_one_the_parser_makes()
    {
        var read = await Reader(new FakeCommand().Then(FullPlan)).GetAsync();

        Assert.Equal(ObservabilityPlanStatus.Ok, read.Status);
        Assert.Equal(5, read.Plan!.Destinations.Count);
        Assert.Equal(string.Empty, read.Message);
    }

    // ---- when it goes wrong ----

    [Fact]
    public async Task A_command_that_hangs_is_stopped_and_is_a_timeout_not_a_hang()
    {
        var command = new FakeCommand().Then(async token =>
        {
            await Task.Delay(System.Threading.Timeout.Infinite, token);
            return new PlanCommandOutput(0, null, FreshPlan);
        });
        var reader = new ObservabilityPlanReader(command.Run) { Timeout = TimeSpan.FromMilliseconds(50) };

        var read = await reader.GetAsync().WaitAsync(TestTimeouts.Ceiling);

        Assert.Equal(ObservabilityPlanStatus.TimedOut, read.Status);
        Assert.Null(read.Plan);
        Assert.StartsWith("the command did not finish in ", read.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("timed out after 30 s", ObservabilityPlanStatus.TimedOut)]
    [InlineData("cancelled", ObservabilityPlanStatus.Failed)]
    [InlineData("cancelled: DefenseClaw for Windows is exiting", ObservabilityPlanStatus.Failed)]
    [InlineData("not started", ObservabilityPlanStatus.Failed)]
    public async Task A_run_the_runner_stopped_says_why_in_the_runners_words(string reason, ObservabilityPlanStatus expected)
    {
        var command = new FakeCommand().Then(_ => Task.FromResult(new PlanCommandOutput(null, reason, string.Empty)));

        var read = await Reader(command).GetAsync();

        Assert.Equal(expected, read.Status);
        Assert.Null(read.Plan);
        Assert.StartsWith("the command did not finish", read.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, "the command exited 1")]
    [InlineData(2, "the command exited 2")]
    [InlineData(-1073741819, "the command exited -1073741819")]
    public async Task A_command_that_exits_non_zero_is_a_failure_that_names_the_code_and_quotes_nothing_it_printed(int code, string message)
    {
        var printed = "Error: Post https://synthuser:synthpass@collector.example.test/v1?api_key=synthkey refused";
        var command = new FakeCommand().Then(printed, code);

        var read = await Reader(command).GetAsync();

        Assert.Equal(ObservabilityPlanStatus.Failed, read.Status);
        Assert.Equal(message, read.Message);
        Assert.DoesNotContain("synth", read.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_command_without_an_exit_code_or_a_reason_is_a_failure()
    {
        var command = new FakeCommand().Then(_ => Task.FromResult(new PlanCommandOutput(null, null, FreshPlan)));

        var read = await Reader(command).GetAsync();

        Assert.Equal(ObservabilityPlanStatus.Failed, read.Status);
        Assert.Equal("the command exited without a code", read.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Usage: defenseclaw observability plan [OPTIONS]")]
    [InlineData("{}")]
    [InlineData("{\"rows\":[]}")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"rows\":[{\"bucket\":")]
    [InlineData("{\"rows\":\"https://synthuser:synthpass@collector.example.test/?api_key=synthkey\"}")]
    public async Task Output_that_is_not_a_plan_is_malformed_and_the_sentence_quotes_none_of_it(string stdout)
    {
        var read = await Reader(new FakeCommand().Then(stdout)).GetAsync();

        Assert.Equal(ObservabilityPlanStatus.Malformed, read.Status);
        Assert.Null(read.Plan);
        Assert.StartsWith("the command did not print a plan (", read.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("synth", read.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Output_the_runner_had_to_cut_is_malformed_even_when_what_is_left_parses()
    {
        var command = new FakeCommand().Then(_ => Task.FromResult(new PlanCommandOutput(0, null, FreshPlan, Truncated: true)));

        var read = await Reader(command).GetAsync();

        Assert.Equal(ObservabilityPlanStatus.Malformed, read.Status);
        Assert.Equal("the command printed more than the app keeps", read.Message);
    }

    [Fact]
    public async Task A_banner_before_the_json_does_not_stop_it_being_read()
    {
        var read = await Reader(new FakeCommand().Then("DefenseClaw 0.8.10\n" + FreshPlan)).GetAsync();

        Assert.True(read.IsOk, read.Message);
    }

    [Fact]
    public async Task No_cli_is_not_installed_and_names_the_executable()
    {
        var command = new FakeCommand().Then(_ => throw new CliNotFoundException("defenseclaw", new[] { @"C:\no\such\bin" }));

        var read = await Reader(command).GetAsync();

        Assert.Equal(ObservabilityPlanStatus.NotInstalled, read.Status);
        Assert.Equal("'defenseclaw' was not found", read.Message);
    }

    [Fact]
    public async Task A_runner_that_throws_is_a_failure_that_reports_the_kind_of_exception_and_not_its_message()
    {
        var command = new FakeCommand().Then(_ => throw new InvalidOperationException("boom https://synthuser:synthpass@collector.example.test/?api_key=synthkey"));

        var read = await Reader(command).GetAsync();

        Assert.Equal(ObservabilityPlanStatus.Failed, read.Status);
        Assert.Equal("the command could not be run (InvalidOperationException)", read.Message);
    }

    [Fact]
    public async Task A_runner_that_is_cancelled_by_something_other_than_the_time_limit_is_a_failure()
    {
        var command = new FakeCommand().Then(_ => throw new OperationCanceledException());

        var read = await Reader(command).GetAsync();

        Assert.Equal(ObservabilityPlanStatus.Failed, read.Status);
        Assert.Equal("the command was cancelled", read.Message);
    }

    // ---- not every tick ----

    [Fact]
    public async Task A_plan_is_reused_until_it_is_five_minutes_old_by_the_monotonic_clock()
    {
        var clock = new ManualClock();
        var command = new FakeCommand().Then(FreshPlan);
        var reader = Reader(command, time: clock);

        var first = await reader.GetAsync();
        clock.Advance(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(59));
        var second = await reader.GetAsync();
        Assert.Same(first, second);
        Assert.Equal(1, command.Calls);

        clock.Advance(TimeSpan.FromSeconds(2));
        var third = await reader.GetAsync();

        Assert.NotSame(first, third);
        Assert.Equal(2, command.Calls);
        Assert.Equal(TimeSpan.FromMinutes(5), ObservabilityPlanReader.DefaultMaxAge);
    }

    [Fact]
    public async Task A_step_of_the_wall_clock_does_not_hold_the_next_read_back_or_bring_it_forward()
    {
        var clock = new ManualClock();
        var command = new FakeCommand().Then(FreshPlan);
        var reader = Reader(command, time: clock);

        _ = await reader.GetAsync();
        clock.StepWallClock(TimeSpan.FromHours(-3));
        clock.Advance(TimeSpan.FromMinutes(1));
        _ = await reader.GetAsync();
        Assert.Equal(1, command.Calls);

        clock.StepWallClock(TimeSpan.FromHours(6));
        _ = await reader.GetAsync();
        Assert.Equal(1, command.Calls);
    }

    [Fact]
    public async Task A_forced_read_runs_the_command_whatever_the_age_of_the_last()
    {
        var command = new FakeCommand().Then(FreshPlan);
        var reader = Reader(command);

        _ = await reader.GetAsync();
        _ = await reader.GetAsync();
        Assert.Equal(1, command.Calls);

        _ = await reader.GetAsync(force: true);
        Assert.Equal(2, command.Calls);
    }

    [Fact]
    public async Task Invalidate_ends_the_answer()
    {
        var command = new FakeCommand().Then(FreshPlan);
        var reader = Reader(command);

        _ = await reader.GetAsync();
        reader.Invalidate();
        _ = await reader.GetAsync();

        Assert.Equal(2, command.Calls);
    }

    [Fact]
    public async Task A_new_configuration_ends_the_answer_and_the_same_one_does_not()
    {
        var command = new FakeCommand().Then(FreshPlan);
        object configuration = new();
        var reader = Reader(command, () => configuration);

        _ = await reader.GetAsync();
        _ = await reader.GetAsync();
        Assert.Equal(1, command.Calls);

        configuration = new object();
        _ = await reader.GetAsync();
        _ = await reader.GetAsync();

        Assert.Equal(2, command.Calls);
    }

    [Fact]
    public async Task A_failure_is_served_for_a_minute_so_a_missing_cli_costs_one_attempt_and_not_one_per_poll()
    {
        var clock = new ManualClock();
        var command = new FakeCommand().Then(_ => throw new CliNotFoundException("defenseclaw", Array.Empty<string>()));
        var reader = Reader(command, time: clock);

        var first = await reader.GetAsync();
        for (var tick = 0; tick < 11; tick++)
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            Assert.Same(first, await reader.GetAsync());
        }

        Assert.Equal(1, command.Calls);

        clock.Advance(TimeSpan.FromSeconds(10));
        var retried = await reader.GetAsync();

        Assert.NotSame(first, retried);
        Assert.Equal(2, command.Calls);
        Assert.Equal(TimeSpan.FromSeconds(60), ObservabilityPlanReader.DefaultFailureRetryAfter);
    }

    [Fact]
    public async Task A_forced_read_ends_the_wait_after_a_failure_and_a_success_replaces_it()
    {
        var command = new FakeCommand().Then("garbage").Then(FreshPlan);
        var reader = Reader(command);

        Assert.Equal(ObservabilityPlanStatus.Malformed, (await reader.GetAsync()).Status);
        Assert.Equal(ObservabilityPlanStatus.Malformed, (await reader.GetAsync()).Status);
        Assert.Equal(1, command.Calls);

        var healed = await reader.GetAsync(force: true);

        Assert.True(healed.IsOk);
        Assert.Same(healed, reader.Current);
    }

    [Fact]
    public async Task A_new_configuration_ends_the_wait_after_a_failure_too()
    {
        var command = new FakeCommand().Then("garbage").Then(FreshPlan);
        object configuration = new();
        var reader = Reader(command, () => configuration);

        Assert.False((await reader.GetAsync()).IsOk);
        configuration = new object();

        Assert.True((await reader.GetAsync()).IsOk);
        Assert.Equal(2, command.Calls);
    }

    [Fact]
    public async Task Current_is_nothing_before_the_first_read_and_the_last_answer_after()
    {
        var reader = Reader(new FakeCommand().Then(FreshPlan));

        Assert.Null(reader.Current);
        var read = await reader.GetAsync();

        Assert.Same(read, reader.Current);
    }

    // ---- concurrency ----

    [Fact]
    public async Task Callers_that_arrive_while_it_runs_share_the_run()
    {
        var gate = new TaskCompletionSource();
        var command = new FakeCommand().Then(async _ =>
        {
            await gate.Task;
            return new PlanCommandOutput(0, null, FreshPlan);
        });
        var reader = Reader(command);

        var callers = Enumerable.Range(0, 6).Select(_ => reader.GetAsync()).ToArray();
        await WaitUntil(() => command.Calls == 1);
        gate.SetResult();
        var reads = await Task.WhenAll(callers).WaitAsync(TestTimeouts.Ceiling);

        Assert.Equal(1, command.Calls);
        Assert.All(reads, r => Assert.Same(reads[0], r));
    }

    [Fact]
    public async Task A_callers_token_stops_that_caller_waiting_and_not_the_run()
    {
        var gate = new TaskCompletionSource();
        var command = new FakeCommand().Then(async _ =>
        {
            await gate.Task;
            return new PlanCommandOutput(0, null, FreshPlan);
        });
        var reader = Reader(command);
        using var impatient = new CancellationTokenSource();

        var cancelled = reader.GetAsync(cancellationToken: impatient.Token);
        var patient = reader.GetAsync();
        await WaitUntil(() => command.Calls == 1);
        impatient.Cancel();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);

        gate.SetResult();
        var read = await patient.WaitAsync(TestTimeouts.Ceiling);

        Assert.True(read.IsOk);
        Assert.Equal(1, command.Calls);
        Assert.Same(read, reader.Current);
    }

    [Fact]
    public async Task A_run_that_was_going_when_the_configuration_changed_is_discarded_and_run_again()
    {
        var first = new TaskCompletionSource();
        var command = new FakeCommand()
            .Then(async _ =>
            {
                await first.Task;
                return new PlanCommandOutput(0, null, FreshPlan);
            })
            .Then(FullPlan);
        object configuration = new();
        var reader = Reader(command, () => configuration);

        var waiting = reader.GetAsync();
        await WaitUntil(() => command.Calls == 1);
        configuration = new object();
        first.SetResult();
        var read = await waiting.WaitAsync(TestTimeouts.Ceiling);

        // What the first run printed described the old configuration: the caller who waited for it gets the second run's answer.
        Assert.Equal(2, command.Calls);
        Assert.Equal(5, read.Plan!.Destinations.Count);
        Assert.Same(read, await reader.GetAsync());
        Assert.Equal(2, command.Calls);
    }

    [Fact]
    public async Task A_configuration_that_keeps_changing_does_not_keep_the_command_running_for_ever()
    {
        var command = new FakeCommand().Then(FreshPlan);
        var reader = Reader(command, () => new object());

        var read = await reader.GetAsync().WaitAsync(TestTimeouts.Ceiling);

        // Three runs at most for one request (the first and two more); the answer is still given.
        Assert.True(read.IsOk);
        Assert.Equal(3, command.Calls);
    }

    // ---- the adapter over the real runner ----

    [Fact]
    public async Task Over_a_runner_that_finds_no_cli_nothing_is_started_and_nothing_is_recorded()
    {
        using var temp = new TempDirectory();
        using var cli = new CliRunner(new DefenseClawPaths(
            dataDirectory: temp.Path,
            binDirectory: Path.Combine(temp.Path, "no-such-bin"),
            searchPath: Array.Empty<string>()));
        var reader = ObservabilityPlanReader.ForCli(cli);

        var read = await reader.GetAsync();

        Assert.Equal(ObservabilityPlanStatus.NotInstalled, read.Status);
        Assert.Equal("'defenseclaw' was not found", read.Message);
        Assert.Empty(cli.Activity);
    }

    [Fact]
    public void A_finished_run_is_its_standard_output_its_exit_code_and_how_it_ended()
    {
        var invocation = Invocation();
        Append(invocation, "DefenseClaw 0.8.10", CliStream.StandardOutput);
        Append(invocation, "warning: example_code: a warning on standard error", CliStream.StandardError);
        Append(invocation, "{\"rows\":[]}", CliStream.StandardOutput);
        Finish(invocation, exitCode: 0);

        var output = ObservabilityPlanReader.OutputOf(invocation);

        Assert.Equal(0, output.ExitCode);
        Assert.Null(output.FailureReason);
        Assert.False(output.Truncated);
        Assert.Equal("DefenseClaw 0.8.10\n{\"rows\":[]}", output.Stdout);
    }

    [Fact]
    public void A_run_that_was_stopped_has_no_exit_code_and_carries_the_reason()
    {
        var invocation = Invocation();
        invocation.FailureReason = "timed out after 30 s";
        invocation.FinishedAt = DateTimeOffset.UtcNow;

        var output = ObservabilityPlanReader.OutputOf(invocation);

        Assert.Null(output.ExitCode);
        Assert.Equal("timed out after 30 s", output.FailureReason);
    }

    [Fact]
    public void A_transcript_the_runner_had_to_trim_is_reported_as_truncated()
    {
        var invocation = Invocation(retainFullOutput: false);
        for (var i = 0; i < CliInvocation.MaxRetainedOutputLines + 10; i++)
        {
            Append(invocation, "line " + i, CliStream.StandardOutput);
        }

        Assert.True(ObservabilityPlanReader.OutputOf(invocation).Truncated);
    }

    private static CliInvocation Invocation(bool retainFullOutput = true) =>
        new(@"C:\test\defenseclaw.exe", ObservabilityPlanReader.Argv.ToArray(), DateTimeOffset.UtcNow, retainFullOutput);

    private static void Append(CliInvocation invocation, string text, CliStream stream) =>
        invocation.Append(new CliOutputLine(DateTimeOffset.UtcNow, stream, text));

    private static void Finish(CliInvocation invocation, int exitCode)
    {
        invocation.ExitCode = exitCode;
        invocation.FinishedAt = DateTimeOffset.UtcNow;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow + TestTimeouts.Ceiling;
        while (!condition())
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "the condition did not come true");
            await Task.Delay(5);
        }
    }
}
