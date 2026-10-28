using System.Text.Json;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Gateway;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.Tests.Overview;

/// <summary>
/// The Overview's three policy cards (CUST-375) and the Tools panel's read-only operator lists, over a synthetic install: the Effective policy card in each
/// state it can be in (supported, unsupported by the runtime, unsupported by the gateway, error, nothing answering), that "Base configuration" only appears
/// beside it, and the config.yaml-derived Admission overrides and Operator lists cards, which are hidden when empty. The gateway is a function; no socket is opened.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class OverviewPolicyCardsTests : IDisposable
{
    private const string Resolved =
        "{\"profiles_configured\":true,\"user\":\"operator\",\"connector\":\"claudecode\",\"profile\":\"engineers\",\"match\":\"default\"," +
        "\"effective\":{\"mode\":\"action\",\"block_at\":\"HIGH\",\"alert_at\":\"LOW\",\"rule_pack_dir\":\"strict\"},\"warnings\":[]}";

    private const string Health = "{\"state\":\"running\",\"policy\":{\"effective_digest\":\"sha256:live\",\"generation\":12}}";

    private const string Config =
        "gateway:\n  api_port: 39872\nguardrail:\n  connector: claudecode\n  enabled: true\n  connectors:\n    claudecode:\n      mode: observe\n" +
        "admission:\n  skill:\n    actions:\n      high: block\n      low:\n        install: none\n        runtime: enable\n" +
        "asset_policy:\n  skill:\n    denied:\n      - name: bad-skill\n  tool:\n    denied:\n      - name: delete_file\n        connector: claudecode\n    allowed:\n      - name: read_file\n";

    private readonly List<TempDirectory> _temps = new();
    private readonly List<AppServices> _services = new();

    public void Dispose()
    {
        foreach (var services in _services)
        {
            services.Dispose();
        }

        foreach (var temp in _temps)
        {
            temp.Dispose();
        }
    }

    private AppServices Open(string runtime, string config = Config)
    {
        var temp = new TempDirectory();
        _temps.Add(temp);
        var services = TestServices.Create(temp, config, RuntimeFixtureRunner.For(runtime));
        _services.Add(services);
        _ = services.Runtime.RefreshAsync().GetAwaiter().GetResult();
        return services;
    }

    private static GatewayResult<JsonDocument> Ok(string body) => GatewayResult<JsonDocument>.Ok(JsonDocument.Parse(body));

    private sealed class FakeGateway(Func<string, GatewayResult<JsonDocument>> answer)
    {
        public List<string> Paths { get; } = new();

        public Task<GatewayResult<JsonDocument>> Fetch(string path, int maxBytes, CancellationToken cancellationToken)
        {
            Paths.Add(path);
            return Task.FromResult(answer(path));
        }
    }

    private static GatewayResult<JsonDocument> Healthy(string path) =>
        path.StartsWith("api/v1/guardrail/profiles/resolve", StringComparison.Ordinal) ? Ok(Resolved) : Ok(Health);

    private static OverviewPanelViewModel Panel(AppServices services, FakeGateway gateway) =>
        new(services) { EffectivePolicyFetch = gateway.Fetch };

    // ---- Effective policy -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_supported_runtime_shows_the_resolved_policy_and_relabels_the_configuration_card()
    {
        var gateway = new FakeGateway(Healthy);
        var vm = Panel(Open("95159fd"), gateway);
        Assert.Equal("Configuration", vm.ConfigurationTitle);

        await vm.RefreshEffectivePolicyAsync(force: true, CancellationToken.None);

        Assert.Equal(EffectivePolicyCardState.Shown, vm.EffectivePolicyState);
        Assert.True(vm.ShowEffectivePolicy);
        Assert.False(vm.EffectivePolicyIsError);
        Assert.Equal("Effective policy · claudecode", vm.EffectivePolicyTitle);
        var rows = vm.EffectivePolicyRows.ToDictionary(row => row.Label, row => row.Value);
        Assert.Equal("engineers", rows["Profile"]);
        Assert.Equal("default", rows["Matched by"]);
        Assert.Equal("action", rows["Mode"]);
        Assert.Equal("HIGH", rows["Block at"]);
        Assert.Equal("LOW", rows["Alert at"]);
        Assert.Equal("strict", rows["Rule pack"]);
        Assert.Equal("sha256:live", rows["Policy digest"]);
        Assert.Equal("12", rows["Generation"]);
        Assert.EndsWith(" on claudecode", rows["Applies to"], StringComparison.Ordinal);
        Assert.Equal("Base configuration", vm.ConfigurationTitle);

        Assert.StartsWith("api/v1/guardrail/profiles/resolve?user=", gateway.Paths[0], StringComparison.Ordinal);
        Assert.EndsWith("&connector=claudecode", gateway.Paths[0], StringComparison.Ordinal);
        Assert.Equal("health", gateway.Paths[1]);
    }

    [Fact]
    public async Task The_scoped_mode_row_reads_Configured_mode_only_while_the_effective_card_is_shown()
    {
        var services = Open("95159fd");
        var vm = Panel(services, new FakeGateway(Healthy));
        services.ConnectorScope.UpdateRoster(new[] { "claudecode", "hermes" });
        Assert.True(services.ConnectorScope.Set("claudecode"));
        vm.BuildConfiguration();
        vm.ToggleConfigurationCommand.Execute(null);
        Assert.Contains(vm.ConfigurationRows, row => row.Label == "Mode");

        await vm.RefreshEffectivePolicyAsync(force: true, CancellationToken.None);

        Assert.DoesNotContain(vm.ConfigurationRows, row => row.Label == "Mode");
        Assert.Contains(vm.ConfigurationRows, row => row.Label == "Configured mode");
        Assert.Equal("Base configuration · claudecode", vm.ConfigurationTitle);
    }

    [Fact]
    public async Task A_runtime_without_the_capability_never_asks_and_looks_exactly_as_before()
    {
        var gateway = new FakeGateway(Healthy);
        var vm = Panel(Open("0.8.10"), gateway);

        await vm.RefreshEffectivePolicyAsync(force: true, CancellationToken.None);

        Assert.Empty(gateway.Paths);
        Assert.Equal(EffectivePolicyCardState.Hidden, vm.EffectivePolicyState);
        Assert.False(vm.ShowEffectivePolicy);
        Assert.Empty(vm.EffectivePolicyRows);
        Assert.Equal("Configuration", vm.ConfigurationTitle);
    }

    [Theory]
    [InlineData(404)]
    [InlineData(405)]
    public async Task A_gateway_that_does_not_serve_the_route_hides_the_card_again_and_restores_the_title(int code)
    {
        var supported = true;
        var gateway = new FakeGateway(path => supported ? Healthy(path) : GatewayResult<JsonDocument>.Error("HTTP " + code, code));
        var vm = Panel(Open("95159fd"), gateway);
        await vm.RefreshEffectivePolicyAsync(force: true, CancellationToken.None);
        Assert.Equal("Base configuration", vm.ConfigurationTitle);

        supported = false;
        await vm.RefreshEffectivePolicyAsync(force: true, CancellationToken.None);

        Assert.Equal(EffectivePolicyCardState.Hidden, vm.EffectivePolicyState);
        Assert.Empty(vm.EffectivePolicyRows);
        Assert.Equal(string.Empty, vm.EffectivePolicyNote);
        Assert.Equal("Configuration", vm.ConfigurationTitle);
    }

    [Fact]
    public async Task An_answer_that_is_not_a_policy_shows_the_error_without_rows_and_still_relabels()
    {
        var gateway = new FakeGateway(path => path.StartsWith("api/", StringComparison.Ordinal)
            ? Ok("{\"profiles_configured\":true,\"profile\":\"engineers\",\"lookup_error\":\"user lookup failed\",\"effective\":{\"mode\":\"action\"}}")
            : Ok(Health));
        var vm = Panel(Open("95159fd"), gateway);

        await vm.RefreshEffectivePolicyAsync(force: true, CancellationToken.None);

        Assert.Equal(EffectivePolicyCardState.Error, vm.EffectivePolicyState);
        Assert.True(vm.ShowEffectivePolicy);
        Assert.True(vm.EffectivePolicyIsError);
        Assert.Empty(vm.EffectivePolicyRows);
        Assert.StartsWith("Effective policy unavailable: ", vm.EffectivePolicyNote, StringComparison.Ordinal);
        Assert.Contains("user lookup failed", vm.EffectivePolicyNote, StringComparison.Ordinal);
        Assert.Equal("Base configuration", vm.ConfigurationTitle);
    }

    [Fact]
    public async Task A_gateway_that_is_not_answering_leaves_no_card_and_no_blame()
    {
        var vm = Panel(Open("95159fd"), new FakeGateway(_ => GatewayResult<JsonDocument>.Unreachable()));

        await vm.RefreshEffectivePolicyAsync(force: true, CancellationToken.None);

        Assert.Equal(EffectivePolicyCardState.Hidden, vm.EffectivePolicyState);
        Assert.Equal(string.Empty, vm.EffectivePolicyNote);
    }

    [Fact]
    public async Task The_same_account_and_connector_are_not_asked_again_within_the_interval_unless_forced()
    {
        var gateway = new FakeGateway(Healthy);
        var vm = Panel(Open("95159fd"), gateway);

        await vm.RefreshEffectivePolicyAsync(force: true, CancellationToken.None);
        var asked = gateway.Paths.Count;
        await vm.RefreshEffectivePolicyAsync(force: false, CancellationToken.None);
        Assert.Equal(asked, gateway.Paths.Count);

        await vm.RefreshEffectivePolicyAsync(force: true, CancellationToken.None);
        Assert.True(gateway.Paths.Count > asked);
    }

    // ---- Admission overrides and Operator lists ---------------------------------------------------------------------------------

    [Fact]
    public void Admission_overrides_and_operator_lists_are_drawn_from_config_yaml_with_counts_per_type()
    {
        var vm = Panel(Open("95159fd"), new FakeGateway(Healthy));

        vm.BuildConfiguration();

        Assert.True(vm.ShowAdmissionOverrides);
        Assert.Equal(
            new[] { ("skill · high", "block"), ("skill · low", "install=none, runtime=enable") },
            vm.AdmissionOverrideRows.Select(row => (row.Label, row.Value)).ToArray());
        Assert.True(vm.ShowOperatorLists);
        Assert.Equal(
            new[] { ("Skills", "1 blocked · 0 allowed"), ("Tools", "1 blocked · 1 allowed") },
            vm.OperatorListRows.Select(row => (row.Label, row.Value)).ToArray());
        Assert.Contains("3 rules", vm.OperatorListsNote, StringComparison.Ordinal);
        Assert.Contains("Tools", vm.OperatorListsNote, StringComparison.Ordinal);
    }

    [Fact]
    public void A_config_with_no_overrides_or_lists_hides_both_cards()
    {
        var vm = Panel(Open("0.8.10", "gateway:\n  api_port: 39872\n"), new FakeGateway(Healthy));

        vm.BuildConfiguration();

        Assert.False(vm.ShowAdmissionOverrides);
        Assert.Empty(vm.AdmissionOverrideRows);
        Assert.False(vm.ShowOperatorLists);
        Assert.Empty(vm.OperatorListRows);
        Assert.Equal(string.Empty, vm.OperatorListsNote);
    }

    [Fact]
    public void A_secure_client_install_shows_no_operator_lists()
    {
        var vm = Panel(Open("95159fd", "deployment_mode: managed_enterprise\nasset_policy:\n  skill:\n    denied:\n      - name: bad-skill\n"), new FakeGateway(Healthy));

        vm.BuildConfiguration();

        Assert.False(vm.ShowOperatorLists);
    }

    // ---- Tools panel ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_tools_panel_lists_every_rule_read_only_with_its_decision_and_scope()
    {
        var tools = new ToolsPanelViewModel(Open("95159fd"));

        tools.BuildOperatorLists();

        Assert.True(tools.ShowOperatorLists);
        Assert.Equal(new[] { "Skills", "Tools" }, tools.OperatorListSections.Select(section => section.Title).ToArray());
        var toolSection = tools.OperatorListSections.Single(section => section.Title == "Tools");
        Assert.Equal("1 blocked · 1 allowed", toolSection.Counts);
        Assert.Equal(
            new[] { ("blocked", "@claudecode/delete_file"), ("allowed", "read_file") },
            toolSection.Entries.Select(entry => (entry.Decision, entry.Target)).ToArray());
        Assert.Equal(new[] { "Bad", "Ok" }, toolSection.Entries.Select(entry => entry.ToneKey).ToArray());
        Assert.Contains("read-only", tools.OperatorListsSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void The_tools_panel_has_no_section_for_a_config_without_lists_and_explains_a_secure_client_install()
    {
        var empty = new ToolsPanelViewModel(Open("0.8.10", "gateway:\n  api_port: 39872\n"));
        empty.BuildOperatorLists();
        Assert.False(empty.ShowOperatorLists);
        Assert.Empty(empty.OperatorListSections);

        var secure = new ToolsPanelViewModel(Open("95159fd", "deployment_mode: managed_enterprise\nasset_policy:\n  tool:\n    denied:\n      - name: x\n"));
        secure.BuildOperatorLists();
        Assert.True(secure.ShowOperatorLists);
        Assert.Empty(secure.OperatorListSections);
        Assert.Contains("Secure Client", secure.OperatorListsSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void The_new_capability_is_judged_from_a_1_0_runtime_and_absent_from_0_8_10()
    {
        Assert.True(Open("95159fd").Runtime.Check(RuntimeCapability.EffectivePolicy).IsAvailable);
        Assert.False(Open("0.8.10").Runtime.Check(RuntimeCapability.EffectivePolicy).IsAvailable);
    }
}
