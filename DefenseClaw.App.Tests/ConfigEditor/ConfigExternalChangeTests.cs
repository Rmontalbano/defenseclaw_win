using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels.ConfigEditor;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.Tests.ConfigEditor;

/// <summary>
/// An open draft survives a change to config.yaml on disk (CUST-377): review the difference, rebase the draft onto the new file (a key changed in both places is
/// flagged and keeps the file's value), undo, or discard. Synthetic text throughout; the pure merge first, then the editor around it.
/// </summary>
public sealed class ConfigExternalChangeTests
{
    private const string Base = "config_version: 9\nllm:\n  provider: openai\n  model: gpt-4o\nguardrail:\n  mode: observe\n  block_at: HIGH\nwatch:\n  debounce_ms: 500\n";

    // ------------------------------------------------------------------ the pure merge

    [Fact]
    public void Edits_to_keys_the_file_did_not_touch_go_onto_the_new_file_and_the_files_own_changes_stay()
    {
        var mine = Base.Replace("model: gpt-4o", "model: gpt-4.1", StringComparison.Ordinal);
        var theirs = Base.Replace("mode: observe", "mode: action", StringComparison.Ordinal);

        var plan = ConfigDraftRebase.Plan(Base, mine, theirs);

        Assert.True(plan.Success, plan.Failure);
        Assert.Equal(["llm.model"], plan.YourKeys);
        Assert.Equal(["guardrail.mode"], plan.DiskKeys);
        Assert.Empty(plan.Conflicts);
        Assert.Equal(Base.Replace("model: gpt-4o", "model: gpt-4.1", StringComparison.Ordinal).Replace("mode: observe", "mode: action", StringComparison.Ordinal), plan.Text);
    }

