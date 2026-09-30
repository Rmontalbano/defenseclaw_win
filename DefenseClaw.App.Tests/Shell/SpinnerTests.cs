using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Appearance;
using DefenseClaw.App.Tests.Appearance;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.Views.Controls;
using Wpf.Ui.Controls;

namespace DefenseClaw.App.Tests.Shell;

/// <summary>
/// The busy spinner (D2-1). A WPF-UI <c>ProgressRing</c> that has been shown once keeps a 60 fps animation clock alive for the
/// life of the process, hidden or not, which held a resident tray app at 2-3 % of a core. <see cref="DcSpinner"/> owns its clock
/// only while it is visible and removes it from the timing tree otherwise, and Themes\DefenseClaw.xaml re-templates every
/// <c>ui:ProgressRing</c> to host one, so the 21 rings need no edit. What is asserted is both the mechanism (no clock, nothing
/// animated) and the cost that matters (the UI thread's cycles after a show and a hide).
/// </summary>
[Collection(UiCollection.Name)]
public sealed class SpinnerTests
{
    /// <summary>The bar for "nothing is animating": an idle dispatcher measures 0.0-0.3, one leaked ring 50-70.</summary>
    private const double IdleMcyclesPerSecond = 10;

    // ------------------------------------------------------------------ the control

    [Fact]
    public void A_spinner_holds_a_clock_only_while_it_is_visible()
    {
        UiThread.Run(() =>
        {
            var spinner = new DcSpinner { Width = 20, Height = 20, Visibility = Visibility.Collapsed };
            using var host = new OffscreenHost(new Grid { Children = { spinner } }, 200, 100);

            Assert.False(spinner.HasActiveClock);
            Assert.False(spinner.IsAnimated);

            spinner.Visibility = Visibility.Visible;
            host.Relayout();
            Assert.True(spinner.HasActiveClock);
            Assert.True(spinner.IsAnimated);

            spinner.Visibility = Visibility.Collapsed;
            host.Relayout();
            Assert.False(spinner.HasActiveClock);
            Assert.False(spinner.IsAnimated);

            // And again: the second show must start a fresh clock and the second hide must remove it too.
            spinner.Visibility = Visibility.Visible;
            host.Relayout();
            Assert.True(spinner.HasActiveClock);
            spinner.Visibility = Visibility.Collapsed;
            host.Relayout();
            Assert.False(spinner.HasActiveClock);
            Assert.False(spinner.IsAnimated);
        });
    }

    [Fact]
    public void A_spinner_that_leaves_the_tree_or_is_told_to_stop_lets_go_of_its_clock()
    {
        UiThread.Run(() =>
        {
            var spinner = new DcSpinner { Width = 20, Height = 20 };
            var grid = new Grid { Children = { spinner } };
            using var host = new OffscreenHost(grid, 200, 100);
            Assert.True(spinner.HasActiveClock);

            spinner.IsSpinning = false;
            Assert.False(spinner.HasActiveClock);
            Assert.False(spinner.IsAnimated);

            spinner.IsSpinning = true;
            Assert.True(spinner.HasActiveClock);

            grid.Children.Remove(spinner);
            host.Relayout();
            Assert.False(spinner.HasActiveClock);
            Assert.False(spinner.IsAnimated);
        });
    }

