using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// Every command this app has shelled out, newest first: <see cref="CliRunner.Activity"/>
/// plus <see cref="CliRunner.InvocationStarted"/> / <see cref="CliRunner.InvocationCompleted"/>
/// to keep the list live.
/// <para>
/// <b>Nothing shells out yet.</b> The app has not shipped any wizard that mutates
/// DefenseClaw state, so on a fresh install this list is empty by design - that is not a
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
/// </summary>
public sealed partial class ActivityPanelViewModel : PanelViewModelBase
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(500);

    private readonly DispatcherTimer _timer;

    [ObservableProperty]
    private bool _isEmpty = true;

    public ActivityPanelViewModel(AppServices services)
        : base(services)
    {
        Services.Cli.InvocationStarted += OnInvocationStarted;
        Services.Cli.InvocationCompleted += OnInvocationCompleted;

        _timer = new DispatcherTimer { Interval = TickInterval };
        _timer.Tick += (_, _) => TickRunningRows();
        _timer.Start();

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
        $"({(CliInvocation.MaxRetainedOutputBytes / 1024).ToString(CultureInfo.CurrentCulture)} KiB) of output - past that the oldest lines are dropped and the invocation says how many. " +
        "Nothing here survives an app restart.";

    public string EmptyTitle => "No CLI activity yet";

    public string EmptyDetail =>
        "The GUI never edits DefenseClaw state directly - every mutation it makes runs as a subprocess, and its exact " +
        "command line, live output and exit code will show up here. The app does not ship any wizard that mutates " +
        "state yet, so this list is expected to stay empty for now.";

    public ObservableCollection<ActivityRow> Rows { get; } = new();

    public override Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        LoadActivity();
        return Task.CompletedTask;
    }

    [RelayCommand]
    private void ClearActivity()
    {
        Services.Cli.ClearActivity();
        LoadActivity();
    }

    /// <summary>Reads the runner's in-memory ring buffer. Not file/DB/process I/O - just a snapshot of a list already in memory.</summary>
    private void LoadActivity()
    {
        Rows.Clear();
        foreach (var invocation in Services.Cli.Activity)
        {
            Rows.Add(new ActivityRow(invocation));
        }

        IsEmpty = Rows.Count == 0;
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
            Rows.Insert(0, new ActivityRow(invocation));
            while (Rows.Count > Services.Cli.ActivityCapacity)
            {
                Rows.RemoveAt(Rows.Count - 1);
            }

            IsEmpty = Rows.Count == 0;
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

    [ObservableProperty]
    private bool _isExpanded;

    /// <summary>
    /// Reused across ticks so a running row allocates nothing in steady state - it is cleared
    /// and refilled with only the lines that arrived since the last tick.
    /// </summary>
    private readonly List<CliOutputLine> _outputBuffer = new();

    /// <summary>
    /// Position in <see cref="Invocation"/>'s monotonic append sequence - deliberately not an
    /// index into its retained lines, which shift when a chatty invocation trims itself. See
    /// <see cref="CliInvocation.CopyNewLines"/>.
    /// </summary>
    private int _outputCursor;

    public ActivityRow(CliInvocation invocation)
    {
        Invocation = invocation;
        Tick();
    }

    /// <summary>
    /// The live, shared instance from <see cref="CliRunner.Activity"/>, still being appended
    /// to while its process runs. Output is only ever read through
    /// <see cref="CliInvocation.CopyNewLines"/>, which takes the invocation's own lock.
    /// </summary>
    public CliInvocation Invocation { get; }

    public string CommandLine => Invocation.CommandLine;

    public ObservableCollection<CliOutputRow> Output { get; } = new();

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
        var isRunning = finishedAt is null;

        IsRunning = isRunning;
        StartedText = startedAt.ToLocalTime().ToString("MMM d HH:mm:ss", CultureInfo.CurrentCulture);
        RelativeStartText = Relative(startedAt);
        UsedStdinSecret = Invocation.UsedStdinSecret;
        DurationText = finishedAt is { } finished
            ? FormatDuration(finished - startedAt)
            : FormatDuration(DateTimeOffset.UtcNow - startedAt);

        ApplyBadge(isRunning, exitCode, failureReason);
        SyncOutput();
    }

    [RelayCommand]
    private void Copy()
    {
        try
        {
            Clipboard.SetText(CommandLine);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Another process owns the clipboard; nothing useful to do about it.
        }
    }

    /// <summary>
    /// Takes the three values rather than the invocation, so it is reading exactly what
    /// <see cref="Tick"/> read - a second read of a live field could disagree with the one
    /// the duration and running flag were computed from.
    /// </summary>
    private void ApplyBadge(bool isRunning, int? exitCode, string? failureReason)
    {
        if (isRunning)
        {
            ExitBadgeText = "running";
            ExitBadgeKey = "Neutral";
            HasFailure = false;
            FailureText = string.Empty;
            return;
        }

        if (failureReason is { Length: > 0 } failure)
        {
            ExitBadgeText = "failed";
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

    /// <summary>
    /// Appends whatever arrived since the last tick, so the bound collection is never rebuilt.
    /// <para>
    /// The cursor is a position in the invocation's append sequence, not an index into its
    /// retained lines. That distinction is load-bearing once an invocation is chatty enough
    /// to trim itself: a remembered <c>OutputLines.Count</c> would point past the first
    /// unread line after a trim and skip everything the trim shifted underneath it, silently.
    /// A row that fell behind a trim gets a notice line saying how much it missed instead.
    /// </para>
    /// </summary>
    private void SyncOutput()
    {
        _outputBuffer.Clear();
        _outputCursor = Invocation.CopyNewLines(_outputCursor, _outputBuffer);

        foreach (var line in _outputBuffer)
        {
            Output.Add(new CliOutputRow(line.Text, line.Stream == CliStream.StandardError));
        }
    }

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
public sealed record CliOutputRow(string Text, bool IsError);
