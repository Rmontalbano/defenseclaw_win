using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels.ConfigEditor;

namespace DefenseClaw.App.Tests.ConfigEditor;

/// <summary>
/// CUST-174: closing the editor, pressing Reload, restoring a backup, or another tool rewriting config.yaml must never
/// silently drop edits — RAW or FORM. The prompt is a seam on the view-model, so every answer here is canned and no
/// window is shown; the window's own part (holding <c>Closing</c>, the dialog) is a thin shell over
/// <see cref="ConfigEditorWindowViewModel.ConfirmCloseAsync"/> and <see cref="ConfigEditorWindowViewModel.CloseNeedsConfirmation"/>.
/// <para>
/// A save that "fails validation" is produced without any CLI: <see cref="ConfigEditorHarness.PlantUnrunnableCli"/> puts a file
/// that is not a program where the runner looks for <c>defenseclaw</c>, so <c>config validate</c> cannot run and the save reports
/// <see cref="SaveStage.ValidationFailed"/>. With no CLI at all (the default), a save writes the file and reports success with a
/// "could not validate" note — the same outcome the Save button's green banner uses.
/// </para>
/// </summary>
public sealed class ConfigEditorUnsavedChangesTests
{
    private const string Edited = "mode: enforce";

    /// <summary>Records every question the view-model asks and answers each with <paramref name="answer"/>.</summary>
    private sealed class CannedPrompt
    {
        public CannedPrompt(ConfigEditorWindowViewModel vm, UnsavedChangesChoice answer)
        {
            Answer = answer;
            vm.UnsavedChangesPrompt = request =>
            {
                Asked.Add(request);
                return Task.FromResult(Answer);
            };
        }

        public UnsavedChangesChoice Answer { get; set; }

        public List<UnsavedChangesRequest> Asked { get; } = new();
    }

    /// <summary>RAW edited in the way a person would: one setting changed, everything else as it was.</summary>
    private static string EditedRaw(ConfigEditorHarness harness) =>
        harness.Raw.Replace("mode: observe", Edited, StringComparison.Ordinal);

    private static async Task<ConfigEditorHarness> DirtyEditorAsync(string eol = LineEndings.Lf)
    {
        var harness = await ConfigEditorHarness.LoadAsync(eol);
        harness.ViewModel.RawText = EditedRaw(harness);
        return harness;
    }

    private static string OnDisk(ConfigEditorHarness harness) => File.ReadAllText(harness.ConfigPath);

    // ------------------------------------------------------------------ dirty tracking

    [Theory]
    [MemberData(nameof(LineEndings.Both), MemberType = typeof(LineEndings))]
    public async Task A_freshly_loaded_editor_has_nothing_to_lose(string eol)
    {
        using var harness = await ConfigEditorHarness.LoadAsync(eol);
        var vm = harness.ViewModel;
        var prompt = new CannedPrompt(vm, UnsavedChangesChoice.Cancel);

        Assert.False(vm.HasUnsavedChanges);
        Assert.False(vm.IsRawModified);
        Assert.False(vm.CloseNeedsConfirmation());
        Assert.True(await vm.ConfirmCloseAsync());
        Assert.Empty(prompt.Asked);
    }

    [Theory]
    [MemberData(nameof(LineEndings.Both), MemberType = typeof(LineEndings))]
    public async Task Typing_in_RAW_is_a_change_and_typing_it_back_is_not(string eol)
    {
        using var harness = await ConfigEditorHarness.LoadAsync(eol);
        var vm = harness.ViewModel;

        vm.RawText = EditedRaw(harness);
        Assert.True(vm.HasUnsavedChanges);
        Assert.True(vm.IsRawModified);

        vm.RawText = harness.Raw;
        Assert.False(vm.HasUnsavedChanges);
        Assert.False(vm.IsRawModified);
    }

    [Theory]
    [MemberData(nameof(LineEndings.Both), MemberType = typeof(LineEndings))]
    public async Task A_FORM_edit_is_a_change_because_it_lands_in_the_same_text(string eol)
    {
        using var harness = await ConfigEditorHarness.LoadAsync(eol);
        var vm = harness.ViewModel;
        Assert.False(vm.HasUnsavedChanges);

        // `action` is one of the two guardrail modes; the field is a list since CUST-268, and a mode the list does not have is held back.
        harness.Field("guardrail.mode").TextValue = "action";

        Assert.Null(vm.FieldErrorMessage);
        Assert.True(vm.HasUnsavedChanges);
        Assert.True(vm.CloseNeedsConfirmation());
    }

