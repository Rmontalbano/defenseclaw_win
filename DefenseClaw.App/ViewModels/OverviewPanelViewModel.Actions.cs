using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.ViewModels;

/// <summary>One read-only command of the Diagnostics menu: what it is called, what it runs, and what it runs on.</summary>
/// <param name="Title">The menu item, and the heading of the result line.</param>
/// <param name="Executable">What <paramref name="Argv"/> is handed to: <c>defenseclaw</c> or <c>defenseclaw-gateway</c>.</param>
/// <param name="Argv">The arguments, without the executable.</param>
/// <param name="Summary">What it does, for the tooltip.</param>
public sealed record DiagnosticCommand(string Title, string Executable, IReadOnlyList<string> Argv, string Summary)
{
    /// <summary>The whole command as a reviewer reads it.</summary>
    public string CommandText => CommandReview.CommandLine(Executable, Argv);
}

/// <summary>
/// Quick Actions (CUST-209): the Mac's row of buttons, through this app's safety model. Nothing here runs by itself and nothing changes
/// state without the shared review: Scan Skills and every gateway action open the in-panel <see cref="CommandReview"/> overlay (the exact argv,
/// its tier from <see cref="CommandTiers"/>, a confirm that must be pressed); the Diagnostics commands are read-only by the same classifier,
/// run straight away, and leave their full output in the Activity panel (the runner records every run). The Mac's "Run gateway as
/// administrator" is not offered. The buttons the TUI offers by text (Enable AI discovery or Scan, notifications on or off, Fill missing keys;
/// CUST-274) are below the gateway actions: drawn only while they apply, reviewed the same way, and off on a read-only installation.
/// </summary>
public sealed partial class OverviewPanelViewModel
{
    /// <summary>The read-only Diagnostics menu, in the Mac's order. Each is on the explicit allow-list of reads (<see cref="CommandReview.MayRunUnreviewed(string, IReadOnlyList{string})"/>) by a test and again when it runs.</summary>
    internal static readonly IReadOnlyList<DiagnosticCommand> DiagnosticCommands = new DiagnosticCommand[]
    {
        new("Validate configuration", CommandReview.DefaultExecutable, new[] { "config", "validate" },
            "Check that config.yaml parses and names valid values."),
        new("Check credentials", CommandReview.DefaultExecutable, new[] { "keys", "check" },
            "Exit 0 when every required key is set."),
        new("Gateway status", GatewayControl.Executable, new[] { "status" },
            "Ask the running gateway for the health of its subsystems."),
        new("Show provenance", GatewayControl.Executable, new[] { "provenance", "show" },
            "Print the gateway's schema version, content hash, generation and binary version."),

        // The TUI's Policy quick action (`p`): it lists, so it reads (CUST-274).
        new("List policies", CommandReview.DefaultExecutable, new[] { "policy", "list" },
            "List the built-in and custom policies and which one is active."),
    };

    /// <summary>What Scan Skills runs: every configured skill (<c>--all</c> is the CLI's explicit alias for that).</summary>
    internal static readonly string[] ScanSkillsArgv = { "skill", "scan", "--all" };

    /// <summary>Lines of a diagnostic's output the result line keeps; the whole of it is in Activity.</summary>
    private const int DiagnosticOutputLines = 8;

    private const int DiagnosticLineLimit = 240;

    private CancellationTokenSource? _diagnosticCts;

    /// <summary>The confirm-and-run overlay Scan Skills and the gateway actions go through (the same control the Discover panels use).</summary>
    public DiscoverActionReview Review { get; }

    [ObservableProperty]
    private bool _isDiagnosticRunning;

    [ObservableProperty]
    private string _diagnosticTitle = string.Empty;

    [ObservableProperty]
    private string _diagnosticMessage = string.Empty;

    [ObservableProperty]
    private bool _hasDiagnosticMessage;

    /// <summary>Ok / Bad / Warn / Neutral: the tone of the result badge.</summary>
    [ObservableProperty]
    private string _diagnosticKey = "Neutral";

    [ObservableProperty]
    private string _diagnosticOutput = string.Empty;

