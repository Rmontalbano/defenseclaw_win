using System.Net;
using System.Windows;
using System.Windows.Controls;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Controls;
using DefenseClaw.App.Views.Panels;

namespace DefenseClaw.App.Tests.Agents;

/// <summary>
/// What the operator sees on the Agents panel (CUST-355), hosted offscreen on the shared UI thread over a view-model whose gateway is an in-memory
/// handler: the tree's badges stay on screen whatever the edge says, the figures the ledger does not carry say "Not reported", the page has no button
/// that changes anything, and a gateway without the routes gets an explanation instead of an empty list. A PNG is written when <c>DC_RENDER_DIR</c> names a folder.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class AgentsPanelViewTests : IDisposable
{
    private readonly AgentsScene _scene = new();

    public void Dispose() => _scene.Dispose();

    private static IEnumerable<string> VisibleTexts(DependencyObject root) =>
        VisualTree.Descendants<FrameworkElement>(root)
            .Where(e => e.IsVisible)
            .Select(e => e switch
            {
                TextBlock block => block.Text.Length > 0 ? block.Text : string.Concat(block.Inlines.OfType<System.Windows.Documents.Run>().Select(r => r.Text)),
                TextBox box => box.Text,
                ContentControl { Content: string content } => content,
                _ => string.Empty,
            })
            .Where(t => t.Length > 0)
            .ToArray();

    private (AgentsPanel View, OffscreenHost Host) Host(AgentsPanelViewModel vm, double width = 1280, double height = 1500) =>
        UiThread.Run(() =>
        {
            var panel = new AgentsPanel { DataContext = vm };
            return (panel, new OffscreenHost(panel, width, height));
        });

    [Fact]
    public async Task An_open_agent_shows_its_tree_with_every_badge_the_counts_and_the_timeline_and_no_button_that_changes_anything()
    {
        var vm = await _scene.OpenAsync();
        _scene.Select("agt-0000000000000001");
        var (view, host) = Host(vm);

        UiThread.Run(() =>
        {
            using (host)
            {
                host.Relayout();
                var toolbar = Assert.Single(VisualTree.Descendants<DcPageToolbar>(view));
                Assert.Equal("Agents", toolbar.Title);
                Assert.Equal(vm.CaptionText, toolbar.Caption);
                Assert.True(toolbar.HasSearch);

                var texts = VisibleTexts(view).ToArray();

                // The directory, per connector.
                Assert.Contains("codex", texts);
                Assert.Contains("claudecode", texts);

                // The tree: every edge wears its method and its status, settled or not.
                foreach (var badge in new[] { "reported", "active", "inferred", "unresolved", "derived", "conflicted", "telepathy", "newstatus", "trace exact" })
                {
                    Assert.Contains(badge, texts);
                }

                // The counts: a figure the ledger does not carry says so, three times, and nothing is a number it made up.
                Assert.Equal(3, texts.Count(t => t == "Not reported"));
                Assert.Contains("Enforced blocks", texts);
                Assert.Contains("Observe-mode would-blocks", texts);
                Assert.Contains("Tokens", texts);
                Assert.Contains("Cost", texts);

                // The timeline, with a header per turn.
                Assert.Contains("Timeline", texts);
                Assert.Contains("Turn turn-0001 · 8 entries", texts);

                Assert.Contains(texts, t => t.StartsWith("Conflicts: 1 relationship is conflicted", StringComparison.Ordinal));

                // Read-only: the only buttons are refresh, open-by-id and load more.
                var buttons = VisualTree.Descendants<Wpf.Ui.Controls.Button>(view)
                    .Select(b => System.Windows.Automation.AutomationProperties.GetName(b))
                    .Where(n => n.Length > 0)
                    .Distinct()
                    .OrderBy(n => n, StringComparer.Ordinal)
                    .ToArray();
                Assert.Equal(["Load more events", "Open agent by id", "Refresh"], buttons);

                RenderTo.Png(host, "cust355-agents-open");
            }
        });
    }

    [Fact]
    public async Task The_tree_and_the_timeline_and_the_directory_rows_are_named_for_a_screen_reader()
    {
        var vm = await _scene.OpenAsync();
        _scene.Select("agt-0000000000000001");
        var (view, host) = Host(vm);

        UiThread.Run(() =>
        {
            using (host)
            {
                host.Relayout();
                var names = VisualTree.Descendants<FrameworkElement>(view).Select(System.Windows.Automation.AutomationProperties.GetName).Where(n => n.Length > 0).ToArray();

                Assert.Contains("Agents, grouped by connector", names);
                Assert.Contains("Agent tree", names);
                Assert.Contains("Timeline entries, in time order", names);
                Assert.Contains(names, n => n.StartsWith("codex agent agt-0000000000000001,", StringComparison.Ordinal));
                Assert.Contains(names, n => n.Contains("inferred, unresolved, confidence 40", StringComparison.Ordinal));
                Assert.Contains(names, n => n.StartsWith("Enforced blocks: 1.", StringComparison.Ordinal));
            }
        });
    }

    [Fact]
    public async Task A_gateway_without_the_routes_shows_the_explanation_and_not_an_empty_list()
    {
        _scene.Handler.Override = _ => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("404 page not found") };
        var vm = await _scene.OpenAsync();
        var (view, host) = Host(vm, height: 900);

        UiThread.Run(() =>
        {
            using (host)
            {
                host.Relayout();
                var texts = VisibleTexts(view).ToArray();

                Assert.Contains(AgentsPanelViewModel.UnsupportedTitle, texts);
                Assert.Contains(AgentsPanelViewModel.UnsupportedDetail, texts);
                Assert.DoesNotContain("Timeline", texts);
                Assert.DoesNotContain(VisualTree.Descendants<ListBox>(view), l => l.Name == "AgentList" && l.IsVisible);

                RenderTo.Png(host, "cust355-agents-unsupported");
            }
        });
    }

    [Fact]
    public async Task A_stale_list_wears_its_banner()
    {
        var vm = await _scene.OpenAsync();
        _scene.Handler.Override = _ => throw new HttpRequestException("refused");
        UiThread.Run(() => vm.RefreshCommand.Execute(null));
        UiThread.WaitFor(() => vm.IsStale, "the stale marker");
        var (view, host) = Host(vm, height: 900);

        UiThread.Run(() =>
        {
            using (host)
            {
                host.Relayout();
                var bar = Assert.Single(VisualTree.Descendants<Wpf.Ui.Controls.InfoBar>(view), b => b.Title == "Stale");

                Assert.True(bar.IsOpen);
                Assert.StartsWith("The agents below are from the last successful read.", bar.Message, StringComparison.Ordinal);
                Assert.Contains("codex", VisibleTexts(view).ToArray());
            }
        });
    }

    [Theory]
    [InlineData("Default", "Dark")]
    [InlineData("Default", "Light")]
    [InlineData("Linear", "Dark")]
    [InlineData("Linear", "Light")]
    [InlineData("Tui", "Dark")]
    [InlineData("Tui", "Light")]
    [InlineData("Cisco", "Dark")]
    [InlineData("Cisco", "Light")]
    public async Task The_selected_directory_row_reads_in_the_on_accent_colour_on_every_line_in_every_style_and_mode(string style, string mode)
    {
        using var fixture = new Appearance.AppearanceFixture(
            new Services.Appearance.AppearanceSettings(Enum.Parse<Services.Appearance.AppearanceStyle>(style), Enum.Parse<Services.Appearance.AppearanceMode>(mode)));
        var vm = await _scene.OpenAsync();
        _scene.Select("agt-0000000000000001");
        var (view, host) = Host(vm);

        UiThread.Run(() =>
        {
            using (host)
            {
                host.Relayout();
                var list = Assert.Single(VisualTree.Descendants<ListBox>(view), l => l.Name == "AgentList");
                var selected = VisualTree.Descendants<ListBoxItem>(list).Single(i => i.IsSelected);
                var onAccent = Appearance.AppearanceFixture.ColorOf("DcOnAccentBrush");
                var accent = Appearance.AppearanceFixture.ColorOf("DcAccentBrush");

                // The id and both secondary lines (last seen, sessions and user): none is left grey.
                var lines = VisualTree.Descendants<TextBlock>(selected).Where(t => t.Text.Length > 0).ToArray();
                Assert.True(lines.Length >= 3, "the row shows its id and two secondary lines");
                foreach (var line in lines)
                {
                    var colour = ((System.Windows.Media.SolidColorBrush)line.Foreground).Color;
                    Assert.True(onAccent == colour, $"{style} {mode}: \"{line.Text}\" is {colour}, not the on-accent {onAccent}");
                }

                // And that colour reads on the accent the row is filled with.
                Assert.True(Appearance.AppearanceContrastTests.Ratio(onAccent, accent) >= 4.5, $"{style} {mode}: on-accent on accent");

                // A row that is not selected keeps the secondary text colour.
                var other = VisualTree.Descendants<ListBoxItem>(list).FirstOrDefault(i => !i.IsSelected);
                if (other is not null)
                {
                    var caption = VisualTree.Descendants<TextBlock>(other).First(t => t.Text.StartsWith("last seen", StringComparison.Ordinal));
                    Assert.Equal(Appearance.AppearanceFixture.ColorOf("DcTextSecondaryBrush"), ((System.Windows.Media.SolidColorBrush)caption.Foreground).Color);
                }

                RenderTo.Png(host, $"cust395-agents-selected-{style.ToLowerInvariant()}-{mode.ToLowerInvariant()}");
            }
        });
    }
}

