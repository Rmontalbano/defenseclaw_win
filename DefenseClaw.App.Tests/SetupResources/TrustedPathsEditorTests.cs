using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels.SetupResources;
using DefenseClaw.Core.Setup;

namespace DefenseClaw.App.Tests.SetupResources;

/// <summary>
/// CUST-270, what is particular to the trusted binary locations editor: the connectors the last discovery scan skipped for the folder they are in
/// (the TUI's proactive highlight, read from <c>agent_discovery.json</c>), the Trust buttons and the banner that hand a folder to the Add wizard, and
/// the way "could not read the allow-list" differs from an empty one.
/// </summary>
public sealed class TrustedPathsEditorTests : IDisposable
{
    private readonly SetupEditorHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private void WriteDiscovery(string? json = null) =>
        File.WriteAllText(Path.Combine(_harness.DataDirectory, "agent_discovery.json"), json ?? FakeSetupCli.Fixture("agent-discovery.synthetic.json"));

    private async Task<TrustedPathsViewModel> OpenAsync(Action<FakeSetupCli>? script = null, string prefill = "", string context = "")
    {
        var (vm, _) = await _harness.OpenAsync(SetupResource.TrustedPaths, script, prefill: prefill, context: context);
        return (TrustedPathsViewModel)vm;
    }

    // ---- connectors in untrusted folders ----

    [Fact]
    public async Task The_connectors_the_last_scan_skipped_are_listed_with_the_folder_to_trust()
    {
        WriteDiscovery();

        var vm = await OpenAsync();

        Assert.Equal(
            new[] { ("claudecode", @"C:\Users\synthetic\.local\bin"), ("cursor", @"D:\agents\cursor\bin") },
            vm.UntrustedConnectors.Select(c => (c.Connector, c.Directory)));
        Assert.True(vm.HasUntrustedConnectors);
        Assert.True(vm.HasUntrustedHeadline);
        Assert.StartsWith("2 connectors' programs are in folders that are not trusted (last discovery scan, ", vm.UntrustedHeadline, StringComparison.Ordinal);
        Assert.Equal("Warn", vm.UntrustedTone);
    }

