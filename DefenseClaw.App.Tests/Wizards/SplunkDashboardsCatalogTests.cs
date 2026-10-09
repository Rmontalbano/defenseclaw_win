using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.Core.Paths;

namespace DefenseClaw.App.Tests.Wizards;

/// <summary>
/// How the catalog learns about <c>setup splunk dashboards</c> (CUST-317): it is a nested group, so <c>setup --help</c> does not list it and
/// the roster stays what the CLI says; its detail is read from the help of the two nouns and of each of its verbs, as the screens a person
/// would read; a CLI that predates it gets a disabled definition rather than an empty wizard; and a rebuilt catalog asks again. The CLI here
/// is a fake that answers with canned help screens: nothing is started and no setup command is run.
/// </summary>
public sealed class SplunkDashboardsCatalogTests
{
    private static readonly string Top = LineEndings.Normalize("""
        Usage: defenseclaw setup [OPTIONS] COMMAND [ARGS]...

          Configure DefenseClaw.

        Options:
          --help  Show this message and exit.

        Commands:
          claude-code  Configure DefenseClaw hooks for Claude Code.
          splunk       Configure Splunk integration for DefenseClaw.
        """);

    private static readonly string TopWithoutSplunk = LineEndings.Normalize("""
        Usage: defenseclaw setup [OPTIONS] COMMAND [ARGS]...

          Configure DefenseClaw.

        Options:
          --help  Show this message and exit.

        Commands:
          claude-code  Configure DefenseClaw hooks for Claude Code.
        """);

    /// <summary>What a CLI without the group prints for <c>setup splunk dashboards --help</c>: the usage of the group above it, and an error.</summary>
    private static readonly string NoSuchCommand = LineEndings.Normalize("""
        Usage: defenseclaw setup splunk [OPTIONS] [COMMAND] [ARGS]...
        Try 'defenseclaw setup splunk --help' for help.

        Error: No such command 'dashboards'.
        """);

    /// <summary>A CLI whose help can be swapped out from under the catalog, recording every screen it was asked for.</summary>
    private sealed class FakeCli
    {
        private readonly object _gate = new();
        private readonly List<string> _asked = new();

        public bool WithSplunk { get; set; } = true;

        public bool HasDashboards { get; set; } = true;

        public bool DashboardsFail { get; set; }

        public IReadOnlyList<string> Asked
        {
            get
            {
                lock (_gate)
                {
                    return _asked.ToArray();
                }
            }
        }

        public Task<HelpProbeResult> Help(string executable, IReadOnlyList<string> path, CancellationToken cancellationToken)
        {
            var key = string.Join(' ', path);
            lock (_gate)
            {
                _asked.Add(key);
            }

            const string Nested = "splunk dashboards";
            string? text = key switch
            {
                "" => WithSplunk ? Top : TopWithoutSplunk,
                "claude-code" => WizardSamples.ClaudeCodeHelp,
                "splunk" => WizardSamples.SplunkHelp,
                Nested when DashboardsFail => null,
                Nested => HasDashboards ? WizardSamples.DashboardsGroupHelp : NoSuchCommand,
                _ when key.StartsWith(Nested + " ", StringComparison.Ordinal) && HasDashboards =>
                    WizardSamples.DashboardsVerbHelp.GetValueOrDefault(key[(Nested.Length + 1)..]),
                _ => null,
            };

            return Task.FromResult(text is null
                ? new HelpProbeResult(string.Empty, "defenseclaw exited 2.")
                : new HelpProbeResult(text, null));
        }
    }

    private static WizardCatalog CatalogOver(FakeCli cli)
    {
        var paths = new DefenseClawPaths(
            binDirectory: @"C:\fake\bin",
            searchPath: Array.Empty<string>(),
            fileExists: _ => true);

        return new WizardCatalog(new SetupHelpProbe(paths, diskCache: null, cli.Help));
    }

