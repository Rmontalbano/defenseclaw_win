namespace DefenseClaw.Core.Audit;

/// <summary>
/// The default-on shadow measurement rows DefenseClaw 1.0.10 writes (action <c>guardrail-inspection</c>, severity INFO, bucket <c>guardrail.evaluation</c>): the
/// effects lane (details start <c>lane=effects-shadow </c>, then <c>mode= tier= would=</c> ...) and secret flow tracking (target <c>secret-flow:shadow</c>, details
/// <c>rule= mode= kind= ... would= connector=</c>). Each records what the lane <em>would</em> have done and changed nothing, so the row is no alert and no block
/// even when it says <c>would=block</c>. A runtime that does not write them has none, so no version gate is needed: absence is the gate.
/// </summary>
public static class ShadowMeasurementRow
{
    /// <summary>The action of every shadow measurement row.</summary>
    public const string Action = "guardrail-inspection";

    /// <summary>The words that start the details of an effects lane row.</summary>
    public const string EffectsLaneMark = "lane=effects-shadow";

    /// <summary>The target of a secret flow row.</summary>
    public const string SecretFlowTarget = "secret-flow:shadow";

    /// <summary>The label of the inspector row and the title of the row.</summary>
    public const string Headline = "Shadow measurement (no action taken)";

    /// <summary>The longest <c>would=</c> value the app repeats.</summary>
    internal const int WouldLimit = 32;

    /// <summary>True for an effects lane or secret flow shadow measurement row.</summary>
    public static bool IsShadowMeasurement(string? action, string? target, string? details) =>
        string.Equals(action?.Trim(), Action, StringComparison.OrdinalIgnoreCase)
        && (IsEffectsLane(details) || IsSecretFlow(target));

    /// <summary>True when the details start <c>lane=effects-shadow</c> followed by a space or the end of the text.</summary>
    public static bool IsEffectsLane(string? details)
    {
        if (string.IsNullOrEmpty(details))
        {
            return false;
        }

        var text = details.AsSpan().TrimStart();
        if (!text.StartsWith(EffectsLaneMark, StringComparison.Ordinal))
        {
            return false;
        }

        return text.Length == EffectsLaneMark.Length || char.IsWhiteSpace(text[EffectsLaneMark.Length]);
    }

    /// <summary>True when the target is exactly <c>secret-flow:shadow</c>.</summary>
    public static bool IsSecretFlow(string? target) => string.Equals(target?.Trim(), SecretFlowTarget, StringComparison.Ordinal);

    /// <summary>The <c>would=</c> token's value (what the lane would have done), at most <see cref="WouldLimit"/> characters; empty when the details have none.</summary>
    public static string Would(string? details)
    {
        if (string.IsNullOrEmpty(details))
        {
            return string.Empty;
        }

        foreach (var token in details.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.StartsWith("would=", StringComparison.Ordinal))
            {
                var value = token["would=".Length..].Trim('"', '\'');
                return value.Length <= WouldLimit ? value : value[..WouldLimit];
            }
        }

        return string.Empty;
    }

    /// <summary>The inspector's sentence for a row; null when the row is not a shadow measurement.</summary>
    public static string? Describe(string? action, string? target, string? details)
    {
        if (!IsShadowMeasurement(action, target, details))
        {
            return null;
        }

        var subject = IsEffectsLane(details) ? "the effects lane" : "secret flow tracking";
        var would = Would(details);
        return would.Length > 0
            ? $"What {subject} would have done (would={would}). The call was not changed."
            : $"What {subject} would have done. The call was not changed.";
    }
}
