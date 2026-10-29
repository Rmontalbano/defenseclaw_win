using DefenseClaw.Core.Audit;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// The Logs panel's filter vocabulary, ported from the Mac (<c>LogPreset</c>, the TUI's <c>FILTER_PRESETS</c>, and the severity / action /
/// event pickers of <c>LogsView</c>): the names the menus offer and the rule each one applies to a row.
/// <para>
/// <b>Held to the 0.8.10 TUI (CUST-263).</b> A row is matched on <see cref="LogEntry.MatchText"/>, which is the line the TUI would match: a log-file line as it is, an
/// event with its severity as a word in it (<c>" info "</c>, <c>" high "</c>) the way the TUI's rendered line has it. <b>No Noise</b> is the TUI's rule
/// (<see cref="LogSignalRule"/>): drop the heartbeat lines, then drop a line marked low-signal unless it says something actionable. <b>Errors</b> and
/// <b>Warnings+</b> keep the Mac's severity floor (High, Medium) and add the TUI's words, <c>error</c>, <c>fatal</c>, <c>panic</c> (and <c>warn</c> for Warnings+),
/// wherever the row says them - which is how an event stored with the severity <c>ERROR</c>, which is not on the severity ladder, is an error here as it is there.
/// <b>Scan</b> also keeps <c>finding</c>, <b>Drift</b> <c>rescan</c> and <b>Guardrail</b> <c>guard</c>, which the TUI matches and the Mac does not; the Mac's own
/// words stay.
/// </para>
/// <para>
/// <b><c>http 4xx</c> / <c>http 5xx</c> stays.</b> A log line that says <c>HTTP 503</c> or <c>http 404</c> is read as an error (<see cref="LogEntry.Severity"/> High), which
/// puts it in Errors and Warnings+, on the tone bar and above a "Severity ≥ High" floor. The TUI does none of that: it colours and filters on the words
/// <c>error</c>, <c>fatal</c> and <c>panic</c> only, so a bare status line is not an error there. It stays because a refused or failed request is what an operator
/// filtering for errors is looking for, the tone bar and the severity picker have always agreed with the presets about it (a test holds that), and a false
/// positive costs one extra row where a miss costs the failure - but it is the one place these presets show more than the TUI's.
/// </para>
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

    /// <summary>The TUI's <c>IMPORTANT_PATTERNS</c> (the Mac's list is the same).</summary>
    internal static readonly string[] ImportantKeywords =
    {
        "error", "fatal", "panic", "warn", "block", "allow", "reject", "quarantine", "scan", "drift", "verdict", "guardrail",
        "connected", "disconnected", "started", "stopped",
    };

    /// <summary>What the TUI's Errors preset looks for in a line.</summary>
    internal static readonly string[] ErrorWords = { "error", "fatal", "panic" };

    /// <summary>What the TUI's Warnings+ preset looks for in a line: the error words and <c>warn</c>.</summary>
    internal static readonly string[] WarningWords = { "error", "fatal", "panic", "warn" };

    /// <summary>The TUI's Scan words (<c>scan</c> and, added by CUST-263, <c>finding</c>).</summary>
    internal static readonly string[] ScanWords = { "scan", "finding" };

    /// <summary>The TUI's Drift words (<c>drift</c> and, added by CUST-263, <c>rescan</c>).</summary>
    internal static readonly string[] DriftWords = { "drift", "rescan" };

    /// <summary>The Guardrail words: the Mac's (<c>guardrail</c>, <c>verdict</c>, <c>judge</c>) and the TUI's <c>guard</c>, added by CUST-263.</summary>
    internal static readonly string[] GuardrailWords = { "guardrail", "guard", "verdict", "judge" };

    /// <summary>The TUI's Hooks word.</summary>
    internal static readonly string[] HookWords = { "hook" };

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
    public static bool Matches(string name, LogEntry entry) => Matches(name, entry, hideLowSignal: true);

    /// <summary>
    /// <see cref="Matches(string, LogEntry)"/> with a say in the low-signal half of No Noise (<paramref name="hideLowSignal"/>). On the Events stream the
    /// low-signal rows belong to the "Actionable only" switch (CUST-262), whose promise is that turning it off shows every event: that stream asks with
    /// <c>false</c>, and No Noise there drops the heartbeat lines only. Everywhere else the TUI's whole rule applies.
    /// </summary>
    public static bool Matches(string name, LogEntry entry, bool hideLowSignal)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var text = entry.MatchText;
        return name switch
        {
            NoNoise => !LogSignalRule.IsNoise(text) && (!hideLowSignal || LogSignalRule.HasActionableSignal(text)),
            Important => ActionableRule.ContainsAny(text, ImportantKeywords),
            Errors => entry.Severity >= AuditSeverity.High || ActionableRule.ContainsAny(text, ErrorWords),
            WarningsPlus => entry.Severity >= AuditSeverity.Medium || ActionableRule.ContainsAny(text, WarningWords),
            Scan => entry.EventType == "scan" || ActionableRule.ContainsAny(text, ScanWords),
            Drift => ActionableRule.ContainsAny(text, DriftWords),
            Guardrail => ActionableRule.ContainsAny(text, GuardrailWords),
            Hooks => entry.EventType == "hook" || ActionableRule.ContainsAny(text, HookWords),
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
