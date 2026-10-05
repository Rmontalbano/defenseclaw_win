using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Panels;
using DefenseClaw.Core.Cli;
using InfoBar = Wpf.Ui.Controls.InfoBar;

namespace DefenseClaw.App.Tests.Setup;

/// <summary>
/// The Setup panel's Readiness and Credentials cards, hosted offscreen with synthetic rows (all names are made up). Set
/// <c>DC_RENDER_DIR</c> to also write a PNG.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class SetupCredentialsViewTests
{
    private static readonly CredentialRow[] Rows =
    {
        new("EXAMPLE_JUDGE_KEY", "EXAMPLE_JUDGE_KEY", "LLM judge", "required", "unset", false, "Key for the judge model"),
        new("EXAMPLE_SCANNER_KEY", "EXAMPLE_SCANNER_KEY", "Skill scanner", "required", "dotenv", true, "Scanner service key"),
        new("EXAMPLE_TELEMETRY_KEY", "EXAMPLE_TELEMETRY_KEY", "Telemetry", "optional", "unset", false, "Optional exporter key"),
        new("EXAMPLE_LEGACY_KEY", "EXAMPLE_LEGACY_KEY", "Legacy", "not_used", "env", true, "Not used by this config"),
    };

    private static readonly ReadinessCheck[] Checks =
    {
        new("Active Connector: claudecode", "configured", ReadinessStatus.Pass),
        new("Gateway / API Health", "Gateway health endpoint is offline.", ReadinessStatus.Fail,
            new ReadinessFix(ReadinessFixKind.Review, "start", new[] { new ReadinessFixStep("defenseclaw-gateway", new[] { "start" }, "Starts the gateway.") })),
        new("Guardrail", "enabled in observe mode", ReadinessStatus.Pass),
        new("Required Credentials", "1 required credential(s) missing", ReadinessStatus.Fail,
            new ReadinessFix(ReadinessFixKind.Terminal, "keys fill-missing", new[] { new ReadinessFixStep("defenseclaw", new[] { "keys", "fill-missing", "--yes" }, "Prompts.") })),
        new("LLM Config", "Unified llm.provider/model is incomplete.", ReadinessStatus.Warn,
            new ReadinessFix(ReadinessFixKind.Wizard, "setup llm", new[] { new ReadinessFixStep("defenseclaw", new[] { "setup", "llm" }, "Wizard.") }, WizardTarget: "llm")),
        new("Scanner Availability", "Scanner binaries are not configured.", ReadinessStatus.Warn,
            new ReadinessFix(ReadinessFixKind.Review, "doctor --fix", new[]
            {
                new ReadinessFixStep("defenseclaw", new[] { "doctor", "--fix", "--dry-run" }, "Preview."),
                new ReadinessFixStep("defenseclaw", new[] { "doctor", "--fix", "--yes" }, "Apply."),
            })),
        new("Observability v8", "Canonical routing is active; local SQLite collection is mandatory.", ReadinessStatus.Pass),
        new("Registry / Asset Policy", "Registry policy is ready or not required.", ReadinessStatus.Pass),
        new("Restart Pending", "No queued restart.", ReadinessStatus.Pass),
    };

    [Fact]
    public void The_cards_render_the_rows_with_a_fix_per_failing_row_and_a_set_button_per_credential()
    {
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp);
        var shell = UiThread.Run(() =>
        {
            var s = new PanelShell(services, 1400, 1500);
            _ = s.Show<SetupPanel>();
            return s;
        });
        try
        {
            var vm = UiThread.Run(() => (SetupPanelViewModel)shell.ViewModel);
            UiThread.WaitFor(() => !vm.IsLoading && !vm.Credentials.IsLoading, "setup catalog and the (unavailable) credential read finished");

            UiThread.Run(() =>
            {
                vm.Credentials.ApplyRows(Rows);
                vm.Readiness.Apply(Checks);
                shell.Host.Relayout();

                var page = shell.Page!;

                // Readiness: every row, and a Fix button only on rows that have one, named for what it fixes.
                var fixes = VisualTree.Descendants<Wpf.Ui.Controls.Button>(page)
                    .Where(b => AutomationProperties.GetName(b).StartsWith("Fix ", StringComparison.Ordinal)).ToArray();
                Assert.Equal(Checks.Count(c => c.Fix is not null), fixes.Count(b => b.IsVisible));
                Assert.Contains(fixes, b => AutomationProperties.GetName(b) == "Fix Scanner Availability" && b.Command.CanExecute(null));
                Assert.All(fixes.Where(b => b.IsVisible), b => Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetHelpText(b))));

                // Credentials: one Set… per row, named by variable, never a value anywhere on the page.
                var sets = VisualTree.Descendants<Wpf.Ui.Controls.Button>(page)
                    .Where(b => AutomationProperties.GetName(b) is var n && n.StartsWith("Set EXAMPLE_", StringComparison.Ordinal) && n.EndsWith("in a terminal", StringComparison.Ordinal)).ToArray();
                Assert.Equal(Rows.Length, sets.Length);
                Assert.Contains(sets, b => AutomationProperties.GetName(b) == "Set EXAMPLE_JUDGE_KEY in a terminal");

                var texts = VisualTree.Descendants<TextBlock>(page).Select(t => t.Text).ToArray();
                Assert.Contains("1 required credential is missing.", texts);
                Assert.Contains("MISSING", texts);
                Assert.Contains("✓ set", texts);
                Assert.Contains("EXAMPLE_JUDGE_KEY", texts);

                // The toolbar of the card.
                var buttons = VisualTree.Descendants<Wpf.Ui.Controls.Button>(page).Select(b => AutomationProperties.GetName(b)).ToArray();
                Assert.Contains("Refresh the credential list", buttons);
                Assert.Contains("Check required credentials", buttons);
                Assert.Contains("Fill missing credentials in a terminal", buttons);

                // Bring both cards into view and write the PNG when asked.
                var scroll = VisualTree.Descendants<ScrollViewer>(page).First(sv => sv.ScrollableHeight > 0);
                var heading = VisualTree.Descendants<TextBlock>(page).First(t => t.Text == "Readiness");
                var content = (UIElement)scroll.Content;
                scroll.ScrollToVerticalOffset(Math.Max(0, heading.TranslatePoint(new Point(), content).Y - 70));
                shell.Host.Relayout();
                RenderTo.Png(shell.Host, "cust266-setup-credentials-1400x1500");
            });
        }
        finally
        {
            UiThread.Run(shell.Dispose);
        }
    }

    [Fact]
    public void An_unreadable_list_shows_the_error_bar_and_the_empty_and_not_loaded_states_say_so()
    {
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp);
        var shell = UiThread.Run(() =>
        {
            var s = new PanelShell(services, 1400, 1500);
            _ = s.Show<SetupPanel>();
            return s;
        });
        try
        {
            var vm = UiThread.Run(() => (SetupPanelViewModel)shell.ViewModel);
            UiThread.WaitFor(() => !vm.IsLoading && !vm.Credentials.IsLoading, "setup catalog and the (unavailable) credential read finished");

            UiThread.Run(() =>
            {
                shell.Host.Relayout();

                // The test services have no CLI, so the activation read failed: the error bar is open, there are no rows.
                var bar = VisualTree.Descendants<InfoBar>(shell.Page!).Single(b => b.Title == "The credential list could not be read");
                Assert.True(bar.IsOpen);
                Assert.Contains("not found", bar.Message, StringComparison.OrdinalIgnoreCase);

                vm.Credentials.ApplyRows(Array.Empty<CredentialRow>());
                shell.Host.Relayout();
                Assert.False(bar.IsOpen);
                Assert.Contains(VisualTree.Descendants<TextBlock>(shell.Page!), t => t.Text == "No credentials to report." && t.IsVisible);
            });
        }
        finally
        {
            UiThread.Run(shell.Dispose);
        }
    }
}
