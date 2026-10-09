using System.Globalization;

namespace DefenseClaw.Core.Audit;

/// <summary>One parsed <c>key=value</c> of an event's details: a readable label and a display value.</summary>
public sealed record DetailPair(string Label, string Value);

/// <summary>
/// Parses the gateway's human-readable <c>key=value</c> detail text (<c>action=block scanner=x token=&lt;redacted len=29
/// sha=c5482d9d&gt;</c>) without mistaking metadata inside a <c>&lt;redacted ...&gt;</c> placeholder for event fields. A port of
/// the Mac app's <c>StructuredDetailParser</c> (v1.1.25); pure, no I/O, never throws.
/// </summary>
public static class StructuredDetailParser
{
    private static readonly HashSet<string> SafeMetadataKeys = new(StringComparer.Ordinal)
    {
        "action", "connector", "decision", "evaluation_id", "finding_count",
        "findings", "max_severity", "rule_ids", "scan_id", "scanner",
    };

    /// <summary>The keys the TUI lists first, in its order (<c>_DETAIL_KEY_ORDER</c>).</summary>
    private static readonly string[] KnownKeyOrder =
    {
        "connector", "tool", "action", "raw_action", "severity", "mode", "decision", "reason", "would_block", "elapsed", "duration_ms", "elapsed_ms", "result", "bytes",
        "source", "raw_args", "raw_content", "raw_payload", "request_id",
    };

    /// <summary>The TUI's own labels for the keys whose raw form is opaque (<c>_DETAIL_KEY_LABELS</c>); any other key reads as <see cref="Label"/> reads it.</summary>
    private static readonly Dictionary<string, string> InspectorLabels = new(StringComparer.Ordinal)
    {
        ["connector"] = "Connector",
        ["action"] = "Decision",
        ["raw_action"] = "Decision (raw)",
        ["severity"] = "Severity (decision)",
        ["mode"] = "Enforcement mode",
        ["would_block"] = "Would block",
        ["elapsed"] = "Elapsed",
        ["duration_ms"] = "Elapsed (ms)",
        ["elapsed_ms"] = "Elapsed (ms)",
        ["tool"] = "Tool",
        ["raw_args"] = "Tool args",
        ["raw_payload"] = "Raw payload",
        ["raw_content"] = "Raw content",
        ["request_id"] = "Request ID",
        ["reason"] = "Reason",
        ["result"] = "Result",
        ["bytes"] = "Bytes",
        ["source"] = "Source",
        ["event"] = "Hook event",
        ["hook"] = "Hook event",
        ["decision"] = "Decision",
        ["registry_status"] = "Registry status",
        ["registry_configured"] = "Registry configured",
        ["skill_name_raw"] = "Skill name (raw)",
        ["source_path"] = "Source path",
        ["surface"] = "Surface",
    };

    /// <summary>
    /// Parses a contiguous structured record from the start of <paramref name="details"/>. When the text starts with prose or
    /// a redaction placeholder the result is empty and the caller keeps the raw text; parsing stops at the first thing
    /// that is not a pair.
    /// </summary>
    public static IReadOnlyList<DetailPair> Pairs(string? details)
    {
        var result = new List<DetailPair>();
        foreach (var (key, value) in RawPairs(details))
        {
            result.Add(new DetailPair(Label(key), DisplayValue(value)));
        }

        return result;
    }

    /// <summary>
    /// The same contiguous record as <see cref="Pairs"/>, but as written: each key as the gateway spelled it and each value without a label or a
    /// reading-aid applied (a <c>&lt;redacted ...&gt;</c> placeholder is still the placeholder). What the hook helpers below look keys up in.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> RawPairs(string? details)
    {
        var text = details ?? string.Empty;
        var index = SkipWhitespace(text, 0);
        var result = new List<KeyValuePair<string, string>>();

        while (index < text.Length)
        {
            if (!TryParsePair(text, index, out var key, out var value, out var next))
            {
                break;
            }

            result.Add(new KeyValuePair<string, string>(key, value));
            index = SkipWhitespace(text, next);
        }

        return result;
    }

