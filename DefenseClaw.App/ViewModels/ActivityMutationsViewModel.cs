using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.Core.Audit;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// The Activity panel's Mutations tab: who changed configuration or policy, read from <c>audit.db</c> (the <c>activity_events</c>
/// journal plus the canonical <c>compliance.activity</c> / <c>enforcement.action</c> audit rows - see <see cref="MutationReader"/>),
/// so a change made from the CLI or the TUI shows up here too, unlike the Commands tab, which is only what this app ran.
/// <para>
/// <b>Loading.</b> Nothing is read until the tab is shown (<see cref="LoadAsync"/>), and a newer load abandons the one running:
/// its token ends the SQLite statement, not only the wait. The window is the newest <see cref="MutationReader.DefaultLimit"/>
/// changes; the connector filter and the search box narrow what was loaded, in memory. A failed read keeps the rows already
/// shown and says why in <see cref="StatusNote"/>.
/// </para>
/// <para>
/// <b>Empty is the expected state</b>, not a failure: the live <c>activity_events</c> table is empty on a fresh install, and the
/// empty state says where changes will appear. Selecting a row opens the shared inspector with a before / after diff.
/// </para>
/// </summary>
public sealed partial class ActivityMutationsViewModel : ObservableObject
{
    /// <summary>The first entry of <see cref="Connectors"/>: no connector filter.</summary>
    public const string AllConnectors = "All connectors";

    private readonly MutationReader _reader;
    private readonly TimeSpan? _timeout;
    private CancellationTokenSource _generation = new();
    private IReadOnlyList<MutationRow> _loaded = Array.Empty<MutationRow>();
    private bool _applyingConnectors;
    private readonly ConnectorScope? _scope;

    [ObservableProperty]
    private MutationRow? _selectedRow;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _selectedConnector = AllConnectors;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isEmpty = true;

    [ObservableProperty]
    private string _emptyTitle = "No mutations";

    [ObservableProperty]
    private string _emptyDetail = DefaultEmptyDetail;

    [ObservableProperty]
    private string _summary = string.Empty;

    /// <summary>Why the last read failed, or empty.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusNote))]
    private string _statusNote = string.Empty;

    /// <summary>True once a read has finished (ok or not), so the first look is not mistaken for "nothing recorded".</summary>
    [ObservableProperty]
    private bool _hasLoaded;

    /// <param name="scope">
    /// The shared connector scope: a change that names another connector (or none) is not listed while it is narrowed. The tab's own
    /// <see cref="SelectedConnector"/> narrows further and is no longer in the toolbar (the scope chip is); it stays as this view-model's
    /// own filter. Null (tests without a shell) means no shared scope.
    /// </param>
    internal ActivityMutationsViewModel(MutationReader reader, TimeSpan? timeout = null, ConnectorScope? scope = null)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _timeout = timeout;
        _scope = scope;
        Connectors.Add(AllConnectors);
    }

    /// <summary>The empty state's sentence (after the title).</summary>
    public const string DefaultEmptyDetail =
        "Gateway configuration mutations and policy changes appear here from the audit database.";

    public ObservableCollection<MutationRow> Rows { get; } = new();

    /// <summary>"All connectors", then every connector a loaded change names.</summary>
    public ObservableCollection<string> Connectors { get; } = new();

    public bool HasSelection => SelectedRow is not null;

    public bool HasStatusNote => StatusNote.Length > 0;

    /// <summary>True when a loaded change names a connector, which is when the filter has something to choose (the Mac shows its chip from two connectors on).</summary>
    public bool CanFilterConnectors => Connectors.Count > 1;

    /// <summary>How many loaded changes there are, before the filters.</summary>
    public int LoadedCount => _loaded.Count;

    partial void OnSelectedRowChanged(MutationRow? value) => OnPropertyChanged(nameof(HasSelection));

    partial void OnSearchTextChanged(string value) => ApplyFilters();

    partial void OnSelectedConnectorChanged(string value)
    {
        if (!_applyingConnectors)
        {
            ApplyFilters();
        }
    }

    [RelayCommand]
    private void ClearSelection() => SelectedRow = null;

    [RelayCommand]
    private Task Refresh() => LoadAsync();

    /// <summary>Reads the history now, abandoning a read that is still running.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource previous;
        var next = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        previous = Interlocked.Exchange(ref _generation, next);
        await previous.CancelAsync().ConfigureAwait(true);
        previous.Dispose();

        IsLoading = true;
        try
        {
            var result = await _reader.ReadAsync(MutationReader.DefaultLimit, _timeout, next.Token).ConfigureAwait(true);
            if (next.IsCancellationRequested)
            {
                return;
            }

            _loaded = result.Items.Select(item => new MutationRow(item)).ToList();
            StatusNote = result.HasMore
                ? string.Create(CultureInfo.CurrentCulture, $"Showing the newest {MutationReader.DefaultLimit} changes; older ones are in the Audit panel.")
                : string.Empty;
            RebuildConnectors();
            ApplyFilters();
        }
        catch (OperationCanceledException)
        {
            // A newer read (or the panel leaving) abandoned this one; it has nothing to say.
        }
        catch (TimeoutException ex)
        {
            StatusNote = ex.Message;
            ApplyFilters();
        }
