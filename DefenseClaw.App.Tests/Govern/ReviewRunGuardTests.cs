using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Config;

namespace DefenseClaw.App.Tests.Govern;

/// <summary>
/// The last question before a confirmed review runs (<see cref="DiscoverActionReview.RunGuard"/>, CUST-312), and how a refusal reads in
/// Activity. A review can stay open as long as the operator reads it, and the list it was opened on can go stale meanwhile; the answer at
/// the moment of Confirm is the one that counts. No process starts: the steps are a script.
/// </summary>
public sealed class ReviewRunGuardTests : IDisposable
{
    private const string Reason = "Changes are off: config.yaml or .env changed after this list was read. Refresh to act on current data.";

    private readonly TempDirectory _temp = new();
    private readonly AppServices _services;

    public ReviewRunGuardTests()
    {
        _services = TestServices.Create(_temp);
    }

    public void Dispose()
    {
        _services.Dispose();
        _temp.Dispose();
    }

    private static CliInvocation Done(IReadOnlyList<string> argv, int exit)
    {
        var invocation = InvocationFactory.Create(retainFullOutput: true, argv.ToArray());
        InvocationFactory.Finish(invocation, exit);
        return invocation;
    }

    private (DiscoverActionReview Review, List<string> Ran, List<DiscoverReviewResult> Finished) Open(Func<string?>? guard, int steps = 2)
    {
        var ran = new List<string>();
        var finished = new List<DiscoverReviewResult>();
        var review = new DiscoverActionReview(_services)
        {
            RunGuard = guard,
            RunStep = (executable, argv, _) =>
            {
                ran.Add(executable + " " + string.Join(' ', argv));
                return Task.FromResult(Done(argv, 0));
            },
        };
        var plan = new List<DiscoverStep>
        {
            new(new[] { "registry", "add", "corp-skills", "--non-interactive" }, "Add the source."),
            new(new[] { "registry", "sync", "corp-skills", "--json" }, "Sync it."),
        };
        review.Open(
            "Add registry source?",
            "Registers a source.",
            plan.Take(steps).ToArray(),
            result =>
            {
                finished.Add(result);
                return Task.CompletedTask;
            });
        return (review, ran, finished);
    }

    [Fact]
    public async Task A_guard_that_answers_stops_the_review_before_anything_starts()
    {
        var asked = 0;
        var (review, ran, finished) = Open(() =>
        {
            asked++;
            return Reason;
        });
        Assert.True(review.IsConfirming);

        await review.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(1, asked);
        Assert.Empty(ran);
        Assert.Empty(finished); // nothing ran, so there is nothing to follow up
        Assert.True(review.IsOpen);
        Assert.True(review.IsFinished);
        Assert.False(review.IsRunning);
        Assert.False(review.IsConfirming);
        Assert.Equal(CommandReviewPhase.Finished, review.Phase);
        Assert.Equal("Bad", review.ResultKey);
        Assert.StartsWith("Not run. " + Reason, review.ResultText, StringComparison.Ordinal);
        Assert.Contains("recorded in the Activity panel", review.ResultText, StringComparison.Ordinal);
        Assert.Null(review.ResultOutput);

        var steps = review.CommandReview!.Steps;
        Assert.Equal(("Refused: not started", "Bad"), (steps[0].StatusText, steps[0].StatusKey));
        Assert.Equal(("Skipped: the first step was refused", "Neutral"), (steps[1].StatusText, steps[1].StatusKey));
    }

