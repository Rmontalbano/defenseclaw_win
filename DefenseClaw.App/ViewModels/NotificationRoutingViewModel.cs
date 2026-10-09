using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Setup;

namespace DefenseClaw.App.ViewModels;

/// <summary>One toggle of the notification routing dialog: a slot of <c>setup notifications-set</c>, what config.yaml says about it, and what the operator has set it to.</summary>
public sealed partial class RoutingRow : ObservableObject
{
    internal RoutingRow(NotificationSlot slot, bool original, bool isOn)
    {
        Slot = slot;
        Original = original;
        _isOn = isOn;
    }

    public NotificationSlot Slot { get; }

    public string Label => Slot.Label;

    public string Description => Slot.Description;

    /// <summary>What config.yaml said when the dialog read it.</summary>
    public bool Original { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChanged), nameof(StateText), nameof(ChangeText), nameof(AutomationName))]
    private bool _isOn;

    /// <summary>False when the installed CLI's help does not list the slot: the toggle is off and the slot is never sent.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutomationName))]
    private bool _isAvailable = true;

    /// <summary>The installed CLI's help does not list the slot (the opposite of <see cref="IsAvailable"/>, for the view).</summary>
    public bool IsUnavailable => !IsAvailable;

    /// <summary>The operator moved it from what config.yaml says.</summary>
    public bool IsChanged => IsAvailable && IsOn != Original;

    public string StateText => IsOn ? "On" : "Off";

    /// <summary>"was off": shown beside a toggle that has been moved.</summary>
    public string ChangeText => IsChanged ? (Original ? "was on" : "was off") : string.Empty;

    /// <summary>Why the toggle is off to the touch, or empty.</summary>
    public string UnavailableText => IsAvailable ? string.Empty : "This DefenseClaw does not list this setting, so it cannot be changed here.";

    /// <summary>What a screen reader announces for the row.</summary>
    public string AutomationName =>
        $"{Label}: {StateText}" + (IsChanged ? ", changed, " + ChangeText : string.Empty) + (IsAvailable ? string.Empty : ". Not available.") + ". " + Description;

    partial void OnIsAvailableChanged(bool value)
    {
        OnPropertyChanged(nameof(UnavailableText));
        OnPropertyChanged(nameof(IsUnavailable));
    }

    /// <summary>Raised when the operator moves the toggle (the dialog recomputes what changes).</summary>
    internal event EventHandler? Moved;

    partial void OnIsOnChanged(bool value) => Moved?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// The notification routing dialog of the Setup hub (CUST-271): the TUI's Notifications Routing wizard and the Mac's, in this app's safety model.
/// Six toggles - three event types, three sources - seeded from config.yaml, and the master switch above them; <b>Review changes</b> turns the
/// toggles that moved into one <c>setup notifications-set SLOT on|off</c> per toggle, shown together and run in order by the shared
/// <see cref="DiscoverActionReview"/> (<see cref="SetupPlan"/>): the first failure stops the plan, the report says which commands ran, and each
/// is its own entry in Activity. Nothing runs from a toggle, and a dialog with no moved toggle says "Nothing to apply" instead of running six
/// commands that would each print "nothing to change".
/// <para>
/// <b>The gateway's notifications are not this app's tray alerts.</b> These are DefenseClaw's own desktop notifications
/// (<c>notifications:</c> in config.yaml), raised by the gateway; the CRITICAL / HIGH / gateway-offline toasts of Settings, Notifications are
/// the app's, from the tray, and change nothing here. One block can raise both. Every sentence this dialog and its review say about it says so.
/// </para>
/// <para>
/// <b>The master switch is the Overview's action</b> (<see cref="NotificationSwitch"/>): the same question, consequence text, argv and button, not
/// a second one. <b>One restart:</b> the dispatcher reads its filters once, at gateway start, so only the last command restarts the gateway
/// (the others carry <c>--no-restart</c>; <see cref="NotificationRouting.Argvs"/>). The guards and the help check are
/// <see cref="SetupDialogViewModel"/>'s: each slot and <c>--no-restart</c> must be listed by the installed CLI's
/// <c>setup notifications-set --help</c>.
/// </para>
/// </summary>
public sealed partial class NotificationRoutingViewModel : SetupDialogViewModel
{
    /// <summary>What the dialog is called, on its heading and in the review.</summary>
    public const string DialogTitle = "Notification routing";

