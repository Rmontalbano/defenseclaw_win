using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.Core.Text;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// The Registries panel's three views (CUST-276): Sources (the master/detail the panel always had), Entries - the cached entries of every source in
/// one table - and Approved - the same table narrowed to what an operator approved. The TUI's <c>RegistriesTab.SOURCES / ENTRIES / APPROVED</c>
/// (<c>tui/panels/registries.py</c>) and the Mac's segmented Sources / Entries / Approved picker.
/// <para>
/// <b>Where the rows come from.</b> Each listed source's <c>registries\&lt;id&gt;\index.json</c>, read the way the selected source's own list is
/// (<see cref="ReadIndexAsync"/>), once per read of the sources; the sources are taken in id order and each keeps its file order, as the TUI's
/// <c>_entry_rows</c> does. A source with nothing cached has no rows; one whose cache cannot be read is named in a warning above the table instead of
/// making the table look complete.
/// </para>
/// <para>
/// <b>Open in Registries.</b> The Skills and MCPs rows a registry promoted send <see cref="RegistryFocus"/> (<see cref="IAcceptsNavigation"/>): the panel
/// switches to Entries and narrows it to the entries of that type and name in every source (the TUI's <c>focus_entry</c>, which keeps a
/// <c>_filter_entry_key</c> until the tab changes), selects the one of the source that promoted the item, and asks the view to scroll to it and focus it.
/// A link that arrives before the first read waits for it. An entry nobody has cached is said so, and the table is left whole. Changing tab, or
/// <see cref="ClearEntryFocusCommand"/>, ends the narrowing.
/// </para>
/// </summary>
public sealed partial class RegistriesPanelViewModel : IAcceptsNavigation
{
    /// <summary>The Sources view: the sources, and the selected one's details and cached entries.</summary>
    public const string SourcesTab = "sources";

    /// <summary>The Entries view: the cached entries of every source.</summary>
    public const string EntriesTab = "entries";

    /// <summary>The Approved view: the entries of every source an operator approved.</summary>
    public const string ApprovedTab = "approved";

    /// <summary>Every cached entry of every listed source, in source-id order (each source in its own file order).</summary>
    private readonly List<RegistryEntryRow> _allEntries = new();

    private int _allEntriesSequence;
    private bool _allEntriesLoaded;
    private RegistryFocus? _pendingFocus;

