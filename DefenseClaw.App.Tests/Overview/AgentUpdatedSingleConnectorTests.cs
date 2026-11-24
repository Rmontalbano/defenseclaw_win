using System.Text.Json;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Gateway.Models;
using DefenseClaw.Core.Install;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.Tests.Overview;

/// <summary>
/// "An agent updated itself" with ONE configured connector (runtime 1.0.10): the gateway has no <c>connectors_not_started</c> list then; it stays up and /health shows
/// <c>guardrail.state = error</c> with the runtime's sentence in <c>last_error</c> (sidecar.go@1.0.10:1735-1737, 5499-5500). The same Critical row with the reviewed
/// Re-select Codex action is drawn, once. A healthy guardrail, other errors and a 1.0.9 runtime draw nothing new. Synthetic JSON throughout.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class AgentUpdatedSingleConnectorTests : IDisposable
{
    private const string CodexOnly =
        "guardrail:\n  enabled: true\n  connector: codex\n  connectors:\n    codex:\n      mode: observe\n";

    private const string CodexError =
        "connector codex setup failed: the Codex executable changed since setup (a Codex update does this); run: defenseclaw setup codex --yes";

    private const string UpdatedTitle = "Codex updated itself. DefenseClaw is not checking it";

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

    private async Task<OverviewPanelViewModel> PanelAsync(string version, string config = CodexOnly)
    {
        _services?.Dispose();
        _services = TestServices.Create(_temp, config, Runtime(version));
        _ = await _services.Runtime.RefreshAsync(force: true);
        return new OverviewPanelViewModel(_services);
    }

    private static GatewaySnapshot Snapshot(string guardrail, string connectors = "[]") => new()
    {
        State = AppGatewayState.Running,
        Install = InstallState.Running,
        Detail = "ok",
        Health = JsonSerializer.Deserialize<GatewayHealth>(
            "{\"uptime_ms\":7200000,\"api\":{\"state\":\"running\"},\"guardrail\":" + guardrail + ",\"connectors\":" + connectors + "}",
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!,
        ActiveConnectors = ["codex"],
        PolledAt = DateTimeOffset.UtcNow,
    };

    private static string ErrorGuardrail(string lastError) =>
        "{\"state\":\"error\",\"last_error\":" + JsonSerializer.Serialize(lastError) + ",\"details\":{\"policy_mode\":\"observe\"}}";

    private static bool IsSelfUpdateRow(AttentionRow row) => row.Title.Contains("updated itself", StringComparison.Ordinal);

    [Fact]
    public async Task One_connector_updated_itself_is_one_critical_row_with_the_reviewed_reselect_and_no_duplicate()
    {
        var vm = await PanelAsync("1.0.10");

        vm.Apply(Snapshot(ErrorGuardrail(CodexError)));

        var row = Assert.Single(vm.Attention, IsSelfUpdateRow);
        Assert.Equal(UpdatedTitle, row.Title);
        Assert.Equal("Critical", row.SeverityKey);
        Assert.Null(row.Command);
        var action = Assert.Single(row.Actions.Items);
        Assert.Equal((AttentionActionIds.ReselectCodex, true), (action.Id, action.Enabled));
        Assert.Contains("the Codex executable changed since setup", row.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("any other connectors", row.Detail, StringComparison.Ordinal);

        // Nothing else says the same thing in other words: no setup-failed or not-enforced row, and exactly one row mentions the sentence.
        Assert.DoesNotContain(vm.Attention, static r => r.Title.Contains("is not enforced:", StringComparison.Ordinal));
        Assert.Single(vm.Attention, static r => r.Detail.Contains("executable changed since setup", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_reselect_from_that_row_opens_the_review_and_runs_nothing()
    {
        var vm = await PanelAsync("1.0.10");
        vm.Apply(Snapshot(ErrorGuardrail(CodexError)));

        await vm.RunAttentionActionCommand.ExecuteAsync(AttentionActionIds.ReselectCodex);

        Assert.True(vm.Review.IsOpen);
        Assert.False(vm.Review.IsRunning);
        Assert.Equal(new[] { "setup", "codex", "--yes", "--mode", "observe" }, Assert.Single(vm.Review.CommandReview!.Steps).Argv);
        Assert.Empty(_services!.Cli.Activity);
    }

    [Fact]
    public async Task For_amp_it_is_critical_with_a_copy_only_command_from_the_fixed_list()
    {
        var vm = await PanelAsync("1.0.10", "guardrail:\n  enabled: true\n  connector: amp\n  connectors:\n    amp:\n      mode: observe\n");

        vm.Apply(Snapshot(ErrorGuardrail("connector amp setup failed: the Amp executable changed since setup (a Amp update does this); run: defenseclaw setup amp --yes")));

        var row = Assert.Single(vm.Attention, IsSelfUpdateRow);
        Assert.Equal("Amp updated itself. DefenseClaw is not checking it", row.Title);
        Assert.Equal("Critical", row.SeverityKey);
        Assert.Equal("defenseclaw setup amp --yes", row.Command);
        Assert.Equal(0, row.Actions.Count);
    }

    [Fact]
    public async Task The_connector_name_comes_only_from_the_fixed_list_never_from_the_text()
    {
        var vm = await PanelAsync("1.0.10");

        vm.Apply(Snapshot(ErrorGuardrail("the evil executable changed since setup; run: defenseclaw setup evil --yes & calc")));

        Assert.DoesNotContain(vm.Attention, IsSelfUpdateRow);
    }

    [Fact]
    public async Task A_very_long_error_is_bounded_in_the_row()
    {
        var vm = await PanelAsync("1.0.10");

        vm.Apply(Snapshot(ErrorGuardrail(new string('z', 20_000) + " " + CodexError)));

        // The sentence is past what is inspected, so nothing is drawn, and nothing long is ever shown.
        Assert.DoesNotContain(vm.Attention, static r => r.Detail.Length > 4000);
        Assert.DoesNotContain(vm.Attention, IsSelfUpdateRow);
    }

    [Fact]
    public async Task With_several_connectors_the_listed_form_is_unchanged_and_not_doubled_by_the_error_text()
    {
        var vm = await PanelAsync(
            "1.0.10",
            "guardrail:\n  enabled: true\n  connector: codex\n  connectors:\n    codex:\n      mode: observe\n    claudecode:\n      mode: observe\n");
        var guardrail = "{\"state\":\"running\",\"details\":{\"policy_mode\":\"observe\",\"connectors_not_started\":[\"codex\"],\"connectors_not_started_reasons\":{\"codex\":" +
                        JsonSerializer.Serialize(CodexError) + "}}}";

        vm.Apply(Snapshot(guardrail, "[{\"name\":\"claudecode\",\"state\":\"running\",\"requests\":4}]"));

        var row = Assert.Single(vm.Attention, IsSelfUpdateRow);
        Assert.Equal("High", row.SeverityKey);
    }

    [Fact]
    public async Task A_healthy_guardrail_and_other_errors_draw_nothing_new()
    {
        var vm = await PanelAsync("1.0.10");

        vm.Apply(Snapshot("{\"state\":\"running\",\"details\":{\"policy_mode\":\"observe\"}}", "[{\"name\":\"codex\",\"state\":\"running\",\"requests\":4}]"));
        Assert.DoesNotContain(vm.Attention, IsSelfUpdateRow);

        // The sentence on a guardrail that is not in error (a stale string) is not read.
        vm.Apply(Snapshot("{\"state\":\"running\",\"last_error\":" + JsonSerializer.Serialize(CodexError) + "}", "[{\"name\":\"codex\",\"state\":\"running\",\"requests\":4}]"));
        Assert.DoesNotContain(vm.Attention, IsSelfUpdateRow);

        vm.Apply(Snapshot(ErrorGuardrail("connector codex setup failed: the config is invalid")));
        Assert.DoesNotContain(vm.Attention, IsSelfUpdateRow);
        Assert.DoesNotContain(vm.Attention, static r => r.Title.Contains("is not enforced:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task On_1_0_9_the_same_health_draws_the_rows_it_drew_before()
    {
        var vm = await PanelAsync("1.0.9");

        vm.Apply(Snapshot(ErrorGuardrail(CodexError)));
        var withError = vm.Attention.Select(static r => (r.Title, r.Detail, r.SeverityKey)).ToList();
        vm.Apply(Snapshot("{\"state\":\"running\",\"details\":{\"policy_mode\":\"observe\"}}"));
        var healthy = vm.Attention.Select(static r => (r.Title, r.Detail, r.SeverityKey)).ToList();

        Assert.DoesNotContain(vm.Attention, IsSelfUpdateRow);
        Assert.Equal(healthy, withError);
        Assert.DoesNotContain(withError, static r => r.Title.Contains("updated itself", StringComparison.Ordinal) || r.Title.Contains("is not enforced", StringComparison.Ordinal));
    }

    [Fact]
    public void The_reader_returns_nothing_for_a_null_or_malformed_block()
    {
        Assert.Empty(AgentUpdatedGuardrailError.From(null));
        Assert.Empty(AgentUpdatedGuardrailError.From(new ServiceState { State = "error" }));
        Assert.Empty(AgentUpdatedGuardrailError.From(new ServiceState { State = "error", LastError = string.Empty }));
        var skip = Assert.Single(AgentUpdatedGuardrailError.From(new ServiceState { State = "ERROR", LastError = CodexError }));
        Assert.Equal(("codex", ConnectorSkipKind.AgentUpdated), (skip.Name, skip.Kind));
        Assert.True(skip.Reason.Length <= ConnectorStartHealth.MaxReasonLength);
    }
}
