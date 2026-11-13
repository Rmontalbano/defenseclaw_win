using System.Text.Json;
using System.Text.RegularExpressions;

namespace DefenseClaw.Core.Audit;

/// <summary>What a Claude Cowork hook row says about its Cowork call; every value may be empty.</summary>
/// <param name="SessionId">The Cowork session (<c>cowork_session_id</c>).</param>
/// <param name="ToolClass">What the Cowork tool does (<c>cowork_tool_class</c>: <c>shell</c>, <c>fetch</c>, <c>browser_navigate</c> ...).</param>
/// <param name="PromptId">The Cowork prompt (<c>prompt_id</c>).</param>
public sealed record CoworkCall(string SessionId, string ToolClass, string PromptId)
{
    /// <summary>The tool class in plain words; empty when the row names none.</summary>
    public string ToolClassLabel => CoworkRow.ToolClassLabel(ToolClass);
}

/// <summary>
/// Claude Cowork rows (DefenseClaw 1.0.8). Cowork is a surface of the <c>claudecode</c> connector, not a connector (internal/gateway/connector/cowork.go
/// <c>CoworkSurface</c>): its hook rows are ordinary <c>connector='claudecode'</c> rows whose hook envelope carries <c>extra.surface = "cowork"</c> with
/// <c>cowork_session_id</c>, <c>prompt_id</c> and <c>cowork_tool_class</c> (internal/gateway/claude_cowork.go). Upstream's own query
/// (internal/coworkaudit/coworkaudit.go) also accepts a top-level <c>surface</c>; the TUI shows such a row's event as <c>cowork:&lt;event&gt;</c>. The same
/// pairs are in the details text's legacy tail, and a hook decision record carries <c>defenseclaw.agent.surface</c>. The connector stays <c>claudecode</c>
/// everywhere, so per-connector counts are untouched.
/// </summary>
public static partial class CoworkRow
{
    /// <summary>The surface value (<c>CoworkSurface</c>).</summary>
    public const string Surface = "cowork";

    /// <summary>The connector Cowork rows carry.</summary>
    public const string Connector = "claudecode";

    /// <summary>The marker the app shows on a Cowork row.</summary>
    public const string Marker = "Cowork";

    /// <summary>The payload attribute of a hook decision record.</summary>
    public const string PayloadSurfaceKey = "defenseclaw.agent.surface";

    /// <summary>
    /// The SQL condition (over <c>audit_events e</c>) for a Cowork row: upstream's filter. <c>instr</c> keeps the JSON parse to rows that can match. Constant text.
    /// </summary>
    public const string SqlPredicate = "(e.connector = '" + Connector + "' AND " + SqlSurfacePredicate + ")";

    /// <summary>
    /// <see cref="SqlPredicate"/> without its connector term, for a query that already filters the connector (and so can steer the planner by it: a second, unsteerable
    /// <c>e.connector = ...</c> inside this condition would pull the planner to <c>idx_audit_connector</c> and a sort of every claudecode row). Constant text.
    /// </summary>
    public const string SqlSurfacePredicate =
        "(instr(e.structured_json, '\"surface\":\"cowork\"') > 0 AND " +
        "(json_extract(e.structured_json, '$.surface') = 'cowork' OR json_extract(e.structured_json, '$.extra.surface') = 'cowork'))";

