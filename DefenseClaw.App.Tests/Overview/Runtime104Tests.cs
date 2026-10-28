using DefenseClaw.App.Services;
using DefenseClaw.App.Services.FirstRun;
using DefenseClaw.App.Services.Updates;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.ViewModels.ConfigEditor;
using DefenseClaw.Core.Gateway;
using DefenseClaw.Core.Install;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.Tests.Overview;

/// <summary>
/// DefenseClaw 1.0.4 in the app (CUST-373), synthetic throughout: the status read (<c>removed_connector_cleanup</c>, a null sidecar), the Overview rows for the
/// removed connectors, a leftover install lock and the new fail-mode rule, the connector lists, the config editor's fail-mode fields and the installer's
/// retry hint. An older runtime, and one that has not answered, must look as before.
/// </summary>
public sealed class Runtime104Tests : IDisposable
{
    private const string ObserveClosedConfig =
        "guardrail:\n  enabled: true\n  connector: claudecode\n  connectors:\n    claudecode:\n      mode: observe\n      hook_fail_mode: closed\n";

    private readonly TempDirectory _temp = new();
    private AppServices? _services;

    public void Dispose()
    {
        _services?.Dispose();
        _temp.Dispose();
    }

    /// <summary>A runtime that answers its version and one help screen, so the probe reads it as <paramref name="version"/>.</summary>
    private static RuntimeProbeRunner Runtime(string version) => (args, _) => Task.FromResult(
        args.Contains("--version-json")
            ? RuntimeProbeOutput.Ok("{\"name\":\"defenseclaw-cli\",\"version\":\"" + version + "\",\"schema_version\":1}")
            : RuntimeProbeOutput.Ok("Usage: defenseclaw [OPTIONS] COMMAND [ARGS]...\n\nCommands:\n  setup  Configure.\n"));

    private async Task<OverviewPanelViewModel> PanelAsync(string? version, string? config = ObserveClosedConfig)
    {
        _services = TestServices.Create(_temp, config, version is null ? null : Runtime(version));
        if (version is not null)
        {
            _ = await _services.Runtime.RefreshAsync(force: true);
        }

        return new OverviewPanelViewModel(_services);
    }

    private static GatewaySnapshot Snapshot() => new()
    {
        State = AppGatewayState.Running,
        Install = InstallState.Running,
        Detail = "ok",
        PolledAt = DateTimeOffset.UtcNow,
    };

    private const string CleanupStatus =
        "{\"environment\":\"dev\",\"sidecar\":{\"running\":true},\"removed_connector_cleanup\":[" +
        "{\"connector\":\"openhands\",\"label\":\"OpenHands\",\"items\":[" +
        "{\"path\":\"C:\\\\Users\\\\operator\\\\.openhands\\\\hooks.json\",\"what\":\"OpenHands still runs DefenseClaw's hook from here; it blocks every call\"," +
        "\"step\":\"remove every hook whose command runs openhands-hook.sh from this file, then restart OpenHands\"}," +
        "{\"path\":\"C:\\\\Users\\\\operator\\\\.defenseclaw\\\\connector_backups\\\\openhands\",\"what\":\"the only copy of the files as they were\",\"step\":\"copy back what you need\"}]}," +
        "{\"connector\":\"omnigent\",\"label\":\"OmniGent\",\"items\":[]},{\"label\":\"no connector\",\"items\":[{\"path\":\"p\"}]}]}";

    // ------------------------------------------------------------------ status --json

    [Fact]
    public void The_status_read_keeps_what_each_removed_connector_left()
    {
        var status = DefenseClawStatusReader.Parse(CleanupStatus);

        var left = Assert.Single(status.RemovedConnectorCleanup);
        Assert.Equal("openhands", left.Connector);
        Assert.Equal("OpenHands", left.Label);
        Assert.Equal(2, left.Items.Count);
        Assert.Contains("openhands-hook.sh", left.Items[0].Step, StringComparison.Ordinal);
        Assert.Equal("copy back what you need", left.Items[1].Step);
    }

    [Theory]
    [InlineData("{\"environment\":\"dev\"}")]
    [InlineData("{\"removed_connector_cleanup\":null}")]
    [InlineData("{\"removed_connector_cleanup\":{}}")]
    [InlineData("{\"removed_connector_cleanup\":[]}")]
    public void An_older_status_has_no_cleanup(string json) =>
        Assert.Empty(DefenseClawStatusReader.Parse(json).RemovedConnectorCleanup);

