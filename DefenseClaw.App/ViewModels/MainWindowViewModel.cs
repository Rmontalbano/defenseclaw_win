using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// Shell view-model. Owns the status line and the banner region, and nothing else — panel
/// state belongs to panel view-models.
/// <para>
/// Every property here is derived from one <see cref="GatewaySnapshot"/>, pushed by
/// <see cref="GatewayMonitor.StateChanged"/>. There is no second source of gateway truth
/// in the UI.
/// </para>
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly AppServices _services;
    private bool _disposed;

    [ObservableProperty]
    private GatewaySnapshot _snapshot = GatewaySnapshot.Initial;

    [ObservableProperty]
    private string _stateLabel = "Checking…";

    [ObservableProperty]
    private string _stateDetail = "Waiting for the first gateway poll…";

    [ObservableProperty]
    private Brush _stateBrush = Brushes.Gray;

    [ObservableProperty]
    private string _connectorSummary = "—";

    [ObservableProperty]
    private string _alertSummary = "Alerts: —";

    [ObservableProperty]
    private string _versionSummary = string.Empty;

    [ObservableProperty]
    private bool _showDegradedBanner;

    [ObservableProperty]
    private string _degradedTitle = "Degraded mode";

    [ObservableProperty]
    private string _degradedMessage = string.Empty;

    [ObservableProperty]
    private bool _showWslBanner;

    [ObservableProperty]
    private string _wslMessage = string.Empty;

    [ObservableProperty]
    private bool _showNotInitializedBanner;

    [ObservableProperty]
    private string _notInitializedMessage = string.Empty;

    [ObservableProperty]
    private bool _showConfigErrorBanner;

    [ObservableProperty]
    private string _configErrorMessage = string.Empty;

    public MainWindowViewModel(AppServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _services.Monitor.StateChanged += OnStateChanged;
        _services.ConfigReloaded += OnConfigReloaded;

        Apply(_services.Monitor.Current);
        ApplyConfigError();
    }

    /// <summary>The command the not-initialized banner offers. Shown, never executed.</summary>
    public static string InitCommandText => GatewaySnapshot.InitCommand;

    /// <summary>The command the degraded banner offers. Shown, never executed.</summary>
    public static string StartGatewayCommandText => GatewaySnapshot.StartGatewayCommand;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _services.Monitor.StateChanged -= OnStateChanged;
        _services.ConfigReloaded -= OnConfigReloaded;
    }

    /// <summary>
    /// Copies a command to the clipboard so the operator can paste it into a terminal.
    /// The shell deliberately does not run DefenseClaw commands from a banner.
    /// </summary>
    [RelayCommand]
    private static void CopyCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return;
        }

        try
        {
            Clipboard.SetText(command);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Another process owns the clipboard; nothing useful to do about it.
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        _ = await _services.Monitor.RefreshAsync().ConfigureAwait(true);
    }

    private void OnStateChanged(object? sender, GatewaySnapshotEventArgs e) => Apply(e.Snapshot);

    private void OnConfigReloaded(object? sender, EventArgs e) => ApplyConfigError();

    private void Apply(GatewaySnapshot snapshot)
    {
        Snapshot = snapshot;
        StateLabel = snapshot.StateLabel;
        StateDetail = snapshot.Detail;
        StateBrush = new SolidColorBrush(ShieldIconFactory.ColorFor(ShieldIconFactory.StateFor(snapshot)));
        ConnectorSummary = snapshot.ConnectorSummary;
        VersionSummary = string.IsNullOrWhiteSpace(snapshot.BinaryVersion)
            ? string.Empty
            : $"DefenseClaw {snapshot.BinaryVersion}";

        AlertSummary = snapshot.AlertsUnavailable is { Length: > 0 } reason
            ? reason
            : $"{snapshot.AlertCount} alert{(snapshot.AlertCount == 1 ? string.Empty : "s")} in the last poll" +
              (snapshot.CriticalAlertCount > 0 ? $" · {snapshot.CriticalAlertCount} CRITICAL" : string.Empty);

        // Unreachable sidecar: SQLite reads and the log tail still work, so say what still
        // works rather than just what broke.
        ShowDegradedBanner = snapshot.IsDegraded;
        DegradedTitle = snapshot.State == AppGatewayState.NotInstalled
            ? "DefenseClaw was not found"
            : "Degraded mode — the gateway is not answering";
        DegradedMessage = snapshot.Detail +
            " The audit database and the log tail still work, so Audit, Logs and Activity stay usable.";

        ShowWslBanner = snapshot.WslGatewayDetected;
        WslMessage = snapshot.PortOwner is { } owner
            ? $"Port {snapshot.ApiPort} is held by {owner.ProcessName} (pid {owner.Pid}). That is a WSL gateway " +
              "relayed onto Windows, not the native install — everything below describes the WSL instance. " +
              "Stop the gateway inside WSL, then restart the native one."
            : $"Port {snapshot.ApiPort} is relayed from WSL, not served by the native install.";

        ShowNotInitializedBanner = snapshot.State == AppGatewayState.NotInitialized;
        NotInitializedMessage = snapshot.Detail;
    }

    private void ApplyConfigError()
    {
        ConfigErrorMessage = _services.ConfigLoadError ?? string.Empty;
        ShowConfigErrorBanner = ConfigErrorMessage.Length > 0;
    }
}
