using System.Text.Json;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Paths;
using DefenseClaw.Core.Setup;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// CUST-270: the exact command line of every verb the Setup editors run, held against the whole 0.8.10 command tree (a verb or a flag the
/// installed CLI does not have must never be built), the tier each one earns, and the gate: on a managed (read-only) installation the three
/// lists and the webhook <c>show</c> run, and nothing else of these does.
/// </summary>
public sealed class SetupResourceArgvTests
{
    private sealed record Leaf(string Path, string[] Flags);

    private static readonly Leaf[] Leaves = LoadLeaves();

    private static Leaf[] LoadLeaves()
    {
        using var document = JsonDocument.Parse(FixtureFiles.ReadText("cli-tree-0.8.10.json"));
        return document.RootElement.GetProperty("commands").EnumerateArray()
            .Where(c => !c.GetProperty("group").GetBoolean())
            .Select(c => new Leaf(
                c.GetProperty("path").GetString()!,
                c.GetProperty("options").EnumerateArray().SelectMany(o => o.GetProperty("names").EnumerateArray().Select(n => n.GetString()!)).ToArray()))
            .ToArray();
    }

    private static InstallationContext Managed() => new InstallationMachine().WithSecureClientLayout().Resolve();

    private static InstallationContext Writable() => InstallationContext.Unmanaged(@"C:\data\.defenseclaw");

    private static string[] Words(string line) => line.Split(' ');

    // ---- the lists ----

    [Theory]
    [InlineData(SetupResource.Observability, "setup observability list --json")]
    [InlineData(SetupResource.Webhooks, "setup webhook list --json")]
    [InlineData(SetupResource.TrustedPaths, "setup trusted-paths list --json")]
    public void Each_list_is_the_documented_json_read(SetupResource resource, string expected) =>
        Assert.Equal(Words(expected), SetupResourceArgv.List(resource));

    [Fact]
    public void The_webhook_show_names_its_target_after_the_end_of_the_options() =>
        Assert.Equal(Words("setup webhook show --json -- example-slack"), SetupResourceArgv.ShowWebhook("example-slack"));

    [Fact]
    public void Observability_has_no_show_in_0_8_10_and_none_is_built()
    {
        Assert.DoesNotContain(Leaves, l => l.Path == "setup observability show");
        Assert.Contains(Leaves, l => l.Path == "setup webhook show");
        Assert.False(SetupResourceArgv.IsRead(Words("setup observability show --json -- x")));
    }

    // ---- the changes ----

    [Fact]
    public void Enable_disable_and_test_name_their_target_after_the_end_of_the_options()
    {
        foreach (var (resource, noun) in new[] { (SetupResource.Observability, "observability"), (SetupResource.Webhooks, "webhook") })
        {
            Assert.Equal(Words($"setup {noun} enable -- one"), SetupResourceArgv.Enable(resource, "one"));
            Assert.Equal(Words($"setup {noun} disable -- one"), SetupResourceArgv.Disable(resource, "one"));
            Assert.Equal(Words($"setup {noun} test -- one"), SetupResourceArgv.Test(resource, "one"));
        }
    }

    [Fact]
    public void Remove_answers_the_clis_question_with_yes_and_a_trusted_path_is_removed_by_its_resolved_directory()
    {
        Assert.Equal(Words("setup observability remove --yes -- one"), SetupResourceArgv.Remove(SetupResource.Observability, "one"));
        Assert.Equal(Words("setup webhook remove --yes -- one"), SetupResourceArgv.Remove(SetupResource.Webhooks, "one"));
        Assert.Equal(
            new[] { "setup", "trusted-paths", "remove", "--", @"C:\Users\synthetic\.synthetic-tools\bin" },
            SetupResourceArgv.Remove(SetupResource.TrustedPaths, @"C:\Users\synthetic\.synthetic-tools\bin"));
    }

