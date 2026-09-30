using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels.ConfigEditor;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.Tests.ConfigEditor;

/// <summary>
/// The FORM tab is built from <c>defenseclaw config show</c>'s whole output. With the runner's ordinary cap
/// (2,000 lines / 256 KiB, oldest dropped) a long config lost its head and parsed as a shorter one, and the
/// truncation notice was invisible because only stdout was read (D3-10).
/// </summary>
public sealed class ConfigEditorFullOutputTests
{
    [Fact]
    public async Task The_read_only_config_reads_ask_for_the_full_output_ceilings()
    {
        using var harness = await ConfigEditorHarness.LoadAsync();

        // An executable that cannot start still leaves an invocation behind, and it records which retention it asked for.
        harness.PlantUnrunnableCli();
        var invocation = await harness.ViewModel.RunReadOnlyAsync(ConfigEditorWindowViewModel.SourceArgv, CancellationToken.None);

        Assert.True(invocation.RetainsFullOutput);
    }

    private static CliInvocation Finished(bool retainFullOutput, int lines)
    {
        var invocation = InvocationFactory.Create(retainFullOutput, ConfigEditorWindowViewModel.SourceArgv);
        InvocationFactory.AppendNumbered(invocation, lines, prefix: "key");
        InvocationFactory.Finish(invocation);
        return invocation;
    }

    [Fact]
    public void A_source_view_longer_than_the_ordinary_cap_is_kept_whole()
    {
        var invocation = Finished(retainFullOutput: true, lines: 5_000);

        var result = ConfigEditorWindowViewModel.FormSourceFrom(invocation, ConfigEditorWindowViewModel.SourceArgv);

        Assert.Null(result.Failure);
        Assert.Equal(5_000, result.Yaml!.Split(Environment.NewLine).Length);
        Assert.StartsWith("key 1" + Environment.NewLine, result.Yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void A_source_view_that_lost_its_head_is_refused_and_never_becomes_the_form_tree()
    {
        // Past even the full-output ceiling the runner drops the oldest lines; a config missing its top must not be edited.
        var invocation = Finished(retainFullOutput: false, lines: 2_500);
        Assert.True(invocation.IsOutputTruncated);

        var result = ConfigEditorWindowViewModel.FormSourceFrom(invocation, ConfigEditorWindowViewModel.SourceArgv);

        Assert.Null(result.Yaml);
        Assert.Contains("only part of config.yaml", result.Failure, StringComparison.Ordinal);
        Assert.Contains("RAW editing still works", result.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public void A_failed_run_still_reports_the_failure_first()
    {
        var invocation = InvocationFactory.Create(true, ConfigEditorWindowViewModel.SourceArgv);
        InvocationFactory.Finish(invocation, exitCode: 2);

        var result = ConfigEditorWindowViewModel.FormSourceFrom(invocation, ConfigEditorWindowViewModel.SourceArgv);

        Assert.Null(result.Yaml);
        Assert.Contains("exited 2", result.Failure, StringComparison.Ordinal);
    }
}
