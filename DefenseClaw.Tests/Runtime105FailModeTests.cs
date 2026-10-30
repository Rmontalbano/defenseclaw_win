using DefenseClaw.Core.Config;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.Tests;

/// <summary>
/// DefenseClaw 1.0.5's fail-mode rule (CUST-397): from 1.0.5 a saved per-user <c>hook_fail_mode</c> (the connector's own, else the global one) wins over the
/// policy tier, which decides only when nothing is saved (<c>hook_outage_fail_mode.go</c> <c>HookOutageFailMode</c>, <c>config.py</c>
/// <c>effective_hook_fail_mode</c>, <c>saved_hook_fail_mode_scope</c> and <c>outage_block_hint</c> at tag 1.0.5). 1.0.4 keeps its "a saved value is not applied"
/// reading, and older runtimes and managed installs are unchanged.
/// </summary>
public sealed class Runtime105FailModeTests
{
    private const string Help = "Usage: defenseclaw [OPTIONS] COMMAND [ARGS]...\n\nCommands:\n  setup  Configure.\n";

    private static RuntimeCapabilities Capabilities(string version) =>
        RuntimeProbe.Evaluate(
            new RuntimeProbeScreens("{\"name\":\"defenseclaw-cli\",\"version\":\"" + version + "\",\"schema_version\":1}", Help, Help, null, null, null, null, null),
            "test", "fp", DateTimeOffset.UnixEpoch).Capabilities;

    // ------------------------------------------------------------------ the capability

    [Theory]
    [InlineData("1.0.5", true)]
    [InlineData("1.0.6", true)]
    [InlineData("1.1.0", true)]
    [InlineData("1.0.4", false)]
    [InlineData("1.0.3", false)]
    [InlineData("1.0.0", false)]
    [InlineData("0.8.10", false)]
    [InlineData("nightly", false)]
    public void The_capability_follows_the_reported_version(string version, bool expected) =>
        Assert.Equal(expected, Capabilities(version).Has(RuntimeCapability.SavedFailModeWins));

    [Fact]
    public void A_runtime_that_cannot_list_its_commands_has_not_shown_it_and_About_names_it()
    {
        var helpless = RuntimeProbe.Evaluate(
            new RuntimeProbeScreens("{\"version\":\"1.0.5\"}", null, null, null, null, null, null, null), "test", "fp", DateTimeOffset.UnixEpoch).Capabilities;

        Assert.False(helpless.Has(RuntimeCapability.SavedFailModeWins));
        Assert.False(RuntimeCapabilities.Unknown.Has(RuntimeCapability.SavedFailModeWins));
        Assert.Contains(RuntimeCapability.SavedFailModeWins, RuntimeCapabilityCatalog.All);
        Assert.Contains("1.0.5", RuntimeCapabilityCatalog.Marker(RuntimeCapability.SavedFailModeWins), StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(RuntimeCapabilityCatalog.DisplayName(RuntimeCapability.SavedFailModeWins)));
    }

    // ------------------------------------------------------------------ the rule, by runtime and installation

    [Theory]
    [InlineData("1.0.3", false, false, false)]
    [InlineData("1.0.3", true, false, false)]
    [InlineData("1.0.4", false, true, false)]
    [InlineData("1.0.4", true, false, false)]
    [InlineData("1.0.5", false, false, true)]
    [InlineData("1.0.5", true, false, false)]
    public void Whether_a_saved_value_is_ignored_or_wins_depends_on_the_release_and_the_installation(string version, bool managed, bool ignored, bool wins)
    {
        var capabilities = Capabilities(version);

        Assert.Equal(ignored, HookFailModeRule.SavedValueIgnored(capabilities, managed));
        Assert.Equal(wins, HookFailModeRule.SavedValueWins(capabilities, managed));
    }

    [Fact]
    public void Nothing_known_is_neither()
    {
        Assert.False(HookFailModeRule.SavedValueWins(null, managed: false));
        Assert.False(HookFailModeRule.SavedValueWins(RuntimeCapabilities.Unknown, managed: false));
        Assert.False(HookFailModeRule.SavedValueIgnored(RuntimeCapabilities.Unknown, managed: false));
    }