    [ObservableProperty]
    private bool _hasDiagnosticOutput;

    /// <summary>"Restart Gateway" while it is up, "Start Gateway" while it is not: the button the Mac shows in the same place.</summary>
    [ObservableProperty]
    private string _gatewayActionLabel = "Start Gateway";

    /// <summary>Why the start/restart button is off (shown as its tooltip), or what it does when it is on.</summary>
    [ObservableProperty]
    private string _gatewayActionTip = string.Empty;

    [ObservableProperty]
    private string _stopGatewayTip = string.Empty;

    private GatewayAction _gatewayAction = GatewayAction.Start;

    private bool _gatewayActionAllowed;

    private bool _stopAllowed;

    /// <summary>The Diagnostics items, for the menu.</summary>
    public IReadOnlyList<DiagnosticCommand> Diagnostics => DiagnosticCommands;

    /// <summary>Esc closes the review dialog. True when it consumed the key (the dialog was open, or is busy and must stay).</summary>
    public bool HandleEscape() => Review.IsOpen && Review.HandleEscape();

    // ---- Scan Skills ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Scan Skills: <c>defenseclaw skill scan --all</c>, which runs the scanners and records findings, so it is reviewed first (the Mac runs it
    /// on the click). The scan can take minutes; the review allows ten.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanChangeInstallation))]
    private void ScanSkills() =>
        Review.Open(
            "Scan all skills?",
            "This runs the skill scanner over every configured skill and records what it finds. It changes scan results and findings; it does not change any skill.",
            new[]
            {
                new DiscoverStep(
                    ScanSkillsArgv,
                    "Scan every configured skill.",
                    CommandTier.StateChanging,
                    CliRunner.ExtendedTimeout),
            },
            onFinished: _ => RefreshAfterActionAsync(),
            primaryText: "Scan skills");

    /// <summary>Open Inventory: the Discover panel, unfiltered. Nothing runs (the Mac's auto-scan on entry is not copied).</summary>
    [RelayCommand]
    private void OpenInventory() => RequestNavigation("inventory");

    // ---- Gateway start / restart / stop --------------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanRunGatewayAction))]
    private void RunGatewayAction() => OpenGatewayReview(_gatewayAction);

    [RelayCommand(CanExecute = nameof(CanStopGateway))]
    private void StopGateway() => OpenGatewayReview(GatewayAction.Stop);

    [RelayCommand(CanExecute = nameof(CanRestartFromMenu))]
    private void RestartGateway() => OpenGatewayReview(GatewayAction.Restart);

    private bool CanRunGatewayAction() => _gatewayActionAllowed;

    private bool CanStopGateway() => _stopAllowed;

    private bool CanRestartFromMenu() => GatewayControl.Availability(GatewayAction.Restart, _snapshot, Services.Installation).Allowed;

    /// <summary>
    /// Start, stop or restart goes through the same review as the tray and the palette: the argv exactly as it will run
    /// (<c>defenseclaw-gateway restart</c>), what it does, and a button that must be pressed. The run is a <see cref="CliRunner"/> call, so its
    /// output is in Activity; the monitor is polled again when it finishes so the headline follows.
    /// </summary>
    private void OpenGatewayReview(GatewayAction action)
    {
        var (allowed, reason) = GatewayControl.Availability(action, _snapshot, Services.Installation);
        if (!allowed)
        {
            ShowDiagnosticMessage(GatewayControl.Title(action), "Neutral", reason ?? "Not available right now.", string.Empty);
            return;
        }

        Review.Open(
            GatewayControl.Title(action) + "?",
            GatewayControl.ReviewNote(action),
            new[]
            {
                new DiscoverStep(
                    GatewayControl.Argv(action),
                    GatewayControl.Summary(action),
                    CommandTier.StateChanging,
                    Executable: GatewayControl.Executable),
            },
            onFinished: _ =>
            {
                // A Stop the operator ran is theirs for the session: no automatic start follows it.
                if (action == GatewayAction.Stop)
                {
                    Services.GatewayAutoStart.MarkUserStopped();
                }

                return RefreshAfterActionAsync();
            },
            primaryText: GatewayControl.Title(action));
    }

