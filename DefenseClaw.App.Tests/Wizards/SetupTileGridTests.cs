using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Panels;
using Wpf.Ui.Controls;

namespace DefenseClaw.App.Tests.Wizards;

/// <summary>The Setup hub's areas are an adaptive grid of tiles 92 tall and at least 233 wide that share the row out, each a Fluent glyph, a title and a two-line blurb (CUST-225; the Mac's 23 areas since CUST-385).</summary>
[Collection(UiCollection.Name)]
public class SetupTileGridTests
{
    private static WizardDefinition Def(string target, string group, PlatformStatus status = PlatformStatus.NotApplicable, string description = "") => new()
    {
        Target = target,
        Title = target,
        Group = group,
        PlatformStatus = status,
        Description = description,
    };

    private static readonly WizardDefinition[] Sample =
    {
        Def("claude-code", WizardGroups.Connectors, PlatformStatus.Certified, "Configure DefenseClaw hooks for Claude Code."),
        Def("codex", WizardGroups.Connectors, PlatformStatus.Certified, "Configure DefenseClaw hooks for Codex."),
        Def("cursor", WizardGroups.Connectors, PlatformStatus.NotCertified, "Configure DefenseClaw hooks for Cursor: hooks fire but the connector has not completed certification, so read the review."),
        Def("guardrail", WizardGroups.GuardrailAndPolicy, PlatformStatus.NotApplicable, "Configure the guardrail."),
        Def("llm", WizardGroups.Credentials, PlatformStatus.NotApplicable, "Configure LLM providers."),
        Def("gateway", WizardGroups.Credentials, PlatformStatus.NotApplicable, "Configure the gateway."),
        Def("sandbox", WizardGroups.Other, PlatformStatus.NotApplicable, "Configure the sandbox."),
        Def("registry", WizardGroups.GuardrailAndPolicy, PlatformStatus.NotApplicable, "Configure registries."),
        Def("webhook", WizardGroups.Observability, PlatformStatus.NotApplicable, "Add a webhook."),
        Def("skill-scanner", WizardGroups.Scanners, PlatformStatus.NotApplicable, "Configure the skill scanner."),
    };

    [Theory]
    [InlineData("llm", WizardGroups.Credentials, SymbolRegular.BrainCircuit20)]
    [InlineData("gateway", WizardGroups.Credentials, SymbolRegular.Globe24)]
    [InlineData("guardrail", WizardGroups.GuardrailAndPolicy, SymbolRegular.ShieldCheckmark24)]
    [InlineData("sandbox", WizardGroups.Other, SymbolRegular.Cube24)]
    [InlineData("registry", WizardGroups.GuardrailAndPolicy, SymbolRegular.Library24)]
    [InlineData("claude-code", WizardGroups.Connectors, SymbolRegular.PlugConnected24)] // a connector falls back to its group's plug
    [InlineData("a-future-target", WizardGroups.Other, SymbolRegular.Settings24)]
    public void Each_wizard_wears_the_glyph_the_mac_gives_it(string target, string group, SymbolRegular expected) =>
        Assert.Equal(expected, WizardTileIcons.For(target, group));

    [Fact]
    public void An_unavailable_tile_says_why_instead_of_its_blurb_and_is_not_badged()
    {
        var card = new WizardCardViewModel(Def("cursor", WizardGroups.Connectors, PlatformStatus.Unsupported, "Configure Cursor."));
        var open = new WizardCardViewModel(Def("claude-code", WizardGroups.Connectors, PlatformStatus.Certified, "Configure Claude Code."));

        Assert.Equal("Configure Claude Code.", open.TileBlurb);
        Assert.True(open.ShowTileBadge);
        Assert.Contains(open.Badge, open.TileToolTip, StringComparison.Ordinal);

        if (card.IsAvailable)
        {
            // Unsupported connectors are policy-gated by name; whichever way the policy goes, the blurb and the reason agree.
            Assert.Equal("Configure Cursor.", card.TileBlurb);
            return;
        }

        Assert.Equal(card.UnavailableReason, card.TileBlurb);
        Assert.False(card.ShowTileBadge);
        Assert.Contains(card.UnavailableReason, card.TileToolTip, StringComparison.Ordinal);
    }

