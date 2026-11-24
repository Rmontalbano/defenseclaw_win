namespace DefenseClaw.Core.Audit;

/// <summary>
/// An inspection that ran out of time (DefenseClaw 1.0.10): the actions <c>inspect-tool-timeout</c> (target the tool name), <c>inspect-request-timeout</c>
/// (target <c>pre-request</c>), <c>inspect-response-timeout</c> (target <c>post-response</c>) and <c>inspect-tool-response-timeout</c> (target the tool name),
/// severity MEDIUM, bucket <c>guardrail.evaluation</c>; details <c>verdict=none reason=&lt;scan_timeout|caller_canceled&gt; elapsed_ms= budget_ms=</c>.
/// <c>scan_timeout</c> is a real miss (the call went on without that check); <c>caller_canceled</c> is the caller giving up. A runtime that does not write
/// these rows has none, so no version gate is needed.
/// </summary>
/// <param name="Action">The row's action, as written.</param>
/// <param name="Reason">The <c>reason=</c> value (<c>scan_timeout</c>, <c>caller_canceled</c> or whatever the runtime wrote, at most 32 characters); empty when absent.</param>
public sealed record InspectTimeoutRow(string Action, string Reason)
{
    /// <summary>The four action names.</summary>
    public static IReadOnlyList<string> Actions { get; } = new[]
    {
        "inspect-tool-timeout", "inspect-request-timeout", "inspect-response-timeout", "inspect-tool-response-timeout",
    };

    /// <summary>The <c>reason=</c> value of a real miss: the scan did not finish in its budget.</summary>
    public const string ScanTimeout = "scan_timeout";

    /// <summary>The <c>reason=</c> value of a caller that gave up.</summary>
    public const string CallerCanceled = "caller_canceled";

    private const int ReasonLimit = 32;

    /// <summary>True when the scan ran out of its budget: a real miss, shown even when "Actionable only" is on.</summary>
    public bool IsScanTimeout => string.Equals(Reason, ScanTimeout, StringComparison.Ordinal);

    /// <summary>True when the caller canceled the inspection.</summary>
    public bool IsCallerCanceled => string.Equals(Reason, CallerCanceled, StringComparison.Ordinal);

    /// <summary>The row's title and label.</summary>
    public string Label => IsCallerCanceled ? "Inspection canceled by the caller, call decided without it" : "Inspection timed out, call decided without it";

    /// <summary>True when <paramref name="action"/> is one of the four timeout actions.</summary>
    public static bool IsTimeoutAction(string? action)
    {
        var verb = action?.Trim();
        if (string.IsNullOrEmpty(verb))
        {
            return false;
        }

        foreach (var name in Actions)
        {
            if (string.Equals(name, verb, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Reads a row. Null for any row whose action is not one of the four; never throws.</summary>
    public static InspectTimeoutRow? Parse(string? action, string? details)
    {
        if (!IsTimeoutAction(action))
        {
            return null;
        }

        var reason = string.Empty;
        if (!string.IsNullOrEmpty(details))
        {
            foreach (var token in details.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.StartsWith("reason=", StringComparison.Ordinal))
                {
                    var value = token["reason=".Length..].Trim('"', '\'');
                    reason = value.Length <= ReasonLimit ? value : value[..ReasonLimit];
                    break;
                }
            }
        }

        return new InspectTimeoutRow(action!.Trim(), reason);
    }
}
