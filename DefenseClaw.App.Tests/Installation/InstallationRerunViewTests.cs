using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Settings;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Shell;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Install;

namespace DefenseClaw.App.Tests.Installation;

/// <summary>
/// CUST-264 on a read-only installation (CUST-308), looked at on the shared UI thread: the Activity panel's Rerun buttons follow the installation
/// while the panel is on screen and when it comes back, the review a Rerun opens cannot be confirmed (a destructive one still opens on Cancel),
/// and the palette lists a recent command the installation now blocks greyed out with the installation's sentence. The runs are synthetic
/// entries; nothing is started.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class InstallationRerunViewTests : IDisposable
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

    private static CliInvocation Finished(string executable, int exit, params string[] argv)
    {
        var invocation = InvocationFactory.CreateFor(executable, argv);
        InvocationFactory.Finish(invocation, exit);
        return invocation;
    }

    private static Button RerunIn(FrameworkElement card) =>
        VisualTree.Descendants<Button>(card).First(button => AutomationProperties.GetName(button) == "Rerun this command");

    // ------------------------------------------------------------------ the Activity panel

    [Fact]
    public void The_panel_turns_Rerun_off_for_a_change_when_the_installation_turns_read_only_and_back_on_when_it_is_writable_again()
    {
        var services = TestServices.Create(_temp);
        _services.Add(services);

        UiThread.Run(() =>
        {
            var panel = new ActivityPanelViewModel(services);
            var change = panel.CreateRow(Finished("defenseclaw", 0, "skill", "block", "--", "pdf-tools"));
            var restart = panel.CreateRow(Finished("defenseclaw-gateway", 0, "restart"));
            var read = panel.CreateRow(Finished("defenseclaw", 0, "doctor"));
            var running = panel.CreateRow(InvocationFactory.Create(false, "setup", "guardrail"));
            foreach (var row in new[] { change, restart, read, running })
            {
                panel.Rows.Add(row);
            }

            panel.SetActive(true);
            try
            {
                Assert.True(change.CanRerun);
                Assert.True(restart.CanRerun);
                Assert.False(running.CanRerun);
                Assert.Contains("Wait for this command to finish", running.RerunHint, StringComparison.Ordinal);

                // config.yaml turns managed while the panel is on screen: the finished rows are not ticked, and still follow.
                services.Installation.Replace(TestInstallations.ManagedAt(_temp.Path));

                foreach (var row in new[] { change, restart, running })
                {
                    Assert.True(row.CanOfferRerun, row.ShortCommand);
                    Assert.False(row.CanRerun, row.ShortCommand);
                    Assert.False(row.RerunCommand.CanExecute(null), row.ShortCommand);
                    Assert.Equal(TestInstallations.ManagedReason, row.RerunHint);
                }

                Assert.True(read.CanRerun);
                Assert.True(read.RerunCommand.CanExecute(null));

                services.Installation.Replace(TestInstallations.UserDefault());

                Assert.True(change.CanRerun);
                Assert.True(restart.CanRerun);
                Assert.True(read.CanRerun);
                Assert.False(running.CanRerun);
                Assert.Contains("Wait for this command to finish", running.RerunHint, StringComparison.Ordinal);
            }
            finally
            {
                panel.SetActive(false);
            }
        });
    }

    [Fact]
    public void A_panel_that_was_away_when_the_installation_turned_read_only_catches_up_when_it_comes_back()
    {
        var services = TestServices.Create(_temp);
        _services.Add(services);

        UiThread.Run(() =>
        {
            var panel = new ActivityPanelViewModel(services);
            var change = panel.CreateRow(Finished("defenseclaw", 0, "skill", "block", "--", "pdf-tools"));
            panel.Rows.Add(change);
            Assert.True(change.CanRerun);

            // Not the panel on screen: it is not told.
            services.Installation.Replace(TestInstallations.ManagedAt(_temp.Path));
            Assert.True(change.CanRerun);

            panel.SetActive(true);
            try
            {
                Assert.False(change.CanRerun);
                Assert.Equal(TestInstallations.ManagedReason, change.RerunHint);
            }
            finally
            {
                panel.SetActive(false);
            }
        });
    }

    [Fact]
    public void A_row_the_panel_builds_on_a_read_only_installation_is_off_from_the_start()
    {
        var services = TestServices.Create(_temp, installation: TestInstallations.ManagedAt(_temp.Path));
        _services.Add(services);

        UiThread.Run(() =>
        {
            var panel = new ActivityPanelViewModel(services);

            var change = panel.CreateRow(Finished("defenseclaw", 1, "setup", "guardrail"));
            var read = panel.CreateRow(Finished("defenseclaw", 0, "skill", "list"));

            Assert.True(change.CanOfferRerun);
            Assert.False(change.CanRerun);
            Assert.Equal(TestInstallations.ManagedReason, change.RerunHint);
            Assert.True(read.CanRerun);
        });
    }

    [Fact]
    public void In_the_real_panel_a_changes_Rerun_is_drawn_off_with_the_sentence_as_its_tooltip_and_a_reads_is_on_and_both_follow_the_installation()
    {
        using var scene = ActivityScene.Open(940, 620, TestInstallations.ManagedAt);

        UiThread.Run(() =>
        {
            scene.ViewModel.SetActive(true);
            try
            {
                var read = scene.ViewModel.CreateRow(Finished("defenseclaw", 0, "doctor"));
                var restart = scene.ViewModel.CreateRow(Finished("defenseclaw-gateway", 0, "restart"));
                var change = scene.ViewModel.CreateRow(Finished("defenseclaw", 1, "skill", "block", "--", "pdf-tools"));
                foreach (var row in new[] { read, restart, change })
                {
                    scene.ViewModel.Rows.Insert(0, row);
                }

                scene.ViewModel.IsEmpty = false;
                scene.Host.Relayout();

                foreach (var row in new[] { change, restart })
                {
                    var button = RerunIn(scene.CardOf(row)!);
                    Assert.True(button.IsVisible, row.ShortCommand);
                    Assert.False(button.IsEnabled, row.ShortCommand);
                    Assert.Equal(TestInstallations.ManagedReason, button.ToolTip);
                    Assert.True(ToolTipService.GetShowOnDisabled(button));
                    Assert.Equal(TestInstallations.ManagedReason, AutomationProperties.GetHelpText(button));
                }

                var allowed = RerunIn(scene.CardOf(read)!);
                Assert.True(allowed.IsEnabled);
                Assert.Contains("Nothing runs until you confirm", (string)allowed.ToolTip, StringComparison.Ordinal);
                scene.Render("activity-rerun-read-only-940x620");

                // The installation is writable again: the same buttons come on, with no reload of the list.
                scene.Services.Installation.Replace(TestInstallations.UserDefault());
                scene.Host.Relayout();

                Assert.True(RerunIn(scene.CardOf(change)!).IsEnabled);
                Assert.True(RerunIn(scene.CardOf(restart)!).IsEnabled);
                Assert.Contains("Nothing runs until you confirm", (string)RerunIn(scene.CardOf(change)!).ToolTip, StringComparison.Ordinal);
            }
            finally
            {
                scene.ViewModel.SetActive(false);
            }
        });
    }

    // ------------------------------------------------------------------ the review a Rerun opens

    private static CommandReviewControl Build(CommandReview review)
    {
        var control = new CommandReviewControl { Review = review };
        control.Measure(new Size(520, 2000));
        control.Arrange(new Rect(0, 0, 520, Math.Max(control.DesiredSize.Height, 1)));
        control.UpdateLayout();
        return control;
    }

    [Fact]
    public void The_review_of_a_change_cannot_be_confirmed_a_destructive_one_still_opens_on_Cancel_and_a_read_is_as_ever()
    {
        var services = TestServices.Create(_temp, installation: TestInstallations.ManagedAt(_temp.Path));
        _services.Add(services);
        var rerun = new CommandRerun(services, owner: () => null);

        UiThread.Run(() =>
        {
            var change = Build(rerun.ReviewFor(Finished("defenseclaw", 0, "skill", "block", "--", "pdf-tools"), CommandTier.StateChanging));
            Assert.False(change.ConfirmButton.IsEnabled);
            Assert.Equal(TestInstallations.ManagedReason, change.ConfirmButton.ToolTip);
            Assert.True(ToolTipService.GetShowOnDisabled(change.ConfirmButton));
            Assert.Equal(TestInstallations.ManagedReason, AutomationProperties.GetHelpText(change.ConfirmButton));
            Assert.Contains(
                change.WarningList.Items.Cast<CommandReviewWarning>(),
                w => w.Title == CommandReviewWarning.ReadOnlyInstallationTitle && w.Message == TestInstallations.ManagedReason);

            var destructive = Build(rerun.ReviewFor(Finished("defenseclaw", 0, "skill", "quarantine", "--", "pdf-tools"), CommandTier.Destructive));
            Assert.False(destructive.ConfirmButton.IsEnabled);
            Assert.True(destructive.CancelButton.IsEnabled);
            Assert.Same(destructive.CancelButton, destructive.InitialFocusTarget());
            Assert.Equal("Destructive", destructive.TierText.Text);

            var read = Build(rerun.ReviewFor(Finished("defenseclaw", 0, "doctor")));
            Assert.True(read.ConfirmButton.IsEnabled);
            Assert.Null(read.ConfirmButton.ToolTip);
            Assert.DoesNotContain(
                read.WarningList.Items.Cast<CommandReviewWarning>(),
                w => w.Title == CommandReviewWarning.ReadOnlyInstallationTitle);
        });
    }

    // ------------------------------------------------------------------ the palette

    [Fact]
    public async Task The_palette_lists_a_recent_command_the_installation_blocks_greyed_out_with_the_sentence_and_still_the_Recent_chip()
    {
        var terraform = new FixedTerraformProbe(new TerraformStatus(TerraformState.Ready, "Terraform 1.9.5 is available.", "1.9.5", @"C:\synthetic\bin\terraform.exe"));
        var services = AppServices.CreateIsolated(
            TestServices.IsolatedPaths(_temp.Path, TestInstallations.ManagedAt(_temp.Path)),
            claudeSettingsPath: _temp.File("claude-settings.json"),
            terraformProbe: terraform);
        _services.Add(services);
        await services.Terraform.RefreshAsync();
        var store = AppSettingsStore.OpenFresh(_temp.File("palette-settings.json"));
        _ = store.Update(s => s with
        {
            Palette = new PaletteSettings { RecentCommandIds = new[] { "cli.setup.splunk.dashboards.destroy", "cli.doctor", "cli.setup.guardrail" } },
        });
        var tray = (TrayIconService)RuntimeHelpers.GetUninitializedObject(typeof(TrayIconService));

        UiThread.Run(() =>
        {
            var catalog = new PanelCatalog(services);
            var actions = new ShellActions(services, catalog, tray, () => null) { SnapshotSource = () => new GatewaySnapshot { Install = InstallState.Running, State = AppGatewayState.Running } };
            var rows = ShellCommandRegistry.Build(catalog, actions, _ => { }, () => { }, curated: CuratedCommandCatalog.For(null).Commands);
            var palette = new CommandPaletteViewModel { RecentsStore = store };
            palette.Load(rows);

            var control = new CommandPaletteControl { DataContext = palette };
            using var host = new OffscreenHost(control, 760, 700);
            host.Relayout();

            var recents = palette.Results.Take(3).ToList();
            Assert.Equal(new[] { "cli.setup.splunk.dashboards.destroy", "cli.doctor", "cli.setup.guardrail" }, recents.Select(r => r.Command.Id));
            Assert.Equal(new[] { false, true, false }, recents.Select(r => r.IsEnabled));
            Assert.Equal("cli.doctor", palette.Selected!.Command.Id);

            foreach (var item in recents)
            {
                var container = VisualTree.Descendants<ListBoxItem>(control).First(c => ReferenceEquals(c.DataContext, item));
                var texts = VisualTree.Descendants<TextBlock>(container).Where(t => t.IsVisible).Select(t => t.Text).ToList();

                Assert.Equal(item.IsEnabled, container.IsEnabled);
                Assert.Contains("Recent", texts);
                Assert.Contains(item.IsEnabled ? item.Command.Description : TestInstallations.ManagedReason, texts);
                Assert.Equal(item.AutomationName, AutomationProperties.GetName(container));
            }

            RenderTo.Png(host, "palette-recents-read-only");
        });
    }
}
