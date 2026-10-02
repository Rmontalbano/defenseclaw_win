using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.Core.Audit;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// Every command this app has shelled out, newest first: <see cref="CliRunner.Activity"/>
/// plus <see cref="CliRunner.InvocationStarted"/> / <see cref="CliRunner.InvocationCompleted"/>
/// to keep the list live.
/// <para>
/// <b>Empty is normal.</b> Every mutation the app makes - the Govern panels' block / allow /
/// remove actions, the Setup wizards and the upgrade flow (and the config check behind the
/// editor's Save) - shells out through <see cref="CliRunner"/> and lands here, but the list lives in memory and starts empty on
/// every launch. So it is empty until something has been run in this session, which is not a
/// fault, and the empty state says so explicitly rather than reading as a broken panel.
/// </para>
/// <para>
/// <b>Why a timer drives live output.</b> <see cref="CliRunner.OutputReceived"/> carries a
/// bare <see cref="CliOutputLine"/> with no invocation id, so there is no cheap way to route
/// one event to one row. Instead, a half-second <see cref="DispatcherTimer"/> re-reads every
/// row still running, which is also what keeps the elapsed-time readout current. Each tick
/// pulls only the output that arrived since the last one via
/// <see cref="CliInvocation.CopyNewLines"/> - a full <see cref="CliInvocation.Snapshot"/> per
/// row twice a second would copy the whole accumulated transcript each time, and it is the
/// long noisy runs where that cost lands hardest. <see cref="CliRunner"/> raises its events
/// from background threads (process callbacks, or continuations captured with
/// <c>ConfigureAwait(false)</c>), so every handler marshals through
/// <see cref="Application.Current"/>'s dispatcher.
/// </para>
/// <para>
/// <b>When the timer runs.</b> Only while the panel is active <i>and</i> some row is still
/// running - never from the constructor, and never while the dashboard is in the tray. A tick
/// that finds nothing running does no work but still wakes the dispatcher twice a second, and
/// this timer used to do exactly that for the whole life of the process once the panel had
/// been visited. The runner's two events stay attached (they only fire when a command
/// starts or ends, and keep <see cref="Rows"/> current while the panel is away); the
/// live-output and elapsed-time work waits for <see cref="OnActivated"/>, which ticks every
/// row once so nothing is stale when the panel is seen.
/// </para>
/// <para>
/// <b>The list is virtualized.</b> <see cref="Rows"/> holds up to <see cref="CliRunner.ActivityCapacity"/> rows, but the view
/// builds only the cards in view and reuses their elements for other rows as it scrolls. A row therefore owns everything
/// about how its card looks and behaves that the operator can change - open or shut, following its output, where it was
/// scrolled to, what is selected - and the view puts that back whenever a card is built for it (see
/// <see cref="ActivityRow"/> and <see cref="ActivityTranscript"/>). New runs are inserted at index 0; the view keeps the
/// operator's place when that happens.
/// </para>
/// <para>
/// <b>Per-entry actions.</b> Each row shows its <see cref="CommandTier"/>, and offers Copy argv,
/// Copy output, Export log (a user-chosen file) and - while it runs - Cancel, which asks
/// <see cref="CliRunner.Cancel(CliInvocation, out string)"/> to kill the process tree. The runner
/// refuses runs that survive app shutdown (the upgrade installer); such a row shows Cancel
/// disabled with the reason instead of a button that can only fail.
/// </para>
/// </summary>
public sealed partial class ActivityPanelViewModel : PanelViewModelBase
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(500);

    private readonly DispatcherTimer _timer;

    [ObservableProperty]
    private bool _isEmpty = true;

    /// <summary>One-line result of the last row action (copied, exported, cancel refused...).</summary>
    [ObservableProperty]
    private string _noticeText = string.Empty;

    /// <summary>Two-way with the notice's InfoBar, so its close button dismisses it.</summary>
    [ObservableProperty]
    private bool _hasNotice;

    /// <summary>The Commands tab's value (the segmented control's <c>SelectedValue</c>).</summary>
    public const string CommandsTab = "Commands";

    /// <summary>The Mutations tab's value.</summary>
    public const string MutationsTab = "Mutations";

    public ActivityPanelViewModel(AppServices services)
        : this(services, new MutationReader((services ?? throw new ArgumentNullException(nameof(services))).Paths.AuditDatabasePath))
    {
    }

    internal ActivityPanelViewModel(AppServices services, MutationReader mutationReader)
        : base(services)
    {
        Mutations = new ActivityMutationsViewModel(mutationReader, scope: Services.ConnectorScope);
        Mutations.PropertyChanged += OnMutationsChanged;
        Services.Cli.InvocationStarted += OnInvocationStarted;
        Services.Cli.InvocationCompleted += OnInvocationCompleted;

        // Created stopped: EnsureTimerState starts it when there is something to tick.
        _timer = new DispatcherTimer { Interval = TickInterval };
        _timer.Tick += (_, _) => TickRunningRows();

        LoadActivity();
    }

    public override string Title => "Activity";

    public override string Description =>
        "Every DefenseClaw mutation this app makes: exact argv, live output and exit code.";

    /// <summary>
    /// Stated once, from fixed capacities - neither changes at runtime.
    /// <para>
    /// Names both caps, because there are two and an operator who only knows about the entry
    /// cap would read a trimmed transcript as a bug. A truncated invocation also says so
    /// inline, at the top of its own output.
    /// </para>
    /// </summary>
    public string CapacityNote =>
        $"Showing the last {Services.Cli.ActivityCapacity.ToString(CultureInfo.CurrentCulture)} invocations, in memory only. " +
        $"Each one keeps up to {CliInvocation.MaxRetainedOutputLines.ToString(CultureInfo.CurrentCulture)} lines " +
        $"({(CliInvocation.MaxRetainedOutputBytes / 1024).ToString(CultureInfo.CurrentCulture)} KiB) of output " +
        $"(a list the app parses as JSON keeps up to {CliInvocation.MaxFullOutputLines.ToString("N0", CultureInfo.CurrentCulture)} lines / " +
        $"{(CliInvocation.MaxFullOutputBytes / (1024 * 1024)).ToString(CultureInfo.CurrentCulture)} MiB) - past that the oldest lines are dropped and the invocation says how many. " +
        "Nothing here survives an app restart.";

    public string EmptyTitle => "No CLI activity yet";

    public string EmptyDetail =>
        "The GUI never edits DefenseClaw state directly - every mutation it makes (a Govern action, a Setup wizard, " +
        "an upgrade) runs as a subprocess, and so does the config check behind the editor's Save; each one's exact " +
        "command line, live output and exit code " +
        "show up here. Nothing has been run in this session yet; the list is kept in memory only, so it also " +
        "starts empty each time the app launches.";

    public ObservableCollection<ActivityRow> Rows { get; } = new();

    /// <summary>The Mutations tab: configuration and policy changes read from the audit database.</summary>
    public ActivityMutationsViewModel Mutations { get; }

    /// <summary>Commands (this app's own CLI runs, in memory) or Mutations (the audit database); two-way with the segmented control.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCommandsTab))]
    [NotifyPropertyChangedFor(nameof(IsMutationsTab))]
    [NotifyPropertyChangedFor(nameof(CaptionText))]
    [NotifyPropertyChangedFor(nameof(ShowConnectorFilter))]
    private string _activeTab = CommandsTab;

    /// <summary>The connector filter is for the Mutations tab, once a loaded change names a connector.</summary>
    public bool ShowConnectorFilter => IsMutationsTab && Mutations.CanFilterConnectors;

    public bool IsCommandsTab => ActiveTab != MutationsTab;

    public bool IsMutationsTab => ActiveTab == MutationsTab;

    /// <summary>The toolbar caption: the Commands capacity note, or the Mutations count.</summary>
    public string CaptionText => IsMutationsTab ? Mutations.Summary : CapacityNote;

    partial void OnActiveTabChanged(string value)
    {
        if (value == MutationsTab)
        {
            if (IsActive || !Mutations.HasLoaded)
            {
                _ = Mutations.LoadAsync();
            }
        }
        else
        {
            // The statement stops with the tab; the rows already read stay for the way back.
            Mutations.Cancel();
        }
    }

    /// <summary>The toolbar caption follows the Mutations summary, which changes as reads land and filters apply.</summary>
    private void OnMutationsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ActivityMutationsViewModel.Summary))
        {
            OnPropertyChanged(nameof(CaptionText));
        }
        else if (e.PropertyName == nameof(ActivityMutationsViewModel.CanFilterConnectors))
        {
            OnPropertyChanged(nameof(ShowConnectorFilter));
        }
    }

    public override Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        LoadActivity();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Catch-up: every row still marked running is ticked once (a row that finished, or grew
    /// output, while the panel was away is stale until then), and the timer is armed if any
    /// are still running.
    /// </summary>
    protected override void OnActivated()
    {
        TickRunningRows();
        if (IsMutationsTab)
        {
            _ = Mutations.LoadAsync();
        }
    }

    /// <summary>The shared connector scope changed: the Mutations already loaded are listed again under it.</summary>
    protected override void OnConnectorScopeChanged() => Mutations.ReapplyScope();

    protected override void OnDeactivated()
    {
        _timer.Stop();
        Mutations.Cancel();
    }

    /// <summary>
    /// What F5 invokes. Re-syncs the list with the runner's ring (rows that are already shown are
    /// kept, so an expanded output stays expanded) and ticks everything that is still running.
    /// It runs no command: the list is in-memory state, not a poll of the CLI.
    /// </summary>
    [RelayCommand]
    private void Refresh()
    {
        if (IsMutationsTab)
        {
            _ = Mutations.LoadAsync();
            return;
        }

        LoadActivity();
        TickRunningRows();
    }

    [RelayCommand]
    private void ClearActivity()
    {
        Services.Cli.ClearActivity();
        LoadActivity();
    }

    [RelayCommand]
    private void DismissNotice()
    {
        HasNotice = false;
        NoticeText = string.Empty;
    }

    /// <summary>Row actions report here; the panel shows the latest one in an InfoBar.</summary>
    private void ShowNotice(string message)
    {
        NoticeText = message;
        HasNotice = message.Length > 0;
    }

    private ActivityRow CreateRow(CliInvocation invocation) => new(invocation, Services.Cli, ShowNotice);

    /// <summary>
    /// Reads the runner's in-memory ring buffer. Not file/DB/process I/O - just a snapshot of a
    /// list already in memory. Rows for invocations that are already shown are reused rather than
    /// rebuilt, so a refresh does not collapse an expanded output or drop its scroll position.
    /// </summary>
    private void LoadActivity()
    {
        var existing = new Dictionary<string, ActivityRow>(Rows.Count, StringComparer.Ordinal);
        foreach (var row in Rows)
        {
            _ = existing.TryAdd(row.Invocation.Id, row);
        }

        var desired = new List<ActivityRow>();
        foreach (var invocation in Services.Cli.Activity)
        {
            desired.Add(existing.TryGetValue(invocation.Id, out var reused) ? reused : CreateRow(invocation));
        }

        SyncCollection(
            Rows,
            desired,
            static row => row.Invocation.Id,
            static (kept, wanted) => ReferenceEquals(kept, wanted));

        IsEmpty = Rows.Count == 0;
        EnsureTimerState();
    }

    /// <summary>
    /// Runs the timer exactly when it can do something: the panel is active and at least one
    /// row is still running. Idempotent, and cheap enough (a scan of at most
    /// <see cref="CliRunner.ActivityCapacity"/> rows) to call after every change to either.
    /// </summary>
    private void EnsureTimerState()
    {
        if (IsActive && Rows.Any(row => row.IsRunning))
        {
            if (!_timer.IsEnabled)
            {
                _timer.Start();
            }
        }
        else
        {
            _timer.Stop();
        }
    }

    private void OnInvocationStarted(object? sender, CliInvocation invocation)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        dispatcher.BeginInvoke(() =>
        {
            // LoadActivity may already have picked this one up from the ring between the runner
            // recording it and this callback running; never show an invocation twice.
            if (Rows.Any(r => ReferenceEquals(r.Invocation, invocation)))
            {
                return;
            }

            Rows.Insert(0, CreateRow(invocation));
            while (Rows.Count > Services.Cli.ActivityCapacity)
            {
                Rows.RemoveAt(Rows.Count - 1);
            }

            IsEmpty = Rows.Count == 0;
            EnsureTimerState();
        });
    }

    private void OnInvocationCompleted(object? sender, CliInvocation invocation)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        dispatcher.BeginInvoke(() =>
        {
            var row = Rows.FirstOrDefault(r => ReferenceEquals(r.Invocation, invocation));
            row?.Tick();
            EnsureTimerState();
        });
    }

    private void TickRunningRows()
    {
        foreach (var row in Rows)
        {
            if (row.IsRunning)
            {
                row.Tick();
            }
        }

        // The last running row just finished: nothing left for the timer to do.
        EnsureTimerState();
    }
}

