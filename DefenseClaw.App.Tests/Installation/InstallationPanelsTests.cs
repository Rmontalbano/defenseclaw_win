using System.Text.Json;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.Discovery;
using DefenseClaw.App.Tests.Redaction;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.ViewModels.Redaction;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Gateway;
using DefenseClaw.Core.Paths;
using DefenseClaw.Core.Redaction;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.Tests.Installation;

/// <summary>
/// AI Discovery, the Runtime panel, the redaction window, Alerts and Inventory on a read-only installation (CUST-308): each keeps reading and
/// showing, and each control that changes something is off with the installation's own sentence - or, for the two that preview first, the preview
/// still works and only the apply is off. Every CLI and gateway answer is a script handed to the view-model, so no process starts and no socket
/// opens; every name is synthetic.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class InstallationPanelsTests : IDisposable
{
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

    private AppServices Create(InstallationContext? installation = null, RuntimeProbeRunner? probe = null)
    {
        var services = TestServices.Create(_temp, runtimeProbeRunner: probe, installation: installation);
        _services.Add(services);
        return services;
    }

    // ------------------------------------------------------------------ AI Discovery

    [Fact]
    public void AI_Discovery_on_a_writable_installation_offers_its_scan_and_its_switch()
    {
        using var scene = DiscoveryScene.Open("ai-discovery-state.0.8.10.json", DiscoveryScene.DiscoveryOn);

        scene.OnUi(() =>
        {
            Assert.Null(scene.ViewModel.InstallationBlockedReason);
            Assert.True(scene.ViewModel.RunScanCommand.CanExecute(null));
            Assert.True(scene.ViewModel.RefreshConnectorsCommand.CanExecute(null));
            Assert.True(scene.ViewModel.ToggleDiscoveryCommand.CanExecute(null));
            Assert.True(scene.ViewModel.AddConnectorCommand.CanExecute(null));
        });
    }

    [Fact]
    public void AI_Discovery_on_a_read_only_installation_still_shows_what_it_found_and_turns_every_change_off()
    {
        using var scene = DiscoveryScene.Open("ai-discovery-state.0.8.10.json", DiscoveryScene.DiscoveryOn);
        scene.Services.Installation.Replace(TestInstallations.ManagedAt(scene.Temp.Path));

        scene.OnUi(() =>
        {
            var vm = scene.ViewModel;

            // What it read from disk is still there.
            Assert.Equal(4, vm.ProductCount);
            Assert.Equal(3, vm.ModelCount);

            // The scan, the connector refresh, the enable/disable switch and a row's Add change DefenseClaw, so they are off.
            Assert.Equal(TestInstallations.ManagedReason, vm.InstallationBlockedReason);
            Assert.False(vm.RunScanCommand.CanExecute(null));
            Assert.False(vm.RefreshConnectorsCommand.CanExecute(null));
            Assert.False(vm.ToggleDiscoveryCommand.CanExecute(null));
            Assert.False(vm.AddConnectorCommand.CanExecute(null));
        });
    }

    [Fact]
    public void An_AI_Discovery_scan_started_anyway_opens_a_review_that_cannot_be_confirmed()
    {
        using var scene = DiscoveryScene.Open("ai-discovery-state.0.8.10.json", DiscoveryScene.DiscoveryOn);
        scene.Services.Installation.Replace(TestInstallations.ManagedAt(scene.Temp.Path));

        scene.OnUi(() =>
        {
            var vm = scene.ViewModel;

            vm.RunScanCommand.Execute(null);

            Assert.True(vm.Review.IsOpen);
            Assert.True(vm.Review.CommandReview!.IsBlocked);
            Assert.Equal(TestInstallations.ManagedReason, vm.Review.CommandReview.BlockedReason);
            Assert.False(vm.Review.ConfirmCommand.CanExecute(null));
            Assert.Empty(scene.Services.Cli.Activity);
        });
    }

    // ------------------------------------------------------------------ the Runtime panel

    private const string Populated = "ai-usage-runtime.populated.synthetic.json";

    private static string Rest(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "runtime-95159fd", "rest", name));

    private static string PermissionsJson() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "runtime-95159fd", "cli", "agent-discovery-runtime-permissions.windows.synthetic.json"));

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

    private async Task<(AiRuntimePanelViewModel Vm, Func<int> Reads)> OpenRuntimeAsync(InstallationContext? installation)
    {
        var services = Create(installation, RuntimeFixtureRunner.For("95159fd"));
        _ = await services.Runtime.RefreshAsync();
        var reads = 0;
        var vm = new AiRuntimePanelViewModel(services)
        {
            ReadSnapshot = _ =>
            {
                reads++;
                return Task.FromResult(GatewayResult<JsonDocument>.Ok(JsonDocument.Parse(Rest(Populated))));
            },
            RunPermissionsRead = (argv, _) => Task.FromResult(Done(argv, 0, PermissionsJson())),
        };
        await vm.InitializeAsync();
        return (vm, () => reads);
    }

    [Fact]
    public async Task The_runtime_panel_on_a_writable_installation_offers_poll_and_disable_for_enabled_planes()
    {
        var (vm, _) = await OpenRuntimeAsync(null);

        Assert.Null(vm.ReadOnlyReason);
        Assert.True(vm.CanPollNow);
        Assert.True(vm.CanEnable);
        Assert.True(vm.CanDisable);
    }

    [Fact]
    public async Task The_runtime_panel_on_a_read_only_installation_keeps_reading_and_turns_every_action_off_with_the_sentence_said_once()
    {
        var (vm, reads) = await OpenRuntimeAsync(TestInstallations.ManagedAt(_temp.Path));

        // The snapshot was read and is on screen.
        Assert.Equal(1, reads());
        Assert.False(vm.IsStale);
        Assert.NotEqual(AiRuntimeState.Unsupported, vm.State);

        Assert.Equal(TestInstallations.ManagedReason, vm.ReadOnlyReason);
        Assert.Equal(TestInstallations.ManagedReason, vm.PollBlockedReason);
        Assert.Equal(TestInstallations.ManagedReason, vm.EnableBlockedReason);
        Assert.Equal(TestInstallations.ManagedReason, vm.DisableBlockedReason);
        Assert.False(vm.CanPollNow);
        Assert.False(vm.CanEnable);
        Assert.False(vm.CanDisable);
        Assert.True(vm.HasActionsNote);
        Assert.Equal(TestInstallations.ManagedReason, vm.ActionsNote);

        // Asking anyway opens nothing, and a refresh (a read) still works.
        vm.PollNowCommand.Execute(null);
        vm.EnablePlanesCommand.Execute(null);
        vm.DisablePlanesCommand.Execute(null);
        Assert.False(vm.Review.IsOpen);

        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(2, reads());
    }

    // ------------------------------------------------------------------ the redaction window

    private sealed record RedactionHarness(RedactionViewModel Vm, FakeRedactionCli Cli);

    private async Task<RedactionHarness> OpenRedactionAsync(InstallationContext? installation)
    {
        var services = Create(installation);
        var cli = new FakeRedactionCli();
        var vm = new RedactionViewModel(services)
        {
            RunCli = cli.Run,
            Gate = () => GateDecision.Open,
        };
        vm.Review.RunStep = cli.Step;
        await vm.RefreshAsync();
        return new RedactionHarness(vm, cli);
    }

    [Fact]
    public async Task The_redaction_window_on_a_writable_installation_offers_an_apply()
    {
        var h = await OpenRedactionAsync(null);

        Assert.True(h.Vm.CanQuickApply);
        Assert.True(h.Vm.CanQuickPreview);
        Assert.False(h.Vm.HasInstallationBlock);
        Assert.Null(h.Vm.InstallationBlockedReason);
        Assert.Equal(string.Empty, h.Vm.ChangesBlockedReason);
    }

    [Fact]
    public async Task The_redaction_window_on_a_read_only_installation_reads_and_previews_but_cannot_apply()
    {
        var h = await OpenRedactionAsync(TestInstallations.ManagedAt(_temp.Path));
        var vm = h.Vm;

        // The policy was read and is current: only the installation holds changes back.
        Assert.True(vm.HasStatus);
        Assert.True(vm.StatusIsCurrent);
        Assert.Equal(14, vm.BucketRows.Count);

        Assert.True(vm.HasInstallationBlock);
        Assert.Equal(TestInstallations.ManagedReason, vm.InstallationBlockedReason);
        Assert.Equal(TestInstallations.ManagedReason, vm.ChangesBlockedReason);
        Assert.False(vm.CanQuickApply);
        Assert.False(vm.CanApplyPreview);
        Assert.True(vm.CanQuickPreview);

        // A preview (--dry-run) writes nothing, so it runs and shows its result.
        vm.QuickProfile = "sensitive";
        await vm.PreviewQuickCommand.ExecuteAsync(null);
        Assert.Contains(h.Cli.Ran, RedactionArgv.IsPreview);
        Assert.NotNull(vm.Outcome);

        // An apply is refused before anything is previewed for it or reviewed, with the same sentence; nothing is applied.
        h.Cli.Ran.Clear();
        await vm.ApplyQuickCommand.ExecuteAsync(null);
        Assert.False(vm.Review.IsOpen);
        Assert.Equal("Changes are off", vm.NoticeTitle);
        Assert.Equal(TestInstallations.ManagedReason, vm.NoticeMessage);
        Assert.Empty(h.Cli.Ran);
        Assert.Empty(h.Cli.Applied);
    }

    // ------------------------------------------------------------------ Alerts

    private static CliInvocation AlertsPreview(IReadOnlyList<string> argv)
    {
        var invocation = InvocationFactory.Create(false, argv.ToArray());
        InvocationFactory.Append(invocation, "Preview: 3 alert(s) matched; digest=sha256:v1:0123456789abcdef");
        InvocationFactory.Append(invocation, "  OK Dry run complete; no alerts were changed.");
        InvocationFactory.Finish(invocation, 0);
        return invocation;
    }

    [Fact]
    public async Task Alerts_on_a_read_only_installation_still_preview_what_would_match_but_cannot_apply_it()
    {
        var services = Create(TestInstallations.ManagedAt(_temp.Path));
        var calls = new List<string[]>();

        await UiThread.Run(async () =>
        {
            var vm = new AlertsPanelViewModel(services)
            {
                RunCli = (argv, _) =>
                {
                    calls.Add(argv.ToArray());
                    return Task.FromResult(AlertsPreview(argv));
                },
                AfterApply = () => Task.CompletedTask,
            };

            // The note is there before anything is opened, and the dry run (a read) is allowed.
            Assert.Equal("State-changing actions disabled: " + TestInstallations.ManagedReason, vm.ReviewBlockedText);
            Assert.True(vm.HasReviewBlockedText);
            Assert.True(vm.OpenAcknowledgeCommand.CanExecute(null));
            await vm.OpenAcknowledgeCommand.ExecuteAsync(null);

            var preview = Assert.Single(calls);
            Assert.Contains("--dry-run", preview);
            Assert.True(vm.IsReviewOpen);
            Assert.True(vm.PreviewSucceeded);
            Assert.Equal(3, vm.PreviewMatched);

            // The preview matched, and the apply is still off: the button, and a confirm by any other route.
            Assert.False(vm.CanConfirmReview);
            Assert.Equal(TestInstallations.ManagedReason, vm.InstallationBlockedReason);
            await vm.ConfirmReviewCommand.ExecuteAsync(null);
            Assert.Single(calls);
            Assert.DoesNotContain(calls, call => call.Contains("--yes"));
            Assert.Empty(services.Cli.Activity);
        });
    }

    [Fact]
    public async Task Alerts_on_a_writable_installation_have_no_note_and_confirm_what_they_previewed()
    {
        var services = Create();
        var calls = new List<string[]>();

        await UiThread.Run(async () =>
        {
            var vm = new AlertsPanelViewModel(services)
            {
                RunCli = (argv, _) =>
                {
                    calls.Add(argv.ToArray());
                    return Task.FromResult(AlertsPreview(argv));
                },
                AfterApply = () => Task.CompletedTask,
            };

            Assert.Null(vm.ReviewBlockedText);
            Assert.False(vm.HasReviewBlockedText);
            await vm.OpenAcknowledgeCommand.ExecuteAsync(null);

            Assert.True(vm.CanConfirmReview);
            await vm.ConfirmReviewCommand.ExecuteAsync(null);
            Assert.Equal(2, calls.Count);
            Assert.Contains("--yes", calls[1]);
        });
    }

    // ------------------------------------------------------------------ Inventory

    [Fact]
    public void Inventory_generates_an_AI_BOM_only_on_a_writable_installation_because_a_run_records_a_scan_event()
    {
        var writable = Create();
        var managed = Create(TestInstallations.ManagedAt(_temp.Path));

        UiThread.Run(() =>
        {
            var open = new InventoryPanelViewModel(writable);
            Assert.True(open.GenerateAiBomCommand.CanExecute(null));
            Assert.Null(open.InstallationBlockedReason);

            var closed = new InventoryPanelViewModel(managed);
            Assert.False(closed.GenerateAiBomCommand.CanExecute(null));
            Assert.Equal(TestInstallations.ManagedReason, closed.InstallationBlockedReason);

            // Skipping the button still gets a review that cannot be confirmed.
            closed.GenerateAiBomCommand.Execute(null);
            Assert.True(closed.Review.IsOpen);
            Assert.True(closed.Review.CommandReview!.IsBlocked);
            Assert.False(closed.Review.ConfirmCommand.CanExecute(null));
        });
        Assert.Empty(managed.Cli.Activity);
    }
}
