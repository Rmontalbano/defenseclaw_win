using System.Text.RegularExpressions;

namespace DefenseClaw.Core.Gateway.Models;

/// <summary>
/// The one-connector form of "the agent updated itself" (runtime 1.0.10). With a single configured connector the gateway has no "not started" list
/// (<c>guardrail.details.connectors_not_started</c> is written only for more than one connector); it keeps serving and /health shows the guardrail in
/// <c>error</c> with <c>last_error</c> like "connector codex setup failed: ... the Codex executable changed since setup (a Codex update does this); run:
/// defenseclaw setup codex --yes" (sidecar.go@1.0.10:1735-1737, 5499-5500; connector/agent_executable_changed.go@1.0.10:49-51). This reads the connector out of
/// that sentence, only from the fixed list codex, amp, hermes and opencode, so a name taken from the text is never used as such. Pure and never throws.
/// </summary>
public static class AgentUpdatedGuardrailError
{
    /// <summary>How much of <c>last_error</c> is looked at.</summary>
    public const int MaxInspectedLength = 4096;

    private static readonly Regex FixPattern = new(
        @"defenseclaw setup (?<name>codex|amp|hermes|opencode) --yes",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture,
        TimeSpan.FromMilliseconds(250));

    /// <summary>
    /// The connectors a guardrail in <c>error</c> says updated themselves, as <see cref="ConnectorSkipKind.AgentUpdated"/> skips; empty when the guardrail is not
    /// in error or the text does not contain the runtime's sentence.
    /// </summary>
    public static IReadOnlyList<ConnectorSkip> From(ServiceState? guardrail)
    {
        try
        {
            if (guardrail is null ||
                !string.Equals(guardrail.State?.Trim(), "error", StringComparison.OrdinalIgnoreCase) ||
                guardrail.LastError is not { Length: > 0 } raw)
            {
                return [];
            }

            var text = raw.Length > MaxInspectedLength ? raw[..MaxInspectedLength] : raw;
            if (text.IndexOf(ConnectorStartHealth.AgentUpdatedReasonPart, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return [];
            }

            var reason = ConnectorStartHealth.CleanReason(text);
            var names = new SortedSet<string>(StringComparer.Ordinal);
            foreach (Match match in FixPattern.Matches(text))
            {
                _ = names.Add(match.Groups["name"].Value.ToLowerInvariant());
            }

            return names.Select(name => new ConnectorSkip(name, ConnectorSkipKind.AgentUpdated, reason)).ToList();
        }
        catch (Exception ex) when (ex is RegexMatchTimeoutException or ArgumentException)
        {
            return [];
        }
    }
}
