using System.Windows.Media;
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
/// </summary>
public sealed partial class TrayFlyoutViewModel : ObservableObject, IDisposable
{
    private readonly AppServices _services;
    private readonly Action _openDashboard;
    private readonly Action _exit;
    private bool _disposed;

    [ObservableProperty]
    private string _stateLabel = "Checking…";

    [ObservableProperty]
    private string _stateDetail = string.Empty;

    [ObservableProperty]
    private Brush _stateBrush = Brushes.Gray;

    [ObservableProperty]
    private string _connectorSummary = "—";

    [ObservableProperty]
    private string _alertSummary = "—";

    [ObservableProperty]
    private string _versionSummary = string.Empty;

    [ObservableProperty]
    private string _lastPolled = "never";

    public TrayFlyoutViewModel(AppServices services, Action openDashboard, Action exit)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _openDashboard = openDashboard ?? throw new ArgumentNullException(nameof(openDashboard));
        _exit = exit ?? throw new ArgumentNullException(nameof(exit));

        _services.Monitor.StateChanged += OnStateChanged;
        Apply(_services.Monitor.Current);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _services.Monitor.StateChanged -= OnStateChanged;
    }

    [RelayCommand]
    private void OpenDashboard() => _openDashboard();

    [RelayCommand]
    private void Exit() => _exit();

    private void OnStateChanged(object? sender, GatewaySnapshotEventArgs e) => Apply(e.Snapshot);

    private void Apply(GatewaySnapshot snapshot)
    {
        StateLabel = snapshot.StateLabel;
        StateDetail = snapshot.Detail;
        StateBrush = new SolidColorBrush(ShieldIconFactory.ColorFor(ShieldIconFactory.StateFor(snapshot)));
        ConnectorSummary = snapshot.ConnectorSummary;
        VersionSummary = string.IsNullOrWhiteSpace(snapshot.BinaryVersion)
            ? $"127.0.0.1:{snapshot.ApiPort}"
            : $"DefenseClaw {snapshot.BinaryVersion} · 127.0.0.1:{snapshot.ApiPort}";

        AlertSummary = snapshot.AlertsUnavailable is { Length: > 0 } reason
            ? reason
            : $"{snapshot.AlertCount} in the last {GatewayMonitor.AlertLimit}" +
              (snapshot.CriticalAlertCount > 0 ? $" · {snapshot.CriticalAlertCount} CRITICAL" : string.Empty);

        LastPolled = snapshot.PolledAt == DateTimeOffset.MinValue
            ? "never"
            : snapshot.PolledAt.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.CurrentCulture);
    }
}
