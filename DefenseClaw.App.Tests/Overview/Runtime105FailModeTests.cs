using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.ViewModels.ConfigEditor;
using DefenseClaw.Core.Config;
using DefenseClaw.Core.Gateway;
using DefenseClaw.Core.Install;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.Tests.Overview;

/// <summary>
/// The Overview's fail-mode rows under DefenseClaw 1.0.5 (CUST-397), synthetic throughout: on a per-user 1.0.5 host a saved <c>hook_fail_mode</c> decides a
/// connector's outage mode, so the 1.0.4 "is not applied" row never shows, an observing connector with a saved <c>closed</c> is the observe/fail-closed row
/// again (in 1.0.5's words), and a saved <c>open</c> that beats a strict rule pack names <c>guardrail fail-mode closed</c> as the runtime's hint does. 1.0.3,
/// 1.0.4 and managed installs read as before.
/// </summary>
public sealed class Runtime105FailModeTests : IDisposable
{
    private const string ObserveSavedClosed =
        "guardrail:\n  enabled: true\n  connector: claudecode\n  connectors:\n    claudecode:\n      mode: observe\n      hook_fail_mode: closed\n";

    private const string ObserveNothingSaved =
        "guardrail:\n  enabled: true\n  connector: claudecode\n  connectors:\n    claudecode:\n      mode: observe\n";

    private const string ActionSavedClosed =
        "guardrail:\n  enabled: true\n  connector: claudecode\n  connectors:\n    claudecode:\n      mode: action\n      hook_fail_mode: closed\n";

    private const string StrictWithConnectorSavedOpen =
        "guardrail:\n  enabled: true\n  connector: claudecode\n  connectors:\n    claudecode:\n      mode: action\n      rule_pack: strict\n      hook_fail_mode: open\n";

    private const string StrictWithGlobalSavedOpen =
        "guardrail:\n  enabled: true\n  connector: claudecode\n  hook_fail_mode: open\n  rule_pack: strict\n  connectors:\n    claudecode:\n      mode: action\n";

    private const string NotApplied = "claudecode: hook_fail_mode: closed is not applied";
    private const string ObserveClosed = "claudecode: observe mode with a fail-closed hook";
    private const string OpenBeatsStrict = "claudecode: a saved hook_fail_mode: open overrides the strict rule pack";

    private readonly TempDirectory _temp = new();
    private AppServices? _services;

    public void Dispose()
    {
        _services?.Dispose();
        _temp.Dispose();
    }

    private static RuntimeProbeRunner Runtime(string version) => (args, _) => Task.FromResult(
        args.Contains("--version-json")
            ? RuntimeProbeOutput.Ok("{\"name\":\"defenseclaw-cli\",\"version\":\"" + version + "\",\"schema_version\":1}")
            : RuntimeProbeOutput.Ok("Usage: defenseclaw [OPTIONS] COMMAND [ARGS]...\n\nCommands:\n  setup  Configure.\n"));

    private async Task<OverviewPanelViewModel> PanelAsync(string version, string config, bool managed = false)
    {
        _services?.Dispose();
        _services = TestServices.Create(_temp, config, Runtime(version));
        _ = await _services.Runtime.RefreshAsync(force: true);
        if (managed)
        {
            _services.Installation.Replace(TestInstallations.Managed(_temp.Path));
        }

        var vm = new OverviewPanelViewModel(_services);
        vm.Apply(new GatewaySnapshot
        {
            State = AppGatewayState.Running,
            Install = InstallState.Running,
            Detail = "ok",
            PolledAt = DateTimeOffset.UtcNow,
        });
        return vm;
    }

    // ------------------------------------------------------------------ observe with a saved closed, by runtime and installation

    [Theory]
    [InlineData("1.0.3", false, "old")]
    [InlineData("1.0.3", true, "old")]
    [InlineData("1.0.4", false, "not-applied")]
    [InlineData("1.0.4", true, "old")]
    [InlineData("1.0.5", false, "saved-wins")]
    [InlineData("1.0.5", true, "old")]
    public async Task Observe_with_a_saved_closed_reads_by_release_and_installation(string version, bool managed, string expected)
    {
        var vm = await PanelAsync(version, ObserveSavedClosed, managed);

        Assert.Equal(expected == "saved-wins", vm.SavedFailModeWins);
        switch (expected)
        {
            case "old":
                Assert.Contains("Pair observe with hook_fail_mode: open", Assert.Single(vm.Attention, r => r.Title == ObserveClosed).Detail, StringComparison.Ordinal);
                Assert.DoesNotContain(vm.Attention, r => r.Title == NotApplied);
                break;
            case "not-applied":
                Assert.Equal(HookFailModeRule.StrictPackCommand, Assert.Single(vm.Attention, r => r.Title == NotApplied).Command);
                Assert.DoesNotContain(vm.Attention, r => r.Title == ObserveClosed);
                break;
            default:
                var row = Assert.Single(vm.Attention, r => r.Title == ObserveClosed);
                Assert.Equal("High", row.SeverityKey);
                Assert.Contains("config.yaml saves hook_fail_mode: closed for claudecode", row.Detail, StringComparison.Ordinal);
                Assert.Contains("a saved fail mode decides", row.Detail, StringComparison.Ordinal);
                Assert.Contains("defenseclaw guardrail fail-mode open --connector claudecode", row.Detail, StringComparison.Ordinal);
                Assert.Contains(HookFailModeRule.SavedWinsOutageRule, row.Detail, StringComparison.Ordinal);
                Assert.DoesNotContain("not apply", row.Detail, StringComparison.Ordinal);
                Assert.DoesNotContain(vm.Attention, r => r.Title == NotApplied);
                break;
        }
    }

