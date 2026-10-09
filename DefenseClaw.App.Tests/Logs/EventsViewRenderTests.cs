using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Panels;
using Microsoft.Data.Sqlite;
using Wpf.Ui.Controls;

namespace DefenseClaw.App.Tests.Logs;

/// <summary>
/// CUST-262 on the real views: the Logs panel's Events view with its "Actionable only" switch and the chip that counts what it leaves out, and the
/// "Events (all activity)" button on Alerts that opens it. Synthetic rows from the real DDL, hosted offscreen on the shared UI thread; a PNG of the Events
/// view is written when <c>DC_RENDER_DIR</c> names a folder.
/// </summary>
[Collection(UiCollection.Name)]
public class EventsViewRenderTests
{
    private static readonly DateTimeOffset Base = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static void Seed(string path)
    {
        AuditTestDatabase.Create(path, rows: 0);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        connection.Open();

        void Add(string id, int minute, string bucket, string eventName, string severity, string? details, string? payload, string connector = "claudecode")
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO audit_events (id, timestamp, action, target, actor, details, severity, bucket, event_name, connector, source, signal, payload_json)
                VALUES ($id, $timestamp, 'act', '', 'gateway', $details, $severity, $bucket, $eventName, $connector, 'sidecar', 'logs', $payload)
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$timestamp", Base.AddMinutes(minute).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture) + "Z");
            command.Parameters.AddWithValue("$details", (object?)details ?? DBNull.Value);
            command.Parameters.AddWithValue("$severity", severity);
            command.Parameters.AddWithValue("$bucket", bucket);
            command.Parameters.AddWithValue("$eventName", eventName);
            command.Parameters.AddWithValue("$connector", connector);
            command.Parameters.AddWithValue("$payload", (object?)payload ?? DBNull.Value);
            _ = command.ExecuteNonQuery();
        }