    [Fact]
    public async Task A_FORM_value_still_in_its_box_is_committed_before_anyone_decides_there_is_nothing_to_lose()
    {
        using var harness = await ConfigEditorHarness.LoadAsync();
        var vm = harness.ViewModel;
        var commits = 0;

        // What the window does: move focus off the FORM box so its LostFocus binding delivers the typed value.
        vm.CommitPendingEdits = () =>
        {
            commits++;
            harness.Field("guardrail.mode").TextValue = "action";
        };

        Assert.False(vm.HasUnsavedChanges);
        Assert.True(vm.CloseNeedsConfirmation());
        Assert.Equal(1, commits);
        Assert.Contains("mode: action", vm.RawText, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(LineEndings.Both), MemberType = typeof(LineEndings))]
    public async Task Reloading_and_a_successful_save_both_clear_the_change(string eol)
    {
        using var harness = await DirtyEditorAsync(eol);
        var vm = harness.ViewModel;
        Assert.True(vm.HasUnsavedChanges);

        await vm.SaveCommand.ExecuteAsync(null);
        Assert.True(vm.SaveResultIsError is false);
        Assert.False(vm.HasUnsavedChanges);

        vm.RawText += "# more\n";
        Assert.True(vm.HasUnsavedChanges);

        _ = new CannedPrompt(vm, UnsavedChangesChoice.Discard);
        await vm.ReloadCommand.ExecuteAsync(null);
        Assert.False(vm.HasUnsavedChanges);
    }

    [Fact]
    public async Task A_save_the_CLI_could_not_validate_leaves_the_editor_modified_until_the_next_load()
    {
        using var harness = await DirtyEditorAsync();
        var vm = harness.ViewModel;
        harness.PlantUnrunnableCli();

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.True(vm.SaveResultIsError);
        Assert.Equal(EditedRaw(harness), OnDisk(harness));
        Assert.True(vm.HasUnsavedChanges);

        // Typing the on-disk text back is "no difference from the file" — modified only ever means "differs from disk" after that.
        vm.RawText += "x";
        vm.RawText = vm.RawText[..^1];
        Assert.False(vm.HasUnsavedChanges);
    }

    // ------------------------------------------------------------------ closing

    [Theory]
    [MemberData(nameof(LineEndings.Both), MemberType = typeof(LineEndings))]
    public async Task Closing_with_unsaved_edits_asks_once_with_save_as_the_default(string eol)
    {
        using var harness = await DirtyEditorAsync(eol);
        var vm = harness.ViewModel;
        var prompt = new CannedPrompt(vm, UnsavedChangesChoice.Cancel);

        Assert.True(vm.CloseNeedsConfirmation());
        _ = await vm.ConfirmCloseAsync();

        var request = Assert.Single(prompt.Asked);
        Assert.Equal(UnsavedChangesContext.Close, request.Context);
        Assert.True(request.CanSave);
        Assert.Equal(UnsavedChangesChoice.Save, request.DefaultChoice);
        Assert.Null(request.Warning);
        Assert.Equal("Save and close", request.SaveLabel);
        Assert.Equal("Discard and close", request.DiscardLabel);
    }

    [Fact]
    public async Task Cancel_keeps_the_window_the_edits_and_the_file_exactly_as_they_were()
    {
        using var harness = await DirtyEditorAsync();
        var vm = harness.ViewModel;
        _ = new CannedPrompt(vm, UnsavedChangesChoice.Cancel);

        Assert.False(await vm.ConfirmCloseAsync());

        Assert.Equal(EditedRaw(harness), vm.RawText);
        Assert.True(vm.HasUnsavedChanges);
        Assert.Equal(harness.Raw, OnDisk(harness));
        Assert.False(vm.ShowSaveResultBanner);
    }

