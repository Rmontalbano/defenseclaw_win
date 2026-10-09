using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.FirstRun;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Config;
using DefenseClaw.Core.Setup;

namespace DefenseClaw.App.ViewModels;

/// <summary>One connector the batch dialog offers: whether it is ticked, and whether config.yaml has it now.</summary>
public sealed partial class BatchRow : ObservableObject
{
    internal BatchRow(OfferedConnector connector, bool configured, string? configuredMode)
    {
        Connector = connector;
        Configured = configured;
        ConfiguredMode = configuredMode;
        _isOn = configured;
    }

    internal OfferedConnector Connector { get; }

    public string Id => Connector.Id;

    public string Label => Connector.Label;

    /// <summary>"not certified on Windows" and the like; empty for a connector with nothing to say.</summary>
    public string Caution => Connector.Caution;

    public bool HasCaution => Connector.HasCaution;

    /// <summary>config.yaml has the connector in <c>guardrail.connectors</c> now.</summary>
    public bool Configured { get; }

    /// <summary>The mode config.yaml gives it today (<c>observe</c>, <c>action</c>), or null.</summary>
    public string? ConfiguredMode { get; }

    public string StateText => Configured
        ? (ConfiguredMode is { Length: > 0 } mode ? $"Configured now ({mode})" : "Configured now")
        : "Not configured";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChanged), nameof(ChangeText), nameof(AutomationName))]
    private bool _isOn;

    public bool IsChanged => IsOn != Configured;

    public string ChangeText => !IsChanged ? string.Empty : IsOn ? "will be added" : "will be dropped";

    public string AutomationName => $"{Label}: {(IsOn ? "selected" : "not selected")}. {StateText}." + (IsChanged ? " " + ChangeText + "." : string.Empty);

    internal event EventHandler? Moved;

    partial void OnIsOnChanged(bool value) => Moved?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// The connector batch dialog (CUST-271): <c>defenseclaw setup --yes --connector X --connector Y [--detected | --all] --mode observe|action
/// [--no-restart]</c> - several connectors configured by one reviewed command, from the Setup hub's connector roster. The TUI's connector picker
/// and the Mac's multi-select, in this app's model: the form starts with the connectors config.yaml has ticked (so doing nothing drops nothing),
/// <b>Review</b> shows the one command with every flag, and the shared <see cref="DiscoverActionReview"/> runs it when confirmed.
/// <para>
/// <b>The batch sets the roster; it does not add to it.</b> The CLI replaces the configured set with the selection (<see cref="ConnectorBatch"/>), so a
/// configured connector that is unticked is dropped, and the review says which. Flags are checked against the installed CLI's own
/// <c>setup --help</c> (<c>--yes</c>, <c>--connector</c> and <c>--mode</c> must be there; <c>--detected</c>, <c>--all</c> and <c>--no-restart</c> are offered only if they are);
/// a help that cannot be read leaves the review off. Guards are <see cref="SetupDialogViewModel"/>'s.
/// </para>
/// </summary>
public sealed partial class ConnectorBatchViewModel : SetupDialogViewModel
{
    private static readonly string[] SetupPath = Array.Empty<string>();

    private static readonly string[] ExtraLabelsAll =
    {
        "Only the connectors ticked above",
        "Also every connector detected on this machine",
        "Also every supported connector",
    };

    private HashSet<string>? _listed;
    private IReadOnlyList<string> _roster = Array.Empty<string>();
    private IReadOnlyList<string> _planArgv = Array.Empty<string>();

    /// <param name="services">The composition the dialog works in.</param>
    /// <param name="helpReader">Reads <c>setup --help</c>; null: the Setup help probe (cached, no Activity entry). A test hands it a screen.</param>
    /// <param name="offered">The connectors to offer; null: the Setup catalog's connector cards, filtered for Windows (<see cref="ConnectorOnboarding.Offerable"/>).</param>
    /// <param name="applied">What the hosting page does after a run (reads the roster again).</param>
    public ConnectorBatchViewModel(
        AppServices services,
        Func<CancellationToken, Task<HelpProbeResult>>? helpReader = null,
        Func<IReadOnlyList<OfferedConnector>>? offered = null,
        Func<DiscoverReviewResult, Task>? applied = null)
        : base(services, "this connector list was read")
    {
        HelpReader = helpReader ?? (ct => WizardCatalog.Shared(Services).Probe.HelpAsync(SetupPath, ct));
        Offered = offered ?? DefaultOffered;
        Applied = applied;
    }

    internal Func<CancellationToken, Task<HelpProbeResult>> HelpReader { get; set; }

