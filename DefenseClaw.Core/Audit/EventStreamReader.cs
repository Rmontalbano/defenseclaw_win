using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using DefenseClaw.Core.Logs;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.Core.Audit;

/// <summary>Which canonical stream <see cref="EventStreamReader"/> reads.</summary>
public enum EventStreamKind
{
    /// <summary>
    /// The decisions: <c>guardrail.evaluation</c>, <c>enforcement.action</c>, <c>asset.scan</c> and <c>security.finding</c>, plus the
    /// errors (HIGH or CRITICAL) of <c>platform.health</c> and <c>diagnostic</c>.
    /// </summary>
    Verdicts,

    /// <summary>
    /// Every bucket; <c>telemetry.ingest</c> only when asked for. That includes each of the TUI's Alerts buckets (<see cref="EventStreamReader.AlertBuckets"/>,
    /// <c>network.egress</c> among them), which is why the Logs panel's Events view is the home of the TUI's "actionable" history (CUST-262): the view
    /// narrows what this stream reads with <see cref="StreamEvent.IsActionable"/>, not with another query.
    /// </summary>
    Events,
}

/// <summary>How an <see cref="EventStreamReader.ReadAsync"/> ended, short of throwing.</summary>
public enum EventStreamStatus
{
    /// <summary>The rows were read (there may be none).</summary>
    Ok,

    /// <summary>There is no <c>audit.db</c> yet, or it holds no <c>audit_events</c> table.</summary>
    NoDatabase,

    /// <summary>The database predates the canonical event schema (no <c>bucket</c> / <c>event_name</c>): these streams do not exist on it.</summary>
    LegacySchema,
}

/// <summary>One canonical event, ready to show: everything credential-shaped already masked and every field bounded.</summary>
/// <param name="Id">The audit row's id.</param>
/// <param name="Timestamp">When it happened (UTC); <see cref="DateTimeOffset.MinValue"/> when the row's timestamp could not be read.</param>
/// <param name="Bucket">The event's bucket (<c>guardrail.evaluation</c>, <c>asset.scan</c>, …).</param>
/// <param name="EventName">The event name (<c>guardrail.judge.completed</c>, <c>finding.observed</c>, …).</param>
/// <param name="Source">Who recorded it.</param>
/// <param name="Severity">The row's severity.</param>
/// <param name="Action">The decision when the payload carries one (<c>block</c>, <c>allow</c>, …), else the row's action.</param>
/// <param name="EventType">The Mac's coarse kind: <c>verdict</c>, <c>judge</c>, <c>scan</c>, <c>scan_finding</c>, <c>activity</c>, <c>error</c>, <c>diagnostic</c> or <c>lifecycle</c>.</param>
/// <param name="Message">One line: event name, action and the reason (or the row's details), masked and at most <see cref="DisplayRedaction.DefaultLimit"/> characters.</param>
/// <param name="Connector">The connector the row is attributed to, or empty.</param>
/// <param name="Actor">Who did it, or empty.</param>
/// <param name="RawJson">The event as indented JSON (its metadata and its payload), masked; the omission notice when the payload was over the size limit.</param>
/// <param name="PayloadOmitted">True when the payload was larger than <see cref="EventStreamReader.PayloadByteLimit"/> and was not read.</param>
public sealed record StreamEvent(
    string Id,
    DateTimeOffset Timestamp,
    string Bucket,
    string EventName,
    string Source,
    AuditSeverity Severity,
    string Action,
    string EventType,
    string Message,
    string Connector,
    string Actor,
    string RawJson,
    bool PayloadOmitted)
{
    /// <summary>
    /// The payload when it was left in the database: <c>payload_json</c>, its size and the limit (see <see cref="OversizedValue"/>); empty
    /// when it was read. The row is still an event - its metadata, message and decision are intact - so a stream whose newest rows all have a
    /// payload this size is a stream of rows each saying why its body is missing, never an empty stream.
    /// </summary>
    public IReadOnlyList<OversizedValue> Oversized { get; init; } = Array.Empty<OversizedValue>();

    /// <summary>The severity as stored (trimmed, not folded): <c>ERROR</c> and <c>FATAL</c> are not on <see cref="AuditSeverity"/>'s ladder, and the actionable rule needs them as written.</summary>
    public string SeverityText { get; init; } = string.Empty;

    /// <summary>What the event was about when its payload says (a finding, enforcement, network, scan target or the agent), masked; empty otherwise.</summary>
    public string Target { get; init; } = string.Empty;

    /// <summary>
    /// True when the TUI would show this event in its default, actionable view (<see cref="ActionableRule"/>): an actionable severity, or one of its
    /// words in the decision, target, event name, bucket, source, connector or reason - the text of the TUI's Alerts row. An event whose payload was
    /// too large to read (<see cref="PayloadOmitted"/>) is actionable: its decision is unknown, and an event is never hidden on text that was not read.
    /// Computed once, when the row is projected.
    /// </summary>
    public bool IsActionable { get; init; } = true;
}

