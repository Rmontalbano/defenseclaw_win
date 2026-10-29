using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.Core.AiRuntime;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.ViewModels;

/// <summary>One line of a plane's requirements, drawn with a word (never colour alone) and a tone.</summary>
public sealed class RequirementView
{
    internal RequirementView(RuntimeRequirement requirement)
    {
        Text = requirement.Text;
        (Label, Tone) = requirement.State switch
        {
            RuntimeRequirementState.Met => ("In place", "Ok"),
            RuntimeRequirementState.NotMet => ("Missing", "Bad"),
            RuntimeRequirementState.CannotCheck => ("Can't check", "Warn"),
            RuntimeRequirementState.Unavailable => ("Not available yet", "Neutral"),
            _ => ("Note", "Neutral"),
        };
        IsGreyed = requirement.State == RuntimeRequirementState.Unavailable;
    }

    public string Text { get; }

    public string Label { get; }

    /// <summary>The badge tone key (<c>Ok</c>, <c>Bad</c>, <c>Warn</c>, <c>Neutral</c>).</summary>
    public string Tone { get; }

    /// <summary>Drawn dimmed: something this app offers no way to do yet.</summary>
    public bool IsGreyed { get; }

    public string AutomationName => $"{Label}: {Text}";
}

/// <summary>One plane of the card: what it records, whether config.yaml and the gateway have it on, and what it needs.</summary>
public sealed class PlaneView
{
    internal PlaneView(RuntimeMonitoringPlane row)
    {
        Row = row;
        Requirements = row.Requirements.Select(static r => new RequirementView(r)).ToArray();
        (RunLabel, RunTone) = row.Run switch
        {
            RuntimePlaneRunState.Running => ("Running", "Ok"),
            RuntimePlaneRunState.NotRunning => (row.SelectedInConfig ? "Not running" : "Off", row.SelectedInConfig ? "Warn" : "Neutral"),
            _ => ("Not known", "Neutral"),
        };
    }

    internal RuntimeMonitoringPlane Row { get; }

    public string Name => Row.DisplayName;

    public string Records => Row.Records;

    public string Privacy => Row.Privacy;

    public string ConfigText => Row.ConfigText;

    public string RunText => Row.RunText;

    public string RunLabel { get; }

    public string RunTone { get; }

    public IReadOnlyList<RequirementView> Requirements { get; }

    public string AutomationName => $"{Name}: {RunLabel}. {ConfigText}";
}

/// <summary>
/// The Runtime monitoring card of the Setup hub (CUST-348, phase 1): the state of AI Discovery runtime monitoring and its three planes on this PC, and the
/// switches that turn it, Plane C and DNS capture on or off. DefenseClaw 1.0 only (<see cref="RuntimeCapability.AiRuntime"/>); the Setup tile and the
/// Overview row are hidden on 0.8.10, and a dialog opened there says so and offers nothing.
/// <para>
/// <b>State</b> (<see cref="RuntimeMonitoringState"/>) is three things side by side: what config.yaml says (<c>ai_discovery.runtime</c>, schema 9), what
/// the gateway's <c>/health</c> says it runs (<c>ai_runtime.details.planes</c>), and what each plane needs. Whatever this app cannot read it says it
/// cannot read: the audit policy needs an elevated prompt, and the gateway's token is the administrator mode's to report
/// (<see cref="IGatewayElevationStatus"/>, the hook for CUST-348 phase 2).
/// </para>
/// <para>
/// <b>Changes</b> go through the CLI, never a file edit: <c>defenseclaw agent discovery runtime enable --yes [--enable-host-plane | --no-enable-host-plane]
/// [--dns-capture | --no-dns-capture] [--no-restart]</c> or <c>... disable --yes [--no-restart]</c> (<see cref="AiRuntimeCommands"/>, <see cref="RuntimeMonitoringPlan"/>).
/// The verbs write <c>ai_discovery.runtime</c> and restart the gateway themselves, so the app's restart queue is not involved. They are reviewed with the exact
/// argv in the shared <see cref="DiscoverActionReview"/>, never auto-run, and refused first by the installation guard (the sentence before every other).
/// 1.0 has no switch for Plane A or B alone, so there is none here.
/// </para>
/// </summary>
public sealed partial class RuntimeMonitoringViewModel : SetupDialogViewModel
{
    /// <summary>What the dialog is called, on its heading and in the review.</summary>
    public const string DialogTitle = "Runtime monitoring";

