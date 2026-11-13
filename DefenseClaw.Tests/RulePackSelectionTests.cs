using DefenseClaw.Core.Config;

namespace DefenseClaw.Tests;

/// <summary>
/// The pack config.yaml selects, as the runtime picks it (guardrail_rules.go@1.0.9:64-110, unchanged since 1.0.0): the connector's entry before the global
/// block, and at one scope <c>rule_pack</c> before <c>rule_pack_dir</c>.
/// </summary>
public sealed class RulePackSelectionTests
{
    private static GuardrailSection Guardrail(string yaml) => ConfigStore.Parse(yaml).Config.Guardrail;

    [Theory]
    // A config_version 9 file as setup writes it: a global name.
    [InlineData("guardrail:\n  rule_pack: strict\n", "strict", "strict")]
    // The name wins over a folder at the same scope.
    [InlineData("guardrail:\n  rule_pack: strict\n  rule_pack_dir: X:\\data\\policies\\guardrail\\default\n", "strict", "strict")]
    // A connector's own name wins over the global one.
    [InlineData("guardrail:\n  rule_pack: strict\n  connectors:\n    codex:\n      rule_pack: permissive\n", "permissive", "permissive")]
    // A connector entry decides as a whole: its folder beats a global name.
    [InlineData("guardrail:\n  rule_pack: strict\n  connectors:\n    codex:\n      rule_pack_dir: X:\\packs\\permissive\n", "permissive", "permissive")]
    // A connector entry without a pack inherits the global one.
    [InlineData("guardrail:\n  rule_pack: strict\n  connectors:\n    codex:\n      mode: observe\n", "strict", "strict")]
    // The global v8 folder.
    [InlineData("guardrail:\n  rule_pack_dir: X:\\data\\policies\\guardrail\\strict\\\n", "strict", "strict")]
    // A custom pack name: its posture stays unknown.
    [InlineData("guardrail:\n  rule_pack: team-pack\n", "team-pack", null)]
    // Nothing set is the built-in default pack; the tier note stays off.
    [InlineData("guardrail:\n  enabled: true\n", "default", null)]
    public void The_pack_is_picked_the_way_the_runtime_picks_it(string yaml, string name, string? tier)
    {
        var selection = RulePackSelection.For(Guardrail(yaml), "codex");

        Assert.Equal(name, selection.DisplayName);
        Assert.Equal(tier, selection.KnownTier(_ => null));
        Assert.Equal(tier, HookFailModeRule.KnownPolicyTier(Guardrail(yaml), "codex", _ => null));
    }

    [Fact]
    public void The_installers_global_default_folder_reads_default()
    {
        var guardrail = Guardrail("guardrail:\n  enabled: true\n  rule_pack_dir: C:\\Users\\someone\\.defenseclaw\\policies\\guardrail\\default\n");
        var selection = RulePackSelection.For(guardrail, "codex");

        Assert.Equal("default", selection.Label(_ => "{\"posture\":\"default\"}"));
        Assert.Equal("default", selection.Label(_ => null));
        Assert.Equal("default", HookFailModeRule.KnownPolicyTier(guardrail, "codex", _ => null));
    }

    [Fact]
    public void A_folder_whose_manifest_names_another_posture_shows_both()
    {
        var selection = RulePackSelection.For(Guardrail("guardrail:\n  rule_pack_dir: X:\\packs\\mypack\n"), "codex");

        Assert.Equal("mypack (strict)", selection.Label(_ => "{\"posture\":\"strict\"}"));
        Assert.Equal("mypack", selection.Label(_ => null));
    }

    [Fact]
    public void A_built_in_name_is_not_read_from_disk()
    {
        var selection = RulePackSelection.For(Guardrail("guardrail:\n  rule_pack: Strict\n"), "codex");

        Assert.Equal("Strict", selection.Label(_ => throw new InvalidOperationException("no read expected")));
    }
}
