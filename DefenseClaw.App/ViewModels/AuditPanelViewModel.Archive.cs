using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DefenseClaw.Core.Audit;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// The Audit panel's second source (CUST-299): <b>Live | Archive</b>. The archive is the <c>audit.db</c> of an earlier DefenseClaw
/// (0.8.10, schema 29), copied outside the live data folder before an upgrade purged its history, and chosen in Settings
/// (<c>archive.path</c>). The switch exists only while one is set.
/// <para>
/// <b>Read-only, twice over.</b> The file is opened <c>mode=ro&amp;immutable=1</c> (<see cref="AuditArchive"/>), so nothing is ever
/// written or created beside it; and <see cref="IsReadOnlySource"/> is what every write action keys off. The Audit panel itself has
/// no write action (it never did: its row menu copies and filters), so today that flag has nothing to disable here; it is the
/// contract for any panel that reads the archive, and Export - which writes a new file the operator names, not the archive - stays on.
/// </para>
/// <para>
/// <b>Never a crash.</b> The archive is checked each time it is opened (<see cref="AuditArchive.InspectAsync"/>): a file that was
/// moved, is not SQLite, is damaged or lacks the audit tables is an error state with the reason, in the banner and in the empty
/// list, and Live is one click away.
/// </para>
/// </summary>
public sealed partial class AuditPanelViewModel
{
    /// <summary>The <see cref="SourceKey"/> of the live <c>audit.db</c>.</summary>
    public const string SourceLive = "live";

    /// <summary>The <see cref="SourceKey"/> of the archived database.</summary>
    public const string SourceArchive = "archive";

    private AuditReader? _archiveReader;

    /// <summary>Bumped by every switch to, or reload of, the archive; an inspection that finishes for an older one is dropped.</summary>
    private int _archiveVersion;

    /// <summary>Which source the list shows: <see cref="SourceLive"/> or <see cref="SourceArchive"/> (the Live | Archive switch).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsArchive))]
    [NotifyPropertyChangedFor(nameof(IsReadOnlySource))]
    [NotifyPropertyChangedFor(nameof(ShowSourceSwitch))]
    [NotifyPropertyChangedFor(nameof(ShowArchiveBanner))]
    [NotifyPropertyChangedFor(nameof(ShowArchiveProblem))]
    private string _sourceKey = SourceLive;

    /// <summary>The archive path in Settings, or null when none is set. Re-read each time the panel is shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasArchive))]
    [NotifyPropertyChangedFor(nameof(ShowSourceSwitch))]
    private string? _archivePath;

    /// <summary>The newest event the archive holds; null before it has been read, or when it has none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ArchiveBanner))]
    private DateTimeOffset? _archiveNewest;

    /// <summary>Why the archive cannot be shown, or empty.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasArchiveProblem))]
    [NotifyPropertyChangedFor(nameof(ShowArchiveBanner))]
    [NotifyPropertyChangedFor(nameof(ShowArchiveProblem))]
    private string _archiveProblem = string.Empty;

    /// <summary>True while the list shows the archive.</summary>
    public bool IsArchive => string.Equals(SourceKey, SourceArchive, StringComparison.Ordinal);

    /// <summary>The source on screen is the archive: every write action is off. See the type documentation.</summary>
    public bool IsReadOnlySource => IsArchive;

    /// <summary>An archive path is set in Settings.</summary>
    public bool HasArchive => ArchivePath is not null;

    /// <summary>The Live | Archive switch shows while an archive is set, and while the archive is on screen (so the way back never disappears).</summary>
    public bool ShowSourceSwitch => HasArchive || IsArchive;

    public bool HasArchiveProblem => ArchiveProblem.Length > 0;

    /// <summary>The "Archived history, up to ..." banner: on while the archive is on screen and readable.</summary>
    public bool ShowArchiveBanner => IsArchive && !HasArchiveProblem;

    /// <summary>The error banner: on while the archive is on screen and could not be read.</summary>
    public bool ShowArchiveProblem => IsArchive && HasArchiveProblem;

    /// <summary>"Archived history, up to Mar 2, 2026 8:03 AM" (the newest event, in the operator's time zone).</summary>
    public string ArchiveBanner => ArchiveNewest is { } newest
        ? $"Archived history, up to {newest.ToLocalTime().ToString("f", CultureInfo.CurrentCulture)}"
        : "Archived history (the archive has no events)";

    /// <summary>The time range a source opens on and Reset filters returns to: the last 24 hours live, all time for the archive (its newest event is months old).</summary>
    private TimeRangeOption DefaultRange => IsArchive ? TimeRangeOption.AllTime : TimeRangeOption.Day;

    /// <summary>The reader of the source on screen; null for an archive that failed its check (and for the archive before it has been checked).</summary>
    private AuditReader? ActiveReader => IsArchive ? _archiveReader : Services.Audit;

