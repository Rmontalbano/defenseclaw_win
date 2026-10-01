using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Controls;
using DefenseClaw.App.Views.Panels;
using DefenseClaw.Core.Logs;

namespace DefenseClaw.App.Tests.Inspector;

/// <summary>
/// The flush inspector pane (CUST-215): what <see cref="DcInspector"/> does by itself, and the Logs panel's adoption of it
/// (Alerts and Audit are covered by their layout tests). A PNG of each state is written when <c>DC_RENDER_DIR</c> names a folder.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class DcInspectorTests
{
    private static DcInspector Build()
    {
        var inspector = new DcInspector
        {
            Title = "Alert details",
            CloseAutomationName = "Close alert details",
            Content = new TextBlock { Text = "body" },
            CloseCommand = new RelayCommand(() => { }),
        };
        return inspector;
    }

    private static bool Press(UIElement target, Key key)
    {
        var press = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(target)!, 0, key)
        {
            RoutedEvent = Keyboard.KeyDownEvent,
            Source = target,
        };
        target.RaiseEvent(press);
        return press.Handled;
    }

    [Fact]
    public void It_is_collapsed_until_IsOpen_and_collapses_again_when_it_closes()
    {
        UiThread.Run(() =>
        {
            var inspector = Build();
            using var host = new OffscreenHost(Wrap(inspector), 900, 500);

            Assert.Equal(Visibility.Collapsed, inspector.Visibility);
            Assert.False(inspector.IsVisible);

            inspector.IsOpen = true;
            host.Relayout();
            Assert.True(inspector.IsVisible);
            Assert.InRange(inspector.ActualWidth, 320, 400);
            Assert.True(inspector.ActualHeight >= 480, "it is the full height of its row");

            inspector.IsOpen = false;
            host.Relayout();
            Assert.False(inspector.IsVisible);
        });
    }

    [Fact]
    public void Esc_inside_the_pane_runs_the_close_command_and_the_X_is_a_named_keyboard_reachable_button()
    {
        UiThread.Run(() =>
        {
            var closes = 0;
            var inspector = new DcInspector
            {
                Title = "Event details",
                CloseAutomationName = "Close event details",
                IsOpen = true,
                Content = new TextBox { Text = "body" },
                CloseCommand = new RelayCommand(() => closes++),
            };
            using var host = new OffscreenHost(Wrap(inspector), 900, 500);

            var close = inspector.CloseButton;
            Assert.NotNull(close);
            Assert.True(close!.Focusable);
            Assert.True(KeyboardNavigation.GetIsTabStop(close));
            Assert.Equal("Close event details", AutomationProperties.GetName(close));

            Assert.True(Press(close, Key.Escape));
            Assert.Equal(1, closes);

            // Esc that a control inside already used is not a close.
            Assert.False(Press(inspector, Key.Enter));
            Assert.Equal(1, closes);
        });
    }

    [Fact]
    public void Opening_it_does_not_take_focus_and_the_pane_itself_is_not_a_tab_stop()
    {
        UiThread.Run(() =>
        {
            var list = new ListBox();
            var inspector = Build();
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(list);
            grid.Children.Add(inspector);
            using var host = new OffscreenHost(grid, 900, 500);

            _ = list.Focus();
            _ = Keyboard.Focus(list);
            var before = Keyboard.FocusedElement;

            inspector.IsOpen = true;
            host.Relayout();

            Assert.False(inspector.Focusable);
            Assert.False(inspector.IsTabStop);
            Assert.Same(before, Keyboard.FocusedElement);
        });
    }

    [Fact]
    public void To_UI_Automation_it_is_a_named_pane()
    {
        UiThread.Run(() =>
        {
            var inspector = Build();
            inspector.IsOpen = true;
            using var host = new OffscreenHost(Wrap(inspector), 900, 500);

            var peer = UIElementAutomationPeer.CreatePeerForElement(inspector);
            Assert.Equal(AutomationControlType.Pane, peer.GetAutomationControlType());
            Assert.Equal("Alert details", peer.GetName());
            Assert.True(peer.IsControlElement());

            AutomationProperties.SetName(inspector, "Details");
            Assert.Equal("Details", peer.GetName());
        });
    }

    [Fact]
    public void A_narrow_panel_gives_the_pane_the_whole_row()
    {
        UiThread.Run(() =>
        {
            var inspector = Build();
            inspector.IsOpen = true;
            CompactLayout.SetIsCompact(inspector, true);
            using var host = new OffscreenHost(Wrap(inspector), 700, 500);

            Assert.Equal(0, Grid.GetColumn(inspector));
            Assert.Equal(2, Grid.GetColumnSpan(inspector));
            Assert.True(inspector.ActualWidth >= 690, $"the pane is {inspector.ActualWidth} wide");
        });
    }

    [Fact]
    public void The_Logs_panel_opens_the_inspector_on_a_selected_line_with_its_parsed_fields_and_raw_text_and_Esc_closes_it()
    {
        using var scene = LogsScene.Open(1400, 900);

        UiThread.Run(() =>
        {
            Assert.False(scene.Inspector.IsVisible);
            var alone = scene.ListCard.ActualWidth;
            Assert.True(alone >= 1000, $"the list alone is {alone}");

            scene.ViewModel.SelectedEntry = scene.ViewModel.DisplayedLines[1];
            scene.Host.Relayout();

            Assert.True(scene.ViewModel.HasSelection);
            Assert.True(scene.Inspector.IsVisible);
            Assert.True(scene.ListCard.IsVisible);
            Assert.InRange(scene.Inspector.ActualWidth, 320, 400);
            Assert.True(scene.ListCard.ActualWidth < alone - 300);

            var raw = VisualTree.Find<TextBox>(scene.Inspector, t => AutomationProperties.GetName(t) == "Raw line");
            Assert.Equal("[api] line 1", raw!.Text);
            var fields = VisualTree.Find<ItemsControl>(scene.Inspector, c => AutomationProperties.GetName(c) == "Log line fields")!;
            var texts = VisualTree.Descendants<TextBlock>(fields).Select(t => t.Text).ToList();
            Assert.Contains("component", texts);
            Assert.Contains("api", texts);

            scene.Render("logs-1400x900-line-selected");

            Assert.True(Press(scene.Panel, Key.Escape), "Esc closes the inspector");
            scene.Host.Relayout();
            Assert.Null(scene.ViewModel.SelectedEntry);
            Assert.False(scene.Inspector.IsVisible);
            Assert.True(scene.ListCard.ActualWidth >= alone - 1, $"{scene.ListCard.ActualWidth} vs {alone}");
        });
    }

    [Fact]
    public void On_a_narrow_Logs_panel_the_inspector_replaces_the_list()
    {
        using var scene = LogsScene.Open(940, 620);

        UiThread.Run(() =>
        {
            Assert.True(scene.Panel.IsCompact);
            scene.Render("logs-940x620-nothing-selected");

            scene.ViewModel.SelectedEntry = scene.ViewModel.DisplayedLines[0];
            scene.Host.Relayout();

            Assert.True(scene.Inspector.IsVisible);
            Assert.False(scene.ListCard.IsVisible);
            Assert.Equal(0, Grid.GetColumn(scene.Inspector));
            Assert.Equal(2, Grid.GetColumnSpan(scene.Inspector));

            scene.Render("logs-940x620-line-selected");

            scene.ViewModel.ClearSelectionCommand.Execute(null);
            scene.Host.Relayout();
            Assert.True(scene.ListCard.IsVisible);
            Assert.False(scene.Inspector.IsVisible);
        });
    }

    [Fact]
    public void A_log_entry_carries_only_the_fields_its_line_has()
    {
        var plain = new LogEntry(LogLine.Parse("[api] hello", 7));
        Assert.Equal(new[] { "component", "line" }, plain.Fields.Select(f => f.Name).ToArray());
        Assert.Equal("7", plain.Fields[1].Value);
        Assert.Equal(string.Empty, plain.LevelText);

        var bare = new LogEntry(LogLine.Parse("just text", 1));
        Assert.Equal(new[] { "line" }, bare.Fields.Select(f => f.Name).ToArray());
    }

    private static Grid Wrap(DcInspector inspector)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(inspector);
        return grid;
    }

    private sealed class LogsScene : IDisposable
    {
        private readonly TempDirectory _temp = new();
        private readonly AppServices _services;

        private LogsScene(int width, int height)
        {
            _services = TestServices.Create(_temp);
            Shell = UiThread.Run(() =>
            {
                var shell = new PanelShell(_services, width, height);
                Panel = shell.Show<LogsPanel>();
                return shell;
            });
            ViewModel = UiThread.Run(() => (LogsPanelViewModel)Shell.ViewModel);

            UiThread.Run(() =>
            {
                ViewModel.SetActive(true);
                ViewModel.AcceptLines("Gateway", Enumerable.Range(0, 30).Select(n => LogLine.Parse($"[api] line {n}", n)).ToArray());
            });
            UiThread.WaitFor(() => ViewModel.DisplayedLines.Count == 30, "log lines projected");
            // The stand-in NavigationView opens its pane with an animation; measuring before it lands reads a wider page.
            UiThread.WaitFor(
                () =>
                {
                    Host.Relayout();
                    return Shell.PageSize.Width <= width - 225;
                },
                "navigation pane fully open",
                timeoutMilliseconds: 5_000);
        }

        public static LogsScene Open(int width, int height) => new(width, height);

        public PanelShell Shell { get; }

        public LogsPanel Panel { get; private set; } = null!;

        public LogsPanelViewModel ViewModel { get; }

        public OffscreenHost Host => Shell.Host;

        public Border ListCard => (Border)Panel.FindName("ListCard");

        public DcInspector Inspector => (DcInspector)Panel.FindName("Inspector");

        public void Render(string name) => RenderTo.Png(Host, name);

        public void Dispose()
        {
            UiThread.Run(Shell.Dispose);
            _services.Dispose();
            _temp.Dispose();
        }
    }
}
