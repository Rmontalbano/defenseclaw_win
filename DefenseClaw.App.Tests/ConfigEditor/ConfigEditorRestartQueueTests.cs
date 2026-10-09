using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels.ConfigEditor;

namespace DefenseClaw.App.Tests.ConfigEditor;

/// <summary>
/// The config editor and the app-wide restart queue (CUST-267): a save that changes a value joins the queue the Setup hub and the Overview show,
/// beside the editor's own transient bar (which these tests leave as it was); a save that changes only the file, one the CLI rejected and one the
/// installation refused do not. Everything runs on temp copies through <see cref="ConfigEditorHarness"/>; the queue is the harness composition's own.
/// </summary>
public sealed class ConfigEditorRestartQueueTests
{
    private static string Enforce(ConfigEditorHarness harness) =>
        harness.Raw.Replace("mode: observe", "mode: enforce", StringComparison.Ordinal);

    [Fact]
    public async Task A_save_that_changes_a_value_queues_a_line_naming_the_section_beside_the_editors_own_bar()
    {
        using var harness = await ConfigEditorHarness.LoadAsync();
        var vm = harness.ViewModel;
        var queue = harness.Services.RestartQueue;
        Assert.False(queue.IsPending);

        vm.RawText = Enforce(harness);
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.True(queue.IsPending);
        Assert.Equal("config.yaml saved in the config editor (guardrail)", queue.Reason);
        Assert.True(vm.ShowRestartPrompt);

        // The editor's bar goes with the next edit or "Not now"; the queue is the app's, and neither touches it.
        vm.DismissRestartPromptCommand.Execute(null);
        Assert.False(vm.ShowRestartPrompt);
        Assert.True(queue.IsPending);
    }

    [Fact]
    public async Task The_queue_is_kept_when_the_editor_window_goes_away()
    {
        using var harness = await ConfigEditorHarness.LoadAsync();
        harness.ViewModel.RawText = Enforce(harness);
        await harness.ViewModel.SaveCommand.ExecuteAsync(null);

        harness.ViewModel.Dispose();

        Assert.True(harness.Services.RestartQueue.IsPending);
    }

    [Fact]
    public async Task A_save_that_moves_only_a_comment_or_spacing_changes_nothing_the_gateway_reads_and_queues_nothing()
    {
        using var harness = await ConfigEditorHarness.LoadAsync();
        var vm = harness.ViewModel;

        vm.RawText = "# my notes\n" + harness.Raw.Replace("host: 127.0.0.1", "host: 127.0.0.1   # loopback", StringComparison.Ordinal);
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.False(vm.SaveResultIsError);
        Assert.False(harness.Services.RestartQueue.IsPending);
    }

    [Fact]
    public async Task Two_saves_of_different_sections_are_two_lines_and_the_same_section_again_is_one()
    {
        using var harness = await ConfigEditorHarness.LoadAsync();
        var vm = harness.ViewModel;
        var queue = harness.Services.RestartQueue;

        vm.RawText = Enforce(harness);
        await vm.SaveCommand.ExecuteAsync(null);
        vm.RawText = vm.RawText.Replace("model: gpt-4o", "model: another-model", StringComparison.Ordinal);
        await vm.SaveCommand.ExecuteAsync(null);
        vm.RawText = vm.RawText.Replace("model: another-model", "model: a-third-model", StringComparison.Ordinal);
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(
            "config.yaml saved in the config editor (guardrail); config.yaml saved in the config editor (llm)",
            queue.Reason);
        Assert.Equal(2, queue.Entries.Count);
    }

    [Fact]
    public async Task A_save_the_cli_rejected_is_not_a_change_the_gateway_will_read_and_queues_nothing()
    {
        using var harness = await ConfigEditorHarness.LoadAsync();
        harness.PlantUnrunnableCli();
        var vm = harness.ViewModel;

        vm.RawText = Enforce(harness);
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.True(vm.SaveResultIsError);
        Assert.False(harness.Services.RestartQueue.IsPending);
    }

    [Fact]
    public async Task A_save_the_installation_refuses_writes_nothing_and_queues_nothing()
    {
        using var harness = await ConfigEditorHarness.LoadAsync();
        var vm = harness.ViewModel;
        harness.Services.Installation.Replace(TestInstallations.ManagedAt(harness.Services.Paths.DataDirectory));

        vm.RawText = Enforce(harness);

        Assert.False(vm.SaveCommand.CanExecute(null));
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.False(harness.Services.RestartQueue.IsPending);
        Assert.Equal(harness.Raw, File.ReadAllText(harness.ConfigPath));
    }

    [Fact]
    public async Task Restoring_after_a_rejected_save_queues_nothing_because_the_file_goes_back_to_what_the_gateway_has()
    {
        using var harness = await ConfigEditorHarness.LoadAsync();
        harness.PlantUnrunnableCli();
        var vm = harness.ViewModel;
        vm.UnsavedChangesPrompt = _ => Task.FromResult(UnsavedChangesChoice.Discard);

        vm.RawText = Enforce(harness);
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.True(vm.SaveResultIsError);

        await vm.RestoreFromBackupCommand.ExecuteAsync(null);

        Assert.Equal(harness.Raw, File.ReadAllText(harness.ConfigPath));
        Assert.False(harness.Services.RestartQueue.IsPending);
    }

    [Fact]
    public async Task Restoring_a_backup_over_a_file_the_gateway_may_be_running_queues_a_line_that_says_so()
    {
        using var harness = await ConfigEditorHarness.LoadAsync();
        var vm = harness.ViewModel;
        var queue = harness.Services.RestartQueue;

        vm.RawText = Enforce(harness);
        await vm.SaveCommand.ExecuteAsync(null);

        // The operator restarted the gateway (it has the enforcing file), then restores the backup of the file before.
        Assert.True(queue.Clear());
        await vm.RestoreFromBackupCommand.ExecuteAsync(null);

        Assert.Equal(harness.Raw, File.ReadAllText(harness.ConfigPath));
        Assert.Equal("config.yaml restored from a backup in the config editor (guardrail)", queue.Reason);
    }
}
