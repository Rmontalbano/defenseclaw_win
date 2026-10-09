using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.Guardrail;
using DefenseClaw.App.Tests.Policies;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Policy;
using DefenseClaw.Core.Policy.Model;
using static DefenseClaw.App.Tests.TestSupport.PolicyModelTestSupport;

namespace DefenseClaw.App.Tests.Installation;

/// <summary>
/// The two Policies panels (the pre-model panel and the one for a runtime that has the policy model) and the Guardrail controls on a read-only
/// installation (CUST-308): they read and show as always, validation stays on, and every change is off with the installation's own sentence
/// as the tooltip and as the notice a request gets anyway. The CLI is a script handed to each view-model, so no process starts.
/// </summary>
public sealed class InstallationPolicyTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly AppServices _services;

    public InstallationPolicyTests()
    {
        _services = TestServices.Create(_temp, runtimeProbeRunner: ProbeRunner(Pinned));
    }

    public void Dispose()
    {
        _services.Dispose();
        _temp.Dispose();
    }

    // ------------------------------------------------------------------ the Policies panel (before the policy model)

    private async Task<(PoliciesPanelViewModel Vm, List<string> Calls, List<string> Ran)> OpenPoliciesAsync()
    {
        var calls = new List<string>();
        var ran = new List<string>();
        var vm = new PoliciesPanelViewModel(_services)
        {
            RunCli = (argv, _) =>
            {
                calls.Add(string.Join(' ', argv));
                if (argv.SequenceEqual(new[] { "policy", "list" }))
                {
                    return Task.FromResult(Result(0, argv, PolicyFixtures.ListReport));
                }

                if (argv.Count == 3 && argv[1] == "show")
                {
                    return Task.FromResult(Result(0, argv, PolicyFixtures.Show(argv[2])));
                }

                return argv.SequenceEqual(new[] { "policy", "validate" })
                    ? Task.FromResult(Result(0, argv, "  OK All validations passed."))
                    : throw new InvalidOperationException("a read-only door ran: " + string.Join(' ', argv));
            },
        };
        vm.Review.RunStep = (executable, argv, _) =>
        {
            ran.Add(executable + " " + string.Join(' ', argv));
            return Task.FromResult(Result(0, argv, "ok"));
        };
        await vm.InitializeAsync();
        return (vm, calls, ran);
    }

    private static async Task SelectAsync(PoliciesPanelViewModel vm, string name)
    {
        vm.SelectedRow = vm.Rows.Single(r => r.Name == name);
        for (var i = 0; i < 200 && (vm.IsDetailLoading || vm.Detail is null); i++)
        {
            await Task.Delay(10);
        }

        Assert.NotNull(vm.Detail);
    }

    [Fact]
    public async Task On_a_writable_installation_the_policy_buttons_are_on()
    {
        var (vm, _, _) = await OpenPoliciesAsync();
        await SelectAsync(vm, "team-baseline");

        Assert.True(vm.CanChange);
        Assert.Null(vm.ChangesBlockedReason);
        Assert.True(vm.CanActivate);
        Assert.True(vm.CanEdit);
        Assert.True(vm.CanDelete);
        Assert.True(vm.CanCreate);
        Assert.StartsWith("Validate the policies", vm.ActivateTip, StringComparison.Ordinal);
    }

    [Fact]
    public async Task On_a_read_only_installation_the_policy_buttons_are_off_with_its_sentence_and_validation_still_runs()
    {
        _services.Installation.Replace(TestInstallations.Managed(_temp.Path));
        var (vm, calls, ran) = await OpenPoliciesAsync();
        await SelectAsync(vm, "team-baseline");

        // The list and the details are reads: they are there.
        Assert.Equal(PoliciesState.Loaded, vm.State);
        Assert.Equal(4, vm.Rows.Count);

        Assert.False(vm.CanChange);
        Assert.Equal(TestInstallations.ManagedReason, vm.ChangesBlockedReason);
        Assert.False(vm.CanActivate);
        Assert.False(vm.CanEdit);
        Assert.False(vm.CanDelete);
        Assert.False(vm.CanCreate);
        Assert.Equal(TestInstallations.ManagedReason, vm.ActivateTip);
        Assert.Equal(TestInstallations.ManagedReason, vm.EditTip);
        Assert.Equal(TestInstallations.ManagedReason, vm.DeleteTip);
        Assert.Equal(TestInstallations.ManagedReason, vm.CreateTip);

        // Asking anyway is refused with the same sentence, and nothing starts.
        await vm.ActivateCommand.ExecuteAsync(null);
        Assert.False(vm.Review.IsOpen);
        Assert.Equal("Changes are off", vm.NoticeTitle);
        Assert.Equal(TestInstallations.ManagedReason, vm.NoticeMessage);
        Assert.Empty(ran);

        // Validation is a read, and a read-only installation allows reads.
        await vm.ValidateCommand.ExecuteAsync(null);
        Assert.Contains("policy validate", calls);
        Assert.Equal(PolicyValidation.Passed, vm.Validation);
        Assert.Empty(ran);
    }

    [Fact]
    public async Task The_policies_buttons_follow_the_installation_while_the_panel_is_on_screen()
    {
        var (vm, _, _) = await OpenPoliciesAsync();
        await SelectAsync(vm, "team-baseline");
        var changed = new List<string?>();

        UiThread.Run(() =>
        {
            vm.SetActive(true);
            try
            {
                ((System.ComponentModel.INotifyPropertyChanged)vm).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

                _services.Installation.Replace(TestInstallations.ManagedAt(_temp.Path));

                Assert.Contains(nameof(PoliciesPanelViewModel.CanChange), changed);
                Assert.Contains(nameof(PoliciesPanelViewModel.ChangesBlockedReason), changed);
                Assert.False(vm.CanChange);

                _services.Installation.Replace(TestInstallations.UserDefault());

                Assert.True(vm.CanChange);
                Assert.Null(vm.ChangesBlockedReason);
            }
            finally
            {
                vm.SetActive(false);
            }
        });
    }

    // ------------------------------------------------------------------ the Policies panel on a runtime with the policy model

    private async Task<(PolicyModelViewModel Vm, ScriptedCli Cli, List<string> Ran)> OpenModelAsync(string scenario = Fresh)
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

    [Fact]
    public async Task On_a_writable_installation_the_model_offers_its_changes()
    {
        var (vm, _, _) = await OpenModelAsync(Connectors);
        vm.SelectedRow = vm.Rows.Single(r => r.Key == "global");

        Assert.True(vm.CanChange);
        Assert.Null(vm.ChangesBlockedReason);
        Assert.True(Act(vm, "Mode", "Log only (observe)").IsEnabled);
    }

    [Fact]
    public async Task On_a_read_only_installation_every_change_of_the_model_is_off_with_its_sentence_and_a_validation_still_runs()
    {
        _services.Installation.Replace(TestInstallations.Managed(_temp.Path));
        var (vm, cli, ran) = await OpenModelAsync(Connectors);
        vm.SelectedRow = vm.Rows.Single(r => r.Key == "global");

        // Read and shown, complete and current; only the changes are off.
        Assert.Equal(PoliciesState.Loaded, vm.State);
        Assert.True(vm.Trust.IsTrusted);
        Assert.False(vm.CanChange);
        Assert.Equal(TestInstallations.ManagedReason, vm.ChangesBlockedReason);

        var changes = vm.ActionGroups.SelectMany(g => g.Actions).Where(a => !a.IsRead).ToArray();
        Assert.NotEmpty(changes);
        Assert.All(changes, a =>
        {
            Assert.False(a.IsEnabled);
            Assert.Equal(TestInstallations.ManagedReason, a.ToolTip);
        });

        // Asking anyway is refused with the same sentence, and no command runs.
        cli.Calls.Clear();
        await vm.RunActionCommand.ExecuteAsync(Act(vm, "Mode", "Log only (observe)"));
        Assert.False(vm.Review.IsOpen);
        Assert.Equal("Changes are off", vm.NoticeTitle);
        Assert.Equal(TestInstallations.ManagedReason, vm.NoticeMessage);
        Assert.Empty(cli.Calls);
        Assert.Empty(ran);

        // A validation is a read.
        var validate = Act(vm, "Validate a rule pack", "strict");
        Assert.True(validate.IsRead);
        Assert.True(validate.IsEnabled);
        await vm.RunActionCommand.ExecuteAsync(validate);
        Assert.Equal("Ok", vm.OutputTone);
        Assert.Empty(cli.Mutations);
    }

    [Fact]
    public async Task The_models_buttons_follow_the_installation_while_the_panel_is_on_screen()
    {
        var (vm, _, _) = await OpenModelAsync(Connectors);
        vm.SelectedRow = vm.Rows.Single(r => r.Key == "global");
        var mode = Act(vm, "Mode", "Log only (observe)");
        Assert.True(mode.IsEnabled);

        UiThread.Run(() =>
        {
            vm.SetActive(true);
            try
            {
                _services.Installation.Replace(TestInstallations.ManagedAt(_temp.Path));
                Assert.False(vm.CanChange);
                Assert.False(mode.IsEnabled);
                Assert.Equal(TestInstallations.ManagedReason, mode.ToolTip);

                _services.Installation.Replace(TestInstallations.UserDefault());
                Assert.True(vm.CanChange);
                Assert.True(mode.IsEnabled);
            }
            finally
            {
                vm.SetActive(false);
            }
        });
    }

    // ------------------------------------------------------------------ the Guardrail controls

    private static async Task<(GuardrailControlsViewModel Vm, GuardrailControlsViewModelTests.FakeCli Cli)> OpenControlsAsync(InstallationGuard guard)
    {
        var cli = new GuardrailControlsViewModelTests.FakeCli();
        var vm = new GuardrailControlsViewModel(cli.Run, guard);
        await vm.RefreshAsync();
        return (vm, cli);
    }

    [Fact]
    public async Task The_guardrail_controls_on_a_writable_installation_review_a_change_as_always()
    {
        var (vm, cli) = await OpenControlsAsync(_services.Installation);

        Assert.Null(vm.ChangesBlockedReason);
        Assert.False(vm.HasChangesBlockedReason);
        Assert.True(vm.InstallationAllowsChanges);
        Assert.True(vm.CanChange);

        vm.TurnHiltOnCommand.Execute(null);

        Assert.True(vm.IsReviewOpen);
        Assert.False(vm.Review!.IsBlocked);
        Assert.Empty(cli.Writes);
    }

    [Fact]
    public async Task The_guardrail_controls_on_a_read_only_installation_still_read_and_offer_no_change()
    {
        _services.Installation.Replace(TestInstallations.Managed(_temp.Path));
        var (vm, cli) = await OpenControlsAsync(_services.Installation);

        // The four reads ran, and their answers are on screen.
        Assert.Equal(new[] { "guardrail status", "guardrail hilt", "guardrail block-message", "guardrail judge list" }, cli.Reads);
        Assert.False(vm.HasError);
        Assert.Equal("Blocked by Acme", vm.CurrentBlockMessage);

        Assert.Equal(TestInstallations.ManagedReason, vm.ChangesBlockedReason);
        Assert.True(vm.HasChangesBlockedReason);
        Assert.False(vm.InstallationAllowsChanges);
        Assert.False(vm.CanChange);

        // A change asked for anyway opens nothing and runs nothing.
        vm.TurnHiltOnCommand.Execute(null);
        vm.TurnHiltOffCommand.Execute(null);
        vm.MessageText = "Blocked by Acme Security";
        vm.SetBlockMessageCommand.Execute(null);
        Assert.False(vm.IsReviewOpen);
        Assert.Empty(cli.Writes);
    }

    [Fact]
    public async Task A_guardrail_review_that_is_somehow_blocked_cannot_be_confirmed()
    {
        var (vm, cli) = await OpenControlsAsync(_services.Installation);
        vm.TurnHiltOnCommand.Execute(null);
        Assert.True(vm.IsReviewOpen);

        // The same review, carrying the installation's reason as a surface that guarded it would: the confirm does nothing.
        vm.Review = vm.Review!.GuardedBy(new InstallationGuard(TestInstallations.Managed(_temp.Path)));
        Assert.True(vm.Review.IsBlocked);
        await vm.ConfirmCommand.ExecuteAsync(null);

        Assert.Empty(cli.Writes);
        Assert.True(vm.IsReviewOpen);
    }
}
