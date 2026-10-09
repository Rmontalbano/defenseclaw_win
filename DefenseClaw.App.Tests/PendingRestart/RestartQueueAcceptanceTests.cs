using System.Reflection;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Gateway.Models;

namespace DefenseClaw.App.Tests.PendingRestart;

/// <summary>
/// The issue's acceptance, end to end across threads (CUST-267): a save in the config editor, or a run the runner finishes on its own thread, queues
/// on the app's queue; the Setup hub - on the UI thread, on screen - shows the banner; it is still there after the operator leaves the page and
/// comes back; and a restart the monitor sees, or Clear, takes it down. Nothing here calls the UI thread on the queue's behalf: the queue marshals
/// <c>Changed</c> itself, which is the one thing the surface tests (which queue from the UI thread) do not cover.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class RestartQueueAcceptanceTests
{
    /// <summary>The monitor's own publish step (private: no poll loop runs in a test), as <c>OverviewScene.Publish</c> reaches it.</summary>
    private static void Publish(AppServices services, GatewaySnapshot snapshot) =>
        _ = typeof(GatewayMonitor).GetMethod("Publish", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(services.Monitor, new object[] { snapshot });

    private static SetupPanelViewModel ActiveSetup(AppServices services) =>
        UiThread.Run(() =>
        {
            var vm = new SetupPanelViewModel(services);
            vm.ShowCards(Array.Empty<WizardDefinition>());
            vm.SetActive(true);
            return vm;
        });

    private static void Settled(SetupPanelViewModel vm)
    {
        UiThread.Run(() => vm.SetActive(false));
        UiThread.WaitFor(() => !vm.Credentials.IsLoading && !vm.IsGuardrailBusy, "the Setup hub's reads finished");
    }

    /// <summary>The readiness checklist's row, as the page has drawn it. It follows the queue's <c>Changed</c>, which the queue posts to the UI thread: wait for it, do not assume it.</summary>
    private static ReadinessRowViewModel RestartRow(SetupPanelViewModel vm) => vm.Readiness.Rows.Single(r => r.Title == "Restart Pending");

    /// <summary>A poll after a restart: the gateway reports a start a moment from now, after anything queued so far.</summary>
    private static GatewaySnapshot Restarted() =>
        OverviewScene.Snapshot(health: new GatewayHealth { StartedAt = DateTimeOffset.UtcNow.AddSeconds(1), UptimeMs = 1_000 }) with
        {
            PolledAt = DateTimeOffset.UtcNow.AddSeconds(2),
        };

    [Fact]
    public async Task A_save_in_the_config_editor_shows_the_banner_across_panel_switches_until_a_restart_or_clear()
    {
        using var harness = await ConfigEditorHarness.LoadAsync();
        var services = harness.Services;
        var vm = ActiveSetup(services);
        try
        {
            UiThread.Run(() => Assert.False(vm.HasRestartPending));

            // A save that changes a value (this continues on the test's thread, not the UI's).
            harness.ViewModel.RawText = harness.Raw.Replace("mode: observe", "mode: enforce", StringComparison.Ordinal);
            await harness.ViewModel.SaveCommand.ExecuteAsync(null);

            UiThread.WaitFor(() => RestartRow(vm).Status == ReadinessStatus.Warn, "the page drawn from the queue after the save");
            UiThread.Run(() =>
            {
                Assert.True(vm.HasRestartPending);
                Assert.StartsWith("config.yaml saved in the config editor (guardrail). Queued ", vm.RestartPendingDetail, StringComparison.Ordinal);

                // The operator goes to the Overview, the Alerts, anywhere, and comes back: the page is rebuilt from the queue.
                vm.SetActive(false);
                vm.SetActive(true);
                Assert.True(vm.HasRestartPending);
                Assert.Equal(ReadinessStatus.Warn, RestartRow(vm).Status);
            });

            // The gateway is restarted by someone else; the next poll reports the new start time.
            Publish(services, Restarted());

            UiThread.WaitFor(() => RestartRow(vm).Status == ReadinessStatus.Pass, "the page drawn from the queue after a restart the monitor saw");
            UiThread.Run(() => Assert.False(vm.HasRestartPending));

            // And the other way out: a second save, then Clear.
            harness.ViewModel.RawText = harness.ViewModel.RawText.Replace("model: gpt-4o", "model: another-model", StringComparison.Ordinal);
            await harness.ViewModel.SaveCommand.ExecuteAsync(null);
            UiThread.WaitFor(() => RestartRow(vm).Status == ReadinessStatus.Warn, "the page drawn from the queue after the second save");

            UiThread.Run(() => vm.ClearRestartCommand.Execute(null));

            Assert.False(services.RestartQueue.IsPending);
            UiThread.Run(() => Assert.False(vm.HasRestartPending));
        }
        finally
        {
            Settled(vm);
        }
    }

    [Fact]
    public async Task A_command_the_runner_finishes_on_its_own_thread_queues_and_the_banner_follows()
    {
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp);
        var vm = ActiveSetup(services);
        try
        {
            // A copy of cmd.exe called defenseclaw.exe (the technique InstallationGateTests uses): a real child that exits 0, in a scratch folder.
            var fake = temp.File("defenseclaw.exe");
            File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), fake);

            var invocation = await services.Cli.RunExecutableAsync(fake, new[] { "/c", "echo", "guardrail", "hilt", "--yes", "--no-restart" });

            Assert.Equal(0, invocation.ExitCode);
            Assert.True(services.RestartQueue.IsPending);
            UiThread.WaitFor(() => RestartRow(vm).Status == ReadinessStatus.Warn, "the page drawn from the queue after a finished run");
            UiThread.Run(() =>
            {
                Assert.True(vm.HasRestartPending);
                Assert.EndsWith("ran with --no-restart", RestartRow(vm).Detail, StringComparison.Ordinal);
            });
        }
        finally
        {
            Settled(vm);
        }
    }
}
