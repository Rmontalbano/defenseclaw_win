using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.Tests;

/// <summary>
/// The command catalogue of the 1.0.7 and 1.0.8 runtimes: the generated <see cref="TuiRegistryCatalogues.Current"/> (the 1.0.8 registry, 250 entries)
/// and <see cref="TuiRegistryCatalogues.CurrentWithoutCowork"/> (1.0.7's, derived: the one tuple <c>setup cowork</c> is the only difference), and which
/// runtime gets which. Every runtime before 1.0.7 keeps the catalogue it always had. Synthetic probe screens throughout.
/// </summary>
public sealed class TuiRegistryCurrentTests
{
    private const string Help = "Usage: defenseclaw [OPTIONS] COMMAND [ARGS]...\n\nCommands:\n  setup  Configure.\n";
    private const string RegistrySetup = "Usage: defenseclaw setup [OPTIONS] COMMAND [ARGS]...\n\nCommands:\n  amp    Amp.\n  devin  Devin.\n  kiro   Kiro.\n";

    private static RuntimeCapabilities Probe(string version) =>
        RuntimeProbe.Evaluate(
            new RuntimeProbeScreens("{\"name\":\"defenseclaw-cli\",\"version\":\"" + version + "\",\"schema_version\":1}", Help, RegistrySetup, null, null, null, null, null),
            "test", "fp", DateTimeOffset.UnixEpoch).Capabilities;

    private static bool IsCowork(TuiRegistryEntry e) => e.Argv.SequenceEqual(new[] { "setup", "cowork", "--yes" });

    [Fact]
    public void The_1_0_8_catalogue_holds_the_whole_registry_and_records_where_it_came_from()
    {
        var current = TuiRegistryCatalogues.Current;

        Assert.Equal(250, current.Entries.Count);
        Assert.Equal(250, current.SourceEntries);
        Assert.Equal(250, TuiRegistryData.CurrentSourceEntries);
        Assert.StartsWith("ba257d3f", current.SourceSha256, StringComparison.Ordinal);
        Assert.Matches("^[0-9a-f]{64}$", current.SourceSha256);
        Assert.NotEqual(TuiRegistryCatalogues.Extended.SourceSha256, current.SourceSha256);
        Assert.Equal("cli/defenseclaw/tui/registry_data.py", current.Source);
        Assert.Contains("1.0.8", current.Runtime, StringComparison.Ordinal);
        Assert.Matches("^[0-9a-f]{7,40}$", TuiRegistryData.CurrentCommit);
        Assert.Equal(230, current.OnWindows.Count);
        Assert.Equal(20, current.HiddenOnWindows.Count);
    }

    [Fact]
    public void The_1_0_7_catalogue_is_the_1_0_8_one_without_setup_cowork_and_nothing_else()
    {
        var current = TuiRegistryCatalogues.Current;
        var derived = TuiRegistryCatalogues.CurrentWithoutCowork;

        var cowork = Assert.Single(current.Entries, IsCowork);
        Assert.Equal("setup cowork", cowork.Name);
        Assert.Null(cowork.WindowsUnavailable);

        Assert.Equal(249, derived.Entries.Count);
        Assert.Equal(249, derived.SourceEntries);
        Assert.DoesNotContain(derived.Entries, IsCowork);
        Assert.Null(derived.Find("setup cowork"));
        Assert.Equal(current.Entries.Where(e => !IsCowork(e)), derived.Entries);
        Assert.Equal(current.OnWindows.Count - 1, derived.OnWindows.Count);
        Assert.Equal(current.HiddenOnWindows.Count, derived.HiddenOnWindows.Count);
    }

    [Fact]
    public void Alerts_is_listed_without_an_option_the_1_0_8_command_does_not_have()
    {
        Assert.Equal(new[] { "alerts" }, TuiRegistryCatalogues.Current.Find("alerts")!.Argv);
        Assert.DoesNotContain(TuiRegistryCatalogues.Current.Entries, e => e.Argv.Contains("--no-tui"));
        Assert.NotNull(TuiRegistryCatalogues.Extended.Find("alerts"));
    }

    [Fact]
    public void Every_argv_of_both_new_catalogues_passes_the_hygiene_check_and_the_decision_model_has_no_entry()
    {
        foreach (var catalogue in new[] { TuiRegistryCatalogues.Current, TuiRegistryCatalogues.CurrentWithoutCowork })
        {
            Assert.All(catalogue.Entries, e => Assert.Null(TuiRegistryCatalogues.ArgvProblem(e.Argv)));
            Assert.All(catalogue.Entries, e => Assert.DoesNotContain("decision-model", e.Argv));
            Assert.Equal(catalogue.Entries.Count, catalogue.Entries.Select(e => e.Name).Distinct(StringComparer.Ordinal).Count());
        }
    }

    [Fact]
    public void A_1_0_8_runtime_gets_the_whole_registry_and_a_1_0_7_runtime_the_same_without_setup_cowork()
    {
        Assert.Same(TuiRegistryCatalogues.Current, TuiRegistryCatalogues.For(Probe("1.0.8")));
        Assert.Same(TuiRegistryCatalogues.Current, TuiRegistryCatalogues.For(Probe("1.0.9")));
        Assert.Same(TuiRegistryCatalogues.CurrentWithoutCowork, TuiRegistryCatalogues.For(Probe("1.0.7")));
    }

    [Fact]
    public void Every_runtime_before_1_0_7_keeps_the_catalogue_it_always_had()
    {
        Assert.Same(TuiRegistryCatalogues.Extended, TuiRegistryCatalogues.For(Probe("1.0.0")));
        Assert.Same(TuiRegistryCatalogues.Extended, TuiRegistryCatalogues.For(Probe("1.0.3")));
        Assert.Same(TuiRegistryCatalogues.ExtendedWithoutRemovedConnectors, TuiRegistryCatalogues.For(Probe("1.0.4")));
        Assert.Same(TuiRegistryCatalogues.ExtendedWithoutRemovedConnectors, TuiRegistryCatalogues.For(Probe("1.0.6")));
        Assert.Same(TuiRegistryCatalogues.Baseline, TuiRegistryCatalogues.For(RuntimeCapabilities.Unknown));
        Assert.Same(TuiRegistryCatalogues.Baseline, TuiRegistryCatalogues.For(null));
    }

    [Fact]
    public void The_baseline_label_says_0_8_10_whatever_the_upstream_tags_init_file_says()
    {
        Assert.Contains("0.8.10", TuiRegistryCatalogues.Baseline.Runtime, StringComparison.Ordinal);
        Assert.DoesNotContain("0.8.6", TuiRegistryCatalogues.Baseline.Runtime, StringComparison.Ordinal);
    }
}
