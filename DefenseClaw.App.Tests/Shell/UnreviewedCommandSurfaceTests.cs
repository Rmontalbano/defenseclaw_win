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
/// Scan Skills, gateway, AI discovery and notification actions are not among them: they open the in-panel review, see <c>OverviewQuickActionsTests</c>
/// and <c>OverviewReviewedActionsTests</c>, as do the Diagnostics checks' read-only guards; Fill missing keys goes to a console and is no read either.)
/// What replaces the review is a guard that fails closed. These tests pin both halves, so a change
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

    // ---- The Overview's buttons the TUI offers by text (CUST-274): reviewed, never unreviewed ----

    public static TheoryData<string[]> OverviewButtonArgvs() => new()
    {
        OverviewPanelViewModel.EnableAiDiscoveryArgv,
        OverviewPanelViewModel.ScanAiDiscoveryArgv,
        OverviewPanelViewModel.NotificationsOnArgv,
        OverviewPanelViewModel.NotificationsOffArgv,
        OverviewPanelViewModel.FillMissingKeysArgv,
    };

    [Theory]
    [MemberData(nameof(OverviewButtonArgvs))]
    public void Every_state_changing_overview_button_is_a_change_that_is_reviewed_and_never_on_the_unreviewed_list(string[] argv)
    {
        Assert.NotEqual(CommandTier.ReadOnly, CommandReview.ResolveTier(argv));
        Assert.False(CommandReview.MayRunUnreviewed(argv));
        Assert.False(CommandTiers.IsUnreviewedRead(argv));

        // And the runner's own gate, which a managed or invalid installation relies on, agrees that none of them is a read.
        Assert.False(InstallationGate.IsReadOnly(CommandReview.DefaultExecutable, argv));
    }

    [Fact]
    public void The_exact_argv_of_the_overview_buttons_are_the_ones_the_tui_registry_and_the_cli_help_give()
    {
        Assert.Equal(new[] { "agent", "discovery", "enable", "--yes" }, OverviewPanelViewModel.EnableAiDiscoveryArgv);
        Assert.Equal(new[] { "agent", "discovery", "scan" }, OverviewPanelViewModel.ScanAiDiscoveryArgv);
        Assert.Equal(new[] { "setup", "notifications", "on" }, OverviewPanelViewModel.NotificationsOnArgv);
        Assert.Equal(new[] { "setup", "notifications", "off" }, OverviewPanelViewModel.NotificationsOffArgv);
        Assert.Equal(new[] { "keys", "fill-missing", "--yes" }, OverviewPanelViewModel.FillMissingKeysArgv);

        // Three exist as TUI registry entries of both runtimes with this very argv, so the palette's rows and the buttons cannot drift apart; the
        // fourth is the registry's `setup notifications` with the argument its hint names (`<status|on|off> [--yes]`).
        foreach (var catalogue in new[] { TuiRegistryCatalogues.Baseline, TuiRegistryCatalogues.Extended })
        {
            Assert.Contains(catalogue.Entries, e => e.Name == "agent discovery enable" && e.Argv.SequenceEqual(OverviewPanelViewModel.EnableAiDiscoveryArgv));
            Assert.Contains(catalogue.Entries, e => e.Name == "agent discovery scan" && e.Argv.SequenceEqual(OverviewPanelViewModel.ScanAiDiscoveryArgv));
            Assert.Contains(catalogue.Entries, e => e.Name == "keys fill-missing" && e.Argv.SequenceEqual(OverviewPanelViewModel.FillMissingKeysArgv));
            var notifications = Assert.Single(catalogue.Entries, e => e.Name == "setup notifications");
            Assert.Equal(new[] { "setup", "notifications" }, notifications.Argv);
            Assert.Contains("on|off", notifications.ArgumentHint, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_overview_policy_list_is_a_read_on_the_unreviewed_list_and_nothing_else_the_buttons_run_is()
    {
        var diagnostic = OverviewPanelViewModel.DiagnosticCommands.Single(c => c.Title == "List policies");

        Assert.Contains("policy list", CommandTiers.UnreviewedReadPaths);
        Assert.True(CommandReview.MayRunUnreviewed(diagnostic.Executable, diagnostic.Argv));
        Assert.Equal(CommandTier.ReadOnly, CommandReview.ResolveTier(diagnostic.Argv));
        Assert.True(InstallationGate.IsReadOnly(diagnostic.Executable, diagnostic.Argv));
    }

    [Theory]
    [InlineData("setup", "observability", "list", "--json")]
    [InlineData("setup", "webhook", "list", "--json")]
    [InlineData("setup", "trusted-paths", "list", "--json")]
    [InlineData("setup", "webhook", "show", "--json", "--", "example-slack")]
    public void The_setup_editors_reads_are_read_only_to_every_surface_yet_never_run_unreviewed_from_a_pick(params string[] argv)
    {
        // CUST-326: named read-only leaves, so a review (the palette's, Activity's Rerun) shows them as the reads they are...
        Assert.Equal(CommandTier.ReadOnly, CommandReview.ResolveTier(argv));
        Assert.True(InstallationGate.IsReadOnly("defenseclaw", argv));

        // ...but the allow-list of what a pick may run with no review is unchanged: the editors run them through their own whole-shape door.
        Assert.False(CommandReview.MayRunUnreviewed(argv));
        Assert.False(CommandTiers.IsUnreviewedRead(argv));
        Assert.DoesNotContain(string.Join(' ', argv.TakeWhile(a => !a.StartsWith('-'))), CommandTiers.UnreviewedReadPaths);
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