/// <summary>What <see cref="EventStreamReader.ReadAsync"/> found.</summary>
/// <param name="Status">Whether <paramref name="Rows"/> is the stream, or why it is not.</param>
/// <param name="Rows">The newest rows, newest first; empty unless <paramref name="Status"/> is <see cref="EventStreamStatus.Ok"/>.</param>
/// <param name="Elapsed">How long the read took, open to last row.</param>
public sealed record EventStreamResult(EventStreamStatus Status, IReadOnlyList<StreamEvent> Rows, TimeSpan Elapsed);

/// <summary>
/// The Logs panel's "Verdicts" and "Events" streams: the newest canonical events of <c>audit.db</c> (the Mac's
/// <c>AuditStore.canonicalHistory</c> and the projection in <c>EventStreamReader.replaceCanonicalHistory</c>).
/// <para>
/// <b>Bounded three ways.</b> At most <see cref="DefaultLimit"/> rows; at most <see cref="PayloadByteLimit"/> bytes of payload per
/// row (a bigger one is not read at all: the row arrives with <see cref="StreamEvent.PayloadOmitted"/> and a notice instead of
/// its body, decided in SQL so the bytes never leave the database); and every shown string cut after masking (see
/// <see cref="DisplayRedaction"/>).
/// </para>
/// <para>
/// <b>Query shape (measured on the live 7.8 GB database, 700 k rows, where <c>telemetry.ingest</c> is the bulk of the newest 20 k
/// and the planner has no <c>sqlite_stat1</c>).</b> The bucket is filtered in SQL, and the filter decides the plan.
/// <list type="bullet">
///   <item><b>Events</b> is <c>bucket IS NOT NULL [AND bucket &lt;&gt; 'telemetry.ingest'] ORDER BY timestamp DESC, rowid DESC LIMIT n</c>:
///   a walk of <c>idx_audit_timestamp</c> in order that stops after n matches, tens of milliseconds, because the non-telemetry rows are dense.</item>
///   <item><b>Verdicts</b> is one arm per bucket (<c>bucket = ?</c> seeks <c>idx_audit_bucket_timestamp</c>, whose order is the sort order) merged
///   with <c>UNION ALL</c>. The errors are <em>not</em> "HIGH or CRITICAL rows of <c>telemetry.ingest</c>", as on the Mac: with no such rows that
///   predicate reads the whole 156 k-row bucket (36 s measured). Only the two small buckets (<c>platform.health</c>, <c>diagnostic</c>) contribute errors; a
///   telemetry error shows in Events with telemetry included.</item>
/// </list>
/// The statements are read-only (<c>Mode=ReadOnly</c>), run through <see cref="ReaderOffload"/>, and a token or the timeout ends a running one
/// with <c>sqlite3_interrupt</c>. A schema probe precedes every read, so a database upgraded under a running app is read with the right shape next time.
/// </para>
/// <para>
/// <b>An unchanged database is not read again</b> (the Mac's <c>appliedCanonicalIDs</c>, one level down). Given an <see cref="AuditChangeProbe"/>
/// (the app shares one), a read that finds the probe's stamp where the last read of the same stream left it returns that
/// <see cref="EventStreamResult"/> as it was - the same object - without a connection, a statement, a row projected, a payload parsed or
/// masked (<see cref="UnchangedReads"/> / <see cref="RowsDecoded"/>); the Logs panel's five-second poll over a quiet database is then one
/// trivial probe. When the database did change (it does all day, mostly for rows this stream does not list) the statement runs again, but
/// a row it has already projected is handed back as it was, by id - an audit row never changes - so only the rows that arrived are parsed
/// and masked, which is most of a read's cost (measured on a 10 GB database: the Events stream 82 ms -> 23 ms, Verdicts 171 ms -> 125 ms, whose sort of up to six arms' rows is the rest). The panel's own id
/// comparison then finds the same list and leaves it alone.
/// </para>
/// </summary>
public sealed class EventStreamReader
{
    /// <summary>The Mac's window: how many rows a stream shows.</summary>
    public const int DefaultLimit = 1000;

