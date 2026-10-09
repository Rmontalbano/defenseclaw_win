using DefenseClaw.App.Services;

namespace DefenseClaw.App.Tests.TestSupport;

/// <summary>
/// A stand-in for the gateway monitor's events and its current snapshot (<see cref="IGatewaySnapshotSource"/>), driven by hand,
/// so <see cref="ConnectorScope"/>, <see cref="AlertCountsService"/> and <see cref="RestartQueue"/> are tested without a poll loop. Counts its
/// subscribers, which is how the "does nothing while nobody listens" promises are checked.
/// </summary>
internal sealed class FakeSnapshotSource : IGatewaySnapshotSource
{
    public GatewaySnapshot Current { get; set; } = GatewaySnapshot.Initial;

    public event EventHandler<GatewaySnapshotEventArgs>? StateChanged;

    public event EventHandler<GatewaySnapshotEventArgs>? AlertCadenceElapsed;

    public event EventHandler<GatewaySnapshotEventArgs>? PollCompleted;

    public int StateChangedSubscribers => StateChanged?.GetInvocationList().Length ?? 0;

    public int CadenceSubscribers => AlertCadenceElapsed?.GetInvocationList().Length ?? 0;

    public int PollSubscribers => PollCompleted?.GetInvocationList().Length ?? 0;

    /// <summary>The monitor publishing a snapshot: it becomes current and <see cref="StateChanged"/> is raised.</summary>
    public void Publish(GatewaySnapshot snapshot)
    {
        Current = snapshot;
        StateChanged?.Invoke(this, new GatewaySnapshotEventArgs(snapshot));
    }

    /// <summary>A completed poll: the snapshot becomes current and <see cref="PollCompleted"/> is raised, whether or not anything the shell renders changed.</summary>
    public void Poll(GatewaySnapshot snapshot)
    {
        Current = snapshot;
        PollCompleted?.Invoke(this, new GatewaySnapshotEventArgs(snapshot));
    }

    /// <summary>A roster of connectors, as a poll would publish it.</summary>
    public void PublishConnectors(params string[] connectors) =>
        Publish(Current with { ActiveConnectors = connectors });

    /// <summary>The 30 s alert tick.</summary>
    public void Tick() => AlertCadenceElapsed?.Invoke(this, new GatewaySnapshotEventArgs(Current));
}