/// <summary>
/// One invocation row, refreshed in place from the live <see cref="CliInvocation"/> - status
/// fields re-read each tick, output pulled as a delta through
/// <see cref="CliInvocation.CopyNewLines"/>.
/// </summary>
public sealed partial class ActivityRow : ObservableObject
{
    [ObservableProperty]
    private bool _isRunning = true;

    [ObservableProperty]
    private string _startedText = string.Empty;

    [ObservableProperty]
    private string _relativeStartText = string.Empty;

    [ObservableProperty]
    private string _durationText = "—";

    [ObservableProperty]
    private string _exitBadgeText = "running";

    /// <summary>Ok / Bad / Warn / Neutral - drives the badge colour.</summary>
    [ObservableProperty]
    private string _exitBadgeKey = "Neutral";

    [ObservableProperty]
    private bool _usedStdinSecret;

    [ObservableProperty]
    private bool _hasFailure;

    [ObservableProperty]
    private string _failureText = string.Empty;

    /// <summary>
    /// Whether the card shows its output. Kept here, not in the card: the panel's card list is virtualized and recycling, so a
    /// card's elements are reused for other entries as it scrolls and are gone while this entry is out of view. The same goes for
    /// <see cref="IsFollowing"/> and, on <see cref="Transcript"/>, where the operator was reading and what they selected.
    /// </summary>
    [ObservableProperty]
    private bool _isExpanded;

