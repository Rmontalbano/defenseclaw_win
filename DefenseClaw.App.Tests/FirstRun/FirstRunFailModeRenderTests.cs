using System.Windows;
using System.Windows.Controls;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Appearance;
using DefenseClaw.App.Services.FirstRun;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.Appearance;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels.FirstRun;
using DefenseClaw.App.Views.Controls;
using DefenseClaw.App.Views.FirstRun;
using DefenseClaw.Core.Install;
using TextBlock = System.Windows.Controls.TextBlock;

namespace DefenseClaw.App.Tests.FirstRun;

/// <summary>
/// The first-run "Policy and enforcement" card per runtime (CUST-390), as the real window's content offscreen at the app's two review sizes in
/// Cisco Light and Default Dark: 1.0.x with the Observe profile (no fail-mode box), 1.0.x with Action (the box next to human approval, with the
/// TUI's hint), 0.8.10 (the box in both profiles, as before) and a per-user 1.0.4 (the outage switch, unchanged). A PNG of each is written when
/// <c>DC_RENDER_DIR</c> names a folder, and never otherwise. The window is never shown and nothing runs.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class FirstRunFailModeRenderTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    public static IEnumerable<object[]> Cases()
    {
        foreach (var (width, height) in new[] { (980, 692), (1468, 875) })
        {
            foreach (var (style, mode) in new[] { ("Cisco", "Light"), ("Default", "Dark") })
            {
                foreach (var state in new[] { "v103-observe", "v103-action", "v0810-observe", "v104-per-user-action" })
                {
                    yield return new object[] { state, style, mode, width, height };
                }
            }
        }
    }

    /// <summary>The nearest ancestor of <paramref name="element"/> of type <typeparamref name="T"/>: the card a header sits in.</summary>
    private static T VisualTreeAncestor<T>(DependencyObject element)
        where T : DependencyObject
    {
        for (var parent = System.Windows.Media.VisualTreeHelper.GetParent(element); parent is not null; parent = System.Windows.Media.VisualTreeHelper.GetParent(parent))
        {
            if (parent is T found)
            {
                return found;
            }
        }

        throw new InvalidOperationException($"no {typeof(T).Name} above the element");
    }

    private static GatewaySnapshot Installed() => new()
    {
        State = AppGatewayState.NotInitialized,
        Install = InstallState.InstalledNotInitialized,
        CliPath = @"C:\synthetic\defenseclaw.exe",
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task The_policy_card_shows_the_fail_mode_the_runtime_asks_and_nothing_is_clipped(string state, string style, string mode, int width, int height)
    {
        var runner = state switch
        {
            "v0810-observe" => RuntimeFixtureRunner.For("0.8.10"),
            "v104-per-user-action" => FirstRunFailModeTests.Versioned("1.0.4"),
            _ => FirstRunFailModeTests.Versioned("1.0.3"),
        };
        using var services = TestServices.Create(_temp, runtimeProbeRunner: runner);
        _ = await services.Runtime.RefreshAsync();

        using var fixture = new AppearanceFixture(new AppearanceSettings(Enum.Parse<AppearanceStyle>(style), Enum.Parse<AppearanceMode>(mode)));
        FirstRunViewModel? vm = null;
        OffscreenHost? host = null;
        try
        {
            UiThread.Run(() =>
            {
                vm = new FirstRunViewModel(services, _ => Task.FromResult<IReadOnlyList<WizardDefinition>>(Array.Empty<WizardDefinition>()), Installed());
                vm.Profile = state.EndsWith("-action", StringComparison.Ordinal) ? "action" : "observe";

                var window = new FirstRunWindow(vm);
                var content = (Grid)window.Content;
                window.Content = null;
                content.DataContext = vm;
                var bar = content.Children.OfType<Wpf.Ui.Controls.TitleBar>().Single();
                content.Children.Remove(bar);
                content.RowDefinitions[0].Height = new GridLength(32);
                host = new OffscreenHost(content, width, height);

                // The card is below the fold at the smaller size: scroll it to the top of the page.
                var scroll = VisualTree.Descendants<ScrollViewer>(content).First(s => s.Name == "PageScroll");
                var header = VisualTree.Descendants<DcCardHeader>(content).Single(h => Equals(h.Content, "Policy and enforcement"));
                scroll.ScrollToVerticalOffset(scroll.VerticalOffset + header.TranslatePoint(new Point(0, 0), scroll).Y - 12);
                host.Relayout();

                RenderTo.Png(host, $"firstrun-failmode-{state}-{style}-{mode}-{width}x{height}");

                var box = VisualTree.Descendants<ComboBox>(content).Single(c => System.Windows.Automation.AutomationProperties.GetName(c) == "Hook failure mode");
                var hint = VisualTree.Descendants<TextBlock>(content).SingleOrDefault(t => t.IsVisible && t.Text == FirstRunFailMode.ActionOnlyHint);
                var outage = VisualTree.Descendants<Wpf.Ui.Controls.ToggleSwitch>(content)
                    .Single(t => System.Windows.Automation.AutomationProperties.GetName(t) == "Block calls during a gateway outage");

                switch (state)
                {
                    case "v103-observe":
                        Assert.False(box.IsVisible);
                        Assert.Null(hint);
                        break;
                    case "v103-action":
                        Assert.True(box.IsVisible);
                        Assert.Equal("closed", box.SelectedValue);
                        Assert.NotNull(hint);
                        var card = VisualTreeAncestor<Border>(header);
                        var corner = hint!.TranslatePoint(new Point(hint.ActualWidth, hint.ActualHeight), card);
                        Assert.True(corner.X <= card.ActualWidth + 0.5 && corner.Y <= card.ActualHeight + 0.5, $"the hint fits the card ({corner} in {card.ActualWidth}x{card.ActualHeight})");
                        Assert.True(hint.ActualHeight + hint.Margin.Top + hint.Margin.Bottom + 0.5 >= hint.DesiredSize.Height, "the hint is not cut off");
                        break;
                    case "v0810-observe":
                        Assert.True(box.IsVisible);
                        Assert.Equal("open", box.SelectedValue);
                        Assert.Null(hint);
                        break;
                    default:
                        Assert.False(box.IsVisible);
                        Assert.True(outage.IsVisible);
                        break;
                }

                Assert.Equal(state == "v104-per-user-action", outage.IsVisible);
                Assert.True(scroll.ExtentWidth <= scroll.ViewportWidth + 1, "the page needs no horizontal scrolling");
            });
        }
        finally
        {
            UiThread.Run(() =>
            {
                host?.Dispose();
                vm?.Dispose();
            });
        }
    }
}
