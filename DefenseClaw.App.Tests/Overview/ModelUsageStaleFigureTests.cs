using System.Text.Json;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Gateway;
using DefenseClaw.Core.Gateway.Models;
using DefenseClaw.Core.Install;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.Tests.Overview;

/// <summary>
/// The Tokenomics row never shows an old figure as current: with no /health answer it says when the figure was read, a failed read replaces it with the
/// neutral "usage could not be read", and turning the switches off clears it. Synthetic only.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class ModelUsageStaleFigureTests : IDisposable
{
    private const string Hook = "guardrail:\n  enabled: true\n  connector: codex\n  connectors:\n    codex:\n      mode: observe\n";

    private const string TokenomicsOn = "observability:\n  tokenomics:\n    enabled: true\n    local_store:\n      enabled: true\n";

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

    private async Task<OverviewPanelViewModel> PanelAsync()
    {
        _services?.Dispose();
        _services = TestServices.Create(_temp, Hook + TokenomicsOn, Runtime("1.0.10"));
        _ = await _services.Runtime.RefreshAsync(force: true);
        return new OverviewPanelViewModel(_services);
    }

    private static GatewaySnapshot Snapshot(bool answering) => new()
    {
        State = AppGatewayState.Running,
        Install = InstallState.Running,
        Detail = "ok",
        Health = answering
            ? JsonSerializer.Deserialize<GatewayHealth>(
                "{\"uptime_ms\":600000,\"api\":{\"state\":\"running\"},\"routing\":{\"state\":\"disabled\",\"details\":{\"enabled\":false}}}",
                new JsonSerializerOptions(JsonSerializerDefaults.Web))
            : null,
        ActiveConnectors = ["codex"],
        PolledAt = DateTimeOffset.UtcNow,
    };

    private static string TokenomicsValue(OverviewPanelViewModel vm)
    {
        if (vm.HasConfigurationOverflow && !vm.ConfigurationExpanded)
        {
            vm.ToggleConfigurationCommand.Execute(null);
        }

        return vm.ConfigurationRows.Single(static r => r.Label == "Tokenomics").Value;
    }

    private static Task<GatewayResult<JsonDocument>> Answer(string json) => Task.FromResult(GatewayResult<JsonDocument>.Ok(JsonDocument.Parse(json)));

    [Fact]
    public async Task A_fresh_figure_has_no_age_and_one_with_no_health_answer_says_when_it_was_read()
    {
        var vm = await PanelAsync();
        vm.ModelUsageFetch = (_, _, _) => Answer("{\"enabled\":true,\"rows\":[{\"key\":\"model-a\",\"calls\":9,\"cost_usd\":1.5,\"cost_calls\":9}]}");

        vm.Apply(Snapshot(answering: true));
        await vm.RefreshModelUsageAsync(force: true, CancellationToken.None);
        var fresh = TokenomicsValue(vm);

        vm.Apply(Snapshot(answering: false));
        var stale = TokenomicsValue(vm);

        Assert.StartsWith("on, 9 calls in the last 24 h", fresh, StringComparison.Ordinal);
        Assert.DoesNotContain("read at", fresh, StringComparison.Ordinal);
        Assert.Contains("9 calls in the last 24 h", stale, StringComparison.Ordinal);
        Assert.Contains("(read at ", stale, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_read_replaces_the_old_figure_instead_of_keeping_it()
    {
        var vm = await PanelAsync();
        var fail = false;
        vm.ModelUsageFetch = (_, _, _) => fail
            ? throw new InvalidOperationException("boom")
            : Answer("{\"enabled\":true,\"rows\":[{\"key\":\"model-a\",\"calls\":9,\"cost_usd\":1.5,\"cost_calls\":9}]}");

        vm.Apply(Snapshot(answering: true));
        await vm.RefreshModelUsageAsync(force: true, CancellationToken.None);
        fail = true;
        await vm.RefreshModelUsageAsync(force: true, CancellationToken.None);

        Assert.Equal("on, usage could not be read", TokenomicsValue(vm));
    }
}
