using System.Windows;
using System.Windows.Controls;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Controls;
using DefenseClaw.App.Views.Panels;

namespace DefenseClaw.App.Tests.Audit;

/// <summary>
/// CUST-261 on the Audit panel as a real view: the Connector column after Type that exists only while more than one connector is active (and is not in the
/// table at all otherwise), the columns that share the room either way, a connector's hook call reading <c>claudecode · preToolUse</c> and
/// <c>allow · 320ms</c> in the table and laid out like the TUI's in the inspector, and the inspector's "Current state". A PNG of the table and of the
/// inspectors is written when <c>DC_RENDER_DIR</c> names a folder.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class AuditConnectorColumnViewTests
{
    private static readonly string[] Plain = { "Time", "Action", "Type", "Target", "Severity", "Run", "Details", "Actions" };

    private static readonly string[] WithConnector = { "Time", "Action", "Type", "Connector", "Target", "Severity", "Run", "Details", "Actions" };

    private static string[] Headers(DataGrid grid) => grid.Columns.Select(c => (string)c.Header).ToArray();

    private static IEnumerable<string> Texts(DependencyObject root) =>
        VisualTree.Descendants<TextBlock>(root).Where(t => t.IsVisible && t.Text.Length > 0).Select(t => t.Text);

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
    public void Two_connectors_add_the_column_after_Type_as_the_TUI_does()
    {
        using var scene = Scene.Open(1400, 900, "claudecode", "codex");

        UiThread.Run(() =>
        {
            Assert.True(scene.ViewModel.ShowConnectorColumn);
            Assert.Equal(WithConnector, Headers(scene.Grid));

            var cells = Texts(scene.Grid).ToList();
            Assert.Contains("claudecode", cells);
            Assert.Contains("codex", cells);
            Assert.Contains("—", cells);

            // Every sortable column has a key, the new one and the two whose cells are read from the details included.
            Assert.All(scene.Grid.Columns.Where(c => (string)c.Header != "Actions"), c => Assert.False(string.IsNullOrEmpty(c.SortMemberPath), $"{c.Header} has no sort key"));
            Assert.Equal("ConnectorCell", scene.Grid.Columns.Single(c => (string)c.Header == "Connector").SortMemberPath);
            Assert.Equal("TargetText", scene.Grid.Columns.Single(c => (string)c.Header == "Target").SortMemberPath);
            Assert.Equal("DetailsText", scene.Grid.Columns.Single(c => (string)c.Header == "Details").SortMemberPath);
        });
    }

    [Fact]
    public void The_column_comes_and_goes_with_the_roster_in_the_same_place_while_the_panel_is_on_screen()
    {
        using var scene = Scene.Open(1400, 900, "claudecode");

        UiThread.Run(() =>
        {
            scene.Services.ConnectorScope.UpdateRoster(new[] { "claudecode", "codex" });
            scene.Host.Relayout();
            Assert.Equal(WithConnector, Headers(scene.Grid));

            scene.Services.ConnectorScope.UpdateRoster(new[] { "claudecode" });
            scene.Host.Relayout();
            Assert.Equal(Plain, Headers(scene.Grid));

            scene.Services.ConnectorScope.UpdateRoster(new[] { "claudecode", "codex", "openclaw" });
            scene.Host.Relayout();
            Assert.Equal(WithConnector, Headers(scene.Grid));
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

            // Each time the columns add up to the room the table has (one DIP short of it, so no sideways scroll bar) - or, where even the floors of
            // the columns do not fit (the window's minimum with a ninth column), to the floors: every column at its floor, and the table scrolls.
            Assert.InRange(plain.Total, plain.Room - 3, Math.Max(plain.Room, plain.Floors) + 1);
            Assert.InRange(with.Total, with.Room - 3, Math.Max(with.Room, with.Floors) + 1);
            Assert.InRange(without.Total, without.Room - 3, Math.Max(without.Room, without.Floors) + 1);
            Assert.True(with.Total >= with.Floors - 1, $"the columns are {with.Total} DIPs wide and their floors add up to {with.Floors}");
            Assert.True(with.Connector >= 64, $"the connector column is {with.Connector} DIPs wide");
        });
    }

    [Fact]
    public void A_hook_call_reads_claudecode_preToolUse_and_allow_320ms_in_the_table()
    {
        using var scene = Scene.Open(1400, 900, "claudecode", "codex");

        UiThread.Run(() =>
        {
            var cells = Texts(scene.Grid).ToList();

            Assert.Contains("claudecode · preToolUse", cells);
            Assert.Contains("allow · 320ms", cells);
            Assert.Contains("codex · preToolUse", cells);
            Assert.Contains("block · HIGH · 41ms", cells);

            // Not the wall of key=value the column used to cut off after "connector=claudecod".
            Assert.DoesNotContain(cells, t => t.StartsWith("connector=", StringComparison.Ordinal));

            scene.Render("cust261-audit-connector-column-hook-row");
        });
    }

    [Fact]
    public void The_inspector_of_a_hook_call_is_titled_for_it_and_lays_out_its_details_like_the_TUI()
    {
        using var scene = Scene.Open(1400, 900, "claudecode", "codex");

        UiThread.Run(() =>
        {
            scene.ViewModel.SelectedRow = scene.ViewModel.Rows.Single(r => r.Id == "h-1");
            scene.Host.Relayout();

            var texts = Texts(scene.Inspector).ToList();
            Assert.Contains("claudecode preToolUse", texts);
            Assert.Contains("Parsed details", texts);
            Assert.Contains("Enforcement mode", texts);
            Assert.Contains("Decision", texts);
            Assert.Contains("320ms", texts);
            Assert.DoesNotContain("Would block", texts);
            Assert.DoesNotContain("Severity (decision)", texts);

            scene.Render("cust261-audit-inspector-hook-row");
        });
    }

    [Fact]
    public void The_inspector_of_a_blocked_skill_says_current_state_blocked()
    {
        using var scene = Scene.Open(1400, 900, "claudecode", "codex");

        UiThread.Run(() => scene.ViewModel.SelectedRow = scene.ViewModel.Rows.Single(r => r.Id == "s-1"));
        UiThread.WaitFor(() => scene.ViewModel.HasCurrentState, "the current state read");

        UiThread.Run(() =>
        {
            scene.Host.Relayout();
            var line = VisualTree.Find<StackPanel>(scene.Inspector, p => System.Windows.Automation.AutomationProperties.GetName(p) == "Current state: blocked");
            Assert.NotNull(line);
            Assert.True(line!.IsVisible);
            Assert.Equal(new[] { "Current state", "blocked" }, Texts(line).ToArray());

            scene.Render("cust261-audit-inspector-current-state");

            // Another row, another answer: the line goes at once.
            scene.ViewModel.SelectedRow = scene.ViewModel.Rows.Single(r => r.Id == "p-1");
            scene.Host.Relayout();
            Assert.DoesNotContain("Current state", Texts(scene.Inspector));
        });
    }

    [Fact]
    public void The_panel_tells_its_view_when_the_roster_changes_while_it_is_on_screen_and_not_otherwise()
    {
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp);

        UiThread.Run(() =>
        {
            var vm = new AuditPanelViewModel(services);
            var raised = 0;
            vm.PropertyChanged += (_, e) => raised += e.PropertyName == nameof(AuditPanelViewModel.ShowConnectorColumn) ? 1 : 0;

            // Away: nobody is told (the view asks again when the panel comes back).
            services.ConnectorScope.UpdateRoster(new[] { "claudecode", "codex" });
            Assert.Equal(0, raised);

            vm.SetActive(true);
            Assert.Equal(1, raised);

            services.ConnectorScope.UpdateRoster(new[] { "claudecode" });
            Assert.Equal(2, raised);

            vm.SetActive(false);
            services.ConnectorScope.UpdateRoster(new[] { "claudecode", "codex" });
            Assert.Equal(2, raised);
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
        private readonly TempDirectory _temp = new();

        // AuditPanel's row style is BasedOn a ListViewItem style that only a Fluent ThemeMode provides.
        private readonly IDisposable _theme = UiThread.FluentTheme();

        private Scene(int width, int height, string[] roster)
        {
            var db = Path.Combine(_temp.Path, "audit.db");
            AuditTestDatabase.Create(db, 0);
            var now = DateTimeOffset.UtcNow;
            CorrelatedRows.Add(db, "h-1", now.AddSeconds(-1), "connector-hook", "INFO", "claudecode", "guardrail.evaluation", "evt", "preToolUse", "gateway",
                "connector=claudecode action=allow severity=NONE mode=observe would_block=false elapsed=320ms", runId: "run-aaa");
            CorrelatedRows.Add(db, "h-2", now.AddSeconds(-2), "connector-hook", "HIGH", "codex", "guardrail.evaluation", "evt", "preToolUse", "gateway",
                "connector=codex action=block severity=HIGH mode=enforce would_block=true elapsed_ms=41", runId: "run-bbb");
            CorrelatedRows.Add(db, "s-1", now.AddSeconds(-3), "skill-block", "HIGH", "claudecode", "enforcement.action", "evt", "evil-skill", "cli", "blocked by operator");
            CorrelatedRows.Add(db, "s-2", now.AddSeconds(-4), "scan-finding", "MEDIUM", "codex", "security.finding", "evt", "skills/other", "scanner", "found a thing");
            CorrelatedRows.Add(db, "p-1", now.AddSeconds(-5), "config.change.applied", "INFO", null, "compliance.activity", "evt", "config.yaml", "cli", "applied");
            CorrelatedRows.AddAction(db, "skill", "evil-skill", """{"install":"block"}""");

            Services = TestServices.Create(_temp);
            if (roster.Length > 0)
            {
                Services.ConnectorScope.UpdateRoster(roster);
            }

            Shell = UiThread.Run(() =>
            {
                var shell = new PanelShell(Services, width, height);
                Panel = shell.Show<AuditPanel>();
                return shell;
            });
            ViewModel = UiThread.Run(() => (AuditPanelViewModel)Shell.ViewModel);

            // The five rows are not all actionable; the panel opens on the actionable events (CUST-262), and this is about the table.
            UiThread.Run(() => ViewModel.ActionableOnly = false);
            UiThread.WaitFor(() => ViewModel.Rows.Count == 5 && !ViewModel.IsLoading, "audit rows loaded");
            UiThread.WaitFor(
                () =>
                {
                    Host.Relayout();
                    return Shell.PageSize.Width <= width - 225;
                },
                "navigation pane fully open",
                timeoutMilliseconds: 5_000);
        }

        public static Scene Open(int width, int height, params string[] roster) => new(width, height, roster);

        public AppServices Services { get; private set; } = null!;

        public PanelShell Shell { get; }

        public AuditPanel Panel { get; private set; } = null!;

        public AuditPanelViewModel ViewModel { get; }

        public OffscreenHost Host => Shell.Host;

        public DataGrid Grid => (DataGrid)Panel.FindName("RowList");

        public DcInspector Inspector => (DcInspector)Panel.FindName("Inspector");

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
            _theme.Dispose();
            Services.Dispose();
            SqlitePools.Release(_temp.Path);
            _temp.Dispose();
        }
    }
}
