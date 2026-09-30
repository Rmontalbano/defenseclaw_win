using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.ViewModels.Wizards;

/// <summary>
/// Drives one setup wizard: pages, validation, the mandatory review screen, and the single
/// CLI invocation at the end.
/// <para>
/// <b>The review screen is not skippable.</b> Every wizard ends on a page that prints the
/// exact argv about to run, because this app's whole contract is that it never edits
/// DefenseClaw state itself — it shells out, and the operator gets to read the command first.
/// The same list is what reaches <see cref="CliRunner.RunAsync"/>, so the screen cannot drift
/// from what executes: both come from <see cref="WizardDefinition.BuildArgv"/>. The review also says
/// how much the command changes (<see cref="CommandTiers"/>), whether it restarts the gateway, what the
/// operator changed from the current configuration, and — for a target with a <c>--dry-run</c> — offers
/// to preview it first.
/// </para>
/// <para>
/// <b>Only what changed is sent.</b> The pages start from the current configuration
/// (<see cref="WizardBaseline"/>) and a field reaches argv only when its answer differs from what
/// leaving it off would mean. An untouched wizard therefore cannot downgrade a connector.
/// </para>
/// <para>
/// <b>A secret is never on argv or on stdin.</b> A secret-taking flag is shown as a credential card
/// (which variable, is it set, how to store it in a real console). Where the CLI itself reads the secret from an
/// environment variable, the card also has a password box, and the typed value is handed to the run as an
/// environment variable of that one child (<see cref="BuildRunOptions"/>) and cleared when the run ends; see
/// <c>WizardViewModel.Secrets.cs</c> and <see cref="SecretRoute"/> for which flags qualify and why.
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
/// a fresh review page. A dry-run preview never locks anything. A run in flight is held on a token of
/// its own, so <see cref="CancelCommand"/> and closing the window can stop it — after a confirmation,
/// because <c>setup</c> verbs write configuration and a killed one can leave it half-applied.
/// <see cref="CliRunner"/> kills the whole process tree on cancellation and reports it as
/// <c>cancelled — process tree killed</c>.
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

    /// <summary>
    /// What the review page's command block says: the exact argv, the tier from <see cref="CommandTiers"/> and the
    /// gateway-restart warning, in the model every confirmation surface shares.
    /// </summary>
    [ObservableProperty]
    private CommandReview? _commandReview;

    [ObservableProperty]
    private bool _isDestructive;

    [ObservableProperty]
    private string _changeSummary = string.Empty;

    [ObservableProperty]
    private bool _hasChanges;

    [ObservableProperty]
    private bool _hasReviewCredentials;

    [ObservableProperty]
    private string _reviewProblem = string.Empty;

    [ObservableProperty]
    private bool _hasReviewProblem;

    [ObservableProperty]
    private string _promptWarning = string.Empty;

    [ObservableProperty]
    private bool _hasPromptWarning;

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
    /// True when the run on display exited 0 with no failure reason and was the real command. Only
    /// this — not <see cref="HasRun"/> — locks <see cref="CanExecute"/>: a failed, cancelled or
    /// preview-only attempt is exactly the case where the operator needs to be able to go on.
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
        ArgumentNullException.ThrowIfNull(definition);

        // The catalog's definition is a cache over --help and knows nothing of this machine; the pages
        // start from what config.yaml says now. Applied per open wizard, never to the shared definition.
        Definition = WizardBaseline.Apply(definition, services.Config, services.ConfigLoadError is null);
        Credentials = new WizardCredentials(services.Paths);

        foreach (var step in Definition.Steps)
        {
            var fields = step.Fields.Select(f => new WizardFieldViewModel(f, _values, Credentials)).ToArray();
            foreach (var field in fields)
            {
                field.PropertyChanged += OnFieldChanged;
                field.PropertyChanged += OnSecretEntryChanged;
                _fields.Add(field);
            }

            Steps.Add(new WizardStepViewModel(step, fields));
        }

        _timer = new DispatcherTimer { Interval = OutputTick };
        _timer.Tick += (_, _) => PullOutput();

        ApplyGates();
        RefreshCredentials();
        SyncPersistState();
        GoTo(0);
    }

    /// <summary>Raised when the window should close: cancelled, or finished and dismissed.</summary>
    public event EventHandler? CloseRequested;

    public WizardDefinition Definition { get; }

    /// <summary>Presence checks and the "type it into a console" launcher for secret fields.</summary>
    public WizardCredentials Credentials { get; }

    public string Title => Definition.Title;

    public string Target => Definition.Target;

    public string Description => Definition.Description;

    public ObservableCollection<WizardStepViewModel> Steps { get; } = new();

    public ObservableCollection<CliOutputRow> Output { get; } = new();

    /// <summary>The visible secret fields, for the review page's credential list.</summary>
    public ObservableCollection<WizardFieldViewModel> ReviewCredentials { get; } = new();

    /// <summary>Sentences for what the operator changed from the current configuration.</summary>
    public ObservableCollection<string> ReviewChanges { get; } = new();

    public string CertificationBadge => PlatformStatusText.Badge(Definition.PlatformStatus);

    public string CertificationKey => PlatformStatusText.Key(Definition.PlatformStatus);

    public string CertificationWarning =>
        PlatformStatusText.Warning(Definition.PlatformStatus, Definition.PlatformNote);

    public bool HasCertificationWarning => CertificationWarning.Length > 0;

    public string BaselineNote => Definition.BaselineNote;

    public bool HasBaselineNote => Definition.BaselineNote.Length > 0;

    public string BaselineWarning => Definition.BaselineWarning;

    public bool HasBaselineWarning => Definition.BaselineWarning.Length > 0;

    /// <summary>Resolved path of the binary that will run, shown under the argv.</summary>
    public string ExecutablePath => _services.Paths.CliPath ?? "defenseclaw (not found on PATH)";

    public string SourceNote => Definition.IsCurated
        ? "Fields are a curated layout over this target's live --help output."
        : "Fields were generated from this target's live --help output.";

    public bool HasHelpText => Definition.HelpText.Length > 0;

    public string HelpText => Definition.HelpText;

    /// <summary>What a screen reader announces for the window: the wizard and where in it we are.</summary>
    public string WindowAutomationName => $"Setup wizard: {Title}. {ProgressText}";

    public bool CanGoBack => PageIndex > 0 && !IsRunning;

    public bool CanGoNext => !IsReview && !IsRunning;

    /// <summary>
    /// Review page, nothing in flight, nothing blocking, and the last real run (if any) did not
    /// succeed. A successful run disables Execute until <see cref="RunAgainCommand"/> or a change of
    /// answers resets it.
    /// </summary>
    public bool CanExecute => IsReview && !IsRunning && !HasReviewProblem && !(HasRun && LastRunSucceeded);

    /// <summary>True after a successful run: the explicit way to run the same command again.</summary>
    public bool CanRunAgain => IsReview && !IsRunning && HasRun && LastRunSucceeded;

    /// <summary>The ordinary primary "Execute" button: the review page of a command that is not destructive.</summary>
    public bool ShowExecute => IsReview && !IsDestructive;

    /// <summary>The danger-styled "Execute" button: same command and enablement, for a destructive tier.</summary>
    public bool ShowDangerExecute => IsReview && IsDestructive;

    /// <summary>True when the target has a <c>--dry-run</c> and it is not already part of the command.</summary>
    public bool CanPreview => IsReview && !IsRunning && !HasReviewProblem && SupportsPreview && !_previewFlagInCommand;

    /// <summary>
    /// The command being built documents a <c>--dry-run</c> that writes nothing (its help says "Preview …
    /// without writing"). Judged on the <i>visible</i> fields: a group's <c>add</c> has one, its <c>list</c>
    /// does not, and appending the flag to the wrong subcommand would only be an error.
    /// </summary>
    public bool SupportsPreview => Definition.VisibleFields(_values).Any(f => f.Flag == "--dry-run");

    private bool _previewFlagInCommand;

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
    /// Escape, from anywhere in the window: dismisses the stop question if it is up, otherwise does what
    /// the Cancel/Close button does (which, mid-run, asks before stopping anything).
    /// </summary>
    public void HandleEscape()
    {
        if (IsStopConfirmVisible)
        {
            KeepRunning();
            return;
        }

        Cancel();
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

        // However the window closed, nothing the operator typed into a password box outlives it.
        ClearSecretEntries();
    }

    /// <summary>
    /// The one mutation this wizard performs. Runs through <see cref="CliRunner"/> so the
    /// argv, the live output and the exit code are recorded in Activity like every other
    /// change the app makes.
    /// </summary>
    [RelayCommand]
    private Task ExecuteAsync() => !CanExecute || _disposed
        ? Task.CompletedTask
        : RunCoreAsync(Definition.BuildArgv(_values), preview: false);

    /// <summary>
    /// Runs the same command with <c>--dry-run</c> added and shows what it would write. Offered only for a
    /// target whose help documents a preview that writes nothing; it never locks Execute.
    /// </summary>
    [RelayCommand]
    private Task PreviewAsync()
    {
        if (!CanPreview || _disposed)
        {
            return Task.CompletedTask;
        }

        var argv = Definition.BuildArgv(_values).Append("--dry-run").ToArray();
        return RunCoreAsync(argv, preview: true);
    }

    /// <summary>
    /// Shared by Execute and Preview. The run holds a token of its own (<see cref="_runCts"/>) so that
    /// <see cref="ConfirmStopCommand"/> can end it. Cancelling makes the runner kill the whole process
    /// tree and report <c>cancelled — process tree killed</c> as data on the returned invocation — it does
    /// not throw, so the outcome arrives through <see cref="ApplyResult"/> like any other.
    /// <para>
    /// No secret is piped: the runner is called without a stdin secret because none of these commands reads
    /// one (see <see cref="SecretRoute"/>). A secret the operator typed for an environment-variable route travels
    /// only in <see cref="BuildRunOptions"/>'s overlay, to the real command and not to a preview, and every typed
    /// secret is cleared when a real run ends — whether it succeeded, failed or was stopped.
    /// </para>
    /// </summary>
    private async Task RunCoreAsync(IReadOnlyList<string> argv, bool preview)
    {
        var suppliedSecrets = !preview && HasSuppliedSecrets;
        var previewSkipsSecrets = preview && HasSuppliedSecrets;

        // A retry after a failed or cancelled attempt starts from a clean console and badge, not
        // the previous attempt's.
        ResetRunState();
        _pendingArgv = argv;
        _stopRequested = false;
        _closeAfterStop = false;
        _runCts = new CancellationTokenSource();
        var token = _runCts.Token;
        ResultMessage = preview
            ? "Previewing with --dry-run. Nothing is written. Cancel stops the command."
            : "Running. Cancel stops the command and everything it started.";
        IsRunning = true;
        RaiseNavigationState();

        _services.Cli.InvocationStarted += OnInvocationStarted;
        _timer.Start();

        try
        {
            // Null unless a typed secret is being supplied, in which case it carries the variable and nothing
            // else is different from a plain run. A preview never gets one: the value goes to a single child.
            var options = RunOptionsFor(preview);
            var invocation = await _services.Cli.RunAsync(argv, null, token, options).ConfigureAwait(true);
            _invocation = invocation;
            PullOutput();
            ApplyResult(invocation, preview);
        }
        catch (CliNotFoundException ex)
        {
            Fail(ex.Message);
        }
        catch (ArgumentException ex)
        {
            // Options the runner refused (a variable name it does not accept). Nothing was started.
            Fail(ex.Message);
        }
        catch (SecretInArgumentException ex)
        {
            // Defence in depth: nothing in this wizard puts a secret in argv, and the runner refuses
            // one anyway (for example the gateway token). If this ever fires, the wizard is the bug.
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

            // The run is over, however it ended: the typed secret goes with it. The result line says so, and
            // says what a preview left alone, so an empty box is never a mystery.
            if (suppliedSecrets)
            {
                ClearSecretEntries(SecretClearedSentence);
                ResultMessage = (ResultMessage + " " + SecretClearedSentence).Trim();
            }
            else if (previewSkipsSecrets)
            {
                ResultMessage = (ResultMessage + " The value you entered is not used by a preview; it is supplied only when you press Execute." +
                                 PreviewSecretNotes()).Trim();
            }

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

    private void ApplyResult(CliInvocation invocation, bool preview)
    {
        LastRunSucceeded = false;
        var prefix = preview ? "preview · " : string.Empty;

        if (invocation.FailureReason is { Length: > 0 } failure)
        {
            // The runner words a stop it performed itself — "cancelled — process tree killed" —
            // so an operator's confirmed Cancel reads as what it was rather than as a failure.
            if (failure.StartsWith("cancelled", StringComparison.Ordinal))
            {
                ExitBadgeText = prefix + "cancelled";
                ExitBadgeKey = "Warn";
                ResultMessage = preview
                    ? failure + ". A preview writes nothing, so there is nothing to clean up."
                    : failure + ". The command may have left setup half-applied — read the output " +
                      "above and the Activity panel before running it again.";
                return;
            }

            ExitBadgeText = prefix + "failed";
            ExitBadgeKey = "Warn";
            ResultMessage = failure;
            return;
        }

        if (invocation.ExitCode is { } code)
        {
            ExitBadgeText = prefix + "exit " + code.ToString(CultureInfo.CurrentCulture);
            ExitBadgeKey = code == 0 ? "Ok" : "Bad";

            // A preview never counts as "the run": Execute must stay available after one.
            LastRunSucceeded = code == 0 && !preview;

            ResultMessage = (preview, code) switch
            {
                (true, 0) => "Preview finished. Nothing was written. Read the output, then press Execute to apply it for real.",
                (true, _) => "The preview failed. The output above is kept exactly as it was produced; nothing was written.",
                (false, 0) => "The command completed. Its argv, output and exit code are in the Activity panel.",
                _ => "The command failed. The output above is kept exactly as it was produced.",
            };
            return;
        }

        ExitBadgeText = prefix + "exit unknown";
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

        // Some credential variables depend on another answer (the destination preset, the key's env name).
        RefreshCredentials();
        RefreshReview();
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

    private void RefreshCredentials(bool fresh = false)
    {
        foreach (var field in _fields)
        {
            if (field.IsSecret)
            {
                field.RefreshCredential(fresh);
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

            // A value stored in a console while the operator was on an earlier page counts on the review.
            RefreshCredentials(fresh: true);
            RefreshReview();
        }
        else
        {
            PageTitle = CurrentStep!.Title;
            PageSubtitle = CurrentStep.Subtitle;
            ProgressText = $"Step {clamped + 1} of {pages.Count + 1}";
        }

        SetValidation(string.Empty);
        RaiseNavigationState();
        OnPropertyChanged(nameof(WindowAutomationName));
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

        // Rules that span fields (splunk: pick a pipeline) only make sense once the page's own answers
        // are individually valid, and are reported in the footer because no single field owns them.
        if (errors.Count == 0 && Definition.CrossValidator?.Invoke(_values) is { Length: > 0 } crossProblem)
        {
            errors.Add(crossProblem);
        }

        SetValidation(string.Join("  ", errors));
        return errors.Count == 0;
    }

    private void SetValidation(string message)
    {
        ValidationSummary = message;
        HasValidationSummary = message.Length > 0;
    }

    /// <summary>
    /// Rebuilds everything the review page says: the exact command, how much it changes, the restart
    /// notice, what the operator changed, which credentials it relies on, and any reason it cannot run.
    /// Cheap — it derives from the answers in memory and starts nothing.
    /// </summary>
    private void RefreshReview()
    {
        if (!IsReview)
        {
            return;
        }

        var argv = Definition.BuildArgv(_values);
        _previewFlagInCommand = argv.Contains("--dry-run", StringComparer.Ordinal);

        var restartWarning = WizardReview.RestartWarning(Definition, _values, argv);
        var review = new CommandReview
        {
            Title = "Command",
            Steps = new[] { new CommandReviewStep(argv) },
            RestartsGateway = restartWarning.Length > 0,
            Warnings = restartWarning.Length > 0
                ? new[] { CommandReviewWarning.GatewayRestart(restartWarning) }
                : Array.Empty<CommandReviewWarning>(),
        };
        CommandReview = review;
        IsDestructive = review.IsDestructive;

        ReviewChanges.Clear();
        foreach (var line in Definition.DescribeChanges(_values))
        {
            ReviewChanges.Add(line);
        }

        HasChanges = ReviewChanges.Count > 0;
        ChangeSummary = HasChanges
            ? "Changed from the current configuration:"
            : "Nothing was changed from the current configuration. Running this re-applies it as it stands.";

        ReviewCredentials.Clear();
        foreach (var field in _fields.Where(f => f.IsVisible && f.IsSecret))
        {
            ReviewCredentials.Add(field);
        }

        HasReviewCredentials = ReviewCredentials.Count > 0;

        ReviewProblem = Definition.CrossValidator?.Invoke(_values) ?? string.Empty;
        HasReviewProblem = ReviewProblem.Length > 0;

        // A non-interactive switch that is off means the CLI will try to ask a question, and this app
        // has no way to answer: it would stop at the first prompt.
        var prompting = _fields
            .Where(f => f.IsVisible && f.Field.Kind == WizardFieldKind.Switch &&
                        WizardFieldBuilder.IsNonInteractiveFlag(f.Field.Flag) && !f.IsOn)
            .Select(f => f.Field.Flag!)
            .ToArray();

        PromptWarning = prompting.Length > 0
            ? $"{string.Join(" and ", prompting)} is off, so the command will try to prompt for input. This app cannot answer prompts, " +
              "so it will stop at the first question. Turn it back on unless you are only previewing."
            : string.Empty;
        HasPromptWarning = PromptWarning.Length > 0;

        RaiseNavigationState();
    }

    private void RaiseNavigationState()
    {
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(CanExecute));
        OnPropertyChanged(nameof(CanRunAgain));
        OnPropertyChanged(nameof(CanPreview));
        OnPropertyChanged(nameof(ShowExecute));
        OnPropertyChanged(nameof(ShowDangerExecute));
        BackCommand.NotifyCanExecuteChanged();
        NextCommand.NotifyCanExecuteChanged();
        ExecuteCommand.NotifyCanExecuteChanged();
        PreviewCommand.NotifyCanExecuteChanged();
    }
}
