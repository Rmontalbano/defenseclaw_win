using DefenseClaw.App.Services;
using DefenseClaw.App.Services.FirstRun;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Runtime;
using DefenseClaw.Core.Setup;

namespace DefenseClaw.App.Tests.Setup;

/// <summary>
/// The connector batch dialog on DefenseClaw 1.0.10 (CUST-456), synthetic throughout: <c>setup -c</c>, <c>--detected</c> and <c>--all</c> then add or update the
/// connectors they name and keep every other one (<c>cmd_setup.py</c> at 1.0.10, <c>_roster_accepts_hook_merge</c>), unless the configured roster is empty or holds
/// openclaw or zeptoclaw. So a configured connector is "left as they were" when it is not ticked, nothing says it is dropped, and the review says what the command
/// does. 1.0.9, an empty roster and a roster with openclaw read exactly as before.
/// </summary>
public sealed class ConnectorBatchKeepsRosterTests : IDisposable
{
    private const string TwoConfigured = "guardrail:\n  connectors:\n    codex:\n      mode: observe\n    claudecode:\n      mode: observe\n";

    private const string OwnerShape = "guardrail:\n  connectors:\n    codex:\n      mode: observe\n";

    private const string WithOpenClaw = "guardrail:\n  connectors:\n    openclaw:\n      mode: observe\n    codex:\n      mode: observe\n";

    private const string EmptyRoster = "guardrail:\n  enabled: true\n";

    private readonly TempDirectory _temp = new();
    private readonly List<AppServices> _services = new();

    public void Dispose()
    {
        foreach (var services in _services)
        {
            services.Dispose();
        }

        _temp.Dispose();
    }