    /// <summary>Said where the dialog cannot work: the runtime does not have the planes.</summary>
    public const string UnsupportedText =
        "Runtime monitoring needs DefenseClaw 1.0 or later. " + RuntimeCapabilityCatalog.UnsupportedMessage + ".";

    public const string ScopeNote =
        "Runtime monitoring is AI Discovery's second half: while the inventory scan looks at files and settings, these planes watch what is running now. " +
        "It is off by default. Everything is read on this PC and recorded locally; nothing here sends anything anywhere by itself.";

    public const string NoSingleAbNote =
        "DefenseClaw has no switch for plane A or plane B alone: turning monitoring on starts both, and turning it off stops every plane. Plane C and DNS capture are separate opt-ins.";

    private static readonly string[] EnablePath = { "agent", "discovery", "runtime", "enable" };
    private static readonly string[] DisablePath = { "agent", "discovery", "runtime", "disable" };

    private readonly IGatewayElevationStatus _elevation;
    private readonly IPlaneCProbes _probes;
    private HashSet<string>? _enableFlags;
    private bool _canDisable = true;
    private bool _canSkipRestart = true;
    private bool _subscribed;
    private RuntimeMonitoringConfig _config = RuntimeMonitoringConfig.Off;
    private ProbeReading<int?>? _commandLineAudit;
    private IReadOnlyList<string>? _plannedArgv;
    private HelpProbeResult _disableHelp = new(string.Empty, "not read");

    /// <param name="services">The composition the dialog works in.</param>
    /// <param name="elevation">Whether the gateway runs elevated; null is <see cref="PendingGatewayElevationStatus"/> (the administrator mode does not exist yet).</param>
    /// <param name="probes">The one registry read (command-line auditing, no elevation needed); null is the real <see cref="WindowsPlaneCProbes"/>.</param>
    /// <param name="helpReader">Reads <c>agent discovery runtime enable|disable --help</c>; null is the hub catalog's cached probe, a test hands it a screen.</param>
    public RuntimeMonitoringViewModel(
        AppServices services,
        IGatewayElevationStatus? elevation = null,
        IPlaneCProbes? probes = null,
        Func<IReadOnlyList<string>, CancellationToken, Task<HelpProbeResult>>? helpReader = null)
        : base(services, "this state was read")
    {
        _elevation = elevation ?? PendingGatewayElevationStatus.Instance;
        _probes = probes ?? new WindowsPlaneCProbes();
        HelpReader = helpReader ?? DefaultHelpReader;
        State = RuntimeMonitoringState.Build(RuntimeMonitoringConfig.Off, RuntimeHealthView.NotReported, false, GatewayElevation.Unknown, null);
    }

    internal Func<IReadOnlyList<string>, CancellationToken, Task<HelpProbeResult>> HelpReader { get; set; }

    /// <summary>Test seam: the latest gateway snapshot. Null reads the monitor's.</summary>
    internal Func<GatewaySnapshot>? SnapshotReader { get; set; }

    private GatewaySnapshot CurrentSnapshot => SnapshotReader is { } read ? read() : Services.Monitor.Current;

    private Task<HelpProbeResult> DefaultHelpReader(IReadOnlyList<string> path, CancellationToken cancellationToken) =>
        WizardCatalog.Shared(Services).Probe.CliHelpAsync(path, cancellationToken);

    public string Title => DialogTitle;

    public string Subtitle => "Whether DefenseClaw is watching what runs on this PC for AI use, plane by plane, and what each plane needs.";

    public string Scope => ScopeNote;

    public string PlanesNote => NoSingleAbNote;

    // ------------------------------------------------------------------ availability (the 1.0 gate)

    /// <summary>The connected runtime has the planes and the two verbs this dialog runs. False on 0.8.10.</summary>
    public bool IsSupported => Support is null;

