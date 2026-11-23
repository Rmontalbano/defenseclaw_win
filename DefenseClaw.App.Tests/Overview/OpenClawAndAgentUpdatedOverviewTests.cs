using System.Globalization;
using System.IO;
using System.Text.Json;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Gateway.Models;
using DefenseClaw.Core.Install;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.Tests.Overview;

/// <summary>
/// The Overview's rows for runtime 1.0.10 connectors (G10C-04, 06, 08, 09, 10), synthetic throughout: an agent that updated itself in place (the gateway keeps
/// running and does not start that connector), the OpenClaw plugin's call-in, the OpenClaw enforcement label, the per-connector failure outcomes, and a
/// detected OpenClaw. The owner's shape (1.0.10, one connector codex, started, no <c>openclaw_plugin</c>) draws none of it, and 1.0.9 draws what it drew before.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class OpenClawAndAgentUpdatedOverviewTests : IDisposable
{
    private const string CodexOnly =
        "guardrail:\n  enabled: true\n  connector: codex\n  connectors:\n    codex:\n      mode: observe\n";

    private const string CodexAndClaude =
        "guardrail:\n  enabled: true\n  connector: codex\n  connectors:\n    codex:\n      mode: observe\n    claudecode:\n      mode: observe\n";

    private const string AmpOnly =
        "guardrail:\n  enabled: true\n  connector: amp\n  connectors:\n    amp:\n      mode: observe\n";

    private const string OpenClawOnly =
        "guardrail:\n  enabled: true\n  connector: openclaw\n  connectors:\n    openclaw:\n      mode: observe\n";

    private const string CodexChanged =
        "selected Codex executable digest does not match protected evidence: C:/x/codex.exe (the Codex executable changed since setup (a Codex update does this); run: defenseclaw setup codex --yes)";

    private const string AmpChanged = "the Amp executable changed since setup (a Amp update does this); run: defenseclaw setup amp --yes";

    private const string CodexRunning = "[{\"name\":\"codex\",\"state\":\"running\",\"requests\":4}]";

    private const string UpdatedTitle = "Codex updated itself; DefenseClaw is not checking it";

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

    private async Task<OverviewPanelViewModel> PanelAsync(string version, string config)
    {
        _services?.Dispose();
        _services = TestServices.Create(_temp, config, Runtime(version));
        _ = await _services.Runtime.RefreshAsync(force: true);
        return new OverviewPanelViewModel(_services);
    }

    private static GatewayHealth Health(string connectors, string guardrailDetails = "", string extraTopLevel = "", long uptimeMs = 7_200_000) =>
        JsonSerializer.Deserialize<GatewayHealth>(
            "{\"uptime_ms\":" + uptimeMs.ToString(CultureInfo.InvariantCulture) + ",\"api\":{\"state\":\"running\"}," +
            "\"guardrail\":{\"state\":\"running\",\"details\":{\"policy_mode\":\"observe\"" + (guardrailDetails.Length > 0 ? "," + guardrailDetails : string.Empty) + "}}," +
            "\"connectors\":" + connectors + (extraTopLevel.Length > 0 ? "," + extraTopLevel : string.Empty) + "}",
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private static GatewaySnapshot Snapshot(GatewayHealth? health, params string[] active) => new()
    {
        State = AppGatewayState.Running,
        Install = InstallState.Running,
        Detail = "ok",
        Health = health,
        ActiveConnectors = active,
        PolledAt = DateTimeOffset.UtcNow,
    };

    private static string NotStarted(string name, string reason) =>
        "\"connectors_not_started\":[\"" + name + "\"],\"connectors_not_started_reasons\":{\"" + name + "\":\"" + reason + "\"}";

    private static string PluginJson(DateTimeOffset seen) =>
        "\"openclaw_plugin\":{\"last_seen_at\":\"" + seen.ToString("o", CultureInfo.InvariantCulture) + "\",\"hook_mode\":\"native\"}";

    private static bool IsNewRow(AttentionRow row) =>
        row.Title.Contains("updated itself", StringComparison.Ordinal) ||
        row.Title.StartsWith("The OpenClaw plugin", StringComparison.Ordinal) ||
        row.Title == "OpenClaw found, not set up";

    private static ConfigRow ConfigRowOf(OverviewPanelViewModel vm, string label)
    {
        if (!vm.ConfigurationExpanded && vm.HasConfigurationOverflow)
        {
            vm.ToggleConfigurationCommand.Execute(null);
        }

        return vm.ConfigurationRows.Single(r => r.Label == label);
    }

    // ------------------------------------------------------------------ (a) the owner's shape

    [Fact]
    public async Task The_owners_shape_draws_none_of_the_new_rows()
    {
        var vm = await PanelAsync("1.0.10", CodexOnly);

        vm.Apply(Snapshot(Health(CodexRunning), "codex"));

        Assert.DoesNotContain(vm.Attention, IsNewRow);
        Assert.DoesNotContain("on_failure", ConfigRowOf(vm, "Hook fail mode").Value, StringComparison.Ordinal);
        Assert.DoesNotContain("gateway down:", ConfigRowOf(vm, "Hook fail mode").Value, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ (b) an agent that updated itself

    [Fact]
    public async Task On_1_0_10_the_only_connector_updated_itself_is_a_critical_row_with_the_reviewed_reselect()
    {
        var vm = await PanelAsync("1.0.10", CodexOnly);

        vm.Apply(Snapshot(Health("[]", NotStarted("codex", CodexChanged)), "codex"));

        var row = Assert.Single(vm.Attention, static r => r.Title == UpdatedTitle);
        Assert.Equal("Critical", row.SeverityKey);
        Assert.Null(row.Command);
        var action = Assert.Single(row.Actions.Items);
        Assert.Equal((AttentionActionIds.ReselectCodex, true), (action.Id, action.Enabled));
        Assert.Contains("is not checking Codex's calls until setup is run again", row.Detail, StringComparison.Ordinal);
        Assert.Contains("the Codex executable changed since setup", row.Detail, StringComparison.Ordinal);

        // The 1.0.7 setup-failed row is not drawn beside it.
        Assert.DoesNotContain(vm.Attention, static r => r.Title.Contains("is not enforced:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task With_a_second_connector_that_is_running_the_same_row_is_high()
    {
        var vm = await PanelAsync("1.0.10", CodexAndClaude);

        vm.Apply(Snapshot(Health("[{\"name\":\"claudecode\",\"state\":\"running\",\"requests\":4}]", NotStarted("codex", CodexChanged)), "codex", "claudecode"));

        Assert.Equal("High", Assert.Single(vm.Attention, static r => r.Title == UpdatedTitle).SeverityKey);
    }

    [Fact]
    public async Task For_amp_the_runtimes_fix_is_a_copy_only_command_from_the_allow_list()
    {
        var vm = await PanelAsync("1.0.10", AmpOnly);

        vm.Apply(Snapshot(Health("[]", NotStarted("amp", AmpChanged)), "amp"));

        var row = Assert.Single(vm.Attention, static r => r.Title == "Amp updated itself; DefenseClaw is not checking it");
        Assert.Equal("Critical", row.SeverityKey);
        Assert.Equal("defenseclaw setup amp --yes", row.Command);
        Assert.Equal(0, row.Actions.Count);
    }

    [Fact]
    public async Task Re_select_codex_from_that_row_opens_the_review_of_setup_codex_with_the_current_mode_and_runs_nothing()
    {
        var vm = await PanelAsync("1.0.10", CodexOnly);
        vm.Apply(Snapshot(Health("[]", NotStarted("codex", CodexChanged)), "codex"));

        await vm.RunAttentionActionCommand.ExecuteAsync(AttentionActionIds.ReselectCodex);

        Assert.True(vm.Review.IsOpen);
        Assert.False(vm.Review.IsRunning);
        Assert.Equal(new[] { "setup", "codex", "--yes", "--mode", "observe" }, Assert.Single(vm.Review.CommandReview!.Steps).Argv);
        Assert.Empty(_services!.Cli.Activity);
    }

    // ------------------------------------------------------------------ (c) 1.0.9 with the same fixture

    [Fact]
    public async Task On_1_0_9_the_same_health_draws_todays_setup_failed_row_word_for_word()
    {
        var vm = await PanelAsync("1.0.9", CodexOnly);

        vm.Apply(Snapshot(Health("[]", NotStarted("codex", CodexChanged)), "codex"));

        var row = Assert.Single(vm.Attention, static r => r.Title.Contains("is not enforced:", StringComparison.Ordinal));
        Assert.Equal("Codex is not enforced: setup failed when the gateway started", row.Title);
        Assert.Equal("High", row.SeverityKey);
        Assert.Equal("defenseclaw setup codex", row.Command);
        Assert.Equal(0, row.Actions.Count);
        Assert.Equal("The gateway did not start this connector. It says: " + CodexChanged + "." + OverviewPanelViewModel.SetupFailedRunSentence, row.Detail);
        Assert.DoesNotContain(vm.Attention, static r => r.Title.Contains("updated itself", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ (d) the OpenClaw plugin

    [Fact]
    public async Task A_fresh_plugin_call_in_draws_no_row()
    {
        var vm = await PanelAsync("1.0.10", OpenClawOnly);

        vm.Apply(Snapshot(Health("[{\"name\":\"openclaw\",\"state\":\"running\",\"requests\":1}]", extraTopLevel: PluginJson(DateTimeOffset.UtcNow.AddSeconds(-30))), "openclaw"));

        Assert.DoesNotContain(vm.Attention, static r => r.Title.StartsWith("The OpenClaw plugin", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_stale_plugin_call_in_is_a_high_row_with_the_age_and_the_restart_command()
    {
        var vm = await PanelAsync("1.0.10", OpenClawOnly);

        vm.Apply(Snapshot(Health("[{\"name\":\"openclaw\",\"state\":\"running\",\"requests\":1}]", extraTopLevel: PluginJson(DateTimeOffset.UtcNow.AddMinutes(-10))), "openclaw"));

        var row = Assert.Single(vm.Attention, static r => r.Title == "The OpenClaw plugin has stopped calling in");
        Assert.Equal("High", row.SeverityKey);
        Assert.Equal("openclaw gateway restart", row.Command);
        Assert.Contains("last called in 10 min ago", row.Detail, StringComparison.Ordinal);
        Assert.Contains("Restart the OpenClaw gateway so it loads the DefenseClaw plugin", row.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_absent_key_after_90_seconds_of_uptime_is_a_high_row()
    {
        var vm = await PanelAsync("1.0.10", OpenClawOnly);

        vm.Apply(Snapshot(Health("[{\"name\":\"openclaw\",\"state\":\"running\",\"requests\":1}]", uptimeMs: 120_000), "openclaw"));

        var row = Assert.Single(vm.Attention, static r => r.Title == "The OpenClaw plugin has not called in");
        Assert.Equal("High", row.SeverityKey);
        Assert.Equal("openclaw gateway restart", row.Command);
        Assert.Contains("has not called in since the gateway started", row.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_absent_key_in_the_first_90_seconds_is_not_a_problem_yet()
    {
        var vm = await PanelAsync("1.0.10", OpenClawOnly);

        vm.Apply(Snapshot(Health("[{\"name\":\"openclaw\",\"state\":\"running\",\"requests\":1}]", uptimeMs: 30_000), "openclaw"));

        Assert.DoesNotContain(vm.Attention, IsNewRow);
    }

    [Fact]
    public async Task Without_openclaw_configured_an_absent_key_is_never_a_row()
    {
        var vm = await PanelAsync("1.0.10", CodexOnly);

        vm.Apply(Snapshot(Health(CodexRunning, uptimeMs: 7_200_000), "codex"));

        Assert.DoesNotContain(vm.Attention, IsNewRow);
    }

    [Fact]
    public async Task On_1_0_9_an_openclaw_in_the_roster_gets_no_plugin_row()
    {
        var vm = await PanelAsync("1.0.9", OpenClawOnly);

        vm.Apply(Snapshot(Health("[{\"name\":\"openclaw\",\"state\":\"running\",\"requests\":1}]"), "openclaw"));

        Assert.DoesNotContain(vm.Attention, static r => r.Title.StartsWith("The OpenClaw plugin", StringComparison.Ordinal));
    }

    [Fact]
    public async Task With_no_health_answer_there_is_no_plugin_row()
    {
        var vm = await PanelAsync("1.0.10", OpenClawOnly);

        vm.Apply(Snapshot(null, "openclaw"));

        Assert.DoesNotContain(vm.Attention, IsNewRow);
    }

    // ------------------------------------------------------------------ (e) the enforcement label

    [Theory]
    [InlineData("1.0.10", "openclaw", "openclaw native hooks (observe)")]
    [InlineData("1.0.9", "openclaw", "openclaw proxy guardrail (observe)")]
    [InlineData("1.0.10", "zeptoclaw", "zeptoclaw proxy guardrail (observe)")]
    [InlineData("1.0.9", "zeptoclaw", "zeptoclaw proxy guardrail (observe)")]
    public async Task The_enforcement_label_names_native_hooks_for_openclaw_only_on_1_0_10(string version, string connector, string expected)
    {
        var vm = await PanelAsync(version, $"guardrail:\n  enabled: true\n  connector: {connector}\n  connectors:\n    {connector}:\n      mode: observe\n");

        vm.Apply(Snapshot(Health("[]"), connector));

        Assert.Equal(expected, ConfigRowOf(vm, "Enforcement").Value);
    }

    // ------------------------------------------------------------------ (f) failure outcomes

    private static string StatusJson(string codexFailMode, string? kiroFailMode = null) =>
        "{\"environment\":\"windows\",\"deployment_mode\":\"standalone\",\"connectors\":[" +
        "{\"name\":\"codex\",\"friendly\":\"Codex\",\"mode\":\"observe\",\"enabled\":true,\"fail_mode\":" + codexFailMode + "}" +
        (kiroFailMode is null ? string.Empty : ",{\"name\":\"kiro\",\"friendly\":\"Kiro\",\"mode\":\"observe\",\"enabled\":true,\"fail_mode\":" + kiroFailMode + "}") + "]}";

    private static string FailMode(string? onFailure) =>
        "{\"effective\":\"open\",\"provenance\":\"saved\",\"configured\":\"open\",\"current\":true" + (onFailure is null ? string.Empty : ",\"on_failure\":" + onFailure) + "}";

    [Fact]
    public async Task Failure_outcomes_are_one_line_from_the_apps_own_text_per_connector()
    {
        var vm = await PanelAsync("1.0.10", CodexOnly);
        vm.Apply(Snapshot(Health(CodexRunning), "codex", "kiro"));

        vm.ApplyStatus(DefenseClawStatusReader.Parse(StatusJson(
            FailMode("{\"gateway_down\":\"allow\",\"no_answer\":\"allow-ordinary\",\"hook_error\":\"block\"}"),
            FailMode("{\"gateway_down\":\"allow\",\"no_answer\":\"block\",\"hook_error\":\"block\"}"))));

        var codex = ConfigRowOf(vm, "Hook fail mode (codex)").Value;
        Assert.Contains("gateway down: allow with a warning; no answer: tool calls allow with a warning unless the call may reach a protected file", codex, StringComparison.Ordinal);
        Assert.Contains("hook or answer error: block; stop and session-end events: never blocked", codex, StringComparison.Ordinal);

        var kiro = ConfigRowOf(vm, "Hook fail mode (kiro)").Value;
        Assert.Contains("gateway down: allow with a warning; no answer: block; hook or answer error: block; stop and session-end events: never blocked", kiro, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_null_or_absent_on_failure_leaves_todays_text_and_an_unknown_value_is_dropped()
    {
        var vm = await PanelAsync("1.0.10", CodexOnly);
        vm.Apply(Snapshot(Health(CodexRunning), "codex"));

        vm.ApplyStatus(DefenseClawStatusReader.Parse(StatusJson(FailMode(null))));
        var absent = ConfigRowOf(vm, "Hook fail mode").Value;
        vm.ApplyStatus(DefenseClawStatusReader.Parse(StatusJson(FailMode("null"))));
        var jsonNull = ConfigRowOf(vm, "Hook fail mode").Value;
        vm.ApplyStatus(DefenseClawStatusReader.Parse(StatusJson(FailMode("{\"gateway_down\":\"SHOW THIS RUNTIME TEXT\",\"no_answer\":\"block\"}"))));
        var unknown = ConfigRowOf(vm, "Hook fail mode").Value;

        Assert.DoesNotContain("gateway down:", absent, StringComparison.Ordinal);
        Assert.Equal(absent, jsonNull);
        Assert.DoesNotContain("SHOW THIS RUNTIME TEXT", unknown, StringComparison.Ordinal);
        Assert.DoesNotContain("gateway down:", unknown, StringComparison.Ordinal);
        Assert.Contains("no answer: block", unknown, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ (g) a detected OpenClaw

    private void WriteDiscovery(string supportedConnector, string state = "active", double score = 0.9)
    {
        var path = _services!.Paths.AiDiscoveryStatePath;
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var scoreText = score.ToString(CultureInfo.InvariantCulture);
        File.WriteAllText(
            path,
            "{\"version\":2,\"signals\":{\"sig-1\":{\"supported_connector\":\"" + supportedConnector + "\",\"state\":\"" + state + "\",\"confidence\":" + scoreText +
            ",\"identity_score\":" + scoreText + ",\"presence_score\":" + scoreText + "}}}");
    }

    [Fact]
    public async Task A_detected_openclaw_that_is_not_configured_is_an_info_row_with_text_only_on_1_0_10()
    {
        var vm = await PanelAsync("1.0.10", CodexOnly);
        WriteDiscovery("openclaw");

        vm.Apply(Snapshot(Health(CodexRunning), "codex"));
        await vm.RefreshAgentsAsync(CancellationToken.None);

        var row = Assert.Single(vm.Attention, static r => r.Title == "OpenClaw found, not set up");
        Assert.Equal("Info", row.SeverityKey);
        Assert.Null(row.Command);
        Assert.Equal(0, row.Actions.Count);
        Assert.Contains("Set it up from Setup -> OpenClaw", row.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(vm.ConnectorRows, static r => r.Name == "openclaw");
        Assert.False(vm.HasUnconfiguredRows);
    }

    [Fact]
    public async Task On_1_0_9_a_detected_openclaw_is_not_mentioned()
    {
        var vm = await PanelAsync("1.0.9", CodexOnly);
        WriteDiscovery("openclaw");

        vm.Apply(Snapshot(Health(CodexRunning), "codex"));
        await vm.RefreshAgentsAsync(CancellationToken.None);

        Assert.DoesNotContain(vm.Attention, IsNewRow);
    }

    [Theory]
    [InlineData("openclaw", "gone", 0.9)]
    [InlineData("openclaw", "active", 0.5)]
    [InlineData("windsurf", "active", 0.9)]
    public async Task A_gone_a_weak_or_another_agent_is_not_an_openclaw_row(string connector, string state, double score)
    {
        var vm = await PanelAsync("1.0.10", CodexOnly);
        WriteDiscovery(connector, state, score);

        vm.Apply(Snapshot(Health(CodexRunning), "codex"));
        await vm.RefreshAgentsAsync(CancellationToken.None);

        Assert.DoesNotContain(vm.Attention, static r => r.Title == "OpenClaw found, not set up");
    }

    [Fact]
    public async Task A_configured_openclaw_is_not_also_reported_as_found()
    {
        var vm = await PanelAsync("1.0.10", OpenClawOnly);
        WriteDiscovery("openclaw");

        vm.Apply(Snapshot(Health("[{\"name\":\"openclaw\",\"state\":\"running\",\"requests\":1}]", extraTopLevel: PluginJson(DateTimeOffset.UtcNow)), "openclaw"));
        await vm.RefreshAgentsAsync(CancellationToken.None);

        Assert.DoesNotContain(vm.Attention, IsNewRow);
    }
}