    [Fact]
    public void The_grid_is_92_tall_tiles_at_least_233_wide_that_open_the_wizard_and_the_guardrail_tile_waits_for_its_wiring()
    {
        // CUST-385: the grid is the Mac's 23 setup areas (it was one tile per catalog card, grouped); the cards are what the tiles open.
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp);
        var shell = UiThread.Run(() =>
        {
            var s = new PanelShell(services, 1500, 900);
            _ = s.Show<SetupPanel>();
            return s;
        });
        try
        {
            var vm = UiThread.Run(() => (SetupPanelViewModel)shell.ViewModel);
            UiThread.WaitFor(() => !vm.IsLoading, "setup catalog read finished");

            UiThread.Run(() =>
            {
                vm.ShowCards(Sample);
                shell.Host.Relayout();

                var tiles = Tiles(shell);
                Assert.Equal(SetupAreas.All.Count, tiles.Length);
                Assert.All(tiles, t =>
                {
                    Assert.True(t.ActualWidth >= 233 - 0.5, "a tile is never narrower than the Mac's 233");
                    Assert.Equal(92, t.ActualHeight, 0.5);
                    Assert.Equal(12, t.Margin.Right);
                    Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(t)));
                    Assert.NotNull(t.Command);
                });
                Assert.Contains(tiles, t => AutomationProperties.GetName(t) == "Configure LLM");
                Assert.Contains(tiles, t => AutomationProperties.GetName(t) == "Open Connector Setup");

                // Several tiles share a row on a wide page: it is a grid, not a list.
                var rows = tiles.Select(t => Math.Round(t.TranslatePoint(new Point(), shell.Page!).Y)).Distinct().Count();
                Assert.True(rows < tiles.Length, "tiles wrap into rows");

                // Keyboard: every tile is a tab stop.
                Assert.All(tiles, t => Assert.True(t.Focusable && t.IsTabStop));

                // The catalog wires the guardrail-controls tile to its window (CUST-222); without somewhere to go it is absent.
                Assert.Single(ControlsTile(shell));
                vm.OpenGuardrailControls = null;
                shell.Host.Relayout();
                Assert.Empty(ControlsTile(shell));
                var opened = 0;
                vm.OpenGuardrailControls = () => opened++;
                shell.Host.Relayout();
                var controls = ControlsTile(shell);
                Assert.Single(controls);
                controls[0].Command.Execute(null);
                Assert.Equal(1, opened);

                VisualTree.Descendants<ScrollViewer>(shell.Page!).FirstOrDefault(sv => sv.ScrollableHeight > 0)?.ScrollToEnd();
                shell.Host.Relayout();
                RenderTo.Png(shell.Host, "setup-tiles-1500x900");
            });
        }
        finally
        {
            UiThread.Run(shell.Dispose);
        }
    }

    [Fact]
    public void Search_filters_the_tiles_and_a_narrow_page_wraps_them()
    {
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp);
        var shell = UiThread.Run(() =>
        {
            var s = new PanelShell(services, 720, 900);
            _ = s.Show<SetupPanel>();
            return s;
        });
        try
        {
            var vm = UiThread.Run(() => (SetupPanelViewModel)shell.ViewModel);
            UiThread.WaitFor(() => !vm.IsLoading, "setup catalog read finished");

            UiThread.Run(() =>
            {
                vm.ShowCards(Sample);
                shell.Host.Relayout();
                RenderTo.Png(shell.Host, "setup-tiles-720x900");

                // A connector's name finds the one tile that leads to it (CUST-385: Connector Setup holds the connector wizards).
                vm.SearchText = "codex";
                shell.Host.Relayout();
                Assert.Equal(new[] { "Open Connector Setup" }, Tiles(shell).Select(AutomationProperties.GetName));

                vm.SearchText = "no such wizard";
                shell.Host.Relayout();
                Assert.Empty(Tiles(shell));
                Assert.True(vm.ShowNoMatch);

                // The certification filter is the chooser's now: it hides cursor (not certified) from the connector list, nothing from the grid.
                vm.ClearFiltersCommand.Execute(null);
                vm.CertifiedOnly = true;
                shell.Host.Relayout();
                Assert.Equal(SetupAreas.All.Count, Tiles(shell).Length);
                Assert.DoesNotContain(vm.ConnectorChoices, c => c.Target == "cursor");
                Assert.Contains(vm.ConnectorChoices, c => c.Target == "codex");
            });
        }
        finally
        {
            UiThread.Run(shell.Dispose);
        }
    }

    [Theory]
    [InlineData(245, 1, 245)]
    [InlineData(489.9, 1, 245)]
    [InlineData(490, 2, 245)]
    [InlineData(914, 3, 245)]
    [InlineData(1160, 4, 245)]
    [InlineData(100, 1, 245)]
    public void The_column_count_follows_the_width_with_a_minimum_column(double width, int columns, double minimum) =>
        Assert.Equal(columns, AdaptiveTileGrid.ColumnsFor(width, minimum));

    [Theory]
    [InlineData(980, 692)]
    [InlineData(1200, 800)]
    [InlineData(1468, 875)]
    public void The_tiles_share_the_whole_row_with_no_gap_wider_than_a_gutter_at_every_window_size(int width, int height)
    {
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp);
        var shell = UiThread.Run(() =>
        {
            var s = new PanelShell(services, width, height);
            _ = s.Show<SetupPanel>();
            return s;
        });
        try
        {
            var vm = UiThread.Run(() => (SetupPanelViewModel)shell.ViewModel);
            UiThread.WaitFor(() => !vm.IsLoading, "setup catalog read finished");

            UiThread.Run(() =>
            {
                vm.ShowCards(Sample);
                shell.Host.Relayout();

                var grid = Assert.Single(VisualTree.Descendants<AdaptiveTileGrid>(shell.Page!));
                var tiles = Tiles(shell);
                Assert.Equal(SetupAreas.All.Count, tiles.Length);

                // One width for every tile, never under the Mac's 233.
                var widths = tiles.Select(t => Math.Round(t.ActualWidth)).Distinct().ToArray();
                Assert.Single(widths);
                Assert.True(widths[0] >= 233, $"tile width {widths[0]}");

                // Per row: the tiles are side by side, and a full row ends at the grid's right edge (less the 12 DIP gutter the tile keeps itself).
                var placed = tiles
                    .Select(t => (Tile: t, Origin: t.TranslatePoint(new Point(), grid)))
                    .ToArray();
                var columns = placed.GroupBy(p => Math.Round(p.Origin.Y)).First().Count();
                foreach (var row in placed.GroupBy(p => Math.Round(p.Origin.Y)))
                {
                    var ordered = row.OrderBy(p => p.Origin.X).ToArray();
                    for (var i = 1; i < ordered.Length; i++)
                    {
                        var gap = ordered[i].Origin.X - (ordered[i - 1].Origin.X + ordered[i - 1].Tile.ActualWidth);
                        Assert.Equal(12, gap, 1.0);
                    }

                    if (ordered.Length == columns)
                    {
                        var last = ordered[^1];
                        Assert.Equal(grid.ActualWidth, last.Origin.X + last.Tile.ActualWidth + 12, 1.0);
                    }
                }

                // The Mac's order is the order on screen: row by row, left to right.
                var reading = placed.OrderBy(p => Math.Round(p.Origin.Y)).ThenBy(p => p.Origin.X).Select(p => p.Tile.DataContext).ToArray();
                Assert.Equal(vm.Areas.Where(a => a.IsShown).Cast<object>().ToArray(), reading);

                // The page does not scroll sideways.
                var scroller = VisualTree.Descendants<ScrollViewer>(shell.Page!).FirstOrDefault(sv => sv.ScrollableHeight > 0);
                if (scroller is not null)
                {
                    Assert.Equal(0, scroller.ScrollableWidth, 0.5);
                    scroller.ScrollToTop();
                    shell.Host.Relayout();
                }

                RenderTo.Png(shell.Host, $"cust395-setup-{width}x{height}");
            });
        }
        finally
        {
            UiThread.Run(shell.Dispose);
        }
    }

    /// <summary>The area tiles on screen (the Mac's grid).</summary>
    private static System.Windows.Controls.Button[] Tiles(PanelShell shell) =>
        VisualTree.Descendants<System.Windows.Controls.Button>(shell.Page!)
            .Where(b => b.IsVisible && b.DataContext is SetupAreaViewModel)
            .ToArray();

    private static System.Windows.Controls.Button[] ControlsTile(PanelShell shell) =>
        VisualTree.Descendants<System.Windows.Controls.Button>(shell.Page!)
            .Where(b => b.IsVisible && AutomationProperties.GetName(b) == "Guardrail controls")
            .ToArray();
}
