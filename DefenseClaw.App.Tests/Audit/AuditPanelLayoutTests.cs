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
    public void The_summary_takes_the_width_the_short_columns_leave_and_the_short_columns_carry_tooltips()
    {
        using var scene = Scene.Open(940, 620);

        UiThread.Run(() =>
        {
            var summary = scene.CellTexts("synthetic event").First();
            var bucket = scene.CellTexts("guardrail.evaluation").First();

            // Every column but the summary is fixed, so the summary is what is left of a 660-odd DIP list.
            Assert.True(summary.ActualWidth >= 200, $"the summary column is only {summary.ActualWidth} DIPs wide");
            Assert.True(bucket.ActualWidth <= 121, $"the bucket column grew to {bucket.ActualWidth}");

            // A bucket, action or connector that does not fit is cut off with an ellipsis - and says its whole value on hover.
            Assert.Equal("guardrail.evaluation", bucket.ToolTip);
            Assert.Equal("hook_decision", scene.CellTexts("hook_decision").First().ToolTip);
            Assert.Equal("claudecode", scene.CellTexts("claudecode").First().ToolTip);
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
            Assert.Equal(3, Grid.GetColumnSpan(scene.DetailCard));

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
            Assert.Equal(2, Grid.GetColumn(scene.DetailCard));
            Assert.Equal(1, Grid.GetColumnSpan(scene.ListCard));
            Assert.True(scene.ListCard.ActualWidth >= 600, $"the list beside the detail is {scene.ListCard.ActualWidth}");
            Assert.True(scene.DetailCard.ActualWidth >= 380, $"the detail is only {scene.DetailCard.ActualWidth}");
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

        public Border DetailCard => (Border)Panel.FindName("DetailCard");

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
