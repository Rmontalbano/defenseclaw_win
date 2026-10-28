using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DefenseClaw.Core.Audit;

/// <summary>One name and value the runtime printed for an alert beyond its basic columns (<c>decision</c>, <c>rule</c>, <c>scanner</c> ...).</summary>
public sealed record RuntimeAlertFact(string Name, string Value);

/// <summary>
/// One alert of <c>defenseclaw alerts --json</c>: the audit row's own columns (<c>id</c> is <c>audit_events.id</c>, so it names the same alert the
/// database queue does) plus the facts the runtime derived (cmd_alerts.py <c>_alerts_json</c>).
/// </summary>
/// <param name="Id">The <c>audit_events.id</c>; unique within a snapshot and never empty.</param>
/// <param name="Timestamp">When it happened (UTC).</param>
/// <param name="RuntimeSeverity">The severity as printed, upper case (the runtime also prints <c>ERROR</c> and <c>WARNING</c>, which are alerts in its list).</param>
/// <param name="Severity">The severity the app shows: <c>ERROR</c> is a High alert (as the Mac shows it), every other spelling its own level.</param>
/// <param name="Action">The audit row's action (<c>scan-finding</c>, <c>connector-hook</c>, <c>sandbox-finding</c> ...).</param>
/// <param name="Target">What it is about; for a finding, the target the runtime resolved from the finding's payload. Null when empty.</param>
/// <param name="Actor">The audit row's actor; empty when none.</param>
/// <param name="Connector">The connector it is attributed to; null for a platform-scoped alert.</param>
/// <param name="Details">The audit row's details text.</param>
/// <param name="Facts">The derived facts present on this alert, in the order the inspector lists them.</param>
public sealed record RuntimeAlertRow(
    string Id,
    DateTimeOffset Timestamp,
    string RuntimeSeverity,
    AuditSeverity Severity,
    string Action,
    string? Target,
    string Actor,
    string? Connector,
    string Details,
    IReadOnlyList<RuntimeAlertFact> Facts)
{
    /// <summary>The row as the alert queue holds one, so it can be merged with the database's.</summary>
    public AlertQueueItem ToQueueItem() => new(Id, Severity, Action, Target, Connector, Timestamp);
}

/// <summary>How <see cref="RuntimeAlertSnapshotParser.Parse"/> ended.</summary>
public enum RuntimeAlertParseStatus
{
    /// <summary>A JSON list of alerts (possibly empty).</summary>
    Ok,

    /// <summary>No JSON list in the output.</summary>
    NotJson,

    /// <summary>A JSON list, but not one this app can trust: too long, a row without an id, action or timestamp, or a repeated id.</summary>
    Invalid,
}

/// <summary>What <see cref="RuntimeAlertSnapshotParser.Parse"/> found.</summary>
/// <param name="Status">Whether <paramref name="Rows"/> is the list, and if not, why.</param>
/// <param name="Rows">The alerts, newest first as the runtime printed them; empty unless <paramref name="Status"/> is <see cref="RuntimeAlertParseStatus.Ok"/>.</param>
/// <param name="Message">One line saying what was wrong; empty when <paramref name="Status"/> is <see cref="RuntimeAlertParseStatus.Ok"/>.</param>
public sealed record RuntimeAlertParseResult(RuntimeAlertParseStatus Status, IReadOnlyList<RuntimeAlertRow> Rows, string Message);

/// <summary>
/// Reads the output of <c>defenseclaw alerts --json --limit 500</c> (runtime 1.0.0 or later; <c>cli/defenseclaw/commands/cmd_alerts.py</c>,
/// <c>_alerts_json</c>): a JSON list of objects with <c>id</c>, <c>timestamp</c>, <c>severity</c>, <c>action</c>, <c>target</c>, <c>actor</c>,
/// <c>connector</c>, <c>details</c> and, when the runtime could work them out, <c>decision</c>, <c>route</c>, <c>rule</c>, <c>scanner</c>,
/// <c>location</c>, <c>sandbox</c>, <c>path</c>, <c>moved_to</c>, <c>description</c>, <c>next_step</c> and the agent columns
/// (<c>user</c>, <c>agent_identity_id</c>, <c>agent_instance_id</c>, <c>session_id</c>, <c>agent_depth</c>).
/// <para>
/// <b>All or nothing</b>, like the Mac (<c>RuntimeV1Alerts.parse</c>): a list with a bad row is refused whole, because a list that silently dropped
/// rows would read as "fewer alerts". The list may be preceded by warning lines the CLI printed; it is found by its opening bracket at the start of a
/// line. Line endings do not matter (JSON whitespace).
/// </para>
/// </summary>
public static class RuntimeAlertSnapshotParser
{
    /// <summary>The most rows the app asks for (<c>--limit 500</c>) and the most it accepts.</summary>
    public const int Limit = 500;

