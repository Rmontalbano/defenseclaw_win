namespace DefenseClaw.Core.Audit;

/// <summary>
/// The 0.8.10 TUI's rule for what its <em>Logs</em> panel's default "No Noise" view hides (CUST-263; <c>tui/panels/logs.py</c>: <c>NOISE_PATTERNS</c>,
/// <c>ACTIONABLE_LOG_PATTERNS</c>, <c>LOW_SIGNAL_LOG_PATTERNS</c>, <c>_line_has_actionable_signal</c>). Three tables and two questions, all asked of one
/// <em>line of text</em> - a log line as it is, or an event as the TUI renders it, with its severity as a word in it.
/// <para>
/// <b>The rule.</b> A line is dropped when it carries a heartbeat (<see cref="NoisePatterns"/>: <c>event tick seq=</c>, <c>payload_len=20</c>, ...), whatever else
/// it says; otherwise it is dropped when it has a low-signal marker (<see cref="LowSignalPatterns"/>: <c>" info "</c>, <c>severity=low</c>, <c>" medium "</c>, ...) and
/// none of <see cref="ActionablePatterns"/> (<c>error</c>, <c>blocked</c>, <c>" high "</c>, <c>critical</c>, <c>warn</c>, ...). A line with neither a
/// marker nor a signal word is kept: the rule hides what it can show to be quiet, not everything it cannot show to be loud.
/// </para>
/// <para>
/// <b>Why this is not <see cref="ActionableRule"/>.</b> That is the TUI's Audit and Alerts rule: the same shape, but its inputs are a severity <em>column</em> (an
/// exact set of low-signal levels) and a haystack, and its words are the thirteen of <c>AUDIT_ACTIONABLE_TOKENS</c>. The Logs lists are the TUI's own and
/// differ - <c>warn</c>, <c>critical</c> and the padded severity words are signal here and not there, and a low-signal marker is a word the line says, not a
/// level a row has - so the two stay two tables, as upstream keeps them, and share what is shared: the matcher (<see cref="ActionableRule.ContainsAny"/>).
/// A test holds each table to the TUI's source and the Logs list to its relation with the Audit one (every Audit word is a Logs signal word).
/// </para>
/// <para>
/// Matching is a case-insensitive substring test over the text given, which is what the TUI does to its lower-cased line. The padded words (<c>" high "</c>)
/// are why a caller that builds the text of an event puts the severity in it with a space on each side, as the TUI's column is.
/// </para>
/// </summary>
public static class LogSignalRule
{
    /// <summary>Heartbeat chatter, dropped by "No Noise" whatever else the line says (the TUI's <c>NOISE_PATTERNS</c>, verbatim and in its order).</summary>
    public static IReadOnlyList<string> NoisePatterns { get; } = new[]
    {
        "event tick seq=",
        "event health seq=",
        "payload_len=20",
        "mallocstacklogging",
        "event sessions.changed seq=nil",
        "content-length=0",
    };

    /// <summary>Words that make a line signal whatever else it says (the TUI's <c>ACTIONABLE_LOG_PATTERNS</c>, verbatim and in its order).</summary>
    public static IReadOnlyList<string> ActionablePatterns { get; } = new[]
    {
        "critical",
        " high ",
        "severity=high",
        "severity:high",
        "error",
        "fatal",
        "panic",
        "warn",
        "block",
        "blocked",
        "deny",
        "denied",
        "reject",
        "rejected",
        "quarantine",
        "fail",
        "failed",
        "failure",
    };

    /// <summary>Markers of a line that is quiet unless it also has a signal word (the TUI's <c>LOW_SIGNAL_LOG_PATTERNS</c>, verbatim and in its order).</summary>
    public static IReadOnlyList<string> LowSignalPatterns { get; } = new[]
    {
        " info ",
        "severity=info",
        "severity:info",
        " low ",
        "severity=low",
        "severity:low",
        " medium ",
        "severity=medium",
        "severity:medium",
    };

    /// <summary>True when <paramref name="text"/> carries one of <see cref="NoisePatterns"/>.</summary>
    public static bool IsNoise(string? text) => ActionableRule.ContainsAny(text, NoisePatterns);

    /// <summary>
    /// The TUI's <c>_line_has_actionable_signal</c>: true when the text has a signal word, or has no low-signal marker at all. False only for a line that is marked
    /// quiet and says nothing that would make an operator look at it.
    /// </summary>
    public static bool HasActionableSignal(string? text) =>
        ActionableRule.ContainsAny(text, ActionablePatterns) || !ActionableRule.ContainsAny(text, LowSignalPatterns);
}
