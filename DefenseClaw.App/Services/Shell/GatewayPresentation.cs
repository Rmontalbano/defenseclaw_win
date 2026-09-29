namespace DefenseClaw.App.Services;

/// <summary>
/// How a <see cref="GatewaySnapshot"/> is worded and toned on the shell's two always-alive
/// surfaces, the dashboard's status strip and the tray flyout, so both say the same thing.
/// <para>
/// The tone keys are the shared design system's <c>Tag</c> vocabulary
/// (<c>Ok / Warn / Bad / Critical / Neutral</c>, see Themes\DefenseClaw.xaml). Surfaces bind
/// <c>Tag</c> to them and the theme picks the colour from WPF-UI brushes, so a live light/dark
/// switch restyles the dot and the badge with no code — the shield-icon colours the strip used
/// to freeze into brushes never followed the theme.
/// </para>
/// </summary>
internal static class GatewayPresentation
{
    /// <summary>
    /// Tone of the gateway's own health. Deliberately independent of alerts: a running gateway
    /// with a CRITICAL finding is still a running gateway, and the alert chip carries the finding.
    /// </summary>
    public static string StateTone(GatewaySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return snapshot.State switch
        {
            AppGatewayState.Running => "Ok",
            AppGatewayState.GatewayStopped => "Bad",
            AppGatewayState.Degraded => "Warn",
            AppGatewayState.WslGatewayDetected => "Warn",
            AppGatewayState.NotInstalled => "Warn",
            AppGatewayState.NotInitialized => "Warn",
            _ => "Neutral",
        };
    }

    /// <summary>
    /// Tone of the alert chip: red only for a CRITICAL in the last list. Everything else,
    /// including "the alert subsystem is not connected" (a normal standalone install), is neutral —
    /// an unavailable list must never look like a finding.
    /// </summary>
    public static string AlertTone(GatewaySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return HasAlertAnswer(snapshot) && snapshot.HasCriticalAlert ? "Critical" : "Neutral";
    }

    /// <summary>
    /// The short chip text. The gateway is asked for its newest <see cref="GatewayMonitor.AlertLimit"/>
    /// alerts, so a count at the limit is "25+" rather than a claim of exactly 25.
    /// </summary>
    public static string AlertText(GatewaySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.PolledAt == DateTimeOffset.MinValue)
        {
            return "Alerts: —";
        }

        if (snapshot.AlertsUnavailable is { Length: > 0 })
        {
            return "Alerts: unavailable";
        }

        var count = snapshot.AlertCount >= GatewayMonitor.AlertLimit
            ? $"{GatewayMonitor.AlertLimit}+ recent alerts"
            : $"{snapshot.AlertCount} recent alert{(snapshot.AlertCount == 1 ? string.Empty : "s")}";

        return snapshot.CriticalAlertCount > 0
            ? $"{count} · {snapshot.CriticalAlertCount} critical"
            : count;
    }

    /// <summary>
    /// The full-sentence version for tooltips and screen readers: the reason an alert list is
    /// unavailable, or the chip text spelled out.
    /// </summary>
    public static string AlertDetail(GatewaySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.PolledAt == DateTimeOffset.MinValue)
        {
            return "Alerts have not been polled yet.";
        }

        if (snapshot.AlertsUnavailable is { Length: > 0 } reason)
        {
            return reason;
        }

        var newest = snapshot.AlertCount >= GatewayMonitor.AlertLimit
            ? $"at least {GatewayMonitor.AlertLimit} recent alerts (the gateway returns its newest {GatewayMonitor.AlertLimit})"
            : $"{snapshot.AlertCount} recent alert{(snapshot.AlertCount == 1 ? string.Empty : "s")}";

        return snapshot.CriticalAlertCount > 0
            ? $"{newest}, {snapshot.CriticalAlertCount} of them critical."
            : $"{newest}, none critical.";
    }

    /// <summary>"Connector: claudecode", "Connectors: a, b" or "No connector".</summary>
    public static string ConnectorText(GatewaySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return snapshot.ActiveConnectors.Count switch
        {
            0 => "No connector",
            1 => $"Connector: {snapshot.ActiveConnectors[0]}",
            _ => $"Connectors: {string.Join(", ", snapshot.ActiveConnectors)}",
        };
    }

    /// <summary>"DefenseClaw 0.8.10", or empty before the version is known.</summary>
    public static string VersionText(GatewaySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return string.IsNullOrWhiteSpace(snapshot.BinaryVersion)
            ? string.Empty
            : $"DefenseClaw {snapshot.BinaryVersion}";
    }

    /// <summary>True when the last poll produced an alert answer (a list, possibly empty).</summary>
    private static bool HasAlertAnswer(GatewaySnapshot snapshot) =>
        snapshot.PolledAt != DateTimeOffset.MinValue && snapshot.AlertsUnavailable is not { Length: > 0 };
}
