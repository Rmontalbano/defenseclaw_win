using System.Windows;
using System.Windows.Controls;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Panels;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.App.Tests.Audit;

/// <summary>
/// The Audit panel as a real view over a synthetic <c>audit.db</c>, in the shell stand-in at the window's 940 x 620 DIP minimum
/// (where the page has about 711 DIPs of width) and at 1400 x 900. At the minimum the list used to be 381 DIPs wide beside a
/// 258 DIP pane that said "Select a row..." while bucket, action and connector truncated to about eight characters each. The
/// detail pane now exists only while a row is selected, and on a narrow panel it replaces the list instead of squeezing it.
/// <para>
/// A PNG of each state is written when the <c>DC_RENDER_DIR</c> environment variable names a folder, and never otherwise.
/// </para>
/// </summary>
[Collection(UiCollection.Name)]
public class AuditPanelLayoutTests
{
    [Fact]
    public void At_the_minimum_window_with_nothing_selected_the_list_takes_the_whole_width_and_there_is_no_empty_pane()
    {
        using var scene = Scene.Open(940, 620);

        UiThread.Run(() =>
        {
            Assert.InRange(scene.Shell.PageSize.Width, 690, 730);

            // The list was 381 DIPs; now it has everything the page has (minus the page's own margins).
            Assert.True(scene.ListCard.ActualWidth >= 600, $"the list card is only {scene.ListCard.ActualWidth} DIPs wide");
            Assert.Equal(Visibility.Collapsed, scene.DetailCard.Visibility);
            Assert.False(scene.DetailCard.IsVisible);
            Assert.False(scene.ViewModel.HasSelection);

            scene.Render("audit-940x620-nothing-selected");
        });
    }

    [Fact]
    public void The_columns_share_the_width_and_a_cell_that_is_cut_off_says_its_whole_value_in_a_tooltip()
    {
        using var scene = Scene.Open(940, 620);

        UiThread.Run(() =>
        {
            var details = scene.CellTexts("synthetic event").First();
            var type = scene.CellTexts("guardrail.evaluation").First();

            // The table's columns are star-sized with a floor each (Time, Action, Type, Target, Severity, Run, Details, row menu): at
            // the minimum window they add up to the whole viewport, with no sideways scroll bar.
            var total = scene.Grid.Columns.Sum(c => c.ActualWidth);
            Assert.True(details.ActualWidth >= 90, $"the details column is only {details.ActualWidth} DIPs wide");
            Assert.True(total <= scene.ListCard.ActualWidth, $"the columns are {total} DIPs wide in a {scene.ListCard.ActualWidth} DIP list");

            // A type or action that does not fit is cut off with an ellipsis - and says its whole value on hover.
            Assert.Equal("guardrail.evaluation", type.ToolTip);
            Assert.Equal("hook_decision", scene.CellTexts("hook_decision").First().ToolTip);
        });
    }

    [Fact]
    public void The_columns_follow_the_mac_table_and_every_sortable_one_has_a_sort_key()
    {
        using var scene = Scene.Open(1400, 900);

        UiThread.Run(() =>
        {
            Assert.Equal(
                new[] { "Time", "Action", "Type", "Target", "Severity", "Run", "Details", "Actions" },
                scene.Grid.Columns.Select(c => (string)c.Header).ToArray());
            Assert.All(scene.Grid.Columns.Where(c => (string)c.Header != "Actions"), c => Assert.False(string.IsNullOrEmpty(c.SortMemberPath), $"{c.Header} has no sort key"));
            Assert.False(scene.Grid.Columns[^1].CanUserSort);

            // The severity word is on every badge, whatever its colour.
            var badges = VisualTree.Descendants<DefenseClaw.App.Views.Controls.DcSeverityBadge>(scene.ListCard).ToList();
            Assert.NotEmpty(badges);
            Assert.All(badges, b => Assert.False(string.IsNullOrWhiteSpace(b.Text)));
        });
    }

