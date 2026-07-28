namespace DefenseClaw.Core.Audit;

/// <summary>
/// Ordered severity ladder. Values observed in the live audit DB are INFO, LOW, MEDIUM,
/// HIGH and CRITICAL; WARN appears in log and alert payloads and is ranked between LOW
/// and MEDIUM. Ordering matters: "severity minimum" filters compare on the enum value.
/// </summary>
public enum AuditSeverity
{
    Unknown = 0,
    Info = 10,
    Low = 20,
    Warn = 30,
    Medium = 40,
    High = 50,
    Critical = 60,
}

public static class AuditSeverityExtensions
{
    /// <summary>Canonical uppercase spellings as stored in <c>audit_events.severity</c>.</summary>
    private static readonly IReadOnlyDictionary<AuditSeverity, string[]> Spellings =
        new Dictionary<AuditSeverity, string[]>
        {
            [AuditSeverity.Info] = new[] { "INFO", "INFORMATIONAL", "NOTICE", "DEBUG", "TRACE" },
            [AuditSeverity.Low] = new[] { "LOW" },
            [AuditSeverity.Warn] = new[] { "WARN", "WARNING" },
            [AuditSeverity.Medium] = new[] { "MEDIUM", "MODERATE" },
            [AuditSeverity.High] = new[] { "HIGH" },
            [AuditSeverity.Critical] = new[] { "CRITICAL", "FATAL" },
        };

    /// <summary>Every non-Unknown level, ascending.</summary>
    public static IReadOnlyList<AuditSeverity> Ladder { get; } = new[]
    {
        AuditSeverity.Info,
        AuditSeverity.Low,
        AuditSeverity.Warn,
        AuditSeverity.Medium,
        AuditSeverity.High,
        AuditSeverity.Critical,
    };

    public static AuditSeverity Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return AuditSeverity.Unknown;
        }

        var normalized = value.Trim().ToUpperInvariant();
        foreach (var (severity, spellings) in Spellings)
        {
            if (Array.IndexOf(spellings, normalized) >= 0)
            {
                return severity;
            }
        }

        return AuditSeverity.Unknown;
    }

    /// <summary>Canonical stored spelling, e.g. <c>HIGH</c>.</summary>
    public static string ToStoredValue(this AuditSeverity severity) =>
        Spellings.TryGetValue(severity, out var spellings) ? spellings[0] : "UNKNOWN";

    /// <summary>
    /// Every stored spelling at or above <paramref name="minimum"/>. The reader turns this
    /// into a <c>severity IN (…)</c> predicate so filtering stays index-friendly rather
    /// than needing a CASE expression per row.
    /// </summary>
    public static IReadOnlyList<string> AtOrAbove(AuditSeverity minimum)
    {
        var result = new List<string>();
        foreach (var (severity, spellings) in Spellings)
        {
            if (severity >= minimum)
            {
                result.AddRange(spellings);
            }
        }

        return result;
    }
}
