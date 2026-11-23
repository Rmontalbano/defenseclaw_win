using System.Globalization;
using System.Text;
using DefenseClaw.Core.Config;

namespace DefenseClaw.Tests;

/// <summary>
/// <see cref="ConfigKeyFloors"/> with the runtime's own list of newer keys (upgrade_shim.py@1.0.10:89-181): the path language (<c>*</c> for every value of a
/// map, <c>[]</c> for every item of a list), the report order, the collapse of a key into its block, the bounds, and the unset command. Synthetic values only.
/// </summary>
public class ConfigKeyFloorPatternTests
{
    private const string ProfileRouting = "guardrail.profiles.*.routing";

    private static ConfigYamlReader Reader(string yaml) => ConfigYamlReader.Parse(yaml);

    private static IReadOnlyList<string> Refused(string yaml, string target) =>
        ConfigKeyFloors.RefusedBy(Reader(yaml), Version.Parse(target));

    public static IEnumerable<object[]> Rows() =>
        ConfigKeyFloors.Floors.Select(static f => new object[] { f.Key, f.FirstRelease.ToString() });

    /// <summary>A flow-style document that sets exactly this key: <c>*</c> becomes a map entry named work, <c>[]</c> a list of one item.</summary>
    private static string Fixture(string key)
    {
        var parts = key.Split('.');
        var value = "1";
        for (var i = parts.Length - 1; i >= 0; i--)
        {
            var part = parts[i];
            var open = part.IndexOf('[', StringComparison.Ordinal);
            var name = open < 0 ? part : part[..open];
            if (open >= 0)
            {
                value = "[" + value + "]";
            }

            value = "{" + (name == "*" ? "work" : name) + ": " + value + "}";
        }

        return value;
    }

    private static string Concrete(string key) => key.Replace("*", "work", StringComparison.Ordinal).Replace("[]", "[0]", StringComparison.Ordinal);

    private static bool Covers(IReadOnlyList<string> reported, string concrete) =>
        reported.Any(r => r == concrete ||
            concrete.StartsWith(r + ".", StringComparison.Ordinal) ||
            concrete.StartsWith(r + "[", StringComparison.Ordinal));

