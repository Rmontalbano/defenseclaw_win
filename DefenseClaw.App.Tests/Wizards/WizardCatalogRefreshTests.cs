using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.Core.Paths;

namespace DefenseClaw.App.Tests.Wizards;

/// <summary>
/// The wizard catalog is a cache over the installed CLI's <c>--help</c>, held for the life of the process. After an
/// in-app upgrade (or the gateway reporting a different binary version) it must be re-read, or the Setup hub keeps
/// showing the old build's targets and flags. The CLI here is a fake that answers with one build's help, then another's.
/// </summary>
public sealed class WizardCatalogRefreshTests
{
    private static readonly string V1Top = LineEndings.Normalize("""
        Usage: defenseclaw setup [OPTIONS] COMMAND [ARGS]...

          Configure DefenseClaw.

        Options:
          --help  Show this message and exit.

        Commands:
          claude-code  Configure DefenseClaw hooks for Claude Code.
          llm          Configure the unified LLM provider block.
        """);

    // 0.8.11 grows a connector and a flag.
    private static readonly string V2Top = LineEndings.Normalize("""
        Usage: defenseclaw setup [OPTIONS] COMMAND [ARGS]...

          Configure DefenseClaw.

        Options:
          --help  Show this message and exit.

        Commands:
          claude-code  Configure DefenseClaw hooks for Claude Code.
          hermes       Configure DefenseClaw for Hermes.
          llm          Configure the unified LLM provider block.
        """);

    private static readonly string V2Llm = WizardSamples.LlmHelp.Replace(
        "  --region TEXT         Cloud region.\n",
        "  --region TEXT         Cloud region.\n  --reasoning TEXT      Reasoning effort.\n",
        StringComparison.Ordinal);

    private static readonly string HermesHelp = LineEndings.Normalize("""
        Usage: defenseclaw setup hermes [OPTIONS]

          Configure DefenseClaw for Hermes.

        Options:
          --mode [observe|action]  Hook policy mode.  [default: observe]
          --help                   Show this message and exit.
        """);

    /// <summary>A CLI whose help can be swapped out from under the catalog, counting the top-level reads.</summary>
    private sealed class FakeCli
    {
        private volatile string _build = "v1";
        private int _topLevelReads;

        public int TopLevelReads => Volatile.Read(ref _topLevelReads);

        public void Upgrade() => _build = "v2";

        public Task<HelpProbeResult> Help(string executable, IReadOnlyList<string> path, CancellationToken cancellationToken)
        {
            var v2 = _build == "v2";
            var key = string.Join(' ', path);

            if (key.Length == 0)
            {
                _ = Interlocked.Increment(ref _topLevelReads);
                return Task.FromResult(new HelpProbeResult(v2 ? V2Top : V1Top, null));
            }

            var text = key switch
            {
                "claude-code" => WizardSamples.ClaudeCodeHelp,
                "llm" => v2 ? V2Llm : WizardSamples.LlmHelp,
                "hermes" when v2 => HermesHelp,
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

    private static bool HasFlag(WizardCatalog catalog, string target, string flag) =>
        catalog.Find(target)!.AllFields.Any(f => f.Flag == flag);

    [Fact]
    public async Task A_refresh_after_a_CLI_change_picks_up_the_new_roster_and_the_new_flags()
    {
        var cli = new FakeCli();
        var catalog = CatalogOver(cli);

        var first = await catalog.LoadAsync();
        _ = await catalog.EnsureDetailAsync("llm");

        Assert.Equal(new[] { "claude-code", "llm" }, first.Select(d => d.Target).OrderBy(t => t, StringComparer.Ordinal));
        Assert.False(HasFlag(catalog, "llm", "--reasoning"));
        Assert.Null(catalog.Find("hermes"));

        cli.Upgrade();

        // Without a refresh the process-wide cache still describes the old build.
        Assert.Null(catalog.Find("hermes"));

        await catalog.RefreshAfterCliChangeAsync();

        Assert.NotNull(catalog.Find("hermes"));
        Assert.True(HasFlag(catalog, "llm", "--reasoning"));

        // Warmed as well: every target's detail is loaded, not left as a phase-one stub.
        Assert.All(new[] { "claude-code", "hermes", "llm" }, target => Assert.True(catalog.Find(target)!.IsDetailLoaded, target));
        Assert.True(HasFlag(catalog, "hermes", "--mode"));
    }

    [Fact]
    public async Task A_refresh_on_a_catalog_nobody_opened_reads_nothing()
    {
        var cli = new FakeCli();
        var catalog = CatalogOver(cli);

        await catalog.RefreshAfterCliChangeAsync();

        Assert.Equal(0, cli.TopLevelReads);

        // The first real load then reads the CLI as it is now.
        cli.Upgrade();
        var roster = await catalog.LoadAsync();
        Assert.Contains(roster, d => d.Target == "hermes");
    }

    [Fact]
    public async Task A_different_binary_version_from_the_gateway_refreshes_the_catalog()
    {
        var cli = new FakeCli();
        var catalog = CatalogOver(cli);
        _ = await catalog.LoadAsync();
        Assert.Equal(1, cli.TopLevelReads);

        // The version the gateway reports is seeded, repeated and blank: none of that is a change.
        await catalog.ObserveBinaryVersionAsync("0.8.10");
        await catalog.ObserveBinaryVersionAsync("0.8.10");
        await catalog.ObserveBinaryVersionAsync(null);
        await catalog.ObserveBinaryVersionAsync("  ");
        Assert.Equal(1, cli.TopLevelReads);
        Assert.Null(catalog.Find("hermes"));

        cli.Upgrade();
        await catalog.ObserveBinaryVersionAsync("0.8.11");

        Assert.Equal(2, cli.TopLevelReads);
        Assert.NotNull(catalog.Find("hermes"));
        Assert.True(HasFlag(catalog, "llm", "--reasoning"));

        // ...and the new version is now the baseline.
        await catalog.ObserveBinaryVersionAsync("0.8.11");
        Assert.Equal(2, cli.TopLevelReads);
    }

    [Fact]
    public async Task A_failed_re_read_after_a_change_does_not_fault()
    {
        var cli = new FakeCli();
        var catalog = new WizardCatalog(new SetupHelpProbe(
            new DefenseClawPaths(binDirectory: @"C:\fake\bin", searchPath: Array.Empty<string>(), fileExists: _ => true),
            diskCache: null,
            (exe, path, ct) => path.Count == 0 && cli.TopLevelReads > 0
                ? Task.FromResult(new HelpProbeResult(string.Empty, "defenseclaw exited 1."))
                : cli.Help(exe, path, ct)));
        _ = await catalog.LoadAsync();

        // The fake now fails every top-level read: the refresh reports it as the catalog's load error instead of throwing.
        await catalog.RefreshAfterCliChangeAsync();

        Assert.NotNull(catalog.LoadError);
    }
}
