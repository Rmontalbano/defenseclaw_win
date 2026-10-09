using System.Text.Json;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Gateway.Models;
using DefenseClaw.Core.Install;
using DefenseClaw.Core.Paths;

namespace DefenseClaw.App.Tests.Overview;

/// <summary>
/// The notices of the 0.8.10 TUI's Overview that "What needs attention" lacked (CUST-274; <c>OverviewPanelModel.build_notices</c> in
/// <c>tui/services/overview_state.py</c>): a missing skill scanner with its install hint as text to copy, a gateway that answered but is not up
/// yet (info), and a connector roster that cannot be built from config.yaml (error). Synthetic snapshots and configs; no process starts.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class OverviewNoticesTests : IDisposable
{
    private const string CliPath = @"C:\Tools\defenseclaw.exe";

    private const string GuardrailOn = "guardrail:\n  enabled: true\n  connector: claudecode\n  connectors:\n    claudecode:\n      mode: observe\n      hook_fail_mode: open\n";

    private readonly TempDirectory _temp = new();
    private AppServices? _services;

    public void Dispose()
    {
        _services?.Dispose();
        _temp.Dispose();
    }

    private OverviewPanelViewModel Panel(string? configYaml = GuardrailOn)
    {
        _services?.Dispose();
        _services = TestServices.Create(_temp, configYaml);
        return new OverviewPanelViewModel(_services);
    }

    private static GatewayHealth Health(string body) =>
        JsonSerializer.Deserialize<GatewayHealth>("{" + body + "}", new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    /// <summary>A reachable, installed gateway whose CLI is known; the health body defaults to everything running.</summary>
    private static GatewaySnapshot Snapshot(GatewayHealth? health = null, string? cliPath = CliPath) => new()
    {
        State = AppGatewayState.Running,
        Install = InstallState.Running,
        Detail = "ok",
        CliPath = cliPath,
        Health = health ?? Health("\"uptime_ms\":600000,\"api\":{\"state\":\"running\"},\"gateway\":{\"state\":\"disabled\"}"),
        PolledAt = DateTimeOffset.UtcNow,
    };

    private static AttentionRow Row(OverviewPanelViewModel vm, string title) => Assert.Single(vm.Attention, r => r.Title == title);

    private static void NoRow(OverviewPanelViewModel vm, string title) => Assert.DoesNotContain(vm.Attention, r => r.Title == title);

    // ------------------------------------------------------------------ the gateway is starting

    [Theory]
    [InlineData("running", "disabled", false)]
    [InlineData("running", "running", false)]
    [InlineData("ready", "healthy", false)]
    [InlineData("ok", "ok", false)]
    [InlineData(" RUNNING ", "Disabled", false)]
    [InlineData("", "", false)]
    [InlineData("starting", "disabled", true)]
    [InlineData("reconnecting", "disabled", true)]
    [InlineData(" Starting ", "disabled", true)]
    [InlineData("running", "starting", true)]
    [InlineData("running", "reconnecting", true)]
    [InlineData("starting", "reconnecting", true)]
    // The TUI's verdict is "starting" for anything that is not up and not down: the gateway block in error, a state it does not know.
    [InlineData("running", "error", true)]
    [InlineData("running", "failed", true)]
    [InlineData("degraded", "disabled", true)]
    // Down or failed is another verdict (offline, error), and the "not answering" row speaks for it.
    [InlineData("stopped", "disabled", false)]
    [InlineData("offline", "starting", false)]
    [InlineData("down", "disabled", false)]
    [InlineData("error", "disabled", false)]
    [InlineData("failed", "starting", false)]
    [InlineData("running", "stopped", false)]
    [InlineData("running", "offline", false)]
    [InlineData("running", "down", false)]
    public void The_gateway_is_starting_by_the_tuis_own_classification(string api, string gateway, bool starting)
    {
        var health = Health($"\"api\":{{\"state\":\"{api}\"}},\"gateway\":{{\"state\":\"{gateway}\"}}");

        Assert.Equal(starting, OverviewPanelViewModel.GatewayIsStarting(health, out var detail));
        Assert.Equal(starting, detail.Length > 0);
    }

    [Fact]
    public void A_gateway_that_does_not_report_a_subsystem_is_not_starting_for_it_and_nothing_answering_is_not_starting_at_all()
    {
        Assert.False(OverviewPanelViewModel.GatewayIsStarting(Health("\"uptime_ms\":5"), out _));
        Assert.False(OverviewPanelViewModel.GatewayIsStarting(null, out var detail));
        Assert.Empty(detail);
    }

    [Fact]
    public void A_gateway_that_is_starting_is_an_info_row_that_names_what_is_not_up()
    {
        var vm = Panel();

        vm.Apply(Snapshot(Health("\"uptime_ms\":4000,\"api\":{\"state\":\"reconnecting\"},\"gateway\":{\"state\":\"disabled\"}")));

        var row = Row(vm, "The gateway is starting");
        Assert.Equal("Info", row.SeverityKey);
        Assert.Contains("The API subsystem of /health reports reconnecting.", row.Detail, StringComparison.Ordinal);
        Assert.Contains("Health checks will retry automatically", row.Detail, StringComparison.Ordinal);
        Assert.False(row.HasCommand);
    }

    [Fact]
    public void The_row_clears_when_the_gateway_reports_running_and_a_standalone_gateway_explains_itself_instead()
    {
        var vm = Panel();
        var standalone = "\"gateway\":{\"state\":\"disabled\",\"details\":{\"hint\":\"Standalone: no fleet uplink is configured.\"}}";

        // Starting wins over the standalone hint, as in the TUI's if / elif chain.
        vm.Apply(Snapshot(Health($"\"uptime_ms\":4000,\"api\":{{\"state\":\"starting\"}},{standalone}")));
        _ = Row(vm, "The gateway is starting");
        NoRow(vm, "The gateway runs standalone");

        vm.Apply(Snapshot(Health($"\"uptime_ms\":9000,\"api\":{{\"state\":\"running\"}},{standalone}")));
        NoRow(vm, "The gateway is starting");
        Assert.Equal("Standalone: no fleet uplink is configured.", Row(vm, "The gateway runs standalone").Detail);
    }

    [Fact]
    public void A_state_word_the_gateway_wrote_cannot_carry_a_control_character_into_the_row()
    {
        var vm = Panel();

        vm.Apply(Snapshot(Health("\"api\":{\"state\":\"warming\\u202eup\"}")));

        var row = Row(vm, "The gateway is starting");
        Assert.DoesNotContain('\u202e', row.Detail);
        Assert.Contains("\\u202E", row.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_gateway_that_did_not_answer_is_not_starting_it_is_the_not_answering_row()
    {
        var vm = Panel();

        vm.Apply(new GatewaySnapshot { State = AppGatewayState.GatewayStopped, Install = InstallState.GatewayStopped, Detail = "x", PolledAt = DateTimeOffset.UtcNow });

        NoRow(vm, "The gateway is starting");
        _ = Row(vm, "The gateway is not answering");
    }

    // ------------------------------------------------------------------ the connector roster

    [Fact]
    public void Connector_names_the_runtime_rejects_are_a_roster_row_with_the_runtimes_own_sentence()
    {
        var vm = Panel(GuardrailOn + "    ClaudeCode:\n      mode: action\n");

        vm.Apply(Snapshot());

        var row = Row(vm, "Connector roster degraded");
        Assert.Equal("Critical", row.SeverityKey);
        Assert.Equal(
            "guardrail.connectors: 'ClaudeCode' and 'claudecode' refer to the same connector 'claudecode'; keep only one" +
            " - showing a reduced view; check your connector config",
            row.Detail);
    }

    [Fact]
    public void An_empty_connector_name_is_a_roster_row_and_a_clean_map_is_not()
    {
        var empty = Panel("guardrail:\n  enabled: true\n  connectors:\n    '': {}\n    codex: {}\n");
        empty.Apply(Snapshot());
        Assert.Contains("empty connector name is not allowed", Row(empty, "Connector roster degraded").Detail, StringComparison.Ordinal);

        var clean = Panel();
        clean.Apply(Snapshot());
        NoRow(clean, "Connector roster degraded");
    }

    [Fact]
    public void The_roster_row_follows_config_yaml_when_it_is_fixed_and_when_it_breaks_again()
    {
        var vm = Panel();
        vm.Apply(Snapshot());
        NoRow(vm, "Connector roster degraded");

        _ = _temp.WriteFile("config.yaml", GuardrailOn + "    ClaudeCode: {}\n");
        _services!.ReloadConfig();
        vm.Apply(Snapshot());
        _ = Row(vm, "Connector roster degraded");

        _ = _temp.WriteFile("config.yaml", GuardrailOn);
        _services.ReloadConfig();
        vm.Apply(Snapshot());
        NoRow(vm, "Connector roster degraded");
    }

    [Fact]
    public void A_config_yaml_that_could_not_be_read_keeps_its_own_row_and_adds_no_roster_row()
    {
        var vm = Panel();
        _ = _temp.WriteFile("config.yaml", "guardrail: [unclosed\n");
        _services!.ReloadConfig();

        vm.Apply(Snapshot());

        Assert.Equal("config.yaml could not be read", vm.Attention[0].Title);
        NoRow(vm, "Connector roster degraded");
    }

    // ------------------------------------------------------------------ the skill scanner

    [Fact]
    public void A_missing_skill_scanner_is_a_warning_row_with_the_install_hint_as_text_to_copy()
    {
        var vm = Panel();

        vm.Apply(Snapshot());

        var row = Row(vm, "skill-scanner not on PATH");
        Assert.Equal("High", row.SeverityKey);
        Assert.Equal("pip install cisco-ai-skill-scanner", row.Command);
        Assert.Equal(OverviewPanelViewModel.InstallSkillScannerCommand, row.Command);
        Assert.True(row.HasCommand);
        Assert.Contains("skill scans cannot run", row.Detail, StringComparison.Ordinal);
        Assert.Contains("The app never runs it for you", row.Detail, StringComparison.Ordinal);
        Assert.Contains("A suggested command is available", row.ToString(), StringComparison.Ordinal);

        // The Scanners card says the same thing about the same lookup.
        var card = vm.ScannerRows.Single(r => r.Name == "skill-scanner");
        Assert.Equal("not found", card.StateText);

        // Copy only: showing the row ran nothing, and the hint is not one of the CLI's commands.
        Assert.Empty(_services!.Cli.Activity);
        Assert.NotEqual("defenseclaw", OverviewPanelViewModel.InstallSkillScannerCommand.Split(' ')[0]);
    }

    [Fact]
    public void The_hint_names_the_distribution_the_cli_itself_names_not_the_executable()
    {
        // The TUI prints "pip install skill-scanner", which is the name of the program; the package that provides it is cisco-ai-skill-scanner
        // (0.8.10's scanner/skill.py says so in its own error). A copy-and-run hint must not point at another project.
        Assert.EndsWith(" cisco-ai-skill-scanner", OverviewPanelViewModel.InstallSkillScannerCommand, StringComparison.Ordinal);
    }

    [Fact]
    public void An_installed_skill_scanner_has_no_row()
    {
        var scanner = Path.Combine(_temp.Path, "bin", "skill-scanner.exe");
        var paths = new DefenseClawPaths(
            dataDirectory: _temp.Path,
            binDirectory: Path.Combine(_temp.Path, "bin"),
            searchPath: Array.Empty<string>(),
            fileExists: path => string.Equals(path, scanner, StringComparison.OrdinalIgnoreCase));
        _ = _temp.WriteFile("config.yaml", GuardrailOn);
        _services = AppServices.CreateIsolated(paths, claudeSettingsPath: _temp.File("claude-settings.json"));
        var vm = new OverviewPanelViewModel(_services);

        vm.Apply(Snapshot());

        NoRow(vm, "skill-scanner not on PATH");
        Assert.Equal("installed", vm.ScannerRows.Single(r => r.Name == "skill-scanner").StateText);
    }

    [Fact]
    public void Without_a_defenseclaw_cli_there_is_no_scanner_row_only_the_not_found_one()
    {
        var vm = Panel();

        vm.Apply(Snapshot(cliPath: null));
        NoRow(vm, "skill-scanner not on PATH");

        vm.Apply(Snapshot(cliPath: string.Empty));
        NoRow(vm, "skill-scanner not on PATH");
    }

    [Fact]
    public void The_row_follows_whether_the_cli_is_known_in_the_very_rebuild_that_learns_it()
    {
        var vm = Panel();

        // The CLI becomes known (first poll after install): the very rebuild that learns it draws the row.
        vm.Apply(Snapshot(cliPath: null));
        NoRow(vm, "skill-scanner not on PATH");
        vm.Apply(Snapshot());
        _ = Row(vm, "skill-scanner not on PATH");

        vm.Apply(Snapshot(cliPath: null));
        NoRow(vm, "skill-scanner not on PATH");
    }

    [Fact]
    public void The_row_is_not_claimed_while_the_first_lookup_is_still_out_and_appears_when_it_answers()
    {
        using var probe = new DeadPathProbe();
        var bin = _temp.File("bin");
        _ = _temp.WriteFile("config.yaml", GuardrailOn);
        _services = AppServices.CreateIsolated(probe.PathsFor(_temp, bin), claudeSettingsPath: _temp.File("claude-settings.json"));

        var vm = UiThread.Run(() =>
        {
            var panel = new OverviewPanelViewModel(_services);
            panel.Apply(Snapshot());

            // The lookup is stuck behind a dead PATH entry: the card says "checking", and the list does not say "missing".
            Assert.Equal("checking", panel.ScannerRows.Single(r => r.Name == "skill-scanner").StateText);
            NoRow(panel, "skill-scanner not on PATH");
            return panel;
        });

        probe.WaitUntilBlocked();
        probe.Release();

        UiThread.WaitFor(() => vm.Attention.Any(r => r.Title == "skill-scanner not on PATH"), "the missing-scanner row to appear");
        UiThread.Run(() => Assert.Equal("not found", vm.ScannerRows.Single(r => r.Name == "skill-scanner").StateText));
    }

    // ------------------------------------------------------------------ order, and the quiet case

    [Fact]
    public void The_new_rows_sit_where_the_tui_puts_them_gateway_first_then_the_roster_then_the_scanner_after_the_guardrail()
    {
        var vm = Panel("guardrail:\n  enabled: false\n  connectors:\n    claudecode: {}\n    ClaudeCode: {}\n");

        vm.Apply(Snapshot(Health("\"uptime_ms\":4000,\"api\":{\"state\":\"starting\"}")));

        var titles = vm.Attention.Select(r => r.Title).ToList();
        var starting = titles.IndexOf("The gateway is starting");
        var roster = titles.IndexOf("Connector roster degraded");
        var guardrail = titles.IndexOf("Guardrail not configured");
        var scanner = titles.IndexOf("skill-scanner not on PATH");
        Assert.True(starting >= 0 && starting < roster, string.Join(" | ", titles));
        Assert.True(roster < guardrail, string.Join(" | ", titles));
        Assert.True(guardrail < scanner, string.Join(" | ", titles));
    }

    [Fact]
    public void A_healthy_install_with_its_scanner_has_nothing_to_say()
    {
        var scanner = Path.Combine(_temp.Path, "bin", "skill-scanner.exe");
        var paths = new DefenseClawPaths(
            dataDirectory: _temp.Path,
            binDirectory: Path.Combine(_temp.Path, "bin"),
            searchPath: Array.Empty<string>(),
            fileExists: path => string.Equals(path, scanner, StringComparison.OrdinalIgnoreCase));
        _ = _temp.WriteFile("config.yaml", GuardrailOn);
        _services = AppServices.CreateIsolated(paths, claudeSettingsPath: _temp.File("claude-settings.json"));
        var vm = new OverviewPanelViewModel(_services);

        vm.Apply(Snapshot());

        Assert.Equal("Nothing needs attention", Assert.Single(vm.Attention).Title);
    }
}
