namespace DefenseClaw.Core.Correlation;

/// <summary>
/// How a relationship was established (<c>method</c> in the gateway's correlation ledger). The order is the order of trust: a reported or
/// trace-exact edge is something an agent or a trace said; a derived edge is something the gateway worked out from reported ids; an inferred
/// edge is a guess the gateway made and says so. <see cref="Unknown"/> is a method this app does not know (a newer gateway): shown verbatim,
/// never as one of the four.
/// </summary>
public enum RelationshipMethod
{
    Reported,
    TraceExact,
    Derived,
    Inferred,
    Unknown,
}

/// <summary>
/// Where a relationship stands (<c>status</c>). Only <see cref="Active"/> is settled. <see cref="Candidate"/> is the gateway's "not resolved
/// yet" (shown as unresolved), <see cref="Conflicted"/> means evidence disagrees, <see cref="Superseded"/> and <see cref="Rejected"/> are kept for
/// the record and drawn as such. Nothing here is ever hidden for being unsettled.
/// </summary>
public enum RelationshipStatus
{
    Active,
    Candidate,
    Superseded,
    Rejected,
    Conflicted,
    Unknown,
}

/// <summary>One logical occurrence the ledger holds (<c>events[]</c>).</summary>
/// <param name="SemanticEventId">The occurrence's id (a UUIDv7).</param>
/// <param name="LogicalEventId">The id of the group of occurrences that are the same event seen through several rails.</param>
/// <param name="Connector">The connector that reported it (<c>codex</c>, <c>claudecode</c>).</param>
/// <param name="ConnectorInstanceId">The connector install that reported it.</param>
/// <param name="Rail">How it arrived: <c>hook</c>, <c>native_otlp</c>, <c>proxy</c>, <c>stream</c>, <c>internal</c>.</param>
/// <param name="EventName">The canonical name (<c>tool_start</c>, <c>turn_end</c>) or the connector's own.</param>
/// <param name="SourceTime">When the source says it happened, or null when it did not say.</param>
/// <param name="ReceivedTime">When the gateway received it; what paging is ordered by.</param>
/// <param name="Completeness"><c>complete</c>, <c>partial</c> or <c>unknown</c>: how much of the identity the connector reports.</param>
public sealed record CorrelationEventRow(
    string SemanticEventId,
    string LogicalEventId,
    string Connector,
    string ConnectorInstanceId,
    string Rail,
    string EventName,
    DateTimeOffset? SourceTime,
    DateTimeOffset? ReceivedTime,
    string Completeness);

/// <summary>
/// One record of an occurrence on one signal (<c>observations[]</c>): the log line, span or metric point. It carries identifiers and the
/// bucket and name of the record, and nothing else - no prompt, tool argument, token count or cost, which the ledger does not store.
/// </summary>
public sealed record CorrelationObservationRow(
    string RecordId,
    string SemanticEventId,
    string Signal,
    string Bucket,
    string EventName,
    DateTimeOffset? ObservedAt,
    string TraceId,
    string SpanId,
    string SessionId,
    string TurnId,
    string AgentId,
    string LifecycleId,
    string ExecutionId,
    string ModelRequestId,
    string ModelResponseId,
    string ToolInvocationId,
    string Status);

/// <summary>One edge of the identity graph (<c>relationships[]</c>), with the method and status that say how far to trust it.</summary>
public sealed record CorrelationRelationshipRow(
    string RelationshipId,
    string FromKind,
    string FromId,
    string ToKind,
    string ToId,
    string Type,
    RelationshipMethod Method,
    string MethodText,
    int Confidence,
    string RuleId,
    long EvidenceCount,
    RelationshipStatus Status,
    string StatusText,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? LastSeenAt);

/// <summary>The record or occurrence an edge stands on (<c>evidence[]</c>). An integrity of <c>failed</c> is shown, not dropped.</summary>
public sealed record CorrelationEvidenceRow(
    string EvidenceId,
    string RelationshipId,
    string RecordId,
    string SemanticEventId,
    string Role,
    string Integrity,
    DateTimeOffset? CreatedAt);

/// <summary>One page of <c>GET /api/v1/correlation/graph</c>.</summary>
/// <param name="Truncated">True when the anchor has more events than the page's limit.</param>
/// <param name="NextAfterTimeText">The <c>after_time</c> that fetches the next page, verbatim (nanoseconds are kept; a parsed time would lose them). Null on the last page.</param>
/// <param name="NextAfterId">The matching <c>after_id</c>.</param>
/// <param name="Skipped">Entries that were not objects, or were over the per-page cap, and so were not read.</param>
public sealed record CorrelationGraphPage(
    IReadOnlyList<CorrelationEventRow> Events,
    IReadOnlyList<CorrelationObservationRow> Observations,
    IReadOnlyList<CorrelationRelationshipRow> Relationships,
    IReadOnlyList<CorrelationEvidenceRow> Evidence,
    DateTimeOffset? AsOf,
    DateTimeOffset? IngestWatermark,
    bool Truncated,
    string? NextAfterTimeText,
    string? NextAfterId,
    int Skipped)
{
    /// <summary>True when there is a next page and the cursor to ask for it.</summary>
    public bool HasNext => Truncated && NextAfterTimeText is { Length: > 0 } && NextAfterId is { Length: > 0 };
}

/// <summary>A delivery the gateway received twice with different content (<c>receipts[]</c> of the conflicts answer).</summary>
public sealed record CorrelationReceiptConflict(
    string ConnectorInstanceId,
    string SemanticEventId,
    string ConflictsWithSemanticEventId,
    long DeliveryCount,
    DateTimeOffset? FirstReceivedAt,
    DateTimeOffset? LastReceivedAt);

/// <summary>One page of <c>GET /api/v1/correlation/conflicts</c>: the conflicted edges and the contradicting deliveries.</summary>
public sealed record CorrelationConflictsPage(
    IReadOnlyList<CorrelationRelationshipRow> Relationships,
    IReadOnlyList<CorrelationReceiptConflict> Receipts,
    bool Truncated);

/// <summary>One agent identity (<c>identities[]</c> of <c>GET /api/v1/agents/identities</c>): one harness install of one user. Its agents in the ledger are per session; the identity is opened through the session it last reported.</summary>
public sealed record AgentIdentityRow(
    string AgentId,
    string UserName,
    string Connector,
    DateTimeOffset? FirstSeen,
    DateTimeOffset? LastSeen,
    string LastSessionId,
    long SessionsSeen,
    bool Retired);

/// <summary>One page of the agent directory.</summary>
/// <param name="Enabled">False when the gateway says it keeps no directory (a managed host).</param>
/// <param name="Persisted">False when the gateway holds the rows in memory only; <paramref name="PersistError"/> says why.</param>
/// <param name="Total">How many identities the gateway has for the filter, whatever this page holds.</param>
/// <param name="NextCursor">The cursor of the next page, or empty on the last.</param>
public sealed record AgentDirectoryPage(
    bool Enabled,
    bool Persisted,
    string PersistError,
    IReadOnlyList<AgentIdentityRow> Rows,
    long Total,
    string NextCursor,
    int Skipped);