    // ---- Connector hooks (CUST-261) -------------------------------------------------------------------------------------------------------------
    // A hook call's audit row says only "connector-hook" and the phase (preToolUse); who made the call and what was decided lives in the key=value
    // details (connector=claudecode action=allow severity=NONE mode=observe would_block=false elapsed=320ms ...). The 0.8.10 TUI reads those into the
    // row (tui/panels/audit.py _row_target_label, _row_details_label, _structured_detail_rows); these are the same readings, over the same parser.

    /// <summary>The audit action of a connector's hook call.</summary>
    public const string HookAction = "connector-hook";

    /// <summary>True for the audit row of a connector's hook call.</summary>
    public static bool IsHook(string? action) => string.Equals(action, HookAction, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The details as a dictionary of keys to values as written (<see cref="RawPairs"/>); a key that repeats keeps its last value, and the keys
    /// are compared as written. Empty when the details are not a record.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Values(string? details)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in RawPairs(details))
        {
            values[key] = value;
        }

        return values;
    }

    /// <summary>
    /// The row's Target cell for a hook call: <c>claudecode · preToolUse</c> - the connector (from the details, else <paramref name="connector"/>, the
    /// row's own column) and the hook phase (the row's target). Just the connector when there is no phase, just the phase when there is no connector,
    /// which is what any other row shows: its target.
    /// </summary>
    public static string HookTarget(string? target, string? details, string? connector = null)
    {
        var name = HookConnector(details, connector);
        var phase = target?.Trim() ?? string.Empty;
        if (name.Length > 0 && phase.Length > 0)
        {
            return name + " · " + phase;
        }

        return name.Length > 0 ? name : phase;
    }

    /// <summary>
    /// The inspector's title for a hook call: <c>claudecode preToolUse</c>, <c>claudecode hook</c> or the phase alone; null when the row says neither
    /// (the caller keeps its action).
    /// </summary>
    public static string? HookTitle(string? target, string? details, string? connector = null)
    {
        var name = HookConnector(details, connector);
        var phase = target?.Trim() ?? string.Empty;
        if (name.Length > 0 && phase.Length > 0)
        {
            return name + " " + phase;
        }

        if (name.Length > 0)
        {
            return name + " hook";
        }

        return phase.Length > 0 ? phase : null;
    }

    /// <summary>
    /// The row's Details cell for a hook call: <c>allow · 320ms</c>, or <c>block · HIGH · 41ms</c> - the decision (<c>action</c>, else
    /// <c>decision</c>), the severity when it says something (<c>NONE</c> is left out, the rest upper-cased) and how long the hook took (<c>elapsed</c>,
    /// else <c>duration_ms</c> or <c>elapsed_ms</c>, which the 0.8.10 gateway writes: a bare number reads as milliseconds). Empty when the details hold
    /// none of those, and the caller shows the details themselves.
    /// </summary>
    public static string HookSummary(string? details)
    {
        var values = Values(details);
        var parts = new List<string>(3);

        var decision = First(values, "action", "decision");
        if (decision.Length > 0)
        {
            parts.Add(decision);
        }

        var severity = First(values, "severity");
        if (severity.Length > 0 && !severity.Equals("NONE", StringComparison.OrdinalIgnoreCase))
        {
            parts.Add(severity.ToUpperInvariant());
        }

        var elapsed = First(values, "elapsed");
        if (elapsed.Length == 0)
        {
            elapsed = First(values, "duration_ms", "elapsed_ms");
            if (elapsed.Length > 0 && IsNumber(elapsed))
            {
                elapsed += "ms";
            }
        }

        if (elapsed.Length > 0)
        {
            parts.Add(elapsed);
        }

        return string.Join(" · ", parts);
    }