    [Theory]
    [InlineData("{\"sidecar\":{\"running\":null,\"reason\":\"API listener inspection failed\"}}", null)]
    [InlineData("{\"sidecar\":{\"running\":true}}", true)]
    [InlineData("{\"sidecar\":{\"running\":false,\"reason\":\"x\"}}", false)]
    [InlineData("{\"sidecar\":null}", null)]
    public void A_sidecar_that_was_not_checked_is_unknown_not_stopped(string json, bool? expected) =>
        Assert.Equal(expected, DefenseClawStatusReader.Parse(json).SidecarRunning);

    // ------------------------------------------------------------------ the Overview rows

    [Fact]
    public async Task The_leftovers_of_a_removed_connector_are_a_row_with_each_step_in_the_runtimes_words()
    {
        var vm = await PanelAsync("1.0.4");
        vm.Apply(Snapshot());

        vm.ApplyStatus(DefenseClawStatusReader.Parse(CleanupStatus));

        var row = Assert.Single(vm.Attention, r => r.Title.StartsWith("OpenHands was removed", StringComparison.Ordinal));
        Assert.Equal("High", row.SeverityKey);
        Assert.Contains("2 leftovers", row.Title, StringComparison.Ordinal);
        Assert.Contains("remove every hook whose command runs openhands-hook.sh", row.Detail, StringComparison.Ordinal);
        Assert.Contains("copy back what you need", row.Detail, StringComparison.Ordinal);
        Assert.False(row.HasCommand);

        vm.ApplyStatus(DefenseClawStatusReader.Parse("{\"environment\":\"dev\"}"));
        Assert.DoesNotContain(vm.Attention, r => r.Title.Contains("was removed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_leftover_install_lock_is_a_row_with_the_installer_as_its_button()
    {
        var vm = await PanelAsync("1.0.4", config: "guardrail:\n  enabled: true\n");
        vm.Apply(Snapshot());
        var path = Path.Combine(_temp.Path, InterruptedInstallLock.LockName);
        _ = Directory.CreateDirectory(path);
        Directory.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-2));

        await vm.RefreshInstallLockAsync(CancellationToken.None);

        var row = Assert.Single(vm.Attention, r => r.Title == OverviewPanelViewModel.InterruptedInstallTitle);
        Assert.Equal("High", row.SeverityKey);
        Assert.Contains("agent hooks are blocking until it's cleared", row.Detail, StringComparison.Ordinal);
        var action = Assert.Single(row.Actions.Items);
        Assert.Equal(OverviewPanelViewModel.OpenInstallerActionId, action.Id);

        AppServices? opened = null;
        vm.OpenInstaller = s => opened = s;
        await vm.RunAttentionActionCommand.ExecuteAsync(action.Id);
        Assert.Same(_services, opened);

        // The lock goes (the installer cleared it): the row goes with the next read.
        Directory.Delete(path);
        await vm.RefreshInstallLockAsync(CancellationToken.None);
        Assert.DoesNotContain(vm.Attention, r => r.Title == OverviewPanelViewModel.InterruptedInstallTitle);
    }

    [Fact]
    public async Task A_lock_means_nothing_on_a_runtime_whose_hooks_never_read_it()
    {
        var vm = await PanelAsync("1.0.3", config: "guardrail:\n  enabled: true\n");
        vm.Apply(Snapshot());
        var path = Path.Combine(_temp.Path, InterruptedInstallLock.LockName);
        _ = Directory.CreateDirectory(path);
        Directory.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-2));

        await vm.RefreshInstallLockAsync(CancellationToken.None);

