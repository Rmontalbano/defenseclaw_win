using DefenseClaw.Core.AiRuntime;

namespace DefenseClaw.App.Services;

/// <summary>
/// Where the Runtime monitoring card learns whether the gateway runs with an administrator token. This is the hook point for the "Run gateway as
/// administrator" mode (CUST-348, phase 2): that feature supplies an implementation (it knows because it started the elevated gateway, or because
/// it read the gateway's token through a limited-information handle) and hands it to <see cref="ViewModels.RuntimeMonitoringViewModel"/>; this phase
/// builds no elevation, so <see cref="Pending"/> is what every caller gets and the card shows "Needs Run gateway as administrator", greyed.
/// </summary>
public interface IGatewayElevationStatus
{
    /// <summary>What is known now. Cheap and non-blocking: called on the UI thread whenever the card is drawn again.</summary>
    GatewayElevation Current { get; }

    /// <summary>Raised when <see cref="Current"/> may have changed (the helper started or stopped the gateway); may be raised on any thread.</summary>
    event EventHandler? Changed;
}

/// <summary>The status when the administrator mode does not exist yet: nothing is known, and nothing is guessed.</summary>
public sealed class PendingGatewayElevationStatus : IGatewayElevationStatus
{
    /// <summary>The one instance.</summary>
    public static PendingGatewayElevationStatus Instance { get; } = new();

    private PendingGatewayElevationStatus()
    {
    }

    public GatewayElevation Current => GatewayElevation.Unknown;

#pragma warning disable CS0067 // Never raised: nothing here ever changes.
    public event EventHandler? Changed;
#pragma warning restore CS0067
}
