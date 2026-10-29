using DefenseClaw.Core.Gateway.Models;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// What the doctor cache says once it is held against the live gateway (CUST-213; the Mac's <c>liveHealthContradicts</c> and
/// <c>missingRequiredCredentials</c>). The cache is a photograph from the last <c>defenseclaw doctor</c> run; <c>/health</c> is the gateway now.
/// </summary>
internal static class DoctorReconciliation
{
    /// <summary>The prefix of the check doctor writes for a required key that is not set (<c>credential ANTHROPIC_API_KEY</c>).</summary>
    internal const string CredentialPrefix = "credential ";

    /// <summary>
    /// True when a cached <i>fail</i> or <i>warn</i> names a subsystem that <paramref name="health"/> reports running: the result is older than the
    /// fix, so it is STALE rather than a failure. Without a live <c>/health</c> (the gateway is not answering) nothing is contradicted. The mapping is
    /// the Mac's: <c>sidecar api</c> to api, <c>guardrail proxy</c> to guardrail, <c>openclaw gateway</c> and <c>gateway</c> to the gateway
    /// subsystem, any <c>otel…</c> check to telemetry.
    /// </summary>
    internal static bool LiveHealthContradicts(DoctorCheckRow check, GatewayHealth? health)
    {
        ArgumentNullException.ThrowIfNull(check);

        if (health is null || check.Status is not ("fail" or "warn"))
        {
            return false;
        }

        var label = check.Label.Trim().ToLowerInvariant();
        return label switch
        {
            "sidecar api" => health.Api?.IsRunning == true,
            "guardrail proxy" => health.Guardrail?.IsRunning == true,
            "openclaw gateway" or "gateway" => health.FleetUplink?.IsRunning == true,
            _ when label.StartsWith("otel", StringComparison.Ordinal) => health.Telemetry?.IsRunning == true,
            _ => false,
        };
    }

    /// <summary>The names of the required keys doctor found missing: every failing <c>credential &lt;NAME&gt;</c> check, in file order.</summary>
    internal static IReadOnlyList<string> MissingRequiredCredentials(DoctorCacheSnapshot doctor)
    {
        ArgumentNullException.ThrowIfNull(doctor);

        var names = new List<string>();
        foreach (var check in doctor.Checks)
        {
            if (check.Status != "fail" || !check.Label.StartsWith(CredentialPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var name = check.Label[CredentialPrefix.Length..].Trim();
            if (name.Length > 0 && !names.Contains(name, StringComparer.Ordinal))
            {
                names.Add(name);
            }
        }

        return names;
    }
}