    /// <summary>
    /// Whether the output list keeps its newest line in view as output arrives. On by default; the view turns it
    /// off when the operator scrolls up to read (and back on when they scroll to the end again), and the Follow
    /// checkbox flips it directly, so the two stay one setting. Two-way with the view.
    /// </summary>
    [ObservableProperty]
    private bool _isFollowing = true;

    /// <summary>The Output expander's header: what the transcript holds now, e.g. <c>Output · 1,204 lines</c>.</summary>
    [ObservableProperty]
    private string _outputHeader = "Output";

    /// <summary>True until the run has printed a line.</summary>
    [ObservableProperty]
    private bool _isOutputEmpty = true;

    /// <summary>True while the run can be cancelled: running, not exempt, and not already being cancelled.</summary>
    [ObservableProperty]
    private bool _canCancel;

    /// <summary>Why Cancel is (dis)abled - also the button's tooltip. Empty once the run has finished.</summary>
    [ObservableProperty]
    private string _cancelHint = string.Empty;

    private readonly CliRunner? _runner;
    private readonly Action<string>? _notify;

    /// <param name="invocation">The live instance from the runner's activity ring.</param>
    /// <param name="runner">Needed for Cancel; a row built without one simply cannot cancel.</param>
    /// <param name="notify">Receives a one-line result for each row action.</param>
    public ActivityRow(CliInvocation invocation, CliRunner? runner = null, Action<string>? notify = null)
    {
        Invocation = invocation;
        _runner = runner;
        _notify = notify;

        // Fixed for the life of the row: the tier is a function of the argv alone.
        var tier = CommandTiers.Classify(invocation.Argv);
        // Words and tone come from the shared command review, so a tier reads the same here as in every dialog.
        TierText = CommandReview.LabelFor(tier);
        TierKey = CommandReview.ToneFor(tier);
        TierHelp = tier switch
        {
            CommandTier.ReadOnly => "Only reads state; runs without a confirmation step.",
            CommandTier.Destructive => "Removes or resets something; needs a confirmation with the exact command.",
            _ => "Changes DefenseClaw state; needs a confirmation with the exact command.",
        };

        Tick();
    }

