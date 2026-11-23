namespace DefenseClaw.Core.Audit;

/// <summary>
/// The audit row DefenseClaw 1.0.10 writes when <c>guardrail.cascade</c> cannot be applied as written: action <c>guardrail-degraded</c>, severity HIGH, bucket
/// <c>platform.health</c>, actor <c>guardrail</c>, details <c>guardrail degraded: guardrail.cascade is invalid: ...</c> or <c>guardrail degraded: guardrail.cascade:
/// the gateway does not apply N narrower-scope setting(s); ...</c>. A <c>guardrail-degraded</c> row for any other subsystem is not this row and is left as it is.
/// The target is not confirmed in upstream source (UNVERIFIED), so either the target <c>guardrail.cascade</c> or the details text marks the row.
/// </summary>
public static class CascadeAuditRow
{
    /// <summary>The action of every degraded-subsystem row.</summary>
    public const string Action = "guardrail-degraded";

    /// <summary>The title of the row.</summary>
    public const string Headline = "Guardrail cascade not applied as written";

    /// <summary>The most characters of the runtime's text the app shows.</summary>
    public const int TextLimit = 600;

    private const string Subsystem = "guardrail.cascade";
    private const string Prefix = "guardrail degraded: ";

    /// <summary>True for the cascade's degraded row.</summary>
    public static bool IsCascade(string? action, string? target, string? details)
    {
        if (!string.Equals(action?.Trim(), Action, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(target?.Trim(), Subsystem, StringComparison.Ordinal))
        {
            return true;
        }

        if (string.IsNullOrEmpty(details))
        {
            return false;
        }

        var text = details.AsSpan().Trim();
        if (text.StartsWith(Prefix, StringComparison.Ordinal))
        {
            text = text[Prefix.Length..];
        }

        return text.StartsWith("guardrail.cascade is invalid:", StringComparison.Ordinal)
            || text.StartsWith("guardrail.cascade: ", StringComparison.Ordinal);
    }

    /// <summary>The runtime's own details text, on one line and at most <see cref="TextLimit"/> characters.</summary>
    public static string Text(string? details)
    {
        if (string.IsNullOrWhiteSpace(details))
        {
            return string.Empty;
        }

        var text = details.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= TextLimit ? text : text[..TextLimit];
    }
}