    [Fact]
    public async Task The_roster_is_what_the_cli_lists_and_the_nested_group_is_not_in_it()
    {
        var cli = new FakeCli();
        var catalog = CatalogOver(cli);

        var roster = await catalog.LoadAsync();

        Assert.Equal(new[] { "claude-code", "splunk" }, roster.Select(d => d.Target).Order(StringComparer.Ordinal));
        Assert.Null(catalog.Find(SplunkDashboards.Target));
        Assert.Equal(new[] { string.Empty }, cli.Asked);
    }

    [Fact]
    public async Task The_detail_is_read_from_the_help_of_the_two_nouns_and_of_each_verb_and_is_the_definition_the_samples_build()
    {
        var cli = new FakeCli();
        var catalog = CatalogOver(cli);
        _ = await catalog.LoadAsync();

        var definition = await catalog.EnsureDetailAsync(SplunkDashboards.Target);

        // Exactly the screens a person would ask for: the group's, and one per verb it lists. Never a screen for a made-up noun.
        Assert.Equal(
            new[] { "splunk dashboards", "splunk dashboards apply", "splunk dashboards destroy", "splunk dashboards plan" },
            cli.Asked.Where(a => a.StartsWith("splunk dashboards", StringComparison.Ordinal)).Distinct().Order(StringComparer.Ordinal));

        Assert.Equal("splunk dashboards", definition.Target);
        Assert.Equal(SplunkDashboards.Title, definition.Title);
        Assert.Equal(WizardGroups.Observability, definition.Group);
        Assert.Equal(SplunkDashboards.Description, definition.Description);
        Assert.Equal(PlatformStatus.NotApplicable, definition.PlatformStatus);
        Assert.True(definition.IsDetailLoaded);
        Assert.Null(definition.DetailError);
        Assert.Equal(string.Empty, definition.UnavailableReason);
        Assert.Null(definition.CrossValidator);
        Assert.Same(definition, catalog.Find(SplunkDashboards.Target));

        // What the catalog builds is what the tests of the pages and the review are run against.
        var sample = WizardSamples.Dashboards();
        Assert.Equal(sample.Steps.Select(s => s.Id), definition.Steps.Select(s => s.Id));
        Assert.Equal(sample.AllFields.Select(f => f.Id), definition.AllFields.Select(f => f.Id));
        Assert.Equal(
            new[] { "setup", "splunk", "dashboards", "plan" },
            definition.BuildArgv(WizardSamples.StartingValues(definition)));
        Assert.All(
            definition.AllFields.Where(f => f.Flag == "--o11y-api-token"),
            f => Assert.Equal("SFX_AUTH_TOKEN", f.Credential!.InAppVariable(new WizardValues())));
    }

    [Fact]
    public async Task The_splunk_wizard_is_built_as_before_and_still_does_not_offer_the_subcommand()
    {
        var cli = new FakeCli();
        var catalog = CatalogOver(cli);
        _ = await catalog.LoadAsync();

        var splunk = await catalog.EnsureDetailAsync("splunk");

        Assert.True(splunk.IsDetailLoaded, splunk.DetailError);
        Assert.DoesNotContain(splunk.AllFields, f => f.Id == "subcommand");
        Assert.DoesNotContain(splunk.Steps, s => s.VisibleWhenValues.Contains("dashboards"));
        Assert.Contains(splunk.AllFields, f => f.Flag == "--o11y");
        Assert.Null(catalog.Find(SplunkDashboards.Target));
    }

