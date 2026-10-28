using System.Text.Json;
using DefenseClaw.Core.Config;
using DefenseClaw.Core.Gateway;

namespace DefenseClaw.Tests.Policy;

/// <summary>
/// The three readers behind the Overview's policy cards (CUST-375), over synthetic fixtures written from the 1.0.4 sources:
/// <c>admission.*.actions</c> (<c>internal/config/admission.go</c>), <c>asset_policy.*.allowed/denied</c> (<c>asset_policy.go</c>) and the answer of
/// <c>handleGuardrailProfileResolve</c> (<c>guardrail_profile.go</c>) with <c>/health</c>'s <c>policy</c> block (<c>generation.go: PolicyHealth</c>).
/// </summary>
public sealed class OperatorPolicyTests
{
    private const string Config =
        "config_version: 9\n" +
        "admission:\n" +
        "  defaults:\n" +
        "    actions:\n" +
        "      low: warn\n" +
        "      critical: block\n" +
        "  skill:\n" +
        "    actions:\n" +
        "      high:\n" +
        "        install: block\n" +
        "        file: quarantine\n" +
        "        runtime: disable\n" +
        "      medium: warn\n" +
        "  mcp:\n" +
        "    scan_on_install: true\n" +
        "asset_policy:\n" +
        "  enabled: true\n" +
        "  skill:\n" +
        "    denied:\n" +
        "      - name: bad-skill\n" +
        "        reason: seen in an incident\n" +
        "      - name: ''\n" +
        "      - reason: no name, ignored\n" +
        "    allowed:\n" +
        "      - name: good-skill\n" +
        "        connector: codex\n" +
        "    registry:\n" +
        "      - name: registry-rule-is-not-an-operator-list\n" +
        "  tool:\n" +
        "    denied:\n" +
        "      - name: delete_file\n" +
        "        connector: codex\n" +
        "      - name: format_disk\n" +
        "    allowed:\n" +
        "      - name: read_file\n";

    private static string Crlf(string text) => text.Replace("\n", "\r\n", StringComparison.Ordinal);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Admission_overrides_list_each_types_actions_in_severity_order_with_shorthands_and_triples(bool crlf)
    {
        var overrides = AdmissionOverrides.FromYaml(crlf ? Crlf(Config) : Config);

        Assert.Equal(
            new[]
            {
                ("defaults · critical", "block"),
                ("defaults · low", "warn"),
                ("skill · high", "install=block, file=quarantine, runtime=disable"),
                ("skill · medium", "warn"),
            },
            overrides.Rows.Select(row => (row.Label, row.Action)).ToArray());
        Assert.False(overrides.IsEmpty);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("gateway:\n  api_port: 18970\n")]
    [InlineData("admission:\n  skill:\n    scan_on_install: true\n")]
    [InlineData("admission: [unclosed")]
    [InlineData("- just\n- a list\n")]
    public void A_config_with_no_overrides_or_that_cannot_be_read_is_empty_and_never_throws(string? yaml)
    {
        Assert.True(AdmissionOverrides.FromYaml(yaml).IsEmpty);
    }

