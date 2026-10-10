using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Settings;
using DefenseClaw.App.Services.Updates;
using DefenseClaw.Core.Config;
using DefenseClaw.Core.Paths;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// Settings (CUST-203): the app's own preferences on one page, after the Mac's four-tab settings window — monitoring, notifications, startup,
/// connection, updates — every one of them a read or a write of <see cref="AppServices.Settings"/> (the app's <c>settings.json</c>), or a
/// read-only look at what the app resolved. <b>It never writes DefenseClaw's state</b>: no config.yaml, no .env, no CLI command. The one
/// thing that reads config.yaml is "Reload config.yaml now", which only re-reads it.
/// <para>
/// <b>Commit on change.</b> A switch writes the moment it flips. The health-interval slider writes once it has been still for
/// <see cref="IntervalCommitDelay"/> (the store writes synchronously, so a drag must not write per tick) and when the page goes away, and the
/// monitor follows the new value live (<see cref="GatewayMonitor.HealthInterval"/>). A write the file refused is said at the top of the page; the
/// change still holds for this session.
/// </para>
/// <para>
/// <b>Other writers.</b> The tray flyout pauses monitoring and the window's own close button reads <c>startup.closeToTray</c>, so while the page is
/// on screen it follows <see cref="AppSettingsStore.Changed"/>; what it shows is always the store's value, never its own copy.
/// </para>
/// <para>
/// <b>Secrets.</b> The token is shown as "configured (hidden)" or "not found", and which rung supplied it; the value is never read into this
/// view-model's state, let alone displayed, copied or logged.
/// </para>
/// </summary>
public sealed partial class SettingsPanelViewModel : PanelViewModelBase
{
    /// <summary>How long the health-interval slider must be still before its value is saved (and so applied): a drag is one write, not sixty.</summary>
    internal static readonly TimeSpan IntervalCommitDelay = TimeSpan.FromMilliseconds(400);

    private const string SaveProblemText =
        "Could not save to the settings file (it is locked or unreadable). The change holds until DefenseClaw closes.";

    private readonly ShellHooks? _hooks;
    private readonly SettingsPlatform _platform;

    /// <summary>Above zero while the page is copying the store's values into its own properties, so those assignments are not written back.</summary>
    private int _syncing;

    private DispatcherTimer? _intervalTimer;

    public SettingsPanelViewModel(AppServices services)
        : this(services, hooks: null, platform: null)
    {
    }

    /// <param name="services">The composition the page reads and the settings store it writes.</param>
    /// <param name="hooks">What the page may ask of the tray (see <see cref="ShellHooks"/>); null when there is none.</param>
    /// <param name="platform">The registry, dialogs, clipboard and Explorer; the real ones when null. A test passes its own.</param>
    internal SettingsPanelViewModel(AppServices services, ShellHooks? hooks, SettingsPlatform? platform = null)
        : base(services)
    {
        _hooks = hooks;
        _platform = platform ?? SettingsPlatform.Real;

        Files = new ObservableCollection<SettingsPathRow>(BuildFiles());
        _selectedRuntimeKind = RuntimeKinds[0];

        // Everything the constructor shows is already in memory (the settings cache, the resolved config and token, the last snapshots); what
        // touches the registry or the disk waits for the page to come on screen (OnActivated).
        LoadFromSettings();
        ShowGateway();
        ShowUpdates();
        ShowRuntimeIdentity();
        ShowInstallation();
    }

    public override string Title => "Settings";

    public override string Description =>
        "How this app behaves: monitoring, notifications, startup, connection and updates. These are the app's own settings; changing them never changes DefenseClaw's configuration.";

    // ------------------------------------------------------------------ the page

    /// <summary>Set when the settings file could not be written; the page says so at the top. Null otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSaveProblem))]
    private string? _saveProblem;

    public bool HasSaveProblem => SaveProblem is not null;

    // ------------------------------------------------------------------ monitoring

    public double HealthIntervalMinimum => MonitoringSettings.MinHealthIntervalSeconds;

    public double HealthIntervalMaximum => MonitoringSettings.MaxHealthIntervalSeconds;

