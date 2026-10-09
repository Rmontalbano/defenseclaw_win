using System.Text.Json;
using DefenseClaw.Core.Cli;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// CUST-311: the tiers of <c>setup local-observability up | down | status | logs | url | env | reset</c>, held against the whole 0.8.10 command
/// tree. <c>setup</c> is a state-changing verb, so by the first-verb rule every one of the seven would be a change; four of them are named
/// reads (read from <c>commands/cmd_setup_local_observability.py</c>), <c>reset</c> is destructive, and the rule that says so is an exact
/// three-token path, so nothing else under <c>setup</c> can use it.
/// </summary>
public sealed class CommandTierLocalObservabilityTests
{
    private const string Group = "setup local-observability";

    private static readonly string[] Tree = Load();

    private static string[] Load()
    {
        using var document = JsonDocument.Parse(FixtureFiles.ReadText("cli-tree-0.8.10.json"));
        return document.RootElement.GetProperty("commands").EnumerateArray()
            .Where(c => !c.GetProperty("group").GetBoolean())
            .Select(c => c.GetProperty("path").GetString()!)
            .ToArray();
    }

    private static string[] StackLeaves => Tree.Where(p => p.StartsWith(Group + " ", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToArray();

    private static string[] Words(string path) => path.Split(' ');

    [Fact]
    public void The_0_8_10_tree_has_exactly_the_seven_verbs()
    {
        Assert.Equal(
            new[] { "down", "env", "logs", "reset", "status", "up", "url" }.Select(v => Group + " " + v).ToArray(),
            StackLeaves);
    }

    [Theory]
    [InlineData("status", CommandTier.ReadOnly)]
    [InlineData("logs", CommandTier.ReadOnly)]
    [InlineData("url", CommandTier.ReadOnly)]
    [InlineData("env", CommandTier.ReadOnly)]
    [InlineData("up", CommandTier.StateChanging)]
    [InlineData("down", CommandTier.StateChanging)]
    [InlineData("reset", CommandTier.Destructive)]
    public void Each_verb_has_the_tier_its_source_earns(string verb, CommandTier expected)
    {
        Assert.Contains(Group + " " + verb, Tree);

        Assert.Equal(expected, CommandTiers.Classify(Words(Group + " " + verb)));
    }

    [Theory]
    [InlineData("url --json", CommandTier.ReadOnly)]
    [InlineData("env --json", CommandTier.ReadOnly)]
    [InlineData("logs --service loki", CommandTier.ReadOnly)]
    [InlineData("logs --follow", CommandTier.ReadOnly)]
    [InlineData("logs --service otel-collector --follow", CommandTier.ReadOnly)]
    [InlineData("up --no-wait --no-config", CommandTier.StateChanging)]
    [InlineData("up --timeout 60 --signals traces,metrics --service-name x", CommandTier.StateChanging)]
    [InlineData("up --no-refresh-bundle --no-refresh-config", CommandTier.StateChanging)]
    [InlineData("down --disable-config", CommandTier.StateChanging)]
    [InlineData("reset --yes", CommandTier.Destructive)]
    public void The_documented_options_do_not_move_a_verb_to_another_tier(string verbAndOptions, CommandTier expected) =>
        Assert.Equal(expected, CommandTiers.Classify(Words(Group + " " + verbAndOptions)));

    [Fact]
    public void Only_the_four_reads_are_read_leaves_across_the_whole_tree()
    {
        var readLeaves = Tree.Where(p => CommandTiers.IsReadOnlyLeaf(Words(p))).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(
            new[] { "env", "logs", "status", "url" }.Select(v => Group + " " + v).ToArray(),
            readLeaves);
    }

    [Fact]
    public void The_exemption_is_the_exact_three_token_path_and_nothing_else_inherits_it()
    {
        // Flags and what follows them never matter to whether a path is the read leaf.
        Assert.True(CommandTiers.IsReadOnlyLeaf(Words(Group + " status")));
        Assert.True(CommandTiers.IsReadOnlyLeaf(Words(Group + " logs --service loki --follow")));
        Assert.True(CommandTiers.IsReadOnlyLeaf(new[] { "setup", "local-observability", "url", "--json", "--", "up" }));

        // A fourth path token, another group, another spelling, a shorter path: not it.
        Assert.False(CommandTiers.IsReadOnlyLeaf(Words(Group + " status extra")));
        Assert.False(CommandTiers.IsReadOnlyLeaf(Words(Group)));
        Assert.False(CommandTiers.IsReadOnlyLeaf(Words("setup splunk status")));
        Assert.False(CommandTiers.IsReadOnlyLeaf(Words("setup observability status")));
        Assert.False(CommandTiers.IsReadOnlyLeaf(Words("local-observability status")));
        Assert.False(CommandTiers.IsReadOnlyLeaf(Words("Setup Local-Observability Status")));
        Assert.False(CommandTiers.IsReadOnlyLeaf(new[] { "setup", "local-observability", "--", "status" }));
        Assert.False(CommandTiers.IsReadOnlyLeaf(Array.Empty<string>()));

        // ... and the classifier treats every one of those as it always did: a change.
        Assert.Equal(CommandTier.StateChanging, CommandTiers.Classify(Words(Group + " status extra")));
        Assert.Equal(CommandTier.StateChanging, CommandTiers.Classify(Words("setup splunk status")));
        Assert.Equal(CommandTier.StateChanging, CommandTiers.Classify(Words("setup observability status")));
        Assert.Equal(CommandTier.StateChanging, CommandTiers.Classify(Words(Group)));
    }

    [Fact]
    public void Text_in_an_option_value_cannot_turn_a_change_into_a_read()
    {
        // The verb is read from the leading words only; a value after a flag is never part of it.
        foreach (var word in new[] { "status", "url", "env", "logs" })
        {
            Assert.Equal(CommandTier.StateChanging, CommandTiers.Classify(new[] { "setup", "local-observability", "up", "--service-name", word }));
            Assert.Equal(CommandTier.StateChanging, CommandTiers.Classify(new[] { "setup", "local-observability", "up", "--endpoint", word }));
            Assert.False(CommandTiers.IsReadOnlyLeaf(new[] { "setup", "local-observability", "up", "--service-name", word }));
        }
    }

    [Fact]
    public void A_preview_flag_keeps_the_meaning_it_always_had_and_does_not_make_a_change_a_read_leaf()
    {
        // `--help` on a change is a preview of it (the existing rule), but it does not make `up` one of the four reads.
        Assert.Equal(CommandTier.ReadOnly, CommandTiers.Classify(new[] { "setup", "local-observability", "up", "--help" }));
        Assert.False(CommandTiers.IsReadOnlyLeaf(new[] { "setup", "local-observability", "up", "--help" }));
    }

    [Fact]
    public void The_four_reads_may_run_unreviewed_by_their_bare_path_only_and_the_other_three_never()
    {
        foreach (var verb in new[] { "env", "logs", "status", "url" })
        {
            Assert.Contains(Group + " " + verb, CommandTiers.UnreviewedReadPaths);
            Assert.True(CommandTiers.IsUnreviewedRead(Words(Group + " " + verb)), verb);
            Assert.False(CommandTiers.IsUnreviewedRead(Words(Group + " " + verb + " --json")), verb);
        }

        foreach (var verb in new[] { "up", "down", "reset" })
        {
            Assert.DoesNotContain(Group + " " + verb, CommandTiers.UnreviewedReadPaths);
            Assert.False(CommandTiers.IsUnreviewedRead(Words(Group + " " + verb)), verb);
        }
    }
}
