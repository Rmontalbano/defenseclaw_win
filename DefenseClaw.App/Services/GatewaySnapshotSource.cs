namespace DefenseClaw.App.Services;

/// <summary>
/// What <see cref="ConnectorScope"/>, <see cref="AlertCountsService"/> and <see cref="RestartQueue"/> need from the gateway monitor, so they
/// can be driven by a fake in a test without a poll loop. <see cref="GatewayMonitor"/> is the only implementation.
/// </summary>
internal interface IGatewaySnapshotSource
{
    /// <summary>The most recent snapshot. Never null.</summary>
    GatewaySnapshot Current { get; }

    /// <summary>Raised on the UI thread when the snapshot changes in a way the shell renders; see <see cref="GatewayMonitor.StateChanged"/>.</summary>
    event EventHandler<GatewaySnapshotEventArgs>? StateChanged;

    /// <summary>The alert cadence tick; see <see cref="GatewayMonitor.AlertCadenceElapsed"/>. Raised on a pool thread.</summary>
    event EventHandler<GatewaySnapshotEventArgs>? AlertCadenceElapsed;

    /// <summary>
    /// Raised on the UI thread after every completed poll, with the volatile detail (the gateway's start time and uptime) that
    /// <see cref="StateChanged"/> leaves out; see <see cref="GatewayMonitor.PollCompleted"/>. Costs nothing while nothing is subscribed.
    /// </summary>
    event EventHandler<GatewaySnapshotEventArgs>? PollCompleted;
}
