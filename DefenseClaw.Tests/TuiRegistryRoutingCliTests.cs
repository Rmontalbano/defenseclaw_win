using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.Tests;

/// <summary>
/// The command catalogue of a 1.0.10 runtime: the generated <see cref="TuiRegistryCatalogues.WithRoutingCli"/> (the 1.0.10 registry, 281 entries), what
/// the runtime hides from Windows in it (19), what this app leaves out for its own reasons (<see cref="RoutingCliExclusions"/>, 23) and which runtime gets
/// it. Every older runtime keeps the catalogue it had. Synthetic probe screens throughout.
/// </summary>
public sealed class TuiRegistryRoutingCliTests
{
    private const string Help = "Usage: defenseclaw [OPTIONS] COMMAND [ARGS]...\n\nCommands:\n  setup  Configure.\n";
    private const string RegistrySetup = "Usage: defenseclaw setup [OPTIONS] COMMAND [ARGS]...\n\nCommands:\n  amp    Amp.\n  devin  Devin.\n  kiro   Kiro.\n";

    private static RuntimeCapabilities Probe(string version) =>
        RuntimeProbe.Evaluate(
            new RuntimeProbeScreens("{\"name\":\"defenseclaw-cli\",\"version\":\"" + version + "\",\"schema_version\":1}", Help, RegistrySetup, null, null, null, null, null),
            "test", "fp", DateTimeOffset.UnixEpoch).Capabilities;

    private static TuiRegistryCatalogue Routing => TuiRegistryCatalogues.WithRoutingCli;

    private static TuiRegistryEntry Entry(TuiBinary binary, params string[] argv) =>
        new(string.Join(' ', argv), binary, argv, "synthetic", "info", false, string.Empty, null);

    private static IEnumerable<TuiRegistryEntry> Routings(IEnumerable<TuiRegistryEntry> entries) => entries.Where(e => e.Argv[0] == "routing");

    private static IEnumerable<TuiRegistryEntry> Substrates(IEnumerable<TuiRegistryEntry> entries) => entries.Where(e => e.Argv[0] == "substrate");

    // ------------------------------------------------------------------ what the registry holds

