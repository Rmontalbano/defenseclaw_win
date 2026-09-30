using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Appearance;
using DefenseClaw.App.Services.Updates;
using DefenseClaw.App.Tests.Appearance;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Shell;
using UiButton = Wpf.Ui.Controls.Button;

namespace DefenseClaw.App.Tests.Shell;

/// <summary>
/// The update banner as drawn (CUST-206): slim, with its three actions reachable and named for a screen reader, its words readable on
/// its own fill, closed it takes no room, and it is built from the style's tokens, so it reads in all four looks in both modes.
/// Rendered offscreen on the shared UI thread; set <c>DC_RENDER_DIR</c> to keep a PNG of each.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class UpdateBannerViewTests : IDisposable
{
    private const string Repo = "https://github.com/cisco-ai-defense/defenseclaw/";

    private readonly TempDirectory _temp = new();
    private readonly List<IDisposable> _disposables = new();

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }

        _temp.Dispose();
    }

    public static IEnumerable<object[]> Looks() =>
        from style in Enum.GetValues<AppearanceStyle>()
        from mode in new[] { AppearanceMode.Dark, AppearanceMode.Light }
        select new object[] { style.ToString(), mode.ToString() };

    private UpdateCheckResult _answer = new()
    {
        State = UpdateCheckState.UpdateAvailable,
        InstalledVersion = "0.8.10",
        LatestVersion = "v0.8.11",
        HtmlUrl = Repo + "releases/tag/v0.8.11",
    };

    /// <summary>A composition whose watcher has already found the release in <see cref="_answer"/>.</summary>
    private AppServices NewServices()
    {
        var services = AppServices.CreateIsolated(
            TestServices.IsolatedPaths(_temp.Path),
            claudeSettingsPath: _temp.File("claude-settings.json"),
            updateWatcherFactory: s => new UpdateWatcher(s.Settings, s.Monitor, (_, _) => Task.FromResult(_answer), post: action => action()));
        _disposables.Add(services);
        _ = services.UpdateWatcher.CheckNowAsync().GetAwaiter().GetResult();
        return services;
    }

    /// <summary>The banner as MainWindow hosts it: in the banner region's StackPanel, which gives it its own height and 24 px gutters.</summary>
    private static StackPanel InWindowBannerRegion(UpdateBanner banner) =>
        new() { Margin = new Thickness(24, 0, 24, 0), VerticalAlignment = VerticalAlignment.Top, Children = { banner } };

    private static IEnumerable<UiButton> Buttons(UpdateBanner banner) => VisualTree.Descendants<UiButton>(banner);

    [Theory]
    [MemberData(nameof(Looks))]
    public void The_banner_is_slim_names_its_actions_and_reads_in_every_look(string style, string mode)
    {
        var services = NewServices();
        using var fixture = new AppearanceFixture(new AppearanceSettings(Enum.Parse<AppearanceStyle>(style), Enum.Parse<AppearanceMode>(mode)));

        UiThread.Run(() =>
        {
            var shell = new MainWindowViewModel(services, reviewUpdate: () => { }, openReleasePage: _ => { });
            var banner = new UpdateBanner { DataContext = shell };
            using var host = new OffscreenHost(InWindowBannerRegion(banner), 1000, 140);

            var bar = (Border)banner.FindName("Bar");
            Assert.Equal(Visibility.Visible, bar.Visibility);

            // Slim: two short lines beside a row of buttons, and as wide as the region it sits in.
            Assert.InRange(banner.ActualHeight, 40, 80);
            Assert.True(banner.ActualWidth > 900, $"the banner should span the region, it is {banner.ActualWidth} wide");

            var title = VisualTree.Find<TextBlock>(banner, t => t.Text == "DefenseClaw 0.8.11 is available");
            Assert.NotNull(title);
            Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(title));
            Assert.NotNull(VisualTree.Find<TextBlock>(banner, t => t.Text.StartsWith("Installed: 0.8.10.", StringComparison.Ordinal)));

            var buttons = Buttons(banner).ToList();
            Assert.Equal(new[] { "Review update", "Release notes", "Dismiss update notice" }, buttons.Select(b => AutomationProperties.GetName(b)).ToArray());
            Assert.All(buttons, button =>
            {
                Assert.True(button.IsEnabled, AutomationProperties.GetName(button));
                Assert.True(button.Focusable && button.IsTabStop, AutomationProperties.GetName(button) + " cannot be reached from the keyboard");
                Assert.True(button.ActualHeight > 0 && button.ActualWidth > 0, AutomationProperties.GetName(button) + " has no size");
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetHelpText(button)));
            });

            // Every action sits inside the bar, none clipped off the right edge.
            var origin = banner.TranslatePoint(default, host.Content);
            foreach (var button in buttons)
            {
                var right = button.TranslatePoint(new Point(button.ActualWidth, 0), host.Content).X;
                Assert.True(right <= origin.X + banner.ActualWidth + 0.5, AutomationProperties.GetName(button) + " is clipped");
            }

            // The words read on the bar's own fill (the surface, as a card's, over the window: Default's is translucent): primary text
            // for the title and secondary for the line under it, at the 4.5:1 the rest of the app holds its text to; the accent edge
            // and icon at the 3:1 of non-text.
            Assert.Equal(AppearanceFixture.ColorOf(AppearanceTokens.Surface), ((SolidColorBrush)bar.Background).Color);
            var fill = Composite(AppearanceFixture.ColorOf(AppearanceTokens.Surface), AppearanceFixture.ColorOf(AppearanceTokens.WindowBackground));
            Assert.True(Ratio(AppearanceFixture.ColorOf(AppearanceTokens.TextPrimary), fill) >= 4.5, $"the title on the bar in {style} {mode}");
            Assert.True(Ratio(AppearanceFixture.ColorOf(AppearanceTokens.TextSecondary), fill) >= 4.5, $"the message on the bar in {style} {mode}");
            Assert.True(Ratio(AppearanceFixture.ColorOf(AppearanceTokens.Accent), fill) >= 3, $"the accent edge on the bar in {style} {mode}");
            Assert.True(Ratio(AppearanceFixture.ColorOf(AppearanceTokens.SectionUpdates), fill) >= 3, $"the icon on the bar in {style} {mode}");

            RenderTo.Png(host, $"update-banner-{style}-{mode}".ToLowerInvariant());
            shell.Dispose();
        });
    }

    [Theory]
    [InlineData(940)]
    [InlineData(1200)]
    public void At_the_windows_minimum_width_and_wider_every_action_is_in_view_and_the_bar_stays_slim(int windowWidth)
    {
        var services = NewServices();
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Cisco, AppearanceMode.Light));

        UiThread.Run(() =>
        {
            var shell = new MainWindowViewModel(services, reviewUpdate: () => { }, openReleasePage: _ => { });
            var banner = new UpdateBanner { DataContext = shell };
            using var host = new OffscreenHost(InWindowBannerRegion(banner), windowWidth, 140);

            Assert.Equal(windowWidth - 48, banner.ActualWidth); // the region's 24 px gutters
            Assert.InRange(banner.ActualHeight, 40, 80);
            foreach (var button in Buttons(banner))
            {
                var right = button.TranslatePoint(new Point(button.ActualWidth, 0), banner).X;
                Assert.True(right <= banner.ActualWidth, AutomationProperties.GetName(button) + " is clipped");
            }

            RenderTo.Png(host, $"update-banner-width-{windowWidth}");
            shell.Dispose();
        });
    }

    [Fact]
    public void Release_notes_is_disabled_when_the_release_has_no_link_that_passed_the_check()
    {
        _answer = _answer with { HtmlUrl = "https://evil.example/cisco-ai-defense/defenseclaw/releases/tag/v0.8.11" };
        var services = NewServices();
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Default, AppearanceMode.Dark));

        UiThread.Run(() =>
        {
            var shell = new MainWindowViewModel(services, reviewUpdate: () => { }, openReleasePage: _ => { });
            var banner = new UpdateBanner { DataContext = shell };
            using var host = new OffscreenHost(InWindowBannerRegion(banner), 1000, 140);

            var byName = Buttons(banner).ToDictionary(b => AutomationProperties.GetName(b));
            Assert.False(byName["Release notes"].IsEnabled);
            Assert.True(byName["Review update"].IsEnabled);
            Assert.True(byName["Dismiss update notice"].IsEnabled);
            shell.Dispose();
        });
    }

    [Fact]
    public void With_nothing_to_say_the_banner_takes_no_room()
    {
        _answer = _answer with { State = UpdateCheckState.UpToDate, LatestVersion = "v0.8.10" };
        var services = NewServices();
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Linear, AppearanceMode.Dark));

        UiThread.Run(() =>
        {
            var shell = new MainWindowViewModel(services, reviewUpdate: () => { }, openReleasePage: _ => { });
            var banner = new UpdateBanner { DataContext = shell };
            using var host = new OffscreenHost(InWindowBannerRegion(banner), 1000, 140);

            Assert.Equal(0, banner.ActualHeight);
            Assert.All(Buttons(banner), button => Assert.False(button.IsVisible, AutomationProperties.GetName(button)));
            shell.Dispose();
        });
    }

    [Fact]
    public void Clicking_dismiss_closes_the_bar_at_once()
    {
        var services = NewServices();
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Cisco, AppearanceMode.Dark));

        UiThread.Run(() =>
        {
            var shell = new MainWindowViewModel(services, reviewUpdate: () => { }, openReleasePage: _ => { });
            var banner = new UpdateBanner { DataContext = shell };
            using var host = new OffscreenHost(InWindowBannerRegion(banner), 1000, 140);
            Assert.True(banner.ActualHeight > 0);

            var dismiss = Buttons(banner).Single(b => AutomationProperties.GetName(b) == "Dismiss update notice");
            dismiss.Command.Execute(dismiss.CommandParameter);
            host.Relayout();

            Assert.False(shell.ShowUpdateBanner);
            Assert.Equal(0, banner.ActualHeight);
            shell.Dispose();
        });
    }

    [Fact]
    public void A_live_style_switch_restyles_the_open_banner()
    {
        var services = NewServices();
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Default, AppearanceMode.Dark));

        UiThread.Run(() =>
        {
            var shell = new MainWindowViewModel(services, reviewUpdate: () => { }, openReleasePage: _ => { });
            var banner = new UpdateBanner { DataContext = shell };

            // A real window: the application refreshes dynamic resources in its windows, not in a bare host.
            var window = new Window { Content = InWindowBannerRegion(banner), Width = 1000, Height = 140 };
            try
            {
                var bar = (Border)banner.FindName("Bar");
                var before = ((SolidColorBrush)bar.Background).Color;

                fixture.Service.SetStyle(AppearanceStyle.Tui);
                fixture.Service.SetMode(AppearanceMode.Light);

                var after = ((SolidColorBrush)bar.Background).Color;
                Assert.Equal(AppearanceFixture.ColorOf(AppearanceTokens.Surface), after);
                Assert.NotEqual(before, after);
            }
            finally
            {
                window.Close();
                shell.Dispose();
            }
        });
    }

    [Fact]
    public void The_banners_markup_carries_no_colour_font_or_radius_of_its_own()
    {
        var markup = File.ReadAllText(MarkupPath());

        Assert.DoesNotMatch(@"#[0-9A-Fa-f]{3,8}\b", markup);
        Assert.DoesNotMatch(@"(Background|Foreground|BorderBrush|Fill)=""[A-Za-z]", markup); // a named colour, not a {DynamicResource ...}
        Assert.DoesNotMatch(@"(FontFamily|CornerRadius)=""[^{]", markup);
    }

    private static string MarkupPath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "DefenseClaw.App", "Views", "Shell", "UpdateBanner.xaml");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("UpdateBanner.xaml was not found above the test binaries.");
    }

    // ------------------------------------------------------------------ contrast (WCAG 2.x relative luminance)

    /// <summary>The colour <paramref name="top"/> (with its alpha) makes over an opaque <paramref name="under"/>.</summary>
    private static Color Composite(Color top, Color under)
    {
        var a = top.A / 255.0;
        byte Mix(byte t, byte u) => (byte)Math.Round((t * a) + (u * (1 - a)));
        return Color.FromRgb(Mix(top.R, under.R), Mix(top.G, under.G), Mix(top.B, under.B));
    }

    private static double Luminance(Color c)
    {
        static double Channel(byte value)
        {
            var s = value / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));
    }

    private static double Ratio(Color a, Color b)
    {
        var (hi, lo) = (Math.Max(Luminance(a), Luminance(b)), Math.Min(Luminance(a), Luminance(b)));
        return (hi + 0.05) / (lo + 0.05);
    }
}