    [Fact]
    public void A_spinner_in_a_window_that_is_hidden_stops_and_resumes_when_it_is_shown_again()
    {
        UiThread.Run(() =>
        {
            // The dashboard closing to the tray hides its window: nothing in it is visible, so nothing may still tick.
            var spinner = new DcSpinner { Width = 20, Height = 20 };
            var window = new Window
            {
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                ShowActivated = false,
                Left = -32000,
                Top = -32000,
                Width = 100,
                Height = 100,
                Content = new Grid { Children = { spinner } },
            };

            try
            {
                window.Show();
                UiThread.Settle();
                Assert.True(spinner.HasActiveClock);

                window.Hide();
                UiThread.Settle();
                Assert.False(spinner.HasActiveClock);
                Assert.False(spinner.IsAnimated);

                window.Show();
                UiThread.Settle();
                Assert.True(spinner.HasActiveClock);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void A_determinate_ring_is_a_still_drawing_of_its_progress()
    {
        // Linear's tokens are literal colours; the test host's bare Application has no accent for Default's to alias.
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Linear, AppearanceMode.Dark));

        UiThread.Run(() =>
        {
            var spinner = new DcSpinner { Width = 24, Height = 24, IsSpinning = false, Progress = 50 };
            using var host = new OffscreenHost(new Grid { Children = { spinner } }, 100, 100);

            Assert.False(spinner.HasActiveClock);
            Assert.True(OpaquePixels(host.Content) > 0);

            // The track is the whole ring, so more progress changes the colour of what is drawn, not how much: compare the pictures.
            var half = Picture(host.Content);
            spinner.Progress = 100;
            host.Relayout();
            Assert.NotEqual(half, Picture(host.Content));
            spinner.Progress = 0;
            host.Relayout();
            Assert.NotEqual(half, Picture(host.Content));
        });
    }

    // ------------------------------------------------------------------ the cost

    [Fact]
    public void After_a_ring_is_shown_and_hidden_the_UI_thread_is_back_to_idle_and_stays_there_on_the_second_round()
    {
        // The element every view declares, not the spinner: this is the measurement that read 50-70 Mcycles/s per ring
        // with WPF-UI's own template (67 hidden after the first show, 92 after the second with the obvious style trigger).
        RingHost? host = null;
        UiThread.Run(() => host = new RingHost());

        UiThread.Run(() => host!.Set(Visibility.Visible));
        Thread.Sleep(300);
        UiThread.Run(() => host!.Set(Visibility.Collapsed));
        Thread.Sleep(250);
        var hidden = UiThreadCost.Measure(TimeSpan.FromSeconds(1), attempts: 3, settledBelow: IdleMcyclesPerSecond);

        UiThread.Run(() => host!.Set(Visibility.Visible));
        Thread.Sleep(300);
        UiThread.Run(() => host!.Set(Visibility.Collapsed));
        Thread.Sleep(250);
        var hiddenAgain = UiThreadCost.Measure(TimeSpan.FromSeconds(1), attempts: 3, settledBelow: IdleMcyclesPerSecond);

        // Removed from the tree while showing: the clock must not outlive the ring.
        UiThread.Run(() => host!.Set(Visibility.Visible));
        Thread.Sleep(300);
        UiThread.Run(() => host!.Dispose());
        Thread.Sleep(250);
        var removed = UiThreadCost.Measure(TimeSpan.FromSeconds(1), attempts: 3, settledBelow: IdleMcyclesPerSecond);

        Assert.True(hidden < IdleMcyclesPerSecond, $"hidden after a show: {hidden:0.0} Mcycles/s (a leaked ring is 50-70)");
        Assert.True(hiddenAgain < IdleMcyclesPerSecond, $"hidden after a second show: {hiddenAgain:0.0} Mcycles/s");
        Assert.True(removed < IdleMcyclesPerSecond, $"removed while showing: {removed:0.0} Mcycles/s");
    }

    // ------------------------------------------------------------------ the ProgressRing every view declares

    [Fact]
    public void A_ProgressRing_takes_the_spinner_through_the_implicit_style_and_only_builds_it_when_shown()
    {
        UiThread.Run(() =>
        {
            var ring = new ProgressRing { IsIndeterminate = true, Width = 16, Height = 16, Visibility = Visibility.Collapsed };
            using var host = new OffscreenHost(new Grid { Children = { ring } }, 200, 100);

            // Collapsed: never measured, so no template, so no spinner and no clock.
            Assert.Null(VisualTree.Find<DcSpinner>(ring));

            ring.Visibility = Visibility.Visible;
            host.Relayout();
            var spinner = VisualTree.Find<DcSpinner>(ring);
            Assert.NotNull(spinner);
            Assert.True(spinner!.HasActiveClock);
            Assert.Equal(16, spinner.ActualWidth);

            ring.Visibility = Visibility.Collapsed;
            host.Relayout();
            Assert.False(spinner.HasActiveClock);
            Assert.False(spinner.IsAnimated);
            Assert.DoesNotContain(VisualTree.Descendants<UIElement>(ring), e => e.HasAnimatedProperties);
        });
    }

    [Fact]
    public void A_ring_that_is_not_indeterminate_shows_its_progress_and_does_not_turn()
    {
        UiThread.Run(() =>
        {
            var ring = new ProgressRing { IsIndeterminate = false, Progress = 40, Width = 24, Height = 24 };
            using var host = new OffscreenHost(new Grid { Children = { ring } }, 200, 100);

            var spinner = VisualTree.Find<DcSpinner>(ring);
            Assert.NotNull(spinner);
            Assert.False(spinner!.IsSpinning);
            Assert.Equal(40, spinner.Progress);
            Assert.False(spinner.HasActiveClock);
        });
    }

    [Fact]
    public void Every_ring_in_the_shipped_XAML_is_left_to_the_implicit_style()
    {
        // The re-template only reaches a ring that takes the implicit style: one with its own Style, or one inside a
        // scope that declares an implicit ProgressRing style of its own, would quietly keep WPF-UI's leaking one.
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var app = AppDirectory();
        var rings = 0;
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(app, "*.xaml", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                                 && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            var name = Path.GetFileName(file);
            var document = XDocument.Load(file);
            foreach (var element in document.Descendants().Where(e => e.Name.LocalName == "ProgressRing"))
            {
                rings++;
                if (element.Attribute("Style") is not null)
                {
                    offenders.Add($"{name}: a ProgressRing sets its own Style");
                }
            }

            foreach (var style in document.Descendants(presentation + "Style"))
            {
                var target = (string?)style.Attribute("TargetType") ?? string.Empty;
                if (target.EndsWith("ProgressRing}", StringComparison.Ordinal) || target.EndsWith(":ProgressRing", StringComparison.Ordinal))
                {
                    if (name != "DefenseClaw.xaml")
                    {
                        offenders.Add($"{name}: a Style targets ProgressRing");
                    }
                }
            }
        }

        Assert.Empty(offenders);
        Assert.True(rings >= 21, $"expected the 21 rings the review counted, found {rings}");
    }

    [Fact]
    public void Every_ring_on_every_panel_hosts_a_spinner_and_none_keeps_animating_once_hidden()
    {
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp);

        // AuditPanel is not built here: its ListViewItem style is based on one that only exists under the app's Fluent
        // ThemeMode, which the test host's Application does not set (review finding D4-13). Its ring is covered by the XAML
        // guard above and is the same one-line declaration as the others.
        var panels = new PanelCatalog(services).Panels
            .Select(p => p.ViewType)
            .Where(t => t.Name != "AuditPanel")
            .ToArray();
        var showMethod = typeof(PanelShell).GetMethod(nameof(PanelShell.Show))!;

        PanelShell? shell = null;
        UiThread.Run(() => shell = new PanelShell(services, 1400, 900));
        var ringsSeen = 0;
        var problems = new List<string>();
        try
        {
            foreach (var view in panels)
            {
                UiThread.Run(() =>
                {
                    var page = (FrameworkElement)showMethod.MakeGenericMethod(view).Invoke(shell, null)!;
                    foreach (var ring in Rings(page))
                    {
                        ringsSeen++;

                        // Whatever the panel's busy flag would do: show the ring (and anything collapsed above it), then put it back.
                        var changed = new List<FrameworkElement>();
                        for (DependencyObject? node = ring; node is FrameworkElement element; node = LogicalTreeHelper.GetParent(node))
                        {
                            if (element.Visibility != Visibility.Visible)
                            {
                                element.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Visible);
                                changed.Add(element);
                            }

                            if (ReferenceEquals(element, page))
                            {
                                break;
                            }
                        }

                        shell!.Host.Relayout();

                        // A ring on a tab that is not selected has no visual tree to measure: its template is still the one that counts.
                        ring.ApplyTemplate();
                        var spinner = VisualTree.Find<DcSpinner>(ring);
                        if (spinner is null)
                        {
                            problems.Add($"{view.Name}: the ProgressRing '{AutomationProperties.GetName(ring)}' did not host a DcSpinner");
                        }
                        else if (ring.IsVisible && !spinner.HasActiveClock)
                        {
                            problems.Add($"{view.Name}: a visible spinner is not turning");
                        }

                        foreach (var element in changed)
                        {
                            element.InvalidateProperty(UIElement.VisibilityProperty);
                        }

                        shell.Host.Relayout();
                    }

                    foreach (var spinner in VisualTree.Descendants<DcSpinner>(page).Where(s => !s.IsVisible))
                    {
                        if (spinner.HasActiveClock || spinner.IsAnimated)
                        {
                            problems.Add($"{view.Name}: a hidden spinner still holds a clock");
                        }
                    }
                });
            }

            // Nothing is on screen that spins, so the UI thread should be idle: this is the number that was 50-70 per ring.
            Thread.Sleep(250);
            var idle = UiThreadCost.Measure(TimeSpan.FromSeconds(1), attempts: 3, settledBelow: IdleMcyclesPerSecond);
            Assert.Empty(problems);
            Assert.True(ringsSeen >= 18, $"expected to reach at least 18 rings on the panels, reached {ringsSeen}");
            Assert.True(idle < IdleMcyclesPerSecond, $"after every panel showed and hid its rings the UI thread costs {idle:0.0} Mcycles/s");
        }
        finally
        {
            UiThread.Run(() => shell?.Dispose());
        }
    }