    /// <summary>Why the dialog offers nothing, or null: <see cref="UnsupportedText"/> when the planes are missing, else the verb the runtime does not list.</summary>
    public string? Support
    {
        get
        {
            var gate = Services.Runtime.Check(RuntimeCapability.AiRuntime);
            if (!gate.IsAvailable)
            {
                return UnsupportedText;
            }

            var capabilities = Services.Runtime.Capabilities;
            foreach (var verb in new[] { AiRuntimeCommands.EnableCommand, AiRuntimeCommands.DisableCommand })
            {
                if (!capabilities.HasAiRuntimeCommand(verb))
                {
                    return $"This DefenseClaw does not list 'agent discovery runtime {verb}', so monitoring cannot be changed from here.";
                }
            }

            return null;
        }
    }

    public bool HasSupportProblem => Support is not null;

    // ------------------------------------------------------------------ state

    /// <summary>The card as last built; see <see cref="RuntimeMonitoringState"/>.</summary>
    public RuntimeMonitoringState State { get; private set; }

    public ObservableCollection<PlaneView> Planes { get; } = new();

    public string Headline => State.Headline;

    public string ApplyNote => State.ApplyNote;

    public bool HasApplyNote => ApplyNote.Length > 0;

    /// <summary>The gateway's side, in one line: its <c>ai_runtime</c> state, or why there is none.</summary>
    public string GatewayLine
    {
        get
        {
            var snapshot = CurrentSnapshot;
            if (snapshot.Health is null)
            {
                return "Gateway: not answering, so what it is running is not known.";
            }

            if (!State.Health.Reported)
            {
                return "Gateway: answering, and it does not report runtime monitoring.";
            }

            var line = "Gateway reports runtime monitoring: " + State.Health.State;
            return State.Health.Degraded ? line + " (degraded: at least one plane is not delivering everything it can)." : line + ".";
        }
    }

    /// <summary>The config schema line: <c>config_version 9</c>, or why there is none.</summary>
    public string ConfigLine => _config.ConfigVersion is { } version
        ? $"config.yaml: config_version {version.ToString(CultureInfo.InvariantCulture)}" + (_config.Enabled || _config.Planes.Count > 0 || _config.EnableHostPlane ? ", ai_discovery.runtime present." : ", no ai_discovery.runtime settings (the default: off).")
        : "config.yaml: no config_version.";

