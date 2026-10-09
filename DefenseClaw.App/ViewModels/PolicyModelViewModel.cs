using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Policy;
using DefenseClaw.Core.Policy.Model;
using DefenseClaw.Core.Runtime;
using Wpf.Ui.Controls;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// The Policies panel on a runtime that has the Mac's policy model (<see cref="RuntimeCapability.PolicyModel"/>; verified against DefenseClaw
/// source commit 95159fd): posture, opt-in packs, chains, rule families, named policies and rule packs. (The runtime's seventh view, Sandbox
/// packs, is left out where sandboxes are unsupported, as its own TUI leaves it out; see <see cref="PolicyModel"/>.)
/// <list type="bullet">
///   <item><b>The data is the runtime's.</b> The catalog is read with the read-only commands that exist at that commit
///     (<see cref="PolicyModelReader"/>) and the rows, details, consequences and "weaker" verdicts are the runtime's own model
///     (<see cref="PolicyModel"/>), not wording of this app's.</item>
///   <item><b>Scopes.</b> The global default and each active connector. Opt-in packs and rule families are shown for the chosen scope; the
///     Posture view lists them all, with what each inherits from the global default or its rule pack.</item>
///   <item><b>Tool-call levels and LLM thresholds are different.</b> Posture's <c>Blocks at</c> / <c>Alerts at</c> decide what happens to a
///     tool call; Policies' <c>LLM block</c> / <c>LLM alert</c> decide what happens to LLM traffic through the guardrail proxy.</item>
///   <item><b>Every change is reviewed</b> with the exact command through <see cref="DiscoverActionReview"/>, and one that protects less needs
///     an acknowledgement. Only these changes exist: <c>guardrail mode | block-at | alert-at | hilt | use-pack | protection</c>,
///     <c>policy activate</c> and <c>policy edit guardrail</c> (<see cref="PolicyActionGuard"/>). A rule pack is validated before it is
///     switched to, and a policy bundle before a policy is activated; a validation that does not pass stops the change.</item>
///   <item><b>Only a complete, recent read authorizes a change</b> (<see cref="CatalogTrust"/>): a part that could not be read, a read gone
///     old, or a <c>config.yaml</c> that changed since turns every change off, with the reason as the tooltip.</item>
/// </list>
/// </summary>
public sealed partial class PolicyModelViewModel : PanelViewModelBase, IDisposable
{
    /// <summary>A read this old is read again when the panel comes back into view.</summary>
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(2);

    /// <summary>The sentence on the acknowledgement checkbox of a review that reduces protection (the same as the 0.8.10 panel's).</summary>
    public const string WeakeningAcknowledgement = PoliciesPanelViewModel.WeakeningAcknowledgement;

    private readonly SevenViewPolicyBackend _backend;
    private IPolicyDataFiles _files;
    private PolicyModelReader? _reader;
    private PolicyCatalog? _catalog;
    private PolicyModel? _model;
    private DateTimeOffset? _lastLoadedAt;
    private readonly object _loadGate = new();
    private bool _loadRunning;
    private bool _reloadWanted;
    private Task? _loadTask;
    private int _generation;
    private bool _disposed;
    private bool _syncing;

    public PolicyModelViewModel(AppServices services)
        : this(services, backend: null, files: null)
    {
    }

    internal PolicyModelViewModel(AppServices services, SevenViewPolicyBackend? backend, IPolicyDataFiles? files)
        : base(services)
    {
        _backend = backend ?? SevenViewPolicyBackend.Instance;

        // A container's files are inside the container; this app cannot reach them, so those views say the catalog was not found.
        _files = files ?? (services.Paths.Runtime.Kind == RuntimeKind.Container ? NoPolicyDataFiles.Instance : FileSystemPolicyData.Instance);
        Trust = CatalogTrust.Watching(services.Paths, readClause: "these settings were read");
        Review = new DiscoverActionReview(services) { RunGuard = ReasonToRefuseRun };
        Review.PropertyChanged += OnReviewChanged;
        foreach (var view in PolicyModel.ViewIds)
        {
            Nav.Add(new PolicyNavItemViewModel(view, PolicyModel.TitleOf(view)));
        }

        _selectedNav = Nav[0];
        _view = Nav[0].View;
    }

