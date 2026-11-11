using System.Text.Json;
using DefenseClaw.Core.Gateway.Models;

namespace DefenseClaw.Tests;

/// <summary>
/// <c>/health</c> <c>connectors[].surfaces</c> (runtime 1.0.8; Claude Cowork is the <c>cowork</c> surface of <c>claudecode</c>). The counters are the hook's own
/// label on a call: they are read, never trusted as coverage. Whatever answers on the gateway port decides what /health says, so a <c>surfaces</c> of the wrong
/// shape must read as "none" and must never fail the parse of the whole document. Synthetic payloads only.
/// </summary>
public class ConnectorSurfaceHealthTests
{
    private static GatewayHealth Health(string connectorMembers) =>
        JsonSerializer.Deserialize<GatewayHealth>(
            "{\"uptime_ms\":600000,\"api\":{\"state\":\"running\"},\"connectors\":[{\"name\":\"claudecode\",\"state\":\"running\",\"requests\":40" + connectorMembers + "},{\"name\":\"codex\",\"requests\":3}]}",
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    [Fact]
    public void A_cowork_surface_is_read_with_its_counter_and_both_times()
    {
        var health = Health(",\"surfaces\":{\"cowork\":{\"requests\":12,\"last_activity_at\":\"2026-11-01T12:00:00Z\",\"last_session_start_at\":\"2026-11-01T11:58:30Z\"}}");

        var claude = health.Connectors[0];
        var cowork = Assert.IsType<ConnectorSurfaceHealth>(claude.Cowork);
        Assert.Equal(12, cowork.Requests);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero), cowork.LastActivityAt);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 11, 58, 30, TimeSpan.Zero), cowork.LastSessionStartAt);
        Assert.Single(claude.LabelledSurfaces);
        Assert.Equal(40, claude.Requests);
    }

    [Fact]
    public void The_surface_name_is_matched_without_case_and_other_surfaces_are_kept()
    {
        var health = Health(",\"surfaces\":{\"Cowork\":{\"requests\":2},\"terminal\":{\"requests\":5}}");

        var claude = health.Connectors[0];
        Assert.Equal(2, claude.Cowork?.Requests);
        Assert.Equal(5, claude.LabelledSurfaces["terminal"].Requests);
    }

    [Fact]
    public void No_surfaces_key_reads_as_none()
    {
        var health = Health(string.Empty);

        Assert.Empty(health.Connectors[0].LabelledSurfaces);
        Assert.Null(health.Connectors[0].Cowork);
        Assert.Null(health.Connectors[1].Cowork);
    }

    [Theory]
    [InlineData(",\"surfaces\":\"text\"")]
    [InlineData(",\"surfaces\":[1,2,3]")]
    [InlineData(",\"surfaces\":7")]
    [InlineData(",\"surfaces\":null")]
    [InlineData(",\"surfaces\":{\"cowork\":\"yes\"}")]
    [InlineData(",\"surfaces\":{\"cowork\":[1]}")]
    [InlineData(",\"surfaces\":{\"cowork\":null}")]
    public void A_surfaces_block_of_the_wrong_shape_reads_as_none_and_the_whole_health_still_parses(string members)
    {
        var health = Health(members);

        Assert.Equal(2, health.Connectors.Count);
        Assert.Equal("running", health.Api?.State);
        Assert.Null(health.Connectors[0].Cowork);
        Assert.Equal(40, health.Connectors[0].Requests);
    }

    [Fact]
    public void A_counter_or_time_of_the_wrong_type_reads_as_zero_or_unknown()
    {
        var health = Health(",\"surfaces\":{\"cowork\":{\"requests\":\"many\",\"last_activity_at\":\"not-a-date\",\"last_session_start_at\":12}}");

        var cowork = Assert.IsType<ConnectorSurfaceHealth>(health.Connectors[0].Cowork);
        Assert.Equal(0, cowork.Requests);
        Assert.Null(cowork.LastActivityAt);
        Assert.Null(cowork.LastSessionStartAt);
    }

    [Fact]
    public void A_negative_or_fractional_counter_is_clamped_to_a_whole_number()
    {
        var negative = Assert.IsType<ConnectorSurfaceHealth>(Health(",\"surfaces\":{\"cowork\":{\"requests\":-4}}").Connectors[0].Cowork);
        var fractional = Assert.IsType<ConnectorSurfaceHealth>(Health(",\"surfaces\":{\"cowork\":{\"requests\":3.0}}").Connectors[0].Cowork);

        Assert.Equal(0, negative.Requests);
        Assert.Equal(3, fractional.Requests);
    }

    [Fact]
    public void The_top_level_connector_block_carries_surfaces_too()
    {
        var health = JsonSerializer.Deserialize<GatewayHealth>(
            "{\"connector\":{\"name\":\"claudecode\",\"surfaces\":{\"cowork\":{\"requests\":1}}}}",
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        Assert.Equal(1, health.Connector?.Cowork?.Requests);
    }
}
