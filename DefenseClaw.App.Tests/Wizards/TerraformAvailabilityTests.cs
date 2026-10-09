using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.TestSupport;

namespace DefenseClaw.App.Tests.Wizards;

/// <summary>
/// An <see cref="ITerraformProbe"/> whose every look waits for the test to answer it, so a test decides when (and with what) Terraform
/// "responds" and can count the looks. Nothing here can start a process.
/// </summary>
internal sealed class ManualTerraformProbe : ITerraformProbe
{
    private readonly object _gate = new();
    private readonly List<TaskCompletionSource<TerraformStatus>> _looks = new();

    /// <summary>How many looks have been asked for.</summary>
    public int Calls
    {
        get
        {
            lock (_gate)
            {
                return _looks.Count;
            }
        }
    }

    public Task<TerraformStatus> ProbeAsync(CancellationToken cancellationToken)
    {
        var look = new TaskCompletionSource<TerraformStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = cancellationToken.Register(() => look.TrySetCanceled(cancellationToken));
        lock (_gate)
        {
            _looks.Add(look);
        }

        return look.Task;
    }

    /// <summary>Waits (on a condition, not a fixed delay) until the service has asked for <paramref name="count"/> looks.</summary>
    public async Task WaitForCallsAsync(int count)
    {
        var deadline = Environment.TickCount64 + 20_000;
        while (Calls < count)
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException($"Timed out waiting for look number {count} (have {Calls}).");
            }

            await Task.Delay(10);
        }
    }

    /// <summary>Answers the newest look.</summary>
    public void Answer(TerraformStatus status)
    {
        TaskCompletionSource<TerraformStatus> look;
        lock (_gate)
        {
            look = _looks[^1];
        }

        _ = look.TrySetResult(status);
    }

    /// <summary>Fails the newest look the way a broken probe would.</summary>
    public void Fail(Exception exception)
    {
        TaskCompletionSource<TerraformStatus> look;
        lock (_gate)
        {
            look = _looks[^1];
        }

        _ = look.TrySetException(exception);
    }
}

/// <summary>
/// The one Terraform look the Setup card and the palette's rows share (CUST-317): when it runs, how long its answer is trusted, what it
/// tells each surface, and that nothing about it is a poll. Fake probe and fake clock throughout; no process starts.
/// </summary>
public sealed class TerraformAvailabilityTests
{
    private static readonly TerraformStatus Ready =
        new(TerraformState.Ready, "Terraform 1.9.5 is available.", "1.9.5", @"C:\synthetic\bin\terraform.exe");

    private static readonly TerraformStatus NotInstalled =
        new(TerraformState.NotInstalled, "Terraform was not found on this machine's PATH.", string.Empty, "terraform");

    private static async Task<TerraformStatus?> LookAsync(TerraformAvailability gate, ManualTerraformProbe probe, TerraformStatus answer, int expectedCalls)
    {
        var look = gate.EnsureFreshAsync();
        await probe.WaitForCallsAsync(expectedCalls);
        probe.Answer(answer);
        await look;
        return gate.Status;
    }

    [Fact]
    public void Before_the_first_look_the_feature_is_closed_with_checking_and_nothing_has_run()
    {
        var probe = new ManualTerraformProbe();
        using var gate = new TerraformAvailability(probe);

        Assert.Null(gate.Status);
        Assert.False(gate.IsChecking);
        Assert.False(gate.Decision.IsAvailable);
        Assert.Equal(TerraformAvailability.CheckingReason, gate.Decision.Reason);
        Assert.Equal("Checking for Terraform…", gate.Decision.Reason);
        Assert.Equal(0, probe.Calls);
    }

    [Fact]
    public async Task A_terraform_that_is_new_enough_opens_it_and_the_change_is_announced_once()
    {
        var probe = new ManualTerraformProbe();
        using var gate = new TerraformAvailability(probe);
        var announced = 0;
        gate.Changed += (_, _) => Interlocked.Increment(ref announced);

        var look = gate.EnsureFreshAsync();
        await probe.WaitForCallsAsync(1);

        // While the first look runs there is still no answer: closed, and saying so.
        Assert.True(gate.IsChecking);
        Assert.False(gate.Decision.IsAvailable);
        Assert.Equal(TerraformAvailability.CheckingReason, gate.Decision.Reason);

        probe.Answer(Ready);
        await look;

        Assert.False(gate.IsChecking);
        Assert.Same(Ready, gate.Status);
        Assert.True(gate.Decision.IsAvailable);
        Assert.Null(gate.Decision.Reason);
        Assert.Equal(1, announced);
    }

