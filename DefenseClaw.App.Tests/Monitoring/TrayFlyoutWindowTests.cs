using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Appearance;
using DefenseClaw.App.Tests.Appearance;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views;

namespace DefenseClaw.App.Tests.Monitoring;

/// <summary>
/// The flyout as a window (CUST-204): 360 wide, its rows in the Mac's order, every row a named button the keyboard can reach, and nothing spilling out
/// of the card in any of the four looks in either mode (TUI's monospace is the widest). The views are built on the shared UI thread and laid
/// out offscreen, exactly as the panels' layout tests are.
/// </summary>
[Collection(UiCollection.Name)]
public class TrayFlyoutWindowTests
{
    /// <summary>Builds the flyout over synthetic data, shows the numbers, and hands the card (the window's content, detached) to <paramref name="check"/>.</summary>
    private static void WithCard(string style, string mode, Action<TrayFlyoutWindow, FrameworkElement, TrayFlyoutViewModel> check)
    {
        using var scene = new FlyoutScene();
        scene.Populate();
        using var fixture = new AppearanceFixture(new AppearanceSettings(Enum.Parse<AppearanceStyle>(style), Enum.Parse<AppearanceMode>(mode)));

        var viewModel = UiThread.Run(() =>
        {
            var created = new TrayFlyoutViewModel(scene.Services, () => { }, () => { }, metricsReader: null, timeProvider: new ManualClock());
            created.SetVisible(true);
            return created;
        });

        try
        {
            UiThread.WaitFor(() => viewModel.Metrics[0].Value != "—" && viewModel.Metrics[2].Value != "—" && viewModel.RecentFindings.Count > 0, "the flyout's numbers");

            UiThread.Run(() =>
            {
                viewModel.Apply(FlyoutScene.RunningSnapshot());

                var window = new TrayFlyoutWindow { DataContext = viewModel };
                var card = (FrameworkElement)window.Content;

                // Detached, so it can be laid out without showing a transparent window; it keeps the font the style gave the window.
                window.Content = null;
                card.SetValue(TextElement.FontFamilyProperty, window.FontFamily);
                card.DataContext = viewModel;

                try
                {
                    check(window, card, viewModel);
                }
                finally
                {
                    window.Close();
                }
            });
        }
        finally
        {
            UiThread.Run(viewModel.Dispose);
        }
    }

