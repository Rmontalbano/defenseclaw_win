using System.Text.Json;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Paths;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// CUST-317: the tiers of <c>setup splunk dashboards plan | apply | destroy</c>, held against the command trees of both runtimes the app
/// knows (DefenseClaw 0.8.10 and the pinned source commit). <c>setup</c> is a state-changing verb, so by the first-verb rule <c>plan</c> and
/// <c>apply</c> are changes; <c>destroy</c> is in the destructive-verb list. Nothing here is a read: <c>plan</c> sounds like one, but it
/// writes Terraform's files and local state, and it is not on the allow-list of commands that run without a review. The only way the
/// command line could make one of them look harmless is a value that spells a read-only flag, which counts only when it stands alone.
/// </summary>
public sealed class CommandTierSplunkDashboardsTests
{
    private const string Group = "setup splunk dashboards";

    private sealed record Leaf(string Path, string[] Options, string[] ValueOptions);

    public static TheoryData<string> Trees { get; } = new()
    {
        "cli-tree-0.8.10.json",
        "runtime-95159fd/cli/cli-tree.json",
    };

    private static Leaf[] Leaves(string fixture)
    {
        using var document = JsonDocument.Parse(FixtureFiles.ReadText(fixture));
        return document.RootElement.GetProperty("commands").EnumerateArray()
            .Where(c => !c.GetProperty("group").GetBoolean())
            .Where(c => c.GetProperty("path").GetString()!.StartsWith(Group + " ", StringComparison.Ordinal))
            .Select(c =>
            {
                var options = c.GetProperty("options").EnumerateArray().ToArray();
                return new Leaf(
                    c.GetProperty("path").GetString()!,
                    options.SelectMany(o => o.GetProperty("names").EnumerateArray().Select(n => n.GetString()!)).ToArray(),
                    options.Where(o => !o.GetProperty("flag").GetBoolean())
                        .SelectMany(o => o.GetProperty("names").EnumerateArray().Select(n => n.GetString()!)).ToArray());
            })
            .OrderBy(l => l.Path, StringComparer.Ordinal)
            .ToArray();
    }

    private static string[] Words(string commandLine) => commandLine.Split(' ');

    [Theory]
    [MemberData(nameof(Trees))]
    public void Each_tree_has_exactly_the_three_verbs_and_the_group_is_not_a_command_of_its_own(string tree)
    {
        Assert.Equal(
            new[] { "apply", "destroy", "plan" }.Select(v => Group + " " + v).ToArray(),
            Leaves(tree).Select(l => l.Path).ToArray());
    }

    [Theory]
    [MemberData(nameof(Trees))]
    public void The_verbs_share_one_option_set_plan_has_no_yes_and_none_has_a_preview_flag(string tree)
    {
        var leaves = Leaves(tree).ToDictionary(l => l.Path.Split(' ')[^1], StringComparer.Ordinal);

        var shared = new[]
        {
            "--api-url", "--o11y-api-token", "--name-prefix", "--with-detectors", "--dashboards-only", "--enable-detectors",
            "--detector-notification", "--work-dir", "--state", "--terraform-bin", "--plugin-dir", "--skip-init", "--skip-validate", "--timeout",
        };

        Assert.Equal(shared.Order(StringComparer.Ordinal), leaves["plan"].Options.Order(StringComparer.Ordinal));
        Assert.Equal(shared.Append("--yes").Order(StringComparer.Ordinal), leaves["apply"].Options.Order(StringComparer.Ordinal));
        Assert.Equal(shared.Append("--yes").Order(StringComparer.Ordinal), leaves["destroy"].Options.Order(StringComparer.Ordinal));

        // plan is the preview; there is no --dry-run to add, so the wizard never offers one.
        Assert.All(leaves.Values, l => Assert.DoesNotContain("--dry-run", l.Options));

        // The token is a value-taking option on every verb: the one flag that must never reach a command line.
        Assert.All(leaves.Values, l => Assert.Contains("--o11y-api-token", l.ValueOptions));
    }

    [Fact]
    public void The_two_trees_agree_on_every_option_of_every_verb()
    {
        Assert.Equal(
            Leaves("cli-tree-0.8.10.json").Select(l => (l.Path, string.Join(',', l.Options))),
            Leaves("runtime-95159fd/cli/cli-tree.json").Select(l => (l.Path, string.Join(',', l.Options))));
    }

    [Theory]
    [InlineData("plan", CommandTier.StateChanging)]
    [InlineData("apply", CommandTier.StateChanging)]
    [InlineData("destroy", CommandTier.Destructive)]
    [InlineData("apply --yes", CommandTier.StateChanging)]
    [InlineData("destroy --yes", CommandTier.Destructive)]
    [InlineData("plan --name-prefix smoke --with-detectors --enable-detectors", CommandTier.StateChanging)]
    [InlineData("apply --yes --api-url https://api.us1.signalfx.com --with-detectors --detector-notification Email,x@example.test", CommandTier.StateChanging)]
    [InlineData("destroy --yes --state C:\\work\\terraform.tfstate --work-dir C:\\work --skip-init --skip-validate --timeout 60", CommandTier.Destructive)]
    public void Each_verb_has_the_tier_its_source_earns_whatever_options_the_wizard_adds(string verbAndOptions, CommandTier expected)
    {
        Assert.Equal(expected, CommandTiers.Classify(Words(Group + " " + verbAndOptions)));
    }