    /// <summary>The setup targets whose card opens this dialog instead of a wizard: the master switch and the categories.</summary>
    public static bool Handles(string? target) =>
        string.Equals(target, "notifications", StringComparison.Ordinal) || string.Equals(target, "notifications-set", StringComparison.Ordinal);

    /// <summary>
    /// The sentence that keeps the gateway's notifications and this app's tray alerts apart. Said on the dialog and in the review, in the words
    /// the Overview's switch uses (<see cref="NotificationSwitch.Explanation"/>).
    /// </summary>
    public const string TrayNote =
        "These are DefenseClaw's own desktop notifications, raised by the gateway for blocked tool calls, would-block verdicts and pending approvals " +
        "(the notifications block of config.yaml). The alerts this app shows from the tray (Settings, Notifications) are a separate setting: " +
        "the same block can raise both, and turning one off leaves the other as it is.";

    private readonly List<NotificationChange> _pending = new();
    private IReadOnlyList<NotificationChange> _planChanges = Array.Empty<NotificationChange>();
    private NotificationRoutingState _state = NotificationRouting.FromYaml(string.Empty);
    private bool _canSkipRestart = true;
    private IReadOnlySet<string>? _listedSlots;

    /// <param name="services">The composition the dialog works in.</param>
    /// <param name="helpReader">
    /// Reads <c>setup notifications-set --help</c>, to check the slots and <c>--no-restart</c> against what the installed CLI lists. Null: the
    /// Setup help probe the hub's catalog already uses (cached, and no Activity entry); a test hands it a screen.
    /// </param>
    public NotificationRoutingViewModel(AppServices services, Func<CancellationToken, Task<HelpProbeResult>>? helpReader = null)
        : base(services)
    {
        HelpReader = helpReader ?? DefaultHelpReader;
        CategoryRows = Array.Empty<RoutingRow>();
        SourceRows = Array.Empty<RoutingRow>();
    }

    /// <summary>Test seam, and the production reader: the help screen of <c>setup notifications-set</c>.</summary>
    internal Func<CancellationToken, Task<HelpProbeResult>> HelpReader { get; set; }

    private Task<HelpProbeResult> DefaultHelpReader(CancellationToken cancellationToken) =>
        WizardCatalog.Shared(Services).Probe.HelpAsync(new[] { "notifications-set" }, cancellationToken);

    // ------------------------------------------------------------------ the dialog

    public string Title => DialogTitle;

    public string Subtitle => "Choose which events make DefenseClaw's gateway raise a desktop notification.";

    public string ScopeNote => TrayNote;

    /// <summary>The three event types, in the TUI's order.</summary>
    public IReadOnlyList<RoutingRow> CategoryRows { get; private set; }

    /// <summary>The three sources, in the TUI's order.</summary>
    public IReadOnlyList<RoutingRow> SourceRows { get; private set; }

    private IEnumerable<RoutingRow> AllRows => CategoryRows.Concat(SourceRows);

