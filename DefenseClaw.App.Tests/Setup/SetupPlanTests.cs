using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.Tests.Setup;

/// <summary>
/// A plan of N invocations reviewed together and run in order (CUST-271's follow-ups): <see cref="SetupPlan"/> opened in the shared
/// <see cref="DiscoverActionReview"/>. A step runs only if the one before it exited 0; a failure stops the plan, nothing after it starts, and the
/// report (<see cref="PlanReport"/>) says which steps ran and which did not. No process starts: the steps are a script, and each is the exact argv
/// the runner would be handed.
/// </summary>
public sealed class SetupPlanTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly AppServices _services;

    public SetupPlanTests()
    {
        _services = TestServices.Create(_temp);
    }

    public void Dispose()
    {
        _services.Dispose();
        _temp.Dispose();
    }

    private static readonly string[][] Three =
    {
        new[] { "setup", "notifications-set", "block_would_block", "on", "--no-restart" },
        new[] { "setup", "notifications-set", "hitl_approval", "off", "--no-restart" },
        new[] { "setup", "notifications-set", "sources.hook", "off" },
    };

    private static SetupPlan Plan(params string[][] argvs) => new()
    {
        Title = "Update notification routing?",
        Summary = "Changes which events raise a desktop notification.",
        Steps = argvs.Select((argv, i) => new DiscoverStep(argv, $"Change {i + 1}.")).ToArray(),
        RestartsGateway = true,
        PrimaryText = "Apply changes",
    };

    private static CliInvocation Done(IReadOnlyList<string> argv, int exit, string? stderr = null, string? failure = null)
    {
        var invocation = InvocationFactory.Create(retainFullOutput: true, argv.ToArray());
        if (stderr is not null)
        {
            InvocationFactory.Append(invocation, stderr, CliStream.StandardError);
        }

        if (failure is not null)
        {
            InvocationFactory.Fail(invocation, failure);
        }
        else
        {
            InvocationFactory.Finish(invocation, exit);
        }

        return invocation;
    }

    /// <summary>A review whose steps answer from <paramref name="answer"/> (by position and argv; exit 0 when null), recording what it was asked to run.</summary>
    private (DiscoverActionReview Review, List<string> Ran, List<DiscoverReviewResult> Finished) Open(
        SetupPlan plan,
        Func<int, IReadOnlyList<string>, CliInvocation>? answer = null)
    {
        var ran = new List<string>();
        var finished = new List<DiscoverReviewResult>();
        var review = new DiscoverActionReview(_services)
        {
            RunStep = (executable, argv, _) =>
            {
                ran.Add(executable + " " + string.Join(' ', argv));
                var made = answer is null ? Done(argv, 0) : answer(ran.Count - 1, argv);
                return Task.FromResult(made);
            },
        };
        review.OpenPlan(plan, result =>
        {
            finished.Add(result);
            return Task.CompletedTask;
        });
        return (review, ran, finished);
    }

    // ---- the review is the whole plan, together ----

    [Fact]
    public void The_plan_is_one_review_with_every_command_numbered_and_nothing_started()
    {
        var (review, ran, _) = Open(Plan(Three));

        Assert.True(review.IsConfirming);
        Assert.Empty(ran);
        var shown = review.CommandReview!;
        Assert.Equal("Update notification routing?", shown.Title);
        Assert.Equal("Changes which events raise a desktop notification.", shown.Summary);
        Assert.Equal(3, shown.Steps.Count);
        Assert.Equal(new[] { 1, 2, 3 }, shown.Steps.Select(s => s.Number));
        Assert.Equal(
            new[]
            {
                "defenseclaw setup notifications-set block_would_block on --no-restart",
                "defenseclaw setup notifications-set hitl_approval off --no-restart",
                "defenseclaw setup notifications-set sources.hook off",
            },
            shown.Steps.Select(s => s.CommandText));
        Assert.True(shown.RestartsGateway);
        Assert.Equal("Apply changes", shown.ConfirmLabel);
        Assert.Equal(CommandTier.StateChanging, shown.Tier);
    }

    [Fact]
    public void A_plan_with_nothing_in_it_is_not_opened()
    {
        var review = new DiscoverActionReview(_services);

        review.OpenPlan(new SetupPlan { Title = "Nothing?", Steps = Array.Empty<DiscoverStep>() });

        Assert.True(new SetupPlan { Title = "Nothing?", Steps = Array.Empty<DiscoverStep>() }.IsEmpty);
        Assert.False(review.IsOpen);
        Assert.Null(review.CommandReview);
    }

    [Fact]
    public void The_plans_warnings_ride_on_the_review_after_the_restart_bar()
    {
        var plan = Plan(Three[0]) with
        {
            Warnings = new[] { new CommandReviewWarning("Master switch off", "Nothing is raised until it is on.") },
        };

        var (review, _, _) = Open(plan);

        Assert.Equal(new[] { "Gateway restart", "Master switch off" }, review.CommandReview!.Warnings.Select(w => w.Title));
    }

    // ---- run in order, all of it ----

    [Fact]
    public async Task Every_step_runs_in_the_order_given_and_the_report_says_all_ran()
    {
        var (review, ran, finished) = Open(Plan(Three));

        await review.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(
            new[]
            {
                "defenseclaw setup notifications-set block_would_block on --no-restart",
                "defenseclaw setup notifications-set hitl_approval off --no-restart",
                "defenseclaw setup notifications-set sources.hook off",
            },
            ran);
        Assert.True(review.IsFinished);
        Assert.Equal("Ok", review.ResultKey);
        Assert.StartsWith("Done. All 3 steps ran.", review.ResultText, StringComparison.Ordinal);

        var result = Assert.Single(finished);
        Assert.True(result.Succeeded);
        Assert.Equal(3, result.Invocations.Count);
        Assert.All(result.Outcomes, o => Assert.Equal(DiscoverStepState.Succeeded, o.State));
        Assert.Equal(new[] { 1, 2, 3 }, result.Outcomes.Select(o => o.Number));
        Assert.All(review.CommandReview!.Steps, s => Assert.Equal(("Succeeded (exit 0)", "Ok"), (s.StatusText, s.StatusKey)));
    }

    // ---- stop on the first failure ----

    [Fact]
    public async Task A_failure_in_the_middle_stops_the_plan_so_the_third_never_starts_and_the_report_says_so()
    {
        var (review, ran, finished) = Open(Plan(Three), (index, argv) => Done(argv, index == 1 ? 1 : 0, stderr: "Error: config save failed"));

        await review.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(2, ran.Count); // the third was never handed to the runner
        Assert.DoesNotContain(ran, line => line.Contains("sources.hook", StringComparison.Ordinal));
        Assert.Equal("Bad", review.ResultKey);
        Assert.Equal(
            "Did not finish. 1 of 3 steps succeeded. Step 2 failed (exit 1). Step 3 was not run. The steps that succeeded are not undone. " +
            "The exact commands and their full output are in the Activity panel.",
            review.ResultText);

        var result = Assert.Single(finished);
        Assert.False(result.Succeeded);
        Assert.Equal(2, result.Invocations.Count);
        Assert.Equal(
            new[] { DiscoverStepState.Succeeded, DiscoverStepState.Failed, DiscoverStepState.NotRun },
            result.Outcomes.Select(o => o.State));
        Assert.Equal(new[] { string.Empty, "exit 1", "an earlier step did not succeed" }, result.Outcomes.Select(o => o.Detail));

        var rows = review.CommandReview!.Steps;
        Assert.Equal(("Succeeded (exit 0)", "Ok"), (rows[0].StatusText, rows[0].StatusKey));
        Assert.Equal(("Failed (exit 1)", "Bad"), (rows[1].StatusText, rows[1].StatusKey));
        Assert.Equal(("Skipped: an earlier step did not succeed", "Neutral"), (rows[2].StatusText, rows[2].StatusKey));
        Assert.Contains("Error: config save failed", review.ResultOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_first_step_that_fails_leaves_the_rest_not_run_and_nothing_undone_is_not_claimed()
    {
        var (review, ran, finished) = Open(Plan(Three), (index, argv) => Done(argv, 2));

        await review.ConfirmCommand.ExecuteAsync(null);

        Assert.Single(ran);
        Assert.Equal(
            "Did not finish. 0 of 3 steps succeeded. Step 1 failed (exit 2). Steps 2 and 3 were not run. " +
            "The exact commands and their full output are in the Activity panel.",
            review.ResultText);
        Assert.DoesNotContain("not undone", review.ResultText, StringComparison.Ordinal);
        Assert.Equal(
            new[] { DiscoverStepState.Failed, DiscoverStepState.NotRun, DiscoverStepState.NotRun },
            Assert.Single(finished).Outcomes.Select(o => o.State));
    }

    [Fact]
    public async Task The_last_step_failing_is_reported_with_everything_before_it_done()
    {
        var (review, ran, _) = Open(Plan(Three), (index, argv) => Done(argv, index == 2 ? 5 : 0));

        await review.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(3, ran.Count);
        Assert.StartsWith("Did not finish. 2 of 3 steps succeeded. Step 3 failed (exit 5). The steps that succeeded are not undone.", review.ResultText, StringComparison.Ordinal);
        Assert.DoesNotContain("was not run", review.ResultText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_step_the_runner_stopped_is_a_failure_with_the_runners_reason_and_ends_the_plan()
    {
        var (review, ran, finished) = Open(Plan(Three), (index, argv) => index == 0 ? Done(argv, 0, failure: "timed out after 120 s") : Done(argv, 0));

        await review.ConfirmCommand.ExecuteAsync(null);

        Assert.Single(ran);
        Assert.Contains("Step 1 failed (timed out after 120 s).", review.ResultText, StringComparison.Ordinal);
        Assert.Equal(("Did not complete: timed out after 120 s", "Bad"), (review.CommandReview!.Steps[0].StatusText, review.CommandReview.Steps[0].StatusKey));
        Assert.Equal("timed out after 120 s", Assert.Single(finished).Outcomes[0].Detail);
    }

    [Fact]
    public async Task A_step_with_a_check_of_its_own_that_rejects_what_it_printed_fails_the_plan_although_it_exited_zero()
    {
        var plan = new SetupPlan
        {
            Title = "Two steps",
            Steps = new[]
            {
                new DiscoverStep(new[] { "init", "--json-summary" }, "Initialise.", Verify: _ => "The report says setup did not finish."),
                new DiscoverStep(new[] { "setup", "codex", "--yes" }, "Add Codex."),
            },
        };
        var (review, ran, finished) = Open(plan);

        await review.ConfirmCommand.ExecuteAsync(null);

        Assert.Single(ran);
        Assert.Contains("Step 1 failed (it reported a problem although it exited 0).", review.ResultText, StringComparison.Ordinal);
        Assert.Contains("The report says setup did not finish.", review.ResultOutput, StringComparison.Ordinal);
        Assert.Equal(DiscoverStepState.NotRun, Assert.Single(finished).Outcomes[1].State);
    }

    [Fact]
    public async Task A_step_the_app_cannot_find_a_program_for_goes_through_the_apps_own_runner_and_ends_the_plan()
    {
        // No seam: the steps go to Services.Cli.RunNamedAsync, which in this composition cannot find the program (its PATH is empty).
        var review = new DiscoverActionReview(_services);
        var finished = new List<DiscoverReviewResult>();
        review.OpenPlan(Plan(Three[0], Three[1]), result =>
        {
            finished.Add(result);
            return Task.CompletedTask;
        });

        await review.ConfirmCommand.ExecuteAsync(null);

        var result = Assert.Single(finished);
        Assert.Equal(new[] { DiscoverStepState.Failed, DiscoverStepState.NotRun }, result.Outcomes.Select(o => o.State));
        Assert.Equal("it could not start", result.Outcomes[0].Detail);
        Assert.Empty(result.Invocations);
        Assert.Contains("Step 1 failed (it could not start). Step 2 was not run.", review.ResultText, StringComparison.Ordinal);
        Assert.Empty(_services.Cli.Activity); // nothing started, so nothing is claimed in Activity
    }

    // ---- one step keeps its own words ----

    [Fact]
    public async Task A_review_of_one_step_says_what_it_always_said()
    {
        var (ok, _, _) = Open(Plan(Three[2]));
        await ok.ConfirmCommand.ExecuteAsync(null);

        var (bad, _, _) = Open(Plan(Three[2]), (_, argv) => Done(argv, 1));
        await bad.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal("Done. The exact command and its full output are in the Activity panel.", ok.ResultText);
        Assert.Equal(
            "Did not finish. Steps after the failing one were not run. The exact command and its full output are in the Activity panel.",
            bad.ResultText);
    }

    // ---- the guards come first ----

    [Fact]
    public async Task A_plan_confirmed_after_the_installation_turned_read_only_runs_none_of_it_and_records_one_refusal()
    {
        var (review, ran, finished) = Open(Plan(Three));
        Assert.False(review.CommandReview!.IsBlocked);

        // The config the plan was reviewed on is edited to managed while it is on screen: the live answer is asked again at Confirm.
        _services.Installation.Replace(TestInstallations.ManagedAt(_temp.Path));
        await review.ConfirmCommand.ExecuteAsync(null);

        Assert.Empty(ran);
        Assert.Empty(finished);
        Assert.StartsWith("Not run. " + TestInstallations.ManagedReason, review.ResultText, StringComparison.Ordinal);
        var entry = Assert.Single(_services.Cli.Activity);
        Assert.Equal(Three[0], entry.Argv);
        Assert.StartsWith(CliRunner.RefusedPrefix, entry.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_plan_opened_on_a_read_only_installation_carries_the_reason_and_cannot_be_confirmed()
    {
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp, installation: TestInstallations.Managed(temp.Path));
        var review = new DiscoverActionReview(services);

        review.OpenPlan(Plan(Three));

        Assert.True(review.CommandReview!.IsBlocked);
        Assert.False(review.ConfirmCommand.CanExecute(null));
        Assert.Contains(review.CommandReview.Warnings, w => w.Title == CommandReviewWarning.ReadOnlyInstallationTitle);
    }

    [Fact]
    public async Task A_run_guard_that_answers_stops_the_whole_plan_before_the_first_step()
    {
        var ran = new List<string>();
        var review = new DiscoverActionReview(_services)
        {
            RunGuard = () => "config.yaml changed after this was read.",
            RunStep = (e, a, _) => { ran.Add(string.Join(' ', a)); return Task.FromResult(Done(a, 0)); },
        };
        review.OpenPlan(Plan(Three));

        await review.ConfirmCommand.ExecuteAsync(null);

        Assert.Empty(ran);
        Assert.StartsWith("Not run. config.yaml changed after this was read.", review.ResultText, StringComparison.Ordinal);
        var rows = review.CommandReview!.Steps;
        Assert.Equal("Refused: not started", rows[0].StatusText);
        Assert.All(rows.Skip(1), r => Assert.Equal("Skipped: the first step was refused", r.StatusText));
        Assert.Equal(Three[0], Assert.Single(_services.Cli.Activity).Argv);
    }

    // ---- the report on its own ----

    private static DiscoverStepOutcome Step(int n, DiscoverStepState state, string detail = "") => new(n, state, "defenseclaw x" + n, detail);

    [Fact]
    public void The_report_names_the_steps_that_ran_failed_and_did_not_run()
    {
        Assert.Equal(string.Empty, PlanReport.Describe(new[] { Step(1, DiscoverStepState.Succeeded) }));
        Assert.Equal(string.Empty, PlanReport.Describe(Array.Empty<DiscoverStepOutcome>()));
        Assert.Equal("All 2 steps ran.", PlanReport.Describe(new[] { Step(1, DiscoverStepState.Succeeded), Step(2, DiscoverStepState.Succeeded) }));
        Assert.Equal(
            "No step ran.",
            PlanReport.Describe(new[] { Step(1, DiscoverStepState.NotRun), Step(2, DiscoverStepState.NotRun) }));
        Assert.Equal(
            "1 of 4 steps succeeded. Step 2 failed (exit 1). Steps 3 and 4 were not run. The steps that succeeded are not undone.",
            PlanReport.Describe(new[]
            {
                Step(1, DiscoverStepState.Succeeded), Step(2, DiscoverStepState.Failed, "exit 1"), Step(3, DiscoverStepState.NotRun), Step(4, DiscoverStepState.NotRun),
            }));
        Assert.Equal(
            "0 of 5 steps succeeded. Step 1 failed. Steps 2, 3, 4 and 5 were not run.",
            PlanReport.Describe(new[]
            {
                Step(1, DiscoverStepState.Failed), Step(2, DiscoverStepState.NotRun), Step(3, DiscoverStepState.NotRun), Step(4, DiscoverStepState.NotRun), Step(5, DiscoverStepState.NotRun),
            }));
    }
}
