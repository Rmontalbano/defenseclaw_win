using System.Globalization;

namespace DefenseClaw.Core.Correlation;

/// <summary>What a timeline entry is, for grouping and for the counts above the timeline.</summary>
public enum TimelineKind
{
    /// <summary>A prompt went in: the start of a turn.</summary>
    Prompt,

    /// <summary>A model request or response.</summary>
    Model,

    /// <summary>A tool call or its result.</summary>
    Tool,

    /// <summary>An approval was asked for or settled.</summary>
    Approval,

    /// <summary>A guardrail or hook evaluated something and decided.</summary>
    Guardrail,

    /// <summary>The gateway blocked something and says so: an enforced block.</summary>
    Block,

    /// <summary>Another enforcement action (a request to block, a quarantine, a redaction, a revoked access).</summary>
    Enforcement,

    /// <summary>How a turn, a session or a subagent ended.</summary>
    Outcome,

    /// <summary>A session or subagent started, a conversation was compacted.</summary>
    Lifecycle,

    /// <summary>Anything this app has no word for.</summary>
    Other,
}

/// <summary>One row of the ordered timeline: an occurrence, with the record that observed it when there is one.</summary>
/// <param name="Time">The record's observed time, else the occurrence's received time; null only when the gateway sent neither.</param>
public sealed record TimelineEntry(
    string SemanticEventId,
    string RecordId,
    DateTimeOffset? Time,
    TimelineKind Kind,
    string Title,
    string Bucket,
    string EventName,
    string Signal,
    string Connector,
    string Rail,
    string AgentId,
    string TurnId,
    string SessionId,
    string ToolInvocationId,
    string Completeness)
{
    /// <summary>A counter or histogram point (a metric record): part of the ledger, noise on a timeline, so it is hidden unless asked for.</summary>
    public bool IsMetric => Signal == "metrics";
}

/// <summary>Turns a record's bucket and name into a <see cref="TimelineKind"/> and a title.</summary>
public static class TimelineClassifier
{
    /// <summary>The kind of an entry, from the record's bucket and name first, then the occurrence's own name.</summary>
    public static TimelineKind Classify(string bucket, string recordName, string eventName)
    {
        // A record whose name says nothing more than its bucket (a span, a counter) takes the meaning of the occurrence it belongs to.
        var fromRecord = Of(bucket, recordName);
        if (fromRecord is not (TimelineKind.Other or TimelineKind.Lifecycle))
        {
            return fromRecord;
        }

        var fromEvent = Of(bucket, eventName);
        return fromEvent != TimelineKind.Other ? fromEvent : fromRecord;
    }

    private static TimelineKind Of(string bucket, string name)
    {
        var b = bucket.Trim().ToLowerInvariant().Replace('_', '.');
        var n = name.Trim().ToLowerInvariant();
        if (n is "enforcement.block.applied" or "tool.invocation.blocked")
        {
            return TimelineKind.Block;
        }

        if (n.Contains("approval", StringComparison.Ordinal))
        {
            return TimelineKind.Approval;
        }

        if (n.StartsWith("enforcement.", StringComparison.Ordinal) || b == "enforcement.action")
        {
            return TimelineKind.Enforcement;
        }

        if (b == "guardrail.evaluation" || n.Contains("guardrail", StringComparison.Ordinal) || n == "hook_decision")
        {
            return TimelineKind.Guardrail;
        }

        if (b == "model.io" || n.StartsWith("model", StringComparison.Ordinal) || n.Contains("gen_ai", StringComparison.Ordinal))
        {
            return TimelineKind.Model;
        }

        if (b == "tool.activity" || n.StartsWith("tool", StringComparison.Ordinal))
        {
            return TimelineKind.Tool;
        }

        switch (n)
        {
            case "turn_start":
                return TimelineKind.Prompt;
            case "turn_end" or "session_end" or "subagent_stop" or "subagent_end":
                return TimelineKind.Outcome;
            case "session_start" or "subagent_start" or "compact_start" or "compact_end":
                return TimelineKind.Lifecycle;
            default:
                return b == "agent.lifecycle" ? TimelineKind.Lifecycle : TimelineKind.Other;
        }
    }

    /// <summary>The words a row shows for an entry: this app's wording for the record's name, else for the occurrence's, else the name itself.</summary>
    public static string Title(string recordName, string eventName)
    {
        var known = Known(recordName);
        if (known.Length == 0)
        {
            known = Known(eventName);
        }

        if (known.Length > 0)
        {
            return known;
        }

        return recordName.Length > 0 ? recordName : eventName.Length > 0 ? eventName : "Event";
    }