    /// <summary>The most payload bytes read per row; a larger payload is replaced by an omission notice.</summary>
    public const int PayloadByteLimit = 64 * 1024;

    /// <summary>The longest <c>details</c> text read from the database, in characters.</summary>
    public const int DetailsLimit = DisplayRedaction.DefaultLimit;

    /// <summary>The longest <see cref="StreamEvent.RawJson"/> kept, in characters, after masking.</summary>
    public const int RawLimit = 192 * 1024;

    /// <summary>How long <see cref="ReadAsync"/> lets the statement run unless told otherwise.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The bucket whose rows are the bulk of the table and are hidden from <see cref="EventStreamKind.Events"/> by default.</summary>
    public const string TelemetryBucket = "telemetry.ingest";

    private static readonly string[] VerdictBuckets =
    {
        "guardrail.evaluation", "enforcement.action", "asset.scan", "security.finding",
    };

    private static readonly string[] ErrorBuckets = { "platform.health", "diagnostic" };

    /// <summary>
    /// The buckets the 0.8.10 TUI's Alerts panel reads from the canonical history (<c>_V8_ALERT_BUCKETS</c> in <c>tui/panels/alerts.py</c>): the
    /// findings, the guardrail and enforcement decisions, scans, egress decisions and the platform's own health and diagnostics. The Events stream
    /// reads every bucket but telemetry, so each of these is in it; the list is the TUI's, kept here so a test can hold the stream to it (CUST-262).
    /// </summary>
    public static IReadOnlyList<string> AlertBuckets { get; } = new[]
    {
        "security.finding", "guardrail.evaluation", "enforcement.action", "asset.scan", "network.egress", "platform.health", "diagnostic",
    };

    // The Mac's keys first, then the ones the TUI's Alerts panel adds (network.decision, scan.verdict): a row that has one of the first keeps what it showed.
    private static readonly string[] DecisionKeys =
    {
        "defenseclaw.guardrail.decision", "defenseclaw.judge.action", "defenseclaw.guardrail.effective_action",
        "defenseclaw.hook.result", "defenseclaw.enforcement.effective_action", "defenseclaw.approval.result",
        "defenseclaw.network.decision", "defenseclaw.network.policy_outcome", "defenseclaw.scan.verdict",
    };

    private static readonly string[] ReasonKeys =
    {
        "defenseclaw.guardrail.reason", "defenseclaw.guardrail.evidence_summary", "defenseclaw.finding.description",
        "defenseclaw.judge.error_summary", "defenseclaw.error.summary",
        "defenseclaw.network.reason", "defenseclaw.finding.evidence_summary",
    };

    /// <summary>What an event was about, in the TUI's order (<c>_v8_alert_event</c>'s target).</summary>
    private static readonly string[] TargetKeys =
    {
        "defenseclaw.finding.target_ref", "defenseclaw.enforcement.target_ref", "defenseclaw.network.target_ref",
        "defenseclaw.scan.target_ref", "defenseclaw.agent.id",
    };

    private static readonly JsonWriterOptions WriterOptions = new() { Indented = true, SkipValidation = true };

    private readonly string _connectionString;
    private readonly int _limit;
    private readonly AuditChangeProbe? _probe;
    private readonly SnapshotMemo<(EventStreamKind Kind, bool IncludeTelemetry), EventStreamResult> _results;

    /// <summary>The rows of each stream's last read by id: what a later read of the same stream does not project again.</summary>
    private readonly Dictionary<(EventStreamKind Kind, bool IncludeTelemetry), Dictionary<string, StreamEvent>> _known = new();