    /// <summary>The slider's value, in seconds. Whole seconds: the slider snaps, and a value set in code is rounded when it is saved.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HealthIntervalText))]
    private double _healthIntervalSeconds = MonitoringSettings.DefaultHealthIntervalSeconds;

    /// <summary>"5 s".</summary>
    public string HealthIntervalText =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)Math.Round(HealthIntervalSeconds)} s");

    /// <summary>The Pause monitoring switch: the same flag the tray flyout's Pause / Resume button and the tray menu flip (<see cref="GatewayMonitor.IsPaused"/>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PauseCaption))]
    private bool _isMonitoringPaused;

    public string PauseCaption => IsMonitoringPaused
        ? "Monitoring is paused: nothing polls the gateway or reads the audit database until you turn this off. The gateway itself keeps running."
        : "Stops this app from polling the gateway and reading the audit database until you resume. The gateway itself keeps running. The tray flyout has the same switch.";

    partial void OnHealthIntervalSecondsChanged(double value)
    {
        if (_syncing == 0)
        {
            ScheduleIntervalCommit();
        }
    }

    partial void OnIsMonitoringPausedChanged(bool value)
    {
        if (_syncing > 0)
        {
            return;
        }

        // The monitor owns the flag (it persists it, publishes the paused snapshot and wakes or parks its loop); this only asks.
        SaveProblem = Services.Monitor.SetPaused(value) ? null : SaveProblemText;
    }

    private void ScheduleIntervalCommit()
    {
        if (_intervalTimer is null)
        {
            _intervalTimer = new DispatcherTimer { Interval = IntervalCommitDelay };
            _intervalTimer.Tick += (_, _) => CommitHealthInterval();
        }

        // Restarted on every change: the value is saved once the slider has been still for the delay.
        _intervalTimer.Stop();
        _intervalTimer.Start();
    }

    /// <summary>Saves the slider's value now, if it differs from the saved one. The timer's tick, the page leaving the screen, and tests.</summary>
    internal void CommitHealthInterval()
    {
        _intervalTimer?.Stop();

        var seconds = (int)Math.Clamp(
            Math.Round(HealthIntervalSeconds),
            MonitoringSettings.MinHealthIntervalSeconds,
            MonitoringSettings.MaxHealthIntervalSeconds);

        if (Services.Settings.Current.Monitoring.HealthIntervalSeconds != seconds)
        {
            Write(settings => settings with { Monitoring = settings.Monitoring with { HealthIntervalSeconds = seconds } });
        }
    }

    // ------------------------------------------------------------------ notifications

    [ObservableProperty]
    private bool _notifyCritical = true;

    [ObservableProperty]
    private bool _notifyHigh = true;

    [ObservableProperty]
    private bool _notifyGateway = true;

    /// <summary>The Mac's note, word for word: what a toast carries.</summary>
    public string NotificationsNote => "Notifications include target and severity only — never prompt or payload contents.";

    /// <summary>What "Reset seen-alert history" last did, in a sentence; empty until it has.</summary>
    [ObservableProperty]
    private string _resetSeenMessage = string.Empty;

    partial void OnNotifyCriticalChanged(bool value) =>
        Write(settings => settings with { Notifications = settings.Notifications with { Critical = value } });

    partial void OnNotifyHighChanged(bool value) =>
        Write(settings => settings with { Notifications = settings.Notifications with { High = value } });

    partial void OnNotifyGatewayChanged(bool value) =>
        Write(settings => settings with { Notifications = settings.Notifications with { Gateway = value } });

    /// <summary>
    /// "Reset seen-alert history": the tray forgets which findings it has announced, so what is still unacknowledged is announced once more. The
    /// same call the command palette's entry makes (<see cref="TrayIconService.ResetSeenAlertHistoryAsync"/>), reached through <see cref="ShellHooks"/>.
    /// </summary>
    [RelayCommand]
    private async Task ResetSeenAlertsAsync()
    {
        if (_hooks?.ResetSeenAlertHistory is not { } reset)
        {
            ResetSeenMessage = "Not available: the tray is not running in this session.";
            return;
        }

        try
        {
            await reset().ConfigureAwait(true);
            ResetSeenMessage = "Seen-alert history reset. Findings that are still unacknowledged will be announced again.";
        }
#pragma warning disable CA1031 // A reset that fails is said on the page; it must not reach the dispatcher's last-resort handler.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Trace.TraceWarning($"settings: resetting the seen-alert history failed: {ex.GetType().Name}: {ex.Message}");
            ResetSeenMessage = $"Could not reset the history: {ex.Message}";
        }
    }

    // ------------------------------------------------------------------ startup

    /// <summary>Start with Windows: whether the per-user Run entry is there and enabled (<see cref="AutostartManager"/>), re-read whenever the page comes on screen.</summary>
    [ObservableProperty]
    private bool _startWithWindows;

    /// <summary>Why the Run entry could not be changed (a policy-locked key); null when the last change went through.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAutostartProblem))]
    private string? _autostartProblem;

    public bool HasAutostartProblem => AutostartProblem is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CloseToTrayCaption))]
    private bool _closeToTray = true;

    public string CloseToTrayCaption => CloseToTray
        ? "Closing the dashboard hides it; DefenseClaw keeps watching from the tray. Choose Exit from the tray menu or flyout to quit."
        : "Closing the dashboard exits DefenseClaw, after asking about an upgrade that is still running or config edits you have not saved.";

    [ObservableProperty]
    private bool _rememberLastPanel;

    /// <summary>The "Start the gateway automatically" switch (<c>startup.autoStartGateway</c>, off by default). Turning it on shows a one-time review first.</summary>
    [ObservableProperty]
    private bool _autoStartGateway;

    partial void OnAutoStartGatewayChanged(bool value)
    {
        if (_syncing > 0)
        {
            return;
        }

        // A start is a change, so a managed or invalid installation cannot be told to make one on every launch (the switch is disabled too).
        if (value && !CanChangeAutoStart)
        {
            Sync(() => AutoStartGateway = false);
            return;
        }

        if (value && !_platform.ConfirmGatewayAutoStart(GatewayAutoStart.ConsentReview()))
        {
            // Declined: it stays off, and the switch says so.
            Sync(() => AutoStartGateway = false);
            return;
        }

        Write(settings => settings with { Startup = settings.Startup with { GatewayAutoStart = value } });
    }

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (_syncing == 0)
        {
            ApplyAutostart(value);
        }
    }

    partial void OnCloseToTrayChanged(bool value) =>
        Write(settings => settings with { Startup = settings.Startup with { CloseToTray = value } });

    partial void OnRememberLastPanelChanged(bool value) =>
        Write(settings => settings with { Startup = settings.Startup with { RememberLastPanel = value } });

    /// <summary>
    /// Makes Start with Windows <paramref name="wanted"/>, through <see cref="AutostartManager"/>'s guarded toggle (the tray menu's own path). Nothing
    /// is flipped when it already is; a registry that refuses leaves the switch where the registry says it is and the reason on the page.
    /// </summary>
    private void ApplyAutostart(bool wanted)
    {
        if (_platform.IsAutostartEnabled() == wanted)
        {
            AutostartProblem = null;
            return;
        }

        var result = _platform.ToggleAutostart();
        AutostartProblem = result.FailureMessage;
        Sync(() => StartWithWindows = result.Enabled);
    }

    // ------------------------------------------------------------------ connection: the gateway

    /// <summary><c>http://127.0.0.1:18970</c>: where the app talks to the gateway (the port is config.yaml's <c>gateway.api_port</c>).</summary>
    [ObservableProperty]
    private string _endpointText = string.Empty;

    /// <summary>"configured (hidden)" or "not found". Never the token.</summary>
    [ObservableProperty]
    private string _tokenStatus = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TokenTone))]
    private bool _tokenFound;

    /// <summary>The token chip's tone: Ok while a token is found, High when it is not (the gateway's authenticated endpoints will refuse the app).</summary>
    public string TokenTone => TokenFound ? "Ok" : "High";

    /// <summary>Which rung of the ladder answered, or what was missing: names the variable, never the value.</summary>
    [ObservableProperty]
    private string _tokenDetail = string.Empty;

    /// <summary>The outcome of the last "Reload config.yaml now" (and of copy and open actions on this card); empty until there is one.</summary>
    [ObservableProperty]
    private string _connectionMessage = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReloadConfigCommand))]
    private bool _isReloading;

    /// <summary>Config file, data directory, .env, audit database, gateway log, Python runtime: where the app reads, each with its Copy and Open-folder.</summary>
    public ObservableCollection<SettingsPathRow> Files { get; }

    private IEnumerable<SettingsPathRow> BuildFiles()
    {
        var paths = Services.Paths;
        yield return NewRow("Config file", paths.ConfigFilePath, isDirectory: false, note: null);
        yield return NewRow("Data directory", paths.DataDirectory, isDirectory: true, note: paths.DataDirectoryOrigin.Description);
        yield return NewRow(".env file", paths.EnvFilePath, isDirectory: false, note: null);
        yield return NewRow("Audit database", paths.AuditDatabasePath, isDirectory: false, note: null);
        yield return NewRow("Gateway log", paths.GatewayLogPath, isDirectory: false, note: null);

        // The interpreter the installed DefenseClaw runs (CUST-329, SG15): the Setup layout's runtime, or the installer script's venv.
        var python = paths.PythonInterpreterPath;
        yield return NewRow(
            "Python runtime",
            python,
            isDirectory: false,
            note: string.Equals(python, paths.VenvPythonPath, StringComparison.OrdinalIgnoreCase)
                ? "From the installer script's .venv under the data directory."
                : "From the Setup install's runtime folder.");
    }

    private SettingsPathRow NewRow(string label, string path, bool isDirectory, string? note)
    {
        SettingsPathRow? row = null;
        row = new SettingsPathRow(
            label,
            path,
            isDirectory,
            note,
            new RelayCommand(() => CopyText(row!.FullPath, $"Copied the {label} path.")),
            new AsyncRelayCommand(() => OpenFolderAsync(row!)));
        return row;
    }

    /// <summary>The endpoint and the token's standing, from what the app resolved (no I/O: the resolved config and token are in memory).</summary>
    private void ShowGateway()
    {
        EndpointText = "http://127.0.0.1:" + Services.ApiPort.ToString(CultureInfo.InvariantCulture);

        // The resolution never carries the plaintext; its source and variable name are what is said.
        var token = Services.Token;
        TokenFound = token.Found;
        TokenStatus = token.Found ? "configured (hidden)" : "not found";
        TokenDetail = token.Source switch
        {
            TokenSource.Environment => $"Read from the {token.VariableName} environment variable.",
            TokenSource.DotEnvFile => $"Read from {token.VariableName} in the .env file.",
            TokenSource.ConfigLiteral => "Read from gateway.token in config.yaml.",
            _ => $"No token found for {token.VariableName} in the environment, the .env file or config.yaml; requests the gateway authenticates will be refused.",
        };

        if (token.Note is { Length: > 0 } note)
        {
            TokenDetail += " " + note;
        }
    }

    /// <summary>
    /// "Reload config.yaml now": re-reads config.yaml and the .env file (<see cref="AppServices.ReloadConfig"/>: the watcher does the same when either
    /// changes, this is for when it missed). Read only. Off the UI thread, as that method asks: it parses YAML.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanReloadConfig))]
    private async Task ReloadConfigAsync()
    {
        IsReloading = true;
        try
        {
            await Task.Run(Services.ReloadConfig).ConfigureAwait(true);
            ShowGateway();

            ConnectionMessage = Services.ConfigLoadError is { Length: > 0 } error
                ? $"config.yaml could not be applied; the last good configuration is still in use. {error}"
                : "config.yaml and .env re-read.";
        }
#pragma warning disable CA1031 // ReloadConfig does not throw; should it, the page says so rather than fault the dispatcher.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ConnectionMessage = $"Could not reload config.yaml: {ex.Message}";
        }
        finally
        {
            IsReloading = false;
        }
    }

    private bool CanReloadConfig() => !IsReloading;

    private void CopyText(string text, string done)
    {
        try
        {
            _platform.CopyText(text);
            ConnectionMessage = done;
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Another program holds the clipboard: say so rather than pretend.
            ConnectionMessage = "Could not copy: another program is holding the clipboard. Try again.";
        }
    }

    /// <summary>
    /// Opens the row's folder in Explorer: the directory itself for the data directory; for a file, the file selected in its folder, or the folder when
    /// the file is not there (yet). Decided off the UI thread (a path under a network home can be slow to answer).
    /// </summary>
    private async Task OpenFolderAsync(SettingsPathRow row)
    {
        var (target, select) = await Task.Run(() => ResolveRevealTarget(row.FullPath, row.IsDirectory)).ConfigureAwait(true);
        if (target is null)
        {
            ConnectionMessage = $"Could not open the {row.Label}'s folder: it does not exist.";
            return;
        }

        ConnectionMessage = _platform.Reveal(target, select)
            ? $"Opened the {row.Label}'s folder in Explorer."
            : "Could not start Explorer.";
    }

    /// <summary>What to hand Explorer for <paramref name="path"/>: the file to select, else the folder to open; null when neither exists.</summary>
    internal static (string? Target, bool SelectFile) ResolveRevealTarget(string path, bool isDirectory)
    {
        if (isDirectory)
        {
            return Directory.Exists(path) ? (path, false) : (null, false);
        }

        if (File.Exists(path))
        {
            return (path, true);
        }

        var parent = Path.GetDirectoryName(path);
        return parent is { Length: > 0 } && Directory.Exists(parent) ? (parent, false) : (null, false);
    }

    // ------------------------------------------------------------------ connection: the CLI

    /// <summary>The <c>defenseclaw.exe</c> the app will run (the override, else PATH, else the install directory), or the sentence saying none was found.</summary>
    [ObservableProperty]
    private string _cliResolvedPath = "Looking it up…";

    [ObservableProperty]
    private bool _cliFound;

    /// <summary>Where the resolved path came from, in a sentence.</summary>
    [ObservableProperty]
    private string _cliSourceNote = string.Empty;

    /// <summary>The operator's choice, or null for the automatic lookup.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCliOverride))]
    [NotifyPropertyChangedFor(nameof(CliOverrideDisplay))]
    [NotifyCanExecuteChangedFor(nameof(ClearCliOverrideCommand))]
    private string? _cliOverridePath;

    public bool HasCliOverride => CliOverridePath is { Length: > 0 };

    public string CliOverrideDisplay => CliOverridePath is { Length: > 0 } path ? path : "Automatic: PATH, then the install directory";

    /// <summary>Why the last file the operator picked was refused (nothing is saved then); null otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCliOverrideProblem))]
    private string? _cliOverrideProblem;

    public bool HasCliOverrideProblem => CliOverrideProblem is not null;

    /// <summary>Asks Windows where <c>defenseclaw.exe</c> is, off the UI thread, and shows the answer (<see cref="DefenseClawPaths.FindExecutableAsync"/>).</summary>
    private async Task RefreshCliAsync()
    {
        var paths = Services.Paths;
        var found = await paths.FindExecutableAsync(DefenseClawPaths.CliExecutableName).ConfigureAwait(true);
        var pinned = paths.CliPathOverride;

        CliFound = found is not null;
        CliResolvedPath = found ?? "Not found (looked on PATH and in the install directory)";

        if (found is null)
        {
            CliSourceNote = pinned is null
                ? "Install DefenseClaw, or choose defenseclaw.exe below."
                : "The file you chose is missing and nothing else was found.";
        }
        else if (pinned is not null && string.Equals(found, pinned, StringComparison.OrdinalIgnoreCase))
        {
            CliSourceNote = "The file you chose. Every command the app runs, and every review it shows, uses it.";
        }
        else if (pinned is not null)
        {
            CliSourceNote = "The file you chose is missing, so the automatic lookup is used instead.";
        }
        else if (IsUnder(found, paths.BinDirectory))
        {
            CliSourceNote = "Found in the install directory.";
        }
        else
        {
            CliSourceNote = "Found on PATH.";
        }
    }

    private static bool IsUnder(string file, string directory) =>
        Path.GetDirectoryName(file) is { } folder &&
        string.Equals(
            Path.TrimEndingDirectorySeparator(folder),
            Path.TrimEndingDirectorySeparator(directory),
            StringComparison.OrdinalIgnoreCase);

    [RelayCommand]
    private void BrowseCliExecutable()
    {
        var start = CliOverridePath is { Length: > 0 } current ? Path.GetDirectoryName(current) : Services.Paths.BinDirectory;
        if (_platform.PickCliExecutable(start) is { } picked)
        {
            ApplyCliOverride(picked);
        }
    }

    [RelayCommand(CanExecute = nameof(HasCliOverride))]
    private void ClearCliOverride() => ApplyCliOverride(null);

    /// <summary>
    /// Validates <paramref name="path"/> (<see cref="DefenseClawPaths.CheckCliPathOverride"/>: an existing file named defenseclaw.exe) and, if it
    /// passes, saves it as <c>connection.cliPathOverride</c>; <see cref="AppServices"/> hands it to the paths the moment it changes, so the next lookup —
    /// and every command and review after it — uses it. A file that fails validation is refused, says why, and changes nothing.
    /// </summary>
    internal void ApplyCliOverride(string? path)
    {
        if (DefenseClawPaths.CheckCliPathOverride(path) is { } problem)
        {
            CliOverrideProblem = problem;
            return;
        }

        CliOverrideProblem = null;
        var chosen = string.IsNullOrWhiteSpace(path) ? null : path.Trim();
        Write(settings => settings with { Connection = settings.Connection with { CliPathOverride = chosen } });
        LoadFromSettings();
        LastCliRefresh = RefreshCliAsync();
    }

    /// <summary>The look at <c>defenseclaw.exe</c> that the last change of the override started; for a test to await.</summary>
    internal Task LastCliRefresh { get; private set; } = Task.CompletedTask;

    [RelayCommand]
    private void CopyCliPath()
    {
        if (CliFound)
        {
            CopyText(CliResolvedPath, "Copied the defenseclaw.exe path.");
        }
    }

    [RelayCommand]
    private async Task OpenCliFolderAsync()
    {
        if (!CliFound)
        {
            return;
        }

        var path = CliResolvedPath;
        var (target, select) = await Task.Run(() => ResolveRevealTarget(path, isDirectory: false)).ConfigureAwait(true);
        ConnectionMessage = target is not null && _platform.Reveal(target, select)
            ? "Showed defenseclaw.exe in Explorer."
            : "Could not open that folder.";
    }

    // ------------------------------------------------------------------ updates

    /// <summary>The installed runtime's version as the gateway or the CLI reported it ("0.8.10"), or "Not detected".</summary>
    [ObservableProperty]
    private string _runtimeVersionText = "Not detected";

    /// <summary>"Up to date", "DefenseClaw 0.8.11 is available", "Not checked yet", "Could not check".</summary>
    [ObservableProperty]
    private string _updateStatusText = "Not checked yet";

    /// <summary>The tone of the status chip: Ok, Medium (something to review), High (the check failed) or Neutral.</summary>
    [ObservableProperty]
    private string _updateStatusTone = "Neutral";

    /// <summary>A sentence of context: where the upgrade is reviewed, or why the last check failed; empty when there is nothing to add.</summary>
    [ObservableProperty]
    private string _updateDetail = string.Empty;

    /// <summary>"2026-09-30 14:32 (12m ago)", or "Never" when no check has got an answer.</summary>
    [ObservableProperty]
    private string _lastCheckedText = "Never";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckNowCommand))]
    private bool _isCheckingUpdates;

    /// <summary>
    /// Shows what the runtime version and the update watcher know, from memory (no network, no I/O): the watcher's last answer, the time of the last
    /// check that got one, and why the most recent one failed, if it did.
    /// </summary>
    private void ShowUpdates()
    {
        var snapshot = Services.Monitor.Current;
        RuntimeVersionText = string.IsNullOrWhiteSpace(snapshot.BinaryVersion)
            ? "Not detected"
            : snapshot.PeerUnverified
                ? $"{snapshot.BinaryVersion.Trim()} (unverified)"
                : snapshot.BinaryVersion.Trim();

        var watcher = Services.UpdateWatcher;
        if (watcher.AvailableVersion is { } available)
        {
            UpdateStatusText = UpdateWatcher.Announcement(available);
            UpdateStatusTone = "Medium";
            UpdateDetail = Services.Installation.UpgradeBlockedReason is { } noUpgrade
                ? "Open Updates… to read about it. " + noUpgrade
                : "Open Updates… to review it. The upgrade itself is only ever run from there, after you have seen the exact command.";
        }
        else if (watcher.Latest.State == UpdateCheckState.UpToDate)
        {
            UpdateStatusText = "Up to date";
            UpdateStatusTone = "Ok";
            UpdateDetail = string.Empty;
        }
        else
        {
            UpdateStatusText = watcher.LastCheckFailed ? "Could not check" : "Not checked yet";
            UpdateStatusTone = watcher.LastCheckFailed ? "High" : "Neutral";
            UpdateDetail = string.Empty;
        }

        if (watcher.LastFailure is { Length: > 0 } failure)
        {
            UpdateDetail = (UpdateDetail.Length > 0 ? UpdateDetail + " " : string.Empty) + $"The last check did not finish: {failure}";
        }

        var stamp = Services.Settings.Current.Updates.LastCheckUnix;
        LastCheckedText = stamp > 0 && stamp <= DateTimeOffset.MaxValue.ToUnixTimeSeconds()
            ? FormatLastChecked(DateTimeOffset.FromUnixTimeSeconds(stamp), DateTimeOffset.UtcNow)
            : "Never";
    }

    /// <summary>"2026-09-30 14:32 (12m ago)".</summary>
    internal static string FormatLastChecked(DateTimeOffset checkedAt, DateTimeOffset now) =>
        string.Create(
            CultureInfo.CurrentCulture,
            $"{checkedAt.ToLocalTime():yyyy-MM-dd HH:mm} ({TrayFlyoutText.Relative(checkedAt, now)})");

    /// <summary>
    /// "Check now": asks the update watcher — the same one behind the banner and "Check for updates" in the palette, so the banner agrees — for
    /// a live look at the newest release (skipping its 24 h cache: the operator asked), and shows the answer here. It only asks; the upgrade is
    /// the Updates window's, and is reviewed there.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCheckNow))]
    private async Task CheckNowAsync()
    {
        IsCheckingUpdates = true;
        try
        {
            _ = await Services.UpdateWatcher.CheckNowAsync(forceRefresh: true).ConfigureAwait(true);
        }
#pragma warning disable CA1031 // The watcher does not throw for a failed check; should something else, the page shows what it knows.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Trace.TraceWarning($"settings: the update check threw {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            IsCheckingUpdates = false;
            ShowUpdates();
        }
    }

    private bool CanCheckNow() => !IsCheckingUpdates;

    /// <summary>"Open Updates…": the Updates window (release, provenance and the reviewed upgrade).</summary>
    [RelayCommand]
    private void OpenUpdates() => _platform.OpenUpdates(Services);

    // ------------------------------------------------------------------ lifetime

    /// <summary>
    /// The page is on screen: follow the things that change under it (the store, config.yaml, the monitor's version, the watcher), and look at what
    /// only the machine knows — the Run entry, whether each file exists, where <c>defenseclaw.exe</c> is — off the UI thread where that is I/O.
    /// </summary>
    protected override void OnActivated()
    {
        Services.Settings.Changed += OnSettingsChanged;
        Services.ConfigReloaded += OnConfigReloaded;
        Services.Monitor.StateChanged += OnMonitorStateChanged;
        Services.UpdateWatcher.Changed += OnUpdateWatcherChanged;
        Services.Runtime.Changed += OnRuntimeChanged;

        LoadFromSettings();
        ShowGateway();
        ShowUpdates();
        ShowRuntimeIdentity();
        ShowInstallation();
        _ = RefreshMachineFactsAsync();

        // One stamp of the CLI file when nothing changed; a fresh probe after an upgrade.
        _ = Services.Runtime.RefreshAsync();
    }

    protected override void OnDeactivated()
    {
        Services.Settings.Changed -= OnSettingsChanged;
        Services.ConfigReloaded -= OnConfigReloaded;
        Services.Monitor.StateChanged -= OnMonitorStateChanged;
        Services.UpdateWatcher.Changed -= OnUpdateWatcherChanged;
        Services.Runtime.Changed -= OnRuntimeChanged;

        // A slider moved a moment before the page went away is saved, not lost with the timer.
        if (_intervalTimer is { IsEnabled: true })
        {
            CommitHealthInterval();
        }
    }

    /// <summary>
    /// F5 and the toolbar's Refresh: everything the page shows, read again — the store, config.yaml's endpoint, the update result, and the
    /// machine facts (a "Start with Windows" changed from the tray menu while the page was open shows up here). Writes nothing.
    /// </summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        LoadFromSettings();
        ShowGateway();
        ShowUpdates();
        ShowInstallation();
        await RefreshMachineFactsAsync().ConfigureAwait(true);
    }

    /// <summary>The registry, the disk and PATH. Safe to run again at any time; the answers land on the UI thread.</summary>
    internal async Task RefreshMachineFactsAsync()
    {
        Sync(() => StartWithWindows = _platform.IsAutostartEnabled());

        var rows = Files.ToArray();
        var exists = await Task.Run(() => rows.Select(row => row.IsDirectory ? Directory.Exists(row.FullPath) : File.Exists(row.FullPath)).ToArray()).ConfigureAwait(true);
        for (var i = 0; i < rows.Length; i++)
        {
            rows[i].Exists = exists[i];
        }

        await RefreshCliAsync().ConfigureAwait(true);
    }

    private void OnSettingsChanged(object? sender, AppSettingsChangedEventArgs e)
    {
        // The palette's recents and the sidebar's "seen" markers are not on this page, and a command run from the palette (or a marker the
        // sidebar moves or baselines in the background, CUST-265) while this page is open would write them: copying the store into the form
        // then would wipe what is half typed in a field that waits for its Apply (the archive path, the developer runtime).
        if ((e.Sections & ~(AppSettingsSections.Palette | AppSettingsSections.Seen)) == AppSettingsSections.None)
        {
            return;
        }

        OnUiThread(LoadFromSettings);
    }

    private void OnConfigReloaded(object? sender, EventArgs e) => ShowGateway();

    private void OnMonitorStateChanged(object? sender, GatewaySnapshotEventArgs e) => ShowUpdates();

    private void OnUpdateWatcherChanged(object? sender, EventArgs e) => OnUiThread(ShowUpdates);

    // ------------------------------------------------------------------ the store

    /// <summary>
    /// Copies the store's values into the properties the page shows, without writing them back. What the tray flyout's Pause changed, a hand edit of
    /// the file, or a save that just finished is what shows; a slider still being dragged is left alone.
    /// </summary>
    private void LoadFromSettings()
    {
        var settings = Services.Settings.Current;
        var intervalPending = _intervalTimer is { IsEnabled: true };

        Sync(() =>
        {
            if (!intervalPending)
            {
                HealthIntervalSeconds = settings.Monitoring.HealthIntervalSeconds;
            }

            IsMonitoringPaused = settings.Monitoring.Paused;
            NotifyCritical = settings.Notifications.Critical;
            NotifyHigh = settings.Notifications.High;
            NotifyGateway = settings.Notifications.Gateway;
            CloseToTray = settings.Startup.CloseToTray;
            RememberLastPanel = settings.Startup.RememberLastPanel;
            AutoStartGateway = settings.Startup.GatewayAutoStart;
            CliOverridePath = settings.Connection.CliPathOverride;
        });

        LoadDeveloperFromSettings();
        LoadArchiveFromSettings();
    }

    /// <summary>Saves a change to the settings store, unless the page is only copying the store's values in; a write the file refuses is reported.</summary>
    private void Write(Func<AppSettings, AppSettings> change)
    {
        if (_syncing > 0)
        {
            return;
        }

        SaveProblem = Services.Settings.Update(change) ? null : SaveProblemText;
    }

    private void Sync(Action assign)
    {
        _syncing++;
        try
        {
            assign();
        }
        finally
        {
            _syncing--;
        }
    }

    /// <summary>Runs <paramref name="action"/> on the UI thread: in place when already there (or when no application is running, as in a headless test).</summary>
    private static void OnUiThread(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        if (dispatcher.HasShutdownStarted)
        {
            return;
        }

        try
        {
            _ = dispatcher.BeginInvoke(action);
        }
        catch (InvalidOperationException)
        {
            // Shutdown began between the check and the post: nothing is left to update.
        }
    }
}
