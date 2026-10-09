using System.Text.Json;
using DefenseClaw.Core.Cli;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// The classifier held against the whole DefenseClaw 0.8.10 command tree (<c>Fixtures/cli-tree-0.8.10.json</c>: every
/// command, its options and whether each takes a value, captured from the live CLI with Click's own introspection),
/// not against the handful of argv the panels happen to build. A verb the classifier calls read-only that mutates
/// state would run without a confirmation; so the read-only set is pinned, and the shapes that used to slip past
/// (D3-08) are checked on every command they could apply to.
/// </summary>
public sealed class CommandTierTreeTests
{
    private sealed record Option(string[] Names, bool Flag);

    private sealed record Command(string Path, bool Group, string Help, Option[] Options)
    {
        public string[] Tokens => Path.Split(' ');

        public bool IsLeaf => !Group;
    }

    private static readonly Command[] Tree = Load();

    private static Command[] Load()
    {
        using var document = JsonDocument.Parse(FixtureFiles.ReadText("cli-tree-0.8.10.json"));
        return document.RootElement.GetProperty("commands").EnumerateArray().Select(c => new Command(
                c.GetProperty("path").GetString()!,
                c.GetProperty("group").GetBoolean(),
                c.GetProperty("help").GetString() ?? string.Empty,
                c.GetProperty("options").EnumerateArray().Select(o => new Option(
                    o.GetProperty("names").EnumerateArray().Select(n => n.GetString()!).ToArray(),
                    o.GetProperty("flag").GetBoolean())).ToArray()))
            .ToArray();
    }

    private static IEnumerable<Command> Leaves => Tree.Where(c => c.IsLeaf);

    /// <summary>
    /// Every leaf the classifier may call read-only, by path. Each is a verb whose help says it lists, shows, checks or
    /// validates. Growing this list is a decision: read the new command's help first.
    /// </summary>
    private static readonly HashSet<string> ReadOnlyLeaves = new(StringComparer.Ordinal)
    {
        "agent components history", "agent components show", "agent confidence policy show", "agent confidence policy validate",
        "agent discovery status", "agent processes", "agent signatures list", "agent signatures validate",
        "codeguard status", "config show", "config validate", "doctor",
        "guardrail judge list", "guardrail status", "keys check", "keys list", "mcp list", "migrations status",
        "observability plan", "plugin info", "plugin list", "policy list", "policy show", "policy validate",
        "registry entries", "registry list", "registry show",

        // The local observability stack's reads (CUST-311), each read in commands/cmd_setup_local_observability.py: url and env print
        // constants, status and logs run `docker compose ps` / `logs --tail 200` and probe loopback. `up`, `down` and `reset` are not here.
        "setup local-observability env", "setup local-observability logs", "setup local-observability status", "setup local-observability url",
        "skill info", "skill list", "skill search",
        "status", "tool list", "tool status", "version",

        // The Setup list editors' reads (CUST-326), each read in cmd_setup_observability.py, cmd_setup_webhook.py and the trusted-paths group of
        // cmd_setup.py: they list or show what config.yaml holds. Read-only by the classifier, but NOT on the unreviewed allow-list.
        "setup observability list", "setup trusted-paths list", "setup webhook list", "setup webhook show",
    };

    private static readonly string[] SetupResourceReads =
    [
        "setup observability list", "setup trusted-paths list", "setup webhook list", "setup webhook show",
    ];

