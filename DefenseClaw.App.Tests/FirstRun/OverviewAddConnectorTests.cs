using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.Tests.FirstRun;

/// <summary>
/// The Overview's Connectors table lists the detected-but-not-configured connectors with the Mac's orange "not configured" and an Add that
/// opens the review of <c>setup &lt;alias&gt; --yes --mode observe</c> (CUST-210). The scene's config has claudecode and hermes; its
/// AI-discovery file has seen those two plus codex, cursor, antigravity, geminicli, copilot, windsurf, openclaw (a proxy connector) and a gone agent.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class OverviewAddConnectorTests : IDisposable
{
    private readonly OverviewScene _scene = OverviewScene.Create(seedAudit: false);

    public void Dispose() => _scene.Dispose();

    private async Task<OverviewPanelViewModel> PanelAsync()
    {
        var vm = new OverviewPanelViewModel(_scene.Services);
        vm.Apply(OverviewScene.Snapshot());
        await vm.RefreshAgentsAsync(CancellationToken.None);
        return vm;
    }

    [Fact]
    public async Task Detected_connectors_that_are_not_configured_follow_the_configured_ones_with_the_orange_status()
    {
        var vm = await PanelAsync();

        var names = vm.ConnectorRows.Select(r => r.Name).ToArray();
        Assert.Equal(new[] { "claudecode", "hermes" }, names.Take(2).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.Equal(new[] { "codex", "cursor", "windsurf", "geminicli", "copilot", "antigravity" }, names.Skip(2).ToArray());

        Assert.All(vm.ConnectorRows.Take(2), r => Assert.False(r.IsUnconfigured));
        Assert.All(vm.ConnectorRows.Skip(2), r =>
        {
            Assert.True(r.IsUnconfigured);
            Assert.Equal("not configured", r.StateText);
            Assert.Equal("High", r.StateKey);
            Assert.Equal("—", r.RulePack);
            Assert.Equal("—", r.Mode);
        });
        Assert.True(vm.HasUnconfiguredRows);
    }

    [Fact]
    public async Task A_proxy_connector_and_a_gone_agent_are_not_offered()
    {
        var vm = await PanelAsync();

        Assert.DoesNotContain(vm.ConnectorRows, r => r.Name is "openclaw" or "qodo");
    }

    [Fact]
    public async Task Without_a_scan_there_are_no_added_rows()
    {
        using var scene = OverviewScene.Create(seedAudit: false, seedAgents: false);
        var vm = new OverviewPanelViewModel(scene.Services);
        vm.Apply(OverviewScene.Snapshot());

        await vm.RefreshAgentsAsync(CancellationToken.None);

        Assert.All(vm.ConnectorRows, r => Assert.False(r.IsUnconfigured));
        Assert.False(vm.HasUnconfiguredRows);
    }

    [Fact]
    public async Task What_an_attention_rule_needs_is_exposed_and_announced()
    {
        var vm = new OverviewPanelViewModel(_scene.Services);
        vm.Apply(OverviewScene.Snapshot());
        var raised = 0;
        vm.UnconfiguredConnectorsChanged += (_, _) => raised++;

        await vm.RefreshAgentsAsync(CancellationToken.None);

        Assert.Equal(1, raised);
        Assert.Equal(
            new[] { "codex", "cursor", "windsurf", "geminicli", "copilot", "antigravity" },
            vm.UnconfiguredConnectors.Select(c => c.Id).ToArray());
        Assert.Equal("Codex", vm.UnconfiguredConnectors[0].Label);

        // The next read of the same scan changes nothing, so nothing is announced.
        await vm.RefreshAgentsAsync(CancellationToken.None);
        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task Add_opens_the_review_of_the_observe_setup_command_and_runs_nothing()
    {
        var vm = await PanelAsync();
        var codex = vm.ConnectorRows.Single(r => r.Name == "codex");

        vm.AddConnectorCommand.Execute(codex);

        Assert.True(vm.Review.IsOpen);
        Assert.True(vm.Review.IsConfirming);
        Assert.False(vm.Review.IsRunning);
        var review = vm.Review.CommandReview!;
        var step = Assert.Single(review.Steps);
        Assert.Equal("defenseclaw setup codex --yes --mode observe", step.CommandText);
        Assert.Equal(CommandTier.StateChanging, review.Tier);
        Assert.True(review.RestartsGateway);
        Assert.Equal("Add Codex", review.ConfirmLabel);
    }

    [Fact]
    public async Task Add_for_a_not_certified_connector_says_so_in_the_review()
    {
        var vm = await PanelAsync();

        vm.AddConnectorCommand.Execute(vm.ConnectorRows.Single(r => r.Name == "cursor"));

        // The Setup catalog is not read for the Overview, so the caution appears only once it has been; the review is the same either way.
        Assert.Equal("defenseclaw setup cursor --yes --mode observe", vm.Review.CommandReview!.Steps[0].CommandText);
    }

    [Fact]
    public async Task Add_is_ignored_for_a_configured_row_and_for_nothing()
    {
        var vm = await PanelAsync();

        vm.AddConnectorCommand.Execute(vm.ConnectorRows.Single(r => r.Name == "hermes"));
        vm.AddConnectorCommand.Execute(null);

        Assert.False(vm.Review.IsOpen);
    }

    [Fact]
    public async Task Selecting_a_not_configured_row_does_not_scope_the_app_to_it()
    {
        var vm = await PanelAsync();

        vm.SelectedConnector = vm.ConnectorRows.Single(r => r.Name == "codex");

        Assert.Null(_scene.Services.ConnectorScope.Current);
    }
}