    /// <summary>
    /// The live, shared instance from <see cref="CliRunner.Activity"/>, still being appended
    /// to while its process runs. Output is only ever read through
    /// <see cref="CliInvocation.CopyNewLines"/>, which takes the invocation's own lock.
    /// </summary>
    public CliInvocation Invocation { get; }

    public string CommandLine => Invocation.CommandLine;

    /// <summary>The argv as an operator would type it: the tool's name (no install path), then the arguments.</summary>
    public string ShortCommand =>
        string.Join(' ', new[] { Path.GetFileNameWithoutExtension(Invocation.Executable) }
            .Concat(Invocation.Argv)
            .Select(QuoteForDisplay));

    /// <summary>Read-only / Changes state / Destructive, from <see cref="CommandTiers"/> via <see cref="CommandReview.LabelFor"/>.</summary>
    public string TierText { get; }

    /// <summary>Neutral / Warn / Bad - the tone key for the tier badge (destructive is red).</summary>
    public string TierKey { get; }

    /// <summary>
    /// True when the run carried secrets in its environment (names only are recorded; see
    /// <see cref="CliInvocation.EnvironmentNames"/>).
    /// </summary>
    public bool UsedEnvironmentSecret => Invocation.EnvironmentNames.Count > 0;

    /// <summary><c>env: NAME=•••</c> for the chip's tooltip; never a value.</summary>
    public string EnvironmentText => Invocation.EnvironmentDisplay;

