using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using static DefenseClaw.App.Tests.TestSupport.PolicyModelTestSupport;

namespace DefenseClaw.App.Tests.Discovery;

/// <summary>
/// AI Discovery's scan, turn on/off and re-detect are off while the data on screen is stale or untrusted (CUST-329, over the house
/// <see cref="CatalogTrust"/> of CUST-283 and CUST-312), with the reason as their tooltip; the installation's own sentence comes first (CUST-308).
/// Refresh is never gated: it is the cure. Only the disk phase of a load runs, and the panel is never activated, so nothing starts a process.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class AiDiscoveryScanTrustTests
{
    private const string State = "ai-discovery-state.0.8.10.json";

    private static bool AllThreeOn(AiDiscoveryPanelViewModel vm) =>
        vm.RunScanCommand.CanExecute(null) && vm.ToggleDiscoveryCommand.CanExecute(null) && vm.RefreshConnectorsCommand.CanExecute(null);

    private static bool AllThreeOff(AiDiscoveryPanelViewModel vm) =>
        !vm.RunScanCommand.CanExecute(null) && !vm.ToggleDiscoveryCommand.CanExecute(null) && !vm.RefreshConnectorsCommand.CanExecute(null);

    [Fact]
    public void A_complete_read_of_an_unmoved_configuration_leaves_the_three_actions_on()
    {
        using var scene = DiscoveryScene.Open(State, DiscoveryScene.DiscoveryOn);

        scene.OnUi(() =>
        {
            var vm = scene.ViewModel;

            Assert.True(vm.Trust.IsTrusted);
            Assert.Null(vm.ChangesBlockedReason);
            Assert.False(vm.HasChangesBlockedReason);
            Assert.True(AllThreeOn(vm));
        });
    }

    [Fact]
    public void A_state_file_that_is_not_there_yet_is_not_a_fault_and_leaves_the_scan_on()
    {
        // The scan is what makes the file; a panel that refused to offer it until the file existed could never get out.
        using var scene = DiscoveryScene.Open(null, DiscoveryScene.DiscoveryOn);

        scene.OnUi(() =>
        {
            Assert.True(scene.ViewModel.Trust.IsTrusted);
            Assert.True(AllThreeOn(scene.ViewModel));
        });
    }

    [Fact]
    public void A_state_file_that_cannot_be_read_is_an_incomplete_read_and_turns_the_three_actions_off_with_the_reason()
    {
        using var scene = DiscoveryScene.Open(null, DiscoveryScene.DiscoveryOn, temp => temp.WriteFile("ai_discovery_state.json", "{ this is not json"));

        scene.OnUi(() =>
        {
            var vm = scene.ViewModel;

            Assert.True(vm.Trust.IsPartial);
            Assert.True(AllThreeOff(vm));
            Assert.StartsWith("Changes are off: discovery was incomplete", vm.ChangesBlockedReason, StringComparison.Ordinal);
            Assert.True(vm.HasChangesBlockedReason);
            Assert.True(vm.RefreshCommand.CanExecute(null));
        });
    }

    [Fact]
    public void A_configuration_that_moved_since_the_read_turns_the_actions_off_and_a_new_read_turns_them_on()
    {
        using var scene = DiscoveryScene.Open(State, DiscoveryScene.DiscoveryOn);
        var edits = new ConfigEdits(scene.Services);

        scene.OnUi(() => Assert.True(AllThreeOn(scene.ViewModel)));

        edits.EditConfig();

        scene.OnUi(() =>
        {
            var vm = scene.ViewModel;

            // The properties never touch the disk; the panel looks when it comes back on screen, when it hears of a reload, and at Confirm.
            Assert.True(vm.Trust.CheckConfig());

            Assert.True(vm.Trust.IsStale);
            Assert.True(AllThreeOff(vm));
            Assert.Equal(CatalogTrust.ConfigChangedReason("this panel was read"), vm.ChangesBlockedReason);
            Assert.True(vm.RefreshCommand.CanExecute(null));
            Assert.Equal(4, vm.ProductCount); // the rows stay
        });

        scene.Reload();

        scene.OnUi(() =>
        {
            Assert.True(scene.ViewModel.Trust.IsTrusted);
            Assert.Null(scene.ViewModel.ChangesBlockedReason);
            Assert.True(AllThreeOn(scene.ViewModel));
        });
    }

    [Fact]
    public void The_installations_sentence_comes_before_the_staleness_reason()
    {
        using var scene = DiscoveryScene.Open(State, DiscoveryScene.DiscoveryOn);
        var edits = new ConfigEdits(scene.Services);
        edits.EditConfig();

        scene.OnUi(() => Assert.True(scene.ViewModel.Trust.CheckConfig()));
        scene.Services.Installation.Replace(TestInstallations.ManagedAt(scene.Temp.Path));

        scene.OnUi(() =>
        {
            var vm = scene.ViewModel;

            Assert.True(vm.Trust.IsStale);
            Assert.Equal(TestInstallations.ManagedReason, vm.ChangesBlockedReason);
            Assert.True(AllThreeOff(vm));
        });
    }

    [Fact]
    public void A_scan_review_left_open_while_the_configuration_moved_is_refused_at_confirm_and_nothing_runs()
    {
        using var scene = DiscoveryScene.Open(State, DiscoveryScene.DiscoveryOn);
        var edits = new ConfigEdits(scene.Services);
        var ran = new List<string>();

        scene.OnUi(() =>
        {
            scene.ViewModel.Review.RunStep = (executable, argv, _) =>
            {
                ran.Add(executable + " " + string.Join(' ', argv));
                return Task.FromResult(Result(0, argv, "ok"));
            };
            scene.ViewModel.RunScanCommand.Execute(null);
            Assert.True(scene.ViewModel.Review.IsOpen);
        });

        edits.EditConfig();

        Task? confirm = null;
        scene.OnUi(() => confirm = scene.ViewModel.Review.ConfirmCommand.ExecuteAsync(null));
        UiThread.WaitFor(() => confirm!.IsCompleted, "the refused confirm");

        scene.OnUi(() =>
        {
            var review = scene.ViewModel.Review;

            Assert.Empty(ran);
            Assert.True(review.IsFinished);
            Assert.Contains(CatalogTrust.ConfigChangedReason("this panel was read"), review.ResultText, StringComparison.Ordinal);
        });
    }
}