    private async Task RefreshAfterActionAsync()
    {
        _ = await Services.Monitor.RefreshAsync().ConfigureAwait(true);
        await RefreshMetricsAsync(force: true, ActiveToken).ConfigureAwait(true);
    }

    /// <summary>Keeps the start/restart and stop buttons to what <see cref="GatewayControl.Availability"/> says of the snapshot, with the reason as a tooltip.</summary>
    private void ApplyGatewayActions(GatewaySnapshot snapshot)
    {
        _gatewayAction = snapshot.IsRunning ? GatewayAction.Restart : GatewayAction.Start;
        var (allowed, reason) = GatewayControl.Availability(_gatewayAction, snapshot, Services.Installation);
        var (stopAllowed, stopReason) = GatewayControl.Availability(GatewayAction.Stop, snapshot, Services.Installation);

        GatewayActionLabel = GatewayControl.Title(_gatewayAction).Replace("gateway", "Gateway", StringComparison.Ordinal);
        GatewayActionTip = allowed ? GatewayControl.Summary(_gatewayAction) + " Asks for confirmation first." : reason ?? string.Empty;
        StopGatewayTip = stopAllowed ? GatewayControl.Summary(GatewayAction.Stop) + " Asks for confirmation first." : stopReason ?? string.Empty;

        var changed = allowed != _gatewayActionAllowed || stopAllowed != _stopAllowed;
        _gatewayActionAllowed = allowed;
        _stopAllowed = stopAllowed;
        if (changed)
        {
            RunGatewayActionCommand.NotifyCanExecuteChanged();
            StopGatewayCommand.NotifyCanExecuteChanged();
            RestartGatewayCommand.NotifyCanExecuteChanged();
        }
    }

    // ---- AI discovery, notifications, missing keys (CUST-274) ----------------------------------------------------
    //
    // The buttons the TUI's Overview offers by text ("disabled - run: defenseclaw agent discovery enable", "try: defenseclaw agent discovery scan",
    // the N quick action, "run: defenseclaw keys fill-missing"), through this app's safety model: each opens the shared review with the exact argv and
    // its tier, runs through the runner so it is in Activity, and is not drawn at all when it does not apply. Every one is off, with the installation's
    // sentence as its tooltip, on a managed or invalid installation - that sentence comes before any other reason.

    /// <summary>
    /// What "Enable AI discovery" runs: the TUI registry's own entry. The verb keeps every other <c>ai_discovery</c> setting, restarts the gateway so
    /// the sidecar builds the discovery service and asks it for a first scan (<c>--restart</c> and <c>--scan</c> are on by default).
    /// </summary>
    internal static readonly string[] EnableAiDiscoveryArgv = { "agent", "discovery", "enable", "--yes" };

    /// <summary>What "Scan AI discovery" runs: one immediate scan, asked of the running gateway (<c>POST /api/v1/ai-usage/scan</c>).</summary>
    internal static readonly string[] ScanAiDiscoveryArgv = { "agent", "discovery", "scan" };

    /// <summary>What the notifications button runs while they are off. <c>setup notifications</c> takes <c>on | off | status</c>; with one of them it asks nothing. The review is <see cref="NotificationSwitch"/>'s, shared with the Setup hub's notification routing dialog (CUST-271).</summary>
    internal static readonly string[] NotificationsOnArgv = NotificationSwitch.OnArgv;

    /// <summary>What the notifications button runs while they are on.</summary>
    internal static readonly string[] NotificationsOffArgv = NotificationSwitch.OffArgv;

    /// <summary>What "Fill missing keys" hands to a console: the same argv the Setup panel's Credentials card and the readiness checklist use.</summary>
    internal static readonly string[] FillMissingKeysArgv = CredentialsViewModel.FillMissingArgv;

    private CredentialTerminal? _terminal;
    private CredentialsViewModel? _credentials;
    private bool _enableAiDiscoveryAllowed;
    private bool _scanAiDiscoveryAllowed;
    private bool _notificationsAllowed;
    private bool _fillMissingKeysAllowed;

