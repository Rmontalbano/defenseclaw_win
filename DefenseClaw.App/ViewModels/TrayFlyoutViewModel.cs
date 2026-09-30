using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// The compact left-click flyout: gateway state, active connectors, alert count, and the
/// two actions an operator actually wants from the tray.
/// <para>
/// Reads the same <see cref="GatewaySnapshot"/> the main window does, so the flyout can
/// never disagree with the dashboard.
/// </para>
/// <para>
/// Two subscriptions with two lifetimes. <see cref="GatewayMonitor.StateChanged"/> — everything
/// that can change materially — is held for the life of the process, so the flyout is right the
/// instant it opens. <see cref="GatewayMonitor.PollCompleted"/>, for the one volatile field the
/// flyout prints (<see cref="LastPolled"/>), is held <b>only while the flyout is on screen</b>
/// (<see cref="SetVisible"/>, driven by the window): a subscriber costs one dispatcher hop per
/// poll, and a tray app spends nearly all of its life with the flyout hidden. Showing it reads
/// <see cref="GatewayMonitor.Current"/>, so nothing waits a poll to be current.
/// </para>
/// </summary>
public sealed partial class TrayFlyoutViewModel : ObservableObject, IDisposable
{
    private readonly AppServices _services;
    private readonly Action _openDashboard;
    private readonly Action _exit;
    private bool _trackingPolls;
    private bool _disposed;

    [ObservableProperty]
    private string _stateLabel = "Checking…";

    [ObservableProperty]
    private string _stateDetail = string.Empty;

    /// <summary>
    /// Design-system tone key for the state dot (<c>Ok / Warn / Bad / Neutral</c>). A key rather
    /// than a brush so the dot is a <c>DcToneDot</c> and follows a live light/dark switch — the
    /// frozen shield-colour brush it replaces never did.
    /// </summary>
    [ObservableProperty]
    private string _stateTone = "Neutral";

    [ObservableProperty]
    private string _connectorSummary = "—";

    [ObservableProperty]
    private string _alertSummary = "—";

    /// <summary><c>Critical</c> when the last alert list held a CRITICAL, else <c>Neutral</c>.</summary>
    [ObservableProperty]
    private string _alertTone = "Neutral";

    /// <summary>"DefenseClaw 0.8.10", or "unknown" until the gateway has reported a version.</summary>
    [ObservableProperty]
    private string _versionSummary = string.Empty;

    /// <summary>Where the gateway's REST API listens, e.g. <c>127.0.0.1:18970</c>.</summary>
    [ObservableProperty]
    private string _endpointSummary = string.Empty;

    [ObservableProperty]
    private string _lastPolled = "never";

    /// <summary>
    /// The one line about fail-mode drift the operator sees before opening the dashboard.
    /// Deliberately a single sentence: the full explanation and the copyable remediation
    /// live in the Overview panel's attention list.
    /// </summary>
    [ObservableProperty]
    private string _failModeNote = string.Empty;

    [ObservableProperty]
    private bool _hasFailModeNote;

    public TrayFlyoutViewModel(AppServices services, Action openDashboard, Action exit)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _openDashboard = openDashboard ?? throw new ArgumentNullException(nameof(openDashboard));
        _exit = exit ?? throw new ArgumentNullException(nameof(exit));

        _services.Monitor.StateChanged += OnStateChanged;
        Apply(_services.Monitor.Current);
    }

    /// <summary>True while <see cref="GatewayMonitor.PollCompleted"/> is subscribed, i.e. while the flyout is showing.</summary>
    internal bool IsTrackingPolls => _trackingPolls;

    /// <summary>
    /// Called by the flyout window (UI thread) when it is shown or hidden. Showing subscribes to
    /// <see cref="GatewayMonitor.PollCompleted"/> and re-reads <see cref="GatewayMonitor.Current"/>,
    /// which also refreshes <see cref="LastPolled"/> for the polls that went by while hidden;
    /// hiding drops the subscription so an idle poll queues nothing to the UI thread.
    /// </summary>
    public void SetVisible(bool visible)
    {
        if (_disposed)
        {
            return;
        }

        if (visible)
        {
            if (!_trackingPolls)
            {
                _trackingPolls = true;
                _services.Monitor.PollCompleted += OnPollCompleted;
            }

            Apply(_services.Monitor.Current);
        }
        else if (_trackingPolls)
        {
            _trackingPolls = false;
            _services.Monitor.PollCompleted -= OnPollCompleted;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _trackingPolls = false;
        _services.Monitor.StateChanged -= OnStateChanged;
        _services.Monitor.PollCompleted -= OnPollCompleted;
    }

    [RelayCommand]
    private void OpenDashboard() => _openDashboard();

    [RelayCommand]
    private void Exit() => _exit();

    private void OnStateChanged(object? sender, GatewaySnapshotEventArgs e) => Apply(e.Snapshot);

    /// <summary>
    /// The cheap path for the one field StateChanged does not carry: the time of the last
    /// poll, which moves every few seconds while everything else usually does not.
    /// </summary>
    private void OnPollCompleted(object? sender, GatewaySnapshotEventArgs e) =>
        LastPolled = FormatPolledAt(e.Snapshot.PolledAt);

    private static string FormatPolledAt(DateTimeOffset polledAt) =>
        polledAt == DateTimeOffset.MinValue
            ? "never"
            : polledAt.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.CurrentCulture);

    private void Apply(GatewaySnapshot snapshot)
    {
        StateLabel = snapshot.StateLabel;
        StateDetail = snapshot.Detail;
        StateTone = GatewayPresentation.StateTone(snapshot);
        ConnectorSummary = snapshot.ConnectorSummary;
        VersionSummary = GatewayPresentation.VersionText(snapshot) is { Length: > 0 } version ? version : "unknown";
        EndpointSummary = $"127.0.0.1:{snapshot.ApiPort}";

        // The same wording and tone as the dashboard's status strip. Before the first poll there is
        // no alert data at all; GatewayPresentation says "—" rather than claim "0".
        AlertSummary = GatewayPresentation.AlertText(snapshot).Replace("Alerts: ", string.Empty, StringComparison.Ordinal);
        AlertTone = GatewayPresentation.AlertTone(snapshot);

        if (snapshot.FailModeDrift is { } drift)
        {
            FailModeNote =
                $"⚠ settings.json forces fail-{drift.EnvFailMode}; {drift.GatewaySource} says " +
                $"{drift.GatewayFailMode} — open the dashboard";
            HasFailModeNote = true;
        }
        else
        {
            FailModeNote = string.Empty;
            HasFailModeNote = false;
        }

        LastPolled = FormatPolledAt(snapshot.PolledAt);
    }
}
