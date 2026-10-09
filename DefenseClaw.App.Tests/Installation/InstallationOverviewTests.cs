using System.ComponentModel;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Paths;

namespace DefenseClaw.App.Tests.Installation;

/// <summary>
/// The Overview on a read-only installation (CUST-308): the banner "State-changing actions disabled: &lt;reason&gt;" with its Review Installation
/// button, and every Quick Action that changes something off with the installation's own sentence as its tooltip - while the reads (the
/// Diagnostics menu, Open Inventory) stay. The installations are synthetic (<see cref="TestInstallations"/>); nothing starts a process.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class InstallationOverviewTests : IDisposable
{
    private readonly OverviewScene _scene = OverviewScene.Create(seedAudit: false);

    public void Dispose() => _scene.Dispose();

    private AppServices Services => _scene.Services;

    private OverviewPanelViewModel Panel(bool running = true)
    {
        var vm = new OverviewPanelViewModel(Services);
        vm.Apply(OverviewScene.Snapshot(running: running));
        return vm;
    }

    /// <summary>A panel built and read on the UI thread (the page's own thread), with the not-configured rows the scene's discovery file gives.</summary>
    private async Task<OverviewPanelViewModel> PanelWithNotConfiguredRowsAsync()
    {
        var vm = UiThread.Run(() => Panel());
        await UiThread.Run(() => vm.RefreshAgentsAsync(CancellationToken.None));
        return vm;
    }

    public static IEnumerable<object[]> ReadOnlyInstallations() =>
        new[]
        {
            new object[] { "managed layout" },
            new object[] { "managed by config.yaml" },
            new object[] { "invalid selection" },
        };

    private static InstallationContext ReadOnly(string which) => which switch
    {
        "managed layout" => TestInstallations.Managed(),
        "managed by config.yaml" => TestInstallations.ManagedByConfigMode(),
        _ => TestInstallations.Invalid(),
    };

    // ------------------------------------------------------------------ the usual installation

    [Fact]
    public async Task A_writable_installation_has_no_banner_and_every_action_is_on()
    {
        var vm = await PanelWithNotConfiguredRowsAsync();

        Assert.Null(vm.InstallationBanner);
        Assert.False(vm.HasInstallationBanner);
        Assert.Null(vm.InstallationBlockedReason);
        Assert.True(vm.CanChangeInstallation);

        Assert.True(vm.ScanSkillsCommand.CanExecute(null));
        Assert.StartsWith("Scan every configured skill", vm.ScanSkillsTip, StringComparison.Ordinal);
        Assert.True(vm.RunGatewayActionCommand.CanExecute(null));
        Assert.True(vm.StopGatewayCommand.CanExecute(null));
        Assert.True(vm.RestartGatewayCommand.CanExecute(null));
        Assert.EndsWith("Asks for confirmation first.", vm.GatewayActionTip, StringComparison.Ordinal);

        var codex = vm.ConnectorRows.Single(r => r.Name == "codex");
        Assert.True(vm.AddConnectorCommand.CanExecute(codex));
    }

    // ------------------------------------------------------------------ a read-only installation

    [Theory]
    [MemberData(nameof(ReadOnlyInstallations))]
    public async Task A_read_only_installation_shows_the_banner_and_turns_every_changing_action_off_with_its_sentence(string which)
    {
        var context = ReadOnly(which);
        Services.Installation.Replace(context);
        var reason = context.BlockedReason!;
        var vm = await PanelWithNotConfiguredRowsAsync();

        Assert.True(vm.HasInstallationBanner);
        Assert.Equal("State-changing actions disabled: " + reason, vm.InstallationBanner);
        Assert.Equal(reason, vm.InstallationBlockedReason);
        Assert.False(vm.CanChangeInstallation);

        // Scan Skills, the gateway buttons and the row's Add are off, and the tooltip of each is the reason, not the usual sentence.
        Assert.False(vm.ScanSkillsCommand.CanExecute(null));
        Assert.Equal(reason, vm.ScanSkillsTip);
        Assert.False(vm.RunGatewayActionCommand.CanExecute(null));
        Assert.Equal(reason, vm.GatewayActionTip);
        Assert.False(vm.StopGatewayCommand.CanExecute(null));
        Assert.Equal(reason, vm.StopGatewayTip);
        Assert.False(vm.RestartGatewayCommand.CanExecute(null));
        Assert.False(vm.AddConnectorCommand.CanExecute(vm.ConnectorRows.Single(r => r.Name == "codex")));
    }

    [Fact]
    public async Task A_read_only_installation_leaves_the_reads_alone()
    {
        Services.Installation.Replace(TestInstallations.Managed());
        var vm = Panel();

        // Navigation and the Diagnostics menu are reads: they are not held back, and a diagnostic goes on to the CLI (which an isolated install lacks).
        Assert.True(vm.OpenInventoryCommand.CanExecute(null));
        Assert.True(vm.RunDiagnosticCommand.CanExecute(OverviewPanelViewModel.DiagnosticCommands[0]));
        await vm.RunDiagnosticCommand.ExecuteAsync(OverviewPanelViewModel.DiagnosticCommands[0]);

        Assert.Equal("Validate configuration", vm.DiagnosticTitle);
        Assert.Contains("'defenseclaw' was not found", vm.DiagnosticMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Review_Installation_opens_Settings_where_the_installation_block_is()
    {
        Services.Installation.Replace(TestInstallations.Managed());
        var vm = Panel();

        Assert.True(vm.ReviewInstallationCommand.CanExecute(null));
        vm.ReviewInstallationCommand.Execute(null);

        Assert.Equal(new NavigationRequest("settings"), Services.Navigation.Pending);
    }

    [Fact]
    public void A_change_that_reaches_a_review_anyway_cannot_be_confirmed_and_runs_nothing()
    {
        Services.Installation.Replace(TestInstallations.Managed());
        var vm = Panel();

        // The button is off; a keyboard route or a caller that skips it still gets the review - with the reason, and no way to run it.
        vm.ScanSkillsCommand.Execute(null);

        Assert.True(vm.Review.IsOpen);
        var review = vm.Review.CommandReview!;
        Assert.True(review.IsBlocked);
        Assert.Equal(TestInstallations.ManagedReason, review.BlockedReason);
        Assert.Contains(review.Warnings, w => w.Title == CommandReviewWarning.ReadOnlyInstallationTitle && w.Message == TestInstallations.ManagedReason);
        Assert.False(vm.Review.ConfirmCommand.CanExecute(null));
        Assert.Empty(Services.Cli.Activity);
    }

    [Fact]
    public async Task Confirming_a_blocked_review_by_any_route_runs_nothing()
    {
        Services.Installation.Replace(TestInstallations.Managed());
        var vm = Panel();
        var ran = new List<string>();
        vm.Review.RunStep = (executable, argv, _) =>
        {
            ran.Add(executable + " " + string.Join(' ', argv));
            throw new InvalidOperationException("a blocked review ran a step");
        };
        vm.ScanSkillsCommand.Execute(null);

        await vm.Review.ConfirmCommand.ExecuteAsync(null);

        Assert.Empty(ran);
        Assert.True(vm.Review.IsOpen);
        Assert.False(vm.Review.IsRunning);
        Assert.Empty(Services.Cli.Activity);
    }

    // ------------------------------------------------------------------ a verdict that moves while the page is open

    [Fact]
    public async Task An_installation_that_turns_read_only_while_the_page_is_on_screen_redraws_the_banner_and_the_actions_and_back()
    {
        // An activated page re-reads the monitor's snapshot, so the gateway has to be known to be running there too.
        _scene.Publish(OverviewScene.Snapshot());
        var vm = await PanelWithNotConfiguredRowsAsync();
        var codex = vm.ConnectorRows.Single(r => r.Name == "codex");

        UiThread.Run(() =>
        {
            vm.SetActive(true);
            try
            {
                var changed = new List<string?>();
                ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => changed.Add(e.PropertyName);
                var scanCanExecute = 0;
                vm.ScanSkillsCommand.CanExecuteChanged += (_, _) => scanCanExecute++;
                Assert.True(vm.ScanSkillsCommand.CanExecute(null));
                Assert.True(vm.RunGatewayActionCommand.CanExecute(null));

                Services.Installation.Replace(TestInstallations.ManagedByConfigMode());

                Assert.Contains(nameof(OverviewPanelViewModel.InstallationBanner), changed);
                Assert.Contains(nameof(OverviewPanelViewModel.HasInstallationBanner), changed);
                Assert.Contains(nameof(OverviewPanelViewModel.ScanSkillsTip), changed);
                Assert.True(scanCanExecute > 0, "the Scan Skills button was not asked again");
                Assert.True(vm.HasInstallationBanner);
                Assert.False(vm.ScanSkillsCommand.CanExecute(null));
                Assert.False(vm.RunGatewayActionCommand.CanExecute(null));
                Assert.Equal(TestInstallations.ManagedReason, vm.GatewayActionTip);
                Assert.False(vm.AddConnectorCommand.CanExecute(codex));

                // Fixed (or edited back): everything is on again.
                Services.Installation.Replace(TestInstallations.UserDefault());

                Assert.False(vm.HasInstallationBanner);
                Assert.True(vm.ScanSkillsCommand.CanExecute(null));
                Assert.True(vm.RunGatewayActionCommand.CanExecute(null));
                Assert.EndsWith("Asks for confirmation first.", vm.GatewayActionTip, StringComparison.Ordinal);
                Assert.True(vm.AddConnectorCommand.CanExecute(codex));
            }
            finally
            {
                vm.SetActive(false);
            }
        });
    }

    [Fact]
    public async Task A_page_that_was_away_when_the_installation_changed_catches_up_when_it_comes_back()
    {
        var vm = await PanelWithNotConfiguredRowsAsync();
        Services.Installation.Replace(TestInstallations.Managed());

        UiThread.Run(() =>
        {
            var changed = new List<string?>();
            ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

            vm.SetActive(true);
            try
            {
                Assert.Contains(nameof(OverviewPanelViewModel.InstallationBanner), changed);
                Assert.True(vm.HasInstallationBanner);
                Assert.False(vm.RunGatewayActionCommand.CanExecute(null));
            }
            finally
            {
                vm.SetActive(false);
            }
        });
    }
}