    internal Func<IReadOnlyList<OfferedConnector>> Offered { get; set; }

    internal Func<DiscoverReviewResult, Task>? Applied { get; set; }

    private IReadOnlyList<OfferedConnector> DefaultOffered()
    {
        var catalog = WizardCatalog.Shared(Services);
        var known = ConnectorOnboarding.Offerable(null)
            .Select(o => catalog.Find(o.Alias))
            .Where(static d => d is not null)
            .Cast<WizardDefinition>()
            .ToList();
        return ConnectorOnboarding.Offerable(known);
    }

    // ------------------------------------------------------------------ the form

    public string Title => "Set up several connectors";

    public string Subtitle => "Configure more than one agent in a single command. Nothing changes until you review the command and confirm it.";

    /// <summary>The connectors to tick, in the order the first-run window lists them.</summary>
    public ObservableCollection<BatchRow> Rows { get; } = new();

    public IReadOnlyList<string> ExtraChoices => ExtraLabelsAll;

    [ObservableProperty]
    private int _extraIndex;

    public IReadOnlyList<string> Modes => ConnectorBatch.Modes;

    [ObservableProperty]
    private string _mode = ConnectorBatch.ObserveMode;

    [ObservableProperty]
    private bool _restartAfter = true;

    /// <summary>The listed help has <c>--no-restart</c>: the box can be unticked.</summary>
    [ObservableProperty]
    private bool _canSkipRestart = true;

    public string RestartNote => RestartAfter
        ? "The gateway restarts once, at the end, so the new connectors' hooks come up."
        : "The gateway is not restarted, so nothing changes in the running gateway until it is restarted.";

    public string ModeNote => Mode == ConnectorBatch.ActionMode
        ? "Action lets DefenseClaw block. If the CLI cannot verify a connector for action mode it configures observe for that one and says so."
        : "Observe records what an agent does and never blocks it.";

    public string RosterLine => _roster.Count == 0
        ? "Configured now: none."
        : "Configured now: " + string.Join(", ", _roster.Select(Label)) + ".";

    private static string Label(string id) => ConnectorOnboarding.Label(id) is { Length: > 0 } label ? label : id;

    partial void OnExtraIndexChanged(int value) => Recompute();

    partial void OnModeChanged(string value)
    {
        OnPropertyChanged(nameof(ModeNote));
        Recompute();
    }

    partial void OnRestartAfterChanged(bool value)
    {
        OnPropertyChanged(nameof(RestartNote));
        Recompute();
    }

    private BatchExtra Extra => ExtraIndex switch
    {
        1 => BatchExtra.Detected,
        2 => BatchExtra.All,
        _ => BatchExtra.None,
    };

    // ------------------------------------------------------------------ seeding

    protected override void Seed(bool keepEdits)
    {
        var reader = ConfigYamlReader.From(Services.Config);
        _roster = SetupStateLines.ActiveConnectors(reader);

        var typed = keepEdits ? Rows.ToDictionary(static r => r.Id, static r => r.IsOn, StringComparer.Ordinal) : null;
        foreach (var row in Rows)
        {
            row.Moved -= OnRowMoved;
        }

        Rows.Clear();
        foreach (var offered in Offered())
        {
            var key = reader.Keys("guardrail", "connectors")
                .FirstOrDefault(k => SetupStateLines.Id(k) == offered.Id);
            var configured = key is not null || _roster.Contains(offered.Id, StringComparer.Ordinal);
            var mode = key is null ? null : reader.Text("guardrail", "connectors", key, "mode");
            var row = new BatchRow(offered, configured, mode);
            if (typed is not null && typed.TryGetValue(offered.Id, out var on))
            {
                row.IsOn = on;
            }

            row.Moved += OnRowMoved;
            Rows.Add(row);
        }

        if (!keepEdits)
        {
            ExtraIndex = 0;
            Mode = ConnectorBatch.ObserveMode;
        }

        OnPropertyChanged(nameof(RosterLine));
        BeginTrustRead();
        Recompute();
    }

    private void OnRowMoved(object? sender, EventArgs e) => Recompute();

    // ------------------------------------------------------------------ the CLI's help

    protected override Task<HelpProbeResult> ReadHelpAsync(CancellationToken cancellationToken) => HelpReader(cancellationToken);

    /// <summary><c>--yes</c>, <c>--connector</c> and <c>--mode</c> are what the command cannot do without; the rest are offered when listed.</summary>
    protected override string? ApplyHelp(HelpProbeResult result)
    {
        _listed = null;
        if (!result.Succeeded)
        {
            return "The options of setup could not be read (" + result.Error + "), so the batch cannot be checked against this DefenseClaw. Press Refresh to try again.";
        }

        var help = SetupHelpParser.Parse(result.Text, commandDepth: 1);
        var listed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var flag in ConnectorBatch.Flags)
        {
            if (help.Lists(flag))
            {
                _ = listed.Add(flag);
            }
        }