        Add("e-high", 1, "guardrail.evaluation", "guardrail.evaluated", "HIGH", "matched a rule: prompt injection", """{"defenseclaw.guardrail.decision":"block","defenseclaw.guardrail.reason":"matched a rule: prompt injection"}""");
        Add("e-quiet-1", 2, "guardrail.evaluation", "guardrail.evaluated", "INFO", null, """{"defenseclaw.guardrail.decision":"allow"}""");
        Add("e-block", 3, "enforcement.action", "enforcement.applied", "INFO", "skill install blocked", """{"defenseclaw.enforcement.effective_action":"block"}""", "codex");
        Add("e-quiet-2", 4, "asset.scan", "scan.completed", "MEDIUM", "scan done: 3 findings", """{"defenseclaw.scan.verdict":"allow"}""");
        Add("e-egress", 5, "network.egress", "egress.decided", "INFO", null, """{"defenseclaw.network.decision":"block","defenseclaw.network.target_ref":"api.example.test","defenseclaw.network.reason":"host is not on the allow list"}""");
        Add("e-quiet-3", 6, "platform.health", "sink.checked", "INFO", "sink ok", null);
        Add("e-error", 7, "platform.health", "sink.checked", "ERROR", "sink unreachable: connection refused", null);
        Add("e-quiet-4", 8, "diagnostic", "boot.checked", "LOW", "boot checks passed", null);
        SqlitePools.Release(path);
    }

    private static ToggleSwitch? Switch(FrameworkElement page) =>
        VisualTree.Find<ToggleSwitch>(page, t => System.Windows.Automation.AutomationProperties.GetName(t) == "Actionable only");

    [Fact]
    public void The_events_view_has_the_switch_and_the_chip_and_lists_only_the_actionable_events()
    {
        using var temp = new TempDirectory();
        Seed(Path.Combine(temp.Path, "audit.db"));
        using var services = TestServices.Create(temp);
        PanelShell? shell = null;
        try
        {
            UiThread.Run(() =>
            {
                shell = new PanelShell(services, 1400, 900);
                _ = shell.Show<LogsPanel>();
            });
            var viewModel = UiThread.Run(() => (LogsPanelViewModel)shell!.ViewModel);

            var load = UiThread.Run(() =>
            {
                viewModel.SetActive(true);
                viewModel.ActiveSource = LogsPanelViewModel.EventsSource;
                return viewModel.LoadStructuredAsync();
            });
            UiThread.WaitFor(() => load.IsCompleted && viewModel.DisplayedLines.Count > 0, "the events to load");

            UiThread.Run(() =>
            {
                shell!.Host.Relayout();
                var page = shell.Page!;
                var toggle = Switch(page);
                Assert.NotNull(toggle);
                Assert.True(toggle!.IsVisible);
                Assert.True(toggle.IsEnabled);
                Assert.True(toggle.IsChecked);

                // Four of the eight events are what the TUI shows; the chip counts the four it leaves out.
                Assert.Equal(4, viewModel.DisplayedLines.Count);
                var texts = VisualTree.Descendants<System.Windows.Controls.TextBlock>(page).Where(t => t.IsVisible).Select(t => t.Text).ToList();
                Assert.Contains("4 low-signal hidden", texts);
            });

            // A picture, when one is asked for, is taken once the switch's own animation has finished (it slides to its state when it first shows).
            if (Environment.GetEnvironmentVariable(RenderTo.EnvironmentVariable) is { Length: > 0 })
            {
                Thread.Sleep(700);
            }

            UiThread.Run(() =>
            {
                shell!.Host.Relayout();
                RenderTo.Png(shell.Host, "cust262-events-actionable");

                // Verdicts is not narrowed: the switch and the chip go with the stream they belong to.
                var page = shell.Page!;
                var toggle = Switch(page)!;
                viewModel.ActiveSource = LogsPanelViewModel.VerdictsSource;
                shell.Host.Relayout();
                Assert.False(toggle.IsVisible);
                Assert.DoesNotContain(VisualTree.Descendants<System.Windows.Controls.TextBlock>(page).Where(t => t.IsVisible).Select(t => t.Text), t => t.Contains("low-signal hidden", StringComparison.Ordinal));
                viewModel.ActiveSource = LogsPanelViewModel.EventsSource;
            });

            var again = UiThread.Run(() => viewModel.LoadStructuredAsync());
            UiThread.WaitFor(() => again.IsCompleted, "the events to load again");
            UiThread.Run(() =>
            {
                // Switched off: every event, and no chip.
                var toggle = Switch(shell!.Page!)!;
                toggle.IsChecked = false;
                shell.Host.Relayout();
                Assert.False(viewModel.ActionableOnly);
                Assert.Equal(8, viewModel.DisplayedLines.Count);
                Assert.DoesNotContain(VisualTree.Descendants<System.Windows.Controls.TextBlock>(shell.Page!).Where(t => t.IsVisible).Select(t => t.Text), t => t.Contains("low-signal hidden", StringComparison.Ordinal));

                // On again, then a search: the switch stays checked but is off while the search is on, and says why.
                toggle.IsChecked = true;
                viewModel.FilterText = "platform";
                shell.Host.Relayout();
                Assert.True(toggle.IsChecked);
                Assert.False(toggle.IsEnabled);
                Assert.Contains("paused while a search is on", (string)toggle.ToolTip, StringComparison.Ordinal);
                Assert.DoesNotContain(VisualTree.Descendants<System.Windows.Controls.TextBlock>(shell.Page!).Where(t => t.IsVisible).Select(t => t.Text), t => t.Contains("low-signal hidden", StringComparison.Ordinal));
                viewModel.FilterText = string.Empty;
                shell.Host.Relayout();
                Assert.True(toggle.IsEnabled);
            });
        }
        finally
        {
            UiThread.Run(() =>
            {
                if (shell?.ViewModel is PanelViewModelBase { IsActive: true } panel)
                {
                    panel.SetActive(false);
                }

                shell?.Dispose();
            });
            SqlitePools.Release(temp.Path);
        }
    }

    [Fact]
    public void Alerts_has_an_events_button_that_asks_the_shell_for_the_events_view()
    {
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp);
        PanelShell? shell = null;
        try
        {
            UiThread.Run(() =>
            {
                shell = new PanelShell(services, 1400, 900);
                var page = shell.Show<AlertsPanel>();
                var button = VisualTree.Find<Wpf.Ui.Controls.Button>(page, b => System.Windows.Automation.AutomationProperties.GetName(b) == "Events (all activity)");

                Assert.NotNull(button);
                Assert.True(button!.IsVisible);
                Assert.True(button.IsEnabled);
                Assert.Equal("Events (all activity)", button.Content);
                Assert.Same(((AlertsPanelViewModel)shell.ViewModel).OpenEventsCommand, button.Command);

                var seen = new List<NavigationRequest>();
                services.Navigation.Requested += (_, e) => seen.Add(e.Request);
                button.Command.Execute(button.CommandParameter);

                var request = Assert.Single(seen);
                Assert.Equal("logs", request.PanelId);
                Assert.Equal(new LogsEvents(), request.Payload);
            });
        }
        finally
        {
            UiThread.Run(() => shell?.Dispose());
        }
    }
}
