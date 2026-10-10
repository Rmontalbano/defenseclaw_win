namespace DefenseClaw.App.Services;

/// <summary>
/// How health-derived figures are marked when the <c>/health</c> answer did not come from the verified gateway (CUST-249 / CUST-341).
/// When <see cref="GatewaySnapshot.PeerUnverified"/> is set, whatever process answers on the port is an unknown peer: its services, connectors,
/// scanners and uptime are shown as reported, never as confirmed. This is the one place that decides that, so every Overview surface that
/// reads <c>/health</c> says the same thing and no view has its own rule.
/// </summary>
internal static class HealthTrustPresentation
{
    /// <summary>The suffix appended to a health-derived figure, the same words the version surfaces use.</summary>
    public const string UnverifiedSuffix = " (unverified)";

    /// <summary>The short note shown on each health-derived Overview surface while the peer is unverified.</summary>
    public const string UnverifiedNote = "Unverified: this health data comes from a process that is not the verified gateway.";

    /// <summary>True when the snapshot carries <c>/health</c> data that must be marked (an unverified peer that did answer).</summary>
    public static bool IsUnverified(GatewaySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return snapshot.PeerUnverified && snapshot.Health is not null;
    }

    /// <summary>The note for a health-derived surface, or empty when the data is verified (or absent).</summary>
    public static string NoteFor(GatewaySnapshot snapshot) => IsUnverified(snapshot) ? UnverifiedNote : string.Empty;

    /// <summary>
    /// <paramref name="text"/> with the "(unverified)" suffix when the snapshot's health is unverified; unchanged otherwise, and never
    /// suffixed when there is no text to mark.
    /// </summary>
    public static string Mark(string text, GatewaySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(text);

        return text.Length > 0 && IsUnverified(snapshot) ? text + UnverifiedSuffix : text;
    }
}