    // ------------------------------------------------------------------ the look

    [Theory]
    [InlineData("Default", "Dark")]
    [InlineData("Linear", "Light")]
    [InlineData("Tui", "Dark")]
    public void The_spinner_draws_in_every_look(string style, string mode)
    {
        using var fixture = new AppearanceFixture(new AppearanceSettings(Enum.Parse<AppearanceStyle>(style), Enum.Parse<AppearanceMode>(mode)));

        UiThread.Run(() =>
        {
            var spinner = new DcSpinner { Width = 32, Height = 32 };
            using var host = new OffscreenHost(new Grid { Children = { spinner } }, 100, 100);
            RenderTo.Png(host, $"spinner-{style}-{mode}");

            Assert.True(spinner.HasActiveClock);
            Assert.True(OpaquePixels(host.Content) > 0, "the spinner painted nothing");
        });
    }

    [Fact]
    public void A_live_style_switch_restarts_a_running_spinner_and_leaves_a_stopped_one_alone()
    {
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Default, AppearanceMode.Dark));

        UiThread.Run(() =>
        {
            var running = new DcSpinner { Width = 20, Height = 20 };
            var stopped = new DcSpinner { Width = 20, Height = 20, Visibility = Visibility.Collapsed };
            using var host = new OffscreenHost(new StackPanel { Children = { running, stopped } }, 200, 100);
            Assert.True(running.HasActiveClock);
            Assert.False(stopped.HasActiveClock);

            fixture.Service.SetStyle(AppearanceStyle.Tui);
            host.Relayout();
            Assert.True(running.HasActiveClock, "a spinner that was turning still turns after the style changed");
            Assert.True(running.IsAnimated);
            Assert.False(stopped.HasActiveClock);

            fixture.Service.SetStyle(AppearanceStyle.Default);
            host.Relayout();
            Assert.True(running.HasActiveClock);
        });
    }

    // ------------------------------------------------------------------ helpers

    private static IEnumerable<ProgressRing> Rings(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is ProgressRing ring)
            {
                yield return ring;
            }

            foreach (var nested in Rings(child))
            {
                yield return nested;
            }
        }
    }

    /// <summary>How many pixels the host's content paints with any opacity: enough to tell a drawing from a blank.</summary>
    private static int OpaquePixels(FrameworkElement root)
    {
        var pixels = Picture(root);
        var count = 0;
        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] > 0)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// The pixels of a host's root element (at the origin; rendering a visual includes its offset in its parent, so the
    /// spinner itself would be cropped out of its own bitmap).
    /// </summary>
    private static byte[] Picture(FrameworkElement root)
    {
        var width = (int)Math.Ceiling(root.ActualWidth);
        var height = (int)Math.Ceiling(root.ActualHeight);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }

    private static string AppDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "DefenseClaw.App", "MainWindow.xaml");
            if (File.Exists(candidate))
            {
                return Path.GetDirectoryName(candidate)!;
            }
        }

        throw new FileNotFoundException("DefenseClaw.App\\MainWindow.xaml was not found above the test output directory.");
    }

    /// <summary>One ring in an off-screen host whose visibility a test flips, as a view-model's busy flag would.</summary>
    private sealed class RingHost : IDisposable
    {
        private readonly ProgressRing _ring = new() { IsIndeterminate = true, Width = 20, Height = 20, Visibility = Visibility.Collapsed };
        private readonly OffscreenHost _host;

        public RingHost()
        {
            _host = new OffscreenHost(new Grid { Children = { _ring } }, 200, 100);
        }

        public void Set(Visibility visibility)
        {
            _ring.Visibility = visibility;
            _host.Relayout();
        }

        public void Dispose() => _host.Dispose();
    }
}
