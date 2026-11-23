using System.Text.RegularExpressions;
using DefenseClaw.App.Services;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.Tests.Shell;

/// <summary>
/// The palette's catalogue for a 1.0.10 runtime: setup openclaw offered, the routing reads and nothing that changes routing, no substrate command, the
/// note and tooltip that say what is not offered and by whom, and every older runtime exactly as before. Synthetic probe screens throughout.
/// </summary>
public sealed class RoutingCliCommandCatalogueTests
{
    private const string Help = "Usage: defenseclaw [OPTIONS] COMMAND [ARGS]...\n\nCommands:\n  setup  Configure.\n";
    private const string RegistrySetup = "Usage: defenseclaw setup [OPTIONS] COMMAND [ARGS]...\n\nCommands:\n  amp    Amp.\n  devin  Devin.\n  kiro   Kiro.\n";
    private const string RuntimeHidesHeading = "Not offered on Windows, because the runtime does not run them here:";

    private static RuntimeCapabilities Probe(string version) =>
        RuntimeProbe.Evaluate(
            new RuntimeProbeScreens("{\"name\":\"defenseclaw-cli\",\"version\":\"" + version + "\",\"schema_version\":1}", Help, RegistrySetup, null, null, null, null, null),
            "test", "fp", DateTimeOffset.UnixEpoch).Capabilities;

    private static CuratedCommandCatalog Routing => CuratedCommandCatalog.For(Probe("1.0.10"));

    private static IEnumerable<CuratedCommand> RoutingRows(CuratedCommandCatalog catalogue) => catalogue.Commands.Where(c => c.Argv[0] == "routing");

    // ------------------------------------------------------------------ which runtime gets it

    [Fact]
    public void A_1_0_10_runtime_gets_the_new_catalogue_and_a_1_0_9_runtime_the_1_0_8_one()
    {
        var routing = Routing;
        var older = CuratedCommandCatalog.For(Probe("1.0.9"));

        Assert.Same(TuiRegistryCatalogues.WithRoutingCli, routing.Source);
        Assert.Same(TuiRegistryCatalogues.Current, older.Source);
        Assert.NotSame(routing, older);

        // The catalogues are built once.
        Assert.Same(routing, CuratedCommandCatalog.For(Probe("1.0.10")));
        Assert.Same(older, CuratedCommandCatalog.For(Probe("1.0.9")));
        Assert.Same(older, CuratedCommandCatalog.For(Probe("1.0.8")));
    }

    // ------------------------------------------------------------------ what is offered

    [Fact]
    public void The_1_0_10_palette_offers_239_commands_with_setup_openclaw_among_them()
    {
        var catalogue = Routing;

        Assert.Equal(239, catalogue.Commands.Count);
        Assert.Equal(19, catalogue.HiddenCount);
        Assert.Equal(23, catalogue.LeftOutCount);

        var openclaw = catalogue.Find("cli.setup.openclaw");
        Assert.NotNull(openclaw);
        Assert.Equal(new[] { "setup", "openclaw", "--yes" }, openclaw!.Argv);

        // 1.0.9 still has it hidden, with the runtime's reason of that release.
        Assert.Null(CuratedCommandCatalog.For(Probe("1.0.9")).Find("cli.setup.openclaw"));
    }

    [Fact]
    public void The_routing_rows_are_the_eight_reads_and_there_is_no_substrate_row_and_no_row_that_changes_routing()
    {
        var catalogue = Routing;

        Assert.Equal(
            new[]
            {
                "routing budgets list", "routing explain", "routing models list", "routing pricing show", "routing rules list", "routing status",
                "routing test", "routing usage",
            },
            RoutingRows(catalogue).Select(c => c.Title).Order(StringComparer.Ordinal).ToArray());

        Assert.DoesNotContain(catalogue.Commands, c => c.Argv[0] == "substrate");
        Assert.DoesNotContain(catalogue.Commands, c => c.Argv.Contains("--connector"));
        Assert.Null(catalogue.Find("cli.routing.enable"));
        Assert.Null(catalogue.Find("cli.routing.disable"));
        Assert.Null(catalogue.Find("cli.routing.enable.connector"));
        Assert.Null(catalogue.Find("cli.routing.setup"));
        Assert.Null(catalogue.Find("cli.substrate.list"));
        Assert.NotNull(catalogue.Find("cli.routing.status"));
    }

