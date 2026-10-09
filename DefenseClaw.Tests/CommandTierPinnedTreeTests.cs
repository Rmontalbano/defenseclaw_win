using System.Text.Json;
using DefenseClaw.Core.Cli;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// <see cref="CommandTierTreeTests"/> for the command tree of DefenseClaw source commit 95159fd
/// (<c>Fixtures/runtime-95159fd/cli/cli-tree.json</c>, every Click command and option of that commit, 263 leaves against 0.8.10's 179). The
/// 0.8.10 suite is untouched. What matters here is the same asymmetry: a verb the classifier calls read-only that mutates state would run
/// without a review step, so anything the newer tree adds that the classifier reads as a read must be a command someone read the help of.
/// </summary>
public sealed class CommandTierPinnedTreeTests
{
    private sealed record Option(string[] Names, bool Flag);

    private sealed record Command(string Path, bool Group, string Help, Option[] Options)
    {
        public string[] Tokens => Path.Split(' ');
    }

    private static readonly Command[] Tree = Load("runtime-95159fd/cli/cli-tree.json");

    private static Command[] Load(string fixture)
    {
        using var document = JsonDocument.Parse(FixtureFiles.ReadText(fixture));
        return document.RootElement.GetProperty("commands").EnumerateArray().Select(c => new Command(
                c.GetProperty("path").GetString()!,
                c.GetProperty("group").GetBoolean(),
                c.GetProperty("help").GetString() ?? string.Empty,
                c.GetProperty("options").EnumerateArray().Select(o => new Option(
                    o.GetProperty("names").EnumerateArray().Select(n => n.GetString()!).ToArray(),
                    o.GetProperty("flag").GetBoolean())).ToArray()))
            .ToArray();
    }

    private static IEnumerable<Command> Leaves => Tree.Where(c => !c.Group);

    /// <summary>
    /// The read-only leaves the classifier may call by path at that commit: the 0.8.10 reviewed set plus the leaves that commit added,
    /// each with its help read (see the comment on each). Growing this is a decision.
    /// </summary>
    private static readonly HashSet<string> ReviewedReadOnly = new(StringComparer.Ordinal)
    {
        // unchanged from 0.8.10
        "agent components history", "agent components show", "agent confidence policy show", "agent confidence policy validate",
        "agent discovery status", "agent processes", "agent signatures list", "agent signatures validate",
        "codeguard status", "config show", "config validate", "doctor",
        "guardrail judge list", "guardrail status", "keys check", "keys list", "mcp list",
        "observability plan", "plugin info", "plugin list", "policy list", "policy show", "policy validate",
        "registry entries", "registry list", "registry show", "skill info", "skill list", "skill search",
        "status", "tool list", "tool status", "version",

        // 0.8.10's local observability reads (CUST-311). The same four leaves, with the same help and options, are in the newer tree.
        "setup local-observability env", "setup local-observability logs", "setup local-observability status", "setup local-observability url",

        // added after 0.8.10; the help of each was read, and each only lists, shows, validates or probes this machine
        "acp status",                         // Show configured ACP posture and editor paths.
        "agent discovery runtime status",     // Show what the runtime planes can and cannot see right now.
        "guardrail protection list",          // List the opt-in protection packs and which scopes have them on.
        "sandbox doctor",                     // Check that this machine can run sandboxes (probes; Windows has no sandboxes).
        "sandbox image list", "sandbox list", "sandbox pack list", "sandbox pack show", "sandbox pack validate",
        "sandbox policy show", "sandbox status",
    };

    /// <summary>In 0.8.10's reviewed set and gone from the newer tree (replaced by <c>migrate --check</c>); the app never runs it.</summary>
    private const string RemovedRead = "migrations status";

    [Fact]
    public void The_fixture_is_the_whole_tree()
    {
        Assert.True(Leaves.Count() >= 250, $"only {Leaves.Count()} leaves - is the fixture truncated?");
        Assert.Contains(Tree, c => c.Path == "setup redaction");
    }

    [Fact]
    public void Every_command_path_fits_in_the_tokens_the_classifier_reads()
    {
        Assert.All(Tree, c => Assert.True(
            c.Tokens.Length <= CommandTiers.MaxPathTokens,
            $"'{c.Path}' is {c.Tokens.Length} tokens; CommandTiers.MaxPathTokens is {CommandTiers.MaxPathTokens}"));
    }

    [Fact]
    public void Every_unreviewed_read_path_the_app_knows_is_still_a_leaf_of_the_newer_tree()
    {
        var leafPaths = Leaves.Select(l => l.Path).ToHashSet(StringComparer.Ordinal);

        Assert.All(CommandTiers.UnreviewedReadPaths.Where(p => p != RemovedRead), path => Assert.Contains(path, leafPaths));
        Assert.DoesNotContain(RemovedRead, leafPaths);
    }

