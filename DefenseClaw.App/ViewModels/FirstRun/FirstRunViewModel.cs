using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.FirstRun;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.Core.Install;
using DefenseClaw.Core.Setup;

namespace DefenseClaw.App.ViewModels.FirstRun;

/// <summary>One of the three state lines at the top of the first-run window.</summary>
public sealed record FirstRunCheckRow(string Label, string Text, string Key, string Detail)
{
    /// <summary>The screen-reader sentence for the row.</summary>
    public override string ToString() => ServiceRow.JoinSentences(Label, Text, Detail);
}

/// <summary>One value of a form drop-down.</summary>
public sealed record FirstRunChoice(string Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A detected connector with its two check boxes: register DefenseClaw for it, and (Action profile) enforce on it.</summary>
public sealed partial class FirstRunConnectorChoice : ObservableObject
{
    public FirstRunConnectorChoice(OfferedConnector connector)
    {
        Connector = connector;
    }

    public OfferedConnector Connector { get; }

    public string Id => Connector.Id;

    public string Label => Connector.Label;

    public string Caution => Connector.Caution;

    public bool HasCaution => Connector.HasCaution;

    [ObservableProperty]
    private bool _isRegistered = true;

    [ObservableProperty]
    private bool _isAction;

    public override string ToString() => Label;
}

/// <summary>One step of the report of the last run, as the window lists it under the verdict.</summary>
public sealed record FirstRunReportRow(string Name, string Status, string Key, string Detail)
{
    public bool HasDetail => Detail.Length > 0;

    public override string ToString() => ServiceRow.JoinSentences(Name, Status, Detail);
}

/// <summary>
/// First-run guided setup (CUST-210), the Mac's first-run sheet on this app's safety model.
/// <para>
/// <b>Never automatic.</b> The state lines come from the monitor's snapshot (no probe of its own); agent detection is a button that reads the
/// AI-discovery scan the gateway already wrote (<c>ai_discovery_state.json</c>: a file read, nothing executed, where the Mac runs
/// <c>agent discover</c>, which executes each agent's <c>--version</c>); the form builds a plan, the plan opens the shared review overlay with
/// the exact argv, and only the operator's confirm runs it. What runs is <see cref="InitPlanBuilder"/>'s plan through
/// <see cref="DiscoverActionReview"/> and so <c>CliRunner</c>: each command lands in Activity, a step runs only if the one before it
/// succeeded, and the <c>init</c> step counts as failed unless its own JSON report says it finished (<see cref="InitReportValidator"/>).
/// </para>
/// </summary>
public sealed partial class FirstRunViewModel : ObservableObject, IDisposable
{
    private readonly AppServices _services;
    private readonly Func<CancellationToken, Task<IReadOnlyList<WizardDefinition>>> _loadCatalog;
    private readonly CancellationTokenSource _lifetime = new();
    private IReadOnlyList<OfferedConnector> _offerable = ConnectorOnboarding.Offerable(null);
    private bool _disposed;

    /// <param name="loadCatalog">Reads the Setup catalog (default: the shared one, whose <c>setup --help</c> probes are read-only and cached); a test passes its own.</param>
    /// <param name="snapshot">The state to start from (default: the monitor's current one).</param>
    public FirstRunViewModel(
        AppServices services,
        Func<CancellationToken, Task<IReadOnlyList<WizardDefinition>>>? loadCatalog = null,
        GatewaySnapshot? snapshot = null)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _loadCatalog = loadCatalog ?? (ct => WizardCatalog.Shared(services).LoadAsync(ct));
        Review = new DiscoverActionReview(services);
        Review.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DiscoverActionReview.IsRunning))
            {
                OnPropertyChanged(nameof(IsBusy));
                Recompute();
            }
        };

        ApplyOffered();
        ApplySnapshot(snapshot ?? services.Monitor.Current);
        Recompute();
    }

    public DiscoverActionReview Review { get; }

    public string Title => "Set up DefenseClaw";

    public ObservableCollection<FirstRunCheckRow> Checks { get; } = new();

    public ObservableCollection<FirstRunConnectorChoice> Detected { get; } = new();

    /// <summary>The connectors offered by the fallback picker (shown while nothing is detected).</summary>
    public ObservableCollection<OfferedConnector> Fallbacks { get; } = new();

    public ObservableCollection<FirstRunReportRow> ReportRows { get; } = new();

    public IReadOnlyList<FirstRunChoice> Profiles { get; } = new FirstRunChoice[]
    {
        new("observe", "Observe - detect and log"),
        new("action", "Action - enforce policy"),
    };

    public IReadOnlyList<FirstRunChoice> ScannerModes { get; } = new FirstRunChoice[]
    {
        new("local", "Local"),
        new("remote", "Remote"),
        new("both", "Both"),
    };

    public IReadOnlyList<FirstRunChoice> FailModes { get; } = new FirstRunChoice[]
    {
        new("open", "Open - allow and log"),
        new("closed", "Closed - block"),
    };

    public IReadOnlyList<string> Severities { get; } = new[] { "CRITICAL", "HIGH", "MEDIUM", "LOW" };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Subtitle))]
    private bool _isInstalled;

    [ObservableProperty]
    private bool _isInitialized;

    [ObservableProperty]
    private string _profile = "observe";

    [ObservableProperty]
    private string _scannerMode = "local";

    [ObservableProperty]
    private bool _llmJudge;

    [ObservableProperty]
    private string _failMode = "open";

    [ObservableProperty]
    private bool _humanApproval;

    [ObservableProperty]
    private string _hiltSeverity = "HIGH";

    [ObservableProperty]
    private bool _startGateway = true;

    [ObservableProperty]
    private bool _verify = true;

    [ObservableProperty]
    private OfferedConnector? _fallback;

    [ObservableProperty]
    private bool _isDetecting;

    [ObservableProperty]
    private bool _hasDetected;

    [ObservableProperty]
    private bool _hasDetectionNote;

    /// <summary>What detection found or why it found nothing; the button's caption turns into "Detect again" after the first press.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetectLabel))]
    private string _detectionNote = string.Empty;

    private bool _detectionRequested;

    [ObservableProperty]
    private string _selectionProblem = string.Empty;

    [ObservableProperty]
    private bool _hasSelectionProblem;

    [ObservableProperty]
    private bool _isActionProfile;

    [ObservableProperty]
    private bool _canReview;

    [ObservableProperty]
    private bool _isChecking;

    [ObservableProperty]
    private bool _hasReport;

    [ObservableProperty]
    private string _reportText = string.Empty;

    [ObservableProperty]
    private string _reportKey = "Neutral";

    [ObservableProperty]
    private string _reportNext = string.Empty;

    [ObservableProperty]
    private bool _hasReportNext;

    /// <summary>The report as text for the selectable, copyable box: the steps that did not pass, then what to run next. Empty when there is neither.</summary>
    [ObservableProperty]
    private string _reportDetailText = string.Empty;

    public bool HasReportDetail => ReportDetailText.Length > 0;

    partial void OnReportDetailTextChanged(string value) => OnPropertyChanged(nameof(HasReportDetail));

    public string DetectLabel => _detectionRequested ? "Detect again" : "Detect installed agents";

    public string Subtitle => IsInstalled
        ? "DefenseClaw registers the hook connectors you select. Observe mode records and never blocks; Action enforces policy on the connectors you choose."
        : "Install the DefenseClaw runtime first, then return here to configure it.";

    /// <summary>Closes the window (raised for "Continue without setup").</summary>
    public event EventHandler? CloseRequested;

    public async Task InitializeAsync()
    {
        if (!IsInstalled)
        {
            return;
        }

        await LoadCatalogAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Reads the CLI's setup catalog (read-only <c>setup --help</c> probes, cached) so the connector list follows the installed CLI's own
    /// platform answers. Until it lands, and if it fails, the built-in list stands.
    /// </summary>
    private async Task LoadCatalogAsync()
    {
        try
        {
            var definitions = await _loadCatalog(_lifetime.Token).ConfigureAwait(true);
            _offerable = ConnectorOnboarding.Offerable(definitions);
            ApplyOffered();
        }
        catch (OperationCanceledException)
        {
            // The window closed.
        }
#pragma warning disable CA1031 // A catalog that cannot be read leaves the built-in list; it must not take the window down.
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"First run: could not read the setup catalog: {ex.Message}");
        }
