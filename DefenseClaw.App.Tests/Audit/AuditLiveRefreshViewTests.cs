using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Controls;
using DefenseClaw.App.Views.Panels;
using Microsoft.Data.Sqlite;
using Wpf.Ui.Controls;

namespace DefenseClaw.App.Tests.Audit;

/// <summary>
/// CUST-262 on the real Audit view: the poll timer (it runs while the panel is on screen and stops when it leaves, and a block event written to the
/// database turns up on its own), the scroll position the view keeps when the live refresh puts rows above the one being read, and the "Actionable only"
/// switch with its chip and the "Updated hh:mm:ss" caption. Synthetic rows from the real DDL, hosted offscreen on the shared UI thread; a PNG of the
/// view is written when <c>DC_RENDER_DIR</c> names a folder.
/// </summary>
[Collection(UiCollection.Name)]
public class AuditLiveRefreshViewTests
{
    [Fact]
    public void A_new_block_event_turns_up_by_itself_while_the_panel_is_on_screen_and_the_timer_stops_when_it_leaves()
    {
        using var scene = Scene.Open(quiet: 30, loud: 0, expectedRows: 0);
        var viewModel = scene.ViewModel;

        UiThread.Run(() =>
        {
            viewModel.LivePollInterval = TimeSpan.FromMilliseconds(50);
            viewModel.LiveTimerEnabled = true;
            viewModel.SetActive(true);
            Assert.True(viewModel.IsLiveTimerRunning);
            Assert.True(viewModel.ActionableOnly);
        });
        Assert.Empty(UiThread.Run(() => viewModel.Rows.ToArray()));

        // A block event, written the way the gateway writes one: no Refresh, no command, only the clock.
        scene.Add("block-by-timer", action: "install-blocked");
        UiThread.WaitFor(() => viewModel.Rows.Any(r => r.Id == "block-by-timer"), "the block event to appear on its own", (int)TestTimeoutMilliseconds);

        UiThread.Run(() =>
        {
            Assert.Equal("block-by-timer", viewModel.Rows[0].Id);
            Assert.True(viewModel.HasLiveStatus);

            // Leaving the screen stops the clock.
            viewModel.SetActive(false);
            Assert.False(viewModel.IsLiveTimerRunning);
        });

        // A row written while the panel was away is read when it is back (one catch-up poll), and the clock runs again.
        scene.Add("block-while-away", action: "install-blocked");
        UiThread.Run(() =>
        {
            viewModel.SetActive(true);
            Assert.True(viewModel.IsLiveTimerRunning);
        });
        UiThread.WaitFor(() => viewModel.Rows.Any(r => r.Id == "block-while-away"), "the catch-up poll", (int)TestTimeoutMilliseconds);
        UiThread.Run(() => viewModel.SetActive(false));
    }

    [Fact]
    public void Rows_put_in_above_a_scrolled_list_leave_the_row_being_read_where_it_was()
    {
        using var scene = Scene.Open(quiet: 120, loud: 0, actionableOnly: false, expectedRows: AuditPanelViewModel.PageSize);
        var viewModel = scene.ViewModel;
        UiThread.Run(() => viewModel.SetActive(true));

        AuditRow reading = null!;
        double topBefore = 0;
        double offsetBefore = 0;
        UiThread.Run(() =>
        {
            var scroller = scene.Scroller;
            scroller.ScrollToVerticalOffset(600);
            scene.Host.Relayout();
            Assert.True(scroller.VerticalOffset > 300, $"the list did not scroll: {scroller.VerticalOffset}");

            // The first row that is fully on screen: the one being read.
            var row = VisualTree.Descendants<DataGridRow>(scene.Grid)
                .Where(r => r.Item is AuditRow && r.TranslatePoint(new Point(0, 0), scene.Grid).Y >= scene.HeaderBottom)
                .OrderBy(r => r.TranslatePoint(new Point(0, 0), scene.Grid).Y)
                .First();
            reading = (AuditRow)row.Item;
            topBefore = row.TranslatePoint(new Point(0, 0), scene.Grid).Y;
            offsetBefore = scroller.VerticalOffset;
            viewModel.SelectedRow = reading;
        });

        // Six new events arrive above everything on the list.
        for (var i = 0; i < 6; i++)
        {
            scene.Add($"new-{i}", secondsAgo: 0.1 * (i + 1), action: "hook_decision");
        }

        var poll = UiThread.Run(() => viewModel.PollLiveAsync());
        UiThread.WaitFor(() => poll.IsCompleted, "the live poll", (int)TestTimeoutMilliseconds);

        UiThread.Run(() =>
        {
            scene.Host.Relayout();
            Assert.Equal(AuditPanelViewModel.PageSize + 6, viewModel.Rows.Count);
            Assert.Equal("new-0", viewModel.Rows[0].Id);

            // The row the operator was reading is the same object, selected still, and on the same line of the screen: the list scrolled by what was put above it.
            Assert.Same(reading, viewModel.SelectedRow);
            var row = (DataGridRow)scene.Grid.ItemContainerGenerator.ContainerFromItem(reading);
            Assert.NotNull(row);
            var topAfter = row.TranslatePoint(new Point(0, 0), scene.Grid).Y;
            Assert.True(Math.Abs(topAfter - topBefore) < 2, $"the row moved from {topBefore} to {topAfter}");
            Assert.True(scene.Scroller.VerticalOffset > offsetBefore + 100, $"the list did not follow what was put above it: {offsetBefore} -> {scene.Scroller.VerticalOffset}");
        });
    }

