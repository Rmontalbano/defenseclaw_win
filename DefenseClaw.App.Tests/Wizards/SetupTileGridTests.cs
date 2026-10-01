using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Panels;
using Wpf.Ui.Controls;

namespace DefenseClaw.App.Tests.Wizards;

/// <summary>The Setup hub's wizards are an adaptive grid of 233 x 92 tiles, each a Fluent glyph, a title and a two-line blurb (CUST-225).</summary>
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
    public void The_grid_is_233_by_92_tiles_that_open_the_wizard_and_the_guardrail_tile_waits_for_its_wiring()
    {
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
                Assert.Equal(Sample.Length, tiles.Length);
                Assert.All(tiles, t =>
                {
                    Assert.Equal(233, t.ActualWidth, 0.5);
                    Assert.Equal(92, t.ActualHeight, 0.5);
                    Assert.Equal(12, t.Margin.Right);
                    Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(t)));
                    Assert.NotNull(t.Command);
                });
                Assert.Contains(tiles, t => AutomationProperties.GetName(t) == "Configure claude-code");

                // Several tiles share a row on a wide page: it is a grid, not a list.
                var rows = tiles.Select(t => Math.Round(t.TranslatePoint(new Point(), shell.Page!).Y)).Distinct().Count();
                Assert.True(rows < tiles.Length, "tiles wrap into rows");

                // Keyboard: every tile is a tab stop.
                Assert.All(tiles, t => Assert.True(t.Focusable && t.IsTabStop));

                // The guardrail-controls tile is absent until the shell gives it somewhere to go.
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

                vm.SearchText = "codex";
                shell.Host.Relayout();
                Assert.Single(Tiles(shell));

                vm.SearchText = "no such wizard";
                shell.Host.Relayout();
                Assert.Empty(Tiles(shell));
                Assert.True(vm.ShowNoMatch);

                vm.ClearFiltersCommand.Execute(null);
                vm.CertifiedOnly = true;
                shell.Host.Relayout();
                Assert.DoesNotContain(Tiles(shell), t => AutomationProperties.GetName(t) == "Configure cursor");
            });
        }
        finally
        {
            UiThread.Run(shell.Dispose);
        }
    }

    private static System.Windows.Controls.Button[] Tiles(PanelShell shell) =>
        VisualTree.Descendants<System.Windows.Controls.Button>(shell.Page!)
            .Where(b => b.IsVisible && b.DataContext is WizardCardViewModel)
            .ToArray();

    private static System.Windows.Controls.Button[] ControlsTile(PanelShell shell) =>
        VisualTree.Descendants<System.Windows.Controls.Button>(shell.Page!)
            .Where(b => b.IsVisible && AutomationProperties.GetName(b) == "Guardrail controls")
            .ToArray();
}
