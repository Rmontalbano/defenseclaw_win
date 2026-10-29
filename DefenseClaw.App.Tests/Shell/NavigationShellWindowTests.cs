using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Appearance;
using DefenseClaw.App.Tests.Appearance;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.Views.Panels;
using DefenseClaw.App.Views.Shell;
using Wpf.Ui.Controls;

namespace DefenseClaw.App.Tests.Shell;

/// <summary>
/// The compact shell (CUST-383) on the real <c>MainWindow</c>, shown far off screen without activation over a scratch installation and a runtime
/// that answers from the 1.0.x probe fixtures: the sidebar in the Mac's order with its platform words, the sidebar toggle and the palette's search
/// box in the title bar (and clickable there), a status strip that is one toolbar button high, and the entries right under it. PNGs at the Mac's
/// sizes, in the Cisco look light and dark (and at 125% and 150% for one of them), are written only when <c>DC_RENDER_DIR</c> names a folder.
/// Synthetic data only.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class NavigationShellWindowTests : IDisposable
{
    private const int WmNcHitTest = 0x0084;
    private const int HtClient = 1;
    private const int HtCaption = 2;

    private readonly TempDirectory _temp = new();
    private readonly AppServices _services;
    private MainWindow? _window;

    public NavigationShellWindowTests()
    {
        _services = TestServices.Create(
            _temp,
            "gateway:\n  api_port: 18970\nguardrail:\n  connector: claudecode\n  enabled: true\n",
            RuntimeFixtureRunner.For("95159fd"));
    }

    public void Dispose()
    {
        UiThread.Run(() =>
        {
            if (_window is not null)
            {
                _window.AllowClose();
                _window.Close();
            }
        });

        _services.Dispose();
        _temp.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private static TrayIconService UnbuiltTray() =>
        (TrayIconService)RuntimeHelpers.GetUninitializedObject(typeof(TrayIconService));

    /// <summary>The dashboard at <paramref name="width"/> x <paramref name="height"/>, shown where no one can see it, its sidebar built, on <paramref name="panelId"/>.</summary>
    private async Task<MainWindow> OpenAsync(double width, double height, string panelId = "sandboxes")
    {
        _ = await _services.Runtime.RefreshAsync();
        _services.Navigation.Request(panelId);
        var window = UiThread.Run(() =>
        {
            var created = new MainWindow(_services, new PanelCatalog(_services), UnbuiltTray())
            {
                // Off every screen, but near enough that its screen coordinates still fit the 16 bits a WM_NCHITTEST carries at any display scale.
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000,
                Top = -10000,
                Width = width,
                Height = height,
                ShowInTaskbar = false,
                ShowActivated = false,
            };
            created.Show();
            return created;
        });
        _window = window;

        UiThread.WaitFor(() => window.RootNavigation.MenuItems.OfType<DcNavigationItem>().Any(), "the sidebar to be built");
        UiThread.Run(() => typeof(GatewayMonitor).GetMethod("Publish", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(_services.Monitor, new object[] { StripScene.Snapshot(new[] { "claudecode" }, version: "1.0.0") }));

        // The navigation frame fades and slides a page in; a picture taken before that ends shows it half there.
        var settled = Environment.TickCount64 + 700;
        UiThread.WaitFor(() => Environment.TickCount64 > settled, "the page transition to end", 5000);
        UiThread.Run(UiThread.Settle);
        return window;
    }

    private static T Named<T>(MainWindow window, string name)
        where T : class =>
        window.FindName(name) as T ?? throw new InvalidOperationException($"MainWindow has no {typeof(T).Name} named {name}.");

    private static double Top(FrameworkElement element, FrameworkElement root) => element.TranslatePoint(new Point(0, 0), root).Y;

    /// <summary>What Windows hears when the mouse is over the middle of <paramref name="element"/> (or <paramref name="x"/> DIPs into it): client or caption.</summary>
    private static int HitTest(MainWindow window, FrameworkElement element, double x = -1)
    {
        var screen = element.PointToScreen(new Point(x < 0 ? element.ActualWidth / 2 : x, element.ActualHeight / 2));
        Assert.InRange(screen.X, short.MinValue, short.MaxValue);
        Assert.InRange(screen.Y, short.MinValue, short.MaxValue);
        var lParam = (IntPtr)(((int)Math.Round(screen.Y) << 16) | ((int)Math.Round(screen.X) & 0xFFFF));
        return (int)SendMessage(new WindowInteropHelper(window).Handle, WmNcHitTest, IntPtr.Zero, lParam);
    }

    /// <summary>The window's content at <paramref name="dpi"/>, on the app's own background (the window paints Mica outside it).</summary>
    private static void Png(MainWindow window, string name, double dpi = 96)
    {
        var directory = Environment.GetEnvironmentVariable(RenderTo.EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        UiThread.Run(() =>
        {
            UiThread.Settle();
            var root = (FrameworkElement)window.Content;
            var scale = dpi / 96d;
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * scale), (int)Math.Ceiling(root.ActualHeight * scale), dpi, dpi, PixelFormats.Pbgra32);

            var backdrop = new DrawingVisual();
            using (var context = backdrop.RenderOpen())
            {
                context.DrawRectangle(Application.Current.TryFindResource("ApplicationBackgroundBrush") as Brush ?? Brushes.Black, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            }

            bitmap.Render(backdrop);
            bitmap.Render(root);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            _ = Directory.CreateDirectory(directory);
            using var stream = File.Create(Path.Combine(directory, name + ".png"));
            encoder.Save(stream);
        });
    }

    // ------------------------------------------------------------------ the sidebar

    [Fact]
    public async Task The_sidebar_lists_the_macs_groups_in_its_order_with_the_windows_extension_last_in_discover()
    {
        var window = await OpenAsync(1180, 760);

        UiThread.Run(() =>
        {
            var entries = window.RootNavigation.MenuItems.Cast<object>()
                .Select(item => item switch
                {
                    DcNavigationHeader header => "# " + header.Text,
                    DcNavigationItem entry => AutomationProperties.GetName(entry),
                    _ => "?",
                })
                .ToArray();

            Assert.Equal(
                new[]
                {
                    "# Monitor", "Overview", "Alerts", "Logs", "Audit", "Activity",
                    "# Govern", "Skills", "MCPs", "Plugins", "Tools", "Policies", "Sandboxes",
                    "# Discover", "Inventory", "AI Discovery", "Runtime", "Registries", "Agents",
                    "# Configure", "Setup",
                },
                entries);

            // The runtime has the planes and the ledger, so every entry is on show; Settings stays pinned in the footer.
            Assert.All(window.RootNavigation.MenuItems.OfType<DcNavigationItem>(), entry => Assert.Equal(Visibility.Visible, entry.Visibility));
            Assert.Equal(new[] { "Settings" }, window.RootNavigation.FooterMenuItems.OfType<DcNavigationItem>().Select(AutomationProperties.GetName).ToArray());
        });
    }

    [Fact]
    public async Task Agents_says_it_is_windows_only_and_sandboxes_where_it_runs_in_a_chip_the_tooltip_and_the_help_text()
    {
        var window = await OpenAsync(1180, 760);

        UiThread.Run(() =>
        {
            DcNavigationItem Entry(string name) => window.RootNavigation.MenuItems.OfType<DcNavigationItem>().Single(e => AutomationProperties.GetName(e) == name);

            var agents = Entry("Agents");
            var agentsChip = VisualTree.Find<System.Windows.Controls.Border>(agents, b => b.Name == SidebarTag.ChipName);
            Assert.NotNull(agentsChip);
            Assert.True(agentsChip.IsVisible);
            Assert.Contains(VisualTree.Descendants<System.Windows.Controls.TextBlock>(agentsChip), t => t.Text == "Windows");
            Assert.EndsWith(PanelTag.WindowsOnly.Description, (string)agents.ToolTip, StringComparison.Ordinal);
            Assert.EndsWith(PanelTag.WindowsOnly.Description, AutomationProperties.GetHelpText(agents), StringComparison.Ordinal);

            var sandboxes = Entry("Sandboxes");
            var sandboxesChip = VisualTree.Find<System.Windows.Controls.Border>(sandboxes, b => b.Name == SidebarTag.ChipName);
            Assert.NotNull(sandboxesChip);
            Assert.Contains(VisualTree.Descendants<System.Windows.Controls.TextBlock>(sandboxesChip), t => t.Text == "Linux/macOS");
            Assert.Contains("Linux/macOS only", (string)sandboxes.ToolTip, StringComparison.Ordinal);
            Assert.Contains("not available on Windows", AutomationProperties.GetHelpText(sandboxes), StringComparison.Ordinal);

            // The chip fits beside the title in the 218 DIP pane: nothing of it is cut off.
            var right = sandboxesChip.TranslatePoint(new Point(sandboxesChip.ActualWidth, 0), sandboxes).X;
            Assert.True(right <= sandboxes.ActualWidth, $"the chip ends at {right:0.#}, past the entry's {sandboxes.ActualWidth:0.#}");

            // A panel the Mac has too carries no word.
            Assert.Null(VisualTree.Find<System.Windows.Controls.Border>(Entry("Policies"), b => b.Name == SidebarTag.ChipName));
            Assert.Equal("Policies", Entry("Policies").Content);
        });
    }

    [Fact]
    public async Task Sandboxes_can_be_selected_to_read_why_and_policies_opens_from_its_new_place()
    {
        var window = await OpenAsync(1180, 760, "policies");

        UiThread.Run(() =>
        {
            var policies = window.RootNavigation.MenuItems.OfType<DcNavigationItem>().Single(e => AutomationProperties.GetName(e) == "Policies");
            Assert.True(policies.IsActive);
            Assert.NotNull(VisualTree.Find<PoliciesPanel>(window.RootNavigation));
        });

        UiThread.Run(() => _services.Navigation.Request("sandboxes"));
        UiThread.WaitFor(() => VisualTree.Find<SandboxesPanel>(window.RootNavigation) is { IsVisible: true }, "the Sandboxes page to be shown");
        UiThread.Run(() =>
        {
            var sandboxes = window.RootNavigation.MenuItems.OfType<DcNavigationItem>().Single(e => AutomationProperties.GetName(e) == "Sandboxes");
            Assert.True(sandboxes.IsEnabled);
            Assert.True(sandboxes.IsActive);
        });
    }

    // ------------------------------------------------------------------ the compact header

    [Fact]
    public async Task The_title_bar_holds_the_sidebar_toggle_and_the_palettes_search_box_and_both_take_the_click()
    {
        var window = await OpenAsync(980, 692);

        UiThread.Run(() =>
        {
            var toggle = Named<Wpf.Ui.Controls.Button>(window, "SidebarToggleButton");
            var launcher = Named<PaletteLauncher>(window, "PaletteLauncher");
            var title = Named<System.Windows.Controls.TextBlock>(window, "TitleText");
            var titleBar = Named<TitleBar>(window, "ShellTitleBar");

            Assert.Equal("Toggle sidebar", AutomationProperties.GetName(toggle));
            Assert.Equal("Open command palette", AutomationProperties.GetName(launcher.Button));
            Assert.Equal("Ctrl+K", AutomationProperties.GetAcceleratorKey(launcher.Button));
            Assert.Equal("DefenseClaw for Windows", title.Text);

            // Windows sends the clicks on the toggle and the box to the app, and a press on the title or on the bar's empty middle drags the window.
            Assert.Equal(HtClient, HitTest(window, toggle));
            Assert.Equal(HtClient, HitTest(window, launcher));
            Assert.Equal(HtClient, HitTest(window, Named<Wpf.Ui.Controls.Button>(window, "ShortcutsButton")));
            Assert.Equal(HtCaption, HitTest(window, title));
            Assert.Equal(HtCaption, HitTest(window, titleBar, 700));

            // The sidebar's own toggle row is gone (the entries start under the strip), and the title bar's toggle folds and opens the sidebar.
            Assert.False(window.RootNavigation.IsPaneToggleVisible);
            Assert.True(window.RootNavigation.IsPaneOpen);
            var headings = window.RootNavigation.MenuItems.OfType<DcNavigationHeader>().ToArray();
            toggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, toggle));
            Assert.False(window.RootNavigation.IsPaneOpen);
            Assert.All(headings, heading => Assert.Equal(0, heading.Opacity));
            toggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, toggle));
            Assert.True(window.RootNavigation.IsPaneOpen);
            Assert.All(headings, heading => Assert.Equal(1, heading.Opacity));
        });
    }

    [Fact]
    public async Task The_search_box_opens_the_palette_as_ctrl_k_does()
    {
        var window = await OpenAsync(980, 692);

        UiThread.Run(() =>
        {
            var shell = (ViewModels.MainWindowViewModel)window.DataContext;
            var launcher = Named<PaletteLauncher>(window, "PaletteLauncher");
            Assert.False(shell.IsPaletteOpen);

            launcher.Button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, launcher.Button));

            Assert.True(shell.IsPaletteOpen);
        });
    }

    [Fact]
    public async Task The_strip_is_status_only_one_toolbar_button_high_and_the_sidebar_starts_right_under_it()
    {
        var window = await OpenAsync(980, 692);

        UiThread.Run(() =>
        {
            var root = (FrameworkElement)window.Content;
            var strip = (FrameworkElement)Named<StatusStripControl>(window, "StatusStrip").Parent;
            while (strip is not System.Windows.Controls.Border)
            {
                strip = (FrameworkElement)strip.Parent;
            }

            // Title bar 32, the menu bar (CUST-384) in its row under it, then the strip at most 41 (it was 47: a labelled Refresh, a palette and a shortcuts button).
            Assert.InRange(window.MenuBar.ActualHeight, 28, 32);
            Assert.Equal(32 + window.MenuBar.ActualHeight, Top(strip, root), 0);
            Assert.InRange(strip.ActualHeight, 36, 41.5);

            // Its only button is the gateway's Refresh, an icon with its name; the palette and the shortcuts moved to the title bar.
            var buttons = VisualTree.Descendants<Wpf.Ui.Controls.Button>(strip).ToArray();
            var refresh = Assert.Single(buttons);
            Assert.Equal("Refresh gateway status", AutomationProperties.GetName(refresh));
            Assert.Null(refresh.Content);

            // No 40 DIP toggle row above the first heading: "Monitor" sits within a few DIPs of the strip's rule, and the pane is the Mac's 218.
            var monitor = window.RootNavigation.MenuItems.OfType<DcNavigationHeader>().First();
            var under = Top(monitor, root) - (Top(strip, root) + strip.ActualHeight);
            Assert.InRange(under, 0, 12);
            Assert.Equal(218, window.RootNavigation.OpenPaneLength);

            // The strip's chips (CUST-273) all still show at the narrow size, beside the pill.
            var chips = Named<StatusStripControl>(window, "StatusStrip");
            Assert.True(chips.IsVisible);
        });
    }

    [Fact]
    public async Task A_count_badge_stays_clear_of_the_scroll_bar_of_a_sidebar_that_scrolls_and_folded_it_is_a_dot_on_its_icon()
    {
        var window = await OpenAsync(980, 692);
        var nav = window.RootNavigation;
        var (activity, viewer, bar) = UiThread.Run(() =>
        {
            var entry = nav.MenuItems.OfType<DcNavigationItem>().First(item => AutomationProperties.GetName(item) == "Activity");
            var scroller = VisualTree.Find<DynamicScrollViewer>(nav, candidate => candidate.Name == "PART_ScrollViewer")!;
            return (entry, scroller, VisualTree.Find<DynamicScrollBar>(scroller, candidate => candidate.Name == "PART_VerticalScrollBar")!);
        });

        // Two runs in the runner's list, and Activity not yet visited: its "new since last visit" capsule (CUST-265) reads 2.
        var unread = _services.UnreadCounts;
        _ = _services.Cli.RecordHandOff("defenseclaw", new[] { "keys", "set" }, "synthetic entry");
        _ = _services.Cli.RecordHandOff("defenseclaw", new[] { "keys", "fill-missing" }, "synthetic entry");
        await unread.RefreshAsync();
        UiThread.WaitFor(() => AutomationProperties.GetName(activity) == "Activity, 2 new since last visit", "Activity's capsule");
        UiThread.Run(UiThread.Settle);
        var badge = UiThread.Run(() => (InfoBadge)activity.InfoBadge!);

        // What the badge paints (WPF-UI's capsule is a border inside a 2 DIP margin; the dot is a border), the entry's icon and the bar, in the sidebar's coordinates.
        Rect Bounds(FrameworkElement element) => element.TransformToAncestor(nav).TransformBounds(new Rect(element.RenderSize));
        System.Windows.Controls.Border Fill() => VisualTree.Find<System.Windows.Controls.Border>(badge)!;
        Rect Ink() => Bounds(Fill());
        Rect Icon() => Bounds(VisualTree.Find<System.Windows.Controls.ContentPresenter>(activity, candidate => candidate.Name == "IconContentPresenter")!);
        double BarLeft() => Bounds(bar).Left;

        var cleared = UiThread.Run(() =>
        {
            // At the Mac's small size the sidebar scrolls, and its bar is drawn over the entries: the capsule must end before it (it overlapped by ~9 DIPs).
            Assert.True(viewer.ScrollableHeight > 0.5, $"the sidebar should scroll at 980x692 (it scrolls {viewer.ScrollableHeight:0.#} DIPs)");
            Assert.True(bar.IsVisible && bar.ActualWidth > 0, "the scroll bar should show while the sidebar scrolls");
            Assert.Equal("2", badge.Value);
            Assert.True(Ink().Right <= BarLeft(), $"the capsule ends at {Ink().Right:0.#} but the scroll bar starts at {BarLeft():0.#}");
            return viewer.Padding.Right;
        });
        var resting = UiThread.Run(() => cleared - bar.ActualWidth);
        Png(window, "shell-sidebar-badge-open-980x692");

        // The pane slides between its widths; measure once it has arrived.
        void SlidePane(bool open)
        {
            UiThread.Run(() => nav.IsPaneOpen = open);
            UiThread.WaitFor(() => open ? viewer.ActualWidth >= nav.OpenPaneLength - 8 : viewer.ActualWidth <= nav.CompactPaneLength + 8, open ? "the pane to open" : "the pane to fold");
            UiThread.Run(UiThread.Settle);
        }

        SlidePane(false);
        UiThread.Run(() =>
        {
            // Folded to its icons the pane has no room for the capsule beside the icon (it covered half the icon and ran under the bar): it is a dot in
            // its colour on the icon's top-right corner, clear of the bar, and the entries keep their width. The count stays in the name and tooltip.
            var dot = Ink();
            var icon = Icon();
            Assert.Equal(resting, viewer.Padding.Right, 1);
            Assert.Empty(VisualTree.Descendants<System.Windows.Controls.TextBlock>(badge));
            Assert.Equal(((SolidColorBrush)Application.Current.FindResource("InfoBadgeAttentionSeverityBackgroundBrush")).Color, ((SolidColorBrush)Fill().Background).Color);
            Assert.InRange(dot.Width, 6, 10);
            Assert.InRange(dot.Height, 6, 10);
            Assert.True(dot.Right <= BarLeft(), $"the dot ends at {dot.Right:0.#} but the scroll bar starts at {BarLeft():0.#}");
            var centre = new Point(dot.Left + (dot.Width / 2), dot.Top + (dot.Height / 2));
            Assert.InRange(centre.X - icon.Right, -5, 5);
            Assert.InRange(centre.Y - icon.Top, -5, 5);
            Assert.Equal("Activity, 2 new since last visit", AutomationProperties.GetName(activity));
            Assert.Contains("2 new since last visit", activity.ToolTip as string, StringComparison.Ordinal);
        });
        Png(window, "shell-sidebar-badge-folded-980x692");

        SlidePane(true);
        UiThread.Run(() =>
        {
            Assert.Equal("2", badge.Value);
            Assert.Equal(cleared, viewer.Padding.Right, 1);
            Assert.True(Ink().Right <= BarLeft(), $"the capsule ends at {Ink().Right:0.#} but the scroll bar starts at {BarLeft():0.#}");
        });

        // Tall enough to show every entry, the bar goes and the entries reach the pane's edge again. A test machine whose screen caps the
        // window's height may still scroll; then the capsule is still clear of the bar.
        UiThread.Run(() => window.Height = 900);
        UiThread.Run(UiThread.Settle);
        UiThread.Run(() =>
        {
            if (viewer.ScrollableHeight > 0.5)
            {
                Assert.True(Ink().Right <= BarLeft(), $"the capsule ends at {Ink().Right:0.#} but the scroll bar starts at {BarLeft():0.#}");
            }
            else
            {
                Assert.Equal(resting, viewer.Padding.Right, 1);
            }
        });
    }

    // ------------------------------------------------------------------ renders

    [Theory]
    [InlineData("Light", 980, 692)]
    [InlineData("Dark", 980, 692)]
    [InlineData("Light", 1468, 875)]
    [InlineData("Dark", 1468, 875)]
    public async Task The_shell_is_rendered_for_review_in_the_cisco_look(string mode, int width, int height)
    {
        using var look = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Cisco, Enum.Parse<AppearanceMode>(mode)));
        var window = await OpenAsync(width, height);

        Png(window, $"shell-sandboxes-cisco-{mode.ToLowerInvariant()}-{width}x{height}");
        if (width == 980 && mode == "Light")
        {
            // The same window drawn at 125% and 150%: the layout is in DIPs, so this shows the text and icons at those densities (not a per-monitor DPI change).
            Png(window, "shell-sandboxes-cisco-light-980x692-125pct", 120);
            Png(window, "shell-sandboxes-cisco-light-980x692-150pct", 144);
        }
    }
}