    [Fact]
    public void At_the_top_the_new_rows_are_simply_first_and_the_list_stays_at_the_top()
    {
        using var scene = Scene.Open(quiet: 120, loud: 0, actionableOnly: false, expectedRows: AuditPanelViewModel.PageSize);
        var viewModel = scene.ViewModel;
        UiThread.Run(() => viewModel.SetActive(true));
        UiThread.Run(() => Assert.Equal(0, scene.Scroller.VerticalOffset));

        scene.Add("fresh", secondsAgo: 0.1);
        var poll = UiThread.Run(() => viewModel.PollLiveAsync());
        UiThread.WaitFor(() => poll.IsCompleted, "the live poll", (int)TestTimeoutMilliseconds);

        UiThread.Run(() =>
        {
            scene.Host.Relayout();
            Assert.Equal("fresh", viewModel.Rows[0].Id);
            Assert.Equal(0, scene.Scroller.VerticalOffset);
        });
    }

    [Fact]
    public void The_switch_the_chip_and_the_caption_are_in_the_strip_and_follow_the_view_model()
    {
        using var scene = Scene.Open(quiet: 70, loud: 6, actionableOnly: true, expectedRows: 6);
        var viewModel = scene.ViewModel;
        UiThread.Run(() => viewModel.SetActive(true));
        var poll = UiThread.Run(() => viewModel.PollLiveAsync());
        UiThread.WaitFor(() => poll.IsCompleted && viewModel.HasLiveStatus, "the first live poll", (int)TestTimeoutMilliseconds);

        UiThread.Run(() =>
        {
            scene.Host.Relayout();
            var toggle = VisualTree.Find<ToggleSwitch>(scene.Panel, t => System.Windows.Automation.AutomationProperties.GetName(t) == "Actionable only");
            Assert.NotNull(toggle);
            Assert.True(toggle!.IsVisible);
            Assert.True(toggle.IsEnabled);
            Assert.True(toggle.IsChecked);
            Assert.Equal(viewModel.ActionableToolTip, toggle.ToolTip);

            // The chip says how many low-signal events the list is leaving out; the caption says when the list was last known to be current.
            var texts = VisualTree.Descendants<System.Windows.Controls.TextBlock>(scene.Panel).Where(t => t.IsVisible).Select(t => t.Text).ToList();
            Assert.Contains("70 low-signal hidden", texts);
            Assert.Contains(texts, t => t.StartsWith("Updated ", StringComparison.Ordinal));
            Assert.Equal(6, viewModel.Rows.Count);
            Assert.Equal("6 actionable events · 70 low-signal hidden · Last 24 hours", scene.PageToolbar.Caption);
            RenderTo.Png(scene.Host, "cust262-audit-actionable");

            // A search asks for something specific: the switch is paused (checked, off, with the reason on its tooltip) and nothing is counted as hidden.
            viewModel.SearchText = "synthetic event";
        });
        UiThread.WaitFor(() => !viewModel.IsLoading && viewModel.Rows.Count == 70, "the search's rows", (int)TestTimeoutMilliseconds);
        UiThread.Run(() =>
        {
            scene.Host.Relayout();
            var toggle = VisualTree.Find<ToggleSwitch>(scene.Panel, t => System.Windows.Automation.AutomationProperties.GetName(t) == "Actionable only")!;
            Assert.False(toggle.IsEnabled);
            Assert.True(toggle.IsChecked);
            Assert.Contains("paused while a search is on", (string)toggle.ToolTip, StringComparison.Ordinal);
            var texts = VisualTree.Descendants<System.Windows.Controls.TextBlock>(scene.Panel).Where(t => t.IsVisible).Select(t => t.Text).ToList();
            Assert.DoesNotContain(texts, t => t.Contains("low-signal hidden", StringComparison.Ordinal));
        });
    }

