using System.Text.Json;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Gateway.Models;
using DefenseClaw.Core.Install;
using DefenseClaw.Core.Observability;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.Tests.Overview;

/// <summary>
/// What the Overview says about the gateway's own health signals from DefenseClaw 1.0.7 and 1.0.8, synthetic and verified from source only: the audit store's
/// size-cap warning (1.0.7; telemetry.details.retention_state "warning"), the OTLP counters' scope and an open circuit (1.0.8), and the reasons the runtime
/// monitoring block gives for being degraded (1.0.3 on). Each runs against the same fixture on a runtime that has the capability and one that does not.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class OverviewHealthSignalsTests : IDisposable
{
    private const string PinnedPlan = "runtime-95159fd/cli/observability-plan.json";

    private const string Config = "config_version: 9\nguardrail:\n  enabled: true\n  connector: codex\n  connectors:\n    codex:\n      mode: observe\n" +
                                  "ai_discovery:\n  runtime:\n    enabled: true\n    planes: [a, b]\n";

    private const string CapWarning =
        "\"retention_state\":\"warning\",\"retention_days\":7,\"retention_cap_used_bytes\":70464307,\"retention_cap_bytes\":67108864,\"retention_cap_reason\":\"active_correlation_state\"";

    private const string OwnSockets =
        "shadow egress partially covered: egress attribution is limited to this process's own sockets; run the gateway elevated for machine-wide coverage";

    private readonly TempDirectory _temp = new();
    private AppServices? _services;

    public void Dispose()
    {
        _services?.Dispose();
        _temp.Dispose();
    }

    private static RuntimeProbeRunner Runtime(string version) => (args, _) => Task.FromResult(
        string.Join(' ', args) switch
        {
            "--version-json" => RuntimeProbeOutput.Ok("{\"name\":\"defenseclaw-cli\",\"version\":\"" + version + "\",\"schema_version\":1}"),
            "agent discovery --help" => RuntimeProbeOutput.Ok("Usage: defenseclaw agent discovery [OPTIONS] COMMAND [ARGS]...\n\nCommands:\n  runtime  Runtime monitoring.\n"),
            "agent discovery runtime --help" => RuntimeProbeOutput.Ok(
                "Usage: defenseclaw agent discovery runtime [OPTIONS] COMMAND [ARGS]...\n\nCommands:\n  status  Status.\n  scan  Scan.\n  findings  Findings.\n  permissions  Permissions.\n"),
            _ => RuntimeProbeOutput.Ok("Usage: defenseclaw [OPTIONS] COMMAND [ARGS]...\n\nCommands:\n  setup  Configure.\n"),
        });

    private async Task<OverviewPanelViewModel> PanelAsync(string version)
    {
        _services?.Dispose();
        _services = TestServices.Create(_temp, Config, Runtime(version));
        _ = await _services.Runtime.RefreshAsync(force: true);
        return new OverviewPanelViewModel(_services);
    }

    private static GatewaySnapshot Snapshot(string telemetryDetails, string aiRuntime = "") => new()
    {
        State = AppGatewayState.Running,
        Install = InstallState.Running,
        Detail = "ok",
        Health = JsonSerializer.Deserialize<GatewayHealth>(
            "{\"uptime_ms\":600000,\"api\":{\"state\":\"running\"}," +
            "\"telemetry\":{\"state\":\"running\",\"details\":{" + telemetryDetails + "}}" +
            (aiRuntime.Length > 0 ? ",\"ai_runtime\":" + aiRuntime : string.Empty) + "}",
            new JsonSerializerOptions(JsonSerializerDefaults.Web)),
        ActiveConnectors = ["codex"],
        PolledAt = DateTimeOffset.UtcNow,
    };

    private static string Otlp(string extra) =>
        "\"destinations\":[{\"name\":\"example-otlp\",\"kind\":\"otlp\",\"enabled\":true,\"state\":\"healthy\",\"reason\":\"activated\",\"signals\":[\"logs\",\"metrics\"]," +
        "\"counters\":{\"accepted\":5,\"delivered\":3,\"retried\":2,\"dropped\":0,\"rejected\":0}" + extra + "}]";

    private static ObservabilityPlanRead Plan() => new(
        ObservabilityPlanStatus.Ok,
        ObservabilityPlanParser.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "runtime-95159fd", "cli", "observability-plan.json"))).Plan,
        string.Empty,
        DateTimeOffset.UtcNow);

    private static bool IsCapRow(AttentionRow row) => row.Title == OverviewPanelViewModel.AuditCapTitle;

    // ------------------------------------------------------------------ audit store cap

    [Theory]
    [InlineData("1.0.7")]
    [InlineData("1.0.8")]
    public async Task A_cap_warning_is_a_medium_row_with_the_clis_sentence_and_a_button_to_the_setting(string version)
    {
        var vm = await PanelAsync(version);

        vm.Apply(Snapshot(CapWarning));

        var row = Assert.Single(vm.Attention, IsCapRow);
        Assert.Equal("Medium", row.SeverityKey);
        Assert.Contains(
            "Warning: audit store cap not reached: 67.2 MiB used, cap 64.0 MiB; active correlation state is retained; end unused sessions or raise observability.local.max_store_mb.",
            row.Detail,
            StringComparison.Ordinal);
        Assert.Contains("does not block tool calls", row.Detail, StringComparison.Ordinal);
        var action = Assert.Single(row.Actions.Items);
        Assert.Equal(OverviewPanelViewModel.OpenAuditCapConfigActionId, action.Id);
        Assert.Equal("Edit max_store_mb", action.Label);
    }

    [Fact]
    public async Task On_1_0_6_the_same_health_makes_no_row()
    {
        var vm = await PanelAsync("1.0.6");

        vm.Apply(Snapshot(CapWarning));

        Assert.DoesNotContain(vm.Attention, IsCapRow);
    }

    [Fact]
    public async Task Without_a_warning_there_is_no_row_even_on_1_0_7()
    {
        var vm = await PanelAsync("1.0.7");

        vm.Apply(Snapshot("\"retention_state\":\"healthy\",\"retention_days\":7,\"retention_cap_used_bytes\":70464307,\"retention_cap_bytes\":67108864"));

        Assert.DoesNotContain(vm.Attention, IsCapRow);
    }

    [Theory]
    [InlineData("1.0.7", "Warn", "warning")]
    [InlineData("1.0.6", "Neutral", "unavailable")]
    public async Task The_local_storage_card_is_amber_only_where_the_runtime_has_the_warning(string version, string tone, string controller)
    {
        var vm = await PanelAsync(version);
        vm.Apply(Snapshot(CapWarning));

        vm.ApplyObservabilityPlan(Plan());

        Assert.True(vm.HasLocalStorage);
        Assert.Equal(tone, vm.LocalStorageToneKey);
        Assert.Equal(controller, vm.LocalStorage!.Controller);
        Assert.Contains("controller=" + controller, vm.LocalStorageSummary, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ OTLP counters

    [Fact]
    public async Task On_1_0_8_the_counters_scope_and_an_open_circuit_are_explained()
    {
        var vm = await PanelAsync("1.0.8");

        vm.Apply(Snapshot(Otlp(",\"circuit_state\":\"open\",\"counters_scope\":\"log and trace records; metric export attempts are counted only in signal_health\"")));

        var detail = Assert.Single(vm.ObservabilityRows).Detail;
        Assert.StartsWith("activated · 5 accepted · 3 delivered · 2 retried · 0 dropped · 0 rejected", detail, StringComparison.Ordinal);
        Assert.Contains("counters: log and trace records", detail, StringComparison.Ordinal);
        Assert.Contains("circuit open: the gateway keeps these records queued and sends them again when the collector answers", detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_closed_circuit_or_no_retries_adds_no_circuit_sentence()
    {
        var vm = await PanelAsync("1.0.8");

        vm.Apply(Snapshot(Otlp(",\"circuit_state\":\"closed\"")));

        Assert.DoesNotContain("circuit", Assert.Single(vm.ObservabilityRows).Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task On_1_0_7_the_detail_is_exactly_what_it_was()
    {
        var vm = await PanelAsync("1.0.7");

        vm.Apply(Snapshot(Otlp(",\"circuit_state\":\"open\",\"counters_scope\":\"log and trace records; metric export attempts are counted only in signal_health\"")));

        Assert.Equal(
            "activated · 5 accepted · 3 delivered · 2 retried · 0 dropped · 0 rejected",
            Assert.Single(vm.ObservabilityRows).Detail);
    }

    // ------------------------------------------------------------------ runtime monitoring degraded

    [Fact]
    public async Task A_degraded_runtime_block_names_the_reasons_and_the_per_user_one_is_calm()
    {
        var vm = await PanelAsync("1.0.6");

        vm.Apply(Snapshot(
            "\"destination_count\":0",
            "{\"state\":\"running\",\"details\":{\"degraded\":true,\"degraded_reasons\":[\"" + OwnSockets + "\"]}}"));

        Assert.True(vm.ShowRuntimeMonitoring);
        var text = vm.RuntimeMonitoringDegradedText;
        Assert.StartsWith("The gateway reports runtime monitoring degraded: shadow egress partially covered:", text, StringComparison.Ordinal);
        Assert.Contains("this app keeps the gateway unelevated, so nothing needs to change.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Warning", text, StringComparison.Ordinal);
        Assert.True(vm.HasRuntimeMonitoringDegraded);
    }

    [Fact]
    public async Task Another_reason_gets_no_per_user_sentence_and_no_reason_says_so()
    {
        var vm = await PanelAsync("1.0.6");

        vm.Apply(Snapshot("\"destination_count\":0", "{\"state\":\"running\",\"details\":{\"degraded\":true,\"degraded_reasons\":[\"process table unreadable: access denied\"]}}"));
        Assert.Equal("The gateway reports runtime monitoring degraded: process table unreadable: access denied.", vm.RuntimeMonitoringDegradedText);

        vm.Apply(Snapshot("\"destination_count\":0", "{\"state\":\"running\",\"details\":{\"degraded\":true}}"));
        Assert.Equal("The gateway reports runtime monitoring degraded and gives no reason.", vm.RuntimeMonitoringDegradedText);
    }

    [Fact]
    public async Task A_runtime_that_is_not_degraded_or_sends_no_block_says_nothing()
    {
        var vm = await PanelAsync("1.0.6");

        vm.Apply(Snapshot("\"destination_count\":0", "{\"state\":\"running\",\"details\":{\"degraded\":false}}"));
        Assert.Equal(string.Empty, vm.RuntimeMonitoringDegradedText);
        Assert.False(vm.HasRuntimeMonitoringDegraded);

        vm.Apply(Snapshot("\"destination_count\":0"));
        Assert.Equal(string.Empty, vm.RuntimeMonitoringDegradedText);
    }

    [Fact]
    public void The_reasons_are_trimmed_cut_and_limited()
    {
        var reasons = string.Join(",", Enumerable.Range(0, 12).Select(static i => "\"  reason " + i + new string('x', i == 0 ? 400 : 1) + "  \""));
        var health = JsonSerializer.Deserialize<GatewayHealth>(
            "{\"ai_runtime\":{\"state\":\"running\",\"details\":{\"degraded\":true,\"degraded_reasons\":[" + reasons + ",7,\"\",null]}}}",
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var view = DefenseClaw.Core.AiRuntime.RuntimeHealthView.From(health);

        Assert.Equal(8, view.Reasons.Count);
        Assert.Equal(300, view.Reasons[0].Length);
        Assert.Equal("reason 1x", view.Reasons[1]);
        Assert.Empty(DefenseClaw.Core.AiRuntime.RuntimeHealthView.NotReported.Reasons);
    }
}