    /// <summary>The console hand-off Fill missing keys goes through (CUST-266's, the one Setup uses); a test replaces it so no window ever opens.</summary>
    internal CredentialTerminal Terminal
    {
        get => _terminal ??= new CredentialTerminal(Services.Paths);
        set
        {
            _terminal = value ?? throw new ArgumentNullException(nameof(value));
            _credentials = null;
        }
    }

    /// <summary>
    /// CUST-266's route for <c>keys fill-missing</c>, used as it is rather than written again: it asks the installation guard, opens the console,
    /// records the hand-off in Activity and says what happened. This panel owns an instance for itself, so its note is this panel's.
    /// </summary>
    private CredentialsViewModel Credentials => _credentials ??= new CredentialsViewModel(Services, Terminal);

    /// <summary>"Enable AI discovery": drawn only while AI discovery is off.</summary>
    [ObservableProperty]
    private bool _showEnableAiDiscovery;

    /// <summary>"Scan AI discovery": drawn only while AI discovery is on.</summary>
    [ObservableProperty]
    private bool _showScanAiDiscovery;

    /// <summary>"Fill missing keys": drawn only while the doctor cache names a required key that is not set.</summary>
    [ObservableProperty]
    private bool _showFillMissingKeys;

    /// <summary>"Turn notifications off" while the runtime's desktop notifications are on, "Turn notifications on" while they are off.</summary>
    [ObservableProperty]
    private string _notificationsLabel = "Turn notifications off";

    [ObservableProperty]
    private string _enableAiDiscoveryTip = string.Empty;

    [ObservableProperty]
    private string _scanAiDiscoveryTip = string.Empty;

    [ObservableProperty]
    private string _notificationsTip = string.Empty;

    [ObservableProperty]
    private string _fillMissingKeysTip = string.Empty;

    /// <summary>
    /// True when AI discovery is on: config.yaml says so, or the gateway is running the service (a file edited and not yet picked up by a restart
    /// is on until the gateway says otherwise). The same test as the agents card's note.
    /// </summary>
    private bool AiDiscoveryIsOn() => Services.Config.Config.AiDiscovery.Enabled || _snapshot.Health?.AiDiscovery?.IsRunning == true;

    /// <summary>The runtime's notification switch: config.yaml's, else its default, which is on for Windows (<c>_default_notifications_enabled</c>, <c>DefaultNotificationsEnabled</c>).</summary>
    private bool NotificationsAreOn() => OverviewFacts().NotificationsEnabled ?? true;