    [Fact]
    public void Every_offered_argv_passes_the_hygiene_check_and_the_connector_option_is_still_not_reviewed()
    {
        Assert.All(Routing.Commands, c =>
        {
            Assert.Null(TuiRegistryCatalogues.ArgvProblem(c.Argv));
            Assert.False(CuratedCommandCatalog.Refuses(c.Argv));
        });
        Assert.DoesNotContain("--connector", TuiRegistryCatalogues.ReviewedFlags);
        Assert.True(CuratedCommandCatalog.Refuses(new[] { "routing", "enable", "--connector" }));
    }

    // ------------------------------------------------------------------ typed values

    [Fact]
    public void A_row_that_needs_more_than_one_plain_word_has_no_form_and_is_copied_for_the_operator_to_finish()
    {
        // `routing test "write a parser"` and `routing explain REQUEST_ID`: neither hint is a single <word>.
        foreach (var title in new[] { "routing test", "routing explain" })
        {
            var row = Assert.Single(RoutingRows(Routing), c => c.Title == title);
            Assert.True(row.NeedsArguments);
            Assert.Null(row.Form);
            Assert.False(row.RunsWithoutReview);
            Assert.Equal("defenseclaw " + title, row.CommandLineText);
        }

        Assert.Null(ArgumentForm.Parse("\"write a parser\""));
        Assert.Null(ArgumentForm.Parse("REQUEST_ID"));

        // What the copy puts on the clipboard is the argv as it stands: nothing the operator typed, so nothing of it to leave unquoted.
        var test = Assert.Single(RoutingRows(Routing), c => c.Title == "routing test");
        Assert.DoesNotContain("parser", test.ClipboardText, StringComparison.Ordinal);
        Assert.Contains("routing test", test.ClipboardText, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_row_has_no_form_or_a_form_for_one_plain_word_and_a_typed_value_always_goes_after_the_terminator()
    {
        var oneWord = new Regex("^<[A-Za-z][A-Za-z0-9_.-]*(\\|[A-Za-z][A-Za-z0-9_.-]*)*>$");

        foreach (var command in Routing.Commands)
        {
            if (command.Form is null)
            {
                continue;
            }

            Assert.Matches(oneWord, command.ArgumentHint);
            if (!command.TypesInApp)
            {
                var argv = command.ArgvWith("some-name");
                Assert.Equal("some-name", argv[^1]);
                Assert.Equal("--", argv[^2]);
                Assert.Null(TuiRegistryCatalogues.ArgvProblem(command.Argv));
            }
        }
    }

    // ------------------------------------------------------------------ review

    [Fact]
    public void No_routing_row_runs_without_a_review_and_none_is_on_the_list_of_unreviewed_reads()
    {
        Assert.All(RoutingRows(Routing), c => Assert.False(c.RunsWithoutReview, c.Title));
        Assert.DoesNotContain(CommandTiers.UnreviewedReadPaths, p => p.StartsWith("routing", StringComparison.Ordinal));
        Assert.DoesNotContain(CommandTiers.UnreviewedReadPaths, p => p.StartsWith("substrate", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ what the palette says about what is not there

    [Fact]
    public void The_note_counts_both_and_the_tooltip_keeps_the_runtimes_lines_and_the_apps_apart()
    {
        var catalogue = Routing;
        var runtimeLines = string.Concat(catalogue.Source.HiddenReasons.Select(
            r => Environment.NewLine + $"{r.Count} {(r.Count == 1 ? "command" : "commands")}: {r.Reason}"));
        var expected =
            RuntimeHidesHeading + runtimeLines + Environment.NewLine +
            "Not offered by this app:" +
            Environment.NewLine + "15 commands: " + RoutingCliExclusions.RoutingChangeReason +
            Environment.NewLine + "8 commands: " + RoutingCliExclusions.SubstrateReason;

        Assert.Equal("42 not offered", catalogue.HiddenNote);
        Assert.Equal(expected, catalogue.HiddenDetail);
        Assert.Contains("18 commands: Sandboxes run on Linux and macOS only.", catalogue.HiddenDetail, StringComparison.Ordinal);

        // The app's sentences are under the app's heading and never among the runtime's lines.
        Assert.DoesNotContain(RoutingCliExclusions.RoutingChangeReason, runtimeLines, StringComparison.Ordinal);
        Assert.DoesNotContain(RoutingCliExclusions.SubstrateReason, runtimeLines, StringComparison.Ordinal);
    }

    [Fact]
    public void The_note_and_tooltip_of_every_older_catalogue_are_what_they_were()
    {
        foreach (var source in new[]
                 {
                     TuiRegistryCatalogues.Baseline, TuiRegistryCatalogues.Extended, TuiRegistryCatalogues.Current,
                     TuiRegistryCatalogues.CurrentWithoutCowork, TuiRegistryCatalogues.ExtendedWithoutRemovedConnectors,
                 })
        {
            var catalogue = new CuratedCommandCatalog(source);
            var hidden = source.HiddenOnWindows.Count;

            Assert.Equal(0, catalogue.LeftOutCount);
            Assert.Equal(hidden, catalogue.HiddenCount);
            Assert.Equal($"{hidden} hidden on Windows", catalogue.HiddenNote);
            Assert.Equal(
                RuntimeHidesHeading + string.Concat(source.HiddenReasons.Select(
                    r => Environment.NewLine + $"{r.Count} {(r.Count == 1 ? "command" : "commands")}: {r.Reason}")),
                catalogue.HiddenDetail);
            Assert.DoesNotContain("this app", catalogue.HiddenDetail, StringComparison.Ordinal);
        }

        Assert.Equal("21 hidden on Windows", new CuratedCommandCatalog(TuiRegistryCatalogues.Baseline).HiddenNote);
        Assert.Equal("21 hidden on Windows", new CuratedCommandCatalog(TuiRegistryCatalogues.Extended).HiddenNote);
        Assert.Equal("20 hidden on Windows", new CuratedCommandCatalog(TuiRegistryCatalogues.Current).HiddenNote);
    }

    [Fact]
    public void A_1_0_9_runtime_is_offered_exactly_the_1_0_8_commands_with_no_routing_row()
    {
        var older = CuratedCommandCatalog.For(Probe("1.0.9"));

        Assert.Equal(230, older.Commands.Count);
        Assert.Empty(RoutingRows(older));
        Assert.DoesNotContain(older.Commands, c => c.Argv[0] == "substrate");
        Assert.Equal("20 hidden on Windows", older.HiddenNote);
    }

    // ------------------------------------------------------------------ what the review says about them

    [Theory]
    [InlineData("routing", "status")]
    [InlineData("routing", "models", "list")]
    [InlineData("routing", "usage")]
    [InlineData("routing", "explain", "req-123")]
    public void The_review_says_a_routing_read_only_reads(params string[] argv)
    {
        Assert.Equal(RoutingReadReview.ReadNote, RoutingReadReview.Note(argv));
        Assert.Contains("changes nothing", RoutingReadReview.ReadNote, StringComparison.Ordinal);
    }

    [Fact]
    public void The_review_calls_routing_test_a_dry_run_that_may_ask_the_classifier()
    {
        var note = RoutingReadReview.Note(new[] { "routing", "test", "write a parser" });

        Assert.Equal(RoutingReadReview.TestNote, note);
        Assert.Contains("dry run", note, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("classifier", note, StringComparison.Ordinal);
        Assert.Contains("hosted model", note, StringComparison.Ordinal);
        Assert.DoesNotContain("No model is called", note, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("routing", "models", "add", "fast")]
    [InlineData("routing", "enable")]
    [InlineData("status")]
    [InlineData("routing")]
    public void Any_other_command_gets_no_routing_sentence(params string[] argv)
    {
        Assert.Equal(string.Empty, RoutingReadReview.Note(argv));
    }}
