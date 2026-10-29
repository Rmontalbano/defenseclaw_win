using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Controls;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.Tests.Agents;

/// <summary>Where the Agents panel (CUST-355) sits in the shell and when it is on offer: only when the connected runtime is 1.0 or later, in every door.</summary>
public sealed class AgentsPanelCatalogTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly List<AppServices> _services = [];

    public void Dispose()
    {
        foreach (var services in _services)
        {
            services.Dispose();
        }

        _temp.Dispose();
    }

    private AppServices Create(string runtimeSet)
    {
        var services = AppServices.CreateIsolated(
            TestServices.IsolatedPaths(_temp.Path),
            claudeSettingsPath: _temp.File("claude-settings.json"),
            runtimeProbeRunner: RuntimeFixtureRunner.For(runtimeSet));
        _services.Add(services);
        return services;
    }

    [Fact]
    public void The_catalog_lists_the_panel_last_in_discover_after_the_macs_four_marked_windows_only_and_it_requires_the_capability()
    {
        var services = Create("95159fd");
        var catalog = new PanelCatalog(services);

        var panel = catalog.ById("agents");

        Assert.NotNull(panel);
        Assert.Equal("Agents", panel.Title);
        Assert.Equal("Discover", panel.Group);
        Assert.Equal(RuntimeCapability.CorrelationApi, panel.Requires);
        Assert.Equal(typeof(DefenseClaw.App.Views.Panels.AgentsPanel), panel.ViewType);

        // CUST-383: the Mac's Discover is Inventory, AI Discovery, Runtime, Registries; Agents, the Windows extension, comes after them and says so.
        Assert.Equal(
            ["inventory", "ai-discovery", "ai-runtime", "registries", "agents"],
            catalog.InGroup("Discover").Select(p => p.Id));
        Assert.Same(PanelTag.WindowsOnly, panel.Tag);
        Assert.Equal("Windows", panel.Tag!.Text);
        Assert.Equal("Discover", DcSections.OfPanelId("agents"));
        Assert.Equal(panel.Icon, DcSections.IconOfPanelId("agents"));
    }

    [Fact]
    public void The_view_model_is_the_agents_panels_and_its_title_matches_the_sidebar_label()
    {
        var services = Create("95159fd");
        var panel = new PanelCatalog(services).ById("agents")!;

        var vm = UiThread.Run(() => Assert.IsType<AgentsPanelViewModel>(panel.ViewModelFactory(services)));

        Assert.Equal(panel.Title, vm.Title);
        Assert.NotEmpty(vm.Description);
    }

    [Fact]
    public async Task Against_0_8_10_the_panel_stays_closed_in_every_door()
    {
        var services = Create("0.8.10");
        _ = await services.Runtime.RefreshAsync();
        var catalog = new PanelCatalog(services);
        var agents = catalog.ById("agents")!;

        Assert.False(catalog.IsOffered(agents));
        Assert.Null(catalog.ChordFor(agents));
        Assert.Equal(RuntimeCapabilityCatalog.UnsupportedMessage, catalog.Gate(agents).Reason);
        Assert.NotSame(agents, catalog.InitialPanel);
        var row = ShellCommandRegistry.BuildPanelCommands(catalog, _ => { }).Single(c => c.Id == "nav.agents");
        Assert.False(row.IsEnabled);
    }

    [Fact]
    public void Before_the_runtime_has_been_probed_the_panel_is_closed_too()
    {
        var services = Create("95159fd");
        var catalog = new PanelCatalog(services);

        Assert.False(catalog.IsOffered(catalog.ById("agents")!));
    }

    [Fact]
    public async Task Against_1_0_the_panel_is_offered_and_has_a_palette_row_but_no_number_chord()
    {
        var services = Create("95159fd");
        _ = await services.Runtime.RefreshAsync();
        var catalog = new PanelCatalog(services);
        var agents = catalog.ById("agents")!;

        Assert.True(catalog.IsOffered(agents));

        // The fifteen number chords are taken by the panels before it; it is reached from the sidebar and the command palette.
        Assert.Null(catalog.ChordFor(agents));
        Assert.Equal("Ctrl+Shift+5", catalog.ChordFor(catalog.ById("ai-runtime")!));
        Assert.True(ShellCommandRegistry.BuildPanelCommands(catalog, _ => { }).Single(c => c.Id == "nav.agents").IsEnabled);
    }
}