    /// <summary>
    /// Re-derives which of the four buttons are drawn, what each says, and whether it can be pressed. No I/O: the inputs are the snapshot, config.yaml,
    /// the doctor cache and the installation, all of which the panel already holds, and a change in any of them comes through
    /// <see cref="BuildAttention"/> or <see cref="OnInstallationChanged"/>.
    /// </summary>
    private void ApplyQuickActions()
    {
        var snapshot = _snapshot;

        // The installation's sentence first (a refresh would not cure it), then whether DefenseClaw is there to run the command at all.
        var unavailable = InstallationBlockedReason ?? GatewayControl.Availability(GatewayAction.Restart, snapshot).Reason;

        var discoveryOn = AiDiscoveryIsOn();
        ShowEnableAiDiscovery = !discoveryOn;
        ShowScanAiDiscovery = discoveryOn;

        var enableAllowed = unavailable is null;
        EnableAiDiscoveryTip = unavailable ??
            "Turn on AI discovery: defenseclaw agent discovery enable --yes. It restarts the gateway and asks for a first scan. Asks for confirmation first.";

        // A scan is a request to the running gateway: with it down there is nothing to ask.
        var scanUnavailable = unavailable ??
            (snapshot.State is AppGatewayState.Running or AppGatewayState.Degraded
                ? null
                : "The gateway is not running, so there is nothing to ask for a scan. Start it first.");
        var scanAllowed = scanUnavailable is null;
        ScanAiDiscoveryTip = scanUnavailable ??
            "Ask the running gateway for one AI discovery scan now: defenseclaw agent discovery scan. Asks for confirmation first.";

        var notificationsOn = NotificationsAreOn();
        NotificationsLabel = notificationsOn ? "Turn notifications off" : "Turn notifications on";
        var notificationsAllowed = unavailable is null;
        NotificationsTip = unavailable ??
            $"DefenseClaw's desktop notifications for blocked tool calls and pending approvals are {(notificationsOn ? "on" : "off")}. " +
            $"{CommandReview.CommandLine(CommandReview.DefaultExecutable, NotificationsArgv(!notificationsOn))} restarts the gateway. Asks for confirmation first.";

        var missingKeys = _doctorSnapshot is { } doctor && DoctorReconciliation.MissingRequiredCredentials(doctor).Count > 0;
        ShowFillMissingKeys = missingKeys;
        var fillAllowed = unavailable is null;
        // No review stands in front of the console (nothing runs in this app), so the tooltip is where the command and its tier are said.
        FillMissingKeysTip = unavailable ??
            $"{CommandReview.LabelFor(CommandReview.ResolveTier(FillMissingKeysArgv))}. " +
            $"Opens a console window running {CommandReview.CommandLine(CommandReview.DefaultExecutable, FillMissingKeysArgv)}, a hidden prompt for each required key " +
            "that is unset. What you type goes to DefenseClaw's .env file and never through this app.";

        if (enableAllowed != _enableAiDiscoveryAllowed || scanAllowed != _scanAiDiscoveryAllowed ||
            notificationsAllowed != _notificationsAllowed || fillAllowed != _fillMissingKeysAllowed)
        {
            _enableAiDiscoveryAllowed = enableAllowed;
            _scanAiDiscoveryAllowed = scanAllowed;
            _notificationsAllowed = notificationsAllowed;
            _fillMissingKeysAllowed = fillAllowed;
            EnableAiDiscoveryCommand.NotifyCanExecuteChanged();
            ScanAiDiscoveryCommand.NotifyCanExecuteChanged();
            ToggleNotificationsCommand.NotifyCanExecuteChanged();
            FillKeysInConsoleCommand.NotifyCanExecuteChanged();
        }
    }

    private static string[] NotificationsArgv(bool turnOn) => turnOn ? NotificationsOnArgv : NotificationsOffArgv;

    private bool CanEnableAiDiscovery() => CanChangeInstallation && _enableAiDiscoveryAllowed;

    private bool CanScanAiDiscovery() => CanChangeInstallation && _scanAiDiscoveryAllowed;

    private bool CanToggleNotifications() => CanChangeInstallation && _notificationsAllowed;

    private bool CanFillMissingKeys() => CanChangeInstallation && _fillMissingKeysAllowed;

    /// <summary>
    /// Enable AI discovery: <c>agent discovery enable --yes</c>. Sets <c>ai_discovery.enabled</c> in config.yaml, leaves the other AI discovery settings
    /// as they are, restarts the gateway so the sidecar builds the service, and asks for a first scan; so it is reviewed first, with the restart said.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanEnableAiDiscovery))]
    private void EnableAiDiscovery() =>
        Review.Open(
            "Turn on AI discovery?",
            "Sets ai_discovery.enabled to true in config.yaml and leaves your other AI discovery settings (mode, scan roots, cadence, detectors) as they are. " +
            "It then restarts the gateway so it starts the discovery service, and asks it for a first scan.",
            new[]
            {
                new DiscoverStep(
                    EnableAiDiscoveryArgv,
                    "Enable the sidecar AI discovery service.",
                    CommandTier.StateChanging,
                    TimeSpan.FromMinutes(5)),
            },
            onFinished: _ => AfterQuickActionAsync(refreshAgents: true),
            restartsGateway: true,
            primaryText: "Turn on");

