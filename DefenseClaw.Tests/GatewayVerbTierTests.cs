using DefenseClaw.Core.Cli;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// The fixed <c>defenseclaw-gateway</c> verbs the command palette offers beyond start, stop, restart and status (CUST-264): <c>watchdog start | stop |
/// status</c>, <c>connector verify | list-backups | teardown</c>, <c>policy reload | domains</c>. Each is held to the installed 0.8.10 gateway's own help
/// screens (<c>Fixtures/runtime-0.8.10/gateway</c>, captured with <c>--help</c>; nothing was run), and its tier is whatever <see cref="CommandTiers"/> says
/// of the exact argv - there is no second table of tiers.
/// </summary>
public sealed class GatewayVerbTierTests
{
    private static string Screen(string name) => FixtureFiles.ReadText("runtime-0.8.10/gateway/" + name);

    /// <summary>The words of the "Available Commands:" block of a help screen: the first word of each line up to the next blank line.</summary>
    private static string[] Commands(string screen)
    {
        var lines = screen.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var start = Array.FindIndex(lines, l => l.StartsWith("Available Commands:", StringComparison.Ordinal));
        Assert.True(start >= 0, "no Available Commands block");
        return lines.Skip(start + 1).TakeWhile(l => l.Length > 0).Select(l => l.Trim().Split(' ')[0]).ToArray();
    }

    /// <summary>The exact argv of every verb, with the tier the classifier gives it and whether it may run with no review.</summary>
    public static IEnumerable<object[]> Verbs() => new[]
    {
        new object[] { new[] { "watchdog", "start" }, CommandTier.StateChanging, false },
        new object[] { new[] { "watchdog", "stop" }, CommandTier.StateChanging, false },
        new object[] { new[] { "watchdog", "status" }, CommandTier.ReadOnly, true },
        new object[] { new[] { "connector", "verify" }, CommandTier.ReadOnly, true },
        new object[] { new[] { "connector", "list-backups" }, CommandTier.ReadOnly, true },
        new object[] { new[] { "connector", "teardown" }, CommandTier.Destructive, false },
        new object[] { new[] { "policy", "reload" }, CommandTier.StateChanging, false },
        new object[] { new[] { "policy", "domains" }, CommandTier.ReadOnly, true },
    };

    // ------------------------------------------------------------------ the 0.8.10 help screens

    [Theory]
    [MemberData(nameof(Verbs))]
    public void Every_verb_is_in_the_installed_gateways_help(string[] argv, CommandTier tier, bool unreviewed)
    {
        _ = (tier, unreviewed);

        // The group is a command of the gateway itself, and the verb is in the group's own list.
        Assert.Contains(argv[0], Commands(Screen("root.txt")));
        Assert.Contains(argv[1], Commands(Screen(argv[0] + ".txt")));

        // And the verb has a screen of its own with exactly this usage: no required argument, nothing to type.
        var verbs = Screen("verbs.txt").Replace("\r\n", "\n", StringComparison.Ordinal);
        var heading = $"=== defenseclaw-gateway {argv[0]} {argv[1]} --help";
        var at = verbs.IndexOf(heading, StringComparison.Ordinal);
        Assert.True(at >= 0, heading);
        var section = verbs[at..];
        var next = section.IndexOf("\n=== ", heading.Length, StringComparison.Ordinal);
        if (next >= 0)
        {
            section = section[..next];
        }

        Assert.Contains($"Usage:\n  defenseclaw-gateway {argv[0]} {argv[1]} [flags]\n", section, StringComparison.Ordinal);
    }

    [Fact]
    public void The_gateway_run_with_nothing_after_it_starts_a_daemon_so_no_verb_is_bare()
    {
        // The root screen says so; this is why every verb here is a two-word argv and nothing builds one from less.
        Assert.Contains("Run without arguments to start the sidecar daemon.", Screen("root.txt"), StringComparison.Ordinal);
        Assert.All(Verbs(), v => Assert.Equal(2, ((string[])v[0]).Length));
    }

