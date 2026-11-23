using System.Text.Json;
using DefenseClaw.Core.Gateway.Models;

namespace DefenseClaw.Tests;

/// <summary>
/// The severity of an invalid cascade is judged on the whole text the gateway wrote, not on the text cut for display: the "WEAKER" marker comes last
/// (cascade.go@1.0.10:320-324), so a long validation error used to lose it. Synthetic JSON.
/// </summary>
public sealed class PolicyReloadHealthLongTextTests
{
    private static GatewayHealth Health(string policy) =>
        JsonSerializer.Deserialize<GatewayHealth>(
            "{\"uptime_ms\":1000,\"api\":{\"state\":\"running\"},\"policy\":" + policy + "}",
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    [Fact]
    public void A_long_error_with_the_marker_last_is_still_weaker_and_the_display_is_cut()
    {
        var text = "guardrail.cascade is invalid: " + new string('x', 700) + "; the guardrail decides with the rule pack defaults until the configuration is fixed. This is WEAKER than the configuration you wrote: unknown";

        var reload = Health("{\"cascade_invalid\":" + JsonSerializer.Serialize(text) + "}").PolicyReload;

        Assert.NotNull(reload);
        Assert.True(reload!.IsWeaker);
        Assert.Equal(PolicyReloadHealth.MaxChars, reload.CascadeInvalid.Length);
        Assert.DoesNotContain(PolicyReloadHealth.WeakerMark, reload.CascadeInvalid, StringComparison.Ordinal);
    }

    [Fact]
    public void A_long_error_without_the_marker_is_not_weaker()
    {
        var text = "guardrail.cascade is invalid: " + new string('x', 700) + "; the guardrail decides with the last-known-good cascade until the configuration is fixed";

        var reload = Health("{\"cascade_invalid\":" + JsonSerializer.Serialize(text) + "}").PolicyReload;

        Assert.False(reload!.IsWeaker);
    }

    [Fact]
    public void A_marker_only_in_the_refused_reload_text_does_not_make_the_invalid_text_weaker()
    {
        var reload = Health("{\"cascade_invalid\":\"guardrail.cascade is invalid: x\",\"last_reload_error\":\"guardrail.cascade WEAKER\"}").PolicyReload;

        Assert.False(reload!.IsWeaker);
    }
}
