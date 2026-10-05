using System.Globalization;
using System.Text.RegularExpressions;
using DefenseClaw.Core.Paths;
using Microsoft.Data.Sqlite;
using YamlDotNet.Serialization;

namespace DefenseClaw.Core.Audit;

/// <summary>One retained LLM-judge response, as the TUI's judge history modal lists it (<c>judge_responses</c>).</summary>
/// <param name="Id">The stable id; empty for a row written without one.</param>
/// <param name="Timestamp">When the judge answered; <see cref="DateTimeOffset.MinValue"/> when the stored text is not a time.</param>
/// <param name="TimestampText">The stored timestamp exactly as written (a Go <c>time.Time</c> string or ISO 8601).</param>
/// <param name="Raw">The judge's raw body, cut to <see cref="JudgeHistoryReader.RawLimit"/> characters and NOT yet masked: display it through <c>DisplayRedaction</c>.</param>
/// <param name="RawTruncated">True when the stored body was longer than <see cref="JudgeHistoryReader.RawLimit"/>.</param>
/// <param name="Source">Which database the row came from.</param>
public sealed record JudgeResponseRow(
    string Id,
    DateTimeOffset Timestamp,
    string TimestampText,
    string Kind,
    string Direction,
    string Action,
    string Severity,
    string LatencyMs,
    string InspectedModel,
    string Model,
    string RequestId,
    string TraceId,
    string RunId,
    string InputHash,
    double? Confidence,
    string ConfidenceText,
    bool FailClosed,
    string PromptTemplateId,
    string ParseError,
    string Raw,
    bool RawTruncated,
    JudgeHistorySource Source);

/// <summary>Which database a <see cref="JudgeResponseRow"/> was read from.</summary>
public enum JudgeHistorySource
{
    /// <summary><c>judge_bodies.db</c> (<c>observability.local.judge_bodies_path</c>): the forensic store the gateway writes now.</summary>
    JudgeBodies,

    /// <summary>The <c>judge_responses</c> table of <c>audit.db</c>, left by older installs.</summary>
    LegacyAudit,
}

/// <summary>What <see cref="JudgeHistoryReader.ReadAsync"/> found, in the terms the window needs to say it.</summary>
public enum JudgeHistoryStatus
{
    /// <summary>Read: <see cref="JudgeHistoryResult.Rows"/> is the answer, possibly empty.</summary>
    Ok,

    /// <summary>Neither database file exists yet. Guidance, not a fault.</summary>
    Missing,

    /// <summary>A database exists but has no <c>judge_responses</c> table yet. Guidance, not a fault.</summary>
    NotInitialized,

    /// <summary>A database exists and could not be read (corrupt, locked past the timeout, no permission). <see cref="JudgeHistoryResult.Message"/> says why.</summary>
    Error,
}

/// <summary>The newest retained judge responses, merged across both databases.</summary>
/// <param name="Rows">Newest first, at most the limit asked for.</param>
/// <param name="Status">Whether this is an answer, guidance or a failure.</param>
/// <param name="Message">The guidance or the error text; empty for <see cref="JudgeHistoryStatus.Ok"/>.</param>
/// <param name="HasMore">True when older rows than <see cref="Rows"/> exist, so a larger limit would show more.</param>
public sealed record JudgeHistoryResult(IReadOnlyList<JudgeResponseRow> Rows, JudgeHistoryStatus Status, string Message, bool HasMore)
{
    /// <summary>The empty answer: nothing found, nothing to say.</summary>
    public static JudgeHistoryResult Empty { get; } = new(Array.Empty<JudgeResponseRow>(), JudgeHistoryStatus.Ok, string.Empty, false);
}