    public override string Title => "Policies";

    public override string Description =>
        "The connected runtime's policy model: posture per scope, opt-in protection packs, chains, rule families, named policies and rule packs.";

    // ---- what is shown -----------------------------------------------------------------------------------------------------

    /// <summary>The views, in order, with their counts.</summary>
    public ObservableCollection<PolicyNavItemViewModel> Nav { get; } = new();

    /// <summary>The global default, then each active connector: what Opt-in packs and Rule families are shown for.</summary>
    public ObservableCollection<string> Scopes { get; } = new() { PolicyScopes.Global };

    /// <summary>The rows of the current view that match the filter.</summary>
    public ObservableCollection<PolicyTableRow> Rows { get; } = new();

    /// <summary>The confirm-and-run overlay every change goes through.</summary>
    public DiscoverActionReview Review { get; }

    /// <summary>Whether the data may authorize a change; see <see cref="CatalogTrust"/>.</summary>
    public CatalogTrust Trust { get; internal set; }

    /// <summary>Test seam: runs a command instead of <c>Services.Cli.RunAsync</c>, so a test feeds exact output and never starts a process.</summary>
    internal Func<IReadOnlyList<string>, CliRunOptions, Task<CliInvocation>>? RunCli { get; set; }

    /// <summary>
    /// Test seam: where the runtime's data files (the chain catalog, the rule families) are read from. Set before the first read; the default is
    /// the file system, or nothing for a container runtime whose files this app cannot reach.
    /// </summary>
    internal IPolicyDataFiles DataFiles
    {
        get => _files;
        set
        {
            _files = value ?? throw new ArgumentNullException(nameof(value));
            _reader = null;
        }
    }

