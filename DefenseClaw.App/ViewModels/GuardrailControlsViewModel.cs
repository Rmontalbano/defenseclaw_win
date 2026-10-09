using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Guardrail;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// The guardrail controls beyond enable / disable / fail-mode (which stay on the Setup panel): the human-in-the-loop (HILT)
/// switch with its minimum severity, the block-message editor and the hook-lane judge gate (CUST-222, Mac "Guardrail Actions").
/// <para>
/// <b>Reads.</b> <c>guardrail status</c>, <c>guardrail hilt</c>, <c>guardrail block-message</c> and <c>guardrail judge list</c> are
/// read-only forms (checked in <c>cmd_guardrail.py</c> / <c>cmd_judge.py</c>: with nothing to set they only print) and run without
/// asking, like the Setup panel's status read. Their text is parsed by <see cref="GuardrailReadings"/>; when a reading cannot be
/// made the controls say so and the verbatim output is one checkbox away.
/// </para>
/// <para>
/// <b>Writes.</b> Nothing runs from a button press: each control opens a <see cref="CommandReview"/> showing the exact argv, the
/// tier <see cref="CommandTiers"/> gives it, the gateway restart, and a checkbox that adds <c>--no-restart</c>. The scope picker
/// (shown only on multi-connector installs, where <c>--connector</c> is valid) adds <c>--connector</c>.
/// </para>
/// Not a panel: it is opened on demand from the Setup / Overview guardrail surfaces (<c>GuardrailControlsWindow.Open</c>), so it
/// starts no timers and holds no subscriptions.
/// </summary>
public sealed partial class GuardrailControlsViewModel : ObservableObject
{
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<CliInvocation>> _run;

    private GuardrailHiltReading? _hilt;
    private GuardrailBlockMessageReading? _blockMessage;
    private GuardrailJudgeReading? _judge;
    private bool _reading;
    private bool _messageEdited;
    private Func<bool, string[]>? _pendingArgv;
    private string _reviewTitle = string.Empty;
    private string _reviewNote = string.Empty;
    private Func<bool, IReadOnlyList<CommandReviewWarning>>? _pendingWarnings;

    public GuardrailControlsViewModel(AppServices services)
        : this((argv, token) => services.Cli.RunAsync(argv, cancellationToken: token), services.Installation)
    {
    }

    /// <param name="run">How a verb is run; tests hand in a fake so no process ever starts.</param>
    public GuardrailControlsViewModel(Func<IReadOnlyList<string>, CancellationToken, Task<CliInvocation>> run)
        : this(run, installation: null)
    {
    }

    /// <param name="run">How a verb is run; tests hand in a fake so no process ever starts.</param>
    /// <param name="installation">Whether the installation may be changed; null (a test with a fake runner) means it may.</param>
    internal GuardrailControlsViewModel(Func<IReadOnlyList<string>, CancellationToken, Task<CliInvocation>> run, InstallationGuard? installation)
    {
        _run = run ?? throw new ArgumentNullException(nameof(run));
        _installation = installation;
        Scopes.Add(new GuardrailScopeOption("All connectors", string.Empty));
        _selectedScope = Scopes[0];
    }

    private readonly InstallationGuard? _installation;

    /// <summary>
    /// Why the controls that change something are off: the installation is managed or invalid, so the app only reads it. Null while it may be
    /// changed. The reads (Refresh, the raw output) are never held back by this.
    /// </summary>
    public string? ChangesBlockedReason => _installation?.BlockedReason;

    public bool HasChangesBlockedReason => ChangesBlockedReason is not null;

    /// <summary>The installation may be changed (the per-connector Add / Remove buttons, which have always ignored whether a command is running, bind to this).</summary>
    public bool InstallationAllowsChanges => ChangesBlockedReason is null;

    /// <summary>What every control that starts a write binds to: nothing is running or being read, and the installation may be changed.</summary>
    public bool CanChange => CanUse && ChangesBlockedReason is null;

    public string Title => "Guardrail controls";

    public ObservableCollection<GuardrailScopeOption> Scopes { get; } = new();

    public ObservableCollection<GuardrailJudgeRowViewModel> JudgeRows { get; } = new();

    public IReadOnlyList<string> MinSeverities => GuardrailControlArgv.Severities;

    public bool HasMultipleConnectors => Scopes.Count > 2;

    public bool HasJudgeRows => JudgeRows.Count > 0;

