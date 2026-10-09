using DefenseClaw.Core.Config;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// Which registry promoted a skill or an MCP server (CUST-276): the TUI's <c>_registry_attribution_from_config</c> and
/// <c>parse_registry_source_id</c> of DefenseClaw 0.8.10, over synthetic <c>asset_policy</c> sections shaped like the ones
/// <c>registries/sync.py</c> writes (<c>_promote_to_asset_policy</c>: a rule per promoted entry, <c>reason: registry:&lt;source id&gt;</c>).
/// </summary>
public sealed class RegistryAttributionTests
{
    private const string Promoted = """
        config_version: 8
        asset_policy:
          enabled: true
          skill:
            default: allow
            registry_required: false
            registry:
              - name: pdf-tools
                connector: ""
                reason: registry:corp-skills
                url: https://registry.example.test/skills/pdf-tools.tar.gz
              - name: résumé-助手
                reason: registry:team.catalog
          mcp:
            default: allow
            registry:
              - name: docs-mcp
                connector: claudecode
                reason: registry:corp-skills
                command: npx
                args_prefix: ["-y", "@example/docs-mcp"]
                transport: stdio
              - name: remote-mcp
                reason: registry:clawhub-main
                url: https://mcp.example.test/sse
            allowed:
              - name: hand-written
                reason: operator decision
        guardrail:
          enabled: true
        """;

    private static RegistryAttribution Read(string assetPolicy) => RegistryAttribution.FromAssetPolicy(assetPolicy);

    [Fact]
    public void The_rules_a_registry_sync_leaves_in_a_whole_config_attribute_the_skills_and_servers_it_promoted()
    {
        // Fixtures/config.registry-promoted.yaml: a synthetic config.yaml in the shape registries/sync.py writes (see its header).
        var document = ConfigStore.Parse(FixtureFiles.ReadText("config.registry-promoted.yaml"), "config.registry-promoted.yaml");

        var attribution = RegistryAttribution.From(document);

        Assert.Equal("corp-skills", attribution.SourceOf("skill", "pdf-tools"));
        Assert.Equal("team.catalog", attribution.SourceOf("skill", "wiki-skill"));
        Assert.Equal("corp-skills", attribution.SourceOf("mcp", "docs-mcp"));
        Assert.Equal("team.catalog", attribution.SourceOf("mcp", "remote-mcp"));
        Assert.Equal(2, attribution.SkillCount);
        Assert.Equal(2, attribution.McpCount);

        // Only the registry list counts: a hand-written rule, a rule with no reason, the allowed and denied lists (even with a registry reason on a
        // rule in them) and the plugin block are not promotions.
        foreach (var (kind, name) in new[]
        {
            ("skill", "hand-written-skill"), ("skill", "allowed-by-hand"), ("skill", "lookalike-allowed"), ("skill", "banned-skill"),
            ("mcp", "second-hand-mcp"), ("plugin", "plugin-that-registries-do-not-promote"), ("skill", "plugin-that-registries-do-not-promote"),
        })
        {
            Assert.Null(attribution.SourceOf(kind, name));
        }
    }

    [Fact]
    public void A_skill_and_an_mcp_server_a_registry_promoted_name_their_source()
    {
        var document = ConfigStore.Parse(Promoted);

        var attribution = RegistryAttribution.From(document);

        Assert.Equal("corp-skills", attribution.SourceOf("skill", "pdf-tools"));
        Assert.Equal("team.catalog", attribution.SourceOf("skill", "résumé-助手"));
        Assert.Equal("corp-skills", attribution.SourceOf("mcp", "docs-mcp"));
        Assert.Equal("clawhub-main", attribution.SourceOf("mcp", "remote-mcp"));
        Assert.Equal(2, attribution.SkillCount);
        Assert.Equal(2, attribution.McpCount);
        Assert.False(attribution.IsEmpty);
    }

