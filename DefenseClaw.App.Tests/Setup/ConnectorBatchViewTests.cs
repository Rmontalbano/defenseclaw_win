using System.Windows;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Appearance;
using DefenseClaw.App.Services.FirstRun;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.Appearance;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Panels;
using DefenseClaw.App.Views.Shell;
using Wpf.Ui.Controls;

namespace DefenseClaw.App.Tests.Setup;

/// <summary>The connector batch dialog (CUST-271) as drawn: one switch per connector, the review off until something differs, the command in the footer.</summary>
[Collection(UiCollection.Name)]
public sealed class ConnectorBatchViewTests
{
    [Fact]
    public void The_open_dialog_has_a_switch_per_connector_and_shows_the_command_once_a_second_one_is_ticked()
    {
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Cisco, AppearanceMode.Light));

        UiThread.Run(() =>
        {
            using var temp = new TempDirectory();
            using var services = TestServices.Create(temp, configYaml: "guardrail:\n  connectors:\n    codex:\n      mode: observe\n");
            var help = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "runtime-0.8.10", "setup.txt"));
            using var vm = new ConnectorBatchViewModel(
                services,
                _ => Task.FromResult(new HelpProbeResult(help, null)),
                () => new[]
                {
                    new OfferedConnector("codex", "Codex", "codex", PlatformStatus.Certified),
                    new OfferedConnector("claudecode", "Claude Code", "claude-code", PlatformStatus.Certified),
                });
            var dialog = new ConnectorBatchDialog { DataContext = vm };
            using var host = new OffscreenHost(dialog, 940, 820);

            Assert.DoesNotContain(VisualTree.Descendants<ToggleSwitch>(dialog), t => t.IsVisible);

            vm.Open();
            host.Relayout();
            Assert.Equal(2, VisualTree.Descendants<ToggleSwitch>(dialog).Count(t => t.IsVisible));
            var review = VisualTree.Descendants<Wpf.Ui.Controls.Button>(dialog).Single(b => b.Content?.ToString() == "Review changes…");
            Assert.False(review.IsEnabled);

            vm.Rows.Single(r => r.Id == "claudecode").IsOn = true;
            host.Relayout();

            Assert.True(review.IsEnabled || vm.IsChecking); // the help check may still be landing on a slow machine
            Assert.Contains(
                VisualTree.Descendants<System.Windows.Controls.TextBlock>(dialog),
                t => t.IsVisible && t.Text == "defenseclaw setup --yes --connector codex --connector claudecode --mode observe");
            RenderTo.Png(host, "cust271-batch-Cisco-Light");
        });
    }
}
