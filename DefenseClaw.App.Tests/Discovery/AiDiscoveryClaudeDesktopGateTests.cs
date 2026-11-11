using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;

namespace DefenseClaw.App.Tests.Discovery;

/// <summary>
/// The Claude Desktop capability gate on the AI Discovery panel: on a 1.0.7 runtime the cards and the connector table are as they were, on
/// 1.0.8 the Claude Desktop entries and the engine wording appear. Synthetic files; nothing runs.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class AiDiscoveryClaudeDesktopGateTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private AppServices? _services;

    public void Dispose()
    {
        _services?.Dispose();
        _temp.Dispose();
    }

    private async Task<AiDiscoveryPanelViewModel> LoadedAsync(string version)
    {
        _ = _temp.WriteFile(
            "ai_discovery_state.json",
            "{\"updated_at\":\"2026-11-10T12:00:00Z\",\"signals\":{" +
            "\"fp-1\":" + AiDiscoveryClaudeDesktopTests.SignalJson("claude_desktop", "Claude", AiDiscoveryClaudeDesktopTests.FirstPartyBlock) + "," +
            "\"fp-2\":" + AiDiscoveryClaudeDesktopTests.SignalJson("claude_desktop", "Claude-3p", AiDiscoveryClaudeDesktopTests.ThirdPartyBlock) + "}}");
        _ = _temp.WriteFile(
            "agent_discovery.json",
            "{\"scanned_at\":\"2026-11-10T12:00:00Z\",\"agents\":{" +
            "\"claudecode\":{\"name\":\"claudecode\",\"installed\":true,\"configured\":true,\"active\":true,\"version\":\"2.1.309\",\"source\":\"claude-desktop\"}," +
            "\"codex\":{\"name\":\"codex\",\"installed\":true,\"configured\":false,\"active\":false,\"version\":\"0.9.0\",\"source\":\"cli\"}}}");

        _services = TestServices.Create(_temp, "guardrail:\n  enabled: true\n", FirstRun.FirstRunFailModeTests.Versioned(version));
        _ = await _services.Runtime.RefreshAsync();

        var vm = new AiDiscoveryPanelViewModel(_services);
        await vm.LoadFromDiskAsync(CancellationToken.None);
        await vm.LoadAgentDiscoveryAsync(CancellationToken.None);
        return vm;
    }

    [Fact]
    public async Task On_a_1_0_7_runtime_the_card_has_no_claude_desktop_entries_and_the_version_column_is_the_version()
    {
        var vm = await LoadedAsync("1.0.7");

        Assert.False(vm.ClaudeDesktopGate);
        var card = Assert.Single(vm.CardsView.Cast<DiscoveryComponentCard>());
        Assert.Empty(card.ClaudeDesktopEntries);
        Assert.False(card.HasClaudeDesktop);
        Assert.Equal(2, card.SignalLines.Count);

        var row = vm.ConnectorDiscovery.Single(r => r.Name == "claudecode");
        Assert.Equal("claude-desktop", row.Source);
        Assert.Equal(row.Version, row.VersionDisplay);
        Assert.Equal("2.1.309", row.VersionDisplay);
    }

    [Fact]
    public async Task On_a_1_0_8_runtime_the_card_lists_both_deployments_and_the_version_names_the_engine()
    {
        var vm = await LoadedAsync("1.0.8");

        Assert.True(vm.ClaudeDesktopGate);
        var card = Assert.Single(vm.CardsView.Cast<DiscoveryComponentCard>());
        Assert.Equal(2, card.ClaudeDesktopEntries.Count);
        Assert.Equal("Claude Desktop · Claude account", card.ClaudeDesktopEntries[0].Title);
        Assert.Equal("Claude Desktop · third-party inference", card.ClaudeDesktopEntries[1].Title);

        Assert.Equal("2.1.309 (Claude Desktop engine)", vm.ConnectorDiscovery.Single(r => r.Name == "claudecode").VersionDisplay);
        Assert.Equal("0.9.0", vm.ConnectorDiscovery.Single(r => r.Name == "codex").VersionDisplay);
    }
}
