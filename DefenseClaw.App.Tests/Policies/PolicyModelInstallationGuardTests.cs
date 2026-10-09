using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Policy;
using DefenseClaw.Core.Policy.Model;
using static DefenseClaw.App.Tests.TestSupport.PolicyModelTestSupport;

namespace DefenseClaw.App.Tests.Policies;

/// <summary>
/// The pinned runtime's Policies model surface (<see cref="PolicyModelViewModel"/>) honours the installation's read-only guard (CUST-329, over
/// CUST-308): on a managed or invalid installation every kind of change - a posture, a rule-pack switch, a policy activation - is off with the
/// installation's sentence, asking anyway is refused before any validation or command runs, and a review opened while the installation was
/// writable cannot run once it is not. Validation (a read) stays on. The CLI is a script handed to the view-model; no process starts.
/// </summary>
public sealed class PolicyModelInstallationGuardTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly AppServices _services;

    public PolicyModelInstallationGuardTests()
    {
        _services = TestServices.Create(_temp, runtimeProbeRunner: ProbeRunner(Pinned));
    }

    public void Dispose()
    {
        _services.Dispose();
        _temp.Dispose();
    }

    private async Task<(PolicyModelViewModel Vm, ScriptedCli Cli, List<string> Ran)> OpenAsync(string scenario = Connectors)
    {
        var cli = new ScriptedCli(scenario);
        var ran = new List<string>();
        var vm = new PolicyModelViewModel(_services, SevenViewPolicyBackend.Instance, new FixtureData()) { RunCli = cli.Run };
        vm.Review.RunStep = (executable, argv, _) =>
        {
            ran.Add(executable + " " + string.Join(' ', argv));
            return Task.FromResult(Result(0, argv, "ok"));
        };
        await vm.InitializeAsync();
        return (vm, cli, ran);
    }

    private static PolicyActionViewModel Act(PolicyModelViewModel vm, string group, string title) =>
        vm.ActionGroups.Single(g => g.Name == group).Actions.Single(a => a.Title == title);

    private static void Go(PolicyModelViewModel vm, string view) => vm.SelectedNav = vm.Nav.Single(n => n.View == view);

    [Fact]
    public async Task On_an_invalid_installation_every_change_is_off_with_its_sentence_like_on_a_managed_one()
    {
        _services.Installation.Replace(TestInstallations.Invalid());
        var invalidReason = _services.Installation.BlockedReason;
        Assert.NotNull(invalidReason);
        var (vm, _, _) = await OpenAsync();
        vm.SelectedRow = vm.Rows.Single(r => r.Key == "global");

        Assert.Equal(PoliciesState.Loaded, vm.State);
        Assert.False(vm.CanChange);
        Assert.Equal(invalidReason, vm.ChangesBlockedReason);
        var changes = vm.ActionGroups.SelectMany(g => g.Actions).Where(a => !a.IsRead).ToArray();
        Assert.NotEmpty(changes);
        Assert.All(changes, a =>
        {
            Assert.False(a.IsEnabled);
            Assert.Equal(invalidReason, a.ToolTip);
        });
    }

    [Fact]
    public async Task A_rule_pack_switch_asked_for_anyway_is_refused_before_its_validation_runs()
    {
        _services.Installation.Replace(TestInstallations.ManagedAt(_temp.Path));
        var (vm, cli, ran) = await OpenAsync();
        vm.SelectedRow = vm.Rows.Single(r => r.Key == "global");
        var pack = Act(vm, "Rule pack", "strict");
        Assert.False(pack.IsEnabled);
        cli.Calls.Clear();

        await vm.RunActionCommand.ExecuteAsync(pack);

        Assert.False(vm.Review.IsOpen);
        Assert.Equal(TestInstallations.ManagedReason, vm.NoticeMessage);
        Assert.DoesNotContain(cli.Calls, c => c.Contains("validate", StringComparison.Ordinal));
        Assert.Empty(cli.Calls);
        Assert.Empty(ran);
    }

    [Fact]
    public async Task A_policy_activation_asked_for_anyway_is_refused_before_policy_validate_runs()
    {
        _services.Installation.Replace(TestInstallations.ManagedAt(_temp.Path));
        var (vm, cli, ran) = await OpenAsync(Fresh);
        Go(vm, "policies");
        vm.SelectedRow = vm.Rows.Single(r => r.Key == "strict");
        var activate = Act(vm, "Activate", "Activate");
        Assert.False(activate.IsEnabled);
        cli.Calls.Clear();

        await vm.RunActionCommand.ExecuteAsync(activate);

        Assert.False(vm.Review.IsOpen);
        Assert.Equal(TestInstallations.ManagedReason, vm.NoticeMessage);
        Assert.Empty(cli.Calls);
        Assert.Empty(ran);
    }

    [Fact]
    public async Task A_review_opened_while_the_installation_was_writable_cannot_run_once_it_is_not()
    {
        var (vm, cli, ran) = await OpenAsync();
        vm.SelectedRow = vm.Rows.Single(r => r.Key == "global");
        await vm.RunActionCommand.ExecuteAsync(Act(vm, "Mode", "Log only (observe)"));
        Assert.True(vm.Review.IsOpen);

        // Logging only protects less, so the review wants its tick before Confirm is live.
        vm.Review.IsAcknowledged = true;
        cli.Calls.Clear();

        // The live answer is asked again at Confirm: nothing runs, and the refusal says why.
        _services.Installation.Replace(TestInstallations.ManagedAt(_temp.Path));
        await vm.Review.ConfirmCommand.ExecuteAsync(null);

        Assert.Empty(ran);
        Assert.Empty(cli.Mutations);
        Assert.True(vm.Review.IsFinished);
        Assert.Contains(TestInstallations.ManagedReason, vm.Review.ResultText, StringComparison.Ordinal);
    }
}