    /// <summary>
    /// The row's Cowork call, or null when it is not a Cowork row: the connector must be <c>claudecode</c> and the surface <c>cowork</c>, from the structured
    /// envelope (top-level or <c>extra</c>), else the details text's pairs, else a decision record's <paramref name="payloadSurface"/>. Never throws.
    /// </summary>
    /// <param name="connector">The row's connector column.</param>
    /// <param name="structured">The row's parsed <c>structured_json</c> (top-level keys); null when not read.</param>
    /// <param name="details">The row's details text; null when not read.</param>
    /// <param name="payloadSurface">The payload's <see cref="PayloadSurfaceKey"/> value; null when not read.</param>
    public static CoworkCall? Read(
        string? connector,
        IReadOnlyDictionary<string, JsonElement>? structured,
        string? details,
        string? payloadSurface = null)
    {
        if (!string.Equals(connector?.Trim(), Connector, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (structured is not null)
        {
            var extra = structured.TryGetValue("extra", out var e) && e.ValueKind == JsonValueKind.Object ? e : (JsonElement?)null;
            var top = structured.TryGetValue("surface", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
            var nested = extra is { } x && x.TryGetProperty("surface", out var ns) && ns.ValueKind == JsonValueKind.String ? ns.GetString() : null;
            if (IsCowork(top) || IsCowork(nested))
            {
                return new CoworkCall(
                    Identifier(Text(extra, "cowork_session_id") ?? Top(structured, "cowork_session_id")),
                    ToolClass(Text(extra, "cowork_tool_class") ?? Top(structured, "cowork_tool_class")),
                    Identifier(Text(extra, "prompt_id") ?? Top(structured, "prompt_id")));
            }
        }

        if (!string.IsNullOrEmpty(details))
        {
            // Only the closing run of key=value tokens counts, and only with a Cowork key beside the surface: a sentence in free details text
            // (a tool argument, a user message) that happens to contain "surface=cowork" is not a Cowork row.
            var legacy = KeyValueTail(HookJudgeFallback.LegacyPart(details));
            var pairs = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Match match in Pair().Matches(legacy))
            {
                _ = pairs.TryAdd(match.Groups[1].Value, match.Groups[2].Value);
            }

            if (pairs.TryGetValue("surface", out var surface) && IsCowork(surface) &&
                (pairs.ContainsKey("cowork_session_id") || pairs.ContainsKey("cowork_tool_class")))
            {
                return new CoworkCall(
                    Identifier(pairs.GetValueOrDefault("cowork_session_id")),
                    ToolClass(pairs.GetValueOrDefault("cowork_tool_class")),
                    Identifier(pairs.GetValueOrDefault("prompt_id")));
            }
        }

        return IsCowork(payloadSurface) ? new CoworkCall(string.Empty, string.Empty, string.Empty) : null;
    }

    /// <summary>
    /// The Cowork call of a row only the runtime listed (<c>alerts --json</c> at 1.0.9 prints <c>surface</c>: <c>cowork</c> or empty, cmd_alerts.py@1.0.9:215-262,:829-830),
    /// which has no envelope to read. Null unless the connector is <c>claudecode</c> and the surface is <c>cowork</c> (any case). The row's session, tool class and
    /// prompt are not in that output, so they are empty. A row both sources return is read from its envelope first; the caller uses this only when that finds nothing.
    /// </summary>
    public static CoworkCall? FromRuntimeSurface(string? connector, string? surface) =>
        string.Equals(connector?.Trim(), Connector, StringComparison.OrdinalIgnoreCase) && IsCowork(surface)
            ? new CoworkCall(string.Empty, string.Empty, string.Empty)
            : null;

    /// <summary>
    /// The TUI's <c>cowork:&lt;event&gt;</c> form of a stored hook target (<c>claudecode:PreToolUse</c>, alert_semantics.cowork_hook_target); empty when the target
    /// is not in that form. <c>alerts --json</c> keeps the stored spelling.
    /// </summary>
    public static string RetargetStored(string? target)
    {
        const string stored = Connector + ":";
        var text = target?.Trim() ?? string.Empty;
        return text.StartsWith(stored, StringComparison.OrdinalIgnoreCase) && text.Length > stored.Length ? Surface + ":" + text[stored.Length..] : string.Empty;
    }

    /// <summary>The closing run of whitespace-separated <c>key=value</c> tokens of a details text (at most 64 of them); empty when the text does not end in one.</summary>
    internal static string KeyValueTail(string text)
    {
        var start = text.Length;
        var end = text.Length;
        for (var count = 0; count < 64; count++)
        {
            while (end > 0 && char.IsWhiteSpace(text[end - 1]))
            {
                end--;
            }

            var tokenStart = end;
            while (tokenStart > 0 && !char.IsWhiteSpace(text[tokenStart - 1]))
            {
                tokenStart--;
            }

            var equals = text.IndexOf('=', tokenStart, end - tokenStart);
            if (end == tokenStart || equals <= tokenStart || !IsKey(text.AsSpan(tokenStart, equals - tokenStart)))
            {
                break;
            }

            start = tokenStart;
            end = tokenStart;
        }

        return text[start..];
    }

    private static bool IsKey(ReadOnlySpan<char> key)
    {
        foreach (var c in key)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_'))
            {
                return false;
            }
        }

        return key.Length > 0;
    }

    /// <summary>The TUI's form of a Cowork row's event: <c>cowork:PreToolUse</c>.</summary>
    public static string Target(string hookEvent) => Surface + ":" + hookEvent;

    /// <summary>A tool class in plain words (connector.CoworkToolClass values); an unknown class as it is.</summary>
    public static string ToolClassLabel(string toolClass) => toolClass switch
    {
        "" => string.Empty,
        "shell" => "shell (Cowork VM)",
        "fetch" => "web fetch",
        "browser_navigate" => "browser navigation",
        "browser_script" => "browser script",
        "browser_form" => "browser form input",
        "browser" => "browser",
        "persistence" => "scheduled task or other persistence",
        "destructive" => "destructive action",
        "scope_change" => "scope change",
        "skill_admission" => "skill admission",
        "plugin_admission" => "plugin admission",
        "mcp_admission" => "MCP server admission",
        "transcript_access" => "transcript access",
        "workspace_unknown" => "workspace tool",
        "desktop" => "desktop tool",
        _ => toolClass,
    };

    /// <summary>The marker and the facts a detail pane lists for a Cowork row: Surface, Cowork session, Cowork tool and Cowork prompt; empty values left out.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Facts(CoworkCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        var rows = new List<KeyValuePair<string, string>> { new("Surface", Marker + " (Claude Desktop)") };
        if (call.SessionId.Length > 0)
        {
            rows.Add(new("Cowork session", call.SessionId));
        }

        if (call.ToolClass.Length > 0)
        {
            rows.Add(new("Cowork tool", call.ToolClassLabel == call.ToolClass ? call.ToolClass : $"{call.ToolClassLabel} ({call.ToolClass})"));
        }

        if (call.PromptId.Length > 0)
        {
            rows.Add(new("Cowork prompt", call.PromptId));
        }

        return rows;
    }