    public bool CanUse => !IsBusy && !IsRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUse), nameof(CanChange))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUse), nameof(CanChange))]
    private bool _isRunning;

    [ObservableProperty]
    private string _error = string.Empty;

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private string _asOf = string.Empty;

    [ObservableProperty]
    private GuardrailScopeOption _selectedScope;

    // ---- HILT

    [ObservableProperty]
    private string _hiltSummary = "Not read yet.";

    /// <summary>Ok / Warn / Neutral: tone of the HILT badge.</summary>
    [ObservableProperty]
    private string _hiltKey = "Neutral";

    [ObservableProperty]
    private string _selectedMinSeverity = "HIGH";

    // ---- Block message

    /// <summary>The message in force for the chosen scope; "Built-in default" when none is set.</summary>
    [ObservableProperty]
    private string _currentBlockMessage = "Not read yet.";

    [ObservableProperty]
    private bool _hasCustomBlockMessage;

    /// <summary>What the operator is typing; starts as the current message and follows it until they edit.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MessageProblem))]
    [NotifyPropertyChangedFor(nameof(HasMessageProblem))]
    private string _messageText = string.Empty;

    // ---- Judge

    [ObservableProperty]
    private string _judgeSummary = "Not read yet.";

    [ObservableProperty]
    private string _judgeKey = "Neutral";

    /// <summary>"Also turn the judge on" for <c>judge add</c> (<c>--enable</c>): the gate does nothing while the judge is off.</summary>
    [ObservableProperty]
    private bool _alsoEnableJudge;

    /// <summary>Optional <c>--timeout</c> seconds for <c>judge add</c>; blank leaves it alone.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimeoutProblem))]
    [NotifyPropertyChangedFor(nameof(HasTimeoutProblem))]
    private string _judgeTimeoutText = string.Empty;

    // ---- Raw output

    [ObservableProperty]
    private bool _showRaw;

    [ObservableProperty]
    private string _raw = string.Empty;

    // ---- Review and result

    [ObservableProperty]
    private bool _isReviewOpen;

    [ObservableProperty]
    private CommandReview? _review;

    [ObservableProperty]
    private bool _restartAfter = true;

    [ObservableProperty]
    private bool _hasResult;

    [ObservableProperty]
    private string _resultHeading = string.Empty;

    [ObservableProperty]
    private string _resultBadge = string.Empty;

    [ObservableProperty]
    private string _resultKey = "Neutral";

    [ObservableProperty]
    private string _resultText = string.Empty;

    /// <summary>The key <c>--connector</c> takes for the chosen scope, or null for the global default.</summary>
    public string? ScopeKey => SelectedScope is { Key.Length: > 0 } scope && HasMultipleConnectors ? scope.Key : null;

    /// <summary>Why the typed message cannot be sent, or empty. An argv value is one line; the CLI echoes it as one.</summary>
    public string MessageProblem =>
        MessageText.Contains('\n', StringComparison.Ordinal) || MessageText.Contains('\r', StringComparison.Ordinal)
            ? "A block message is one line."
            : string.Empty;

    public bool HasMessageProblem => MessageProblem.Length > 0;

    /// <summary>Why the typed timeout cannot be sent, or empty (blank is fine: the timeout is left alone).</summary>
    public string TimeoutProblem => JudgeTimeoutText.Trim().Length == 0 || ParseTimeout(JudgeTimeoutText, out _)
        ? string.Empty
        : "Seconds, as a number from 0 to 10.";

    public bool HasTimeoutProblem => TimeoutProblem.Length > 0;

    private static bool ParseTimeout(string text, out double? seconds)
    {
        seconds = null;
        if (text.Trim().Length == 0)
        {
            return true;
        }

        if (double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value is >= 0 and <= 10)
        {
            seconds = value;
            return true;
        }

        return false;
    }

    partial void OnSelectedScopeChanged(GuardrailScopeOption value) => ApplyReadings();

    partial void OnMessageTextChanged(string value) => _messageEdited = value != CurrentMessageForScope();

    partial void OnRestartAfterChanged(bool value) => RefreshReview();

    // ------------------------------------------------------------------ reading

    /// <summary>Reads the four read-only verbs. Overlapping calls collapse into the one in flight.</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (_reading)
        {
            return;
        }

        _reading = true;
        IsBusy = true;

        // The window is opened on demand and holds no subscriptions, so an installation that turned read-only while it was open is noticed here.
        OnPropertyChanged(nameof(ChangesBlockedReason));
        OnPropertyChanged(nameof(HasChangesBlockedReason));
        OnPropertyChanged(nameof(InstallationAllowsChanges));

        try
        {
            var status = await ReadAsync(new[] { "guardrail", "status" }).ConfigureAwait(true);
            var hilt = await ReadAsync(GuardrailControlArgv.ReadHilt()).ConfigureAwait(true);
            var message = await ReadAsync(GuardrailControlArgv.ReadBlockMessage()).ConfigureAwait(true);
            var judge = await ReadAsync(GuardrailControlArgv.ReadJudge()).ConfigureAwait(true);

            var failure = new[] { status, hilt, message, judge }.FirstOrDefault(r => r.Error.Length > 0);
            if (failure is not null)
            {
                Error = failure.Error;
                HasError = true;
                return;
            }

            Error = string.Empty;
            HasError = false;
            AsOf = "as of " + DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture);

            _hilt = GuardrailReadings.ParseHilt(hilt.Text);
            _blockMessage = GuardrailReadings.ParseBlockMessage(message.Text);
            _judge = GuardrailReadings.ParseJudge(judge.Text);
            Raw = string.Join(
                "\n\n",
                new[] { ("guardrail status", status.Text), ("guardrail hilt", hilt.Text), ("guardrail block-message", message.Text), ("guardrail judge list", judge.Text) }
                    .Select(p => $"$ defenseclaw {p.Item1}\n{p.Item2.Trim()}"));

            SyncScopes(GuardrailStatusParser.Parse(status.Text));
            ApplyReadings();
        }
        finally
        {
            _reading = false;
            IsBusy = false;
        }
    }

    private sealed record ReadResult(string Text, string Error);

    private async Task<ReadResult> ReadAsync(string[] argv)
    {
        var name = string.Join(' ', argv);
        try
        {
            var invocation = await _run(argv, CancellationToken.None).ConfigureAwait(true);
            if (invocation.FailureReason is { Length: > 0 } reason)
            {
                return new ReadResult(string.Empty, $"{name} did not finish: {reason}.");
            }

            if (invocation.ExitCode is not 0)
            {
                var detail = string.Join(' ', LastLines(invocation, 3));
                var code = invocation.ExitCode?.ToString(CultureInfo.CurrentCulture) ?? "without a code";
                return new ReadResult(string.Empty, $"{name} exited {code}. {detail}".Trim());
            }

            return new ReadResult(Stdout(invocation), string.Empty);
        }
        catch (CliNotFoundException ex)
        {
            return new ReadResult(string.Empty, "defenseclaw was not found, so the guardrail cannot be read. " + ex.Message);
        }
    }

    private void SyncScopes(GuardrailStatus status)
    {
        var keep = SelectedScope.Key;
        var roster = status.Connectors.Where(c => c.Key.Length > 0).ToList();

        Scopes.Clear();
        Scopes.Add(new GuardrailScopeOption("All connectors", string.Empty));
        foreach (var row in roster)
        {
            Scopes.Add(new GuardrailScopeOption(row.Name.Length > 0 ? row.Name : row.Key, row.Key));
        }

        OnPropertyChanged(nameof(HasMultipleConnectors));

        // Setting the property raises OnSelectedScopeChanged, which applies the readings; if the scope did not change, do it here.
        var next = Scopes.FirstOrDefault(s => s.Key == keep) ?? Scopes[0];
        if (ReferenceEquals(next, SelectedScope))
        {
            ApplyReadings();
        }
        else
        {
            SelectedScope = next;
        }
    }

    /// <summary>Re-derives what the controls show from the last readings and the chosen scope. No CLI call.</summary>
    private void ApplyReadings()
    {
        var key = ScopeKey;

        if (_hilt is { IsRead: true } hilt)
        {
            var effective = key is null ? null : hilt.Connectors.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));
            var on = effective?.Enabled ?? hilt.Enabled;
            var min = effective?.MinSeverity is { Length: > 0 } m ? m : hilt.MinSeverity;
            var where = key is null ? string.Empty : $" for {SelectedScope.Label}";
            HiltSummary = on == true
                ? $"HILT is on{where}: confirmable actions at {(min.Length > 0 ? min : "the minimum severity")} or above ask for approval. CRITICAL always blocks."
                : $"HILT is off{where}: risky actions are allowed or blocked without asking.";
            HiltKey = on == true ? "Ok" : "Neutral";
            if (Array.IndexOf(GuardrailControlArgv.Severities.ToArray(), min) >= 0)
            {
                SelectedMinSeverity = min;
            }
        }
        else if (_hilt is not null)
        {
            HiltSummary = "The HILT setting could not be read from the CLI's output; the verbatim text is under Show CLI output.";
            HiltKey = "Warn";
        }

        if (_blockMessage is { IsRead: true } message)
        {
            var current = CurrentMessageForScope();
            CurrentBlockMessage = current.Length > 0 ? current : "Built-in default";
            HasCustomBlockMessage = current.Length > 0;
            if (!_messageEdited)
            {
                MessageText = current.Replace('\n', ' ');
                _messageEdited = false;
            }
        }
        else if (_blockMessage is not null)
        {
            CurrentBlockMessage = "The block message could not be read from the CLI's output; the verbatim text is under Show CLI output.";
            HasCustomBlockMessage = false;
        }

        if (_judge is { IsRead: true } judge)
        {
            JudgeRows.Clear();
            foreach (var row in judge.Connectors)
            {
                JudgeRows.Add(new GuardrailJudgeRowViewModel(row, judge.IsGated(row.Key)));
            }

            OnPropertyChanged(nameof(HasJudgeRows));
            var gate = judge.GateAll ? "every hook connector" : judge.Gate.Count > 0 ? string.Join(", ", judge.Gate) : "no hook connector";
            JudgeSummary = $"The judge is {(judge.JudgeEnabled == true ? "on" : "off")}; the hook lane is open to {gate}. Hook timeout {(judge.HookTimeout.Length > 0 ? judge.HookTimeout : "unknown")}.";
            JudgeKey = judge.JudgeEnabled == true && (judge.GateAll || judge.Gate.Count > 0) ? "Ok" : "Neutral";
        }
        else if (_judge is not null)
        {
            JudgeSummary = "The judge gate could not be read from the CLI's output; the verbatim text is under Show CLI output.";
            JudgeKey = "Warn";
        }
    }

    /// <summary>The message in force for the chosen scope (the connector's, else the global one); empty for the built-in default.</summary>
    private string CurrentMessageForScope()
    {
        if (_blockMessage is not { IsRead: true } message)
        {
            return string.Empty;
        }

        return ScopeKey is { } key && message.Connectors.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase)) is { } connector
            ? connector.Message
            : message.Message;
    }

    // ------------------------------------------------------------------ writes

    [RelayCommand]
    private void TurnHiltOn() => BeginHilt(on: true);

    [RelayCommand]
    private void TurnHiltOff() => BeginHilt(on: false);

    private void BeginHilt(bool on)
    {
        var scope = ScopeLabel();
        var severity = SelectedMinSeverity;
        Begin(
            on ? $"Turn HILT on at {severity}{scope}?" : $"Turn HILT off{scope}?",
            on
                ? $"Human-in-the-loop pauses a confirmable action at {severity} or above and asks you to approve it, instead of allowing or blocking it silently. CRITICAL findings still block outright."
                : "Risky actions are no longer paused for approval: they are allowed or blocked by policy alone. The minimum severity is kept.",
            restart => GuardrailControlArgv.Hilt(on, severity, ScopeKey, restart),
            restart => BareScopeWarnings(
                "Rewrites every connector's HILT",
                "Without a connector scope this changes the global HILT policy and writes it to every active connector (the CLI does so for each one it lists). Choose a connector in Scope to change one only."));
    }

    [RelayCommand]
    private void SetBlockMessage()
    {
        var text = MessageText.Trim();
        if (text.Length == 0 || HasMessageProblem)
        {
            return;
        }

        var scope = ScopeLabel();
        Begin(
            $"Set the block message{scope}?",
            $"Shown to the agent's user when an action is blocked and the verdict carries no reason of its own. New message: \"{text}\". Audit rows and notifications keep the real reason.",
            restart => GuardrailControlArgv.BlockMessage(text, ScopeKey, restart),
            restart => BareScopeWarnings(
                "Overwrites every connector's message",
                "Without a connector scope this sets the global message and replaces the per-connector override of every active connector, so a message set for one connector is lost. Choose a connector in Scope to change one only."));
    }

    [RelayCommand]
    private void ClearBlockMessage()
    {
        var scope = ScopeLabel();
        Begin(
            $"Clear the block message{scope}?",
            "Reverts to the global message, or the built-in default when none is set. Audit rows and notifications are unaffected.",
            restart => GuardrailControlArgv.BlockMessage(null, ScopeKey, restart),
            restart => BareScopeWarnings(
                "Clears every connector's message",
                "Without a connector scope this clears the global message and the per-connector override of every active connector. Choose a connector in Scope to clear one only."));
    }

    /// <summary>Opts a hook connector (or <c>all</c>) into the judge.</summary>
    [RelayCommand]
    private void AddJudge(string? connector)
    {
        if (string.IsNullOrWhiteSpace(connector) || HasTimeoutProblem)
        {
            return;
        }

        ParseTimeout(JudgeTimeoutText, out var timeout);
        var enable = AlsoEnableJudge;
        var target = connector.Trim();
        var all = string.Equals(target, GuardrailControlArgv.AllConnectors, StringComparison.OrdinalIgnoreCase);
        Begin(
            all ? "Send every hook connector's content to the LLM judge?" : $"Send {target} content to the LLM judge?",
            "Opts the connector into the hook-lane LLM judge, which adds latency (up to the hook timeout) and an LLM call to every inspected hook event.",
            restart => GuardrailControlArgv.JudgeAdd(target, enable, timeout, restart),
            restart => _judge is { JudgeEnabled: false } && !enable
                ? new[]
                {
                    new CommandReviewWarning(
                        "The judge is off",
                        "The gate has no effect until the judge itself is enabled. Tick \"Also turn the judge on\" in the controls, or enable it with the guardrail setup wizard."),
                }
                : Array.Empty<CommandReviewWarning>());
    }

    /// <summary>Opts a hook connector (or <c>all</c>) out of the judge.</summary>
    [RelayCommand]
    private void RemoveJudge(string? connector)
    {
        if (string.IsNullOrWhiteSpace(connector))
        {
            return;
        }

        var target = connector.Trim();
        var all = string.Equals(target, GuardrailControlArgv.AllConnectors, StringComparison.OrdinalIgnoreCase);
        Begin(
            all ? "Turn the hook-lane judge off for every connector?" : $"Stop sending {target} content to the LLM judge?",
            all
                ? "Empties the hook gate, so no hook connector's content reaches the LLM judge. Regex scanning continues."
                : "Takes the connector out of the hook gate. Regex scanning continues; the judge setting itself is not changed.",
            restart => GuardrailControlArgv.JudgeRemove(target, restart),
            restart => Array.Empty<CommandReviewWarning>());
    }

    private string ScopeLabel() => ScopeKey is not null ? $" for {SelectedScope.Label}" : string.Empty;

    /// <summary>The bar a bare (unscoped) write carries when more than one connector is active; none on a single-connector install.</summary>
    private IReadOnlyList<CommandReviewWarning> BareScopeWarnings(string title, string message) =>
        HasMultipleConnectors && ScopeKey is null
            ? new[] { new CommandReviewWarning(title, message) }
            : Array.Empty<CommandReviewWarning>();

    private void Begin(
        string title,
        string note,
        Func<bool, string[]> argv,
        Func<bool, IReadOnlyList<CommandReviewWarning>> warnings)
    {
        if (!CanChange)
        {
            return;
        }

        _reviewTitle = title;
        _reviewNote = note;
        _pendingArgv = argv;
        _pendingWarnings = warnings;
        RestartAfter = true;
        HasResult = false;
        RefreshReview();
        IsReviewOpen = true;
    }

    private void RefreshReview()
    {
        if (_pendingArgv is null)
        {
            return;
        }

        var argv = _pendingArgv(RestartAfter);
        var restarts = CommandReview.RestartsGatewayFor(argv);
        var warnings = new List<CommandReviewWarning>(_pendingWarnings?.Invoke(RestartAfter) ?? Array.Empty<CommandReviewWarning>())
        {
            restarts
                ? CommandReviewWarning.GatewayRestart()
                : new CommandReviewWarning(
                    "Gateway not restarted",
                    "The gateway is not restarted, so the change is saved but not yet in effect until it next restarts."),
        };

        var review = new CommandReview
        {
            Title = _reviewTitle,
            Summary = _reviewNote,
            Steps = new[] { new CommandReviewStep(argv, floor: CommandTier.StateChanging) },
            RestartsGateway = restarts,
            Warnings = warnings,
        };
        Review = _installation is { } installation ? review.GuardedBy(installation) : review;
    }

    [RelayCommand]
    private void CancelReview() => IsReviewOpen = false;

    [RelayCommand]
    private void DismissResult() => HasResult = false;

    [RelayCommand]
    private async Task ConfirmAsync()
    {
        if (!IsReviewOpen || IsRunning || _pendingArgv is null || Review is { IsBlocked: true })
        {
            return;
        }

        var argv = _pendingArgv(RestartAfter);
        IsRunning = true;

        try
        {
            var invocation = await _run(argv, CancellationToken.None).ConfigureAwait(true);
            IsReviewOpen = false;
            ShowResult(argv, invocation);

            if (invocation.ExitCode is 0 && invocation.FailureReason is null)
            {
                IsRunning = false;
                _messageEdited = false;
                await RefreshAsync().ConfigureAwait(true);
            }
        }
        catch (CliNotFoundException ex)
        {
            IsReviewOpen = false;
            HasResult = true;
            ResultHeading = "defenseclaw was not found";
            ResultBadge = "not run";
            ResultKey = "Bad";
            ResultText = ex.Message;
        }
        finally
        {
            IsRunning = false;
        }
    }

    private void ShowResult(IReadOnlyList<string> argv, CliInvocation invocation)
    {
        HasResult = true;
        ResultHeading = "defenseclaw " + string.Join(' ', argv) + " finished";

        (ResultBadge, ResultKey) = (invocation.FailureReason, invocation.ExitCode) switch
        {
            ({ Length: > 0 } reason, _) => (reason.StartsWith("cancelled", StringComparison.Ordinal) ? "cancelled" : "failed", "Warn"),
            (_, 0) => ("exit 0", "Ok"),
            (_, { } code) => ("exit " + code.ToString(CultureInfo.CurrentCulture), "Bad"),
            _ => ("exit unknown", "Neutral"),
        };

        var lines = invocation.OutputLines.Select(l => l.Text.TrimEnd()).Where(t => t.Length > 0).TakeLast(40);
        ResultText = invocation.FailureReason is { Length: > 0 } failure
            ? failure + "\n" + string.Join('\n', lines)
            : string.Join('\n', lines);
    }

    private static string Stdout(CliInvocation invocation) => string.Join(
        '\n',
        invocation.OutputLines.Where(l => l.Stream == CliStream.StandardOutput).Select(l => l.Text));

    private static IEnumerable<string> LastLines(CliInvocation invocation, int count) => invocation.OutputLines
        .Select(l => l.Text.Trim())
        .Where(t => t.Length > 0)
        .TakeLast(count);
}