    /// <summary>
    /// Scan AI discovery: <c>agent discovery scan</c>. It asks the running gateway for one immediate scan; the gateway writes the result and records an
    /// <c>ai.discovery</c> event in the audit trail like any scheduled scan, so it is reviewed first.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanScanAiDiscovery))]
    private void ScanAiDiscovery() =>
        Review.Open(
            "Run an AI discovery scan?",
            "It asks the running DefenseClaw gateway to scan this machine for AI tools right now: the same scan it already runs on its own schedule. " +
            "The result is written to ai_discovery_state.json and inventory.db, which is what the agents card reads, and the gateway records it in the audit trail " +
            "as an ai.discovery event like any scheduled scan.\n\n" +
            "It needs the gateway running with AI discovery turned on; otherwise the command fails and nothing changes.",
            new[]
            {
                new DiscoverStep(
                    ScanAiDiscoveryArgv,
                    "Ask the gateway for one immediate AI discovery scan.",
                    CommandTier.StateChanging,
                    TimeSpan.FromMinutes(3)),
            },
            onFinished: _ => AfterQuickActionAsync(refreshAgents: true),
            primaryText: "Run scan");

    /// <summary>
    /// The notifications switch: <c>setup notifications on|off</c>. It writes <c>notifications.enabled</c> and restarts the gateway, whose dispatcher
    /// reads it once at start. These are the runtime's own desktop notifications; this app's tray alerts have their own settings, and the review says so.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanToggleNotifications))]
    private void ToggleNotifications() =>
        NotificationSwitch.Open(Review, !NotificationsAreOn(), onFinished: _ => AfterQuickActionAsync(refreshAgents: false));

    /// <summary>
    /// Fill missing keys: <c>keys fill-missing --yes</c> reads each value at a hidden console prompt (<c>getpass</c>, which reads the console and not
    /// stdin), so it cannot run inside this app and is not offered the review's confirm-and-run. It goes the way Setup's button does
    /// (<see cref="CredentialsViewModel.OpenFillMissingInTerminalAsync"/>): a console window running the exact command, an Activity entry for the hand-off,
    /// and no value ever on an argv or through this process.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanFillMissingKeys))]
    private async Task FillKeysInConsoleAsync()
    {
        const string title = "Fill missing keys";

        await Credentials.OpenFillMissingInTerminalAsync().ConfigureAwait(true);

        // "Neutral" is the route's word for "the console opened"; a guard's refusal or a console that would not start keeps the route's own sentence.
        if (Credentials.NoteKey != "Neutral")
        {
            ShowDiagnosticMessage(title, Credentials.NoteKey, Credentials.Note, string.Empty);
            return;
        }

        ShowDiagnosticMessage(
            title,
            "Neutral",
            $"Opened a console window running `{CommandReview.CommandLine(CommandReview.DefaultExecutable, FillMissingKeysArgv)}`. Type each value at its hidden prompt there. " +
            "What needs attention comes from the last doctor run, so run doctor again when you are done to refresh it.",
            string.Empty);
    }

    /// <summary>
    /// After a reviewed button ran: the command wrote config.yaml and may have restarted the gateway, so the new file is taken now (not at the
    /// watcher's next tick), the monitor and the counts are asked again, and - for AI discovery - the agents card is read again.
    /// </summary>
    private async Task AfterQuickActionAsync(bool refreshAgents)
    {
        Services.ReloadConfig();

        // The buttons and rows follow the file now, whether or not the watcher's notification (which only an active panel hears) is on its way.
        Apply(Services.Monitor.Current);
        await RefreshAfterActionAsync().ConfigureAwait(true);
        if (refreshAgents)
        {
            await RefreshAgentsAsync(ActiveToken).ConfigureAwait(true);
        }
    }

    // ---- Diagnostics (read-only) ---------------------------------------------------------------------------------

