namespace DefenseClaw.Core.Audit;

/// <summary>Filter + pagination spec for <see cref="AuditReader"/>.</summary>
public sealed record AuditQuery
{
    /// <summary>Guard rail so a runaway page size cannot pull the whole table into memory.</summary>
    public const int MaxLimit = 5000;

    /// <summary>Restrict to a single bucket, e.g. <c>guardrail.evaluation</c>.</summary>
    public string? Bucket { get; init; }

    /// <summary>Restrict to any of several buckets. Combined with <see cref="Bucket"/> via OR.</summary>
    public IReadOnlyList<string>? Buckets { get; init; }

    /// <summary>Drop anything below this level.</summary>
    public AuditSeverity? MinimumSeverity { get; init; }

    /// <summary>Exact connector match. Use <see cref="IncludeNullConnector"/> for platform rows.</summary>
    public string? Connector { get; init; }

    /// <summary>Keep rows whose connector is NULL alongside a <see cref="Connector"/> match.</summary>
    public bool IncludeNullConnector { get; init; }

    /// <summary>Case-insensitive substring match on <c>action</c>.</summary>
    public string? ActionContains { get; init; }

    /// <summary>Case-insensitive substring match across details/event_name/tool_name/target.</summary>
    public string? SearchText { get; init; }

    /// <summary>Inclusive lower bound.</summary>
    public DateTimeOffset? From { get; init; }

    /// <summary>Exclusive upper bound.</summary>
    public DateTimeOffset? To { get; init; }

    /// <summary>Page size. Clamped to [1, <see cref="MaxLimit"/>].</summary>
    public int Limit { get; init; } = 100;

    /// <summary>Keyset anchor: return rows strictly after this one in sort order.</summary>
    public AuditCursor? After { get; init; }

    /// <summary>Default is newest-first, which is what every panel wants.</summary>
    public bool Ascending { get; init; }

    public int EffectiveLimit => Math.Clamp(Limit, 1, MaxLimit);
}

/// <summary>One page of results plus the cursor needed to fetch the next.</summary>
public sealed record AuditPage(IReadOnlyList<AuditEvent> Events, AuditCursor? NextCursor, bool HasMore);
