using System.Text.Json;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Gateway.Models;
using DefenseClaw.Core.Install;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.Tests.Overview;

/// <summary>
/// "What needs attention" for a configured connector the gateway did not start (CUST-448, runtime 1.0.7; agent_not_installed.go, cmd_status.py). Synthetic
/// fixtures. A runtime before 1.0.7 draws exactly what it drew before, with the same fixture.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class ConnectorSkipAttentionTests : IDisposable
{
    private const string Config =
        "guardrail:\n  enabled: true\n  connector: codex\n  connectors:\n    codex:\n      mode: observe\n    hermes:\n      mode: observe\n";

    private const string NotInstalled =
        "the agent is not installed for this account (no hermes executable found); reinstall it and run: defenseclaw-gateway restart, or remove the connector: defenseclaw setup remove hermes";

    private const string Refused = "Codex hooks are turned off in a config.toml ([features] hooks = false), so Codex runs no DefenseClaw hook";

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

    private async Task<OverviewPanelViewModel> PanelAsync(string version)
    {
        _services?.Dispose();
        _services = TestServices.Create(_temp, Config, Runtime(version));
        _ = await _services.Runtime.RefreshAsync(force: true);
        return new OverviewPanelViewModel(_services);
    }

    private static GatewayHealth Health(string guardrailDetails, string connectors = "[{\"name\":\"codex\",\"state\":\"running\",\"requests\":0}]") =>
        JsonSerializer.Deserialize<GatewayHealth>(
            "{\"uptime_ms\":7200000,\"api\":{\"state\":\"running\"}," +
            "\"guardrail\":{\"state\":\"running\",\"details\":{\"policy_mode\":\"observe\"" + (guardrailDetails.Length > 0 ? "," + guardrailDetails : string.Empty) + "}}," +
            "\"connectors\":" + connectors + "}",
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private static GatewaySnapshot Snapshot(GatewayHealth? health) => new()
    {
        State = AppGatewayState.Running,
        Install = InstallState.Running,
        Detail = "ok",
        Health = health,
        ActiveConnectors = ["codex", "hermes"],
        PolledAt = DateTimeOffset.UtcNow,
    };

    private static readonly string NotInstalledDetails =
        "\"connectors_not_started\":[\"hermes\"],\"connectors_refused_by_agent_settings\":{\"hermes\":\"" + NotInstalled + "\"}," +
        "\"connectors_not_started_reasons\":{\"hermes\":\"" + NotInstalled + "\"}";

    private static bool IsSkipRow(AttentionRow row) => row.Title.Contains("is not enforced:", StringComparison.Ordinal);

    // ------------------------------------------------------------------ rows

    [Fact]
    public async Task On_1_0_7_an_agent_that_is_not_installed_is_a_high_row_with_remove_and_restart()
    {
        var vm = await PanelAsync("1.0.7");

        vm.Apply(Snapshot(Health(NotInstalledDetails)));

        var row = Assert.Single(vm.Attention, IsSkipRow);
        Assert.Equal("Hermes is not enforced: the agent is not installed", row.Title);
        Assert.Equal("High", row.SeverityKey);
        Assert.StartsWith("The gateway did not start this connector. It says: the agent is not installed for this account", row.Detail, StringComparison.Ordinal);
        Assert.Collection(
            row.Actions.Items,
            remove => Assert.Equal(("connector-skip-remove:hermes", "Remove connector"), (remove.Id, remove.Label)),
            restart => Assert.Equal(("connector-skip-restart", "Restart gateway"), (restart.Id, restart.Label)));
    }

    [Fact]
    public async Task Remove_connector_opens_a_review_of_setup_remove_with_yes_and_runs_nothing()
    {
        var vm = await PanelAsync("1.0.7");
        vm.Apply(Snapshot(Health(NotInstalledDetails)));

        await vm.RunAttentionActionCommand.ExecuteAsync("connector-skip-remove:hermes");

        Assert.True(vm.Review.IsOpen);
        Assert.False(vm.Review.IsRunning);
        var step = Assert.Single(vm.Review.CommandReview!.Steps);
        Assert.Equal(new[] { "setup", "remove", "hermes", "--yes" }, step.Argv);
        Assert.Empty(_services!.Cli.Activity);
    }

    [Fact]
    public async Task Remove_connector_for_a_name_the_gateway_did_not_report_opens_nothing()
    {
        var vm = await PanelAsync("1.0.7");
        vm.Apply(Snapshot(Health(NotInstalledDetails)));

        await vm.RunAttentionActionCommand.ExecuteAsync("connector-skip-remove:codex");

        Assert.False(vm.Review.IsOpen);
    }

    [Fact]
    public async Task A_refusal_by_the_agents_settings_has_a_restart_only()
    {
        var vm = await PanelAsync("1.0.7");

        vm.Apply(Snapshot(Health("\"connectors_not_started\":[\"hermes\"],\"connectors_refused_by_agent_settings\":{\"hermes\":\"" + Refused + "\"}")));

        var row = Assert.Single(vm.Attention, IsSkipRow);
        Assert.Equal("Hermes is not enforced: its own settings refuse DefenseClaw hooks", row.Title);
        Assert.Equal("connector-skip-restart", Assert.Single(row.Actions.Items).Id);
        Assert.Contains(Refused + ".", row.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_setup_failure_is_a_copy_only_command_with_no_buttons()
    {
        var vm = await PanelAsync("1.0.7");

        vm.Apply(Snapshot(Health("\"connectors_not_started\":[\"hermes\"]")));

        var row = Assert.Single(vm.Attention, IsSkipRow);
        Assert.Equal("Hermes is not enforced: setup failed when the gateway started", row.Title);
        Assert.Equal("defenseclaw setup hermes", row.Command);
        Assert.Equal(0, row.Actions.Count);
    }

    [Fact]
    public async Task On_1_0_6_the_same_health_draws_no_row_and_the_old_state_text()
    {
        var vm = await PanelAsync("1.0.6");

        vm.Apply(Snapshot(Health(NotInstalledDetails)));

        Assert.DoesNotContain(vm.Attention, IsSkipRow);
        Assert.Equal("not running", vm.ConnectorRows.Single(static r => r.Name == "hermes").StateText);
    }

    [Fact]
    public async Task With_no_health_answer_there_is_no_row()
    {
        var vm = await PanelAsync("1.0.7");

        vm.Apply(Snapshot(null));

        Assert.DoesNotContain(vm.Attention, IsSkipRow);
    }

    // ------------------------------------------------------------------ the Connectors card and zero requests

    [Fact]
    public async Task A_configured_connector_the_gateway_did_not_start_reads_not_running_not_enforced()
    {
        var vm = await PanelAsync("1.0.7");

        vm.Apply(Snapshot(Health(NotInstalledDetails)));

        Assert.Equal("not running (not enforced)", vm.ConnectorRows.Single(static r => r.Name == "hermes").StateText);
        Assert.NotEqual("not running (not enforced)", vm.ConnectorRows.Single(static r => r.Name == "codex").StateText);
    }

    [Fact]
    public async Task A_connector_the_gateway_did_not_start_has_no_no_requests_yet_row()
    {
        var vm = await PanelAsync("1.0.7");

        // The gateway lists it (the primary one), with zero requests, and also says it did not start it.
        vm.Apply(Snapshot(Health(
            "\"connectors_not_started\":[\"codex\"]",
            "[{\"name\":\"codex\",\"state\":\"running\",\"requests\":0}]")));

        Assert.DoesNotContain(vm.Attention, static r => r.Title == "Codex: no requests yet");
    }

    [Fact]
    public async Task On_1_0_6_the_no_requests_yet_row_is_drawn_as_before()
    {
        var vm = await PanelAsync("1.0.6");

        vm.Apply(Snapshot(Health(
            "\"connectors_not_started\":[\"codex\"]",
            "[{\"name\":\"codex\",\"state\":\"running\",\"requests\":0}]")));

        Assert.Contains(vm.Attention, static r => r.Title == "Codex: no requests yet");
    }

    // ------------------------------------------------------------------ not doubling up with the rows read from the agent's own files

    [Theory]
    [InlineData("codex", "C:\\x\\settings.json", null, ConnectorSkipKind.RefusedByAgentSettings, false)]
    [InlineData("codex", null, "C:\\x\\config.toml", ConnectorSkipKind.RefusedByAgentSettings, true)]
    [InlineData("claudecode", "C:\\x\\settings.json", null, ConnectorSkipKind.RefusedByAgentSettings, true)]
    [InlineData("claudecode", null, "C:\\x\\config.toml", ConnectorSkipKind.RefusedByAgentSettings, false)]
    [InlineData("claudecode", null, null, ConnectorSkipKind.RefusedByAgentSettings, false)]
    [InlineData("hermes", "C:\\x\\settings.json", "C:\\x\\config.toml", ConnectorSkipKind.RefusedByAgentSettings, false)]
    [InlineData("codex", null, "C:\\x\\config.toml", ConnectorSkipKind.SetupFailed, false)]
    [InlineData("codex", null, "C:\\x\\config.toml", ConnectorSkipKind.AgentNotInstalled, false)]
    public void The_disk_row_covers_only_a_refusal_of_codex_or_claude_code_it_has_already_drawn(
        string name, string? claude, string? codex, ConnectorSkipKind kind, bool covered)
    {
        var switches = new OverviewPanelViewModel.AgentHookSwitches(claude, codex, codex is null ? null : "hooks");

        Assert.Equal(covered, OverviewPanelViewModel.DiskRowCovers(new ConnectorSkip(name, kind, "r"), switches));
    }
}
