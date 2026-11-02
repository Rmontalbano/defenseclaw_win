using System.Text.Json;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Gateway.Models;
using DefenseClaw.Core.Install;
using DefenseClaw.Core.Observability;

namespace DefenseClaw.App.Tests.Overview;

/// <summary>
/// What DefenseClaw 1.0.6's <c>/health</c> and config.yaml add for the Overview (CUST-426), synthetic and verified from source only: the judge-fallback row
/// (judge_health.go; cmd_status.py <c>_print_judge_fallbacks</c> and <c>judge_max_parallel_hint</c>) and the audit store's size cap on the Telemetry card and
/// the Local SQLite line (health.go:1732; observability.local.max_store_mb). Each with the field present, absent and malformed; nothing asks the runtime's version.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class JudgeFallbackAndSizeCapOverviewTests : IDisposable
{
    private const string Fallbacks =
        "\"judge_fallbacks\":12,\"judge_fallbacks_by_connector\":{\"codex\":9,\"claudecode\":3},\"judge_last_fallback\":\"timeout (codex)\"";

    private readonly TempDirectory _temp = new();
    private AppServices? _services;

    public void Dispose()
    {
        _services?.Dispose();
        _temp.Dispose();
    }

    private OverviewPanelViewModel Panel(string config)
    {
        _services?.Dispose();
        _services = TestServices.Create(_temp, config);
        return new OverviewPanelViewModel(_services);
    }

    private static GatewayHealth Health(string guardrailDetails, string telemetryDetails = "") =>
        JsonSerializer.Deserialize<GatewayHealth>(
            "{\"uptime_ms\":600000,\"api\":{\"state\":\"running\"}," +
            "\"guardrail\":{\"state\":\"running\",\"details\":{\"policy_mode\":\"observe\"" + (guardrailDetails.Length > 0 ? "," + guardrailDetails : string.Empty) + "}}," +
            "\"telemetry\":{\"state\":\"running\",\"details\":{\"destination_count\":1" + (telemetryDetails.Length > 0 ? "," + telemetryDetails : string.Empty) + "}}}",
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private static GatewaySnapshot Snapshot(GatewayHealth? health) => new()
    {
        State = AppGatewayState.Running,
        Install = InstallState.Running,
        Detail = "ok",
        Health = health,
        ActiveConnectors = ["codex"],
        PolledAt = DateTimeOffset.UtcNow,
    };

    private static string Config(string judge = "", string local = "") =>
        "guardrail:\n  enabled: true\n  connector: codex\n  connectors:\n    codex:\n      mode: observe\n" + judge +
        (local.Length > 0 ? "observability:\n  local:\n" + local : string.Empty);

    private static bool IsFallbackRow(AttentionRow row) => row.Title.StartsWith(OverviewPanelViewModel.JudgeFallbacksTitlePrefix, StringComparison.Ordinal);

    // ------------------------------------------------------------------ judge fallbacks

    [Fact]
    public void A_count_above_zero_is_a_medium_row_in_the_clis_words_with_the_max_parallel_hint()
    {
        var vm = Panel(Config());

        vm.Apply(Snapshot(Health(Fallbacks)));

        var row = Assert.Single(vm.Attention, IsFallbackRow);
        Assert.Equal("Judge fallbacks: the rules decided 12 hook check(s)", row.Title);
        Assert.Equal("Medium", row.SeverityKey);
        Assert.Contains("The rules decided 12 hook check(s) because the judge gave no verdict in time (claudecode 3, codex 9); last: timeout (codex).", row.Detail, StringComparison.Ordinal);
        Assert.Contains("Counted since the judge started.", row.Detail, StringComparison.Ordinal);
        Assert.Contains("a slow judge model: set guardrail.judge.max_parallel", row.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("  judge:\n    max_parallel: 0\n", true)]
    [InlineData("  judge:\n    max_parallel: -1\n", true)]
    [InlineData("  judge:\n    enabled: true\n", true)]
    [InlineData("  judge:\n    max_parallel: 2\n", false)]
    [InlineData("  judge:\n    max_parallel: 8\n", false)]
    [InlineData("  judge:\n    max_parallel: many\n", false)]
    public void The_hint_is_made_only_while_max_parallel_is_off(string judge, bool hint)
    {
        var vm = Panel(Config(judge));

        vm.Apply(Snapshot(Health(Fallbacks)));

        var row = Assert.Single(vm.Attention, IsFallbackRow);
        Assert.Equal(hint, row.Detail.Contains("guardrail.judge.max_parallel", StringComparison.Ordinal));
    }

    [Fact]
    public void The_row_follows_the_count_and_goes_when_it_is_gone()
    {
        var vm = Panel(Config());

        vm.Apply(Snapshot(Health("\"judge_fallbacks\":1")));
        Assert.Equal("Judge fallbacks: the rules decided 1 hook check(s)", Assert.Single(vm.Attention, IsFallbackRow).Title);

        vm.Apply(Snapshot(Health("\"judge_fallbacks\":1500")));
        Assert.Equal("Judge fallbacks: the rules decided 1,500 hook check(s)", Assert.Single(vm.Attention, IsFallbackRow).Title);

        vm.Apply(Snapshot(Health(string.Empty)));
        Assert.DoesNotContain(vm.Attention, IsFallbackRow);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\"judge_state\":\"ok\",\"judge_recent_calls\":4")]
    [InlineData("\"judge_fallbacks\":0")]
    [InlineData("\"judge_fallbacks\":\"12\"")]
    [InlineData("\"judge_fallbacks\":-3")]
    [InlineData("\"judge_fallbacks\":null")]
    [InlineData("\"judge_fallbacks\":{\"codex\":12}")]
    public void A_runtime_that_sends_no_count_or_a_malformed_one_has_no_row(string details)
    {
        var vm = Panel(Config());

        vm.Apply(Snapshot(Health(details)));

        Assert.DoesNotContain(vm.Attention, IsFallbackRow);
    }

    [Fact]
    public void A_gateway_that_did_not_answer_or_has_no_guardrail_block_has_no_row()
    {
        var vm = Panel(Config());

        vm.Apply(Snapshot(null));
        Assert.DoesNotContain(vm.Attention, IsFallbackRow);

        vm.Apply(Snapshot(JsonSerializer.Deserialize<GatewayHealth>("{\"uptime_ms\":600000,\"api\":{\"state\":\"running\"}}", new JsonSerializerOptions(JsonSerializerDefaults.Web))));
        Assert.DoesNotContain(vm.Attention, IsFallbackRow);
    }

    [Fact]
    public void Malformed_companions_do_not_stop_the_row()
    {
        var vm = Panel(Config());

        vm.Apply(Snapshot(Health("\"judge_fallbacks\":7,\"judge_fallbacks_by_connector\":\"codex\",\"judge_last_fallback\":false")));

        var row = Assert.Single(vm.Attention, IsFallbackRow);
        Assert.Contains("The rules decided 7 hook check(s) because the judge gave no verdict in time.", row.Detail, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ audit size cap: the Telemetry card

    private static ServiceRow Telemetry(string telemetryDetails)
    {
        var health = Health(string.Empty, telemetryDetails);
        return OverviewPanelViewModel.BuildServiceCards(Snapshot(health), health, [], new HashSet<string>(), null).Single(static c => c.Key == "telemetry");
    }

    [Theory]
    [InlineData("\"retention_days\":7", "retention 7 days")]
    [InlineData("\"retention_days\":7,\"retention_max_store_mb\":512", "retention 7 days, size cap 512 MiB")]
    [InlineData("\"retention_days\":0,\"retention_max_store_mb\":64", "retention 0 days, size cap 64 MiB")]
    // Anything that is not a positive whole number leaves the text as it was.
    [InlineData("\"retention_days\":7,\"retention_max_store_mb\":0", "retention 7 days")]
    [InlineData("\"retention_days\":7,\"retention_max_store_mb\":\"512\"", "retention 7 days")]
    [InlineData("\"retention_days\":7,\"retention_max_store_mb\":12.5", "retention 7 days")]
    [InlineData("\"retention_days\":7,\"retention_max_store_mb\":null", "retention 7 days")]
    [InlineData("\"retention_days\":7,\"retention_max_store_mb\":{\"mb\":512}", "retention 7 days")]
    [InlineData("\"retention_max_store_mb\":512", "")]
    [InlineData("", "")]
    public void The_telemetry_card_adds_the_cap_only_when_the_gateway_reports_one(string details, string posture) =>
        Assert.Equal(posture, Telemetry(details).Posture);

    // ------------------------------------------------------------------ audit size cap: the Local SQLite line

    private OverviewPanelViewModel PanelWithPlan(string config, string telemetryDetails)
    {
        var vm = Panel(config);
        vm.Apply(Snapshot(Health(string.Empty, telemetryDetails)));
        vm.ApplyObservabilityPlan(new ObservabilityPlanRead(
            ObservabilityPlanStatus.Ok,
            ObservabilityPlanParser.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "runtime-95159fd", "cli", "observability-plan.json"))).Plan,
            string.Empty,
            DateTimeOffset.UtcNow));
        return vm;
    }

    [Theory]
    // config.yaml's cap wins over the gateway's, as its window does.
    [InlineData("    retention_days: 7\n    max_store_mb: 256\n", "\"retention_days\":90", "7 days, size cap 256 MiB", "Neutral")]
    // Only the gateway reports it (config.yaml silent).
    [InlineData("    retention_days: 7\n", "\"retention_days\":7,\"retention_max_store_mb\":512", "7 days, size cap 512 MiB", "Neutral")]
    // A cap with no window is bounded: it reads as the cap alone and is not "without limit".
    [InlineData("    retention_days: 0\n    max_store_mb: 128\n", "\"retention_days\":0", "size cap 128 MiB", "Neutral")]
    // Neither: the line is what it was, and a window of 0 is still the warning.
    [InlineData("    retention_days: 7\n", "\"retention_days\":7", "7 days", "Neutral")]
    [InlineData("    retention_days: 0\n", "\"retention_days\":0", "unbounded", "Warn")]
    // A cap the runtime would refuse is no cap.
    [InlineData("    retention_days: 7\n    max_store_mb: 8\n", "\"retention_days\":7", "7 days", "Neutral")]
    [InlineData("    retention_days: 7\n    max_store_mb: plenty\n", "\"retention_days\":7", "7 days", "Neutral")]
    public void The_local_sqlite_line_says_the_cap_beside_the_window_in_the_runtimes_words(string local, string telemetry, string shown, string tone)
    {
        var vm = PanelWithPlan(Config(local: local), telemetry);

        Assert.Equal(shown, vm.LocalStorage!.Retention);
        Assert.Contains($"retention={shown} ", vm.LocalStorageSummary, StringComparison.Ordinal);
        Assert.Equal(tone, vm.LocalStorageToneKey);
    }

    [Fact]
    public void With_no_window_anywhere_the_cap_still_shows_and_the_default_stays_the_default()
    {
        var capped = PanelWithPlan(Config(local: "    max_store_mb: 64\n"), string.Empty);
        var plain = PanelWithPlan(Config(), string.Empty);

        Assert.Equal("runtime default, size cap 64 MiB", capped.LocalStorage!.Retention);
        Assert.Equal("runtime default", plain.LocalStorage!.Retention);
    }
}
