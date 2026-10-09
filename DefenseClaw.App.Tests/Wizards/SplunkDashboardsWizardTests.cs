using System.ComponentModel;
using System.Security;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.ViewModels.Wizards;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Config;
using DefenseClaw.Core.Paths;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.Tests.Wizards;

/// <summary>
/// The wizard's last page for each verb of <c>setup splunk dashboards</c> (CUST-317): the exact command, its tier, whether it is destructive,
/// what it warns about, and how the Splunk token travels. Driven through the real <see cref="WizardViewModel"/> on the shared UI thread; the
/// help screens are synthetic, the runner has no CLI, and the one child that is ever started is <c>cmd.exe</c> printing a variable.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class SplunkDashboardsReviewPageTests
{
    private const string Typed = "synthetic-splunk-token-317-xyz";

    private static SecureString Secure(string text)
    {
        var secure = new SecureString();
        foreach (var c in text)
        {
            secure.AppendChar(c);
        }

        return secure;
    }

    private sealed class Scene : IDisposable
    {
        private readonly TempDirectory _temp = new();

        /// <param name="installation">
        /// The installation the composition starts with, made from the scratch folder (<see cref="TestInstallations.ManagedAt"/>); null is the usual
        /// writable one.
        /// </param>
        public Scene(Func<string, InstallationContext>? installation = null)
        {
            Services = AppServices.CreateIsolated(
                TestServices.IsolatedPaths(_temp.Path, installation?.Invoke(_temp.Path)),
                claudeSettingsPath: _temp.File("claude-settings.json"));
            ViewModel = UiThread.Run(() => new WizardViewModel(Services, WizardSamples.Dashboards()));
        }

        public AppServices Services { get; }

        public WizardViewModel ViewModel { get; }

        /// <summary>The scratch folder the composition treats as its data directory.</summary>
        public string Folder => _temp.Path;

        public WizardFieldViewModel Field(string id) => ViewModel.Steps.SelectMany(s => s.Fields).Single(f => f.Id == id);

        /// <summary>Picks the verb, sets the answers and walks to the review page.</summary>
        public CommandReview Review(string verb, params (string Id, string Value)[] answers) => UiThread.Run(() =>
        {
            Field("subcommand").Value = verb;
            foreach (var (id, value) in answers)
            {
                Field(id).Value = value;
            }

            for (var i = 0; i < 12 && !ViewModel.IsReview; i++)
            {
                ViewModel.Next();
            }

            Assert.True(ViewModel.IsReview, ViewModel.ValidationSummary);
            return ViewModel.CommandReview!;
        });

        public void Dispose()
        {
            UiThread.Run(() => ViewModel.Dispose());
            Services.Dispose();
            _temp.Dispose();
        }
    }

    [Fact]
    public void The_wizard_opens_on_the_command_page_with_plan_chosen()
    {
        using var scene = new Scene();

        UiThread.Run(() =>
        {
            Assert.Equal("Command", scene.ViewModel.CurrentStep!.Title);
            Assert.Equal("plan", scene.Field("subcommand").Value);
            Assert.False(scene.ViewModel.IsReview);
            Assert.Equal("splunk dashboards", scene.ViewModel.Target);
            Assert.Equal("Splunk dashboards", scene.ViewModel.Title);
        });
    }

    [Theory]
    [InlineData("plan", CommandTier.StateChanging, "setup splunk dashboards plan")]
    [InlineData("apply", CommandTier.StateChanging, "setup splunk dashboards apply --yes")]
    [InlineData("destroy", CommandTier.Destructive, "setup splunk dashboards destroy --yes")]
    public void Each_verb_is_reviewed_with_its_exact_command_and_the_tier_its_source_earns(string verb, CommandTier tier, string command)
    {
        using var scene = new Scene();

        var review = scene.Review(verb);

        Assert.Equal(command.Split(' '), Assert.Single(review.Steps).Argv);
        Assert.Equal("defenseclaw " + command, review.CommandText);
        Assert.Equal(tier, review.Tier);

        // None of them writes config.yaml, so none restarts the gateway - which the general rule for a `setup` command would have said.
        Assert.False(review.RestartsGateway);
        Assert.DoesNotContain(review.Warnings, w => w.Title == "Gateway restart");

        UiThread.Run(() =>
        {
            // The line above "what you changed" says what the verb does to Splunk, not "nothing was changed, running this re-applies it".
            Assert.Equal(SplunkDashboardsReview.Summary(command.Split(' ')), scene.ViewModel.ChangeSummary);
            Assert.DoesNotContain("re-applies", scene.ViewModel.ChangeSummary, StringComparison.Ordinal);
            Assert.Equal(tier == CommandTier.Destructive, scene.ViewModel.IsDestructive);
            Assert.Equal(tier == CommandTier.Destructive, scene.ViewModel.ShowDangerExecute);
            Assert.Equal(tier != CommandTier.Destructive, scene.ViewModel.ShowExecute);
            Assert.True(scene.ViewModel.CanExecute);

            // plan is the preview; there is no --dry-run to offer.
            Assert.False(scene.ViewModel.CanPreview);
            Assert.False(scene.ViewModel.HasPromptWarning, scene.ViewModel.PromptWarning);
        });
    }

    [Fact]
    public void Destroy_is_labelled_destructive_with_the_danger_button_and_the_others_are_a_change()
    {
        using var scene = new Scene();

        var plan = scene.Review("plan");
        Assert.Equal("Changes state", plan.TierLabel);
        Assert.Equal("Run command", plan.ConfirmLabel);

        var apply = scene.Review("apply");
        Assert.Equal("Changes state", apply.TierLabel);
        Assert.Equal("Run command", apply.ConfirmLabel);

        var destroy = scene.Review("destroy");
        Assert.Equal("Destructive", destroy.TierLabel);
        Assert.Equal("Run destructive command", destroy.ConfirmLabel);
        Assert.True(destroy.IsDestructive);
    }

    [Fact]
    public void Switching_the_verb_back_to_plan_lowers_the_tier_with_it()
    {
        using var scene = new Scene();

        Assert.Equal(CommandTier.Destructive, scene.Review("destroy").Tier);

        UiThread.Run(() =>
        {
            scene.ViewModel.Back();
            scene.Field("subcommand").Value = "plan";
        });

        UiThread.Run(() =>
        {
            for (var i = 0; i < 12 && !scene.ViewModel.IsReview; i++)
            {
                scene.ViewModel.Next();
            }

            Assert.Equal(CommandTier.StateChanging, scene.ViewModel.CommandReview!.Tier);
            Assert.False(scene.ViewModel.IsDestructive);
            Assert.Equal(new[] { "setup", "splunk", "dashboards", "plan" }, Assert.Single(scene.ViewModel.CommandReview.Steps).Argv);
        });
    }

    [Fact]
    public void Each_verb_carries_the_bars_that_are_true_of_it()
    {
        using var scene = new Scene();

        Assert.Empty(scene.Review("plan").Warnings);

        Assert.Equal(
            new[] { SplunkDashboardsReview.AppliesNowTitle, SplunkDashboardsReview.RemovesDetectorsTitle },
            scene.Review("apply").Warnings.Select(w => w.Title).ToArray());

        Assert.Equal(
            new[] { SplunkDashboardsReview.DeletesTitle },
            scene.Review("destroy").Warnings.Select(w => w.Title).ToArray());
    }

    [Fact]
    public void Turning_detectors_on_takes_the_removal_bar_away_and_puts_the_flag_on_the_command()
    {
        using var scene = new Scene();

        var review = scene.Review("apply", ("apply:with-detectors", ToggleValues.On), ("apply:enable-detectors", ToggleValues.On));

        Assert.Equal(
            new[] { "setup", "splunk", "dashboards", "apply", "--with-detectors", "--enable-detectors", "--yes" },
            Assert.Single(review.Steps).Argv);
        Assert.Equal(new[] { SplunkDashboardsReview.AppliesNowTitle }, review.Warnings.Select(w => w.Title).ToArray());
        UiThread.Run(() =>
        {
            Assert.True(scene.ViewModel.HasChanges);
            Assert.Contains(scene.ViewModel.ReviewChanges, c => c.Contains("detectors", StringComparison.OrdinalIgnoreCase));
            Assert.EndsWith("What you chose:", scene.ViewModel.ChangeSummary, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Turning_the_confirmation_switch_off_says_the_command_will_prompt_and_still_names_the_verb()
    {
        using var scene = new Scene();

        var review = scene.Review("destroy", ("destroy:yes", ToggleValues.Off));

        Assert.Equal(new[] { "setup", "splunk", "dashboards", "destroy" }, Assert.Single(review.Steps).Argv);
        UiThread.Run(() => Assert.True(scene.ViewModel.HasPromptWarning));
        Assert.Equal(CommandTier.Destructive, review.Tier);

        // Without --yes the review cannot say it stands in for the CLI's question, because it does not.
        Assert.DoesNotContain("which is why --yes", Assert.Single(review.Warnings).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("plan", "--dry-run")]
    [InlineData("apply", "--help")]
    [InlineData("destroy", "--dry-run")]
    [InlineData("destroy", "--version")]
    public void A_value_typed_into_a_name_that_spells_a_preview_flag_cannot_make_the_review_look_harmless(string verb, string typed)
    {
        using var scene = new Scene();

        var review = scene.Review(verb, ($"{verb}:name-prefix", typed));

        Assert.Contains(typed, Assert.Single(review.Steps).Argv);
        Assert.Equal(verb == "destroy" ? CommandTier.Destructive : CommandTier.StateChanging, review.Tier);
    }

    // ------------------------------------------------------------------ the token

    [Fact]
    public void A_typed_token_is_supplied_as_sfx_auth_token_in_the_environment_and_only_the_name_is_shown()
    {
        using var scene = new Scene();
        var token = scene.Field("apply:o11y-api-token");

        UiThread.Run(() =>
        {
            Assert.True(token.IsSecret);
            Assert.True(token.OffersInAppEntry);
            Assert.Equal("SFX_AUTH_TOKEN", token.InAppEnvName);
            Assert.Contains("the CLI does not store it", token.InAppExplanation, StringComparison.Ordinal);
            Assert.DoesNotContain("~/.defenseclaw/.env", token.InAppExplanation, StringComparison.Ordinal);
        });

        _ = scene.Review("apply");
        UiThread.Run(() => token.SetEntry(Secure("  " + Typed + "  ")));

        UiThread.Run(() =>
        {
            Assert.True(token.HasEntry);
            var options = scene.ViewModel.BuildRunOptions();
            Assert.NotNull(options);
            var (name, secret) = Assert.Single(options!.EnvironmentOverlay);
            Assert.Equal("SFX_AUTH_TOKEN", name);
            Assert.Equal(Typed, secret.Reveal());

            // Nothing a screen can show carries the value; the review names the variable and says it is masked.
            var note = Assert.Single(scene.ViewModel.EnvironmentNotes);
            Assert.StartsWith("SFX_AUTH_TOKEN=•••", note, StringComparison.Ordinal);
            Assert.Contains("not on the command line", note, StringComparison.Ordinal);
            Assert.All(
                new[]
                {
                    note, token.EntryStatus, token.CredentialReviewNote, token.InAppExplanation, token.CredentialStatus,
                    options.ToString(), scene.ViewModel.CommandReview!.CommandText, scene.ViewModel.CommandReview.ClipboardText,
                },
                text => Assert.DoesNotContain(Typed, text, StringComparison.Ordinal));
            Assert.All(Assert.Single(scene.ViewModel.CommandReview.Steps).Argv, a => Assert.DoesNotContain("synthetic-splunk-token", a, StringComparison.Ordinal));
            Assert.DoesNotContain("--o11y-api-token", Assert.Single(scene.ViewModel.CommandReview.Steps).Argv);
        });
    }

    [Theory]
    [InlineData("plan")]
    [InlineData("apply")]
    [InlineData("destroy")]
    public void The_token_is_offered_on_every_verb_and_goes_to_that_verbs_run_only(string verb)
    {
        using var scene = new Scene();

        _ = scene.Review(verb);
        UiThread.Run(() =>
        {
            scene.Field($"{verb}:o11y-api-token").SetEntry(Secure(Typed));
            var options = scene.ViewModel.BuildRunOptions();

            Assert.Equal(new[] { "SFX_AUTH_TOKEN" }, options!.EnvironmentOverlay.Keys);
        });
    }

    [Fact]
    public void Nothing_typed_means_an_ordinary_run_that_reads_the_variable_from_where_it_is_stored()
    {
        using var scene = new Scene();

        _ = scene.Review("apply");

        UiThread.Run(() =>
        {
            Assert.Null(scene.ViewModel.BuildRunOptions());
            Assert.Empty(scene.ViewModel.EnvironmentNotes);
            Assert.Contains(scene.Field("apply:o11y-api-token"), scene.ViewModel.ReviewCredentials);
        });
    }

    [Fact]
    public void Closing_the_wizard_drops_the_token()
    {
        using var scene = new Scene();
        var token = scene.Field("destroy:o11y-api-token");
        UiThread.Run(() => token.SetEntry(Secure(Typed)));
        Assert.True(UiThread.Run(() => token.HasEntry));

        UiThread.Run(() => scene.ViewModel.Dispose());

        Assert.False(UiThread.Run(() => token.HasEntry));
        Assert.Null(UiThread.Run(() => token.MaterializeSecret()));
    }

    [Fact]
    public async Task The_typed_token_reaches_a_real_child_only_through_its_environment_and_the_run_is_recorded_without_it()
    {
        using var scene = new Scene();
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        _ = scene.Review("apply");
        var options = UiThread.Run(() =>
        {
            scene.Field("apply:o11y-api-token").SetEntry(Secure(Typed));
            return scene.ViewModel.BuildRunOptions()!;
        });

        // The child is cmd.exe printing the variable (never defenseclaw, never terraform): the runner masks every overlay value in its output.
        var invocation = await scene.Services.Cli.RunExecutableAsync(cmd, new[] { "/c", "set", "SFX_AUTH_TOKEN" }, options: options);

        Assert.Equal(0, invocation.ExitCode);
        Assert.Contains(invocation.OutputLines, l => l.Text == $"SFX_AUTH_TOKEN={SecretValue.Redacted}");
        Assert.Equal("env: SFX_AUTH_TOKEN=•••", invocation.EnvironmentDisplay);
        Assert.Equal(new[] { "SFX_AUTH_TOKEN" }, invocation.EnvironmentNames);
        Assert.DoesNotContain(Typed, invocation.CommandLine, StringComparison.Ordinal);
        Assert.All(invocation.OutputLines, l => Assert.DoesNotContain(Typed, l.Text, StringComparison.Ordinal));

        // It is in Activity like every other run, by names only.
        var recorded = Assert.Single(scene.Services.Cli.Activity);
        Assert.Same(invocation, recorded);
        Assert.Equal(new[] { "SFX_AUTH_TOKEN" }, recorded.EnvironmentNames);
        Assert.DoesNotContain(Typed, recorded.CommandLine + recorded.EnvironmentDisplay, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_token_that_is_also_pasted_into_an_ordinary_field_is_refused_by_the_runner_before_any_child_starts()
    {
        using var scene = new Scene();
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        _ = scene.Review("apply", ("apply:name-prefix", Typed));
        var (argv, options) = UiThread.Run(() =>
        {
            scene.Field("apply:o11y-api-token").SetEntry(Secure(Typed));
            return (Assert.Single(scene.ViewModel.CommandReview!.Steps).Argv, scene.ViewModel.BuildRunOptions()!);
        });

        // The prefix is on the command line (it is not a secret field), and so would the token be if it were the same text: the runner says no.
        Assert.Contains(Typed, argv);
        _ = await Assert.ThrowsAsync<SecretInArgumentException>(
            () => scene.Services.Cli.RunExecutableAsync(cmd, new[] { "/c", "echo" }.Concat(argv).ToArray(), options: options));
        Assert.Empty(scene.Services.Cli.Activity);
    }

    [Fact]
    public async Task Running_the_wizard_with_no_cli_starts_nothing_and_records_nothing()
    {
        using var scene = new Scene();
        _ = scene.Review("plan");

        // Started on the UI thread and awaited here, so the view-model's continuation has a free dispatcher to come back to.
        await UiThread.Run(() => scene.ViewModel.ExecuteCommand.ExecuteAsync(null));

        UiThread.Run(() =>
        {
            Assert.Equal("not run", scene.ViewModel.ExitBadgeText);
            Assert.Contains("defenseclaw", scene.ViewModel.ResultMessage, StringComparison.OrdinalIgnoreCase);
        });
        Assert.Empty(scene.Services.Cli.Activity);
    }

    // ------------------------------------------------------------------ a read-only installation (CUST-308)

    [Theory]
    [InlineData("plan", "setup splunk dashboards plan")]
    [InlineData("apply", "setup splunk dashboards apply --yes")]
    [InlineData("destroy", "setup splunk dashboards destroy --yes")]
    public void On_a_read_only_installation_every_verb_is_reviewed_with_its_exact_command_but_cannot_be_executed(string verb, string command)
    {
        using var scene = new Scene(TestInstallations.ManagedAt);

        var review = scene.Review(verb);

        // All three change something (plan writes Terraform's files and state), so the review is blocked, with the installation's sentence once.
        Assert.True(review.IsBlocked);
        Assert.Equal(TestInstallations.ManagedReason, review.BlockedReason);
        _ = Assert.Single(review.Warnings, w => w.Title == CommandReviewWarning.ReadOnlyInstallationTitle && w.Message == TestInstallations.ManagedReason);

        // What it would have run is still said exactly, and the verb's own bars are still there beside the installation's.
        Assert.Equal(command.Split(' '), Assert.Single(review.Steps).Argv);
        Assert.Equal("defenseclaw " + command, review.CommandText);
        Assert.Equal(
            SplunkDashboardsReview.Warnings(command.Split(' ')).Select(w => w.Title).Prepend(CommandReviewWarning.ReadOnlyInstallationTitle).Order(StringComparer.Ordinal).ToArray(),
            review.Warnings.Select(w => w.Title).Order(StringComparer.Ordinal).ToArray());

        UiThread.Run(() =>
        {
            Assert.False(scene.ViewModel.CanExecute);
            Assert.Equal(TestInstallations.ManagedReason, scene.ViewModel.InstallationBlockedReason);

            // Pressing it anyway starts nothing: Execute is off, and the runner would refuse the run in any case.
            scene.ViewModel.ExecuteCommand.Execute(null);
            Assert.False(scene.ViewModel.IsRunning);
            Assert.False(scene.ViewModel.HasRun);
        });
        Assert.Empty(scene.Services.Cli.Activity);
    }

    [Fact]
    public void On_a_writable_installation_the_same_review_is_not_blocked()
    {
        using var scene = new Scene();

        var review = scene.Review("destroy");

        Assert.False(review.IsBlocked);
        Assert.DoesNotContain(review.Warnings, w => w.Title == CommandReviewWarning.ReadOnlyInstallationTitle);
        UiThread.Run(() =>
        {
            Assert.Null(scene.ViewModel.InstallationBlockedReason);
            Assert.True(scene.ViewModel.CanExecute);
        });
    }

    [Fact]
    public void An_open_wizard_follows_the_installation_when_it_turns_read_only_and_back()
    {
        using var scene = new Scene();
        _ = scene.Review("apply");

        UiThread.Run(() =>
        {
            Assert.True(scene.ViewModel.CanExecute);

            scene.Services.Installation.Replace(TestInstallations.ManagedAt(scene.Folder));
            Assert.False(scene.ViewModel.CanExecute);
            Assert.True(scene.ViewModel.CommandReview!.IsBlocked);
            Assert.Equal(TestInstallations.ManagedReason, scene.ViewModel.InstallationBlockedReason);

            scene.Services.Installation.Replace(TestInstallations.UserDefault());
            Assert.True(scene.ViewModel.CanExecute);
            Assert.False(scene.ViewModel.CommandReview!.IsBlocked);
        });
    }

    [Fact]
    public async Task The_runner_refuses_the_command_a_surface_forgot_to_turn_off_and_records_it_without_the_token()
    {
        using var scene = new Scene(TestInstallations.ManagedAt);
        _ = scene.Review("destroy");
        var (argv, options) = UiThread.Run(() =>
        {
            scene.Field("destroy:o11y-api-token").SetEntry(Secure(Typed));
            return (Assert.Single(scene.ViewModel.CommandReview!.Steps).Argv, scene.ViewModel.BuildRunOptions()!);
        });

        // The courtesy buttons are one thing; the guard is the runner, which says no before it records or starts anything.
        var refused = await scene.Services.Cli.RunAsync(argv, options: options);

        Assert.False(refused.IsRunning);
        Assert.Null(refused.ExitCode);
        Assert.StartsWith(CliRunner.RefusedPrefix + " — ", refused.FailureReason, StringComparison.Ordinal);
        Assert.Contains(TestInstallations.ManagedReason, refused.FailureReason, StringComparison.Ordinal);
        Assert.Same(refused, Assert.Single(scene.Services.Cli.Activity));
        Assert.Equal(argv, refused.Argv);

        // The entry holds the argv and the sentence and nothing else: the token was in the child's environment, which never started.
        var everything = refused.CommandLine + refused.FailureReason + refused.EnvironmentDisplay + string.Join('\n', refused.OutputLines.Select(l => l.Text));
        Assert.DoesNotContain(Typed, everything, StringComparison.Ordinal);
        Assert.Empty(refused.EnvironmentNames);
    }
}

/// <summary>
/// The Setup hub's Splunk dashboards card (CUST-317): unavailable while Terraform has not answered or says no (with the probe's reason, in the
/// "not available" group), launchable when it is there and new enough, looked at again on Refresh - never on a timer - and present at all only
/// when the catalog has the Splunk card it hangs off.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class SplunkDashboardsCardTests
{
    private static WizardDefinition Def(string target, string group, PlatformStatus status = PlatformStatus.NotApplicable, string description = "") => new()
    {
        Target = target,
        Title = target,
        Group = group,
        PlatformStatus = status,
        Description = description,
        IsDetailLoaded = true,
    };

    private static readonly TerraformStatus Ready =
        new(TerraformState.Ready, "Terraform 1.9.5 is available.", "1.9.5", @"C:\synthetic\bin\terraform.exe");

    private static readonly TerraformStatus NoTerraform =
        new(TerraformState.NotInstalled, "Terraform was not found on this machine's PATH.", string.Empty, "terraform");

    // ------------------------------------------------------------------ the card

    [Fact]
    public void A_card_that_needs_terraform_follows_the_gate_and_moves_between_its_group_and_not_available()
    {
        var card = new WizardCardViewModel(SplunkDashboards.Stub());
        var reason = "Terraform was not found on this machine's PATH. The Splunk dashboards are created by Terraform 1.5.0 or later; check again.";

        Assert.True(card.ApplyTerraform(new GateDecision(false, reason)));

        Assert.False(card.IsAvailable);
        Assert.Equal(reason, card.UnavailableReason);
        Assert.Equal(reason, card.TileBlurb);
        Assert.Equal(WizardGroups.Unavailable, card.Group);
        Assert.Contains("not available on this machine", card.AutomationName, StringComparison.Ordinal);
        Assert.Contains(reason, card.TileToolTip, StringComparison.Ordinal);
        Assert.False(card.ShowTileBadge);

        // The same answer again is nothing to regroup for.
        Assert.False(card.ApplyTerraform(new GateDecision(false, reason)));

        Assert.True(card.ApplyTerraform(GateDecision.Open));
        Assert.True(card.IsAvailable);
        Assert.Equal(string.Empty, card.UnavailableReason);
        Assert.Equal(SplunkDashboards.Description, card.TileBlurb);
        Assert.Equal(WizardGroups.Observability, card.Group);
        Assert.Equal("Configure Splunk dashboards", card.LaunchAutomationName);
    }

    [Fact]
    public void The_card_says_which_command_it_runs_and_a_search_for_terraform_finds_it()
    {
        var card = new WizardCardViewModel(SplunkDashboards.Stub());

        Assert.Equal("defenseclaw setup splunk dashboards", card.CommandHint);
        Assert.Equal(SplunkDashboards.Title, card.Title);
        Assert.True(card.Matches("terraform"));
        Assert.True(card.Matches("dashboards"));
        Assert.True(card.Matches("splunk"));
        Assert.Equal(WizardGroups.Observability, card.Group);
    }

    [Fact]
    public void A_definition_that_lands_later_neither_forgets_the_gate_nor_overrides_a_reason_the_policy_gives()
    {
        var card = new WizardCardViewModel(SplunkDashboards.Stub());
        _ = card.ApplyTerraform(new GateDecision(false, "Terraform is not installed."));

        // Phase two of the catalog replaces the definition wholesale.
        card.Apply(Def(SplunkDashboards.Target, WizardGroups.Observability, description: "richer"));
        Assert.False(card.IsAvailable);
        Assert.Equal("Terraform is not installed.", card.UnavailableReason);

        // A CLI that itself calls it unsupported is not overruled by Terraform being fine.
        _ = card.ApplyTerraform(GateDecision.Open);
        card.Apply(Def(SplunkDashboards.Target, WizardGroups.Observability, PlatformStatus.Unsupported));
        Assert.False(card.IsAvailable);
        Assert.Contains("unsupported on Windows", card.UnavailableReason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cli_without_the_command_keeps_the_card_disabled_with_its_own_reason_whatever_terraform_says()
    {
        var card = new WizardCardViewModel(SplunkDashboards.Stub());
        _ = card.ApplyTerraform(GateDecision.Open);
        Assert.True(card.IsAvailable);

        card.Apply(SplunkDashboards.Missing());

        Assert.False(card.IsAvailable);
        Assert.Equal(SplunkDashboards.Missing().UnavailableReason, card.UnavailableReason);
        Assert.Equal(WizardGroups.Unavailable, card.Group);

        // "Install Terraform" would be the wrong advice for a CLI that cannot run it anyway.
        _ = card.ApplyTerraform(new GateDecision(false, "Terraform was not found."));
        Assert.Equal(SplunkDashboards.Missing().UnavailableReason, card.UnavailableReason);

        // And a CLI that has it again (a re-read after an upgrade) gives the card back to the probe.
        card.Apply(Def(SplunkDashboards.Target, WizardGroups.Observability));
        Assert.Equal("Terraform was not found.", card.UnavailableReason);
    }

    [Fact]
    public void Every_other_card_ignores_the_terraform_gate_and_this_card_ignores_the_docker_gate()
    {
        var connector = new WizardCardViewModel(Def("claude-code", WizardGroups.Connectors, PlatformStatus.Certified));
        var splunk = new WizardCardViewModel(Def("splunk", WizardGroups.Observability));
        var stack = new WizardCardViewModel(Def("local-observability", WizardGroups.Observability));
        var dashboards = new WizardCardViewModel(SplunkDashboards.Stub());

        foreach (var other in new[] { connector, splunk, stack })
        {
            Assert.False(other.ApplyTerraform(new GateDecision(false, "Terraform is not installed.")), other.Target);
            Assert.True(other.IsAvailable, other.Target);
        }

        Assert.False(dashboards.ApplyDocker(new GateDecision(false, "Docker is not running.")));
        Assert.True(dashboards.IsAvailable);
        Assert.Equal(WizardGroups.Connectors, connector.Group);
    }

    // ------------------------------------------------------------------ the hub

    private static readonly WizardDefinition[] Cards =
    {
        Def("claude-code", WizardGroups.Connectors, PlatformStatus.Certified, "Configure DefenseClaw hooks for Claude Code."),
        Def("splunk", WizardGroups.Observability, description: "Configure Splunk integration for DefenseClaw."),
        Def("webhook", WizardGroups.Observability, description: "Add a webhook."),
    };

    private sealed class Hub : IDisposable
    {
        private readonly TempDirectory _temp = new();
        private readonly AppServices _services;

        /// <param name="installation">The installation the composition starts with, made from the scratch folder; null is the usual writable one.</param>
        public Hub(WizardDefinition[]? cards = null, DefenseClawPaths? paths = null, Func<string, InstallationContext>? installation = null)
        {
            Probe = new ManualTerraformProbe();
            _services = AppServices.CreateIsolated(
                paths ?? TestServices.IsolatedPaths(_temp.Path, installation?.Invoke(_temp.Path)),
                claudeSettingsPath: _temp.File("claude-settings.json"),
                terraformProbe: Probe);
            ViewModel = UiThread.Run(() =>
            {
                var vm = new SetupPanelViewModel(_services);
                vm.ShowCards(cards ?? Cards);
                return vm;
            });
        }

        public ManualTerraformProbe Probe { get; }

        public SetupPanelViewModel ViewModel { get; }

        public AppServices Services => _services;

        /// <summary>The scratch folder the composition treats as its data directory.</summary>
        public string Folder => _temp.Path;

        public WizardCardViewModel? Dashboards => ViewModel.Groups.SelectMany(g => g.Cards).SingleOrDefault(c => c.Target == SplunkDashboards.Target);

        public string GroupOfDashboards => ViewModel.Groups.Single(g => g.Cards.Contains(Dashboards!)).Name;

        public void Dispose()
        {
            UiThread.Run(() => ViewModel.SetActive(false));
            _services.Dispose();
            _temp.Dispose();
        }
    }

    [Fact]
    public void The_dashboards_card_is_there_when_the_catalog_has_the_splunk_card_it_hangs_off_and_not_otherwise()
    {
        using var with = new Hub();
        UiThread.Run(() =>
        {
            Assert.NotNull(with.Dashboards);
            Assert.Equal(SplunkDashboards.Title, with.Dashboards!.Title);
            Assert.Contains(with.ViewModel.Groups.SelectMany(g => g.Cards), c => c.Target == "splunk");
        });

        using var without = new Hub(new[] { Cards[0], Cards[2] });
        UiThread.Run(() => Assert.Null(without.Dashboards));
    }

    [Fact]
    public void Before_terraform_has_answered_the_card_is_not_available_and_says_it_is_checking()
    {
        using var hub = new Hub();

        UiThread.Run(() =>
        {
            Assert.False(hub.Dashboards!.IsAvailable);
            Assert.Equal(TerraformAvailability.CheckingReason, hub.Dashboards.UnavailableReason);
            Assert.Equal(WizardGroups.Unavailable, hub.GroupOfDashboards);

            // Everything else on the page is where it was, the Splunk card included.
            Assert.True(hub.ViewModel.Groups.SelectMany(g => g.Cards).Single(c => c.Target == "claude-code").IsAvailable);
            Assert.True(hub.ViewModel.Groups.SelectMany(g => g.Cards).Single(c => c.Target == "splunk").IsAvailable);
        });
        Assert.Equal(0, hub.Probe.Calls);
    }

    [Fact]
    public async Task Coming_on_screen_looks_at_terraform_once_and_a_yes_moves_the_card_into_observability()
    {
        using var hub = new Hub();

        UiThread.Run(() => hub.ViewModel.SetActive(true));
        await hub.Probe.WaitForCallsAsync(1);
        hub.Probe.Answer(Ready);

        UiThread.WaitFor(() => hub.Dashboards!.IsAvailable, "the probe's yes");
        UiThread.Run(() =>
        {
            Assert.Equal(WizardGroups.Observability, hub.GroupOfDashboards);
            Assert.Equal(string.Empty, hub.Dashboards!.UnavailableReason);
            Assert.Equal(SplunkDashboards.Description, hub.Dashboards.TileBlurb);
        });
        Assert.Equal(1, hub.Probe.Calls);
    }

    [Theory]
    [InlineData(TerraformState.NotInstalled, "Terraform was not found on this machine's PATH.")]
    [InlineData(TerraformState.TooOld, "Terraform 1.2.0 at C:\\synthetic\\bin\\terraform.exe is older than the 1.5.0 that the dashboards module requires.")]
    [InlineData(TerraformState.NotWorking, "Terraform was found at C:\\synthetic\\bin\\terraform.exe, but \"terraform version -json\" failed (exit code 1). The dashboards need Terraform 1.5.0 or later.")]
    public async Task A_no_leaves_the_card_in_not_available_with_the_probes_own_reason_and_how_to_put_it_right(TerraformState state, string summary)
    {
        using var hub = new Hub();

        UiThread.Run(() => hub.ViewModel.SetActive(true));
        await hub.Probe.WaitForCallsAsync(1);
        hub.Probe.Answer(new TerraformStatus(state, summary, string.Empty, string.Empty));

        UiThread.WaitFor(() => hub.Dashboards!.UnavailableReason.StartsWith(summary, StringComparison.Ordinal), "the probe's no");
        UiThread.Run(() =>
        {
            Assert.False(hub.Dashboards!.IsAvailable);
            Assert.Equal(WizardGroups.Unavailable, hub.GroupOfDashboards);
            Assert.Equal(hub.Dashboards.UnavailableReason, hub.Dashboards.TileBlurb);
            Assert.Contains("TERRAFORM_BIN", hub.Dashboards.UnavailableReason, StringComparison.Ordinal);
            Assert.Contains("Refresh", hub.Dashboards.UnavailableReason, StringComparison.Ordinal);
            Assert.Contains("not available on this machine", hub.Dashboards.AutomationName, StringComparison.Ordinal);

            // A card that cannot be opened is not launched.
            hub.ViewModel.LaunchCommand.Execute(hub.Dashboards);
            Assert.False(hub.Dashboards.IsOpening);
        });
    }

    [Fact]
    public async Task A_look_that_could_not_run_leaves_the_card_launchable_because_the_cli_runs_terraform_itself()
    {
        using var hub = new Hub();

        UiThread.Run(() => hub.ViewModel.SetActive(true));
        await hub.Probe.WaitForCallsAsync(1);
        hub.Probe.Answer(new TerraformStatus(TerraformState.Unknown, "Terraform could not be checked from here (x).", string.Empty, string.Empty));

        UiThread.WaitFor(() => hub.Dashboards!.IsAvailable, "fail open");
        UiThread.Run(() => Assert.Equal(WizardGroups.Observability, hub.GroupOfDashboards));
    }

    [Fact]
    public async Task Refresh_looks_again_and_the_card_follows_terraform_in_both_directions()
    {
        using var hub = new Hub();
        UiThread.Run(() => hub.ViewModel.SetActive(true));
        await hub.Probe.WaitForCallsAsync(1);
        hub.Probe.Answer(NoTerraform);
        UiThread.WaitFor(() => !hub.Dashboards!.IsAvailable && !hub.Dashboards.UnavailableReason.Contains("Checking", StringComparison.Ordinal), "the first no");

        // "I installed Terraform" - the page's Refresh, not a timer.
        UiThread.Run(() => hub.ViewModel.RefreshCommand.Execute(null));
        await hub.Probe.WaitForCallsAsync(2);
        hub.Probe.Answer(Ready);
        UiThread.WaitFor(() => hub.Dashboards!.IsAvailable, "the yes after Refresh");
        UiThread.Run(() => Assert.Equal(WizardGroups.Observability, hub.GroupOfDashboards));

        // ... and it goes again.
        UiThread.WaitFor(() => !hub.ViewModel.RefreshCommand.IsRunning, "the first Refresh to end");
        UiThread.Run(() => hub.ViewModel.RefreshCommand.Execute(null));
        await hub.Probe.WaitForCallsAsync(3);
        hub.Probe.Answer(NoTerraform);
        UiThread.WaitFor(() => !hub.Dashboards!.IsAvailable, "the no after the second Refresh");
        UiThread.Run(() => Assert.Equal(WizardGroups.Unavailable, hub.GroupOfDashboards));
    }

    [Fact]
    public async Task Refresh_forgets_where_the_path_lookups_found_things_so_an_install_made_since_is_seen()
    {
        var exists = false;
        using var temp = new TempDirectory();
        var paths = new DefenseClawPaths(
            dataDirectory: temp.Path,
            binDirectory: Path.Combine(temp.Path, "no-such-bin"),
            searchPath: new[] { @"C:\synthetic\tools" },
            fileExists: path => exists && string.Equals(path, @"C:\synthetic\tools\terraform.exe", StringComparison.OrdinalIgnoreCase));
        using var hub = new Hub(paths: paths);

        // The app looked once, before Terraform was installed: "not found" is remembered.
        Assert.Null(await paths.FindExecutableAsync("terraform"));
        exists = true;
        Assert.Null(await paths.FindExecutableAsync("terraform"));

        UiThread.Run(() =>
        {
            hub.ViewModel.SetActive(true);
            hub.ViewModel.RefreshCommand.Execute(null);
        });
        await hub.Probe.WaitForCallsAsync(1);
        hub.Probe.Answer(Ready);
        UiThread.WaitFor(() => !hub.ViewModel.RefreshCommand.IsRunning, "Refresh to end");

        Assert.Equal(@"C:\synthetic\tools\terraform.exe", await paths.FindExecutableAsync("terraform"));
    }

    [Fact]
    public async Task A_hub_that_is_off_screen_listens_to_nothing_and_catches_up_when_it_comes_back()
    {
        using var hub = new Hub();
        UiThread.Run(() => hub.ViewModel.SetActive(true));
        await hub.Probe.WaitForCallsAsync(1);

        // Coming on screen asked Docker as well; its fixed answer comes back at once and posts a callback to the UI thread. Let that one run
        // now, while the hub is on screen, so it cannot land after the hub has gone and read the Terraform answer this test is about to give.
        await hub.Services.LocalStack.EnsureFreshAsync();
        UiThread.Run(UiThread.Settle);

        UiThread.Run(() => hub.ViewModel.SetActive(false));

        // The answer lands while the hub is away: nothing is rebuilt for nobody.
        hub.Probe.Answer(Ready);
        await hub.Services.Terraform.EnsureFreshAsync();
        Assert.True(hub.Services.Terraform.Decision.IsAvailable);
        UiThread.Run(() => Assert.False(hub.Dashboards!.IsAvailable));

        // Back on screen, the one catch-up pass puts the card where Terraform says it belongs - with no new look, the answer being fresh.
        UiThread.Run(() => hub.ViewModel.SetActive(true));
        UiThread.Run(() =>
        {
            Assert.True(hub.Dashboards!.IsAvailable);
            Assert.Equal(WizardGroups.Observability, hub.GroupOfDashboards);
        });
        Assert.Equal(1, hub.Probe.Calls);
    }

    [Fact]
    public async Task Cards_built_after_the_answer_started_from_it_instead_of_flashing_checking()
    {
        using var hub = new Hub();
        var look = hub.Services.Terraform.RefreshAsync();
        await hub.Probe.WaitForCallsAsync(1);
        hub.Probe.Answer(NoTerraform);
        await look;

        // What the catalog's re-read does: every card is built again. After a no it starts closed with the reason - not open, and not "Checking".
        UiThread.Run(() =>
        {
            hub.ViewModel.ShowCards(Cards);

            Assert.False(hub.Dashboards!.IsAvailable);
            Assert.StartsWith(NoTerraform.Summary, hub.Dashboards.UnavailableReason, StringComparison.Ordinal);
            Assert.DoesNotContain("Checking", hub.Dashboards.UnavailableReason, StringComparison.Ordinal);
            Assert.Equal(WizardGroups.Unavailable, hub.GroupOfDashboards);
        });

        // After a yes it starts open.
        var again = hub.Services.Terraform.RefreshAsync();
        await hub.Probe.WaitForCallsAsync(2);
        hub.Probe.Answer(Ready);
        await again;

        UiThread.Run(() =>
        {
            hub.ViewModel.ShowCards(Cards);

            Assert.True(hub.Dashboards!.IsAvailable);
            Assert.Equal(WizardGroups.Observability, hub.GroupOfDashboards);
        });
    }

    [Fact]
    public void The_hub_listens_to_the_terraform_look_only_while_it_is_on_screen()
    {
        using var hub = new Hub();

        UiThread.Run(() =>
        {
            var before = Subscribers(hub.Services.Terraform, "Changed");
            hub.ViewModel.SetActive(true);
            Assert.Equal(before + 1, Subscribers(hub.Services.Terraform, "Changed"));

            hub.ViewModel.SetActive(false);
            Assert.Equal(before, Subscribers(hub.Services.Terraform, "Changed"));
        });
    }

    // ------------------------------------------------------------------ a read-only installation (CUST-308)

    private const string TerraformMissing =
        "Terraform was not found on this machine's PATH. The Splunk dashboards are created by Terraform 1.5.0 or later; check again.";

    [Fact]
    public void On_a_read_only_installation_the_tile_stays_in_its_group_is_off_and_says_the_installations_sentence()
    {
        var card = new WizardCardViewModel(SplunkDashboards.Stub(), () => TestInstallations.ManagedReason);
        _ = card.ApplyTerraform(GateDecision.Open);
        var raised = new List<string?>();
        ((INotifyPropertyChanged)card).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        // Terraform is fine: the platform and the CLI offer the wizard, so the card keeps its place - and cannot be opened.
        Assert.True(card.IsAvailable);
        Assert.False(card.CanLaunch);
        Assert.Equal(WizardGroups.Observability, card.Group);
        Assert.Equal(TestInstallations.ManagedReason, card.InstallationBlockedReason);
        Assert.StartsWith(card.CommandHint, card.TileToolTip, StringComparison.Ordinal);
        Assert.EndsWith(TestInstallations.ManagedReason, card.TileToolTip, StringComparison.Ordinal);
        Assert.Equal(SplunkDashboards.Description, card.TileBlurb);

        card.RefreshInstallation();
        Assert.Contains(nameof(WizardCardViewModel.CanLaunch), raised);
        Assert.Contains(nameof(WizardCardViewModel.TileToolTip), raised);
    }

    [Fact]
    public void The_installations_sentence_comes_before_terraforms_and_the_tooltip_says_only_one_of_them()
    {
        var closed = new GateDecision(false, TerraformMissing);
        var readOnly = new WizardCardViewModel(SplunkDashboards.Stub(), () => TestInstallations.ManagedReason);
        var writable = new WizardCardViewModel(SplunkDashboards.Stub(), () => null);
        _ = readOnly.ApplyTerraform(closed);
        _ = writable.ApplyTerraform(closed);

        // Terraform is missing on both, and both sit in "not available" for it. Installing Terraform would not make the wizard runnable on the
        // read-only one, so its tile does not send the operator to do that.
        Assert.Equal(WizardGroups.Unavailable, readOnly.Group);
        Assert.False(readOnly.CanLaunch);
        Assert.EndsWith(TestInstallations.ManagedReason, readOnly.TileToolTip, StringComparison.Ordinal);
        Assert.DoesNotContain(TerraformMissing, readOnly.TileToolTip, StringComparison.Ordinal);

        // The same card on a writable installation says Terraform's and only that.
        Assert.Equal(WizardGroups.Unavailable, writable.Group);
        Assert.False(writable.CanLaunch);
        Assert.EndsWith(TerraformMissing, writable.TileToolTip, StringComparison.Ordinal);
        Assert.DoesNotContain(TestInstallations.ManagedReason, writable.TileToolTip, StringComparison.Ordinal);
    }

    [Fact]
    public void Before_terraform_has_answered_the_installations_sentence_is_still_the_one_on_the_tooltip()
    {
        var card = new WizardCardViewModel(SplunkDashboards.Stub(), () => TestInstallations.ManagedReason);
        _ = card.ApplyTerraform(new GateDecision(false, TerraformAvailability.CheckingReason));

        Assert.False(card.IsAvailable);
        Assert.EndsWith(TestInstallations.ManagedReason, card.TileToolTip, StringComparison.Ordinal);
        Assert.DoesNotContain("Checking", card.TileToolTip, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cli_without_the_command_says_so_not_the_installation_because_nothing_about_the_installation_would_help()
    {
        var card = new WizardCardViewModel(SplunkDashboards.Missing(), () => TestInstallations.ManagedReason);

        Assert.False(card.IsAvailable);
        Assert.False(card.CanLaunch);
        Assert.EndsWith(SplunkDashboards.Missing().UnavailableReason, card.TileToolTip, StringComparison.Ordinal);
        Assert.DoesNotContain(TestInstallations.ManagedReason, card.TileToolTip, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_hub_launches_nothing_from_the_dashboards_tile_on_a_read_only_installation_even_with_terraform_there()
    {
        using var hub = new Hub(installation: TestInstallations.ManagedAt);

        UiThread.Run(() => hub.ViewModel.SetActive(true));
        await hub.Probe.WaitForCallsAsync(1);
        hub.Probe.Answer(Ready);
        UiThread.WaitFor(() => hub.Dashboards!.IsAvailable, "the probe's yes");

        // Pressing the tile anyway ends at once: no window opens and nothing runs.
        await UiThread.Run(() => hub.ViewModel.LaunchCommand.ExecuteAsync(hub.Dashboards));

        UiThread.Run(() =>
        {
            Assert.False(hub.Dashboards!.CanLaunch);
            Assert.False(hub.Dashboards.IsOpening);
            Assert.Equal(TestInstallations.ManagedReason, hub.Dashboards.InstallationBlockedReason);
            Assert.EndsWith(TestInstallations.ManagedReason, hub.Dashboards.TileToolTip, StringComparison.Ordinal);
            Assert.Equal(WizardGroups.Observability, hub.GroupOfDashboards);
            Assert.True(hub.ViewModel.HasInstallationBlock);
        });
        Assert.DoesNotContain(hub.Services.Cli.Activity, run => run.Argv.Contains("dashboards"));
    }

    [Fact]
    public async Task The_dashboards_tile_follows_the_installation_while_the_page_is_open()
    {
        using var hub = new Hub();
        UiThread.Run(() => hub.ViewModel.SetActive(true));
        await hub.Probe.WaitForCallsAsync(1);
        hub.Probe.Answer(Ready);
        UiThread.WaitFor(() => hub.Dashboards!.IsAvailable, "the probe's yes");

        UiThread.Run(() =>
        {
            var card = hub.Dashboards!;
            var raised = new List<string?>();
            ((INotifyPropertyChanged)card).PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            Assert.True(card.CanLaunch);

            // config.yaml is edited to managed while the page is open.
            hub.Services.Installation.Replace(TestInstallations.ManagedAt(hub.Folder));

            Assert.Contains(nameof(WizardCardViewModel.CanLaunch), raised);
            Assert.False(card.CanLaunch);
            Assert.EndsWith(TestInstallations.ManagedReason, card.TileToolTip, StringComparison.Ordinal);

            // ... and fixed again.
            hub.Services.Installation.Replace(TestInstallations.UserDefault());

            Assert.True(card.CanLaunch);
            Assert.DoesNotContain(TestInstallations.ManagedReason, card.TileToolTip, StringComparison.Ordinal);
        });
    }

    private static int Subscribers(object owner, string eventName)
    {
        var field = owner.GetType().GetField(eventName, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?? throw new InvalidOperationException($"{owner.GetType().Name}.{eventName} is not a field-like event any more; update this helper.");
        return (field.GetValue(owner) as Delegate)?.GetInvocationList().Length ?? 0;
    }
}
