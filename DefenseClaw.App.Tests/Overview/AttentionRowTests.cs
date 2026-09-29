using System.Text.Json;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.ClaudeCode;
using DefenseClaw.Core.Gateway.Models;
using DefenseClaw.Core.Install;
using DefenseClaw.Core.Net;

namespace DefenseClaw.App.Tests.Overview;

/// <summary>
/// "What needs attention": four independent signals collapsed into one list. The suggested command is displayed and
/// copyable but is never part of what a screen reader announces for the row.
/// </summary>
public sealed class AttentionRowTests : IDisposable
{
    private const string SettingsPath = "C:\\Users\\someone\\.claude\\settings.json";

    private readonly TempDirectory _temp = new();
    private DefenseClaw.App.Services.AppServices? _services;

    public void Dispose()
    {
        _services?.Dispose();
        _temp.Dispose();
    }

    private OverviewPanelViewModel Panel(string? configYaml = null)
    {
        _services = TestServices.Create(_temp, configYaml);
        return new OverviewPanelViewModel(_services);
    }

    private static GatewaySnapshot Snapshot(AppGatewayState state, string detail = "", Action<GatewaySnapshotBuilder>? tweak = null)
    {
        var builder = new GatewaySnapshotBuilder { State = state, Detail = detail };
        tweak?.Invoke(builder);
        return builder.Build();
    }

    /// <summary>Init-only properties cannot be set through a lambda, so the few a test varies go through here.</summary>
    private sealed class GatewaySnapshotBuilder
    {
        public AppGatewayState State { get; set; }

        public string Detail { get; set; } = string.Empty;

        public int CriticalAlertCount { get; set; }

        public IReadOnlyList<GatewayAlert> RecentAlerts { get; set; } = Array.Empty<GatewayAlert>();

        public string? AlertsUnavailable { get; set; }

        public FailModeDrift? Drift { get; set; }

        public PortOwner? PortOwner { get; set; }

        public int ApiPort { get; set; } = 18970;

        public GatewaySnapshot Build() => new()
        {
            State = State,
            Detail = Detail,
            CriticalAlertCount = CriticalAlertCount,
            RecentAlerts = RecentAlerts,
            AlertsUnavailable = AlertsUnavailable,
            FailModeDrift = Drift,
            PortOwner = PortOwner,
            ApiPort = ApiPort,
            PolledAt = DateTimeOffset.UtcNow,
        };
    }

    private static GatewayAlert Alert(string id, string severity) => new()
    {
        Id = id,
        Timestamp = DateTimeOffset.UtcNow,
        Severity = severity,
        Structured = new Dictionary<string, JsonElement>
        {
            [GatewayAlert.Keys.RuleId] = JsonSerializer.SerializeToElement("CMD-ENV-DUMP"),
        },
    };

    private static FailModeDrift Drift(string env, string gateway, string? guardrailMode) => new()
    {
        EnvFailMode = env,
        GatewayFailMode = gateway,
        GatewaySource = FailModeDrift.StatusSource,
        GuardrailMode = guardrailMode,
        SettingsPath = SettingsPath,
    };

    // ------------------------------------------------------------------ AttentionRow.ToString

