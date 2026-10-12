using System.Text.Json;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.AiRuntime;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Gateway.Models;
using DefenseClaw.Core.Paths;

namespace DefenseClaw.App.Tests.Runtime;

/// <summary>
/// The Runtime monitoring card (CUST-348) over a scratch composition, a script for the CLI and synthetic config.yaml / <c>/health</c> documents written from the
/// 1.0.0 source (<c>Fixtures/runtime-1.0.0</c>): the plane state from config and health, the exact argv of each switch, the review before anything runs, the
/// installation guard first, and the 0.8.10 gate. No process starts and no registry or machine read is real.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class RuntimeMonitoringViewModelTests : IDisposable
{
    private const string Ab = "config_version: 9\nai_discovery:\n  runtime:\n    enabled: true\n    planes: [a, b]\n";
    private const string Off = "config_version: 9\nai_discovery:\n  enabled: true\n";

    /// <summary>The help screen of <c>agent discovery runtime enable</c>, laid out as Click prints the options in cmd_agent.py's <c>runtime_enable</c>.</summary>
    private const string EnableHelp = """
        Usage: defenseclaw agent discovery runtime enable [OPTIONS]

          Turn on the runtime planes.

        Options:
          --enable-host-plane / --no-enable-host-plane
                                          Kernel process, file, and identity events.
                                          Needs elevated privilege.
          --dns-capture / --no-dns-capture
                                          Passive DNS observation so peers are named
                                          rather than inferred.
          --poll-interval-s INTEGER RANGE
                                          Seconds between polls.
          --min-risk-to-report INTEGER RANGE
                                          Reporting floor.
          --restart / --no-restart        Restart the gateway to apply.  [default: restart]
          --yes                           Skip the confirmation prompt.
          --help                          Show this message and exit.
        """;

    private const string DisableHelp = """
        Usage: defenseclaw agent discovery runtime disable [OPTIONS]

          Turn off the runtime planes. Recorded findings are preserved.

        Options:
          --restart / --no-restart  Restart the gateway to apply.  [default: restart]
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

    private static GatewaySnapshot Running(string healthFixture) => GatewaySnapshot.Initial with
    {
        State = AppGatewayState.Running,
        Health = JsonSerializer.Deserialize<GatewayHealth>(Fixture("runtime-1.0.0", "rest", healthFixture), new JsonSerializerOptions(JsonSerializerDefaults.Web)),
    };

    private static Task<HelpProbeResult> Screens(IReadOnlyList<string> path, CancellationToken _) =>
        Task.FromResult(new HelpProbeResult(path[^1] == "disable" ? DisableHelp : EnableHelp, null));

    private sealed class Script
    {
        public List<string> Ran { get; } = new();

        public Task<CliInvocation> Step(string executable, IReadOnlyList<string> argv, CliRunOptions? options)
        {
            Ran.Add(executable + " " + string.Join(' ', argv));
            var invocation = InvocationFactory.Create(retainFullOutput: true, argv.ToArray());
            InvocationFactory.Finish(invocation, 0);
            return Task.FromResult(invocation);
        }
    }

    private sealed class FakeElevation : IGatewayElevationStatus
    {
        private GatewayElevation _current;

        public GatewayElevation Current
        {
            get => _current;
            set
            {
                _current = value;
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }

        public event EventHandler? Changed;
    }

    private sealed class FakeProbes : IPlaneCProbes
    {
        public ProbeReading<int?> CommandLine { get; set; } = ProbeReading<int?>.Ok(1);

        public int Reads { get; private set; }

        public ProbeReading<TokenElevation> ReadElevation() => throw new InvalidOperationException("The card must not read the app's token.");

        public ProbeReading<bool> ReadEventLogReadersMembership() => throw new InvalidOperationException("The card must not read group membership.");

        public ProbeReading<bool> ReadSecurityChannelOpens() => throw new InvalidOperationException("The card must not open the Security log.");

        public ProbeReading<int?> ReadCommandLineAudit()
        {
            Reads++;
            return CommandLine;
        }
    }

    private async Task<(RuntimeMonitoringViewModel Vm, AppServices Services, Script Cli, FakeProbes Probes)> DialogAsync(
        string yaml,
        string health = "health.ai-runtime.ab-running.synthetic.json",
        string runtime = "95159fd",
        InstallationContext? installation = null,
        IGatewayElevationStatus? elevation = null)
    {
        _ = _temp.WriteFile("config.yaml", yaml);
        var services = AppServices.CreateIsolated(
            TestServices.IsolatedPaths(_temp.Path, installation),
            claudeSettingsPath: _temp.File("claude-settings.json"),
            runtimeProbeRunner: RuntimeFixtureRunner.For(runtime),
            readerTimeouts: TestServices.ReaderTimeouts);
        _services.Add(services);
        _ = await services.Runtime.RefreshAsync();

        var cli = new Script();
        var probes = new FakeProbes();
        var vm = new RuntimeMonitoringViewModel(services, elevation, probes, Screens)
        {
            SnapshotReader = () => Running(health),
        };
        vm.Review.RunStep = cli.Step;
        return (vm, services, cli, probes);
    }

    private static async Task<RuntimeMonitoringViewModel> OpenedAsync(RuntimeMonitoringViewModel vm)
    {
        vm.Open();
        var until = DateTime.UtcNow + TestTimeouts.Ceiling;
        while (!vm.IsHelpReady && DateTime.UtcNow < until)
        {
            await Task.Delay(10);
        }

        Assert.True(vm.IsHelpReady, vm.HelpProblem);
        await vm.CommandLineAuditRead;
        return vm;
    }

    // ---- state ----

    [Fact]
    public async Task Opening_on_1_0_reads_the_planes_from_config_and_health_and_nothing_has_changed()
    {
        var (vm, _, cli, probes) = await DialogAsync(Ab);

        await OpenedAsync(vm);
        await vm.CommandLineAuditRead;

        Assert.True(vm.IsSupported);
        Assert.True(vm.MonitoringOn);
        Assert.False(vm.HostPlaneOn);
        Assert.False(vm.DnsCaptureOn);
        Assert.Equal(new[] { "A · inference heartbeat", "B · shadow egress", "C · agent actions" }, vm.Planes.Select(p => p.Name));
        Assert.Equal(new[] { "Running", "Running", "Off" }, vm.Planes.Select(p => p.RunLabel));
        Assert.Equal("Runtime monitoring is on in config.yaml.", vm.Headline);
        Assert.Equal("config.yaml: config_version 9, ai_discovery.runtime present.", vm.ConfigLine);
        Assert.Equal("Gateway reports runtime monitoring: running.", vm.GatewayLine);
        Assert.False(vm.HasChanges);
        Assert.Equal("Nothing to apply", vm.Summary);
        Assert.Equal("Nothing to apply: no switch differs from config.yaml.", vm.ReviewBlockedReason);
        Assert.Empty(cli.Ran);

        // The only machine read is the registry value, and it came through the probe seam.
        await WaitUntilAsync(() => probes.Reads == 1);
    }

    [Fact]
    public async Task Plane_c_shows_needs_run_gateway_as_administrator_greyed_while_the_mode_does_not_exist()
    {
        var (vm, _, _, _) = await DialogAsync(Fixture("runtime-1.0.0", "config", "config.v9.runtime.synthetic.yaml"), "health.ai-runtime.c-blind.synthetic.json");

        await OpenedAsync(vm);

        var c = vm.Planes.Single(p => p.Name.StartsWith("C", StringComparison.Ordinal));
        var elevated = c.Requirements.Single(r => r.Text.StartsWith("Needs Run gateway as administrator", StringComparison.Ordinal));
        Assert.True(elevated.IsGreyed);
        Assert.Equal("Not available yet", elevated.Label);
        var audit = c.Requirements.Single(r => r.Text.StartsWith("Advanced Audit Policy", StringComparison.Ordinal));
        Assert.Equal("Can't check", audit.Label);
        Assert.Contains("can't check without admin", audit.Text, StringComparison.Ordinal);
        Assert.Equal("Not running", c.RunLabel);
        Assert.Contains("an ETW session needs an elevated token", c.RunText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_elevation_hook_changes_the_card_when_the_administrator_mode_reports()
    {
        var elevation = new FakeElevation();
        var (vm, _, _, _) = await DialogAsync(Fixture("runtime-1.0.0", "config", "config.v9.runtime.synthetic.yaml"), "health.ai-runtime.c-blind.synthetic.json", elevation: elevation);
        await OpenedAsync(vm);

        elevation.Current = GatewayElevation.NotElevated;
        UiThread.WaitFor(
            () => vm.Planes[2].Requirements.Any(r => r.Text.StartsWith("Needs Run gateway as administrator", StringComparison.Ordinal) && r.Label == "Missing"),
            "the card to follow the gateway running without administrator rights");

        elevation.Current = GatewayElevation.Elevated;
        UiThread.WaitFor(
            () => vm.Planes[2].Requirements.Any(r => r.Text.StartsWith("The gateway runs as administrator", StringComparison.Ordinal) && r.Label == "In place" && !r.IsGreyed),
            "the card to follow the gateway running as administrator");
        // The audit policy is still not claimed: elevation of the gateway does not let this app read it.
        Assert.Equal("Can't check", vm.Planes[2].Requirements.Single(r => r.Text.StartsWith("Advanced Audit Policy", StringComparison.Ordinal)).Label);
    }

    // ---- changes: the exact argv, reviewed before it runs ----

    [Fact]
    public async Task Turning_monitoring_on_reviews_the_enable_command_and_runs_it_only_when_confirmed()
    {
        var (vm, _, cli, _) = await DialogAsync(Off);
        await OpenedAsync(vm);
        Assert.False(vm.MonitoringOn);

        vm.MonitoringOn = true;

        Assert.Equal(new[] { "Runtime monitoring: off → on (planes A and B)." }, vm.ChangeLines);
        Assert.Null(vm.ReviewBlockedReason);
        vm.ReviewChangesCommand.Execute(null);

        Assert.True(vm.Review.IsConfirming);
        Assert.Empty(cli.Ran);
        var review = vm.Review.CommandReview!;
        Assert.Equal("Turn on runtime monitoring?", review.Title);
        Assert.Equal(new[] { "defenseclaw agent discovery runtime enable --yes --no-enable-host-plane" }, review.Steps.Select(s => s.CommandText));
        Assert.True(review.RestartsGateway);
        Assert.Equal("Turn on", review.ConfirmLabel);

        await vm.Review.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "defenseclaw agent discovery runtime enable --yes --no-enable-host-plane" }, cli.Ran);
    }

    [Fact]
    public async Task Plane_c_and_dns_capture_are_chosen_with_monitoring_and_warned_about_in_the_review()
    {
        var (vm, _, cli, _) = await DialogAsync(Off);
        await OpenedAsync(vm);
        Assert.False(vm.CanEditPlaneSwitches);

        vm.MonitoringOn = true;
        Assert.True(vm.CanEditPlaneSwitches);
        vm.HostPlaneOn = true;
        vm.DnsCaptureOn = true;
        vm.ReviewChangesCommand.Execute(null);

        var review = vm.Review.CommandReview!;
        Assert.Equal(
            new[] { "defenseclaw agent discovery runtime enable --yes --enable-host-plane --dns-capture" },
            review.Steps.Select(s => s.CommandText));
        Assert.Contains(review.Warnings, w => w.Title == "Plane C reads the Security log");
        Assert.Contains(review.Warnings, w => w.Title == "DNS capture records domains");
        Assert.Empty(cli.Ran);
    }

    [Fact]
    public async Task Turning_plane_c_off_while_monitoring_stays_on_sends_only_the_host_plane_flag()
    {
        var (vm, _, cli, _) = await DialogAsync(Fixture("runtime-1.0.0", "config", "config.v9.runtime.synthetic.yaml"), "health.ai-runtime.c-blind.synthetic.json");
        await OpenedAsync(vm);
        Assert.True(vm.HostPlaneOn);
        Assert.True(vm.DnsCaptureOn);

        vm.HostPlaneOn = false;
        vm.ReviewChangesCommand.Execute(null);
        await vm.Review.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "defenseclaw agent discovery runtime enable --yes --no-enable-host-plane" }, cli.Ran);
    }

    [Fact]
    public async Task Turning_monitoring_off_is_the_disable_command()
    {
        var (vm, _, cli, _) = await DialogAsync(Ab);
        await OpenedAsync(vm);

        vm.MonitoringOn = false;
        vm.ReviewChangesCommand.Execute(null);

        var review = vm.Review.CommandReview!;
        Assert.Equal("Turn off runtime monitoring?", review.Title);
        Assert.Equal(new[] { "defenseclaw agent discovery runtime disable --yes" }, review.Steps.Select(s => s.CommandText));
        Assert.Equal("Turn off", review.ConfirmLabel);

        await vm.Review.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "defenseclaw agent discovery runtime disable --yes" }, cli.Ran);
    }

    [Fact]
    public async Task Unticking_the_restart_adds_no_restart_and_says_the_gateway_is_not_restarted()
    {
        var (vm, _, cli, _) = await DialogAsync(Ab);
        await OpenedAsync(vm);

        vm.MonitoringOn = false;
        vm.RestartAfter = false;
        vm.ReviewChangesCommand.Execute(null);

        var review = vm.Review.CommandReview!;
        Assert.Equal(new[] { "defenseclaw agent discovery runtime disable --yes --no-restart" }, review.Steps.Select(s => s.CommandText));
        Assert.False(review.RestartsGateway);
        Assert.Contains(review.Warnings, w => w.Title == "Gateway not restarted");
        Assert.Empty(cli.Ran);
    }

    [Fact]
    public async Task A_declined_review_runs_nothing_and_the_switches_stay_as_set()
    {
        var (vm, _, cli, _) = await DialogAsync(Off);
        await OpenedAsync(vm);
        vm.MonitoringOn = true;
        vm.ReviewChangesCommand.Execute(null);

        vm.Review.DismissCommand.Execute(null);

        Assert.False(vm.Review.IsOpen);
        Assert.Empty(cli.Ran);
        Assert.True(vm.MonitoringOn);
    }

    [Fact]
    public async Task A_flag_the_installed_cli_does_not_list_is_never_sent()
    {
        var (vm, _, _, _) = await DialogAsync(Ab);
        vm.HelpReader = (path, _) => Task.FromResult(new HelpProbeResult(
            path[^1] == "disable" ? DisableHelp : EnableHelp.Replace("--enable-host-plane / --no-enable-host-plane", "--something-else", StringComparison.Ordinal), null));

        await OpenedAsync(vm);

        Assert.False(vm.HostPlaneAvailable);
        Assert.True(vm.DnsAvailable);
    }

    // ---- guards ----

    [Fact]
    public async Task A_managed_installation_refuses_every_change_with_its_sentence_first()
    {
        var (vm, _, cli, _) = await DialogAsync(Off, installation: TestInstallations.Managed(_temp.Path));
        await OpenedAsync(vm);

        vm.MonitoringOn = true;

        Assert.Equal(TestInstallations.ManagedReason, vm.InstallationBlockedReason);
        Assert.Equal(TestInstallations.ManagedReason, vm.ReviewBlockedReason);
        Assert.False(vm.CanEdit);
        Assert.False(vm.ReviewChangesCommand.CanExecute(null));
        vm.ReviewChangesCommand.Execute(null);
        Assert.False(vm.Review.IsOpen);
        Assert.Empty(cli.Ran);
    }

    [Fact]
    public async Task The_installation_sentence_comes_before_the_runtime_gate_on_0_8_10()
    {
        var (vm, _, cli, _) = await DialogAsync(Off, runtime: "0.8.10", installation: TestInstallations.Managed(_temp.Path));
        await OpenedAsync(vm);

        Assert.Equal(TestInstallations.ManagedReason, vm.ReviewBlockedReason);
        Assert.Empty(cli.Ran);
    }

    [Fact]
    public async Task A_stale_state_turns_changes_off_until_refreshed()
    {
        var (vm, _, cli, _) = await DialogAsync(Off);
        await OpenedAsync(vm);
        vm.MonitoringOn = true;
        Assert.Null(vm.ReviewBlockedReason);

        // Another tool rewrites config.yaml after the card was read.
        File.WriteAllText(_temp.File("config.yaml"), Ab);
        File.SetLastWriteTimeUtc(_temp.File("config.yaml"), DateTime.UtcNow.AddMinutes(5));

        vm.ReviewChangesCommand.Execute(null);

        Assert.False(vm.Review.IsOpen);
        Assert.NotNull(vm.ReviewBlockedReason);
        Assert.Empty(cli.Ran);
    }

    // ---- 0.8.10 ----

    [Fact]
    public async Task On_0_8_10_the_card_is_unsupported_and_offers_nothing()
    {
        var (vm, services, cli, _) = await DialogAsync(Off, runtime: "0.8.10");
        await OpenedAsync(vm);

        Assert.False(vm.IsSupported);
        Assert.True(vm.HasSupportProblem);
        Assert.Equal(RuntimeMonitoringViewModel.UnsupportedText, vm.Support);
        Assert.Contains("Requires a compatible DefenseClaw runtime", vm.Support, StringComparison.Ordinal);
        Assert.False(vm.CanEditPlaneSwitches);
        Assert.Equal(vm.Support, vm.ReviewBlockedReason);
        Assert.False(vm.ReviewChangesCommand.CanExecute(null));
        Assert.Empty(cli.Ran);

        // The Setup tile is hidden entirely.
        var setup = new SetupPanelViewModel(services);
        Assert.False(setup.HasRuntimeMonitoringTile);
        Assert.False(setup.OpenRuntimeMonitoringTileCommand.CanExecute(null));
    }

    [Fact]
    public async Task On_1_0_the_setup_tile_is_there_and_the_overview_link_opens_the_card()
    {
        var (_, services, _, _) = await DialogAsync(Ab);
        var setup = new SetupPanelViewModel(services);

        Assert.True(setup.HasRuntimeMonitoringTile);
        Assert.True(setup.OpenRuntimeMonitoringTileCommand.CanExecute(null));

        setup.Accept(new RuntimeMonitoringLink());
        Assert.True(setup.Monitoring.IsOpen);
        setup.Monitoring.Dispose();
    }

    // ---- the Overview row ----

    [Fact]
    public async Task The_overview_row_summarises_the_planes_on_1_0_and_is_absent_on_0_8_10()
    {
        var (_, services, _, _) = await DialogAsync(Ab);
        var (vm, shown) = UiThread.Run(() =>
        {
            var overview = new OverviewPanelViewModel(services);
            overview.Apply(Running("health.ai-runtime.ab-running.synthetic.json"));
            return (overview, (overview.ShowRuntimeMonitoring, overview.RuntimeMonitoringText));
        });

        Assert.True(shown.ShowRuntimeMonitoring);
        Assert.Equal("Runtime monitoring: on · A running · B running · C off", shown.RuntimeMonitoringText);
        Assert.NotNull(vm.OpenRuntimeMonitoringCommand);

        using var oldTemp = new TempDirectory();
        using var old = AppServices.CreateIsolated(
            TestServices.IsolatedPaths(oldTemp.Path),
            claudeSettingsPath: oldTemp.File("claude-settings.json"),
            runtimeProbeRunner: RuntimeFixtureRunner.For("0.8.10"));
        _ = await old.Runtime.RefreshAsync();
        var hidden = UiThread.Run(() =>
        {
            var overview = new OverviewPanelViewModel(old);
            overview.Apply(Running("health.ai-runtime.ab-running.synthetic.json"));
            return (overview.ShowRuntimeMonitoring, overview.RuntimeMonitoringText);
        });
        Assert.False(hidden.ShowRuntimeMonitoring);
        Assert.Equal(string.Empty, hidden.RuntimeMonitoringText);
    }

    [Fact]
    public async Task The_overview_link_sends_the_setup_panel_a_runtime_monitoring_request()
    {
        var (_, services, _, _) = await DialogAsync(Ab);
        NavigationRequest? seen = null;
        services.Navigation.Requested += (_, e) => seen = e.Request;

        UiThread.Run(() =>
        {
            var overview = new OverviewPanelViewModel(services);
            overview.OpenRuntimeMonitoringCommand.Execute(null);
        });

        Assert.Equal("setup", seen?.PanelId);
        Assert.IsType<RuntimeMonitoringLink>(seen?.Payload);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow + TestTimeouts.Ceiling;
        while (!condition() && DateTime.UtcNow < until)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }
}
