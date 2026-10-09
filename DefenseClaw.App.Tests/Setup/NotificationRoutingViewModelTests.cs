using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Paths;
using DefenseClaw.Core.Setup;

namespace DefenseClaw.App.Tests.Setup;

/// <summary>
/// The notification routing dialog (CUST-271), over a scratch composition and a script for the CLI: it seeds six switches from config.yaml, turns
/// the ones that moved into one reviewed plan of <c>setup notifications-set</c> commands, says "Nothing to apply" when none did, and keeps the
/// gateway's notifications apart from the tray's alerts in every sentence. No process starts; the help screen is the installed 0.8.10 CLI's own
/// (<c>Fixtures/runtime-0.8.10/setup-notifications-set.txt</c>, captured with <c>--help</c>).
/// </summary>
public sealed class NotificationRoutingViewModelTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly List<AppServices> _services = new();

    public void Dispose()
    {
        foreach (var services in _services)
        {
            services.Dispose();
        }

        _temp.Dispose();
    }

    private static string HelpText(string name = "setup-notifications-set.txt") =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "runtime-0.8.10", name));

    private static Func<CancellationToken, Task<HelpProbeResult>> Help(string? text = null, string? error = null) =>
        _ => Task.FromResult(new HelpProbeResult(text ?? HelpText(), error));

    private static CliInvocation Done(IReadOnlyList<string> argv, int exit)
    {
        var invocation = InvocationFactory.Create(retainFullOutput: true, argv.ToArray());
        InvocationFactory.Finish(invocation, exit);
        return invocation;
    }

    private sealed class Script
    {
        public List<string> Ran { get; } = new();

        /// <summary>Exit code by position (0 when not listed).</summary>
        public Dictionary<int, int> Exit { get; } = new();

        /// <summary>What the "CLI" does to config.yaml when a step runs (by position), so a test sees the dialog read it back.</summary>
        public Dictionary<int, Action> Effect { get; } = new();

        public Task<CliInvocation> Step(string executable, IReadOnlyList<string> argv, CliRunOptions? options)
        {
            var index = Ran.Count;
            Ran.Add(executable + " " + string.Join(' ', argv));
            var exit = Exit.GetValueOrDefault(index);
            if (exit == 0 && Effect.TryGetValue(index, out var effect))
            {
                effect();
            }

            return Task.FromResult(Done(argv, exit));
        }
    }

    private (NotificationRoutingViewModel Vm, AppServices Services, Script Cli) Dialog(
        string yaml = "",
        InstallationContext? installation = null,
        Func<CancellationToken, Task<HelpProbeResult>>? help = null)
    {
        var services = TestServices.Create(_temp, configYaml: yaml, installation: installation);
        _services.Add(services);
        var cli = new Script();
        var vm = new NotificationRoutingViewModel(services, help ?? Help());
        vm.Review.RunStep = cli.Step;
        return (vm, services, cli);
    }

    private static RoutingRow Row(NotificationRoutingViewModel vm, string slot) =>
        vm.CategoryRows.Concat(vm.SourceRows).Single(r => r.Slot.Id == slot);

    private static IEnumerable<string> Commands(NotificationRoutingViewModel vm) =>
        vm.Review.CommandReview!.Steps.Select(s => s.CommandText);

    // ---- seeded from config ----

    [Fact]
    public void Opening_seeds_the_six_switches_from_config_and_nothing_has_changed()
    {
        var (vm, _, cli) = Dialog("notifications:\n  block_would_block: true\n  hitl_approval: no\n  sources:\n    asset_policy: off\n");

        vm.Open();

        Assert.True(vm.IsOpen);
        Assert.Equal(
            new[] { "block_enforced", "block_would_block", "hitl_approval" },
            vm.CategoryRows.Select(r => r.Slot.Id));
        Assert.Equal(
            new[] { "sources.hook", "sources.guardrail", "sources.asset_policy" },
            vm.SourceRows.Select(r => r.Slot.Id));
        Assert.Equal(new[] { true, true, false }, vm.CategoryRows.Select(r => r.IsOn));
        Assert.Equal(new[] { true, true, false }, vm.SourceRows.Select(r => r.IsOn));
        Assert.All(vm.CategoryRows.Concat(vm.SourceRows), r => Assert.False(r.IsChanged));
        Assert.False(vm.HasChanges);
        Assert.Empty(cli.Ran);
    }

    [Fact]
    public void A_config_that_says_nothing_seeds_the_runtimes_defaults_and_the_master_switch_on()
    {
        var (vm, _, _) = Dialog(string.Empty);

        vm.Open();

        Assert.Equal(new[] { true, false, true }, vm.CategoryRows.Select(r => r.IsOn));
        Assert.All(vm.SourceRows, r => Assert.True(r.IsOn));
        Assert.True(vm.MasterOn);
        Assert.False(vm.MasterIsExplicit);
        Assert.Equal("Desktop notifications are on", vm.MasterText);
        Assert.Equal("config.yaml does not say, so this is the runtime's default on Windows.", vm.MasterDetail);
        Assert.True(vm.HasMasterDetail);
    }

    [Fact]
    public void The_master_switch_reads_off_when_config_says_so()
    {
        var (vm, _, _) = Dialog("notifications:\n  enabled: false\n");

        vm.Open();

        Assert.False(vm.MasterOn);
        Assert.True(vm.MasterIsExplicit);
        Assert.Equal("Desktop notifications are off", vm.MasterText);
        Assert.Equal(string.Empty, vm.MasterDetail);
        Assert.False(vm.HasMasterDetail);
        Assert.Equal("Turn on…", vm.MasterButtonText);
        Assert.True(vm.ShowMasterOffNote);
        Assert.Contains("none of the switches below raises one", vm.MasterOffNote, StringComparison.Ordinal);
    }

    [Fact]
    public void The_dialog_starts_from_the_file_not_from_the_copy_the_watcher_last_saw()
    {
        var (vm, _, _) = Dialog("notifications:\n  hitl_approval: true\n");
        File.WriteAllText(_temp.File("config.yaml"), "notifications:\n  hitl_approval: false\n");

        vm.Open();

        Assert.False(Row(vm, "hitl_approval").IsOn);
    }

    [Theory]
    [InlineData("notifications", true)]
    [InlineData("notifications-set", true)]
    [InlineData("guardrail", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void The_two_notification_tiles_open_the_dialog_and_no_other_does(string? target, bool opens) =>
        Assert.Equal(opens, NotificationRoutingViewModel.Handles(target));

    // ---- the acceptance: two switches, one reviewed run ----

    [Fact]
    public async Task Flipping_two_switches_is_one_reviewed_plan_of_two_commands_run_in_order()
    {
        var (vm, _, cli) = Dialog();
        vm.Open();

        Row(vm, "block_would_block").IsOn = true;
        Row(vm, "sources.hook").IsOn = false;

        Assert.True(vm.HasChanges);
        Assert.Equal("2 changes", vm.Summary);
        Assert.Equal(
            new[] { "Block (would-block / observe): off to on", "Source: hooks: on to off" },
            vm.ChangeLines);
        Assert.True(vm.ReviewChangesCommand.CanExecute(null));
        Assert.Null(vm.ReviewBlockedReason);

        vm.ReviewChangesCommand.Execute(null);

        // The review shows both commands together and nothing has run.
        Assert.True(vm.Review.IsOpen);
        Assert.True(vm.Review.IsConfirming);
        Assert.Empty(cli.Ran);
        var review = vm.Review.CommandReview!;
        Assert.Equal("Update notification routing?", review.Title);
        Assert.Equal(
            new[]
            {
                "defenseclaw setup notifications-set block_would_block on --no-restart",
                "defenseclaw setup notifications-set sources.hook off",
            },
            Commands(vm));
        Assert.Equal(new[] { 1, 2 }, review.Steps.Select(s => s.Number));
        Assert.True(review.RestartsGateway);
        Assert.Equal("Apply 2 changes", review.ConfirmLabel);

        await vm.Review.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(
            new[]
            {
                "defenseclaw setup notifications-set block_would_block on --no-restart",
                "defenseclaw setup notifications-set sources.hook off",
            },
            cli.Ran);
        Assert.Equal("Ok", vm.Review.ResultKey);
        Assert.StartsWith("Done. All 2 steps ran.", vm.Review.ResultText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task What_ran_is_read_back_from_config_so_the_switches_show_the_file_and_the_result_names_what_was_applied()
    {
        var (vm, _, cli) = Dialog();
        vm.Open();
        Row(vm, "block_would_block").IsOn = true;
        Row(vm, "sources.hook").IsOn = false;
        vm.ReviewChangesCommand.Execute(null);

        // The CLI writes config.yaml as each command runs.
        cli.Effect[0] = () => File.WriteAllText(_temp.File("config.yaml"), "notifications:\n  block_would_block: true\n");
        cli.Effect[1] = () => File.WriteAllText(_temp.File("config.yaml"), "notifications:\n  block_would_block: true\n  sources:\n    hook: false\n");
        await vm.Review.ConfirmCommand.ExecuteAsync(null);

        Assert.False(vm.HasChanges); // the switches start again from what the file says now
        Assert.True(Row(vm, "block_would_block").IsOn);
        Assert.False(Row(vm, "sources.hook").IsOn);
        Assert.False(Row(vm, "sources.hook").IsChanged);
        Assert.Equal("Ok", vm.ResultKey);
        Assert.Equal("Applied: Block (would-block / observe): off to on; Source: hooks: on to off.", vm.ResultText);
        Assert.True(vm.HasResult);
        Assert.False(vm.ReviewChangesCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_failure_part_way_names_what_was_applied_what_was_not_and_the_switch_that_did_not_get_in_shows_as_it_is()
    {
        var (vm, _, cli) = Dialog();
        vm.Open();
        Row(vm, "block_would_block").IsOn = true;
        Row(vm, "hitl_approval").IsOn = false;
        Row(vm, "sources.guardrail").IsOn = false;
        vm.ReviewChangesCommand.Execute(null);

        cli.Exit[1] = 1; // the second command fails
        cli.Effect[0] = () => File.WriteAllText(_temp.File("config.yaml"), "notifications:\n  block_would_block: true\n");
        await vm.Review.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(2, cli.Ran.Count); // the third never started
        Assert.Equal("Bad", vm.ResultKey);
        Assert.StartsWith("Applied: Block (would-block / observe): off to on.", vm.ResultText, StringComparison.Ordinal);
        Assert.Contains(
            "Not applied: HITL approval: on to off; Source: guardrail: on to off. 1 of 3 steps succeeded. Step 2 failed (exit 1). Step 3 was not run.",
            vm.ResultText,
            StringComparison.Ordinal);

        // The file has only the first change: the toggles that were refused are back to what the file says.
        Assert.True(Row(vm, "block_would_block").IsOn);
        Assert.True(Row(vm, "hitl_approval").IsOn);
        Assert.True(Row(vm, "sources.guardrail").IsOn);
        Assert.False(vm.HasChanges);
    }

    // ---- nothing to apply ----

    [Fact]
    public void With_no_switch_moved_it_says_nothing_to_apply_and_review_is_off_with_that_as_its_reason()
    {
        var (vm, _, _) = Dialog();
        vm.Open();

        Assert.Equal("Nothing to apply", vm.Summary);
        Assert.False(vm.ReviewChangesCommand.CanExecute(null));
        Assert.Equal("Nothing to apply: no switch differs from config.yaml.", vm.ReviewBlockedReason);
        Assert.Equal(vm.ReviewBlockedReason, vm.ReviewTip);
        Assert.Null(vm.BuildPlan());
    }

    [Fact]
    public void A_switch_moved_and_moved_back_is_nothing_to_apply_again()
    {
        var (vm, _, _) = Dialog();
        vm.Open();

        Row(vm, "hitl_approval").IsOn = false;
        Assert.Equal("1 change", vm.Summary);
        Assert.True(vm.ReviewChangesCommand.CanExecute(null));
        Assert.Equal("was on", Row(vm, "hitl_approval").ChangeText);

        Row(vm, "hitl_approval").IsOn = true;
        Assert.Equal("Nothing to apply", vm.Summary);
        Assert.False(vm.ReviewChangesCommand.CanExecute(null));
        Assert.Equal(string.Empty, Row(vm, "hitl_approval").ChangeText);
    }

    // ---- one restart ----

    [Fact]
    public void Only_the_last_command_restarts_the_gateway_and_the_review_says_so_once()
    {
        var (vm, _, _) = Dialog();
        vm.Open();
        Row(vm, "block_would_block").IsOn = true;
        Row(vm, "hitl_approval").IsOn = false;
        Row(vm, "sources.guardrail").IsOn = false;

        var plan = vm.BuildPlan()!;

        Assert.Equal(
            new[]
            {
                "setup notifications-set block_would_block on --no-restart",
                "setup notifications-set hitl_approval off --no-restart",
                "setup notifications-set sources.guardrail off",
            },
            plan.Steps.Select(s => string.Join(' ', s.Argv)));
        Assert.True(plan.RestartsGateway);
        Assert.Contains("The gateway restarts once, after the last command", plan.Summary, StringComparison.Ordinal);
        Assert.Equal("Apply 3 changes", plan.PrimaryText);
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void Without_a_restart_every_command_says_no_restart_and_the_review_warns_it_will_not_take_effect()
    {
        var (vm, _, _) = Dialog();
        vm.Open();
        Row(vm, "block_would_block").IsOn = true;
        Row(vm, "hitl_approval").IsOn = false;
        vm.RestartAfter = false;

        var plan = vm.BuildPlan()!;

        Assert.All(plan.Steps, s => Assert.Equal("--no-restart", s.Argv[^1]));
        Assert.False(plan.RestartsGateway);
        Assert.Contains("not in effect until it next restarts", plan.Summary, StringComparison.Ordinal);
        Assert.Contains(plan.Warnings, w => w.Title == "Gateway not restarted");
        Assert.Contains("not in effect until it next restarts", vm.RestartNote, StringComparison.Ordinal);
    }

    [Fact]
    public void A_single_change_restarts_by_itself_so_it_carries_no_flag()
    {
        var (vm, _, _) = Dialog();
        vm.Open();
        Row(vm, "hitl_approval").IsOn = false;

        var plan = vm.BuildPlan()!;

        Assert.Equal("setup notifications-set hitl_approval off", string.Join(' ', Assert.Single(plan.Steps).Argv));
        Assert.Equal("Apply change", plan.PrimaryText);
        Assert.Contains("one setting", plan.Summary, StringComparison.Ordinal);
    }

    // ---- the gateway's notifications are not the tray's alerts ----

    [Fact]
    public void The_dialog_the_review_and_the_master_switch_all_say_these_are_not_the_trays_alerts()
    {
        var (vm, _, _) = Dialog();
        vm.Open();
        Row(vm, "hitl_approval").IsOn = false;

        Assert.Contains("raised by the gateway", vm.ScopeNote, StringComparison.Ordinal);
        Assert.Contains("The alerts this app shows from the tray (Settings, Notifications) are a separate setting", vm.ScopeNote, StringComparison.Ordinal);
        Assert.Contains("the same block can raise both", vm.ScopeNote, StringComparison.Ordinal);
        Assert.Contains("the alerts this app shows from the tray are a separate setting and do not change", vm.BuildPlan()!.Summary, StringComparison.Ordinal);
        Assert.Contains("The alerts this app shows from the tray are a separate setting (Settings, Notifications) and do not change.", NotificationSwitch.Explanation(turnOn: false), StringComparison.Ordinal);
    }

    // ---- the master switch is the Overview's action ----

    [Fact]
    public void The_master_switch_opens_the_overviews_own_review_with_its_question_words_and_argv()
    {
        var (vm, _, cli) = Dialog("notifications:\n  enabled: true\n");
        vm.Open();

        Assert.Equal("Turn off…", vm.MasterButtonText);
        Assert.True(vm.ToggleMasterCommand.CanExecute(null));
        vm.ToggleMasterCommand.Execute(null);

        var review = vm.Review.CommandReview!;
        Assert.Equal("Turn desktop notifications off?", review.Title);
        Assert.Equal(NotificationSwitch.Explanation(turnOn: false), review.Summary);
        Assert.Same(OverviewPanelViewModel.NotificationsOffArgv, NotificationSwitch.OffArgv);
        Assert.Equal("defenseclaw setup notifications off", Assert.Single(review.Steps).CommandText);
        Assert.True(review.RestartsGateway);
        Assert.Equal("Turn off", review.ConfirmLabel);
        Assert.Empty(cli.Ran);
    }

    [Fact]
    public void Turning_it_on_when_it_is_off_is_the_same_action_the_other_way()
    {
        var (vm, _, _) = Dialog("notifications:\n  enabled: false\n");
        vm.Open();

        vm.ToggleMasterCommand.Execute(null);

        var review = vm.Review.CommandReview!;
        Assert.Equal("Turn desktop notifications on?", review.Title);
        Assert.Same(OverviewPanelViewModel.NotificationsOnArgv, NotificationSwitch.OnArgv);
        Assert.Equal("defenseclaw setup notifications on", Assert.Single(review.Steps).CommandText);
        Assert.Equal("Turn on", review.ConfirmLabel);
    }

    [Fact]
    public async Task After_the_master_switch_ran_the_new_state_shows_and_the_switches_the_operator_moved_stay_moved()
    {
        var (vm, _, cli) = Dialog("notifications:\n  enabled: true\n");
        vm.Open();
        Row(vm, "block_would_block").IsOn = true;

        vm.ToggleMasterCommand.Execute(null);
        cli.Effect[0] = () => File.WriteAllText(_temp.File("config.yaml"), "notifications:\n  enabled: false\n");
        await vm.Review.ConfirmCommand.ExecuteAsync(null);

        Assert.False(vm.MasterOn);
        Assert.Equal("Desktop notifications are now off.", vm.ResultText);
        Assert.Equal("Ok", vm.ResultKey);
        Assert.True(Row(vm, "block_would_block").IsOn);
        Assert.True(Row(vm, "block_would_block").IsChanged); // still a change, measured against the file as it is now
        Assert.Equal("1 change", vm.Summary);
        Assert.False(vm.IsStale); // the dialog noted the file again after its own change
    }

    [Fact]
    public void With_the_master_switch_off_the_plan_warns_that_nothing_will_be_raised_yet()
    {
        var (vm, _, _) = Dialog("notifications:\n  enabled: false\n");
        vm.Open();
        Row(vm, "hitl_approval").IsOn = false;

        var plan = vm.BuildPlan()!;

        var warning = Assert.Single(plan.Warnings);
        Assert.Equal("Desktop notifications are off", warning.Title);
        Assert.Contains("nothing is raised until you turn it on", warning.Message, StringComparison.Ordinal);
    }

    // ---- the installation first ----

    [Fact]
    public void On_a_read_only_installation_every_control_that_changes_something_is_off_with_the_installations_sentence()
    {
        var (vm, _, _) = Dialog(installation: TestInstallations.Managed(_temp.Path));
        vm.Open();

        Assert.Equal(TestInstallations.ManagedReason, vm.InstallationBlockedReason);
        Assert.True(vm.HasInstallationBlock);
        Assert.False(vm.CanEdit);
        Assert.False(vm.ToggleMasterCommand.CanExecute(null));
        Assert.Equal(TestInstallations.ManagedReason, vm.MasterTip);
        Assert.Equal(TestInstallations.ManagedReason, vm.ReviewBlockedReason); // before "nothing to apply"
        Assert.False(vm.ReviewChangesCommand.CanExecute(null));
    }

    [Fact]
    public void The_installation_also_comes_before_a_stale_reading()
    {
        var (vm, services, _) = Dialog();
        vm.Open();
        Row(vm, "hitl_approval").IsOn = false;
        File.WriteAllText(_temp.File("config.yaml"), "notifications:\n  hitl_approval: false\n");
        Assert.True(vm.Trust.CheckConfig());
        services.Installation.Replace(TestInstallations.ManagedAt(_temp.Path));

        Assert.Equal(TestInstallations.ManagedReason, vm.ReviewBlockedReason);
    }

    [Fact]
    public void A_dialog_open_when_the_installation_turns_read_only_turns_its_controls_off()
    {
        var (vm, services, _) = Dialog();
        vm.Open();
        Row(vm, "hitl_approval").IsOn = false;
        Assert.True(vm.ReviewChangesCommand.CanExecute(null));

        services.Installation.Replace(TestInstallations.ManagedAt(_temp.Path));

        Assert.False(vm.ReviewChangesCommand.CanExecute(null));
        Assert.False(vm.CanEdit);
        Assert.True(vm.HasInstallationBlock);
    }

    // ---- the settings' age and config.yaml changing under the dialog (CUST-312) ----

    [Fact]
    public void When_config_changes_after_the_settings_were_read_the_dialog_says_so_and_review_is_off()
    {
        var (vm, _, _) = Dialog();
        vm.Open();
        Row(vm, "hitl_approval").IsOn = false;
        Assert.True(vm.ReviewChangesCommand.CanExecute(null));

        File.WriteAllText(_temp.File("config.yaml"), "notifications:\n  block_would_block: true\n  # edited elsewhere\n");
        Assert.True(vm.Trust.CheckConfig());

        Assert.True(vm.IsStale);
        Assert.Equal(CatalogTrust.ConfigChangedReason("these settings were read"), vm.StaleReason);
        Assert.Equal(vm.StaleReason, vm.ReviewBlockedReason);
        Assert.False(vm.ReviewChangesCommand.CanExecute(null));
        Assert.False(vm.ToggleMasterCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_review_confirmed_after_config_changed_runs_nothing_and_records_one_refusal_in_activity()
    {
        var (vm, services, cli) = Dialog();
        vm.Open();
        Row(vm, "block_would_block").IsOn = true;
        Row(vm, "sources.hook").IsOn = false;
        vm.ReviewChangesCommand.Execute(null);
        Assert.True(vm.Review.IsConfirming);

        // The file is edited while the question is on screen: the look at the files is made again at Confirm.
        File.WriteAllText(_temp.File("config.yaml"), "notifications:\n  hitl_approval: false\n  # edited while the review was open\n");
        await vm.Review.ConfirmCommand.ExecuteAsync(null);

        Assert.Empty(cli.Ran);
        Assert.StartsWith(
            "Not run. " + CatalogTrust.ConfigChangedReason("these settings were read"),
            vm.Review.ResultText,
            StringComparison.Ordinal);
        var entry = Assert.Single(services.Cli.Activity);
        Assert.Equal(new[] { "setup", "notifications-set", "block_would_block", "on", "--no-restart" }, entry.Argv);
        Assert.StartsWith(CliRunner.RefusedPrefix, entry.FailureReason, StringComparison.Ordinal);
        Assert.True(vm.IsStale);
    }

    [Fact]
    public void Refresh_starts_the_switches_again_from_the_file_and_clears_the_stale_notice()
    {
        var (vm, _, _) = Dialog();
        vm.Open();
        Row(vm, "hitl_approval").IsOn = false;
        File.WriteAllText(_temp.File("config.yaml"), "notifications:\n  hitl_approval: false\n");
        Assert.True(vm.Trust.CheckConfig());
        Assert.True(vm.IsStale);

        vm.RefreshCommand.Execute(null);

        Assert.False(vm.IsStale);
        Assert.False(Row(vm, "hitl_approval").IsOn);
        Assert.False(Row(vm, "hitl_approval").IsChanged); // the file says it, so it is no change
        Assert.False(vm.HasChanges);
    }

    [Fact]
    public void A_config_that_cannot_be_read_leaves_the_toggles_unknown_and_everything_off()
    {
        var (vm, _, _) = Dialog("notifications: [unclosed\n");
        vm.Open();

        Assert.True(vm.HasConfigProblem);
        Assert.StartsWith("config.yaml could not be read (", vm.ConfigProblem, StringComparison.Ordinal);
        Assert.False(vm.CanEdit);
        Assert.Equal(vm.ConfigProblem, vm.ReviewBlockedReason);
        Assert.False(vm.ToggleMasterCommand.CanExecute(null));
    }

    // ---- the CLI's help is checked, not assumed ----

    [Fact]
    public void Every_slot_the_cli_lists_is_available_and_the_help_is_ready()
    {
        var (vm, _, _) = Dialog();
        vm.Open();

        Assert.True(vm.IsHelpReady);
        Assert.False(vm.IsChecking);
        Assert.False(vm.HasHelpProblem);
        Assert.All(vm.CategoryRows.Concat(vm.SourceRows), r => Assert.True(r.IsAvailable));
    }

    /// <summary>A CLI that predates the asset-policy source: its help does not list the slot.</summary>
    private static readonly string OlderHelp = """
        Usage: defenseclaw setup notifications-set [OPTIONS] {block_enforced|block_would_block|hitl_approval|sources.hook|
                                                   sources.guardrail} {on|off}

          Toggle a single notifications category or source.

        Options:
          --restart / --no-restart  Restart defenseclaw-gateway after the toggle.  [default: restart]
          --help                    Show this message and exit.
        """;

    [Fact]
    public void A_slot_the_installed_cli_does_not_list_is_off_to_the_touch_and_never_part_of_a_plan()
    {
        var (vm, _, _) = Dialog(help: Help(OlderHelp));
        vm.Open();

        var unlisted = Row(vm, "sources.asset_policy");
        Assert.False(unlisted.IsAvailable);
        Assert.True(unlisted.IsUnavailable);
        Assert.Contains("does not list this setting", unlisted.UnavailableText, StringComparison.Ordinal);
        Assert.True(Row(vm, "sources.guardrail").IsAvailable);
        Assert.True(Row(vm, "block_enforced").IsAvailable);

        unlisted.IsOn = false; // however it got there
        Assert.False(vm.HasChanges);
        Assert.False(unlisted.IsChanged);
    }

    [Fact]
    public void A_help_that_cannot_be_read_fails_closed_with_the_reason_and_refresh_tries_again()
    {
        var failing = true;
        var (vm, _, _) = Dialog(help: _ => Task.FromResult(failing
            ? new HelpProbeResult(string.Empty, "defenseclaw is not on PATH.")
            : new HelpProbeResult(HelpText(), null)));
        vm.Open();
        Row(vm, "hitl_approval").IsOn = false;

        Assert.True(vm.HasHelpProblem);
        Assert.False(vm.IsHelpReady);
        Assert.Contains("defenseclaw is not on PATH.", vm.HelpProblem, StringComparison.Ordinal);
        Assert.Equal(vm.HelpProblem, vm.ReviewBlockedReason);
        Assert.False(vm.ReviewChangesCommand.CanExecute(null));

        failing = false;
        vm.RefreshCommand.Execute(null);

        Assert.False(vm.HasHelpProblem);
        Assert.True(vm.IsHelpReady);
        Assert.False(vm.HasChanges); // refresh started the switches again from the file
        Row(vm, "hitl_approval").IsOn = false;
        Assert.True(vm.ReviewChangesCommand.CanExecute(null));
    }

    [Fact]
    public void A_help_that_lists_no_settings_is_a_problem_not_an_empty_dialog()
    {
        var (vm, _, _) = Dialog(help: Help("Usage: defenseclaw setup notifications-set [OPTIONS]\n\n  Nothing to choose.\n\nOptions:\n  --help  Show this message and exit.\n"));
        vm.Open();

        Assert.True(vm.HasHelpProblem);
        Assert.Contains("lists no settings to choose from", vm.HelpProblem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cli_whose_help_has_no_no_restart_gets_no_flag_and_the_review_says_each_command_restarts()
    {
        var withoutFlag = HelpText().Replace("--restart / --no-restart", "--restart", StringComparison.Ordinal);
        var (vm, _, _) = Dialog(help: Help(withoutFlag));
        vm.Open();
        Row(vm, "block_would_block").IsOn = true;
        Row(vm, "hitl_approval").IsOn = false;

        var plan = vm.BuildPlan()!;

        Assert.All(plan.Steps, s => Assert.DoesNotContain("--no-restart", s.Argv));
        Assert.Contains(plan.Warnings, w => w.Title == "One restart per command" && w.Message.Contains("restarts 2 times", StringComparison.Ordinal));
        Assert.Contains("each of them restarts the gateway", plan.Summary, StringComparison.Ordinal);
    }

    // ---- keys and states ----

    [Fact]
    public void Escape_closes_the_review_first_and_then_the_dialog_and_does_nothing_when_it_is_shut()
    {
        var (vm, _, _) = Dialog();
        Assert.False(vm.HandleEscape());

        vm.Open();
        Row(vm, "hitl_approval").IsOn = false;
        vm.ReviewChangesCommand.Execute(null);
        Assert.True(vm.Review.IsOpen);

        Assert.True(vm.HandleEscape());
        Assert.False(vm.Review.IsOpen);
        Assert.True(vm.IsOpen);

        Assert.True(vm.HandleEscape());
        Assert.False(vm.IsOpen);
    }

    [Fact]
    public void Closing_forgets_nothing_the_file_has_and_reopening_starts_from_the_file_again()
    {
        var (vm, _, _) = Dialog();
        vm.Open();
        Row(vm, "hitl_approval").IsOn = false;
        vm.CloseCommand.Execute(null);
        Assert.False(vm.IsOpen);

        vm.Open();

        Assert.True(Row(vm, "hitl_approval").IsOn);
        Assert.False(vm.HasChanges);
    }

    [Fact]
    public void Reading_the_review_blocks_a_second_review_and_the_master_switch()
    {
        var (vm, _, _) = Dialog();
        vm.Open();
        Row(vm, "hitl_approval").IsOn = false;
        vm.ReviewChangesCommand.Execute(null);

        Assert.Equal("Another command is running.", vm.ReviewBlockedReason);
        Assert.False(vm.ReviewChangesCommand.CanExecute(null));
        Assert.False(vm.ToggleMasterCommand.CanExecute(null));
    }

    [Fact]
    public void The_rows_have_names_a_screen_reader_can_use()
    {
        var (vm, _, _) = Dialog();
        vm.Open();
        Row(vm, "hitl_approval").IsOn = false;

        Assert.Equal(
            "HITL approval: Off, changed, was on. A human-in-the-loop approval prompt is waiting for an answer.",
            Row(vm, "hitl_approval").AutomationName);
        Assert.StartsWith("Block (enforced): On.", Row(vm, "block_enforced").AutomationName, StringComparison.Ordinal);
    }

    [Fact]
    public void A_dialog_that_is_closed_or_disposed_listens_to_nothing()
    {
        var (vm, services, _) = Dialog();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        // Never opened: the installation changing is not its business.
        services.Installation.Replace(TestInstallations.ManagedAt(_temp.Path));
        Assert.Empty(raised);

        services.Installation.Replace(TestInstallations.UserDefault());
        vm.Open();
        raised.Clear();
        vm.CloseCommand.Execute(null);
        raised.Clear();
        services.Installation.Replace(TestInstallations.ManagedAt(_temp.Path));
        Assert.Empty(raised);

        vm.Open();
        vm.Dispose();
        vm.Dispose();
        raised.Clear();
        services.Installation.Replace(TestInstallations.UserDefault());
        Assert.Empty(raised);
    }
}