    private static string SetupHelp() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "runtime-0.8.10", "setup.txt"));

    private static readonly IReadOnlyList<OfferedConnector> Connectors = new[]
    {
        new OfferedConnector("codex", "Codex", "codex", PlatformStatus.Certified),
        new OfferedConnector("claudecode", "Claude Code", "claude-code", PlatformStatus.Certified),
        new OfferedConnector("cursor", "Cursor", "cursor", PlatformStatus.NotCertified),
    };

    private static RuntimeProbeRunner Runtime(string version) => (args, _) => Task.FromResult(
        args.Contains("--version-json")
            ? RuntimeProbeOutput.Ok("{\"name\":\"defenseclaw-cli\",\"version\":\"" + version + "\",\"schema_version\":1}")
            : RuntimeProbeOutput.Ok("Usage: defenseclaw [OPTIONS] COMMAND [ARGS]...\n\nCommands:\n  setup  Configure.\n"));

    private async Task<ConnectorBatchViewModel> OpenedAsync(string version, string yaml)
    {
        var services = TestServices.Create(_temp, configYaml: yaml, runtimeProbeRunner: Runtime(version));
        _services.Add(services);
        _ = await services.Runtime.RefreshAsync(force: true);

        var vm = new ConnectorBatchViewModel(
            services,
            _ => Task.FromResult(new HelpProbeResult(SetupHelp(), null)),
            () => Connectors);
        vm.Open();
        for (var i = 0; i < 100 && vm.IsChecking; i++)
        {
            await Task.Delay(10);
        }

        return vm;
    }

    private static BatchRow Row(ConnectorBatchViewModel vm, string id) => vm.Rows.Single(r => r.Id == id);

    private static IReadOnlyList<CommandReviewWarning> ReviewWarnings(ConnectorBatchViewModel vm)
    {
        vm.ReviewChangesCommand.Execute(null);
        return vm.Review.CommandReview!.Warnings;
    }

    // ------------------------------------------------------------------ 1.0.10, an all-hook roster: add or update, keep the rest

    [Fact]
    public async Task On_1_0_10_a_configured_connector_starts_unticked_and_is_left_as_it_was()
    {
        var vm = await OpenedAsync("1.0.10", TwoConfigured);

        Assert.True(vm.KeepsRoster);
        Assert.Equal(new[] { false, false, false }, vm.Rows.Select(r => r.IsOn));
        Assert.Equal("Configured now (observe). Left as they were.", Row(vm, "codex").StateText);
        Assert.Equal("Not configured", Row(vm, "cursor").StateText);
        Assert.True(vm.HasRosterNote);
        Assert.Contains("keeps its configuration and mode", vm.RosterNote, StringComparison.Ordinal);
        Assert.Contains("Remove a connector", vm.RosterNote, StringComparison.Ordinal);
        Assert.Contains(ConnectorBatchViewModel.ModeAppliesOnlyToNamed, vm.ModeNote, StringComparison.Ordinal);
    }

    [Fact]
    public async Task On_1_0_10_adding_one_connector_says_the_others_are_left_as_they_were_and_never_that_they_are_dropped()
    {
        var vm = await OpenedAsync("1.0.10", TwoConfigured);

        Row(vm, "cursor").IsOn = true;

        Assert.Equal("defenseclaw setup --yes --connector cursor --mode observe", vm.CommandPreview);
        Assert.Equal("1 change to apply", vm.Summary);
        Assert.Equal(new[] { "Add Cursor in observe mode", "Left as they were: Claude Code, Codex" }, vm.ChangeLines);
        Assert.DoesNotContain(vm.ChangeLines, l => l.StartsWith("Drop", StringComparison.Ordinal));
        Assert.Equal("will be added", Row(vm, "cursor").ChangeText);
        Assert.Equal(string.Empty, Row(vm, "codex").ChangeText);

        var warnings = ReviewWarnings(vm);

        Assert.DoesNotContain(warnings, w => w.Title is "Drops configured connectors" or "Sets the roster");
        var kept = Assert.Single(warnings, w => w.Title == ConnectorBatchViewModel.KeepsRosterTitle);
        Assert.Contains("adds or updates the connectors it names", kept.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Others keep their configuration and mode", kept.Message, StringComparison.Ordinal);
        Assert.Contains("Left as they were: Claude Code, Codex", kept.Message, StringComparison.Ordinal);
        Assert.Contains("Remove a connector", kept.Message, StringComparison.Ordinal);
        Assert.StartsWith("Adds or updates Cursor in observe mode", vm.Review.CommandReview!.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task On_1_0_10_ticking_a_configured_connector_updates_it_and_names_the_mode_it_moves_to()
    {
        var vm = await OpenedAsync("1.0.10", TwoConfigured);

        Row(vm, "codex").IsOn = true;
        vm.Mode = ConnectorBatch.ActionMode;

        Assert.Equal("will be updated", Row(vm, "codex").ChangeText);
        Assert.Contains("Codex: observe to action", vm.ChangeLines);
        Assert.Contains("Left as they were: Claude Code", vm.ChangeLines);
        Assert.Equal("defenseclaw setup --yes --connector codex --mode action", vm.CommandPreview);
        Assert.Equal("1 change to apply", vm.Summary);
    }

    [Fact]
    public async Task On_1_0_10_ticking_a_configured_connector_at_its_own_mode_is_an_update_the_review_can_run()
    {
        var vm = await OpenedAsync("1.0.10", TwoConfigured);

        Row(vm, "codex").IsOn = true;

        Assert.Equal("will be updated", Row(vm, "codex").ChangeText);
        Assert.True(vm.HasChanges);
        Assert.Equal("1 change to apply", vm.Summary);
        Assert.Equal(new[] { "Set up Codex again (stays observe)", "Left as they were: Claude Code" }, vm.ChangeLines);
        Assert.Equal("defenseclaw setup --yes --connector codex --mode observe", vm.CommandPreview);
    }

    [Fact]
    public async Task On_1_0_10_the_owner_shape_with_only_codex_opens_with_nothing_to_apply_and_nothing_dropped()
    {
        var vm = await OpenedAsync("1.0.10", OwnerShape);

        Assert.False(vm.HasChanges);
        Assert.Equal("Nothing to apply", vm.Summary);
        Assert.DoesNotContain(vm.ChangeLines, l => l.StartsWith("Drop", StringComparison.Ordinal));
        Assert.StartsWith("Tick at least one connector", vm.ReviewBlockedReason, StringComparison.Ordinal);

        // --detected and --all would give every connector they name, Codex included, the chosen mode, so the dialog does not offer them here.
        vm.ExtraIndex = 1;
        Assert.Equal(0, vm.ExtraIndex);
        Assert.False(vm.HasExtraChoices);
        Assert.Single(vm.ExtraChoices);
        Assert.False(vm.HasChanges);
        Assert.StartsWith("Tick at least one connector", vm.ReviewBlockedReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task On_1_0_10_the_argv_names_only_the_ticked_connectors_and_never_detected_or_all()
    {
        var vm = await OpenedAsync("1.0.10", OwnerShape);

        Row(vm, "cursor").IsOn = true;
        vm.ExtraIndex = 2;
        vm.Mode = ConnectorBatch.ActionMode;

        Assert.Equal(0, vm.ExtraIndex);
        Assert.Equal("defenseclaw setup --yes --connector cursor --mode action", vm.CommandPreview);
        Assert.Equal(new[] { "Add Cursor in action mode", "Left as they were: Codex" }, vm.ChangeLines);

        _ = ReviewWarnings(vm);
        Assert.Equal(new[] { "setup", "--yes", "--connector", "cursor", "--mode", "action" }, vm.LastArgv);
        Assert.DoesNotContain("--detected", vm.LastArgv);
        Assert.DoesNotContain("--all", vm.LastArgv);
    }

    [Fact]
    public async Task On_1_0_10_a_connector_without_its_own_mode_is_judged_by_the_global_guardrail_mode()
    {
        var vm = await OpenedAsync("1.0.10", "guardrail:\n  mode: action\n  connectors:\n    codex:\n      enabled: true\n");

        Assert.Equal("action", Row(vm, "codex").ConfiguredMode);
        Row(vm, "codex").IsOn = true;
        Assert.Contains("Codex: action to observe", vm.ChangeLines);
    }

    [Fact]
    public async Task On_1_0_9_a_connector_without_its_own_mode_still_reads_as_observe()
    {
        var vm = await OpenedAsync("1.0.9", "guardrail:\n  mode: action\n  connectors:\n    codex:\n      enabled: true\n");

        Assert.Null(Row(vm, "codex").ConfiguredMode);
        Assert.Equal(3, vm.ExtraChoices.Count);
        Assert.True(vm.HasExtraChoices);
    }

    [Fact]
    public async Task Connector_ids_from_config_yaml_are_shown_visible_and_cut_to_64_characters()
    {
        var longId = new string('x', 100);
        var vm = await OpenedAsync("1.0.10", "guardrail:\n  connectors:\n    codex:\n      mode: observe\n    " + longId + ":\n      mode: observe\n");

        Row(vm, "cursor").IsOn = true;

        var kept = Assert.Single(vm.ChangeLines, l => l.StartsWith("Left as they were", StringComparison.Ordinal));
        Assert.DoesNotContain(longId, kept, StringComparison.Ordinal);
        Assert.Contains(new string('x', 63) + "…", kept, StringComparison.Ordinal);
        Assert.DoesNotContain(longId, vm.RosterLine, StringComparison.Ordinal);
        Assert.Equal(64, ConnectorBatchViewModel.Shown(longId).Length);
        Assert.Equal("a\\u202Eb", ConnectorBatchViewModel.Shown("a‮b"));
    }

    // ------------------------------------------------------------------ 1.0.9: exactly today's lines

    [Fact]
    public async Task On_1_0_9_the_same_roster_starts_ticked_and_unticking_says_it_is_dropped_word_for_word()
    {
        var vm = await OpenedAsync("1.0.9", TwoConfigured);

        Assert.False(vm.KeepsRoster);
        Assert.Equal(new[] { true, true, false }, vm.Rows.Select(r => r.IsOn));
        Assert.Equal("Configured now (observe)", Row(vm, "codex").StateText);
        Assert.False(vm.HasRosterNote);
        Assert.DoesNotContain(ConnectorBatchViewModel.ModeAppliesOnlyToNamed, vm.ModeNote, StringComparison.Ordinal);
        Assert.False(vm.HasChanges);

        Row(vm, "claudecode").IsOn = false;

        Assert.Equal("will be dropped", Row(vm, "claudecode").ChangeText);
        Assert.Equal(new[] { "Drop Claude Code" }, vm.ChangeLines);
        Assert.Equal("1 change to apply", vm.Summary);

        var warnings = ReviewWarnings(vm);
        var drops = Assert.Single(warnings, w => w.Title == "Drops configured connectors");
        Assert.Equal("The batch sets the connector roster to the selection. Not watched any more: Claude Code.", drops.Message);
        Assert.DoesNotContain(warnings, w => w.Title == ConnectorBatchViewModel.KeepsRosterTitle);
        Assert.StartsWith("Configures Codex in observe mode in one command", vm.Review.CommandReview!.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task On_1_0_9_adding_a_connector_with_detected_says_the_batch_sets_the_roster()
    {
        var vm = await OpenedAsync("1.0.9", TwoConfigured);

        vm.ExtraIndex = 1;
        var warnings = ReviewWarnings(vm);

        var sets = Assert.Single(warnings, w => w.Title == "Sets the roster");
        Assert.Equal("The batch sets the connector roster to the selection. A configured connector that is not selected is dropped.", sets.Message);
    }

    // ------------------------------------------------------------------ 1.0.10 with a roster that still replaces

    [Fact]
    public async Task On_1_0_10_a_roster_with_openclaw_keeps_the_replace_semantics_and_the_old_lines()
    {
        var vm = await OpenedAsync("1.0.10", WithOpenClaw);

        Assert.False(vm.KeepsRoster);
        Assert.False(vm.HasRosterNote);
        Assert.Equal(new[] { true, false, false }, vm.Rows.Select(r => r.IsOn));

        Row(vm, "codex").IsOn = false;
        Row(vm, "cursor").IsOn = true;

        Assert.Contains("Drop Codex", vm.ChangeLines);
        Assert.Contains("Drop openclaw", vm.ChangeLines);
        Assert.DoesNotContain(vm.ChangeLines, l => l.StartsWith("Left as they were", StringComparison.Ordinal));
        var warnings = ReviewWarnings(vm);
        Assert.Contains(warnings, w => w.Title == "Drops configured connectors");
        Assert.DoesNotContain(warnings, w => w.Title == ConnectorBatchViewModel.KeepsRosterTitle);
    }

    [Fact]
    public async Task On_1_0_10_an_empty_roster_is_unchanged_too()
    {
        var vm = await OpenedAsync("1.0.10", EmptyRoster);

        Assert.False(vm.KeepsRoster);
        Assert.False(vm.HasRosterNote);
        Assert.Equal("Configured now: none.", vm.RosterLine);

        Row(vm, "codex").IsOn = true;

        Assert.Equal(new[] { "Add Codex in observe mode" }, vm.ChangeLines);
        Assert.Equal("1 change to apply", vm.Summary);

        vm.ExtraIndex = 2;
        var warnings = ReviewWarnings(vm);
        Assert.Contains(warnings, w => w.Title == "Sets the roster");
        Assert.DoesNotContain(warnings, w => w.Title == ConnectorBatchViewModel.KeepsRosterTitle);
    }
}
