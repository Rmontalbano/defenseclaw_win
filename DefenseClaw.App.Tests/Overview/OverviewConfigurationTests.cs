using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.ClaudeCode;
using DefenseClaw.Core.Gateway.Models;

namespace DefenseClaw.App.Tests.Overview;

/// <summary>
/// "What needs attention" (top three, the Mac's tags), the Configuration card (zebra rows, "Show N more settings", the hook fail mode from
/// <c>status --json</c>) and the Connectors table whose rows are the scope selector (CUST-209, CUST-205). Synthetic data only.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class OverviewConfigurationTests : IDisposable
{
    private readonly OverviewScene _scene = OverviewScene.Create(seedAudit: false, seedAgents: false);

    public void Dispose() => _scene.Dispose();

    private OverviewPanelViewModel Panel()
    {
        var vm = new OverviewPanelViewModel(_scene.Services);
        _scene.Publish(OverviewScene.Snapshot());
        vm.Apply(OverviewScene.Snapshot());
        return vm;
    }

    private static ConfigRow Row(OverviewPanelViewModel vm, string label, bool expanded = true)
    {
        if (expanded && !vm.ConfigurationExpanded && vm.HasConfigurationOverflow)
        {
            vm.ToggleConfigurationCommand.Execute(null);
        }

        return vm.ConfigurationRows.Single(r => r.Label == label);
    }

    // ---- What needs attention ----

    private static GatewaySnapshot ManyProblems() => OverviewScene.Snapshot(health: OverviewScene.Health(eventHistoryFailure: "sqlite_write_failed")) with
    {
        State = AppGatewayState.WslGatewayDetected,
        CriticalAlertCount = 2,
        RecentAlerts = new[] { new GatewayAlert { Id = "a", Severity = "HIGH", Timestamp = DateTimeOffset.UtcNow } },
        AlertsUnavailable = null,
        FailModeDrift = new FailModeDrift
        {
            EnvFailMode = "closed",
            GatewayFailMode = "open",
            GatewaySource = FailModeDrift.StatusSource,
            GuardrailMode = "observe",
            SettingsPath = "C:\\Users\\someone\\.claude\\settings.json",
        },
    };

    [Fact]
    public void Attention_shows_the_first_three_and_show_all_reveals_the_rest_and_hides_them_again()
    {
        var vm = Panel();
        vm.Apply(ManyProblems());

        Assert.True(vm.Attention.Count > OverviewPanelViewModel.AttentionRowsShown, $"only {vm.Attention.Count} rows");
        Assert.Equal(3, vm.VisibleAttention.Count);
        Assert.Equal(vm.Attention.Take(3).Select(r => r.Title), vm.VisibleAttention.Select(r => r.Title));
        Assert.True(vm.HasAttentionOverflow);
        Assert.Equal($"Show all {vm.Attention.Count}", vm.AttentionMoreText);

        vm.ToggleAttentionCommand.Execute(null);
        Assert.Equal(vm.Attention.Select(r => r.Title), vm.VisibleAttention.Select(r => r.Title));
        Assert.Equal("Show fewer", vm.AttentionMoreText);

        vm.ToggleAttentionCommand.Execute(null);
        Assert.Equal(3, vm.VisibleAttention.Count);
    }

    [Fact]
    public void Everything_the_old_overview_said_is_still_in_attention_the_wsl_gateway_the_drift_and_the_alerts()
    {
        var vm = Panel();

        vm.Apply(ManyProblems());

        Assert.Contains(vm.Attention, r => r.Title.StartsWith("A WSL gateway owns the API port", StringComparison.Ordinal));
        Assert.Contains(vm.Attention, r => r.Title.StartsWith("claudecode: settings.json overrides the hook fail mode", StringComparison.Ordinal));
        Assert.Contains(vm.Attention, r => r.Title.Contains("CRITICAL alert", StringComparison.Ordinal));
        Assert.Contains(vm.Attention, r => r.Title.Contains("HIGH finding", StringComparison.Ordinal));
        Assert.Contains(vm.Attention, r => r.Title.Contains("event history", StringComparison.Ordinal));
    }

    [Fact]
    public void A_short_list_has_no_show_all_and_a_list_that_shrinks_collapses()
    {
        var vm = Panel();
        vm.Apply(ManyProblems());
        vm.ToggleAttentionCommand.Execute(null);
        Assert.True(vm.ShowAllAttention);

        vm.Apply(OverviewScene.Snapshot());

        Assert.Single(vm.Attention);
        Assert.False(vm.HasAttentionOverflow);
        Assert.False(vm.ShowAllAttention);
        Assert.Empty(vm.AttentionMoreText);
        Assert.Single(vm.VisibleAttention);
    }

    [Theory]
    [InlineData("Critical", "[!]")]
    [InlineData("Bad", "[!]")]
    [InlineData("High", "[*]")]
    [InlineData("Warn", "[*]")]
    [InlineData("Medium", "[*]")]
    [InlineData("Ok", "[OK]")]
    [InlineData("Info", "[>]")]
    [InlineData("Low", "[>]")]
    [InlineData("Neutral", "[>]")]
    public void A_row_has_the_macs_bracket_tag_for_its_severity_and_keeps_its_severity_word(string severity, string tag)
    {
        var row = new AttentionRow { Title = "t", SeverityKey = severity };

        Assert.Equal(tag, row.Tag);
        Assert.StartsWith(severity + ":", row.ToString(), StringComparison.Ordinal);
    }

    // ---- Configuration ----

    [Fact]
    public void The_card_shows_four_zebra_rows_and_offers_the_rest()
    {
        var vm = Panel();

        Assert.Equal(OverviewPanelViewModel.ConfigurationRowsShown, vm.ConfigurationRows.Count);
        Assert.True(vm.HasConfigurationOverflow);
        Assert.Equal(new[] { false, true, false, true }, vm.ConfigurationRows.Select(r => r.Alternate).ToArray());

        vm.ToggleConfigurationCommand.Execute(null);
        var total = vm.ConfigurationRows.Count;
        Assert.True(total > 4);
        Assert.Equal("Show Fewer Settings", vm.ConfigurationMoreText);
        Assert.All(vm.ConfigurationRows.Select((r, i) => (r, i)), pair => Assert.Equal(pair.i % 2 == 1, pair.r.Alternate));

        vm.ToggleConfigurationCommand.Execute(null);
        Assert.Equal(4, vm.ConfigurationRows.Count);
        Assert.Equal($"Show {total - 4} More Settings", vm.ConfigurationMoreText);
    }

    [Fact]
    public void Without_status_json_the_rows_are_config_yamls_and_the_fail_mode_says_so()
    {
        var vm = Panel();

        Assert.Equal("2 active", Row(vm, "Agents").Value);
        Assert.Equal("per-connector (see the connectors table)", Row(vm, "Policy posture").Value);
        Assert.Equal("2 connectors (hook observability)", Row(vm, "Enforcement").Value);
        Assert.Equal("open (config.yaml)", Row(vm, "Hook fail mode (claudecode)").Value);
        Assert.Equal("Neutral", Row(vm, "Hook fail mode (claudecode)").ToneKey);
        Assert.Equal("enabled · scanner local", Row(vm, "Guardrail").Value);
        Assert.Equal("not set", Row(vm, "Deployment mode").Value);
        Assert.Equal("on · mode enhanced · scan every 5 min", Row(vm, "AI discovery").Value);
        Assert.Equal($"127.0.0.1:{OverviewScene.Port}", Row(vm, "Gateway API").Value);
        Assert.Contains(_scene.Temp.Path, Row(vm, "Data directory").Value, StringComparison.Ordinal);
        Assert.Equal(_scene.Services.Config.Path, Row(vm, "Config file").Value);
        Assert.DoesNotContain(vm.ConfigurationRows, r => r.Label == "Environment");
    }

    [Fact]
    public void With_status_json_the_fail_mode_is_what_the_hook_effectively_obeys_with_its_provenance_and_drift()
    {
        var vm = Panel();

        vm.ApplyStatus(DefenseClawStatusReader.Parse(OverviewScene.StatusJson), DateTimeOffset.UtcNow.AddMinutes(-3));

        // Collapsed, the four rows the card opens with include the hook fail mode of the connector that has drifted, where an operator looks first.
        Assert.Contains("Hook fail mode (claudecode)", vm.ConfigurationRows.Select(r => r.Label));

        var claude = Row(vm, "Hook fail mode (claudecode)");
        Assert.Equal("closed (effective, set by claude-env) · config.yaml says open · drift: registration-stale, windows-sidecar-closed", claude.Value);
        Assert.Equal("Warn", claude.ToneKey);

        var hermes = Row(vm, "Hook fail mode (hermes)");
        Assert.Equal("open (effective, set by config)", hermes.Value);
        Assert.Equal("Neutral", hermes.ToneKey);

        Assert.Equal("windows", Row(vm, "Environment").Value);
        Assert.Equal("global user config", Row(vm, "Scope").Value);
        Assert.Equal("not available", Row(vm, "Sandbox").Value);
        Assert.Equal("off (disabled)", Row(vm, "Application protection").Value);
        Assert.StartsWith("Fail mode and deployment rows from defenseclaw status --json, read", vm.ConfigurationSourceNote, StringComparison.Ordinal);
    }

    [Fact]
    public void A_single_connector_install_reads_agent_and_one_fail_mode_row()
    {
        using var single = OverviewScene.Create(twoConnectors: false, seedAudit: false, seedAgents: false);
        var vm = new OverviewPanelViewModel(single.Services);
        single.Publish(OverviewScene.Snapshot(twoConnectors: false));
        vm.Apply(OverviewScene.Snapshot(twoConnectors: false));

        Assert.Equal("claudecode", Row(vm, "Agent").Value);
        Assert.Equal("claudecode: observe (default)", Row(vm, "Policy posture").Value);
        Assert.Equal("claudecode hook observability (observe)", Row(vm, "Enforcement").Value);
        Assert.Equal("open (config.yaml)", Row(vm, "Hook fail mode").Value);
    }

    [Fact]
    public void Scoped_to_a_connector_the_card_is_that_connectors_with_the_global_rows_marked()
    {
        var vm = Panel();
        vm.ApplyStatus(DefenseClawStatusReader.Parse(OverviewScene.StatusJson));
        _scene.Services.ConnectorScope.Set("hermes");
        vm.ApplyScope();

        Assert.Equal("Configuration · hermes", vm.ConfigurationTitle);
        Assert.Equal("Hermes (hermes)", Row(vm, "Connector").Value);
        Assert.Equal("observe", Row(vm, "Mode").Value);
        Assert.Equal("strict", Row(vm, "Rule pack").Value);
        Assert.Equal("enabled", Row(vm, "Guardrail").Value);
        Assert.Equal("open (effective, set by config)", Row(vm, "Hook fail mode").Value);
        Assert.Equal("running", Row(vm, "Status").Value);
        Assert.Contains("ago", Row(vm, "Last activity").Value, StringComparison.Ordinal);
        Assert.Contains(vm.ConfigurationRows, r => r.Label == "Deployment mode (global)");
        Assert.DoesNotContain(vm.ConfigurationRows, r => r.Label == "Agents");
    }

    [Theory]
    [InlineData(null, "default")]
    [InlineData("", "default")]
    [InlineData("   ", "default")]
    [InlineData("C:\\packs\\strict", "strict")]
    [InlineData("C:\\packs\\strict\\", "strict")]
    [InlineData("/home/me/packs/loose", "loose")]
    [InlineData("strict", "strict")]
    public void A_rule_pack_is_named_by_the_last_segment_of_its_directory(string? directory, string expected) =>
        Assert.Equal(expected, OverviewPanelViewModel.RulePackName(directory));

    // ---- Connectors ----

    [Fact]
    public void The_table_has_a_row_per_connector_with_the_macs_columns()
    {
        var vm = Panel();
        vm.ApplyStatus(DefenseClawStatusReader.Parse(OverviewScene.StatusJson));

        var claude = vm.ConnectorRows.Single(r => r.Name == "claudecode");
        Assert.Equal("Claude Code (claudecode)", claude.DisplayName);
        Assert.Equal("default", claude.RulePack);
        Assert.Equal(3707.ToString("N0", System.Globalization.CultureInfo.CurrentCulture), claude.CallsText);
        Assert.Equal("running", claude.StateText);
        Assert.Equal("3m ago", claude.LastActivityShort);
        Assert.Equal("Neutral", claude.BlocksKey);

        var hermes = vm.ConnectorRows.Single(r => r.Name == "hermes");
        Assert.Equal("Hermes (hermes)", hermes.DisplayName);
        Assert.Equal("strict", hermes.RulePack);
        Assert.Equal("52", hermes.CallsText);
        Assert.Equal("3", hermes.BlocksText);
        Assert.Equal("Bad", hermes.BlocksKey);
        Assert.Equal("4h ago", hermes.LastActivityShort);
        Assert.Equal("0", hermes.AlertsText);
    }

    [Fact]
    public void A_connector_only_config_yaml_names_is_not_running_and_one_nothing_names_is_not_configured()
    {
        var vm = Panel();
        vm.Apply(OverviewScene.Snapshot(running: false) with { ActiveConnectors = new[] { "claudecode", "hermes", "codex" } });

        Assert.Equal("not running", vm.ConnectorRows.Single(r => r.Name == "claudecode").StateText);
        Assert.Equal("not configured", vm.ConnectorRows.Single(r => r.Name == "codex").StateText);
        Assert.Equal("never", vm.ConnectorRows.Single(r => r.Name == "codex").LastActivityShort);
    }

    [Fact]
    public void The_effective_fail_mode_makes_an_observe_connector_with_a_closed_hook_a_warning_and_the_drift_is_on_the_row()
    {
        var vm = Panel();

        vm.ApplyStatus(DefenseClawStatusReader.Parse(OverviewScene.StatusJson));

        var claude = vm.ConnectorRows.Single(r => r.Name == "claudecode");
        Assert.Equal("closed", claude.FailMode);
        Assert.True(claude.HasWarning);
        Assert.True(claude.HasNotes);
        Assert.Contains("fail-mode drift: registration-stale, windows-sidecar-closed", claude.Drift, StringComparison.Ordinal);
        Assert.False(vm.ConnectorRows.Single(r => r.Name == "hermes").HasNotes);
    }

    [Fact]
    public async Task The_alerts_column_follows_the_unacknowledged_counts_per_connector()
    {
        using var scene = OverviewScene.Create();
        var vm = new OverviewPanelViewModel(scene.Services);
        scene.Publish(OverviewScene.Snapshot());
        vm.Apply(OverviewScene.Snapshot());
        Assert.Equal("0", vm.ConnectorRows.Single(r => r.Name == "hermes").AlertsText);

        await scene.Services.AlertCounts.RefreshAsync();
        vm.Apply(OverviewScene.Snapshot());

        Assert.Equal("1", vm.ConnectorRows.Single(r => r.Name == "claudecode").AlertsText);
        Assert.Equal("1", vm.ConnectorRows.Single(r => r.Name == "hermes").AlertsText);
        Assert.Equal("High", vm.ConnectorRows.Single(r => r.Name == "hermes").AlertsKey);
    }

    [Fact]
    public void A_ticking_field_updates_the_row_in_place_and_a_structural_change_replaces_it()
    {
        var vm = Panel();
        var before = vm.ConnectorRows.Single(r => r.Name == "hermes");

        var moved = OverviewScene.Health();
        vm.Apply(OverviewScene.Snapshot(health: moved));
        Assert.Same(before, vm.ConnectorRows.Single(r => r.Name == "hermes"));

        vm.ApplyStatus(DefenseClawStatusReader.Parse(OverviewScene.StatusJson));
        Assert.NotSame(before, vm.ConnectorRows.Single(r => r.Name == "hermes"));
    }

    // ---- Scope ----

    [Fact]
    public void Selecting_a_row_scopes_the_overview_and_the_cards_say_so()
    {
        UiThread.Run(() =>
        {
            var vm = Panel();
            vm.SetActive(true);
            Assert.Equal("Select a row to scope the Overview to that connector.", vm.ConnectorCaption);
            Assert.True(vm.HasConnectorCaption);

            vm.SelectedConnector = vm.ConnectorRows.Single(r => r.Name == "hermes");

            Assert.Equal("hermes", _scene.Services.ConnectorScope.Current);
            Assert.True(vm.IsScoped);
            Assert.Equal("Scanners · hermes", vm.ScannersTitle);
            Assert.Equal("Enforcement · hermes", vm.EnforcementTitle);
            Assert.Equal("Configuration · hermes", vm.ConfigurationTitle);
            Assert.Equal("Overview scoped to hermes.", vm.ConnectorCaption);
            Assert.Equal("Open hermes Alerts →", vm.ScopedAlertsText);

            // The Mac puts the connector's policy first in Scanners.
            Assert.Equal("policy", vm.ScannerRows[0].Name);
            Assert.Equal("observe", vm.ScannerRows[0].StateText);
            Assert.Contains("rule pack strict", vm.ScannerRows[0].Detail, StringComparison.Ordinal);

            vm.ClearScopeCommand.Execute(null);
            Assert.Null(_scene.Services.ConnectorScope.Current);
            Assert.Null(vm.SelectedConnector);
            Assert.Equal("Scanners", vm.ScannersTitle);
            Assert.DoesNotContain(vm.ScannerRows, r => r.Name == "policy");

            vm.SetActive(false);
        });
    }

    [Fact]
    public void A_scope_set_elsewhere_moves_the_selection_and_a_reset_clears_it()
    {
        UiThread.Run(() =>
        {
            var vm = Panel();
            vm.SetActive(true);

            _scene.Services.ConnectorScope.Set("claudecode");
            Assert.Equal("claudecode", vm.SelectedConnector?.Name);

            _scene.Services.ConnectorScope.Set(null);
            Assert.Null(vm.SelectedConnector);

            vm.SetActive(false);
        });
    }

    [Fact]
    public void A_one_connector_install_keeps_the_scope_at_all_and_hides_the_caption_but_still_highlights_the_row()
    {
        using var single = OverviewScene.Create(twoConnectors: false, seedAudit: false, seedAgents: false);
        UiThread.Run(() =>
        {
            var vm = new OverviewPanelViewModel(single.Services);
            single.Publish(OverviewScene.Snapshot(twoConnectors: false));
            vm.Apply(OverviewScene.Snapshot(twoConnectors: false));
            vm.SetActive(true);

            Assert.Empty(vm.ConnectorCaption);
            Assert.False(vm.HasConnectorCaption);

            vm.SelectedConnector = vm.ConnectorRows.Single();

            Assert.Null(single.Services.ConnectorScope.Current);
            Assert.False(vm.IsScoped);
            Assert.Equal("Scanners", vm.ScannersTitle);
            Assert.NotNull(vm.SelectedConnector);

            vm.SetActive(false);
        });
    }

    [Fact]
    public void A_poll_that_replaces_the_selected_row_does_not_reset_the_scope()
    {
        UiThread.Run(() =>
        {
            var vm = Panel();
            vm.SetActive(true);
            vm.SelectedConnector = vm.ConnectorRows.Single(r => r.Name == "hermes");
            Assert.Equal("hermes", _scene.Services.ConnectorScope.Current);

            // status --json arrives: the row's structure changes (a friendly name), so the row object is replaced.
            var replaced = vm.ConnectorRows.Single(r => r.Name == "hermes");
            vm.ApplyStatus(DefenseClawStatusReader.Parse(OverviewScene.StatusJson));

            Assert.NotSame(replaced, vm.ConnectorRows.Single(r => r.Name == "hermes"));
            Assert.Equal("hermes", _scene.Services.ConnectorScope.Current);
            Assert.Equal("hermes", vm.SelectedConnector?.Name);

            vm.SetActive(false);
        });
    }

    [Fact]
    public void The_scoped_alerts_link_opens_alerts()
    {
        Panel().OpenScopedAlertsCommand.Execute(null);

        Assert.Equal("alerts", _scene.Services.Navigation.Pending!.PanelId);
    }
}