        var missing = new[] { "--yes", "--connector", "--mode" }.Where(f => !listed.Contains(f)).ToArray();
        if (missing.Length > 0)
        {
            return "The installed DefenseClaw's setup does not list " + string.Join(", ", missing) +
                   ", so it has no batch form this app can send. Use the connector tiles below one at a time.";
        }

        _listed = listed;
        CanSkipRestart = listed.Contains("--no-restart");
        return null;
    }

    protected override void HelpChecked()
    {
        if (!CanSkipRestart)
        {
            RestartAfter = true;
        }

        Recompute();
    }

    private bool IsListed(string flag) => _listed is null || _listed.Contains(flag);

    // ------------------------------------------------------------------ what the form says

    private IEnumerable<string> Selected => Rows.Where(static r => r.IsOn).Select(static r => r.Id);

    public ObservableCollection<string> ChangeLines { get; } = new();

    [ObservableProperty]
    private string _commandPreview = string.Empty;

    [ObservableProperty]
    private bool _hasChanges;

    public bool HasCommandPreview => CommandPreview.Length > 0;

    [ObservableProperty]
    private string _summary = "Nothing to apply";

    /// <summary>Roster members the batch would drop: configured now, and not among the ticked ones (when nothing is added by <c>--detected</c>/<c>--all</c>).</summary>
    private IReadOnlyList<string> Dropped() =>
        _roster.Where(r => !Selected.Contains(r, StringComparer.Ordinal)).ToArray();

    private void Recompute()
    {
        var selected = Selected.ToArray();
        var extra = Extra;

        ChangeLines.Clear();
        var argv = ConnectorBatch.Argv(selected, extra, Mode, RestartAfter || !CanSkipRestart);
        var rosterSet = new HashSet<string>(_roster, StringComparer.Ordinal);
        var added = selected.Where(s => !rosterSet.Contains(s)).ToArray();
        var modeMoves = Rows
            .Where(r => r.IsOn && r.Configured && !string.Equals(r.ConfiguredMode ?? ConnectorBatch.ObserveMode, Mode, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var dropped = extra == BatchExtra.None ? Dropped() : Array.Empty<string>();

        foreach (var id in added)
        {
            ChangeLines.Add($"Add {Label(id)} in {Mode} mode");
        }

        foreach (var row in modeMoves)
        {
            ChangeLines.Add($"{row.Label}: {row.ConfiguredMode ?? "observe"} to {Mode}");
        }

        foreach (var id in dropped)
        {
            ChangeLines.Add($"Drop {Label(id)}");
        }

        if (extra == BatchExtra.Detected)
        {
            ChangeLines.Add("Also every connector detected on this machine");
        }
        else if (extra == BatchExtra.All)
        {
            ChangeLines.Add("Also every supported connector");
        }

        HasChanges = argv is not null && (added.Length > 0 || modeMoves.Length > 0 || dropped.Count > 0 || extra != BatchExtra.None);
        CommandPreview = HasChanges && argv is not null ? CommandReview.CommandLine(CommandReview.DefaultExecutable, argv) : string.Empty;
        Summary = !HasChanges
            ? "Nothing to apply"
            : ChangeLines.Count == 1 ? "1 change to apply" : $"{ChangeLines.Count} changes to apply";
        OnPropertyChanged(nameof(HasCommandPreview));
        NotifyActions();
    }

    protected override void NotifyActions()
    {
        RaiseGuardState();
        OnPropertyChanged(nameof(ReviewTip));
        OnPropertyChanged(nameof(ReviewBlocked));
        OnPropertyChanged(nameof(CanChooseDetected));
        OnPropertyChanged(nameof(CanChooseAll));
        ReviewChangesCommand.NotifyCanExecuteChanged();
    }

    protected override void OnDisposing()
    {
        foreach (var row in Rows)
        {
            row.Moved -= OnRowMoved;
        }
    }

    public bool CanChooseDetected => IsListed("--detected");

    public bool CanChooseAll => IsListed("--all");

    /// <summary>Why "Review" is off, in this order, or null: the shared guards, the help, another command, nothing chosen, a flag the CLI lacks, nothing to apply.</summary>
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

            if (Rows.Count == 0)
            {
                return "No connector can be set up on this machine from here.";
            }

            if (!Selected.Any() && Extra == BatchExtra.None)
            {
                return "Tick at least one connector, or choose detected or supported connectors: with none the CLI would open a picker this app cannot answer.";
            }

            if (Extra == BatchExtra.Detected && !IsListed("--detected"))
            {
                return "The installed DefenseClaw's setup does not list --detected.";
            }

            if (Extra == BatchExtra.All && !IsListed("--all"))
            {
                return "The installed DefenseClaw's setup does not list --all.";
            }

            return HasChanges ? null : "Nothing to apply: the ticked connectors are the ones configured now, in the mode chosen.";
        }
    }

    public bool ReviewBlocked => ReviewBlockedReason is not null;

    public string ReviewTip => ReviewBlockedReason ?? "Shows the exact command first. Nothing runs until you confirm.";

    // ------------------------------------------------------------------ the plan

    /// <summary>The plan for the form as it is: one step, the batch command, and what it does to the roster. Null when it selects nothing.</summary>
    internal SetupPlan? BuildPlan()
    {
        var restart = RestartAfter || !CanSkipRestart;
        var argv = ConnectorBatch.Argv(Selected, Extra, Mode, restart);
        if (argv is null)
        {
            return null;
        }

        _planArgv = argv;
        var dropped = Extra == BatchExtra.None ? Dropped() : Array.Empty<string>();
        var picked = Selected.Select(Label).ToArray();
        var what = Extra switch
        {
            BatchExtra.Detected => (picked.Length == 0 ? string.Empty : string.Join(", ", picked) + " and ") + "every connector detected on this machine",
            BatchExtra.All => (picked.Length == 0 ? string.Empty : string.Join(", ", picked) + " and ") + "every supported connector",
            _ => string.Join(", ", picked),
        };

        var warnings = new List<CommandReviewWarning>();
        if (dropped.Count > 0)
        {
            warnings.Add(new CommandReviewWarning(
                "Drops configured connectors",
                "The batch sets the connector roster to the selection. Not selected, so no longer watched: " + string.Join(", ", dropped.Select(Label)) + "."));
        }
        else if (Extra != BatchExtra.None)
        {
            warnings.Add(new CommandReviewWarning(
                "Sets the roster",
                "The batch sets the connector roster to the selection, so a configured connector that is not among them is dropped."));
        }

        if (Mode == ConnectorBatch.ActionMode)
        {
            warnings.Add(new CommandReviewWarning(
                "Action mode can block",
                "In action mode DefenseClaw can block an agent's tool calls. A connector the CLI cannot verify for action mode is configured in observe mode instead."));
        }

        var uncertified = Rows.Where(static r => r.IsOn && r.HasCaution).Select(static r => r.Label).ToArray();
        if (uncertified.Length > 0)
        {
            warnings.Add(new CommandReviewWarning(
                "Not certified on Windows",
                string.Join(", ", uncertified) + (uncertified.Length == 1 ? " is" : " are") + " not certified on Windows. They can be added; expect rough edges."));
        }

        if (!restart)
        {
            warnings.Add(new CommandReviewWarning(
                "Gateway not restarted",
                "The gateway is not restarted, so the connectors' hooks are not live until it is."));
        }

        return new SetupPlan
        {
            Title = "Set up these connectors?",
            Summary = $"Configures {what} in {Mode} mode in one command" +
                      (restart ? ", then restarts the gateway once." : ", without restarting the gateway."),
            Steps = new[]
            {
                new DiscoverStep(
                    argv,
                    "Configure several connectors as one batch.",
                    CommandTier.StateChanging,
                    TimeSpan.FromMinutes(5)),
            },
            RestartsGateway = restart,
            Warnings = warnings,
            PrimaryText = "Set up",
        };
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand(CanExecute = nameof(CanReview))]
    private void ReviewChanges()
    {
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

    private bool CanReview() => ReviewBlockedReason is null;

    private async Task AfterPlanAsync(DiscoverReviewResult result)
    {
        Services.ReloadConfig();
        Seed(keepEdits: false);

        ResultKey = result.Succeeded ? "Ok" : "Bad";
        ResultText = result.Succeeded
            ? "Done. " + RosterLine
            : "Not applied. " + PlanReport.Describe(result.Outcomes) + " The review and the Activity panel have the output.";
        ResultText = ResultText.Replace("  ", " ", StringComparison.Ordinal).Trim();

        if (Applied is { } applied)
        {
            await applied(result).ConfigureAwait(true);
        }
    }

    /// <summary>The argv the last review opened with, for a test.</summary>
    internal IReadOnlyList<string> LastArgv => _planArgv;
}