    /// <summary>
    /// The inspector's rows for a record of key=value details, the way the TUI lays them out: the keys it knows first and in its order (connector, tool,
    /// decision, reason, ...), the rest after them as they came; each with the TUI's label (<c>action</c> reads "Decision", <c>mode</c> "Enforcement
    /// mode"); a <c>&lt;redacted ...&gt;</c> placeholder as its length and digest; <c>would_block</c> and <c>registry_configured</c> as yes / no; and the
    /// two fields that are noise on every passing hook call left out - <c>severity=NONE</c>, and <c>would_block=false</c> while <c>mode=observe</c>.
    /// Empty when the details are not a record (the caller keeps the details text, or the metadata it can pick out of prose).
    /// </summary>
    public static IReadOnlyList<DetailPair> InspectorRows(string? details)
    {
        var pairs = RawPairs(details);
        if (pairs.Count == 0)
        {
            return Array.Empty<DetailPair>();
        }

        // A key that repeats keeps its last value at the position it first had (what a dictionary does in the TUI).
        var order = new List<string>(pairs.Count);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in pairs)
        {
            if (!values.ContainsKey(key))
            {
                order.Add(key);
            }

            values[key] = value;
        }

        var observing = values.TryGetValue("mode", out var mode) && mode.Equals("observe", StringComparison.OrdinalIgnoreCase);
        var rows = new List<DetailPair>(order.Count);

        foreach (var key in KnownKeyOrder.Concat(order.Where(k => !KnownKeyOrder.Contains(k, StringComparer.Ordinal))))
        {
            if (!values.TryGetValue(key, out var value))
            {
                continue;
            }

            if ((key == "severity" && value.Equals("NONE", StringComparison.OrdinalIgnoreCase)) || (key == "would_block" && observing && value == "false"))
            {
                continue;
            }

            rows.Add(new DetailPair(InspectorLabel(key), Reading(key, value)));
        }

