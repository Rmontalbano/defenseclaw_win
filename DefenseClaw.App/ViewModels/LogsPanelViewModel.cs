using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.Core.Logs;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// Live tail of <c>gateway.log</c> / <c>watchdog.log</c>.
/// <para>
/// <b>The 0.8.7 sidecar writes bare lines.</b> There is no timestamp and no level on almost
/// every line - just <c>[component] message</c> (plus a box-drawing banner at startup), so
/// <see cref="LogLine.Component"/> is the axis worth filtering on, not
/// <see cref="LogLine.Level"/>, which is nearly always <see cref="LogLevel.Unknown"/>.
/// </para>
/// <para>
/// <b>Lifecycle.</b> <see cref="AppServices.GatewayLog"/> and
/// <see cref="AppServices.WatchdogLog"/> are constructed with <c>StartAtEnd: true</c> and
/// deliberately not started - this panel owns them. On first load it rewinds each tailer
/// with <see cref="LogTailer.Reset"/> and drains <see cref="LogTailer.ReadNewLines"/> to
/// seed history, then starts live watching on both so switching the source toggle is
/// instant. Both <see cref="LogTailer.LinesReceived"/> and <see cref="LogTailer.Truncated"/>
/// fire on a background thread, so every handler marshals through
/// <see cref="Application.Current"/>'s dispatcher.
/// </para>
/// </summary>
public sealed partial class LogsPanelViewModel : PanelViewModelBase
{
    private const int MaxBufferedLines = 5000;
    private const int MaxSeedIterations = 50;
    private const string NoComponentLabel = "(no component)";

    private readonly SourceState _gateway;
    private readonly SourceState _watchdog;
    private List<LogEntry> _selectedEntries = new();

    [ObservableProperty]
    private string _activeSource = "Gateway";

    [ObservableProperty]
    private string _filterText = string.Empty;

    [ObservableProperty]
    private bool _autoScroll = true;

    [ObservableProperty]
    private string _statusPath = string.Empty;

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

    public LogsPanelViewModel(AppServices services)
        : base(services)
    {
        _gateway = new SourceState("Gateway", Services.GatewayLog, Services.Paths.GatewayLogPath);
        _watchdog = new SourceState("Watchdog", Services.WatchdogLog, Services.Paths.WatchdogLogPath);
    }

    public override string Title => "Logs";

    public override string Description => "Tail of gateway.log and watchdog.log, filtered by component.";

    /// <summary>Components observed in the active source's buffer, plus "(no component)".</summary>
    public ObservableCollection<ComponentFilterOption> ComponentFilters { get; } = new();

    /// <summary>The filtered view bound to the list - what survives the component and text filters.</summary>
    public ObservableCollection<LogEntry> DisplayedLines { get; } = new();

    public override async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await Task.Run(() => SeedSource(_gateway), cancellationToken).ConfigureAwait(true);
        await Task.Run(() => SeedSource(_watchdog), cancellationToken).ConfigureAwait(true);

        AttachHandlers(_gateway);
        AttachHandlers(_watchdog);

        // Both tailers stay live regardless of which source is on screen, so flipping the
        // toggle never has to wait on a fresh watcher to spin up.
        _gateway.Tailer.StartWatching();
        _watchdog.Tailer.StartWatching();