    [Fact]
    public async Task A_cli_that_predates_the_group_gets_a_disabled_definition_and_its_verbs_are_never_asked_for()
    {
        var cli = new FakeCli { HasDashboards = false };
        var catalog = CatalogOver(cli);
        _ = await catalog.LoadAsync();

        var definition = await catalog.EnsureDetailAsync(SplunkDashboards.Target);

        Assert.NotEmpty(definition.UnavailableReason);
        Assert.Contains("no \"setup splunk dashboards\" command", definition.UnavailableReason, StringComparison.Ordinal);
        Assert.Empty(definition.Steps);
        Assert.True(definition.IsDetailLoaded);
        Assert.Null(definition.DetailError);
        Assert.DoesNotContain(cli.Asked, a => a.StartsWith("splunk dashboards ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_help_that_could_not_be_read_is_an_error_to_retry_and_not_a_missing_command()
    {
        var cli = new FakeCli { DashboardsFail = true };
        var catalog = CatalogOver(cli);
        _ = await catalog.LoadAsync();

        var failed = await catalog.EnsureDetailAsync(SplunkDashboards.Target);

        // Neither runnable nor "not part of this DefenseClaw": the screen could not be read, which is a moment and not a fact about the CLI.
        Assert.NotNull(failed.DetailError);
        Assert.Equal(string.Empty, failed.UnavailableReason);
        Assert.Equal(WizardGroups.Observability, failed.Group);
        Assert.Equal(SplunkDashboards.Title, failed.Title);
        Assert.Equal(SplunkDashboards.Description, failed.Description);
        Assert.Equal(PlatformStatus.Unknown, failed.PlatformStatus);

        // Opening the wizard is the natural moment to retry.
        cli.DashboardsFail = false;
        var retried = await catalog.EnsureDetailAsync(SplunkDashboards.Target);
        Assert.Null(retried.DetailError);
        Assert.NotEmpty(retried.Steps);
    }

    [Fact]
    public async Task A_rebuilt_catalog_asks_again_about_the_dashboards_in_both_directions()
    {
        var cli = new FakeCli();
        var catalog = CatalogOver(cli);
        _ = await catalog.LoadAsync();
        Assert.Empty((await catalog.EnsureDetailAsync(SplunkDashboards.Target)).UnavailableReason);

        // An upgrade removes the command (a rollback), the catalog is told, and the derived card is read again with the rest.
        cli.HasDashboards = false;
        await catalog.RefreshAfterCliChangeAsync();
        Assert.NotEmpty(catalog.Find(SplunkDashboards.Target)!.UnavailableReason);

        // ... and the next one brings it back.
        cli.HasDashboards = true;
        await catalog.RefreshAfterCliChangeAsync();
        var back = catalog.Find(SplunkDashboards.Target)!;
        Assert.Equal(string.Empty, back.UnavailableReason);
        Assert.NotEmpty(back.Steps);
    }

    [Fact]
    public async Task A_catalog_nobody_opened_reads_nothing_about_the_dashboards_when_the_cli_changes()
    {
        var cli = new FakeCli();
        var catalog = CatalogOver(cli);

        await catalog.RefreshAfterCliChangeAsync();

        Assert.Empty(cli.Asked);
        Assert.Null(catalog.Find(SplunkDashboards.Target));
    }

    [Fact]
    public async Task A_catalog_that_was_in_use_warms_the_dashboards_with_the_rest_when_the_roster_has_splunk()
    {
        var cli = new FakeCli();
        var catalog = CatalogOver(cli);
        _ = await catalog.LoadAsync();
        Assert.Null(catalog.Find(SplunkDashboards.Target));

        await catalog.RefreshAfterCliChangeAsync();

        // The splunk wizard's own detail probes the group too, so the screens asked for prove nothing: what is new is the derived definition.
        var derived = catalog.Find(SplunkDashboards.Target);
        Assert.NotNull(derived);
        Assert.True(derived!.IsDetailLoaded);
        Assert.NotEmpty(derived.Steps);
    }

    [Fact]
    public async Task A_roster_without_splunk_has_nothing_to_derive_so_the_dashboards_are_never_read()
    {
        var cli = new FakeCli { WithSplunk = false };
        var catalog = CatalogOver(cli);
        _ = await catalog.LoadAsync();

        await catalog.RefreshAfterCliChangeAsync();

        Assert.Null(catalog.Find(SplunkDashboards.Target));
        Assert.DoesNotContain(cli.Asked, a => a.StartsWith("splunk", StringComparison.Ordinal));
    }
}