    /// <summary>
    /// The connector's own saved value decides first, then the global one, then (nothing saved) the policy tier; the hint to close an outage follows the same
    /// order (<c>outage_block_hint</c>). The table is crossed with every runtime and installation above: only the per-user 1.0.5 row reads a saved value as
    /// deciding, and the scope and hint are the same functions there.
    /// </summary>
    [Theory]
    [InlineData("guardrail:\n  connectors:\n    codex:\n      hook_fail_mode: open\n", SavedFailModeScope.Connector, "open", "defenseclaw guardrail fail-mode closed --connector codex")]
    [InlineData("guardrail:\n  hook_fail_mode: closed\n  connectors:\n    codex:\n      hook_fail_mode: OPEN\n", SavedFailModeScope.Connector, "open", "defenseclaw guardrail fail-mode closed --connector codex")]
    [InlineData("guardrail:\n  hook_fail_mode: open\n  connectors:\n    codex:\n      mode: observe\n", SavedFailModeScope.Global, "open", "defenseclaw guardrail fail-mode closed")]
    [InlineData("guardrail:\n  hook_fail_mode: bogus\n", SavedFailModeScope.Global, "closed", "defenseclaw guardrail fail-mode closed")]
    [InlineData("guardrail:\n  hook_fail_mode: \"  \"\n  connectors:\n    codex:\n      hook_fail_mode: \"\"\n", SavedFailModeScope.None, null, "defenseclaw guardrail use-pack strict")]
    [InlineData("guardrail:\n  enabled: true\n", SavedFailModeScope.None, null, "defenseclaw guardrail use-pack strict")]
    public void The_saved_value_that_decides_and_the_hint_follow_the_runtimes_order(string yaml, SavedFailModeScope scope, string? mode, string hint)
    {
        var guardrail = ConfigStore.Parse(yaml).Config.Guardrail;

        Assert.Equal(scope, HookFailModeRule.SavedScope(guardrail, "codex"));
        Assert.Equal(mode, HookFailModeRule.SavedMode(guardrail, "codex"));
        Assert.Equal(hint, HookFailModeRule.OutageBlockHint(scope, "codex"));
    }

    [Fact]
    public void Config_reads_the_global_fail_mode_and_both_rule_packs_with_lf_and_crlf()
    {
        foreach (var newline in new[] { "\n", "\r\n" })
        {
            var yaml = string.Join(newline, "guardrail:", "  hook_fail_mode: open", "  rule_pack: strict", "  connectors:", "    codex:", "      rule_pack: permissive", "");
            var guardrail = ConfigStore.Parse(yaml).Config.Guardrail;

            Assert.Equal("open", guardrail.HookFailMode);
            Assert.Equal("strict", guardrail.RulePack);
            Assert.Equal("permissive", guardrail.Connectors["codex"].RulePack);
        }
    }

    [Theory]
    [InlineData("guardrail:\n  rule_pack: strict\n", "strict")]
    [InlineData("guardrail:\n  rule_pack: strict\n  connectors:\n    codex:\n      rule_pack: default\n", "default")]
    [InlineData("guardrail:\n  connectors:\n    codex:\n      rule_pack: Permissive\n", "permissive")]
    [InlineData("guardrail:\n  connectors:\n    codex:\n      rule_pack_dir: C:\\packs\\strict\\\n", "strict")]
    [InlineData("guardrail:\n  rule_pack: team-pack\n", null)]
    [InlineData("guardrail:\n  enabled: true\n", null)]
    public void The_policy_tier_is_known_only_where_config_yaml_names_a_built_in_pack(string yaml, string? tier) =>
        Assert.Equal(tier, HookFailModeRule.KnownPolicyTier(ConfigStore.Parse(yaml).Config.Guardrail, "codex"));

    [Fact]
    public void The_1_0_5_rule_text_is_the_runtimes_own()
    {
        Assert.StartsWith("On a per-user install an explicit saved mode wins. When unset, the gateway-outage mode follows the policy tier", HookFailModeRule.SavedWinsOutageRule, StringComparison.Ordinal);
        Assert.Contains("follows the policy tier", HookFailModeRule.PerUserOutageRule, StringComparison.Ordinal);
        Assert.DoesNotContain("saved mode wins", HookFailModeRule.PerUserOutageRule, StringComparison.Ordinal);
    }
}