        Assert.Null(vm.LastInstallLock);
        Assert.DoesNotContain(vm.Attention, r => r.Title == OverviewPanelViewModel.InterruptedInstallTitle);
    }

    [Fact]
    public async Task Observe_with_a_saved_closed_is_still_the_old_warning_before_1_0_4_and_when_the_runtime_is_unknown()
    {
        foreach (var version in new[] { "1.0.3", null })
        {
            _services?.Dispose();
            var vm = await PanelAsync(version);

            vm.Apply(Snapshot());

            var row = Assert.Single(vm.Attention, r => r.Title == "claudecode: observe mode with a fail-closed hook");
            Assert.Contains("Pair observe with hook_fail_mode: open", row.Detail, StringComparison.Ordinal);
            Assert.DoesNotContain(vm.Attention, r => r.Title.Contains("is not applied", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task On_a_per_user_1_0_4_host_a_saved_closed_is_said_to_be_ignored_and_the_strict_pack_is_the_way()
    {
        var vm = await PanelAsync("1.0.4");

        vm.Apply(Snapshot());

        Assert.DoesNotContain(vm.Attention, r => r.Title == "claudecode: observe mode with a fail-closed hook");
        var row = Assert.Single(vm.Attention, r => r.Title == "claudecode: hook_fail_mode: closed is not applied");
        Assert.Equal("defenseclaw guardrail use-pack strict", row.Command);
        Assert.Contains("follows the policy tier", row.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task On_a_managed_1_0_4_host_the_administrators_explicit_value_still_applies()
    {
        var vm = await PanelAsync("1.0.4");
        _services!.Installation.Replace(TestInstallations.Managed(_temp.Path));

        vm.Apply(Snapshot());

        Assert.False(vm.SavedFailModeIgnored);
        Assert.Contains(vm.Attention, r => r.Title == "claudecode: observe mode with a fail-closed hook");
    }

    // ------------------------------------------------------------------ connector lists

    [Fact]
    public void The_fallback_and_the_catalogue_offer_neither_removed_connector_whatever_the_runtime()
    {
        Assert.DoesNotContain(ConnectorOnboarding.Offerable(null, connectorsRetired: true), o => RemovedConnectors.IsRemoved(o.Id));
        Assert.DoesNotContain(ConnectorOnboarding.Offerable(null), o => RemovedConnectors.IsRemoved(o.Id));
    }

    [Fact]
    public void The_config_editor_stops_offering_them_on_1_0_4_and_keeps_them_otherwise()
    {
        var before = ConfigChoiceProfile.ForHost(extendedConnectors: true);
        var after = ConfigChoiceProfile.ForHost(extendedConnectors: true, connectorsRetired: true);

        Assert.Contains("openhands", ConfigFieldCatalog.AllConnectors(before));
        Assert.Contains("omnigent", ConfigFieldCatalog.AllConnectors(before));
        Assert.DoesNotContain("openhands", ConfigFieldCatalog.AllConnectors(after));
        Assert.DoesNotContain("omnigent", ConfigFieldCatalog.AllConnectors(after));
        Assert.DoesNotContain("omnigent", ConfigFieldCatalog.SupportedConnectors(after));
        Assert.Contains("codex", ConfigFieldCatalog.AllConnectors(after));

        // 0.8.10's own profile is the one the editor started with: unchanged.
        Assert.Contains("openhands", ConfigFieldCatalog.AllConnectors(ConfigChoiceProfile.Windows));
    }

    [Fact]
    public void The_fail_mode_fields_say_why_they_are_read_only_by_runtime()
    {
        var older = ConfigFieldCatalog.Resolve("guardrail.hook_fail_mode", ConfigChoiceProfile.ForHost());
        var current = ConfigFieldCatalog.Resolve("guardrail.connectors.codex.hook_fail_mode", ConfigChoiceProfile.ForHost(savedFailModeIgnored: true));

        Assert.Contains("guardrail fail-mode", older!.ReadOnlyReason, StringComparison.Ordinal);
        Assert.DoesNotContain("policy tier", older.ReadOnlyReason, StringComparison.Ordinal);
        Assert.Contains("policy tier", current!.ReadOnlyReason, StringComparison.Ordinal);
        Assert.Contains("defenseclaw guardrail use-pack strict", current.ReadOnlyReason, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ the installer's hint

    [Fact]
    public void An_installer_reason_that_ends_in_accept_current_install_gets_the_hint()
    {
        const string reason = "Could not close the interrupted install's hooks again after its gateway exited; run: & 'C:\\x\\install.ps1' -Version '1.0.4' -Yes -AcceptCurrentInstall";

        Assert.Equal(InstallScriptPlan.AcceptCurrentInstallHint, InstallScriptPlan.HintFor(reason));
        Assert.Equal(InstallScriptPlan.ConnectorRecoveryHint, InstallScriptPlan.HintFor("active connector recovery remains -AcceptCurrentInstall"));
        Assert.Null(InstallScriptPlan.HintFor("The install stays locked"));
        Assert.Null(InstallScriptPlan.HintFor(null));
    }

    [Fact]
    public void A_cut_reason_still_names_the_switch()
    {
        var transcript = "  x " + new string('a', 600) + " run: & 'install.ps1' -Yes -AcceptCurrentInstall\r\n";

        var reason = InstallScriptPlan.ExtractFailureReason(transcript)!;

        Assert.True(reason.Length <= InstallScriptPlan.MaxFailureReasonChars);
        Assert.EndsWith("(-AcceptCurrentInstall)", reason, StringComparison.Ordinal);
        Assert.NotNull(InstallScriptPlan.HintFor(reason));
    }
}