    [Fact]
    public async Task A_folder_trusted_since_the_scan_is_left_out()
    {
        WriteDiscovery();

        // D:\agents\cursor\bin is in the allow-list and in good order; the scan cached before it was trusted.
        var vm = await OpenAsync(c => c.TrustedPaths = c.TrustedPaths.Replace(
            "\"resolved\": \"D:\\\\agents\\\\bin\"",
            "\"resolved\": \"D:\\\\agents\\\\cursor\\\\bin\\\\\"",
            StringComparison.Ordinal).Replace("\"status\": \"missing\"", "\"status\": \"ok\"", StringComparison.Ordinal));

        Assert.Equal(new[] { "claudecode" }, vm.UntrustedConnectors.Select(c => c.Connector));
        Assert.StartsWith("1 connector's program is in a folder that is not trusted", vm.UntrustedHeadline, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_folder_that_is_in_the_list_but_not_in_good_order_does_not_count_as_trusted()
    {
        WriteDiscovery();

        // The folder is listed with status unsafe-permissions: the CLI does not honor it, so the connector is still outside a trusted folder.
        var vm = await OpenAsync(c => c.TrustedPaths = c.TrustedPaths.Replace(
            "\"resolved\": \"C:\\\\Users\\\\Public\\\\tools\"",
            "\"resolved\": \"C:\\\\Users\\\\synthetic\\\\.local\\\\bin\"",
            StringComparison.Ordinal));

        Assert.Equal(new[] { "claudecode", "cursor" }, vm.UntrustedConnectors.Select(c => c.Connector));
    }

    [Fact]
    public async Task A_scan_with_nothing_to_report_says_so_and_a_missing_or_broken_file_says_it_could_not_be_read()
    {
        WriteDiscovery("""{"scanned_at": "2026-10-08T17:30:00Z", "agents": {"codex": {"name": "codex", "binary_path": "C:\\Program Files\\nodejs\\codex.cmd", "error": ""}}}""");
        var none = await OpenAsync();
        Assert.Empty(none.UntrustedConnectors);
        Assert.StartsWith("No connector program was found outside a trusted folder in the last discovery scan", none.UntrustedHeadline, StringComparison.Ordinal);
        Assert.Equal("Neutral", none.UntrustedTone);

        File.Delete(Path.Combine(_harness.DataDirectory, "agent_discovery.json"));
        var missing = await OpenAsync();
        Assert.Empty(missing.UntrustedConnectors);
        Assert.Contains("No discovery scan has been recorded yet", missing.UntrustedHeadline, StringComparison.Ordinal);
        Assert.True(missing.HasUntrustedHeadline);

        WriteDiscovery("this is not json");
        var broken = await OpenAsync();
        Assert.Empty(broken.UntrustedConnectors);
        Assert.Contains("could not be read", broken.UntrustedHeadline, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_discovery_file_that_is_far_larger_than_it_should_be_is_not_read()
    {
        File.WriteAllText(Path.Combine(_harness.DataDirectory, "agent_discovery.json"), new string(' ', (1 << 20) + 1));

        var vm = await OpenAsync();

        Assert.Empty(vm.UntrustedConnectors);
        Assert.Contains("larger than expected", vm.UntrustedHeadline, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Trust_opens_the_add_wizard_on_add_with_the_folder_filled_in_and_reads_the_list_again()
    {
        WriteDiscovery();
        var (editor, cli) = await _harness.OpenAsync(SetupResource.TrustedPaths);
        var vm = (TrustedPathsViewModel)editor;
        var presets = new List<WizardPreset>();
        vm.OpenWizard = preset =>
        {
            presets.Add(preset);
            return Task.CompletedTask;
        };

        await vm.TrustCommand.ExecuteAsync(vm.UntrustedConnectors[1]);

        Assert.Equal(new WizardPreset("add", @"D:\agents\cursor\bin"), Assert.Single(presets));
        Assert.Equal(2, cli.Ran.Count);
        Assert.Empty(cli.Applied); // the wizard reviews and runs its own command; this window runs nothing
        Assert.False(vm.Review.IsOpen);
    }

    [Fact]
    public async Task Trust_is_off_on_a_managed_installation_and_opens_nothing()
    {
        WriteDiscovery();
        var services = _harness.Services(TestInstallations.ManagedAt(_harness.DataDirectory));
        var (editor, _) = await _harness.OpenAsync(SetupResource.TrustedPaths, services: services);
        var vm = (TrustedPathsViewModel)editor;
        var opened = 0;
        vm.OpenWizard = _ => { opened++; return Task.CompletedTask; };

        // The connectors are still listed (a read); only trusting one is off.
        Assert.Equal(2, vm.UntrustedConnectors.Count);
        Assert.False(vm.CanAdd);
        await vm.TrustCommand.ExecuteAsync(vm.UntrustedConnectors[0]);

        Assert.Equal(0, opened);
        Assert.Equal(TestInstallations.ManagedReason, vm.NoticeMessage);
    }

    // ---- what a failed connector setup hands over ----

    [Fact]
    public async Task A_window_opened_by_a_failed_setup_offers_the_folder_it_named()
    {
        const string context = "Setup of Claude Code stopped: its program is in C:\\Users\\synthetic\\.local\\bin, which is not a trusted folder.";

        var vm = await OpenAsync(prefill: @"C:\Users\synthetic\.local\bin", context: context);

        Assert.True(vm.HasContext);
        Assert.True(vm.HasPrefill);
        Assert.Equal(context, vm.ContextMessage);
        Assert.Equal(@"C:\Users\synthetic\.local\bin", vm.Prefill);

        var presets = new List<WizardPreset>();
        vm.OpenWizard = preset =>
        {
            presets.Add(preset);
            return Task.CompletedTask;
        };
        await vm.TrustPrefillCommand.ExecuteAsync(null);

        Assert.Equal(new WizardPreset("add", @"C:\Users\synthetic\.local\bin"), Assert.Single(presets));
    }

    [Fact]
    public async Task A_window_the_operator_browsed_to_has_no_banner_and_trust_prefill_does_nothing()
    {
        var vm = await OpenAsync();
        var opened = 0;
        vm.OpenWizard = _ => { opened++; return Task.CompletedTask; };

        Assert.False(vm.HasContext);
        Assert.False(vm.HasPrefill);
        await vm.TrustPrefillCommand.ExecuteAsync(null);

        Assert.Equal(0, opened);
    }

    [Fact]
    public async Task A_window_that_is_opened_again_by_a_setup_takes_the_new_reason()
    {
        var vm = await OpenAsync();
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.SetContext(@"  D:\agents\bin  ", "  Setup of Cursor stopped.  ");

        Assert.Equal(@"D:\agents\bin", vm.Prefill);
        Assert.Equal("Setup of Cursor stopped.", vm.ContextMessage);
        Assert.Contains(nameof(TrustedPathsViewModel.HasContext), changed);
        Assert.Contains(nameof(TrustedPathsViewModel.HasPrefill), changed);
    }

    // ---- the allow-list could not be read ----

    [Fact]
    public async Task An_allow_list_that_could_not_be_read_is_unknown_and_everything_that_changes_it_is_off()
    {
        WriteDiscovery();
        var vm = await OpenAsync(c => c.ReadExitCode = 1);

        Assert.Equal(SetupListState.Failed, vm.State);
        Assert.Equal("Could not read the trusted-path allow-list", vm.ErrorTitle);
        Assert.False(vm.ShowEmpty);
        Assert.Empty(vm.Rows);
        Assert.False(vm.CanRemove);

        // Without the allow-list the connectors cannot be compared with it, so none is listed as if the list were empty. Add stays: the wizard reviews its own command.
        Assert.Empty(vm.UntrustedConnectors);
        Assert.False(vm.HasUntrustedHeadline);
        Assert.True(vm.CanAdd);
    }
}