/// <summary>
/// Reads the retained judge responses the TUI's <c>J</c> on Logs shows (<c>tui/services/judge_history.py</c>): the v8 forensic store
/// <c>judge_bodies.db</c> and the legacy <c>judge_responses</c> table in <c>audit.db</c>, merged newest first with the forensic store
/// winning when an id is in both.
/// <para>
/// <b>Read-only, always.</b> Both files are opened <c>Mode=ReadOnly</c> through <see cref="ReadOnlyQuery"/> (they are the gateway's), with
/// a timeout that ends a wedged statement. A missing file or table is an empty source during the upgrade window; a file that is there and
/// cannot be read is reported as <see cref="JudgeHistoryStatus.Error"/>, never thrown and never hidden.
/// </para>
/// <para>
/// <b>Whatever the schema.</b> The columns present are probed (<c>PRAGMA table_info</c>) and only those are selected; the body column is
/// <c>raw</c> or, in the gateway's own schema, <c>raw_response</c>. A body is read through <c>substr</c>, so a multi-megabyte judge answer
/// never leaves SQLite beyond <see cref="RawLimit"/> characters. Rows order by <c>timestamp_unix_nano</c> when the column exists, else by
/// the stored timestamp parsed here (Go's <c>2006-01-02 15:04:05.999999999 -0700 MST</c> and ISO 8601 both), then by id.
/// </para>
/// </summary>
public sealed partial class JudgeHistoryReader
{
    /// <summary>How many rows the window shows first, and each "Load more" adds (the TUI's 20).</summary>
    public const int DefaultLimit = 20;

    /// <summary>The most characters of one raw body kept; the stored body can be a whole model reply.</summary>
    public const int RawLimit = 65_536;

    /// <summary>The longest any other text field is kept.</summary>
    public const int FieldLimit = 2_048;

    /// <summary>Shown when neither file exists, the same words as the TUI.</summary>
    public const string MissingGuidance = "Judge history is unavailable; configure observability.local.judge_bodies_path or audit_db. The databases appear once the gateway has retained its first judge response.";

    /// <summary>Shown when a database has no <c>judge_responses</c> table yet.</summary>
    public const string NotInitializedGuidance = "The judge_responses table is not initialized yet.";

    private const string Table = "judge_responses";
    private const string UnixNanoFunction = "defenseclaw_unix_nano";
    private const long InvalidUnixNano = long.MinValue;

    // The columns the history shows, in the TUI's order. Only these names ever reach the SELECT.
    private static readonly string[] HistoryColumns =
    {
        "id", "timestamp", "kind", "direction", "action", "severity", "latency_ms", "inspected_model", "model", "request_id",
        "trace_id", "run_id", "input_hash", "confidence", "fail_closed_applied", "prompt_template_id", "parse_error",
    };

    [GeneratedRegex(@"^(?<base>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})(?:\.(?<fraction>\d{1,9}))? (?<offset>[+-]\d{4}) [A-Za-z]+(?: m=[+-][\d.]+)?$", RegexOptions.CultureInvariant, 250)]
    private static partial Regex GoTimestamp();

    [GeneratedRegex(@"[.,](?<fraction>\d{1,9})(?=Z|[+-]\d{2}:?\d{2}|$)", RegexOptions.CultureInvariant, 250)]
    private static partial Regex IsoFraction();

    private readonly string? _judgeBodiesPath;
    private readonly string? _legacyPath;
    private readonly TimeSpan? _timeout;

    /// <param name="judgeBodiesPath">Path to <c>judge_bodies.db</c>; null for none.</param>
    /// <param name="legacyAuditPath">Path to <c>audit.db</c>, whose old <c>judge_responses</c> table is merged in; null for none.</param>
    /// <param name="timeout">How long one database may take; <see cref="ReadOnlyQuery.DefaultTimeout"/> when null.</param>
    public JudgeHistoryReader(string? judgeBodiesPath, string? legacyAuditPath, TimeSpan? timeout = null)
    {
        _judgeBodiesPath = string.IsNullOrWhiteSpace(judgeBodiesPath) ? null : judgeBodiesPath;
        _legacyPath = string.IsNullOrWhiteSpace(legacyAuditPath) ? null : legacyAuditPath;
        _timeout = timeout;
    }

    /// <summary>The <c>judge_bodies.db</c> this reads.</summary>
    public string? JudgeBodiesPath => _judgeBodiesPath;

    /// <summary>The <c>audit.db</c> whose legacy table this reads.</summary>
    public string? LegacyAuditPath => _legacyPath;