    private static Rect BoundsIn(FrameworkElement element, FrameworkElement ancestor) =>
        element.TransformToAncestor(ancestor).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));

    private static string NameOf(Button button) =>
        AutomationProperties.GetName(button) is { Length: > 0 } name ? name : button.Content as string ?? string.Empty;

    // ------------------------------------------------------------------ size and fit

    [Theory]
    [InlineData("Default", "Dark")]
    [InlineData("Default", "Light")]
    [InlineData("Linear", "Dark")]
    [InlineData("Linear", "Light")]
    [InlineData("Tui", "Dark")]
    [InlineData("Tui", "Light")]
    [InlineData("Cisco", "Dark")]
    [InlineData("Cisco", "Light")]
    public void The_flyout_is_360_wide_and_nothing_spills_out_of_its_card_in_any_look(string style, string mode)
    {
        WithCard(style, mode, (window, card, _) =>
        {
            Assert.Equal(360, window.Width);

            using var host = new OffscreenHost(card, 360, 900);

            // The card sits in the window's 10 px shadow margin, so it is 340 wide; its padding and hairline leave the inner edge.
            Assert.Equal(340, card.ActualWidth, precision: 1);
            var border = (Border)card;
            var innerLeft = border.Padding.Left + border.BorderThickness.Left;
            var innerRight = card.ActualWidth - border.Padding.Right - border.BorderThickness.Right;

            var buttons = VisualTree.Descendants<Button>(card).ToList();
            Assert.NotEmpty(buttons);
            foreach (var button in buttons)
            {
                var bounds = BoundsIn(button, card);

                // A row's hover plate reaches 6 px into the card's padding so its text stays on the edge (the plate's own margin); a footer button does not.
                var reach = button is Wpf.Ui.Controls.Button ? 0.5 : 6.5;
                Assert.True(bounds.Right <= innerRight + reach, $"{style} {mode}: '{NameOf(button)}' ends at {bounds.Right:0.0}, past {innerRight:0.0}.");
                Assert.True(bounds.Left >= innerLeft - reach, $"{style} {mode}: '{NameOf(button)}' starts at {bounds.Left:0.0}, before {innerLeft:0.0}.");
            }

            Assert.True(card.ActualHeight > 300 && card.ActualHeight < 600, $"{style} {mode}: the card is {card.ActualHeight:0} tall.");
        });
    }

    [Fact]
    public void The_bars_are_as_long_as_the_macs_easings_make_them()
    {
        WithCard("Default", "Dark", (_, card, _) =>
        {
            using var host = new OffscreenHost(card, 360, 900);

            double Ratio(string tone)
            {
                var fill = VisualTree.Descendants<Border>(card).Single(b => Equals(b.Tag, tone));
                var track = (Grid)fill.Parent;
                return fill.ActualWidth / track.ActualWidth;
            }

            // 46 hook calls, 4 blocks over 46 hook calls, 12 findings: n / (n + 250), eight times the rate, n / (n + 10).
            Assert.Equal(46.0 / (46 + 250), Ratio("Accent"), precision: 2);
            Assert.Equal(4.0 / 46 * 8, Ratio("Bad"), precision: 2);
            Assert.Equal(12.0 / (12 + 10), Ratio("Warn"), precision: 2);

            // Four pixels thin.
            Assert.All(
                VisualTree.Descendants<Border>(card).Where(b => b.Tag is "Accent" or "Bad" or "Warn"),
                b => Assert.Equal(4, b.ActualHeight, precision: 1));
        });
    }

    // ------------------------------------------------------------------ order, names, keyboard

    [Fact]
    public void Every_row_is_a_named_button_the_keyboard_reaches_in_the_macs_order()
    {
        WithCard("Default", "Dark", (_, card, _) =>
        {
            using var host = new OffscreenHost(card, 360, 900);

            var buttons = VisualTree.Descendants<Button>(card).ToList();
            var names = buttons.Select(NameOf).ToList();

            Assert.Equal(
                new[]
                {
                    "Hook Calls 46, latest 500 audit events. Opens Logs.",
                    "Blocks 4, latest 500 decisions · 9% block rate. Opens Audit.",
                    "Findings 12, unacknowledged. Opens Alerts.",
                    "Review acknowledge",
                },
                names.Take(4));

            // Then the five newest findings, each one named for what it is and where it goes.
            Assert.All(names.Skip(4).Take(5), n => Assert.EndsWith("Opens Alerts.", n, StringComparison.Ordinal));

            // Then the footer: Open Dashboard, the gear, Pause, Exit.
            Assert.Equal(new[] { "Open Dashboard", "Settings", "Pause monitoring", "Exit" }, names.Skip(9));

            Assert.All(buttons, b =>
            {
                Assert.True(b.IsTabStop, $"'{NameOf(b)}' is not a tab stop.");
                Assert.True(b.Focusable, $"'{NameOf(b)}' is not focusable.");
                Assert.True(b.IsEnabled, $"'{NameOf(b)}' is disabled.");
                Assert.NotEmpty(NameOf(b));
            });

            // The keyboard ring is the design system's, not the platform's dotted line.
            Assert.All(buttons.Where(b => b is not Wpf.Ui.Controls.Button), b => Assert.NotNull(b.FocusVisualStyle));
        });
    }

    [Fact]
    public void The_window_carries_its_name_and_the_icons_are_decorative()
    {
        WithCard("Default", "Dark", (window, card, _) =>
        {
            using var host = new OffscreenHost(card, 360, 900);

            Assert.Equal("DefenseClaw status", AutomationProperties.GetName(window));
            Assert.DoesNotContain(VisualTree.Descendants<Wpf.Ui.Controls.SymbolIcon>(card), i => i.GetType() != typeof(DefenseClaw.App.Views.Controls.DcSymbolIcon));
        });
    }

    [Fact]
    public void Pause_becomes_resume_in_place_with_its_own_name()
    {
        WithCard("Default", "Dark", (_, card, viewModel) =>
        {
            using var host = new OffscreenHost(card, 360, 900);

            Button PauseButton() => VisualTree.Descendants<Button>(card).Single(b => NameOf(b).EndsWith("monitoring", StringComparison.Ordinal));
            Assert.Equal("Pause monitoring", NameOf(PauseButton()));
            Assert.Contains(VisualTree.Descendants<TextBlock>(PauseButton()), t => t.Text == "Pause");

            viewModel.TogglePauseCommand.Execute(null);
            host.Relayout();

            Assert.Equal("Resume monitoring", NameOf(PauseButton()));
            Assert.Contains(VisualTree.Descendants<TextBlock>(PauseButton()), t => t.Text == "Resume");

            // The header says it too.
            Assert.Contains(VisualTree.Descendants<TextBlock>(card), t => t.Text == "Monitoring paused");
            Assert.Contains(VisualTree.Descendants<TextBlock>(card), t => t.Text == "Paused");

            viewModel.TogglePauseCommand.Execute(null);
        });
    }

    // ------------------------------------------------------------------ closing

    [Fact]
    public void A_row_that_opens_the_dashboard_hides_the_flyout()
    {
        using var scene = new FlyoutScene();
        scene.Populate(hookCalls: 2, blocks: 0, other: 2, findings: 2);

        UiThread.Run(() =>
        {
            using var viewModel = new TrayFlyoutViewModel(scene.Services, () => { }, () => { }, metricsReader: null, timeProvider: new ManualClock());
            var window = new TrayFlyoutWindow
            {
                DataContext = viewModel,
                ShowActivated = false,
                Left = -32000,
                Top = -32000,
            };

            try
            {
                window.Show();
                Assert.True(window.IsVisible);
                Assert.True(viewModel.IsTrackingPolls);

                viewModel.Metrics[2].OpenCommand.Execute(null);

                Assert.False(window.IsVisible);
                Assert.False(viewModel.IsTrackingPolls);
                Assert.False(viewModel.IsLive);
                Assert.Equal("alerts", scene.Services.Navigation.Pending?.PanelId);

                // And a window that is given another view-model stops listening to the old one.
                window.Show();
                viewModel.ReviewAcknowledgeCommand.Execute(null);
                Assert.False(window.IsVisible);
            }
            finally
            {
                window.ForceClose();
            }
        });
    }

    // ------------------------------------------------------------------ theme tokens only

    [Fact]
    public void The_flyout_xaml_hard_codes_no_colour_font_or_radius()
    {
        var path = Path.Combine(AppDirectory(), "Views", "TrayFlyoutWindow.xaml");
        var text = File.ReadAllText(path);

        // A token is a {DynamicResource Dc...}; a literal colour, font family or corner radius is a look the four styles cannot change.
        Assert.DoesNotMatch(new Regex(@"(?<!&)#[0-9A-Fa-f]{3,8}\b"), text);
        Assert.DoesNotMatch(new Regex(@"\bFontFamily="), text);
        Assert.DoesNotMatch(new Regex(@"\bCornerRadius=""\d"), text);
        Assert.DoesNotMatch(new Regex(@"\b(Foreground|Background|BorderBrush|Fill|Stroke)=""(?!\{|Transparent"")"), text);
        Assert.DoesNotMatch(new Regex(@"\bEffect=""(?!\{)"), text);

        // Tokens are consumed live (DynamicResource); only a style may be a static reference.
        Assert.DoesNotMatch(new Regex(@"StaticResource Dc\w*(Brush|Radius|Shadow|FontFamily|FontSize|Thickness)"), text);
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
}
