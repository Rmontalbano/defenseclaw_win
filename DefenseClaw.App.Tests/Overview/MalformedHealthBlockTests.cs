using System.Text.Json;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Gateway;
using DefenseClaw.Core.Gateway.Models;
using DefenseClaw.Core.Install;

namespace DefenseClaw.App.Tests.Overview;

/// <summary>
/// <c>sinks</c> is a /health block the health model does not name, so it stays untyped until the Observability card parses it. A block of the wrong
/// shape (a number for <c>state</c>, a date that is not one) made that parse throw out of <c>OverviewPanelViewModel.Apply</c>, which aborted the Apply
/// before it re-derived the gateway action, the enforcement cards and the doctor card, and skipped the data refresh that follows it on every poll.
/// Whatever answers on the gateway port decides what /health says, so a block of the wrong shape is skipped, not fatal.
/// </summary>
public sealed class MalformedHealthBlockTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private AppServices? _services;

    public void Dispose()
    {
        _services?.Dispose();
        _temp.Dispose();
    }

    private OverviewPanelViewModel Panel()
    {
        _services = TestServices.Create(_temp, "guardrail:\n  enabled: true\n  connector: claudecode\n");
        return new OverviewPanelViewModel(_services);
    }

    private static GatewayHealth Health(string body) =>
        JsonSerializer.Deserialize<GatewayHealth>("{" + body + "}", new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    /// <summary>A reachable, installed gateway whose /health carries the given extra top-level members.</summary>
    private static GatewaySnapshot Running(string extraMembers) => new()
    {
        State = AppGatewayState.Running,
        Install = InstallState.Running,
        Detail = "ok",
        Health = Health("\"uptime_ms\":600000,\"api\":{\"state\":\"running\"}," + extraMembers),
        PolledAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void A_well_formed_sinks_block_still_fills_the_observability_card()
    {
        var vm = Panel();
        vm.Apply(GatewaySnapshot.Initial);

        vm.Apply(Running("\"sinks\":{\"state\":\"running\",\"details\":{\"sinks\":[{\"name\":\"audit-file\",\"kind\":\"file\",\"state\":\"healthy\",\"enabled\":true}]}}"));

        var row = Assert.Single(vm.ObservabilityRows);
        Assert.Equal("audit-file", row.Name);
        Assert.Equal("audit_sinks", row.Target);
        Assert.Equal("healthy", row.State);
        Assert.Equal("Ok", row.StateKey);
        Assert.Equal("Restart Gateway", vm.GatewayActionLabel);
    }

    [Theory]
    [InlineData("\"sinks\":{\"state\":5}")]
    [InlineData("\"sinks\":{\"since\":\"not-a-date\"}")]
    [InlineData("\"sinks\":{\"last_error\":{\"a\":1}}")]
    [InlineData("\"sinks\":{\"state\":\"running\",\"details\":{\"sinks\":\"nope\"}}")]
    [InlineData("\"sinks\":[1,2,3]")]
    [InlineData("\"sinks\":\"text\"")]
    [InlineData("\"sinks\":null")]
    public void A_sinks_block_of_the_wrong_shape_is_skipped_and_the_rest_of_the_panel_still_renders(string sinks)
    {
        var vm = Panel();
        vm.Apply(GatewaySnapshot.Initial);
        Assert.Equal("Start Gateway", vm.GatewayActionLabel);

        var thrown = Record.Exception(() => vm.Apply(Running(sinks)));

        Assert.Null(thrown);
        Assert.Empty(vm.ObservabilityRows);
        // ApplyGatewayActions runs after the Observability card is built: it only follows when that step did not throw.
        Assert.Equal("Restart Gateway", vm.GatewayActionLabel);
    }
}