    [Fact]
    public void Selecting_a_row_at_the_minimum_window_shows_the_detail_in_place_of_the_list()
    {
        using var scene = Scene.Open(940, 620);

        UiThread.Run(() =>
        {
            Assert.True(scene.Panel.IsCompact, "a 711 DIP panel is under the 960 DIP compact width");

            scene.ViewModel.SelectedRow = scene.ViewModel.Rows[3];
            scene.Host.Relayout();

            // The pane appears, and has the room a pane beside a list never had: the whole width.
            Assert.True(scene.DetailCard.IsVisible);
            Assert.True(scene.DetailCard.ActualWidth >= 600, $"the detail is only {scene.DetailCard.ActualWidth} DIPs wide");
            Assert.False(scene.ListCard.IsVisible, "the list steps aside while the detail is open");
            Assert.Equal(0, Grid.GetColumn(scene.DetailCard));
            Assert.Equal(2, Grid.GetColumnSpan(scene.DetailCard));

            scene.Render("audit-940x620-row-selected");

            // Closing it (Esc, or its button) gives the list back, whole.
            scene.ViewModel.ClearSelectionCommand.Execute(null);
            scene.Host.Relayout();
            Assert.True(scene.ListCard.IsVisible);
            Assert.False(scene.DetailCard.IsVisible);
            Assert.True(scene.ListCard.ActualWidth >= 600);
        });
    }

    [Fact]
    public void On_a_wide_window_the_detail_sits_beside_the_list_and_only_while_a_row_is_selected()
    {
        using var scene = Scene.Open(1400, 900);

        UiThread.Run(() =>
        {
            Assert.False(scene.Panel.IsCompact);
            Assert.False(scene.DetailCard.IsVisible);
            var alone = scene.ListCard.ActualWidth;
            Assert.True(alone >= 1000, $"the list alone is {alone}");

            scene.ViewModel.SelectedRow = scene.ViewModel.Rows[1];
            scene.Host.Relayout();

            Assert.True(scene.ListCard.IsVisible);
            Assert.True(scene.DetailCard.IsVisible);
            Assert.Equal(1, Grid.GetColumn(scene.DetailCard));
            Assert.Equal(1, Grid.GetColumnSpan(scene.ListCard));
            Assert.True(scene.ListCard.ActualWidth >= 600, $"the list beside the detail is {scene.ListCard.ActualWidth}");
            Assert.True(scene.DetailCard.ActualWidth >= 320, $"the detail is only {scene.DetailCard.ActualWidth}");
            Assert.True(scene.ListCard.ActualWidth < alone - 300);

            scene.Render("audit-1400x900-row-selected");
        });
    }

    [Fact]
    public void Resizing_across_the_compact_width_with_a_row_selected_moves_between_beside_and_instead()
    {
        using var scene = Scene.Open(1400, 900);

        UiThread.Run(() =>
        {
            scene.ViewModel.SelectedRow = scene.ViewModel.Rows[0];
            scene.Host.Relayout();
            Assert.True(scene.ListCard.IsVisible && scene.DetailCard.IsVisible);

            scene.Host.Resize(940, 620);
            Assert.True(scene.Panel.IsCompact);
            Assert.False(scene.ListCard.IsVisible);
            Assert.True(scene.DetailCard.IsVisible);

            scene.Host.Resize(1400, 900);
            Assert.False(scene.Panel.IsCompact);
            Assert.True(scene.ListCard.IsVisible && scene.DetailCard.IsVisible);
        });
    }