    private readonly object _knownGate = new();
    private long _reads;
    private long _rowsDecoded;
    private long _unchangedReads;

    /// <param name="databasePath">Path to <c>audit.db</c>.</param>
    /// <param name="limit">How many rows a read returns; <see cref="DefaultLimit"/> in the app, smaller in a test.</param>
    /// <param name="probe">The change probe of this database, shared with the other readers of it; null reads every time.</param>
    /// <param name="timeProvider">The clock the remembered answers' age is measured on; the system's when null.</param>
    public EventStreamReader(string databasePath, int limit = DefaultLimit, AuditChangeProbe? probe = null, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(databasePath);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        DatabasePath = databasePath;
        _limit = limit;
        _connectionString = AuditReader.BuildReadOnlyConnectionString(databasePath);
        _probe = probe;
        _results = new SnapshotMemo<(EventStreamKind, bool), EventStreamResult>(capacity: 3, time: timeProvider);
    }

    public string DatabasePath { get; }

    /// <summary>How many times <see cref="ReadAsync"/> has been called, counted when the call is made; a hidden panel's tests hold it still.</summary>
    public long ReadCount => Interlocked.Read(ref _reads);

    /// <summary>How many rows this reader has projected into <see cref="StreamEvent"/>s over its life; a read of an unchanged database must not add to it.</summary>
    public long RowsDecoded => Interlocked.Read(ref _rowsDecoded);

    /// <summary>How many reads were answered without a statement because the probe said the database had not changed.</summary>
    public long UnchangedReads => Interlocked.Read(ref _unchangedReads);

    /// <summary>Reads the newest rows of <paramref name="kind"/>, newest first. Never blocks the caller's thread.</summary>
    /// <param name="kind">Which stream.</param>
    /// <param name="includeTelemetry">For <see cref="EventStreamKind.Events"/>: include the <c>telemetry.ingest</c> bucket. Ignored for Verdicts.</param>
    /// <param name="timeout">How long the statement may run; <see cref="DefaultTimeout"/> when null, <see cref="Timeout.InfiniteTimeSpan"/> for no limit.</param>
    /// <param name="cancellationToken">Stops a running statement, not only the wait for it.</param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <exception cref="TimeoutException">The statement did not finish in <paramref name="timeout"/> and was interrupted.</exception>
    /// <exception cref="SqliteException">The database is locked past the busy timeout, corrupt, or unreadable.</exception>
    public async Task<EventStreamResult> ReadAsync(
        EventStreamKind kind,
        bool includeTelemetry = false,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        _ = Interlocked.Increment(ref _reads);

        var limit = timeout ?? DefaultTimeout;
        if (limit <= TimeSpan.Zero && limit != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), limit, "The timeout must be positive.");
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (limit != Timeout.InfiniteTimeSpan)
        {
            linked.CancelAfter(limit);
        }

        try
        {
            return await ReaderOffload.Run(() => ReadCoreAsync(kind, includeTelemetry, linked.Token), linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && linked.IsCancellationRequested)
        {
            throw new TimeoutException(
                string.Create(CultureInfo.InvariantCulture, $"The {kind} stream query did not finish within {limit.TotalSeconds:0.##} s and was stopped."));
        }
    }

