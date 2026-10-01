using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace DefenseClaw.Core.Audit;

/// <summary>The file formats <see cref="AuditExport"/> writes.</summary>
public enum AuditExportFormat
{
    Json,
    Csv,
}

/// <summary>
/// Serializes audit events for the Audit panel's export: the Mac's JSON schema (id, timestamp, action, target, actor, details,
/// severity, run_id) plus bucket, event_name and connector, or the same columns as CSV. Pure; the caller owns the file.
/// <para>
/// <b>CSV is injection-safe.</b> A spreadsheet runs a cell that starts with <c>=</c>, <c>+</c>, <c>-</c> or <c>@</c> (or a tab or
/// carriage return) as a formula, and audit text is attacker-influenced (a command line, a file name), so such a cell is
/// written with a leading apostrophe, which the spreadsheet shows as plain text. JSON is not a formula carrier and is
/// written verbatim.
/// </para>
/// </summary>
public static class AuditExport
{
    /// <summary>The columns, in order, as they are named in both formats.</summary>
    public static readonly IReadOnlyList<string> Fields = new[]
    {
        "id", "timestamp", "action", "target", "actor", "details", "severity", "run_id", "bucket", "event_name", "connector",
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The file extension (with the dot) for <paramref name="format"/>.</summary>
    public static string Extension(AuditExportFormat format) => format == AuditExportFormat.Csv ? ".csv" : ".json";

    /// <summary>One event's values, in <see cref="Fields"/> order; a missing value is the empty string.</summary>
    public static IReadOnlyList<string> Values(AuditEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return new[]
        {
            e.Id,
            e.Timestamp == DateTimeOffset.MinValue
                ? e.RawTimestamp
                : e.Timestamp.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture),
            e.Action,
            e.Target ?? string.Empty,
            e.Actor ?? string.Empty,
            e.Details ?? string.Empty,
            e.Severity ?? string.Empty,
            e.RunId ?? string.Empty,
            e.Bucket ?? string.Empty,
            e.EventName ?? string.Empty,
            e.Connector ?? string.Empty,
        };
    }

    public static string Serialize(IEnumerable<AuditEvent> events, AuditExportFormat format)
    {
        ArgumentNullException.ThrowIfNull(events);
        return format == AuditExportFormat.Csv ? ToCsv(events) : ToJson(events);
    }

    public static string ToJson(IEnumerable<AuditEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        var rows = events.Select(e =>
        {
            var values = Values(e);
            var row = new Dictionary<string, string>(Fields.Count, StringComparer.Ordinal);
            for (var i = 0; i < Fields.Count; i++)
            {
                row[Fields[i]] = values[i];
            }

            return row;
        }).ToList();

        return JsonSerializer.Serialize(rows, JsonOptions);
    }

    public static string ToCsv(IEnumerable<AuditEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        var text = new StringBuilder().Append(string.Join(',', Fields)).Append("\r\n");
        foreach (var e in events)
        {
            text.Append(string.Join(',', Values(e).Select(CsvCell))).Append("\r\n");
        }

        return text.ToString();
    }

    /// <summary>One CSV cell: formula-neutralised (see the class remarks), then quoted when it holds a comma, a quote or a line break.</summary>
    public static string CsvCell(string? value)
    {
        var cell = value ?? string.Empty;
        if (cell.Length > 0 && cell[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            cell = "'" + cell;
        }

        return cell.AsSpan().IndexOfAny(",\"\r\n") >= 0
            ? "\"" + cell.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : cell;
    }
}