        return rows;
    }

    private static string InspectorLabel(string key) => InspectorLabels.TryGetValue(key, out var label) ? label : Label(key);

    /// <summary>A value as the inspector reads it: a redaction placeholder as its size and digest, the two yes/no flags as words, anything else as written.</summary>
    private static string Reading(string key, string value)
    {
        var shown = DisplayValue(value);
        if (key is "would_block" or "registry_configured")
        {
            return shown switch
            {
                "true" => "yes",
                "false" => "no",
                _ => shown,
            };
        }

        return shown;
    }

    private static string HookConnector(string? details, string? column)
    {
        var fromDetails = Values(details).TryGetValue("connector", out var value) ? value.Trim() : string.Empty;
        return fromDetails.Length > 0 ? fromDetails : column?.Trim() ?? string.Empty;
    }

    /// <summary>The first of <paramref name="keys"/> the record has a value for (trimmed); empty when none.</summary>
    private static string First(IReadOnlyDictionary<string, string> values, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (values.TryGetValue(key, out var value) && value.Trim() is { Length: > 0 } trimmed)
            {
                return trimmed;
            }
        }

        return string.Empty;
    }

    /// <summary>A plain number: digits with at most one point (<c>320</c>, <c>0.5</c>).</summary>
    private static bool IsNumber(string text)
    {
        var points = 0;
        foreach (var c in text)
        {
            if (c == '.')
            {
                points++;
            }
            else if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return points <= 1 && text != ".";
    }

    /// <summary>
    /// Extracts only the useful top-level metadata (<c>action</c>, <c>scanner</c>, <c>rule_ids</c>, ...) from an otherwise raw
    /// record. Angle-bracket placeholders are skipped as a unit, so their <c>len</c> and <c>sha</c> attributes can never
    /// become rows. Each key appears once (the first occurrence).
    /// </summary>
    public static IReadOnlyList<DetailPair> SafeMetadataPairs(string? details)
    {
        var text = details ?? string.Empty;
        var index = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<DetailPair>();

        while (index < text.Length)
        {
            index = SkipWhitespace(text, index);
            if (index >= text.Length)
            {
                break;
            }

            if (text[index] == '<')
            {
                index = SkipAngleGroup(text, index);
                continue;
            }

            if (TryParsePair(text, index, out var key, out var value, out var next))
            {
                var normalized = key.ToLowerInvariant();
                if (SafeMetadataKeys.Contains(normalized) && seen.Add(normalized))
                {
                    result.Add(new DetailPair(Label(normalized), DisplayValue(value)));
                }

                index = next;
            }
            else
            {
                index = SkipToken(text, index);
            }
        }

        return result;
    }

    /// <summary>"rule_ids" becomes "Rule IDs", "max_severity" becomes "Max Severity".</summary>
    public static string Label(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        switch (key.ToLowerInvariant())
        {
            case "evaluation_id": return "Evaluation ID";
            case "rule_ids": return "Rule IDs";
            case "scan_id": return "Scan ID";
        }

        var words = key.Split('_', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words.Select(w => char.ToUpper(w[0], CultureInfo.InvariantCulture) + w[1..].ToLowerInvariant()));
    }

    private static bool TryParsePair(string text, int start, out string key, out string value, out int next)
    {
        key = value = string.Empty;
        next = start;
        var index = start;
        while (index < text.Length && text[index] != '=' && !char.IsWhiteSpace(text[index]))
        {
            if (text[index] is '<' or '>')
            {
                return false;
            }

            index++;
        }

        if (index == start || index >= text.Length || text[index] != '=')
        {
            return false;
        }

        key = text[start..index];
        index++;
        if (index >= text.Length)
        {
            return false;
        }

        var valueStart = index;
        if (text[index] is '"' or '\'' or '`')
        {
            var quote = text[index];
            index++;
            var chars = new System.Text.StringBuilder();
            var escaped = false;
            while (index < text.Length)
            {
                var c = text[index++];
                if (escaped)
                {
                    chars.Append(c);
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == quote)
                {
                    value = chars.ToString();
                    next = index;
                    return true;
                }
                else
                {
                    chars.Append(c);
                }
            }

            return false;
        }

        if (text[index] == '<')
        {
            var after = SkipAngleGroup(text, index);
            if (after <= index || text[after - 1] != '>')
            {
                return false;
            }

            value = text[index..after];
            next = after;
            return true;
        }

        while (index < text.Length && !char.IsWhiteSpace(text[index]))
        {
            index++;
        }

        if (index <= valueStart)
        {
            return false;
        }

        value = text[valueStart..index];
        next = index;
        return true;
    }

    /// <summary><c>&lt;redacted len=29 sha=c5482d9d&gt;</c> reads "redacted · 29 bytes · sha:c5482d9d"; any other value is unchanged.</summary>
    private static string DisplayValue(string value)
    {
        if (!value.StartsWith("<redacted", StringComparison.Ordinal) || !value.EndsWith('>'))
        {
            return value;
        }

        var body = value[1..^1]["redacted".Length..];
        string? length = null;
        string? digest = null;
        foreach (var pair in AttributePairs(body))
        {
            if (length is null && pair.Key.Equals("len", StringComparison.OrdinalIgnoreCase))
            {
                length = pair.Value;
            }
            else if (digest is null && pair.Key.Equals("sha", StringComparison.OrdinalIgnoreCase))
            {
                digest = pair.Value;
            }
        }

        var parts = new List<string> { "redacted" };
        if (length is not null)
        {
            parts.Add($"{length} bytes");
        }

        if (digest is not null)
        {
            parts.Add($"sha:{digest}");
        }

        return string.Join(" · ", parts);
    }

    private static List<KeyValuePair<string, string>> AttributePairs(string text)
    {
        var result = new List<KeyValuePair<string, string>>();
        var index = SkipWhitespace(text, 0);
        while (index < text.Length && TryParsePair(text, index, out var key, out var value, out var next))
        {
            result.Add(new KeyValuePair<string, string>(key, value));
            index = SkipWhitespace(text, next);
        }

        return result;
    }

    private static int SkipWhitespace(string text, int start)
    {
        var index = start;
        while (index < text.Length && char.IsWhiteSpace(text[index]))
        {
            index++;
        }

        return index;
    }

    private static int SkipToken(string text, int start)
    {
        var index = start;
        while (index < text.Length && !char.IsWhiteSpace(text[index]))
        {
            index++;
        }

        return index;
    }

    private static int SkipAngleGroup(string text, int start)
    {
        var index = start;
        var depth = 0;
        while (index < text.Length)
        {
            if (text[index] == '<')
            {
                depth++;
            }

            if (text[index] == '>')
            {
                depth--;
                if (depth == 0)
                {
                    return index + 1;
                }
            }

            index++;
        }

        return index;
    }
}