    private async Task<EventStreamResult> ReadCoreAsync(EventStreamKind kind, bool includeTelemetry, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();

        if (!File.Exists(DatabasePath))
        {
            return new EventStreamResult(EventStreamStatus.NoDatabase, Array.Empty<StreamEvent>(), clock.Elapsed);
        }

        // The stamp before anything is read (see SnapshotMemo). Telemetry only changes the Events stream; Verdicts ignores it, so it shares one answer.
        var key = (kind, kind == EventStreamKind.Events && includeTelemetry);
        var stamp = _probe?.Sample() ?? AuditStamp.Unknown;
        if (_results.TryGet(key, stamp, out var remembered))
        {
            _ = Interlocked.Increment(ref _unchangedReads);
            return remembered with { Elapsed = clock.Elapsed };
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var interrupt = ReaderOffload.InterruptOnCancel(connection, cancellationToken);

        var columns = await ColumnsAsync(connection, cancellationToken).ConfigureAwait(false);
        if (columns.Count == 0)
        {
            return Remember(key, stamp, new EventStreamResult(EventStreamStatus.NoDatabase, Array.Empty<StreamEvent>(), clock.Elapsed));
        }

        if (!Supports(columns))
        {
            return Remember(key, stamp, new EventStreamResult(EventStreamStatus.LegacySchema, Array.Empty<StreamEvent>(), clock.Elapsed));
        }

        await using var command = connection.CreateCommand();
        // nosemgrep: csharp-sqli -- allow-list: BuildSql emits literal columns (or NULL when the table lacks one), const limits and the fixed bucket lists; $limit is bound
        command.CommandText = BuildSql(columns, kind, includeTelemetry);
        command.Parameters.AddWithValue("$limit", _limit);

        // An audit row never changes once written, so a row this stream has already projected is the same row now: it is handed back as it
        // was (the same object) and its payload is neither copied out of the page nor parsed nor masked again. That is nearly all of the
        // cost of a read (the Events stream on a 10 GB database: 82 ms -> 23 ms; Verdicts 171 ms -> 125 ms, the rest being its sort) - so a database that moved for another reason
        // (it does all day) re-projects only the rows that arrived. The panel's own id comparison then finds the same list.
        Dictionary<string, StreamEvent>? known;
        lock (_knownGate)
        {
            _ = _known.TryGetValue(key, out known);
        }

        var rows = new List<StreamEvent>(Math.Min(_limit, 1024));
        var projected = 0;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var id = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                if (id.Length > 0 && known is not null && known.TryGetValue(id, out var prior))
                {
                    rows.Add(prior);
                    continue;
                }

                rows.Add(Project(reader));
                projected++;
            }
        }

        _ = Interlocked.Add(ref _rowsDecoded, projected);

