using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Panels;

namespace DefenseClaw.App.Tests.PendingRestart;

/// <summary>
/// The queued gateway restart as the operator sees it (CUST-267): the Setup hub's banner and the Overview's attention row, hosted offscreen in the
/// shell stand-in over a synthetic install, with the queue filled the way a config-editor save and a <c>--no-restart</c> run fill it. Set
/// <c>DC_RENDER_DIR</c> to also write a PNG of each.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class RestartQueueViewTests : IDisposable
{
    private const string Saved = "config.yaml saved in the config editor (guardrail, llm)";
    private const string Ran = "guardrail hilt ran with --no-restart";

    private readonly OverviewScene _scene = OverviewScene.Create(seedAudit: false, seedAgents: false);

    public void Dispose() => _scene.Dispose();

    private AppServices Services => _scene.Services;

    /// <summary>The buttons with this name that are drawn: a row template makes one for every row and collapses the ones a row does not offer.</summary>
    private static Wpf.Ui.Controls.Button[] Drawn(FrameworkElement page, string name) =>
        VisualTree.Descendants<Wpf.Ui.Controls.Button>(page).Where(b => AutomationProperties.GetName(b) == name && b.IsVisible).ToArray();

    private static Wpf.Ui.Controls.Button ButtonNamed(FrameworkElement page, string name) => Assert.Single(Drawn(page, name));

    private static TextBlock Text(FrameworkElement page, Func<string, bool> match) =>
        VisualTree.Descendants<TextBlock>(page).First(t => match(t.Text));

    [Fact]
    public void The_setup_hub_draws_the_banner_with_the_reasons_and_two_buttons_until_the_queue_is_empty()
    {
        _scene.Publish(OverviewScene.Snapshot());
        var shell = UiThread.Run(() =>
        {
            var s = new PanelShell(Services, 1400, 900);
            _ = s.Show<SetupPanel>();
            return s;
        });
        try
        {
            var vm = UiThread.Run(() => (SetupPanelViewModel)shell.ViewModel);
            UiThread.WaitFor(() => !vm.IsLoading && !vm.Credentials.IsLoading && !vm.IsGuardrailBusy, "the Setup hub's first reads finished");

            UiThread.Run(() =>
            {
                var page = shell.Page!;
                shell.Host.Relayout();

                // Nothing queued: no banner, and the checklist says so.
                Assert.False(Text(page, t => t == "Gateway restart pending").IsVisible);
                Assert.Empty(Drawn(page, "Restart the gateway now"));

                // A save in the config editor, and a guardrail run told not to restart.
                Assert.True(Services.RestartQueue.Queue(Saved));
                Assert.True(Services.RestartQueue.Queue(Ran));
                shell.Host.Relayout();

                var title = Text(page, t => t == "Gateway restart pending");
                Assert.True(title.IsVisible);
                var detail = Text(page, t => t.StartsWith(Saved, StringComparison.Ordinal) && t.Contains("Queued ", StringComparison.Ordinal));
                Assert.True(detail.IsVisible);
                Assert.Contains(Ran, detail.Text, StringComparison.Ordinal);
                Assert.Contains("Queued ", detail.Text, StringComparison.Ordinal);
                Assert.EndsWith(RestartQueueText.Consequence, detail.Text, StringComparison.Ordinal);

                // The banner is above everything else on the page: it is the first thing seen, not something found by scrolling.
                var connectors = Text(page, t => t == "Configured connectors");
                Assert.True(title.TranslatePoint(new Point(), page).Y < connectors.TranslatePoint(new Point(), page).Y);

                var restart = ButtonNamed(page, "Restart the gateway now");
                var clear = ButtonNamed(page, "Clear the queued restart");
                Assert.True(restart.IsVisible && clear.IsVisible);
                Assert.True(restart.IsEnabled && clear.IsEnabled);
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetHelpText(restart)));
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetHelpText(clear)));
                Assert.Same(vm.RestartNowCommand, restart.Command);
                Assert.Same(vm.ClearRestartCommand, clear.Command);

                // The checklist's row is the same queue.
                var row = vm.Readiness.Rows.Single(r => r.Title == "Restart Pending");
                Assert.Equal(ReadinessStatus.Warn, row.Status);
                Assert.Equal(Saved + "; " + Ran, row.Detail);
                Assert.Contains(VisualTree.Descendants<TextBlock>(page), t => t.Text == Saved + "; " + Ran && t.IsVisible);

                RenderTo.Png(shell.Host, "cust267-setup-banner-1400x900");

                // Clear: the banner goes, the row passes, and nothing was run.
                clear.Command.Execute(null);
                shell.Host.Relayout();

                Assert.False(Text(page, t => t == "Gateway restart pending").IsVisible);
                Assert.Contains(VisualTree.Descendants<TextBlock>(page), t => t.Text == "No queued restart." && t.IsVisible);
                Assert.Empty(Services.Cli.Activity);
            });
        }
        finally
        {
            UiThread.Run(shell.Dispose);
        }
    }

    [Fact]
    public void On_a_read_only_installation_the_banner_shows_and_restart_now_is_off_with_the_installations_sentence()
    {
        _scene.Publish(OverviewScene.Snapshot());
        Services.Installation.Replace(TestInstallations.ManagedAt(_scene.Temp.Path));
        var shell = UiThread.Run(() =>
        {
            var s = new PanelShell(Services, 1400, 900);
            _ = s.Show<SetupPanel>();
            return s;
        });
        try
        {
            var vm = UiThread.Run(() => (SetupPanelViewModel)shell.ViewModel);
            UiThread.WaitFor(() => !vm.IsLoading && !vm.Credentials.IsLoading && !vm.IsGuardrailBusy, "the Setup hub's first reads finished");

            UiThread.Run(() =>
            {
                var page = shell.Page!;
                _ = Services.RestartQueue.Queue(Saved);
                shell.Host.Relayout();

                var restart = ButtonNamed(page, "Restart the gateway now");
                var clear = ButtonNamed(page, "Clear the queued restart");
                Assert.True(restart.IsVisible);
                Assert.False(restart.IsEnabled);
                Assert.Equal(Services.Installation.BlockedReason, restart.ToolTip);
                Assert.True(clear.IsEnabled);
            });
        }
        finally
        {
            UiThread.Run(shell.Dispose);
        }
    }

    [Fact]
    public void The_overview_draws_the_attention_row_with_its_two_buttons_and_they_are_the_panels_commands()
    {
        var shell = UiThread.Run(() =>
        {
            _scene.Publish(OverviewScene.Snapshot());
            var s = new PanelShell(Services, 1400, 900);
            var view = s.Show<OverviewPanel>();
            var vm = (OverviewPanelViewModel)s.ViewModel;
            vm.Apply(OverviewScene.Snapshot());
            s.Host.Relayout();
            return s;
        });
        try
        {
            UiThread.Run(() =>
            {
                var page = shell.Page!;
                var vm = (OverviewPanelViewModel)shell.ViewModel;

                Assert.Empty(Drawn(page, "Restart the gateway now"));

                Assert.True(Services.RestartQueue.Queue(Saved));
                Assert.True(Services.RestartQueue.Queue(Ran));
                vm.Apply(OverviewScene.Snapshot());
                shell.Host.Relayout();

                var title = Text(page, t => t == "Gateway restart pending");
                Assert.True(title.IsVisible);
                var detail = Text(page, t => t.StartsWith(Saved, StringComparison.Ordinal) && t.Contains("Queued ", StringComparison.Ordinal));
                Assert.Contains(Ran, detail.Text, StringComparison.Ordinal);
                Assert.EndsWith(RestartQueueText.Consequence, detail.Text, StringComparison.Ordinal);

                // The row is in the card that is first on the page, in the first three, with its severity word.
                Assert.Equal("Medium", vm.VisibleAttention[0].SeverityKey);
                Assert.True(vm.VisibleAttention[0].OffersRestart);

                var restart = ButtonNamed(page, "Restart the gateway now");
                var clear = ButtonNamed(page, "Clear the queued restart");
                Assert.True(restart.IsVisible && clear.IsVisible);
                Assert.True(restart.IsEnabled && clear.IsEnabled);
                Assert.Same(vm.RestartPendingNowCommand, restart.Command);
                Assert.Same(vm.ClearPendingRestartCommand, clear.Command);
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetHelpText(restart)));

                RenderTo.Png(shell.Host, "cust267-overview-attention-1400x900");

                // Restart now opens the review in this panel and nothing has run.
                restart.Command.Execute(null);
                Assert.True(vm.Review.IsOpen);
                Assert.Equal(new[] { "restart" }, Assert.Single(vm.Review.CommandReview!.Steps).Argv);
                Assert.Empty(Services.Cli.Activity);
                vm.Review.DismissCommand.Execute(null);

                clear.Command.Execute(null);
                shell.Host.Relayout();
                Assert.False(Services.RestartQueue.IsPending);
                Assert.Empty(Drawn(page, "Restart the gateway now"));
            });
        }
        finally
        {
            UiThread.Run(shell.Dispose);
        }
    }
}