    private static bool IsCowork(string? value) => string.Equals(value?.Trim(), Surface, StringComparison.OrdinalIgnoreCase);

    private static string? Text(JsonElement? element, string name) =>
        element is { } e && e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? Top(IReadOnlyDictionary<string, JsonElement> structured, string name) =>
        structured.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>An id as the gateway keeps it (<c>coworkIdentifier</c>: <c>[A-Za-z0-9._:-]</c>, at most 128); anything else is dropped.</summary>
    private static string Identifier(string? value)
    {
        var id = value?.Trim() ?? string.Empty;
        return id.Length is > 0 and <= 128 && IdentifierText().IsMatch(id) ? id : string.Empty;
    }

    private static string ToolClass(string? value)
    {
        var word = value?.Trim() ?? string.Empty;
        return word.Length is > 0 and <= 40 && ToolClassText().IsMatch(word) ? word : string.Empty;
    }

    [GeneratedRegex(@"(?:^|\s)(surface|cowork_session_id|cowork_tool_class|prompt_id)=(\S+)(?=\s|$)", RegexOptions.CultureInvariant)]
    private static partial Regex Pair();

    [GeneratedRegex(@"^[A-Za-z0-9._:-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierText();

    [GeneratedRegex(@"^[a-z_]+$", RegexOptions.CultureInvariant)]
    private static partial Regex ToolClassText();
}
