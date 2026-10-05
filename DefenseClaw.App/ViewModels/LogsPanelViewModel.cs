using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.Core.Audit;
using DefenseClaw.Core.Logs;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// The Logs panel: four streams behind one list - <c>gateway.log</c> and <c>watchdog.log</c> (live tails) and the canonical
/// <b>Verdicts</b> and <b>Events</b> streams read from <c>audit.db</c> - with the Mac's presets, severity floor, action and event
/// filters, "N shown · N matching · N total" and adjacent-duplicate collapse.
/// <para>
/// <b>The 0.8.7 sidecar writes bare lines.</b> There is no timestamp and no level on almost every line - just <c>[component] message</c>
/// (plus a box-drawing banner at startup), so <see cref="LogLine.Component"/> is the axis worth filtering on, and a row's severity comes from
/// the words in it (error, warn …), as on the Mac.
/// </para>
/// <para>
/// <b>Lifecycle of the file streams.</b> <see cref="AppServices.GatewayLog"/> and <see cref="AppServices.WatchdogLog"/> are constructed with
/// <c>StartAtEnd: true</c> and deliberately not started - this panel owns them. On first load it rewinds each tailer with
/// <see cref="LogTailer.Reset"/> and drains <see cref="LogTailer.ReadNewLines"/> to seed history, then starts live watching on both so
/// switching the source is instant. Both <see cref="LogTailer.LinesReceived"/> and <see cref="LogTailer.Truncated"/> fire on a background
/// thread, so every handler marshals through <see cref="Application.Current"/>'s dispatcher. So do <see cref="LogTailer.TailFaulted"/> and
/// <see cref="LogTailer.TailRecovered"/>: a tail that hit an error keeps retrying by itself, and while it does the panel says so in a
/// "Log tail paused" banner rather than looking like a quiet gateway.
/// </para>
/// <para>
/// <b>Buffering never stops; projecting does.</b> The tailers run for the life of the process so no line is ever lost, and every arriving line
/// goes into its source's buffer whether or not the panel is on screen. What is deferred while the panel is inactive (see the activation
/// contract on <see cref="PanelViewModelBase"/>) is the <i>projection</i> - the filtering, collapsing and adding to
/// <see cref="DisplayedLines"/>, which on a chatty gateway is a batch of list inserts several times a second. The panel is marked stale
/// instead, and <see cref="OnActivated"/> re-projects the buffer once, so what is seen on return is exactly what a panel that had been live all
/// along would show. The buffer's own trim (<see cref="MaxBufferedLines"/>) is unchanged and bounds that catch-up.
/// </para>
/// <para>
/// <b>The database streams are read only while the panel is on screen.</b> Verdicts and Events are the newest 1,000 canonical events from
/// <see cref="EventStreamReader"/> (read-only, off the UI thread, interrupted when the operator leaves or switches stream). They are read when
/// the panel is activated on one, when one is switched to, on Reload, and then every <see cref="StructuredPollInterval"/> while it stays on
/// screen; a poll that finds the same rows changes nothing, so the selection and the scroll position survive. Events leave out the
/// <c>telemetry.ingest</c> bucket (most of the table) unless asked. The Windows build has no separate OTel stream: the Events stream is
/// every canonical event, which is what the Mac's OTel stream holds.
/// </para>
/// <para>
/// <b>Redaction.</b> Every line and event shown or copied is masked by <see cref="DisplayRedaction"/> before it is cut; there is no switch.
/// </para>
/// </summary>
public sealed partial class LogsPanelViewModel : PanelViewModelBase, IAcceptsNavigation
{
    /// <summary>The source names, as the segmented control's values.</summary>
    public const string GatewaySource = "Gateway";

    public const string VerdictsSource = "Verdicts";

    public const string EventsSource = "Events";

    public const string WatchdogSource = "Watchdog";

    /// <summary>How often a database stream is re-read while it is on screen.</summary>
    public static readonly TimeSpan StructuredPollInterval = TimeSpan.FromSeconds(5);

    private const int MaxBufferedLines = 5000;
    private const int MaxSeedIterations = 50;
    private const string NoComponentLabel = "(no component)";

    private readonly SourceState _gateway;
    private readonly SourceState _watchdog;
    private readonly StructuredState _verdicts = new(VerdictsSource, EventStreamKind.Verdicts);
    private readonly StructuredState _events = new(EventsSource, EventStreamKind.Events);
    private List<LogEntry> _selectedEntries = new();
    private EventStreamReader? _reader;
    private CancellationTokenSource? _loadCts;
    private DispatcherTimer? _pollTimer;
    private int _matching;
    private int _suspend;

    /// <summary>
    /// True once <see cref="InitializeAsync"/> has seeded both buffers and attached the handlers, on the UI thread. Until then the buffers are
    /// still being filled on a background thread, so <see cref="OnActivated"/> (which may run first on the very first visit) must not read them.
    /// </summary>
    private bool _seeded;

