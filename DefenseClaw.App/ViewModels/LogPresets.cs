using DefenseClaw.Core.Audit;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// The Logs panel's filter vocabulary, ported from the Mac (<c>LogPreset</c>, the TUI's <c>FILTER_PRESETS</c>, and the severity / action /
/// event pickers of <c>LogsView</c>): the names the menus offer and the rule each one applies to a row.
/// </summary>
public static class LogPresets
{
    public const string All = "all";
    public const string NoNoise = "no-noise";
    public const string Important = "important";
    public const string Errors = "errors";
    public const string WarningsPlus = "warnings+";
    public const string Scan = "scan";
    public const string Drift = "drift";
    public const string Guardrail = "guardrail";
    public const string Hooks = "hooks";

    /// <summary>The preset menu, in the Mac's order.</summary>
    public static IReadOnlyList<string> Names { get; } = new[]
    {
        All, NoNoise, Important, Errors, WarningsPlus, Scan, Drift, Guardrail, Hooks,
    };

    /// <summary>The Severity ≥ picker: "Any severity", then each floor from critical down to low.</summary>
    public static IReadOnlyList<string> SeverityOptions { get; } = new[]
    {
        AnySeverity, "≥ Critical", "≥ High", "≥ Medium", "≥ Low",
    };

    public const string AnySeverity = "Any severity";

    /// <summary>The Action picker: a row's action must contain the choice (the Mac's list).</summary>
    public static IReadOnlyList<string> ActionOptions { get; } = new[]
    {
        "all", "block", "alert", "confirm", "allow", "reject", "scan", "hook",
    };

    /// <summary>The Event picker: a row's kind must contain the choice, except "scan", which is exactly <c>scan</c> and so leaves out <c>scan_finding</c>.</summary>
    public static IReadOnlyList<string> EventOptions { get; } = new[]
    {
        "all", "verdict", "judge", "lifecycle", "error", "diagnostic", "scan", "scan_finding", "activity", "audit", "hook", "egress", "skill", "mcp", "plugin",
    };

    private static readonly string[] ImportantKeywords =
    {
        "error", "fatal", "panic", "warn", "block", "allow", "reject", "quarantine", "scan", "drift", "verdict", "guardrail",
        "connected", "disconnected", "started", "stopped",
    };

    /// <summary>The preset named <paramref name="name"/> in the menu's spelling (without case), or null when there is none.</summary>
    public static string? Resolve(string? name) =>
        Names.FirstOrDefault(n => string.Equals(n, name?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The severity a "≥ X" choice stands for, or null for "Any severity" (and anything unknown).</summary>
    public static AuditSeverity? FloorOf(string? option) => option switch
    {
        "≥ Critical" => AuditSeverity.Critical,
        "≥ High" => AuditSeverity.High,
        "≥ Medium" => AuditSeverity.Medium,
        "≥ Low" => AuditSeverity.Low,
        _ => null,
    };

    /// <summary>Whether <paramref name="entry"/> passes the preset <paramref name="name"/>; an unknown name passes everything.</summary>
    public static bool Matches(string name, LogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var text = entry.MatchText;
        return name switch
        {
            NoNoise => !LogEntry.IsNoise(text),
            Important => ImportantKeywords.Any(k => text.Contains(k, StringComparison.Ordinal)),
            Errors => entry.Severity >= AuditSeverity.High,
            WarningsPlus => entry.Severity >= AuditSeverity.Medium,
            Scan => entry.EventType == "scan" || text.Contains("scan", StringComparison.Ordinal),
            Drift => text.Contains("drift", StringComparison.Ordinal),
            Guardrail => text.Contains("guardrail", StringComparison.Ordinal) || text.Contains("verdict", StringComparison.Ordinal) || text.Contains("judge", StringComparison.Ordinal),
            Hooks => entry.EventType == "hook" || text.Contains("hook", StringComparison.Ordinal),
            _ => true,
        };
    }

    /// <summary>The Action picker's rule: "all", or the row's action contains the choice (without case).</summary>
    public static bool MatchesAction(string option, LogEntry entry) =>
        option == "all" || entry.Action.Contains(option, StringComparison.OrdinalIgnoreCase);

    /// <summary>The Event picker's rule: "all", exactly <c>scan</c> for "scan", otherwise the row's kind contains the choice (without case).</summary>
    public static bool MatchesEvent(string option, LogEntry entry) => option switch
    {
        "all" => true,
        "scan" => string.Equals(entry.EventType, "scan", StringComparison.OrdinalIgnoreCase),
        _ => entry.EventType.Contains(option, StringComparison.OrdinalIgnoreCase),
    };
}