/// <summary>One connector in the judge list: its effective state, and the one action that makes sense for it.</summary>
public sealed class GuardrailJudgeRowViewModel
{
    public GuardrailJudgeRowViewModel(GuardrailConnectorJudge row, bool gated)
    {
        Key = row.Key;
        State = row.State;
        Note = row.Note;
        IsGated = gated;
    }

    public string Key { get; }

    public string State { get; }

    public string Note { get; }

    /// <summary>The connector is named in (or covered by "all" in) the hook gate.</summary>
    public bool IsGated { get; }

    /// <summary>
    /// Proxy-backed connectors (<c>openclaw</c>, <c>zeptoclaw</c>: <c>_PROXY_BACKED_CONNECTORS</c> in cmd_setup.py) are judged whenever the
    /// judge is on and the CLI rejects add / remove for them, as it does for a name it does not know.
    /// </summary>
    public bool CanChange =>
        !(Key.Equals("openclaw", StringComparison.OrdinalIgnoreCase) || Key.Equals("zeptoclaw", StringComparison.OrdinalIgnoreCase)) &&
        !State.Contains("proxy lane", StringComparison.OrdinalIgnoreCase) &&
        !State.Contains("unknown connector", StringComparison.OrdinalIgnoreCase);

    public bool CanAdd => CanChange && !IsGated;

    public bool CanRemove => CanChange && IsGated;

    public string ToneKey => State.StartsWith("judged", StringComparison.OrdinalIgnoreCase) ? "Ok" : "Neutral";

    public bool HasNote => Note.Length > 0;

    public override string ToString() => $"{Key}: {State}" + (Note.Length > 0 ? ". " + Note : string.Empty);
}
