using System.Windows;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Controls;
using DefenseClaw.App.Views.Panels;
using DefenseClaw.App.Views.Shell;
using DefenseClaw.Core.Runtime;
using Wpf.Ui.Controls;

namespace DefenseClaw.App.Tests.Sandboxes;

/// <summary>
/// The Sandboxes panel (CUST-383): the Mac's Sandboxes slot in Govern, which on Windows explains and runs nothing. It is on offer whatever the runtime
/// (a gate would hide the one page that says why), takes no chord, says what the connected runtime has, and its only command shows Setup. The decision
/// that sandboxes are not usable here is the runtime capability catalog's (<see cref="RuntimeCapabilityCatalog.IsUsableHere"/>). Synthetic data only;
/// the runtime answers from the probe fixtures.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class SandboxesPanelTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly List<AppServices> _services = new();

    public void Dispose()
    {
        foreach (var services in _services)
        {
            services.Dispose();
        }

        _temp.Dispose();
    }

    private AppServices Create(string? runtimeSet = null)
    {
        var services = TestServices.Create(_temp, runtimeProbeRunner: runtimeSet is null ? null : RuntimeFixtureRunner.For(runtimeSet));
        _services.Add(services);
        return services;
    }

    private static SandboxesPanelViewModel Build(AppServices services) =>
        UiThread.Run(() => Assert.IsType<SandboxesPanelViewModel>(new PanelCatalog(services).ById("sandboxes")!.ViewModelFactory(services)));

    // ------------------------------------------------------------------ the catalog

    [Theory]
    [InlineData(null)]
    [InlineData("0.8.10")]
    [InlineData("95159fd")]
    public async Task It_is_in_govern_after_policies_on_offer_whatever_the_runtime_with_no_chord_and_an_enabled_palette_row(string? runtimeSet)
    {
        var services = Create(runtimeSet);
        if (runtimeSet is not null)
        {
            _ = await services.Runtime.RefreshAsync();
        }

        var catalog = new PanelCatalog(services);
        var panel = catalog.ById("sandboxes");

        Assert.NotNull(panel);
        Assert.Equal("Sandboxes", panel.Title);
        Assert.Equal("Govern", panel.Group);
        Assert.Null(panel.Requires);
        Assert.Equal(typeof(SandboxesPanel), panel.ViewType);
        Assert.Equal(SymbolRegular.Cube24, panel.Icon);
        Assert.Equal(new[] { "skills", "mcps", "plugins", "tools", "policies", "sandboxes" }, catalog.InGroup("Govern").Select(p => p.Id).ToArray());
        Assert.Equal("Govern", DcSections.OfPanelId("sandboxes"));
        Assert.Equal("Govern", DcSections.OfPanelId("policies"));

        // Always on offer (selectable, to read why); no chord, and none of the fifteen moved for it.
        Assert.True(catalog.IsOffered(panel));
        Assert.Null(catalog.ChordFor(panel));
        Assert.DoesNotContain(panel, catalog.ChordOrder.Take(ShellShortcuts.NumberedPanels));
        var row = ShellCommandRegistry.BuildPanelCommands(catalog, _ => { }).Single(c => c.Id == "nav.sandboxes");
        Assert.True(row.IsEnabled);
        Assert.Null(row.Shortcut);
        Assert.Equal("Go to Sandboxes", row.Title);

        // Its sidebar word is the capability catalog's decision: not usable on Windows, named as About names it.
        Assert.False(RuntimeCapabilityCatalog.IsUsableHere(RuntimeCapability.Sandbox));
        Assert.Same(PanelTag.SandboxesElsewhere, panel.Tag);
        Assert.Equal("Linux/macOS", panel.Tag!.Text);
        Assert.StartsWith(RuntimeCapabilityCatalog.DisplayName(RuntimeCapability.Sandbox), panel.Tag.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_panel_the_runtime_does_not_offer_is_hidden_in_the_sidebar_but_sandboxes_never_is()
    {
        var services = Create("0.8.10");
        _ = await services.Runtime.RefreshAsync();
        var catalog = new PanelCatalog(services);

        UiThread.Run(() =>
        {
            // On 0.8.10 the panels that need a newer runtime are hidden, and their palette rows say why; Sandboxes, which needs nothing to explain, stays.
            var items = catalog.Panels.ToDictionary(p => p.Id, _ => new DcNavigationItem(), StringComparer.Ordinal);
            MainWindow.ApplyPanelGates(catalog, items);

            Assert.Equal(Visibility.Collapsed, items["ai-runtime"].Visibility);
            Assert.Equal(Visibility.Collapsed, items["agents"].Visibility);
            Assert.Equal(Visibility.Visible, items["sandboxes"].Visibility);
            Assert.Equal(Visibility.Visible, items["policies"].Visibility);

            var commands = ShellCommandRegistry.BuildPanelCommands(catalog, _ => { });
            Assert.Equal(RuntimeCapabilityCatalog.UnsupportedMessage, commands.Single(c => c.Id == "nav.agents").DisabledReason);
            Assert.Null(commands.Single(c => c.Id == "nav.sandboxes").DisabledReason);
        });
    }

    [Fact]
    public void A_deep_link_or_a_remembered_selection_reaches_policies_and_sandboxes_in_their_new_group()
    {
        var services = Create();
        var catalog = new PanelCatalog(services);

        Assert.True(services.Settings.Update(s => s with { Startup = s.Startup with { RememberLastPanel = true, LastPanelId = "policies" } }));
        Assert.Equal("policies", catalog.InitialPanel.Id);

        Assert.True(services.Settings.Update(s => s with { Startup = s.Startup with { LastPanelId = "sandboxes" } }));
        Assert.Equal("sandboxes", catalog.InitialPanel.Id);

        // A deep link raised before the window exists wins over the remembered one, and keeps its payload for the panel.
        var payload = new object();
        services.Navigation.Request("policies", payload);
        Assert.Equal("policies", catalog.InitialPanel.Id);
        Assert.Same(payload, services.Navigation.Pending!.Payload);
    }

    // ------------------------------------------------------------------ the page

    [Fact]
    public void Before_the_runtime_is_identified_the_page_says_sandboxes_are_not_available_on_windows_and_why()
    {
        var model = Build(Create());

        Assert.Equal("Sandboxes", model.Title);
        Assert.False(model.IsUsableHere);
        Assert.Equal("Sandboxes are not available on Windows", model.StateTitle);
        Assert.Contains("Linux and macOS only", model.StateDetail, StringComparison.Ordinal);
        Assert.Contains("offers no sandbox actions", model.StateDetail, StringComparison.Ordinal);
        Assert.Equal("Linux and macOS only", model.CaptionText);
        Assert.Equal("The connected runtime has not been identified yet.", model.RuntimeFact);
        Assert.Contains(SandboxesPanelViewModel.ElsewhereSetupCommand, model.ElsewhereText, StringComparison.Ordinal);
        Assert.StartsWith("On a Linux or macOS host", model.ElsewhereText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("95159fd", "The connected runtime (defenseclaw-cli 1.0.0) has the sandbox commands; they target Linux and macOS.")]
    [InlineData("0.8.10", "The connected runtime (defenseclaw-cli 0.8.10) has no sandbox commands.")]
    public async Task The_page_says_what_the_connected_runtime_has_and_follows_it_while_on_screen(string runtimeSet, string expected)
    {
        var services = Create(runtimeSet);
        var model = Build(services);
        UiThread.Run(() => model.SetActive(true));

        _ = await services.Runtime.RefreshAsync();

        UiThread.WaitFor(() => model.RuntimeFact == expected, $"the runtime fact to read '{expected}' (it reads '{UiThread.Run(() => model.RuntimeFact)}')");
        Assert.Equal("Sandboxes are not available on Windows", model.StateTitle);

        UiThread.Run(() => model.SetActive(false));
    }

    [Fact]
    public void Its_only_command_shows_setup_and_nothing_runs()
    {
        var services = Create();
        var model = Build(services);
        var started = 0;
        services.Cli.InvocationStarted += (_, _) => started++;

        // Every command the view-model has: Open Setup, and nothing that could reach the CLI.
        var commands = typeof(SandboxesPanelViewModel).GetProperties()
            .Where(p => typeof(System.Windows.Input.ICommand).IsAssignableFrom(p.PropertyType))
            .Select(p => p.Name)
            .ToArray();
        Assert.Equal(new[] { "OpenSetupCommand" }, commands);

        UiThread.Run(() => model.OpenSetupCommand.Execute(null));

        Assert.Equal("setup", services.Navigation.Pending?.PanelId);
        Assert.Equal(0, started);
        Assert.Empty(services.Cli.Activity);
        Assert.IsAssignableFrom<IRelayCommand>(model.OpenSetupCommand);
    }

    [Fact]
    public void The_view_shows_the_reason_and_one_button_that_opens_setup_and_nothing_to_copy_or_run()
    {
        var services = Create();

        UiThread.Run(() =>
        {
            using var shell = new PanelShell(services, 940, 620);
            var page = shell.Show<SandboxesPanel>();

            var toolbar = Assert.Single(VisualTree.Descendants<DcPageToolbar>(page));
            Assert.Equal("Sandboxes", toolbar.Title);
            Assert.Equal("Linux and macOS only", toolbar.Caption);
            Assert.False(toolbar.HasSearch);

            var texts = VisualTree.Descendants<System.Windows.Controls.TextBlock>(page).Where(t => t.IsVisible).Select(t => t.Text).ToArray();
            Assert.Contains("Sandboxes are not available on Windows", texts);
            Assert.Contains(texts, t => t.Contains("Linux and macOS only, so there is nothing to set up on this PC", StringComparison.Ordinal));

            // One button, and it is Open Setup; no text box to copy a command from.
            var button = Assert.Single(VisualTree.Descendants<System.Windows.Controls.Primitives.ButtonBase>(page), b => b.IsVisible);
            Assert.Equal("Open Setup", System.Windows.Automation.AutomationProperties.GetName(button));
            Assert.DoesNotContain(VisualTree.Descendants<System.Windows.Controls.TextBox>(page), box => box.IsVisible);

            // At the window's minimum the explanation fits beside the sidebar without clipping its sides.
            var explanation = (FrameworkElement)page.FindName("Explanation");
            Assert.True(explanation.ActualWidth > 0 && explanation.ActualWidth <= page.ActualWidth);
        });
    }
}