    /// <summary>
    /// A reader for the install <paramref name="paths"/> describes, honouring <c>observability.local.judge_bodies_path</c> (and the older
    /// root <c>judge_bodies_db</c>) and <c>observability.local.path</c> (<c>audit_db</c>) of <paramref name="configYaml"/> when it names
    /// them, else the default files in the data directory. A config that cannot be parsed is the same as one that names nothing.
    /// </summary>
    public static JudgeHistoryReader ForConfig(DefenseClawPaths paths, string? configYaml, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var (judgeBodies, audit) = ConfiguredPaths(configYaml);
        return new JudgeHistoryReader(
            paths.ResolveConfiguredFile(judgeBodies, paths.JudgeBodiesDatabasePath),
            paths.ResolveConfiguredFile(audit, paths.AuditDatabasePath),
            timeout);
    }

    private static (string? JudgeBodies, string? Audit) ConfiguredPaths(string? configYaml)
    {
        if (string.IsNullOrWhiteSpace(configYaml))
        {
            return (null, null);
        }

        try
        {
            var root = new DeserializerBuilder().Build().Deserialize<Dictionary<string, object?>>(configYaml);
            if (root is null)
            {
                return (null, null);
            }

            string? local = null;
            string? localAudit = null;
            if (root.TryGetValue("observability", out var observability) && observability is IDictionary<object, object?> section
                && section.TryGetValue("local", out var localNode) && localNode is IDictionary<object, object?> localSection)
            {
                local = Scalar(localSection, "judge_bodies_path");
                localAudit = Scalar(localSection, "path");
            }

            return (
                local ?? Scalar(root, "judge_bodies_db"),
                localAudit ?? Scalar(root, "audit_db"));
        }
        catch (Exception ex) when (ex is YamlDotNet.Core.YamlException or InvalidCastException or InvalidOperationException)
        {
            return (null, null);
        }
    }

    private static string? Scalar<TKey>(IDictionary<TKey, object?> map, string key)
        where TKey : notnull =>
        map.TryGetValue((TKey)(object)key, out var value) && value is string text && !string.IsNullOrWhiteSpace(text) ? text : null;

    /// <summary>
    /// The newest <paramref name="limit"/> responses across both databases. Never throws for a database problem (that is
    /// <see cref="JudgeHistoryStatus.Error"/>); a caller's cancellation is an <see cref="OperationCanceledException"/>.
    /// </summary>
    public async Task<JudgeHistoryResult> ReadAsync(int limit = DefaultLimit, CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
        {
            limit = DefaultLimit;
        }

        var sources = new List<(JudgeHistorySource Kind, string Path)>();
        if (_judgeBodiesPath is not null && File.Exists(_judgeBodiesPath))
        {
            sources.Add((JudgeHistorySource.JudgeBodies, _judgeBodiesPath));
        }

        if (_legacyPath is not null && File.Exists(_legacyPath) && !SamePath(_legacyPath, _judgeBodiesPath))
        {
            sources.Add((JudgeHistorySource.LegacyAudit, _legacyPath));
        }

        if (sources.Count == 0)
        {
            return new JudgeHistoryResult(Array.Empty<JudgeResponseRow>(), JudgeHistoryStatus.Missing, MissingGuidance, false);
        }

        var perSource = new List<IReadOnlyList<(JudgeResponseRow Row, long Nano)>>();
        var initialized = false;
        try
        {
            foreach (var (kind, path) in sources)
            {
                // One more than asked for tells whether older rows exist.
                var read = await ReadSourceAsync(path, kind, limit + 1, cancellationToken).ConfigureAwait(false);
                initialized |= read.HasTable;
                perSource.Add(read.Rows);
            }
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or TimeoutException or InvalidOperationException)
        {
            return new JudgeHistoryResult(Array.Empty<JudgeResponseRow>(), JudgeHistoryStatus.Error, ErrorText(ex), false);
        }

        var merged = Merge(perSource);
        var shown = merged.Take(limit).ToList();
        if (shown.Count == 0 && !initialized)
        {
            return new JudgeHistoryResult(shown, JudgeHistoryStatus.NotInitialized, NotInitializedGuidance, false);
        }

        return new JudgeHistoryResult(shown, JudgeHistoryStatus.Ok, string.Empty, merged.Count > limit);
    }

