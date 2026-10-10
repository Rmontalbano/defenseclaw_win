using System.Text.Json;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Gateway.Models;

namespace DefenseClaw.App.Tests.Overview;

/// <summary>
/// CUST-341 / CUST-249: the Overview's health-derived surfaces (uptime, the Services card's gateway uptime, and the note above the Services,
/// Scanners and Connectors boxes) say so when <c>/health</c> came from a peer that is not the verified gateway. Synthetic data only.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class OverviewHealthTrustTests : IDisposable
{
    private readonly OverviewScene _scene = OverviewScene.Create();

    public void Dispose() => _scene.Dispose();

    private static GatewayHealth Health() => JsonSerializer.Deserialize<GatewayHealth>(
        "{\"uptime_ms\":600000,\"api\":{\"state\":\"running\"},\"guardrail\":{\"state\":\"running\"},\"gateway\":{\"state\":\"running\"}," +
        "\"connector\":{\"name\":\"claudecode\",\"state\":\"running\",\"requests\":5},\"connectors\":[{\"name\":\"claudecode\",\"state\":\"running\",\"requests\":5}]}",
        new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private static GatewaySnapshot Snapshot(bool unverified) => OverviewScene.Snapshot(health: Health()) with { PeerUnverified = unverified };

    [Fact]
    public void An_unverified_peers_uptime_and_gateway_card_are_marked_and_the_boxes_carry_the_note()
    {
        var vm = new OverviewPanelViewModel(_scene.Services);

        vm.Apply(Snapshot(unverified: true));

        Assert.EndsWith(HealthTrustPresentation.UnverifiedSuffix, vm.UptimeText, StringComparison.Ordinal);
        Assert.Equal(HealthTrustPresentation.UnverifiedNote, vm.HealthTrustNote);
        var gateway = Assert.Single(vm.ServiceRows, r => r.Key == "gateway");
        Assert.Contains("(unverified)", gateway.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_verified_gateways_health_is_shown_plainly_with_no_note()
    {
        var vm = new OverviewPanelViewModel(_scene.Services);

        vm.Apply(Snapshot(unverified: false));

        Assert.DoesNotContain("unverified", vm.UptimeText, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(vm.HealthTrustNote);
        var gateway = Assert.Single(vm.ServiceRows, r => r.Key == "gateway");
        Assert.DoesNotContain("(unverified)", gateway.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unverified_peer_that_answered_nothing_has_no_health_to_mark()
    {
        var vm = new OverviewPanelViewModel(_scene.Services);

        vm.Apply(Snapshot(unverified: true) with { Health = null });

        Assert.Equal("—", vm.UptimeText);
        Assert.Empty(vm.HealthTrustNote);
    }
}

/// <summary>CUST-341: the one shared rule for marking health-derived figures, tested without any view.</summary>
public sealed class HealthTrustPresentationTests
{
    private static GatewaySnapshot WithHealth(bool unverified) => new()
    {
        State = AppGatewayState.Running,
        Health = new GatewayHealth(),
        PeerUnverified = unverified,
        PolledAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void An_unverified_figure_gets_the_suffix_and_a_verified_one_does_not()
    {
        Assert.Equal("2h (unverified)", HealthTrustPresentation.Mark("2h", WithHealth(unverified: true)));
        Assert.Equal("2h", HealthTrustPresentation.Mark("2h", WithHealth(unverified: false)));
    }

    [Fact]
    public void Nothing_is_suffixed_when_there_is_no_text_or_no_health()
    {
        Assert.Equal(string.Empty, HealthTrustPresentation.Mark(string.Empty, WithHealth(unverified: true)));
        Assert.Equal("2h", HealthTrustPresentation.Mark("2h", WithHealth(unverified: true) with { Health = null }));
    }

    [Fact]
    public void The_note_is_present_only_for_unverified_health()
    {
        Assert.Equal(HealthTrustPresentation.UnverifiedNote, HealthTrustPresentation.NoteFor(WithHealth(unverified: true)));
        Assert.Empty(HealthTrustPresentation.NoteFor(WithHealth(unverified: false)));
        Assert.Empty(HealthTrustPresentation.NoteFor(WithHealth(unverified: true) with { Health = null }));
    }
}