    // ------------------------------------------------------------------ the master switch

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MasterText), nameof(MasterButtonText), nameof(MasterTip), nameof(ShowMasterOffNote))]
    private bool _masterOn = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MasterDetail), nameof(HasMasterDetail))]
    private bool _masterIsExplicit;

    public string MasterText => $"Desktop notifications are {(MasterOn ? "on" : "off")}";

    /// <summary>Under the master line: that the file does not say, so the runtime's default is what is on screen. Empty when it does.</summary>
    public string MasterDetail => MasterIsExplicit ? string.Empty : "config.yaml does not say, so this is the runtime's default on Windows.";

    public bool HasMasterDetail => MasterDetail.Length > 0;

    public string MasterButtonText => MasterOn ? "Turn off…" : "Turn on…";

    /// <summary>The Overview's tooltip for the same action, or the reason it is off.</summary>
    public string MasterTip =>
        ChangesBlockedReason ??
        $"DefenseClaw's desktop notifications for blocked tool calls and pending approvals are {(MasterOn ? "on" : "off")}. " +
        $"{CommandReview.CommandLine(CommandReview.DefaultExecutable, NotificationSwitch.Argv(!MasterOn))} restarts the gateway. Asks for confirmation first.";

    public bool ShowMasterOffNote => !MasterOn;

    public string MasterOffNote =>
        "Desktop notifications are off, so none of the switches below raises one until you turn them on. You can still change them now; the choices are saved.";

    // ------------------------------------------------------------------ what moved

    [ObservableProperty]
    private bool _restartAfter = true;

    public string RestartNote =>
        RestartAfter
            ? "The gateway reads these once, when it starts. With several changes only the last command restarts it."
            : "The gateway is not restarted, so the changes are saved but not in effect until it next restarts.";

    public ObservableCollection<string> ChangeLines { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private bool _hasChanges;

    /// <summary>"Nothing to apply", or "2 changes".</summary>
    public string Summary => HasChanges
        ? (_pending.Count == 1 ? "1 change" : _pending.Count.ToString(CultureInfo.CurrentCulture) + " changes")
        : "Nothing to apply";

    /// <summary>What <see cref="Summary"/> counts, for a test and for the dialog's tooltip.</summary>
    internal IReadOnlyList<NotificationChange> Changes => _pending;

    partial void OnRestartAfterChanged(bool value)
    {
        OnPropertyChanged(nameof(RestartNote));
        NotifyActions();
    }

    // ------------------------------------------------------------------ seeding

    /// <summary>
    /// Starts the toggles from config.yaml as the app holds it. <paramref name="keepEdits"/> keeps the operator's unsaved choices (after the
    /// master switch ran: its change does not touch a slot), measured against the file as it is now.
    /// </summary>
    protected override void Seed(bool keepEdits)
    {
        var edits = keepEdits ? AllRows.ToDictionary(static r => r.Slot.Id, static r => r.IsOn, StringComparer.Ordinal) : null;
        foreach (var row in AllRows)
        {
            row.Moved -= OnRowMoved;
        }

        _state = NotificationRouting.Read(Services.Config);
        MasterOn = _state.MasterEnabled;
        MasterIsExplicit = _state.MasterIsExplicit;

        var rows = NotificationRouting.Slots
            .Select(slot =>
            {
                var original = _state[slot.Id];
                var row = new RoutingRow(slot, original, edits is not null && edits.TryGetValue(slot.Id, out var edited) ? edited : original);
                row.Moved += OnRowMoved;
                return row;
            })
            .ToArray();
        CategoryRows = rows.Where(static r => r.Slot.Kind == NotificationSlotKind.Category).ToArray();
        SourceRows = rows.Where(static r => r.Slot.Kind == NotificationSlotKind.Source).ToArray();
        OnPropertyChanged(nameof(CategoryRows));
        OnPropertyChanged(nameof(SourceRows));
        ApplyListedSlots();

        // The settings were read from the file as it is now: note its signature, so a change to it after this is noticed (CUST-312).
        BeginTrustRead();
        Recompute();
    }

    private void OnRowMoved(object? sender, EventArgs e) => Recompute();

    /// <summary>The changes the toggles describe, the lines that say them, and the buttons that follow.</summary>
    private void Recompute()
    {
        var wanted = AllRows.Where(static r => r.IsAvailable).ToDictionary(static r => r.Slot.Id, static r => r.IsOn, StringComparer.Ordinal);
        _pending.Clear();
        _pending.AddRange(NotificationRouting.Diff(_state, wanted));

        ChangeLines.Clear();
        foreach (var change in _pending)
        {
            ChangeLines.Add(change.Describe());
        }

        HasChanges = _pending.Count > 0;
        OnPropertyChanged(nameof(Summary));
        NotifyActions();
    }

    // ------------------------------------------------------------------ the CLI's help

    protected override Task<HelpProbeResult> ReadHelpAsync(CancellationToken cancellationToken) => HelpReader(cancellationToken);

    /// <summary>
    /// Checks <c>setup notifications-set --help</c>: each slot the dialog would send must be one the CLI lists, and <c>--no-restart</c> must be
    /// an option (it is what lets the plan restart once). A screen that cannot be read leaves the plan off, with the reason.
    /// </summary>
    protected override string? ApplyHelp(HelpProbeResult result)
    {
        _listedSlots = null;
        if (!result.Succeeded)
        {
            return "The options of setup notifications-set could not be read (" + result.Error + "), so the settings cannot be checked against this DefenseClaw. Press Refresh to try again.";
        }

        var help = SetupHelpParser.Parse(result.Text);
        var slots = help.FirstChoices();
        if (slots.Count == 0)
        {
            return "The installed DefenseClaw's setup notifications-set help lists no settings to choose from, so there is nothing this dialog can change. Press Refresh to read it again.";
        }

        _listedSlots = new HashSet<string>(slots, StringComparer.Ordinal);
        _canSkipRestart = help.Lists("--no-restart");
        return null;
    }

    protected override void HelpChecked()
    {
        ApplyListedSlots();
        Recompute();
    }

    /// <summary>A slot the CLI does not list is off to the touch and never sent; before the help has been read nothing is marked.</summary>
    private void ApplyListedSlots()
    {
        foreach (var row in AllRows)
        {
            row.IsAvailable = _listedSlots is null || _listedSlots.Contains(row.Slot.Id);
        }
    }

    // ------------------------------------------------------------------ what is on and off

    protected override void NotifyActions()
    {
        RaiseGuardState();
        OnPropertyChanged(nameof(MasterTip));
        OnPropertyChanged(nameof(ReviewTip));
        OnPropertyChanged(nameof(ReviewBlocked));
        ReviewChangesCommand.NotifyCanExecuteChanged();
        ToggleMasterCommand.NotifyCanExecuteChanged();
    }

    protected override void OnDisposing()
    {
        foreach (var row in AllRows)
        {
            row.Moved -= OnRowMoved;
        }
    }

    /// <summary>
    /// Why "Review changes" is off, or null when it is on. In this order, so a control says one sentence and the first is the one that matters:
    /// the installation, the unreadable file, the settings' age or a change to config.yaml since (a refresh cures that), the CLI's help, another
    /// command, and last, nothing to apply.
    /// </summary>
    public string? ReviewBlockedReason
    {
        get
        {
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

            if (Review.IsOpen || Review.IsRunning)
            {
                return "Another command is running.";
            }

            return HasChanges ? null : "Nothing to apply: no switch differs from config.yaml.";
        }
    }

    /// <summary>"Review changes" is off.</summary>
    public bool ReviewBlocked => ReviewBlockedReason is not null;

    /// <summary>The tooltip of "Review changes": the reason it is off, or what it does.</summary>
    public string ReviewTip => ReviewBlockedReason ?? "Shows the exact commands first, one for each switch you moved. Nothing runs until you confirm.";

    private bool CanReview() => ReviewBlockedReason is null;

    /// <summary>The master switch's button: the installation, the unreadable file, the settings' age, and another command.</summary>
    private bool CanToggleMaster() => ChangesBlockedReason is null && !Review.IsOpen && !Review.IsRunning;

    // ------------------------------------------------------------------ the plan

    /// <summary>
    /// The plan for the toggles as they are: one step per change, the commands of <see cref="NotificationRouting.Argvs"/>, and the sentences
    /// that say what running them does. Null when nothing changed.
    /// </summary>
    internal SetupPlan? BuildPlan()
    {
        if (_pending.Count == 0)
        {
            return null;
        }

        var argvs = NotificationRouting.Argvs(_pending, RestartAfter, _canSkipRestart);
        var steps = _pending
            .Select((change, i) => new DiscoverStep(
                argvs[i],
                $"Turn “{change.Slot.Label}” {change.Value}.",
                CommandTier.StateChanging))
            .ToArray();

        var restart = RestartAfter
            ? (_canSkipRestart || _pending.Count == 1
                ? "The gateway restarts once, after the last command, because it reads these settings only when it starts."
                : "The installed DefenseClaw has no --no-restart for these commands, so each of them restarts the gateway.")
            : "The gateway is not restarted, so the changes are saved but not in effect until it next restarts.";

        var warnings = new List<CommandReviewWarning>();
        if (!RestartAfter)
        {
            warnings.Add(new CommandReviewWarning(
                "Gateway not restarted",
                "The gateway is not restarted, so the new settings are saved but not in effect until it next restarts."));
        }
        else if (!_canSkipRestart && _pending.Count > 1)
        {
            warnings.Add(new CommandReviewWarning(
                "One restart per command",
                $"This DefenseClaw's setup notifications-set has no --no-restart, so the gateway restarts {_pending.Count.ToString(CultureInfo.CurrentCulture)} times, once after each command."));
        }

        if (!MasterOn)
        {
            warnings.Add(new CommandReviewWarning(
                "Desktop notifications are off",
                "The master switch is off, so these changes are saved but nothing is raised until you turn it on."));
        }

        return new SetupPlan
        {
            Title = "Update notification routing?",
            Summary =
                $"Changes which events DefenseClaw's gateway raises a desktop notification for: {(_pending.Count == 1 ? "one setting" : _pending.Count.ToString(CultureInfo.CurrentCulture) + " settings")} " +
                $"in the notifications block of config.yaml, one command each, in the order shown. {restart} " +
                "These are the gateway's own notifications; the alerts this app shows from the tray are a separate setting and do not change.",
            Steps = steps,
            RestartsGateway = RestartAfter,
            Warnings = warnings,
            PrimaryText = _pending.Count == 1 ? "Apply change" : $"Apply {_pending.Count.ToString(CultureInfo.CurrentCulture)} changes",
        };
    }

    /// <summary>Opens the review of the moved switches. Nothing runs from here: the commands start when the operator confirms them.</summary>
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
        _planChanges = _pending.ToArray();
        Review.OpenPlan(plan, AfterPlanAsync);
    }

    /// <summary>
    /// The plan ran (or failed on its way): read config.yaml again, start the toggles from what it says now - a change that did not get in
    /// shows as it is, not as it was asked for - and say which changes were applied and which were not.
    /// </summary>
    private Task AfterPlanAsync(DiscoverReviewResult result)
    {
        var changes = _planChanges;
        Services.ReloadConfig();
        Seed(keepEdits: false);

        var applied = new List<string>();
        var notApplied = new List<string>();
        for (var i = 0; i < changes.Count; i++)
        {
            var outcome = i < result.Outcomes.Count ? result.Outcomes[i] : null;
            (outcome?.State == DiscoverStepState.Succeeded ? applied : notApplied).Add(changes[i].Describe());
        }

        var parts = new List<string>();
        if (applied.Count > 0)
        {
            parts.Add("Applied: " + string.Join("; ", applied) + ".");
        }

        if (notApplied.Count > 0)
        {
            parts.Add("Not applied: " + string.Join("; ", notApplied) + ". " + PlanReport.Describe(result.Outcomes));
        }

        ResultKey = result.Succeeded ? "Ok" : "Bad";
        ResultText = string.Join(' ', parts).Trim();
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------ the master switch

    /// <summary>
    /// The master switch: the Overview's reviewed action (<see cref="NotificationSwitch"/>), with its question, consequence text and argv. The
    /// toggles the operator has moved are kept (the switch does not touch a slot).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanToggleMaster))]
    private void ToggleMaster()
    {
        _ = Trust.CheckConfig();
        if (!CanToggleMaster())
        {
            NotifyActions();
            return;
        }

        var turnOn = !MasterOn;
        ResultText = string.Empty;
        NotificationSwitch.Open(Review, turnOn, onFinished: result => AfterMasterAsync(result, turnOn));
    }

    private Task AfterMasterAsync(DiscoverReviewResult result, bool turnedOn)
    {
        Services.ReloadConfig();
        Seed(keepEdits: true);

        ResultKey = result.Succeeded ? "Ok" : "Bad";
        ResultText = result.Succeeded
            ? $"Desktop notifications are now {(turnedOn ? "on" : "off")}."
            : $"Desktop notifications were not turned {(turnedOn ? "on" : "off")}. The review and the Activity panel have the output.";
        return Task.CompletedTask;
    }
}