    [Fact]
    public void A_name_no_registry_promoted_has_no_source_and_the_two_kinds_are_kept_apart()
    {
        var attribution = RegistryAttribution.From(ConfigStore.Parse(Promoted));

        Assert.Null(attribution.SourceOf("skill", "unlisted"));
        Assert.Null(attribution.SourceOf("skill", "docs-mcp")); // an MCP server's rule does not badge a skill of that name
        Assert.Null(attribution.SourceOf("mcp", "pdf-tools"));
        Assert.Null(attribution.SourceOf("mcp", "hand-written")); // the allowed list is the operator's, not a registry's
        Assert.Null(attribution.SourceOf("plugin", "pdf-tools")); // a registry never promotes a plugin
        Assert.Null(attribution.SourceOf("tool", "pdf-tools"));
    }

    [Fact]
    public void Names_and_kinds_are_matched_exactly()
    {
        var attribution = RegistryAttribution.From(ConfigStore.Parse(Promoted));

        Assert.Null(attribution.SourceOf("skill", "PDF-Tools"));
        Assert.Null(attribution.SourceOf("skill", "pdf-tools "));
        Assert.Null(attribution.SourceOf("Skill", "pdf-tools"));
    }

    [Fact]
    public void A_rule_with_another_reason_or_none_is_not_a_registrys()
    {
        var attribution = Read("""
            asset_policy:
              skill:
                registry:
                  - name: by-hand
                    reason: approved by the security team
                  - name: no-reason
                  - name: null-reason
                    reason: ~
                  - name: wrong-case
                    reason: Registry:corp-skills
                  - name: prefix-only
                    reason: "registry:"
                  - name: prefix-and-spaces
                    reason: "registry:    "
                  - name: embedded
                    reason: "see registry:corp-skills"
                  - name: good
                    reason: registry:corp-skills
            """);

        Assert.Equal(1, attribution.SkillCount);
        Assert.Equal("corp-skills", attribution.SourceOf("skill", "good"));
        Assert.All(
            new[] { "by-hand", "no-reason", "null-reason", "wrong-case", "prefix-only", "prefix-and-spaces", "embedded" },
            name => Assert.Null(attribution.SourceOf("skill", name)));
    }

    [Fact]
    public void A_rule_with_no_name_attributes_nothing_and_the_name_the_reason_and_the_id_are_trimmed()
    {
        var attribution = Read("""
            asset_policy:
              skill:
                registry:
                  - reason: registry:corp-skills
                  - name: ""
                    reason: registry:corp-skills
                  - name: "   "
                    reason: registry:corp-skills
                  - name: ~
                    reason: registry:corp-skills
                  - name: "  padded  "
                    reason: "  registry:   corp-skills  "
            """);

        Assert.Equal(1, attribution.SkillCount);
        Assert.Equal("corp-skills", attribution.SourceOf("skill", "padded"));
    }

