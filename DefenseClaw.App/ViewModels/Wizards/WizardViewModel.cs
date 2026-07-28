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
/// <see cref="CliRunner.InvocationStarted"/> and ticks <see cref="CliInvocation.Snapshot"/> on
/// a dispatcher timer, appending the delta. The invocation lands in the Activity panel too;
/// the runner records it there without this view-model doing anything.
/// </para>
/// </summary>
public sealed partial class WizardViewModel : ObservableObject
{
    private static readonly TimeSpan OutputTick = TimeSpan.FromMilliseconds(250);

    private readonly AppServices _services;
    private readonly WizardValues _values = new();
    private readonly List<WizardFieldViewModel> _fields = new();
    private readonly DispatcherTimer _timer;
    private CliInvocation? _invocation;
    private IReadOnlyList<string> _pendingArgv = Array.Empty<string>();
    private int _syncedOutputCount;

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

    [ObservableProperty]
    private bool _hasRun;

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

    public bool CanExecute => IsReview && !IsRunning && !HasRun;

    /// <summary>Visible pages only: a gated page the current answers exclude is not a page.</summary>
    private IReadOnlyList<WizardStepViewModel> VisibleSteps =>
        Steps.Where(s => s.IsVisible && s.HasFields).ToArray();

    [RelayCommand]
    public void Back()
    {
        if (PageIndex > 0)
        {
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

    [RelayCommand]
    private void Cancel()
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
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
    /// </summary>
    [RelayCommand]
    private async Task ExecuteAsync()
    {
        if (!CanExecute)
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

        Output.Clear();
        _syncedOutputCount = 0;
        _invocation = null;
        _pendingArgv = argv;
        ResultMessage = string.Empty;
        IsRunning = true;
        RaiseNavigationState();

        _services.Cli.InvocationStarted += OnInvocationStarted;
        _timer.Start();

        try
        {
            var invocation = await _services.Cli.RunAsync(argv, secret).ConfigureAwait(true);
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
            IsRunning = false;
            HasRun = true;
            RaiseNavigationState();
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

    private void PullOutput()
    {
        if (_invocation is null)
        {
            return;
        }

        var snapshot = _invocation.Snapshot();
        for (var i = _syncedOutputCount; i < snapshot.OutputLines.Count; i++)
        {
            var line = snapshot.OutputLines[i];
            Output.Add(new CliOutputRow(line.Text, line.Stream == CliStream.StandardError));
        }

        _syncedOutputCount = snapshot.OutputLines.Count;
    }

    private void ApplyResult(CliInvocation invocation)
    {
        if (invocation.FailureReason is { Length: > 0 } failure)
        {
            ExitBadgeText = "failed";
            ExitBadgeKey = "Warn";
            ResultMessage = failure;
            return;
        }

        if (invocation.ExitCode is { } code)
        {
            ExitBadgeText = "exit " + code.ToString(CultureInfo.CurrentCulture);
            ExitBadgeKey = code == 0 ? "Ok" : "Bad";
            ResultMessage = code == 0
                ? "The command completed. Its argv, output and exit code are in the Activity panel."
                : "The command failed. The output above is kept exactly as it was produced.";
            return;
        }

        ExitBadgeText = "exit unknown";
        ExitBadgeKey = "Neutral";
        ResultMessage = "The process ended without reporting an exit code.";
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
        BackCommand.NotifyCanExecuteChanged();
        NextCommand.NotifyCanExecuteChanged();
        ExecuteCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Display quoting only: the runner uses ArgumentList, so nothing is ever re-parsed.</summary>
    private static string Quote(string value) =>
        value.Length == 0 || value.Any(char.IsWhiteSpace) ? $"\"{value}\"" : value;
}