    [Fact]
    public void The_table_keeps_this_apps_six_rows_first_then_the_runtimes_61()
    {
        var floors = ConfigKeyFloors.Floors;

        Assert.Equal(67, floors.Count);
        Assert.Equal(
            ["decision_model", "decision_model.hook_connectors", "observability.local.max_store_mb", "guardrail.judge.max_parallel", "guardrail.protected_files", "guardrail.judge.extra_body"],
            floors.Take(6).Select(static f => f.Key));
        Assert.Equal("openshell.trusted_ca_bundle", floors[6].Key);
        Assert.Equal(new Version(1, 0, 3), floors[6].FirstRelease);
        Assert.Equal("guardrail.profile_assignments[].match.substrate", floors[^1].Key);
        Assert.Equal(floors.Count, floors.Select(static f => f.Key).Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [MemberData(nameof(Rows))]
    public void Every_row_is_refused_by_the_release_before_its_first_and_accepted_by_it(string key, string first)
    {
        var floor = Version.Parse(first);
        var yaml = Fixture(key);
        var concrete = Concrete(key);

        var before = Refused(yaml, new Version(floor.Major, floor.Minor, floor.Build - 1).ToString());
        Assert.True(Covers(before, concrete), $"{key}: {string.Join(", ", before)} does not cover {concrete}");
        Assert.Empty(Refused(yaml, first));
        Assert.Empty(Refused(yaml, new Version(floor.Major, floor.Minor, floor.Build + 1).ToString()));
    }

    [Theory]
    [InlineData("connectors", "1.0.5")]
    [InlineData("openshell.trusted_ca_bundle", "1.0.2")]
    [InlineData("guardrail.internal_domains", "1.0.5")]
    [InlineData("guardrail.chains", "1.0.5")]
    public void The_older_runtime_rows_name_their_own_key(string key, string target)
    {
        Assert.Equal([key], Refused(Fixture(key), target));
    }

    [Fact]
    public void A_profile_and_a_connector_inside_it_are_named_by_their_concrete_keys()
    {
        const string yaml = """
            guardrail:
              profiles:
                work:
                  cascade: {}
                  connectors:
                    codex:
                      cascade: {}
            """;

        Assert.Equal(
            ["guardrail.profiles.work.cascade", "guardrail.profiles.work.connectors.codex.cascade"],
            Refused(yaml, "1.0.9"));
        Assert.Empty(Refused(yaml, "1.0.10"));
    }

    [Fact]
    public void A_list_item_is_named_by_its_index()
    {
        const string yaml = """
            routing:
              models:
                - name: first
                  bedrock:
                    region: x
                - name: second
                  credential: c
            """;

        Assert.Equal(["routing.models[0].bedrock", "routing.models[1].credential"], Refused(yaml, "1.0.9"));
    }

    [Fact]
    public void Map_names_are_walked_in_ordinal_order()
    {
        const string yaml = "guardrail:\n  profiles:\n    zeta: {routing: 1}\n    alpha: {routing: 1}\n    Beta: {routing: 1}\n";

        Assert.Equal(
            ["guardrail.profiles.Beta.routing", "guardrail.profiles.alpha.routing", "guardrail.profiles.zeta.routing"],
            Refused(yaml, "1.0.9"));
    }

    [Fact]
    public void An_empty_or_dotted_map_name_is_skipped()
    {
        const string yaml = "guardrail:\n  profiles:\n    \"a.b\": {routing: 1}\n    \"\": {routing: 1}\n    ok: {routing: 1}\n";

        Assert.Equal(["guardrail.profiles.ok.routing"], Refused(yaml, "1.0.9"));
    }

    [Fact]
    public void A_hostile_map_name_is_reported_once_under_the_pattern()
    {
        var yaml = "guardrail:\n  profiles:\n    \"bad\\nname\": {routing: 1}\n    \"" + new string('x', 200) + "\": {routing: 1}\n    \"has space\": {routing: 1}\n";

        Assert.Equal([ProfileRouting], Refused(yaml, "1.0.9"));
    }

    [Fact]
    public void A_good_name_is_still_echoed_beside_a_hostile_one()
    {
        var yaml = "guardrail:\n  profiles:\n    \"bad\\nname\": {routing: 1}\n    ok: {routing: 1}\n";

        var refused = Refused(yaml, "1.0.9");

        Assert.Equal([ProfileRouting, "guardrail.profiles.ok.routing"], refused);
        Assert.DoesNotContain(refused, static k => k.Contains('\n', StringComparison.Ordinal));
    }

    [Fact]
    public void A_key_with_no_value_counts_like_any_other()
    {
        Assert.Equal(["routing.context"], Refused("routing:\n  context:\n", "1.0.9"));
        Assert.Equal(["guardrail.profiles.work.routing"], Refused("guardrail:\n  profiles:\n    work:\n      routing:\n", "1.0.9"));
    }

    [Fact]
    public void A_block_and_a_key_inside_it_are_reported_as_the_block()
    {
        Assert.Equal(["connectors"], Refused("connectors:\n  openclaw: {}\n", "1.0.5"));
        Assert.Equal(["connectors.openclaw"], Refused("connectors:\n  openclaw: {}\n", "1.0.9"));
        Assert.Equal(["decision_model"], Refused("decision_model:\n  protocol: x\n", "1.0.5"));
        Assert.Equal(["decision_model.protocol"], Refused("decision_model:\n  protocol: x\n", "1.0.9"));
    }

    [Fact]
    public void A_target_from_the_first_release_refuses_nothing_and_an_empty_document_refuses_nothing()
    {
        Assert.Empty(ConfigKeyFloors.RefusedBy(ConfigYamlReader.Empty, new Version(1, 0, 0)));
        Assert.Empty(Refused("guardrail:\n  cascade: {}\nrouting:\n  context: {}\n", "1.0.10"));
        Assert.Empty(Refused("- a\n- b\n", "1.0.0"));
    }

    [Fact]
    public void A_config_with_twenty_thousand_profiles_reports_at_most_64_keys_and_does_not_throw()
    {
        var yaml = new StringBuilder("guardrail:\n  profiles:\n");
        for (var i = 0; i < 20_000; i++)
        {
            yaml.Append(CultureInfo.InvariantCulture, $"    p{i}:\n      cascade: 1\n");
        }

        Assert.True(yaml.Length < ConfigYamlReader.MaxLength);

        var refused = ConfigKeyFloors.RefusedBy(ConfigYamlReader.Parse(yaml.ToString()), new Version(1, 0, 9));

        Assert.NotEmpty(refused);
        Assert.True(refused.Count <= 64, $"{refused.Count} keys");
        Assert.Equal(refused.Count, refused.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Aliased_nodes_are_bounded_by_the_budget_and_do_not_throw()
    {
        var yaml = new StringBuilder("x: &m {apis: 1, bedrock: 1, credential: 1}\nrouting:\n  models: [");
        yaml.Append(string.Join(", ", Enumerable.Repeat("*m", 3000)));
        yaml.Append("]\n");

        var refused = ConfigKeyFloors.RefusedBy(ConfigYamlReader.Parse(yaml.ToString()), new Version(1, 0, 9));

        Assert.True(refused.Count <= 64);
        Assert.All(refused, static k => Assert.StartsWith("routing.models[", k, StringComparison.Ordinal));
    }

    [Fact]
    public void Nested_aliased_lists_do_not_throw()
    {
        var yaml = new StringBuilder("l0: &l0 [a, a, a, a, a, a, a, a, a, a]\n");
        for (var level = 1; level <= 4; level++)
        {
            yaml.Append(CultureInfo.InvariantCulture, $"l{level}: &l{level} [{string.Join(", ", Enumerable.Repeat($"*l{level - 1}", 10))}]\n");
        }

        yaml.Append("routing:\n  models: *l4\n  context: 1\n");

        var refused = ConfigKeyFloors.RefusedBy(ConfigYamlReader.Parse(yaml.ToString()), new Version(1, 0, 9));

        Assert.True(refused.Count <= 64);
        Assert.Contains("routing.context", refused);
    }

    [Fact]
    public void The_unset_command_lists_plain_keys_only()
    {
        Assert.Equal(
            "defenseclaw config unset routing.context decision_model",
            ConfigKeyFloors.UnsetCommand(["routing.context", "guardrail.profiles.*.cascade", "routing.models[0].bedrock", "decision_model"]));
        Assert.Null(ConfigKeyFloors.UnsetCommand(["guardrail.profiles.*.routing", "routing.models[0].apis"]));
        Assert.Null(ConfigKeyFloors.UnsetCommand([]));
        Assert.Null(ConfigKeyFloors.UnsetCommand(["a b", "a;b", "$(x)", "", ".a", "a.", "a..b", "a\nb"]));
    }

    [Fact]
    public void The_unset_command_for_a_config_also_drops_keys_it_does_not_have()
    {
        var config = Reader("routing:\n  context: 1\n");

        Assert.Equal("defenseclaw config unset routing.context", ConfigKeyFloors.UnsetCommand(config, ["routing.context", "routing.admin", "guardrail.profiles.*.cascade"]));
        Assert.Null(ConfigKeyFloors.UnsetCommand(config, ["routing.admin"]));
        Assert.Null(ConfigKeyFloors.UnsetCommand(ConfigYamlReader.Empty, ["routing.context"]));
    }

    [Fact]
    public void No_refused_key_is_ever_a_wildcard_in_the_unset_command()
    {
        const string yaml = "guardrail:\n  profiles:\n    \"bad\\nname\": {routing: 1, cascade: 1}\n    ok: {routing: 1}\nrouting:\n  context: 1\n  models: [{bedrock: 1}]\n";

        var command = ConfigKeyFloors.UnsetCommand(Reader(yaml), Refused(yaml, "1.0.9"));

        Assert.NotNull(command);
        Assert.DoesNotContain('*', command);
        Assert.DoesNotContain('[', command);
        Assert.Contains("routing.context", command, StringComparison.Ordinal);
        Assert.Contains("guardrail.profiles.ok.routing", command, StringComparison.Ordinal);
    }
}
