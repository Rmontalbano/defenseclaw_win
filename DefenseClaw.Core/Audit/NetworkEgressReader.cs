using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.Core.Audit;

/// <summary>
/// One outbound network decision the gateway recorded (the Mac's <c>EgressEvent</c>): where the traffic was headed, whether the
/// gateway let it through, and - the part the "silent bypass" signal is about - whether it <em>looked like an LLM call</em> that
/// went around the guardrail.
/// </summary>
public sealed record EgressEvent
{
    public required string Id { get; init; }

    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.MinValue;

    /// <summary>False when the stored timestamp was missing or not a time: such a row is never counted as recent.</summary>
    public bool TimestampParsed { get; init; }

    /// <summary><c>defenseclaw.network.target_ref</c>: the host (or URL) the traffic was bound for.</summary>
    public string Target { get; init; } = string.Empty;

    /// <summary><c>defenseclaw.network.decision</c> (or <c>policy_outcome</c>): <c>allow</c>, <c>block</c>, ... as written.</summary>
    public string Decision { get; init; } = string.Empty;

    /// <summary><c>defenseclaw.network.branch</c>: <c>passthrough</c>, <c>shape</c>, ... - which path of the proxy decided.</summary>
    public string Branch { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public bool LooksLikeLlm { get; init; }

    public string Connector { get; init; } = string.Empty;

    public string TargetPath { get; init; } = string.Empty;

    public string BodyShape { get; init; } = string.Empty;

    public string Source { get; init; } = string.Empty;

    /// <summary>The network attributes as written (<c>defenseclaw.network.*</c>, prefix kept), for the inspector's attribute list.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Attributes { get; init; } = Array.Empty<KeyValuePair<string, string>>();

    /// <summary>The gateway let it through (<c>allow</c> / <c>allowed</c>, without case or padding).</summary>
    public bool IsAllowed => IsAllow(Decision);

    /// <summary>
    /// Allowed, yet either LLM-shaped traffic that took the passthrough branch or anything on the <c>shape</c> branch: the Mac's
    /// <c>count_recent_silent_bypass</c> (the TUI's own rule) - traffic that reached a model without the guardrail's say.
    /// </summary>
    public bool IsSilentBypass =>
        IsAllowed && (
            (string.Equals(Branch.Trim(), "passthrough", StringComparison.OrdinalIgnoreCase) && LooksLikeLlm) ||
            string.Equals(Branch.Trim(), "shape", StringComparison.OrdinalIgnoreCase));

    /// <summary>The Mac lists an egress event as an alert when it was not allowed, or when it looked like an LLM call.</summary>
    public bool IsAlertWorthy => !IsAllowed || LooksLikeLlm;

    /// <summary>MEDIUM for a block or an LLM-shaped <c>shape</c> branch, INFO otherwise (the Mac's severity for an egress row).</summary>
    public string Severity =>
        string.Equals(Decision.Trim(), "block", StringComparison.OrdinalIgnoreCase) ||
        (string.Equals(Branch.Trim(), "shape", StringComparison.OrdinalIgnoreCase) && LooksLikeLlm)
            ? "MEDIUM"
            : "INFO";

    private static bool IsAllow(string decision) =>
        string.Equals(decision.Trim(), "allow", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(decision.Trim(), "allowed", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Reads the gateway's egress decisions from <c>audit.db</c> without writing to it: the <c>network.egress</c> bucket of
/// <c>audit_events</c> (the Mac reads the same events from <c>gateway.jsonl</c>) and, on a database that has it,
/// <c>network_egress_events</c>, the older table of the same decisions. Attributes are decoded tolerantly (see <see cref="Decode"/>): the
/// gateway's spelling has moved between releases, and a row that does not decode is still a row.
/// <para>
/// Both statements are index searches on a timestamp-ordered index (<c>idx_audit_bucket_timestamp</c>, <c>idx_egress_timestamp</c>) that
/// stop at their limit; the window of the silent-bypass count is a range on the same index.
/// </para>
/// </summary>
public sealed class NetworkEgressReader
{
    /// <summary>How many recent events the Alerts panel lists (the Mac's <c>suffix(100)</c>).</summary>
    public const int DefaultLimit = 100;

    /// <summary>The Mac's "silent bypass" window: 300 seconds.</summary>
    public static readonly TimeSpan SilentBypassWindow = TimeSpan.FromSeconds(300);

    /// <summary>The most rows of the bypass window that are decoded; a window with more than this is already a flood.</summary>
    private const int WindowRowCap = 5_000;

    private const int PayloadLimit = 65_536;

    private readonly string _databasePath;
    private long _reads;

    public NetworkEgressReader(string databasePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(databasePath);
        _databasePath = databasePath;
    }

    /// <summary>How many reads were asked for; the idle-cost tests hold it still.</summary>
    public long ReadCount => Interlocked.Read(ref _reads);

    /// <summary>The newest <paramref name="limit"/> egress events of both sources, newest first. Empty when there is no database or no such table.</summary>
    public Task<IReadOnlyList<EgressEvent>> ReadRecentAsync(
        int limit = DefaultLimit,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        _ = Interlocked.Increment(ref _reads);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        return ReadOnlyQuery.RunAsync<IReadOnlyList<EgressEvent>>(
            _databasePath,
            timeout,
            cancellationToken,
            Array.Empty<EgressEvent>(),
            async (connection, token) =>
            {
                var rows = new List<EgressEvent>();
                rows.AddRange(await ReadAuditAsync(connection, limit, null, token).ConfigureAwait(false));
                rows.AddRange(await ReadLegacyTableAsync(connection, limit, token).ConfigureAwait(false));
                return rows
                    .OrderByDescending(e => e.Timestamp)
                    .Take(limit)
                    .ToList();
            });
    }

    /// <summary>
    /// How many allowed, LLM-shaped egress events (<see cref="EgressEvent.IsSilentBypass"/>) happened in the last
    /// <paramref name="window"/> (<see cref="SilentBypassWindow"/> by default) before <paramref name="now"/>. Counted from the audit bucket
    /// only: the older table has no branch to say "passthrough".
    /// </summary>
    public Task<int> CountSilentBypassAsync(
        DateTimeOffset now,
        TimeSpan? window = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        _ = Interlocked.Increment(ref _reads);
        var cutoff = now - (window ?? SilentBypassWindow);

        return ReadOnlyQuery.RunAsync(
            _databasePath,
            timeout,
            cancellationToken,
            0,
            async (connection, token) =>
            {
                var rows = await ReadAuditAsync(connection, WindowRowCap, cutoff, token).ConfigureAwait(false);
                return rows.Count(e => e.TimestampParsed && e.Timestamp >= cutoff && e.Timestamp <= now.AddMinutes(1) && e.IsSilentBypass);
            });
    }

    private static async Task<List<EgressEvent>> ReadAuditAsync(
        SqliteConnection connection, int limit, DateTimeOffset? since, CancellationToken cancellationToken)
    {
        var columns = await ReadOnlyQuery.ColumnsOfAsync(connection, "audit_events", cancellationToken).ConfigureAwait(false);
        var events = new List<EgressEvent>();
        if (!columns.Contains("bucket") || !columns.Contains("id") || !columns.Contains("timestamp"))
        {
            return events;
        }

        string Pick(string name) => columns.Contains(name) ? name : "NULL";
        string Clipped(string name) => columns.Contains(name) ? $"substr({name}, 1, {PayloadLimit})" : "NULL";

        await using var command = connection.CreateCommand();
        command.CommandText =
            // nosemgrep: csharp-sqli -- allow-list: Pick and Clipped emit only the literal column names written here (or NULL when the table lacks the column) and the const PayloadLimit; $limit and $since are bound
            $"""
            SELECT id, timestamp, {Pick("action")}, {Pick("target")}, {Pick("severity")}, {Pick("connector")}, {Pick("source")},
                   {Clipped("details")}, {Clipped("structured_json")}, {Clipped("payload_json")}
            FROM audit_events
            WHERE bucket = 'network.egress'{(since is null ? string.Empty : " AND timestamp >= $since")}
            ORDER BY timestamp DESC, rowid DESC LIMIT $limit
            """;
        command.Parameters.AddWithValue("$limit", limit);
        if (since is { } from)
        {
            // Second resolution, one second early: the stored text is RFC 3339 with a trimmed fraction, so a prefix compare is
            // safe in that direction, and the exact bound is applied to the parsed instant.
            command.Parameters.AddWithValue("$since", from.UtcDateTime.AddSeconds(-1).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var raw = ReadOnlyQuery.Text(reader, 1);
            var parsed = ReadOnlyQuery.Timestamp(raw);
            events.Add(Decode(
                id: "audit:" + (ReadOnlyQuery.Text(reader, 0) ?? string.Empty),
                timestamp: parsed == DateTimeOffset.MinValue ? null : parsed,
                action: ReadOnlyQuery.Text(reader, 2),
                columnTarget: ReadOnlyQuery.Text(reader, 3),
                connector: ReadOnlyQuery.Text(reader, 5),
                source: ReadOnlyQuery.Text(reader, 6),
                details: ReadOnlyQuery.Text(reader, 7),
                json: new[] { ReadOnlyQuery.Text(reader, 8), ReadOnlyQuery.Text(reader, 9) }));
        }

        return events;
    }

    /// <summary>The older <c>network_egress_events</c> table: hostname, URL, policy outcome and a blocked flag instead of attributes.</summary>
    private static async Task<List<EgressEvent>> ReadLegacyTableAsync(SqliteConnection connection, int limit, CancellationToken cancellationToken)
    {
        var columns = await ReadOnlyQuery.ColumnsOfAsync(connection, "network_egress_events", cancellationToken).ConfigureAwait(false);
        var events = new List<EgressEvent>();
        if (!columns.Contains("id") || !columns.Contains("timestamp") || !columns.Contains("hostname"))
        {
            return events;
        }

        string Pick(string name) => columns.Contains(name) ? name : "NULL";

        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT id, timestamp, hostname, {Pick("url")}, {Pick("policy_outcome")}, {Pick("blocked")}, {Pick("details")}, {Pick("connector")}
            FROM network_egress_events ORDER BY timestamp DESC LIMIT $limit
            """;
        command.Parameters.AddWithValue("$limit", limit);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var parsed = ReadOnlyQuery.Timestamp(ReadOnlyQuery.Text(reader, 1));
            var blocked = !reader.IsDBNull(5) && Convert.ToInt64(reader.GetValue(5), CultureInfo.InvariantCulture) != 0;
            var outcome = ReadOnlyQuery.Text(reader, 4)?.Trim() ?? string.Empty;
            events.Add(new EgressEvent
            {
                Id = "egress:" + (ReadOnlyQuery.Text(reader, 0) ?? string.Empty),
                Timestamp = parsed,
                TimestampParsed = parsed != DateTimeOffset.MinValue,
                Target = ReadOnlyQuery.Text(reader, 2)?.Trim() ?? string.Empty,
                Decision = blocked ? "block" : outcome.ToLowerInvariant(),
                Reason = ReadOnlyQuery.Text(reader, 6)?.Trim() ?? string.Empty,
                TargetPath = ReadOnlyQuery.Text(reader, 3)?.Trim() ?? string.Empty,
                Connector = ReadOnlyQuery.Text(reader, 7)?.Trim() ?? string.Empty,
                Source = "network_egress_events",
            });
        }

        return events;
    }

    /// <summary>
    /// Decodes one egress row from whatever the row carries. The attributes are looked up by the name the gateway writes
    /// (<c>defenseclaw.network.decision</c>), without the <c>defenseclaw.</c> prefix, or bare, in any of the JSON documents, at the top level
    /// or inside a nested object (a payload that wraps its fields in <c>attributes</c>), without case; a value may be text, a number or a
    /// boolean. A missing decision falls back to the row's action (a "block" or "deny" in it); a missing target to the row's target column.
    /// Nothing here throws for what the database holds.
    /// </summary>
    internal static EgressEvent Decode(
        string id,
        DateTimeOffset? timestamp,
        string? action,
        string? columnTarget,
        string? connector,
        string? source,
        string? details,
        IEnumerable<string?> json)
    {
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var document in json)
        {
            Flatten(document, attributes);
        }

        string Get(params string[] names)
        {
            foreach (var name in names)
            {
                foreach (var key in new[] { "defenseclaw.network." + name, "network." + name, name })
                {
                    if (attributes.TryGetValue(key, out var exact) && exact.Length > 0)
                    {
                        return exact;
                    }
                }

                var suffix = "network." + name;
                foreach (var (key, value) in attributes)
                {
                    if (value.Length > 0 && key.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase))
                    {
                        return value;
                    }
                }
            }

            return string.Empty;
        }

        var decision = Get("decision", "policy_outcome", "outcome").Trim();
        if (decision.Length == 0 && action is { Length: > 0 })
        {
            decision = action.Contains("block", StringComparison.OrdinalIgnoreCase) || action.Contains("deny", StringComparison.OrdinalIgnoreCase)
                ? "block"
                : string.Empty;
        }

        var target = Get("target_ref", "hostname", "host", "url");
        var llm = Get("looks_like_llm");

        return new EgressEvent
        {
            Id = id,
            Timestamp = timestamp ?? DateTimeOffset.MinValue,
            TimestampParsed = timestamp is not null,
            Target = target.Length > 0 ? target : columnTarget?.Trim() ?? string.Empty,
            Decision = decision,
            Branch = Get("branch").Trim(),
            Reason = Get("reason") is { Length: > 0 } reason ? reason : details?.Trim() ?? string.Empty,
            LooksLikeLlm = llm.Equals("true", StringComparison.OrdinalIgnoreCase) || llm == "1",
            Connector = connector?.Trim() ?? string.Empty,
            TargetPath = Get("target_path"),
            BodyShape = Get("body_shape"),
            Source = source?.Trim() ?? string.Empty,
            Attributes = attributes
                .Where(p => p.Key.Contains("network.", StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };
    }

    /// <summary>Adds every scalar of a JSON object to <paramref name="into"/> under its dotted path; anything that is not an object is skipped.</summary>
    private static void Flatten(string? json, Dictionary<string, string> into)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                Walk(document.RootElement, string.Empty, into, depth: 0);
            }
        }
        catch (JsonException)
        {
            // One bad row must not break the list; it decodes to what its columns say.
        }
    }

    private static void Walk(JsonElement element, string prefix, Dictionary<string, string> into, int depth)
    {
        foreach (var property in element.EnumerateObject())
        {
            var path = prefix.Length == 0 ? property.Name : prefix + "." + property.Name;
            switch (property.Value.ValueKind)
            {
                case JsonValueKind.Object when depth < 3:
                    Walk(property.Value, path, into, depth + 1);
                    break;
                case JsonValueKind.String:
                    _ = into.TryAdd(path, property.Value.GetString() ?? string.Empty);
                    break;
                case JsonValueKind.True:
                    _ = into.TryAdd(path, "true");
                    break;
                case JsonValueKind.False:
                    _ = into.TryAdd(path, "false");
                    break;
                case JsonValueKind.Number:
                    _ = into.TryAdd(path, property.Value.ToString());
                    break;
            }
        }
    }
}
