using DefenseClaw.Core.Config;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.Tests;

/// <summary>Which config.yaml changes the running gateway applies by itself and which need a restart (CUST-377), against configwrite.restartKeys at 1.0.4.</summary>
public sealed class ConfigReloadRulesTests
{
    private static ConfigReloadVerdict Classify(string before, string after) => ConfigReloadRules.Classify(before, after);

    [Theory]
    [InlineData("guardrail.mode", false)]
    [InlineData("guardrail.block_at", false)]
    [InlineData("guardrail.hilt.min_severity", false)]
    [InlineData("guardrail.judge.model", false)]
    [InlineData("guardrail.connectors.claudecode.enabled", false)]
    [InlineData("guardrail.connectors.claudecode.mode", false)]
    [InlineData("guardrail.profile_assignments", false)]
    [InlineData("llm.model", false)]
    [InlineData("scanners.mcp_scanner.unverified_server", false)]
    [InlineData("openshell.trusted_ca_bundle", false)]
    [InlineData("observability.destinations", false)]
    [InlineData("notifications.enabled", false)]
    [InlineData("asset_policy.mode", false)]
    [InlineData("ai_discovery.enabled", false)]
    [InlineData("gateway.watcher.enabled", false)]
    [InlineData("gateway.config_reload.debounce_ms", false)]
    [InlineData("policy_dir", false)]
    [InlineData("guardrail.host", true)]
    [InlineData("guardrail.port", true)]
    [InlineData("guardrail.enabled", true)]
    [InlineData("guardrail.connector", true)]
    [InlineData("guardrail.scanner_mode", true)]
    [InlineData("guardrail.retain_judge_bodies", true)]
    [InlineData("guardrail.hook_self_heal", true)]
    [InlineData("guardrail.hook_self_heal_debounce_ms", true)]
    [InlineData("gateway.api_port", true)]
    [InlineData("gateway.host", true)]
    [InlineData("gateway.token_env", true)]
    [InlineData("gateway.config_reload.mode", true)]
    [InlineData("data_dir", true)]
    [InlineData("observability.local.path", true)]
    [InlineData("claw.mode", true)]
    [InlineData("agent.id", true)]
    [InlineData("routing.enabled", true)]
    [InlineData("plugin_dir", true)]
    [InlineData("environment", true)]
    [InlineData("deployment_mode", true)]
    [InlineData("enterprise.profile", true)]
    [InlineData("fleet.mqtt_broker_url", true)]
    [InlineData("fleet.auto_register", false)]
    [InlineData("audit_db", true)]
    [InlineData("some_future_section.key", true)]
    public void A_key_is_hot_unless_it_is_in_the_restart_set_or_a_section_the_gateway_does_not_reload(string path, bool needsRestart)
    {
        Assert.Equal(needsRestart, ConfigReloadRules.NeedsRestart(path.Split('.')));
    }

    [Fact]
    public void The_restart_set_is_the_one_configwrite_ships()
    {
        // restartKeys, in order (internal/config/configwrite/restart_keys.go at 1.0.4; config_writer.RESTART_KEYS is its Python mirror).
        Assert.Equal(
            [
                "data_dir", "observability.local.path", "observability.local.judge_bodies_path", "gateway",
                "guardrail.host", "guardrail.port", "guardrail.enabled", "guardrail.connector",
                "guardrail.scanner_mode", "guardrail.retain_judge_bodies", "guardrail.hook_self_heal", "guardrail.hook_self_heal_debounce_ms",
                "claw", "agent", "routing", "plugin_dir",
                "deployment_mode", "enterprise.profile", "enterprise.network", "enterprise.openshell",
                "environment", "tenant_id", "workspace_id", "discovery_source",
                "fleet.mqtt_broker_url", "fleet.mqtt_require_tls", "fleet.ota_key_env", "fleet.emergency_key_env", "fleet.device_key_env",
                "fleet.mqtt_user_env", "fleet.mqtt_pass_env", "fleet.enabled",
            ],
            ConfigReloadRules.RestartKeys);
    }

    [Fact]
    public void A_change_to_a_hot_key_alone_is_hot_only_and_names_no_restart_path()
    {
        var verdict = Classify("guardrail:\n  mode: observe\n  block_at: HIGH\nllm:\n  model: a\n", "guardrail:\n  mode: action\n  block_at: HIGH\nllm:\n  model: b\n");

        Assert.True(verdict.Parsed);
        Assert.Equal(["guardrail.mode", "llm.model"], verdict.ChangedPaths);
        Assert.Empty(verdict.RestartPaths);
        Assert.True(verdict.HotOnly);
    }

    [Fact]
    public void One_restart_key_among_hot_ones_is_named_alone()
    {
        var verdict = Classify("guardrail:\n  mode: observe\n  port: 4000\n", "guardrail:\n  mode: action\n  port: 4001\n");

        Assert.Equal(["guardrail.mode", "guardrail.port"], verdict.ChangedPaths);
        Assert.Equal(["guardrail.port"], verdict.RestartPaths);
        Assert.False(verdict.HotOnly);
    }