#pragma warning restore CA1031
    }

    private void ApplyOffered()
    {
        Fallbacks.Clear();
        foreach (var offered in _offerable)
        {
            Fallbacks.Add(offered);
        }

        Fallback = Fallbacks.FirstOrDefault(f => f.Id == Fallback?.Id) ?? Fallbacks.FirstOrDefault(static f => f.Id == "codex") ?? Fallbacks.FirstOrDefault();

        // A detected connector the catalog now says cannot work here leaves the list.
        for (var i = Detected.Count - 1; i >= 0; i--)
        {
            if (_offerable.All(o => o.Id != Detected[i].Id))
            {
                Detected.RemoveAt(i);
            }
        }

        HasDetected = Detected.Count > 0;
    }

    /// <summary>Runtime / Configuration / Gateway from the monitor's snapshot: what is true now, not a fresh probe.</summary>
    private void ApplySnapshot(GatewaySnapshot snapshot)
    {
        var install = snapshot.Install;
        var known = install is not null;
        IsInstalled = install is not null && install != InstallState.NotInstalled;
        IsInitialized = install is InstallState.GatewayStopped or InstallState.Running;

        Checks.Clear();
        Checks.Add(Row("Runtime", known, IsInstalled, snapshot.CliPath ?? string.Empty, "found", "not found"));
        Checks.Add(Row("Configuration", known, IsInitialized, string.Empty, "initialized", "not initialized"));
        Checks.Add(Row("Gateway", known, snapshot.IsRunning, string.Empty, "running", "not running"));
    }

    private static FirstRunCheckRow Row(string label, bool known, bool ok, string detail, string yes, string no) =>
        !known
            ? new FirstRunCheckRow(label, "checking", "Neutral", detail)
            : new FirstRunCheckRow(label, ok ? yes : no, ok ? "Ok" : "Neutral", detail);

    partial void OnProfileChanged(string value) => Recompute();

    partial void OnHumanApprovalChanged(bool value) => Recompute();

    partial void OnFallbackChanged(OfferedConnector? value) => Recompute();

    partial void OnIsDetectingChanged(bool value) => Recompute();

    partial void OnIsInstalledChanged(bool value) => Recompute();

    /// <summary>Validity of the form and what the Review button may do. No I/O.</summary>
    private void Recompute()
    {
        IsActionProfile = Profile == "action";

        var problem = string.Empty;
        if (!IsInstalled)
        {
            problem = string.Empty;
        }
        else if (Detected.Count > 0)
        {
            var registered = Detected.Where(static d => d.IsRegistered).ToList();
            if (registered.Count == 0)
            {
                problem = "Select at least one connector to register.";
            }
            else if (IsActionProfile && registered.All(static d => !d.IsAction))
            {
                problem = "Select at least one connector for Action mode.";
            }
        }
        else if (Fallback is null)
        {
            problem = "No connector is available to configure on this machine.";
        }

        SelectionProblem = problem;
        HasSelectionProblem = problem.Length > 0;
        CanReview = IsInstalled && !HasSelectionProblem && !IsDetecting && !Review.IsRunning;
        RunCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The plan the form describes, as it would be reviewed. Pure of side effects; the review opens it.</summary>
    internal InitPlan BuildPlan()
    {
        var registered = Detected.Where(static d => d.IsRegistered).Select(static d => d.Id).ToHashSet(StringComparer.Ordinal);
        var action = Detected.Where(static d => d.IsRegistered && d.IsAction).Select(static d => d.Id).ToHashSet(StringComparer.Ordinal);

        return InitPlanBuilder.Build(new InitPlanOptions
        {
            Detected = Detected.Select(static d => d.Id).ToArray(),
            Registered = registered,
            Action = action,
            FallbackConnector = Fallback?.Id ?? "codex",
            Profile = Profile,
            ScannerMode = ScannerMode,
            LlmJudge = LlmJudge,
            FailMode = FailMode,
            HumanApproval = HumanApproval,
            HiltSeverity = HiltSeverity,
            StartGateway = StartGateway,
            Verify = Verify,
        });
    }

    // ---- Commands ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Detect installed agents: an explicit button, never run on its own. Reads the last AI-discovery scan (<c>ai_discovery_state.json</c>) and
    /// keeps the connectors that are offered here; executes nothing.
    /// </summary>
    [RelayCommand]
    private async Task DetectAsync()
    {
        if (IsDetecting)
        {
            return;
        }

        IsDetecting = true;
        _detectionRequested = true;
        OnPropertyChanged(nameof(DetectLabel));
        try
        {
            var path = _services.Paths.AiDiscoveryStatePath;
            IReadOnlyList<string> found;
            string note;
            if (!File.Exists(path))
            {
                found = Array.Empty<string>();
                note = "No AI discovery scan has been recorded yet, so there is nothing to read. Choose a connector below. After setup, " +
                       "the gateway's scan fills AI Discovery and Detect will find agents.";
            }
            else
            {
                try
                {
                    found = await Task.Run(() => ConnectorOnboarding.ParseDetected(File.ReadAllText(path)), _lifetime.Token).ConfigureAwait(true);
                    note = string.Empty;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    found = Array.Empty<string>();
                    note = $"ai_discovery_state.json could not be read: {ex.Message}";
                }
            }

            ApplyDetected(found);
            if (note.Length == 0)
            {
                note = Detected.Count == 0
                    ? "The last AI discovery scan did not identify a connector that can be set up here. Choose one below."
                    : $"Found {Detected.Count} installed agent{(Detected.Count == 1 ? string.Empty : "s")} in the last AI discovery scan. They are pre-selected.";
            }

            DetectionNote = note;
            HasDetectionNote = true;
        }
        catch (OperationCanceledException)
        {
            // The window closed mid-read.
        }
        finally
        {
            IsDetecting = false;
        }
    }

    /// <summary>Replaces the detected list, keeping the operator's choices for connectors that are still there and ticking new ones.</summary>
    internal void ApplyDetected(IReadOnlyList<string> found)
    {
        var offered = found
            .Select(id => _offerable.FirstOrDefault(o => o.Id == id))
            .Where(static o => o is not null)
            .Cast<OfferedConnector>()
            .ToList();

        var previous = Detected.ToDictionary(static d => d.Id, StringComparer.Ordinal);
        foreach (var choice in Detected)
        {
            choice.PropertyChanged -= OnChoiceChanged;
        }

        Detected.Clear();
        foreach (var connector in offered)
        {
            var choice = new FirstRunConnectorChoice(connector);
            if (previous.TryGetValue(connector.Id, out var old))
            {
                choice.IsRegistered = old.IsRegistered;
                choice.IsAction = old.IsAction;
            }

            choice.PropertyChanged += OnChoiceChanged;
            Detected.Add(choice);
        }

        HasDetected = Detected.Count > 0;
        Recompute();
    }

    private void OnChoiceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is FirstRunConnectorChoice { IsRegistered: false, IsAction: true } choice)
        {
            choice.IsAction = false;
        }

        Recompute();
    }

    /// <summary>Re-reads the monitor (and so the install state) after the operator installed or initialized something outside this window.</summary>
    [RelayCommand]
    private async Task CheckAgainAsync()
    {
        if (IsChecking)
        {
            return;
        }

        IsChecking = true;
        try
        {
            var snapshot = await _services.Monitor.RefreshAsync(_lifetime.Token).ConfigureAwait(true);
            ApplySnapshot(snapshot);
            Recompute();
            if (IsInstalled)
            {
                await LoadCatalogAsync().ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
            // The window closed.
        }
        finally
        {
            IsChecking = false;
        }
    }

    /// <summary>Builds the plan and opens the shared review: the exact commands, the strictest tier, a confirm that must be pressed.</summary>
    [RelayCommand(CanExecute = nameof(CanReview))]
    private void Run()
    {
        var plan = BuildPlan();
        var cautions = plan.Connectors
            .Select(id => _offerable.FirstOrDefault(o => o.Id == id))
            .Where(static o => o is { HasCaution: true })
            .Select(static o => o!.Caution)
            .ToList();

        HasReport = false;
        Review.Open(
            plan.Title,
            plan.Summary,
            plan.Steps,
            onFinished: OnReviewFinishedAsync,
            warning: cautions.Count == 0 ? null : string.Join(' ', cautions),
            primaryText: "Set up DefenseClaw");
    }

    private async Task OnReviewFinishedAsync(DiscoverReviewResult result)
    {
        // The first step is init: read its report for the verdict list. (A step that did not run leaves no invocation.)
        if (result.Invocations.Count > 0)
        {
            ShowReport(InitReportValidator.Validate(DiscoverCli.Stdout(result.Invocations[0])), result.Succeeded);
        }

        try
        {
            var snapshot = await _services.Monitor.RefreshAsync(_lifetime.Token).ConfigureAwait(true);
            ApplySnapshot(snapshot);
        }
        catch (OperationCanceledException)
        {
            // The window closed.
        }

        Recompute();
    }

    internal void ShowReport(InitReportResult report, bool succeeded)
    {
        ArgumentNullException.ThrowIfNull(report);

        ReportRows.Clear();
        foreach (var step in report.Steps.Where(static s => s.Status != "pass"))
        {
            ReportRows.Add(new FirstRunReportRow(
                step.Name,
                step.Status.ToUpperInvariant(),
                step.Status switch { "fail" => "Bad", "warn" => "Warn", _ => "Neutral" },
                step.Detail));
        }

        ReportText = !report.IsAccepted ? report.Message : succeeded ? report.Message : "Setup finished, but a later step did not. See the review for which.";
        ReportKey = !report.IsAccepted || !succeeded ? "Bad" : report.Status == "partial" ? "Warn" : "Ok";
        var next = report.NextCommands.Count == 0 ? string.Empty : "Next: " + string.Join(" ; ", report.NextCommands);
        ReportNext = next;
        HasReportNext = next.Length > 0;
        var detail = new List<string>();
        detail.AddRange(ReportRows.Select(r => r.Detail.Length == 0 ? $"{r.Status}  {r.Name}" : $"{r.Status}  {r.Name}: {r.Detail}"));
        if (next.Length > 0)
        {
            detail.Add(next);
        }

        ReportDetailText = string.Join(Environment.NewLine, detail);
        HasReport = true;
    }

    [RelayCommand]
    private void OpenUpdates() => Views.Updates.UpdatesWindow.Show(_services);

    [RelayCommand]
    private void OpenActivity()
    {
        _services.Navigation.Request("activity");
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Esc closes the review dialog first; with none open it closes the window (unless a run is going).</summary>
    public bool HandleEscape()
    {
        if (Review.IsOpen)
        {
            return Review.HandleEscape();
        }

        return false;
    }

    public bool IsBusy => Review.IsRunning;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var choice in Detected)
        {
            choice.PropertyChanged -= OnChoiceChanged;
        }

        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
