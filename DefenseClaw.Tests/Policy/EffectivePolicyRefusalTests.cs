using System.Text.Json;
using DefenseClaw.Core.Gateway;

namespace DefenseClaw.Tests.Policy;

/// <summary>
/// CUST-401: the two fields 1.0.5's <c>handleGuardrailProfileResolve</c> adds (<c>internal/gateway/guardrail_profile.go:1835-1846</c>): <c>refused_paths</c> and
/// <c>sandbox_refused_reason</c>. A sandbox refusal beside a decision that is not refused is still a policy (the hook decision); a refusal on a request path is
/// not, and the message names the path. Synthetic answers; an older gateway's answer reads exactly as before.
/// </summary>
public sealed class EffectivePolicyRefusalTests
{
    private const string Reason = "DefenseClaw refused this request: group membership is unconfirmed; run `defenseclaw guardrail profile explain --user operator` to inspect the identity decision";

    private static EffectivePolicyRead Read(string json)
    {
        using var document = JsonDocument.Parse(json);
        return EffectivePolicyReader.Parse(document.RootElement.Clone());
    }

    [Fact]
    public void A_sandbox_only_refusal_is_a_policy_with_the_reason_and_without_the_gateways_duplicate_warning()
    {
        var read = Read(
            "{\"profiles_configured\":true,\"profile\":\"strict\",\"match\":\"user\",\"refused_paths\":[\"sandbox\"],\"sandbox_refused_reason\":\"" + Reason + "\"," +
            "\"warnings\":[\"a sandbox run of this account for codex is refused (" + Reason + "); its hook and inspect requests are not\",\"another note\"]," +
            "\"effective\":{\"mode\":\"action\",\"block_at\":\"HIGH\",\"alert_at\":\"MEDIUM\"}}");

        Assert.Equal(EffectivePolicyStatus.Ok, read.Status);
        var policy = read.Policy!;
        Assert.Equal("strict", policy.Profile);
        Assert.Equal(new[] { "sandbox" }, policy.RefusedOn);
        Assert.Equal(Reason, policy.SandboxRefusedReason);
        Assert.Equal(new[] { "another note" }, policy.Warnings);
    }

    [Fact]
    public void Without_the_new_fields_the_answer_reads_as_it_did()
    {
        var read = Read("{\"profiles_configured\":true,\"profile\":\"strict\",\"warnings\":[\"a sandbox run of this account for codex is refused (x); its hook and inspect requests are not\"],\"effective\":{\"mode\":\"observe\"}}");

        var policy = read.Policy!;
        Assert.Empty(policy.RefusedOn);
        Assert.Equal(string.Empty, policy.SandboxRefusedReason);
        Assert.Single(policy.Warnings);
    }

    [Theory]
    [InlineData("[\"hook\"]", "for hook requests")]
    [InlineData("[\"hook\",\"inspect\"]", "for hook and inspect requests")]
    [InlineData("[\"hook\",\"unknown\",\"hook\"]", "for hook requests")]
    public void A_refusal_on_a_request_path_is_not_a_policy_and_names_the_path(string paths, string where)
    {
        var read = Read("{\"profiles_configured\":true,\"profile\":\"strict\",\"refused\":true,\"refused_reason\":\"" + Reason + "\",\"refused_paths\":" + paths + ",\"effective\":{\"mode\":\"action\"}}");

        Assert.Equal(EffectivePolicyStatus.Error, read.Status);
        Assert.Null(read.Policy);
        Assert.Contains(where, read.Message, StringComparison.Ordinal);
        Assert.Contains("group membership is unconfirmed", read.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_refusal_on_both_paths_names_none_and_is_still_an_error()
    {
        // explainOnRequestPaths (identity_resolution.go:551) leaves refused_paths out when hook and inspect both refuse.
        var read = Read("{\"profiles_configured\":true,\"profile\":\"strict\",\"refused\":true,\"refused_reason\":\"identity unconfirmed\",\"effective\":{\"mode\":\"action\"}}");

        Assert.Equal(EffectivePolicyStatus.Error, read.Status);
        Assert.Equal("The gateway refused to resolve a profile: identity unconfirmed", read.Message);
    }
}
