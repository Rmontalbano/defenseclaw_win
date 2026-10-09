using System.Text.Json.Nodes;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Settings;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.ViewModels.ConfigEditor;
using DefenseClaw.Core.Config;
using DefenseClaw.Core.Paths;
using DefenseClaw.Core.Runtime;
using Wpf.Ui.Controls;

namespace DefenseClaw.App.Tests.Settings;

/// <summary>
/// Runtime detection and the developer runtime selector (CUST-291) at the app level: the settings section and its round trip, the
/// Settings -> Advanced form, the gate in the command palette, About's identity and capability list, and that the default (the selector
/// off) changes nothing. Help text comes from the same fixtures the Core suite uses; no process is started.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class RuntimeSelectorTests : IDisposable
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

    // ------------------------------------------------------------------ a runner that answers from the fixtures

    private static RuntimeProbeRunner FixtureRunner(string set) => RuntimeFixtureRunner.For(set);

    private AppServices Create(RuntimeProbeRunner? runner = null, DefenseClawPaths? paths = null)
    {
        var services = AppServices.CreateIsolated(
            paths ?? TestServices.IsolatedPaths(_temp.Path),
            claudeSettingsPath: _temp.File("claude-settings.json"),
            runtimeProbeRunner: runner);
        _services.Add(services);
        return services;
    }

    private static SettingsPanelViewModel Build(AppServices services, FakePlatform? platform = null) =>
        UiThread.Run(() => new SettingsPanelViewModel(services, hooks: null, (platform ?? new FakePlatform()).Build()));

    private static AppSettings Saved(AppServices services) => AppSettingsStore.OpenFresh(services.Settings.FilePath).Current;

    // ------------------------------------------------------------------ the settings section

    [Fact]
    public void The_selector_is_off_by_default_and_off_is_the_installed_runtime_whatever_else_is_filled_in()
    {
        var defaults = new DeveloperSettings();
        Assert.False(defaults.Enabled);
        Assert.Equal(RuntimeKind.Installed, defaults.Kind);
        Assert.True(defaults.ToSelection().IsDefault);

        var filledButOff = new DeveloperSettings
        {
            Enabled = false,
            Kind = RuntimeKind.Container,
            ContainerName = "dc-next-1",
            GatewayUrl = "http://127.0.0.1:18971",
            HostDataFolder = _temp.Path,
        };
        Assert.True(filledButOff.ToSelection().IsDefault);
        Assert.False(filledButOff.Raw().IsDefault);
    }

    [Fact]
    public void An_enabled_choice_that_does_not_validate_falls_back_to_the_installed_runtime_so_the_app_always_starts()
    {
        var missingFile = new DeveloperSettings
        {
            Enabled = true,
            Kind = RuntimeKind.Cli,
            CliPath = _temp.File("defenseclaw.exe"), // not created
            HomeDirectory = _temp.File("home"),
        };
        Assert.True(missingFile.ToSelection().IsDefault);

        _ = _temp.WriteFile("defenseclaw.exe", "stub");
        var now = missingFile;
        Assert.Equal(RuntimeKind.Cli, now.ToSelection().Kind);
        Assert.Equal(_temp.File("home"), now.ToSelection().DataDirectoryOverride);
    }

    [Fact]
    public void The_section_round_trips_through_the_file_and_an_unknown_kind_reads_as_installed()
    {
        var path = _temp.File("settings.json");
        var store = AppSettingsStore.OpenFresh(path);
        var written = new DeveloperSettings
        {
            Enabled = true,
            Kind = RuntimeKind.Cli,
            CliPath = @"D:\next\bin\defenseclaw.exe",
            HomeDirectory = @"D:\next\home",
            GatewayUrl = "http://127.0.0.1:18972",
        };

        Assert.True(store.Update(s => s with { Developer = written }));

        var reread = AppSettingsStore.OpenFresh(path).Current.Developer;
        Assert.Equal(written, reread);
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Equal("cli", json["developer"]!["kind"]!.GetValue<string>());

        json["developer"]!["kind"] = "warp-drive";
        File.WriteAllText(path, json.ToJsonString());
        Assert.Equal(RuntimeKind.Installed, AppSettingsStore.OpenFresh(path).Current.Developer.Kind);
    }

    [Fact]
    public void A_user_who_never_touches_the_selector_gets_no_developer_section_in_the_file()
    {
        var path = _temp.File("settings.json");
        var store = AppSettingsStore.OpenFresh(path);

        Assert.True(store.Update(s => s with { Monitoring = s.Monitoring with { Paused = true } }));

        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Null(json["developer"]);
    }

    // ------------------------------------------------------------------ Settings -> Advanced

    [Fact]
    public void The_page_starts_with_the_selector_off_showing_the_installed_runtime_and_no_form()
    {
        var model = Build(Create());

        Assert.False(model.DeveloperEnabled);
        Assert.Equal(RuntimeKind.Installed, model.SelectedRuntimeKind.Kind);
        Assert.False(model.ShowRuntimeForm);
        Assert.False(model.ShowCliFields);
        Assert.False(model.ShowContainerFields);
        Assert.Equal("Installed runtime", model.RuntimeInUseText);
        Assert.False(model.HasRuntimeProblem);
        Assert.Equal(3, model.RuntimeKinds.Count);
    }

    [Fact]
    public void The_switch_writes_at_once_and_reveals_the_fields_for_the_chosen_kind()
    {
        var services = Create();
        var model = Build(services);

        UiThread.Run(() =>
        {
            model.DeveloperEnabled = true;
            model.SelectedRuntimeKind = model.RuntimeKinds[1];
        });

        Assert.True(Saved(services).Developer.Enabled);
        Assert.True(model.ShowCliFields);
        Assert.False(model.ShowContainerFields);
        Assert.True(model.ShowRuntimeForm);

        UiThread.Run(() => model.SelectedRuntimeKind = model.RuntimeKinds[2]);
        Assert.True(model.ShowContainerFields);
        Assert.False(model.ShowCliFields);
    }

    [Fact]
    public void A_valid_cli_choice_is_saved_and_says_a_restart_is_needed_and_a_new_page_shows_it()
    {
        var services = Create();
        var cli = _temp.WriteFile("defenseclaw.exe", "stub");
        var home = _temp.File("next-home");
        var model = Build(services);

        UiThread.Run(() =>
        {
            model.DeveloperEnabled = true;
            model.SelectedRuntimeKind = model.RuntimeKinds[1];
            model.RuntimeCliPath = cli;
            model.RuntimeHomeDirectory = home;
            model.RuntimeGatewayUrl = "http://127.0.0.1:18972";
            model.ApplyRuntime();
        });

        Assert.False(model.HasRuntimeProblem);
        Assert.Contains("Restart", model.RuntimeMessage, StringComparison.Ordinal);
        var saved = Saved(services).Developer;
        Assert.True(saved.Enabled);
        Assert.Equal(RuntimeKind.Cli, saved.Kind);
        Assert.Equal(cli, saved.CliPath);
        Assert.Equal(home, saved.HomeDirectory);
        Assert.Equal("http://127.0.0.1:18972", saved.GatewayUrl);

        // The running app is still on the installed runtime until it restarts.
        Assert.Equal("Installed runtime", model.RuntimeInUseText);

        var again = Build(services);
        Assert.True(again.DeveloperEnabled);
        Assert.Equal(RuntimeKind.Cli, again.SelectedRuntimeKind.Kind);
        Assert.Equal(cli, again.RuntimeCliPath);
        Assert.Equal(home, again.RuntimeHomeDirectory);
        Assert.Equal("http://127.0.0.1:18972", again.RuntimeGatewayUrl);
    }

    [Fact]
    public void A_choice_that_does_not_validate_is_refused_with_the_reason_and_saves_nothing()
    {
        var services = Create();
        var model = Build(services);

        UiThread.Run(() =>
        {
            model.DeveloperEnabled = true;
            model.SelectedRuntimeKind = model.RuntimeKinds[2];
            model.RuntimeContainerName = "dc-next-1";
            model.RuntimeGatewayUrl = "http://example.com:18971"; // not on this PC
            model.RuntimeHostDataFolder = _temp.Path;
            model.ApplyRuntime();
        });

        Assert.True(model.HasRuntimeProblem);
        Assert.Contains("on this PC", model.RuntimeProblem, StringComparison.Ordinal);
        var saved = Saved(services).Developer;
        Assert.Equal(RuntimeKind.Installed, saved.Kind);
        Assert.Null(saved.ContainerName);
        Assert.Null(saved.GatewayUrl);
    }

    [Fact]
    public void A_valid_container_choice_round_trips_and_use_installed_turns_the_selector_off_keeping_the_fields()
    {
        var services = Create();
        var model = Build(services);

        UiThread.Run(() =>
        {
            model.DeveloperEnabled = true;
            model.SelectedRuntimeKind = model.RuntimeKinds[2];
            model.RuntimeContainerName = "dc-next-1";
            model.RuntimeGatewayUrl = "http://127.0.0.1:18971";
            model.RuntimeHostDataFolder = _temp.Path;
            model.ApplyRuntime();
        });

        var saved = Saved(services).Developer;
        Assert.Equal(RuntimeKind.Container, saved.Kind);
        Assert.Equal("dc-next-1", saved.ContainerName);
        Assert.Equal(_temp.Path, saved.HostDataFolder);
        Assert.Equal(RuntimeKind.Container, saved.ToSelection().Kind);

        UiThread.Run(() => model.UseInstalledRuntimeCommand.Execute(null));

        var off = Saved(services).Developer;
        Assert.False(off.Enabled);
        Assert.Equal(RuntimeKind.Installed, off.Kind);
        Assert.Equal("dc-next-1", off.ContainerName);
        Assert.True(off.ToSelection().IsDefault);
        Assert.False(model.DeveloperEnabled);
        Assert.Equal(string.Empty, model.RuntimeMessage);
    }

    [Fact]
    public void A_saved_choice_that_has_since_become_unusable_says_the_installed_runtime_will_be_used()
    {
        var services = Create();
        Assert.True(services.Settings.Update(s => s with
        {
            Developer = new DeveloperSettings
            {
                Enabled = true,
                Kind = RuntimeKind.Cli,
                CliPath = System.IO.Path.Combine(_temp.File("gone"), "defenseclaw.exe"),
                HomeDirectory = _temp.File("home"),
            },
        }));

        var model = Build(services);

        Assert.Contains("installed runtime is used", model.RuntimeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void The_browse_buttons_fill_the_fields_and_a_cancelled_picker_changes_nothing()
    {
        var platform = new FakePlatform { Picked = @"D:\next\bin\defenseclaw.exe", PickedFolder = @"D:\next\home" };
        var model = Build(Create(), platform);

        UiThread.Run(() =>
        {
            model.BrowseRuntimeCliCommand.Execute(null);
            model.BrowseRuntimeHomeCommand.Execute(null);
        });

        Assert.Equal(@"D:\next\bin\defenseclaw.exe", model.RuntimeCliPath);
        Assert.Equal(@"D:\next\home", model.RuntimeHomeDirectory);

        platform.Picked = null;
        platform.PickedFolder = null;
        UiThread.Run(() =>
        {
            model.BrowseRuntimeCliCommand.Execute(null);
            model.BrowseRuntimeDataFolderCommand.Execute(null);
        });

        Assert.Equal(@"D:\next\bin\defenseclaw.exe", model.RuntimeCliPath);
        Assert.Equal(string.Empty, model.RuntimeHostDataFolder);
    }

    // ------------------------------------------------------------------ the running app follows the selection

    [Fact]
    public void A_container_selection_points_the_gateway_client_at_its_published_port_and_makes_the_data_folder_read_only()
    {
        var selection = RuntimeSelection.ForContainer("dc-next-1", "http://127.0.0.1:18971", _temp.Path);
        var paths = new DefenseClawPaths(
            dataDirectory: _temp.Path,
            binDirectory: _temp.File("no-such-bin"),
            searchPath: Array.Empty<string>(),
            runtime: selection);
        var services = Create(paths: paths);

        Assert.Equal(18971, services.ApiPort);
        Assert.Equal(RuntimeKind.Container, services.Runtime.Selection.Kind);
        Assert.True(services.Paths.DataDirectoryReadOnly);
    }

    [Fact]
    public async Task The_config_editor_never_writes_a_read_only_container_copy()
    {
        var yaml = _temp.WriteFile("config.yaml", "gateway:\n  api_port: 18970\n");
        var paths = new DefenseClawPaths(
            dataDirectory: _temp.Path,
            binDirectory: _temp.File("no-such-bin"),
            searchPath: Array.Empty<string>(),
            runtime: RuntimeSelection.ForContainer("dc-next-1", "http://127.0.0.1:18971", _temp.Path));
        var before = File.ReadAllText(yaml);

        var outcome = await new ConfigSaveService(paths, cli: null).SaveAsync("gateway:\n  api_port: 1\n", FileSignature.Capture(yaml));

        Assert.False(outcome.Success);
        Assert.Contains("read-only", outcome.Message, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllText(yaml));
        Assert.Equal(new[] { yaml }, Directory.GetFiles(_temp.Path, "config.yaml*"));
    }

    [Fact]
    public void The_default_gateway_port_is_still_the_one_in_config_yaml()
    {
        _ = _temp.WriteFile("config.yaml", "gateway:\n  api_port: 18123\n");
        var services = Create();

        Assert.Equal(18123, services.ApiPort);
        Assert.True(services.Runtime.Selection.IsDefault);
        Assert.False(services.Paths.DataDirectoryReadOnly);
    }

    // ------------------------------------------------------------------ the gate

    [Fact]
    public void Before_the_runtime_has_answered_every_gated_feature_is_closed_with_the_standard_sentence()
    {
        var services = Create(FixtureRunner("95159fd"));

        var gate = services.Runtime.Check(RuntimeCapability.AcpGuard);

        Assert.False(gate.IsAvailable);
        Assert.Equal(RuntimeCapabilityCatalog.UnsupportedMessage, gate.Reason);
        Assert.True(services.Runtime.Check(null).IsAvailable);
    }

    [Fact]
    public async Task Against_0_8_10_fixtures_nothing_new_appears()
    {
        var services = Create(FixtureRunner("0.8.10"));

        var snapshot = await services.Runtime.RefreshAsync();

        Assert.True(snapshot.IsKnown);
        Assert.Equal("0.8.10", snapshot.Identity!.Version);
        foreach (var capability in RuntimeCapabilityCatalog.All)
        {
            Assert.False(services.Runtime.Check(capability).IsAvailable, capability.ToString());
        }

        Assert.Equal("No features beyond the 0.8.10 baseline", RuntimeSummary.Features(snapshot));
    }

    [Fact]
    public async Task Against_the_pin_fixtures_every_capability_opens_and_Changed_fires_once_on_the_first_answer()
    {
        var services = Create(FixtureRunner("95159fd"));
        var raised = 0;
        services.Runtime.Changed += (_, _) => Interlocked.Increment(ref raised);

        var snapshot = await services.Runtime.RefreshAsync();

        Assert.Equal("1.0.0", snapshot.Identity!.Version);
        foreach (var capability in RuntimeCapabilityCatalog.All)
        {
            Assert.True(services.Runtime.Check(capability).IsAvailable, capability.ToString());
        }

        Assert.Equal(1, raised);
        Assert.StartsWith("defenseclaw-cli 1.0.0", RuntimeSummary.Identity(snapshot), StringComparison.Ordinal);
        Assert.Contains("Advanced redaction", RuntimeSummary.Features(snapshot), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_runtime_that_does_not_answer_keeps_everything_hidden()
    {
        var services = Create((_, _) => Task.FromResult(RuntimeProbeOutput.Fail("timed out")));

        var snapshot = await services.Runtime.RefreshAsync();

        Assert.False(snapshot.IsKnown);
        foreach (var capability in RuntimeCapabilityCatalog.All)
        {
            Assert.False(services.Runtime.Check(capability).IsAvailable);
        }

        Assert.Equal("Features unknown until the runtime answers", RuntimeSummary.Features(snapshot));
    }

    [Fact]
    public async Task About_lists_the_identity_and_one_row_per_capability()
    {
        var services = Create(FixtureRunner("95159fd"));
        var model = Build(services);

        Assert.All(model.RuntimeCapabilityRows, row => Assert.Equal("Unknown", row.Status));
        Assert.Equal(RuntimeCapabilityCatalog.All.Count, model.RuntimeCapabilityRows.Count);

        _ = await services.Runtime.RefreshAsync();
        UiThread.Run(() => model.RecheckRuntimeCommand.Execute(null));
        UiThread.WaitFor(() => model.RuntimeIdentityText.StartsWith("defenseclaw-cli 1.0.0", StringComparison.Ordinal), "the identity to show");

        Assert.All(model.RuntimeCapabilityRows, row => Assert.Equal("Available", row.Status));
        Assert.Contains(model.RuntimeCapabilityRows, row => row.Name == "ACP guard");
        Assert.Contains("Linux and macOS only", model.RuntimeCapabilityNotes, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_command_palette_row_of_a_panel_that_needs_a_capability_is_disabled_until_the_runtime_has_it()
    {
        var services = Create(FixtureRunner("95159fd"));
        var gated = new PanelDescriptor("acp", "ACP Guard", "Configure", SymbolRegular.ShieldSettings24, typeof(object), _ => null!, RuntimeCapability.AcpGuard);
        var plain = new PanelDescriptor("plain", "Plain", "Configure", SymbolRegular.ShieldSettings24, typeof(object), _ => null!);
        var catalog = new PanelCatalog(services, new[] { plain, gated });

        var before = ShellCommandRegistry.BuildPanelCommands(catalog, _ => { });
        Assert.True(before.Single(c => c.Id == "nav.plain").IsEnabled);
        var closed = before.Single(c => c.Id == "nav.acp");
        Assert.False(closed.IsEnabled);
        Assert.Equal(RuntimeCapabilityCatalog.UnsupportedMessage, closed.DisabledReason);

        _ = await services.Runtime.RefreshAsync();

        var after = ShellCommandRegistry.BuildPanelCommands(catalog, _ => { }).Single(c => c.Id == "nav.acp");
        Assert.True(after.IsEnabled);
        Assert.Null(after.DisabledReason);
    }

    [Fact]
    public void Every_panel_0_8_10_has_stays_enabled_on_an_unprobed_runtime_so_nothing_regresses()
    {
        var services = Create();
        var catalog = new PanelCatalog(services);

        var commands = ShellCommandRegistry.BuildPanelCommands(catalog, _ => { });

        Assert.NotEmpty(commands);
        var needsMore = catalog.Panels.Where(p => p.Requires is not null).Select(p => "nav." + p.Id).ToArray();
        Assert.Equal(new[] { "nav.ai-runtime" }, needsMore);
        Assert.All(commands.Where(c => !needsMore.Contains(c.Id)), command => Assert.True(command.IsEnabled, command.Title));

        // The one panel that needs a newer runtime (Runtime, CUST-309) is listed but disabled, with the standard sentence and no chord.
        var runtime = commands.Single(c => c.Id == "nav.ai-runtime");
        Assert.False(runtime.IsEnabled);
        Assert.Equal(RuntimeCapabilityCatalog.UnsupportedMessage, runtime.DisabledReason);
        Assert.Null(runtime.Shortcut);
    }

    [Fact]
    public async Task Against_0_8_10_fixtures_the_runtime_panel_stays_closed_in_every_door()
    {
        var services = Create(FixtureRunner("0.8.10"));
        _ = await services.Runtime.RefreshAsync();
        var catalog = new PanelCatalog(services);
        var runtime = catalog.ById("ai-runtime")!;

        Assert.False(catalog.IsOffered(runtime));
        Assert.Null(catalog.ChordFor(runtime));
        Assert.Null(catalog.PanelForChord(catalog.ChordOrder.ToList().IndexOf(runtime)));
        Assert.NotSame(runtime, catalog.InitialPanel);
        Assert.False(ShellCommandRegistry.BuildPanelCommands(catalog, _ => { }).Single(c => c.Id == "nav.ai-runtime").IsEnabled);
    }

    [Fact]
    public async Task Against_the_pin_fixtures_the_runtime_panel_opens_with_the_fifteenth_chord()
    {
        var services = Create(FixtureRunner("95159fd"));
        _ = await services.Runtime.RefreshAsync();
        var catalog = new PanelCatalog(services);
        var runtime = catalog.ById("ai-runtime")!;

        Assert.True(catalog.IsOffered(runtime));
        Assert.Equal("Ctrl+Shift+5", catalog.ChordFor(runtime));
        Assert.Same(runtime, catalog.PanelForChord(14));

        var row = ShellCommandRegistry.BuildPanelCommands(catalog, _ => { }).Single(c => c.Id == "nav.ai-runtime");
        Assert.True(row.IsEnabled);
        Assert.Equal("Ctrl+Shift+5", row.Shortcut);
    }
}
