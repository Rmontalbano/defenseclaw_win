using System.Text.Json;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// What a listed item is, decided from the fields DefenseClaw 0.8.10's <c>list --json</c> prints
/// (<c>actions{file,runtime,install}</c>, <c>verdict</c>, <c>status</c>, <c>disabled</c>, <c>enabled</c>,
/// <c>scan{…}</c> / <c>severity</c>). Mirrors the CLI's own precedence
/// (<c>compute_verdict</c>: quarantine, then block, then disable, then allow).
/// </summary>
public readonly record struct GovernItemState(
    bool Blocked,
    bool Allowed,
    bool Quarantined,
    bool Disabled,
    string? Status,
    string? Verdict,
    string? Severity,
    int? Findings,
    bool? ScanClean,
    string? ActionsText)
{
    /// <summary>The state badge: what is being done to the item, else what its own status says.</summary>
    public string Label =>
        Quarantined ? "Quarantined"
        : Blocked ? "Blocked"
        : Disabled ? "Disabled"
        : Allowed ? "Allowed"
        : !string.IsNullOrWhiteSpace(Status) ? Capitalize(Status!)
        : "Unknown";

    public string Tone =>
        Quarantined || Blocked ? "Bad"
        : Disabled ? "Warn"
        : Allowed || string.Equals(Status, "active", StringComparison.OrdinalIgnoreCase) ? "Ok"
        : "Neutral";

    public string ScanLabel
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Severity) && !string.Equals(Severity, "CLEAN", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(Severity, "NONE", StringComparison.OrdinalIgnoreCase))
            {
                return Findings is > 0
                    ? $"{Severity!.ToUpperInvariant()} · {Findings} finding{(Findings == 1 ? string.Empty : "s")}"
                    : Severity!.ToUpperInvariant();
            }

            if (ScanClean == true || string.Equals(Severity, "CLEAN", StringComparison.OrdinalIgnoreCase))
            {
                return "Scan clean";
            }

            return "Not scanned";
        }
    }

    public string ScanTone => (Severity ?? string.Empty).ToUpperInvariant() switch
    {
        "CRITICAL" => "Critical",
        "HIGH" => "High",
        "MEDIUM" => "Medium",
        "LOW" => "Low",
        "CLEAN" => "Ok",
        _ => ScanClean == true ? "Ok" : "Neutral",
    };

    public bool NeedsAttention =>
        (Severity ?? string.Empty).ToUpperInvariant() is "CRITICAL" or "HIGH" or "MEDIUM"
        || string.Equals(Verdict, "rejected", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Verdict, "warning", StringComparison.OrdinalIgnoreCase);

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}

