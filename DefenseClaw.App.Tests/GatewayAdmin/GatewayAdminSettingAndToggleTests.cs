using System.Text.Json.Nodes;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Settings;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.GatewayAdmin.Contract;

namespace DefenseClaw.App.Tests.GatewayAdmin;

/// <summary>The saved switch, and the view-model the Overview and Settings share (its words, its states, its refusals).</summary>
public sealed class GatewayAdminSettingAndToggleTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    // ------------------------------------------------------------------ the setting

    [Fact]
    public void The_mode_is_off_by_default_and_a_file_that_never_turned_it_on_is_unchanged()
    {
        var path = System.IO.Path.Combine(_temp.Path, "settings.json");
        var store = AppSettingsStore.OpenFresh(path);
        Assert.False(store.Current.Connection.GatewayAdministratorMode);

        _ = store.Update(s => s with { Connection = s.Connection with { CliPathOverride = @"C:\Tools\defenseclaw.exe" } });

        var connection = JsonNode.Parse(File.ReadAllText(path))!["connection"]!.AsObject();
        Assert.False(connection.ContainsKey("gatewayAdministratorMode"));
    }

    [Fact]
    public void The_mode_round_trips_and_turning_it_off_removes_the_key()
    {
        var path = System.IO.Path.Combine(_temp.Path, "settings.json");
        var store = AppSettingsStore.OpenFresh(path);

        _ = store.Update(s => s with { Connection = s.Connection with { GatewayAdministratorMode = true } });
        Assert.True(JsonNode.Parse(File.ReadAllText(path))!["connection"]!["gatewayAdministratorMode"]!.GetValue<bool>());
        Assert.True(AppSettingsStore.OpenFresh(path).Current.Connection.GatewayAdministratorMode);

        _ = store.Update(s => s with { Connection = s.Connection with { GatewayAdministratorMode = false } });
        Assert.False(JsonNode.Parse(File.ReadAllText(path))!["connection"]!.AsObject().ContainsKey("gatewayAdministratorMode"));
        Assert.False(AppSettingsStore.OpenFresh(path).Current.Connection.GatewayAdministratorMode);
    }

    [Fact]
    public void A_value_that_is_not_a_boolean_reads_as_off()
    {
        var path = System.IO.Path.Combine(_temp.Path, "settings.json");
        File.WriteAllText(path, "{\"connection\":{\"gatewayAdministratorMode\":\"yes\"}}");

        Assert.False(AppSettingsStore.OpenFresh(path).Current.Connection.GatewayAdministratorMode);
    }

    // ------------------------------------------------------------------ the shared view-model

    private AppServices Services(AdminRig rig, DefenseClaw.Core.Paths.InstallationContext? installation = null) =>
        AppServices.CreateIsolated(
            TestServices.IsolatedPaths(_temp.Path, installation),
            gatewayAdminFactory: _ => rig.Dependencies,
            adminLayout: rig.LayoutSource,
            adminFileSystem: rig.Fs,
            readerTimeouts: TestServices.ReaderTimeouts);

    [Fact]
    public async Task On_a_runtime_without_the_feature_the_switch_is_off_and_explained()
    {
        var rig = new AdminRig { Supported = false };
        using var services = Services(rig);
        _ = await services.GatewayAdmin.RefreshAsync();
        var vm = new GatewayAdminToggleViewModel(services);

        vm.Apply(services.GatewayAdmin.Status);

        Assert.False(vm.CanToggle);
        Assert.False(vm.IsOn);
        Assert.Equal("Needs DefenseClaw 1.0.", vm.StatusText);
        Assert.Equal("Needs DefenseClaw 1.0.", vm.ToggleTip);
        Assert.False(vm.ShowRepair);
        Assert.False(vm.ShowPrepare);
    }

    [Fact]
    public async Task The_switch_has_the_macs_words_for_windows_and_saves_when_flipped()
    {
        var rig = new AdminRig();
        using var services = Services(rig);
        _ = await services.GatewayAdmin.RefreshAsync();
        var vm = new GatewayAdminToggleViewModel(services);

        Assert.Equal("Run gateway as administrator", GatewayAdminToggleViewModel.Heading);
        Assert.Equal(
            "Runs your installed gateway with administrator rights (one Windows prompt per start). Needed only for runtime monitoring's agent-actions plane.",
            GatewayAdminToggleViewModel.Caption);
        Assert.True(vm.CanToggle);
        Assert.False(vm.IsOn);

        vm.IsOn = true;

        Assert.True(vm.IsOn);
        Assert.Equal(new[] { true }, rig.Saved);
        Assert.Empty(rig.Elevator.Requests);
    }

    [Fact]
    public async Task A_switch_the_account_cannot_have_goes_back_to_off_and_saves_nothing()
    {
        var rig = new AdminRig();
        rig.Account.Kind = AdminAccountKind.StandardUser;
        using var services = Services(rig);
        _ = await services.GatewayAdmin.RefreshAsync();
        var vm = new GatewayAdminToggleViewModel(services);
        var changes = new List<string?>();
        vm.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        Assert.False(vm.CanToggle);
        vm.IsOn = true;

        Assert.False(vm.IsOn);
        Assert.Contains(nameof(GatewayAdminToggleViewModel.IsOn), changes);
        Assert.Empty(rig.Saved);
        Assert.Contains("local administrator", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_switch_that_is_on_can_always_be_turned_off()
    {
        var rig = new AdminRig { ModeOn = true, Supported = false };
        using var services = Services(rig);
        _ = await services.GatewayAdmin.RefreshAsync();
        var vm = new GatewayAdminToggleViewModel(services);

        Assert.True(vm.IsOn);
        Assert.True(vm.CanToggle);

        vm.IsOn = false;

        Assert.False(vm.IsOn);
        Assert.Equal(new[] { false }, rig.Saved);
    }

    [Fact]
    public async Task An_ownership_problem_shows_the_repair_button_and_the_files_even_with_the_mode_off()
    {
        var rig = new AdminRig(helperInstalled: true);
        rig.Fs.AddFile(Path.Combine(AdminRig.Profile, ".defenseclaw", "hook_contract_lock.json"), new byte[] { 1 }, AdminAcl.Administrators);
        using var services = Services(rig);
        _ = await services.GatewayAdmin.RefreshAsync();
        var vm = new GatewayAdminToggleViewModel(services);

        Assert.True(vm.ShowRepair);
        Assert.Equal(GatewayAdminStatus.OwnershipSentence, vm.StatusText);
        Assert.Contains("hook_contract_lock.json", vm.DetailText, StringComparison.Ordinal);
        Assert.True(vm.HasDetail);
        Assert.True(vm.RepairOwnershipCommand.CanExecute(null));
    }

    [Fact]
    public async Task Prepare_is_offered_only_while_the_mode_is_on_and_the_copy_is_missing_or_differs()
    {
        var off = new AdminRig();
        using var offServices = Services(off);
        _ = await offServices.GatewayAdmin.RefreshAsync();
        Assert.False(new GatewayAdminToggleViewModel(offServices).ShowPrepare);

        var on = new AdminRig { ModeOn = true };
        using var onServices = Services(on);
        _ = await onServices.GatewayAdmin.RefreshAsync();
        Assert.True(new GatewayAdminToggleViewModel(onServices).ShowPrepare);

        var ready = new AdminRig(helperInstalled: true, copyInstalled: true) { ModeOn = true };
        using var readyServices = Services(ready);
        _ = await readyServices.GatewayAdmin.RefreshAsync();
        Assert.False(new GatewayAdminToggleViewModel(readyServices).ShowPrepare);
    }

    [Fact]
    public async Task The_buttons_are_off_with_the_installations_sentence_on_a_read_only_installation()
    {
        var rig = new AdminRig(helperInstalled: true);
        rig.Fs.AddFile(Path.Combine(AdminRig.Profile, ".defenseclaw", "gateway.pid"), new byte[] { 1 }, AdminAcl.Administrators);
        var managed = TestInstallations.Managed();
        using var services = Services(rig, managed);
        _ = await services.GatewayAdmin.RefreshAsync();
        var vm = new GatewayAdminToggleViewModel(services);

        Assert.True(vm.ShowRepair);
        Assert.False(vm.RepairOwnershipCommand.CanExecute(null));
        Assert.False(vm.PrepareCopyCommand.CanExecute(null));
        Assert.Equal(managed.BlockedReason, vm.ActionsTip);
    }

    [Fact]
    public async Task While_attached_it_follows_the_service_and_after_detach_it_does_not()
    {
        var rig = new AdminRig();
        using var services = Services(rig);
        _ = await services.GatewayAdmin.RefreshAsync();
        var vm = new GatewayAdminToggleViewModel(services);
        vm.Attach();

        rig.ModeOn = true;
        _ = await services.GatewayAdmin.RefreshAsync();

        // The change is posted to the dispatcher when another test in the process has one running, so wait for it.
        var deadline = DateTime.UtcNow + TestTimeouts.Ceiling;
        while (!vm.IsOn && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.True(vm.IsOn);

        vm.Detach();
        rig.ModeOn = false;
        _ = await services.GatewayAdmin.RefreshAsync();
        await Task.Delay(100);
        Assert.True(vm.IsOn);
    }

    [Fact]
    public void An_isolated_composition_looks_at_nothing_of_the_real_machine()
    {
        using var services = AppServices.CreateIsolated(TestServices.IsolatedPaths(_temp.Path), readerTimeouts: TestServices.ReaderTimeouts);

        var status = services.GatewayAdmin.ReadStatus();

        Assert.False(status.Supported);
        Assert.False(status.IsActive);
        Assert.False(services.GatewayAdmin.TrySetMode(true));
    }
}