    [Fact]
    public void What_the_help_says_the_verbs_do_is_what_the_reviews_and_tiers_assume()
    {
        var verbs = Screen("verbs.txt");

        // teardown restores a backup, removes what the connector wrote and clears its shims, and leaves the service, the token and the audit database.
        Assert.Contains("Restoring the agent framework's config from its pristine backup.", verbs, StringComparison.Ordinal);
        Assert.Contains("This subcommand does NOT touch the sidecar's own systemd unit, the", verbs, StringComparison.Ordinal);

        // verify is a check with three outcomes, none of which writes anything.
        Assert.Contains("0   connector is clean", verbs, StringComparison.Ordinal);
        Assert.Contains("1   connector has residual state", verbs, StringComparison.Ordinal);
        Assert.Contains("2   connector unknown / config error", verbs, StringComparison.Ordinal);

        // reload is answered by the running sidecar; the watchdog's start is a background daemon.
        Assert.Contains("Tell the running sidecar daemon to reload OPA policies", verbs, StringComparison.Ordinal);
        Assert.Contains("Start the watchdog as a background daemon", verbs, StringComparison.Ordinal);
        Assert.Contains("Stop the running watchdog daemon", verbs, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ tiers and the review gate

    [Theory]
    [MemberData(nameof(Verbs))]
    public void The_tier_is_the_classifiers_and_only_a_listed_read_runs_without_a_review(string[] argv, CommandTier tier, bool unreviewed)
    {
        Assert.Equal(tier, CommandTiers.Classify(argv));
        Assert.Equal(unreviewed, CommandTiers.IsUnreviewedGatewayRead(argv));

        // Not one of the Python CLI's list, whatever the words: the two executables have a list each.
        Assert.False(CommandTiers.IsUnreviewedRead(argv));
    }

    [Fact]
    public void The_gateways_unreviewed_reads_are_exactly_these_six()
    {
        Assert.Equal(
            new[] { "connector list-backups", "connector verify", "policy domains", "provenance show", "status", "watchdog status" },
            CommandTiers.UnreviewedGatewayReadPaths.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("connector", "verify", "--json")]
    [InlineData("connector", "verify", "--connector", "codex")]
    [InlineData("connector", "list-backups", "--data-dir", "D:\\data", "--json")]
    [InlineData("policy", "domains", "--json")]
    public void The_reads_stay_reads_with_their_own_options_but_are_then_reviewed(params string[] argv)
    {
        Assert.Equal(CommandTier.ReadOnly, CommandTiers.Classify(argv));

        // Only the bare verb is on the list: an argv with an option on it is shown first.
        Assert.False(CommandTiers.IsUnreviewedGatewayRead(argv));
    }

    [Theory]
    [InlineData("connector", "verify", "--reveal")]
    [InlineData("connector", "verify", "--fix")]
    [InlineData("connector", "verify", "extra")]
    [InlineData("connector", "verify-all")]
    [InlineData("connector", "list-backups", "restore")]
    [InlineData("policy", "domains", "add")]
    [InlineData("Connector", "verify")]
    [InlineData("verify")]
    [InlineData("domains")]
    [InlineData("connector")]
    [InlineData("connector", "reconcile")]
    [InlineData("policy", "evaluate")]
    [InlineData("policy", "evaluate-firewall")]
    public void A_lookalike_does_not_inherit_the_exemption(params string[] argv)
    {
        Assert.NotEqual(CommandTier.ReadOnly, CommandTiers.Classify(argv));
        Assert.False(CommandTiers.IsUnreviewedGatewayRead(argv));
    }

    [Theory]
    [InlineData("connector", "teardown", "--json")]
    [InlineData("connector", "teardown", "--connector", "codex")]
    public void Teardown_is_destructive_with_any_options(params string[] argv) =>
        Assert.Equal(CommandTier.Destructive, CommandTiers.Classify(argv));

    [Fact]
    public void The_shared_vocabulary_did_not_move()
    {
        // `verify`, `list-backups` and `domains` are leaves of the gateway, named in full; they are not read verbs everywhere. Another command that
        // happens to use one of the words is still a change until someone has read it.
        Assert.Equal(CommandTier.StateChanging, CommandTiers.Classify(new[] { "acp", "verify" }));
        Assert.Equal(CommandTier.StateChanging, CommandTiers.Classify(new[] { "skill", "list-backups" }));
        Assert.Equal(CommandTier.StateChanging, CommandTiers.Classify(new[] { "firewall", "domains" }));
        Assert.Equal(CommandTier.ReadOnly, CommandTiers.Classify(new[] { "skill", "list" }));
    }
}
