namespace DefenseClaw.Core.Audit;

/// <summary>
/// The extra labelled values the Audit inspector and the Logs detail show for the 1.0.10 guardrail rows: a shadow measurement (<see cref="ShadowMeasurementRow"/>),
/// an inspection timeout (<see cref="InspectTimeoutRow"/>) and the cascade's degraded row (<see cref="CascadeAuditRow"/>). None needs a runtime gate: a runtime
/// that does not write the row never produces the values. The same rows feed both panels so they cannot disagree.
/// </summary>
public static class GuardrailRowNotes
{
    /// <summary>The inspector label of a shadow measurement.</summary>
    public const string ShadowLabel = "Shadow measurement";

    /// <summary>The inspector label of an inspection timeout.</summary>
    public const string TimeoutLabel = "Inspection timeout";

    /// <summary>The inspector label of the cascade row.</summary>
    public const string CascadeLabel = "Guardrail cascade";

    /// <summary>The inspector label of the runtime's own cascade text.</summary>
    public const string CascadeTextLabel = "Runtime says";

    /// <summary>The row's title in place of the bare action word; null for any other row.</summary>
    public static string? Headline(string? action, string? target, string? details)
    {
        if (ShadowMeasurementRow.IsShadowMeasurement(action, target, details))
        {
            return ShadowMeasurementRow.Headline;
        }

        if (InspectTimeoutRow.Parse(action, details) is { } timeout)
        {
            return timeout.Label;
        }

        return CascadeAuditRow.IsCascade(action, target, details) ? CascadeAuditRow.Headline : null;
    }

    /// <summary>The labelled values for the row, in the order shown; empty for any other row.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> For(string? action, string? target, string? details)
    {
        if (ShadowMeasurementRow.Describe(action, target, details) is { } shadow)
        {
            return new[] { new KeyValuePair<string, string>(ShadowLabel, shadow) };
        }

        if (InspectTimeoutRow.Parse(action, details) is { } timeout)
        {
            return new[] { new KeyValuePair<string, string>(TimeoutLabel, timeout.Label) };
        }

        if (CascadeAuditRow.IsCascade(action, target, details))
        {
            var rows = new List<KeyValuePair<string, string>> { new(CascadeLabel, CascadeAuditRow.Headline) };
            if (CascadeAuditRow.Text(details) is { Length: > 0 } text)
            {
                rows.Add(new KeyValuePair<string, string>(CascadeTextLabel, text));
            }

            return rows;
        }

        return Array.Empty<KeyValuePair<string, string>>();
    }
}
