using DefenseClaw.App.Services;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.Tests.Shell;

/// <summary>
/// Which command palette catalogue a runtime gets: a 1.0.8 runtime the 1.0.8 registry (with <c>setup cowork</c>), a 1.0.7 runtime the same without
/// that one entry, and every older runtime exactly what it was offered before the newer registries existed. Synthetic probe screens throughout.
/// </summary>
public sealed class CurrentCommandCatalogueTests
{
    private const string Help = "Usage: defenseclaw [OPTIONS] COMMAND [ARGS]...\n\nCommands:\n  setup  Configure.\n";
    private const string RegistrySetup = "Usage: defenseclaw setup [OPTIONS] COMMAND [ARGS]...\n\nCommands:\n  amp    Amp.\n  devin  Devin.\n  kiro   Kiro.\n";

    private static readonly string[] CoworkArgv = { "setup", "cowork", "--yes" };

    private static RuntimeCapabilities Probe(string version) =>
        RuntimeProbe.Evaluate(
            new RuntimeProbeScreens("{\"name\":\"defenseclaw-cli\",\"version\":\"" + version + "\",\"schema_version\":1}", Help, RegistrySetup, null, null, null, null, null),
            "test", "fp", DateTimeOffset.UnixEpoch).Capabilities;

    private static bool OffersCowork(CuratedCommandCatalog catalogue) => catalogue.Commands.Any(c => c.Argv.SequenceEqual(CoworkArgv));

    [Fact]
    public void A_1_0_8_runtime_is_offered_setup_cowork_and_a_1_0_7_runtime_is_not_but_is_offered_everything_else()
    {
        var current = CuratedCommandCatalog.For(Probe("1.0.8"));
        var older = CuratedCommandCatalog.For(Probe("1.0.7"));

        Assert.Same(TuiRegistryCatalogues.Current, current.Source);
        Assert.Same(TuiRegistryCatalogues.CurrentWithoutCowork, older.Source);
        Assert.True(OffersCowork(current));
        Assert.False(OffersCowork(older));

        Assert.Equal(
            current.Commands.Where(c => !c.Argv.SequenceEqual(CoworkArgv)).Select(c => c.Id + "|" + c.Summary).Order(StringComparer.Ordinal),
            older.Commands.Select(c => c.Id + "|" + c.Summary).Order(StringComparer.Ordinal));
        Assert.Equal(current.Commands.Count - 1, older.Commands.Count);

        // The catalogues are built once.
        Assert.Same(current, CuratedCommandCatalog.For(Probe("1.0.8")));
        Assert.Same(older, CuratedCommandCatalog.For(Probe("1.0.7")));
    }

    [Fact]
    public void The_1_0_8_alerts_row_has_no_option_and_every_row_of_both_catalogues_passes_the_argv_check()
    {
        foreach (var catalogue in new[] { CuratedCommandCatalog.For(Probe("1.0.8")), CuratedCommandCatalog.For(Probe("1.0.7")) })
        {
            Assert.Contains(catalogue.Commands, c => c.Argv.SequenceEqual(new[] { "alerts" }));
            Assert.All(catalogue.Commands, c => Assert.Null(TuiRegistryCatalogues.ArgvProblem(c.Argv)));
        }
    }

    [Fact]
    public void Older_runtimes_are_offered_the_catalogues_they_always_were()
    {
        var extended = CuratedCommandCatalog.For(Probe("1.0.0"));
        Assert.Same(TuiRegistryCatalogues.Extended, extended.Source);
        Assert.Same(extended, CuratedCommandCatalog.For(Probe("1.0.3")));

        // 1.0.4 to 1.0.6 get the removed-connector registry as before, which the palette has always shown as the 0.8.10 list.
        var baseline = CuratedCommandCatalog.For(null);
        Assert.Same(TuiRegistryCatalogues.Baseline, baseline.Source);
        Assert.Same(baseline, CuratedCommandCatalog.For(Probe("1.0.4")));
        Assert.Same(baseline, CuratedCommandCatalog.For(Probe("1.0.6")));
        Assert.Same(baseline, CuratedCommandCatalog.For(RuntimeCapabilities.Unknown));

        Assert.All(new[] { extended, baseline }, c => Assert.False(OffersCowork(c)));
    }
}