    [Fact]
    public void A_key_changed_in_both_places_to_different_values_is_a_conflict_and_keeps_the_files_value()
    {
        var mine = Base.Replace("model: gpt-4o", "model: gpt-4.1", StringComparison.Ordinal);
        var theirs = Base.Replace("model: gpt-4o", "model: o3", StringComparison.Ordinal);

        var plan = ConfigDraftRebase.Plan(Base, mine, theirs);

        Assert.True(plan.Success, plan.Failure);
        var conflict = Assert.Single(plan.Conflicts);
        Assert.Equal("llm.model", conflict.Path);
        Assert.Equal("gpt-4.1", conflict.Yours);
        Assert.Equal("o3", conflict.OnDisk);
        Assert.Contains("model: o3", plan.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("gpt-4.1", plan.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_same_change_made_in_both_places_is_not_a_conflict()
    {
        var both = Base.Replace("model: gpt-4o", "model: o3", StringComparison.Ordinal);

        var plan = ConfigDraftRebase.Plan(Base, both, both);

        Assert.True(plan.Success, plan.Failure);
        Assert.Empty(plan.Conflicts);
        Assert.Equal(both, plan.Text);
    }

    [Fact]
    public void A_key_the_file_removed_that_the_draft_changed_is_a_conflict_shown_as_removed()
    {
        var mine = Base.Replace("block_at: HIGH", "block_at: LOW", StringComparison.Ordinal);
        var theirs = Base.Replace("  block_at: HIGH\n", string.Empty, StringComparison.Ordinal);

        var plan = ConfigDraftRebase.Plan(Base, mine, theirs);

        Assert.True(plan.Success, plan.Failure);
        var conflict = Assert.Single(plan.Conflicts);
        Assert.Equal("guardrail.block_at", conflict.Path);
        Assert.Equal("LOW", conflict.Yours);
        Assert.Equal(ConfigDraftRebase.Removed, conflict.OnDisk);
        Assert.DoesNotContain("block_at", plan.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_secret_in_a_conflict_is_never_shown()
    {
        var baseText = "gateway:\n  host: 127.0.0.1\n  token: base-not-a-real-token\n";
        var plan = ConfigDraftRebase.Plan(
            baseText,
            baseText.Replace("base-not-a-real-token", "mine-not-a-real-token", StringComparison.Ordinal),
            baseText.Replace("base-not-a-real-token", "theirs-not-a-real-token", StringComparison.Ordinal));

        var conflict = Assert.Single(plan.Conflicts);
        Assert.Equal(ConfigDiffReviewBuilder.Hidden, conflict.Yours);
        Assert.Equal(ConfigDiffReviewBuilder.Hidden, conflict.OnDisk);
        Assert.DoesNotContain("not-a-real-token", plan.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Neighbouring_keys_edited_on_each_side_are_both_kept()
    {
        var mine = Base.Replace("provider: openai", "provider: anthropic", StringComparison.Ordinal);
        var theirs = Base.Replace("model: gpt-4o", "model: o3", StringComparison.Ordinal);

        var plan = ConfigDraftRebase.Plan(Base, mine, theirs);

        Assert.True(plan.Success, plan.Failure);
        Assert.Contains("provider: anthropic", plan.Text, StringComparison.Ordinal);
        Assert.Contains("model: o3", plan.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Both_sides_adding_a_different_key_at_the_same_place_keeps_both()
    {
        var mine = Base + "notifications:\n  enabled: true\n";
        var theirs = Base + "update:\n  channel: stable\n";

        var plan = ConfigDraftRebase.Plan(Base, mine, theirs);

        Assert.True(plan.Success, plan.Failure);
        Assert.Contains("notifications:", plan.Text, StringComparison.Ordinal);
        Assert.Contains("update:", plan.Text, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(LineEndings.Both), MemberType = typeof(LineEndings))]
    public void The_result_follows_the_files_line_ending_whichever_the_draft_used(string eol)
    {
        var mine = Base.Replace("model: gpt-4o", "model: gpt-4.1", StringComparison.Ordinal);
        var theirs = LineEndings.With(Base.Replace("mode: observe", "mode: action", StringComparison.Ordinal), eol);

        var plan = ConfigDraftRebase.Plan(Base, mine, theirs);

        Assert.True(plan.Success, plan.Failure);
        Assert.Equal(LineEndings.With(plan.Text, eol), plan.Text);
        Assert.EndsWith(eol, plan.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_draft_that_is_not_yaml_or_a_file_that_does_not_load_cannot_be_rebased_and_says_why()
    {
        var notYaml = ConfigDraftRebase.Plan(Base, "llm: [unclosed\n", Base);
        var fileBroken = ConfigDraftRebase.Plan(Base, Base + "x: 1\n", "llm: [unclosed\n");

        Assert.False(notYaml.Success);
        Assert.Contains("draft", notYaml.Failure, StringComparison.Ordinal);
        Assert.False(fileBroken.Success);
        Assert.Contains("on disk", fileBroken.Failure, StringComparison.Ordinal);
        Assert.Empty(notYaml.Text);
        Assert.Empty(fileBroken.Text);
    }

    [Fact]
    public void Edits_a_line_merge_cannot_place_are_refused_whole_never_half_applied()
    {
        // The draft changes line 3; the file deletes the line after it and the line before it: no line is shared to anchor on.
        var baseText = "a:\n  x: 1\n  y: 2\n  z: 3\n";
        var mine = "a:\n  x: 1\n  y: 20\n  z: 3\n";
        var theirs = "a:\n  w: 0\n";

        var plan = ConfigDraftRebase.Plan(baseText, mine, theirs);

        // Either it is combined and every key checks out (y was changed here and removed there: a conflict, the file's removal kept and y listed),
        // or it is refused with no text: never a text that silently lost y.
        if (plan.Success)
        {
            Assert.Contains(plan.Conflicts, c => c.Path == "a.y" && c.Yours == "20" && c.OnDisk == ConfigDraftRebase.Removed);
            Assert.DoesNotContain("y:", plan.Text, StringComparison.Ordinal);
        }
        else
        {
            Assert.Empty(plan.Text);
            Assert.NotNull(plan.Failure);
        }
    }

    [Fact]
    public void An_unparsable_base_is_treated_as_empty_so_a_draft_is_still_comparable()
    {
        var plan = ConfigDraftRebase.Plan("llm: [unclosed\n", Base, Base);

        Assert.True(plan.Success, plan.Failure);
        Assert.Empty(plan.Conflicts);
    }

    // ------------------------------------------------------------------ the editor around it

    private static async Task<ConfigEditorHarness> DraftOverChangedFileAsync(string eol = LineEndings.Lf)
    {
        var harness = await ConfigEditorHarness.LoadAsync(eol);
        harness.ViewModel.RawText = harness.Raw.Replace("model: gpt-4o", "model: gpt-4.1", StringComparison.Ordinal);
        harness.WriteConfigExternally(harness.Raw.Replace("mode: observe", "mode: action", StringComparison.Ordinal));
        await harness.ViewModel.HandleDiskChangeAsync();
        return harness;
    }

    [Theory]
    [MemberData(nameof(LineEndings.Both), MemberType = typeof(LineEndings))]
    public async Task Review_shows_what_changed_on_disk_and_changes_nothing(string eol)
    {
        using var harness = await DraftOverChangedFileAsync(eol);
        var vm = harness.ViewModel;
        var draft = vm.RawText;
        Assert.True(vm.ShowExternalChangeBanner);

        await vm.ReviewExternalChangeCommand.ExecuteAsync(null);

        Assert.True(vm.IsReviewingExternalChange);
        Assert.NotNull(vm.ExternalChangeDiff);
        Assert.Equal(1, vm.ExternalChangeDiff!.Added);
        Assert.Equal(1, vm.ExternalChangeDiff.Removed);
        Assert.Contains(vm.ExternalChangeDiff.Lines, line => line.Kind == "Added" && line.Text.Contains("mode: action", StringComparison.Ordinal));
        Assert.True(vm.CanRebaseDraft);
        Assert.Contains("1 setting changed on disk", vm.ExternalChangeSummary, StringComparison.Ordinal);
        Assert.Equal(draft, vm.RawText);
        Assert.True(vm.HasUnsavedChanges);

        vm.CancelExternalReviewCommand.Execute(null);
        Assert.False(vm.IsReviewingExternalChange);
        Assert.Equal(draft, vm.RawText);
    }

    [Theory]
    [MemberData(nameof(LineEndings.Both), MemberType = typeof(LineEndings))]
    public async Task Rebase_puts_the_draft_on_the_new_file_and_a_following_save_does_not_report_drift(string eol)
    {
        using var harness = await DraftOverChangedFileAsync(eol);
        var vm = harness.ViewModel;
        await vm.ReviewExternalChangeCommand.ExecuteAsync(null);

        await vm.RebaseDraftCommand.ExecuteAsync(null);

        Assert.False(vm.IsReviewingExternalChange);
        Assert.False(vm.ShowExternalChangeNotice);
        Assert.Contains("model: gpt-4.1", vm.RawText, StringComparison.Ordinal);
        Assert.Contains("mode: action", vm.RawText, StringComparison.Ordinal);
        Assert.True(vm.HasUnsavedChanges);
        Assert.True(vm.HasRebaseNotice);
        Assert.True(vm.CanUndoRebase);
        Assert.Contains("Nothing is saved yet", vm.RebaseNotice, StringComparison.Ordinal);
        Assert.Equal(harness.Raw.Replace("mode: observe", "mode: action", StringComparison.Ordinal), File.ReadAllText(harness.ConfigPath));

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.False(vm.ShowDriftBanner);
        Assert.False(vm.SaveResultIsError);
        Assert.False(vm.HasUnsavedChanges);
        var saved = File.ReadAllText(harness.ConfigPath);
        Assert.Contains("model: gpt-4.1", saved, StringComparison.Ordinal);
        Assert.Contains("mode: action", saved, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_conflict_keeps_the_files_value_lists_it_and_undo_brings_the_draft_back_exactly()
    {
        using var harness = await ConfigEditorHarness.LoadAsync();
        var vm = harness.ViewModel;
        var draft = harness.Raw.Replace("model: gpt-4o", "model: gpt-4.1", StringComparison.Ordinal);
        vm.RawText = draft;
        harness.WriteConfigExternally(harness.Raw.Replace("model: gpt-4o", "model: o3", StringComparison.Ordinal));
        await vm.HandleDiskChangeAsync();

        await vm.ReviewExternalChangeCommand.ExecuteAsync(null);
        var conflict = Assert.Single(vm.RebaseConflicts);
        Assert.Equal("llm.model", conflict.Path);
        Assert.True(vm.HasRebaseConflicts);

        await vm.RebaseDraftCommand.ExecuteAsync(null);

        Assert.Contains("model: o3", vm.RawText, StringComparison.Ordinal);
        Assert.DoesNotContain("gpt-4.1", vm.RawText, StringComparison.Ordinal);
        Assert.Contains("llm.model", vm.RebaseNotice, StringComparison.Ordinal);
        Assert.Contains("gpt-4.1", vm.RebaseNotice, StringComparison.Ordinal);
        Assert.True(vm.CanUndoRebase);

        vm.UndoRebaseCommand.Execute(null);

        Assert.Equal(draft, vm.RawText);
        Assert.True(vm.HasUnsavedChanges);
        Assert.False(vm.CanUndoRebase);
        Assert.False(vm.HasRebaseNotice);
    }

    [Fact]
    public async Task Editing_after_a_rebase_ends_undo_because_it_would_drop_the_edit_but_keeps_the_notice()
    {
        using var harness = await DraftOverChangedFileAsync();
        var vm = harness.ViewModel;
        await vm.ReviewExternalChangeCommand.ExecuteAsync(null);
        await vm.RebaseDraftCommand.ExecuteAsync(null);
        Assert.True(vm.CanUndoRebase);

        vm.RawText += "# one more thing\n";

        Assert.False(vm.CanUndoRebase);
        Assert.True(vm.HasRebaseNotice);
        vm.UndoRebaseCommand.Execute(null);
        Assert.EndsWith("# one more thing\n", vm.RawText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Discard_my_edits_and_reload_takes_the_file_and_clears_the_review()
    {
        using var harness = await DraftOverChangedFileAsync();
        var vm = harness.ViewModel;
        await vm.ReviewExternalChangeCommand.ExecuteAsync(null);

        await vm.ReloadDiscardingEditsCommand.ExecuteAsync(null);

        Assert.False(vm.IsReviewingExternalChange);
        Assert.False(vm.HasUnsavedChanges);
        Assert.Equal(File.ReadAllText(harness.ConfigPath), vm.RawText);
    }

    [Fact]
    public async Task A_file_that_no_longer_parses_cannot_be_rebased_onto_and_the_draft_is_untouched()
    {
        using var harness = await ConfigEditorHarness.LoadAsync();
        var vm = harness.ViewModel;
        vm.RawText = harness.Raw.Replace("model: gpt-4o", "model: gpt-4.1", StringComparison.Ordinal);
        var draft = vm.RawText;
        harness.WriteConfigExternally("llm: [unclosed\n");
        await vm.HandleDiskChangeAsync();
        Assert.True(vm.ShowExternalChangeNotice);

        await vm.ReviewExternalChangeCommand.ExecuteAsync(null);

        Assert.True(vm.IsReviewingExternalChange);
        Assert.False(vm.CanRebaseDraft);
        Assert.Contains("on disk", vm.RebaseBlockedReason, StringComparison.Ordinal);

        await vm.RebaseDraftCommand.ExecuteAsync(null);

        Assert.Equal(draft, vm.RawText);
        Assert.False(vm.CanUndoRebase);
        Assert.True(vm.IsReviewingExternalChange);
    }

    [Fact]
    public async Task A_form_value_held_back_for_its_error_blocks_a_rebase_rather_than_being_dropped()
    {
        using var harness = await ConfigEditorHarness.LoadAsync();
        var vm = harness.ViewModel;
        harness.Field("gateway.api_port").NumberValue = 70000;
        Assert.True(vm.HasBlockingFieldErrors);
        harness.WriteConfigExternally(harness.Raw.Replace("mode: observe", "mode: action", StringComparison.Ordinal));
        await vm.HandleDiskChangeAsync();

        await vm.ReviewExternalChangeCommand.ExecuteAsync(null);
        Assert.False(vm.CanRebaseDraft);
        Assert.Contains("error", vm.RebaseBlockedReason, StringComparison.Ordinal);

        await vm.RebaseDraftCommand.ExecuteAsync(null);

        Assert.True(vm.HasBlockingFieldErrors);
        Assert.False(vm.CanUndoRebase);
    }

    [Fact]
    public async Task A_save_that_reports_drift_offers_the_same_review_and_a_rebase_clears_the_drift()
    {
        using var harness = await ConfigEditorHarness.LoadAsync();
        var vm = harness.ViewModel;
        vm.RawText = harness.Raw.Replace("model: gpt-4o", "model: gpt-4.1", StringComparison.Ordinal);
        harness.WriteConfigExternally(harness.Raw.Replace("mode: observe", "mode: action", StringComparison.Ordinal));

        await vm.SaveCommand.ExecuteAsync(null);
        Assert.True(vm.ShowDriftBanner);

        await vm.ReviewExternalChangeCommand.ExecuteAsync(null);
        await vm.RebaseDraftCommand.ExecuteAsync(null);

        Assert.False(vm.ShowDriftBanner);
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.False(vm.ShowDriftBanner);
        Assert.Contains("model: gpt-4.1", File.ReadAllText(harness.ConfigPath), StringComparison.Ordinal);
        Assert.Contains("mode: action", File.ReadAllText(harness.ConfigPath), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ an unloadable config.yaml

    [Fact]
    public async Task A_config_that_does_not_parse_gets_a_card_with_the_parsers_line_and_column_and_raw_keeps_the_file()
    {
        var broken = "llm:\n  model: gpt-4o\n  provider: [unclosed\nguardrail:\n  mode: observe\n";
        using var harness = await ConfigEditorHarness.LoadAsync(raw: broken, maskedSource: string.Empty);
        var vm = harness.ViewModel;

        Assert.True(vm.HasLoadProblem);
        var problem = vm.LoadProblem!;
        Assert.Equal(ConfigLoadProblemKind.NotYaml, problem.Kind);
        Assert.True(problem.HasLocation);
        Assert.Matches(@"^Line \d+, column \d+$", problem.Location);
        Assert.False(string.IsNullOrWhiteSpace(problem.Reason));
        Assert.Contains("RAW", problem.Guidance, StringComparison.Ordinal);
        Assert.Equal(broken, vm.RawText);
        Assert.False(vm.ShowParseErrorBar);
        Assert.NotNull(vm.ParseError);
        Assert.True(vm.ShowFormEmptyState);
        Assert.Equal("FORM cannot be built from invalid YAML", vm.FormEmptyTitle);
    }

    [Fact]
    public async Task A_valid_config_has_no_load_problem()
    {
        using var harness = await ConfigEditorHarness.LoadAsync();

        Assert.False(harness.ViewModel.HasLoadProblem);
        Assert.Null(harness.ViewModel.LoadProblem);
        Assert.False(harness.ViewModel.HasLoadProblemActionMessage);
    }

    [Fact]
    public async Task A_config_over_the_nesting_guard_is_refused_with_a_reason_and_no_position()
    {
        var deep = "a: " + new string('[', DefenseClaw.Core.IO.ReadLimits.ConfigYamlFlowDepth + 5) + new string(']', DefenseClaw.Core.IO.ReadLimits.ConfigYamlFlowDepth + 5) + "\n";
        using var harness = await ConfigEditorHarness.LoadAsync(raw: deep, maskedSource: string.Empty);

        var problem = harness.ViewModel.LoadProblem;
        Assert.NotNull(problem);
        Assert.Equal(ConfigLoadProblemKind.Refused, problem!.Kind);
        Assert.False(problem.HasLocation);
        Assert.Equal(string.Empty, problem.Location);
    }

    [Fact]
    public async Task Fixing_the_file_elsewhere_loads_it_by_itself_and_a_second_failure_shows_its_new_reason()
    {
        var broken = "llm: [unclosed\n";
        using var harness = await ConfigEditorHarness.LoadAsync(raw: broken, maskedSource: string.Empty);
        var vm = harness.ViewModel;
        Assert.True(vm.HasLoadProblem);

        harness.WriteConfigExternally(ConfigSamples.Raw);
        await vm.HandleDiskChangeAsync();

        Assert.False(vm.HasLoadProblem);
        Assert.Null(vm.ParseError);
        Assert.Equal(ConfigSamples.Raw, vm.RawText);
    }

    [Fact]
    public async Task Open_in_editor_and_Show_in_Explorer_use_the_seams_and_say_so_when_they_cannot()
    {
        using var harness = await ConfigEditorHarness.LoadAsync(raw: "llm: [unclosed\n", maskedSource: string.Empty);
        var vm = harness.ViewModel;
        string? opened = null;
        (string Path, bool Select)? revealed = null;
        vm.OpenFileRequest = path => { opened = path; return true; };
        vm.RevealRequest = (path, select) => { revealed = (path, select); return true; };

        vm.OpenConfigInEditorCommand.Execute(null);
        vm.ShowConfigInExplorerCommand.Execute(null);

        Assert.Equal(harness.ConfigPath, opened);
        Assert.Equal((harness.ConfigPath, true), revealed);
        Assert.False(vm.HasLoadProblemActionMessage);

        vm.OpenFileRequest = _ => false;
        vm.OpenConfigInEditorCommand.Execute(null);
        Assert.True(vm.HasLoadProblemActionMessage);

        vm.RevealRequest = (_, _) => false;
        vm.ShowConfigInExplorerCommand.Execute(null);
        Assert.Contains("Explorer", vm.LoadProblemActionMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void The_problem_for_an_unreadable_or_oversize_file_is_a_reason_and_guidance_without_a_position()
    {
        var locked = ConfigLoadProblem.FromRead(new IOException("The process cannot access the file because it is being used by another process."));
        var big = ConfigLoadProblem.FromRead(new DefenseClaw.Core.IO.FileTooLargeException("C:\\x\\config.yaml", 10, 5));

        Assert.Equal(ConfigLoadProblemKind.Unreadable, locked.Kind);
        Assert.Contains("being used", locked.Reason, StringComparison.Ordinal);
        Assert.False(locked.HasLocation);
        Assert.Contains("Reload", locked.Guidance, StringComparison.Ordinal);
        Assert.Equal(ConfigLoadProblemKind.Refused, big.Kind);
    }

    // ------------------------------------------------------------------ the notes and the hot-reload outcome (a 1.0.4 runtime)

    private static RuntimeProbeRunner Runtime(string version) => (args, _) => Task.FromResult(
        args.Contains("--version-json")
            ? RuntimeProbeOutput.Ok("{\"name\":\"defenseclaw-cli\",\"version\":\"" + version + "\",\"schema_version\":1}")
            : RuntimeProbeOutput.Ok("Usage: defenseclaw [OPTIONS] COMMAND [ARGS]...\n\nCommands:\n  setup  Configure.\n"));

    [Theory]
    [InlineData("1.0.4")]
    [InlineData("1.0.3")]
    [InlineData("0.8.10")]
    public async Task A_save_that_only_changes_hot_keys_needs_no_restart_on_a_gateway_that_reloads_it(string version)
    {
        using var harness = await ConfigEditorHarness.LoadAsync(runtimeProbeRunner: Runtime(version));
        var vm = harness.ViewModel;
        var expectedHot = version != "0.8.10";
        Assert.Equal(expectedHot, vm.GatewayHotReloads);

        vm.RawText = harness.Raw.Replace("model: gpt-4o", "model: gpt-4.1", StringComparison.Ordinal);
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.False(vm.SaveResultIsError);
        Assert.Equal(!expectedHot, harness.Services.RestartQueue.IsPending);
        Assert.Equal(!expectedHot, vm.ShowRestartPrompt);
        Assert.Equal(expectedHot, vm.SaveResultMessage.Contains(ConfigEditorWindowViewModel.HotReloadSentence, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_save_that_changes_a_restart_key_still_queues_a_restart_and_names_only_that_section()
    {
        using var harness = await ConfigEditorHarness.LoadAsync(runtimeProbeRunner: Runtime("1.0.4"));
        var vm = harness.ViewModel;

        vm.RawText = harness.Raw
            .Replace("model: gpt-4o", "model: gpt-4.1", StringComparison.Ordinal)
            .Replace("api_port: 18970", "api_port: 18971", StringComparison.Ordinal);
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.True(harness.Services.RestartQueue.IsPending);
        Assert.Contains("gateway", harness.Services.RestartQueue.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("llm", harness.Services.RestartQueue.Reason, StringComparison.Ordinal);
        Assert.True(vm.ShowRestartPrompt);
        Assert.DoesNotContain(ConfigEditorWindowViewModel.HotReloadSentence, vm.SaveResultMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_splunk_local_stack_record_and_the_strictest_profile_rule_are_noted_only_for_a_1_0_4_runtime()
    {
        var raw = ConfigSamples.Raw.Replace("observability: {}\n", string.Empty, StringComparison.Ordinal) +
            "observability:\n  destinations:\n  - name: local-splunk\n    kind: splunk_hec\n    token_env: SPLUNK_TOKEN\n    local_stack:\n      marker: defenseclaw-local-splunk\n      container_id: 0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\n";
        raw = raw.Replace("guardrail:\n", "guardrail:\n  profile_assignments:\n  - profile: strict\n    match:\n      agents: [a]\n", StringComparison.Ordinal);

        using var current = await ConfigEditorHarness.LoadAsync(raw: raw, maskedSource: raw, runtimeProbeRunner: Runtime("1.0.4"));
        using var older = await ConfigEditorHarness.LoadAsync(raw: raw, maskedSource: raw, runtimeProbeRunner: Runtime("1.0.3"));

        Assert.True(current.ViewModel.HasFormNotes);
        Assert.Equal(2, current.ViewModel.FormNotes.Count);
        Assert.Contains(current.ViewModel.FormNotes, note => note.Contains("\"local-splunk\"", StringComparison.Ordinal) && note.Contains("0123456789ab", StringComparison.Ordinal) && note.Contains("read-only", StringComparison.Ordinal));
        Assert.Contains(current.ViewModel.FormNotes, note => note.Contains("strictest", StringComparison.Ordinal));
        Assert.DoesNotContain(current.ViewModel.FormNotes, note => note.Contains("SPLUNK_TOKEN", StringComparison.Ordinal));
        Assert.False(older.ViewModel.HasFormNotes);
    }
}
