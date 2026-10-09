using System.Windows;
using System.Windows.Controls;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Panels;

namespace DefenseClaw.App.Tests.Alerts;

/// <summary>
/// CUST-261 on the Alerts panel as a real view: the Connector column that exists only while more than one connector is active (and is not there at all
/// otherwise, so a one-connector install has the table it always had), the columns that share the room either way, and the inspector's Correlation rows.
/// A PNG of the table and of the inspector is written when <c>DC_RENDER_DIR</c> names a folder.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class AlertsConnectorColumnViewTests
{
    private static readonly string[] Plain = { "Time", "Severity", "Kind", "Action", "Target", "Details", "Run", "Actions" };

    private static readonly string[] WithConnector = { "Time", "Severity", "Kind", "Action", "Connector", "Target", "Details", "Run", "Actions" };

    private static string[] Headers(DataGrid grid) => grid.Columns.Select(c => (string)c.Header).ToArray();

    [Fact]
    public void One_connector_has_the_columns_it_always_had()
    {
        using var scene = Scene.Open(1400, 900, "claudecode");

        UiThread.Run(() =>
        {
            Assert.False(scene.ViewModel.ShowConnectorColumn);
            Assert.Equal(Plain, Headers(scene.Grid));
        });
    }

    [Fact]
    public void Two_connectors_add_the_column_after_Action_and_the_cells_name_the_connector()
    {
        using var scene = Scene.Open(1400, 900, "claudecode", "codex");

        UiThread.Run(() =>
        {
            Assert.True(scene.ViewModel.ShowConnectorColumn);
            Assert.Equal(WithConnector, Headers(scene.Grid));

            var cells = VisualTree.Descendants<TextBlock>(scene.Grid).Where(t => t.Text is "claudecode" or "codex" or "—").Select(t => t.Text).ToList();
            Assert.Contains("claudecode", cells);
            Assert.Contains("codex", cells);
            Assert.Contains("—", cells);

            // A sortable column with a key of its own.
            var column = scene.Grid.Columns.Single(c => (string)c.Header == "Connector");
            Assert.Equal("ConnectorCell", column.SortMemberPath);
        });
    }

    [Fact]
    public void The_column_comes_and_goes_with_the_roster_in_the_same_place_while_the_panel_is_on_screen()
    {
        using var scene = Scene.Open(1400, 900, "claudecode");

        UiThread.Run(() =>
        {
            Assert.Equal(Plain, Headers(scene.Grid));

            scene.Services.ConnectorScope.UpdateRoster(new[] { "claudecode", "codex" });
            scene.Host.Relayout();
            Assert.Equal(WithConnector, Headers(scene.Grid));

            scene.Services.ConnectorScope.UpdateRoster(new[] { "claudecode" });
            scene.Host.Relayout();
            Assert.Equal(Plain, Headers(scene.Grid));

            scene.Services.ConnectorScope.UpdateRoster(new[] { "claudecode", "codex", "openclaw" });
            scene.Host.Relayout();
            Assert.Equal(WithConnector, Headers(scene.Grid));
            Assert.Single(scene.Grid.Columns, c => (string)c.Header == "Connector");
        });
    }

    [Theory]
    [InlineData(1400, 900)]
    [InlineData(940, 620)]
    public void The_columns_share_the_room_with_the_column_and_without_it_and_leave_no_dead_space(int width, int height)
    {
        using var scene = Scene.Open(width, height, "claudecode");

        UiThread.Run(() =>
        {
            var plain = SharedWidth(scene);
            scene.Services.ConnectorScope.UpdateRoster(new[] { "claudecode", "codex" });
            scene.Host.Relayout();
            var with = SharedWidth(scene);
            scene.Services.ConnectorScope.UpdateRoster(new[] { "claudecode" });
            scene.Host.Relayout();
            var without = SharedWidth(scene);

            // Each time the columns add up to the room the table has (one DIP short of it, so no sideways scroll bar), and the new column has its floor.
            // Each time the columns add up to the room the table has (one DIP short of it, so no sideways scroll bar) - or, where even the floors of
            // the columns do not fit, to the floors: every column at its floor, and the table scrolls.
            Assert.InRange(plain.Total, plain.Room - 3, Math.Max(plain.Room, plain.Floors) + 1);
            Assert.InRange(with.Total, with.Room - 3, Math.Max(with.Room, with.Floors) + 1);
            Assert.InRange(without.Total, without.Room - 3, Math.Max(without.Room, without.Floors) + 1);
            Assert.True(with.Total >= with.Floors - 1, $"the columns are {with.Total} DIPs wide and their floors add up to {with.Floors}");
            Assert.True(with.Connector >= 64, $"the connector column is {with.Connector} DIPs wide");
        });
    }

    [Fact]
    public void The_inspector_lists_the_correlation_ids_as_selectable_text_and_leaves_the_section_out_when_there_are_none()
    {
        using var scene = Scene.Open(1400, 900, "claudecode", "codex");

        UiThread.Run(() =>
        {
            scene.ViewModel.SelectedAlert = scene.ViewModel.Alerts.Single(a => a.Key == "f-1");
            scene.Host.Relayout();

            var boxes = VisualTree.Descendants<TextBox>(scene.Inspector)
                .Where(t => System.Windows.Automation.AutomationProperties.GetName(t) is "Run ID" or "Trace ID" or "Request ID" or "Session ID")
                .ToList();
            Assert.Equal(
                new[] { ("Run ID", "run-aaa"), ("Trace ID", "4bf92f3577b34da6a3ce929d0e0e4736"), ("Request ID", "req-11111111"), ("Session ID", "ses-aaaa") },
                boxes.Select(b => (System.Windows.Automation.AutomationProperties.GetName(b), b.Text)).ToArray());
            Assert.All(boxes, b => Assert.True(b.IsReadOnly && b.IsVisible));
            Assert.Contains(VisualTree.Descendants<TextBlock>(scene.Inspector), t => t.Text == "Correlation");

            scene.Render("cust261-alerts-inspector-correlation");

            // No ids, no section.
            scene.ViewModel.SelectedAlert = scene.ViewModel.Alerts.Single(a => a.Key == "f-3");
            scene.Host.Relayout();
            Assert.DoesNotContain(
                VisualTree.Descendants<TextBox>(scene.Inspector),
                t => t.IsVisible && System.Windows.Automation.AutomationProperties.GetName(t) is "Run ID" or "Trace ID" or "Request ID" or "Session ID");
        });
    }

    [Fact]
    public void The_panel_tells_its_view_when_the_roster_changes_while_it_is_on_screen_and_not_otherwise()
    {
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp);

        UiThread.Run(() =>
        {
            var vm = new AlertsPanelViewModel(services);
            var raised = 0;
            vm.PropertyChanged += (_, e) => raised += e.PropertyName == nameof(AlertsPanelViewModel.ShowConnectorColumn) ? 1 : 0;

            // Away: nobody is told (the view asks again when the panel comes back).
            services.ConnectorScope.UpdateRoster(new[] { "claudecode", "codex" });
            Assert.Equal(0, raised);

            vm.SetActive(true);
            Assert.Equal(1, raised);

            services.ConnectorScope.UpdateRoster(new[] { "claudecode" });
            Assert.Equal(2, raised);
            services.ConnectorScope.UpdateRoster(new[] { "claudecode", "codex", "openclaw" });
            Assert.Equal(3, raised);

            vm.SetActive(false);
            services.ConnectorScope.UpdateRoster(new[] { "claudecode" });
            Assert.Equal(3, raised);
        });
    }

    [Fact]
    public void Rendered_with_the_connector_column_and_a_row_selected()
    {
        using var scene = Scene.Open(1400, 900, "claudecode", "codex");

        UiThread.Run(() =>
        {
            scene.ViewModel.SelectedAlert = scene.ViewModel.Alerts.Single(a => a.Key == "f-2");
            scene.Host.Relayout();

            Assert.Equal(WithConnector, Headers(scene.Grid));
            scene.Render("cust261-alerts-connector-column");
        });
    }

    private static (double Total, double Room, double Floors, double Connector) SharedWidth(Scene scene)
    {
        var scroller = VisualTree.Find<ScrollViewer>(scene.Grid)!;
        var shown = scene.Grid.Columns.Where(c => c.Visibility == Visibility.Visible).ToList();
        var connector = scene.Grid.Columns.FirstOrDefault(c => (string)c.Header == "Connector")?.ActualWidth ?? 0;

        // The row menu's column is a plain 32 DIPs wide, which is also its floor.
        var floors = shown.Sum(c => (string)c.Header == "Actions" ? 32 : c.MinWidth);
        return (shown.Sum(c => c.ActualWidth), Math.Floor(scroller.ViewportWidth) - 1, floors, connector);
    }

    private sealed class Scene : IDisposable
    {
        private static readonly DateTimeOffset Base = DateTimeOffset.UtcNow.AddHours(-3);

        private readonly TempDirectory _temp = new();

        private Scene(int width, int height, string[] roster)
        {
            Services = TestServices.Create(_temp);
            _ = new AlertQueueDatabase(Services.Paths.AuditDatabasePath);
            var db = Services.Paths.AuditDatabasePath;
            CorrelatedRows.Add(db, "f-1", Base.AddMinutes(1), "scan-finding", "HIGH", "claudecode", target: "/skills/evil", actor: "scanner", details: "Calls out to an unknown host",
                runId: "run-aaa", traceId: "4bf92f3577b34da6a3ce929d0e0e4736", requestId: "req-11111111", sessionId: "ses-aaaa");
            CorrelatedRows.Add(db, "f-2", Base.AddMinutes(2), "skill-block", "CRITICAL", "codex", target: "evil-skill", actor: "cli", details: "Blocked by policy",
                runId: "run-bbb", traceId: "00f067aa0ba902b700f067aa0ba902b7", requestId: "req-22222222", sessionId: "ses-bbbb");
            CorrelatedRows.Add(db, "f-3", Base.AddMinutes(3), "guardrail-block", "MEDIUM", "claudecode", target: "skills/my skill", actor: "gateway", details: "Prompt injection attempt");
            CorrelatedRows.Add(db, "f-4", Base.AddMinutes(4), "config-drift", "LOW", null, target: "config.yaml", details: "Configuration changed on disk");

            if (roster.Length > 0)
            {
                Services.ConnectorScope.UpdateRoster(roster);
            }

            Shell = UiThread.Run(() =>
            {
                var shell = new PanelShell(Services, width, height);
                Panel = shell.Show<AlertsPanel>();
                return shell;
            });
            ViewModel = UiThread.Run(() => (AlertsPanelViewModel)Shell.ViewModel);

            UiThread.WaitFor(() => ViewModel.Alerts.Count == 4, "alerts loaded");
            UiThread.Run(() => Host.Relayout());
        }

        public static Scene Open(int width, int height, params string[] roster) => new(width, height, roster);

        public AppServices Services { get; }

        public PanelShell Shell { get; }

        public AlertsPanel Panel { get; private set; } = null!;

        public AlertsPanelViewModel ViewModel { get; }

        public OffscreenHost Host => Shell.Host;

        public DataGrid Grid => (DataGrid)Panel.FindName("AlertList");

        public DefenseClaw.App.Views.Controls.DcInspector Inspector => (DefenseClaw.App.Views.Controls.DcInspector)Panel.FindName("Inspector");

        public void Render(string name)
        {
            // The inspector fades in over 120 ms; when a person is going to look at the picture, give it the time to land.
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(RenderTo.EnvironmentVariable)))
            {
                var until = Environment.TickCount64 + 400;
                while (Environment.TickCount64 < until)
                {
                    Thread.Sleep(20);
                    UiThread.Settle();
                }
            }

            RenderTo.Png(Host, name);
        }

        public void Dispose()
        {
            UiThread.Run(Shell.Dispose);
            Services.Dispose();
            SqlitePools.Release(_temp.Path);
            _temp.Dispose();
        }
    }
}
