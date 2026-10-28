using System.Globalization;
using System.Text.Json;
using DefenseClaw.Core.AiRuntime;

namespace DefenseClaw.Core.Gateway;

/// <summary>How a read of the effective policy ended.</summary>
public enum EffectivePolicyStatus
{
    /// <summary>The gateway resolved a policy for the account and connector.</summary>
    Ok,

    /// <summary>The gateway has no such route, or serves it as unsupported (HTTP 404 or 405): a runtime without it. A limit of the version, never an error.</summary>
    Unsupported,

    /// <summary>Nothing answered, or the listener is not the gateway: nothing to say, and nothing to blame on the policy.</summary>
    Unreachable,

    /// <summary>The gateway answered, and what it said is not a policy the card can vouch for (refused token, identity lookup failed, no profile, unreadable answer).</summary>
    Error,
}

/// <summary>
/// The policy the gateway applies to one account on one connector, as <c>GET /api/v1/guardrail/profiles/resolve?user=&amp;connector=</c> answers it, plus the
/// policy identity <c>/health</c> carries (<c>policy.effective_digest</c>, <c>policy.generation</c>). Only display fields are kept; nothing else the answer
/// carries (the subject's groups, the cache block, the profile digests) is read.
/// </summary>
/// <param name="ProfilesConfigured">False on an install with no guardrail profiles: the policy is the base configuration and <paramref name="Profile"/> is empty.</param>
/// <param name="Profile">The profile the account gets; empty when no profiles are configured.</param>
/// <param name="Match">Why that profile (<c>default</c>, a user or group match, ...); empty when the gateway did not say.</param>
/// <param name="Mode">The hook mode for the connector (<c>observe</c> or <c>action</c>).</param>
/// <param name="BlockAt">The severity at which a finding blocks.</param>
/// <param name="AlertAt">The severity at which a finding alerts.</param>
/// <param name="RulePack">The rule pack folder (<c>rule_pack_dir</c>).</param>
/// <param name="Digest"><c>policy.effective_digest</c> of the live generation; empty when <c>/health</c> did not publish it.</param>
/// <param name="Generation"><c>policy.generation</c>; empty when <c>/health</c> did not publish it.</param>
/// <param name="Warnings">The gateway's own sentences about this resolution, cut and capped.</param>
public sealed record EffectivePolicy(
    bool ProfilesConfigured,
    string Profile,
    string Match,
    string Mode,
    string BlockAt,
    string AlertAt,
    string RulePack,
    string Digest,
    string Generation,
    IReadOnlyList<string> Warnings);

/// <summary>The outcome of one read: the policy, or the sentence that says why there is none.</summary>
public sealed record EffectivePolicyRead(EffectivePolicyStatus Status, EffectivePolicy? Policy, string Message)
{
    public static EffectivePolicyRead Unsupported { get; } = new(EffectivePolicyStatus.Unsupported, null, string.Empty);

    public static EffectivePolicyRead Unreachable { get; } = new(EffectivePolicyStatus.Unreachable, null, string.Empty);

    public static EffectivePolicyRead Failed(string message) => new(EffectivePolicyStatus.Error, null, message);
}

/// <summary>
/// Reads the effective policy from the gateway's answers. The route and its shape are the ones <c>handleGuardrailProfileResolve</c>
/// (<c>internal/gateway/guardrail_profile.go</c>, routed at <c>api.go:1229</c>) and <c>profileEffectiveView</c> write at 1.0.x; the identity block is
/// <c>PolicyHealth</c> (<c>internal/gateway/generation.go</c>). The route is an administrator view: loopback only, behind the gateway token, and it answers
/// 404 under the Secure Client integration, 405 for anything but GET and 403 for a caller that is a hook; the Mac (<c>GatewayClient.appliedPolicy</c>) fails
/// closed on an identity lookup error or an empty profile and so does this.
/// </summary>
public static class EffectivePolicyReader
{
    /// <summary>The route, relative to the gateway's base address.</summary>
    public const string Route = "api/v1/guardrail/profiles/resolve";

    /// <summary>The identity route: its <c>policy</c> block is read for the digest and the generation.</summary>
    public const string HealthRoute = "health";

    /// <summary>The longest answer read from either route; both are a few hundred bytes to a few KiB.</summary>
    public const int MaxBytes = 256 * 1024;

    private const int MaxWarnings = 4;
    private const int MaxFieldLength = 160;

