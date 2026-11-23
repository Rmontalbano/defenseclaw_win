using System.Text.Json;
using DefenseClaw.Core.Gateway.Models;

namespace DefenseClaw.Tests;

/// <summary>
/// <c>/health</c>'s <c>openclaw_plugin</c> of the wrong shape never fails the whole parse: it is read from the extra keys, a block that is not an object is absent, and a
/// member of the wrong type is dropped while the rest is kept. Synthetic JSON.
/// </summary>
public sealed class OpenClawPluginHealthShapeTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("\"x\"")]
    [InlineData("5")]
    [InlineData("[]")]
    [InlineData("true")]
    [InlineData("null")]
    public void A_block_that_is_not_an_object_is_absent_and_the_other_fields_are_still_read(string value)
    {
        var health = JsonSerializer.Deserialize<GatewayHealth>(
            "{\"uptime_ms\":4200,\"api\":{\"state\":\"running\"},\"openclaw_plugin\":" + value + "}", Web)!;

        Assert.Null(health.OpenClawPlugin);
        Assert.Equal(4200, health.UptimeMs);
        Assert.Equal("running", health.Api!.State);
    }

    [Fact]
    public void A_member_of_the_wrong_type_is_dropped_and_the_other_member_is_kept()
    {
        var health = JsonSerializer.Deserialize<GatewayHealth>(
            "{\"uptime_ms\":1,\"openclaw_plugin\":{\"last_seen_at\":12345,\"hook_mode\":\"native\"}}", Web)!;

        Assert.NotNull(health.OpenClawPlugin);
        Assert.Null(health.OpenClawPlugin!.LastSeenAt);
        Assert.Null(health.OpenClawPlugin.LastSeen);
        Assert.Equal("native", health.OpenClawPlugin.HookMode);
    }

    [Fact]
    public void The_key_stays_in_the_extra_keys_and_a_long_value_is_cut()
    {
        var health = JsonSerializer.Deserialize<GatewayHealth>(
            "{\"uptime_ms\":1,\"openclaw_plugin\":{\"hook_mode\":\"" + new string('m', 500) + "\"}}", Web)!;

        Assert.True(health.AdditionalData!.ContainsKey("openclaw_plugin"));
        Assert.Equal(32, health.OpenClawPlugin!.HookMode!.Length);
    }
}