    [Fact]
    public void A_row_with_a_command_says_one_is_available_without_repeating_it()
    {
        var command = Drift("closed", "open", "observe").RemediationCommand;
        var row = new AttentionRow
        {
            Title = "claudecode: settings.json overrides the hook fail mode",
            Detail = "The hook obeys the env var.",
            SeverityKey = "Critical",
            Command = command,
        };

        var spoken = row.ToString();

        Assert.True(row.HasCommand);
        Assert.Equal(
            "Critical: claudecode: settings.json overrides the hook fail mode. The hook obeys the env var. A suggested command is available.",
            spoken);
        Assert.DoesNotContain(command, spoken, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadAllText", spoken, StringComparison.Ordinal);
        Assert.DoesNotContain("Copy-Item", spoken, StringComparison.Ordinal);
        Assert.DoesNotContain(SettingsPath, spoken, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("defenseclaw-gateway start")]
    [InlineData("defenseclaw init")]
    [InlineData("& { Remove-Item -Recurse C:\\somewhere }")]
    public void No_command_text_ever_reaches_the_spoken_row(string command)
    {
        var row = new AttentionRow { Title = "Something", Detail = "Detail.", Command = command };

        Assert.DoesNotContain(command, row.ToString(), StringComparison.Ordinal);
        Assert.EndsWith("A suggested command is available.", row.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_row_with_no_command_does_not_claim_one(string? command)
    {
        var row = new AttentionRow { Title = "Nothing needs attention", Detail = "All good.", SeverityKey = "Ok", Command = command };

        Assert.False(row.HasCommand);
        Assert.Equal("Ok: Nothing needs attention. All good.", row.ToString());
    }

    [Fact]
    public void A_row_with_no_detail_has_no_dangling_punctuation_gap()
    {
        var row = new AttentionRow { Title = "Checking the gateway…" };

        Assert.Equal("Info: Checking the gateway….", row.ToString());
    }

    [Fact]
    public void Two_rows_with_the_same_content_are_equal_so_an_unchanged_row_keeps_its_visuals()
    {
        var a = new AttentionRow { Title = "t", Detail = "d", SeverityKey = "High", Command = "c" };
        var b = new AttentionRow { Title = "t", Detail = "d", SeverityKey = "High", Command = "c" };

        Assert.Equal(a, b);
        Assert.NotEqual(a, b with { Command = "other" });
    }

    // ------------------------------------------------------------------ the rows the panel builds

    private static AttentionRow Only(OverviewPanelViewModel vm) => Assert.Single(vm.Attention);

    [Fact]
    public void Before_the_first_poll_the_panel_does_not_claim_the_gateway_is_healthy()
    {
        var vm = Panel();

        vm.Apply(GatewaySnapshot.Initial);

        var row = Only(vm);
        Assert.Equal("Checking the gateway…", row.Title);
        Assert.Equal("Info", row.SeverityKey);
    }

    [Fact]
    public void A_healthy_running_gateway_says_nothing_needs_attention()
    {
        var vm = Panel();

        vm.Apply(Snapshot(AppGatewayState.Running));

        var row = Only(vm);
        Assert.Equal("Nothing needs attention", row.Title);
        Assert.Equal("Ok", row.SeverityKey);
        Assert.False(row.HasCommand);
    }

    [Fact]
    public void A_stopped_gateway_is_high_and_offers_the_start_command_without_running_it()
    {
        var vm = Panel();

        vm.Apply(Snapshot(AppGatewayState.GatewayStopped, "Nothing is listening on port 18970."));

        var row = Only(vm);
        Assert.Equal("The gateway is not answering", row.Title);
        Assert.Equal("High", row.SeverityKey);
        Assert.Equal("defenseclaw-gateway start", row.Command);
        Assert.Contains("Nothing is listening on port 18970.", row.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("defenseclaw-gateway start", row.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(AppGatewayState.Degraded, "The gateway answered, but not cleanly", "Medium", null)]
    [InlineData(AppGatewayState.NotInstalled, "DefenseClaw was not found", "Critical", null)]
    [InlineData(AppGatewayState.NotInitialized, "Installed, but never initialized", "Medium", "defenseclaw init")]
    public void Each_bad_state_has_its_own_severity_and_command(AppGatewayState state, string title, string severity, string? command)
    {
        var vm = Panel();

        vm.Apply(Snapshot(state, "detail"));

        var row = Only(vm);
        Assert.Equal(title, row.Title);
        Assert.Equal(severity, row.SeverityKey);
        Assert.Equal(command, row.Command);
    }

    [Fact]
    public void A_wsl_gateway_names_the_process_that_owns_the_port()
    {
        var vm = Panel();

        vm.Apply(Snapshot(AppGatewayState.WslGatewayDetected, tweak: b => b.PortOwner = new PortOwner(4242, "wslrelay", "127.0.0.1", 18970)));

        var row = Only(vm);
        Assert.Equal("A WSL gateway owns the API port", row.Title);
        Assert.Equal("High", row.SeverityKey);
        Assert.Contains("wslrelay", row.Detail, StringComparison.Ordinal);
        Assert.Contains("pid 4242", row.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_settings_json_override_in_the_dangerous_pairing_is_critical_and_carries_the_remediation_command()
    {
        var vm = Panel();
        var drift = Drift("closed", "open", "observe");

        vm.Apply(Snapshot(AppGatewayState.Running, tweak: b => b.Drift = drift));

        var row = Only(vm);
        Assert.Equal("claudecode: settings.json overrides the hook fail mode", row.Title);
        Assert.Equal("Critical", row.SeverityKey);
        Assert.Equal(drift.RemediationCommand, row.Command);
        Assert.Contains("blocks the agent it is only meant to watch", row.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(drift.RemediationCommand, row.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("ReadAllText", row.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_settings_json_override_that_is_not_the_dangerous_pairing_is_high()
    {
        var vm = Panel();

        vm.Apply(Snapshot(AppGatewayState.Running, tweak: b => b.Drift = Drift("open", "closed", "observe")));

        var row = Only(vm);
        Assert.Equal("High", row.SeverityKey);
        Assert.DoesNotContain("meant to watch", row.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void An_observe_connector_with_a_fail_closed_hook_in_config_yaml_is_a_high_row_of_its_own()
    {
        var vm = Panel("guardrail:\n  connectors:\n    claudecode:\n      mode: observe\n      hook_fail_mode: closed\n    codex:\n      mode: observe\n      hook_fail_mode: open\n");

        vm.Apply(Snapshot(AppGatewayState.Running));

        var row = Only(vm);
        Assert.Equal("claudecode: observe mode with a fail-closed hook", row.Title);
        Assert.Equal("High", row.SeverityKey);
    }

    [Fact]
    public void An_unreadable_config_yaml_is_the_first_row_and_critical()
    {
        var vm = Panel("gateway: [unclosed\n");

        vm.Apply(Snapshot(AppGatewayState.GatewayStopped, "down"));

        Assert.Equal(2, vm.Attention.Count);
        Assert.Equal("config.yaml could not be read", vm.Attention[0].Title);
        Assert.Equal("Critical", vm.Attention[0].SeverityKey);
        Assert.Equal("The gateway is not answering", vm.Attention[1].Title);
    }

    [Theory]
    [InlineData(1, "1 CRITICAL alert in the last poll")]
    [InlineData(3, "3 CRITICAL alerts in the last poll")]
    public void Critical_alerts_are_counted_with_the_right_plural(int count, string title)
    {
        var vm = Panel();

        vm.Apply(Snapshot(AppGatewayState.Running, tweak: b => b.CriticalAlertCount = count));

        var row = Only(vm);
        Assert.Equal(title, row.Title);
        Assert.Equal("Critical", row.SeverityKey);
    }

    [Fact]
    public void High_alerts_are_counted_case_insensitively_and_use_the_high_tone()
    {
        var vm = Panel();
        var alerts = new[] { Alert("1", "HIGH"), Alert("2", "high"), Alert("3", "MEDIUM"), Alert("4", "HIGH") };

        vm.Apply(Snapshot(AppGatewayState.Running, tweak: b => b.RecentAlerts = alerts));

        var row = Only(vm);
        Assert.Equal("3 HIGH findings in the last 25 alerts", row.Title);
        Assert.Equal("High", row.SeverityKey);
    }

    [Fact]
    public void One_high_alert_is_singular()
    {
        var vm = Panel();

        vm.Apply(Snapshot(AppGatewayState.Running, tweak: b => b.RecentAlerts = new[] { Alert("1", "HIGH") }));

        Assert.Equal("1 HIGH finding in the last 25 alerts", Only(vm).Title);
    }

    [Fact]
    public void Alerts_not_being_served_is_information_when_the_gateway_is_up_and_not_repeated_when_it_is_down()
    {
        var vm = Panel();

        vm.Apply(Snapshot(AppGatewayState.Running, tweak: b => b.AlertsUnavailable = "gateway: not connected"));
        var row = Only(vm);
        Assert.Equal("Alerts are not being served", row.Title);
        Assert.Equal("Info", row.SeverityKey);
        Assert.Equal("gateway: not connected", row.Detail);

        vm.Apply(Snapshot(AppGatewayState.GatewayStopped, "down", b => b.AlertsUnavailable = "gateway: not connected"));
        Assert.DoesNotContain(vm.Attention, r => r.Title == "Alerts are not being served");
    }

    [Fact]
    public void Several_signals_stack_in_the_order_they_are_checked_and_the_ok_row_disappears()
    {
        var vm = Panel("guardrail:\n  connectors:\n    claudecode:\n      mode: observe\n      hook_fail_mode: closed\n");
        var drift = Drift("closed", "closed", "observe");

        vm.Apply(Snapshot(
            AppGatewayState.Degraded,
            "answered 500",
            b =>
            {
                b.Drift = drift;
                b.CriticalAlertCount = 2;
                b.RecentAlerts = new[] { Alert("1", "HIGH") };
            }));

        Assert.Equal(
            new[]
            {
                "The gateway answered, but not cleanly",
                "claudecode: observe mode with a fail-closed hook",
                "claudecode: settings.json overrides the hook fail mode",
                "2 CRITICAL alerts in the last poll",
                "1 HIGH finding in the last 25 alerts",
            },
            vm.Attention.Select(r => r.Title).ToArray());
        Assert.DoesNotContain(vm.Attention, r => r.Title == "Nothing needs attention");
    }

    [Fact]
    public void Applying_the_same_snapshot_twice_does_not_replace_unchanged_rows()
    {
        var vm = Panel();
        var snapshot = Snapshot(AppGatewayState.GatewayStopped, "down");

        vm.Apply(snapshot);
        var first = Only(vm);
        vm.Apply(snapshot);

        Assert.Same(first, Only(vm));
    }
}