    [Fact]
    public void The_detail_pane_still_pretty_prints_the_selected_rows_structured_json()
    {
        using var scene = Scene.Open(1400, 900);

        UiThread.Run(() =>
        {
            scene.ViewModel.SelectedRow = scene.ViewModel.Rows[0];
            scene.Host.Relayout();

            var box = VisualTree.Find<TextBox>(scene.DetailCard, t => System.Windows.Automation.AutomationProperties.GetName(t) == "structured_json");
            Assert.NotNull(box);
            Assert.Contains("\"decision\": \"allow\"", box!.Text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void The_preset_strip_leads_the_filters_sit_behind_an_expander_and_the_inspector_offers_the_correlation_sections()
    {
        using var scene = Scene.Open(1400, 900);

        UiThread.Run(() =>
        {
            // Five presets in the Mac's order; the chosen one follows the view-model.
            var presets = VisualTree.Find<DefenseClaw.App.Views.Controls.DcSegmented>(scene.Panel, s => System.Windows.Automation.AutomationProperties.GetName(s) == "Audit view");
            Assert.NotNull(presets);
            Assert.Equal(
                new[] { "All", "Risk", "Blocks", "Scans", "Credentials" },
                presets!.Items.OfType<DefenseClaw.App.Views.Controls.DcSegment>().Select(s => (string)s.Content).ToArray());
            Assert.Equal("all", presets.SelectedValue);
            scene.ViewModel.Accept(new DefenseClaw.App.Services.AuditPreset("scans"));
            Assert.Equal("scans", presets.SelectedValue);
            scene.ViewModel.ActivePreset = "all";

            // The old filters are all still there, behind a closed "Filters" expander that counts the ones that are on.
            var expander = (Expander)scene.Panel.FindName("FiltersExpander");
            Assert.False(expander.IsExpanded);
            Assert.DoesNotContain(VisualTree.Descendants<ComboBox>(expander), c => c.IsVisible);
            scene.Render("audit-1400x900-presets-filters-closed");
            expander.IsExpanded = true;
            scene.Host.Relayout();
            var combos = VisualTree.Descendants<ComboBox>(expander).Select(c => System.Windows.Automation.AutomationProperties.GetName(c)).ToList();
            Assert.Equal(new[] { "Bucket", "Minimum severity", "Connector", "Time range", "Action, pick to fill the action filter" }, combos);
            Assert.True(VisualTree.Descendants<ComboBox>(expander).All(c => c.IsVisible));
            scene.Render("audit-1400x900-presets-filters-open");
            expander.IsExpanded = false;

            // The inspector: both chips, the run and the related sections, and Ctrl+E is wired to the export.
            scene.ViewModel.SelectedRow = scene.ViewModel.Rows[0];
            scene.Host.Relayout();
            var names = VisualTree.Descendants<FrameworkElement>(scene.DetailCard).Select(e => System.Windows.Automation.AutomationProperties.GetName(e)).ToList();
            Assert.Contains("Same target", names);
            Assert.Contains("Same run", names);
            Assert.Contains("Related events", names);
            Assert.Contains(scene.Panel.CommandBindings.Cast<System.Windows.Input.CommandBinding>(), b => b.Command == AuditPanel.ExportShortcut);
            Assert.Contains(AuditPanel.ExportShortcut.InputGestures.OfType<System.Windows.Input.KeyGesture>(), g => g.Key == System.Windows.Input.Key.E && g.Modifiers == System.Windows.Input.ModifierKeys.Control);
            scene.Render("audit-1400x900-inspector-correlation");
        });
    }

    [Fact]
    public void A_header_sorts_the_loaded_rows_and_the_row_menu_copies_and_narrows_to_the_target()
    {
        using var scene = Scene.Open(1400, 900);

        UiThread.Run(() =>
        {
            // Time descending is the query's own order; the Time header sorts the loaded rows the other way.
            var time = VisualTree.Descendants<System.Windows.Controls.Primitives.DataGridColumnHeader>(scene.Grid)
                .First(h => h.Column is not null && (string)h.Column.Header == "Time");
            Assert.Equal("evt-000000", ((AuditRow)scene.Grid.Items[0]).Id);
            var source = PresentationSource.FromVisual(time);
            time.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, source, Environment.TickCount, System.Windows.Input.Key.Space) { RoutedEvent = System.Windows.Input.Keyboard.KeyDownEvent, Source = time });
            time.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, source, Environment.TickCount, System.Windows.Input.Key.Space) { RoutedEvent = System.Windows.Input.Keyboard.KeyUpEvent, Source = time });
            scene.Host.Relayout();
            Assert.Equal(System.ComponentModel.ListSortDirection.Ascending, time.SortDirection);
            Assert.Equal("evt-000059", ((AuditRow)scene.Grid.Items[0]).Id);
            Assert.Equal("evt-000000", scene.ViewModel.Rows[0].Id);

            // The row menu: copy the event, copy its JSON, narrow to its target. Nothing in it changes anything.
            var row = VisualTree.Descendants<DataGridRow>(scene.Grid).First();
            var items = ContextMenuService.GetContextMenu(row)!.Items.OfType<MenuItem>().ToList();
            Assert.Equal(new[] { "Copy details", "Copy structured JSON", "Show same target", "Show same run" }, items.Select(i => (string)i.Header).ToArray());
            Assert.All(items, i => Assert.NotNull(i.Icon));

            scene.ViewModel.NoteSelection(new[] { scene.ViewModel.Rows[0], scene.ViewModel.Rows[1] });
            Assert.Equal(2, AuditPanelViewModel.CopyDetailsText(scene.ViewModel.ActionRows).Split(Environment.NewLine).Length);
        });
    }