        RebuildComponentFilters();
        ApplyFilters();
    }

    /// <summary>Called by the code-behind after the ListView's selection changes.</summary>
    public void UpdateSelection(IEnumerable<LogEntry> selected) => _selectedEntries = selected.ToList();

    partial void OnActiveSourceChanged(string value)
    {
        ShowRotationNotice = false;
        RebuildComponentFilters();
        ApplyFilters();
    }

    partial void OnFilterTextChanged(string value) => ApplyFilters();

    partial void OnAutoScrollChanged(bool value) => LiveStateText = value ? "live" : "paused";

    [RelayCommand]
    private void SelectSource(string? source)
    {
        if (!string.IsNullOrEmpty(source))
        {
            ActiveSource = source;
        }
    }

    [RelayCommand]
    private void Clear()
    {
        var state = ActiveState;
        state.Buffer.Clear();
        state.Components.Clear();
        state.HasComponentless = false;
        RebuildComponentFilters();
        ApplyFilters();
    }

    [RelayCommand]
    private void CopySelection()
    {
        if (_selectedEntries.Count == 0)
        {
            return;
        }

        var text = string.Join(Environment.NewLine, _selectedEntries.Select(e => e.Raw));
        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Another process owns the clipboard; nothing useful to do about it.
        }
    }

    private SourceState ActiveState => string.Equals(ActiveSource, "Watchdog", StringComparison.Ordinal)
        ? _watchdog
        : _gateway;

    /// <summary>
    /// Rewinds the tailer and drains it in bounded batches. Runs on a background thread via
    /// <see cref="Task.Run(Action)"/> in <see cref="InitializeAsync"/> - this is the file I/O
    /// the panel contract says never belongs in the constructor.
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
                Append(state, line);
            }
        }
    }

    private void AttachHandlers(SourceState state)
    {
        state.Tailer.LinesReceived += (_, e) => OnLinesReceived(state, e.Lines);
        state.Tailer.Truncated += (_, _) => OnTruncated(state);
    }

    /// <summary><see cref="LogTailer.LinesReceived"/> fires on a background poll thread.</summary>
    private void OnLinesReceived(SourceState state, IReadOnlyList<LogLine> lines)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        dispatcher.BeginInvoke(() =>
        {
            var isActive = ReferenceEquals(state, ActiveState);
            var componentsChanged = false;

            foreach (var line in lines)
            {
                var before = state.Components.Count;
                var hadComponentless = state.HasComponentless;
                Append(state, line);
                componentsChanged |= state.Components.Count != before || state.HasComponentless != hadComponentless;

                // Append rather than rebuild: a full rebuild would reset the scroll offset
                // and the selection on every poll, which is exactly what a paused tail must
                // not do while the operator is reading back through it.
                if (isActive && Passes(line))
                {
                    DisplayedLines.Add(new LogEntry(line));
                }
            }

            if (!isActive)
            {
                return;
            }

            if (componentsChanged)
            {
                RebuildComponentFilters();
            }

            TrimDisplayed();
            UpdateStatusText(state);
        });
    }

    /// <summary>Keeps the rendered list bounded the same way the buffer is.</summary>
    private void TrimDisplayed()
    {
        while (DisplayedLines.Count > MaxBufferedLines)
        {
            DisplayedLines.RemoveAt(0);
        }
    }

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
            state.Buffer.Clear();
            state.Components.Clear();
            state.HasComponentless = false;

            if (ReferenceEquals(state, ActiveState))
            {
                RebuildComponentFilters();
                ApplyFilters();
                ShowRotationNotice = true;
                RotationNoticeText = $"{Path.GetFileName(state.FilePath)} rotated or was truncated - the buffer was cleared and is replaying from the top of the new file.";
            }
        });
    }

    private static void Append(SourceState state, LogLine line)
    {
        state.Buffer.Add(line);
        if (line.Component is { Length: > 0 } component)
        {
            state.Components.Add(component);
        }
        else
        {
            state.HasComponentless = true;
        }

        var excess = state.Buffer.Count - MaxBufferedLines;
        if (excess > 0)
        {
            state.Buffer.RemoveRange(0, excess);
        }
    }

    /// <summary>
    /// Keeps the checkbox list in sync with what the active source's buffer actually
    /// contains, adding newly observed components and dropping ones no longer present
    /// (e.g. right after a truncation clears the buffer) while preserving the enabled state
    /// of filters that survive.
    /// </summary>
    private void RebuildComponentFilters()
    {
        var state = ActiveState;
        var names = new List<string>(state.Components.OrderBy(c => c, StringComparer.OrdinalIgnoreCase));
        if (state.HasComponentless)
        {
            names.Add(NoComponentLabel);
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

    private void OnComponentFilterChanged(object? sender, PropertyChangedEventArgs e) => ApplyFilters();

    /// <summary>
    /// The one filter predicate, shared by the full rebuild and the append path so a line
    /// can never be shown by one and hidden by the other.
    /// </summary>
    private bool Passes(LogLine line)
    {
        if (ComponentFilters.Count > 0 && !ComponentFilters.All(f => f.IsEnabled))
        {
            var name = line.Component is { Length: > 0 } component ? component : NoComponentLabel;
            var match = ComponentFilters.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.Ordinal));

            // A component seen for the first time has no checkbox yet; show it, and the
            // rebuild that follows adds it enabled.
            if (match is not null && !match.IsEnabled)
            {
                return false;
            }
        }

        var needle = FilterText.Trim();
        return needle.Length == 0 || line.Raw.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Rebuilds <see cref="DisplayedLines"/> from the active source's buffer.</summary>
    private void ApplyFilters()
    {
        var state = ActiveState;

        DisplayedLines.Clear();
        foreach (var line in state.Buffer)
        {
            if (Passes(line))
            {
                DisplayedLines.Add(new LogEntry(line));
            }
        }

        UpdateStatusText(state);
    }

    private void UpdateStatusText(SourceState state)
    {
        StatusPath = state.FilePath;
        StatusLineCount = $"{DisplayedLines.Count} of {state.Buffer.Count} line(s) buffered";
        LiveStateText = AutoScroll ? "live" : "paused";

        var exists = File.Exists(state.FilePath);
        IsEmpty = DisplayedLines.Count == 0;

        if (!IsEmpty)
        {
            EmptyTitle = string.Empty;
            EmptyDetail = string.Empty;
            return;
        }

        if (!exists)
        {
            EmptyTitle = $"{Path.GetFileName(state.FilePath)} does not exist yet";
            EmptyDetail = "It is created the first time the gateway/sidecar writes to it. This is not an error.";
        }
        else if (state.Buffer.Count == 0)
        {
            EmptyTitle = "No lines yet";
            EmptyDetail = $"{state.FilePath} exists but is currently empty.";
        }
        else
        {
            EmptyTitle = "No lines match the current filters";
            EmptyDetail = "Clear the component filter or the free-text search to see buffered lines again.";
        }
    }

    /// <summary>Everything the panel owns for one log file: its tailer, path and buffer.</summary>
    private sealed class SourceState
    {
        public SourceState(string name, LogTailer tailer, string filePath)
        {
            Name = name;
            Tailer = tailer;
            FilePath = filePath;
        }

        public string Name { get; }

        public LogTailer Tailer { get; }

        public string FilePath { get; }

        public List<LogLine> Buffer { get; } = new();

        public HashSet<string> Components { get; } = new(StringComparer.Ordinal);

        public bool HasComponentless { get; set; }
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
}

/// <summary>One rendered log line: a dimmed component prefix plus the message.</summary>
public sealed class LogEntry
{
    public LogEntry(LogLine line)
    {
        ArgumentNullException.ThrowIfNull(line);

        Sequence = line.Sequence;
        Raw = line.Raw;
        Message = line.Message.Length > 0 ? line.Message : line.Raw;
        HasComponent = line.Component is { Length: > 0 };
        ComponentText = HasComponent ? line.Component! : "-";
    }

    public long Sequence { get; }

    public string Raw { get; }

    public string Message { get; }

    public string ComponentText { get; }

    public bool HasComponent { get; }
}
