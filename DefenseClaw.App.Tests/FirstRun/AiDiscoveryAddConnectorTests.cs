using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.Tests.FirstRun;

/// <summary>"Add" on the AI Discovery connector table (CUST-210): an installed agent that is not active gets one; nothing runs until the review is confirmed.</summary>
[Collection(UiCollection.Name)]
public sealed class AiDiscoveryAddConnectorTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly AppServices _services;

    public AiDiscoveryAddConnectorTests()
    {
        // agent_discovery.json as agent_discovery.py writes it: per connector, installed / configured (the agent's own config) / active (DefenseClaw is set up for it).
        _ = _temp.WriteFile(
            "agent_discovery.json",
            "{\"scanned_at\":\"2026-09-30T10:00:00Z\",\"agents\":{" +
            "\"claudecode\":{\"name\":\"claudecode\",\"installed\":true,\"configured\":true,\"active\":true,\"version\":\"2.1.0\"}," +
            "\"codex\":{\"name\":\"codex\",\"installed\":true,\"configured\":false,\"active\":false,\"version\":\"0.9.0\"}," +
            "\"cursor\":{\"name\":\"cursor\",\"installed\":false,\"configured\":false,\"active\":false}," +
            "\"openclaw\":{\"name\":\"openclaw\",\"installed\":true,\"configured\":false,\"active\":false}}}");
        _services = TestServices.Create(_temp);
    }

    public void Dispose()
    {
        _services.Dispose();
        _temp.Dispose();
    }

    private async Task<AiDiscoveryPanelViewModel> LoadedAsync()
    {
        var vm = new AiDiscoveryPanelViewModel(_services);
        await vm.LoadAgentDiscoveryAsync(CancellationToken.None);
        return vm;
    }

    [Fact]
    public async Task Only_an_installed_agent_that_is_not_active_and_can_be_set_up_here_gets_an_add()
    {
        var vm = await LoadedAsync();

        Assert.Equal(new[] { "codex" }, vm.ConnectorDiscovery.Where(r => r.CanAdd).Select(r => r.Name).ToArray());
        Assert.Equal(4, vm.ConnectorDiscovery.Count);
    }

    [Fact]
    public async Task Add_opens_the_review_of_the_observe_setup_command_and_runs_nothing()
    {
        var vm = await LoadedAsync();

        vm.AddConnectorCommand.Execute(vm.ConnectorDiscovery.Single(r => r.Name == "codex"));

        Assert.True(vm.Review.IsOpen);
        Assert.False(vm.Review.IsRunning);
        var review = vm.Review.CommandReview!;
        Assert.Equal("defenseclaw setup codex --yes --mode observe", Assert.Single(review.Steps).CommandText);
        Assert.Equal(CommandTier.StateChanging, review.Tier);
        Assert.True(review.RestartsGateway);
        Assert.Equal("Add Codex", review.ConfirmLabel);
    }

    [Fact]
    public async Task Add_is_ignored_for_a_row_without_one()
    {
        var vm = await LoadedAsync();

        vm.AddConnectorCommand.Execute(vm.ConnectorDiscovery.Single(r => r.Name == "claudecode"));
        vm.AddConnectorCommand.Execute(vm.ConnectorDiscovery.Single(r => r.Name == "openclaw"));
        vm.AddConnectorCommand.Execute(null);

        Assert.False(vm.Review.IsOpen);
    }
}
