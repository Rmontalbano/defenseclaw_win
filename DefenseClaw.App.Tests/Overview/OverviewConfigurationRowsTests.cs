using System.Reflection;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;

namespace DefenseClaw.App.Tests.Overview;

/// <summary>
/// The Configuration card's rows the 0.8.10 TUI prints that this one lacked (CUST-274; <c>_overview_renderable</c> in <c>tui/app.py</c>): Human approval,
/// Policy dir, LLM provider and model, and AI Defense - the last as a host and never as the URL the TUI prints. Each is mapped from a made-up config.yaml;
/// every credential in it starts with <c>synth</c>, and the tests hold that none of them reaches a row.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class OverviewConfigurationRowsTests : IDisposable
{
    private static readonly string[] Secrets = { "synthuser", "synthpass", "synthtoken", "synthkey", "synthpath-secret", "synthfrag" };

    private const string OneConnector = "guardrail:\n  connector: claudecode\n  enabled: true\n  connectors:\n    claudecode:\n      mode: observe\n      hook_fail_mode: open\n";

    private const string TwoConnectors = "guardrail:\n  connector: claudecode\n  enabled: true\n  connectors:\n    claudecode:\n      mode: observe\n      hook_fail_mode: open\n    hermes:\n      mode: action\n      hook_fail_mode: open\n";

    private readonly TempDirectory _temp = new();
    private AppServices? _services;

    public void Dispose()
    {
        _services?.Dispose();
        _temp.Dispose();
    }

    private OverviewPanelViewModel Panel(string configYaml, params string[] connectors)
    {
        _services?.Dispose();
        _services = TestServices.Create(_temp, configYaml);
        var vm = new OverviewPanelViewModel(_services);
        var snapshot = Snapshot(connectors.Length == 0 ? new[] { "claudecode" } : connectors);

        // The shared connector scope follows the monitor's roster, not the panel's: a scope can only be set on a connector the monitor reported.
        _ = typeof(GatewayMonitor).GetMethod("Publish", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(_services.Monitor, new object[] { snapshot });
        vm.Apply(snapshot);
        return vm;
    }

    private static GatewaySnapshot Snapshot(string[] connectors) => new()
    {
        State = AppGatewayState.Running,
        Install = DefenseClaw.Core.Install.InstallState.Running,
        Detail = "ok",
        ActiveConnectors = connectors,
        PolledAt = DateTimeOffset.UtcNow,
    };

    private static ConfigRow Row(OverviewPanelViewModel vm, string label)
    {
        if (!vm.ConfigurationExpanded && vm.HasConfigurationOverflow)
        {
            vm.ToggleConfigurationCommand.Execute(null);
        }

        return vm.ConfigurationRows.Single(r => r.Label == label);
    }

    private static bool Has(OverviewPanelViewModel vm, string label)
    {
        if (!vm.ConfigurationExpanded && vm.HasConfigurationOverflow)
        {
            vm.ToggleConfigurationCommand.Execute(null);
        }

        return vm.ConfigurationRows.Any(r => r.Label == label);
    }

    // ------------------------------------------------------------------ everything set

    [Fact]
    public void A_config_that_sets_all_of_them_gives_the_tuis_rows_with_the_endpoint_as_a_host()
    {
        var vm = Panel(
            OneConnector.Replace("connectors:", "hilt:\n    enabled: true\n    min_severity: critical\n  connectors:", StringComparison.Ordinal) +
            "llm:\n  provider: openai\n  model: gpt-4o\n  api_key: synthkey\n" +
            "cisco_ai_defense:\n  endpoint: https://synthuser:synthpass@aidefense.example.test:8443/synthpath-secret/v1?token=synthtoken#synthfrag\n  api_key: synthkey\n" +
            "policy_dir: D:\\policies\\acme\n");

        var approval = Row(vm, "Human approval");
        Assert.Equal("ON (min CRITICAL)", approval.Value);
        Assert.Equal("Ok", approval.ToneKey);

        Assert.Equal(@"D:\policies\acme", Row(vm, "Policy dir").Value);
        Assert.Equal("openai", Row(vm, "LLM provider").Value);
        Assert.Equal("gpt-4o", Row(vm, "LLM model").Value);
        Assert.Equal("aidefense.example.test:8443", Row(vm, "AI Defense").Value);

        // No row, and no label, carries any part of the address or any key.
        var everything = string.Join('\n', vm.ConfigurationRows.Select(r => r.Label + "=" + r.Value));
        Assert.All(Secrets, secret => Assert.DoesNotContain(secret, everything, StringComparison.Ordinal));
        Assert.DoesNotContain("https://", everything, StringComparison.Ordinal);
    }

    [Fact]
    public void Human_approval_off_is_off_in_the_neutral_tone_and_a_connectors_own_block_wins_with_one_connector()
    {
        var off = Panel(OneConnector);
        Assert.Equal("OFF", Row(off, "Human approval").Value);
        Assert.Equal("Neutral", Row(off, "Human approval").ToneKey);

        var own = Panel(
            "guardrail:\n  hilt:\n    enabled: false\n  connectors:\n    claudecode:\n      mode: observe\n      hilt:\n        enabled: true\n        min_severity: low\n");
        Assert.Equal("ON (min LOW)", Row(own, "Human approval").Value);
    }

    [Fact]
    public void With_several_connectors_the_global_block_is_the_row_whatever_one_connector_says()
    {
        var vm = Panel(
            TwoConnectors.Replace("connectors:", "hilt:\n    enabled: true\n  connectors:", StringComparison.Ordinal).Replace("      mode: observe\n", "      mode: observe\n      hilt:\n        enabled: false\n", StringComparison.Ordinal),
            "claudecode",
            "hermes");

        Assert.Equal("ON (min HIGH)", Row(vm, "Human approval").Value);
    }

    // ------------------------------------------------------------------ what the config does not say

    [Fact]
    public void A_sparse_config_gets_the_runtimes_defaults_named_as_defaults_and_no_llm_rows()
    {
        var vm = Panel(OneConnector + "llm:\n  api_key_env: EXAMPLE_KEY\ncisco_ai_defense:\n  api_key_env: EXAMPLE_KEY\n");

        Assert.Equal("OFF", Row(vm, "Human approval").Value);
        Assert.Equal(Path.Combine(_services!.Paths.DataDirectory, "policies") + "  (default)", Row(vm, "Policy dir").Value);
        Assert.Equal(OverviewPanelViewModel.DefaultAiDefenseHost + "  (default)", Row(vm, "AI Defense").Value);
        Assert.False(Has(vm, "LLM provider"));
        Assert.False(Has(vm, "LLM model"));
    }

    [Fact]
    public void An_endpoint_set_empty_hides_the_row_and_a_policy_folder_set_empty_is_a_dash_as_in_the_tui()
    {
        var vm = Panel(OneConnector + "cisco_ai_defense:\n  endpoint: ''\npolicy_dir:\n");

        Assert.False(Has(vm, "AI Defense"));
        Assert.Equal("—", Row(vm, "Policy dir").Value);
    }

    [Fact]
    public void An_endpoint_with_no_host_the_app_can_vouch_for_is_never_shown_raw()
    {
        var vm = Panel(OneConnector + "cisco_ai_defense:\n  endpoint: https://synthuser:synthpass@exa mple.test/synthpath-secret\n");

        var row = Row(vm, "AI Defense");
        Assert.Equal("<unreadable endpoint>", row.Value);
        Assert.DoesNotContain("synth", row.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void The_llm_rows_follow_the_loaders_order_so_an_older_config_still_shows_its_model()
    {
        var vm = Panel(OneConnector + "inspect_llm:\n  provider: bedrock\n  model: anthropic.claude\ndefault_llm_model: from-default\n");

        Assert.Equal("bedrock", Row(vm, "LLM provider").Value);
        Assert.Equal("from-default", Row(vm, "LLM model").Value);
    }

    // ------------------------------------------------------------------ where they sit

    [Fact]
    public void The_rows_sit_with_what_they_belong_to_in_the_card()
    {
        var vm = Panel(OneConnector + "llm:\n  provider: openai\n  model: gpt-4o\n");
        vm.ToggleConfigurationCommand.Execute(null);

        var labels = vm.ConfigurationRows.Select(r => r.Label).ToList();
        int At(string label) => labels.IndexOf(label);

        // Redaction stays right behind Guardrail and Deployment mode right behind it (the Observability card's arrangement); human approval follows those.
        Assert.Equal(At("Guardrail") + 1, At("Redaction"));
        Assert.Equal(At("Redaction") + 1, At("Deployment mode"));
        Assert.True(At("Deployment mode") < At("Human approval") && At("Human approval") < At("AI discovery"), string.Join(" | ", labels));
        Assert.True(At("AI discovery") < At("LLM provider"), string.Join(" | ", labels));
        Assert.True(At("LLM provider") < At("LLM model") && At("LLM model") < At("AI Defense"), string.Join(" | ", labels));
        Assert.True(At("AI Defense") < At("Gateway API"), string.Join(" | ", labels));
        Assert.True(At("Policy dir") >= 0 && At("Policy dir") < At("Data directory"), string.Join(" | ", labels));

        // The zebra stripe still alternates over the longer list.
        Assert.All(vm.ConfigurationRows.Select((r, i) => (r, i)), pair => Assert.Equal(pair.i % 2 == 1, pair.r.Alternate));
    }

    [Fact]
    public void The_first_four_rows_are_still_the_ones_an_operator_looks_at_first()
    {
        var vm = Panel(OneConnector);

        Assert.Equal(OverviewPanelViewModel.ConfigurationRowsShown, vm.ConfigurationRows.Count);
        Assert.Equal(new[] { "Agent", "Policy posture", "Enforcement", "Hook fail mode" }, vm.ConfigurationRows.Select(r => r.Label).ToArray());
    }

    // ------------------------------------------------------------------ scoped to one connector

    [Fact]
    public void Scoped_to_a_connector_the_new_rows_are_the_global_ones_and_say_so()
    {
        var vm = Panel(
            TwoConnectors.Replace("connectors:", "hilt:\n    enabled: true\n    min_severity: medium\n  connectors:", StringComparison.Ordinal) +
            "llm:\n  provider: openai\n  model: gpt-4o\ncisco_ai_defense:\n  endpoint: https://synthuser:synthpass@aidefense.example.test/synthpath-secret\n",
            "claudecode",
            "hermes");
        _services!.ConnectorScope.Set("hermes");
        vm.ApplyScope();

        Assert.Equal("ON (min MEDIUM)", Row(vm, "Human approval (global)").Value);
        Assert.Equal("openai", Row(vm, "LLM provider (global)").Value);
        Assert.Equal("gpt-4o", Row(vm, "LLM model (global)").Value);
        Assert.Equal("aidefense.example.test", Row(vm, "AI Defense (global)").Value);
        Assert.True(Has(vm, "Policy dir (global)"));
        Assert.False(Has(vm, "Human approval"));

        var everything = string.Join('\n', vm.ConfigurationRows.Select(r => r.Label + "=" + r.Value));
        Assert.All(Secrets, secret => Assert.DoesNotContain(secret, everything, StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ a config that changes under the page

    [Fact]
    public void A_reloaded_config_changes_the_rows_with_the_next_build()
    {
        var vm = Panel(OneConnector);
        Assert.Equal("OFF", Row(vm, "Human approval").Value);

        _ = _temp.WriteFile("config.yaml", OneConnector.Replace("connectors:", "hilt:\n    enabled: true\n  connectors:", StringComparison.Ordinal) + "llm:\n  model: gpt-4o\n");
        _services!.ReloadConfig();
        vm.Apply(Snapshot(new[] { "claudecode" }));

        Assert.Equal("ON (min HIGH)", Row(vm, "Human approval").Value);
        Assert.Equal("gpt-4o", Row(vm, "LLM model").Value);

        // A config that stops saying it takes the row back to the default.
        _ = _temp.WriteFile("config.yaml", OneConnector);
        _services.ReloadConfig();
        vm.Apply(Snapshot(new[] { "claudecode" }));
        Assert.Equal("OFF", Row(vm, "Human approval").Value);
        Assert.False(Has(vm, "LLM model"));
    }

    [Fact]
    public void A_config_that_is_not_yaml_keeps_the_last_good_rows_and_adds_no_facts_of_its_own()
    {
        var vm = Panel(OneConnector + "policy_dir: D:\\policies\\acme\n");
        _ = _temp.WriteFile("config.yaml", "guardrail: [unclosed\n");
        _services!.ReloadConfig();

        vm.Apply(Snapshot(new[] { "claudecode" }));

        // The reload failed, so the last good document is still the one in force, and the rows are still its.
        Assert.Equal(@"D:\policies\acme", Row(vm, "Policy dir").Value);
    }
}
