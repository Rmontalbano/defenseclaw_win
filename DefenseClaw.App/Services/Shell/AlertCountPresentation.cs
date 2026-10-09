using System.Globalization;
using DefenseClaw.Core.Audit;

namespace DefenseClaw.App.Services;

/// <summary>
/// How the one "unacknowledged findings" number (<see cref="AlertCounts"/>) is worded on the surfaces that show it, so the
/// sidebar badge, its screen-reader name, the tray tooltip and the notifications all say the same thing: "441", "500+",
/// "441 unacknowledged findings".
/// </summary>
internal static class AlertCountPresentation
{
    /// <summary>The badge text: the total, with a plus when the window was full ("500+"). Empty when there is nothing to show.</summary>
    public static string Badge(AlertCounts counts)
    {
        ArgumentNullException.ThrowIfNull(counts);
        return counts.Total <= 0 ? string.Empty : Compact(counts);
    }

    /// <summary>"441" or "500+", whatever the total (zero reads "0").</summary>
    public static string Compact(AlertCounts counts)
    {
        ArgumentNullException.ThrowIfNull(counts);
        return counts.Total.ToString(CultureInfo.InvariantCulture) + (counts.HasMore ? "+" : string.Empty);
    }

    /// <summary>"441 unacknowledged findings", "1 unacknowledged finding", "500+ unacknowledged findings", "no unacknowledged findings".</summary>
    public static string Sentence(AlertCounts counts)
    {
        ArgumentNullException.ThrowIfNull(counts);

        if (counts.Total <= 0)
        {
            return "no unacknowledged findings";
        }

        var one = counts.Total == 1 && !counts.HasMore;
        return $"{Compact(counts)} unacknowledged finding{(one ? string.Empty : "s")}";
    }

    /// <summary>The Alerts sidebar entry's accessible name: "Alerts, 441 unacknowledged findings", or just "Alerts" when nothing waits.</summary>
    public static string NavigationName(string title, AlertCounts counts)
    {
        ArgumentNullException.ThrowIfNull(counts);
        return counts.Total <= 0 ? title : $"{title}, {Sentence(counts)}";
    }

    /// <summary>
    /// Why the Overview entry carries a caution badge, in words a screen reader can append to "Overview": "gateway stopped",
    /// "gateway degraded", "DefenseClaw not installed". Null while the gateway is not degraded, which is when there is no badge.
    /// </summary>
    public static string? DegradedReason(GatewaySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!snapshot.IsDegraded)
        {
            return null;
        }

        return snapshot.State switch
        {
            AppGatewayState.GatewayStopped => "gateway stopped",
            AppGatewayState.NotInstalled => "DefenseClaw not installed",
            _ => "gateway degraded",
        };
    }

    /// <summary>
    /// The tray tooltip: the count first, since it is what the operator glances at (the real number: the icon stops at "9+"),
    /// then the state. The shell cuts a tooltip at 127 characters, and this stays well inside that. When the count is not known
    /// yet (the first read has not finished, or there is no audit database and no gateway to ask) the line is the state alone.
    /// <para>
    /// The state is the gateway's (<see cref="GatewaySnapshot.StateLabel"/>, which says "Monitoring paused" while the operator has
    /// paused it), and while a scan is running it is led by "Scanning", the second state the icon shows. A pause outranks a scan, as
    /// it does on the icon, so a paused tooltip never says scanning.
    /// </para>
    /// </summary>
    /// <param name="snapshot">The gateway state.</param>
    /// <param name="counts">The counts, or null while <see cref="AlertCountsService.HasData"/> is false.</param>
    /// <param name="scanning">True while a scan is running (<see cref="ScanActivity.IsScanning"/>).</param>
    public static string TrayTooltip(GatewaySnapshot snapshot, AlertCounts? counts, bool scanning = false)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var state = scanning && !snapshot.IsPaused ? $"Scanning — {snapshot.StateLabel}" : snapshot.StateLabel;
        return counts is null
            ? $"DefenseClaw — {state}"
            : $"DefenseClaw — {Sentence(counts)}\n{state}";
    }
}
