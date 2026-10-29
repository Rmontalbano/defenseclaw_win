using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Appearance;
using DefenseClaw.App.Tests.Appearance;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.Views.Shell;
using DefenseClaw.Core.Cli;
using Wpf.Ui.Controls;
using MenuItem = System.Windows.Controls.MenuItem;

namespace DefenseClaw.App.Tests.Shell;

/// <summary>
/// The menu bar as WPF draws it (CUST-384): a keyboard menu (Alt / F10, access keys, not a Tab stop), each row the registry command it stands for,
/// re-read in place when a menu opens without running anything, and choosing a row hands the window that very command. Plus the real dashboard
/// window with the bar under its title bar. Nothing here shows a popup (a menu's popup would open on the real screen): opening is the
/// <c>SubmenuOpened</c> event the bar listens to, and the submenu renders are the popup's own content hosted off screen. Synthetic data only; PNGs are
/// written only when <c>DC_RENDER_DIR</c> names a folder.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class ShellMenuBarViewTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly AppServices _services;
    private readonly PanelCatalog _catalog;
    private readonly ShellActions _actions;
    private readonly FakeAppearance _appearance = new();
    private readonly List<string> _ran = new();
    private readonly List<string> _toasts = new();
    private readonly List<CliInvocation> _activity = new();
    private int _reads;

    public ShellMenuBarViewTests()
    {
        _services = TestServices.Create(_temp, "gateway:\n  api_port: 18970\n");
        _catalog = new PanelCatalog(_services);
        var tray = (TrayIconService)RuntimeHelpers.GetUninitializedObject(typeof(TrayIconService));
        _actions = new ShellActions(_services, _catalog, tray, () => null)
        {
            Toast = (title, message) => _toasts.Add(title + ": " + message),
            ActivitySource = () => _activity.ToArray(),
            AppUpdateChecker = () => Task.CompletedTask,
            AboutPresenter = (_, _) => { },
        };
    }

    public void Dispose()
    {
        _services.Dispose();
        _temp.Dispose();
    }

    /// <summary>The menus over the real registry, with every row's handler replaced by one that only writes its id down.</summary>
    private IReadOnlyList<ShellMenu> Menus(PanelCatalog? catalog = null)
    {
        _reads++;
        catalog ??= _catalog;
        var window = new ShellWindowCommands(() => _ran.Add("palette"), () => _ran.Add("tray"), () => _ran.Add("exit"), () => true, () => _ran.Add("sidebar"));
        var commands = ShellCommandRegistry.Build(catalog, _actions, _ => _ran.Add("navigate"), () => _ran.Add("shortcuts"), _appearance, curated: null, _services.ConnectorScope)
            .Concat(ShellCommandRegistry.BuildMenuOnlyCommands(_actions, window))
            .Select(c => c with { Run = () => _ran.Add(c.Id) })
            .ToList();
        return ShellMenuModel.Build(commands, catalog, id => ShellMenuModel.CheckedState(id, _appearance, sidebarOpen: true));
    }

    private static IEnumerable<MenuItem> Items(ItemsControl parent)
    {
        foreach (var child in parent.Items.OfType<MenuItem>())
        {
            yield return child;
            foreach (var nested in Items(child))
            {
                yield return nested;
            }
        }
    }

    /// <summary>The row of this command; Settings is in both File and Go, so a menu can be named.</summary>
    private static MenuItem Row(ShellMenuBar bar, string id, string? menu = null) =>
        Items(menu is null ? bar.MenuBar : bar.TopLevel(menu)!).Single(item => item.Tag is ShellCommand command && command.Id == id);

    private static void Open(MenuItem top) => top.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent, top));

    private ShellMenuBar Bar(Func<IReadOnlyList<ShellMenu>>? source = null)
    {
        var bar = new ShellMenuBar { Source = source ?? (() => Menus()) };
        bar.Rebuild();
        return bar;
    }

    // ------------------------------------------------------------------ keyboard and accessibility

    [Fact]
    public void The_bar_is_the_windows_main_menu_reached_with_Alt_or_F10_not_with_Tab()
    {
        UiThread.Run(() =>
        {
            var bar = Bar();
            using var host = new OffscreenHost(bar, 980, 40);

            Assert.True(bar.MenuBar.IsMainMenu);
            Assert.Equal(KeyboardNavigationMode.None, KeyboardNavigation.GetTabNavigation(bar.MenuBar));
            Assert.Equal("Menu bar", AutomationProperties.GetName(bar.MenuBar));
            Assert.Empty(bar.InputBindings);
            Assert.Empty(bar.MenuBar.InputBindings);

            var tops = bar.MenuBar.Items.OfType<MenuItem>().ToList();
            Assert.Equal(new[] { "_File", "_View", "_Monitor", "_Commands", "_Go", "_Help" }, tops.Select(t => (string)t.Header));
            Assert.Equal(new[] { "File", "View", "Monitor", "Commands", "Go", "Help" }, tops.Select(AutomationProperties.GetName));

            // The rows show the chord as text only; no row carries a command or a key binding of its own (the window's key handler owns every chord).
            foreach (var item in Items(bar.MenuBar))
            {
                Assert.Null(item.Command);
                Assert.Empty(item.InputBindings);
                Assert.False(string.IsNullOrEmpty(AutomationProperties.GetName(item)), $"{item.Header} has no accessible name");
            }
        });
    }

    [Fact]
    public void Each_row_shows_its_commands_chord_state_and_sentence()
    {
        UiThread.Run(() =>
        {
            var bar = Bar();
            using var host = new OffscreenHost(bar, 980, 40);

            var palette = Row(bar, ShellCommandRegistry.PaletteId);
            Assert.Equal("Ctrl+K", palette.InputGestureText);
            Assert.Equal("Command palette…", AutomationProperties.GetName(palette));
            Assert.Equal("Ctrl+K", AutomationProperties.GetAcceleratorKey(palette));

            Assert.Equal("F5", Row(bar, "app.refresh-panel").InputGestureText);
            Assert.Equal("Ctrl+,", Row(bar, "nav.settings", ShellMenuModel.FileKey).InputGestureText);
            Assert.Equal("Ctrl+,", Row(bar, "nav.settings", ShellMenuModel.GoKey).InputGestureText);
            Assert.Equal("Ctrl+1", Row(bar, "nav.overview").InputGestureText);

            // Nothing in Activity yet: copy is off, and says why where the palette says it.
            var copy = Row(bar, "app.copy-last-output");
            Assert.False(copy.IsEnabled);
            Assert.Equal("There is no command output to copy yet.", copy.ToolTip);
            Assert.True(ToolTipService.GetShowOnDisabled(copy));
            Assert.Equal("Unavailable. There is no command output to copy yet.", AutomationProperties.GetHelpText(copy));

            // A panel the runtime does not offer (nothing probed here): greyed with the palette's sentence and no chord.
            var runtime = Row(bar, "nav.ai-runtime");
            Assert.False(runtime.IsEnabled);
            Assert.Equal(string.Empty, runtime.InputGestureText);
            Assert.Equal(_catalog.Gate(_catalog.ById("ai-runtime")!).Reason, runtime.ToolTip);

            // The check marks: the style in use, and the sidebar while it is open.
            Assert.True(Row(bar, ShellMenuModel.StyleId(AppearanceStyle.Default)).IsChecked);
            Assert.False(Row(bar, ShellMenuModel.StyleId(AppearanceStyle.Linear)).IsChecked);
            Assert.True(Row(bar, ShellCommandRegistry.SidebarId).IsChecked);

            // The Go menu's group headings are separators: never a row to land on, named for a screen reader.
            var go = bar.TopLevel(ShellMenuModel.GoKey)!;
            var headings = go.Items.OfType<Separator>().Where(s => s.Tag is string).ToList();
            Assert.Equal(PanelCatalog.Groups, headings.Select(s => (string)s.Tag));
            Assert.All(headings, s => Assert.False(s.Focusable));
            Assert.All(headings, s => Assert.Equal((string)s.Tag, AutomationProperties.GetName(s)));
        });
    }

    // ------------------------------------------------------------------ opening and choosing

    [Fact]
    public void Opening_a_menu_re_reads_its_rows_in_place_and_runs_nothing()
    {
        UiThread.Run(() =>
        {
            var bar = Bar();
            using var host = new OffscreenHost(bar, 980, 40);
            var copy = Row(bar, "app.copy-last-output");
            Assert.False(copy.IsEnabled);

            // Something ran: the next open of Commands says copy can act now - on the same item, so focus and the popup are undisturbed.
            var invocation = InvocationFactory.Create(argv: new[] { "doctor" });
            InvocationFactory.Append(invocation, "all checks passed");
            InvocationFactory.Finish(invocation, 0);
            _activity.Add(invocation);

            var reads = _reads;
            Open(bar.TopLevel(ShellMenuModel.CommandsKey)!);
            Assert.Equal(reads + 1, _reads);
            Assert.Same(copy, Row(bar, "app.copy-last-output"));
            Assert.True(copy.IsEnabled);
            Assert.Equal("Put the newest Activity entry's output on the clipboard.", copy.ToolTip);

            // Every menu opened: re-read, nothing run, nothing toasted, nothing asked of the shell.
            foreach (var top in bar.MenuBar.Items.OfType<MenuItem>())
            {
                Open(top);
            }

            Assert.Equal(reads + 7, _reads);
            Assert.Empty(_ran);
            Assert.Empty(_toasts);
            Assert.Null(_services.Navigation.Pending);
            Assert.Empty(_services.Cli.Activity);

            // A submenu inside a menu (View's Style) does not read again: its menu was read as it opened.
            var style = Items(bar.MenuBar).Single(i => i.Tag is string key && key == ShellMenuModel.StyleKey);
            Open(style);
            Assert.Equal(reads + 7, _reads);
        });
    }

    [Fact]
    public void Choosing_a_row_hands_the_window_the_registry_command_it_stands_for()
    {
        UiThread.Run(() =>
        {
            var menus = Menus();
            var bar = Bar(() => menus);
            using var host = new OffscreenHost(bar, 980, 40);
            var chosen = new List<ShellCommand>();
            bar.CommandChosen += (_, command) => chosen.Add(command);

            var exit = Row(bar, ShellCommandRegistry.ExitId);
            exit.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, exit));

            var expected = ShellMenuModel.Rows(menus.Single(m => m.Key == ShellMenuModel.FileKey).Entries).Single(r => r.Id == ShellCommandRegistry.ExitId).Command;
            Assert.Same(expected, Assert.Single(chosen));

            // A greyed-out row hands nothing over, whatever reaches it.
            var copy = Row(bar, "app.copy-last-output");
            copy.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, copy));
            Assert.Single(chosen);

            // The bar runs nothing itself: the window does, when it is handed the command.
            Assert.Empty(_ran);
        });
    }

    [Fact]
    public void A_menu_whose_rows_changed_is_rebuilt_from_the_sidebar_as_it_now_is()
    {
        UiThread.Run(() =>
        {
            // A sidebar with a page more in Govern (another change to the catalog, here a made-up Approvals page): the Go menu follows it on its next open.
            var panels = _catalog.Panels.ToList();
            panels.Insert(panels.FindIndex(p => p.Id == "tools") + 1, new PanelDescriptor("approvals", "Approvals", "Govern", SymbolRegular.Gavel24, typeof(Grid), _ => throw new NotSupportedException()));
            var wider = new PanelCatalog(_services, panels);

            var current = _catalog;
            var bar = Bar(() => Menus(current));
            using var host = new OffscreenHost(bar, 980, 40);
            Assert.DoesNotContain(Items(bar.MenuBar), i => i.Tag is ShellCommand { Id: "nav.approvals" });

            current = wider;
            Open(bar.TopLevel(ShellMenuModel.GoKey)!);

            var go = bar.TopLevel(ShellMenuModel.GoKey)!;
            var ids = go.Items.OfType<MenuItem>().Select(i => ((ShellCommand)i.Tag).Id).ToList();
            Assert.Equal(wider.SidebarOrder.Select(p => "nav." + p.Id).Concat(wider.FooterPanels.Select(p => "nav." + p.Id)), ids);
            Assert.True(ids.IndexOf("nav.approvals") == ids.IndexOf("nav.tools") + 1);
        });
    }

    // ------------------------------------------------------------------ the bar in the real window, and renders

    [Theory]
    [InlineData(980, 692, "Dark")]
    [InlineData(1468, 875, "Dark")]
    [InlineData(980, 692, "Light")]
    [InlineData(1468, 875, "Light")]
    public void The_bar_sits_directly_under_the_title_bar_of_the_dashboard(int width, int height, string mode)
    {
        using var look = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Default, mode == "Dark" ? AppearanceMode.Dark : AppearanceMode.Light));
        MainWindow? window = null;
        try
        {
            window = UiThread.Run(() =>
            {
                var created = new MainWindow(_services, new PanelCatalog(_services), (TrayIconService)RuntimeHelpers.GetUninitializedObject(typeof(TrayIconService)))
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -32000,
                    Top = -32000,
                    Width = width,
                    Height = height,
                    ShowInTaskbar = false,
                    ShowActivated = false,
                };
                created.Show();
                return created;
            });

            var shown = window;
            UiThread.WaitFor(() => ((ShellMenuBar)shown.FindName("MenuBar")).MenuBar.Items.Count == 6, "the menu bar to be built");

            UiThread.Run(() =>
            {
                UiThread.Settle();
                var root = (FrameworkElement)shown.Content;
                var bar = (ShellMenuBar)shown.FindName("MenuBar");
                var title = VisualTree.Find<TitleBar>(root)!;
                var strip = (FrameworkElement)((FrameworkElement)shown.FindName("StatusStrip")).Parent;

                double Top(FrameworkElement element) => element.TransformToAncestor(root).Transform(new Point(0, 0)).Y;
                Png(root, $"cust384-shell-{width}x{height}-{mode.ToLowerInvariant()}");

                Assert.Equal(0, Top(title), 1);
                Assert.Equal(title.ActualHeight, Top(bar), 1);
                Assert.True(Top(strip) >= Top(bar) + bar.ActualHeight - 0.5, "the status strip starts below the menu bar");
                Assert.True(bar.ActualHeight is >= 20 and <= 40, $"the bar is {bar.ActualHeight} DIP tall");

                // The bar's menus are the File ... Help of the model, built from the window's own registry.
                Assert.Equal(new[] { "File", "View", "Monitor", "Commands", "Go", "Help" }, bar.MenuBar.Items.OfType<MenuItem>().Select(AutomationProperties.GetName));
            });
        }
        finally
        {
            if (window is not null)
            {
                UiThread.Run(() =>
                {
                    window.AllowClose();
                    window.Close();
                });
            }
        }
    }

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void Render_the_open_menus_as_their_popups_draw_them(string mode)
    {
        using var look = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Default, mode == "Dark" ? AppearanceMode.Dark : AppearanceMode.Light));
        UiThread.Run(() =>
        {
            var bar = Bar();
            using var host = new OffscreenHost(bar, 980, 40);

            foreach (var key in new[] { ShellMenuModel.FileKey, ShellMenuModel.ViewKey, ShellMenuModel.CommandsKey, ShellMenuModel.GoKey })
            {
                var top = bar.TopLevel(key)!;
                _ = top.ApplyTemplate();
                var popup = VisualTree.Descendants<Popup>(top).First();
                var content = (FrameworkElement)popup.Child;

                // The popup's own content (its border and the rows), lent to an off-screen host for the picture and given back: no popup opens.
                popup.Child = null;
                try
                {
                    var frame = new Border { Padding = new Thickness(12), Child = content, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
                    frame.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    using var shot = new OffscreenHost(frame, Math.Ceiling(frame.DesiredSize.Width), Math.Ceiling(frame.DesiredSize.Height));

                    var rows = VisualTree.Descendants<MenuItem>(content).Count();
                    Assert.True(rows > 0, $"the {key} menu draws its rows");
                    RenderTo.Png(shot, $"cust384-menu-{key}-{mode.ToLowerInvariant()}");
                    frame.Child = null;
                }
                finally
                {
                    popup.Child = content;
                }
            }
        });
    }

    /// <summary>A PNG of <paramref name="element"/> on the window's own background (the window paints Mica outside it).</summary>
    private static void Png(FrameworkElement element, string name)
    {
        var directory = Environment.GetEnvironmentVariable(RenderTo.EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        var width = (int)Math.Ceiling(element.ActualWidth);
        var height = (int)Math.Ceiling(element.ActualHeight);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var backdrop = new DrawingVisual();
        using (var context = backdrop.RenderOpen())
        {
            context.DrawRectangle(Application.Current.TryFindResource("ApplicationBackgroundBrush") as Brush ?? Brushes.Black, null, new Rect(0, 0, width, height));
        }

        bitmap.Render(backdrop);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        _ = Directory.CreateDirectory(directory);
        using var stream = File.Create(Path.Combine(directory, name + ".png"));
        encoder.Save(stream);
    }
}
