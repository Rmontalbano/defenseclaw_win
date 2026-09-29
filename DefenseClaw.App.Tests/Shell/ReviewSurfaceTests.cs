using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Shell;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.Tests.Shell;

/// <summary>
/// What each surface hands the shared review control: the Govern confirm overlay, the Discover review
/// (including its run phases), the gateway dialog and the guardrail review. Nothing here can start a process:
/// the test services have no CLI, so a confirmed run ends in "not found" before anything could launch.
/// </summary>
public sealed class ReviewSurfaceTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly AppServices _services;

    public ReviewSurfaceTests()
    {
        _services = TestServices.Create(_temp);
    }

    public void Dispose()
    {
        _services.Dispose();
        _temp.Dispose();
    }

    // ------------------------------------------------------------------ Govern

    private static void Act(GovernPanelViewModelBase vm, GovernRow row, GovernVerbs verb) =>
        ((IGovernRowHost)vm).OnRowAction(row, verb);

    private static GovernRow Skill(SkillsPanelViewModel vm, string name) =>
        Assert.Single(vm.ParseRows($$"""[{"name": "{{name}}"}]"""));

    [Fact]
    public void A_govern_confirm_is_one_reviewed_command_with_the_default_labels()
    {
        var vm = new SkillsPanelViewModel(_services);

        Act(vm, Skill(vm, "pdf-tools"), GovernVerbs.Block);

        var review = Assert.IsType<CommandReview>(vm.ConfirmReview);
        var step = Assert.Single(review.Steps);
        Assert.Equal("defenseclaw skill block -- pdf-tools", step.CommandText);
        Assert.Equal(CommandTier.StateChanging, review.Tier);
        Assert.Equal("Run command", review.ConfirmLabel);
        Assert.Equal(vm.ConfirmHeading, review.Title);
        Assert.True(vm.IsConfirmOpen);
    }

    [Fact]
    public void A_destructive_govern_verb_is_marked_destructive_and_worded_for_it()
    {
        var vm = new SkillsPanelViewModel(_services);

        Act(vm, Skill(vm, "pdf-tools"), GovernVerbs.Quarantine);

        Assert.True(vm.ConfirmReview!.IsDestructive);
        Assert.Equal("Destructive", vm.ConfirmTierText);
        Assert.Equal("Run destructive command", vm.ConfirmReview.ConfirmLabel);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("--dry-run")]
    [InlineData("--version")]
    public void A_reason_typed_by_the_operator_cannot_talk_a_quarantine_down_to_a_preview(string reason)
    {
        var vm = new SkillsPanelViewModel(_services) { Reason = reason };

        Act(vm, Skill(vm, "pdf-tools"), GovernVerbs.Quarantine);

        Assert.Equal(CommandTier.Destructive, vm.ConfirmReview!.Tier);

        vm.ConfirmNoCommand.Execute(null);
        Act(vm, Skill(vm, "pdf-tools"), GovernVerbs.Block);

        Assert.Equal(CommandTier.StateChanging, vm.ConfirmReview!.Tier);
    }

    [Fact]
    public void Cancelling_a_govern_review_closes_it_and_runs_nothing()
    {
        var vm = new SkillsPanelViewModel(_services);
        Act(vm, Skill(vm, "pdf-tools"), GovernVerbs.Block);

        vm.ConfirmNoCommand.Execute(null);

        Assert.False(vm.IsConfirmOpen);
        Assert.Empty(_services.Cli.Activity);
    }

    // ------------------------------------------------------------------ Discover

    private static DiscoverStep Step(string argvText, CommandTier floor = CommandTier.StateChanging, string purpose = "does a thing") =>
        new(argvText.Split(' '), purpose, floor);

    [Fact]
    public void A_discover_review_takes_the_stricter_of_the_classifier_and_the_steps_floor()
    {
        var review = new DiscoverActionReview(_services);

        review.Open("Remove?", "Gone.", new[] { Step("registry remove corp --json") });
        Assert.Equal(CommandTier.Destructive, review.CommandReview!.Tier);

        review.Open("Edit?", "Changed.", new[] { Step("registry edit corp --json", CommandTier.Destructive) });
        Assert.Equal(CommandTier.Destructive, review.CommandReview!.Tier);

        review.Open("Sync?", "Fetched.", new[] { Step("registry sync list --json") });
        Assert.Equal(CommandTier.StateChanging, review.CommandReview!.Tier);
    }

    [Theory]
    [InlineData("registry list --json", CommandTier.ReadOnly, CommandTier.StateChanging)]
    [InlineData("registry list --json", CommandTier.StateChanging, CommandTier.StateChanging)]
    [InlineData("registry list --json", CommandTier.Destructive, CommandTier.Destructive)]
    [InlineData("registry remove corp", CommandTier.ReadOnly, CommandTier.Destructive)]
    [InlineData("registry approve corp x --type skill", CommandTier.StateChanging, CommandTier.StateChanging)]
    public void A_discover_step_is_never_shown_as_read_only(string argv, CommandTier floor, CommandTier expected) =>
        Assert.Equal(expected, DiscoverActionReview.EffectiveTier(Step(argv, floor)));

    [Fact]
    public void A_discover_review_of_two_steps_numbers_them_and_shows_the_strictest_tier()
    {
        var review = new DiscoverActionReview(_services);

        review.Open("Add and sync?", "Both.", new[] { Step("registry add corp"), Step("registry sync corp --json") });

        var shown = review.CommandReview!;
        Assert.True(shown.HasMultipleSteps);
        Assert.Equal(new[] { "Step 1", "Step 2" }, shown.Steps.Select(s => s.Heading).ToArray());
        Assert.Equal(new[] { "does a thing", "does a thing" }, shown.Steps.Select(s => s.Purpose).ToArray());
        Assert.Equal("Both.", shown.Summary);
    }

    [Fact]
    public void A_single_discover_step_has_no_step_number()
    {
        var review = new DiscoverActionReview(_services);

        review.Open("Sync?", "Fetched.", new[] { Step("registry sync corp --json") });

        Assert.Equal(string.Empty, Assert.Single(review.CommandReview!.Steps).Heading);
        Assert.False(review.CommandReview.HasMultipleSteps);
    }

    [Fact]
    public void A_restart_and_a_warning_become_warning_bars_in_that_order_and_the_restart_gets_a_badge()
    {
        var review = new DiscoverActionReview(_services);

        review.Open(
            "Turn on?",
            "Enables it.",
            new[] { Step("agent discovery enable") },
            restartsGateway: true,
            warning: "Read this first.");

        var shown = review.CommandReview!;
        Assert.True(shown.RestartsGateway);
        Assert.Equal(new[] { "Gateway restart", "Before you continue" }, shown.Warnings.Select(w => w.Title).ToArray());
        Assert.StartsWith(CommandReview.RestartSentence, shown.Warnings[0].Message, StringComparison.Ordinal);
        Assert.Equal("Read this first.", shown.Warnings[1].Message);
    }

    [Fact]
    public void Without_a_restart_or_a_warning_there_are_no_warning_bars()
    {
        var review = new DiscoverActionReview(_services);

        review.Open("Sync?", "Fetched.", new[] { Step("registry sync corp") }, warning: "  ");

        Assert.Empty(review.CommandReview!.Warnings);
        Assert.False(review.CommandReview.RestartsGateway);
    }

    [Fact]
    public void The_confirm_button_uses_the_panels_wording_or_falls_back_to_the_tier_default()
    {
        var review = new DiscoverActionReview(_services);

        review.Open("Sync?", "Fetched.", new[] { Step("registry sync corp") }, primaryText: "Sync");
        Assert.Equal("Sync", review.CommandReview!.ConfirmLabel);

        review.Open("Sync?", "Fetched.", new[] { Step("registry sync corp") });
        Assert.Equal("Run command", review.CommandReview!.ConfirmLabel);

        review.Open("Remove?", "Gone.", new[] { Step("registry remove corp") });
        Assert.Equal("Run destructive command", review.CommandReview!.ConfirmLabel);
    }

    [Fact]
    public void Opening_a_discover_review_starts_in_the_review_phase_with_nothing_run()
    {
        var review = new DiscoverActionReview(_services);
        Assert.False(review.IsOpen);

        review.Open("Sync?", "Fetched.", new[] { Step("registry sync corp") });

        Assert.True(review.IsOpen);
        Assert.True(review.IsConfirming);
        Assert.Equal(CommandReviewPhase.Review, review.Phase);
        Assert.False(review.HasResult);
        Assert.False(review.CommandReview!.Steps[0].HasStatus);
    }

    [Fact]
    public void An_empty_discover_review_is_ignored()
    {
        var review = new DiscoverActionReview(_services);

        review.Open("Nothing?", "Nothing.", Array.Empty<DiscoverStep>());

        Assert.False(review.IsOpen);
        Assert.Null(review.CommandReview);
    }

    [Fact]
    public async Task Confirming_runs_the_steps_in_order_stops_at_the_first_failure_and_finishes_in_the_finished_phase()
    {
        var review = new DiscoverActionReview(_services);
        DiscoverReviewResult? result = null;
        review.Open(
            "Add and sync?",
            "Both.",
            new[] { Step("registry add corp"), Step("registry sync corp --json") },
            r =>
            {
                result = r;
                return Task.CompletedTask;
            });
        var phases = new List<CommandReviewPhase>();
        review.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DiscoverActionReview.Phase))
            {
                phases.Add(review.Phase);
            }
        };

        await review.ConfirmCommand.ExecuteAsync(null);

        var steps = review.CommandReview!.Steps;
        Assert.Equal("Could not start", steps[0].StatusText);
        Assert.Equal("Bad", steps[0].StatusKey);
        Assert.StartsWith("Skipped", steps[1].StatusText, StringComparison.Ordinal);
        Assert.Equal(CommandReviewPhase.Finished, review.Phase);
        Assert.Contains(CommandReviewPhase.Running, phases);
        Assert.Equal(CommandReviewPhase.Finished, phases[^1]);
        Assert.True(review.HasResult);
        Assert.Equal("Bad", review.ResultKey);
        Assert.Contains("was not found", review.ResultOutput, StringComparison.Ordinal);
        Assert.NotNull(result);
        Assert.False(result!.Succeeded);
        Assert.Empty(result.Invocations);
    }

    [Fact]
    public void Cancelling_a_discover_review_calls_back_once_and_closing_a_finished_one_does_not()
    {
        var review = new DiscoverActionReview(_services);
        var cancelled = 0;
        review.Open("Sync?", "Fetched.", new[] { Step("registry sync corp") }, onCancelled: () => cancelled++);

        Assert.True(review.HandleEscape());
        Assert.False(review.IsOpen);
        Assert.Equal(1, cancelled);
        Assert.False(review.HandleEscape());
        Assert.Equal(1, cancelled);
    }

    [Fact]
    public async Task Dismissing_after_the_run_finishes_is_not_a_cancellation()
    {
        var review = new DiscoverActionReview(_services);
        var cancelled = 0;
        review.Open("Sync?", "Fetched.", new[] { Step("registry sync corp") }, onCancelled: () => cancelled++);
        await review.ConfirmCommand.ExecuteAsync(null);

        review.DismissCommand.Execute(null);

        Assert.False(review.IsOpen);
        Assert.Equal(0, cancelled);
    }

    // ------------------------------------------------------------------ gateway dialog

    [Theory]
    [InlineData(GatewayAction.Start, "Start gateway?", "defenseclaw-gateway start", "Start gateway")]
    [InlineData(GatewayAction.Stop, "Stop gateway?", "defenseclaw-gateway stop", "Stop gateway")]
    [InlineData(GatewayAction.Restart, "Restart gateway?", "defenseclaw-gateway restart", "Restart gateway")]
    public void The_gateway_dialog_reviews_the_exact_gateway_command_and_names_its_button_for_the_action(
        GatewayAction action, string title, string command, string confirm)
    {
        var review = GatewayActionDialog.ReviewFor(action);

        Assert.Equal(title, review.Title);
        Assert.Equal(command, review.CommandText);
        Assert.Equal(confirm, review.ConfirmLabel);
        Assert.Equal(GatewayControl.ReviewNote(action), review.Summary);
        Assert.Equal(CommandTier.StateChanging, review.Tier);
        Assert.False(review.IsDestructive);
        Assert.Equal(GatewayControl.CommandText(action), review.Steps[0].CommandText);
    }

    // ------------------------------------------------------------------ guardrail review

    private SetupPanelViewModel Setup() => new(_services);

    [Fact]
    public void Enabling_the_guardrail_reviews_a_state_change_that_restarts_the_gateway()
    {
        StaThread.Run(() =>
        {
            var vm = Setup();

            vm.EnableGuardrailCommand.Execute(null);

            Assert.True(vm.IsGuardrailReviewOpen);
            var review = vm.GuardrailReview!;
            Assert.Equal("Enable the guardrail?", review.Title);
            Assert.Equal("defenseclaw guardrail enable --yes", review.CommandText);
            Assert.Equal(CommandTier.StateChanging, review.Tier);
            Assert.True(review.RestartsGateway);
            Assert.Equal("Gateway restart", Assert.Single(review.Warnings).Title);
            Assert.Equal("Run command", review.ConfirmLabel);
        });
    }

    [Fact]
    public void Turning_the_restart_checkbox_off_rebuilds_the_review_with_no_restart_and_says_it_is_not_in_effect()
    {
        StaThread.Run(() =>
        {
            var vm = Setup();
            vm.EnableGuardrailCommand.Execute(null);

            vm.GuardrailRestartAfter = false;

            Assert.True(vm.IsGuardrailReviewOpen);
            var review = vm.GuardrailReview!;
            Assert.Equal("defenseclaw guardrail enable --yes --no-restart", review.CommandText);
            Assert.False(review.RestartsGateway);
            var warning = Assert.Single(review.Warnings);
            Assert.Equal("Gateway not restarted", warning.Title);
            Assert.Contains("not yet in effect", warning.Message, StringComparison.Ordinal);

            vm.GuardrailRestartAfter = true;
            Assert.Equal("defenseclaw guardrail enable --yes", vm.GuardrailReview!.CommandText);
        });
    }

    [Fact]
    public void Disabling_the_guardrail_is_destructive_because_it_tears_the_live_hooks_down()
    {
        StaThread.Run(() =>
        {
            var vm = Setup();

            vm.DisableGuardrailCommand.Execute(null);

            var review = vm.GuardrailReview!;
            Assert.Equal("defenseclaw guardrail disable --yes", review.CommandText);
            Assert.Equal(CommandTier.Destructive, review.Tier);
            Assert.Equal("Run destructive command", review.ConfirmLabel);
            Assert.Contains("tears down the connector hooks", review.Summary, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void A_fail_mode_change_names_the_mode_and_is_an_ordinary_state_change()
    {
        StaThread.Run(() =>
        {
            var vm = Setup();

            vm.SetFailModeClosedCommand.Execute(null);

            var review = vm.GuardrailReview!;
            Assert.Equal("Set the hook fail mode to closed?", review.Title);
            Assert.Equal("defenseclaw guardrail fail-mode closed --yes", review.CommandText);
            Assert.Equal(CommandTier.StateChanging, review.Tier);
        });
    }

    [Fact]
    public void Cancelling_the_guardrail_review_closes_it_and_runs_nothing()
    {
        StaThread.Run(() =>
        {
            var vm = Setup();
            vm.EnableGuardrailCommand.Execute(null);

            vm.CancelGuardrailReviewCommand.Execute(null);

            Assert.False(vm.IsGuardrailReviewOpen);
            Assert.Empty(_services.Cli.Activity);
        });
    }
}