    [Fact]
    public void The_read_only_set_of_the_newer_tree_is_exactly_the_reviewed_one()
    {
        var readOnly = Leaves.Where(l => CommandTiers.Classify(l.Tokens) == CommandTier.ReadOnly).Select(l => l.Path).ToHashSet();

        Assert.Empty(readOnly.Except(ReviewedReadOnly));
        Assert.Empty(ReviewedReadOnly.Except(readOnly));

        // The app's own allow-list is the 0.8.10 set: nothing in it is a mutation on the newer tree, and every member but the removed one still exists.
        Assert.All(CommandTiers.UnreviewedReadPaths, path => Assert.True(ReviewedReadOnly.Contains(path) || path == RemovedRead, path));
    }

    [Fact]
    public void No_new_leaf_is_an_unreviewed_read_just_because_its_last_word_is_a_read_verb()
    {
        var offenders = Leaves.Where(l => !ReviewedReadOnly.Contains(l.Path) && CommandTiers.IsUnreviewedRead(l.Tokens)).Select(l => l.Path).ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void No_command_whose_help_says_it_changes_something_is_read_only()
    {
        string[] mutating =
        [
            "Add ", "Remove ", "Delete ", "Enable ", "Disable ", "Install ", "Uninstall ", "Set ", "Create ", "Update ", "Reset ", "Restore ",
            "Quarantine ", "Block ", "Allow ", "Unblock ", "Approve ", "Reject ", "Mark ", "Configure ", "Start ", "Stop ", "Rotate ", "Save ",
            "Write ", "Apply ", "Activate ", "Edit ", "Initialize ", "Wipe ", "Upgrade ", "Register ", "Toggle ", "Rewrite ", "Destroy ",
            "Dismiss ", "Record ", "Re-enable ", "Trigger ", "Fetch, ", "Zero-prompt", "Opt ", "Interactively prompt", "Launch ",
        ];

        var offenders = Leaves
            .Where(l => mutating.Any(o => l.Help.StartsWith(o, StringComparison.Ordinal)))
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
    }

    [Fact]
    public void A_value_taking_read_only_spelling_never_makes_a_command_read_only()
    {
        var offenders = new List<string>();
        foreach (var command in Tree.Where(c => !ReviewedReadOnly.Contains(c.Path)))
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
    }

    [Fact]
    public void Every_option_that_prints_secrets_is_recognised_and_takes_a_command_out_of_read_only()
    {
        var sensitive = new[] { "--reveal", "--show-values", "--show-credentials" };

        foreach (var command in Tree.Where(c => c.Options.Any(o => o.Names.Any(sensitive.Contains))))
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
    public void Options_whose_names_suggest_they_print_a_secret_are_all_known_to_the_classifier()
    {
        // The newer tree may add flags that reveal values; any option named like one must either be recognised or be listed here after reading it.
        var suspicious = Tree
            .SelectMany(c => c.Options.SelectMany(o => o.Names).Select(n => (c.Path, Name: n)))
            .Where(x => x.Name.Contains("reveal", StringComparison.Ordinal) || x.Name.Contains("show-", StringComparison.Ordinal) ||
                        x.Name.Contains("secret", StringComparison.Ordinal) || x.Name.Contains("plaintext", StringComparison.Ordinal))
            .Where(x => !CommandTiers.PrintsSecrets(x.Path.Split(' ').Append(x.Name).ToArray()))
            .Select(x => $"{x.Path} {x.Name}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        // Each of these was read: they select what to display or what to store, not a value to print.
        Assert.Empty(suspicious.Except(Reviewed));
    }

    private static readonly HashSet<string> Reviewed = new(StringComparer.Ordinal)
    {
        "agent usage --show-gone",                          // also list signals that have gone: a filter
        "registry test --show-entries",                     // print the catalog's entries: names, not credentials
        "setup guardrail --judge-bedrock-secret-key-env",   // the NAME of an environment variable
        "setup llm --bedrock-secret-key-env",
        "setup provider add --bedrock-secret-key-env",
        "setup webhook add --secret-env",
        "setup observability add --plaintext",              // "send OTLP without TLS": a transport choice
    };

    [Fact]
    public void No_option_the_classifier_treats_as_a_switch_takes_a_value_anywhere_in_the_newer_tree()
    {
        var names = Tree.SelectMany(c => c.Options).SelectMany(o => o.Names).Distinct().ToArray();
        var valueTaking = Tree.SelectMany(c => c.Options).Where(o => !o.Flag).SelectMany(o => o.Names).ToHashSet();

        var wrong = names.Where(n => CommandTiers.IsSwitch(n) && valueTaking.Contains(n)).ToArray();

        Assert.Empty(wrong);
    }
}