    public string TierHelp { get; }

    /// <summary>
    /// The incremental line model behind <see cref="Output"/>: the invocation's transcript, kept up to date from its
    /// cursor and trimmed the way the invocation trims itself. See <see cref="ActivityTranscript"/>.
    /// </summary>
    public ActivityTranscript Transcript { get; } = new();

    /// <summary>
    /// The lines the output list binds to. That list is a virtualizing, recycling <c>ListBox</c> with a bounded
    /// height (the Logs panel's pattern): a transcript can be 200,000 lines long (a <see cref="CliRunOptions.JsonRead"/>
    /// invocation), and only the few dozen lines in view are ever turned into elements.
    /// </summary>
    public TranscriptCollection Output => Transcript.Lines;

    /// <summary>
    /// What a screen reader announces for the row (UI Automation falls back to
    /// <c>ToString()</c> for an item with no explicit name): the command, its state and tier, and
    /// when it started - not the type name.
    /// </summary>
    public override string ToString() =>
        $"{ShortCommand}. {ExitBadgeText}. {TierText}. Started {StartedText}, {DurationText}.";

    /// <summary>
    /// Re-reads the invocation and applies it. Safe to call from the UI thread while the
    /// process is still running on a background thread.
    /// <para>
    /// The completion fields are read into locals once, at the top, and everything below uses
    /// those: the invocation can finish mid-tick, and re-reading <c>FinishedAt</c> per use is
    /// what would let a row render a "running" badge next to a settled duration. That is the
    /// whole of what <see cref="CliInvocation.Snapshot"/> bought here - it reads those same
    /// fields without locking too - and it charged a full copy of the accumulated transcript
    /// for it, twice a second, for the entire length of the run.
    /// </para>
    /// </summary>
    public void Tick()
    {
        var startedAt = Invocation.StartedAt;
        var finishedAt = Invocation.FinishedAt;
        var exitCode = Invocation.ExitCode;
        var failureReason = Invocation.FailureReason;
        var cancelRequested = Invocation.CancelRequested;
        var isRunning = finishedAt is null;

        IsRunning = isRunning;
        StartedText = startedAt.ToLocalTime().ToString("MMM d HH:mm:ss", CultureInfo.CurrentCulture);
        RelativeStartText = Relative(startedAt);
        UsedStdinSecret = Invocation.UsedStdinSecret;
        DurationText = finishedAt is { } finished
            ? FormatDuration(finished - startedAt)
            : FormatDuration(DateTimeOffset.UtcNow - startedAt);

        ApplyBadge(isRunning, exitCode, failureReason, cancelRequested);
        ApplyCancelState(isRunning, cancelRequested);
        SyncOutput();
    }

