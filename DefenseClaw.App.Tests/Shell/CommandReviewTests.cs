using System.ComponentModel;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.Tests.Shell;

/// <summary>
/// The model behind every confirmation surface: how a command's tier is decided (the classifier, a floor,
/// stricter wins), what the dialog says about it by default, and the display text of the command.
/// </summary>
public class CommandReviewTests
{
    // ------------------------------------------------------------------ tier resolution

    [Theory]
    [InlineData("skill list", CommandTier.ReadOnly)]
    [InlineData("skill info --json --connector codex -- pdf", CommandTier.ReadOnly)]
    [InlineData("skill block --connector codex -- pdf", CommandTier.StateChanging)]
    [InlineData("mcp set --command npx -- docs", CommandTier.StateChanging)]
    [InlineData("skill remove -- pdf", CommandTier.Destructive)]
    [InlineData("registry remove corp --non-interactive", CommandTier.Destructive)]
    [InlineData("guardrail frobnicate", CommandTier.StateChanging)]
    public void With_no_floor_the_tier_is_what_the_classifier_says(string argv, CommandTier expected) =>
        Assert.Equal(expected, CommandReview.ResolveTier(argv.Split(' ')));

    [Fact]
    public void An_empty_argv_is_never_read_only()
    {
        Assert.Equal(CommandTier.StateChanging, CommandReview.ResolveTier(Array.Empty<string>()));
    }

    [Theory]
    [InlineData("skill list", CommandTier.StateChanging, CommandTier.StateChanging)]
    [InlineData("skill list", CommandTier.Destructive, CommandTier.Destructive)]
    [InlineData("skill block -- x", CommandTier.ReadOnly, CommandTier.StateChanging)]
    [InlineData("skill block -- x", CommandTier.Destructive, CommandTier.Destructive)]
    public void A_floor_raises_the_tier_and_never_lowers_it(string argv, CommandTier floor, CommandTier expected) =>
        Assert.Equal(expected, CommandReview.ResolveTier(argv.Split(' '), floor));

    [Theory]
    [InlineData("skill remove -- x", CommandTier.StateChanging)]
    [InlineData("skill remove -- x", CommandTier.ReadOnly)]
    public void The_classifier_can_raise_a_tier_above_the_floor(string argv, CommandTier floor) =>
        Assert.Equal(CommandTier.Destructive, CommandReview.ResolveTier(argv.Split(' '), floor));

    [Theory]
    [InlineData("skill block -- --help", CommandTier.StateChanging)]
    [InlineData("skill block -- --dry-run", CommandTier.StateChanging)]
    [InlineData("skill quarantine -- --help", CommandTier.Destructive)]
    [InlineData("skill quarantine --reason x -- --version", CommandTier.Destructive)]
    [InlineData("skill unblock -- list", CommandTier.StateChanging)]
    public void A_target_after_the_double_dash_can_never_lower_the_tier(string argv, CommandTier expected) =>
        Assert.Equal(expected, CommandReview.ResolveTier(argv.Split(' ')));

    [Fact]
    public void A_preview_flag_before_the_double_dash_still_reads_as_a_preview_unless_a_floor_says_otherwise()
    {
        var argv = new[] { "setup", "llm", "--dry-run" };

        Assert.Equal(CommandTier.ReadOnly, CommandReview.ResolveTier(argv));
        Assert.Equal(CommandTier.StateChanging, CommandReview.ResolveTier(argv, CommandTier.StateChanging));
    }

    [Fact]
    public void A_source_id_that_spells_a_read_only_verb_does_not_downgrade_a_sync()
    {
        Assert.Equal(CommandTier.StateChanging, CommandReview.ResolveTier(new[] { "registry", "sync", "list", "--json" }));
    }

    [Theory]
    [InlineData(CommandTier.ReadOnly, CommandTier.ReadOnly, CommandTier.ReadOnly)]
    [InlineData(CommandTier.ReadOnly, CommandTier.StateChanging, CommandTier.StateChanging)]
    [InlineData(CommandTier.Destructive, CommandTier.StateChanging, CommandTier.Destructive)]
    [InlineData(CommandTier.StateChanging, CommandTier.Destructive, CommandTier.Destructive)]
    public void Stricter_picks_the_more_dangerous_tier(CommandTier a, CommandTier b, CommandTier expected)
    {
        Assert.Equal(expected, CommandReview.Stricter(a, b));
        Assert.Equal(expected, CommandReview.Stricter(b, a));
    }

    [Fact]
    public void The_argv_is_copied_so_a_caller_cannot_change_a_command_after_it_was_reviewed()
    {
        var argv = new List<string> { "skill", "block", "--", "x" };
        var step = new CommandReviewStep(argv);

        argv[1] = "remove";

        Assert.Equal("defenseclaw skill block -- x", step.CommandText);
        Assert.Equal(CommandTier.StateChanging, step.Tier);
    }

    // ------------------------------------------------------------------ the review as a whole