    private static string Known(string name) => name.Trim().ToLowerInvariant() switch
    {
        "turn_start" => "Prompt submitted",
        "turn_end" => "Turn finished",
        "session_start" => "Session started",
        "session_end" => "Session ended",
        "subagent_start" => "Subagent started",
        "subagent_stop" or "subagent_end" => "Subagent finished",
        "compact_start" => "Context compaction started",
        "compact_end" => "Context compaction finished",
        "model_start" or "model.request" => "Model request",
        "model_end" or "model.response" => "Model response",
        "model.call.failed" => "Model call failed",
        "model.stream.completed" => "Model stream completed",
        "tool_start" or "tool.invocation.requested" or "tool.invocation.started" => "Tool call",
        "tool_end" or "tool.invocation.completed" => "Tool result",
        "tool.invocation.failed" => "Tool call failed",
        "tool.invocation.blocked" => "Tool call blocked",
        "approval.requested" => "Approval requested",
        "approval.resolved" => "Approval resolved",
        "hook_decision" => "Hook decision",
        "guardrail.evaluation.started" => "Guardrail evaluation started",
        "guardrail.evaluation.completed" => "Guardrail evaluation completed",
        "guardrail.evaluation.failed" => "Guardrail evaluation failed",
        "guardrail.judge.completed" => "Guardrail judge completed",
        "enforcement.block.applied" => "Block applied",
        "enforcement.block.requested" => "Block requested",
        "enforcement.block.failed" => "Block failed",
        "enforcement.quarantine.applied" => "Quarantine applied",
        "enforcement.redaction.applied" => "Redaction applied",
        "enforcement.release.applied" => "Release applied",
        "enforcement.access.revoked" => "Access revoked",
        _ => string.Empty,
    };
}

/// <summary>
/// Everything loaded for one agent view: the occurrences, records, relationships and evidence of every page read, merged by id, and which
/// occurrences each agent's pages returned. Pages of one agent add to it; a later copy of the same id replaces the earlier one (the ledger
/// is append-and-amend, so the newest read of an edge is the one that knows its latest status).
/// </summary>
public sealed class AgentLedger
{
    /// <summary>The most agents one view loads; a runaway delegation chain stops here and the view says so.</summary>
    public const int MaxAgents = 64;

    /// <summary>The most occurrences held across all pages.</summary>
    public const int MaxEvents = 10_000;

