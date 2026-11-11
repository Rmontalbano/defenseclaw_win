using System.Text.Json;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.ClaudeCode;
using DefenseClaw.Core.Gateway.Models;
using DefenseClaw.Core.Install;
using DefenseClaw.Core.Paths;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.Tests.Overview;

/// <summary>
/// Claude Cowork on the Overview (runtime 1.0.8, CUST-445): the Configuration card's "Cowork" row, the "What needs attention" row and its buttons, and the
/// Cowork line under the Claude Code connector. Synthetic <c>status --json</c> documents, one per reason code, at a 1.0.8 runtime and (the same documents) at a
/// 1.0.6 runtime, which must show nothing. Green comes only from <c>protected: true</c>; the live /health counters are a note.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class CoworkOverviewTests : IDisposable
{
    private const string Config = "guardrail:\n  enabled: true\n  connector: claudecode\n  connectors:\n    claudecode:\n      mode: observe\n      hook_fail_mode: open\n";

    private const string Zip = "C:\\\\Users\\\\example\\\\.defenseclaw\\\\cowork\\\\defenseclaw-cowork-plugin.zip";

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

    private async Task<OverviewPanelViewModel> PanelAsync(string version, InstallationContext? installation = null)
    {
        _services?.Dispose();
        _services = TestServices.Create(_temp, Config, Runtime(version), installation);
        _ = await _services.Runtime.RefreshAsync(force: true);
        return new OverviewPanelViewModel(_services);
    }

    private static GatewaySnapshot Snapshot(GatewayHealth? health = null) => new()
    {
        State = AppGatewayState.Running,
        Install = InstallState.Running,
        Detail = "ok",
        Health = health,
        ActiveConnectors = new[] { "claudecode" },
        PolledAt = DateTimeOffset.UtcNow,
    };

    private static string StatusJson(string? cowork) =>
        "{\"environment\":\"windows\",\"deployment_mode\":\"standalone\",\"connectors\":[{\"name\":\"claudecode\",\"friendly\":\"Claude Code\",\"mode\":\"observe\",\"enabled\":true}]" +
        (cowork is null ? string.Empty : ",\"cowork\":" + cowork) + "}";

    private static string NotProtected(string code, string detail, string step) =>
        "{\"protected\":false,\"detail\":\"" + detail + "\",\"step\":\"" + step + "\",\"reason_code\":\"" + code + "\",\"last_event_at\":null,\"plugin_version\":null,\"plugin_zip\":null}";

    private const string ProtectedJson =
        "{\"protected\":true,\"detail\":\"protected, observe mode: records only (plugin 1.0.20261101120000)\",\"step\":\"\",\"reason_code\":\"\",\"last_event_at\":\"2026-11-01T12:30:00Z\",\"plugin_version\":\"1.0.20261101120000\",\"plugin_zip\":\"" + Zip + "\"}";

    private static void Show(OverviewPanelViewModel vm, string? cowork)
    {
        vm.Apply(Snapshot());
        vm.ApplyStatus(DefenseClawStatusReader.Parse(StatusJson(cowork)));
    }

    private static ConfigRow? CoworkRow(OverviewPanelViewModel vm)
    {
        if (!vm.ConfigurationExpanded && vm.HasConfigurationOverflow)
        {
            vm.ToggleConfigurationCommand.Execute(null);
        }

        return vm.ConfigurationRows.SingleOrDefault(static r => r.Label == "Cowork");
    }

    private static List<AttentionRow> CoworkAttention(OverviewPanelViewModel vm) =>
        vm.Attention.Where(static r => r.Title.StartsWith("Cowork", StringComparison.Ordinal)).ToList();

    // ------------------------------------------------------------------ the Configuration row

    [Fact]
    public async Task A_protected_state_is_a_green_row_with_the_clis_sentence_and_the_claude_code_note()
    {
        var vm = await PanelAsync("1.0.8");

        Show(vm, ProtectedJson);

        var row = CoworkRow(vm);
        Assert.NotNull(row);
        Assert.Equal("Ok", row.ToneKey);
        Assert.StartsWith("protected, observe mode: records only (plugin 1.0.20261101120000)", row.Value, StringComparison.Ordinal);
        Assert.Contains("uses the Claude Code connector's mode and fail mode", row.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("Next:", row.Value, StringComparison.Ordinal);
        Assert.Empty(CoworkAttention(vm));
    }

    [Fact]
    public async Task The_row_sits_right_after_the_hook_fail_mode_row()
    {
        var vm = await PanelAsync("1.0.8");

        Show(vm, ProtectedJson);
        vm.ToggleConfigurationCommand.Execute(null);

        var labels = vm.ConfigurationRows.Select(static r => r.Label).ToList();
        Assert.Equal(labels.IndexOf("Hook fail mode") + 1, labels.IndexOf("Cowork"));
    }

    [Fact]
    public async Task A_state_that_is_not_protected_is_a_warning_row_with_the_next_step()
    {
        var vm = await PanelAsync("1.0.8");

        Show(vm, NotProtected(CoworkReasonCodes.NoEvents, "not protected yet: no Cowork event has reached DefenseClaw since plugin 1.0.20261101120000 was built", "upload the zip in Claude Desktop"));

        var row = CoworkRow(vm);
        Assert.NotNull(row);
        Assert.Equal("Warn", row.ToneKey);
        Assert.StartsWith("not protected yet: no Cowork event has reached DefenseClaw since plugin 1.0.20261101120000 was built", row.Value, StringComparison.Ordinal);
        Assert.Contains(" · Next: upload the zip in Claude Desktop", row.Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_cowork_object_means_no_row_and_no_attention_at_1_0_8()
    {
        var vm = await PanelAsync("1.0.8");

        Show(vm, null);

        Assert.Null(CoworkRow(vm));
        Assert.Empty(CoworkAttention(vm));
    }

    [Fact]
    public async Task A_later_read_without_the_object_removes_the_row()
    {
        var vm = await PanelAsync("1.0.8");
        Show(vm, NotProtected(CoworkReasonCodes.PluginNotBuilt, "not protected: example", "defenseclaw setup cowork"));
        Assert.NotNull(CoworkRow(vm));

        vm.ApplyStatus(DefenseClawStatusReader.Parse(StatusJson(null)));

        Assert.Null(CoworkRow(vm));
        Assert.Empty(CoworkAttention(vm));
    }

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("1.0.5")]
    [InlineData("1.0.6")]
    [InlineData("1.0.7")]
    public async Task A_runtime_before_1_0_8_shows_nothing_for_the_same_status_documents(string version)
    {
        var vm = await PanelAsync(version);

        foreach (var cowork in new[] { ProtectedJson, NotProtected(CoworkReasonCodes.NoEvents, "not protected yet", "upload the zip"), NotProtected(CoworkReasonCodes.PluginNotBuilt, "not protected", "defenseclaw setup cowork") })
        {
            Show(vm, cowork);

            Assert.Null(CoworkRow(vm));
            Assert.Empty(CoworkAttention(vm));
        }

        Assert.Null(CoworkProtectionStore.For(_services!).Current);
    }

    // ------------------------------------------------------------------ the attention row

    [Theory]
    [InlineData(CoworkReasonCodes.NoEvents, "Cowork is not protected yet: upload the plugin in Claude Desktop", "High", "open-cowork-wizard", "Set up Cowork")]
    [InlineData(CoworkReasonCodes.PluginNotBuilt, "Cowork is not protected", "Medium", "open-cowork-wizard", "Set up Cowork")]
    [InlineData(CoworkReasonCodes.ClaudeCodeOff, "Cowork is not protected: Claude Code is off", "Medium", "open-claude-code-wizard", "Set up Claude Code")]
    public async Task Each_state_that_is_not_protected_gets_its_row_and_its_button(string code, string title, string severity, string actionId, string label)
    {
        var vm = await PanelAsync("1.0.8");

        Show(vm, NotProtected(code, "not protected: example", "example step"));

        var row = Assert.Single(CoworkAttention(vm));
        Assert.Equal(title, row.Title);
        Assert.Equal(severity, row.SeverityKey);
        var action = Assert.Single(row.Actions.Items);
        Assert.Equal(actionId, action.Id);
        Assert.Equal(label, action.Label);
        Assert.StartsWith("not protected: example", row.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_plugin_that_awaits_upload_says_the_step_too_and_a_plugin_not_built_says_the_detail_alone()
    {
        var vm = await PanelAsync("1.0.8");

        Show(vm, NotProtected(CoworkReasonCodes.NoEvents, "not protected yet: nothing since the build", "upload the zip, then start a Cowork task"));
        Assert.Equal("not protected yet: nothing since the build upload the zip, then start a Cowork task", Assert.Single(CoworkAttention(vm)).Detail);

        Show(vm, NotProtected(CoworkReasonCodes.PluginNotBuilt, "not protected: Claude Desktop is installed, but Cowork has no DefenseClaw plugin", "defenseclaw setup cowork"));
        Assert.Equal("not protected: Claude Desktop is installed, but Cowork has no DefenseClaw plugin", Assert.Single(CoworkAttention(vm)).Detail);
    }

    [Theory]
    [InlineData(CoworkReasonCodes.GatewayDown)]
    [InlineData(CoworkReasonCodes.ClaudeCodeNotStarted)]
    [InlineData(CoworkReasonCodes.HealthUnavailable)]
    public async Task A_state_that_depends_on_the_gateway_raises_no_row_of_its_own_but_the_configuration_row_still_warns(string code)
    {
        var vm = await PanelAsync("1.0.8");

        Show(vm, NotProtected(code, "not protected: gateway", "defenseclaw-gateway start"));

        Assert.Empty(CoworkAttention(vm));
        Assert.Equal("Warn", CoworkRow(vm)?.ToneKey);
    }

    [Fact]
    public async Task A_reason_code_a_later_runtime_adds_is_a_medium_row_with_no_button()
    {
        var vm = await PanelAsync("1.0.8");

        Show(vm, NotProtected("cowork_future_reason", "not protected: something new", "do the new thing"));

        var row = Assert.Single(CoworkAttention(vm));
        Assert.Equal("Medium", row.SeverityKey);
        Assert.Equal("not protected: something new do the new thing", row.Detail);
        Assert.False(row.HasActions);
    }

    [Fact]
    public async Task A_state_that_is_not_protected_with_no_reason_code_raises_no_row()
    {
        var vm = await PanelAsync("1.0.8");

        Show(vm, "{\"protected\":false,\"detail\":\"\"}");

        Assert.Empty(CoworkAttention(vm));
    }

    [Fact]
    public async Task A_managed_installation_is_not_asked()
    {
        var vm = await PanelAsync("1.0.8", TestInstallations.Managed(_temp.Path));

        Show(vm, NotProtected(CoworkReasonCodes.NoEvents, "not protected yet", "upload the zip"));

        Assert.Empty(CoworkAttention(vm));
    }

    // ------------------------------------------------------------------ the buttons

    [Theory]
    [InlineData(CoworkReasonCodes.NoEvents, "open-cowork-wizard")]
    [InlineData(CoworkReasonCodes.ClaudeCodeOff, "open-claude-code-wizard")]
    public async Task A_button_opens_its_wizard_once(string code, string actionId)
    {
        var vm = await PanelAsync("1.0.8");
        var opened = new List<string>();
        vm.CoworkWizardOpener = () =>
        {
            opened.Add("cowork");
            return Task.CompletedTask;
        };
        vm.ClaudeCodeWizardOpener = () =>
        {
            opened.Add("claude-code");
            return Task.CompletedTask;
        };
        Show(vm, NotProtected(code, "not protected: example", "example step"));

        await vm.RunAttentionActionCommand.ExecuteAsync(actionId);

        Assert.Equal(new[] { actionId == "open-cowork-wizard" ? "cowork" : "claude-code" }, opened);
    }

    // ------------------------------------------------------------------ the shared store

    [Fact]
    public async Task A_status_read_is_published_to_the_store_the_readiness_list_shares()
    {
        var vm = await PanelAsync("1.0.8");
        var store = CoworkProtectionStore.For(_services!);
        var changes = 0;
        store.Changed += (_, _) => changes++;

        Show(vm, NotProtected(CoworkReasonCodes.PluginNotBuilt, "not protected: example", "defenseclaw setup cowork"));

        Assert.True(store.Current?.IsNotBuilt);
        Assert.True(changes > 0);

        vm.ApplyStatus(DefenseClawStatusReader.Parse(StatusJson(null)));
        Assert.Null(store.Current);
    }

    // ------------------------------------------------------------------ the live /health label

    private static GatewayHealth HealthWithCowork(string connectors) =>
        JsonSerializer.Deserialize<GatewayHealth>(
            "{\"uptime_ms\":600000,\"api\":{\"state\":\"running\"},\"connectors\":" + connectors + "}",
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private static string ClaudeCodeWithCowork(int requests) =>
        "[{\"name\":\"claudecode\",\"state\":\"running\",\"requests\":40,\"surfaces\":{\"cowork\":{\"requests\":" + requests +
        ",\"last_activity_at\":\"" + DateTimeOffset.UtcNow.AddMinutes(-3).ToString("O", System.Globalization.CultureInfo.InvariantCulture) + "\"}}}]";

    [Fact]
    public async Task The_claude_code_connector_row_names_the_cowork_calls_as_labels_not_coverage()
    {
        var vm = await PanelAsync("1.0.8");

        vm.Apply(Snapshot(HealthWithCowork(ClaudeCodeWithCowork(12))));

        var row = Assert.Single(vm.ConnectorRows, static r => r.Name == "claudecode");
        Assert.Contains("Cowork: 12 calls, last 3m ago (labels, not proof of coverage)", row.Counters, StringComparison.Ordinal);
        Assert.StartsWith("40 requests", row.Counters, StringComparison.Ordinal);
    }

    [Fact]
    public async Task One_call_is_not_plural_and_a_connector_with_no_cowork_surface_has_no_line()
    {
        var vm = await PanelAsync("1.0.8");

        vm.Apply(Snapshot(HealthWithCowork(ClaudeCodeWithCowork(1))));
        Assert.Contains("Cowork: 1 call, last", Assert.Single(vm.ConnectorRows, static r => r.Name == "claudecode").Counters, StringComparison.Ordinal);

        vm.Apply(Snapshot(HealthWithCowork("[{\"name\":\"claudecode\",\"state\":\"running\",\"requests\":40}]")));
        Assert.DoesNotContain("Cowork", Assert.Single(vm.ConnectorRows, static r => r.Name == "claudecode").Counters, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_runtime_before_1_0_8_adds_no_cowork_line_even_when_the_gateway_sends_the_surface()
    {
        var vm = await PanelAsync("1.0.6");

        vm.Apply(Snapshot(HealthWithCowork(ClaudeCodeWithCowork(12))));

        var row = Assert.Single(vm.ConnectorRows, static r => r.Name == "claudecode");
        Assert.DoesNotContain("Cowork", row.Counters, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_live_cowork_call_never_turns_the_row_green()
    {
        var vm = await PanelAsync("1.0.8");
        vm.Apply(Snapshot(HealthWithCowork(ClaudeCodeWithCowork(12))));

        vm.ApplyStatus(DefenseClawStatusReader.Parse(StatusJson(NotProtected(CoworkReasonCodes.NoEvents, "not protected yet", "upload the zip"))));

        var row = CoworkRow(vm);
        Assert.NotNull(row);
        Assert.Equal("Warn", row.ToneKey);
        Assert.Contains("last Cowork call seen live 3m ago", row.Value, StringComparison.Ordinal);
        _ = Assert.Single(CoworkAttention(vm));
    }
}