    /// <summary>Which view is on screen: <see cref="SourcesTab"/>, <see cref="EntriesTab"/> or <see cref="ApprovedTab"/> (two-way with the segmented control).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(IsSourcesTab),
        nameof(IsEntriesTab),
        nameof(IsApprovedTab),
        nameof(ShowSourcesView),
        nameof(ShowEntriesView),
        nameof(EmptyEntriesText),
        nameof(CanChangeSelectedSource))]
    private string _activeTab = SourcesTab;

    /// <summary>The selected source's publisher, fetch time and counts, read with its entries (a dash and no counts until an index has been read).</summary>
    [ObservableProperty]
    private RegistrySourceFacts _sourceFacts = RegistrySourceFacts.None;

    /// <summary>The row selected in the Entries or Approved table.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedTabEntry), nameof(CanReviewTabEntry), nameof(CanChangeSelectedSource))]
    private RegistryEntryRow? _selectedTabEntry;

    /// <summary>
    /// The entry the table is narrowed to (every source's entries of this type and name), set by <see cref="FocusEntry"/> and ended by a change of tab or
    /// <see cref="ClearEntryFocusCommand"/>; null: the table shows its whole tab.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEntryFocus), nameof(EntryFocusText))]
    private RegistryFocus? _entryFocus;

    /// <summary>Why a link to an entry did not land on one (nobody has it cached); null otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFocusNote))]
    private string? _focusNote;

    /// <summary>The sources whose cache could not be read in full (corrupt, too large, cut at the row limit), one line each; null when all could.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEntriesNote))]
    private string? _entriesNote;

    /// <summary>What the Entries or Approved table lists now: its tab's rows, narrowed by <see cref="EntryFocus"/>.</summary>
    public BatchObservableCollection<RegistryEntryRow> TabEntries { get; } = new();

    public bool IsSourcesTab => ActiveTab == SourcesTab;

    public bool IsEntriesTab => ActiveTab == EntriesTab;

    public bool IsApprovedTab => ActiveTab == ApprovedTab;

    /// <summary>The sources master/detail is on screen: that tab, and there are sources.</summary>
    public bool ShowSourcesView => HasSources && IsSourcesTab;

    /// <summary>The Entries or Approved table is on screen: one of those tabs, and there are sources.</summary>
    public bool ShowEntriesView => HasSources && !IsSourcesTab;

    /// <summary>How many entries the Entries tab holds across all sources; null until they have been read (the segment shows no count then).</summary>
    public int? EntriesCount => _allEntriesLoaded ? _allEntries.Count : null;

    /// <summary>How many of them are approved (the Approved tab's count); null until they have been read.</summary>
    public int? ApprovedCount => _allEntriesLoaded ? _allEntries.Count(row => row.Approved) : null;

    public bool HasTabEntries => TabEntries.Count > 0;

    /// <summary>The table has nothing to list: its empty-state sentence is shown in its place.</summary>
    public bool ShowTabEmpty => ShowEntriesView && TabEntries.Count == 0;

    /// <summary>The TUI's empty states: an Approved tab with nothing approved, and an Entries tab with nothing cached.</summary>
    public string EmptyEntriesText => IsApprovedTab
        ? "No entries approved yet. Select an entry on the Entries tab and choose Approve."
        : "Sync a source to populate this view.";

    public bool HasSelectedTabEntry => SelectedTabEntry is not null;

    public bool CanReviewTabEntry => SelectedTabEntry is { CanReview: true, SourceId: { Length: > 0 } } && IsDataTrusted;

    public bool HasEntryFocus => EntryFocus is not null;

    /// <summary>The strip over a narrowed table: <c>Showing skill “pdf-tools” in every source (2 matches)</c>.</summary>
    public string EntryFocusText => EntryFocus is { } focus
        ? $"Showing {DisplayNames.Visible(focus.EntryType)} “{DisplayNames.Visible(focus.Name)}” in every source ({TabEntries.Count.ToString("N0", CultureInfo.CurrentCulture)} {(TabEntries.Count == 1 ? "match" : "matches")})"
        : string.Empty;

    public bool HasFocusNote => !string.IsNullOrEmpty(FocusNote);

    public bool HasEntriesNote => !string.IsNullOrEmpty(EntriesNote);

    /// <summary>
    /// Raised after a link to an entry landed on one (<see cref="FocusEntry"/>): the table's selected row is the entry, and the view scrolls to it and
    /// gives it the keyboard focus. UI thread.
    /// </summary>
    public event EventHandler? FocusEntryRequested;

    /// <summary>
    /// A deep link (<see cref="IAcceptsNavigation"/>): a <see cref="RegistryFocus"/> opens the Entries tab on that entry (see the type's summary). It is
    /// applied now when the entries have been read and no read of them is under way, and otherwise when that read ends, so a link that lands on the very
    /// first visit - or on a visit that has just started a read because the data was old - is answered from the rows the read brings; the operator
    /// choosing another tab in the meantime withdraws it. A payload of another type, or one that names no entry, is ignored.
    /// </summary>
    public void Accept(object payload)
    {
        if (payload is not RegistryFocus focus || string.IsNullOrWhiteSpace(focus.EntryType) || string.IsNullOrWhiteSpace(focus.Name))
        {
            return;
        }

        // Showing the tab ends any narrowing of the one before it; the link then sets its own.
        ActiveTab = EntriesTab;
        _pendingFocus = focus;
        ApplyPendingFocus();
    }

    /// <summary>
    /// Lands a link that was waiting - for the first read of the entries, or for the read that is replacing them - once there is a read to answer
    /// from. Called when a link arrives, when the entries have been read and when a read of the sources ends.
    /// </summary>
    private void ApplyPendingFocus()
    {
        if (_pendingFocus is not { } focus || !_allEntriesLoaded || _refreshing)
        {
            return;
        }

        _pendingFocus = null;
        _ = FocusEntry(focus);
    }

    /// <summary>
    /// Shows the Entries tab narrowed to the entries of <paramref name="focus"/>'s type and name in every source (the TUI's <c>focus_entry</c>), with the
    /// one from the source it names selected - the first match when that source does not list it - and the view asked to bring it into sight.
    /// True when some source has it cached. When none does the tab is shown whole, with <see cref="FocusNote"/> saying so.
    /// </summary>
    internal bool FocusEntry(RegistryFocus focus)
    {
        ArgumentNullException.ThrowIfNull(focus);

        ActiveTab = EntriesTab;
        _pendingFocus = null;

        var wanted = focus with { EntryType = focus.EntryType.Trim() };
        var found = _allEntries.Any(row => Matches(row, wanted));
        EntryFocus = found ? wanted : null;
        FocusNote = found ? null : NotFoundNote(wanted);

        // The link chooses the row: what was selected before (another source's entry of the same name, say) does not outrank the source it names.
        if (found)
        {
            SelectedTabEntry = null;
        }

        RebuildTabEntries();

        if (found && SelectedTabEntry is not null)
        {
            FocusEntryRequested?.Invoke(this, EventArgs.Empty);
        }

        return found;
    }

    private static bool Matches(RegistryEntryRow row, RegistryFocus focus) =>
        string.Equals(row.Type, focus.EntryType, StringComparison.OrdinalIgnoreCase) && string.Equals(row.Name, focus.Name, StringComparison.Ordinal);

    /// <summary>What the panel says when a link finds no cached entry: with a source named, whether that source is still configured decides what to do about it.</summary>
    private string NotFoundNote(RegistryFocus focus)
    {
        var what = $"{DisplayNames.Visible(focus.EntryType)} “{DisplayNames.Visible(focus.Name)}”";
        if (string.IsNullOrWhiteSpace(focus.SourceId))
        {
            return $"No registry source has a cached entry for {what}.";
        }

        var source = DisplayNames.Visible(focus.SourceId.Trim());
        var configured = Sources.Any(candidate => string.Equals(candidate.Id, focus.SourceId.Trim(), StringComparison.Ordinal));
        return configured
            ? $"A rule from registry:{source} allows {what}, but no source has a cached entry for it. " +
              "Sync that source to refresh its cache; the entry may also have left its manifest."
            : $"A rule from registry:{source} allows {what}, but there is no registry source called “{source}” and no source has a cached entry for it. " +
              "The source may have been removed from config.yaml by hand; its rule is still in the allow policy.";
    }

    /// <summary>
    /// Ends the narrowing to one entry: the table shows its whole tab again, with the entry still selected - and, because the list under it just grew,
    /// brought back into sight (the button that was pressed is gone with the strip, so the focus goes to the row).
    /// </summary>
    [RelayCommand]
    private void ClearEntryFocus()
    {
        EntryFocus = null;
        FocusNote = null;
        RebuildTabEntries();

        if (SelectedTabEntry is not null)
        {
            FocusEntryRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Esc ends a narrowing, after the dialogs and forms (the TUI's Esc leaves a focused view).</summary>
    private bool ClearEntryFocusOnEscape()
    {
        if (!IsEntriesTab || (EntryFocus is null && FocusNote is null))
        {
            return false;
        }

        ClearEntryFocus();
        return true;
    }

    /// <summary>The selected row's source, on the Sources tab: the selected source; on the others: the source of the selected entry (the Mac's Sync Selected).</summary>
    private RegistrySourceRow? ActionSource => IsSourcesTab
        ? SelectedSource
        : SelectedTabEntry?.SourceId is { } id ? Sources.FirstOrDefault(source => string.Equals(source.Id, id, StringComparison.Ordinal)) : null;

    partial void OnActiveTabChanged(string value)
    {
        if (value is not (SourcesTab or EntriesTab or ApprovedTab))
        {
            ActiveTab = SourcesTab;
            return;
        }

        // The TUI's set_tab: a tab starts at its top, with no entry in focus, and a link still waiting is the operator's no longer.
        _pendingFocus = null;
        EntryFocus = null;
        FocusNote = null;
        SelectedTabEntry = null;
        RebuildTabEntries();
    }

    /// <summary>Selects the source of the selected entry on the Sources tab (an entry's details are its source's).</summary>
    [RelayCommand]
    private void ShowEntrySource()
    {
        if (SelectedTabEntry?.SourceId is not { } id)
        {
            return;
        }

        var source = Sources.FirstOrDefault(candidate => string.Equals(candidate.Id, id, StringComparison.Ordinal));
        ActiveTab = SourcesTab;
        if (source is not null)
        {
            SelectedSource = source;
        }
    }

    [RelayCommand]
    private void ApproveListedEntry() => ReviewListedEntry(approve: true);

    [RelayCommand]
    private void RejectListedEntry() => ReviewListedEntry(approve: false);

    private void ReviewListedEntry(bool approve)
    {
        if (SelectedTabEntry is not { CanReview: true, SourceId: { Length: > 0 } sourceId } entry || RefuseUntrustedChange())
        {
            return;
        }

        ReviewEntry(approve, sourceId, entry);
    }

    // ---- Reading every source's cache ----------------------------------------------------------------------------------

    /// <summary>
    /// Reads the cached entries of every source in <see cref="Sources"/> (in id order, in parallel, off the UI thread) and shows them on the Entries and
    /// Approved tabs, keeping the selected row and any narrowing; a link that was waiting for the first read is applied at the end. A newer read
    /// supersedes one still running.
    /// </summary>
    internal async Task LoadAllEntriesAsync()
    {
        var sequence = ++_allEntriesSequence;
        var dataDirectory = Services.Paths.DataDirectory;
        var ids = Sources.Select(source => source.Id).Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToList();

        var reads = await Task.WhenAll(ids.Select(id => ReadIndexAsync(dataDirectory, id, stampSource: true))).ConfigureAwait(true);
        if (sequence != _allEntriesSequence)
        {
            return;
        }

        _allEntries.Clear();
        var notes = new List<string>();
        for (var i = 0; i < ids.Count; i++)
        {
            _allEntries.AddRange(reads[i].Rows);
            if (reads[i].IsProblem && reads[i].Message is { } message)
            {
                notes.Add($"{DisplayNames.Visible(ids[i])}: {message}");
            }
        }

        _allEntriesLoaded = true;
        EntriesNote = notes.Count == 0 ? null : string.Join('\n', notes);
        OnPropertyChanged(nameof(EntriesCount));
        OnPropertyChanged(nameof(ApprovedCount));
        RebuildTabEntries();
        ApplyPendingFocus();
    }

    private async Task LoadAllEntriesSafelyAsync()
    {
        try
        {
            await LoadAllEntriesAsync().ConfigureAwait(true);
        }
#pragma warning disable CA1031 // The cross-source tables are a view of files; a failure is reported above them and must not fail the read of the sources.
        catch (Exception ex)
        {
            Trace.TraceError($"Registry entries (all sources) read failed: {ex}");
            EntriesNote = $"Could not read the cached entries: {ex.Message}";
            _allEntriesLoaded = true;
            ApplyPendingFocus();
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// Rebuilds the table from the entries read: the Approved tab keeps the approved ones, a narrowing keeps those of its type and name. The selected
    /// row stays selected when it is still listed (matched by source, type and name, because a read makes new row objects); a narrowing selects the
    /// entry of the source it names, else the first. One reset notification for the whole list.
    /// </summary>
    private void RebuildTabEntries()
    {
        var previous = SelectedTabEntry?.Key;
        IEnumerable<RegistryEntryRow> rows = _allEntries;
        if (IsApprovedTab)
        {
            rows = rows.Where(row => row.Approved);
        }

        if (EntryFocus is { } focus)
        {
            rows = rows.Where(row => Matches(row, focus));
        }

        var list = rows.ToList();
        TabEntries.ReplaceAll(list);

        var selected = previous is null ? null : list.FirstOrDefault(row => row.Key == previous);
        if (selected is null && EntryFocus is { } narrowed)
        {
            selected = list.FirstOrDefault(row => string.Equals(row.SourceId, narrowed.SourceId, StringComparison.Ordinal)) ?? list.FirstOrDefault();
        }

        SelectedTabEntry = selected;
        OnPropertyChanged(nameof(HasTabEntries));
        OnPropertyChanged(nameof(ShowTabEmpty));
        OnPropertyChanged(nameof(EntryFocusText));
    }
}