    [Fact]
    public void A_review_of_several_steps_takes_the_strictest_tier()
    {
        var review = new CommandReview
        {
            Title = "Add and sync",
            Steps = new[]
            {
                new CommandReviewStep(new[] { "registry", "add", "corp" }, floor: CommandTier.StateChanging, number: 1),
                new CommandReviewStep(new[] { "registry", "remove", "old" }, floor: CommandTier.StateChanging, number: 2),
            },
        };

        Assert.Equal(CommandTier.Destructive, review.Tier);
        Assert.True(review.IsDestructive);
        Assert.True(review.HasMultipleSteps);
        Assert.Equal(new[] { CommandTier.StateChanging, CommandTier.Destructive }, review.Steps.Select(s => s.Tier).ToArray());
        Assert.Equal(new[] { "Step 1", "Step 2" }, review.Steps.Select(s => s.Heading).ToArray());
    }

    [Fact]
    public void The_copied_text_is_every_command_one_per_line()
    {
        var review = new CommandReview
        {
            Title = "t",
            Steps = new[]
            {
                new CommandReviewStep(new[] { "registry", "add", "corp" }),
                new CommandReviewStep(new[] { "registry", "sync", "corp", "--json" }),
            },
        };

        Assert.Equal(
            "defenseclaw registry add corp" + Environment.NewLine + "defenseclaw registry sync corp --json",
            review.CommandText);
    }

    [Fact]
    public void A_single_command_review_is_one_step_with_the_executable_in_front()
    {
        var review = CommandReview.ForCommand("Restart gateway?", new[] { "restart" }, executable: "defenseclaw-gateway");

        var step = Assert.Single(review.Steps);
        Assert.Equal("defenseclaw-gateway restart", step.CommandText);
        Assert.Equal("defenseclaw-gateway restart", review.CommandText);
        Assert.False(review.HasMultipleSteps);
        Assert.Equal(CommandTier.StateChanging, review.Tier);
    }

    [Fact]
    public void A_review_with_no_steps_is_treated_as_a_change_rather_than_a_read()
    {
        var review = new CommandReview { Title = "t", Steps = Array.Empty<CommandReviewStep>() };

        Assert.Equal(CommandTier.StateChanging, review.Tier);
    }

    // ------------------------------------------------------------------ what the dialog says

    [Theory]
    [InlineData(CommandTier.ReadOnly, "Read-only", "Neutral")]
    [InlineData(CommandTier.StateChanging, "Changes state", "Warn")]
    [InlineData(CommandTier.Destructive, "Destructive", "Bad")]
    public void Each_tier_has_a_word_and_a_tone(CommandTier tier, string label, string tone)
    {
        Assert.Equal(label, CommandReview.LabelFor(tier));
        Assert.Equal(tone, CommandReview.ToneFor(tier));
    }

    [Fact]
    public void The_tier_word_and_tone_follow_the_review_tier()
    {
        var read = CommandReview.ForCommand("t", new[] { "skill", "list" });
        var change = CommandReview.ForCommand("t", new[] { "skill", "block", "--", "x" });
        var destroy = CommandReview.ForCommand("t", new[] { "skill", "remove", "--", "x" });

        Assert.Equal(("Read-only", "Neutral"), (read.TierLabel, read.TierKey));
        Assert.Equal(("Changes state", "Warn"), (change.TierLabel, change.TierKey));
        Assert.Equal(("Destructive", "Bad"), (destroy.TierLabel, destroy.TierKey));
    }

    [Theory]
    [InlineData("skill list", "Run command")]
    [InlineData("skill block -- x", "Run command")]
    [InlineData("skill remove -- x", "Run destructive command")]
    public void The_confirm_button_names_the_action_by_tier_unless_the_surface_names_it(string argv, string expected)
    {
        var review = CommandReview.ForCommand("t", argv.Split(' '));

        Assert.Equal(expected, review.ConfirmLabel);
    }

    [Fact]
    public void A_surface_can_name_the_confirm_button_and_a_blank_name_falls_back_to_the_default()
    {
        var named = CommandReview.ForCommand("t", new[] { "skill", "remove", "--", "x" }) with { ConfirmLabel = "Remove skill" };
        var blank = CommandReview.ForCommand("t", new[] { "skill", "remove", "--", "x" }) with { ConfirmLabel = string.Empty };

        Assert.Equal("Remove skill", named.ConfirmLabel);
        Assert.Equal("Run destructive command", blank.ConfirmLabel);
        Assert.Equal("Cancel", named.CancelLabel);
    }

    [Fact]
    public void Screen_readers_get_the_title_and_the_tier_and_the_way_out()
    {
        var review = CommandReview.ForCommand("Remove skill “x”?", new[] { "skill", "remove", "--", "x" });

        Assert.Equal("Review command: Remove skill “x”?", review.AutomationName);
        Assert.Contains("Destructive", review.AutomationHelp, StringComparison.Ordinal);
        Assert.Contains("Nothing runs until you confirm", review.AutomationHelp, StringComparison.Ordinal);
        Assert.Contains("Escape", review.AutomationHelp, StringComparison.Ordinal);
    }