    /// <summary>
    /// Copies the exact command line (executable and argv) in the form PowerShell reads as one literal argument per
    /// name (<see cref="CliInvocation.PowerShellCommandLine"/>), so an argument such as <c>x&amp;calc</c> cannot run
    /// anything when pasted. What the card shows (<see cref="CommandLine"/>) is for reading and quotes only whitespace.
    /// </summary>
    [RelayCommand]
    private void Copy() => CopyText(Invocation.PowerShellCommandLine, "Copied the command line for PowerShell.");

    /// <summary>
    /// Copies the retained transcript as plain text, one line per line, exactly as it is shown
    /// (including the truncation marker when output was dropped). Read on click, from the whole
    /// retained transcript - not only what the row has rendered so far.
    /// </summary>
    [RelayCommand]
    private void CopyOutput() =>
        CopyText(
            string.Join(Environment.NewLine, Invocation.OutputLines.Select(l => l.Text)),
            Invocation.OutputLines.Count == 0 ? "There is no output to copy yet." : "Copied the output.");

    /// <summary>
    /// Saves the command, its outcome and its transcript to a file the operator chooses. The
    /// transcript is what the runner already scrubbed of secrets on capture; argv never holds one.
    /// </summary>
    [RelayCommand]
    private void ExportLog()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export command log",
            FileName = SuggestFileName(Invocation),
            DefaultExt = ".log",
            AddExtension = true,
            OverwritePrompt = true,
            Filter = "Log files (*.log)|*.log|Text files (*.txt)|*.txt|All files (*.*)|*.*",
        };

        var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
        var chosen = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        if (chosen != true)
        {
            return;
        }

        try
        {
            File.WriteAllText(dialog.FileName, BuildLogExport(Invocation), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            _notify?.Invoke($"Exported the log to {dialog.FileName}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _notify?.Invoke($"Could not write {dialog.FileName}: {ex.Message}");
        }
    }

    /// <summary>
    /// Asks the runner to kill this run's whole process tree. The runner says no, with a reason,
    /// for a run that survives app shutdown (the upgrade installer) or one that already finished;
    /// the reason is shown rather than swallowed. On success the row flips to "cancelling..."
    /// at once and settles when the run reports back.
    /// </summary>
    [RelayCommand]
    private void Cancel()
    {
        if (_runner is null)
        {
            _notify?.Invoke("This entry cannot be cancelled from here.");
            return;
        }

        var accepted = _runner.Cancel(Invocation, out var reason);
        if (!accepted)
        {
            _notify?.Invoke(reason);
        }

        Tick();
    }

    /// <summary>
    /// Takes the values rather than the invocation, so it is reading exactly what
    /// <see cref="Tick"/> read - a second read of a live field could disagree with the one
    /// the duration and running flag were computed from.
    /// </summary>
    private void ApplyBadge(bool isRunning, int? exitCode, string? failureReason, bool cancelRequested)
    {
        if (isRunning)
        {
            ExitBadgeText = cancelRequested ? "cancelling…" : "running";
            ExitBadgeKey = "Neutral";
            HasFailure = false;
            FailureText = string.Empty;
            return;
        }

        if (failureReason is { Length: > 0 } failure)
        {
            // "cancelled" is the operator's own doing, not a fault - but it is not a success either.
            ExitBadgeText = failure.StartsWith("cancelled", StringComparison.Ordinal) ? "cancelled" : "failed";
            ExitBadgeKey = "Warn";
            HasFailure = true;
            FailureText = failure;
            return;
        }

        HasFailure = false;
        FailureText = string.Empty;

        if (exitCode is { } code)
        {
            ExitBadgeText = code == 0 ? "exit 0" : $"exit {code.ToString(CultureInfo.CurrentCulture)}";
            ExitBadgeKey = code == 0 ? "Ok" : "Bad";
        }
        else
        {
            ExitBadgeText = "exit unknown";
            ExitBadgeKey = "Neutral";
        }
    }

    private void ApplyCancelState(bool isRunning, bool cancelRequested)
    {
        if (!isRunning)
        {
            CanCancel = false;
            CancelHint = string.Empty;
        }
        else if (Invocation.SurvivesShutdown)
        {
            CanCancel = false;
            CancelHint =
                "This is the upgrade installer. It cannot be cancelled from here: killing it part-way can leave " +
                "DefenseClaw half-installed, so it stops only when it finishes.";
        }
        else if (cancelRequested)
        {
            CanCancel = false;
            CancelHint = "Cancelling - waiting for the process tree to exit.";
        }
        else
        {
            CanCancel = _runner is not null;
            CancelHint = "Kill this command and everything it started.";
        }
    }

    /// <summary>
    /// Appends whatever arrived since the last tick and drops what the invocation has since trimmed, so the bound
    /// collection is never rebuilt. The cursor bookkeeping - a position in the invocation's append sequence, not an index
    /// into its retained lines, which shift when a chatty invocation trims itself - lives in <see cref="ActivityTranscript"/>.
    /// </summary>
    private void SyncOutput()
    {
        if (!Transcript.Pull(Invocation))
        {
            return;
        }

        IsOutputEmpty = Transcript.LineCount == 0 && Transcript.DroppedLineCount == 0;
        OutputHeader = Transcript.Describe();
    }

    /// <summary>
    /// Copies the lines selected in the output list (Ctrl+C, or the list's context menu), in transcript order however the
    /// selection was made. The whole transcript is Copy output.
    /// </summary>
    public void CopyLines(IEnumerable<ActivityOutputLine> selected)
    {
        var text = ActivityTranscript.FormatLines(selected, out var count);
        if (count == 0)
        {
            _notify?.Invoke("Select one or more output lines first, or use Copy output for all of it.");
            return;
        }

        CopyText(text, count == 1 ? "Copied 1 line." : $"Copied {count.ToString("N0", CultureInfo.CurrentCulture)} lines.");
    }

    /// <summary>Test seam: what the copy actions write with. Null uses the real clipboard.</summary>
    internal Action<string>? ClipboardWriter { get; set; }

    private void CopyText(string text, string success)
    {
        try
        {
            if (ClipboardWriter is { } write)
            {
                write(text);
            }
            else
            {
                Clipboard.SetText(text);
            }

            _notify?.Invoke(success);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Another process owns the clipboard; say so rather than pretending it worked.
            _notify?.Invoke("Could not copy: another program is holding the clipboard. Try again.");
        }
    }

    /// <summary>
    /// The text of an exported log: a short header (what ran, when, how it ended) followed by
    /// the transcript. Standard-error lines carry a <c>[stderr]</c> prefix so the two streams stay
    /// distinguishable in a flat file; notices (the truncation marker) are kept verbatim.
    /// </summary>
    internal static string BuildLogExport(CliInvocation invocation)
    {
        var lines = invocation.OutputLines;
        var builder = new StringBuilder();

        _ = builder.AppendLine("DefenseClaw for Windows - command log");
        _ = builder.Append("Command:  ").AppendLine(invocation.CommandLine);
        _ = builder.Append("Started:  ").AppendLine(invocation.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture));

        if (invocation.FinishedAt is { } finished)
        {
            _ = builder.Append("Finished: ").AppendLine(finished.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture));
            _ = builder.Append("Duration: ").AppendLine(FormatDuration(finished - invocation.StartedAt));
        }
        else
        {
            _ = builder.AppendLine("Finished: (still running when exported - this is a snapshot)");
        }

        _ = builder.Append("Result:   ").AppendLine(DescribeResult(invocation));

        if (invocation.UsedStdinSecret)
        {
            _ = builder.AppendLine("Stdin:    a secret was piped in on stdin; its value is never recorded");
        }

        if (invocation.EnvironmentNames.Count > 0)
        {
            _ = builder.Append("Env:      ").Append(string.Join(", ", invocation.EnvironmentNames))
                .AppendLine(" set for this run only; values are never recorded");
        }

        if (invocation.IsOutputTruncated)
        {
            _ = builder.Append("Note:     ").Append(invocation.DroppedOutputLineCount.ToString(CultureInfo.InvariantCulture))
                .AppendLine(" earlier line(s) were dropped to stay inside the retention budget");
        }

        _ = builder.AppendLine(new string('-', 72));

        foreach (var line in lines)
        {
            _ = builder.AppendLine(line.Stream == CliStream.StandardError ? "[stderr] " + line.Text : line.Text);
        }

        return builder.ToString();
    }

    private static string DescribeResult(CliInvocation invocation)
    {
        if (invocation.IsRunning)
        {
            return invocation.CancelRequested ? "running (cancel requested)" : "running";
        }

        if (invocation.FailureReason is { Length: > 0 } reason)
        {
            return reason;
        }

        return invocation.ExitCode is { } code
            ? $"exit {code.ToString(CultureInfo.InvariantCulture)}"
            : "exit code unknown";
    }

    /// <summary>
    /// <c>defenseclaw-doctor-20260928-221009.log</c>: the tool, the command path up to the first
    /// flag (at most three words), and the start time - only characters a file name can hold.
    /// </summary>
    internal static string SuggestFileName(CliInvocation invocation)
    {
        var words = new List<string> { Path.GetFileNameWithoutExtension(invocation.Executable) };
        words.AddRange(invocation.Argv.TakeWhile(a => !a.StartsWith('-')).Take(CommandTiers.MaxPathTokens));

        var safe = new StringBuilder();
        foreach (var word in words)
        {
            foreach (var ch in word)
            {
                _ = safe.Append(char.IsAsciiLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '-');
            }

            _ = safe.Append('-');
        }

        var stem = safe.ToString().Trim('-');
        while (stem.Contains("--", StringComparison.Ordinal))
        {
            stem = stem.Replace("--", "-", StringComparison.Ordinal);
        }

        if (stem.Length > 60)
        {
            stem = stem[..60].TrimEnd('-');
        }

        return $"{(stem.Length == 0 ? "command" : stem)}-{invocation.StartedAt.ToLocalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.log";
    }

    private static string QuoteForDisplay(string value) =>
        value.Length == 0 || value.Any(char.IsWhiteSpace) ? $"\"{value}\"" : value;

    private static string Relative(DateTimeOffset value)
    {
        var delta = DateTimeOffset.UtcNow - value;
        return delta switch
        {
            { TotalSeconds: < 5 } => "just now",
            { TotalSeconds: < 60 } => $"{(int)delta.TotalSeconds}s ago",
            { TotalMinutes: < 60 } => $"{(int)delta.TotalMinutes}m ago",
            { TotalHours: < 24 } => $"{(int)delta.TotalHours}h ago",
            _ => $"{(int)delta.TotalDays}d ago",
        };
    }

    private static string FormatDuration(TimeSpan span) => span.TotalHours >= 1
        ? $"{(int)span.TotalHours}h {span.Minutes}m {span.Seconds}s"
        : span.TotalMinutes >= 1
            ? $"{(int)span.TotalMinutes}m {span.Seconds}s"
            : $"{span.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)}s";
}

/// <summary>One captured line of subprocess output.</summary>
public sealed record CliOutputRow(string Text, bool IsError)
{
    /// <summary>The line as a screen reader should say it (a record dumps its members otherwise).</summary>
    public override string ToString() => IsError ? $"error: {Text}" : Text;

    /// <summary>
    /// The line as a text box shows it: a stderr line starts with "! " (the gutter mark of the list consoles), so the difference
    /// from stdout survives where there is no colour or gutter - a screen reader, a screenshot, a paste.
    /// </summary>
    public string DisplayLine => IsError ? "! " + Text : Text;
}