    [Theory]
    [InlineData("plan")]
    [InlineData("apply --yes")]
    [InlineData("destroy --yes")]
    public void None_of_them_is_a_read_so_none_runs_without_a_review_and_none_prints_secrets(string verbAndOptions)
    {
        var argv = Words(Group + " " + verbAndOptions);

        Assert.NotEqual(CommandTier.ReadOnly, CommandTiers.Classify(argv));
        Assert.False(CommandTiers.IsUnreviewedRead(argv));
        Assert.False(CommandTiers.IsReadOnlyLeaf(argv));
        Assert.False(CommandTiers.PrintsSecrets(argv));
        Assert.DoesNotContain(CommandTiers.UnreviewedReadPaths, p => p.StartsWith(Group, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("plan", "--name-prefix", "--dry-run")]
    [InlineData("plan", "--api-url", "--help")]
    [InlineData("apply", "--name-prefix", "--version")]
    [InlineData("apply", "--detector-notification", "--version-json")]
    [InlineData("destroy", "--name-prefix", "--dry-run")]
    [InlineData("destroy", "--api-url", "--help")]
    [InlineData("destroy", "--work-dir", "--dry-run")]
    [InlineData("destroy", "--state", "--help")]
    [InlineData("destroy", "--timeout", "--dry-run")]
    public void Text_typed_into_an_option_that_spells_a_preview_flag_cannot_lower_a_tier(string verb, string option, string typed)
    {
        var argv = Words(Group + " " + verb)
            .Concat(verb == "plan" ? Array.Empty<string>() : new[] { "--yes" })
            .Append(option)
            .Append(typed)
            .ToArray();

        var expected = verb == "destroy" ? CommandTier.Destructive : CommandTier.StateChanging;
        Assert.Equal(expected, CommandTiers.Classify(argv));
    }

    [Theory]
    [MemberData(nameof(Trees))]
    public void Whatever_a_value_taking_option_is_given_the_tier_of_its_verb_holds(string tree)
    {
        var typed = new[] { "--dry-run", "--help", "--version", "--version-json" };
        var checkedCount = 0;

        foreach (var leaf in Leaves(tree))
        {
            var expected = leaf.Path.EndsWith(" destroy", StringComparison.Ordinal) ? CommandTier.Destructive : CommandTier.StateChanging;
            foreach (var option in leaf.ValueOptions.Where(o => o != "--help"))
            {
                foreach (var value in typed)
                {
                    var argv = Words(leaf.Path).Append(option).Append(value).ToArray();
                    Assert.True(CommandTiers.Classify(argv) == expected, string.Join(' ', argv));
                    checkedCount++;
                }
            }
        }

        Assert.True(checkedCount >= 3 * 8 * 4, $"only {checkedCount} shapes were checked - is the fixture truncated?");
    }

    [Fact]
    public void The_token_flag_can_never_be_on_a_command_catalogue_argv_and_the_palettes_own_are_accepted()
    {
        Assert.Null(TuiRegistryCatalogues.ArgvProblem(Words(Group + " plan")));
        Assert.Null(TuiRegistryCatalogues.ArgvProblem(Words(Group + " apply --yes")));
        Assert.Null(TuiRegistryCatalogues.ArgvProblem(Words(Group + " destroy --yes")));

        Assert.NotNull(TuiRegistryCatalogues.ArgvProblem(Words(Group + " plan --o11y-api-token anything")));
        Assert.NotNull(TuiRegistryCatalogues.ArgvProblem(Words(Group + " apply --yes --o11y-api-token anything")));
        Assert.DoesNotContain("--o11y-api-token", TuiRegistryCatalogues.ReviewedFlags);
        Assert.DoesNotContain("--terraform-bin", TuiRegistryCatalogues.ReviewedFlags);
    }

    // ------------------------------------------------------------------ a read-only installation (CUST-308)

    [Theory]
    [InlineData("plan")]
    [InlineData("apply --yes")]
    [InlineData("destroy --yes")]
    [InlineData("apply --yes --with-detectors --enable-detectors")]
    [InlineData("plan --name-prefix --dry-run")]
    [InlineData("apply --yes --api-url --help")]
    [InlineData("destroy --yes --name-prefix --help")]
    public void A_read_only_installation_refuses_every_dashboards_command_the_app_can_build_and_a_writable_one_refuses_none(string verbAndOptions)
    {
        var argv = Words(Group + " " + verbAndOptions);
        var managed = new InstallationMachine().WithSecureClientLayout().Resolve();
        var invalid = new InstallationMachine().WithEnvironment("DEFENSECLAW_HOME", "relative").Resolve();

        // plan sounds like a read and is not one (it writes Terraform's files and state), so the gate that is the runner's refuses it too, with
        // the installation's own sentence - and so does a value that spells a preview flag.
        Assert.NotEqual(CommandTier.ReadOnly, InstallationGate.TierOf("defenseclaw", argv));
        foreach (var context in new[] { managed, invalid })
        {
            Assert.Equal(context.BlockedReason, InstallationGate.RefusalFor(context, "defenseclaw", argv));
        }

        Assert.Null(InstallationGate.RefusalFor(InstallationContext.Unmanaged(@"C:\data\.defenseclaw"), "defenseclaw", argv));
    }

    [Fact]
    public void Only_a_standalone_help_flag_makes_a_dashboards_command_a_read_on_a_read_only_installation()
    {
        var managed = new InstallationMachine().WithSecureClientLayout().Resolve();

        // The group's and a verb's own help print and change nothing: the gate lets them through, as it does for every command.
        foreach (var help in new[] { Group + " --help", Group + " plan --help", Group + " destroy --help" })
        {
            Assert.Null(InstallationGate.RefusalFor(managed, "defenseclaw", Words(help)));
        }

        Assert.NotNull(InstallationGate.RefusalFor(managed, "defenseclaw", Words(Group + " destroy --yes --name-prefix --help")));
    }
}
