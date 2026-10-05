using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Install;

namespace DefenseClaw.App.Tests.Overview;

/// <summary>
/// Quick Actions (CUST-209): nothing runs by itself, and nothing that changes state runs without the shared review. Scan Skills and the gateway
/// actions open the in-panel review (the exact argv, its tier, a button that must be pressed); the Diagnostics commands are read-only by
/// <see cref="CommandTiers"/> and run at once, into Activity. The Mac's "Run gateway as administrator" is not offered.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class OverviewQuickActionsTests : IDisposable
{
    private readonly OverviewScene _scene = OverviewScene.Create(seedAudit: false, seedAgents: false);

    public void Dispose() => _scene.Dispose();

    private OverviewPanelViewModel Panel(bool running = true)
    {
        var vm = new OverviewPanelViewModel(_scene.Services);
        vm.Apply(OverviewScene.Snapshot(running: running));
        return vm;
    }

    // ---- Diagnostics: read-only, by the same classifier every surface uses ----

    [Fact]
    public void The_diagnostics_menu_is_the_macs_four_read_only_checks_with_their_exact_argv()
    {
        var commands = OverviewPanelViewModel.DiagnosticCommands;

        Assert.Equal(new[] { "Validate configuration", "Check credentials", "Gateway status", "Show provenance" }, commands.Select(c => c.Title).ToArray());
        Assert.Equal("defenseclaw config validate", commands[0].CommandText);
        Assert.Equal("defenseclaw keys check", commands[1].CommandText);
        Assert.Equal("defenseclaw-gateway status", commands[2].CommandText);
        Assert.Equal("defenseclaw-gateway provenance show", commands[3].CommandText);
        Assert.All(commands, c => Assert.False(string.IsNullOrWhiteSpace(c.Summary)));
    }

    [Fact]
    public void Every_diagnostic_is_read_only_so_it_may_run_without_a_review_and_the_state_changing_ones_are_not_among_them()
    {
        Assert.All(OverviewPanelViewModel.DiagnosticCommands, c => Assert.Equal(CommandTier.ReadOnly, CommandReview.ResolveTier(c.Argv)));

        // What the review surfaces are for: these are not read-only, so none of them may ever be a diagnostic.
        Assert.NotEqual(CommandTier.ReadOnly, CommandReview.ResolveTier(OverviewPanelViewModel.ScanSkillsArgv));
        foreach (var action in new[] { GatewayAction.Start, GatewayAction.Stop, GatewayAction.Restart })
        {
            Assert.NotEqual(CommandTier.ReadOnly, CommandReview.ResolveTier(GatewayControl.Argv(action)));
        }
    }

    [Fact]
    public async Task A_diagnostic_runs_through_the_runner_and_says_what_happened_when_the_cli_is_missing()
    {
        var vm = Panel();
        var validate = OverviewPanelViewModel.DiagnosticCommands[0];

        await vm.RunDiagnosticCommand.ExecuteAsync(validate);

        Assert.True(vm.HasDiagnosticMessage);
        Assert.Equal("Validate configuration", vm.DiagnosticTitle);
        Assert.Equal("Bad", vm.DiagnosticKey);
        Assert.Contains("'defenseclaw' was not found", vm.DiagnosticMessage, StringComparison.Ordinal);
        Assert.False(vm.IsDiagnosticRunning);

        await vm.RunDiagnosticCommand.ExecuteAsync(OverviewPanelViewModel.DiagnosticCommands[2]);
        Assert.Contains("'defenseclaw-gateway' was not found", vm.DiagnosticMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_command_that_is_not_read_only_is_reviewed_and_never_run_by_the_click()
    {
        var vm = Panel();
        var removal = new DiagnosticCommand("Remove", CommandReview.DefaultExecutable, new[] { "skill", "remove", "--", "pdf-tools" }, "Not a diagnostic.");

        await vm.RunDiagnosticCommand.ExecuteAsync(removal);

        // The review is up, with the exact argv and the tier the classifier gives it; nothing was started.
        Assert.True(vm.Review.IsOpen);
        Assert.False(vm.Review.IsRunning);
        var review = vm.Review.CommandReview!;
        Assert.Equal("Run “Remove”?", review.Title);
        Assert.Contains("is not on DefenseClaw for Windows' list of commands known to be read-only, so it is reviewed first", review.Summary, StringComparison.Ordinal);
        Assert.Equal(new[] { "skill", "remove", "--", "pdf-tools" }, Assert.Single(review.Steps).Argv);
        Assert.Equal(CommandTier.Destructive, review.Tier);
        Assert.False(vm.HasDiagnosticMessage);
        Assert.False(vm.IsDiagnosticRunning);
        Assert.Empty(_scene.Services.Cli.Activity);
    }

    [Theory]
    [InlineData("plan", "apply")]
    [InlineData("validate", "fix")]
    [InlineData("skill", "list", "purge")]
    public async Task A_command_the_classifier_calls_read_only_but_that_is_not_on_the_list_is_reviewed_as_a_change_too(params string[] argv)
    {
        var vm = Panel();
        Assert.Equal(CommandTier.ReadOnly, CommandTiers.Classify(argv));

        await vm.RunDiagnosticCommand.ExecuteAsync(new DiagnosticCommand("Guess", CommandReview.DefaultExecutable, argv, "Looks like a read."));

        Assert.True(vm.Review.IsOpen);
        var review = vm.Review.CommandReview!;
        Assert.Equal(argv, Assert.Single(review.Steps).Argv);
        Assert.Equal(CommandTier.StateChanging, review.Tier);
        Assert.Equal("Changes state", review.TierLabel);
        Assert.False(vm.HasDiagnosticMessage);
        Assert.Empty(_scene.Services.Cli.Activity);
    }

    [Fact]
    public async Task A_gateway_verb_that_is_not_one_of_the_two_listed_reads_is_reviewed_against_the_gateway_executable()
    {
        var vm = Panel();

        await vm.RunDiagnosticCommand.ExecuteAsync(
            new DiagnosticCommand("Gateway doctor", GatewayControl.Executable, new[] { "doctor" }, "Not a gateway read."));

        Assert.True(vm.Review.IsOpen);
        var step = Assert.Single(vm.Review.CommandReview!.Steps);
        Assert.Equal(GatewayControl.Executable, step.Executable);
        Assert.Equal("defenseclaw-gateway doctor", step.CommandText);
        Assert.NotEqual(CommandTier.ReadOnly, vm.Review.CommandReview.Tier);
        Assert.Empty(_scene.Services.Cli.Activity);
    }

    [Fact]
    public async Task A_listed_diagnostic_does_not_open_the_review_it_runs()
    {
        var vm = Panel();

        await vm.RunDiagnosticCommand.ExecuteAsync(OverviewPanelViewModel.DiagnosticCommands[0]);

        Assert.False(vm.Review.IsOpen);
        Assert.True(vm.HasDiagnosticMessage);
    }

    [Fact]
    public void Open_command_palette_asks_the_shell_for_it_and_open_activity_and_inventory_are_navigation()
    {
        var vm = Panel();
        var raised = 0;
        _scene.Services.Navigation.PaletteRequested += (_, _) => raised++;

        vm.OpenCommandPaletteCommand.Execute(null);
        Assert.Equal(1, raised);

        vm.OpenActivityCommand.Execute(null);
        Assert.Equal(new NavigationRequest("activity"), _scene.Services.Navigation.Pending);

        vm.OpenInventoryCommand.Execute(null);
        Assert.Equal(new NavigationRequest("inventory"), _scene.Services.Navigation.Pending);
        Assert.Empty(_scene.Services.Cli.Activity);
    }

    // ---- Scan Skills: reviewed ----

    [Fact]
    public void Scan_skills_opens_the_review_with_the_exact_command_and_runs_nothing()
    {
        var vm = Panel();

        vm.ScanSkillsCommand.Execute(null);

        Assert.True(vm.Review.IsOpen);
        Assert.False(vm.Review.IsRunning);
        var review = vm.Review.CommandReview!;
        Assert.Equal("Scan all skills?", review.Title);
        Assert.Equal("Scan skills", review.ConfirmLabel);
        var step = Assert.Single(review.Steps);
        Assert.Equal(new[] { "skill", "scan", "--all" }, step.Argv);
        Assert.Equal("defenseclaw skill scan --all", step.CommandText);
        Assert.Equal(CommandReview.DefaultExecutable, step.Executable);
        Assert.NotEqual(CommandTier.ReadOnly, review.Tier);
        Assert.Empty(_scene.Services.Cli.Activity);
    }

    [Fact]
    public void Cancelling_the_review_runs_nothing()
    {
        var vm = Panel();
        vm.ScanSkillsCommand.Execute(null);

        vm.Review.DismissCommand.Execute(null);

        Assert.False(vm.Review.IsOpen);
        Assert.Empty(_scene.Services.Cli.Activity);
    }

    [Fact]
    public void Escape_closes_the_review_and_is_not_consumed_when_there_is_none()
    {
        var vm = Panel();
        Assert.False(vm.HandleEscape());

        vm.ScanSkillsCommand.Execute(null);
        Assert.True(vm.HandleEscape());
        Assert.False(vm.Review.IsOpen);
        Assert.False(vm.HandleEscape());
        Assert.Empty(_scene.Services.Cli.Activity);
    }

    // ---- The gateway: start / restart / stop, reviewed ----

    [Fact]
    public void While_the_gateway_runs_the_button_is_restart_and_it_opens_the_gateway_review()
    {
        var vm = Panel(running: true);

        Assert.Equal("Restart Gateway", vm.GatewayActionLabel);
        Assert.True(vm.RunGatewayActionCommand.CanExecute(null));

        vm.RunGatewayActionCommand.Execute(null);

        Assert.True(vm.Review.IsOpen);
        var review = vm.Review.CommandReview!;
        Assert.Equal("Restart gateway?", review.Title);
        var step = Assert.Single(review.Steps);
        Assert.Equal(GatewayControl.Executable, step.Executable);
        Assert.Equal("defenseclaw-gateway restart", step.CommandText);
        Assert.Equal(CommandTier.StateChanging, review.Tier);
        Assert.Equal("Restart gateway", review.ConfirmLabel);
        Assert.Contains("fail mode", review.Summary, StringComparison.Ordinal);
        Assert.Empty(_scene.Services.Cli.Activity);
    }

    [Fact]
    public void While_it_is_down_the_button_is_start_and_stop_is_off_with_the_reason_as_its_tip()
    {
        var vm = Panel(running: false);

        Assert.Equal("Start Gateway", vm.GatewayActionLabel);
        Assert.True(vm.RunGatewayActionCommand.CanExecute(null));
        Assert.False(vm.StopGatewayCommand.CanExecute(null));
        Assert.Equal("The gateway is not running.", vm.StopGatewayTip);

        vm.RunGatewayActionCommand.Execute(null);
        Assert.Equal("defenseclaw-gateway start", vm.Review.CommandReview!.Steps[0].CommandText);
    }

    [Fact]
    public void Stop_is_reviewed_and_names_what_the_hooks_do_meanwhile()
    {
        var vm = Panel(running: true);

        Assert.True(vm.StopGatewayCommand.CanExecute(null));
        vm.StopGatewayCommand.Execute(null);

        var review = vm.Review.CommandReview!;
        Assert.Equal("Stop gateway?", review.Title);
        Assert.Equal("defenseclaw-gateway stop", review.Steps[0].CommandText);
        Assert.Contains("fail mode", review.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Until_the_install_is_known_the_gateway_actions_are_off_and_say_why()
    {
        var vm = new OverviewPanelViewModel(_scene.Services);

        Assert.False(vm.RunGatewayActionCommand.CanExecute(null));
        Assert.False(vm.StopGatewayCommand.CanExecute(null));
        Assert.False(vm.RestartGatewayCommand.CanExecute(null));
        Assert.Contains("Still checking", vm.GatewayActionTip, StringComparison.Ordinal);
    }

    [Fact]
    public void Not_installed_is_a_reason_not_a_review()
    {
        var vm = new OverviewPanelViewModel(_scene.Services);
        vm.Apply(OverviewScene.Snapshot(running: false) with { Install = InstallState.NotInstalled, State = AppGatewayState.NotInstalled });

        Assert.False(vm.RunGatewayActionCommand.CanExecute(null));
        Assert.Equal("DefenseClaw is not installed on this machine.", vm.GatewayActionTip);
    }

    [Fact]
    public async Task Confirming_runs_the_gateway_executable_not_defenseclaw_and_the_result_is_in_the_review()
    {
        var vm = Panel(running: true);
        vm.RunGatewayActionCommand.Execute(null);

        await vm.Review.ConfirmCommand.ExecuteAsync(null);

        // The isolated install has no CLI: the run ends at "not found", naming the gateway executable, and the review says it failed.
        Assert.True(vm.Review.IsFinished);
        Assert.Equal("Bad", vm.Review.ResultKey);
        Assert.Contains("'defenseclaw-gateway' was not found", vm.Review.ResultOutput, StringComparison.Ordinal);
    }

    // ---- The original doctor button is unchanged ----

    [Fact]
    public void The_doctor_button_is_still_plain_doctor_and_the_quick_action_shares_it()
    {
        var vm = Panel();

        Assert.Equal(new[] { "doctor" }, OverviewPanelViewModel.DoctorArgv);
        Assert.Equal(CommandTier.ReadOnly, CommandReview.ResolveTier(OverviewPanelViewModel.DoctorArgv));
        Assert.NotNull(vm.RunDoctorCommand);
    }
}
