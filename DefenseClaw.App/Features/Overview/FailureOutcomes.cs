namespace DefenseClaw.App.ViewModels;

/// <summary>
/// What a connector's hook does in each failure, from <c>defenseclaw status --json</c> <c>connectors[].fail_mode.on_failure</c> (runtime 1.0.10,
/// fail_mode.py@1.0.10:920-967 <c>failure_outcomes</c>, :1018). Three closed values; the field is JSON null for proxy connectors (openclaw, zeptoclaw), Secure Client and
/// upstream fail-open, and absent before 1.0.10. The values are kept as the runtime sent them and only ever looked up in the app's own fixed text
/// (<see cref="Sentence"/>); the runtime's own words are never shown.
/// </summary>
/// <param name="GatewayDown"><c>allow</c>, <c>block</c> or <c>allow-if-service-stopped</c>.</param>
/// <param name="NoAnswer"><c>allow-ordinary</c> or <c>block</c>.</param>
/// <param name="HookError"><c>block</c>.</param>
internal sealed record FailureOutcomes(string? GatewayDown, string? NoAnswer, string? HookError)
{
    /// <summary>The line the runtime always adds (fail_mode.py@1.0.10:997).</summary>
    internal const string NeverBlockedLine = "stop and session-end events: never blocked";

    /// <summary>
    /// One line for a connector: <c>gateway down: allow with a warning; no answer: block; hook or answer error: block; stop and session-end events: never blocked</c>.
    /// A value outside the closed set leaves its part out; null when nothing known is left (no outcomes, or none the app recognises).
    /// </summary>
    internal static string? Sentence(FailureOutcomes? outcomes)
    {
        if (outcomes is null)
        {
            return null;
        }

        var parts = new List<string>(4);
        Add(parts, "gateway down", outcomes.GatewayDown, GatewayDownText);
        Add(parts, "no answer", outcomes.NoAnswer, NoAnswerText);
        Add(parts, "hook or answer error", outcomes.HookError, HookErrorText);
        if (parts.Count == 0)
        {
            return null;
        }

        parts.Add(NeverBlockedLine);
        return string.Join("; ", parts);
    }

    private static void Add(List<string> parts, string label, string? value, Func<string, string?> text)
    {
        if (value is not null && text(value) is { } outcome)
        {
            parts.Add($"{label}: {outcome}");
        }
    }

    private static string? GatewayDownText(string value) => value switch
    {
        "allow" => "allow with a warning",
        "allow-if-service-stopped" => "allow with a warning when the hook proves the gateway service is stopped",
        "block" => "block",
        _ => null,
    };

    private static string? NoAnswerText(string value) => value switch
    {
        "allow-ordinary" => "tool calls allow with a warning unless the call may reach a protected file, prompts and tool results allow with a warning when the gateway is frozen and block when a running gateway answers too late",
        "block" => "block",
        _ => null,
    };

    private static string? HookErrorText(string value) => value == "block" ? "block" : null;
}