/// <summary>Small, allocation-light JSON readers shared by the four Govern view-models.</summary>
public static class GovernJson
{
    public static string? Str(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value))
        {
            var text = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null,
            };

            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        return null;
    }

    public static bool? Bool(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value))
        {
            return value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            };
        }

        return null;
    }

    public static int? Int(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var number))
        {
            return number;
        }

        return null;
    }

    public static JsonElement? Obj(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Object
            ? value
            : null;

    /// <summary>A JSON array of strings joined with spaces (MCP <c>args</c>); non-string members are shown as JSON.</summary>
    public static string? JoinedArray(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Array)
        {
            var parts = value.EnumerateArray()
                .Select(v => v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : v.GetRawText())
                .ToArray();
            return parts.Length == 0 ? null : string.Join(' ', parts);
        }

        return Str(element, name);
    }

    public static string Pretty(JsonElement element)
    {
        try
        {
            return JsonSerializer.Serialize(element, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException)
        {
            return element.GetRawText();
        }
    }

    /// <summary>Pretty-prints a JSON text; returns the text unchanged when it is not JSON.</summary>
    public static string PrettyText(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return Pretty(document.RootElement);
        }
        catch (JsonException)
        {
            return text;
        }
    }

    /// <summary>
    /// Reads <c>actions{file,runtime,install}</c>, <c>verdict</c>, <c>status</c>, <c>disabled</c>/<c>enabled</c> and the
    /// scan severity (<c>scan.max_severity</c> for skills and plugins, a bare <c>severity</c> string for MCP servers).
    /// </summary>
    public static GovernItemState Interpret(JsonElement item)
    {
        var actions = Obj(item, "actions");
        var file = actions is { } a1 ? Str(a1, "file") : null;
        var runtime = actions is { } a2 ? Str(a2, "runtime") : null;
        var install = actions is { } a3 ? Str(a3, "install") : null;

        var verdict = Str(item, "verdict");
        var status = Str(item, "status");

        var quarantined = Is(file, "quarantine") || Is(verdict, "quarantined") || Is(status, "quarantined");
        var blocked = Is(install, "block") || Is(verdict, "blocked") || Is(status, "blocked");
        var allowed = Is(install, "allow") || Is(verdict, "allowed") || Is(status, "allowed");
        var disabled = Is(runtime, "disable")
                       || Bool(item, "disabled") == true
                       || Bool(item, "enabled") == false
                       || Is(status, "disabled");

        var scan = Obj(item, "scan");
        var severity = scan is { } s1 ? Str(s1, "max_severity") : Str(item, "severity");
        var findings = scan is { } s2 ? Int(s2, "total_findings") : null;
        var clean = scan is { } s3 ? Bool(s3, "clean") : null;

        var actionParts = new List<string>();
        if (file is not null)
        {
            actionParts.Add($"file: {file}");
        }

        if (runtime is not null)
        {
            actionParts.Add($"runtime: {runtime}");
        }

        if (install is not null)
        {
            actionParts.Add($"install: {install}");
        }

        return new GovernItemState(
            blocked,
            allowed && !blocked,
            quarantined,
            disabled,
            status,
            verdict,
            severity,
            findings,
            clean,
            actionParts.Count == 0 ? null : string.Join(" · ", actionParts));
    }

    /// <summary>
    /// The items of a <c>list --json</c> payload, whichever of the shapes 0.8.10 prints: a bare array of items,
    /// an array of <c>{"connector": …, "&lt;itemsKey&gt;": […]}</c> groups (several connectors), or one such
    /// group object (<c>--connector</c> for skills, MCP servers and tools). The group's connector is returned
    /// alongside each item so a row can name where it lives even when the item omits it.
    /// </summary>
    /// <exception cref="FormatException">The payload is neither an array nor a group object.</exception>
    public static IReadOnlyList<(JsonElement Item, string? GroupConnector)> Flatten(JsonElement root, string itemsKey)
    {
        var result = new List<(JsonElement, string?)>();

        switch (root.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var entry in root.EnumerateArray())
                {
                    if (TryGroup(entry, itemsKey, out var groupItems, out var connector))
                    {
                        result.AddRange(groupItems.Select(i => (i, connector)));
                    }
                    else if (entry.ValueKind == JsonValueKind.Object)
                    {
                        result.Add((entry, null));
                    }
                }

                break;

            case JsonValueKind.Object when TryGroup(root, itemsKey, out var items, out var groupConnector):
                result.AddRange(items.Select(i => (i, groupConnector)));
                break;

            default:
                throw new FormatException($"Expected a JSON array or a {{\"{itemsKey}\": [...]}} object, got {root.ValueKind}.");
        }

        return result;
    }

    private static bool TryGroup(JsonElement candidate, string itemsKey, out List<JsonElement> items, out string? connector)
    {
        items = new List<JsonElement>();
        connector = null;

        if (candidate.ValueKind == JsonValueKind.Object
            && candidate.TryGetProperty(itemsKey, out var array)
            && array.ValueKind == JsonValueKind.Array)
        {
            connector = Str(candidate, "connector");
            items.AddRange(array.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object));
            return true;
        }

        return false;
    }

    private static bool Is(string? value, string expected) =>
        string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
}