    [Fact]
    public async Task Discard_lets_the_close_go_ahead_without_touching_the_file()
    {
        using var harness = await DirtyEditorAsync();
        var vm = harness.ViewModel;
        _ = new CannedPrompt(vm, UnsavedChangesChoice.Discard);

        Assert.True(await vm.ConfirmCloseAsync());

        Assert.Equal(harness.Raw, OnDisk(harness));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(harness.ConfigPath)!, "config.yaml.bak-*"));
    }

    [Theory]
    [MemberData(nameof(LineEndings.Both), MemberType = typeof(LineEndings))]
    public async Task Save_writes_the_file_with_a_backup_and_only_then_lets_the_close_go_ahead(string eol)
    {
        using var harness = await DirtyEditorAsync(eol);
        var vm = harness.ViewModel;
        _ = new CannedPrompt(vm, UnsavedChangesChoice.Save);

        Assert.True(await vm.ConfirmCloseAsync());

        Assert.Equal(EditedRaw(harness), OnDisk(harness));
        Assert.False(vm.HasUnsavedChanges);
        Assert.False(vm.SaveResultIsError);
        var backup = Assert.Single(Directory.GetFiles(Path.GetDirectoryName(harness.ConfigPath)!, "config.yaml.bak-*"));
        Assert.Equal(harness.Raw, File.ReadAllText(backup));
    }

    [Fact]
    public async Task A_save_that_does_not_validate_keeps_the_window_open_with_the_error_showing()
    {
        using var harness = await DirtyEditorAsync();
        var vm = harness.ViewModel;
        harness.PlantUnrunnableCli();
        var prompt = new CannedPrompt(vm, UnsavedChangesChoice.Save);

        Assert.False(await vm.ConfirmCloseAsync());

        Assert.Single(prompt.Asked);
        Assert.True(vm.ShowSaveResultBanner);
        Assert.True(vm.SaveResultIsError);
        Assert.Contains("validation could not run", vm.SaveResultMessage, StringComparison.Ordinal);
        Assert.Equal(EditedRaw(harness), vm.RawText);
        Assert.True(vm.HasUnsavedChanges);
        Assert.True(vm.CloseNeedsConfirmation());
    }

    [Fact]
    public async Task A_save_refused_because_the_file_moved_keeps_the_window_open_and_shows_the_drift()
    {
        using var harness = await DirtyEditorAsync();
        var vm = harness.ViewModel;
        var prompt = new CannedPrompt(vm, UnsavedChangesChoice.Save);

        // Save is on offer only while the file is what the editor loaded; here another tool has already rewritten it.
        harness.WriteConfigExternally(harness.Raw + "# somebody else\n");

        Assert.False(await vm.ConfirmCloseAsync());

        var request = Assert.Single(prompt.Asked);
        Assert.False(request.CanSave);
        Assert.Null(request.SaveLabel);
        Assert.Equal(UnsavedChangesChoice.Cancel, request.DefaultChoice);
        Assert.Contains("changed on disk", request.Warning, StringComparison.Ordinal);

        // A Save answer to a prompt that did not offer Save is not honoured, and nothing was overwritten.
        Assert.Equal(harness.Raw + "# somebody else\n", OnDisk(harness));
        Assert.Equal(EditedRaw(harness), vm.RawText);
        Assert.False(vm.SaveResultIsError);
    }

    [Fact]
    public async Task With_no_prompt_wired_up_the_answer_is_cancel_never_discard()
    {
        using var harness = await DirtyEditorAsync();
        var vm = harness.ViewModel;
        Assert.Null(vm.UnsavedChangesPrompt);

        Assert.False(await vm.ConfirmCloseAsync());

        Assert.Equal(EditedRaw(harness), vm.RawText);
        Assert.Equal(harness.Raw, OnDisk(harness));
    }

    [Fact]
    public async Task Invalid_yaml_defaults_to_cancel_and_says_why_but_still_offers_save()
    {
        using var harness = await ConfigEditorHarness.LoadAsync();
        var vm = harness.ViewModel;
        var prompt = new CannedPrompt(vm, UnsavedChangesChoice.Cancel);

        vm.RawText = harness.Raw + "broken: [unclosed\n";
        vm.NotifyFormTabSelected();
        Assert.NotNull(vm.ParseError);

        _ = await vm.ConfirmCloseAsync();

        var request = Assert.Single(prompt.Asked);
        Assert.True(request.CanSave);
        Assert.Equal(UnsavedChangesChoice.Cancel, request.DefaultChoice);
        Assert.Contains("YAML", request.Warning, StringComparison.Ordinal);
        Assert.Equal(harness.Raw, OnDisk(harness));
    }

    [Fact]
    public async Task A_second_close_while_a_question_is_open_is_refused_not_stacked()
    {
        using var harness = await DirtyEditorAsync();
        var vm = harness.ViewModel;
        var release = new TaskCompletionSource<UnsavedChangesChoice>();
        var asked = 0;
        vm.UnsavedChangesPrompt = _ =>
        {
            asked++;
            return release.Task;
        };

        var first = vm.ConfirmCloseAsync();
        Assert.False(first.IsCompleted);
        Assert.True(vm.CloseNeedsConfirmation());

        Assert.False(await vm.ConfirmCloseAsync());
        Assert.Equal(1, asked);

        release.SetResult(UnsavedChangesChoice.Discard);
        Assert.True(await first);
    }

    // ------------------------------------------------------------------ Reload

    [Fact]
    public async Task Reload_with_no_edits_just_reloads_and_never_asks()
    {
        using var harness = await ConfigEditorHarness.LoadAsync();
        var vm = harness.ViewModel;
        var prompt = new CannedPrompt(vm, UnsavedChangesChoice.Cancel);
        harness.WriteConfigExternally(harness.Raw + "# newer\n");

        await vm.ReloadCommand.ExecuteAsync(null);

        Assert.Empty(prompt.Asked);
        Assert.Equal(harness.Raw + "# newer\n", vm.RawText);
    }

    [Fact]
    public async Task Reload_with_edits_asks_and_cancel_leaves_the_editor_alone()
    {
        using var harness = await DirtyEditorAsync();
        var vm = harness.ViewModel;
        var prompt = new CannedPrompt(vm, UnsavedChangesChoice.Cancel);

        await vm.ReloadCommand.ExecuteAsync(null);

        var request = Assert.Single(prompt.Asked);
        Assert.Equal(UnsavedChangesContext.Reload, request.Context);
        Assert.Equal("Save and reload", request.SaveLabel);
        Assert.Equal(EditedRaw(harness), vm.RawText);
        Assert.True(vm.HasUnsavedChanges);
    }

    [Fact]
    public async Task Reload_with_edits_and_discard_replaces_them_with_the_file()
    {
        using var harness = await DirtyEditorAsync();
        var vm = harness.ViewModel;
        _ = new CannedPrompt(vm, UnsavedChangesChoice.Discard);

        await vm.ReloadCommand.ExecuteAsync(null);

        Assert.Equal(harness.Raw, vm.RawText);
        Assert.False(vm.HasUnsavedChanges);
        Assert.Equal(harness.Raw, OnDisk(harness));
    }

    [Fact]
    public async Task Reload_with_edits_and_save_saves_then_reloads_the_saved_file()
    {
        using var harness = await DirtyEditorAsync();
        var vm = harness.ViewModel;
        _ = new CannedPrompt(vm, UnsavedChangesChoice.Save);

        await vm.ReloadCommand.ExecuteAsync(null);

        Assert.Equal(EditedRaw(harness), OnDisk(harness));
        Assert.Equal(EditedRaw(harness), vm.RawText);
        Assert.False(vm.HasUnsavedChanges);
    }

    [Fact]
    public async Task Reload_does_not_go_ahead_when_the_save_it_asked_for_failed()
    {
        using var harness = await DirtyEditorAsync();
        var vm = harness.ViewModel;
        harness.PlantUnrunnableCli();
        _ = new CannedPrompt(vm, UnsavedChangesChoice.Save);

        await vm.ReloadCommand.ExecuteAsync(null);

        Assert.Equal(EditedRaw(harness), vm.RawText);
        Assert.True(vm.HasUnsavedChanges);
        Assert.True(vm.SaveResultIsError);
        Assert.True(vm.ShowSaveResultBanner);
    }

    // ------------------------------------------------------------------ Restore

    [Fact]
    public async Task Restore_with_edits_made_since_the_last_write_asks_discard_or_cancel_and_never_offers_save()
    {
        using var harness = await DirtyEditorAsync();
        var vm = harness.ViewModel;
        var prompt = new CannedPrompt(vm, UnsavedChangesChoice.Save);

        await vm.SaveCommand.ExecuteAsync(null);
        Assert.NotNull(vm.LastBackupPath);
        vm.RawText += "# typed after the save\n";

        // A Save answer is meaningless for Restore and is treated as Cancel.
        await vm.RestoreFromBackupCommand.ExecuteAsync(null);
        var request = Assert.Single(prompt.Asked);
        Assert.Equal(UnsavedChangesContext.Restore, request.Context);
        Assert.False(request.CanSave);
        Assert.Equal(UnsavedChangesChoice.Cancel, request.DefaultChoice);
        Assert.EndsWith("# typed after the save\n", vm.RawText, StringComparison.Ordinal);
        Assert.Equal(EditedRaw(harness), OnDisk(harness));

        prompt.Answer = UnsavedChangesChoice.Discard;
        await vm.RestoreFromBackupCommand.ExecuteAsync(null);
        Assert.Equal(harness.Raw, OnDisk(harness));
        Assert.Equal(harness.Raw, vm.RawText);
        Assert.False(vm.HasUnsavedChanges);
    }

    [Fact]
    public async Task Restoring_right_after_a_rejected_save_needs_no_question_because_the_rejected_text_is_the_file()
    {
        using var harness = await DirtyEditorAsync();
        var vm = harness.ViewModel;
        harness.PlantUnrunnableCli();
        var prompt = new CannedPrompt(vm, UnsavedChangesChoice.Cancel);

        await vm.SaveCommand.ExecuteAsync(null);
        Assert.True(vm.ShowRestoreAction);
        Assert.True(vm.HasUnsavedChanges);

        await vm.RestoreFromBackupCommand.ExecuteAsync(null);

        Assert.Empty(prompt.Asked);
        Assert.Equal(harness.Raw, OnDisk(harness));
        Assert.Equal(harness.Raw, vm.RawText);
    }

    // ------------------------------------------------------------------ the file changing behind the editor

    [Fact]
    public async Task A_change_on_disk_reloads_an_editor_with_nothing_to_lose()
    {
        using var harness = await ConfigEditorHarness.LoadAsync();
        var vm = harness.ViewModel;

        harness.WriteConfigExternally(harness.Raw + "# by another tool\n");
        await vm.HandleDiskChangeAsync();

        Assert.Equal(harness.Raw + "# by another tool\n", vm.RawText);
        Assert.False(vm.HasUnsavedChanges);
        Assert.False(vm.ShowExternalChangeNotice);
    }

    [Theory]
    [MemberData(nameof(LineEndings.Both), MemberType = typeof(LineEndings))]
    public async Task A_change_on_disk_never_overwrites_a_buffer_with_edits_it_raises_a_notice_instead(string eol)
    {
        using var harness = await DirtyEditorAsync(eol);
        var vm = harness.ViewModel;
        var typed = vm.RawText;

        harness.WriteConfigExternally(harness.Raw + "# by another tool\n");
        await vm.HandleDiskChangeAsync();

        Assert.Equal(typed, vm.RawText);
        Assert.True(vm.HasUnsavedChanges);
        Assert.True(vm.ShowExternalChangeNotice);
        Assert.True(vm.ShowExternalChangeBanner);
        Assert.Equal(harness.Raw + "# by another tool\n", OnDisk(harness));
    }

    [Fact]
    public async Task Repeated_notifications_for_the_same_change_are_one_notice()
    {
        using var harness = await DirtyEditorAsync();
        var vm = harness.ViewModel;
        var raised = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.ShowExternalChangeNotice))
            {
                raised++;
            }
        };

        harness.WriteConfigExternally(harness.Raw + "# by another tool\n");
        await vm.HandleDiskChangeAsync();
        await vm.HandleDiskChangeAsync();
        await vm.HandleDiskChangeAsync();

        Assert.True(vm.ShowExternalChangeNotice);
        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task Nothing_happens_when_config_yaml_is_what_the_editor_last_read_or_wrote()
    {
        using var harness = await DirtyEditorAsync();
        var vm = harness.ViewModel;
        var typed = vm.RawText;

        // .env edits, duplicate watcher events: the file is unchanged.
        await vm.HandleDiskChangeAsync();
        Assert.False(vm.ShowExternalChangeNotice);

        // The editor's own save moves the file too, and is not "another tool".
        _ = new CannedPrompt(vm, UnsavedChangesChoice.Save);
        await vm.SaveCommand.ExecuteAsync(null);
        vm.RawText += "# typed after the save\n";
        typed = vm.RawText;
        await vm.HandleDiskChangeAsync();

        Assert.Equal(typed, vm.RawText);
        Assert.False(vm.ShowExternalChangeNotice);
    }

    [Fact]
    public async Task Keep_my_edits_silences_that_version_of_the_file_but_not_the_next_one()
    {
        using var harness = await DirtyEditorAsync();
        var vm = harness.ViewModel;
        var typed = vm.RawText;

        harness.WriteConfigExternally(harness.Raw + "# first\n");
        await vm.HandleDiskChangeAsync();
        Assert.True(vm.ShowExternalChangeNotice);

        vm.KeepEditsCommand.Execute(null);
        Assert.False(vm.ShowExternalChangeNotice);
        Assert.False(vm.ShowExternalChangeBanner);

        await vm.HandleDiskChangeAsync();
        Assert.False(vm.ShowExternalChangeNotice);
        Assert.Equal(typed, vm.RawText);

        harness.WriteConfigExternally(harness.Raw + "# second\n");
        await vm.HandleDiskChangeAsync();
        Assert.True(vm.ShowExternalChangeNotice);
        Assert.Equal(typed, vm.RawText);
    }

    [Fact]
    public async Task The_notices_reload_takes_the_files_version_and_drops_the_edits_on_purpose()
    {
        using var harness = await DirtyEditorAsync();
        var vm = harness.ViewModel;
        var prompt = new CannedPrompt(vm, UnsavedChangesChoice.Cancel);

        harness.WriteConfigExternally(harness.Raw + "# by another tool\n");
        await vm.HandleDiskChangeAsync();
        await vm.ReloadDiscardingEditsCommand.ExecuteAsync(null);

        Assert.Empty(prompt.Asked);
        Assert.Equal(harness.Raw + "# by another tool\n", vm.RawText);
        Assert.False(vm.HasUnsavedChanges);
        Assert.False(vm.ShowExternalChangeNotice);
    }

    [Fact]
    public async Task With_a_notice_showing_the_close_prompt_does_not_offer_a_save_the_drift_check_would_refuse()
    {
        using var harness = await DirtyEditorAsync();
        var vm = harness.ViewModel;
        var prompt = new CannedPrompt(vm, UnsavedChangesChoice.Cancel);

        harness.WriteConfigExternally(harness.Raw + "# by another tool\n");
        await vm.HandleDiskChangeAsync();
        Assert.False(await vm.ConfirmCloseAsync());

        var request = Assert.Single(prompt.Asked);
        Assert.False(request.CanSave);
        Assert.Equal(UnsavedChangesChoice.Cancel, request.DefaultChoice);
    }

    [Fact]
    public async Task A_cancelled_question_re_checks_the_file_so_a_change_that_arrived_meanwhile_is_not_lost()
    {
        using var harness = await DirtyEditorAsync();
        var vm = harness.ViewModel;
        vm.UnsavedChangesPrompt = _ =>
        {
            // The file moves while the question is on screen; the watcher's notification is ignored during a question.
            harness.WriteConfigExternally(harness.Raw + "# during the question\n");
            return Task.FromResult(UnsavedChangesChoice.Cancel);
        };

        Assert.False(await vm.ConfirmCloseAsync());

        Assert.True(vm.ShowExternalChangeNotice);
        Assert.Equal(EditedRaw(harness), vm.RawText);
    }

    [Fact]
    public async Task An_edit_that_lands_while_an_automatic_reload_is_reading_the_file_wins_and_a_save_still_reports_the_drift()
    {
        using var harness = await ConfigEditorHarness.LoadAsync();
        var vm = harness.ViewModel;
        var typed = EditedRaw(harness);
        var external = harness.Raw + "# by another tool" + Environment.NewLine;

        // The buffer is clean when the reload starts; the operator types the moment it begins loading.
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.IsLoading) && vm.IsLoading)
            {
                vm.RawText = typed;
            }
        };
        harness.WriteConfigExternally(external);

        await vm.HandleDiskChangeAsync();

        Assert.Equal(typed, vm.RawText);
        Assert.True(vm.HasUnsavedChanges);
        Assert.True(vm.ShowExternalChangeNotice);
        Assert.False(vm.IsLoading);

        // The stood-down reload did not adopt the new file's signature, so saving over it is still refused.
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.True(vm.ShowDriftBanner);
        Assert.Equal(external, OnDisk(harness));
    }

    [Fact]
    public async Task The_shells_config_reloaded_event_drives_the_notice_once_watching_and_stops_after_dispose()
    {
        using var harness = await DirtyEditorAsync();
        var vm = harness.ViewModel;
        vm.WatchDiskChanges();

        harness.WriteConfigExternally(harness.Raw + "# by another tool" + Environment.NewLine);
        harness.Services.ReloadConfig();
        await WaitForAsync(() => vm.ShowExternalChangeNotice);
        Assert.Equal(EditedRaw(harness), vm.RawText);

        // Stop listening, clear the notice by hand, and the next change to the file is not reacted to.
        vm.Dispose();
        vm.KeepEditsCommand.Execute(null);
        Assert.False(vm.ShowExternalChangeNotice);

        harness.WriteConfigExternally(harness.Raw + "# after dispose" + Environment.NewLine);
        harness.Services.ReloadConfig();
        await vm.HandleDiskChangeAsync();

        Assert.False(vm.ShowExternalChangeNotice);
        Assert.Equal(EditedRaw(harness), vm.RawText);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        // The shell raises the event on the UI dispatcher in the app and inline here; wait rather than assume which.
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(50);
        }

        Assert.True(condition(), "The condition was not met within 5 seconds.");
    }

    // ------------------------------------------------------------------ the prompt's own wording and defaults

    [Theory]
    [InlineData(UnsavedChangesContext.Close, false, false, true, UnsavedChangesChoice.Save, "Save and close", "Discard and close")]
    [InlineData(UnsavedChangesContext.Reload, false, false, true, UnsavedChangesChoice.Save, "Save and reload", "Discard and reload")]
    [InlineData(UnsavedChangesContext.Close, true, false, true, UnsavedChangesChoice.Cancel, "Save and close", "Discard and close")]
    [InlineData(UnsavedChangesContext.Reload, true, false, true, UnsavedChangesChoice.Cancel, "Save and reload", "Discard and reload")]
    [InlineData(UnsavedChangesContext.Close, false, true, false, UnsavedChangesChoice.Cancel, null, "Discard and close")]
    [InlineData(UnsavedChangesContext.Reload, true, true, false, UnsavedChangesChoice.Cancel, null, "Discard and reload")]
    [InlineData(UnsavedChangesContext.Restore, false, false, false, UnsavedChangesChoice.Cancel, null, "Discard and restore")]
    [InlineData(UnsavedChangesContext.Restore, true, true, false, UnsavedChangesChoice.Cancel, null, "Discard and restore")]
    public void The_prompt_defaults_to_save_unless_saving_is_unsafe_or_unavailable_and_never_to_discard(
        UnsavedChangesContext context,
        bool invalidYaml,
        bool fileChanged,
        bool canSave,
        UnsavedChangesChoice expectedDefault,
        string? saveLabel,
        string discardLabel)
    {
        var request = UnsavedChangesRequest.Create(context, invalidYaml, fileChanged);

        Assert.Equal(canSave, request.CanSave);
        Assert.Equal(expectedDefault, request.DefaultChoice);
        Assert.Equal(saveLabel, request.SaveLabel);
        Assert.Equal(discardLabel, request.DiscardLabel);
        Assert.NotEqual(UnsavedChangesChoice.Discard, request.DefaultChoice);
        Assert.False(string.IsNullOrWhiteSpace(request.Heading));
        Assert.False(string.IsNullOrWhiteSpace(request.Message));

        // The text only describes Save when Save is on offer.
        Assert.Equal(canSave, request.Message.Contains("Save backs up", StringComparison.Ordinal));
    }

    [Fact]
    public void A_restore_prompt_carries_no_save_warning_even_for_invalid_yaml()
    {
        var request = UnsavedChangesRequest.Create(UnsavedChangesContext.Restore, hasParseError: true, fileChangedOnDisk: false);

        Assert.Null(request.Warning);
    }
}
