using System.Text.Json;
using DefenseClaw.Core.Gateway.Models;

namespace DefenseClaw.Tests;

/// <summary>
/// The invalid-cascade part of <c>/health</c>'s top-level <c>policy</c> object (runtime 1.0.10, <see cref="PolicyReloadHealth"/>): <c>cascade_invalid</c>
/// as the gateway writes it, <c>last_reload_error</c> only when it names <c>guardrail.cascade</c>, nothing from the OPA fallback or another refusal, and
/// nothing at all on the owner's machine (a healthy policy block) or before 1.0.10 (no keys). Synthetic JSON.
/// </summary>
public sealed class PolicyReloadHealthTests
{
    private const string Invalid =
        "guardrail.cascade is invalid: unknown: sideways is not one of pack, alert, confirm, block; the guardrail decides with the rule pack defaults " +
        "(no guardrail.cascade settings) until the configuration is fixed. This is WEAKER than the configuration you wrote: unknown";

    private const string Refused =
        "config reload refused: guardrail.cascade is invalid: judge.max_action: x; the running configuration stays until it is fixed (defenseclaw guardrail cascade explain)";

    private static GatewayHealth Health(string policy) =>
        JsonSerializer.Deserialize<GatewayHealth>(
            "{\"uptime_ms\":1000,\"api\":{\"state\":\"running\"}" + (policy.Length > 0 ? ",\"policy\":" + policy : string.Empty) + "}",
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    [Fact]
    public void A_healthy_policy_block_says_nothing()
    {
        var health = Health("{\"effective_digest\":\"abc\",\"generation\":4,\"config_generation\":4,\"component_count\":7}");

        Assert.Null(health.PolicyReload);
    }

    [Fact]
    public void No_policy_block_at_all_says_nothing()
    {
        Assert.Null(Health(string.Empty).PolicyReload);
    }

    [Fact]
    public void The_policy_object_stays_in_the_additional_data_for_its_other_readers()
    {
        var health = Health("{\"generation\":4,\"cascade_invalid\":\"x\"}");

        Assert.True(health.AdditionalData!.ContainsKey("policy"));
    }

    [Fact]
    public void An_invalid_cascade_is_the_gateways_own_text_and_a_weaker_fallback_is_marked()
    {
        var reload = Health("{\"cascade_invalid\":" + JsonSerializer.Serialize(Invalid) + "}").PolicyReload;

        Assert.NotNull(reload);
        Assert.Equal(Invalid, reload!.CascadeInvalid);
        Assert.True(reload.IsWeaker);
        Assert.Equal(string.Empty, reload.CascadeReloadError);
    }

    [Fact]
    public void A_fallback_that_is_not_weaker_is_not_marked()
    {
        var reload = Health("{\"cascade_invalid\":\"guardrail.cascade is invalid: x; the guardrail decides with the last-known-good cascade until the configuration is fixed\"}").PolicyReload;

        Assert.False(reload!.IsWeaker);
    }

    [Fact]
    public void A_refused_reload_that_names_the_cascade_is_kept()
    {
        var reload = Health("{\"last_reload_error\":" + JsonSerializer.Serialize(Refused) + "}").PolicyReload;

        Assert.Equal(Refused, reload!.CascadeReloadError);
        Assert.Equal(string.Empty, reload.CascadeInvalid);
    }

    [Theory]
    [InlineData("opa: opa binary not found")]
    [InlineData("opa: guardrail.cascade policy failed to compile")]
    [InlineData("config reload refused: gateway.api_port is invalid")]
    [InlineData("scanner pin mismatch")]
    [InlineData("")]
    public void Other_reload_errors_and_the_opa_fallback_say_nothing(string error)
    {
        var health = Health("{\"last_reload_error\":" + JsonSerializer.Serialize(error) + ",\"opa_unavailable\":\"x\"}");

        Assert.Null(health.PolicyReload);
    }

    [Theory]
    [InlineData("{\"cascade_invalid\":3,\"last_reload_error\":{\"a\":1}}")]
    [InlineData("{\"cascade_invalid\":null,\"last_reload_error\":null}")]
    [InlineData("{\"cascade_invalid\":\"\"}")]
    [InlineData("[1,2]")]
    [InlineData("\"policy\"")]
    [InlineData("7")]
    public void A_field_or_block_of_the_wrong_shape_says_nothing_and_does_not_fail_the_parse(string policy)
    {
        Assert.Null(Health(policy).PolicyReload);
    }

    [Fact]
    public void Each_text_is_cut_to_400_characters_and_made_fit_to_draw()
    {
        var long400 = "guardrail.cascade is invalid: " + new string('e', 2000);
        var reload = Health("{\"cascade_invalid\":" + JsonSerializer.Serialize(long400) + ",\"last_reload_error\":" + JsonSerializer.Serialize("guardrail.cascade\nbroken‮" + new string('q', 900)) + "}").PolicyReload!;

        Assert.Equal(PolicyReloadHealth.MaxChars, reload.CascadeInvalid.Length);
        Assert.True(reload.CascadeReloadError.Length <= PolicyReloadHealth.MaxChars);
        Assert.DoesNotContain('\n', reload.CascadeReloadError);
        Assert.DoesNotContain('‮', reload.CascadeReloadError);
    }
}
