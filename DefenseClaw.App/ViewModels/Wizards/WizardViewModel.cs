using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Config;

namespace DefenseClaw.App.ViewModels.Wizards;

/// <summary>
/// Drives one setup wizard: pages, validation, the mandatory review screen, and the single
/// CLI invocation at the end.
/// <para>
/// <b>The review screen is not skippable.</b> Every wizard ends on a page that prints the
/// exact argv about to run, because this app's whole contract is that it never edits
/// DefenseClaw state itself — it shells out, and the operator gets to read the command first.
/// The same list is what reaches <see cref="CliRunner.RunAsync"/>, so the screen cannot drift
/// from what executes: both come from <see cref="WizardDefinition.BuildArgv"/>.
/// </para>
/// <para>
/// <b>Live output.</b> <see cref="CliRunner.OutputReceived"/> carries no invocation id, so —
/// exactly as the Activity panel does — this captures the invocation from
/// <see cref="CliRunner.InvocationStarted"/> and polls it on a dispatcher timer, pulling the
/// delta through <see cref="CliInvocation.CopyNewLines"/> rather than re-copying the whole
/// transcript each tick. The invocation lands in the Activity panel too; the runner records it
/// there without this view-model doing anything.
/// </para>
/// <para>
/// <b>A run can be re-run and can be stopped.</b> <see cref="HasRun"/> means "a run's outcome is on
/// display", not "this wizard has been used": it is reset — along with the badge, the message and
/// the console — the moment the operator goes <see cref="Back"/> or changes an answer, because all
/// of that describes answers that no longer exist. A failed or cancelled attempt can also simply be
/// retried from the review page as it stands. Only a <i>successful</i> run locks
/// <see cref="ExecuteCommand"/> (a setup verb applied twice is not something to allow by a stray
/// double-click); the deliberate way past that is <see cref="RunAgainCommand"/>, which returns to
/// a fresh review page. A run in flight is held on a token of its own, so <see cref="CancelCommand"/>
/// and closing the window can stop it — after a confirmation, because <c>setup</c> verbs write
/// configuration and a killed one can leave it half-applied. <see cref="CliRunner"/> kills the whole
/// process tree on cancellation and reports it as <c>cancelled — process tree killed</c>.
/// </para>
/// </summary>
public sealed partial class WizardViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan OutputTick = TimeSpan.FromMilliseconds(250);

    private readonly AppServices _services;
    private readonly WizardValues _values = new();
    private readonly List<WizardFieldViewModel> _fields = new();
    private readonly DispatcherTimer _timer;
    private CliInvocation? _invocation;
    private IReadOnlyList<string> _pendingArgv = Array.Empty<string>();

    /// <summary>
    /// The current run's own cancellation source. Non-null exactly while <see cref="IsRunning"/>,
    /// and only ever touched on the UI thread. Nothing else shares it: a run's stop button must not
    /// be able to cancel anything but that run.
    /// </summary>
    private CancellationTokenSource? _runCts;

    /// <summary>True once the operator has confirmed stopping the current run.</summary>
    private bool _stopRequested;

    /// <summary>
    /// True when the stop was asked for by closing the window, so the window should close once the
    /// killed run has finished reporting rather than sit there showing a result nobody asked to see.
    /// </summary>
    private bool _closeAfterStop;

    private bool _disposed;

    /// <summary>
    /// Reused between ticks so the 250ms console poll allocates nothing in steady state —
    /// it is cleared and refilled with only the delta, never the whole transcript.
    /// </summary>
    private readonly List<CliOutputLine> _outputBuffer = new();

    /// <summary>
    /// Position in <see cref="_invocation"/>'s monotonic append sequence, not an index into
    /// its retained lines — see <see cref="CliInvocation.CopyNewLines"/>. Reset to 0 with the
    /// console when a new run starts.
    /// </summary>
    private int _outputCursor;

    [ObservableProperty]
    private int _pageIndex;

    [ObservableProperty]
    private WizardStepViewModel? _currentStep;

    [ObservableProperty]
    private bool _isReview;

    [ObservableProperty]
    private string _pageTitle = string.Empty;

    [ObservableProperty]
    private string _pageSubtitle = string.Empty;

    [ObservableProperty]
    private string _progressText = string.Empty;

    [ObservableProperty]
    private string _commandText = string.Empty;

    [ObservableProperty]
    private string _stdinNote = string.Empty;

    [ObservableProperty]
    private bool _hasStdinSecret;

    [ObservableProperty]
    private bool _hasStdinConflict;

    [ObservableProperty]
    private string _stdinConflictNote = string.Empty;

    [ObservableProperty]
    private string _validationSummary = string.Empty;

    [ObservableProperty]
    private bool _hasValidationSummary;

    [ObservableProperty]
    private bool _isRunning;

    /// <summary>
    /// True while a run's outcome (badge, message, output) is on display. Reset by
    /// <see cref="ResetRunState"/> when the operator goes back or edits an answer — it is not a
    /// permanent "already used" mark.
    /// </summary>
    [ObservableProperty]
    private bool _hasRun;

    /// <summary>
    /// True when the run on display exited 0 with no failure reason. Only this — not
    /// <see cref="HasRun"/> — locks <see cref="CanExecute"/>: a failed or cancelled attempt is
    /// exactly the case where the operator needs to be able to try again.
    /// </summary>
    [ObservableProperty]
    private bool _lastRunSucceeded;

    /// <summary>The "stop the running command?" overlay. See <see cref="RequestStop"/>.</summary>
    [ObservableProperty]
    private bool _isStopConfirmVisible;

    [ObservableProperty]
    private string _stopConfirmMessage = string.Empty;

    [ObservableProperty]
    private string _exitBadgeText = string.Empty;

    /// <summary>Ok / Bad / Warn / Neutral — the badge colour key on the review page.</summary>
    [ObservableProperty]
    private string _exitBadgeKey = "Neutral";

    [ObservableProperty]
    private string _resultMessage = string.Empty;

    public WizardViewModel(AppServices services, WizardDefinition definition)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));

        foreach (var step in definition.Steps)
        {
            var fields = step.Fields.Select(f => new WizardFieldViewModel(f, _values)).ToArray();
            foreach (var field in fields)
            {
                field.PropertyChanged += OnFieldChanged;
                _fields.Add(field);
            }

            Steps.Add(new WizardStepViewModel(step, fields));
        }

        _timer = new DispatcherTimer { Interval = OutputTick };
        _timer.Tick += (_, _) => PullOutput();

        ApplyGates();
        GoTo(0);
    }

    /// <summary>Raised when the window should close: cancelled, or finished and dismissed.</summary>
    public event EventHandler? CloseRequested;

    public WizardDefinition Definition { get; }

    public string Title => Definition.Title;

    public string Target => Definition.Target;

    public string Description => Definition.Description;

    public ObservableCollection<WizardStepViewModel> Steps { get; } = new();

    public ObservableCollection<CliOutputRow> Output { get; } = new();

    public string CertificationBadge => PlatformStatusText.Badge(Definition.PlatformStatus);

    public string CertificationKey => PlatformStatusText.Key(Definition.PlatformStatus);

    public string CertificationWarning =>
        PlatformStatusText.Warning(Definition.PlatformStatus, Definition.PlatformNote);

    public bool HasCertificationWarning => CertificationWarning.Length > 0;

    /// <summary>Resolved path of the binary that will run, shown under the argv.</summary>
    public string ExecutablePath => _services.Paths.CliPath ?? "defenseclaw (not found on PATH)";

    public string SourceNote => Definition.IsCurated
        ? "Fields are a curated layout over this target's live --help output."
        : "Fields were generated from this target's live --help output.";

    public bool HasHelpText => Definition.HelpText.Length > 0;

    public string HelpText => Definition.HelpText;

    public bool CanGoBack => PageIndex > 0 && !IsRunning;

    public bool CanGoNext => !IsReview && !IsRunning;

    /// <summary>
    /// Review page, nothing in flight, and the last run (if any) did not succeed. A successful run
    /// disables Execute until <see cref="RunAgainCommand"/> or a change of answers resets it.
    /// </summary>
    public bool CanExecute => IsReview && !IsRunning && !(HasRun && LastRunSucceeded);

    /// <summary>True after a successful run: the explicit way to run the same command again.</summary>
    public bool CanRunAgain => IsReview && !IsRunning && HasRun && LastRunSucceeded;

    /// <summary>Visible pages only: a gated page the current answers exclude is not a page.</summary>
    private IReadOnlyList<WizardStepViewModel> VisibleSteps =>
        Steps.Where(s => s.IsVisible && s.HasFields).ToArray();

    [RelayCommand]
    public void Back()
    {
        if (PageIndex > 0)
        {
            // The badge, message and console describe a run of answers the operator is about to
            // change. Left in place they would also keep Execute disabled after a success.
            ResetRunState();
            GoTo(PageIndex - 1);
        }
    }

    [RelayCommand]
    public void Next()
    {
        if (!ValidateCurrentPage())
        {
            return;
        }

        GoTo(PageIndex + 1);
    }

    /// <summary>
    /// Before a run: abandons the wizard. During one: asks whether to stop it (the window stays open
    /// and shows the outcome). After one: dismisses — the footer shows this as "Close".
    /// </summary>
    [RelayCommand]
    private void Cancel()
    {
        if (IsRunning)
        {
            RequestStop(closeAfterStop: false);
            return;
        }

        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The window's close was requested while a run is in flight. Asks the same question as
    /// <see cref="Cancel"/>, and — only if the operator says yes — closes the window once the killed
    /// run has finished reporting.
    /// </summary>
    public void RequestStopAndClose() => RequestStop(closeAfterStop: true);

    /// <summary>
    /// Shows the overlay. A confirmation is required because <c>defenseclaw setup</c> verbs write
    /// configuration, and a run killed partway can leave it half-applied. Not a modal dialog: this can
    /// be raised from <c>Window.Closing</c>, where blocking would risk holding up an app exit.
    /// </summary>
    private void RequestStop(bool closeAfterStop)
    {
        if (!IsRunning)
        {
            return;
        }

        _closeAfterStop = closeAfterStop;
        StopConfirmMessage = closeAfterStop
            ? "Closing this window stops the command and everything it started. It may leave setup " +
              "half-applied — read the output and the Activity panel afterwards before running it again."
            : "This ends the command and everything it started. It may leave setup half-applied — read " +
              "the output and the Activity panel afterwards before running it again.";
        IsStopConfirmVisible = true;
    }

    /// <summary>The operator said yes: cancel the run's token, which makes the runner kill its process tree.</summary>
    [RelayCommand]
    private void ConfirmStop()
    {
        IsStopConfirmVisible = false;

        if (!IsRunning)
        {
            // The run finished while the question was on screen; there is nothing left to stop.
            _closeAfterStop = false;
            return;
        }

        _stopRequested = true;
        ResultMessage = "Stopping the command — ending its process tree…";

        try
        {
            _runCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The run finished and released its source between the check and here.
        }
    }

    /// <summary>The operator said no: dismiss the question and let the run carry on.</summary>
    [RelayCommand]
    private void KeepRunning()
    {
        IsStopConfirmVisible = false;
        _closeAfterStop = false;
    }

    /// <summary>
    /// After a successful run: returns the review page to a fresh, executable state. Deliberately a
    /// separate step from <see cref="ExecuteCommand"/> so that running the same setup twice is two
    /// decisions, not one stray double-click.
    /// </summary>
    [RelayCommand]
    private void RunAgain()
    {
        ResetRunState();
        ResultMessage = "Ready to run again. Check the command above, then press Execute.";
    }

    /// <summary>
    /// Ends this wizard's hold on the world. Called by the window when it closes, however it closes:
    /// stops the console poll and — because a wizard's child must not outlive its wizard, unlike the
    /// upgrade installer — cancels a run still in flight. The normal paths never get here running
    /// (the window asks first); this is the backstop for the closes it cannot refuse.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _services.Cli.InvocationStarted -= OnInvocationStarted;

        try
        {
            _runCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already released by a run that just finished.
        }
    }

    [RelayCommand]
    private void CopyCommand()
    {
        try
        {
            Clipboard.SetText(CommandText);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Another process owns the clipboard; nothing useful to do about it.
        }
    }

    /// <summary>
    /// The one mutation this wizard performs. Runs through <see cref="CliRunner"/> so the
    /// argv, the live output and the exit code are recorded in Activity like every other
    /// change the app makes.
    /// <para>
    /// The run holds a token of its own (<see cref="_runCts"/>) so that <see cref="ConfirmStopCommand"/>
    /// can end it. Cancelling makes the runner kill the whole process tree and report
    /// <c>cancelled — process tree killed</c> as data on the returned invocation — it does not throw,
    /// so the outcome arrives through <see cref="ApplyResult"/> like any other.
    /// </para>
    /// </summary>
    [RelayCommand]
    private async Task ExecuteAsync()
    {
        if (!CanExecute || _disposed)
        {
            return;
        }

        var argv = Definition.BuildArgv(_values);
        var secret = ResolveSecret(out var secretError);
        if (secretError is { Length: > 0 })
        {
            SetValidation(secretError);
            return;
        }

        // A retry after a failed or cancelled attempt starts from a clean console and badge, not
        // the previous attempt's.
        ResetRunState();
        _pendingArgv = argv;
        _stopRequested = false;
        _closeAfterStop = false;
        _runCts = new CancellationTokenSource();
        var token = _runCts.Token;
        ResultMessage = "Running. Cancel stops the command and everything it started.";
        IsRunning = true;
        RaiseNavigationState();

        _services.Cli.InvocationStarted += OnInvocationStarted;
        _timer.Start();

        try
        {
            var invocation = await _services.Cli.RunAsync(argv, secret, token).ConfigureAwait(true);
            _invocation = invocation;
            PullOutput();
            ApplyResult(invocation);
        }
        catch (CliNotFoundException ex)
        {
            Fail(ex.Message);
        }
        catch (SecretInArgumentException ex)
        {
            // Defence in depth: the field model keeps secrets out of argv, and the runner
            // refuses them anyway. If this ever fires, the wizard is the bug.
            Fail(ex.Message);
        }
        finally
        {
            _services.Cli.InvocationStarted -= OnInvocationStarted;
            _timer.Stop();

            _runCts?.Dispose();
            _runCts = null;

            // Only a stop the operator confirmed closes the window on its own. A run that simply
            // finished while the "close?" question was up leaves its result on screen to be read.
            var closeNow = _closeAfterStop && _stopRequested;
            _closeAfterStop = false;
            IsStopConfirmVisible = false;

            IsRunning = false;
            HasRun = true;
            RaiseNavigationState();

            if (closeNow)
            {
                CloseRequested?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private void OnInvocationStarted(object? sender, CliInvocation invocation)
    {
        // Match on the argv captured before the run: another panel could shell out while this
        // wizard is running, and this fires on the runner's thread, not the UI's.
        if (_invocation is not null || !invocation.Argv.SequenceEqual(_pendingArgv, StringComparer.Ordinal))
        {
            return;
        }

        _invocation = invocation;
    }

    /// <summary>
    /// Drains whatever the command printed since the last tick into the console.
    /// <para>
    /// Pulls the delta rather than a snapshot: <see cref="CliInvocation.CopyNewLines"/> costs
    /// one lock plus the lines that actually arrived, where snapshotting re-copied the whole
    /// accumulated transcript on every one of the four ticks a second.
    /// </para>
    /// <para>
    /// <see cref="_outputCursor"/> is a position in the invocation's monotonic append
    /// sequence, not an index into its retained list, so it stays correct if a chatty command
    /// trims its own output mid-run; the drop arrives as a notice line rather than as lines
    /// silently skipped.
    /// </para>
    /// </summary>
    private void PullOutput()
    {
        if (_invocation is null)
        {
            return;
        }

        _outputBuffer.Clear();
        _outputCursor = _invocation.CopyNewLines(_outputCursor, _outputBuffer);

        foreach (var line in _outputBuffer)
        {
            Output.Add(new CliOutputRow(line.Text, line.Stream == CliStream.StandardError));
        }
    }

    private void ApplyResult(CliInvocation invocation)
    {
        LastRunSucceeded = false;

        if (invocation.FailureReason is { Length: > 0 } failure)
        {
            // The runner words a stop it performed itself — "cancelled — process tree killed" —
            // so an operator's confirmed Cancel reads as what it was rather than as a failure.
            if (failure.StartsWith("cancelled", StringComparison.Ordinal))
            {
                ExitBadgeText = "cancelled";
                ExitBadgeKey = "Warn";
                ResultMessage = failure + ". The command may have left setup half-applied — read the output " +
                                "above and the Activity panel before running it again.";
                return;
            }

            ExitBadgeText = "failed";
            ExitBadgeKey = "Warn";
            ResultMessage = failure;
            return;
        }

        if (invocation.ExitCode is { } code)
        {
            ExitBadgeText = "exit " + code.ToString(CultureInfo.CurrentCulture);
            ExitBadgeKey = code == 0 ? "Ok" : "Bad";
            LastRunSucceeded = code == 0;
            ResultMessage = code == 0
                ? "The command completed. Its argv, output and exit code are in the Activity panel."
                : "The command failed. The output above is kept exactly as it was produced.";
            return;
        }

        ExitBadgeText = "exit unknown";
        ExitBadgeKey = "Neutral";
        ResultMessage = "The process ended without reporting an exit code.";
    }

    /// <summary>
    /// Forgets the last run: the badge, the message, the console and the "already ran" state, so the
    /// review page is executable again and no longer describes a run it did not just perform. Called
    /// when the operator goes back or changes an answer, when a run is about to start, and by
    /// <see cref="RunAgainCommand"/>. A no-op while a run is in flight — its state is live, not stale.
    /// </summary>
    private void ResetRunState()
    {
        if (IsRunning)
        {
            return;
        }

        // Runs on every keystroke in a field, so a wizard that has not run pays nothing for it.
        if (!HasRun && !LastRunSucceeded && Output.Count == 0 && ResultMessage.Length == 0 && ExitBadgeText.Length == 0)
        {
            return;
        }

        HasRun = false;
        LastRunSucceeded = false;
        ExitBadgeText = string.Empty;
        ExitBadgeKey = "Neutral";
        ResultMessage = string.Empty;
        Output.Clear();
        _outputBuffer.Clear();
        _outputCursor = 0;
        _invocation = null;
        RaiseNavigationState();
    }

    private void Fail(string message)
    {
        ExitBadgeText = "not run";
        ExitBadgeKey = "Bad";
        ResultMessage = message;
    }

    /// <summary>
    /// Collects the one secret this run may carry. stdin takes a single value, so two filled
    /// secret fields is a validation failure rather than a silent choice between them.
    /// </summary>
    private SecretValue? ResolveSecret(out string? error)
    {
        error = null;

        var filled = _fields
            .Where(f => f.IsVisible && f.IsSecret && f.Value.Trim().Length > 0)
            .ToArray();

        if (filled.Length == 0)
        {
            return null;
        }

        if (filled.Length > 1)
        {
            error = "Only one secret can be piped to the command's stdin. Clear all but one: " +
                    string.Join(", ", filled.Select(f => f.Label));
            return null;
        }

        return new SecretValue(filled[0].Value.Trim());
    }

    private void OnFieldChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, nameof(WizardFieldViewModel.Value), StringComparison.Ordinal))
        {
            return;
        }

        // An answer changed, so the last run's badge, message and console describe a command that
        // is no longer the one on the review page.
        ResetRunState();

        ApplyGates();
        RefreshCommand();
    }

    /// <summary>Re-evaluates every step and field gate against the current answers.</summary>
    private void ApplyGates()
    {
        foreach (var step in Steps)
        {
            step.IsVisible = WizardDefinition.IsVisible(step.Step.VisibleWhenFieldId, step.Step.VisibleWhenValues, _values);

            foreach (var field in step.Fields)
            {
                field.IsVisible = step.IsVisible &&
                    WizardDefinition.IsVisible(field.Field.VisibleWhenFieldId, field.Field.VisibleWhenValues, _values);
            }
        }
    }

    /// <summary>
    /// Moves to a page index over the currently visible pages; the index one past the end is
    /// always the review page.
    /// </summary>
    private void GoTo(int index)
    {
        var pages = VisibleSteps;
        var clamped = Math.Clamp(index, 0, pages.Count);

        PageIndex = clamped;
        IsReview = clamped >= pages.Count;
        CurrentStep = IsReview ? null : pages[clamped];

        if (IsReview)
        {
            PageTitle = "Review";
            PageSubtitle = "This is the exact command that will run. Nothing has changed yet.";
            ProgressText = $"Step {pages.Count + 1} of {pages.Count + 1}";
            RefreshCommand();
        }
        else
        {
            PageTitle = CurrentStep!.Title;
            PageSubtitle = CurrentStep.Subtitle;
            ProgressText = $"Step {clamped + 1} of {pages.Count + 1}";
        }

        SetValidation(string.Empty);
        RaiseNavigationState();
    }

    private bool ValidateCurrentPage()
    {
        if (CurrentStep is null)
        {
            return true;
        }

        var errors = new List<string>();
        foreach (var field in CurrentStep.Fields.Where(f => f.IsVisible))
        {
            field.ValidationError = field.Validate();
            if (field.ValidationError.Length > 0)
            {
                errors.Add(field.ValidationError);
            }
        }

        SetValidation(string.Join("  ", errors));
        return errors.Count == 0;
    }

    private void SetValidation(string message)
    {
        ValidationSummary = message;
        HasValidationSummary = message.Length > 0;
    }

    private void RefreshCommand()
    {
        if (!IsReview)
        {
            return;
        }

        var argv = Definition.BuildArgv(_values);
        CommandText = "defenseclaw " + string.Join(' ', argv.Select(Quote));

        var secrets = _fields.Where(f => f.IsVisible && f.IsSecret && f.Value.Trim().Length > 0).ToArray();
        HasStdinSecret = secrets.Length > 0;
        StdinNote = HasStdinSecret
            ? $"{string.Join(", ", secrets.Select(s => s.Label))} will be written to the command's stdin, not to the command line. " +
              "It is not in the argv above, and it will not appear in the Activity panel or in captured output."
            : string.Empty;

        // A secret can only reach the CLI through the prompt it would have typed into. If the
        // command was also told not to prompt, the value is silently dropped — say so here
        // rather than letting the operator find out from a half-configured destination.
        var suppressors = _fields
            .Where(f => f.IsVisible &&
                        WizardFieldBuilder.IsNonInteractiveFlag(f.Field.Flag) &&
                        string.Equals(f.Value, ToggleValues.On, StringComparison.OrdinalIgnoreCase))
            .Select(f => f.Field.Flag!)
            .ToArray();

        HasStdinConflict = HasStdinSecret && suppressors.Length > 0;
        StdinConflictNote = HasStdinConflict
            ? $"{string.Join(" and ", suppressors)} tells this command not to prompt, so the value piped on stdin will not be read. " +
              "Turn that off to let the command ask for it, or put the secret in ~/.defenseclaw/.env and point the matching *-env flag at its variable name — which is what DefenseClaw stores anyway."
            : string.Empty;
    }

    private void RaiseNavigationState()
    {
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(CanExecute));
        OnPropertyChanged(nameof(CanRunAgain));
        BackCommand.NotifyCanExecuteChanged();
        NextCommand.NotifyCanExecuteChanged();
        ExecuteCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Display quoting only: the runner uses ArgumentList, so nothing is ever re-parsed.</summary>
    private static string Quote(string value) =>
        value.Length == 0 || value.Any(char.IsWhiteSpace) ? $"\"{value}\"" : value;
}