    /// <summary>The columns beyond the basic ones, in the order the inspector lists them (the Mac's order, plus the runtime's description and next step).</summary>
    private static readonly string[] FactKeys =
    [
        "decision", "route", "rule", "scanner", "location", "sandbox", "path", "moved_to", "description", "next_step",
        "user", "agent_identity_id", "agent_instance_id", "session_id", "agent_depth",
    ];

    public static RuntimeAlertParseResult Parse(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return Fail(RuntimeAlertParseStatus.NotJson, "the runtime printed nothing");
        }

        JsonDocument? document = null;
        foreach (var start in ArrayStarts(output))
        {
            document = TryParseValue(output.AsSpan(start));
            if (document is not null)
            {
                break;
            }
        }

        if (document is null)
        {
            return Fail(RuntimeAlertParseStatus.NotJson, "the output had no JSON list");
        }

        using (document)
        {
            var list = document.RootElement;
            if (list.GetArrayLength() > Limit)
            {
                return Fail(RuntimeAlertParseStatus.Invalid, $"the list had more than {Limit} alerts");
            }

            var rows = new List<RuntimeAlertRow>(list.GetArrayLength());
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var element in list.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    return Fail(RuntimeAlertParseStatus.Invalid, "an entry of the list was not an object");
                }

                var id = Text(element, "id");
                if (id.Length == 0 || !seen.Add(id))
                {
                    return Fail(RuntimeAlertParseStatus.Invalid, id.Length == 0 ? "an alert had no id" : "an alert id appeared twice");
                }

                var action = Text(element, "action");
                if (action.Length == 0)
                {
                    return Fail(RuntimeAlertParseStatus.Invalid, "an alert had no action");
                }

                if (!DateTimeOffset.TryParse(Text(element, "timestamp"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var timestamp))
                {
                    return Fail(RuntimeAlertParseStatus.Invalid, "an alert had no readable timestamp");
                }

                var runtimeSeverity = Text(element, "severity").ToUpperInvariant();
                var severity = runtimeSeverity == "ERROR" ? AuditSeverity.High : AuditSeverityExtensions.Parse(runtimeSeverity);
                var target = Text(element, "target");
                var connector = Text(element, "connector");

                var facts = new List<RuntimeAlertFact>();
                foreach (var key in FactKeys)
                {
                    if (FactValue(element, key) is { Length: > 0 } value)
                    {
                        facts.Add(new RuntimeAlertFact(key, value));
                    }
                }

                // A level the app has no word for stays visible: the facts say what the runtime called it.
                if (runtimeSeverity.Length > 0 && !string.Equals(runtimeSeverity, severity.ToStoredValue(), StringComparison.Ordinal))
                {
                    facts.Add(new RuntimeAlertFact("runtime_severity", runtimeSeverity));
                }

                rows.Add(new RuntimeAlertRow(
                    id,
                    timestamp,
                    runtimeSeverity,
                    severity,
                    action,
                    target.Length == 0 ? null : target,
                    Text(element, "actor"),
                    connector.Length == 0 ? null : connector,
                    Text(element, "details"),
                    facts));
            }

            return new RuntimeAlertParseResult(RuntimeAlertParseStatus.Ok, rows, string.Empty);
        }
    }

    private static RuntimeAlertParseResult Fail(RuntimeAlertParseStatus status, string message) =>
        new(status, Array.Empty<RuntimeAlertRow>(), message);

    /// <summary>The offsets at which a line starts with an opening bracket, first the very start of the text.</summary>
    private static IEnumerable<int> ArrayStarts(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '[')
            {
                if (text[i] is '\n' or '\r' or ' ' or '\t' or '﻿')
                {
                    continue;
                }

                // Skip the rest of a non-JSON line.
                while (i < text.Length && text[i] is not ('\n' or '\r'))
                {
                    i++;
                }

                continue;
            }

            yield return i;

            // The rest of this line is the list's own; the next candidate is a later line.
            while (i < text.Length && text[i] is not ('\n' or '\r'))
            {
                i++;
            }
        }
    }

    /// <summary>The first JSON value of <paramref name="text"/> when it is an array; text after it (more output) is ignored. Null when it is not.</summary>
    private static JsonDocument? TryParseValue(ReadOnlySpan<char> text)
    {
        var bytes = Encoding.UTF8.GetBytes(text.ToArray());
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { AllowTrailingCommas = false });
        try
        {
            if (!JsonDocument.TryParseValue(ref reader, out var document))
            {
                return null;
            }

            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                return document;
            }

            document.Dispose();
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? (value.GetString() ?? string.Empty).Trim() : string.Empty;

    /// <summary>A fact's value: a string or a number (the agent depth); anything else is not a fact.</summary>
    private static string? FactValue(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()?.Trim(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }
}