    [Fact]
    public void A_review_has_no_warnings_and_no_restart_badge_until_it_is_given_some()
    {
        var review = CommandReview.ForCommand("t", new[] { "skill", "block", "--", "x" });

        Assert.Empty(review.Warnings);
        Assert.False(review.RestartsGateway);
        Assert.Equal(string.Empty, review.Summary);
    }

    [Fact]
    public void The_gateway_restart_warning_carries_the_required_sentence()
    {
        var warning = CommandReviewWarning.GatewayRestart();

        Assert.Equal("Gateway restart", warning.Title);
        Assert.StartsWith(CommandReview.RestartSentence, warning.Message, StringComparison.Ordinal);
        Assert.Equal("This restarts the DefenseClaw gateway.", CommandReview.RestartSentence);
        Assert.Equal("Custom.", CommandReviewWarning.GatewayRestart("Custom.").Message);
    }

    // ------------------------------------------------------------------ display text

    [Theory]
    [InlineData("pdf-tools", "pdf-tools")]
    [InlineData("", "\"\"")]
    [InlineData("my skill", "\"my skill\"")]
    [InlineData("a\tb", "\"a\tb\"")]
    [InlineData("say \"hi\" there", "\"say \\\"hi\\\" there\"")]
    [InlineData("[\"a,b\"]", "[\"a,b\"]")]
    [InlineData("--dangerous", "--dangerous")]
    public void Arguments_are_quoted_only_when_a_reader_could_otherwise_split_them(string argument, string expected) =>
        Assert.Equal(expected, CommandReview.Quote(argument));

    [Fact]
    public void The_command_line_is_the_executable_then_each_quoted_argument()
    {
        Assert.Equal(
            "defenseclaw skill block --reason \"not vetted\" -- x",
            CommandReview.CommandLine("defenseclaw", new[] { "skill", "block", "--reason", "not vetted", "--", "x" }));
        Assert.Equal("defenseclaw", CommandReview.CommandLine("defenseclaw", Array.Empty<string>()));
    }

    // ------------------------------------------------------------------ step status

    [Fact]
    public void A_step_reports_its_status_as_it_changes()
    {
        var step = new CommandReviewStep(new[] { "registry", "sync", "corp" }, number: 2);
        var changed = new List<string?>();
        ((INotifyPropertyChanged)step).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.False(step.HasStatus);
        Assert.Equal("Neutral", step.StatusKey);

        step.SetStatus("Running…", "Warn");

        Assert.True(step.HasStatus);
        Assert.Equal("Running…", step.StatusText);
        Assert.Equal("Warn", step.StatusKey);
        Assert.Contains(nameof(CommandReviewStep.StatusText), changed);
        Assert.Contains(nameof(CommandReviewStep.HasStatus), changed);
        Assert.Equal("Command for step 2", step.AutomationName);
        Assert.Equal("Command that will run", new CommandReviewStep(new[] { "skill", "list" }).AutomationName);
    }

    // ------------------------------------------------------------------ restarting the gateway

    [Theory]
    [InlineData("setup claude-code", true)]
    [InlineData("setup llm --provider x", true)]
    [InlineData("setup claude-code --no-restart", false)]
    [InlineData("setup claude-code --dry-run", false)]
    [InlineData("setup observability list", false)]
    [InlineData("setup observability add x", true)]
    [InlineData("guardrail enable --yes", false)]
    [InlineData("skill block -- x", false)]
    public void Only_a_setup_command_that_writes_configuration_restarts_the_gateway(string argv, bool expected) =>
        Assert.Equal(expected, CommandReview.RestartsGatewayFor(argv.Split(' ')));

    [Fact]
    public void The_wizard_restart_paragraph_is_the_shared_notice_plus_a_pointer_to_its_toggle()
    {
        var definition = WizardSamples.ClaudeCode();
        var values = WizardSamples.StartingValues(definition);

        var withToggle = WizardReview.RestartWarning(definition, values, new[] { "setup", "claude-code" });
        var notRestarting = WizardReview.RestartWarning(definition, values, new[] { "setup", "claude-code", "--no-restart" });

        Assert.StartsWith(CommandReview.RestartNotice, withToggle, StringComparison.Ordinal);
        Assert.Contains("--no-restart", withToggle, StringComparison.Ordinal);
        Assert.Equal(string.Empty, notRestarting);
    }

    [Fact]
    public void The_wizard_restart_paragraph_does_not_offer_a_toggle_the_wizard_does_not_have()
    {
        var definition = WizardSamples.Llm();
        var values = WizardSamples.StartingValues(definition);

        var warning = WizardReview.RestartWarning(definition, values, new[] { "setup", "llm", "--provider", "x" });

        Assert.Equal(CommandReview.RestartNotice, warning);
    }
}