    /// <summary>Rebuilds <see cref="State"/> from config.yaml, the last health poll and the elevation status. No I/O beyond what is already held.</summary>
    internal void Rebuild()
    {
        _config = RuntimeMonitoringConfig.From(Services.Config);
        var snapshot = CurrentSnapshot;
        State = RuntimeMonitoringState.Build(
            _config,
            RuntimeHealthView.From(snapshot.Health),
            snapshot.Health is not null,
            _elevation.Current,
            _commandLineAudit);

        Planes.Clear();
        foreach (var row in State.Planes)
        {
            Planes.Add(new PlaneView(row));
        }

        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(Headline));
        OnPropertyChanged(nameof(ApplyNote));
        OnPropertyChanged(nameof(HasApplyNote));
        OnPropertyChanged(nameof(GatewayLine));
        OnPropertyChanged(nameof(ConfigLine));
        OnPropertyChanged(nameof(Support));
        OnPropertyChanged(nameof(IsSupported));
        OnPropertyChanged(nameof(HasSupportProblem));
    }

    // ------------------------------------------------------------------ the switches

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditPlaneSwitches), nameof(MonitoringText))]
    private bool _monitoringOn;

    [ObservableProperty]
    private bool _hostPlaneOn;

    [ObservableProperty]
    private bool _dnsCaptureOn;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RestartNote))]
    private bool _restartAfter = true;

    public string MonitoringText => MonitoringOn ? "Runtime monitoring: on" : "Runtime monitoring: off";

    /// <summary>Plane C and DNS capture can be chosen only while monitoring is to be on (the verb that sets them is the one that turns it on).</summary>
    public bool CanEditPlaneSwitches => CanEdit && IsSupported && MonitoringOn;

    public string RestartNote => RestartAfter
        ? "The verb restarts the gateway so the change takes effect now."
        : "The gateway is not restarted (--no-restart): the setting is saved in config.yaml and takes effect the next time the gateway restarts.";

    public bool CanChooseRestart => _canSkipRestart;

    public string HostPlaneNote =>
        "Plane C reads process, identity and privilege events from the Windows Security log. It needs a gateway running as administrator and Advanced Audit Policy; until both are in place it reports itself blind and records nothing.";

    public string DnsNote =>
        "DNS capture names the peer of each connection from the answer this PC resolved (recording the domains it looked up) instead of by reverse DNS. It reads the DNS Client operational log, which Windows keeps off by default.";

    partial void OnMonitoringOnChanged(bool value) => Recompute();

    partial void OnHostPlaneOnChanged(bool value) => Recompute();

    partial void OnDnsCaptureOnChanged(bool value) => Recompute();

    partial void OnRestartAfterChanged(bool value) => Recompute();

    public ObservableCollection<string> ChangeLines { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private bool _hasChanges;

    public string Summary => HasChanges
        ? (ChangeLines.Count == 1 ? "1 change" : ChangeLines.Count.ToString(CultureInfo.CurrentCulture) + " changes")
        : "Nothing to apply";

    /// <summary>The exact argv the review will show for the choices as they are, or null when nothing changed. Not run from here.</summary>
    internal IReadOnlyList<string>? PlannedArgv => _plannedArgv;

    private bool _seeding;

    /// <summary>The last registry read started by <see cref="Seed"/>; a test awaits it so the card is not rebuilt under its feet.</summary>
    internal Task CommandLineAuditRead { get; private set; } = Task.CompletedTask;

    protected override void Seed(bool keepEdits)
    {
        Subscribe();
        _seeding = true;
        try
        {
            Rebuild();
            if (!keepEdits)
            {
                var current = RuntimeMonitoringPlan.Current(_config);
                MonitoringOn = current.Enabled;
                HostPlaneOn = current.HostPlane;
                DnsCaptureOn = current.DnsCapture;
            }
        }
        finally
        {
            _seeding = false;
        }

        BeginTrustRead();
        Recompute();
        CommandLineAuditRead = ReadCommandLineAuditAsync();
    }

    /// <summary>The one machine read: the registry value Windows keeps for command-line auditing (readable without elevation). Off the UI thread.</summary>
    private async Task ReadCommandLineAuditAsync()
    {
        ProbeReading<int?> reading;
        try
        {
            reading = await Task.Run(_probes.ReadCommandLineAudit).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Trace.TraceWarning($"Runtime monitoring: command-line audit read failed: {ex.Message}");
            reading = ProbeReading<int?>.Failed(ex.Message);
        }

        if (IsDisposed)
        {
            return;
        }

        _commandLineAudit = reading;
        Rebuild();
    }

    private void Subscribe()
    {
        if (_subscribed)
        {
            return;
        }

        _subscribed = true;
        Services.Monitor.PollCompleted += OnGatewayChanged;
        _elevation.Changed += OnElevationChanged;
        Services.Runtime.Changed += OnRuntimeChanged;
    }

    private void OnGatewayChanged(object? sender, EventArgs e) => Refresh(runOnUi: true);

    private void OnElevationChanged(object? sender, EventArgs e) => Refresh(runOnUi: true);

    private void OnRuntimeChanged(object? sender, EventArgs e) => Refresh(runOnUi: true);

    private void Refresh(bool runOnUi)
    {
        if (!IsOpen || IsDisposed)
        {
            return;
        }

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (runOnUi && dispatcher is { } d && !d.CheckAccess())
        {
            _ = d.BeginInvoke(new Action(() => Refresh(runOnUi: false)));
            return;
        }

        // The state is re-read; the operator's unsaved switches are kept.
        Rebuild();
        NotifyActions();
    }

    protected override void OnDisposing()
    {
        if (_subscribed)
        {
            _subscribed = false;
            Services.Monitor.PollCompleted -= OnGatewayChanged;
            _elevation.Changed -= OnElevationChanged;
            Services.Runtime.Changed -= OnRuntimeChanged;
        }
    }

    private RuntimeMonitoringPlan.Wanted Wanted() => new(MonitoringOn, HostPlaneOn, DnsCaptureOn);

    private void Recompute()
    {
        if (_seeding)
        {
            return;
        }

        var changes = RuntimeMonitoringPlan.Diff(_config, Wanted());
        ChangeLines.Clear();
        foreach (var change in changes)
        {
            ChangeLines.Add(change.Description);
        }

        _plannedArgv = RuntimeMonitoringPlan.Argv(_config, Wanted(), RestartAfter || !_canSkipRestart);
        HasChanges = changes.Count > 0;
        OnPropertyChanged(nameof(Summary));
        NotifyActions();
    }

    // ------------------------------------------------------------------ the CLI's help

    protected override async Task<HelpProbeResult> ReadHelpAsync(CancellationToken cancellationToken)
    {
        var enable = HelpReader(EnablePath, cancellationToken);
        var disable = HelpReader(DisablePath, cancellationToken);

        HelpProbeResult enableResult;
        try
        {
            enableResult = await enable.ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            enableResult = new HelpProbeResult(string.Empty, ex.Message);
        }

        try
        {
            _disableHelp = await disable.ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _disableHelp = new HelpProbeResult(string.Empty, ex.Message);
        }

        return enableResult;
    }

    /// <summary>
    /// Checks <c>agent discovery runtime enable --help</c>: <c>--yes</c> must be an option (without it the verb stops at a question this app cannot
    /// answer), <c>--enable-host-plane</c> and <c>--dns-capture</c> must be listed for those switches to be sent, and <c>--no-restart</c> for the restart
    /// choice. <c>disable --help</c> must list <c>--yes</c> for monitoring to be turned off from here.
    /// </summary>
    protected override string? ApplyHelp(HelpProbeResult result)
    {
        _enableFlags = null;
        if (!result.Succeeded)
        {
            return "The options of agent discovery runtime enable could not be read (" + result.Error + "), so changes cannot be checked against this DefenseClaw. Press Refresh to try again.";
        }

        var help = SetupHelpParser.Parse(result.Text, commandDepth: 4);
        if (!help.Lists("--yes"))
        {
            return "The installed DefenseClaw's agent discovery runtime enable has no --yes, so it would stop at a question this app cannot answer. Nothing can be sent from here.";
        }

        _enableFlags = new HashSet<string>(StringComparer.Ordinal);
        foreach (var option in help.Options)
        {
            foreach (var name in option.Names)
            {
                _ = _enableFlags.Add(name);
            }

            if (option.NegativeFlag is { Length: > 0 } negative)
            {
                _ = _enableFlags.Add(negative);
            }
        }

        _canSkipRestart = help.Lists("--no-restart");
        if (_disableHelp.Succeeded)
        {
            _canDisable = SetupHelpParser.Parse(_disableHelp.Text, commandDepth: 4).Lists("--yes");
        }
        else
        {
            _canDisable = false;
        }

        return null;
    }

    protected override void HelpChecked()
    {
        OnPropertyChanged(nameof(HostPlaneAvailable));
        OnPropertyChanged(nameof(DnsAvailable));
        OnPropertyChanged(nameof(CanChooseRestart));
        Recompute();
    }

    /// <summary>The CLI lists <c>--enable-host-plane</c> and its negative; before the help is read everything is assumed listed (the plan stays off until it is).</summary>
    public bool HostPlaneAvailable => _enableFlags is null || (_enableFlags.Contains("--enable-host-plane") && _enableFlags.Contains("--no-enable-host-plane"));

    public bool DnsAvailable => _enableFlags is null || (_enableFlags.Contains("--dns-capture") && _enableFlags.Contains("--no-dns-capture"));

    // ------------------------------------------------------------------ what is on and off

    protected override void NotifyActions()
    {
        RaiseGuardState();
        OnPropertyChanged(nameof(CanEditPlaneSwitches));
        OnPropertyChanged(nameof(ReviewTip));
        OnPropertyChanged(nameof(ReviewBlocked));
        OnPropertyChanged(nameof(SwitchTip));
        ReviewChangesCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Why "Review changes" is off, or null. In this order, so one sentence is said and the first is the one that matters: the runtime has no planes
    /// (0.8.10), the installation (CUST-308), the unreadable file, the state's age or a change to config.yaml since (CUST-312), the CLI's help, another
    /// command, and last, nothing to apply.
    /// </summary>
    public string? ReviewBlockedReason
    {
        get
        {
            // The installation's sentence comes before every other, then the runtime's.
            if (InstallationBlockedReason is { } installation)
            {
                return installation;
            }

            if (Support is { } support)
            {
                return support;
            }

            if (ChangesBlockedReason is { } reason)
            {
                return reason;
            }

            if (IsChecking)
            {
                return "Checking this DefenseClaw's options…";
            }

            if (HasHelpProblem)
            {
                return HelpProblem;
            }

            if (!MonitoringOn && Wanted().Enabled == false && _config.Enabled && !_canDisable)
            {
                return "This DefenseClaw's agent discovery runtime disable has no --yes, so monitoring cannot be turned off from here.";
            }

            if (Review.IsOpen || Review.IsRunning)
            {
                return "Another command is running.";
            }

            return HasChanges ? null : "Nothing to apply: no switch differs from config.yaml.";
        }
    }

    public bool ReviewBlocked => ReviewBlockedReason is not null;

    /// <summary>The tooltip of "Review changes": the reason it is off, or what it does.</summary>
    public string ReviewTip => ReviewBlockedReason ?? "Shows the exact command first. Nothing runs until you confirm.";

    /// <summary>The tooltip of the switches: the reason they cannot be changed, or empty.</summary>
    public string SwitchTip => InstallationBlockedReason ?? Support ?? (HasConfigProblem ? ConfigProblem : string.Empty);

    private bool CanReview() => ReviewBlockedReason is null;

    // ------------------------------------------------------------------ the plan

    /// <summary>The reviewed plan for the switches as they are: one step, the verb. Null when nothing changed or it cannot be built.</summary>
    internal SetupPlan? BuildPlan()
    {
        if (_plannedArgv is not { } argv)
        {
            return null;
        }

        var turningOff = !MonitoringOn;
        var restart = RestartAfter || !_canSkipRestart;
        var warnings = new List<CommandReviewWarning>();
        if (!turningOff && HostPlaneOn)
        {
            warnings.Add(new CommandReviewWarning("Plane C reads the Security log", AiRuntimePanelViewModel.PlaneCWarning));
        }

        if (!turningOff && DnsCaptureOn)
        {
            warnings.Add(new CommandReviewWarning("DNS capture records domains", RuntimeMonitoringState.PrivacyDns));
        }

        if (!restart)
        {
            warnings.Add(new CommandReviewWarning(
                "Gateway not restarted",
                "The gateway is not restarted, so the setting is saved but runtime monitoring does not change until it next restarts."));
        }

        var summary = turningOff
            ? AiRuntimePanelViewModel.DisableExplanation
            : AiRuntimePanelViewModel.EnableExplanation;

        return new SetupPlan
        {
            Title = turningOff ? "Turn off runtime monitoring?" : _config.Enabled ? "Change runtime monitoring settings?" : "Turn on runtime monitoring?",
            Summary = summary + "\n\n" + string.Join("\n", ChangeLines.Select(static l => "- " + l)),
            Steps = new[]
            {
                new DiscoverStep(
                    argv,
                    turningOff ? "Turn off the AI Discovery runtime planes." : "Apply the AI Discovery runtime plane settings.",
                    CommandTier.StateChanging,
                    TimeSpan.FromMinutes(5)),
            },
            RestartsGateway = restart,
            Warnings = warnings,
            PrimaryText = turningOff ? "Turn off" : _config.Enabled ? "Apply" : "Turn on",
        };
    }

    /// <summary>Opens the review. Nothing runs from here: the command starts when the operator confirms it.</summary>
    [RelayCommand(CanExecute = nameof(CanReview))]
    private void ReviewChanges()
    {
        // The look at the files again (CUST-312): a button that was on a minute ago must not authorize anything now.
        _ = Trust.CheckConfig();
        if (ReviewBlockedReason is not null)
        {
            NotifyActions();
            return;
        }

        var plan = BuildPlan();
        if (plan is null)
        {
            return;
        }

        ResultText = string.Empty;
        Review.OpenPlan(plan, AfterPlanAsync);
    }

    private Task AfterPlanAsync(DiscoverReviewResult result)
    {
        Services.ReloadConfig();
        Seed(keepEdits: false);

        var step = result.Outcomes.Count > 0 ? result.Outcomes[0] : null;
        ResultKey = step?.State == DiscoverStepState.Succeeded ? "Ok" : "Bad";
        ResultText = step?.State == DiscoverStepState.Succeeded
            ? "Applied. config.yaml now says: " + State.Summary + "."
            : "Not applied. " + PlanReport.Describe(result.Outcomes) + (step is { Detail.Length: > 0 } ? " " + step.Detail : string.Empty);
        return Task.CompletedTask;
    }
}
