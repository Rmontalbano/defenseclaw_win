using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels.Wizards;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.Tests.Shell;

/// <summary>
/// What a review says when the command it shows is not quite the command that runs (arguments the CLI rewrites,
/// secret values on the screen), and the text that goes on the clipboard: safe to paste, not just to read.
/// </summary>
public sealed class CommandReviewHazardTests : IDisposable
{
    private const string Variable = "DCW_APP_TEST_TARGET_X1";

    public CommandReviewHazardTests() => Environment.SetEnvironmentVariable(Variable, "elsewhere");

    public void Dispose() => Environment.SetEnvironmentVariable(Variable, null);

    private static CommandReview Review(params string[] argv) => CommandReview.ForCommand("t", argv);

    // ------------------------------------------------------------------ the clipboard form

    [Fact]
    public void The_clipboard_text_quotes_every_argument_for_powershell_while_the_display_text_stays_readable()
    {
        var review = Review("skill", "quarantine", "--", "x&calc");

        Assert.Equal("defenseclaw skill quarantine -- x&calc", review.CommandText);
        Assert.Equal("defenseclaw skill quarantine -- 'x&calc'", review.ClipboardText);
    }

    [Theory]
    [InlineData("x&calc", "'x&calc'")]
    [InlineData("x;calc", "'x;calc'")]
    [InlineData("x$(calc)", "'x$(calc)'")]
    [InlineData("a $(calc) b", "'a $(calc) b'")]
    [InlineData("x`calc", "'x`calc'")]
    [InlineData("x'y", "'x''y'")]
    [InlineData("pdf-tools", "pdf-tools")]
    public void A_hostile_name_is_one_literal_argument_on_the_clipboard(string name, string quoted) =>
        Assert.Equal("defenseclaw skill block -- " + quoted, Review("skill", "block", "--", name).ClipboardText);

    [Fact]
    public void A_review_of_several_steps_copies_one_safe_command_per_line()
    {
        var review = new CommandReview
        {
            Title = "t",
            Steps = new[]
            {
                new CommandReviewStep(new[] { "registry", "add", "a&b" }),
                new CommandReviewStep(new[] { "registry", "sync", "a&b" }),
            },
        };

        Assert.Equal(
            "defenseclaw registry add 'a&b'" + Environment.NewLine + "defenseclaw registry sync 'a&b'",
            review.ClipboardText);
    }

    [Fact]
    public void A_step_for_an_executable_with_a_path_is_called_with_the_call_operator()
    {
        var step = new CommandReviewStep(new[] { "start" }, executable: @"C:\Program Files\DefenseClaw\defenseclaw-gateway.exe");

        Assert.Equal(@"& 'C:\Program Files\DefenseClaw\defenseclaw-gateway.exe' start", step.ClipboardText);
    }

    // ------------------------------------------------------------------ arguments the CLI would rewrite

    [Fact]
    public void An_argument_the_cli_would_expand_puts_a_warning_on_the_review_with_what_it_becomes()
    {
        var review = Review("skill", "block", "--", "%" + Variable + "%");

        var warning = Assert.Single(review.Warnings);
        Assert.Equal(CommandReviewWarning.ArgumentExpansionTitle, warning.Title);
        Assert.Contains("“%" + Variable + "%” would arrive as “elsewhere”", warning.Message, StringComparison.Ordinal);
        Assert.Contains("%VARIABLES%", warning.Message, StringComparison.Ordinal);
        Assert.Single(Assert.Single(review.Steps).Hazards);
    }

    [Fact]
    public void An_argument_that_expands_to_itself_gets_no_warning()
    {
        // A wildcard nothing matches, a question mark, a percent sign and a dollar sign in prose.
        var review = Review("skill", "block", "--reason", "why? 50% of $5", "--", "no-such-*");

        Assert.Empty(review.Warnings);
        Assert.Empty(review.Steps[0].Hazards);
    }

    [Fact]
    public void The_gateway_does_not_expand_its_arguments_so_its_reviews_carry_no_such_warning()
    {
        var review = CommandReview.ForCommand("t", new[] { "start", "%" + Variable + "%" }, executable: "defenseclaw-gateway");

        Assert.Empty(review.Warnings);
    }