    /// <summary>True while the background seed is running: nothing may project the buffers then (see <see cref="Reproject"/>).</summary>
    private bool _seeding;

    /// <summary>
    /// True when lines arrived, or a rotation cleared the buffer, while the panel was inactive and <see cref="DisplayedLines"/> therefore no
    /// longer mirrors the buffer. Cleared by the next projection.
    /// </summary>
    private bool _projectionStale;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFileSource))]
    [NotifyPropertyChangedFor(nameof(IsStructuredSource))]
    [NotifyPropertyChangedFor(nameof(IsEventsSource))]
    [NotifyCanExecuteChangedFor(nameof(ClearCommand))]
    private string _activeSource = GatewaySource;

    [ObservableProperty]
    private string _filterText = string.Empty;

    [ObservableProperty]
    private bool _autoScroll = true;

    /// <summary>The preset (see <see cref="LogPresets.Names"/>); "no-noise", as on the Mac, until the operator or a navigation request says otherwise.</summary>
    [ObservableProperty]
    private string _selectedPreset = LogPresets.NoNoise;

    [ObservableProperty]
    private string _selectedSeverity = LogPresets.AnySeverity;

    [ObservableProperty]
    private string _selectedAction = "all";

    [ObservableProperty]
    private string _selectedEvent = "all";

    /// <summary>Events only: include the <c>telemetry.ingest</c> bucket, which is hidden by default (it is most of the table).</summary>
    [ObservableProperty]
    private bool _includeTelemetry;

    [ObservableProperty]
    private string _statusPath = string.Empty;

    /// <summary>"N shown · N matching · N total", the Mac's count line.</summary>
    [ObservableProperty]
    private string _statusLineCount = string.Empty;

    [ObservableProperty]
    private string _liveStateText = "live";

    [ObservableProperty]
    private bool _isEmpty;

    [ObservableProperty]
    private string _emptyTitle = string.Empty;

    [ObservableProperty]
    private string _emptyDetail = string.Empty;

    [ObservableProperty]
    private bool _showRotationNotice;

    [ObservableProperty]
    private string _rotationNoticeText = string.Empty;

    [ObservableProperty]
    private bool _showTailFault;

    [ObservableProperty]
    private string _tailFaultText = string.Empty;

    /// <summary>True while a database stream is being read and nothing is on screen yet.</summary>
    [ObservableProperty]
    private bool _isLoading;

    /// <summary>True while a database stream could not be read: <see cref="StreamNoticeText"/> says why.</summary>
    [ObservableProperty]
    private bool _showStreamNotice;

    [ObservableProperty]
    private string _streamNoticeText = string.Empty;

    public LogsPanelViewModel(AppServices services)
        : base(services)
    {
        _gateway = new SourceState("Gateway", Services.GatewayLog, Services.Paths.GatewayLogPath);
        _watchdog = new SourceState("Watchdog", Services.WatchdogLog, Services.Paths.WatchdogLogPath);
    }

    public override string Title => "Logs";

    public override string Description => "gateway.log and watchdog.log tails, and the Verdicts and Events streams from audit.db, with presets and filters.";

    /// <summary>The preset menu.</summary>
    public IReadOnlyList<string> Presets => LogPresets.Names;

    public IReadOnlyList<string> SeverityOptions => LogPresets.SeverityOptions;

    public IReadOnlyList<string> ActionOptions => LogPresets.ActionOptions;

    public IReadOnlyList<string> EventOptions => LogPresets.EventOptions;

    /// <summary>True on <c>gateway.log</c> / <c>watchdog.log</c>, which have a component checklist and can be cleared.</summary>
    public bool IsFileSource => !IsStructuredSource;

    /// <summary>True on Verdicts and Events, which are read from the database.</summary>
    public bool IsStructuredSource => ActiveSource is VerdictsSource or EventsSource;

    /// <summary>True on Events, which alone has the telemetry switch.</summary>
    public bool IsEventsSource => ActiveSource == EventsSource;

    /// <summary>Components observed in the active log file's buffer, plus "(no component)".</summary>
    public ObservableCollection<ComponentFilterOption> ComponentFilters { get; } = new();

    /// <summary>
    /// The filtered view bound to the list - what survives the filters, with adjacent duplicates folded into one row. Bounded to
    /// <see cref="MaxBufferedLines"/>; a batch that overflows it is trimmed in one operation (see
    /// <see cref="BatchObservableCollection{T}.AppendCapped"/>), not one row at a time.
    /// </summary>
    public BatchObservableCollection<LogEntry> DisplayedLines { get; } = new();

    /// <summary>The reader behind Verdicts and Events; made on first use from the audit database path. A test points it at a fixture.</summary>
    internal EventStreamReader StreamReader
    {
        get => _reader ??= new EventStreamReader(Services.Paths.AuditDatabasePath);
        set => _reader = value;
    }

    public override async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _seeding = true;
        try
        {
            await Task.Run(() => SeedSource(_gateway), cancellationToken).ConfigureAwait(true);
            await Task.Run(() => SeedSource(_watchdog), cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            _seeding = false;
        }

        AttachHandlers(_gateway);
        AttachHandlers(_watchdog);

        // Both tailers stay live regardless of which source is on screen, so flipping the
        // toggle never has to wait on a fresh watcher to spin up.
        _gateway.Tailer.StartWatching();
        _watchdog.Tailer.StartWatching();

        RebuildComponentFilters();
        ApplyFilters();

        // Last, and on the UI thread with nothing awaited since the handlers were attached:
        // from here on the buffers are only touched from the UI thread, so OnActivated may
        // read them.
        _seeded = true;

        if (IsActive)
        {
            BeginStructuredIfShown();
        }
    }

    /// <summary>
    /// The catch-up half of the deferral described on the type: if anything arrived (or the file rotated) while the panel was away, re-project
    /// the buffer once now; and bring a database stream up to date and start polling it. A no-op before <see cref="InitializeAsync"/> has
    /// finished - on the first visit activation can run first, and the initial projection is InitializeAsync's own.
    /// </summary>
    protected override void OnActivated()
    {
        Services.ConnectorScope.Changed += OnConnectorScopeChanged;

        if (!_seeded)
        {
            return;
        }

        if (_projectionStale)
        {
            RebuildComponentFilters();
            ApplyFilters();
        }

        BeginStructuredIfShown();
    }

    /// <summary>Leaving the screen stops reading the database: a read in flight is interrupted and the poll timer stops.</summary>
    protected override void OnDeactivated()
    {
        Services.ConnectorScope.Changed -= OnConnectorScopeChanged;
        StopStructured();
    }

    private void OnConnectorScopeChanged(object? sender, EventArgs e)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            Reproject();
        }
        else
        {
            _ = dispatcher.BeginInvoke(Reproject);
        }
    }

    /// <summary>Called by the code-behind after the ListBox's selection changes.</summary>
    public void UpdateSelection(IEnumerable<LogEntry> selected) => _selectedEntries = selected.ToList();

    /// <summary>The row the inspector shows (the list's SelectedItem, so the first of an extended selection); null closes it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private LogEntry? _selectedEntry;

    /// <summary>True while a row is selected: the inspector is open.</summary>
    public bool HasSelection => SelectedEntry is not null;

    /// <summary>The inspector's X and Esc: deselects, which closes the pane.</summary>
    [RelayCommand]
    private void ClearSelection() => SelectedEntry = null;

    /// <summary>
    /// A navigation request (the Overview's Hook Calls tile sends <see cref="LogsPreset"/> "hooks"): a named preset applied from a clean
    /// slate - no search text, any severity, every action and event - on the stream it reads best from. "hooks" opens Events, the stream
    /// that holds the hook calls (the Mac's OTel stream, which it opens there too); the other presets open the gateway log, as on the Mac.
    /// </summary>
    public void Accept(object payload)
    {
        if (payload is not LogsPreset request || LogPresets.Resolve(request.Name) is not { } preset)
        {
            return;
        }

        _suspend++;
        try
        {
            FilterText = string.Empty;
            SelectedSeverity = LogPresets.AnySeverity;
            SelectedAction = "all";
            SelectedEvent = "all";
            IncludeTelemetry = false;
            AutoScroll = true;
            SelectedPreset = preset;
            ActiveSource = preset == LogPresets.Hooks ? EventsSource : GatewaySource;
        }
        finally
        {
            _suspend--;
        }

        AfterSourceOrFilterChanged(sourceChanged: true);
    }

    partial void OnActiveSourceChanged(string value)
    {
        ShowRotationNotice = false;
        ShowStreamNotice = false;
        SelectedEntry = null;
        if (ActiveFileState is { } state)
        {
            ShowFaultOf(state);
        }
        else
        {
            ShowTailFault = false;
        }

        AfterSourceOrFilterChanged(sourceChanged: true);
    }

    partial void OnSelectedPresetChanged(string value) => AfterSourceOrFilterChanged();

    partial void OnSelectedSeverityChanged(string value) => AfterSourceOrFilterChanged();

    partial void OnSelectedActionChanged(string value) => AfterSourceOrFilterChanged();

    partial void OnSelectedEventChanged(string value) => AfterSourceOrFilterChanged();

    /// <summary>The telemetry switch changes what is read, not only what is shown: the Events read is redone.</summary>
    partial void OnIncludeTelemetryChanged(bool value)
    {
        _events.Reset();
        AfterSourceOrFilterChanged(sourceChanged: ActiveSource == EventsSource);
    }

    /// <summary>
    /// The source update itself is debounced - <c>LogsPanel.xaml</c> binds <see cref="FilterText"/> with <c>Delay=400</c>, the same
    /// coalescing AuditPanel uses on its filter boxes - so this fires once per pause in typing, not once per keystroke, before it re-projects
    /// the whole <see cref="MaxBufferedLines"/> buffer.
    /// </summary>
    partial void OnFilterTextChanged(string value) => AfterSourceOrFilterChanged();

    partial void OnAutoScrollChanged(bool value) => LiveStateText = value ? "live" : "paused";

    /// <summary>One re-projection (and, when the stream changed, one read) however many properties a request or a handler just set.</summary>
    private void AfterSourceOrFilterChanged(bool sourceChanged = false)
    {
        if (_suspend > 0)
        {
            return;
        }

        Reproject();
        if (sourceChanged && IsActive && _seeded)
        {
            BeginStructuredIfShown();
        }
    }

    /// <summary>Re-derives the checklist and the list from what is buffered; nothing while the background seed still owns the buffers.</summary>
    private void Reproject()
    {
        if (_suspend > 0 || _seeding)
        {
            return;
        }

        RebuildComponentFilters();
        ApplyFilters();
    }

    [RelayCommand]
    private void SelectSource(string? source)
    {
        if (!string.IsNullOrEmpty(source))
        {
            ActiveSource = source;
        }
    }

    /// <summary>
    /// What F5 invokes. A log file's tailer already keeps its buffer current for the life of the process, so there is no file to re-read:
    /// Refresh re-projects the buffer through the current filters (rebuilding the component checklist from what it holds) and the code-behind
    /// then scrolls to the newest line when live. A database stream is read again. It waits for the seed to finish on the very first visit.
    /// </summary>
    [RelayCommand]
    private void Refresh()
    {
        if (!_seeded)
        {
            return;
        }

        if (IsStructuredSource)
        {
            _ = LoadStructuredAsync();
            return;
        }

        RebuildComponentFilters();
        ApplyFilters();
    }

    /// <summary>
    /// The Mac's "Reload from disk". On Verdicts / Events: a fresh read of the database. On a log file: the buffer is emptied and the tailer
    /// rewound to the start, so the live tail replays the file (within a poll or two, the newest 5,000 lines come back); a line already in
    /// flight when it is pressed can appear twice.
    /// </summary>
    [RelayCommand]
    private void ReloadFromDisk()
    {
        if (!_seeded)
        {
            return;
        }

        if (ActiveFileState is { } state)
        {
            ClearBuffer(state);
            state.Tailer.Reset();
            RebuildComponentFilters();
            ApplyFilters();
            return;
        }

        ActiveStructuredState?.Reset();
        _ = LoadStructuredAsync();
    }

    private bool CanClear() => IsFileSource;

    [RelayCommand(CanExecute = nameof(CanClear))]
    private void Clear()
    {
        if (ActiveFileState is not { } state)
        {
            return;
        }

        ClearBuffer(state);
        RebuildComponentFilters();
        ApplyFilters();
    }

    private static void ClearBuffer(SourceState state)
    {
        state.Buffer.Clear();
        state.Components.Clear();
        state.HasComponentless = false;
    }

    [RelayCommand]
    private void CopySelection()
    {
        if (_selectedEntries.Count == 0)
        {
            return;
        }

        // Always the masked text: a file line as it is, an event as its one-line summary (the inspector's Raw box has the JSON).
        var text = string.Join(Environment.NewLine, _selectedEntries.Select(e => e.IsStructured ? $"{e.TimeText} {e.Label} {e.Message}".Trim() : e.Raw));
        _ = Views.Controls.DcClipboard.TrySetText(text);
    }

    /// <summary>The log file on screen; null on Verdicts and Events.</summary>
    private SourceState? ActiveFileState => ActiveSource switch
    {
        WatchdogSource => _watchdog,
        GatewaySource => _gateway,
        _ => null,
    };

    /// <summary>The database stream on screen; null on a log file.</summary>
    private StructuredState? ActiveStructuredState => ActiveSource switch
    {
        VerdictsSource => _verdicts,
        EventsSource => _events,
        _ => null,
    };

    private SourceState FileStateNamed(string source) =>
        string.Equals(source, WatchdogSource, StringComparison.Ordinal) ? _watchdog : _gateway;

    /// <summary>
    /// Rewinds the tailer and drains it in bounded batches. Runs on a background thread via <see cref="Task.Run(Action)"/> in
    /// <see cref="InitializeAsync"/> - this is the file I/O the panel contract says never belongs in the constructor.
    /// </summary>
    private static void SeedSource(SourceState state)
    {
        state.Tailer.Reset();
        for (var i = 0; i < MaxSeedIterations; i++)
        {
            var lines = state.Tailer.ReadNewLines();
            if (lines.Count == 0)
            {
                break;
            }

            foreach (var line in lines)
            {
                Append(state, new LogEntry(line, state.StreamName));
            }
        }
    }

    private void AttachHandlers(SourceState state)
    {
        state.Tailer.LinesReceived += (_, e) => OnLinesReceived(state, e.Lines);
        state.Tailer.Truncated += (_, _) => OnTruncated(state);
        state.Tailer.TailFaulted += (_, e) => OnTailFaulted(state, e);
        state.Tailer.TailRecovered += (_, _) => OnTailRecovered(state);
    }

    /// <summary><see cref="LogTailer.TailFaulted"/> fires on the tailer's background loop.</summary>
    private void OnTailFaulted(SourceState state, LogTailFaultedEventArgs e)
    {
        var dispatcher = Application.Current?.Dispatcher;
        dispatcher?.BeginInvoke(() => ApplyTailFault(state, e));
    }

    /// <summary><see cref="LogTailer.TailRecovered"/> fires on the tailer's background loop.</summary>
    private void OnTailRecovered(SourceState state)
    {
        var dispatcher = Application.Current?.Dispatcher;
        dispatcher?.BeginInvoke(() => ApplyTailRecovered(state));
    }

    /// <summary>
    /// The UI-thread half of <see cref="OnTailFaulted"/>: remembers what went wrong for that source and, when it is the one on screen, says so.
    /// Before this the tail simply stopped and the list looked like a quiet gateway.
    /// </summary>
    private void ApplyTailFault(SourceState state, LogTailFaultedEventArgs e)
    {
        var reason = e.Exception.Message.ReplaceLineEndings(" ").Trim();
        state.FaultText =
            $"The tail of {Path.GetFileName(state.FilePath)} hit an error ({e.Exception.GetType().Name}: {reason}). " +
            $"New lines are paused; it retries in {Math.Max(1, (int)Math.Ceiling(e.RetryIn.TotalSeconds))} s and resumes by itself.";
        ShowFaultOf(state);
    }

    private void ApplyTailRecovered(SourceState state)
    {
        state.FaultText = null;
        ShowFaultOf(state);
    }

    /// <summary>Shows the fault banner for <paramref name="state"/> when it is the source on screen; another source's fault is shown when it is switched to.</summary>
    private void ShowFaultOf(SourceState state)
    {
        if (!ReferenceEquals(state, ActiveFileState))
        {
            return;
        }

        TailFaultText = state.FaultText ?? string.Empty;
        ShowTailFault = state.FaultText is not null;
    }

    /// <summary>
    /// Test seam: reports a tailer fault for <paramref name="source"/> ("Gateway" or "Watchdog") as if its loop had just
    /// raised <see cref="LogTailer.TailFaulted"/>, on the calling thread.
    /// </summary>
    internal void AcceptTailFault(string source, LogTailFaultedEventArgs e) =>
        ApplyTailFault(FileStateNamed(source), e);

    /// <summary>Test seam: the matching <see cref="LogTailer.TailRecovered"/>.</summary>
    internal void AcceptTailRecovered(string source) =>
        ApplyTailRecovered(FileStateNamed(source));

    /// <summary><see cref="LogTailer.LinesReceived"/> fires on a background poll thread.</summary>
    private void OnLinesReceived(SourceState state, IReadOnlyList<LogLine> lines)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        dispatcher.BeginInvoke(() => ApplyLines(state, lines));
    }

    /// <summary>
    /// The UI-thread half of <see cref="OnLinesReceived"/>: files a batch into its source's buffer and, when that source is on screen, into
    /// <see cref="DisplayedLines"/> - folding a line into the row above it when it repeats it. Separate from the dispatcher hop so a test can
    /// drive it without an <see cref="Application"/>.
    /// </summary>
    private void ApplyLines(SourceState state, IReadOnlyList<LogLine> lines)
    {
        // Nobody is looking: keep the line (the buffer is the record, and OnActivated
        // re-projects from it) but do none of the UI work. IsActive is read here, when the
        // queued call runs, not when the batch was received.
        if (!IsActive)
        {
            foreach (var line in lines)
            {
                Append(state, new LogEntry(line, state.StreamName));
            }

            // Only the source on screen has a projection to fall behind; the other one is
            // rebuilt in full when the operator switches to it.
            if (ReferenceEquals(state, ActiveFileState))
            {
                _projectionStale = true;
            }

            return;
        }

        var isActiveSource = ReferenceEquals(state, ActiveFileState);
        var componentsChanged = false;
        var shown = isActiveSource ? new List<LogEntry>(lines.Count) : null;
        var last = isActiveSource && DisplayedLines.Count > 0 ? DisplayedLines[^1] : null;

        foreach (var line in lines)
        {
            var before = state.Components.Count;
            var hadComponentless = state.HasComponentless;
            var entry = new LogEntry(line, state.StreamName);
            Append(state, entry);
            componentsChanged |= state.Components.Count != before || state.HasComponentless != hadComponentless;

            if (shown is null || !Passes(entry))
            {
                continue;
            }

            _matching++;
            if (last is not null && last.Repeats(entry))
            {
                last.Absorb(entry);
                continue;
            }

            last = entry.Fork();
            shown.Add(last);
        }

        if (shown is null)
        {
            return;
        }

        if (componentsChanged)
        {
            RebuildComponentFilters();
        }

        // Append rather than rebuild: a full rebuild would reset the scroll offset and the
        // selection on every poll, which is exactly what a paused tail must not do while the
        // operator is reading back through it. When the batch overflows the cap the collection
        // drops the excess in one operation - a burst of 2,000 lines on a full list used to be
        // 2,000 RemoveAt(0) shifts and notifications inside this one callback.
        DisplayedLines.AppendCapped(shown, MaxBufferedLines);
        _matching = Math.Min(_matching, state.Buffer.Count);
        UpdateStatusText();
    }

    /// <summary>
    /// Test seam: files <paramref name="lines"/> as if <paramref name="source"/>'s tailer had just
    /// delivered them, on the calling thread. "Gateway" or "Watchdog".
    /// </summary>
    internal void AcceptLines(string source, IReadOnlyList<LogLine> lines) =>
        ApplyLines(FileStateNamed(source), lines);

    /// <summary>The active log file's buffered line count, for tests; 0 on a database stream.</summary>
    internal int BufferedCount => ActiveFileState?.Buffer.Count ?? 0;

    /// <summary><see cref="LogTailer.Truncated"/> fires on a background poll thread.</summary>
    private void OnTruncated(SourceState state)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        dispatcher.BeginInvoke(() =>
        {
            ClearBuffer(state);

            if (ReferenceEquals(state, ActiveFileState))
            {
                if (IsActive)
                {
                    RebuildComponentFilters();
                    ApplyFilters();
                }
                else
                {
                    // The displayed list still holds the old file's lines; OnActivated
                    // re-projects the (now empty, then refilling) buffer.
                    _projectionStale = true;
                }

                ShowRotationNotice = true;
                RotationNoticeText = $"{Path.GetFileName(state.FilePath)} rotated or was truncated - the buffer was cleared and is replaying from the top of the new file.";
            }
        });
    }

    /// <summary>
    /// Files one entry. The buffer is a bounded queue, so once it holds <see cref="MaxBufferedLines"/> the add itself evicts the oldest line
    /// in O(1): there is no trim step, and no array shift per line (eval #24).
    /// </summary>
    private static void Append(SourceState state, LogEntry entry)
    {
        state.Buffer.Enqueue(entry);
        if (state.Buffer.Count > MaxBufferedLines)
        {
            _ = state.Buffer.Dequeue();
        }

        if (entry.HasComponent)
        {
            _ = state.Components.Add(entry.ComponentText);
        }
        else
        {
            state.HasComponentless = true;
        }
    }

    /// <summary>
    /// Keeps the checkbox list in sync with what the active log file's buffer actually contains, adding newly observed components and
    /// dropping ones no longer present (e.g. right after a truncation clears the buffer) while preserving the enabled state of filters that
    /// survive. A database stream has no checklist.
    /// </summary>
    private void RebuildComponentFilters()
    {
        var state = ActiveFileState;
        var names = new List<string>();
        if (state is not null)
        {
            names.AddRange(state.Components.OrderBy(c => c, StringComparer.OrdinalIgnoreCase));
            if (state.HasComponentless)
            {
                names.Add(NoComponentLabel);
            }
        }

        for (var i = ComponentFilters.Count - 1; i >= 0; i--)
        {
            if (!names.Contains(ComponentFilters[i].Name, StringComparer.Ordinal))
            {
                ComponentFilters[i].PropertyChanged -= OnComponentFilterChanged;
                ComponentFilters.RemoveAt(i);
            }
        }

        var existing = new HashSet<string>(ComponentFilters.Select(f => f.Name), StringComparer.Ordinal);
        foreach (var name in names)
        {
            if (existing.Contains(name))
            {
                continue;
            }

            var filter = new ComponentFilterOption(name);
            filter.PropertyChanged += OnComponentFilterChanged;
            ComponentFilters.Add(filter);
        }
    }

    private void OnComponentFilterChanged(object? sender, PropertyChangedEventArgs e) => Reproject();

    /// <summary>
    /// The one filter predicate, shared by the full rebuild and the append path so a line can never be shown by one and hidden by the other:
    /// the component checklist, the connector scope, the preset, the severity floor, the action and event pickers and the search text.
    /// </summary>
    private bool Passes(LogEntry entry)
    {
        if (ComponentFilters.Count > 0 && !ComponentFilters.All(f => f.IsEnabled))
        {
            var name = entry.HasComponent ? entry.ComponentText : NoComponentLabel;
            var match = ComponentFilters.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.Ordinal));

            // A component seen for the first time has no checkbox yet; show it, and the
            // rebuild that follows adds it enabled.
            if (match is not null && !match.IsEnabled)
            {
                return false;
            }
        }

        if (!Services.ConnectorScope.Allows(entry.Connector))
        {
            return false;
        }

        // The Mac hides a row with nothing left to read under every preset but "all".
        if (SelectedPreset != LogPresets.All && entry.IsBlank)
        {
            return false;
        }

        if (!LogPresets.Matches(SelectedPreset, entry))
        {
            return false;
        }

        if (LogPresets.FloorOf(SelectedSeverity) is { } floor && entry.Severity < floor)
        {
            return false;
        }

        if (!LogPresets.MatchesAction(SelectedAction, entry) || !LogPresets.MatchesEvent(SelectedEvent, entry))
        {
            return false;
        }

        var needle = FilterText.Trim();
        return needle.Length == 0
            || entry.Raw.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || entry.Message.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Rebuilds <see cref="DisplayedLines"/> from the active source's rows, folding each run of identical adjacent rows into one. Every full
    /// projection - a filter change, a source switch, the activation catch-up - leaves the list mirroring the source, so it also clears the
    /// stale mark. One reset notification, not a clear and a row at a time.
    /// </summary>
    private void ApplyFilters()
    {
        _projectionStale = false;

        var source = ActiveFileState is { } file ? (IReadOnlyCollection<LogEntry>)file.Buffer : ActiveStructuredState?.Entries ?? (IReadOnlyCollection<LogEntry>)Array.Empty<LogEntry>();

        var rows = new List<LogEntry>();
        LogEntry? last = null;
        var matching = 0;
        foreach (var entry in source)
        {
            if (!Passes(entry))
            {
                continue;
            }

            matching++;
            if (last is not null && last.Repeats(entry))
            {
                last.Absorb(entry);
                continue;
            }

            last = entry.Fork();
            rows.Add(last);
        }

        _matching = matching;
        DisplayedLines.ReplaceAll(rows);
        UpdateStatusText();
    }

    private void UpdateStatusText()
    {
        var file = ActiveFileState;
        var structured = ActiveStructuredState;
        var total = file?.Buffer.Count ?? structured?.Entries.Count ?? 0;
        var shown = DisplayedLines.Count;

        StatusPath = file?.FilePath ?? Services.Paths.AuditDatabasePath;
        StatusLineCount = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{shown:N0} shown · {Math.Min(_matching, total):N0} matching · {total:N0} total");
        LiveStateText = AutoScroll ? "live" : "paused";

        IsEmpty = shown == 0;
        if (!IsEmpty)
        {
            EmptyTitle = string.Empty;
            EmptyDetail = string.Empty;
            return;
        }

        if (structured is not null)
        {
            EmptyTitle = "No log lines";
            EmptyDetail = IsLoading ? "Reading audit.db…"
                : structured.Missing ? "audit.db does not exist yet. It is created the first time the gateway records an event."
                : !structured.Loaded ? "The stream has not been read yet."
                : structured.Entries.Count == 0 ? $"No data in audit.db for the {structured.Name} stream yet."
                : "Nothing matches the current filters.";
            return;
        }

        var path = file!.FilePath;
        if (file.Buffer.Count > 0)
        {
            EmptyTitle = "No log lines";
            EmptyDetail = "Nothing matches the current filters. Choose preset \"all\", Any severity and \"all\" actions and events, or clear the search, to see the buffered lines again.";
        }
        else if (!File.Exists(path))
        {
            EmptyTitle = $"{Path.GetFileName(path)} does not exist yet";
            EmptyDetail = "It is created the first time the gateway/sidecar writes to it. This is not an error.";
        }
        else
        {
            EmptyTitle = "No lines yet";
            EmptyDetail = $"{path} exists but is currently empty.";
        }
    }

    // ---- Verdicts / Events: read from audit.db, only while the panel is on screen ----

    /// <summary>
    /// Reads the database stream on screen, if there is one and the panel is active: starts the poll timer and does a first read. Called
    /// when the panel is activated, when a stream is switched to, and when the first seed finishes.
    /// </summary>
    private void BeginStructuredIfShown()
    {
        if (!IsActive || ActiveStructuredState is null)
        {
            StopStructured();
            return;
        }

        StartPolling();
        _ = LoadStructuredAsync();
    }

    private void StartPolling()
    {
        // No dispatcher (a unit test's thread): nothing to tick on; the tests drive LoadStructuredAsync themselves.
        if (_pollTimer is not null || Application.Current?.Dispatcher is not { } dispatcher)
        {
            return;
        }

        _pollTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = StructuredPollInterval };
        _pollTimer.Tick += (_, _) =>
        {
            if (IsActive && IsStructuredSource && !IsLoading)
            {
                _ = LoadStructuredAsync();
            }
        };
        _pollTimer.Start();
    }

    /// <summary>Stops polling and interrupts a read in flight: nothing reads the database while the panel is away.</summary>
    private void StopStructured()
    {
        _pollTimer?.Stop();
        _pollTimer = null;

        var cts = Interlocked.Exchange(ref _loadCts, null);
        if (cts is not null)
        {
            cts.Cancel();
            cts.Dispose();
        }

        IsLoading = false;
    }

    /// <summary>
    /// Reads the database stream on screen and projects it. A read that is already running is cancelled (its statement is interrupted) in
    /// favour of this one; a read that finishes after the operator moved on is dropped. A failure keeps the rows already shown and says
    /// why in the banner. The same rows as last time change nothing, so a poll does not disturb the selection or the scroll position.
    /// </summary>
    internal async Task LoadStructuredAsync()
    {
        var state = ActiveStructuredState;
        if (state is null || !IsActive)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _loadCts, cts);
        if (previous is not null)
        {
            previous.Cancel();
            previous.Dispose();
        }

        var token = cts.Token;
        var includeTelemetry = IncludeTelemetry;
        IsLoading = !state.Loaded;
        if (IsLoading)
        {
            UpdateStatusText();
        }

        EventStreamResult? result = null;
        string? failure = null;
        try
        {
            result = await StreamReader.ReadAsync(state.Kind, includeTelemetry, cancellationToken: token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
#pragma warning disable CA1031 // A stream that cannot be read is a banner, not a crash: every failure lands in the same message.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            failure = $"Could not read the {state.Name} stream from audit.db ({ex.GetType().Name}: {ex.Message.ReplaceLineEndings(" ").Trim()}). Showing what was last read; Reload tries again.";
        }

        // Moved on while the read ran (another stream, another read, or off the screen): this answer is stale.
        if (token.IsCancellationRequested || !ReferenceEquals(state, ActiveStructuredState) || includeTelemetry != IncludeTelemetry || !IsActive)
        {
            return;
        }

        IsLoading = false;
        if (failure is not null)
        {
            StreamNoticeText = failure;
            ShowStreamNotice = true;
            state.Loaded = true;
            UpdateStatusText();
            return;
        }

        switch (result!.Status)
        {
            case EventStreamStatus.LegacySchema:
                StreamNoticeText = "This audit.db predates the canonical event schema (no bucket / event_name columns), so the Verdicts and Events streams are not available. The gateway and watchdog logs still are.";
                ShowStreamNotice = true;
                state.Missing = false;
                break;
            case EventStreamStatus.NoDatabase:
                ShowStreamNotice = false;
                state.Missing = true;
                break;
            default:
                ShowStreamNotice = false;
                state.Missing = false;
                break;
        }

        state.Loaded = true;

        // Oldest first, like a log; the reader returns newest first.
        var ids = result.Rows.Select(r => r.Id).ToArray();
        if (ids.AsSpan().SequenceEqual(state.LastIds))
        {
            UpdateStatusText();
            return;
        }

        state.LastIds = ids;
        var entries = new List<LogEntry>(result.Rows.Count);
        for (var i = result.Rows.Count - 1; i >= 0; i--)
        {
            entries.Add(new LogEntry(result.Rows[i], result.Rows.Count - 1 - i, state.Name.ToLowerInvariant()));
        }

        state.Entries = entries;
        ApplyFilters();
    }

    /// <summary>Everything the panel owns for one log file: its tailer, path and buffer.</summary>
    private sealed class SourceState
    {
        public SourceState(string name, LogTailer tailer, string filePath)
        {
            Name = name;
            Tailer = tailer;
            FilePath = filePath;
            StreamName = name.ToLowerInvariant();
        }

        public string Name { get; }

        /// <summary>The label of a line with no <c>[component]</c>: "gateway" / "watchdog".</summary>
        public string StreamName { get; }

        public LogTailer Tailer { get; }

        public string FilePath { get; }

        /// <summary>The newest <see cref="MaxBufferedLines"/> lines, oldest first; the oldest drops off the front as a new one arrives.</summary>
        public Queue<LogEntry> Buffer { get; } = new();

        public HashSet<string> Components { get; } = new(StringComparer.Ordinal);

        public bool HasComponentless { get; set; }

        /// <summary>What the banner says while this source's tail is faulted; null when it is healthy.</summary>
        public string? FaultText { get; set; }
    }

    /// <summary>One database stream: what was last read from it.</summary>
    private sealed class StructuredState
    {
        public StructuredState(string name, EventStreamKind kind)
        {
            Name = name;
            Kind = kind;
        }

        public string Name { get; }

        public EventStreamKind Kind { get; }

        /// <summary>The rows of the last read, oldest first.</summary>
        public List<LogEntry> Entries { get; set; } = new();

        /// <summary>The ids of the last read, newest first: what a poll compares with to see whether anything changed.</summary>
        public string[] LastIds { get; set; } = Array.Empty<string>();

        /// <summary>True once a read has come back (even with no rows).</summary>
        public bool Loaded { get; set; }

        /// <summary>True when the database does not exist yet.</summary>
        public bool Missing { get; set; }

        /// <summary>Forgets the last read, so the next one is shown whatever it holds.</summary>
        public void Reset()
        {
            Entries = new List<LogEntry>();
            LastIds = Array.Empty<string>();
            Loaded = false;
            Missing = false;
        }
    }
}

/// <summary>One component checkbox above the list.</summary>
public sealed partial class ComponentFilterOption : ObservableObject
{
    [ObservableProperty]
    private bool _isEnabled = true;

    public ComponentFilterOption(string name)
    {
        Name = name;
    }

    public string Name { get; }

    /// <summary>The component name, which is what a screen reader should say for the checklist item.</summary>
    public override string ToString() => Name;
}