    /// <summary>The model of the last good read; null before the first one.</summary>
    internal PolicyModel? CurrentModel => _model;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLoading), nameof(ShowContent), nameof(ShowError))]
    private PoliciesState _state = PoliciesState.Loading;

    [ObservableProperty]
    private string _errorTitle = string.Empty;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    /// <summary>The heading of the warning bar over a read that is old or incomplete.</summary>
    [ObservableProperty]
    private string _readWarningTitle = string.Empty;

    /// <summary>Why the rows on screen are from an earlier read, or are incomplete; empty otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReadWarning))]
    private string _readWarning = string.Empty;

    /// <summary><c>● default policy · default pack · 0 of 5 opt-in packs · 26 chains (4 can block)</c>: the toolbar's caption.</summary>
    [ObservableProperty]
    private string _header = string.Empty;

    /// <summary>The current view's status line.</summary>
    [ObservableProperty]
    private string _headline = string.Empty;

    /// <summary>Why the current view could not be read; empty when it could.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasViewError), nameof(HeadlineTone))]
    private string _viewError = string.Empty;

    /// <summary>What an empty view says; empty while it has rows.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    private string _emptyText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty), nameof(NoMatches))]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _view;

    [ObservableProperty]
    private PolicyNavItemViewModel? _selectedNav;

    [ObservableProperty]
    private string _selectedScope = PolicyScopes.Global;

    /// <summary>The current view's columns; the view builds its grid from them when they change.</summary>
    [ObservableProperty]
    private IReadOnlyList<PolicyColumn> _columns = Array.Empty<PolicyColumn>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private PolicyTableRow? _selectedRow;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CaptionText), nameof(CanChange), nameof(ChangesBlockedReason), nameof(CanRunReads))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChange), nameof(ChangesBlockedReason), nameof(CanRunReads))]
    private bool _isRunning;

    public bool ShowLoading => State == PoliciesState.Loading;

    public bool ShowContent => State == PoliciesState.Loaded;

    public bool ShowError => State is PoliciesState.Error or PoliciesState.CliUnavailable;

    public bool HasReadWarning => ReadWarning.Length > 0;

    public bool HasViewError => ViewError.Length > 0;

    /// <summary>The tone of the status line: the error tone while the view could not be read, otherwise the quiet one.</summary>
    public string HeadlineTone => HasViewError ? "Bad" : "Neutral";

    public bool HasSelection => SelectedRow is not null;

    /// <summary>The empty state shows: the view has no rows, and nothing explains why (an error does).</summary>
    public bool ShowEmpty => EmptyText.Length > 0 && SearchText.Trim().Length == 0 && Rows.Count == 0;

    /// <summary>The filter hides every row of a view that has some.</summary>
    public bool NoMatches => SearchText.Trim().Length > 0 && Rows.Count == 0 && TotalRows > 0;

    /// <summary>How many rows the view has before the filter.</summary>
    public int TotalRows { get; private set; }

    /// <summary>The view's rows before the filter: what the grid sizes its columns by, so a filter does not make them jump.</summary>
    public IReadOnlyList<PolicyTableRow> AllRows { get; private set; } = Array.Empty<PolicyTableRow>();

    /// <summary>The scope chooser shows for the views that depend on a scope, when there is more than one to choose from.</summary>
    public bool ShowScopeChooser => View is "optin" or "families" && Scopes.Count > 1;

    /// <summary>The toolbar caption: the model's header (policy, pack, opt-in packs, chains).</summary>
    public string CaptionText => State == PoliciesState.Loaded ? Header : string.Empty;

    /// <summary>What a view whose rows cannot be changed says under the table.</summary>
    public string ReadOnlyNote => View switch
    {
        "chains" => "Built in and read-only: the runtime does not let chains be changed or turned off.",
        "families" => "Read-only: a scope's rule families follow its rule pack. Switch the pack on Posture or Rule packs.",
        _ => string.Empty,
    };

    public bool HasReadOnlyNote => ReadOnlyNote.Length > 0;

    // ---- what may change -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// The data is a complete, recent read and nothing else is running: a change may be offered. Re-read at the moment a change is requested
    /// (<see cref="RefuseChange"/>), because a button drawn a few minutes ago must not authorize anything now.
    /// </summary>
    public bool CanChange => ChangesBlockedReasonNow is null;

    /// <summary>Why changes are off, as a tooltip sentence; null while they are on.</summary>
    public string? ChangesBlockedReason => ChangesBlockedReasonNow;

    /// <summary>Validation (a read) can start: not while another command runs.</summary>
    public bool CanRunReads => !IsRunning && !IsBusy;

    private string? ChangesBlockedReasonNow => TrustBlockedReason ?? (IsBusy || IsRunning ? "Another command is running." : null);

    /// <summary>
    /// Why the data on screen may not be acted on: the installation is read-only (managed or invalid; it comes first, being the one reason a
    /// refresh would not cure), or no read yet, a failed or partial or old one, or a configuration that moved since (the trust says all of
    /// those). Null when it may.
    /// </summary>
    private string? TrustBlockedReason =>
        Services.Installation.BlockedReason
        ?? (State != PoliciesState.Loaded
            ? Trust.Reason ?? "Changes are off until the policies have been read."
            : Trust.Reason);

    protected override void OnInstallationChanged() => NotifyTrust();

    private void NotifyTrust()
    {
        OnPropertyChanged(nameof(CanChange));
        OnPropertyChanged(nameof(ChangesBlockedReason));
        OnPropertyChanged(nameof(CanRunReads));
        OnPropertyChanged(nameof(ActionsNote));
        OnPropertyChanged(nameof(HasActionsNote));
        foreach (var group in ActionGroups)
        {
            foreach (var action in group.Actions)
            {
                action.Refresh();
            }
        }
    }

    // ---- lifecycle -----------------------------------------------------------------------------------------------------------

    public override Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        // The model can be on screen before it is initialized: on the Policies panel's first visit the probe's own notification may build and
        // activate it while the visit is still waiting for that probe, and the activation reads. A good read that has finished is the first read
        // (one still running is joined below); a failed one is tried again.
        lock (_loadGate)
        {
            if (_lastLoadedAt is not null && !_loadRunning)
            {
                return Task.CompletedTask;
            }
        }

        return LoadIfIdleAsync();
    }

    protected override void OnActivated()
    {
        Services.ConfigReloaded += OnConfigReloaded;

        // A change to config.yaml or .env while the panel was away is not heard: compare the files with what the read saw.
        _ = Trust.CheckConfig();

        NotifyTrust();

        // One catch-up read per visit, and only when the data is old or the configuration moved. Not a timer.
        var stale = _lastLoadedAt is null || Trust.IsStale || DefenseClaw.Core.Time.WallClock.Elapsed(_lastLoadedAt.Value) >= StaleAfter;
        if (stale && !_loadRunning && !Review.IsOpen)
        {
            _ = LoadIfIdleAsync();
        }
    }

    protected override void OnDeactivated() => Services.ConfigReloaded -= OnConfigReloaded;

    /// <summary>Stops listening and drops any read still in flight: the panel went back to the surface of a runtime without the model.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _generation++;
        Services.ConfigReloaded -= OnConfigReloaded;
        Review.PropertyChanged -= OnReviewChanged;
    }

    private void OnConfigReloaded(object? sender, EventArgs e)
    {
        // The latest read began after this change (a change this panel made, read straight away): the trust finds nothing moved since it.
        if (_disposed || !Trust.CheckConfig())
        {
            return;
        }

        NotifyTrust();

        // Read again now unless something is running or being decided; a finished review reads again by itself.
        if (!_loadRunning && !IsRunning && !Review.IsOpen)
        {
            _ = LoadIfIdleAsync();
        }
    }

    private void OnReviewChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DiscoverActionReview.IsOpen) && !Review.IsOpen && Trust.IsStale && !_loadRunning && !IsRunning && !_disposed)
        {
            _ = LoadIfIdleAsync();
        }
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadIfIdleAsync();

    // ---- reading -------------------------------------------------------------------------------------------------------------

    private PolicyModelReader Reader => _reader ??= _backend.Reader((argv, token) => RunReadAsync(argv, token), _files);

    private Task<CliInvocation> RunCliAsync(IReadOnlyList<string> argv, CliRunOptions options, CancellationToken cancellationToken) =>
        RunCli is { } run ? run(argv, options) : Services.Cli.RunAsync(argv, cancellationToken: cancellationToken, options: options);

    /// <summary>
    /// Runs one of the read-only commands the panel starts on its own (the catalog reads, a pack validation, <c>policy validate</c>). Anything
    /// else throws: this is the one door the panel starts a command through without a review, and it is not a door for a change.
    /// </summary>
    internal Task<CliInvocation> RunReadAsync(IReadOnlyList<string> argv, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(argv);
        if (!PolicyActionGuard.IsAllowedRead(argv))
        {
            throw new InvalidOperationException($"'{string.Join(' ', argv)}' is not a read-only Policies command, so it has to be reviewed first.");
        }

        return RunCliAsync(argv, CliRunOptions.JsonRead, cancellationToken);
    }

    /// <summary>
    /// Reads now - unless a read is already running, which then is the read (a second trigger for the same moment: the first visit's two, a click
    /// on Refresh over an activation read). Returns the running read, so a caller that waits waits for it.
    /// </summary>
    private Task LoadIfIdleAsync() => StartLoad(queueIfRunning: false);

    /// <summary>
    /// Reads now. A read already in flight may have begun before whatever asks for this one (a change was just made), so it reads once more when
    /// that one ends. Returns the whole of it, the second read included.
    /// </summary>
    private Task LoadAsync() => StartLoad(queueIfRunning: true);

    private Task StartLoad(bool queueIfRunning)
    {
        // On the UI thread in the app, where this cannot interleave; the lock keeps two callers on different threads (a test: the first visit off
        // the UI thread, a notification marshalled to it) from both starting a read.
        lock (_loadGate)
        {
            if (_disposed)
            {
                return Task.CompletedTask;
            }

            if (_loadRunning)
            {
                if (queueIfRunning)
                {
                    _reloadWanted = true;
                }

                return _loadTask ?? Task.CompletedTask;
            }

            _loadRunning = true;
            _loadTask = LoadLoopAsync();
            return _loadTask;
        }
    }

    private async Task LoadLoopAsync()
    {
        var released = false;
        IsBusy = true;
        try
        {
            while (true)
            {
                lock (_loadGate)
                {
                    _reloadWanted = false;
                }

                await LoadOnceAsync().ConfigureAwait(true);

                // Decided under the lock a caller asks for another read under, so a request made as this one ends is never lost.
                lock (_loadGate)
                {
                    if (_disposed || !_reloadWanted)
                    {
                        _loadRunning = false;
                        released = true;
                        break;
                    }
                }
            }
        }
        finally
        {
            if (!released)
            {
                lock (_loadGate)
                {
                    _loadRunning = false;
                }
            }

            IsBusy = false;
            NotifyTrust();
        }
    }

    private async Task LoadOnceAsync()
    {
        var generation = ++_generation;

        // What config.yaml and .env look like as this read starts: the data it produces was read under that.
        Trust.BeginRead();
        try
        {
            var read = await Reader.ReadAsync(_catalog).ConfigureAwait(true);
            if (generation == _generation)
            {
                ApplyRead(read);
            }
        }
        catch (CliNotFoundException ex)
        {
            if (generation == _generation)
            {
                FailLoad(PoliciesState.CliUnavailable, "The defenseclaw CLI was not found", $"Policies cannot be read without it. {ex.Message}");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Nothing here may escape: this runs fire-and-forget on activation.
            if (generation == _generation)
            {
                FailLoad(PoliciesState.Error, "Could not read the policies", ex.Message);
            }
        }
    }

    private void ApplyRead(PolicyCatalogRead read)
    {
        if (!read.ReadAnything)
        {
            FailLoad(PoliciesState.Error, "Could not read the policies", string.Join(" ", read.Problems));
            return;
        }

        _catalog = read.Catalog;
        _model = new PolicyModel(read.Catalog);
        _lastLoadedAt = DateTimeOffset.Now;
        ErrorTitle = string.Empty;
        ErrorMessage = string.Empty;
        if (read.IsComplete)
        {
            Trust.MarkComplete();
            ReadWarningTitle = string.Empty;
            ReadWarning = string.Empty;
        }
        else
        {
            Trust.MarkPartial(read.Problems);
            ReadWarningTitle = "Incomplete read";
            ReadWarning = string.Join("\n", read.Problems) + "\nThe rest is shown; changes are off until a read is complete.";
        }

        State = PoliciesState.Loaded;
        Rebuild();
    }

    /// <summary>Keeps the last good rows (labelled, and no longer authorizing anything) when a refresh fails; an error state when there are none.</summary>
    private void FailLoad(PoliciesState failedState, string title, string message)
    {
        Trust.MarkFailed($"{title}: {message}");

        if (_model is not null && _lastLoadedAt is { } at)
        {
            ReadWarningTitle = "Showing an older read";
            ReadWarning = $"{title}: {message} Showing the last good read (as of {at.LocalDateTime:HH:mm}); changes are off until the policies are read again.";
            return;
        }

        _catalog = null;
        _model = null;
        _lastLoadedAt = null;
        ErrorTitle = title;
        ErrorMessage = message;
        State = failedState;
        ClearRows();
    }

    // ---- the current view ----------------------------------------------------------------------------------------------------

    partial void OnSelectedNavChanged(PolicyNavItemViewModel? value)
    {
        if (_syncing || value is null || string.Equals(value.View, View, StringComparison.Ordinal))
        {
            return;
        }

        View = value.View;
        SelectedRow = null;
        Rebuild();
        OnPropertyChanged(nameof(ShowScopeChooser));
        OnPropertyChanged(nameof(ReadOnlyNote));
        OnPropertyChanged(nameof(HasReadOnlyNote));
    }

    partial void OnSelectedScopeChanged(string value)
    {
        if (!_syncing)
        {
            Rebuild();
        }
    }

    partial void OnSearchTextChanged(string value) => Rebuild();

    partial void OnSelectedRowChanged(PolicyTableRow? value)
    {
        // The posture row and the opt-in / families scope are one choice (the runtime's own).
        if (!_syncing && value is not null && string.Equals(View, "posture", StringComparison.Ordinal) && !string.Equals(SelectedScope, value.Key, StringComparison.Ordinal))
        {
            _syncing = true;
            try
            {
                SelectedScope = value.Key;
            }
            finally
            {
                _syncing = false;
            }

            SyncChrome();
        }

        RefreshInspector();
    }

    /// <summary>The scope the model needs for the view being shown.</summary>
    private ScopePosture? ScopeRowOfView() => _model?.ScopeRowOrGlobal(SelectedScope);

    private void ClearRows()
    {
        Rows.Clear();
        TotalRows = 0;
        SelectedRow = null;
        foreach (var item in Nav)
        {
            item.Badge = string.Empty;
        }

        Header = string.Empty;
        Headline = string.Empty;
        ViewError = string.Empty;
        EmptyText = string.Empty;
        RefreshInspector();
        OnPropertyChanged(nameof(CaptionText));
    }

    /// <summary>Brings every bound value in line with the model, the view, the scope and the filter. Keeps the selected row when it is still there.</summary>
    private void Rebuild()
    {
        if (_model is not { } model)
        {
            return;
        }

        var keep = SelectedRow?.Key;
        _syncing = true;
        try
        {
            SyncScopes(model);
            SyncChrome();

            var scope = ScopeRowOfView();
            var table = model.Table(View, scope);
            AllRows = table.Rows;
            Columns = table.Columns;
            TotalRows = table.Rows.Count;

            var query = SearchText.Trim();
            var wanted = table.Rows
                .Where(r => query.Length == 0 || r.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();
            SyncCollection(Rows, wanted, r => r.Key, SameRow);

            SelectedRow = keep is null ? null : Rows.FirstOrDefault(r => string.Equals(r.Key, keep, StringComparison.Ordinal));
        }
        finally
        {
            _syncing = false;
        }

        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(NoMatches));
        OnPropertyChanged(nameof(ShowScopeChooser));
        OnPropertyChanged(nameof(ReadOnlyNote));
        OnPropertyChanged(nameof(HasReadOnlyNote));
        RefreshInspector();
    }

    /// <summary>The navigation counts and the text over the table: what depends on the model and the scope but not on the rows.</summary>
    private void SyncChrome()
    {
        if (_model is not { } model)
        {
            return;
        }

        var scope = ScopeRowOfView();
        foreach (var entry in model.Nav(scope))
        {
            Nav.First(n => string.Equals(n.View, entry.View, StringComparison.Ordinal)).Badge = entry.Badge;
        }

        Header = model.Header();

        // The runtime's line points at its scope picker with a "▾"; here the scope is a chooser of its own.
        Headline = model.Headline(View, scope).Replace(" ▾", string.Empty, StringComparison.Ordinal);
        ViewError = model.ViewError(View);
        EmptyText = model.EmptyState(View, scope);
        OnPropertyChanged(nameof(CaptionText));
    }

    private void SyncScopes(PolicyModel model)
    {
        var scopes = model.Scopes;
        SyncCollection(Scopes, scopes, s => s, (a, b) => true);
        if (!scopes.Contains(SelectedScope, StringComparer.Ordinal))
        {
            SelectedScope = PolicyScopes.Global;
        }
    }

    private static bool SameRow(PolicyTableRow a, PolicyTableRow b) =>
        string.Equals(a.SearchText, b.SearchText, StringComparison.Ordinal)
        && a.Cells.Count == b.Cells.Count
        && a.Cells.Zip(b.Cells).All(p => p.First == p.Second);

    // ---- selection and Esc ---------------------------------------------------------------------------------------------------

    [RelayCommand]
    private void ClearSelection() => SelectedRow = null;

    /// <summary>
    /// Esc closes the topmost transient surface: the review, then the output and notice, then the detail. True when it closed something.
    /// </summary>
    public bool HandleEscape()
    {
        if (Review.HandleEscape())
        {
            return true;
        }

        if (HasNotice)
        {
            CloseNotice();
            return true;
        }

        if (HasOutput)
        {
            CloseOutput();
            return true;
        }

        if (SelectedRow is not null)
        {
            SelectedRow = null;
            return true;
        }

        return false;
    }

    private static void TraceFault(string what, Exception ex) => Trace.TraceError($"Policies panel: {what}: {ex}");
}