    [Fact]
    public void The_expansion_warning_says_which_step_when_a_review_has_several()
    {
        var review = new CommandReview
        {
            Title = "t",
            Steps = new[]
            {
                new CommandReviewStep(new[] { "registry", "add", "ok" }, number: 1),
                new CommandReviewStep(new[] { "registry", "sync", "%" + Variable + "%" }, number: 2),
            },
        };

        Assert.StartsWith("The DefenseClaw CLI expands", review.Warnings.Single().Message, StringComparison.Ordinal);
        Assert.Contains("Step 2: “%" + Variable + "%” would arrive as “elsewhere”", review.Warnings.Single().Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_warnings_a_caller_supplies_come_first_and_the_derived_ones_follow_without_duplicates()
    {
        var supplied = new[] { CommandReviewWarning.GatewayRestart(), new CommandReviewWarning("Before you continue", "Read this.") };
        var review = new CommandReview
        {
            Title = "t",
            Steps = new[] { new CommandReviewStep(new[] { "config", "show", "--reveal", "%" + Variable + "%" }) },
            Warnings = supplied,
        };

        Assert.Equal(
            new[] { "Gateway restart", "Before you continue", CommandReviewWarning.ArgumentExpansionTitle, CommandReviewWarning.SecretOutputTitle },
            review.Warnings.Select(w => w.Title));

        // A caller that already carries the bar is not given a second one.
        var again = review with { Warnings = review.Warnings };
        Assert.Equal(review.Warnings.Count, again.Warnings.Count);
    }

    // ------------------------------------------------------------------ secret values on the screen

    [Theory]
    [InlineData("config", "show", "--reveal")]
    [InlineData("keys", "list", "--show-values")]
    [InlineData("setup", "splunk", "--show-credentials")]
    public void Asking_for_secret_values_is_a_reviewed_change_with_a_warning_of_its_own(params string[] argv)
    {
        var review = Review(argv);

        Assert.NotEqual(CommandTier.ReadOnly, review.Tier);
        var warning = Assert.Single(review.Warnings);
        Assert.Equal(CommandReviewWarning.SecretOutputTitle, warning.Title);
        Assert.Contains("Activity panel", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_plain_config_show_is_still_read_only_and_carries_no_warning()
    {
        var review = Review("config", "show", "--format", "yaml");

        Assert.Equal(CommandTier.ReadOnly, review.Tier);
        Assert.Empty(review.Warnings);
    }

    // ------------------------------------------------------------------ a flag spelled as a value

    [Theory]
    [InlineData("setup guardrail --block-message --show", true)]
    [InlineData("setup webhook add --name --dry-run", true)]
    [InlineData("setup guardrail --show", false)]
    [InlineData("setup claude-code --no-restart", false)]
    [InlineData("setup claude-code --yes --no-restart", false)]
    [InlineData("setup claude-code --reason=x --dry-run", false)]
    [InlineData("setup claude-code -- --no-restart", true)]
    public void A_restart_flag_only_counts_when_it_is_a_flag_and_not_another_options_value(string argv, bool restarts) =>
        Assert.Equal(restarts, CommandReview.RestartsGatewayFor(argv.Split(' ')));

    // ------------------------------------------------------------------ the wizard's floor

    [Fact]
    public void A_wizard_review_is_never_read_only_even_when_a_typed_value_spells_a_preview_flag()
    {
        var help = SetupHelpParser.Parse(
            "Usage: defenseclaw setup remove [OPTIONS] CONNECTOR\n\n  Remove a connector from the configured set.\n\nOptions:\n  --help  Show this message and exit.\n");
        var (steps, _) = WizardStepFactory.Build("remove", help);
        var definition = new WizardDefinition
        {
            Target = "remove",
            Title = "Remove",
            Group = WizardGroups.Credentials,
            Steps = steps,
            PlatformStatus = help.PlatformStatus,
            IsDetailLoaded = true,
        };
        using var temp = new TempDirectory();

        StaThread.Run(() =>
        {
            using var services = TestServices.Create(temp);
            using var vm = new WizardViewModel(services, definition);
            var field = vm.Steps.SelectMany(s => s.Fields).Single(f => f.Field.IsPositional);
            field.Value = "--dry-run";
            while (!vm.IsReview)
            {
                vm.Next();
                Assert.False(vm.HasValidationSummary, vm.ValidationSummary);
            }

            // Alone, "setup remove --dry-run" classifies as a preview; a wizard writes configuration, so it is not shown as one.
            Assert.Equal(CommandTier.ReadOnly, CommandTiers.Classify(new[] { "setup", "remove", "--dry-run" }));
            Assert.Equal(new[] { "setup", "remove", "--dry-run" }, vm.CommandReview!.Steps[0].Argv);
            Assert.NotEqual(CommandTier.ReadOnly, vm.CommandReview.Tier);
        });
    }
}