#pragma warning disable CA1031 // A locked, corrupt or unreadable database is a note on the tab, not a crash; the rows already shown stay.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            StatusNote = "The audit database could not be read: " + ex.Message;
            ApplyFilters();
        }
        finally
        {
            if (ReferenceEquals(_generation, next))
            {
                IsLoading = false;
                HasLoaded = true;
            }
        }
    }

    /// <summary>The shared connector scope changed: the loaded changes are listed again under it (no new read).</summary>
    public void ReapplyScope() => ApplyFilters();

    /// <summary>Stops a read that is running (the panel left the screen).</summary>
    public void Cancel() => _generation.Cancel();

    private void RebuildConnectors()
    {
        var wanted = _loaded
            .Select(row => row.Item.Connector)
            .Where(name => !string.IsNullOrEmpty(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Cast<string>()
            .Prepend(AllConnectors)
            .ToList();

        _applyingConnectors = true;
        try
        {
            SyncStrings(Connectors, wanted);
            if (!Connectors.Contains(SelectedConnector, StringComparer.OrdinalIgnoreCase))
            {
                SelectedConnector = AllConnectors;
            }
        }
        finally
        {
            _applyingConnectors = false;
        }

        OnPropertyChanged(nameof(CanFilterConnectors));
    }

    private static void SyncStrings(ObservableCollection<string> target, IReadOnlyList<string> desired)
    {
        if (target.SequenceEqual(desired))
        {
            return;
        }

        target.Clear();
        foreach (var name in desired)
        {
            target.Add(name);
        }
    }

    private void ApplyFilters()
    {
        var search = SearchText.Trim();
        var connector = SelectedConnector;
        var scoped = !string.Equals(connector, AllConnectors, StringComparison.Ordinal);

        var visible = _loaded
            .Where(row => !scoped || string.Equals(row.Item.Connector, connector, StringComparison.OrdinalIgnoreCase))
            .Where(row => _scope is null || _scope.Allows(row.Item.Connector))
            .Where(row => row.Matches(search))
            .ToList();

        var selectedId = SelectedRow?.Id;
        Rows.Clear();
        foreach (var row in visible)
        {
            Rows.Add(row);
        }

        // A reload builds new row objects: keep the open inspector on the same change.
        SelectedRow = selectedId is null ? null : visible.FirstOrDefault(row => string.Equals(row.Id, selectedId, StringComparison.Ordinal));

        IsEmpty = Rows.Count == 0;
        if (_loaded.Count == 0)
        {
            EmptyTitle = "No mutations";
            EmptyDetail = DefaultEmptyDetail;
        }
        else
        {
            EmptyTitle = "No mutations match";
            EmptyDetail = "Nothing loaded matches the connector or the search. Clear them to see all " +
                          _loaded.Count.ToString("N0", CultureInfo.CurrentCulture) + " changes.";
        }

        Summary = _loaded.Count == 0
            ? "Configuration and policy changes"
            : Rows.Count == _loaded.Count
                ? string.Create(CultureInfo.CurrentCulture, $"{Rows.Count:N0} {(Rows.Count == 1 ? "change" : "changes")}")
                : string.Create(CultureInfo.CurrentCulture, $"{Rows.Count:N0} of {_loaded.Count:N0} changes");
    }
}
