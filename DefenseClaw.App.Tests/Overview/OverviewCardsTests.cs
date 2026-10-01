using System.Globalization;
using System.Text.Json;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Audit;
using DefenseClaw.Core.Gateway.Models;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.App.Tests.Overview;

/// <summary>
/// The Overview's data cards (CUST-209): Activity (the hourly query and the summary), Observability (the destinations and the gateway's own
/// event-history failure), the Discovered AI agents and the <c>status --json</c> they share the panel with. Synthetic data only.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class OverviewCardsTests : IDisposable
{
    private readonly OverviewScene _scene = OverviewScene.Create();

    public void Dispose() => _scene.Dispose();

    private OverviewPanelViewModel Panel() => new(_scene.Services);

    private static string N(int value) => value.ToString("N0", CultureInfo.CurrentCulture);

    // ---- Activity ----

    [Fact]
    public async Task The_hourly_read_fills_the_chart_with_twenty_four_hours_and_the_summary_with_the_days_decisions()
    {
        var vm = Panel();

        await vm.RefreshHourlyAsync(CancellationToken.None);

        Assert.Equal(24, vm.HourlyBuckets.Count);
        Assert.True(vm.HasHourlyData);
        Assert.Equal(9, vm.HourlyBuckets.Sum(b => b.Allowed));
        Assert.Equal(2, vm.HourlyBuckets.Sum(b => b.Blocked));
        Assert.Equal("9 allowed · 2 blocked", vm.ActivitySummary.Single(i => i.Label == "Hook decisions").Value);

        // The other numbers are status --json's, and say so until that has been read.
        Assert.Equal("—", vm.ActivitySummary.Single(i => i.Label == "Skills").Value);
        Assert.Equal("—", vm.ActivitySummary.Single(i => i.Label == "Total scans").Value);

        vm.ApplyStatus(DefenseClawStatusReader.Parse(OverviewScene.StatusJson));
        Assert.Equal("0 blocked · 3 allowed", vm.ActivitySummary.Single(i => i.Label == "Skills").Value);
        Assert.Equal("1 blocked · 1 allowed", vm.ActivitySummary.Single(i => i.Label == "MCPs").Value);
        Assert.Equal(N(5032), vm.ActivitySummary.Single(i => i.Label == "Total scans").Value);
    }

    [Fact]
    public async Task The_chart_is_described_for_a_screen_reader_with_the_totals_and_the_busiest_hour()
    {
        var vm = Panel();
        await vm.RefreshHourlyAsync(CancellationToken.None);

        Assert.StartsWith("Hook decisions per hour, last 24 hours: 9 allowed, 2 blocked.", vm.HourlyAutomationSummary, StringComparison.Ordinal);
        Assert.Contains("Busiest hour", vm.HourlyAutomationSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_day_with_no_decisions_says_so_instead_of_drawing_an_empty_chart()
    {
        using var quiet = OverviewScene.Create(seedAudit: false);
        var vm = new OverviewPanelViewModel(quiet.Services);

        await vm.RefreshHourlyAsync(CancellationToken.None);

        Assert.False(vm.HasHourlyData);
        Assert.Empty(vm.HourlyBuckets);
        Assert.Contains("No audit database yet", vm.ActivityNote, StringComparison.Ordinal);
        Assert.Equal("—", vm.ActivitySummary.Single(i => i.Label == "Hook decisions").Value);
    }

    [Fact]
    public async Task A_database_without_the_index_says_why_there_is_no_chart_and_a_broken_one_says_it_could_not_be_read()
    {
        SqliteConnection.ClearAllPools();
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _scene.Temp.File("audit.db"), Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"DROP INDEX {HourlyActivityReader.IndexName}";
            _ = command.ExecuteNonQuery();
        }

        var vm = Panel();
        await vm.RefreshHourlyAsync(CancellationToken.None);
        Assert.False(vm.HasHourlyData);
        Assert.Contains(HourlyActivityReader.IndexName, vm.ActivityNote, StringComparison.Ordinal);

        SqliteConnection.ClearAllPools();
        File.WriteAllText(_scene.Temp.File("audit.db"), "not a database");
        await vm.RefreshHourlyAsync(CancellationToken.None);
        Assert.Contains("could not be read", vm.ActivityNote, StringComparison.Ordinal);
        Assert.Empty(vm.HourlyBuckets);
    }

    [Fact]
    public async Task A_cancelled_hourly_read_leaves_the_card_as_it_was()
    {
        var vm = Panel();
        await vm.RefreshHourlyAsync(CancellationToken.None);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await vm.RefreshHourlyAsync(cancelled.Token);

        Assert.Equal(24, vm.HourlyBuckets.Count);
        Assert.True(vm.HasHourlyData);
    }

    // ---- Observability ----

    [Fact]
    public void Every_destination_and_sink_the_gateway_lists_is_a_row_with_its_state_and_signals()
    {
        var vm = Panel();

        vm.Apply(OverviewScene.Snapshot());

        Assert.True(vm.HasObservabilityRows);
        var local = vm.ObservabilityRows.Single(r => r.Name == "local-sqlite");
        Assert.Equal("otel", local.Target);
        Assert.Equal("sqlite", local.Kind);
        Assert.Equal("healthy", local.State);
        Assert.Equal("Ok", local.StateKey);
        Assert.Equal("logs", local.Signals);
        Assert.Contains("activated", local.Detail, StringComparison.Ordinal);

        var splunk = vm.ObservabilityRows.Single(r => r.Name == "splunk-hec");
        Assert.Equal("splunk_hec", splunk.Kind);
        Assert.Equal("degraded", splunk.State);
        Assert.Equal("Warn", splunk.StateKey);
        Assert.Equal("logs, traces", splunk.Signals);
        Assert.Contains("retrying", splunk.Detail, StringComparison.Ordinal);
        Assert.Contains("120 accepted", splunk.Detail, StringComparison.Ordinal);

        var sink = vm.ObservabilityRows.Single(r => r.Name == "audit-file");
        Assert.Equal("audit_sinks", sink.Target);
        Assert.Equal("jsonl", sink.Kind);
        Assert.Equal("audit-events", sink.Signals);
        Assert.Equal("enabled", sink.State);
    }

    [Theory]
    [InlineData("healthy", "Ok")]
    [InlineData("ENABLED", "Ok")]
    [InlineData("error", "Bad")]
    [InlineData("failed", "Bad")]
    [InlineData("degraded", "Warn")]
    [InlineData("disabled", "Neutral")]
    [InlineData("something new", "Neutral")]
    public void A_destination_state_has_a_tone_the_theme_knows(string state, string tone) =>
        Assert.Equal(tone, OverviewPanelViewModel.ObservabilityTone(state));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_gateways_event_history_failure_is_shown_wherever_the_build_puts_it_and_raised_as_attention(bool inDetails)
    {
        var vm = Panel();

        vm.Apply(OverviewScene.Snapshot(health: OverviewScene.Health(eventHistoryFailure: "sqlite_write_failed", failureInDetails: inDetails)));

        Assert.True(vm.HasEventHistoryFailure);
        Assert.Equal("sqlite_write_failed", vm.EventHistoryFailure);
        var row = Assert.Single(vm.Attention, r => r.Title.Contains("event history", StringComparison.Ordinal));
        Assert.Equal("High", row.SeverityKey);
        Assert.Contains("sqlite_write_failed", row.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void An_event_history_failure_at_the_top_level_of_health_is_found_too_and_an_object_is_spelled_out()
    {
        var whole = OverviewScene.HealthJson();
        var json = whole[..^1] + ",\"event_history_failure\":{\"reason\":\"sqlite_write_failed\",\"count\":3}}";
        var health = JsonSerializer.Deserialize<GatewayHealth>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        Assert.Equal("sqlite_write_failed · count: 3", OverviewPanelViewModel.FindFailure(health));
    }

    [Fact]
    public void No_failure_means_no_banner_and_no_attention_row()
    {
        var vm = Panel();

        vm.Apply(OverviewScene.Snapshot());

        Assert.False(vm.HasEventHistoryFailure);
        Assert.Empty(vm.EventHistoryFailure);
        Assert.DoesNotContain(vm.Attention, r => r.Title.Contains("event history", StringComparison.Ordinal));
    }

    [Fact]
    public void Without_a_gateway_the_card_says_the_destinations_cannot_be_listed()
    {
        var vm = Panel();

        vm.Apply(OverviewScene.Snapshot(running: false));

        Assert.False(vm.HasObservabilityRows);
        Assert.Contains("not answering", vm.ObservabilityEmptyText, StringComparison.Ordinal);
    }

    // ---- status --json ----

    [Fact]
    public void Status_json_is_read_with_the_fail_mode_provenance_and_drift_of_each_connector()
    {
        var status = DefenseClawStatusReader.Parse(OverviewScene.StatusJson);

        Assert.Equal("windows", status.Environment);
        Assert.Equal(string.Empty, status.DeploymentMode);
        Assert.Equal("global user config", status.Scope);
        Assert.False(status.SandboxAvailable);
        Assert.Equal(3, status.AllowedSkills);
        Assert.Equal(1, status.BlockedMcps);
        Assert.Equal(5032, status.TotalScans);
        Assert.False(status.ApplicationProtectionEnabled);

        var claude = status.Connector("CLAUDECODE")!;
        Assert.Equal("Claude Code", claude.Friendly);
        Assert.Equal("closed", claude.FailMode!.Effective);
        Assert.Equal("claude-env", claude.FailMode.Provenance);
        Assert.Equal("open", claude.FailMode.Configured);
        Assert.False(claude.FailMode.Current);
        Assert.Equal(new[] { "registration-stale", "windows-sidecar-closed" }, claude.FailMode.Drift);
        Assert.True(claude.FailMode.HasDrift);
        Assert.False(status.Connector("hermes")!.FailMode!.HasDrift);
        Assert.Null(status.Connector("nobody"));
    }

    [Fact]
    public void Status_json_tolerates_a_missing_field_and_refuses_what_is_not_an_object()
    {
        var sparse = DefenseClawStatusReader.Parse("{\"environment\":\"windows\",\"connectors\":[{\"name\":\"x\"},{\"friendly\":\"no name\"},7]}");

        Assert.Equal("windows", sparse.Environment);
        Assert.Null(sparse.TotalScans);
        Assert.Null(sparse.SandboxAvailable);
        Assert.Equal("x", Assert.Single(sparse.Connectors).Name);
        Assert.Null(sparse.Connectors[0].FailMode);

        _ = Assert.ThrowsAny<JsonException>(() => DefenseClawStatusReader.Parse("[1,2]"));
        _ = Assert.ThrowsAny<JsonException>(() => DefenseClawStatusReader.Parse("not json"));
    }

    [Fact]
    public void The_one_cli_read_the_panel_makes_is_read_only_and_is_the_documented_argv()
    {
        Assert.Equal(new[] { "status", "--json" }, OverviewPanelViewModel.StatusArgv);
        Assert.Equal(DefenseClaw.Core.Cli.CommandTier.ReadOnly, CommandReview.ResolveTier(OverviewPanelViewModel.StatusArgv));
    }

    [Fact]
    public async Task Without_the_cli_the_configuration_says_its_fail_mode_rows_are_config_yamls_alone()
    {
        var vm = Panel();
        vm.Apply(OverviewScene.Snapshot());

        await vm.RefreshStatusAsync(force: true, CancellationToken.None);

        Assert.Contains("was not found", vm.ConfigurationSourceNote, StringComparison.Ordinal);
        Assert.Contains("config.yaml", vm.ConfigurationSourceNote, StringComparison.Ordinal);
        Assert.Empty(_scene.Services.Cli.Activity);
    }

    [Fact]
    public async Task The_status_read_is_gated_to_ten_minutes_and_a_refresh_forces_it()
    {
        var vm = Panel();
        await vm.RefreshStatusAsync(force: false, CancellationToken.None);
        var first = vm.ConfigurationSourceNote;
        Assert.NotEmpty(first);

        // A second, unforced call inside the window is a no-op: the gate was stamped by the first, even though it failed.
        vm.ApplyStatus(DefenseClawStatusReader.Parse(OverviewScene.StatusJson));
        var afterApply = vm.ConfigurationSourceNote;
        await vm.RefreshStatusAsync(force: false, CancellationToken.None);
        Assert.Equal(afterApply, vm.ConfigurationSourceNote);
        Assert.Equal(TimeSpan.FromMinutes(10), OverviewPanelViewModel.StatusRefreshInterval);
    }

    // ---- Discovered AI agents ----

    [Fact]
    public async Task The_card_shows_the_top_eight_agents_not_the_models_with_the_macs_tags_and_the_overflow()
    {
        var vm = Panel();
        vm.Apply(OverviewScene.Snapshot());

        await vm.RefreshAgentsAsync(CancellationToken.None);

        Assert.True(vm.HasAgents);
        Assert.Equal(8, vm.AgentRows.Count);
        Assert.Empty(vm.AgentsNote);

        // New first, then changed, then the rest by confidence; one row per connector however many signals back it; the gone one falls after the cap.
        Assert.Equal(new[] { "Codex", "Cursor", "Claude Code", "Antigravity", "Gemini CLI", "GitHub Copilot", "Hermes Agent", "OpenClaw" }, vm.AgentRows.Select(r => r.Name).ToArray());
        Assert.Equal("[NEW]", vm.AgentRows[0].Badge);
        Assert.Equal("Ok", vm.AgentRows[0].BadgeKey);
        Assert.Equal("[CHG]", vm.AgentRows[1].Badge);
        Assert.Equal("[OK ]", vm.AgentRows[2].Badge);
        Assert.Equal("98%", vm.AgentRows[2].Confidence);
        Assert.Equal("Anthropic (claudecode)", vm.AgentRows[2].Vendor);
        Assert.Equal("seen 3m ago", vm.AgentRows[0].Seen);

        // 10 distinct agents (Windsurf and Qodo beyond the eight). The Mac's "+N more".
        Assert.Equal("+2 more", vm.AgentsOverflowText);
        Assert.StartsWith("11 active", vm.AgentsSummary, StringComparison.Ordinal);
        Assert.Contains("1 new", vm.AgentsSummary, StringComparison.Ordinal);
        Assert.Contains("1 changed", vm.AgentsSummary, StringComparison.Ordinal);
        Assert.Contains("1 gone", vm.AgentsSummary, StringComparison.Ordinal);
        Assert.Contains("mode enhanced", vm.AgentsSummary, StringComparison.Ordinal);
        Assert.Contains("scanned", vm.AgentsSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_file_and_an_unreadable_one_each_say_what_to_do_or_what_happened()
    {
        using var none = OverviewScene.Create(seedAgents: false, seedAudit: false);
        var vm = new OverviewPanelViewModel(none.Services);
        await vm.RefreshAgentsAsync(CancellationToken.None);
        Assert.False(vm.HasAgents);
        Assert.Contains("No scan has been recorded", vm.AgentsNote, StringComparison.Ordinal);
        Assert.Contains("defenseclaw agent discovery scan", vm.AgentsNote, StringComparison.Ordinal);

        File.WriteAllText(none.Temp.File("ai_discovery_state.json"), "{ broken");
        await vm.RefreshAgentsAsync(CancellationToken.None);
        Assert.Contains("could not be read", vm.AgentsNote, StringComparison.Ordinal);
        Assert.Empty(vm.AgentRows);
        Assert.Empty(vm.AgentsSummary);

        File.WriteAllText(none.Temp.File("ai_discovery_state.json"), "{\"version\":2,\"signals\":{}}");
        await vm.RefreshAgentsAsync(CancellationToken.None);
        Assert.Contains("No AI usage detected", vm.AgentsNote, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_discovery_off_in_config_and_no_file_the_card_says_it_is_off()
    {
        using var off = OverviewScene.Create(seedAgents: false, seedAudit: false);
        File.WriteAllText(off.Temp.File("config.yaml"), "gateway:\n  api_port: " + OverviewScene.Port + "\nai_discovery:\n  enabled: false\n");
        off.Services.ReloadConfig();
        var vm = new OverviewPanelViewModel(off.Services);

        await vm.RefreshAgentsAsync(CancellationToken.None);

        Assert.Contains("AI discovery is off", vm.AgentsNote, StringComparison.Ordinal);
    }

    [Fact]
    public void See_all_opens_the_ai_discovery_panel()
    {
        Panel().SeeAllAgentsCommand.Execute(null);

        Assert.Equal(new NavigationRequest("ai-discovery"), _scene.Services.Navigation.Pending);
    }

    [Fact]
    public void Agent_ages_are_the_macs_words()
    {
        var now = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal("now", OverviewAgentReader.Age(now.AddSeconds(5), now));
        Assert.Equal("50s ago", OverviewAgentReader.Age(now.AddSeconds(-50), now));
        Assert.Equal("3m ago", OverviewAgentReader.Age(now.AddMinutes(-3), now));
        Assert.Equal("4h ago", OverviewAgentReader.Age(now.AddHours(-4), now));
        Assert.Equal("2d ago", OverviewAgentReader.Age(now.AddDays(-2), now));
    }

    [Fact]
    public void An_agent_row_is_spoken_as_a_sentence_not_a_record_dump()
    {
        var row = new DiscoveredAgentRow { Id = "x", Badge = "[NEW]", Name = "Codex", Vendor = "OpenAI (codex)", Confidence = "98%", Seen = "seen 3m ago" };

        Assert.Equal("Codex. OpenAI (codex). new. 98% confidence. seen 3m ago", row.ToString());
        Assert.DoesNotContain("[", new DiscoveredAgentRow { Id = "y", Badge = "[OK ]", Name = "Claude Code" }.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Signals_without_a_connector_are_told_apart_by_their_vendor_and_name_and_a_bad_confidence_is_clamped()
    {
        var now = DateTimeOffset.UtcNow;
        var json = "{\"signals\":[" +
                   "{\"name\":\"Tool\",\"vendor\":\"Acme\",\"category\":\"ai_cli\",\"confidence\":1.7,\"state\":\"seen\"}," +
                   "{\"name\":\"Tool\",\"vendor\":\"Acme\",\"category\":\"ai_cli\",\"confidence\":0.5,\"state\":\"seen\"}," +
                   "{\"name\":\"Other\",\"vendor\":\"Acme\",\"category\":\"ai_cli\",\"state\":\"seen\"}]}";

        var agents = OverviewAgentReader.Parse(json, now);

        Assert.Equal(2, agents.Rows.Count);
        Assert.Equal("100%", agents.Rows[0].Confidence);
        Assert.Equal("0%", agents.Rows[1].Confidence);
        Assert.Equal("seen -", agents.Rows[0].Seen);
        Assert.Equal(0, agents.Overflow);
    }
}