    private sealed class Scene : IDisposable
    {
        private readonly TempDirectory _temp = new();
        private readonly AppServices _services;

        // AuditPanel's row style is BasedOn a ListViewItem style that only a Fluent ThemeMode provides.
        private readonly IDisposable _theme = UiThread.FluentTheme();

        private Scene(int width, int height)
        {
            AuditTestDatabase.Create(Path.Combine(_temp.Path, "audit.db"), 60, structuredJson: "{\"decision\":\"allow\",\"rule\":\"r1\"}");
            _services = TestServices.Create(_temp);

            Shell = UiThread.Run(() =>
            {
                var shell = new PanelShell(_services, width, height);
                Panel = shell.Show<AuditPanel>();
                return shell;
            });
            ViewModel = UiThread.Run(() => (AuditPanelViewModel)Shell.ViewModel);

            // The 60 rows are quiet INFO hook decisions; the panel opens on the actionable events (CUST-262), and this is about the layout.
            UiThread.Run(() => ViewModel.ActionableOnly = false);
            UiThread.WaitFor(() => ViewModel.Rows.Count == 60 && !ViewModel.IsLoading, "audit rows loaded");

            // The stand-in NavigationView opens its 220-DIP pane with an animation; measuring before it lands reads a
            // page ~37 DIPs too wide (seen as an order-dependent failure when this class runs with others).
            UiThread.WaitFor(
                () =>
                {
                    Host.Relayout();
                    return Shell.PageSize.Width <= width - 225;
                },
                "navigation pane fully open",
                timeoutMilliseconds: 5_000);
        }

        public static Scene Open(int width, int height) => new(width, height);

        public PanelShell Shell { get; }

        public AuditPanel Panel { get; private set; } = null!;

        public AuditPanelViewModel ViewModel { get; }

        public OffscreenHost Host => Shell.Host;

        public Border ListCard => (Border)Panel.FindName("ListCard");

        public DefenseClaw.App.Views.Controls.DcInspector DetailCard => (DefenseClaw.App.Views.Controls.DcInspector)Panel.FindName("Inspector");

        public DataGrid Grid => (DataGrid)Panel.FindName("RowList");

        /// <summary>The realized cell texts of the list that show <paramref name="text"/> (prefix match).</summary>
        public IEnumerable<TextBlock> CellTexts(string text) =>
            VisualTree.Descendants<TextBlock>(ListCard).Where(t => t.Text.StartsWith(text, StringComparison.Ordinal));

        public void Render(string name) => RenderTo.Png(Host, name);

        public void Dispose()
        {
            UiThread.Run(Shell.Dispose);
            _theme.Dispose();
            _services.Dispose();
            SqliteConnection.ClearAllPools();
            _temp.Dispose();
        }
    }
}