    /// <summary>The path and query for <paramref name="user"/> and <paramref name="connector"/> (either may be empty: the gateway then resolves without it).</summary>
    public static string Path(string user, string connector)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(connector);
        return Route + "?user=" + Uri.EscapeDataString(user.Trim()) + "&connector=" + Uri.EscapeDataString(connector.Trim());
    }

    /// <summary>
    /// Turns the two gateway answers into a read. <paramref name="health"/> is optional context (null, or not Ok, leaves the digest and generation empty);
    /// it never decides the outcome.
    /// </summary>
    public static EffectivePolicyRead FromGateway(GatewayResult<JsonDocument> resolve, GatewayResult<JsonDocument>? health)
    {
        ArgumentNullException.ThrowIfNull(resolve);

        switch (resolve.Status)
        {
            case GatewayStatus.Ok:
                return resolve.Value is { } document ? Parse(document.RootElement, IdentityOf(health)) : EffectivePolicyRead.Failed("The gateway's answer was empty.");

            case GatewayStatus.Unreachable:
                return EffectivePolicyRead.Unreachable;

            case GatewayStatus.Unauthorized:
                return EffectivePolicyRead.Failed("The gateway refused this app's API token.");

            case GatewayStatus.NotConnected:
                return EffectivePolicyRead.Failed("The gateway is running but its policy engine is not wired up.");

            default:
                return resolve.HttpStatusCode switch
                {
                    404 or 405 => EffectivePolicyRead.Unsupported,
                    403 => EffectivePolicyRead.Failed("The gateway only serves the effective policy to its administrator on this machine (HTTP 403)."),
                    400 => EffectivePolicyRead.Failed("The gateway refused the query: " + AiRuntimeJson.Prose(resolve.ErrorMessage, 200)),
                    { } code => EffectivePolicyRead.Failed("The gateway returned HTTP " + code.ToString(CultureInfo.InvariantCulture) + "."),
                    null => EffectivePolicyRead.Failed("The gateway returned an error: " + AiRuntimeJson.Prose(resolve.ErrorMessage, 200)),
                };
        }
    }

    /// <summary>Reads a resolve answer (fixtures and tests). <paramref name="identity"/> is the digest and generation from <c>/health</c>, or null.</summary>
    public static EffectivePolicyRead Parse(JsonElement root, (string Digest, string Generation)? identity = null)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return EffectivePolicyRead.Failed("The gateway's answer is not a JSON object.");
        }

        var configured = root.TryGetProperty("profiles_configured", out var flag) && flag.ValueKind == JsonValueKind.True;
        var profile = Text(root, "profile");

        // The Mac's rule: with profiles configured an account whose identity could not be resolved, a refused decision or an empty profile is not a policy.
        if (configured)
        {
            if (Text(root, "lookup_error") is { Length: > 0 } lookup)
            {
                return EffectivePolicyRead.Failed("The gateway could not resolve this account: " + lookup);
            }

            if (root.TryGetProperty("refused", out var refused) && refused.ValueKind == JsonValueKind.True)
            {
                var reason = Text(root, "refused_reason");
                return EffectivePolicyRead.Failed("The gateway refused to resolve a profile" + (reason.Length > 0 ? ": " + reason : "."));
            }

            if (profile.Length == 0)
            {
                return EffectivePolicyRead.Failed("The gateway returned no profile for this account.");
            }
        }

        if (!root.TryGetProperty("effective", out var effective) || effective.ValueKind != JsonValueKind.Object || !effective.EnumerateObject().Any())
        {
            return EffectivePolicyRead.Failed("The gateway returned no effective policy.");
        }

        var warnings = new List<string>();
        if (root.TryGetProperty("warnings", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && Cut(AiRuntimeJson.Prose(item.GetString(), 320), 320) is { Length: > 0 } warning && warnings.Count < MaxWarnings)
                {
                    warnings.Add(warning);
                }
            }
        }

        var policy = new EffectivePolicy(
            configured,
            profile,
            Text(root, "match"),
            Text(effective, "mode"),
            Text(effective, "block_at"),
            Text(effective, "alert_at"),
            Text(effective, "rule_pack_dir").Length > 0 ? Text(effective, "rule_pack_dir") : Text(effective, "rule_pack"),
            identity?.Digest ?? Text(root, "digest"),
            identity?.Generation ?? string.Empty,
            warnings);
        return new EffectivePolicyRead(EffectivePolicyStatus.Ok, policy, string.Empty);
    }

    /// <summary>The digest and generation of <c>/health</c>'s <c>policy</c> block, or null when it does not carry one.</summary>
    public static (string Digest, string Generation)? IdentityOf(GatewayResult<JsonDocument>? health)
    {
        if (health is not { IsOk: true, Value: { } document } || document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("policy", out var policy) || policy.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var generation = policy.TryGetProperty("generation", out var number) && number.ValueKind == JsonValueKind.Number && number.TryGetUInt64(out var value)
            ? value.ToString(CultureInfo.InvariantCulture)
            : string.Empty;
        var digest = Text(policy, "effective_digest");
        return digest.Length == 0 && generation.Length == 0 ? null : (digest, generation);
    }

    private static string Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? Cut(AiRuntimeJson.Prose(value.GetString(), MaxFieldLength), MaxFieldLength)
            : string.Empty;

    private static string Cut(string text, int limit) => text.Length <= limit ? text : text[..limit];
}