    [Fact]
    public async Task On_1_0_5_nothing_saved_and_nothing_reported_is_no_row_and_an_enforcing_connector_is_no_row()
    {
        var nothingSaved = await PanelAsync("1.0.5", ObserveNothingSaved);
        Assert.DoesNotContain(nothingSaved.Attention, r => r.Title.StartsWith("claudecode:", StringComparison.Ordinal));

        var enforcing = await PanelAsync("1.0.5", ActionSavedClosed);
        Assert.DoesNotContain(enforcing.Attention, r => r.Title.StartsWith("claudecode:", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ a saved open over a strict rule pack

    [Theory]
    [InlineData(StrictWithConnectorSavedOpen, "defenseclaw guardrail fail-mode closed --connector claudecode", "for claudecode")]
    [InlineData(StrictWithGlobalSavedOpen, "defenseclaw guardrail fail-mode closed", "for every connector")]
    public async Task On_1_0_5_a_saved_open_over_a_strict_pack_names_guardrail_fail_mode_closed_not_the_strict_pack(string config, string hint, string where)
    {
        var vm = await PanelAsync("1.0.5", config);

        var row = Assert.Single(vm.Attention, r => r.Title == OpenBeatsStrict);
        Assert.Equal("Medium", row.SeverityKey);
        Assert.Equal(hint, row.Command);
        Assert.Contains("hook_fail_mode: open " + where, row.Detail, StringComparison.Ordinal);
        Assert.Contains("wins over the policy tier", row.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(HookFailModeRule.StrictPackCommand, row.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1.0.3", false, false)]
    [InlineData("1.0.3", true, false)]
    [InlineData("1.0.4", false, false)]
    [InlineData("1.0.4", true, false)]
    [InlineData("1.0.5", false, true)]
    [InlineData("1.0.5", true, false)]
    public async Task The_strict_pack_row_is_a_1_0_5_per_user_row_only(string version, bool managed, bool shown)
    {
        var vm = await PanelAsync(version, StrictWithConnectorSavedOpen, managed);

        Assert.Equal(shown, vm.Attention.Any(r => r.Title == OpenBeatsStrict));
        Assert.DoesNotContain(vm.Attention, r => r.Title == NotApplied);
    }

    // ------------------------------------------------------------------ the configuration card's fail-mode row

    [Theory]
    [InlineData("1.0.5", ObserveSavedClosed, "outage mode saved for claudecode (closed), which wins over the policy tier")]
    [InlineData("1.0.5", StrictWithGlobalSavedOpen, "outage mode saved for every connector (open), which wins over the policy tier")]
    [InlineData("1.0.5", ObserveNothingSaved, "no outage mode saved: the policy tier decides")]
    [InlineData("1.0.4", ObserveSavedClosed, null)]
    [InlineData("1.0.3", ObserveSavedClosed, null)]
    public async Task The_configuration_row_says_what_decides_the_outage_mode_on_1_0_5_only(string version, string config, string? decider)
    {
        var vm = await PanelAsync(version, config);

        Assert.Equal(decider, vm.FailModeDecider("claudecode"));
        if (decider is null)
        {
            Assert.DoesNotContain(vm.ConfigurationRows, r => r.Value.Contains("policy tier", StringComparison.Ordinal));
        }
    }

    // ------------------------------------------------------------------ the config editor's read-only reason

    [Fact]
    public void The_fail_mode_fields_say_a_saved_value_wins_on_1_0_5()
    {
        var current = ConfigFieldCatalog.Resolve("guardrail.connectors.codex.hook_fail_mode", ConfigChoiceProfile.ForHost(savedFailModeWins: true));
        var global = ConfigFieldCatalog.Resolve("guardrail.hook_fail_mode", ConfigChoiceProfile.ForHost(savedFailModeWins: true));

        foreach (var field in new[] { current!, global! })
        {
            Assert.Contains("a saved value decides", field.ReadOnlyReason, StringComparison.Ordinal);
            Assert.Contains("with none saved, the policy tier decides", field.ReadOnlyReason, StringComparison.Ordinal);
            Assert.Contains("defenseclaw guardrail fail-mode", field.ReadOnlyReason, StringComparison.Ordinal);
            Assert.DoesNotContain("does not apply", field.ReadOnlyReason, StringComparison.Ordinal);
        }
    }
}
