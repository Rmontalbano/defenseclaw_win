using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.ViewModels.ConfigEditor;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Observability;

namespace DefenseClaw.App.Tests.Shell;

/// <summary>
/// The Overview panel and the config editor run some commands without a <c>CommandReview</c>, on purpose: every command either one
/// hands the CLI unreviewed is read-only, and a read-only command runs without a confirmation everywhere in the app. (The Overview's
/// Scan Skills and gateway actions are not among them: they open the in-panel review, see <c>OverviewQuickActionsTests</c>, as do the
/// Diagnostics checks' read-only guards.) What replaces the review is a guard that fails closed. These tests pin both halves, so a change
/// that adds a command that is not read-only to either surface fails here (and, if it slips past, at run time) instead of running unreviewed
/// — the fix is then to show it through <c>CommandReviewControl</c> like the Govern, Discover and gateway surfaces.
/// </summary>
public sealed class UnreviewedCommandSurfaceTests
{
    [Fact]
    public void The_overview_doctor_button_runs_plain_doctor_which_is_read_only()
    {
        Assert.Equal(new[] { "doctor" }, OverviewPanelViewModel.DoctorArgv);
        Assert.Equal(CommandTier.ReadOnly, CommandReview.ResolveTier(OverviewPanelViewModel.DoctorArgv));
    }

    [Fact]
    public void The_overview_doctor_line_is_the_command_as_the_review_surfaces_would_print_it()
    {
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp);
        var vm = new OverviewPanelViewModel(services);

        Assert.Equal("defenseclaw doctor", vm.DoctorCommandText);
        Assert.Equal(CommandReview.CommandLine(CommandReview.DefaultExecutable, OverviewPanelViewModel.DoctorArgv), vm.DoctorCommandText);
    }

    [Fact]
    public void The_overview_observability_card_reads_the_plan_with_a_command_that_is_read_only()
    {
        Assert.Equal(new[] { "observability", "plan", "--format", "json" }, ObservabilityPlanReader.Argv);
        Assert.Equal(CommandTier.ReadOnly, CommandReview.ResolveTier(ObservabilityPlanReader.Argv.ToArray()));
        Assert.NotEqual(CommandTier.ReadOnly, CommandReview.ResolveTier(new[] { "observability", "destination", "test", "example", "--write-probe" }));
    }

    [Fact]
    public void Doctor_with_fix_is_not_read_only_so_the_overview_could_never_offer_it_unreviewed()
    {
        Assert.NotEqual(CommandTier.ReadOnly, CommandReview.ResolveTier(new[] { "doctor", "--fix" }));
    }

    [Fact]
    public void Config_validate_after_a_save_is_read_only_and_stays_unconfirmed()
    {
        Assert.Equal(new[] { "config", "validate" }, ConfigSaveService.ValidateArgv);
        Assert.Equal(CommandTier.ReadOnly, CommandReview.ResolveTier(ConfigSaveService.ValidateArgv));
    }

    [Theory]
    [MemberData(nameof(ConfigEditorArgvs))]
    public void Every_command_the_config_editor_runs_is_read_only(string[] argv) =>
        Assert.Equal(CommandTier.ReadOnly, CommandReview.ResolveTier(argv));

    public static TheoryData<string[]> ConfigEditorArgvs() => new()
    {
        ConfigEditorWindowViewModel.SourceArgv,
        ConfigEditorWindowViewModel.EffectiveArgv,
        ConfigSaveService.ValidateArgv,
    };

    [Theory]
    [InlineData("config", "set", "guardrail.mode", "enforce")]
    [InlineData("setup", "guardrail", "--mode", "enforce")]
    [InlineData("skill", "remove", "--", "pdf-tools")]
    public async Task The_config_editor_refuses_a_command_that_is_not_read_only_instead_of_running_it(params string[] argv)
    {
        using var harness = await ConfigEditorHarness.LoadAsync();

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.ViewModel.RunReadOnlyAsync(argv, CancellationToken.None));

        Assert.Contains("without review", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("not a read-only command", refusal.Message, StringComparison.Ordinal);
    }
}
