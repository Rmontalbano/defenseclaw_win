using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Appearance;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.Appearance;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Panels;
using DefenseClaw.App.Views.Shell;
using DefenseClaw.Core.AiRuntime;
using DefenseClaw.Core.Gateway.Models;
using Wpf.Ui.Controls;

namespace DefenseClaw.App.Tests.Runtime;

/// <summary>
/// The Runtime monitoring card (CUST-348) as the operator sees it: the Setup tile, the dialog laid out offscreen with the three planes and their requirements,
/// the review on top of it, and the 0.8.10 explanation. Pictures are written only when <c>DC_RENDER_DIR</c> names a folder. No process starts.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class RuntimeMonitoringViewTests : IDisposable
{
    private const string EnableHelp = """
        Usage: defenseclaw agent discovery runtime enable [OPTIONS]

        Options:
          --enable-host-plane / --no-enable-host-plane
                                          Kernel process, file, and identity events.
          --dns-capture / --no-dns-capture
                                          Passive DNS observation.
          --restart / --no-restart        Restart the gateway to apply.
          --yes                           Skip the confirmation prompt.
          --help                          Show this message and exit.
        """;

    private const string DisableHelp = """
        Usage: defenseclaw agent discovery runtime disable [OPTIONS]

        Options:
          --restart / --no-restart  Restart the gateway to apply.
          --yes                     Skip the confirmation prompt.
          --help                    Show this message and exit.
        """;

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

    private static string Fixture(params string[] path) =>
        File.ReadAllText(Path.Combine([AppContext.BaseDirectory, "Fixtures", .. path]));

    private static GatewaySnapshot Running(string health) => GatewaySnapshot.Initial with
    {
        State = AppGatewayState.Running,
        Health = JsonSerializer.Deserialize<GatewayHealth>(Fixture("runtime-1.0.0", "rest", health), new JsonSerializerOptions(JsonSerializerDefaults.Web)),
    };

    private async Task<AppServices> ServicesAsync(string runtime, string yaml)
    {
        _ = _temp.WriteFile("config.yaml", yaml);
        var services = AppServices.CreateIsolated(
            TestServices.IsolatedPaths(_temp.Path),
            claudeSettingsPath: _temp.File("claude-settings.json"),
            runtimeProbeRunner: RuntimeFixtureRunner.For(runtime));
        _services.Add(services);
        _ = await services.Runtime.RefreshAsync();
        return services;
    }

    private sealed class Scope : IDisposable
    {
        public Scope(AppServices services, string health, int height = 1500)
        {
            Vm = new RuntimeMonitoringViewModel(
                services,
                probes: new Probes(),
                helpReader: (path, _) => Task.FromResult(new HelpProbeResult(path[^1] == "disable" ? DisableHelp : EnableHelp, null)))
            {
                SnapshotReader = () => Running(health),
            };
            Dialog = new RuntimeMonitoringDialog { DataContext = Vm };
            Host = new OffscreenHost(Dialog, 940, height);
        }

        public RuntimeMonitoringViewModel Vm { get; }

        public RuntimeMonitoringDialog Dialog { get; }

        public OffscreenHost Host { get; }

        public void Dispose()
        {
            Host.Dispose();
            Vm.Dispose();
        }
    }

    private sealed class Probes : IPlaneCProbes
    {
        public ProbeReading<TokenElevation> ReadElevation() => throw new InvalidOperationException();

        public ProbeReading<bool> ReadEventLogReadersMembership() => throw new InvalidOperationException();

        public ProbeReading<bool> ReadSecurityChannelOpens() => throw new InvalidOperationException();

        public ProbeReading<int?> ReadCommandLineAudit() => ProbeReading<int?>.Ok(0);
    }

    private static IEnumerable<string> Names(DependencyObject root) =>
        VisualTree.Descendants<DependencyObject>(root).Select(AutomationProperties.GetName).Where(n => n.Length > 0);

    private static Wpf.Ui.Controls.Button Button(DependencyObject root, string content) =>
        VisualTree.Descendants<Wpf.Ui.Controls.Button>(root).Single(b => b.Content?.ToString() == content);

    private static void OpenAndSettle(Scope scope)
    {
        scope.Vm.Open();
        UiThread.WaitFor(() => scope.Vm.IsHelpReady, "the help check to finish");
        scope.Host.Relayout();
    }

    [Fact]
    public async Task A_closed_dialog_shows_nothing()
    {
        var services = await ServicesAsync("95159fd", "config_version: 9\n");
        UiThread.Run(() =>
        {
            using var scope = new Scope(services, "health.ai-runtime.ab-running.synthetic.json");
            Assert.DoesNotContain(VisualTree.Descendants<ToggleSwitch>(scope.Dialog), t => t.IsVisible);
            Assert.DoesNotContain(VisualTree.Descendants<CommandReviewControl>(scope.Dialog), c => c.IsVisible);
        });
    }

    [Theory]
    [InlineData("Cisco", "Light")]
    [InlineData("Linear", "Dark")]
    public async Task The_open_card_lists_three_planes_with_their_requirements_and_three_switches(string styleName, string modeName)
    {
        var services = await ServicesAsync("95159fd", Fixture("runtime-1.0.0", "config", "config.v9.runtime.synthetic.yaml"));
        using var appearance = new AppearanceFixture(new AppearanceSettings(Enum.Parse<AppearanceStyle>(styleName), Enum.Parse<AppearanceMode>(modeName)));

        UiThread.Run(() =>
        {
            using var scope = new Scope(services, "health.ai-runtime.c-blind.synthetic.json", 2200);
            OpenAndSettle(scope);

            var names = Names(scope.Dialog).ToArray();
            Assert.Contains("The three runtime planes", names);
            Assert.Contains(names, n => n.StartsWith("A · inference heartbeat: Running", StringComparison.Ordinal));
            Assert.Contains(names, n => n.StartsWith("C · agent actions: Not running", StringComparison.Ordinal));
            Assert.Contains(names, n => n.StartsWith("Not available yet: Needs Run gateway as administrator", StringComparison.Ordinal));
            Assert.Contains(names, n => n.StartsWith("Can't check: Advanced Audit Policy", StringComparison.Ordinal));

            var toggles = VisualTree.Descendants<ToggleSwitch>(scope.Dialog).Where(t => t.IsVisible).ToList();
            Assert.Equal(3, toggles.Count);
            Assert.All(toggles, t => Assert.True(t.IsEnabled));

            Assert.False(Button(scope.Dialog, "Review changes…").IsEnabled);
            RenderTo.Png(scope.Host, $"cust348a-runtime-monitoring-{styleName}-{modeName}");
        });
    }

    [Fact]
    public async Task A_moved_switch_enables_review_and_the_review_shows_the_exact_command_on_top()
    {
        var services = await ServicesAsync("95159fd", "config_version: 9\nai_discovery:\n  enabled: true\n");
        using var appearance = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Cisco, AppearanceMode.Light));

        UiThread.Run(() =>
        {
            using var scope = new Scope(services, "health.ai-runtime.ab-running.synthetic.json", 1900);
            OpenAndSettle(scope);
            Assert.False(scope.Vm.MonitoringOn);

            scope.Vm.MonitoringOn = true;
            scope.Host.Relayout();
            Assert.True(Button(scope.Dialog, "Review changes…").IsEnabled);

            scope.Vm.ReviewChangesCommand.Execute(null);
            scope.Host.Relayout();

            var control = Assert.Single(VisualTree.Descendants<CommandReviewControl>(scope.Dialog), c => c.IsVisible);
            Assert.Equal(
                new[] { "defenseclaw agent discovery runtime enable --yes --no-enable-host-plane" },
                control.Review!.Steps.Select(s => s.CommandText));
            RenderTo.Png(scope.Host, "cust348a-runtime-monitoring-review-Cisco-Light");
        });
    }

    [Fact]
    public async Task On_0_8_10_the_dialog_says_it_needs_1_0_and_every_switch_is_off()
    {
        var services = await ServicesAsync("0.8.10", "config_version: 8\n");
        using var appearance = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Cisco, AppearanceMode.Light));

        UiThread.Run(() =>
        {
            using var scope = new Scope(services, "health.ai-runtime.ab-running.synthetic.json", 1500);
            scope.Vm.Open();
            scope.Host.Relayout();

            Assert.Contains(VisualTree.Descendants<InfoBar>(scope.Dialog), bar => bar.IsOpen && bar.Message == RuntimeMonitoringViewModel.UnsupportedText);
            Assert.All(VisualTree.Descendants<ToggleSwitch>(scope.Dialog).Where(t => t.IsVisible), t => Assert.False(t.IsEnabled));
            Assert.False(Button(scope.Dialog, "Review changes…").IsEnabled);
            RenderTo.Png(scope.Host, "cust348a-runtime-monitoring-0.8.10-Cisco-Light");
        });
    }

    [Fact]
    public async Task The_setup_hub_has_a_tile_on_1_0_that_opens_the_card_and_none_on_0_8_10()
    {
        foreach (var (runtime, shown) in new[] { ("95159fd", true), ("0.8.10", false) })
        {
            var services = await ServicesAsync(runtime, "config_version: 9\n");
            var shell = UiThread.Run(() => new PanelShell(services, 1200, 900));
            try
            {
                UiThread.Run(() => shell.Show<SetupPanel>());
                var vm = UiThread.Run(() => (SetupPanelViewModel)shell.ViewModel);
                UiThread.WaitFor(() => !vm.IsLoading, "the setup catalog read to finish");

                UiThread.Run(() =>
                {
                    shell.Host.Relayout();
                    var tiles = VisualTree.Descendants<System.Windows.Controls.Button>(shell.Page!)
                        .Where(b => b.IsVisible && AutomationProperties.GetName(b) == "Runtime monitoring")
                        .ToArray();
                    Assert.Equal(shown ? 1 : 0, tiles.Length);

                    if (shown)
                    {
                        Assert.True(tiles[0].Command.CanExecute(null));
                        tiles[0].Command.Execute(null);
                        Assert.True(vm.Monitoring.IsOpen);
                        vm.Monitoring.Dispose();
                    }
                });
            }
            finally
            {
                UiThread.Run(shell.Dispose);
            }
        }
    }
}