    [Fact]
    public void A_too_deep_flow_collection_is_refused_before_the_parser_sees_it()
    {
        var deep = "admission: " + new string('[', 5000) + new string(']', 5000) + "\n";

        Assert.True(AdmissionOverrides.FromYaml(deep).IsEmpty);
        Assert.True(OperatorAssetLists.FromYaml(deep).IsEmpty);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Operator_lists_read_denied_and_allowed_per_type_and_ignore_nameless_rules_and_the_registry(bool crlf)
    {
        var lists = OperatorAssetLists.FromYaml(crlf ? Crlf(Config) : Config);

        Assert.False(lists.IsEmpty);
        Assert.False(lists.SecureClient);
        Assert.Equal(5, lists.Total);
        Assert.Equal(new[] { "bad-skill" }, lists.DeniedFor("skill").Select(rule => rule.Name).ToArray());
        Assert.Equal("seen in an incident", lists.DeniedFor("skill")[0].Reason);
        var allowedSkill = Assert.Single(lists.AllowedFor("skill"));
        Assert.Equal("@codex/good-skill", allowedSkill.Target);
        Assert.Equal(new[] { "@codex/delete_file", "format_disk" }, lists.DeniedFor("tool").Select(rule => rule.Target).ToArray());
        Assert.Equal(new[] { "read_file" }, lists.AllowedFor("tool").Select(rule => rule.Name).ToArray());
        Assert.Empty(lists.DeniedFor("mcp"));
        Assert.Empty(lists.AllowedFor("plugin"));
    }

    [Fact]
    public void A_config_with_no_asset_policy_has_no_lists()
    {
        Assert.True(OperatorAssetLists.FromYaml("gateway:\n  api_port: 18970\n").IsEmpty);
        Assert.True(OperatorAssetLists.FromYaml(null).IsEmpty);
        Assert.Equal(0, OperatorAssetLists.FromYaml("asset_policy:\n  enabled: true\n").Total);
    }

    [Theory]
    [InlineData("deployment_mode: managed_enterprise\nenterprise:\n  profile: secure_client\n", true)]
    [InlineData("deployment_mode: managed_enterprise\n", true)]
    [InlineData("deployment_mode: managed_enterprise\nenterprise:\n  profile: standalone\n", false)]
    [InlineData("deployment_mode: standalone\n", false)]
    [InlineData("", false)]
    public void A_secure_client_install_keeps_its_decisions_in_the_audit_store_so_its_lists_are_not_the_policy(string head, bool secureClient)
    {
        var lists = OperatorAssetLists.FromYaml(head + "asset_policy:\n  skill:\n    denied:\n      - name: bad-skill\n");

        Assert.Equal(secureClient, lists.SecureClient);
        Assert.Equal(secureClient, lists.IsEmpty);
    }

    [Fact]
    public void A_list_longer_than_the_limit_is_cut_and_the_cut_is_counted()
    {
        var yaml = "asset_policy:\n  skill:\n    denied:\n" +
                   string.Concat(Enumerable.Range(0, OperatorAssetLists.MaxRulesPerList + 3).Select(i => $"      - name: skill-{i}\n"));

        var lists = OperatorAssetLists.FromYaml(yaml);

        Assert.Equal(OperatorAssetLists.MaxRulesPerList, lists.DeniedFor("skill").Count);
        Assert.Equal(3, lists.Truncated);
    }

    // ---- Effective policy -------------------------------------------------------------------------------------------------

    private const string Resolved =
        "{\"profiles_configured\":true,\"user\":\"operator\",\"connector\":\"codex\",\"agent\":\"\",\"profile\":\"engineers\",\"digest\":\"sha256:profile\"," +
        "\"match\":\"user\",\"matched_group\":\"\",\"subject_source\":\"lookup\",\"assignment\":\"0\"," +
        "\"subject\":{\"user_id\":\"1000\",\"user_name\":\"operator\",\"groups\":[\"staff\"]}," +
        "\"effective\":{\"mode\":\"action\",\"block_at\":\"HIGH\",\"alert_at\":\"LOW\",\"rule_pack_dir\":\"strict\",\"hilt\":{\"enabled\":false,\"min_severity\":\"HIGH\"},\"resolved_block_at\":\"HIGH\"}," +
        "\"warnings\":[\"more than one profile may apply\"],\"secret\":\"never projected\"}";

    private const string Health =
        "{\"state\":\"running\",\"policy\":{\"effective_digest\":\"sha256:live-digest\",\"generation\":9,\"config_generation\":4,\"built_at\":\"2026-10-01T10:00:00Z\"}}";

    private static GatewayResult<JsonDocument> Json(string body) => GatewayResult<JsonDocument>.Ok(JsonDocument.Parse(body));

    [Fact]
    public void The_path_names_the_account_and_connector_and_escapes_both()
    {
        Assert.Equal("api/v1/guardrail/profiles/resolve?user=operator&connector=codex", EffectivePolicyReader.Path("operator", "codex"));
        Assert.Equal("api/v1/guardrail/profiles/resolve?user=DOMAIN%5Cop%20erator&connector=", EffectivePolicyReader.Path(" DOMAIN\\op erator ", string.Empty));
    }

    [Fact]
    public void A_resolved_profile_projects_only_the_display_fields_and_takes_the_digest_and_generation_from_health()
    {
        var read = EffectivePolicyReader.FromGateway(Json(Resolved), Json(Health));

        Assert.Equal(EffectivePolicyStatus.Ok, read.Status);
        var policy = read.Policy!;
        Assert.True(policy.ProfilesConfigured);
        Assert.Equal("engineers", policy.Profile);
        Assert.Equal("user", policy.Match);
        Assert.Equal("action", policy.Mode);
        Assert.Equal("HIGH", policy.BlockAt);
        Assert.Equal("LOW", policy.AlertAt);
        Assert.Equal("strict", policy.RulePack);
        Assert.Equal("sha256:live-digest", policy.Digest);
        Assert.Equal("9", policy.Generation);
        Assert.Equal(new[] { "more than one profile may apply" }, policy.Warnings);
        Assert.DoesNotContain("never projected", policy.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Without_a_health_policy_block_the_digest_is_the_profiles_own_and_the_generation_is_empty()
    {
        var read = EffectivePolicyReader.FromGateway(Json(Resolved), Json("{\"state\":\"running\"}"));

        Assert.Equal("sha256:profile", read.Policy!.Digest);
        Assert.Equal(string.Empty, read.Policy.Generation);
    }

    [Fact]
    public void An_install_with_no_profiles_resolves_to_the_base_policy_with_an_empty_profile()
    {
        const string noProfiles =
            "{\"profiles_configured\":false,\"user\":\"operator\",\"connector\":\"codex\",\"agent\":\"\",\"profile\":\"\",\"match\":\"\"," +
            "\"effective\":{\"mode\":\"observe\",\"block_at\":\"CRITICAL\",\"alert_at\":\"MEDIUM\",\"rule_pack_dir\":\"default\"}}";

        var read = EffectivePolicyReader.FromGateway(Json(noProfiles), null);

        Assert.Equal(EffectivePolicyStatus.Ok, read.Status);
        Assert.False(read.Policy!.ProfilesConfigured);
        Assert.Equal(string.Empty, read.Policy.Profile);
        Assert.Equal("observe", read.Policy.Mode);
    }

    [Theory]
    [InlineData("{\"profiles_configured\":true,\"profile\":\"\",\"effective\":{\"mode\":\"action\"}}", "no profile")]
    [InlineData("{\"profiles_configured\":true,\"profile\":\"a\",\"lookup_error\":\"user not found\",\"effective\":{\"mode\":\"action\"}}", "user not found")]
    [InlineData("{\"profiles_configured\":true,\"profile\":\"a\",\"refused\":true,\"refused_reason\":\"identity not confirmed\",\"effective\":{\"mode\":\"action\"}}", "identity not confirmed")]
    [InlineData("{\"profiles_configured\":false,\"profile\":\"\",\"effective\":{}}", "no effective policy")]
    [InlineData("[1,2]", "not a JSON object")]
    public void An_answer_that_is_not_a_policy_the_card_can_vouch_for_is_an_error_with_a_reason(string body, string reason)
    {
        var read = EffectivePolicyReader.FromGateway(Json(body), null);

        Assert.Equal(EffectivePolicyStatus.Error, read.Status);
        Assert.Null(read.Policy);
        Assert.Contains(reason, read.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(404)]
    [InlineData(405)]
    public void A_gateway_without_the_route_is_unsupported_and_never_an_error(int code)
    {
        var read = EffectivePolicyReader.FromGateway(GatewayResult<JsonDocument>.Error("gateway returned HTTP " + code, code), null);

        Assert.Equal(EffectivePolicyStatus.Unsupported, read.Status);
        Assert.Equal(string.Empty, read.Message);
    }

    [Fact]
    public void Other_failures_are_errors_with_their_own_sentence_and_an_unreachable_gateway_is_silent()
    {
        Assert.Equal(EffectivePolicyStatus.Unreachable, EffectivePolicyReader.FromGateway(GatewayResult<JsonDocument>.Unreachable(), null).Status);

        var forbidden = EffectivePolicyReader.FromGateway(GatewayResult<JsonDocument>.Error("forbidden", 403), null);
        Assert.Equal(EffectivePolicyStatus.Error, forbidden.Status);
        Assert.Contains("administrator", forbidden.Message, StringComparison.Ordinal);

        var unauthorized = EffectivePolicyReader.FromGateway(GatewayResult<JsonDocument>.Unauthorized(), null);
        Assert.Equal(EffectivePolicyStatus.Error, unauthorized.Status);
        Assert.DoesNotContain("Bearer", unauthorized.Message, StringComparison.Ordinal);

        var rejected = EffectivePolicyReader.FromGateway(GatewayResult<JsonDocument>.Error("unknown connector \"x\"", 400), null);
        Assert.Contains("unknown connector", rejected.Message, StringComparison.Ordinal);
    }
}
