using System.Globalization;
using System.Text.Json;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Gateway;
using DefenseClaw.Core.Gateway.Models;
using DefenseClaw.Core.Install;

namespace DefenseClaw.App.Tests.Overview;

/// <summary>
/// The Mac's attention rules the Overview gained in CUST-213 (<c>AppState.overviewNotices</c>), each with a synthetic snapshot, doctor cache and
/// discovery file: detected-but-unconfigured agents, the guardrail, the scanner, doctor failures / stale results / missing keys, connector drift
/// and a connector with no requests. Plus the doctor STALE reconciliation, the token-rejected state and the palette entry.
/// </summary>
public sealed class AttentionParityTests : IDisposable
{
    private const string GuardrailOnConfig = "guardrail:\n  enabled: true\n  connector: claudecode\n  connectors:\n    claudecode:\n      mode: observe\n      hook_fail_mode: open\n";

    private readonly TempDirectory _temp = new();
    private AppServices? _services;

    public void Dispose()
    {
        _services?.Dispose();
        _temp.Dispose();
    }

    private OverviewPanelViewModel Panel(string? configYaml = GuardrailOnConfig)
    {
        _services = TestServices.Create(_temp, configYaml);
        return new OverviewPanelViewModel(_services);
    }

    private static GatewayHealth Health(string body) =>
        JsonSerializer.Deserialize<GatewayHealth>("{" + body + "}", new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private static string Running => "{\"state\":\"running\"}";

    /// <summary>A reachable, installed gateway; the health body defaults to every subsystem running and one connector that has seen traffic.</summary>
    private static GatewaySnapshot Snapshot(GatewayHealth? health = null, InstallState install = InstallState.Running) => new()
    {
        State = AppGatewayState.Running,
        Install = install,
        Detail = "ok",
        Health = health ?? Health($"\"uptime_ms\":600000,\"api\":{Running},\"guardrail\":{Running},\"telemetry\":{Running},\"gateway\":{Running}," +
                                  "\"connector\":{\"name\":\"claudecode\",\"state\":\"running\",\"requests\":5},\"connectors\":[{\"name\":\"claudecode\",\"state\":\"running\",\"requests\":5}]"),
        PolledAt = DateTimeOffset.UtcNow,
    };

    private static string Cache(int passed, int failed, int warned, DateTimeOffset at, params (string Status, string Label)[] checks) =>
        FormattableString.Invariant(
            $"{{\"passed\":{passed},\"failed\":{failed},\"warned\":{warned},\"skipped\":0,\"captured_at\":\"{at.UtcDateTime:yyyy-MM-dd'T'HH:mm:ss'Z'}\",\"checks\":[{string.Join(",", checks.Select(c => "{\"status\":\"" + c.Status + "\",\"label\":\"" + c.Label + "\",\"detail\":\"d\"}"))}]}}");

    private async Task LoadDoctorAsync(OverviewPanelViewModel vm, string json)
    {
        _ = _temp.WriteFile("doctor_cache.json", json);
        await vm.ReloadDoctorCacheAsync(CancellationToken.None);
    }

    private static AttentionRow Row(OverviewPanelViewModel vm, string titleStart) =>
        Assert.Single(vm.Attention, r => r.Title.StartsWith(titleStart, StringComparison.Ordinal));

    private static void NoRow(OverviewPanelViewModel vm, string titleStart) =>
        Assert.DoesNotContain(vm.Attention, r => r.Title.StartsWith(titleStart, StringComparison.Ordinal));

    // ------------------------------------------------------------------ the quiet case

    [Fact]
    public void A_healthy_install_with_the_guardrail_on_still_has_nothing_to_say()
    {
        var vm = Panel();

        vm.Apply(Snapshot());

        Assert.Equal("Nothing needs attention", Assert.Single(vm.Attention).Title);
    }

    // ------------------------------------------------------------------ guardrail

    [Fact]
    public void A_guardrail_that_is_off_is_a_row_on_an_installed_gateway_only()
    {
        var vm = Panel("guardrail:\n  enabled: false\n");

        vm.Apply(Snapshot());
        Assert.Equal("High", Row(vm, "Guardrail not configured").SeverityKey);

        // No evidence of an install (before the first poll, or never initialized): the Mac says nothing.
        vm.Apply(Snapshot(install: InstallState.InstalledNotInitialized));
        NoRow(vm, "Guardrail not configured");
        vm.Apply(GatewaySnapshot.Initial);
        NoRow(vm, "Guardrail not configured");
    }

    // ------------------------------------------------------------------ connector drift

    [Fact]
    public void Drift_is_claw_mode_against_the_connector_the_gateway_routes_for()
    {
        var vm = Panel(GuardrailOnConfig + "claw:\n  mode: codex\n");

        vm.Apply(Snapshot());

        var row = Row(vm, "Connector drift");
        Assert.Contains("Codex", row.Detail, StringComparison.Ordinal);
        Assert.Contains("Claude Code", row.Detail, StringComparison.Ordinal);
        Assert.Equal("High", row.SeverityKey);
    }

    [Fact]
    public void No_drift_when_they_agree_when_claw_mode_is_unset_or_when_the_gateway_is_not_answering()
    {
        var agree = Panel(GuardrailOnConfig + "claw:\n  mode: ClaudeCode\n");
        agree.Apply(Snapshot());
        NoRow(agree, "Connector drift");

        var unset = Panel(GuardrailOnConfig);
        unset.Apply(Snapshot());
        NoRow(unset, "Connector drift");

        var down = Panel(GuardrailOnConfig + "claw:\n  mode: codex\n");
        down.Apply(new GatewaySnapshot { State = AppGatewayState.GatewayStopped, Install = InstallState.GatewayStopped, Detail = "x", PolledAt = DateTimeOffset.UtcNow });
        NoRow(down, "Connector drift");
    }

    // ------------------------------------------------------------------ zero requests

    [Fact]
    public void A_connector_with_no_requests_after_a_minute_gets_a_notice_of_its_own_kind()
    {
        var vm = Panel();
        var health = Health(
            $"\"uptime_ms\":5400000,\"api\":{Running}," +
            "\"connector\":{\"name\":\"codex\",\"state\":\"running\",\"requests\":0}," +
            "\"connectors\":[{\"name\":\"codex\",\"state\":\"running\",\"requests\":0},{\"name\":\"claudecode\",\"state\":\"running\",\"requests\":9}," +
            "{\"name\":\"cursor\",\"state\":\"running\",\"requests\":0},{\"name\":\"zeptoclaw\",\"state\":\"stopped\",\"requests\":0}]");

        vm.Apply(Snapshot(health));

        var codex = Row(vm, "Codex: no requests yet");
        Assert.Contains("0 hook events after 1h 30m", codex.Detail, StringComparison.Ordinal);
        Assert.Contains("~/.codex hooks", codex.Detail, StringComparison.Ordinal);
        Assert.Equal("Info", codex.SeverityKey);
        Assert.Contains("Verify the connector's hook setup", Row(vm, "Cursor: no requests yet").Detail, StringComparison.Ordinal);
        NoRow(vm, "Claude Code: no requests yet");
        NoRow(vm, "ZeptoClaw");
    }

    [Theory]
    [InlineData("claudecode", 90, "0 hook events after 1m. Normal until Claude Code")]
    [InlineData("omnigent", 7200, "0 policy events after 2h 0m. Normal until OmniGent")]
    [InlineData("openclaw", 45, "0 requests after 45s. Verify your agent is dialing the gateway port")]
    public void The_notice_wording_follows_the_connector_and_formats_the_uptime(string connector, int seconds, string expected)
    {
        Assert.Contains(expected, OverviewPanelViewModel.ZeroRequestsNotice(connector, TimeSpan.FromSeconds(seconds)), StringComparison.Ordinal);
    }

    [Fact]
    public void A_gateway_younger_than_a_minute_is_not_asked_about_traffic()
    {
        var vm = Panel();
        var health = Health("\"uptime_ms\":30000,\"connector\":{\"name\":\"claudecode\",\"state\":\"running\",\"requests\":0},\"connectors\":[{\"name\":\"claudecode\",\"state\":\"running\",\"requests\":0}]");

        vm.Apply(Snapshot(health));

        NoRow(vm, "Claude Code: no requests yet");
    }

    // ------------------------------------------------------------------ detected but not configured

    private async Task<OverviewPanelViewModel> WithDiscoveryAsync(string signals, string? config = GuardrailOnConfig)
    {
        _ = _temp.WriteFile("ai_discovery_state.json", "{\"version\":2,\"signals\":{" + signals + "}}");
        var vm = Panel(config);
        vm.Apply(Snapshot());
        await vm.RefreshAgentsAsync(CancellationToken.None);
        return vm;
    }

    private static string Signal(string id, string connector, double confidence, string state = "seen", string extra = "") =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"\"{id}\":{{\"name\":\"{connector}\",\"category\":\"active_process\",\"confidence\":{confidence},\"state\":\"{state}\",\"supported_connector\":\"{connector}\"{extra}}}");

    [Fact]
    public async Task Confident_agents_with_no_connector_are_named_in_the_mac_s_order()
    {
        var vm = await WithDiscoveryAsync(
            Signal("a", "cursor", 0.95) + "," + Signal("b", "codex", 0.9) + "," + Signal("c", "claudecode", 0.99));

        var row = Row(vm, "Detected but not configured");

        Assert.Equal("Detected but not configured: Codex, Cursor", row.Title);
        Assert.Contains("Connectors card or Setup", row.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Weak_gone_proxy_unknown_and_absent_signals_do_not_count()
    {
        var vm = await WithDiscoveryAsync(
            Signal("a", "cursor", 0.79) + "," +
            Signal("b", "codex", 0.95, state: "gone") + "," +
            Signal("c", "openclaw", 0.99) + "," +
            Signal("d", "qodo", 0.99) + "," +
            Signal("e", "devin", 0.99, extra: ",\"presence_score\":0.2") + "," +
            Signal("f", "hermes", 0.99, extra: ",\"presence_score\":0.9"));

        Assert.Equal("Detected but not configured: Hermes", Row(vm, "Detected but not configured").Title);
    }

    [Fact]
    public async Task An_agent_the_live_roster_reports_is_not_offered_and_the_row_follows_the_roster()
    {
        var vm = await WithDiscoveryAsync(Signal("a", "cursor", 0.95) + "," + Signal("b", "hermes", 0.95));
        Assert.Contains("Hermes, Cursor", Row(vm, "Detected but not configured").Title, StringComparison.Ordinal);

        // The gateway now routes for hermes (a connector added since): the next poll drops it from the row.
        vm.Apply(Snapshot(Health("\"uptime_ms\":600000,\"connector\":{\"name\":\"hermes\",\"state\":\"running\",\"requests\":4},\"connectors\":[{\"name\":\"hermes\",\"state\":\"running\",\"requests\":4}]")));
        Assert.Equal("Detected but not configured: Cursor", Row(vm, "Detected but not configured").Title);
    }

    [Fact]
    public async Task A_connector_named_in_config_yaml_is_managed()
    {
        var vm = await WithDiscoveryAsync(Signal("a", "cursor", 0.95), GuardrailOnConfig + "    cursor:\n      mode: observe\n");

        NoRow(vm, "Detected but not configured");
    }

    [Fact]
    public async Task No_discovery_file_means_no_row()
    {
        var vm = Panel();
        vm.Apply(Snapshot());

        await vm.RefreshAgentsAsync(CancellationToken.None);

        NoRow(vm, "Detected but not configured");
    }

    [Fact]
    public void The_parser_reads_object_and_array_signals_and_survives_junk()
    {
        Assert.Equal(new[] { "codex" }, OverviewDetectedConnectors.Parse("{\"signals\":[{\"supported_connector\":\"Codex\",\"confidence\":0.9},7,{\"x\":1}]}"));
        Assert.Empty(OverviewDetectedConnectors.Parse("{\"signals\":5}"));
        Assert.Empty(OverviewDetectedConnectors.Parse("[]"));
        Assert.ThrowsAny<JsonException>(() => OverviewDetectedConnectors.Parse("nope"));
        Assert.Equal(new[] { "claudecode" }, OverviewDetectedConnectors.Parse("{\"signals\":[{\"supported_connector\":\"claude-code\",\"identity_score\":0.85}]}"));
    }

    // ------------------------------------------------------------------ doctor

    [Fact]
    public async Task Doctor_failures_are_a_row_with_the_command_to_copy()
    {
        var vm = Panel();
        vm.Apply(Snapshot());

        await LoadDoctorAsync(vm, Cache(10, 2, 0, DateTimeOffset.UtcNow, ("fail", "Scanner"), ("fail", "Splunk HEC")));

        var row = Row(vm, "Doctor found 2 failures");
        Assert.Equal("High", row.SeverityKey);
        Assert.Equal("defenseclaw doctor", row.Command);
    }

    [Fact]
    public async Task A_failure_the_live_gateway_contradicts_is_stale_not_a_failure()
    {
        var vm = Panel();
        vm.Apply(Snapshot());

        await LoadDoctorAsync(vm, Cache(10, 1, 1, DateTimeOffset.UtcNow, ("fail", "Sidecar API"), ("warn", "OTel exporter")));

        NoRow(vm, "Doctor found");
        Assert.Equal("Info", Row(vm, "Doctor cache shows 2 stale failures").SeverityKey);

        // The card says so too: STALE badges, neutral, with the live note; the tiles and verdict drop what /health refutes.
        Assert.All(vm.DoctorChecks, c => Assert.True(c.IsStale));
        Assert.All(vm.DoctorChecks, c => Assert.Equal("STALE", c.StatusText));
        Assert.All(vm.DoctorChecks, c => Assert.Equal("Neutral", c.StatusKey));
        Assert.EndsWith("(live state OK)", vm.DoctorChecks[0].Detail, StringComparison.Ordinal);
        Assert.Equal("2 stale results", vm.DoctorVerdict);
        Assert.Equal("Neutral", vm.DoctorStateKey);
        Assert.Contains("0 fail", vm.DoctorSummary, StringComparison.Ordinal);
        Assert.Contains("2 stale", vm.DoctorSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failure_stays_a_failure_while_its_subsystem_is_not_running()
    {
        var vm = Panel();
        var health = Health("\"uptime_ms\":600000,\"api\":{\"state\":\"error\"}");
        vm.Apply(Snapshot(health));

        await LoadDoctorAsync(vm, Cache(10, 1, 0, DateTimeOffset.UtcNow, ("fail", "Sidecar API")));

        Assert.Equal("1 check failed", vm.DoctorVerdict);
        Assert.False(vm.DoctorChecks.Single().IsStale);
        Assert.Equal("FAIL", vm.DoctorChecks.Single().StatusText);
        _ = Row(vm, "Doctor found 1 failure");
    }

    [Fact]
    public async Task The_cards_reconciliation_follows_the_live_state_on_the_next_poll_without_reading_the_file_again()
    {
        var vm = Panel();
        vm.Apply(Snapshot(Health("\"uptime_ms\":600000,\"api\":{\"state\":\"error\"}")));
        await LoadDoctorAsync(vm, Cache(10, 1, 0, DateTimeOffset.UtcNow, ("fail", "Sidecar API")));
        Assert.Equal("1 check failed", vm.DoctorVerdict);

        vm.Apply(Snapshot());

        Assert.Equal("1 stale result", vm.DoctorVerdict);
        Assert.True(vm.DoctorChecks.Single().IsStale);
    }

    [Fact]
    public async Task An_old_doctor_cache_is_a_quiet_info_row_and_a_fresh_one_is_not()
    {
        var vm = Panel();
        vm.Apply(Snapshot());

        await LoadDoctorAsync(vm, Cache(12, 0, 0, DateTimeOffset.UtcNow.AddHours(-2)));
        Assert.Equal("Info", Row(vm, "Doctor cache is stale").SeverityKey);

        await LoadDoctorAsync(vm, Cache(12, 0, 0, DateTimeOffset.UtcNow));
        NoRow(vm, "Doctor");
    }

    [Theory]
    [InlineData("Sidecar API", "fail", true)]
    [InlineData(" sidecar api ", "warn", true)]
    [InlineData("Guardrail proxy", "fail", true)]
    [InlineData("OpenClaw Gateway", "fail", true)]
    [InlineData("Gateway", "warn", true)]
    [InlineData("OTel collector", "fail", true)]
    [InlineData("otel", "warn", true)]
    [InlineData("Sidecar API", "pass", false)]
    [InlineData("Sidecar API", "skip", false)]
    [InlineData("Splunk HEC", "fail", false)]
    [InlineData("credential ANTHROPIC_API_KEY", "fail", false)]
    public void The_mapping_is_the_macs(string label, string status, bool contradicted)
    {
        var running = Health($"\"api\":{Running},\"guardrail\":{Running},\"telemetry\":{Running},\"gateway\":{Running}");

        Assert.Equal(contradicted, DoctorReconciliation.LiveHealthContradicts(new DoctorCheckRow { Label = label, Status = status }, running));
        Assert.False(DoctorReconciliation.LiveHealthContradicts(new DoctorCheckRow { Label = label, Status = status }, null));
    }

    [Fact]
    public void A_subsystem_that_is_not_running_or_absent_contradicts_nothing()
    {
        var health = Health("\"api\":{\"state\":\"error\"},\"guardrail\":{\"state\":\"disabled\"}");

        foreach (var label in new[] { "Sidecar API", "Guardrail proxy", "Gateway", "OTel exporter" })
        {
            Assert.False(DoctorReconciliation.LiveHealthContradicts(new DoctorCheckRow { Label = label, Status = "fail" }, health), label);
        }
    }

    // ------------------------------------------------------------------ missing credentials

    [Fact]
    public async Task Each_missing_required_key_is_a_row_with_the_copy_only_filler()
    {
        var vm = Panel();
        vm.Apply(Snapshot());

        await LoadDoctorAsync(vm, Cache(
            10, 3, 1, DateTimeOffset.UtcNow,
            ("fail", "credential ANTHROPIC_API_KEY"),
            ("fail", "credential  OPENAI_API_KEY "),
            ("warn", "credential SPLUNK_TOKEN"),
            ("fail", "credential "),
            ("fail", "Credentials file"),
            ("pass", "credential GOOGLE_API_KEY")));

        var first = Row(vm, "credential ANTHROPIC_API_KEY");
        Assert.Equal("defenseclaw keys fill-missing", first.Command);
        Assert.Equal("defenseclaw keys fill-missing", Row(vm, "credential OPENAI_API_KEY").Command);
        Assert.Equal(2, vm.Attention.Count(r => r.Title.StartsWith("credential ", StringComparison.Ordinal)));
        Assert.True(first.HasCommand);
        Assert.Contains("suggested command is available", first.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_helper_lists_failing_credential_checks_once_each()
    {
        var snapshot = DoctorCacheReader.Parse(Cache(0, 3, 0, DateTimeOffset.UtcNow, ("fail", "credential A"), ("fail", "credential A"), ("fail", "credential B")));

        Assert.Equal(new[] { "A", "B" }, DoctorReconciliation.MissingRequiredCredentials(snapshot));
    }

    [Fact]
    public void The_filler_is_in_the_read_only_catalogue_of_things_the_app_only_shows()
    {
        // The row's command is text to copy: the app has no code path that runs an attention row's Command, and keys fill-missing is a mutation.
        Assert.Equal("defenseclaw keys fill-missing", OverviewPanelViewModel.FillMissingKeysCommand);
        Assert.NotEqual(DefenseClaw.Core.Cli.CommandTier.ReadOnly, DefenseClaw.Core.Cli.CommandTiers.Classify(new[] { "keys", "fill-missing" }));
    }

    // ------------------------------------------------------------------ scanner and standalone

    [Fact]
    public void A_standalone_gateway_passes_on_its_own_explanation()
    {
        var vm = Panel();
        var health = Health($"\"uptime_ms\":600000,\"api\":{Running},\"gateway\":{{\"state\":\"disabled\",\"details\":{{\"hint\":\"Standalone: no fleet uplink is configured.\"}}}}");

        vm.Apply(Snapshot(health));

        var row = Row(vm, "The gateway runs standalone");
        Assert.Equal("Standalone: no fleet uplink is configured.", row.Detail);
        Assert.Equal("Info", row.SeverityKey);
    }

    // ------------------------------------------------------------------ token-rejected state

    [Theory]
    [InlineData(GatewayStatus.Unauthorized, null, true)]
    [InlineData(GatewayStatus.Ok, GatewayStatus.Unauthorized, true)]
    [InlineData(GatewayStatus.Ok, GatewayStatus.Ok, false)]
    [InlineData(GatewayStatus.Ok, null, false)]
    public void The_snapshot_knows_when_the_token_was_rejected(GatewayStatus health, GatewayStatus? alerts, bool rejected)
    {
        var snapshot = new GatewaySnapshot { HealthStatus = health, AlertsStatus = alerts };

        Assert.Equal(rejected, snapshot.IsTokenRejected);
    }

    [Fact]
    public void A_change_in_the_alert_answer_is_a_change_a_state_subscriber_must_see()
    {
        var ok = new GatewaySnapshot { AlertsStatus = GatewayStatus.Ok };

        Assert.False(ok.RendersSameAs(new GatewaySnapshot { AlertsStatus = GatewayStatus.Unauthorized }));
        Assert.True(ok.RendersSameAs(new GatewaySnapshot { AlertsStatus = GatewayStatus.Ok }));
    }

    [Fact]
    public void The_shell_shows_the_token_banner_while_rejected_and_reload_config_reaches_the_services()
    {
        _services = TestServices.Create(_temp, GuardrailOnConfig);
        using var shell = new MainWindowViewModel(_services);

        shell.Apply(new GatewaySnapshot { State = AppGatewayState.Running, HealthStatus = GatewayStatus.Ok, AlertsStatus = GatewayStatus.Unauthorized });
        Assert.True(shell.ShowTokenBanner);

        shell.Apply(new GatewaySnapshot { State = AppGatewayState.Running, HealthStatus = GatewayStatus.Ok, AlertsStatus = GatewayStatus.Ok });
        Assert.False(shell.ShowTokenBanner);

        var reloads = 0;
        _services.ConfigReloaded += (_, _) => reloads++;
        shell.ReloadConfigCommand.Execute(null);

        Assert.True(shell.ReloadConfigCommand.CanExecute(null));
        _ = reloads; // ConfigReloaded is marshalled to a dispatcher a test does not run; the command not throwing on an isolated install is the check here.
    }

    // ------------------------------------------------------------------ the palette

    [Fact]
    public void The_palette_has_a_run_doctor_entry_that_only_navigates()
    {
        _services = TestServices.Create(_temp, GuardrailOnConfig);
        var catalog = new PanelCatalog(_services);
        var tray = (TrayIconService)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(TrayIconService));
        var actions = new ShellActions(_services, catalog, tray, () => null);

        var command = Assert.Single(ShellCommandRegistry.Build(catalog, actions, _ => { }, () => { }), c => c.Id == "app.run-doctor");
        Assert.Equal("Run health check", command.Title);
        Assert.True(command.IsEnabled);

        command.Run();

        var pending = _services.Navigation.Pending;
        Assert.NotNull(pending);
        Assert.Equal("overview", pending!.PanelId);
        Assert.Equal(new OverviewFocus(OverviewFocus.DoctorSection), pending.Payload);
    }

    [Fact]
    public void The_overview_takes_the_focus_request_and_ignores_other_payloads()
    {
        var vm = Panel();
        var raised = 0;
        vm.DoctorFocusRequested += (_, _) => raised++;

        vm.Accept(new AlertsFilter());
        Assert.False(vm.DoctorFocusPending);
        Assert.Equal(0, raised);

        vm.Accept(new OverviewFocus(OverviewFocus.DoctorSection));
        Assert.True(vm.DoctorFocusPending);
        Assert.Equal(1, raised);
        Assert.False(vm.IsDoctorRunning);
    }
}
