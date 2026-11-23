using System.Text.Json;
using DefenseClaw.Core.Text;

namespace DefenseClaw.Core.Gateway.Models;

/// <summary>
/// The two things <c>/health</c>'s top-level <c>policy</c> object says about an invalid guardrail cascade (runtime 1.0.10, <c>PolicyHealth</c>,
/// generation.go@1.0.10:154-179):
/// <list type="bullet">
///   <item><c>cascade_invalid</c>: set while the live configuration's cascade was invalid and the gateway applied a fallback (the last-known-good
///     cascade or the rule pack defaults). The gateway's text already says what it decides with, and ends in "This is WEAKER than the configuration you
///     wrote" when the fallback protects less.</item>
///   <item><c>last_reload_error</c>: the last refused reload. It also carries other refusals and the OPA fallback (<c>opa: ...</c>), so it is used only
///     when it names <c>guardrail.cascade</c>; the gateway says "the running configuration stays until it is fixed".</item>
/// </list>
/// Read from <see cref="GatewayHealth.AdditionalData"/>, never bound to a typed <c>policy</c> member, so a <c>policy</c> of the wrong shape can never fail
/// the whole <c>/health</c> parse and the other readers of that object are not disturbed. Both keys are absent before 1.0.10 and while nothing is wrong,
/// so there is no capability gate: a null answer is "nothing to report". Each text is bounded and only drawn.
/// </summary>
public sealed record PolicyReloadHealth(string CascadeInvalid, string CascadeReloadError, bool IsWeaker = false)
{
    /// <summary>The longest text looked at to judge <see cref="IsWeaker"/>: the gateway puts the marker last, so it is judged on the whole text before the display is cut.</summary>
    public const int MaxJudgedChars = 16384;

    /// <summary>The longest text kept of each.</summary>
    public const int MaxChars = 400;

    /// <summary>The text of <c>guardrail.cascade</c> that marks a refusal as one about the cascade.</summary>
    public const string CascadeKey = "guardrail.cascade";

    /// <summary>The gateway's wording when the fallback is weaker than what was written.</summary>
    public const string WeakerMark = "WEAKER";

    /// <summary>What there is to report; null when neither key says anything about the cascade.</summary>
    public static PolicyReloadHealth? From(IReadOnlyDictionary<string, JsonElement>? additionalData)
    {
        if (additionalData is null || !additionalData.TryGetValue("policy", out var policy) || policy.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var invalid = Text(policy, "cascade_invalid");
        var reload = Text(policy, "last_reload_error");
        if (reload.StartsWith("opa:", StringComparison.OrdinalIgnoreCase) || !reload.Contains(CascadeKey, StringComparison.OrdinalIgnoreCase))
        {
            reload = string.Empty;
        }

        return invalid.Length == 0 && reload.Length == 0 ? null : new PolicyReloadHealth(invalid, reload, MentionsWeaker(policy, "cascade_invalid"));
    }

    /// <summary>True when the whole <c>cascade_invalid</c> text (up to <see cref="MaxJudgedChars"/>) holds the marker, whatever is later cut for display.</summary>
    private static bool MentionsWeaker(JsonElement policy, string key)
    {
        if (!policy.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var full = value.GetString() ?? string.Empty;
        return (full.Length <= MaxJudgedChars ? full : full[..MaxJudgedChars]).Contains(WeakerMark, StringComparison.Ordinal);
    }

    private static string Text(JsonElement policy, string key)
    {
        if (!policy.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return string.Empty;
        }

        var visible = DisplayNames.Visible((value.GetString() ?? string.Empty).Trim());
        return visible.Length <= MaxChars ? visible : visible[..MaxChars];
    }
}
