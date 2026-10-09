using System.Runtime.CompilerServices;
using System.Windows.Automation;
using System.Windows.Controls;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Shell;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Install;
using DefenseClaw.Core.Paths;

namespace DefenseClaw.App.Tests.Installation;

/// <summary>
/// The shell's shared surfaces on a read-only installation (CUST-308): the gateway start/stop/restart rule every surface shares, the command
/// palette (its rows and its Run), the review model every confirmation is built from, the shared review control, and the review dialog that runs
/// the commands. Nothing here starts a process: the runner is the isolated one, the review and the toast are test seams.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class InstallationShellTests : IDisposable
{
    private static readonly string[] ReadArgv = { "skill", "list" };
    private static readonly string[] ChangeArgv = { "skill", "block", "--connector", "claudecode", "--", "pdf-tools" };

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

    private AppServices Create(InstallationContext? installation = null)
    {
        var services = TestServices.Create(_temp, installation: installation);
        _services.Add(services);
        return services;
    }

    private sealed record Harness(
        ShellActions Actions,
        PanelCatalog Catalog,
        AppServices Services,
        List<string> Toasts,
        List<CommandReview> Reviews,
        List<GatewayAction> Lifecycle);

    /// <summary>Actions over isolated services, as the palette tests build them: the review is declined (and recorded), the toast and the gateway runner are recorders.</summary>
    private Harness Harness_(InstallationContext? installation = null)
    {
        var services = Create(installation);
        var tray = (TrayIconService)RuntimeHelpers.GetUninitializedObject(typeof(TrayIconService));
        var toasts = new List<string>();
        var reviews = new List<CommandReview>();
        var lifecycle = new List<GatewayAction>();
        var catalog = new PanelCatalog(services);
        var actions = new ShellActions(services, catalog, tray, () => null)
        {
            Toast = (title, message) => toasts.Add(title + ": " + message),
            ClipboardWriter = _ => { },
            Confirmer = review =>
            {
                reviews.Add(review);
                return false;
            },
            GatewayActionRunner = action =>
            {
                lifecycle.Add(action);
                return Task.CompletedTask;
            },
        };
        return new Harness(actions, catalog, services, toasts, reviews, lifecycle);
    }

    // ------------------------------------------------------------------ the gateway rule every surface shares

    [Theory]
    [InlineData(GatewayAction.Start, false)]
    [InlineData(GatewayAction.Stop, true)]
    [InlineData(GatewayAction.Restart, true)]
    public void The_gateway_availability_is_the_gateways_own_on_a_writable_installation(GatewayAction action, bool running)
    {
        var services = Create();
        var snapshot = OverviewScene.Snapshot(running: running);

        Assert.Equal(GatewayControl.Availability(action, snapshot), GatewayControl.Availability(action, snapshot, services.Installation));
        Assert.True(GatewayControl.Availability(action, snapshot, services.Installation).Allowed);
    }

    [Theory]
    [InlineData(GatewayAction.Start, false)]
    [InlineData(GatewayAction.Stop, true)]
    [InlineData(GatewayAction.Restart, true)]
    public void A_read_only_installation_is_told_so_before_the_gateways_own_state_is_considered(GatewayAction action, bool running)
    {
        var services = Create(TestInstallations.Managed(_temp.Path));
        var snapshot = OverviewScene.Snapshot(running: running);

        var (allowed, reason) = GatewayControl.Availability(action, snapshot, services.Installation);

        Assert.False(allowed);
        Assert.Equal(TestInstallations.ManagedReason, reason);

        // The same answer when the gateway's state would have said no for its own reasons.
        var unknown = GatewayControl.Availability(action, new GatewaySnapshot(), services.Installation);
        Assert.False(unknown.Allowed);
        Assert.Equal(TestInstallations.ManagedReason, unknown.Reason);
    }

    // ------------------------------------------------------------------ the palette

    [Fact]
    public void The_palettes_own_rows_that_change_something_are_off_with_the_sentence_and_the_rest_are_on()
    {
        var h = Harness_(TestInstallations.Managed(_temp.Path));
        var rows = ShellCommandRegistry.Build(h.Catalog, h.Actions, _ => { }, () => { }, curated: Array.Empty<CuratedCommand>());

        var scan = Assert.Single(rows, r => r.Id == "app.scan-ai");
        Assert.False(scan.IsEnabled);
        Assert.Equal(TestInstallations.ManagedReason, scan.DisabledReason);

        foreach (var verb in new[] { "start", "stop", "restart" })
        {
            var gateway = Assert.Single(rows, r => r.Id == "gateway." + verb);
            Assert.False(gateway.IsEnabled);
            Assert.Equal(TestInstallations.ManagedReason, gateway.DisabledReason);
        }

        // Reads, navigation and the app's own settings are not held back.
        foreach (var id in new[] { "app.refresh-gateway", "app.run-doctor", "app.diagnose", "app.check-updates", "app.config-editor", "app.copy-last-output" })
        {
            Assert.True(Assert.Single(rows, r => r.Id == id).IsEnabled, id);
        }
    }

    [Fact]
    public void On_a_writable_installation_the_scan_row_is_on()
    {
        var h = Harness_();
        var rows = ShellCommandRegistry.Build(h.Catalog, h.Actions, _ => { }, () => { }, curated: Array.Empty<CuratedCommand>());

        var scan = Assert.Single(rows, r => r.Id == "app.scan-ai");
        Assert.True(scan.IsEnabled);
        Assert.Null(scan.DisabledReason);
    }

    [Theory]
    [InlineData("baseline")]
    [InlineData("extended")]
    public void Every_registry_row_is_either_a_read_or_off_with_the_sentence_on_a_read_only_installation(string runtime)
    {
        var h = Harness_(TestInstallations.Managed(_temp.Path));
        var catalogue = new CuratedCommandCatalog(runtime == "baseline" ? TuiRegistryCatalogues.Baseline : TuiRegistryCatalogues.Extended);
        var rows = ShellCommandRegistry.BuildCliCommands(catalogue.Commands, h.Actions);
        var guard = h.Services.Installation;

        var disabled = 0;
        var enabledReads = 0;
        var copyOnly = 0;
        var dockerReads = 0;
        foreach (var row in rows)
        {
            var command = row.Cli!;
            var onlyCopies = (command.NeedsTerminal && !command.TypesInApp) || (command.NeedsArguments && command.Form is null);
            var changes = guard.ReasonFor(command.Executable, command.Argv) is not null;

            if (command.LifecycleAction is not null || (changes && !onlyCopies))
            {
                Assert.False(row.IsEnabled, row.Title);
                Assert.Equal(TestInstallations.ManagedReason, row.DisabledReason);
                disabled++;
            }
            else if (WizardWindowsPolicy.CommandNeedsDocker(command.Argv) && !onlyCopies)
            {
                // A read that needs Docker (the local stack's status or logs): the installation has nothing against it, so it is as available as
                // the Docker look says, with that look's reason - never the installation's.
                Assert.False(changes, row.Title);
                Assert.Equal(h.Actions.LocalStack.Decision.IsAvailable, row.IsEnabled);
                Assert.NotEqual(TestInstallations.ManagedReason, row.DisabledReason);
                dockerReads++;
            }
            else
            {
                // On: it is a read the runner lets through, or it runs nothing (it copies the command for a terminal).
                Assert.True(row.IsEnabled, row.Title);
                Assert.True(onlyCopies || !changes, row.Title);
                if (onlyCopies)
                {
                    copyOnly++;
                }
                else
                {
                    enabledReads++;
                }
            }
        }

        Assert.True(disabled > 100, $"only {disabled} of {rows.Count} rows are off on a read-only installation");
        Assert.True(enabledReads > 20, $"only {enabledReads} read rows are on");
        Assert.True(copyOnly > 0);
        Assert.True(dockerReads > 0, "the local stack's reads were not among the rows");
    }

    [Fact]
    public void A_local_stack_change_is_off_with_the_installations_sentence_not_dockers_and_its_reads_are_not()
    {
        var h = Harness_(TestInstallations.Managed(_temp.Path));
        var rows = ShellCommandRegistry.BuildCliCommands(new CuratedCommandCatalog(TuiRegistryCatalogues.Baseline).Commands, h.Actions);

        // up, down and reset change a stack and the configuration around it: the installation comes before whatever Docker says.
        foreach (var title in new[] { "setup local-observability up", "setup local-observability down", "setup local-observability reset" })
        {
            var change = Assert.Single(rows, r => r.Title == title);
            Assert.False(change.IsEnabled, title);
            Assert.Equal(TestInstallations.ManagedReason, change.DisabledReason);
        }

        // status and logs only read.
        foreach (var title in new[] { "setup local-observability status", "setup local-observability logs" })
        {
            var read = Assert.Single(rows, r => r.Title == title);
            Assert.NotEqual(TestInstallations.ManagedReason, read.DisabledReason);
        }
    }

    [Theory]
    [InlineData("baseline")]
    [InlineData("extended")]
    public async Task Running_any_registry_row_on_a_read_only_installation_never_puts_a_change_in_front_of_the_operator_or_starts_one(string runtime)
    {
        var h = Harness_(TestInstallations.Managed(_temp.Path));
        var catalogue = runtime == "baseline" ? TuiRegistryCatalogues.Baseline : TuiRegistryCatalogues.Extended;
        var refusals = 0;

        foreach (var entry in catalogue.OnWindows)
        {
            var command = CuratedCommand.FromRegistry(entry);
            h.Toasts.Clear();
            h.Reviews.Clear();
            h.Lifecycle.Clear();

            await h.Actions.RunCuratedAsync(command);

            // A review that is shown at all is of a read (a read the list does not know, which the classifier allows).
            Assert.All(h.Reviews, review => Assert.Null(h.Services.Installation.ReasonFor(review.Steps)));

            var onlyCopies = (command.NeedsTerminal && !command.TypesInApp) || (command.NeedsArguments && command.Form is null);
            if (command.LifecycleAction is null && !onlyCopies && !CuratedCommandCatalog.Refuses(command.Argv) &&
                h.Services.Installation.ReasonFor(command.Executable, command.Argv) is { } reason)
            {
                Assert.Equal(command.Title + ": " + reason, Assert.Single(h.Toasts));
                Assert.Empty(h.Reviews);
                refusals++;
            }
        }

        Assert.True(refusals > 100, $"only {refusals} entries were refused");
        Assert.Empty(h.Services.Cli.Activity);
    }

    // ------------------------------------------------------------------ the review model

    [Fact]
    public void A_review_of_a_read_is_never_blocked_and_a_review_of_a_change_is_blocked_only_on_a_read_only_installation()
    {
        var writable = Create().Installation;
        var managed = Create(TestInstallations.Managed(_temp.Path)).Installation;
        var read = CommandReview.ForCommand("List skills?", ReadArgv);
        var change = CommandReview.ForCommand("Block the skill?", ChangeArgv, CommandTier.StateChanging);

        Assert.False(read.GuardedBy(managed).IsBlocked);
        Assert.False(change.GuardedBy(writable).IsBlocked);
        Assert.Same(change, change.GuardedBy(writable));

        var blocked = change.GuardedBy(managed);
        Assert.True(blocked.IsBlocked);
        Assert.Equal(TestInstallations.ManagedReason, blocked.BlockedReason);
        Assert.Contains(blocked.Warnings, w => w.Title == CommandReviewWarning.ReadOnlyInstallationTitle && w.Message == TestInstallations.ManagedReason);
        Assert.Equal(change.Steps, blocked.Steps);

        // The bar is in the warnings once, and a review that is not blocked carries none of it.
        Assert.Single(blocked.Warnings, w => w.Title == CommandReviewWarning.ReadOnlyInstallationTitle);
        Assert.DoesNotContain(change.Warnings, w => w.Title == CommandReviewWarning.ReadOnlyInstallationTitle);
    }

    [Fact]
    public void A_review_with_one_changing_step_among_reads_is_blocked()
    {
        var managed = Create(TestInstallations.Managed(_temp.Path)).Installation;
        var review = new CommandReview
        {
            Title = "Two steps?",
            Steps = new[] { new CommandReviewStep(ReadArgv), new CommandReviewStep(ChangeArgv, floor: CommandTier.StateChanging) },
        };

        Assert.True(review.GuardedBy(managed).IsBlocked);
    }

    // ------------------------------------------------------------------ the shared review control

    private static CommandReviewControl Build(CommandReview? review, Action<CommandReviewControl>? configure = null)
    {
        var control = new CommandReviewControl { Review = review };
        configure?.Invoke(control);
        control.Measure(new System.Windows.Size(520, 2000));
        control.Arrange(new System.Windows.Rect(0, 0, 520, Math.Max(control.DesiredSize.Height, 1)));
        control.UpdateLayout();
        return control;
    }

    [Fact]
    public void The_control_keeps_the_confirm_button_off_for_a_blocked_review_and_says_why_on_hover_and_to_a_screen_reader()
    {
        var managed = Create(TestInstallations.Managed(_temp.Path)).Installation;
        var review = CommandReview.ForCommand("Block the skill?", ChangeArgv, CommandTier.StateChanging).GuardedBy(managed);

        UiThread.Run(() =>
        {
            var control = Build(review);

            Assert.False(control.ConfirmButton.IsEnabled);
            Assert.Equal(TestInstallations.ManagedReason, control.ConfirmButton.ToolTip);
            Assert.True(ToolTipService.GetShowOnDisabled(control.ConfirmButton));
            Assert.Equal(TestInstallations.ManagedReason, AutomationProperties.GetHelpText(control.ConfirmButton));
            Assert.Contains(
                control.WarningList.Items.Cast<CommandReviewWarning>(),
                w => w.Title == CommandReviewWarning.ReadOnlyInstallationTitle && w.Message == TestInstallations.ManagedReason);
        });
    }

    [Fact]
    public void The_control_leaves_the_confirm_button_to_its_host_for_a_review_that_is_not_blocked_and_gives_it_back_when_the_review_is_replaced()
    {
        var managed = Create(TestInstallations.Managed(_temp.Path)).Installation;
        var change = CommandReview.ForCommand("Block the skill?", ChangeArgv, CommandTier.StateChanging);

        UiThread.Run(() =>
        {
            var control = Build(change);
            Assert.True(control.ConfirmButton.IsEnabled);
            Assert.Null(control.ConfirmButton.ToolTip);
            Assert.False(ToolTipService.GetShowOnDisabled(control.ConfirmButton));

            control.Review = change.GuardedBy(managed);
            Assert.False(control.ConfirmButton.IsEnabled);

            control.Review = change;
            Assert.True(control.ConfirmButton.IsEnabled);
            Assert.Null(control.ConfirmButton.ToolTip);
        });
    }

    [Fact]
    public void The_control_hands_the_confirm_button_back_to_its_bound_command_when_a_blocked_review_is_replaced_and_never_overrides_it_before()
    {
        var managed = Create(TestInstallations.Managed(_temp.Path)).Installation;
        var change = CommandReview.ForCommand("Block the skill?", ChangeArgv, CommandTier.StateChanging);
        var allowed = false;
        var command = new CommunityToolkit.Mvvm.Input.RelayCommand(() => { }, () => allowed);

        UiThread.Run(() =>
        {
            // An unblocked review leaves the decision to the command, here one that says no (an acknowledgement not yet given).
            var control = Build(change, c => c.ConfirmCommand = command);
            Assert.False(control.ConfirmButton.IsEnabled);

            // Blocked: off whatever the command says.
            allowed = true;
            command.NotifyCanExecuteChanged();
            Assert.True(control.ConfirmButton.IsEnabled);
            control.Review = change.GuardedBy(managed);
            Assert.False(control.ConfirmButton.IsEnabled);

            // Unblocked again: the command's answer comes back, in both directions.
            control.Review = change;
            Assert.True(control.ConfirmButton.IsEnabled);
            allowed = false;
            command.NotifyCanExecuteChanged();
            Assert.False(control.ConfirmButton.IsEnabled);
        });
    }

    // ------------------------------------------------------------------ the review dialog that runs the commands

    private static DiscoverStep ScanSkills() => new(new[] { "skill", "scan", "--all" }, "Scan every configured skill.");

    [Fact]
    public async Task A_review_opened_on_a_read_only_installation_carries_the_reason_and_confirming_it_runs_nothing()
    {
        var services = Create(TestInstallations.Managed(_temp.Path));
        var review = new DiscoverActionReview(services);
        var ran = new List<string>();
        review.RunStep = (executable, argv, _) =>
        {
            ran.Add(executable + " " + string.Join(' ', argv));
            throw new InvalidOperationException("a blocked review ran a step");
        };

        review.Open("Scan all skills?", "Scans.", new[] { ScanSkills() }, primaryText: "Scan skills");

        Assert.True(review.IsOpen);
        Assert.True(review.CommandReview!.IsBlocked);
        Assert.False(review.ConfirmCommand.CanExecute(null));
        await review.ConfirmCommand.ExecuteAsync(null);

        Assert.Empty(ran);
        Assert.False(review.IsRunning);
        Assert.False(review.IsFinished);
        Assert.Empty(services.Cli.Activity);
    }

    [Fact]
    public async Task A_review_opened_while_the_installation_was_writable_is_refused_at_confirm_if_the_installation_turned_read_only_meanwhile()
    {
        var services = Create();
        var review = new DiscoverActionReview(services);
        var ran = new List<string>();
        review.RunStep = (executable, argv, _) =>
        {
            ran.Add(executable + " " + string.Join(' ', argv));
            throw new InvalidOperationException("a refused review ran a step");
        };
        review.Open("Scan all skills?", "Scans.", new[] { ScanSkills() }, primaryText: "Scan skills");
        Assert.False(review.CommandReview!.IsBlocked);
        Assert.True(review.ConfirmCommand.CanExecute(null));

        // The config the review was opened on is edited to managed while the question is on screen: the live answer is asked again at Confirm.
        services.Installation.Replace(TestInstallations.ManagedAt(_temp.Path));
        await review.ConfirmCommand.ExecuteAsync(null);

        Assert.Empty(ran);
        Assert.True(review.IsFinished);
        Assert.Equal("Bad", review.ResultKey);
        Assert.Equal("Refused: not started", review.CommandReview.Steps[0].StatusText);
        Assert.Equal("Not run. " + TestInstallations.ManagedReason + " Nothing was changed; the refusal is recorded in the Activity panel.", review.ResultText);

        // The refusal is the one Activity entry every refusal is: "refused", no exit code, the command that was not started.
        var recorded = Assert.Single(services.Cli.Activity);
        Assert.Null(recorded.ExitCode);
        Assert.StartsWith(CliRunner.RefusedPrefix, recorded.FailureReason, StringComparison.Ordinal);
        Assert.Contains(TestInstallations.ManagedReason, recorded.FailureReason, StringComparison.Ordinal);
        Assert.Equal(new[] { "skill", "scan", "--all" }, recorded.Argv);
    }

    [Fact]
    public async Task The_installation_comes_before_the_data_guard_so_a_review_that_is_refused_says_one_reason()
    {
        var services = Create();
        var review = new DiscoverActionReview(services) { RunGuard = () => "Changes are off: this list was read before config.yaml changed." };
        review.Open("Scan all skills?", "Scans.", new[] { ScanSkills() }, primaryText: "Scan skills");

        services.Installation.Replace(TestInstallations.ManagedAt(_temp.Path));
        await review.ConfirmCommand.ExecuteAsync(null);

        Assert.Contains(TestInstallations.ManagedReason, review.ResultText, StringComparison.Ordinal);
        Assert.DoesNotContain("config.yaml changed", review.ResultText, StringComparison.Ordinal);
        Assert.Contains(TestInstallations.ManagedReason, Assert.Single(services.Cli.Activity).FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_review_with_only_reads_is_not_refused_by_the_installation_at_confirm()
    {
        var services = Create(TestInstallations.ManagedAt(_temp.Path));
        var review = new DiscoverActionReview(services);
        var ran = new List<string>();
        review.RunStep = (executable, argv, _) =>
        {
            ran.Add(executable + " " + string.Join(' ', argv));
            var done = InvocationFactory.Create(retainFullOutput: true, argv.ToArray());
            InvocationFactory.Finish(done, 0);
            return Task.FromResult(done);
        };

        review.Open("List skills?", "Lists.", new[] { new DiscoverStep(ReadArgv, "List the skills.") }, primaryText: "List");
        Assert.False(review.CommandReview!.IsBlocked);
        await review.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "defenseclaw skill list" }, ran);
        Assert.Equal("Ok", review.ResultKey);
        Assert.Empty(services.Cli.Activity);
    }
}