    private static string ErrorText(Exception ex) =>
        ex switch
        {
            TimeoutException => "The judge history database did not answer in time (it may be busy). " + ex.Message,
            SqliteException sqlite => "The judge history database could not be read: " + sqlite.Message,
            _ => "The judge history database could not be read: " + ex.Message,
        };

    /// <summary>Both sources' rows as one list: newest first, an id read once (the forensic store's copy), a row without an id kept.</summary>
    private static List<JudgeResponseRow> Merge(IReadOnlyList<IReadOnlyList<(JudgeResponseRow Row, long Nano)>> perSource)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var combined = new List<(JudgeResponseRow Row, long Nano, int Sequence)>();
        var sequence = 0;
        foreach (var rows in perSource.OrderByDescending(r => r.Count > 0 && r[0].Row.Source == JudgeHistorySource.JudgeBodies))
        {
            foreach (var (row, nano) in rows)
            {
                if (row.Id.Length > 0 && !seen.Add(row.Id))
                {
                    continue;
                }

                combined.Add((row, nano, sequence++));
            }
        }

        return combined
            .OrderByDescending(c => c.Nano)
            .ThenByDescending(c => c.Row.Id, StringComparer.Ordinal)
            .ThenBy(c => c.Sequence)
            .Select(c => c.Row)
            .ToList();
    }

    private async Task<(IReadOnlyList<(JudgeResponseRow Row, long Nano)> Rows, bool HasTable)> ReadSourceAsync(
        string path,
        JudgeHistorySource kind,
        int limit,
        CancellationToken cancellationToken)
    {
        var none = (Rows: (IReadOnlyList<(JudgeResponseRow Row, long Nano)>)Array.Empty<(JudgeResponseRow Row, long Nano)>(), HasTable: false);
        return await ReadOnlyQuery.RunAsync(
            path,
            _timeout,
            cancellationToken,
            none,
            async (connection, token) =>
            {
                var columns = await ReadOnlyQuery.ColumnsOfAsync(connection, Table, token).ConfigureAwait(false);
                if (columns.Count == 0)
                {
                    return none;
                }

                connection.CreateFunction<string?, long>(UnixNanoFunction, UnixNano);

                var selected = HistoryColumns.Where(columns.Contains).ToList();
                var hasNano = columns.Contains("timestamp_unix_nano");
                var rawColumn = columns.Contains("raw") ? "raw" : columns.Contains("raw_response") ? "raw_response" : null;
                if (selected.Count == 0 && rawColumn is null)
                {
                    return (Rows: none.Rows, HasTable: true);
                }

                var select = selected.Select(c => $"\"{c}\"").ToList();
                if (hasNano)
                {
                    select.Add("\"timestamp_unix_nano\" AS \"sort_nano\"");
                }

                if (rawColumn is not null)
                {
                    // The length and the leading slice only: a huge body is never pulled into memory.
                    select.Add($"length(CAST(\"{rawColumn}\" AS TEXT)) AS \"raw_length\"");
                    select.Add($"substr(CAST(\"{rawColumn}\" AS TEXT), 1, {RawLimit.ToString(CultureInfo.InvariantCulture)}) AS \"raw\"");
                }

                string order;
                if (columns.Contains("timestamp_unix_nano") && columns.Contains("timestamp"))
                {
                    order = $"COALESCE(\"timestamp_unix_nano\", {UnixNanoFunction}(\"timestamp\")) DESC";
                }
                else if (columns.Contains("timestamp"))
                {
                    order = $"{UnixNanoFunction}(\"timestamp\") DESC";
                }
                else
                {
                    order = "rowid DESC";
                }

                if (columns.Contains("id"))
                {
                    order += ", \"id\" DESC";
                }

                await using var command = connection.CreateCommand();
                command.CommandText = $"SELECT {string.Join(", ", select)} FROM {Table} ORDER BY {order} LIMIT $limit";
                command.Parameters.AddWithValue("$limit", limit);

                var rows = new List<(JudgeResponseRow Row, long Nano)>();
                await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                var ordinals = Enumerable.Range(0, reader.FieldCount).ToDictionary(reader.GetName, i => i, StringComparer.OrdinalIgnoreCase);
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    string Field(string name) =>
                        ordinals.TryGetValue(name, out var i) ? Clip(ReadOnlyQuery.Text(reader, i), FieldLimit) : string.Empty;

                    var timestampText = ordinals.TryGetValue("timestamp", out var tsOrdinal) ? ReadOnlyQuery.Text(reader, tsOrdinal) ?? string.Empty : string.Empty;
                    var confidenceText = Field("confidence");
                    double? confidence = double.TryParse(confidenceText, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

                    var raw = Field0(reader, ordinals, "raw");
                    var rawLength = ordinals.TryGetValue("raw_length", out var lengthOrdinal) && !reader.IsDBNull(lengthOrdinal) ? reader.GetInt64(lengthOrdinal) : 0;

                    var nano = ordinals.TryGetValue("sort_nano", out var nanoOrdinal) && !reader.IsDBNull(nanoOrdinal) ? reader.GetInt64(nanoOrdinal) : UnixNano(timestampText);

                    rows.Add((new JudgeResponseRow(
                        Field("id").Trim(),
                        TimestampOf(timestampText),
                        timestampText,
                        Field("kind"),
                        Field("direction"),
                        Field("action"),
                        Field("severity"),
                        Field("latency_ms"),
                        Field("inspected_model"),
                        Field("model"),
                        Field("request_id"),
                        Field("trace_id"),
                        Field("run_id"),
                        Field("input_hash"),
                        confidence,
                        confidenceText,
                        IsSet(Field("fail_closed_applied")),
                        Field("prompt_template_id"),
                        Field("parse_error"),
                        raw,
                        rawLength > RawLimit,
                        kind), nano));
                }

                return (Rows: (IReadOnlyList<(JudgeResponseRow Row, long Nano)>)rows, HasTable: true);
            }).ConfigureAwait(false);
    }

    private static string Field0(SqliteDataReader reader, Dictionary<string, int> ordinals, string name) =>
        ordinals.TryGetValue(name, out var i) ? ReadOnlyQuery.Text(reader, i) ?? string.Empty : string.Empty;

    private static string Clip(string? value, int limit) =>
        value is null ? string.Empty : value.Length <= limit ? value : value[..limit];

    private static bool IsSet(string value) =>
        value.Length > 0 && (value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
            (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && number != 0));

    private static bool SamePath(string left, string? right)
    {
        if (right is null)
        {
            return false;
        }

        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static DateTimeOffset TimestampOf(string text)
    {
        var nano = UnixNano(text);
        return nano == InvalidUnixNano ? DateTimeOffset.MinValue : DateTimeOffset.UnixEpoch.AddTicks(nano / 100);
    }

    /// <summary>
    /// A stored timestamp as nanoseconds since the Unix epoch, <see cref="long.MinValue"/> when the text is not a time (so it sorts last).
    /// Reads Go's <c>time.Time</c> string (<c>2026-01-02 15:04:05.123456789 +0000 UTC</c>) and ISO 8601, with or without a zone (none is UTC).
    /// </summary>
    internal static long UnixNano(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return InvalidUnixNano;
        }

        try
        {
            DateTimeOffset parsed;
            string fraction;
            var go = GoTimestamp().Match(text);
            if (go.Success)
            {
                var offset = go.Groups["offset"].Value;
                if (!DateTimeOffset.TryParseExact(
                        go.Groups["base"].Value + " " + offset.Insert(3, ":"),
                        "yyyy-MM-dd HH:mm:ss zzz",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out parsed))
                {
                    return InvalidUnixNano;
                }

                fraction = go.Groups["fraction"].Value;
            }
            else
            {
                if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out parsed))
                {
                    return InvalidUnixNano;
                }

                var match = IsoFraction().Match(text);
                fraction = match.Success ? match.Groups["fraction"].Value : string.Empty;
            }

            var seconds = parsed.ToUnixTimeSeconds();
            var nanos = fraction.Length > 0
                ? long.Parse(fraction.PadRight(9, '0'), CultureInfo.InvariantCulture)
                : parsed.Ticks % TimeSpan.TicksPerSecond * 100;
            return checked((seconds * 1_000_000_000L) + nanos);
        }
        catch (Exception ex) when (ex is RegexMatchTimeoutException or ArgumentOutOfRangeException or OverflowException or FormatException)
        {
            return InvalidUnixNano;
        }
    }
}