    [Fact]
    public void The_1_0_10_catalogue_holds_the_whole_registry_and_records_where_it_came_from()
    {
        Assert.Equal(281, Routing.Entries.Count);
        Assert.Equal(281, Routing.SourceEntries);
        Assert.Equal(281, TuiRegistryData.RoutingSourceEntries);
        Assert.Matches("^[0-9a-f]{64}$", Routing.SourceSha256);
        Assert.StartsWith("8992928a", Routing.SourceSha256, StringComparison.Ordinal);
        Assert.NotEqual(TuiRegistryCatalogues.Current.SourceSha256, Routing.SourceSha256);
        Assert.Equal("cli/defenseclaw/tui/registry_data.py", Routing.Source);
        Assert.Contains("1.0.10", Routing.Runtime, StringComparison.Ordinal);
        Assert.Equal("a64bfdb93", TuiRegistryData.RoutingCommit);
        Assert.Equal(281, Routing.Entries.Select(e => e.Name).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void It_is_the_1_0_8_registry_plus_8_substrate_and_23_routing_commands_and_nothing_else()
    {
        var current = TuiRegistryCatalogues.Current;
        var added = Routing.Entries.Where(e => current.Find(e.Name) is null).ToArray();

        Assert.Equal(31, added.Length);
        Assert.Equal(8, Substrates(added).Count());
        Assert.Equal(23, Routings(added).Count());

        // Every entry of 1.0.8 is still there, with the same argv and text; only the Windows verdict of setup openclaw differs.
        foreach (var old in current.Entries)
        {
            var now = Routing.Find(old.Name);
            Assert.NotNull(now);
            Assert.Equal(old.Argv, now!.Argv);
            Assert.Equal(old.Description, now.Description);
            Assert.Equal(old.ArgumentHint, now.ArgumentHint);
            if (old.Name != "setup openclaw")
            {
                Assert.Equal(old.WindowsUnavailable, now.WindowsUnavailable);
            }
        }
    }

    [Fact]
    public void Setup_openclaw_is_offered_by_the_runtime_now_and_was_hidden_before()
    {
        Assert.NotNull(TuiRegistryCatalogues.Current.Find("setup openclaw")!.WindowsUnavailable);
        var openclaw = Routing.Find("setup openclaw")!;
        Assert.Null(openclaw.WindowsUnavailable);
        Assert.Contains(openclaw, Routing.OnWindows);
        Assert.Equal(new[] { "setup", "openclaw", "--yes" }, openclaw.Argv);
    }

    // ------------------------------------------------------------------ exact counts

    [Fact]
    public void The_counts_are_281_entries_19_hidden_by_the_runtime_23_left_out_by_the_app_and_239_offered()
    {
        Assert.Equal(19, Routing.HiddenOnWindows.Count);
        Assert.Equal(23, Routing.LeftOut.Count);
        Assert.Equal(239, Routing.OnWindows.Count);
        Assert.Equal(Routing.Entries.Count, Routing.OnWindows.Count + Routing.HiddenOnWindows.Count + Routing.LeftOut.Count);

        // The runtime's 19 are the 18 sandbox commands and setup zeptoclaw: none of substrate or routing, and each with the runtime's own sentence.
        Assert.Equal(18, Routing.HiddenOnWindows.Count(e => e.Argv[0] == "sandbox"));
        Assert.Contains(Routing.HiddenOnWindows, e => e.Name == "setup zeptoclaw");
        Assert.Empty(Substrates(Routing.HiddenOnWindows));
        Assert.Empty(Routings(Routing.HiddenOnWindows));
        Assert.All(Routing.HiddenOnWindows, e => Assert.False(string.IsNullOrWhiteSpace(e.WindowsUnavailable)));
    }

    [Fact]
    public void The_left_out_entries_are_the_8_substrate_and_the_15_routing_commands_that_are_not_plain_reads_each_with_the_apps_sentence_and_no_runtime_reason()
    {
        Assert.Equal(8, Substrates(Routing.LeftOut).Count());
        Assert.Equal(15, Routings(Routing.LeftOut).Count());
        Assert.Equal(23, Substrates(Routing.LeftOut).Count() + Routings(Routing.LeftOut).Count());

        // They are runtime-offered entries: the runtime gave no Windows reason, so the app's sentence is never put in its place.
        Assert.All(Routing.LeftOut, e => Assert.Null(e.WindowsUnavailable));
        Assert.All(Substrates(Routing.LeftOut), e => Assert.Equal(RoutingCliExclusions.SubstrateReason, Routing.LeftOutReasonOf(e)));
        Assert.All(Routings(Routing.LeftOut), e => Assert.Equal(RoutingCliExclusions.RoutingChangeReason, Routing.LeftOutReasonOf(e)));
        Assert.Null(Routing.LeftOutReasonOf(Routing.OnWindows[0]));

        Assert.Equal(
            new[] { (RoutingCliExclusions.RoutingChangeReason, 15), (RoutingCliExclusions.SubstrateReason, 8) },
            Routing.LeftOutReasons.ToArray());
    }

    // ------------------------------------------------------------------ what is offered

    [Fact]
    public void Only_the_eight_reads_of_routing_are_offered_and_no_substrate_command()
    {
        var offered = Routings(Routing.OnWindows).Select(e => string.Join(' ', e.Argv)).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(
            new[] { "routing budgets list", "routing explain", "routing models list", "routing pricing show", "routing rules list", "routing status", "routing test", "routing usage" },
            offered);
        Assert.Equal(offered, RoutingCliExclusions.OfferedRoutingCommands.Order(StringComparer.Ordinal).ToArray());
        Assert.Empty(Substrates(Routing.OnWindows));
        Assert.All(Substrates(Routing.Entries), e => Assert.DoesNotContain(e, Routing.OnWindows));
    }

    [Theory]
    [InlineData("routing setup")]
    [InlineData("routing enable")]
    [InlineData("routing disable")]
    [InlineData("routing enable connector")]
    [InlineData("routing disable connector")]
    [InlineData("routing models add")]
    [InlineData("routing models remove")]
    [InlineData("routing models set-default")]
    [InlineData("routing rules add")]
    [InlineData("routing rules remove")]
    [InlineData("routing rules move")]
    [InlineData("routing budgets add")]
    [InlineData("routing budgets remove")]
    [InlineData("routing pricing set")]
    [InlineData("routing pricing import")]
    public void A_routing_command_that_changes_routing_is_left_out(string name)
    {
        var entry = Routing.Find(name);

        Assert.NotNull(entry);
        Assert.Contains(entry, Routing.LeftOut);
        Assert.DoesNotContain(entry, Routing.OnWindows);
        Assert.DoesNotContain(entry, Routing.HiddenOnWindows);
        Assert.Equal(RoutingCliExclusions.RoutingChangeReason, Routing.LeftOutReasonOf(entry!));
    }

    [Fact]
    public void The_connector_forms_carry_an_option_that_is_not_reviewed_and_it_stays_that_way()
    {
        foreach (var name in new[] { "routing enable connector", "routing disable connector" })
        {
            var entry = Routing.Find(name)!;
            Assert.Contains("--connector", entry.Argv);
            Assert.NotNull(TuiRegistryCatalogues.ArgvProblem(entry.Argv));
            Assert.DoesNotContain(entry, Routing.OnWindows);
        }

        Assert.DoesNotContain("--connector", TuiRegistryCatalogues.ReviewedFlags);
    }

    [Fact]
    public void Every_offered_argv_passes_the_hygiene_check_and_carries_only_reviewed_options()
    {
        Assert.All(Routing.OnWindows, e =>
        {
            Assert.Null(TuiRegistryCatalogues.ArgvProblem(e.Argv));
            Assert.DoesNotContain("--", e.Argv);
            Assert.All(e.Argv.Where(a => a.StartsWith('-')), flag => Assert.Contains(flag, TuiRegistryCatalogues.ReviewedFlags));
            Assert.Equal(e.NeedsArgument, e.ArgumentHint.Length > 0);
        });

        // Nothing the argv check refuses is left out by the argv rule alone: the 23 are all substrate or routing today.
        Assert.All(Routing.LeftOut, e => Assert.Contains(e.Argv[0], new[] { "substrate", "routing" }));
    }

    // ------------------------------------------------------------------ the rule itself

    [Fact]
    public void The_exclusion_rule_leaves_out_a_routing_verb_nobody_has_read_and_an_argv_the_hygiene_check_refuses()
    {
        Assert.Null(RoutingCliExclusions.Reason(Entry(TuiBinary.Cli, "routing", "status")));
        Assert.Null(RoutingCliExclusions.Reason(Entry(TuiBinary.Cli, "doctor")));

        // A verb a later registry adds under routing, a longer spelling of a read, and the same words for the gateway binary: all out until read.
        Assert.Equal(RoutingCliExclusions.RoutingChangeReason, RoutingCliExclusions.Reason(Entry(TuiBinary.Cli, "routing", "frobnicate")));
        Assert.Equal(RoutingCliExclusions.RoutingChangeReason, RoutingCliExclusions.Reason(Entry(TuiBinary.Cli, "routing", "status", "--json")));
        Assert.Equal(RoutingCliExclusions.RoutingChangeReason, RoutingCliExclusions.Reason(Entry(TuiBinary.Cli, "routing", "models", "list", "extra")));
        Assert.Equal(RoutingCliExclusions.RoutingChangeReason, RoutingCliExclusions.Reason(Entry(TuiBinary.Gateway, "routing", "status")));
        Assert.Equal(RoutingCliExclusions.RoutingChangeReason, RoutingCliExclusions.Reason(Entry(TuiBinary.Cli, "routing")));

        Assert.Equal(RoutingCliExclusions.SubstrateReason, RoutingCliExclusions.Reason(Entry(TuiBinary.Cli, "substrate", "list")));
        Assert.Equal(RoutingCliExclusions.SubstrateReason, RoutingCliExclusions.Reason(Entry(TuiBinary.Cli, "substrate", "a-verb-added-later")));

        // Belt and braces: an argv the check refuses is never offered, whatever it is.
        Assert.Equal(RoutingCliExclusions.ArgvReason, RoutingCliExclusions.Reason(Entry(TuiBinary.Cli, "scan", "--token")));
        Assert.Equal(RoutingCliExclusions.ArgvReason, RoutingCliExclusions.Reason(Entry(TuiBinary.Cli, "scan", "a b")));
    }

    [Fact]
    public void The_sentences_are_plain_and_do_not_pass_the_apps_choice_off_as_the_runtimes()
    {
        Assert.DoesNotContain("Windows", RoutingCliExclusions.RoutingChangeReason, StringComparison.Ordinal);
        Assert.DoesNotContain("Windows", RoutingCliExclusions.SubstrateReason, StringComparison.Ordinal);
        Assert.DoesNotContain("runtime", RoutingCliExclusions.RoutingChangeReason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("runtime", RoutingCliExclusions.SubstrateReason, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ which runtime gets which

    [Fact]
    public void A_1_0_10_runtime_gets_the_new_catalogue_and_a_1_0_9_runtime_still_gets_the_1_0_8_one()
    {
        Assert.Same(TuiRegistryCatalogues.WithRoutingCli, TuiRegistryCatalogues.For(Probe("1.0.10")));
        Assert.Same(TuiRegistryCatalogues.WithRoutingCli, TuiRegistryCatalogues.For(Probe("1.0.11")));

        Assert.Same(TuiRegistryCatalogues.Current, TuiRegistryCatalogues.For(Probe("1.0.9")));
        Assert.Same(TuiRegistryCatalogues.Current, TuiRegistryCatalogues.For(Probe("1.0.8")));
        Assert.Same(TuiRegistryCatalogues.CurrentWithoutCowork, TuiRegistryCatalogues.For(Probe("1.0.7")));
        Assert.Same(TuiRegistryCatalogues.Extended, TuiRegistryCatalogues.For(Probe("1.0.0")));
        Assert.Same(TuiRegistryCatalogues.ExtendedWithoutRemovedConnectors, TuiRegistryCatalogues.For(Probe("1.0.4")));
        Assert.Same(TuiRegistryCatalogues.Baseline, TuiRegistryCatalogues.For(RuntimeCapabilities.Unknown));
        Assert.Same(TuiRegistryCatalogues.Baseline, TuiRegistryCatalogues.For(null));
    }

    [Fact]
    public void No_older_catalogue_leaves_anything_out_so_their_offered_and_hidden_lists_are_what_they_were()
    {
        var older = new[]
        {
            TuiRegistryCatalogues.Baseline, TuiRegistryCatalogues.Extended, TuiRegistryCatalogues.ExtendedWithoutRemovedConnectors,
            TuiRegistryCatalogues.Current, TuiRegistryCatalogues.CurrentWithoutCowork,
        };

        foreach (var catalogue in older)
        {
            Assert.Empty(catalogue.LeftOut);
            Assert.Empty(catalogue.LeftOutReasons);
            Assert.Equal(catalogue.Entries.Count, catalogue.OnWindows.Count + catalogue.HiddenOnWindows.Count);
            Assert.Equal(catalogue.Entries.Where(e => e.IsAvailableOnWindows), catalogue.OnWindows);
        }

        Assert.Equal(210, TuiRegistryCatalogues.Baseline.OnWindows.Count);
        Assert.Equal(232, TuiRegistryCatalogues.Extended.OnWindows.Count);
        Assert.Equal(230, TuiRegistryCatalogues.Current.OnWindows.Count);
        Assert.Equal(229, TuiRegistryCatalogues.CurrentWithoutCowork.OnWindows.Count);
    }
}