    private readonly Dictionary<string, CorrelationEventRow> _events = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CorrelationObservationRow> _observations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CorrelationRelationshipRow> _relationships = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CorrelationEvidenceRow> _evidence = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _agentEvents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string? Time, string? Id, bool More)> _cursors = new(StringComparer.Ordinal);

    /// <summary>True once a page was dropped for want of room (<see cref="MaxEvents"/>).</summary>
    public bool Capped { get; private set; }

    /// <summary>Records and entries the gateway sent that could not be read, summed over the pages.</summary>
    public int Skipped { get; private set; }

    public IReadOnlyCollection<CorrelationRelationshipRow> Relationships => _relationships.Values;

    public IReadOnlyCollection<CorrelationEvidenceRow> Evidence => _evidence.Values;

    public int EventCount => _events.Count;

    /// <summary>True when the agent's pages have been read at least once.</summary>
    public bool Has(string agentId) => _agentEvents.ContainsKey(agentId);

    /// <summary>How many agents have pages in this ledger.</summary>
    public int AgentCount => _agentEvents.Count;

    /// <summary>The cursor of the agent's next page, or null when there is none (the last page was read, or none has been).</summary>
    public (string Time, string Id)? NextCursor(string agentId) =>
        _cursors.TryGetValue(agentId, out var cursor) && cursor.More && cursor.Time is not null && cursor.Id is not null ? (cursor.Time, cursor.Id) : null;

    /// <summary>True when the agent has events the ledger has not read yet.</summary>
    public bool HasMore(string agentId) => NextCursor(agentId) is not null;

    /// <summary>The agents that have pages in the ledger (a session's own slice is not one), in the order they were read.</summary>
    public IReadOnlyList<string> AgentIds =>
        _agentEvents.Keys.Where(k => !k.StartsWith("session:", StringComparison.Ordinal)).ToArray();

    /// <summary>The slice key of the pages read around a session (an agent's pages are keyed by its id).</summary>
    public static string SessionKey(string sessionId) => "session:" + sessionId;

    /// <summary>
    /// The agents a slice's records name, in the order they first appear in time, plus - for a session slice - the agents that have a
    /// <c>belongs_to</c> edge to that session. The starting points of an agent walk.
    /// </summary>
    public IReadOnlyList<string> AgentsOf(string sliceKey)
    {
        var agents = new List<string>();
        if (!_agentEvents.TryGetValue(sliceKey, out var ids))
        {
            return agents;
        }

        agents.AddRange(_observations.Values
            .Where(o => o.AgentId.Length > 0 && ids.Contains(o.SemanticEventId))
            .OrderBy(o => o.ObservedAt ?? DateTimeOffset.MaxValue)
            .ThenBy(o => o.RecordId, StringComparer.Ordinal)
            .Select(o => o.AgentId));
        if (sliceKey.StartsWith("session:", StringComparison.Ordinal))
        {
            var session = sliceKey["session:".Length..];
            agents.AddRange(_relationships.Values
                .Where(r => r.FromKind == "agent" && r.ToKind == "session" && r.ToId == session && r.Type == "belongs_to")
                .OrderBy(r => r.CreatedAt ?? DateTimeOffset.MaxValue)
                .Select(r => r.FromId));
        }

        return agents.Distinct(StringComparer.Ordinal).ToArray();
    }

    /// <summary>Adds one page read for the slice <paramref name="agentId"/> (an agent's id, or <see cref="SessionKey"/>). Returns false when the ledger is full and the page was not kept.</summary>
    public bool AddPage(string agentId, CorrelationGraphPage page)
    {
        ArgumentException.ThrowIfNullOrEmpty(agentId);
        ArgumentNullException.ThrowIfNull(page);

        var fresh = page.Events.Count(e => !_events.ContainsKey(e.SemanticEventId));
        if (_events.Count + fresh > MaxEvents || (!_agentEvents.ContainsKey(agentId) && _agentEvents.Count >= MaxAgents))
        {
            Capped = true;
            _cursors[agentId] = (null, null, false);
            return false;
        }

        Skipped += page.Skipped;
        if (!_agentEvents.TryGetValue(agentId, out var ids))
        {
            ids = new HashSet<string>(StringComparer.Ordinal);
            _agentEvents[agentId] = ids;
        }

        foreach (var e in page.Events)
        {
            _events[e.SemanticEventId] = e;
            _ = ids.Add(e.SemanticEventId);
        }

        foreach (var o in page.Observations)
        {
            _observations[o.RecordId] = o;
        }

        foreach (var r in page.Relationships)
        {
            _relationships[r.RelationshipId] = r;
        }

        foreach (var v in page.Evidence)
        {
            _evidence[v.EvidenceId] = v;
        }

        _cursors[agentId] = (page.NextAfterTimeText, page.NextAfterId, page.HasNext);
        return true;
    }

    /// <summary>
    /// The ordered timeline of the occurrences these agents' pages returned: one entry per record, plus one per occurrence that has none, sorted by
    /// the record's time (else the occurrence's received time), ties by occurrence id and record id - the order the gateway's own timeline uses.
    /// An entry with no time at all sorts last.
    /// </summary>
    public IReadOnlyList<TimelineEntry> Timeline(IEnumerable<string> agentIds)
    {
        ArgumentNullException.ThrowIfNull(agentIds);

        var wanted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in agentIds)
        {
            if (_agentEvents.TryGetValue(id, out var ids))
            {
                wanted.UnionWith(ids);
            }
        }

        var observed = new HashSet<string>(StringComparer.Ordinal);
        var entries = new List<TimelineEntry>();
        foreach (var o in _observations.Values)
        {
            if (!wanted.Contains(o.SemanticEventId) || !_events.TryGetValue(o.SemanticEventId, out var e))
            {
                continue;
            }

            _ = observed.Add(e.SemanticEventId);
            var kind = TimelineClassifier.Classify(o.Bucket, o.EventName, e.EventName);
            entries.Add(new TimelineEntry(
                e.SemanticEventId, o.RecordId, o.ObservedAt ?? e.ReceivedTime, kind, TimelineClassifier.Title(o.EventName, e.EventName),
                o.Bucket, o.EventName.Length > 0 ? o.EventName : e.EventName, o.Signal, e.Connector, e.Rail, o.AgentId, o.TurnId, o.SessionId,
                o.ToolInvocationId, e.Completeness));
        }

        foreach (var id in wanted)
        {
            if (!observed.Contains(id) && _events.TryGetValue(id, out var e))
            {
                var kind = TimelineClassifier.Classify(string.Empty, string.Empty, e.EventName);
                entries.Add(new TimelineEntry(
                    e.SemanticEventId, string.Empty, e.ReceivedTime, kind, TimelineClassifier.Title(string.Empty, e.EventName),
                    string.Empty, e.EventName, string.Empty, e.Connector, e.Rail, string.Empty, string.Empty, string.Empty, string.Empty, e.Completeness));
            }
        }

        entries.Sort(CompareEntries);
        return entries;
    }

    private static int CompareEntries(TimelineEntry a, TimelineEntry b)
    {
        if (a.Time is null != b.Time is null)
        {
            return a.Time is null ? 1 : -1;
        }

        var byTime = a.Time is { } x && b.Time is { } y ? x.CompareTo(y) : 0;
        if (byTime != 0)
        {
            return byTime;
        }

        var byEvent = string.CompareOrdinal(a.SemanticEventId, b.SemanticEventId);
        return byEvent != 0 ? byEvent : string.CompareOrdinal(a.RecordId, b.RecordId);
    }

    /// <summary>Evidence rows of one relationship.</summary>
    public IEnumerable<CorrelationEvidenceRow> EvidenceOf(string relationshipId) =>
        _evidence.Values.Where(v => v.RelationshipId == relationshipId);
}

