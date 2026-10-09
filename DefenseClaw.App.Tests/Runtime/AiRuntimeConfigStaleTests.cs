using System.Text.Json;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.Govern;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.AiRuntime;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Gateway;

namespace DefenseClaw.App.Tests.Runtime;

/// <summary>
/// Stale-data gating (CUST-312) on the Runtime panel, which already marks a snapshot stale when a read fails (CUST-309) and does it the same way
/// when <c>config.yaml</c> or <c>.env</c> changed after the snapshot was read: the snapshot stays, the STALE banner and the note under the buttons
/// say why, Poll now / Enable / Disable are off, a fresh read restores them, and a change started or confirmed after the edit is refused. The
/// gateway and the CLI are scripts handed to the view-model: no socket opens and no process starts.
/// </summary>
public sealed class AiRuntimeConfigStaleTests : IDisposable
{
    private const string Populated = "ai-usage-runtime.populated.synthetic.json";

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

    private static string Fixture(string relative) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "runtime-95159fd", relative.Replace('/', Path.DirectorySeparatorChar)));

    private static GatewayResult<JsonDocument> Snapshot() => GatewayResult<JsonDocument>.Ok(JsonDocument.Parse(Fixture("rest/" + Populated)));

    private static CliInvocation Done(IReadOnlyList<string> argv, int exit, string output)
    {
        var invocation = InvocationFactory.Create(retainFullOutput: true, argv.ToArray());
        if (output.Length > 0)
        {
            InvocationFactory.Append(invocation, output);
        }

        InvocationFactory.Finish(invocation, exit);
        return invocation;
    }

    private sealed record Scene(AiRuntimePanelViewModel Vm, AppServices Services, ConfigEdits Files, List<string> Ran, Func<int> Reads);

    private async Task<Scene> OpenAsync(Func<Task>? duringRead = null)
    {
        var services = AppServices.CreateIsolated(
            TestServices.IsolatedPaths(_temp.Path),
            claudeSettingsPath: _temp.File("claude-settings.json"),
            runtimeProbeRunner: RuntimeFixtureRunner.For("95159fd"));
        _services.Add(services);
        _ = await services.Runtime.RefreshAsync();

        var reads = 0;
        var ran = new List<string>();
        var vm = new AiRuntimePanelViewModel(services)
        {
            ReadSnapshot = async _ =>
            {
                reads++;
                if (duringRead is not null)
                {
                    await duringRead();
                }

                return Snapshot();
            },
            RunPermissionsRead = (argv, _) => Task.FromResult(Done(argv, 0, Fixture("cli/agent-discovery-runtime-permissions.windows.synthetic.json"))),
        };
        vm.Review.RunStep = (executable, argv, _) =>
        {
            ran.Add(executable + " " + string.Join(' ', argv));
            return Task.FromResult(Done(argv, 0, "ok"));
        };
        await vm.InitializeAsync();
        return new Scene(vm, services, new ConfigEdits(services), ran, () => reads);
    }

    [Fact]
    public async Task A_config_reload_after_the_read_marks_the_snapshot_stale_keeps_it_and_turns_the_actions_off_and_a_fresh_read_restores_them()
    {
        var scene = await OpenAsync();
        var vm = scene.Vm;
        vm.SetActive(true);
        try
        {
            Assert.False(vm.IsStale);
            Assert.True(vm.CanPollNow && vm.CanEnable && vm.CanDisable);
            var rows = vm.Rows.Select(r => r.Id).ToArray();
            var reads = scene.Reads();

            scene.Files.EditConfig();
            scene.Files.Reload();

            Assert.True(vm.IsStale);
            Assert.Equal(AiRuntimePanelViewModel.ConfigMovedReason, vm.StaleReason);
            Assert.StartsWith("STALE", vm.StaleBanner, StringComparison.Ordinal);
            Assert.Contains(AiRuntimePanelViewModel.ConfigMovedReason, vm.StaleBanner, StringComparison.Ordinal);
            Assert.Equal("Stale", vm.CoverageBadgeText);

            // It is kept, as a stale snapshot is, and what it cannot do any more is authorize a change.
            Assert.Equal(AiRuntimeState.Ready, vm.State);
            Assert.Equal(rows, vm.Rows.Select(r => r.Id).ToArray());
            Assert.False(vm.CanPollNow || vm.CanEnable || vm.CanDisable);
            Assert.Contains("stale", vm.PollBlockedReason, StringComparison.Ordinal);
            Assert.Contains("config.yaml or .env file changed after this snapshot was read", vm.PollBlockedReason, StringComparison.Ordinal);
            Assert.Contains("refresh it first", vm.ActionsNote, StringComparison.Ordinal);
            Assert.Equal(reads, scene.Reads()); // hearing about it reads nothing

            await vm.RefreshCommand.ExecuteAsync(null);

            Assert.False(vm.IsStale);
            Assert.Equal(string.Empty, vm.StaleBanner);
            Assert.True(vm.CanPollNow && vm.CanEnable && vm.CanDisable);
            Assert.Equal(reads + 1, scene.Reads());
        }
        finally
        {
            vm.SetActive(false);
        }
    }

    [Fact]
    public async Task A_change_to_env_alone_does_the_same()
    {
        var scene = await OpenAsync();
        scene.Vm.SetActive(true);
        try
        {
            scene.Files.EditEnv();
            scene.Files.Reload();

            Assert.True(scene.Vm.IsStale);
            Assert.Equal(AiRuntimePanelViewModel.ConfigMovedReason, scene.Vm.StaleReason);
        }
        finally
        {
            scene.Vm.SetActive(false);
        }
    }

    [Fact]
    public async Task A_reload_that_changes_nothing_the_read_saw_changes_nothing()
    {
        var scene = await OpenAsync();
        scene.Vm.SetActive(true);
        try
        {
            scene.Files.Reload();

            Assert.False(scene.Vm.IsStale);
            Assert.True(scene.Vm.CanPollNow);
        }
        finally
        {
            scene.Vm.SetActive(false);
        }
    }

    [Fact]
    public async Task A_snapshot_read_after_the_edit_is_not_stale_when_the_watcher_speaks_late()
    {
        var scene = await OpenAsync();
        scene.Vm.SetActive(true);
        try
        {
            scene.Files.EditConfig();                                 // a change this panel made (Enable, Disable) ...
            await scene.Vm.RefreshCommand.ExecuteAsync(null);         // ... and the read straight after it ...
            scene.Files.Reload();                                     // ... then the watcher's notification for that same edit

            Assert.False(scene.Vm.IsStale);
            Assert.True(scene.Vm.CanPollNow);
        }
        finally
        {
            scene.Vm.SetActive(false);
        }
    }

    [Fact]
    public async Task Files_that_moved_while_a_read_was_running_make_the_snapshot_it_produced_stale()
    {
        ConfigEdits? files = null;
        var edit = false;
        var scene = await OpenAsync(() =>
        {
            if (edit)
            {
                files!.EditConfig();
            }

            return Task.CompletedTask;
        });
        files = scene.Files;
        Assert.False(scene.Vm.IsStale);

        edit = true;
        await scene.Vm.RefreshCommand.ExecuteAsync(null); // config.yaml is rewritten while this read is in flight

        Assert.True(scene.Vm.IsStale);
        Assert.Equal(AiRuntimePanelViewModel.ConfigMovedReason, scene.Vm.StaleReason);
        Assert.Equal(AiRuntimeState.Ready, scene.Vm.State);

        edit = false;
        await scene.Vm.RefreshCommand.ExecuteAsync(null);
        Assert.False(scene.Vm.IsStale);
    }

    [Fact]
    public async Task A_panel_that_was_away_finds_a_change_made_meanwhile_when_it_comes_back_and_reads_again()
    {
        var scene = await OpenAsync();
        scene.Vm.SetActive(true);
        scene.Vm.SetActive(false);
        var reads = scene.Reads();

        scene.Files.EditConfig();
        scene.Files.Reload(); // nobody is listening
        Assert.False(scene.Vm.IsStale);

        scene.Vm.SetActive(true);
        try
        {
            UiThreadless.WaitFor(() => scene.Reads() == reads + 1 && !scene.Vm.IsLoading);
            Assert.False(scene.Vm.IsStale);
            Assert.True(scene.Vm.CanPollNow);
        }
        finally
        {
            scene.Vm.SetActive(false);
        }
    }

    [Fact]
    public async Task The_panel_listens_to_config_reloads_only_while_it_is_on_screen()
    {
        var scene = await OpenAsync();
        var baseline = ConfigEdits.Subscribers(scene.Services);

        scene.Vm.SetActive(true);
        Assert.Equal(baseline + 1, ConfigEdits.Subscribers(scene.Services));

        scene.Vm.SetActive(false);
        Assert.Equal(baseline, ConfigEdits.Subscribers(scene.Services));
    }

    [Fact]
    public async Task A_change_started_after_an_edit_nobody_has_heard_about_is_refused_before_a_review_opens()
    {
        var scene = await OpenAsync();
        Assert.True(scene.Vm.CanPollNow);

        scene.Files.EditConfig(); // the buttons have not been told

        scene.Vm.PollNowCommand.Execute(null);

        Assert.False(scene.Vm.Review.IsOpen);
        Assert.True(scene.Vm.IsStale);
        Assert.False(scene.Vm.CanPollNow);
        Assert.Empty(scene.Ran);
    }

    [Fact]
    public async Task A_review_confirmed_after_an_edit_does_not_run_and_the_refusal_is_in_activity()
    {
        var scene = await OpenAsync();
        var vm = scene.Vm;
        vm.PollNowCommand.Execute(null);
        Assert.True(vm.Review.IsOpen);

        scene.Files.EditEnv(); // the operator reads the review; meanwhile .env changes
        await vm.Review.ConfirmCommand.ExecuteAsync(null);

        Assert.Empty(scene.Ran);
        Assert.True(vm.Review.IsFinished);
        Assert.StartsWith("Not run.", vm.Review.ResultText, StringComparison.Ordinal);
        Assert.Contains("stale", vm.Review.ResultText, StringComparison.Ordinal);
        Assert.True(vm.IsStale);
        Assert.False(vm.CanPollNow);

        var entry = Assert.Single(scene.Services.Cli.Activity);
        Assert.Equal(AiRuntimeCommands.PollNow, entry.Argv);
        Assert.Null(entry.ExitCode);
        Assert.StartsWith(CliRunner.RefusedPrefix, entry.FailureReason, StringComparison.Ordinal);

        // Close it, read again, and the same poll is offered and runs.
        vm.Review.DismissCommand.Execute(null);
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.PollNowCommand.Execute(null);
        await vm.Review.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal("defenseclaw " + string.Join(' ', AiRuntimeCommands.PollNow), Assert.Single(scene.Ran));
    }
}
