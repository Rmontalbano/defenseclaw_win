using System.Net.Http;
using System.Text.Json;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Gateway;
using DefenseClaw.Core.Gateway.Models;
using DefenseClaw.Core.Install;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.Tests.Overview;

/// <summary>
/// The "Model routing", "Route-only" and "Tokenomics" rows of the Configuration card on a DefenseClaw 1.0.10 runtime (CUST-457), from synthetic
/// <c>/health</c> blocks and <c>GET /usage</c> bodies shaped from the 1.0.10 source. Held here: a default install (routing disabled, tokenomics keys absent) reads
/// calm - neutral rows, no attention row, no call to <c>/usage</c>; only the runtime's own problem (state error, a route-only connector not ready) is a warning;
/// a failed usage read is a neutral sentence; and a 1.0.9 runtime's card is exactly what it was, whatever the gateway puts in <c>routing</c>.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class ModelRoutingOverviewTests : IDisposable
{
    private const string Hook = "guardrail:\n  enabled: true\n  connector: codex\n  connectors:\n    codex:\n      mode: observe\n      hook_fail_mode: open\n";

    private const string TokenomicsOn = "observability:\n  tokenomics:\n    enabled: true\n    local_store:\n      enabled: true\n";

    private const string Since = "\"since\":\"2026-11-20T10:00:00Z\"";

    private const string DisabledRouting = "{\"state\":\"disabled\"," + Since + ",\"details\":{\"enabled\":false}}";

    private readonly TempDirectory _temp = new();
    private AppServices? _services;

    public void Dispose()
    {
        _services?.Dispose();
        _temp.Dispose();
    }

    private static RuntimeProbeRunner Runtime(string version) => (args, _) => Task.FromResult(
        args.Contains("--version-json")
            ? RuntimeProbeOutput.Ok("{\"name\":\"defenseclaw-cli\",\"version\":\"" + version + "\",\"schema_version\":1}")
            : RuntimeProbeOutput.Ok("Usage: defenseclaw [OPTIONS] COMMAND [ARGS]...\n\nCommands:\n  setup  Configure.\n"));

    private async Task<OverviewPanelViewModel> PanelAsync(string version, string config)
    {
        _services?.Dispose();
        _services = TestServices.Create(_temp, config, Runtime(version));
        _ = await _services.Runtime.RefreshAsync(force: true);
        return new OverviewPanelViewModel(_services);
    }

    private static GatewayHealth Health(string? routing) =>
        JsonSerializer.Deserialize<GatewayHealth>(
            "{\"uptime_ms\":600000,\"api\":{\"state\":\"running\"},\"guardrail\":{\"state\":\"running\",\"details\":{\"policy_mode\":\"observe\"}}" +
            (routing is null ? string.Empty : ",\"routing\":" + routing) + "}",
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

    private static List<ConfigRow> Rows(OverviewPanelViewModel vm)
    {
        if (vm.HasConfigurationOverflow && !vm.ConfigurationExpanded)
        {
            vm.ToggleConfigurationCommand.Execute(null);
        }

        return vm.ConfigurationRows.ToList();
    }

    private static ConfigRow? Row(OverviewPanelViewModel vm, string label) => Rows(vm).SingleOrDefault(r => r.Label == label);

    private static List<(string, string, string)> Shape(OverviewPanelViewModel vm) => Rows(vm).Select(static r => (r.Label, r.Value, r.ToneKey)).ToList();

    private static GatewayResult<JsonDocument> Body(string json) => GatewayResult<JsonDocument>.Ok(JsonDocument.Parse(json));

    private sealed class UsageStub
    {
        public int Calls { get; private set; }

        public string? LastPath { get; private set; }

        public int? LastMax { get; private set; }

        public Func<GatewayResult<JsonDocument>> Answer { get; set; } = () => throw new InvalidOperationException("no answer scripted");

        public Task<GatewayResult<JsonDocument>> Fetch(string path, int max, CancellationToken ct)
        {
            Calls++;
            LastPath = path;
            LastMax = max;
            return Task.FromResult(Answer());
        }
    }

    private static List<AttentionRow> RoutingAttention(OverviewPanelViewModel vm) =>
        vm.Attention.Where(static r =>
            r.Title.Contains("routing", StringComparison.OrdinalIgnoreCase) ||
            r.Title.Contains("tokenomics", StringComparison.OrdinalIgnoreCase) ||
            r.Title.Contains("usage", StringComparison.OrdinalIgnoreCase) ||
            r.Title.Contains("route-only", StringComparison.OrdinalIgnoreCase)).ToList();

    // ------------------------------------------------------------------ the owner's machine

    [Fact]
    public async Task The_owners_shape_routing_disabled_and_no_tokenomics_keys_is_calm_everywhere()
    {
        var vm = await PanelAsync("1.0.10", Hook);
        var stub = new UsageStub();
        vm.ModelUsageFetch = stub.Fetch;

        vm.Apply(Snapshot(Health(DisabledRouting)));
        await vm.RefreshModelUsageAsync(force: true, CancellationToken.None);

        var routing = Row(vm, "Model routing");
        Assert.NotNull(routing);
        Assert.Equal("disabled", routing.Value);
        Assert.Equal("Neutral", routing.ToneKey);

        var tokenomics = Row(vm, "Tokenomics");
        Assert.NotNull(tokenomics);
        Assert.Equal("off, no usage recorded", tokenomics.Value);
        Assert.Equal("Neutral", tokenomics.ToneKey);

        Assert.DoesNotContain(Rows(vm), static r => r.Label.StartsWith("Route-only", StringComparison.Ordinal));
        Assert.Empty(RoutingAttention(vm));
        Assert.Equal(0, stub.Calls); // off: the usage route is never called
    }

    [Fact]
    public async Task No_row_of_this_work_is_a_warning_on_the_owners_shape_even_with_a_config_that_says_nothing()
    {
        var vm = await PanelAsync("1.0.10", string.Empty);

        vm.Apply(Snapshot(Health(DisabledRouting)));

        Assert.All(
            Rows(vm).Where(static r => r.Label is "Model routing" or "Tokenomics" || r.Label.StartsWith("Route-only", StringComparison.Ordinal)),
            static r => Assert.NotEqual("Warn", r.ToneKey));
    }

    // ------------------------------------------------------------------ model routing

    [Fact]
    public async Task Rules_running_with_two_models_reads_state_classifier_and_models()
    {
        var vm = await PanelAsync("1.0.10", Hook);

        vm.Apply(Snapshot(Health("{\"state\":\"running\"," + Since + ",\"details\":{\"enabled\":true,\"classifier\":\"rules\",\"mode\":\"managed\",\"version\":\"0.3.0\",\"model_count\":2}}")));

        var row = Row(vm, "Model routing");
        Assert.NotNull(row);
        Assert.Equal("running · rules · 2 models", row.Value);
        Assert.Equal("Neutral", row.ToneKey);
    }

    [Fact]
    public async Task A_1_0_9_shaped_block_on_a_1_0_10_runtime_gives_the_row_without_the_missing_parts()
    {
        var vm = await PanelAsync("1.0.10", Hook);

        vm.Apply(Snapshot(Health("{\"state\":\"running\"," + Since + ",\"details\":{\"enabled\":true,\"mode\":\"managed\",\"version\":\"0.3.0\",\"model_count\":2,\"port\":8080}}")));

        Assert.Equal("running · 2 models", Row(vm, "Model routing")!.Value);
        Assert.DoesNotContain(Rows(vm), static r => r.Label.StartsWith("Route-only", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_vllm_sr_error_is_a_warning_with_a_bounded_one_line_last_error_and_no_attention_row()
    {
        var vm = await PanelAsync("1.0.10", Hook);
        var error = "docker is not running\n" + new string('y', 600);

        vm.Apply(Snapshot(Health("{\"state\":\"error\",\"last_error\":" + JsonSerializer.Serialize(error) + "," + Since + ",\"details\":{\"enabled\":true,\"classifier\":\"vllm_sr\",\"model_count\":1}}")));

        var row = Row(vm, "Model routing");
        Assert.NotNull(row);
        Assert.Equal("Warn", row.ToneKey);
        Assert.StartsWith("error · vllm_sr · 1 model · docker is not running", row.Value, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', row.Value);
        Assert.True(row.Value.Length < 300);
        Assert.Empty(RoutingAttention(vm));
    }

    [Fact]
    public async Task A_route_only_codex_that_is_not_ready_is_its_own_warning_row_with_cause_problem_and_fix()
    {
        var vm = await PanelAsync("1.0.10", Hook);

        vm.Apply(Snapshot(Health(
            "{\"state\":\"running\"," + Since + ",\"details\":{\"enabled\":true,\"classifier\":\"rules\",\"model_count\":1,\"route_only\":[" +
            "{\"connector\":\"codex\",\"custody\":false,\"credential\":\"agent\",\"ready\":false,\"cause\":\"upstream_not_set\"," +
            "\"problem\":\"routing.route_only.upstreams.codex is not set\",\"fix\":\"set routing.route_only.upstreams.codex (base_url and api) in config.yaml\"}]}}")));

        Assert.Equal("Neutral", Row(vm, "Model routing")!.ToneKey);
        var row = Row(vm, "Route-only codex");
        Assert.NotNull(row);
        Assert.Equal("Warn", row.ToneKey);
        Assert.Equal("not ready (upstream_not_set). routing.route_only.upstreams.codex is not set. Fix: set routing.route_only.upstreams.codex (base_url and api) in config.yaml", row.Value);
        Assert.Empty(RoutingAttention(vm));
    }

    [Fact]
    public async Task A_ready_route_only_connector_is_a_neutral_ready_row()
    {
        var vm = await PanelAsync("1.0.10", Hook);

        vm.Apply(Snapshot(Health("{\"state\":\"running\",\"details\":{\"enabled\":true,\"classifier\":\"rules\",\"model_count\":1,\"route_only\":[{\"connector\":\"codex\",\"credential\":\"agent\",\"ready\":true}]}}")));

        var row = Row(vm, "Route-only codex");
        Assert.NotNull(row);
        Assert.Equal("ready", row.Value);
        Assert.Equal("Neutral", row.ToneKey);
    }

    [Fact]
    public async Task Without_a_routing_block_there_is_no_routing_row_at_all()
    {
        var vm = await PanelAsync("1.0.10", Hook);

        vm.Apply(Snapshot(Health(null)));

        Assert.Null(Row(vm, "Model routing"));
        Assert.NotNull(Row(vm, "Tokenomics")); // the tokenomics row comes from config.yaml, not from /health
    }

    // ------------------------------------------------------------------ tokenomics

    private const string RunningRules = "{\"state\":\"running\",\"details\":{\"enabled\":true,\"classifier\":\"rules\",\"model_count\":1}}";

    [Fact]
    public async Task Tokenomics_on_with_no_rows_says_so_and_asks_for_the_last_24_hours_by_model_once()
    {
        var vm = await PanelAsync("1.0.10", Hook + TokenomicsOn);
        var stub = new UsageStub { Answer = () => Body("{\"enabled\":true,\"by\":\"model\",\"scope\":\"own\",\"rows\":[]}") };
        vm.ModelUsageFetch = stub.Fetch;

        vm.Apply(Snapshot(Health(DisabledRouting)));
        await vm.RefreshModelUsageAsync(force: false, CancellationToken.None);

        var row = Row(vm, "Tokenomics");
        Assert.NotNull(row);
        Assert.StartsWith("on, no usage in the last 24 h", row.Value, StringComparison.Ordinal);
        Assert.Equal("Neutral", row.ToneKey);
        Assert.Equal(1, stub.Calls);
        Assert.Equal("usage?by=model&since=24h&limit=100", stub.LastPath);
        Assert.Equal(64 * 1024, stub.LastMax);

        // Within the refresh interval a poll does not ask again; Refresh does.
        await vm.RefreshModelUsageAsync(force: false, CancellationToken.None);
        Assert.Equal(1, stub.Calls);
        await vm.RefreshModelUsageAsync(force: true, CancellationToken.None);
        Assert.Equal(2, stub.Calls);
    }

    [Fact]
    public async Task In_hook_mode_the_row_says_the_agents_own_calls_are_not_recorded()
    {
        var vm = await PanelAsync("1.0.10", Hook + TokenomicsOn);
        var stub = new UsageStub { Answer = () => Body("{\"enabled\":true,\"rows\":[]}") };
        vm.ModelUsageFetch = stub.Fetch;

        vm.Apply(Snapshot(Health(DisabledRouting)));
        await vm.RefreshModelUsageAsync(force: true, CancellationToken.None);

        Assert.Equal("on, no usage in the last 24 h. " + OverviewPanelViewModel.HookModeUsageNote, Row(vm, "Tokenomics")!.Value);
    }

    [Fact]
    public async Task With_a_route_only_connector_reported_the_hook_mode_sentence_is_left_out()
    {
        var vm = await PanelAsync("1.0.10", Hook + TokenomicsOn);
        var stub = new UsageStub { Answer = () => Body("{\"enabled\":true,\"rows\":[]}") };
        vm.ModelUsageFetch = stub.Fetch;

        vm.Apply(Snapshot(Health("{\"state\":\"running\",\"details\":{\"enabled\":true,\"classifier\":\"rules\",\"model_count\":1,\"route_only\":[{\"connector\":\"codex\",\"ready\":true}]}}")));
        await vm.RefreshModelUsageAsync(force: true, CancellationToken.None);

        Assert.Equal("on, no usage in the last 24 h", Row(vm, "Tokenomics")!.Value);
    }

    [Fact]
    public async Task Unpriced_rows_are_counted_and_say_there_is_no_price_table_never_zero_dollars()
    {
        var vm = await PanelAsync("1.0.10", Hook + TokenomicsOn);
        var stub = new UsageStub
        {
            Answer = () => Body("{\"enabled\":true,\"rows\":[{\"key\":\"other\",\"calls\":7,\"usage_calls\":7,\"input_tokens\":10,\"output_tokens\":5,\"cost_calls\":0,\"latency_ms_sum\":3}]}"),
        };
        vm.ModelUsageFetch = stub.Fetch;

        vm.Apply(Snapshot(Health(RunningRules)));
        await vm.RefreshModelUsageAsync(force: true, CancellationToken.None);

        var value = Row(vm, "Tokenomics")!.Value;
        Assert.StartsWith("on, 7 calls in the last 24 h, no price table for these models, so no cost", value, StringComparison.Ordinal);
        Assert.DoesNotContain("$", value, StringComparison.Ordinal);
        Assert.DoesNotContain("0.00", value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Priced_rows_give_calls_dollars_and_how_many_calls_were_priced()
    {
        var vm = await PanelAsync("1.0.10", Hook + TokenomicsOn);
        var stub = new UsageStub
        {
            Answer = () => Body(
                "{\"enabled\":true,\"rows\":[{\"key\":\"model-a\",\"calls\":9,\"cost_usd\":1.256,\"cost_calls\":9}," +
                "{\"key\":\"other\",\"calls\":3,\"cost_calls\":0}]}"),
        };
        vm.ModelUsageFetch = stub.Fetch;

        vm.Apply(Snapshot(Health(RunningRules)));
        await vm.RefreshModelUsageAsync(force: true, CancellationToken.None);

        Assert.StartsWith("on, 12 calls in the last 24 h, $1.26 (9 of 12 calls priced)", Row(vm, "Tokenomics")!.Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_gateway_that_says_its_store_is_off_while_config_says_on_is_said_plainly()
    {
        var vm = await PanelAsync("1.0.10", Hook + TokenomicsOn);
        var stub = new UsageStub { Answer = () => Body("{\"enabled\":false,\"hint\":\"set observability.tokenomics.enabled\",\"rows\":[]}") };
        vm.ModelUsageFetch = stub.Fetch;

        vm.Apply(Snapshot(Health(DisabledRouting)));
        await vm.RefreshModelUsageAsync(force: true, CancellationToken.None);

        var row = Row(vm, "Tokenomics")!;
        Assert.Equal("on in config.yaml, gateway reports usage store off", row.Value);
        Assert.Equal("Neutral", row.ToneKey);
    }

    public static TheoryData<string> Failures => new() { "not-found", "forbidden", "server-error", "unreachable", "bad-json", "throws" };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task A_failed_usage_read_is_a_neutral_sentence_and_no_warning_or_attention_row(string failure)
    {
        var vm = await PanelAsync("1.0.10", Hook + TokenomicsOn);
        var stub = new UsageStub
        {
            Answer = () => failure switch
            {
                "not-found" => GatewayResult<JsonDocument>.Error("HTTP 404", 404),
                "forbidden" => GatewayResult<JsonDocument>.Error("HTTP 403", 403),
                "server-error" => GatewayResult<JsonDocument>.Error("the usage store could not be read", 500),
                "unreachable" => GatewayResult<JsonDocument>.Unreachable("gateway timed out"),
                "bad-json" => Body("{\"rows\":[]}"),
                _ => throw new HttpRequestException("connection reset"),
            },
        };
        vm.ModelUsageFetch = stub.Fetch;

        vm.Apply(Snapshot(Health(DisabledRouting)));
        await vm.RefreshModelUsageAsync(force: true, CancellationToken.None);

        var row = Row(vm, "Tokenomics")!;
        Assert.Equal("on, usage could not be read", row.Value);
        Assert.Equal("Neutral", row.ToneKey);
        Assert.Empty(RoutingAttention(vm));
    }

    [Fact]
    public async Task Tokenomics_enabled_without_the_local_store_does_not_ask_the_gateway()
    {
        var vm = await PanelAsync("1.0.10", Hook + "observability:\n  tokenomics:\n    enabled: true\n");
        var stub = new UsageStub();
        vm.ModelUsageFetch = stub.Fetch;

        vm.Apply(Snapshot(Health(DisabledRouting)));
        await vm.RefreshModelUsageAsync(force: true, CancellationToken.None);

        Assert.Equal("on, local usage store off, no usage kept", Row(vm, "Tokenomics")!.Value);
        Assert.Equal(0, stub.Calls);
    }

    [Fact]
    public async Task The_local_store_alone_is_off_the_runtime_needs_both()
    {
        var vm = await PanelAsync("1.0.10", Hook + "observability:\n  tokenomics:\n    local_store:\n      enabled: true\n");
        var stub = new UsageStub();
        vm.ModelUsageFetch = stub.Fetch;

        vm.Apply(Snapshot(Health(DisabledRouting)));
        await vm.RefreshModelUsageAsync(force: true, CancellationToken.None);

        Assert.Equal("off, no usage recorded", Row(vm, "Tokenomics")!.Value);
        Assert.Equal(0, stub.Calls);
    }

    // ------------------------------------------------------------------ older runtimes

    [Theory]
    [InlineData("1.0.9")]
    [InlineData("1.0.5")]
    public async Task An_older_runtime_has_no_routing_or_tokenomics_rows_and_never_asks_for_usage(string version)
    {
        var vm = await PanelAsync(version, Hook + TokenomicsOn);
        var stub = new UsageStub();
        vm.ModelUsageFetch = stub.Fetch;

        vm.Apply(Snapshot(Health("{\"state\":\"running\"," + Since + ",\"details\":{\"enabled\":true,\"classifier\":\"rules\",\"model_count\":2,\"route_only\":[{\"connector\":\"codex\",\"ready\":false,\"cause\":\"upstream_not_set\"}]}}")));
        await vm.RefreshModelUsageAsync(force: true, CancellationToken.None);

        Assert.DoesNotContain(Rows(vm), static r => r.Label is "Model routing" or "Tokenomics" || r.Label.StartsWith("Route-only", StringComparison.Ordinal));
        Assert.Equal(0, stub.Calls);
        Assert.Empty(RoutingAttention(vm));
    }

    [Fact]
    public async Task A_1_0_9_runtimes_card_is_the_same_whatever_the_gateway_puts_in_routing()
    {
        var vm = await PanelAsync("1.0.9", Hook);
        vm.Apply(Snapshot(Health(null)));
        var without = Shape(vm);

        vm = await PanelAsync("1.0.9", Hook);
        vm.Apply(Snapshot(Health("{\"state\":\"running\",\"last_error\":\"boom\"," + Since + ",\"details\":{\"enabled\":true,\"classifier\":\"vllm_sr\",\"model_count\":2}}")));
        var with = Shape(vm);

        Assert.Equal(without, with);
        Assert.DoesNotContain(with, static r => r.Item1.Contains("routing", StringComparison.OrdinalIgnoreCase) || r.Item1.Contains("Tokenomics", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_1_0_10_card_differs_from_the_1_0_9_card_only_by_the_two_new_rows()
    {
        var older = await PanelAsync("1.0.9", Hook);
        older.Apply(Snapshot(Health(DisabledRouting)));
        var before = Shape(older);

        var newer = await PanelAsync("1.0.10", Hook);
        newer.Apply(Snapshot(Health(DisabledRouting)));
        var after = Shape(newer);

        Assert.Equal(before, after.Where(static r => r.Item1 is not ("Model routing" or "Tokenomics")).ToList());
    }
}
