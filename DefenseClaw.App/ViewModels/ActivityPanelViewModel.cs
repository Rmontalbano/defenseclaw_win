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
/// one event to one row. Instead, a half-second <see cref="DispatcherTimer"/> re-reads
/// <see cref="CliInvocation.Snapshot"/> for every row still running, which is also what keeps
/// the elapsed-time readout current. <see cref="CliRunner"/> raises its events from
/// background threads (process callbacks, or continuations captured with
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

    /// <summary>Stated once, from the runner's fixed capacity - never changes at runtime.</summary>
    public string CapacityNote =>
        $"Showing the last {Services.Cli.ActivityCapacity.ToString(CultureInfo.CurrentCulture)} invocations, in memory only. Nothing here survives an app restart.";

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

/// <summary>One invocation row, refreshed from <see cref="CliInvocation.Snapshot"/>.</summary>
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

    private int _syncedOutputCount;

    public ActivityRow(CliInvocation invocation)
    {
        Invocation = invocation;
        Tick();
    }

    /// <summary>The live, shared instance from <see cref="CliRunner.Activity"/> - used only for identity and to pull fresh snapshots, never read directly.</summary>
    public CliInvocation Invocation { get; }

    public string CommandLine => Invocation.CommandLine;

    public ObservableCollection<CliOutputRow> Output { get; } = new();

    /// <summary>
    /// Re-reads a <see cref="CliInvocation.Snapshot"/> and applies it. Safe to call from the
    /// UI thread even while the process is still running on a background thread, because the
    /// snapshot is an immutable copy rather than the live, concurrently-mutated instance.
    /// </summary>
    public void Tick()
    {
        var snapshot = Invocation.Snapshot();
        IsRunning = snapshot.IsRunning;
        StartedText = snapshot.StartedAt.ToLocalTime().ToString("MMM d HH:mm:ss", CultureInfo.CurrentCulture);
        RelativeStartText = Relative(snapshot.StartedAt);
        UsedStdinSecret = snapshot.UsedStdinSecret;
        DurationText = snapshot.IsRunning
            ? FormatDuration(DateTimeOffset.UtcNow - snapshot.StartedAt)
            : snapshot.Duration is { } duration ? FormatDuration(duration) : "—";

        ApplyBadge(snapshot);
        SyncOutput(snapshot.OutputLines);
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

    private void ApplyBadge(CliInvocation snapshot)
    {
        if (snapshot.IsRunning)
        {
            ExitBadgeText = "running";
            ExitBadgeKey = "Neutral";
            HasFailure = false;
            FailureText = string.Empty;
            return;
        }

        if (snapshot.FailureReason is { Length: > 0 } failure)
        {
            ExitBadgeText = "failed";
            ExitBadgeKey = "Warn";
            HasFailure = true;
            FailureText = failure;
            return;
        }

        HasFailure = false;
        FailureText = string.Empty;

        if (snapshot.ExitCode is { } code)
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
    /// Output only ever grows, so each tick appends the delta since the last sync instead of
    /// rebuilding the whole collection.
    /// </summary>
    private void SyncOutput(IReadOnlyList<CliOutputLine> lines)
    {
        for (var i = _syncedOutputCount; i < lines.Count; i++)
        {
            var line = lines[i];
            Output.Add(new CliOutputRow(line.Text, line.Stream == CliStream.StandardError));
        }

        _syncedOutputCount = lines.Count;
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