    [Fact]
    public async Task The_refusal_is_recorded_in_activity_as_the_command_that_would_have_run_first()
    {
        var (review, _, _) = Open(() => Reason);

        await review.ConfirmCommand.ExecuteAsync(null);

        var entry = Assert.Single(_services.Cli.Activity);
        Assert.Equal("defenseclaw", entry.Executable);
        Assert.Equal(new[] { "registry", "add", "corp-skills", "--non-interactive" }, entry.Argv);
        Assert.False(entry.IsRunning);
        Assert.Null(entry.ExitCode);
        Assert.Equal(CliRunner.RefusedPrefix + " — " + Reason, entry.FailureReason);
        Assert.Contains(Reason, Assert.Single(entry.OutputLines).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refused_review_closes_without_calling_back_and_the_next_one_starts_clean()
    {
        var refuse = true;
        var (review, ran, finished) = Open(() => refuse ? Reason : null);
        await review.ConfirmCommand.ExecuteAsync(null);

        review.DismissCommand.Execute(null);
        Assert.False(review.IsOpen);
        Assert.Empty(finished);

        refuse = false;
        review.Open("Sync?", "Syncs.", new[] { new DiscoverStep(new[] { "registry", "sync", "--all", "--json" }, "Sync.") });
        Assert.True(review.IsConfirming);
        Assert.False(review.IsFinished);
        await review.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "defenseclaw registry sync --all --json" }, ran);
        Assert.Single(_services.Cli.Activity); // only the refusal: the second review ran through the script, not the runner
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task A_guard_with_no_answer_lets_the_review_run_every_step(string? answer)
    {
        var (review, ran, finished) = Open(() => answer);

        await review.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(
            new[] { "defenseclaw registry add corp-skills --non-interactive", "defenseclaw registry sync corp-skills --json" },
            ran);
        Assert.Single(finished);
        Assert.True(finished[0].Succeeded);
        Assert.Equal("Ok", review.ResultKey);
        Assert.Empty(_services.Cli.Activity);
    }

    [Fact]
    public async Task A_review_without_a_guard_runs_as_it_always_did()
    {
        var (review, ran, finished) = Open(guard: null, steps: 1);

        await review.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "defenseclaw registry add corp-skills --non-interactive" }, ran);
        Assert.Single(finished);
    }

    [Fact]
    public async Task A_command_with_a_secret_in_it_is_refused_all_the_same_and_is_not_written_down()
    {
        _services.Cli.RegisterSecret(new SecretValue("leaky-value-12345678"));
        var ran = new List<string>();
        var review = new DiscoverActionReview(_services)
        {
            RunGuard = () => Reason,
            RunStep = (executable, argv, _) =>
            {
                ran.Add(string.Join(' ', argv));
                return Task.FromResult(Done(argv, 0));
            },
        };
        review.Open("Add?", "Adds.", new[] { new DiscoverStep(new[] { "registry", "add", "x", "--auth-env", "leaky-value-12345678" }, "Add.") });

        await review.ConfirmCommand.ExecuteAsync(null);

        Assert.Empty(ran);
        Assert.True(review.IsFinished);
        Assert.StartsWith("Not run.", review.ResultText, StringComparison.Ordinal);
        Assert.Empty(_services.Cli.Activity);
    }

    // ------------------------------------------------------------------ Activity

    [Fact]
    public void Activity_badges_a_refusal_refused_not_failed()
    {
        var entry = _services.Cli.RecordRefusal("defenseclaw", new[] { "skill", "block", "--", "example" }, Reason);

        var row = new ActivityRow(entry);

        Assert.Equal("refused", row.ExitBadgeText);
        Assert.Equal("Warn", row.ExitBadgeKey);
        Assert.True(row.HasFailure);
        Assert.Equal(CliRunner.RefusedPrefix + " — " + Reason, row.FailureText);
        Assert.False(row.IsRunning);
        Assert.Contains("refused", row.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_cancelled_run_and_a_failed_one_keep_their_badges()
    {
        var failed = InvocationFactory.Create(false, "doctor");
        InvocationFactory.Fail(failed, "timed out after 120 s");
        var cancelled = InvocationFactory.Create(false, "doctor");
        InvocationFactory.Fail(cancelled, "cancelled by the operator");

        Assert.Equal("failed", new ActivityRow(failed).ExitBadgeText);
        Assert.Equal("cancelled", new ActivityRow(cancelled).ExitBadgeText);
    }
}