    [Fact]
    public void Comments_spacing_order_and_list_layout_are_not_changes()
    {
        var verdict = Classify(
            "# a\nguardrail:\n  mode: observe\n  allowed:\n  - a\n  - b\nllm:\n  model: x\n",
            "llm:\n    model: x\n\nguardrail:\n    allowed: [a, b]\n    mode: observe   # same\n");

        Assert.True(verdict.Parsed);
        Assert.Empty(verdict.ChangedPaths);
        Assert.False(verdict.HotOnly);
    }

    [Fact]
    public void A_list_is_one_value_at_its_key()
    {
        var verdict = Classify("guardrail:\n  allowed:\n  - a\n", "guardrail:\n  allowed:\n  - a\n  - b\n");

        Assert.Equal(["guardrail.allowed"], verdict.ChangedPaths);
        Assert.True(verdict.HotOnly);
    }

    [Fact]
    public void A_gateway_set_to_restart_on_reload_restarts_for_everything()
    {
        var before = "gateway:\n  config_reload:\n    mode: restart\nllm:\n  model: a\n";
        var after = "gateway:\n  config_reload:\n    mode: restart\nllm:\n  model: b\n";

        Assert.Equal(["llm.model"], Classify(before, after).RestartPaths);

        // Turning it off is itself a restart-key change (mode), and everything else rides along.
        var turnedOff = Classify(before, "gateway:\n  config_reload:\n    mode: hot\nllm:\n  model: b\n");
        Assert.Contains("llm.model", turnedOff.RestartPaths);
        Assert.Contains("gateway.config_reload.mode", turnedOff.RestartPaths);
    }

    [Fact]
    public void Quoting_a_value_differently_is_a_change_because_the_type_differs()
    {
        var verdict = Classify("guardrail:\n  enabled: true\n", "guardrail:\n  enabled: \"true\"\n");

        Assert.Equal(["guardrail.enabled"], verdict.ChangedPaths);
        Assert.Equal(["guardrail.enabled"], verdict.RestartPaths);
    }

    [Theory]
    [InlineData("a: [unclosed\n", "a: 1\n")]
    [InlineData("a: 1\n", "a: [unclosed\n")]
    [InlineData("- a\n- b\n", "a: 1\n")]
    public void Text_that_cannot_be_compared_is_unknown_so_the_caller_keeps_its_old_assumption(string before, string after)
    {
        var verdict = Classify(before, after);

        Assert.False(verdict.Parsed);
        Assert.False(verdict.HotOnly);
        Assert.Same(ConfigReloadVerdict.Unknown, verdict);
    }

    [Fact]
    public void A_text_nested_past_the_parse_guard_is_unknown_not_a_hang()
    {
        var deep = "a: " + new string('[', ReadLimitsDepth + 1) + new string(']', ReadLimitsDepth + 1) + "\n";

        Assert.False(Classify(deep, "a: 1\n").Parsed);
    }

    private const int ReadLimitsDepth = DefenseClaw.Core.IO.ReadLimits.ConfigYamlFlowDepth;

    [Fact]
    public void Config_yaml_that_is_empty_on_one_side_is_every_key_added_or_removed()
    {
        var added = Classify(string.Empty, "llm:\n  model: a\n");
        var removed = Classify("llm:\n  model: a\n", "# nothing\n");

        Assert.Equal(["llm.model"], added.ChangedPaths);
        Assert.True(added.HotOnly);
        Assert.Equal(["llm.model"], removed.ChangedPaths);
        Assert.Empty(Classify(string.Empty, "# nothing here\n").ChangedPaths);
    }

    [Theory]
    [InlineData("1.0.4", true)]
    [InlineData("1.0.3", true)]
    [InlineData("1.0.0", true)]
    [InlineData("0.8.10", false)]
    [InlineData("nightly", false)]
    public void The_capability_follows_the_reported_version_and_an_unknown_runtime_has_neither_1_0_4_capability(string version, bool hotReload)
    {
        var capabilities = RuntimeProbe.Evaluate(
            new RuntimeProbeScreens(
                "{\"name\":\"defenseclaw-cli\",\"version\":\"" + version + "\",\"schema_version\":1}",
                "Usage: defenseclaw [OPTIONS] COMMAND [ARGS]...\n\nCommands:\n  setup  Configure.\n",
                "Usage: defenseclaw [OPTIONS] COMMAND [ARGS]...\n\nCommands:\n  setup  Configure.\n",
                null, null, null, null, null),
            "test",
            "fp",
            DateTimeOffset.UnixEpoch).Capabilities;

        Assert.Equal(hotReload, capabilities.Has(RuntimeCapability.GatewayConfigReload));
        Assert.Equal(version == "1.0.4", capabilities.Has(RuntimeCapability.Config104Keys));
        Assert.False(RuntimeCapabilities.Unknown.Has(RuntimeCapability.GatewayConfigReload));
        Assert.False(RuntimeCapabilities.Unknown.Has(RuntimeCapability.Config104Keys));
    }
}