    [Theory]
    [InlineData(TerraformState.NotInstalled, "Terraform was not found on this machine's PATH.")]
    [InlineData(TerraformState.TooOld, "Terraform 1.2.0 at C:\\synthetic\\bin\\terraform.exe is older than the 1.5.0 that the dashboards module requires.")]
    [InlineData(TerraformState.NotWorking, "Terraform was found at C:\\synthetic\\bin\\terraform.exe, but \"terraform version -json\" failed (exit code 1). The dashboards need Terraform 1.5.0 or later.")]
    public async Task Every_definite_no_closes_it_and_gives_the_probes_own_reason_and_how_to_put_it_right(TerraformState state, string summary)
    {
        var probe = new ManualTerraformProbe();
        using var gate = new TerraformAvailability(probe);

        _ = await LookAsync(gate, probe, new TerraformStatus(state, summary, string.Empty, string.Empty), expectedCalls: 1);

        Assert.False(gate.Decision.IsAvailable);
        Assert.StartsWith(summary, gate.Decision.Reason, StringComparison.Ordinal);

        // What to do about it, and how to look again, for both surfaces: the Setup page has a Refresh, the palette is opened again.
        Assert.Contains("Terraform 1.5.0 or later", gate.Decision.Reason, StringComparison.Ordinal);
        Assert.Contains("TERRAFORM_BIN", gate.Decision.Reason, StringComparison.Ordinal);
        Assert.Contains("check again", gate.Decision.Reason, StringComparison.Ordinal);
        Assert.Contains("Refresh", gate.Decision.Reason, StringComparison.Ordinal);
        Assert.Contains("command palette", gate.Decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_look_that_could_not_run_does_not_close_it_because_the_cli_runs_terraform_itself()
    {
        var probe = new ManualTerraformProbe();
        using var gate = new TerraformAvailability(probe);

        _ = await LookAsync(gate, probe, new TerraformStatus(TerraformState.Unknown, "Terraform could not be checked from here (x).", string.Empty, string.Empty), expectedCalls: 1);

        Assert.True(gate.Decision.IsAvailable);
    }

    [Fact]
    public async Task Callers_that_arrive_while_a_look_is_running_join_it_instead_of_starting_another()
    {
        var probe = new ManualTerraformProbe();
        using var gate = new TerraformAvailability(probe);

        var first = gate.EnsureFreshAsync();
        var second = gate.RefreshAsync();
        var third = gate.EnsureFreshAsync();
        await probe.WaitForCallsAsync(1);
        probe.Answer(Ready);
        await Task.WhenAll(first, second, third);

        Assert.Equal(1, probe.Calls);
    }

    [Fact]
    public async Task A_yes_is_trusted_for_a_minute_and_then_looked_at_again()
    {
        var clock = new ManualClock();
        var probe = new ManualTerraformProbe();
        using var gate = new TerraformAvailability(probe, clock);
        _ = await LookAsync(gate, probe, Ready, expectedCalls: 1);

        clock.Advance(TerraformAvailability.FreshWhenAvailable - TimeSpan.FromSeconds(1));
        await gate.EnsureFreshAsync();
        Assert.Equal(1, probe.Calls);

        clock.Advance(TimeSpan.FromSeconds(2));
        _ = await LookAsync(gate, probe, Ready, expectedCalls: 2);
        Assert.Equal(2, probe.Calls);
        Assert.Equal(TimeSpan.FromSeconds(60), TerraformAvailability.FreshWhenAvailable);
    }

    [Fact]
    public async Task A_no_is_trusted_only_for_a_few_seconds_because_the_operator_is_about_to_fix_it()
    {
        var clock = new ManualClock();
        var probe = new ManualTerraformProbe();
        using var gate = new TerraformAvailability(probe, clock);
        _ = await LookAsync(gate, probe, NotInstalled, expectedCalls: 1);
        Assert.True(TerraformAvailability.FreshWhenNot < TerraformAvailability.FreshWhenAvailable);
        Assert.Equal(TimeSpan.FromSeconds(15), TerraformAvailability.FreshWhenNot);

        clock.Advance(TerraformAvailability.FreshWhenNot - TimeSpan.FromSeconds(1));
        await gate.EnsureFreshAsync();
        Assert.Equal(1, probe.Calls);

        clock.Advance(TimeSpan.FromSeconds(2));
        _ = await LookAsync(gate, probe, Ready, expectedCalls: 2);

        Assert.True(gate.Decision.IsAvailable);
    }

    [Fact]
    public async Task A_stale_answer_is_still_the_answer_until_a_surface_asks_for_a_newer_one_and_nothing_polls()
    {
        var clock = new ManualClock();
        var probe = new ManualTerraformProbe();
        using var gate = new TerraformAvailability(probe, clock);
        _ = await LookAsync(gate, probe, Ready, expectedCalls: 1);

        // Time passing alone costs nothing and changes nothing: freshness is only ever read by a caller of EnsureFreshAsync.
        clock.Advance(TimeSpan.FromHours(6));

        Assert.Equal(1, probe.Calls);
        Assert.True(gate.Decision.IsAvailable);
        Assert.Same(Ready, gate.Status);
    }

    [Fact]
    public async Task Refresh_looks_again_even_when_the_answer_is_fresh_and_stale_data_keeps_the_card_from_flickering()
    {
        var probe = new ManualTerraformProbe();
        using var gate = new TerraformAvailability(probe);
        _ = await LookAsync(gate, probe, Ready, expectedCalls: 1);

        var again = gate.RefreshAsync();
        await probe.WaitForCallsAsync(2);

        // The second look is running; the answer already held is still what a surface sees, not "Checking for Terraform…".
        Assert.True(gate.IsChecking);
        Assert.True(gate.Decision.IsAvailable);

        probe.Answer(NotInstalled);
        await again;

        Assert.False(gate.Decision.IsAvailable);
        Assert.Contains("not found", gate.Decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_look_that_repeats_the_answer_announces_nothing_and_one_that_changes_it_does()
    {
        var probe = new ManualTerraformProbe();
        using var gate = new TerraformAvailability(probe);
        var announced = 0;
        gate.Changed += (_, _) => Interlocked.Increment(ref announced);

        _ = await LookAsync(gate, probe, Ready, expectedCalls: 1);
        var again = gate.RefreshAsync();
        await probe.WaitForCallsAsync(2);
        probe.Answer(new TerraformStatus(Ready.State, Ready.Summary, Ready.Version, Ready.Path));
        await again;
        Assert.Equal(1, announced);

        var third = gate.RefreshAsync();
        await probe.WaitForCallsAsync(3);
        probe.Answer(NotInstalled);
        await third;
        Assert.Equal(2, announced);

        // Same state and sentence but a different Terraform answering is a change a surface would show.
        var fourth = gate.RefreshAsync();
        await probe.WaitForCallsAsync(4);
        probe.Answer(new TerraformStatus(NotInstalled.State, NotInstalled.Summary, string.Empty, @"D:\elsewhere\terraform.exe"));
        await fourth;
        Assert.Equal(3, announced);
    }

    [Fact]
    public async Task A_probe_that_throws_is_could_not_check_and_never_faults_the_caller()
    {
        var probe = new ManualTerraformProbe();
        using var gate = new TerraformAvailability(probe);

        var look = gate.EnsureFreshAsync();
        await probe.WaitForCallsAsync(1);
        probe.Fail(new InvalidOperationException("boom"));
        await look;

        Assert.Equal(TerraformState.Unknown, gate.Status!.State);
        Assert.Contains("boom", gate.Status.Summary, StringComparison.Ordinal);
        Assert.True(gate.Decision.IsAvailable);
    }

    [Fact]
    public async Task A_cancelled_caller_stops_waiting_but_the_look_goes_on_for_everyone_else()
    {
        var probe = new ManualTerraformProbe();
        using var gate = new TerraformAvailability(probe);
        using var cancel = new CancellationTokenSource();

        var impatient = gate.RefreshAsync(cancel.Token);
        await probe.WaitForCallsAsync(1);
        await cancel.CancelAsync();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => impatient);

        probe.Answer(Ready);
        await gate.EnsureFreshAsync();

        Assert.Same(Ready, gate.Status);
        Assert.Equal(1, probe.Calls);
    }

    [Fact]
    public async Task A_subscriber_that_throws_neither_stops_the_others_nor_faults_the_look()
    {
        var probe = new ManualTerraformProbe();
        using var gate = new TerraformAvailability(probe);
        var heard = 0;
        gate.Changed += (_, _) => throw new InvalidOperationException("a broken subscriber");
        gate.Changed += (_, _) => Interlocked.Increment(ref heard);

        _ = await LookAsync(gate, probe, Ready, expectedCalls: 1);

        Assert.Equal(1, heard);
        Assert.True(gate.Decision.IsAvailable);
    }

    [Fact]
    public async Task Disposing_ends_a_look_in_flight_without_an_answer_and_later_calls_start_nothing()
    {
        var probe = new ManualTerraformProbe();
        var gate = new TerraformAvailability(probe);

        var look = gate.EnsureFreshAsync();
        await probe.WaitForCallsAsync(1);
        gate.Dispose();
        await look;

        Assert.Null(gate.Status);

        await gate.EnsureFreshAsync();
        await gate.RefreshAsync();
        Assert.Equal(1, probe.Calls);
    }

    [Fact]
    public async Task The_fixed_probe_answers_what_it_was_given_every_time()
    {
        var fixedAnswer = new FixedTerraformProbe(NotInstalled);

        Assert.Same(NotInstalled, await fixedAnswer.ProbeAsync(CancellationToken.None));
        Assert.Same(NotInstalled, await fixedAnswer.ProbeAsync(CancellationToken.None));
    }
}