    private static readonly HashSet<string> ReadVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "list", "show", "status", "info", "check", "validate", "version", "search", "doctor", "entries", "processes",
        "history", "plan", "url", "env", "logs",
    };

    // How a command's own help opens when it changes something. A leaf whose help starts with one of these is a
    // mutation whatever its name suggests.
    private static readonly string[] MutatingHelpOpenings =
    {
        "Add ", "Remove ", "Delete ", "Enable ", "Disable ", "Install ", "Uninstall ", "Set ", "Create ", "Update ",
        "Reset ", "Restore ", "Quarantine ", "Block ", "Allow ", "Unblock ", "Approve ", "Reject ", "Mark ", "Configure ",
        "Start ", "Stop ", "Rotate ", "Save ", "Write ", "Apply ", "Activate ", "Edit ", "Initialize ", "Wipe ", "Upgrade ",
        "Register ", "Toggle ", "Rewrite ", "Destroy ", "Dismiss ", "Record ", "Re-enable ", "Trigger ", "Fetch, ",
        "Zero-prompt", "Opt ", "Interactively prompt", "Launch ",
    };

    [Fact]
    public void The_fixture_is_the_whole_tree()
    {
        Assert.True(Leaves.Count() >= 170, $"only {Leaves.Count()} leaves - is the fixture truncated?");
        Assert.Contains(Tree, c => c.Path == "setup splunk dashboards destroy");
    }

    [Fact]
    public void Every_command_path_fits_in_the_tokens_the_classifier_reads()
    {
        // A verb in a token the classifier never reads is a verb it cannot see.
        Assert.All(Tree, c => Assert.True(
            c.Tokens.Length <= CommandTiers.MaxPathTokens,
            $"'{c.Path}' is {c.Tokens.Length} tokens; CommandTiers.MaxPathTokens is {CommandTiers.MaxPathTokens}"));
    }

    [Fact]
    public void The_read_only_set_is_exactly_the_reviewed_one()
    {
        var readOnly = Leaves.Where(l => CommandTiers.Classify(l.Tokens) == CommandTier.ReadOnly).Select(l => l.Path).ToHashSet();

        Assert.Empty(readOnly.Except(ReadOnlyLeaves));
        Assert.Empty(ReadOnlyLeaves.Except(readOnly));
    }

    [Fact]
    public void The_allow_list_of_unreviewed_reads_is_exactly_the_reviewed_read_only_set()
    {
        // What may run with no review step is not "whatever the classifier calls read-only" but this list, and growing it is a
        // decision made twice: here, after reading the new command's help, and in CommandTiers.UnreviewedReadPaths.
        Assert.Equal(
            ReadOnlyLeaves.Except(SetupResourceReads).Order(StringComparer.Ordinal),
            CommandTiers.UnreviewedReadPaths.Order(StringComparer.Ordinal));
        Assert.All(SetupResourceReads, path => Assert.False(CommandTiers.IsUnreviewedRead(path.Split(' ')), path));
    }

    [Fact]
    public void Every_unreviewed_read_is_a_real_leaf_of_the_tree_and_the_classifier_agrees_it_is_a_read()
    {
        var leafPaths = Leaves.Select(l => l.Path).ToHashSet(StringComparer.Ordinal);

        foreach (var path in CommandTiers.UnreviewedReadPaths)
        {
            Assert.Contains(path, leafPaths);
            var argv = path.Split(' ');
            Assert.True(CommandTiers.IsUnreviewedRead(argv), path);
            Assert.Equal(CommandTier.ReadOnly, CommandTiers.Classify(argv));
        }
    }

    [Fact]
    public void No_leaf_outside_the_reviewed_set_is_an_unreviewed_read_not_even_with_a_read_verb_at_the_end()
    {
        var offenders = Leaves.Where(l => !ReadOnlyLeaves.Contains(l.Path) && CommandTiers.IsUnreviewedRead(l.Tokens)).Select(l => l.Path).ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Only_read_verbs_are_ever_read_only()
    {
        foreach (var leaf in Leaves.Where(l => CommandTiers.Classify(l.Tokens) == CommandTier.ReadOnly))
        {
            Assert.True(ReadVerbs.Contains(leaf.Tokens[^1]), $"'{leaf.Path}' is read-only but '{leaf.Tokens[^1]}' is not a read verb");
        }
    }

    [Fact]
    public void No_command_whose_help_says_it_changes_something_is_read_only()
    {
        var offenders = Leaves
            .Where(l => MutatingHelpOpenings.Any(o => l.Help.StartsWith(o, StringComparison.Ordinal)))
            .Where(l => CommandTiers.Classify(l.Tokens) == CommandTier.ReadOnly)
            .Select(l => $"{l.Path}: {l.Help}")
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Every_leaf_with_a_destructive_verb_in_its_path_is_destructive()
    {
        var destructive = new[] { "remove", "delete", "reset", "uninstall", "quarantine", "teardown", "destroy", "unset", "dismiss" };

        var wrong = Leaves
            .Where(l => l.Tokens.Any(t => destructive.Contains(t)))
            .Where(l => CommandTiers.Classify(l.Tokens) != CommandTier.Destructive)
            .Select(l => l.Path)
            .ToArray();

        Assert.Empty(wrong);
        Assert.Equal(CommandTier.Destructive, CommandTiers.Classify(["setup", "splunk", "dashboards", "destroy"]));
    }

    [Fact]
    public void A_value_taking_read_only_spelling_never_makes_a_command_read_only()
    {
        // upgrade --version 0.9.0 is an upgrade; the same shape on any command whose --version/--help/--dry-run takes
        // a value must not read as the query.
        var offenders = new List<string>();
        foreach (var command in Tree.Where(c => !ReadOnlyLeaves.Contains(c.Path)))
        {
            foreach (var option in command.Options.Where(o => !o.Flag))
            {
                foreach (var name in option.Names.Where(n => n is "--version" or "--help" or "--dry-run" or "--version-json"))
                {
                    var argv = command.Tokens.Concat(new[] { name, "value", "--yes" }).ToArray();
                    if (CommandTiers.Classify(argv) == CommandTier.ReadOnly)
                    {
                        offenders.Add(string.Join(' ', argv));
                    }
                }
            }
        }

        Assert.Empty(offenders);
        Assert.Contains(Tree, c => c.Path == "upgrade" && c.Options.Any(o => !o.Flag && o.Names.Contains("--version")));
        Assert.NotEqual(CommandTier.ReadOnly, CommandTiers.Classify(["upgrade", "--version", "0.9.0", "--yes"]));
    }

    [Fact]
    public void Every_option_that_prints_secrets_is_recognised_and_takes_a_command_out_of_read_only()
    {
        // The options the CLI itself documents as printing secret values.
        var sensitive = new[] { "--reveal", "--show-values", "--show-credentials" };
        var found = Tree.Where(c => c.Options.Any(o => o.Names.Any(sensitive.Contains))).ToArray();

        // config show --reveal, keys list --show-values, setup splunk --show-credentials.
        Assert.Contains(found, c => c.Path == "config show");
        Assert.Contains(found, c => c.Path == "keys list");
        Assert.Contains(found, c => c.Path == "setup splunk");

        foreach (var command in found)
        {
            foreach (var name in command.Options.SelectMany(o => o.Names).Where(sensitive.Contains))
            {
                var argv = command.Tokens.Append(name).ToArray();
                Assert.True(CommandTiers.PrintsSecrets(argv), string.Join(' ', argv));
                Assert.NotEqual(CommandTier.ReadOnly, CommandTiers.Classify(argv));
            }
        }
    }

    [Fact]
    public void No_option_the_classifier_treats_as_a_switch_takes_a_value_anywhere_in_the_tree()
    {
        // CommandTiers.IsStandaloneFlag lets a read-only flag follow a switch without becoming its value. If a
        // "switch" took a value on some command, "--x --dry-run" there would be a name and not a preview.
        var names = Tree.SelectMany(c => c.Options).SelectMany(o => o.Names).Distinct().ToArray();
        var valueTaking = Tree.SelectMany(c => c.Options).Where(o => !o.Flag).SelectMany(o => o.Names).ToHashSet();

        var wrong = names.Where(n => CommandTiers.IsSwitch(n) && valueTaking.Contains(n)).ToArray();

        Assert.Empty(wrong);
        Assert.All(new[] { "--yes", "--json", "--fix", "--no-restart", "--skip-scan" }, n => Assert.True(CommandTiers.IsSwitch(n), n));
    }
}