        var byId = new Dictionary<string, StreamEvent>(rows.Count, StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row.Id.Length > 0)
            {
                _ = byId.TryAdd(row.Id, row);
            }
        }

        lock (_knownGate)
        {
            _known[key] = byId;
        }

        return Remember(key, stamp, new EventStreamResult(EventStreamStatus.Ok, rows, clock.Elapsed));
    }

    private EventStreamResult Remember((EventStreamKind Kind, bool IncludeTelemetry) key, AuditStamp stamp, EventStreamResult result)
    {
        _results.Store(key, stamp, result);
        return result;
    }

    /// <summary>
    /// The <c>EXPLAIN QUERY PLAN</c> detail lines for the exact statement <see cref="ReadAsync"/> runs on this database; empty when there is
    /// no database or its schema cannot serve the streams. The Core suite pins the plan: on a few synthetic rows a bad plan is invisible, on the live one it is seconds.
    /// </summary>
    public Task<IReadOnlyList<string>> ExplainAsync(EventStreamKind kind, bool includeTelemetry = false, CancellationToken cancellationToken = default) =>
        ReaderOffload.Run(() => ExplainCoreAsync(kind, includeTelemetry, cancellationToken), cancellationToken);

    private async Task<IReadOnlyList<string>> ExplainCoreAsync(EventStreamKind kind, bool includeTelemetry, CancellationToken cancellationToken)
    {
        if (!File.Exists(DatabasePath))
        {
            return Array.Empty<string>();
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var interrupt = ReaderOffload.InterruptOnCancel(connection, cancellationToken);

        var columns = await ColumnsAsync(connection, cancellationToken).ConfigureAwait(false);
        if (!Supports(columns))
        {
            return Array.Empty<string>();
        }

        await using var command = connection.CreateCommand();
        // nosemgrep: csharp-sqli -- allow-list: the EXPLAIN prefix and BuildSql, as annotated above; $limit is bound
        command.CommandText = "EXPLAIN QUERY PLAN " + BuildSql(columns, kind, includeTelemetry);
        command.Parameters.AddWithValue("$limit", _limit);

        var lines = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            lines.Add(reader.GetString(3));
        }

        return lines;
    }

    private static bool Supports(HashSet<string> columns) =>
        columns.Contains("id") && columns.Contains("timestamp") && columns.Contains("bucket") && columns.Contains("event_name")
        && columns.Contains("severity") && columns.Contains("action");

    private static async Task<HashSet<string>> ColumnsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_info('audit_events')";

        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            _ = columns.Add(reader.GetString(0));
        }

        return columns;
    }

    /// <summary>
    /// The statement (see the type documentation for why it is shaped this way). Optional columns follow the database: a missing one is a
    /// NULL in its place; <c>signal = 'logs'</c> (the Mac's filter, which keeps metric and trace rows out) applies only where the column exists.
    /// </summary>
    private static string BuildSql(HashSet<string> columns, EventStreamKind kind, bool includeTelemetry)
    {
        string Optional(string name, string expression) => columns.Contains(name) ? expression : "NULL";

        var projection =
            $"""
            e.rowid AS rid, e.id AS id, e.timestamp AS ts, e.bucket AS bucket, e.event_name AS event_name,
                    {Optional("source", "e.source")} AS source, e.severity AS severity, e.action AS action,
                    {Optional("actor", "e.actor")} AS actor, {Optional("details", $"substr(e.details, 1, {DetailsLimit.ToString(CultureInfo.InvariantCulture)})")} AS details,
                    {Optional("connector", "e.connector")} AS connector,
                    {Optional("payload_json", PayloadCap.Within("e.payload_json", PayloadByteLimit))} AS payload,
                    {Optional("payload_json", PayloadCap.SizeWhenOver("e.payload_json", PayloadByteLimit))} AS omitted
            """;

        var logsOnly = columns.Contains("signal") ? " AND e.signal = 'logs'" : string.Empty;

        if (kind == EventStreamKind.Events)
        {
            var telemetry = includeTelemetry ? string.Empty : $" AND e.bucket <> '{TelemetryBucket}'";
            return
                $"""
                SELECT rid, id, ts, bucket, event_name, source, severity, action, actor, details, connector, payload, omitted FROM (
                    SELECT {projection}
                        FROM audit_events e
                        WHERE e.bucket IS NOT NULL AND e.bucket <> ''{telemetry}{logsOnly}
                        ORDER BY e.timestamp DESC, e.rowid DESC LIMIT $limit
                )
                """;
        }

        var arms = new List<string>();
        foreach (var bucket in VerdictBuckets)
        {
            arms.Add(Arm(bucket, string.Empty));
        }

        foreach (var bucket in ErrorBuckets)
        {
            arms.Add(Arm(bucket, " AND UPPER(e.severity) IN ('HIGH', 'CRITICAL', 'ERROR', 'FATAL')"));
        }

        return
            $"""
            SELECT rid, id, ts, bucket, event_name, source, severity, action, actor, details, connector, payload, omitted FROM (
                {string.Join("\n    UNION ALL\n    ", arms.Select(arm => "SELECT * FROM (" + arm + ")"))}
            ) ORDER BY ts DESC, rid DESC LIMIT $limit
            """;

        string Arm(string bucket, string residual) =>
            $"""
            SELECT {projection}
                    FROM audit_events e
                    WHERE e.bucket = '{bucket}'{residual}{logsOnly}
                    ORDER BY e.timestamp DESC, e.rowid DESC LIMIT $limit
            """;
    }

    private static StreamEvent Project(SqliteDataReader reader)
    {
        string Text(int ordinal) => reader.IsDBNull(ordinal) ? string.Empty : reader.GetString(ordinal);

        var id = Text(1);
        var rawTimestamp = Text(2);
        var bucket = Text(3);
        var eventName = Text(4);
        var source = Text(5);
        var severityText = Text(6).Trim();
        var severity = AuditSeverityExtensions.Parse(severityText);
        var rowAction = Text(7);
        var actor = Text(8);
        var details = Text(9);
        var connector = Text(10).Trim();
        var payload = reader.IsDBNull(11) ? null : reader.GetString(11);

        // Column 12 is the payload's size when it was over the limit (the statement did not select it), NULL otherwise.
        var omittedBytes = reader.IsDBNull(12) ? 0L : reader.GetInt64(12);
        var omitted = omittedBytes > 0;
        IReadOnlyList<OversizedValue> oversized = omitted
            ? new[] { new OversizedValue("payload_json", omittedBytes, PayloadByteLimit) }
            : Array.Empty<OversizedValue>();

        var timestamp = DateTimeOffset.TryParse(rawTimestamp, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;

        JsonDocument? document = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(payload))
            {
                try
                {
                    document = JsonDocument.Parse(payload);
                }
                catch (JsonException)
                {
                    // Not JSON: it is shown as the string it is.
                }
            }

            var root = document is { RootElement.ValueKind: JsonValueKind.Object } ? document.RootElement : (JsonElement?)null;

            var decision = FirstValue(root, DecisionKeys);
            var action = decision.Length > 0 ? decision : rowAction;
            var reason = FirstValue(root, ReasonKeys);
            var target = FirstValue(root, TargetKeys);
            var summary = reason.Length > 0 ? reason : details;
            var message = DisplayRedaction.Text($"{eventName} {action} — {summary}");

            // The TUI's Alerts panel judges a row on its action, target and a details line that names the bucket, event, source and connector
            // (tui/panels/alerts.py _v8_alert_event); the row's own action is searched as well as the decision the payload carries.
            var actionable = omitted
                || ActionableRule.IsActionable(severityText, string.Join(' ', decision, rowAction, target, eventName, bucket, source, connector, summary));

            return new StreamEvent(
                Id: id,
                Timestamp: timestamp,
                Bucket: bucket,
                EventName: eventName,
                Source: source,
                Severity: severity,
                Action: DisplayRedaction.Text(action),
                EventType: TypeOf(bucket, eventName, severity),
                Message: message,
                Connector: DisplayRedaction.Text(connector),
                Actor: DisplayRedaction.Text(actor),
                RawJson: BuildRaw(eventName, bucket, source, severity, omitted, OversizedValue.Describe(oversized), payload, document),
                PayloadOmitted: omitted)
            {
                Oversized = oversized,
                SeverityText = severityText,
                Target = DisplayRedaction.Text(target),
                IsActionable = actionable,
            };
        }
        finally
        {
            document?.Dispose();
        }
    }

    /// <summary>The Mac's coarse kind of an event, from its bucket (and, for the judge, its name).</summary>
    internal static string TypeOf(string bucket, string eventName, AuditSeverity severity)
    {
        if (eventName.StartsWith("guardrail.judge.", StringComparison.Ordinal))
        {
            return "judge";
        }

        return bucket switch
        {
            "guardrail.evaluation" or "enforcement.action" => "verdict",
            "security.finding" => "scan_finding",
            "asset.scan" => "scan",
            "network.egress" => "egress",
            "compliance.activity" => "activity",
            "platform.health" or "diagnostic" or TelemetryBucket => severity >= AuditSeverity.High ? "error" : "diagnostic",
            _ => "lifecycle",
        };
    }

    private static string FirstValue(JsonElement? root, string[] keys)
    {
        if (root is not { } element)
        {
            return string.Empty;
        }

        foreach (var key in keys)
        {
            if (!element.TryGetProperty(key, out var value))
            {
                continue;
            }

            var text = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? string.Empty,
                JsonValueKind.Number => value.GetRawText(),
                _ => string.Empty,
            };

            if (text.Length > 0)
            {
                return text;
            }
        }

        return string.Empty;
    }

    private static string BuildRaw(string eventName, string bucket, string source, AuditSeverity severity, bool omitted, string omittedReason, string? payload, JsonDocument? document)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("event_name", eventName);
            writer.WriteString("bucket", bucket);
            writer.WriteString("source", source);
            writer.WriteString("severity", severity == AuditSeverity.Unknown ? string.Empty : severity.ToString().ToUpperInvariant());
            writer.WriteBoolean("payload_omitted", omitted);
            if (omitted)
            {
                // Why the body is missing, in the row's own text: the Logs panel shows this JSON as it is.
                writer.WriteString("payload_unavailable", omittedReason);
            }

            if (document is not null)
            {
                writer.WritePropertyName("body");
                document.RootElement.WriteTo(writer);
            }
            else if (!string.IsNullOrEmpty(payload))
            {
                writer.WriteString("body", payload);
            }

            writer.WriteEndObject();
        }

        // Masked first, cut second: see DisplayRedaction.
        return DisplayRedaction.Text(Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length), RawLimit);
    }
}