    /// <summary>Takes the Settings path: shows or hides the switch, and follows a change to the archive that is on screen.</summary>
    private void RefreshArchiveSetting()
    {
        var path = Services.Settings.Current.Archive.Path;
        var changed = !string.Equals(path, ArchivePath, StringComparison.OrdinalIgnoreCase);
        ArchivePath = path;

        if (!changed || !IsArchive)
        {
            return;
        }

        if (path is null)
        {
            SourceKey = SourceLive;
        }
        else
        {
            // A different file: nothing read from the old one (rows, filter lists) applies.
            ResetForSource();
            LastLoad = EnterArchiveAsync();
        }
    }

    protected override void OnActivated()
    {
        RefreshArchiveSetting();
        StartLive();
    }

    partial void OnSourceKeyChanged(string value)
    {
        ResetForSource();

        if (IsArchive)
        {
            LastLoad = EnterArchiveAsync();
            return;
        }

        _archiveReader = null;
        ArchiveProblem = string.Empty;
        ArchiveNewest = null;
        if (!Services.Audit.Exists)
        {
            ShowNoLiveDatabase();
        }
        else
        {
            Reload();
        }
    }

    /// <summary>
    /// Puts the list back to a blank page for the other source: cancels what is loading, clears the rows and the
    /// filter lists read from the old database (the next load reads the new one's), and resets every filter without
    /// reloading. The time range is "All time" for the archive - its newest event is months old, so the 24 hours the live view
    /// opens on would be an empty list - and the default for live.
    /// </summary>
    private void ResetForSource()
    {
        _ = StartGeneration();

        var wasPending = _reloadPending;
        _reloadDeferrals++;
        var wasApplyingScope = _applyingScope;
        _applyingScope = true;
        try
        {
            SelectedRow = null;
            Rows.Clear();
            _shown = null;
            _cursor = null;
            HasMore = false;
            IsRowCapReached = false;
            IsLoading = false;
            StatusNote = string.Empty;
            ExportNote = string.Empty;
            HiddenCount = 0;
            ForgetLive();

            // The lists first, the selections after: a combo box bound to a list it just lost the selected item from writes null
            // back to the selection, so a selection set beforehand would not survive the Clear.
            Connectors.Clear();
            Connectors.Add(ConnectorOption.All);
            Buckets.Clear();
            Buckets.Add(AnyBucket);
            Actions.Clear();
            Actions.Add(AnyAction);
            _filterOptionsLoaded = false;

            SelectedConnector = ConnectorOption.All;
            SelectedBucket = AnyBucket;
            SelectedSeverity = SeverityOption.Any;
            SelectedActionOption = AnyAction;
            ActionFilter = string.Empty;
            SearchText = string.Empty;
            ActivePreset = PresetAll;
            RunFilter = string.Empty;
            SelectedRange = DefaultRange;
        }
        finally
        {
            _applyingScope = wasApplyingScope;
            _reloadDeferrals--;
            _reloadPending = wasPending;
        }

        // The chip's connector, if one is chosen, still scopes this source too.
        if (Services.ConnectorScope.Current is not null)
        {
            OnConnectorScopeChanged();
        }
    }

    /// <summary>
    /// Opens the archive: checks the file (<see cref="AuditArchive.InspectAsync"/>, off the UI thread - a path on a dead share can
    /// stall for seconds), then loads its first page. A file that fails the check is the error state, with the reason. Also what
    /// Refresh does while the archive is on screen, so fixing the file and pressing F5 recovers.
    /// </summary>
    private async Task EnterArchiveAsync()
    {
        var version = Interlocked.Increment(ref _archiveVersion);
        _ = StartGeneration();
        _archiveReader = null;
        ArchiveProblem = string.Empty;
        ArchiveNewest = null;
        IsLoading = true;

        var path = ArchivePath;
        var dataDirectory = Services.Paths.DataDirectory;
        AuditArchiveCheck check;
        try
        {
            check = await Task.Run(() => AuditArchive.InspectAsync(path, dataDirectory)).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (version != Volatile.Read(ref _archiveVersion) || !IsArchive)
        {
            // Left the archive, or asked for it again, while the file was being checked.
            return;
        }

        if (!check.IsUsable)
        {
            ShowArchiveError(check.Problem ?? "The archive could not be read.");
            return;
        }

        _archiveReader = AuditArchive.OpenReader(check.FullPath);
        ArchiveNewest = check.Newest;
        await LoadAsync(append: false, CancellationToken.None).ConfigureAwait(true);
    }

    /// <summary>The archive on screen cannot be read: say why in the banner and the empty list, and show no rows.</summary>
    private void ShowArchiveError(string problem)
    {
        _archiveReader = null;
        Rows.Clear();
        _shown = null;
        SelectedRow = null;
        _cursor = null;
        HasMore = false;
        IsRowCapReached = false;
        IsLoading = false;
        ArchiveProblem = problem;
        ArchiveNewest = null;
        StatusNote = string.Empty;
        ResultSummary = string.Empty;
        IsEmpty = true;
        EmptyTitle = "Archive unavailable";
        EmptyDetail = problem;
    }
}