    /// <summary>
    /// Runs one Diagnostics command. It runs straight away only when it is on the explicit allow-list of known reads and still classifies as
    /// read-only (<see cref="CommandReview.MayRunUnreviewed(string, IReadOnlyList{string})"/> - the classifier alone is a first-verb guess);
    /// anything else is shown in the review first, like Scan Skills, and runs only when confirmed. The run goes through <see cref="CliRunner"/>,
    /// which puts the exact argv, the output and the exit code in the Activity panel.
    /// </summary>
    [RelayCommand]
    private async Task RunDiagnosticAsync(DiagnosticCommand? command)
    {
        if (command is null || IsDiagnosticRunning)
        {
            return;
        }

        if (!CommandReview.MayRunUnreviewed(command.Executable, command.Argv))
        {
            // Not a command the app knows to be a read, so nothing here runs it on a click. The step's floor is a change, and the classifier can only raise it.
            Review.Open(
                $"Run “{command.Title}”?",
                $"{command.Summary} It is not on DefenseClaw for Windows' list of commands known to be read-only, so it is reviewed first.",
                new[] { new DiscoverStep(command.Argv, command.Summary, CommandTier.StateChanging, Executable: command.Executable) },
                primaryText: "Run command");
            return;
        }

        var cts = new CancellationTokenSource();
        _diagnosticCts = cts;
        IsDiagnosticRunning = true;
        ShowDiagnosticMessage(command.Title, "Neutral", $"Running '{command.CommandText}'…", string.Empty);

        try
        {
            var invocation = await Services.Cli
                .RunNamedAsync(command.Executable, command.Argv, cancellationToken: cts.Token)
                .ConfigureAwait(true);

            var tail = Tail(invocation);
            if (invocation.FailureReason is { Length: > 0 } reason)
            {
                ShowDiagnosticMessage(command.Title, "Bad", $"'{command.CommandText}' did not finish: {reason}. The Activity panel has what it printed.", tail);
            }
            else if (invocation.ExitCode == 0)
            {
                ShowDiagnosticMessage(command.Title, "Ok", $"'{command.CommandText}' succeeded (exit 0). The full output is in the Activity panel.", tail);
            }
            else
            {
                ShowDiagnosticMessage(
                    command.Title,
                    "Warn",
                    $"'{command.CommandText}' exited {invocation.ExitCode?.ToString(System.Globalization.CultureInfo.CurrentCulture) ?? "?"}. The full output is in the Activity panel.",
                    tail);
            }
        }
        catch (CliNotFoundException ex)
        {
            ShowDiagnosticMessage(command.Title, "Bad", $"'{ex.ExecutableName}' was not found: {ex.Message}", string.Empty);
        }
        catch (SecretInArgumentException ex)
        {
            ShowDiagnosticMessage(command.Title, "Bad", ex.Message, string.Empty);
        }
        finally
        {
            IsDiagnosticRunning = false;
            _diagnosticCts = null;
            cts.Dispose();
        }
    }

    /// <summary>Stops a diagnostic in flight; the runner kills its whole process tree.</summary>
    [RelayCommand]
    private void CancelDiagnostic()
    {
        try
        {
            _diagnosticCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // It finished in the instant the button was pressed.
        }
    }

    /// <summary>Open Activity, where every command this panel ran is recorded with its full output.</summary>
    [RelayCommand]
    private void OpenActivity() => RequestNavigation("activity");

    /// <summary>Open the command palette (the Mac's last Diagnostics item).</summary>
    [RelayCommand]
    private void OpenCommandPalette() => Services.Navigation.RequestPalette();

    private void ShowDiagnosticMessage(string title, string key, string message, string output)
    {
        DiagnosticTitle = title;
        DiagnosticKey = key;
        DiagnosticMessage = message;
        DiagnosticOutput = output;
        HasDiagnosticOutput = output.Length > 0;
        HasDiagnosticMessage = message.Length > 0;
    }

    /// <summary>The last few non-empty lines of what the command printed, each cut to a sensible width: the result line's excerpt.</summary>
    private static string Tail(CliInvocation invocation)
    {
        var lines = invocation.OutputLines
            .Select(static l => l.Text)
            .Where(static t => !string.IsNullOrWhiteSpace(t))
            .ToList();
        if (lines.Count == 0)
        {
            return string.Empty;
        }

        var shown = lines.Skip(Math.Max(0, lines.Count - DiagnosticOutputLines))
            .Select(static l => l.Length > DiagnosticLineLimit ? l[..DiagnosticLineLimit] + "…" : l);
        var omitted = lines.Count - Math.Min(lines.Count, DiagnosticOutputLines);
        return (omitted > 0 ? $"… {omitted} earlier line(s) in Activity{Environment.NewLine}" : string.Empty) + string.Join(Environment.NewLine, shown);
    }
}
