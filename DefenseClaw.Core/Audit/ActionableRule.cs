namespace DefenseClaw.Core.Audit;

/// <summary>
/// The 0.8.10 TUI's "actionable" rule (CUST-262): which rows are <em>signal</em> and which are the low-signal rows its Audit panel and its
/// Alerts panel hide until the operator asks for everything. One predicate, shared by the Audit panel's "Actionable only" switch and the Logs
/// panel's Events view, so the two can never disagree about what a row is.
/// <para>
/// <b>The rule</b> (<c>tui/panels/audit.py</c> <c>_is_low_signal_event</c>, <c>tui/panels/alerts.py</c> <c>_is_low_signal_alert</c>; the two
/// are the same rule over different text). A row is <em>low-signal</em> - hidden - only when <em>both</em> hold:
/// </para>
/// <list type="number">
///   <item>its severity, trimmed and upper-cased, is one of <see cref="LowSignalSeverities"/> (<c>INFO</c>, <c>LOW</c>, <c>MEDIUM</c>,
///   <c>WARNING</c>, or none at all); and</item>
///   <item>none of <see cref="Tokens"/> (<c>block</c>, <c>deny</c>, <c>reject</c>, <c>quarantine</c>, <c>fail</c>, <c>error</c>, <c>fatal</c>,
///   <c>panic</c> and their inflections) occurs, as a case-insensitive <em>substring</em>, in the row's text.</item>
/// </list>
/// <para>
/// Everything else is <em>actionable</em>: <c>CRITICAL</c>, <c>HIGH</c> and <c>ERROR</c> always (<see cref="ActionableSeverities"/>), and a
/// severity the rule has no opinion on (<c>FATAL</c>, <c>WARN</c>, <c>NONE</c>, anything unlisted) is never hidden either - the TUI's order of
/// tests is "actionable severity, then not a low-signal severity, then the words".
/// </para>
/// <para>
/// <b>It is a substring test, as the TUI's is.</b> <c>would_block=false</c> contains <c>block</c>, so a connector-hook row whose details say
/// that is shown (on a real 3.5-day window of hook traffic that is every hook row); <c>blocked</c>, <c>failure</c> and the rest are
/// substrings of the shorter tokens and are listed only to keep the table the TUI's own. Which text is searched belongs to the caller: for an
/// audit row it is the id, action, target, actor, severity, details and run id (<see cref="IsActionable(AuditEvent)"/>, the columns the TUI's
/// Audit panel selects); <see cref="StreamEvent"/> builds the Alerts panel's own (decision, target, event name, bucket, source, connector, reason).
/// </para>
/// <para>
/// <b>What the TUI does with "not actionable"</b> is hide it by default and show it again - for good, until the next launch - the moment a
/// search or a preset is chosen (<c>show_all_events</c>, <c>show_all_severities</c>). The panels here keep the choice and suspend it
/// <em>while</em> such a filter is on, so clearing the search brings the actionable view back.
/// </para>
/// </summary>
public static class ActionableRule
{
    /// <summary>Severities that are always actionable (the TUI's <c>ACTIONABLE_SEVERITIES</c> / <c>AUDIT_ACTIONABLE_SEVERITIES</c>).</summary>
    public static IReadOnlyList<string> ActionableSeverities { get; } = new[] { "CRITICAL", "HIGH", "ERROR" };

    /// <summary>
    /// Severities a row may have and still be hidden (the TUI's <c>LOW_SIGNAL_SEVERITIES</c> / <c>AUDIT_LOW_SIGNAL_SEVERITIES</c>). The empty
    /// string is a row with no severity: the Audit panel's own set has it, and the Alerts panel's rows never lack one (it reads "INFO" for a blank).
    /// </summary>
    public static IReadOnlyList<string> LowSignalSeverities { get; } = new[] { "INFO", "LOW", "MEDIUM", "WARNING", string.Empty };

    /// <summary>The words that make a low-severity row actionable anyway (the TUI's <c>AUDIT_ACTIONABLE_TOKENS</c>, verbatim and in its order).</summary>
    public static IReadOnlyList<string> Tokens { get; } = new[]
    {
        "block", "blocked", "deny", "denied", "reject", "rejected", "quarantine", "fail", "failed", "failure", "error", "fatal", "panic",
    };

    /// <summary>
    /// True when a row with this <paramref name="severity"/> and this <paramref name="text"/> is worth an operator's attention (see the type
    /// documentation). <paramref name="severity"/> is the stored text, not a parsed level: <c>ERROR</c> and <c>FATAL</c> are not on
    /// <see cref="AuditSeverity"/>'s ladder, and the rule needs them as written.
    /// </summary>
    public static bool IsActionable(string? severity, string? text) => SeverityDecides(severity) || HasToken(text);

    /// <summary>
    /// <see cref="IsActionable(string?, string?)"/> for an audit row, over the text the TUI's Audit panel searches: id, action, target, actor,
    /// severity, details and run id (<c>_event_haystack</c>; each is searched where it stands, which is the same as searching them joined, since no
    /// token has a space in it). The columns the TUI does not select - event name, bucket, tool, agent, connector - are not part of it, so a row
    /// is judged on what the TUI would judge it on.
    /// <para>
    /// A row whose <c>details</c> are too large to load (<see cref="AuditEvent.Oversized"/>) is <em>actionable</em>: what it says is unknown, and a
    /// row is never hidden on the strength of text that was not read.
    /// </para>
    /// </summary>
    public static bool IsActionable(AuditEvent audit)
    {
        ArgumentNullException.ThrowIfNull(audit);

        if (SeverityDecides(audit.Severity))
        {
            return true;
        }

        foreach (var value in audit.Oversized)
        {
            if (string.Equals(value.Column, "details", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return HasToken(audit.Id) || HasToken(audit.Action) || HasToken(audit.Target) || HasToken(audit.Actor)
            || HasToken(audit.Severity) || HasToken(audit.Details) || HasToken(audit.RunId);
    }

    /// <summary>True when any of <see cref="Tokens"/> occurs in <paramref name="text"/>, without regard to case.</summary>
    public static bool HasToken(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        foreach (var token in Tokens)
        {
            if (text.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when the severity alone makes the row actionable: an actionable severity, or one the rule does not list as low-signal.</summary>
    private static bool SeverityDecides(string? severity)
    {
        var level = severity is null ? string.Empty : severity.Trim().ToUpperInvariant();
        return Contains(ActionableSeverities, level) || !Contains(LowSignalSeverities, level);
    }

    private static bool Contains(IReadOnlyList<string> set, string level)
    {
        for (var i = 0; i < set.Count; i++)
        {
            if (string.Equals(set[i], level, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