    [Fact]
    public void The_last_rule_for_a_name_wins_as_it_does_in_the_tui()
    {
        // _registry_attribution_from_config assigns out[name] = source_id in rule order, so a later rule replaces an earlier one.
        var attribution = Read("""
            asset_policy:
              skill:
                registry:
                  - name: shared
                    connector: claudecode
                    reason: registry:first
                  - name: shared
                    connector: codex
                    reason: registry:second
                  - name: shared
                    reason: promoted by hand
            """);

        Assert.Equal("second", attribution.SourceOf("skill", "shared"));
        Assert.Equal(1, attribution.SkillCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n")]
    [InlineData("asset_policy:\n")]
    [InlineData("asset_policy: nothing\n")]
    [InlineData("asset_policy:\n  skill: oops\n  mcp:\n")]
    [InlineData("asset_policy:\n  skill:\n    registry: not-a-list\n")]
    [InlineData("asset_policy:\n  skill:\n    registry:\n      - just-a-string\n      - [a, list]\n")]
    [InlineData("asset_policy:\n  skill:\n    registry:\n      - name: [a, list]\n        reason: registry:x\n")]
    [InlineData("asset_policy: [unclosed\n")]
    [InlineData("\tasset_policy: {\n")]
    [InlineData("guardrail:\n  enabled: true\n")]
    public void A_section_that_is_missing_unreadable_or_oddly_shaped_attributes_nothing(string? section)
    {
        var attribution = RegistryAttribution.FromAssetPolicy(section);

        Assert.True(attribution.IsEmpty);
        Assert.Null(attribution.SourceOf("skill", "anything"));
        Assert.Same(RegistryAttribution.Empty, attribution);
    }

    [Fact]
    public void A_config_with_no_asset_policy_attributes_nothing()
    {
        var document = ConfigStore.Parse("config_version: 8\nclaw:\n  mode: claudecode\n");

        Assert.Same(RegistryAttribution.Empty, RegistryAttribution.From(document));
    }

    [Fact]
    public void The_kind_and_name_must_be_given()
    {
        var attribution = RegistryAttribution.Empty;

        _ = Assert.Throws<ArgumentNullException>(() => attribution.SourceOf(null!, "x"));
        _ = Assert.Throws<ArgumentNullException>(() => attribution.SourceOf("skill", null!));
        _ = Assert.Throws<ArgumentNullException>(() => RegistryAttribution.From(null!));
    }

    [Theory]
    [InlineData("registry:corp-skills", "corp-skills")]
    [InlineData("  registry:corp-skills  ", "corp-skills")]
    [InlineData("registry:  corp skills  ", "corp skills")]
    [InlineData("registry:a:b", "a:b")]
    [InlineData("registry:", null)]
    [InlineData("registry:   ", null)]
    [InlineData("Registry:corp-skills", null)]
    [InlineData("registry corp-skills", null)]
    [InlineData("operator", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void The_source_id_is_what_follows_the_prefix(string? reason, string? expected)
    {
        Assert.Equal(expected, RegistryAttribution.SourceIdFromReason(reason));
    }

    [Theory]
    [InlineData("corp-skills", "registry:corp-skills")]
    [InlineData("  corp-skills  ", "registry:corp-skills")]
    [InlineData("exactly-18-chars-ok", "registry:exactly-18-char...")] // 19 characters: cut to 15 and "..."
    [InlineData("exactly-18-chars!", "registry:exactly-18-chars!")] // 17: whole
    [InlineData("0123456789abcdefgh", "registry:0123456789abcdefgh")] // 18: whole
    [InlineData("0123456789abcdefghi", "registry:0123456789abcde...")] // 19: 15 + "..."
    [InlineData("enterprise-skills-catalog-prod", "registry:enterprise-skil...")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData(null, "")]
    public void The_badge_is_registry_colon_and_the_id_cut_as_the_tui_cuts_it(string? id, string expected)
    {
        Assert.Equal(expected, RegistryAttribution.Badge(id));
    }

    [Fact]
    public void The_badge_never_splits_a_surrogate_pair_and_takes_another_limit()
    {
        // 20 astral characters (U+1F600): 15 of them fit before the "..." and each is two UTF-16 units.
        var id = string.Concat(Enumerable.Repeat("\U0001F600", 20));

        var badge = RegistryAttribution.Badge(id);

        Assert.Equal("registry:" + string.Concat(Enumerable.Repeat("\U0001F600", 15)) + "...", badge);
        Assert.Equal(9 + 15 + 3, badge.EnumerateRunes().Count());
        Assert.DoesNotContain('�', badge); // a cut surrogate pair would enumerate as the replacement character
        Assert.Equal("registry:abcdefg...", RegistryAttribution.Badge("abcdefghijklmnopqrstuvwxyz", 10));
        Assert.Equal("registry:abc", RegistryAttribution.Badge("abc", 10));
    }
}