    private static long TestTimeoutMilliseconds => 120_000;

    /// <summary>The real Audit panel in the shell stand-in, over a database written for it. UI thread work goes through <see cref="UiThread"/>.</summary>
    private sealed class Scene : IDisposable
    {
        private readonly TempDirectory _temp = new();

        // AuditPanel's row style is BasedOn a ListViewItem style that only a Fluent ThemeMode provides.
        private readonly IDisposable _theme = UiThread.FluentTheme();

        private Scene(int quiet, int loud, bool actionableOnly, int? expectedRows)
        {
            DbPath = Path.Combine(_temp.Path, "audit.db");
            AuditTestDatabase.Create(DbPath, quiet);
            for (var i = 0; i < loud; i++)
            {
                AuditEventWriter.Add(DbPath, $"loud-{i}", DateTimeOffset.UtcNow.AddSeconds(-(i * 3) - 0.5), action: i % 2 == 0 ? "install-blocked" : "hook_decision", severity: i % 2 == 0 ? "INFO" : "HIGH", details: "synthetic loud");
            }

            Services = TestServices.Create(_temp);
            Shell = UiThread.Run(() =>
            {
                var shell = new PanelShell(Services, 1400, 900);
                Panel = shell.Show<AuditPanel>();
                return shell;
            });
            ViewModel = UiThread.Run(() => (AuditPanelViewModel)Shell.ViewModel);

            UiThread.Run(() =>
            {
                ViewModel.LiveTimerEnabled = false;
                if (!actionableOnly)
                {
                    ViewModel.ActionableOnly = false;
                }
            });

            var rows = expectedRows ?? Math.Min(quiet + loud, AuditPanelViewModel.PageSize);
            UiThread.WaitFor(() => ViewModel.ResultSummary != "Loading…" && ViewModel.Rows.Count == rows && !ViewModel.IsLoading, "audit rows loaded");
            UiThread.WaitFor(
                () =>
                {
                    Host.Relayout();
                    return Shell.PageSize.Width <= 1400 - 225;
                },
                "navigation pane fully open",
                timeoutMilliseconds: 5_000);
        }

        public static Scene Open(int quiet, int loud, bool actionableOnly = true, int? expectedRows = null) => new(quiet, loud, actionableOnly, expectedRows);

        public string DbPath { get; }

        public AppServices Services { get; }

        public PanelShell Shell { get; }

        public AuditPanel Panel { get; private set; } = null!;

        public AuditPanelViewModel ViewModel { get; }

        public OffscreenHost Host => Shell.Host;

        public System.Windows.Controls.DataGrid Grid => (System.Windows.Controls.DataGrid)Panel.FindName("RowList");

        public ScrollViewer Scroller => VisualTree.Find<ScrollViewer>(Grid)!;

        public DcPageToolbar PageToolbar => (DcPageToolbar)Panel.FindName("PageToolbar");

        /// <summary>The bottom of the column header row, in the grid's own coordinates: rows above it are scrolled out of sight.</summary>
        public double HeaderBottom =>
            VisualTree.Descendants<DataGridColumnHeadersPresenter>(Grid).First().TranslatePoint(new Point(0, 0), Grid).Y
            + VisualTree.Descendants<DataGridColumnHeadersPresenter>(Grid).First().ActualHeight;

        /// <summary>An INFO hook decision (or <paramref name="action"/>) <paramref name="secondsAgo"/> seconds ago, written to the live database.</summary>
        public void Add(string id, double secondsAgo = 0.2, string action = "hook_decision") =>
            AuditEventWriter.Add(DbPath, id, DateTimeOffset.UtcNow.AddSeconds(-secondsAgo), action, "INFO", "synthetic extra");

        public void Dispose()
        {
            UiThread.Run(() =>
            {
                ViewModel.SetActive(false);
                Shell.Dispose();
            });
            _theme.Dispose();
            Services.Dispose();
            SqlitePools.Release(_temp.Path);
            _temp.Dispose();
        }
    }
}
