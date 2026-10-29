using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Appearance;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.Appearance;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.ConfigEditor;
using DefenseClaw.App.Views.Panels;
using DefenseClaw.Core.Runtime;
using DefenseClaw.Core.Setup;

namespace DefenseClaw.App.Tests.Setup;

/// <summary>
/// The Setup page in the Mac's order (CUST-385; <c>Features/SetupView.swift</c> and <c>SetupDefinitions.swift</c> at v1.1.34): the 23 area tiles with the Mac's
/// labels and order, each leading to a flow this app already has (Connector Setup to the single-connector wizards, the batch dialog and <c>setup remove</c>);
/// Sandbox honest and closed, ACP Guard gated on the runtime; what no area reaches in a Windows group; the config editor inline beneath.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class SetupAreasTests
{
    /// <summary>The Mac's live grid on runtime 1.0.4, in its order (TUIWizards.all).</summary>
    private static readonly string[] MacOrder =
    {
        "Connector Setup", "Credentials", "Cisco AI Defense", "LLM", "Local OTel", "Galileo", "Token Rotation", "Custom Providers", "Skill Scanner",
        "MCP Scanner", "Gateway", "Guardrail", "Splunk", "Observability", "Webhooks", "Sandbox", "Registries", "Notifications Routing", "AI Discovery",
        "Splunk Dashboards", "Trusted Paths", "Guardrail Actions", "ACP Guard",
    };

    private static WizardDefinition Def(string target, string group, PlatformStatus status = PlatformStatus.NotApplicable, string? title = null) => new()
    {
        Target = target,
        Title = title ?? target,
        Group = group,
        PlatformStatus = status,
        Description = "Configure " + target + ".",
    };

    /// <summary>A catalog like 1.0.4's on Windows: connectors, remove, the Mac's wizard targets, and two the Mac has no tile for.</summary>
    private static readonly WizardDefinition[] Catalog =
    {
        Def("claude-code", WizardGroups.Connectors, PlatformStatus.Certified, "Claude Code"),
        Def("codex", WizardGroups.Connectors, PlatformStatus.Certified, "Codex"),
        Def("cursor", WizardGroups.Connectors, PlatformStatus.NotCertified, "Cursor"),
        Def("remove", WizardGroups.Connectors),
        Def("guardrail", WizardGroups.GuardrailAndPolicy),
        Def("llm", WizardGroups.Credentials, title: "LLM providers"),
        Def("gateway", WizardGroups.Credentials),
        Def("rotate-token", WizardGroups.Credentials),
        Def("provider", WizardGroups.GuardrailAndPolicy),
        Def("registry", WizardGroups.GuardrailAndPolicy),
        Def("trusted-paths", WizardGroups.GuardrailAndPolicy),
        Def("skill-scanner", WizardGroups.Scanners),
        Def("mcp-scanner", WizardGroups.Scanners),
        Def("observability", WizardGroups.Observability),
        Def("local-observability", WizardGroups.Observability),
        Def("galileo", WizardGroups.Observability),
        Def("splunk", WizardGroups.Observability),
        Def("webhook", WizardGroups.Observability),
        Def("notifications", WizardGroups.Other),
        Def("notifications-set", WizardGroups.Other),
        Def("migrate-llm", WizardGroups.Credentials),
        Def("routing", WizardGroups.Other),
    };

    private static SetupPanelViewModel Hub(AppServices services) => UiThread.Run(() =>
    {
        var vm = new SetupPanelViewModel(services);
        vm.ShowCards(Catalog);
        return vm;
    });

    private static SetupAreaViewModel Area(SetupPanelViewModel vm, string id) => vm.Areas.Single(a => a.Id == id);

    // ------------------------------------------------------------------ the grid

    [Fact]
    public void The_grid_is_the_Macs_twenty_three_areas_with_its_labels_in_its_order()
    {
        Assert.Equal(MacOrder, SetupAreas.All.Select(a => a.Title));
        Assert.Equal(SetupAreas.All.Count, SetupAreas.All.Select(a => a.Id).Distinct(StringComparer.Ordinal).Count());

        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp);
        var vm = Hub(services);

        Assert.Equal(MacOrder, vm.Areas.Select(a => a.Title));
        Assert.Equal("23 of 23", vm.AreaCountText);

        // Every tile names the flow it reaches: a wizard tile its card, the rest their own route.
        UiThread.Run(() =>
        {
            Assert.Same(vm.Groups.SelectMany(g => g.Cards).Single(c => c.Target == "llm"), Area(vm, "llm").Card);
            Assert.Same(vm.Groups.SelectMany(g => g.Cards).Single(c => c.Target == "guardrail"), Area(vm, "ai-defense").Card);
            Assert.Equal("cisco", Area(vm, "ai-defense").Spec.Goal);
            Assert.Equal("notifications-set", Area(vm, "notification-routing").Card!.Target);
            Assert.Equal("rotate-token", Area(vm, "token-rotation").Card!.Target);
            Assert.Equal("provider", Area(vm, "custom-providers").Card!.Target);
            Assert.Equal("webhook", Area(vm, "webhooks").Card!.Target);
            // (Local OTel and Splunk Dashboards wait on the Docker and Terraform looks, which nothing has answered here.)
            Assert.All(
                vm.Areas.Where(a => a.Spec.Route == SetupAreaRoute.Wizard && a.Id is not ("splunk-dashboards" or "local-observability" or "gateway")),
                a => Assert.True(a.CanOpen, a.Title + ": " + a.Reason));

            // The Windows policy the card already had stays: setup gateway is the OpenClaw fleet uplink, which Windows does not run, and the tile says so.
            Assert.False(Area(vm, "gateway").CanOpen);
            Assert.Contains("OpenClaw", Area(vm, "gateway").Blurb, StringComparison.Ordinal);

            // Registries: setup registry needs a terminal here, and its card sends the operator to the Registries panel - so does the tile.
            Assert.True(Area(vm, "registries").CanOpen);
            vm.OpenAreaCommand.Execute(Area(vm, "registries"));
            Assert.Equal(new NavigationRequest("registries"), services.Navigation.Pending);
        });
    }

    [Fact]
    public void A_wizard_the_installed_CLI_lacks_says_so_instead_of_opening()
    {
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp);
        var vm = UiThread.Run(() =>
        {
            var hub = new SetupPanelViewModel(services);
            hub.ShowCards(Catalog.Where(d => d.Target != "galileo").ToArray());
            return hub;
        });

        var galileo = Area(vm, "galileo");
        Assert.False(galileo.CanOpen);
        Assert.Null(galileo.Card);
        Assert.Equal("The installed DefenseClaw has no 'defenseclaw setup galileo' command.", galileo.Reason);
        Assert.Equal(galileo.Reason, galileo.Blurb);
    }

    // ------------------------------------------------------------------ Connector Setup

    [Fact]
    public void Connector_Setup_is_one_tile_over_the_existing_single_connector_batch_and_remove_flows()
    {
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp);
        var shell = UiThread.Run(() =>
        {
            var s = new PanelShell(services, 1468, 875);
            _ = s.Show<SetupPanel>();
            return s;
        });
        try
        {
            var vm = UiThread.Run(() => (SetupPanelViewModel)shell.ViewModel);
            UiThread.WaitFor(() => !vm.IsLoading, "setup catalog read finished");

            UiThread.Run(() =>
            {
                vm.ShowCards(Catalog);
                shell.Host.Relayout();

                // One connector tile in the grid, no connector card anywhere else on the page.
                var connector = Area(vm, "connector");
                Assert.Single(vm.Areas, a => a.Spec.Route == SetupAreaRoute.ConnectorSetup);
                Assert.DoesNotContain(vm.ExtraGroups.SelectMany(g => g.Cards), SetupAreas.IsConnectorCard);
                Assert.True(connector.CanOpen);

                vm.OpenAreaCommand.Execute(connector);
                Assert.True(vm.IsConnectorSetupOpen);
                shell.Host.Relayout();
                RenderTo.Png(shell.Host, "cust385-connector-setup-1468x875");

                // The chooser lists the catalog's own connector cards - the same objects, so the same wizards - and not `setup remove`.
                var cards = vm.Groups.SelectMany(g => g.Cards).ToList();
                Assert.Equal(new[] { "claude-code", "codex", "cursor" }, vm.ConnectorChoices.Select(c => c.Target).Order(StringComparer.Ordinal));
                Assert.All(vm.ConnectorChoices, c => Assert.Contains(c, cards));

                // Its tiles run the hub's own Launch on their card: the single-connector wizard, nothing new.
                var tiles = VisualTree.Descendants<Button>(VisualTree.Find<Border>(shell.Page!, b => b.IsVisible && AutomationProperties.GetName(b) == "Connector Setup")!)
                    .Where(b => b.IsVisible && b.DataContext is WizardCardViewModel card && vm.ConnectorChoices.Contains(card))
                    .ToArray();
                Assert.Equal(3, tiles.Length);
                Assert.All(tiles, t =>
                {
                    Assert.Same(vm.LaunchCommand, t.Command);
                    Assert.Same(t.DataContext, t.CommandParameter);
                });
                Assert.Contains(tiles, t => AutomationProperties.GetName(t) == "Configure Claude Code");

                // Remove is `setup remove`'s card; several at once is the batch dialog.
                Assert.Equal("remove", vm.RemoveConnectorCard!.Target);
                var chooser = VisualTree.Find<Border>(shell.Page!, b => b.IsVisible && AutomationProperties.GetName(b) == "Connector Setup")!;
                var remove = VisualTree.Find<Wpf.Ui.Controls.Button>(chooser, b => b.IsVisible && (b.Content as string) == "Remove a connector…");
                Assert.NotNull(remove);
                Assert.Same(vm.RemoveConnectorCommand, remove!.Command);
                var several = VisualTree.Find<Wpf.Ui.Controls.Button>(chooser, b => b.IsVisible && (b.Content as string) == "Set up several…");
                Assert.NotNull(several);
                Assert.Same(vm.OpenBatchFromChooserCommand, several!.Command);

                several.Command.Execute(null);
                Assert.False(vm.IsConnectorSetupOpen);
                Assert.True(vm.Batch.IsOpen);
                _ = vm.Batch.HandleEscape();

                // The chooser's certification filter.
                vm.IsConnectorSetupOpen = true;
                vm.CertifiedOnly = true;
                Assert.DoesNotContain(vm.ConnectorChoices, c => c.Target == "cursor");
                Assert.True(vm.HandleConnectorSetupEscape());
                Assert.False(vm.IsConnectorSetupOpen);
            });
        }
        finally
        {
            UiThread.Run(shell.Dispose);
        }
    }

    [Fact]
    public void A_connectors_name_finds_the_Connector_Setup_tile()
    {
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp);
        var vm = Hub(services);

        UiThread.Run(() =>
        {
            vm.SearchText = "claude";
            Assert.Equal(new[] { "connector" }, vm.Areas.Where(a => a.IsShown).Select(a => a.Id));

            vm.SearchText = "splunk";
            Assert.Equal(new[] { "splunk", "splunk-dashboards" }, vm.Areas.Where(a => a.IsShown).Select(a => a.Id).Where(id => id.StartsWith("splunk", StringComparison.Ordinal)));
        });
    }

    // ------------------------------------------------------------------ the other routes

    [Fact]
    public void Credentials_and_Guardrail_Actions_open_their_cards_and_AI_Discovery_its_tuning()
    {
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp);
        var vm = Hub(services);

        UiThread.Run(() =>
        {
            var asked = new List<string?>();
            vm.SectionRequested += (_, _) => asked.Add(vm.PendingSection);

            vm.IsCredentialsExpanded = false;
            vm.OpenAreaCommand.Execute(Area(vm, "credentials"));
            Assert.True(vm.IsCredentialsExpanded);

            vm.IsGuardrailExpanded = false;
            vm.OpenAreaCommand.Execute(Area(vm, "guardrail-actions"));
            Assert.True(vm.IsGuardrailExpanded);

            vm.ShowConfigEditorCommand.Execute(null);
            Assert.Equal(new[] { SetupPanelViewModel.CredentialsSection, SetupPanelViewModel.GuardrailSection, SetupPanelViewModel.ConfigEditorSection }, asked);

            vm.OpenAreaCommand.Execute(Area(vm, "ai-discovery"));
            Assert.Equal(new NavigationRequest("ai-discovery", AiDiscoveryTune.Instance), services.Navigation.Pending);

            // The deep link brings the inline editor into view; nothing is loaded or run by it.
            vm.PendingSection = null;
            vm.Accept(ConfigEditorFocus.Instance);
            Assert.Equal(SetupPanelViewModel.ConfigEditorSection, vm.PendingSection);
        });

        Assert.Empty(services.Cli.Activity);
    }

    [Fact]
    public void Sandbox_is_honest_and_closed_and_ACP_Guard_keeps_its_slot_behind_its_capability()
    {
        using var temp = new TempDirectory();
        using var unknown = TestServices.Create(temp);
        var vm = Hub(unknown);

        var sandbox = Area(vm, "sandbox");
        Assert.False(sandbox.CanOpen);
        Assert.Equal(SetupAreas.SandboxReason, sandbox.Blurb);
        Assert.Contains("Linux and macOS only", sandbox.TileToolTip, StringComparison.Ordinal);
        Assert.False(RuntimeCapabilityCatalog.IsUsableHere(RuntimeCapability.Sandbox));

        var acp = Area(vm, "acp-guard");
        Assert.False(acp.CanOpen);
        Assert.False(acp.IsComing);
        Assert.Equal(RuntimeCapabilityCatalog.UnsupportedMessage, acp.Reason);
    }

    [Fact]
    public async Task On_a_runtime_with_the_ACP_guard_its_tile_opens_the_ACP_Guard_wizard()
    {
        // CUST-294 replaced the "coming" tile (CUST-385) with the wizard: the tile now opens it, and its blurb is the Mac's.
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp, runtimeProbeRunner: RuntimeFixtureRunner.For("95159fd"));
        _ = await services.Runtime.RefreshAsync(force: true);
        Assert.True(services.Runtime.Capabilities.Has(RuntimeCapability.AcpGuard));

        var vm = Hub(services);
        var acp = Area(vm, "acp-guard");
        Assert.False(acp.IsComing);
        Assert.True(acp.CanOpen);
        Assert.Equal(SetupAreas.All.Single(a => a.Id == "acp-guard").Blurb, acp.Blurb);
        Assert.Equal(SetupAreas.AcpGuardToolTip, acp.TileToolTip);

        // Sandboxes stay closed on Windows whatever the runtime says.
        Assert.False(Area(vm, "sandbox").CanOpen);
    }

    [Fact]
    public void What_no_area_reaches_goes_in_the_Windows_group_and_nothing_twice()
    {
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp);
        var vm = Hub(services);

        var extras = vm.ExtraGroups.SelectMany(g => g.Cards).Select(c => c.Target).Order(StringComparer.Ordinal).ToArray();
        // (setup notifications, the master switch, opens the same routing dialog as Notifications Routing: that tile reaches it.)
        Assert.Equal(new[] { "migrate-llm", "routing" }, extras);
        Assert.True(vm.HasExtraGroups);
    }

    [Fact]
    public void A_read_only_installation_closes_the_tiles_that_change_things_and_keeps_the_ones_that_only_show()
    {
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp, installation: TestInstallations.Managed());
        var vm = Hub(services);

        UiThread.Run(() =>
        {
            Assert.False(Area(vm, "llm").CanOpen);
            Assert.Equal(TestInstallations.ManagedReason, Area(vm, "llm").Reason);
            Assert.Equal(Area(vm, "llm").Spec.Blurb, Area(vm, "llm").Blurb); // the page's banner says why; the tile keeps its words
            Assert.False(Area(vm, "connector").CanOpen);
            Assert.Contains(TestInstallations.ManagedReason, Area(vm, "connector").TileToolTip, StringComparison.Ordinal);
            Assert.True(Area(vm, "credentials").CanOpen);
            Assert.True(Area(vm, "guardrail-actions").CanOpen);
            Assert.True(Area(vm, "ai-discovery").CanOpen);
        });
    }

    // ------------------------------------------------------------------ the page

    public static IEnumerable<object[]> Renders() => new[]
    {
        new object[] { 980, 692, "Light" },
        new object[] { 980, 692, "Dark" },
        new object[] { 1468, 875, "Light" },
        new object[] { 1468, 875, "Dark" },
    };

    [Theory]
    [MemberData(nameof(Renders))]
    public void The_page_is_the_grid_first_and_the_config_editor_inline_beneath_it(int width, int height, string mode)
    {
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp, ConfigSamples.Raw);
        services.ConfigWatcher.Dispose();
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Default, Enum.Parse<AppearanceMode>(mode)));

        // The draft is the session's: its FORM is fed the masked view a CLI would print, so nothing runs.
        var session = ConfigEditorSession.For(services);
        session.Editor.FormSourceOverride = _ => Task.FromResult(ConfigSamples.MaskedSource);

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
            UiThread.WaitFor(() => session.Editor.HasLoadStarted && !session.Editor.IsLoading, "the inline editor loaded the draft");

            UiThread.Run(() =>
            {
                // A fixture catalog stands in for the CLI's (which this composition cannot reach), so its "could not be read" banner is not the page's.
                vm.ShowCards(Catalog);
                vm.HasLoadError = false;
                vm.LoadError = string.Empty;
                shell.Host.Relayout();
                UiThread.Settle();
                shell.Host.Relayout();
                RenderTo.Png(shell.Host, $"cust385-setup-{width}x{height}-{mode}-top");

                // Top of the page: the heading, then the grid. The heading comes before the first tile, the first tile is Connector Setup.
                var page = shell.Page!;
                var heading = VisualTree.Find<TextBlock>(page, t => t.Text == "Setup Areas")!;
                var tiles = VisualTree.Descendants<Button>(page).Where(b => b.IsVisible && b.DataContext is SetupAreaViewModel).ToArray();
                Assert.Equal(23, tiles.Length);
                Assert.Equal("Open Connector Setup", AutomationProperties.GetName(tiles[0]));
                Assert.True(Y(heading, page) < Y(tiles[0], page));

                // Beneath the grid, after the Windows group: the config editor, inline, on the session's draft.
                var editorHeading = VisualTree.Find<TextBlock>(page, t => t.Text == "Config Editor")!;
                var inline = VisualTree.Find<ConfigEditorView>(page)!;
                Assert.Same(session, inline.Session);
                Assert.Same(session.Editor, inline.ViewModel);
                Assert.True(Y(tiles[^1], page) < Y(editorHeading, page));
                Assert.True(Y(editorHeading, page) < Y(inline, page));
                Assert.True(Y(inline, page) < Y((FrameworkElement)page.FindName("ReadinessSection"), page));

                // The inline editor's order: the search, the catalog label and Reload, the section list and the form, Revert and Save.
                var search = VisualTree.Find<Wpf.Ui.Controls.TextBox>(inline, t => AutomationProperties.GetName(t) == "Search all settings")!;
                var label = VisualTree.Find<TextBlock>(inline, t => t.Text == session.Editor.CatalogLabel)!;
                var sections = VisualTree.Find<ListBox>(inline, l => l.Name == "SectionList")!;
                var save = VisualTree.Find<Wpf.Ui.Controls.Button>(inline, b => b.IsVisible && (b.Content as string) == "Save")!;
                var revert = VisualTree.Find<Wpf.Ui.Controls.Button>(inline, b => b.IsVisible && (b.Content as string) == "Revert")!;
                Assert.StartsWith("runtime · ", session.Editor.CatalogLabel, StringComparison.Ordinal);
                Assert.Equal(session.Editor.Sections.Count, sections.Items.Count);
                Assert.True(Y(search, inline) <= Y(label, inline));
                Assert.True(Y(label, inline) < Y(sections, inline));
                Assert.True(Y(sections, inline) < Y(save, inline));
                Assert.Equal(Y(save, inline), Y(revert, inline), 1);

                // Scrolled to the editor (the toolbar's Config editor button).
                vm.ShowConfigEditorCommand.Execute(null);
                UiThread.Settle();
                shell.Host.Relayout();
                RenderTo.Png(shell.Host, $"cust385-setup-{width}x{height}-{mode}-editor");
                var scroll = (ScrollViewer)page.FindName("PageScroll");
                Assert.True(scroll.VerticalOffset > 0);
            });
        }
        finally
        {
            UiThread.Run(shell.Dispose);
        }
    }

    private static double Y(FrameworkElement element, FrameworkElement root) => element.TranslatePoint(new Point(0, 0), root).Y;
}
