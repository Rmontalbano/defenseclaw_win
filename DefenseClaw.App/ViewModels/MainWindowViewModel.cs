using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Updates;
using DefenseClaw.Core.Audit;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// Shell view-model. Owns the status line and the banner region, and nothing else — panel
/// state belongs to panel view-models.
/// <para>
/// Every property here is derived from one <see cref="GatewaySnapshot"/>, pushed by
/// <see cref="GatewayMonitor.StateChanged"/>. There is no second source of gateway truth
/// in the UI.
/// </para>
/// <para>
/// Not a panel, so it is exempt from the activation contract on
/// <see cref="PanelViewModelBase"/> and stays subscribed while the window is in the tray:
/// it is a few property assignments, and StateChanged now only fires on a material change,
/// so there is nothing worth pausing. Everything it shows is in
/// <see cref="GatewaySnapshot.RendersSameAs"/>'s compared set; nothing here prints uptime or
/// a poll time. (<c>Apply</c> does test <see cref="GatewaySnapshot.PolledAt"/> against its
/// unset value, but only to recognise the <see cref="GatewaySnapshot.Initial"/> snapshot, and
/// the first real poll is always published, so that test can never go stale.)
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

    /// <summary>
    /// Design-system tone key for the state pill and its dot (<c>Ok / Warn / Bad / Neutral</c>).
    /// A string, not a brush: <c>DcBadge</c> / <c>DcToneDot</c> resolve it against WPF-UI theme
    /// brushes, so the strip follows a live light/dark switch — a frozen C#-built brush could not.
    /// </summary>
    [ObservableProperty]
    private string _stateTone = "Neutral";

    /// <summary>Screen-reader name of the state pill ("Gateway status: Running"); the detail sentence next to it is read on its own.</summary>
    [ObservableProperty]
    private string _stateAutomationName = "Gateway status: checking";

    [ObservableProperty]
    private string _connectorSummary = "—";

    /// <summary>The connector chip text ("Connector: claudecode").</summary>
    [ObservableProperty]
    private string _connectorChipText = "No connector";

    [ObservableProperty]
    private string _alertSummary = "Alerts: —";

    /// <summary><c>Critical</c> when the last alert list held a CRITICAL, else <c>Neutral</c>.</summary>
    [ObservableProperty]
    private string _alertTone = "Neutral";

    /// <summary>The alert chip's tooltip / screen-reader sentence (also the reason when unavailable).</summary>
    [ObservableProperty]
    private string _alertDetail = "Alerts have not been polled yet.";

    [ObservableProperty]
    private string _versionSummary = string.Empty;

    /// <summary>False until the gateway has reported a version; hides the version chip.</summary>
    [ObservableProperty]
    private bool _hasVersion;

    /// <summary>The command palette overlay is showing. Set by the window, which also manages focus.</summary>
    [ObservableProperty]
    private bool _isPaletteOpen;

    /// <summary>The keyboard-shortcuts overlay is showing. Set by the window, which also manages focus.</summary>
    [ObservableProperty]
    private bool _isShortcutsOpen;

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

    /// <summary>The gateway answered 401 to the token the app sent (CUST-213): the token was probably rotated. Cleared by the first poll that is not rejected.</summary>
    [ObservableProperty]
    private bool _showTokenBanner;

    /// <summary>The token banner's "Reload config": re-reads config.yaml and the .env (where the rotated token lives), then polls again. Read-only.</summary>
    [RelayCommand]
    private void ReloadConfig()
    {
        _services.ReloadConfig();
        _ = _services.Monitor.RefreshAsync();
    }

    [ObservableProperty]
    private string _configErrorMessage = string.Empty;

    // ---- Sidebar badges (CUST-201): the same count the tray tooltip shows, and the gateway's own state. ----

    /// <summary>
    /// The red count on the Alerts sidebar entry: the unacknowledged findings ("441", "500+" when the queue window was full),
    /// empty while there is nothing to show: no badge for zero, or for a count that has not been read yet. A later read that
    /// fails keeps the last good number (<see cref="AlertCountsService"/> never zeroes a badge, which would read as "all clear").
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAlertBadge))]
    private string _alertBadgeText = string.Empty;

    /// <summary>"441 unacknowledged findings", appended to "Alerts" for a screen reader and the tooltip; empty with no badge.</summary>
    [ObservableProperty]
    private string _alertBadgeDescription = string.Empty;

    /// <summary>Why the Overview entry carries its caution badge ("gateway stopped"); empty while the gateway is not degraded.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOverviewBadge))]
    private string _overviewBadgeDescription = string.Empty;

    /// <param name="services">The composition root.</param>
    /// <param name="reviewUpdate">What the update banner's "Review update…" does; opens the Updates window when null. A test passes a recorder.</param>
    /// <param name="openReleasePage">Opens an already-vetted release page; the shell's default handler when null. A test passes a recorder.</param>
    public MainWindowViewModel(AppServices services, Action? reviewUpdate = null, Action<string>? openReleasePage = null)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _reviewUpdate = reviewUpdate ?? (() => _ = Views.Updates.UpdatesWindow.Show(_services));
        _openReleasePage = openReleasePage;
        _services.Monitor.StateChanged += OnStateChanged;
        _services.ConfigReloaded += OnConfigReloaded;

        // Subscribing starts the counts service if nothing else has (the tray normally has), and a window built later than the
        // first read never hears about it: so the current counts are taken now, and the subscription only carries changes.
        _services.AlertCounts.Changed += OnAlertCountsChanged;
        ApplyAlertCounts(_services.AlertCounts.HasData ? _services.AlertCounts.Current : null);

        Apply(_services.Monitor.Current);
        ApplyConfigError();

        _services.UpdateWatcher.Changed += OnUpdateWatcherChanged;
        ApplyUpdateBanner();
    }

    /// <summary>True when the Alerts entry shows a count.</summary>
    public bool HasAlertBadge => AlertBadgeText.Length > 0;

    /// <summary>True when the Overview entry shows its caution badge (the gateway is stopped, degraded or not installed).</summary>
    public bool HasOverviewBadge => OverviewBadgeDescription.Length > 0;

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
        _services.UpdateWatcher.Changed -= OnUpdateWatcherChanged;
        _services.AlertCounts.Changed -= OnAlertCountsChanged;
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

    /// <summary>
    /// Shows a panel, telling it <paramref name="payload"/> if there is one: what a status-strip chip or a banner does when
    /// clicked (<c>RequestNavigation("alerts", new AlertsFilter(AuditSeverity.Critical))</c>). See <see cref="ShellNavigation"/>.
    /// </summary>
    public void RequestNavigation(string panelId, object? payload = null) => _services.Navigation.Request(panelId, payload);

    /// <summary>The same without a payload, for a XAML binding: <c>Command="{Binding OpenPanelCommand}" CommandParameter="alerts"</c>.</summary>
    [RelayCommand]
    private void OpenPanel(string? panelId)
    {
        if (!string.IsNullOrWhiteSpace(panelId))
        {
            RequestNavigation(panelId);
        }
    }

    /// <summary>Opens the first-run guided setup (the not-initialized banner's button). It runs nothing by itself.</summary>
    [RelayCommand]
    private void OpenFirstRun() => Views.FirstRun.FirstRunWindow.Show(_services);

    [RelayCommand]
    private async Task RefreshAsync()
    {
        _ = await _services.Monitor.RefreshAsync().ConfigureAwait(true);
    }

    private void OnStateChanged(object? sender, GatewaySnapshotEventArgs e) => Apply(e.Snapshot);

    private void OnAlertCountsChanged(object? sender, AlertCountsChangedEventArgs e) => ApplyAlertCounts(e.Counts);

    /// <summary>
    /// Sets the Alerts badge from <paramref name="counts"/> (null: not read yet). A read that failed keeps the last good counts
    /// (<see cref="AlertCountsService"/>), so the badge keeps saying what was last known rather than going blank.
    /// </summary>
    private void ApplyAlertCounts(AlertCounts? counts)
    {
        AlertBadgeText = counts is null ? string.Empty : AlertCountPresentation.Badge(counts);
        AlertBadgeDescription = counts is null || counts.Total <= 0 ? string.Empty : AlertCountPresentation.Sentence(counts);
    }

    /// <summary>
    /// AppServices marshals <c>ConfigReloaded</c> onto the Dispatcher, so the banner
    /// properties below are set on the UI thread even though the edit was spotted by the
    /// config watcher on a background one.
    /// </summary>
    private void OnConfigReloaded(object? sender, EventArgs e) => ApplyConfigError();

    internal void Apply(GatewaySnapshot snapshot)
    {
        Snapshot = snapshot;
        StateLabel = snapshot.StateLabel;
        StateDetail = snapshot.Detail;
        StateTone = GatewayPresentation.StateTone(snapshot);
        StateAutomationName = $"Gateway status: {snapshot.StateLabel}";
        ConnectorSummary = snapshot.ConnectorSummary;
        ConnectorChipText = GatewayPresentation.ConnectorText(snapshot);
        VersionSummary = GatewayPresentation.VersionText(snapshot);
        HasVersion = VersionSummary.Length > 0;

        // The Initial snapshot carries no alert answer and no "unavailable" reason, so without
        // the PolledAt test inside GatewayPresentation it would render as a confident "0 alerts"
        // before any poll has happened.
        AlertSummary = GatewayPresentation.AlertText(snapshot);
        AlertTone = GatewayPresentation.AlertTone(snapshot);
        AlertDetail = GatewayPresentation.AlertDetail(snapshot);

        // The banner covers three different situations, and each gets its own words: the
        // sidecar is not answering, it answered but not cleanly (IsDegraded includes both), or
        // there is no install at all. SQLite reads and the log tail still work when there is
        // one, so say what still works rather than just what broke - but not when nothing is
        // installed, where there is nothing to say still works.
        ShowDegradedBanner = snapshot.IsDegraded;
        DegradedTitle = snapshot.State switch
        {
            AppGatewayState.NotInstalled => "DefenseClaw was not found",
            AppGatewayState.Degraded => "Degraded mode — the gateway answered, but not cleanly",
            _ => "Degraded mode — the gateway is not answering",
        };
        DegradedMessage = snapshot.State == AppGatewayState.NotInstalled
            ? snapshot.Detail
            : snapshot.Detail +
              " The audit database and the log tail still work, so Audit, Logs and Activity stay usable.";

        ShowWslBanner = snapshot.WslGatewayDetected;
        WslMessage = snapshot.PortOwner is { } owner
            ? $"Port {snapshot.ApiPort} is held by {owner.ProcessName} (pid {owner.Pid}). That is a WSL gateway " +
              "relayed onto Windows, not the native install — everything below describes the WSL instance. " +
              "Stop the gateway inside WSL, then restart the native one."
            : $"Port {snapshot.ApiPort} is relayed from WSL, not served by the native install.";

        ShowTokenBanner = snapshot.IsTokenRejected;

        ShowNotInitializedBanner = snapshot.State == AppGatewayState.NotInitialized;
        NotInitializedMessage = snapshot.Detail;

        OverviewBadgeDescription = AlertCountPresentation.DegradedReason(snapshot) ?? string.Empty;
    }

    private void ApplyConfigError()
    {
        ConfigErrorMessage = _services.ConfigLoadError ?? string.Empty;
        ShowConfigErrorBanner = ConfigErrorMessage.Length > 0;
    }

    // ---- Update banner (CUST-206) -------------------------------------------------------------------------------
    // The state of the "DefenseClaw X is available" banner, all of it derived from UpdateWatcher. The banner offers to review the
    // update (the existing Updates window: nothing is installed from here), to read the release notes and to stop being told about
    // this release; it never runs anything itself.

    private readonly Action _reviewUpdate;
    private readonly Action<string>? _openReleasePage;

    /// <summary>A newer release is out and the operator has not dismissed it: the banner shows.</summary>
    [ObservableProperty]
    private bool _showUpdateBanner;

    /// <summary>"DefenseClaw 0.8.11 is available".</summary>
    [ObservableProperty]
    private string _updateBannerTitle = string.Empty;

    /// <summary>The line under the title: what is installed, and that the upgrade is reviewed and confirmed in the Updates window, not here.</summary>
    [ObservableProperty]
    private string _updateBannerMessage = string.Empty;

    /// <summary>The release has a page that passed the link check, so "Release notes" can open it.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenReleaseNotesCommand))]
    private bool _hasUpdateReleaseNotes;

    /// <summary>Opens the Updates window, where the upgrade is reviewed, verified and confirmed.</summary>
    [RelayCommand]
    private void ReviewUpdate() => _reviewUpdate();

    /// <summary>Opens the release page, and only if it is an https link into the DefenseClaw repository (<see cref="UpdateChecker.TrustedRepoUrl"/>).</summary>
    [RelayCommand(CanExecute = nameof(HasUpdateReleaseNotes))]
    private void OpenReleaseNotes() => _ = UpdateWatcher.TryOpenReleasePage(_services.UpdateWatcher.ReleaseUrl, _openReleasePage);

    /// <summary>Hides the banner for this release, for good; the next release shows it again.</summary>
    [RelayCommand]
    private void DismissUpdate() => _services.UpdateWatcher.Dismiss();

    /// <summary>UpdateWatcher raises <c>Changed</c> on the UI thread, so the properties below are set where the bindings live.</summary>
    private void OnUpdateWatcherChanged(object? sender, EventArgs e) => ApplyUpdateBanner();

    private void ApplyUpdateBanner()
    {
        var watcher = _services.UpdateWatcher;
        if (!watcher.ShowBanner || watcher.AvailableVersion is not { } version)
        {
            ShowUpdateBanner = false;
            HasUpdateReleaseNotes = false;
            return;
        }

        UpdateBannerTitle = UpdateWatcher.Announcement(version);
        UpdateBannerMessage = (watcher.InstalledVersion is { Length: > 0 } installed ? $"Installed: {installed}. " : string.Empty) +
                              "You review and confirm the upgrade in the Updates window.";
        HasUpdateReleaseNotes = watcher.ReleaseUrl is not null;
        ShowUpdateBanner = true;
    }
}