/// <summary>A turn's worth of the timeline, in the order the turns began.</summary>
/// <param name="TurnId">The turn's id, or empty for the entries that report none.</param>
public sealed record TimelineGroup(string TurnId, IReadOnlyList<TimelineEntry> Entries)
{
    /// <summary>What a header shows: the turn, shortened, or the plain statement that none was reported.</summary>
    public string Label => TurnId.Length == 0 ? "No turn reported" : "Turn " + AgentTree.Short(TurnId);
}

/// <summary>Groups an ordered timeline by turn.</summary>
public static class TimelineGrouping
{
    /// <summary>
    /// Groups <paramref name="entries"/> (already ordered) by turn id. Groups come in the order their first entry appears; an entry that reports
    /// no turn stays in the group "No turn reported", so nothing is attributed to a turn the gateway did not say. Entries inside a group keep
    /// their order.
    /// </summary>
    public static IReadOnlyList<TimelineGroup> ByTurn(IEnumerable<TimelineEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var order = new List<string>();
        var groups = new Dictionary<string, List<TimelineEntry>>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (!groups.TryGetValue(entry.TurnId, out var list))
            {
                list = [];
                groups[entry.TurnId] = list;
                order.Add(entry.TurnId);
            }

            list.Add(entry);
        }

        return order.Select(id => new TimelineGroup(id, groups[id])).ToArray();
    }
}

/// <summary>
/// The numbers above the timeline. Counts come only from what the ledger holds; anything it does not hold is <see cref="NotReported"/>, never an
/// estimate. The ledger stores identifiers and the bucket and name of each record (it says so: "no trace body, metric value, labels, prompt, tool
/// arguments, or result content"), so a token count, a cost, or whether a guardrail verdict was an enforced block or an observe-mode would-block
/// is not in it.
/// </summary>
public sealed record AgentKpis(
    int EnforcedBlocks,
    int GuardrailEvaluations,
    int ApprovalsRequested,
    int ApprovalsResolved,
    int ModelCalls,
    int ToolCalls,
    int MetricPointsHidden)
{
    /// <summary>What a figure the ledger does not carry says.</summary>
    public const string NotReported = "Not reported";

    /// <summary>Why observe-mode would-blocks are not counted.</summary>
    public const string WouldBlockNote =
        "Not reported: an observe-mode would-block is part of a guardrail verdict, which the correlation ledger does not store. Only blocks the gateway names as applied are counted above.";

    /// <summary>Why tokens and cost are not shown.</summary>
    public const string UsageNote =
        "Not reported: the correlation ledger holds identifiers and event names, not token counts or cost. Nothing is estimated.";

    /// <summary>Counts distinct occurrences of each kind in <paramref name="entries"/>, metric points excluded.</summary>
    public static AgentKpis From(IEnumerable<TimelineEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var blocks = new HashSet<string>(StringComparer.Ordinal);
        var guardrails = new HashSet<string>(StringComparer.Ordinal);
        var requested = new HashSet<string>(StringComparer.Ordinal);
        var resolved = new HashSet<string>(StringComparer.Ordinal);
        var models = new HashSet<string>(StringComparer.Ordinal);
        var tools = new HashSet<string>(StringComparer.Ordinal);
        var metrics = 0;
        foreach (var e in entries)
        {
            if (e.IsMetric)
            {
                metrics++;
                continue;
            }

            switch (e.Kind)
            {
                case TimelineKind.Block:
                    _ = blocks.Add(e.SemanticEventId);
                    break;
                case TimelineKind.Guardrail:
                    _ = guardrails.Add(e.SemanticEventId);
                    break;
                case TimelineKind.Approval when e.EventName.Contains("requested", StringComparison.OrdinalIgnoreCase):
                    _ = requested.Add(e.SemanticEventId);
                    break;
                case TimelineKind.Approval when e.EventName.Contains("resolve", StringComparison.OrdinalIgnoreCase):
                    _ = resolved.Add(e.SemanticEventId);
                    break;
                case TimelineKind.Model:
                    _ = models.Add(e.SemanticEventId);
                    break;
                case TimelineKind.Tool:
                    _ = tools.Add(e.SemanticEventId);
                    break;
            }
        }

        return new AgentKpis(blocks.Count, guardrails.Count, requested.Count, resolved.Count, models.Count, tools.Count, metrics);
    }

    /// <summary>A count as text.</summary>
    public static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);
}