    [Fact]
    public void A_trusted_path_has_nothing_to_enable_disable_or_test()
    {
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => SetupResourceArgv.Enable(SetupResource.TrustedPaths, "x"));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => SetupResourceArgv.Disable(SetupResource.TrustedPaths, "x"));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => SetupResourceArgv.Test(SetupResource.TrustedPaths, "x"));
        Assert.DoesNotContain(Leaves, l => l.Path is "setup trusted-paths enable" or "setup trusted-paths disable" or "setup trusted-paths test");
    }

    [Fact]
    public void Every_command_built_is_a_leaf_of_the_0_8_10_tree_and_every_flag_in_it_is_one_the_leaf_has()
    {
        var built = new List<IReadOnlyList<string>>
        {
            SetupResourceArgv.List(SetupResource.Observability),
            SetupResourceArgv.List(SetupResource.Webhooks),
            SetupResourceArgv.List(SetupResource.TrustedPaths),
            SetupResourceArgv.ShowWebhook("x"),
            SetupResourceArgv.Remove(SetupResource.TrustedPaths, "x"),
        };
        foreach (var resource in new[] { SetupResource.Observability, SetupResource.Webhooks })
        {
            built.Add(SetupResourceArgv.Enable(resource, "x"));
            built.Add(SetupResourceArgv.Disable(resource, "x"));
            built.Add(SetupResourceArgv.Test(resource, "x"));
            built.Add(SetupResourceArgv.Remove(resource, "x"));
        }

        foreach (var argv in built)
        {
            var path = string.Join(' ', argv.TakeWhile(a => !a.StartsWith('-')).Take(CommandTiers.MaxPathTokens));
            var leaf = Leaves.SingleOrDefault(l => l.Path == path);
            Assert.True(leaf is not null, $"'{path}' is not a command of the 0.8.10 CLI");

            foreach (var flag in argv.TakeWhile(a => a != "--").Where(a => a.StartsWith('-')))
            {
                Assert.Contains(flag, leaf!.Flags);
            }
        }
    }

    [Theory]
    [InlineData("enable", CommandTier.StateChanging)]
    [InlineData("disable", CommandTier.StateChanging)]
    [InlineData("test", CommandTier.StateChanging)]
    [InlineData("remove", CommandTier.Destructive)]
    public void A_change_earns_its_tier_whatever_the_target_is_called(string verb, CommandTier expected)
    {
        foreach (var resource in new[] { SetupResource.Observability, SetupResource.Webhooks })
        {
            // A target that spells a read (or a preview flag) is after the end of the options and cannot move the tier.
            foreach (var target in new[] { "plain", "list", "show", "status", "--help", "--dry-run" })
            {
                var argv = verb switch
                {
                    "enable" => SetupResourceArgv.Enable(resource, target),
                    "disable" => SetupResourceArgv.Disable(resource, target),
                    "test" => SetupResourceArgv.Test(resource, target),
                    _ => SetupResourceArgv.Remove(resource, target),
                };

                Assert.Equal(expected, TierShown(argv));
            }
        }
    }

    [Fact]
    public void The_lists_and_the_webhook_show_are_read_only_by_the_classifier_itself_so_every_surface_agrees_with_the_guard()
    {
        // CUST-326: they were once called a change by the first-verb classifier and let through by the guard alone. Both say read now.
        var reads = new List<IReadOnlyList<string>> { SetupResourceArgv.ShowWebhook("example-slack") };
        reads.AddRange(new[] { SetupResource.Observability, SetupResource.Webhooks, SetupResource.TrustedPaths }.Select(SetupResourceArgv.List));

        foreach (var argv in reads)
        {
            Assert.True(SetupResourceArgv.IsRead(argv));
            Assert.Equal(CommandTier.ReadOnly, TierShown(argv));
            Assert.Equal(CommandTier.ReadOnly, InstallationGate.TierOf("defenseclaw", argv));
            Assert.Equal(CommandTier.ReadOnly, InstallationGate.TierOf("defenseclaw.exe", argv));
        }

        // A name that spells a verb comes after the end of the options and moves nothing.
        Assert.Equal(CommandTier.ReadOnly, TierShown(SetupResourceArgv.ShowWebhook("remove")));
    }

    [Theory]
    [InlineData("setup observability show --json -- x")]      // no such command; unknown is a change
    [InlineData("setup trusted-paths show --json")]
    [InlineData("setup observability list extra --json")]    // a fourth token is not the leaf
    [InlineData("setup webhook list --reveal")]              // a secret-printing flag is never a read
    [InlineData("setup webhook show --show-values --json -- x")]
    [InlineData("setup splunk list --json")]
    [InlineData("setup webhook add --json")]
    [InlineData("setup webhook test -- x")]
    [InlineData("setup trusted-paths add C:/opt")]
    [InlineData("setup trusted-paths list-all")]
    [InlineData("setup")]
    public void Nothing_near_the_four_reads_inherits_their_tier(string line) =>
        Assert.NotEqual(CommandTier.ReadOnly, TierShown(Words(line)));

    [Fact]
    public void Removing_a_trusted_path_is_destructive()
    {
        foreach (var target in new[] { @"C:\opt\tools", "list", "--help", "--dry-run" })
        {
            Assert.Equal(CommandTier.Destructive, TierShown(SetupResourceArgv.Remove(SetupResource.TrustedPaths, target)));
        }
    }

    /// <summary>What the review shows (<c>CommandReview.ResolveTier</c>): the classifier over the argv up to the end-of-options marker.</summary>
    private static CommandTier TierShown(IReadOnlyList<string> argv)
    {
        var terminator = argv.ToList().IndexOf("--");
        return CommandTiers.Classify(terminator < 0 ? argv : argv.Take(terminator).ToArray());
    }

    // ---- what counts as a read ----

    public static TheoryData<string, bool> Shapes => new()
    {
        { "setup observability list --json", true },
        { "setup webhook list --json", true },
        { "setup trusted-paths list --json", true },
        { "setup webhook show --json -- example-slack", true },
        { "setup webhook show --json -- a.b_c-1", true },

        // not these exact shapes
        { "setup observability list", false },
        { "setup webhook list", false },
        { "setup observability list --json --connector claudecode", false },
        { "setup webhook list --json --connector claudecode", false },
        { "setup webhook list --connector claudecode --json", false },
        { "setup trusted-paths list --json --extra", false },
        { "setup splunk list --json", false },
        { "setup local-observability list --json", false },
        { "setup webhook show example-slack", false },
        { "setup webhook show --json example-slack", false },
        { "setup webhook show --json -- ", false },
        { "setup webhook show --json -- -x", false },
        { "setup observability show --json -- x", false },
        { "Setup Webhook Show --json -- x", false },
        { "setup webhook test -- x", false },
        { "setup webhook enable -- x", false },
        { "setup webhook remove --yes -- x", false },
        { "setup observability test -- x", false },
        { "setup observability disable -- x", false },
        { "setup trusted-paths remove -- C:\\opt", false },
        { "setup trusted-paths add C:\\opt", false },
        { "setup trusted-paths list --json -- remove", false },
        { "setup webhook show --json -- x --reveal", false },
        { "", false },
    };

    [Theory]
    [MemberData(nameof(Shapes))]
    public void Only_the_exact_shapes_of_the_lists_and_the_webhook_show_are_reads(string line, bool expected)
    {
        var argv = line.Length == 0 ? Array.Empty<string>() : Words(line);

        Assert.Equal(expected, SetupResourceArgv.IsRead(argv));
    }

    [Fact]
    public void A_read_that_carries_a_target_with_a_space_or_a_control_character_is_not_a_read()
    {
        Assert.False(SetupResourceArgv.IsRead(["setup", "webhook", "show", "--json", "--", "two names"]));
        Assert.False(SetupResourceArgv.IsRead(["setup", "webhook", "show", "--json", "--", "x\ny"]));
        Assert.False(SetupResourceArgv.IsRead(["setup", "webhook", "show", "--json", "--", "*"]));
        Assert.False(SetupResourceArgv.IsRead(["setup", "webhook", "show", "--json", "--", "%USERNAME%"]));
        Assert.False(SetupResourceArgv.IsRead(["setup", "webhook", "show", "--json", "--", new string('a', SetupResourceArgv.MaxNameLength + 1)]));
        Assert.True(SetupResourceArgv.IsRead(["setup", "webhook", "show", "--json", "--", new string('a', SetupResourceArgv.MaxNameLength)]));
    }

    [Theory]
    [InlineData("local-sqlite", true)]
    [InlineData("example-otlp", true)]
    [InlineData("a", true)]
    [InlineData("Mixed.Case_1-x", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("-leading-dash", false)]
    [InlineData(".leading-dot", false)]
    [InlineData("has space", false)]
    [InlineData("tab\there", false)]
    [InlineData("wild*card", false)]
    [InlineData("wild?card", false)]
    [InlineData("br[ack]et", false)]
    [InlineData("%USERNAME%", false)]
    [InlineData("$HOME", false)]
    [InlineData("~tilde", false)]
    [InlineData("quote\"d", false)]
    [InlineData("semi;colon", false)]
    [InlineData("amp&ersand", false)]
    [InlineData("bidi\u202Eeman", false)]
    [InlineData("caf\u00E9", false)]
    public void Only_a_plain_name_is_put_on_a_command_line(string? name, bool expected) =>
        Assert.Equal(expected, SetupResourceArgv.IsSafeName(name));

    // ---- the gate ----

    [Fact]
    public void A_managed_installation_runs_the_lists_and_the_webhook_show_and_refuses_every_change()
    {
        var managed = Managed();
        Assert.False(managed.IsMutable);

        var reads = new List<IReadOnlyList<string>>
        {
            SetupResourceArgv.List(SetupResource.Observability),
            SetupResourceArgv.List(SetupResource.Webhooks),
            SetupResourceArgv.List(SetupResource.TrustedPaths),
            SetupResourceArgv.ShowWebhook("example-slack"),
        };
        foreach (var argv in reads)
        {
            Assert.Null(InstallationGate.RefusalFor(managed, "defenseclaw", argv));
            Assert.Equal(CommandTier.ReadOnly, InstallationGate.TierOf("defenseclaw", argv));
        }

        var changes = new List<IReadOnlyList<string>> { SetupResourceArgv.Remove(SetupResource.TrustedPaths, @"C:\opt\tools") };
        foreach (var resource in new[] { SetupResource.Observability, SetupResource.Webhooks })
        {
            changes.Add(SetupResourceArgv.Enable(resource, "x"));
            changes.Add(SetupResourceArgv.Disable(resource, "x"));
            changes.Add(SetupResourceArgv.Test(resource, "x"));
            changes.Add(SetupResourceArgv.Remove(resource, "x"));
        }

        foreach (var argv in changes)
        {
            Assert.Equal(managed.BlockedReason, InstallationGate.RefusalFor(managed, "defenseclaw", argv));
            Assert.NotEqual(CommandTier.ReadOnly, InstallationGate.TierOf("defenseclaw", argv));
            Assert.Null(InstallationGate.RefusalFor(Writable(), "defenseclaw", argv));
        }
    }

    [Fact]
    public void The_gate_reads_only_the_defenseclaw_cli_with_these_shapes()
    {
        var managed = Managed();

        // The same words handed to another program are a change: unknown is never a read.
        Assert.NotNull(InstallationGate.RefusalFor(managed, "cmd.exe", SetupResourceArgv.List(SetupResource.Webhooks)));
        Assert.NotNull(InstallationGate.RefusalFor(managed, "defenseclaw-gateway", SetupResourceArgv.List(SetupResource.Webhooks)));

        // The gateway has no setup verb, whatever the words say.
        Assert.NotNull(InstallationGate.RefusalFor(managed, "defenseclaw-gateway", Words("setup local-observability status")));

        // A list is a read to the classifier whatever is added to it (CUST-326: it is the leaf, not the whole shape), so the guard lets it run;
        // the editors' own door (SetupResourceArgv.IsRead) is the stricter test, and does not.
        Assert.Null(InstallationGate.RefusalFor(managed, "defenseclaw", Words("setup webhook list --json --connector claudecode")));
        Assert.Null(InstallationGate.RefusalFor(managed, "defenseclaw", Words("setup webhook list")));
        Assert.False(SetupResourceArgv.IsRead(Words("setup webhook list --json --connector claudecode")));
        Assert.False(SetupResourceArgv.IsRead(Words("setup webhook list")));
    }

    [Fact]
    public void The_targets_that_have_an_editor_are_the_three_nouns_and_no_other_target_has_one()
    {
        Assert.Equal(SetupResource.Observability, SetupResourceArgv.ForTarget("observability"));
        Assert.Equal(SetupResource.Webhooks, SetupResourceArgv.ForTarget("webhook"));
        Assert.Equal(SetupResource.TrustedPaths, SetupResourceArgv.ForTarget("trusted-paths"));
        Assert.All(new[] { "local-observability", "splunk", "galileo", "webhooks", "notifications", "", "Observability" }, target => Assert.Null(SetupResourceArgv.ForTarget(target)));
        Assert.Null(SetupResourceArgv.ForTarget(null));
    }
}
