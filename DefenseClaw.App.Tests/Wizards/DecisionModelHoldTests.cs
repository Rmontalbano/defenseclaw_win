using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Paths;

namespace DefenseClaw.App.Tests.Wizards;

/// <summary>
/// No decision-model UI until DefenseClaw 1.0.7 ships (CUST-423): the feature could still change. 1.0.6 adds <c>setup decision-model</c>
/// (<c>cmd_setup.py</c> 1415), which the catalog would turn into an "Other" card by itself. That one card is held back through
/// <see cref="WizardCatalog.HeldUntilRelease"/>; every other target, and the read-only config section and doctor row, are untouched.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class DecisionModelHoldTests
{
    /// <summary>A 1.0.6-shaped <c>setup --help</c>: the new target sits among ordinary ones, a preview connector among the connectors.</summary>
    private static readonly string Top106 = LineEndings.Normalize("""
        Usage: defenseclaw setup [OPTIONS] COMMAND [ARGS]...

          Configure DefenseClaw.

        Options:
          --help  Show this message and exit.

        Commands:
          amp             Configure DefenseClaw for Amp.
          claude-code     Configure DefenseClaw hooks for Claude Code.
          decision-model  Configure the unified decision model (decision_model:).
          llm             Configure the unified LLM provider block.
          opencode        OpenCode: preview on windows.
        """);

    private const string DecisionModelHelp = """
        Usage: defenseclaw setup decision-model [OPTIONS]

          Configure the unified decision model (decision_model:).

        Options:
          --show                  Print the current decision model settings and exit.
          --enable / --disable    Turn the decision model on or off.
          --help                  Show this message and exit.
        """;

    private static WizardCatalog CatalogOver(Action<string>? onHelp = null)
    {
        var paths = new DefenseClawPaths(binDirectory: @"C:\fake\bin", searchPath: Array.Empty<string>(), fileExists: _ => true);
        return new WizardCatalog(new SetupHelpProbe(paths, diskCache: null, (_, path, _) =>
        {
            var key = string.Join(' ', path);
            onHelp?.Invoke(key);
            var text = key switch
            {
                "" => Top106,
                "claude-code" => WizardSamples.ClaudeCodeHelp,
                "llm" => WizardSamples.LlmHelp,
                "opencode" => PreviewStatusTests.OpenCodeHelp,
                "decision-model" => LineEndings.Normalize(DecisionModelHelp),
                _ => null,
            };

            return Task.FromResult(text is null ? new HelpProbeResult(string.Empty, "defenseclaw exited 2.") : new HelpProbeResult(text, null));
        }));
    }

    [Fact]
    public async Task The_decision_model_card_is_not_in_the_catalog_and_the_others_are()
    {
        var catalog = CatalogOver();

        var roster = await catalog.LoadAsync();

        Assert.DoesNotContain(roster, d => d.Target == "decision-model");
        Assert.Null(catalog.Find("decision-model"));
        Assert.Equal(new[] { "amp", "claude-code", "llm", "opencode" }, roster.Select(d => d.Target).OrderBy(t => t, StringComparer.Ordinal));
    }

    [Fact]
    public async Task The_held_target_is_never_probed_even_when_every_card_is_warmed()
    {
        var probed = new List<string>();
        var catalog = CatalogOver(path => { lock (probed) { probed.Add(path); } });

        _ = await catalog.LoadAsync();
        await catalog.RefreshAfterCliChangeAsync();
        _ = await catalog.EnsureDetailAsync("llm");

        lock (probed)
        {
            Assert.DoesNotContain("decision-model", probed);
        }
    }

    [Fact]
    public async Task A_preview_connector_beside_it_keeps_its_status_through_the_catalog()
    {
        var catalog = CatalogOver();

        var roster = await catalog.LoadAsync();
        Assert.Equal(PlatformStatus.Preview, roster.Single(d => d.Target == "opencode").PlatformStatus);

        var detail = await catalog.EnsureDetailAsync("opencode");
        Assert.Equal(PlatformStatus.Preview, detail.PlatformStatus);
        Assert.Equal(PreviewStatusTests.PreviewNote, detail.PlatformNote);
        Assert.True(detail.IsDetailLoaded);
        Assert.Null(detail.DetailError);
    }

    [Fact]
    public async Task The_hubs_search_and_cards_do_not_find_it()
    {
        var catalog = CatalogOver();
        var roster = await catalog.LoadAsync();
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp);

        UiThread.Run(() =>
        {
            var vm = new SetupPanelViewModel(services);
            vm.ShowCards(roster);

            vm.SearchText = "decision";
            Assert.Empty(vm.Groups.SelectMany(g => g.Cards));

            vm.SearchText = "llm";
            Assert.Contains(vm.Groups.SelectMany(g => g.Cards), c => c.Target == "llm");

            vm.SearchText = string.Empty;
            Assert.Equal(roster.Count, vm.Groups.SelectMany(g => g.Cards).Count());
        });
    }

    [Fact]
    public void The_hold_is_exactly_one_named_entry_citing_the_ticket()
    {
        Assert.Equal(new[] { "decision-model" }, WizardCatalog.HeldUntilRelease.ToArray());
    }

    [Fact]
    public void The_command_palettes_registry_has_no_decision_model_entry_so_it_needs_no_filter()
    {
        // The palette is fed by the generated TUI registry, not by the wizard catalog; DefenseClaw 1.0.6's own registry_data.py lists no
        // decision-model command either. A regeneration that adds one fails here, which is the moment to decide whether the palette holds it too.
        foreach (var catalogue in new[] { TuiRegistryCatalogues.Baseline, TuiRegistryCatalogues.Extended, TuiRegistryCatalogues.ExtendedWithoutRemovedConnectors })
        {
            Assert.DoesNotContain(catalogue.Entries, e => e.Argv.Contains("decision-model"));
        }
    }
}
