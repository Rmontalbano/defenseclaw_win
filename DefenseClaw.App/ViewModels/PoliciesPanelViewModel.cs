using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Policy;

namespace DefenseClaw.App.ViewModels;

/// <summary>Where the Policies list is in its life.</summary>
public enum PoliciesState
{
    Loading,
    Loaded,
    Empty,
    Error,
    CliUnavailable,
}

/// <summary>What the last <c>policy validate</c> said.</summary>
public enum PolicyValidation
{
    /// <summary>Not run since the panel opened or the list was re-read.</summary>
    NotRun,
    Running,
    Passed,
    Failed,
}

/// <summary>
/// The Policies panel on the installed 0.8.10 <c>defenseclaw policy</c> commands: the table of built-in and custom policies with the active
/// one marked, the selected policy's detail (<c>policy show</c>), and the actions on it.
/// <list type="bullet">
///   <item><b>Reads run directly.</b> <c>list</c>, <c>show</c>, <c>validate</c> and <c>test</c> only read (the source writes nothing for any
///     of them) and their output is shown, copyable, in the panel. They go through <see cref="RunReadAsync"/>, which refuses any other
///     command, so nothing this panel starts on its own can change state.</item>
///   <item><b>Every change is reviewed.</b> Activate, create, edit and delete open the shared <see cref="DiscoverActionReview"/> with the exact
///     argv and the tier. Activate also shows what the switch changes from the active policy, and a switch that reduces protection needs
///     an explicit acknowledgement before the confirm button works (<see cref="PolicyDiff"/>).</item>
///   <item><b>Validate first.</b> Activate runs <c>policy validate</c> and offers the review only when it passes.</item>
///   <item><b>Only a trusted list authorizes a change</b> (<see cref="CatalogTrust"/>): a failed or old read keeps its rows on screen and
///     turns every change off, with the reason as the tooltip.</item>
/// </list>
/// What each command does is in <see cref="IPolicyBackend"/>, so the newer runtime's Policies surface (CUST-293) swaps in behind the same
/// panel behaviour.
/// </summary>
public sealed partial class PoliciesPanelViewModel : PanelViewModelBase
{
    /// <summary>A read this old is read again when the panel comes back into view.</summary>
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(2);

    private readonly IPolicyBackend _backend;
    private readonly List<PolicyRow> _all = new();
    private readonly Dictionary<string, PolicyDetail> _details = new(StringComparer.Ordinal);
    private DateTimeOffset? _lastLoadedAt;
    private bool _loadRunning;
    private int _detailGeneration;

    public PoliciesPanelViewModel(AppServices services)
        : this(services, backend: null)
    {
    }

    internal PoliciesPanelViewModel(AppServices services, IPolicyBackend? backend)
        : base(services)
    {
        _backend = backend ?? PolicyBackends.ForInstalledRuntime();
        Review = new DiscoverActionReview(services);
        Form = new PolicyFormViewModel();
        Rows = new ObservableCollection<PolicyRow>();
    }

    public override string Title => "Policies";

    public override string Description =>
        "Security policies for skills, MCP servers and plugins: which are built in, which you made, and which one is active.";

    public ObservableCollection<PolicyRow> Rows { get; }

    /// <summary>The confirm-and-run overlay every change goes through.</summary>
    public DiscoverActionReview Review { get; }

    /// <summary>The create / edit form (open: <see cref="PolicyFormViewModel.IsOpen"/>).</summary>
    public PolicyFormViewModel Form { get; }

    /// <summary>Whether the list may authorize a change; see <see cref="CatalogTrust"/>.</summary>
    public CatalogTrust Trust { get; internal set; } = new();

    /// <summary>Test seam: runs a command instead of <c>Services.Cli.RunAsync</c>, so a test feeds exact output and never starts a process.</summary>
    internal Func<IReadOnlyList<string>, CliRunOptions, Task<CliInvocation>>? RunCli { get; set; }

    // ---- State ---------------------------------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLoading), nameof(ShowList), nameof(ShowEmpty), nameof(ShowError))]
    private PoliciesState _state = PoliciesState.Loading;

    [ObservableProperty]
    private string _errorTitle = string.Empty;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    /// <summary>Why the rows on screen are from an earlier read (a refresh failed); empty otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRefreshWarning))]
    private string _refreshWarning = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CaptionText))]
    private string _searchText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private PolicyRow? _selectedRow;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CaptionText), nameof(CanChange), nameof(ChangesBlockedReason))]
    private bool _isBusy;

    public bool ShowLoading => State == PoliciesState.Loading;

    public bool ShowList => State is PoliciesState.Loaded;

    public bool ShowEmpty => State == PoliciesState.Empty;

    public bool ShowError => State is PoliciesState.Error or PoliciesState.CliUnavailable;

    public bool HasRefreshWarning => RefreshWarning.Length > 0;

    public bool HasSelection => SelectedRow is not null;

    /// <summary>The policy marked active in the last read; null when none is.</summary>
    public string? ActiveName => _all.FirstOrDefault(r => r.IsActive)?.Name;

    /// <summary>The toolbar caption: how many policies, which is active.</summary>
    public string CaptionText
    {
        get
        {
            if (State == PoliciesState.Loading || (State != PoliciesState.Loaded && State != PoliciesState.Empty))
            {
                return string.Empty;
            }

            var shown = Rows.Count == _all.Count ? $"{_all.Count} policies" : $"{Rows.Count} of {_all.Count} policies";
            return ActiveName is { } active ? $"{shown} - active: {active}" : $"{shown} - none active";
        }
    }

    // ---- What may change -----------------------------------------------------------------------------------------------

    /// <summary>
    /// The rows are a complete, recent read and nothing else is running: a change may be offered. Re-read at the moment a change is
    /// requested (<see cref="RefuseChange"/>), because a button drawn a few minutes ago must not authorize anything now.
    /// </summary>
    public bool CanChange => ChangesBlockedReasonNow is null;

    /// <summary>Why changes are off, as a tooltip sentence; null while they are on.</summary>
    public string? ChangesBlockedReason => ChangesBlockedReasonNow;

    private string? ChangesBlockedReasonNow
    {
        get
        {
            if (State is not (PoliciesState.Loaded or PoliciesState.Empty))
            {
                return Trust.Reason ?? "Changes are off until the policies have been read.";
            }

            if (Trust.Reason is { } reason)
            {
                return reason;
            }

            return IsBusy || IsRunning ? "Another command is running." : null;
        }
    }

    private void NotifyTrust()
    {
        OnPropertyChanged(nameof(CanChange));
        OnPropertyChanged(nameof(ChangesBlockedReason));
        OnPropertyChanged(nameof(CanActivate));
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(ActivateTip));
        OnPropertyChanged(nameof(EditTip));
        OnPropertyChanged(nameof(DeleteTip));
        OnPropertyChanged(nameof(CreateTip));
        OnPropertyChanged(nameof(CanCreate));
    }

    // ---- Lifecycle -----------------------------------------------------------------------------------------------------

    public override Task InitializeAsync(CancellationToken cancellationToken = default) => LoadAsync();

    protected override void OnActivated()
    {
        // The bound values only update when something raises PropertyChanged: do it now, so an old list is not offered as fresh.
        NotifyTrust();

        // One catch-up read per visit, and only when the data is old. Not a timer.
        var stale = _lastLoadedAt is null || DefenseClaw.Core.Time.WallClock.Elapsed(_lastLoadedAt.Value) >= StaleAfter;
        if (stale && !_loadRunning && !Review.IsOpen && !Form.IsOpen)
        {
            _ = LoadAsync();
        }
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();

    // ---- Reading -------------------------------------------------------------------------------------------------------

    private Task<CliInvocation> RunCliAsync(IReadOnlyList<string> argv, CliRunOptions options) =>
        RunCli is { } run ? run(argv, options) : Services.Cli.RunAsync(argv, options: options);

    /// <summary>
    /// Runs one of the four commands that only read (<c>policy list|show|validate|test</c>) without a review. Anything else throws: this
    /// is the one door the panel starts a command through on its own, and it is not a door for a change. <c>policy test</c> is
    /// <c>opa test</c> on the bundled Rego (nothing written; the generic verb list calls "test" a change, which is why the check is
    /// by exact command here and not by <see cref="CommandTiers.Classify"/>).
    /// </summary>
    internal Task<CliInvocation> RunReadAsync(IReadOnlyList<string> argv)
    {
        ArgumentNullException.ThrowIfNull(argv);

        var isRead = argv.Count >= 2
                     && argv[0] == "policy"
                     && ((argv[1] is "list" or "validate" or "test" && argv.Count == 2)
                         || (argv[1] == "show" && argv.Count == 3 && PolicyNames.IsSafe(argv[2])));
        if (!isRead)
        {
            throw new InvalidOperationException($"'{string.Join(' ', argv)}' is not a read-only policy command, so it has to be reviewed first.");
        }

        return RunCliAsync(argv, CliRunOptions.JsonRead);
    }

    private async Task LoadAsync()
    {
        if (_loadRunning)
        {
            return;
        }

        _loadRunning = true;
        Trust.MarkPending();
        IsBusy = true;
        try
        {
            await LoadOnceAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // Nothing here may escape: this runs fire-and-forget on activation.
            FailLoad(PoliciesState.Error, "Could not read the policies", ex.Message);
        }
        finally
        {
            _loadRunning = false;
            IsBusy = false;
            NotifyTrust();
            OnPropertyChanged(nameof(CaptionText));
        }
    }

    private async Task LoadOnceAsync()
    {
        var command = "defenseclaw " + string.Join(' ', _backend.ListArgv);
        CliInvocation invocation;
        try
        {
            invocation = await RunReadAsync(_backend.ListArgv).ConfigureAwait(true);
        }
        catch (CliNotFoundException ex)
        {
            FailLoad(PoliciesState.CliUnavailable, "The defenseclaw CLI was not found", $"Policies cannot be listed without it. {ex.Message}");
            return;
        }

        if (invocation.FailureReason is { Length: > 0 } failure)
        {
            FailLoad(PoliciesState.Error, $"'{command}' did not complete", failure);
            return;
        }

        if (invocation.ExitCode != 0)
        {
            FailLoad(PoliciesState.Error, $"'{command}' failed", Summarize(invocation));
            return;
        }

        if (invocation.IsOutputTruncated)
        {
            FailLoad(PoliciesState.Error, "The policy list is too large to read here", $"'{command}' printed more than this app keeps for one command.");
            return;
        }

        if (!_backend.TryParseList(Stdout(invocation), out var listing, out var parseError))
        {
            FailLoad(PoliciesState.Error, $"Unexpected output from '{command}'", parseError);
            return;
        }

        var selectedName = SelectedRow?.Name;
        _all.Clear();
        _all.AddRange(listing.Policies.Select(p => new PolicyRow(p)));
        _details.Clear();
        _lastLoadedAt = DateTimeOffset.Now;
        Trust.MarkComplete();
        RefreshWarning = string.Empty;
        ErrorTitle = string.Empty;
        ErrorMessage = string.Empty;
        Validation = PolicyValidation.NotRun;
        State = _all.Count == 0 ? PoliciesState.Empty : PoliciesState.Loaded;
        ApplyFilter();

        // Keep the selection across a refresh (its detail is read again: the policy may have changed).
        var reselected = selectedName is null ? null : Rows.FirstOrDefault(r => r.Name == selectedName);
        if (ReferenceEquals(reselected, SelectedRow) && reselected is not null)
        {
            _ = LoadDetailAsync(reselected);
        }
        else
        {
            SelectedRow = reselected;
        }

        OnPropertyChanged(nameof(ActiveName));
    }

    /// <summary>Keeps the last good rows (labelled, and no longer authorizing anything) when a refresh fails; an error state when there are none.</summary>
    private void FailLoad(PoliciesState failedState, string title, string message)
    {
        Trust.MarkFailed($"{title}: {message}");

        if (_lastLoadedAt is { } at && _all.Count > 0)
        {
            RefreshWarning = $"{title}: {message} Showing the last good read (as of {at.LocalDateTime:HH:mm}); changes are off until the list is read again.";
            return;
        }

        _all.Clear();
        Rows.Clear();
        _lastLoadedAt = null;
        ErrorTitle = title;
        ErrorMessage = message;
        State = failedState;
    }

    private void ApplyFilter()
    {
        var query = SearchText.Trim();
        var wanted = _all
            .Where(r => query.Length == 0
                        || r.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || r.Description.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || r.Kind.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || (r.IsActive && "active".Contains(query, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        SyncCollection(Rows, wanted, r => r.Name, (existing, desired) => existing.Summary == desired.Summary);
        if (SelectedRow is { } selected && !Rows.Contains(selected))
        {
            SelectedRow = Rows.FirstOrDefault(r => r.Name == selected.Name);
        }

        OnPropertyChanged(nameof(CaptionText));
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    // ---- Detail --------------------------------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetail), nameof(DetailSections))]
    private PolicyDetail? _detail;

    [ObservableProperty]
    private string _detailRawText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetailError))]
    private string _detailError = string.Empty;

    [ObservableProperty]
    private bool _isDetailLoading;

    public bool HasDetail => Detail is not null;

    public bool HasDetailError => DetailError.Length > 0;

    /// <summary>The inspector's body: the selected policy's sections.</summary>
    public IReadOnlyList<PolicyDetailSection> DetailSections =>
        Detail is { } detail ? PolicyDetailSections.Build(detail) : Array.Empty<PolicyDetailSection>();

    partial void OnSelectedRowChanged(PolicyRow? value)
    {
        Detail = null;
        DetailRawText = string.Empty;
        DetailError = string.Empty;
        NotifyTrust();
        if (value is not null)
        {
            _ = LoadDetailAsync(value);
        }
        else
        {
            _detailGeneration++;
            IsDetailLoading = false;
        }
    }

    /// <summary>Reads <c>policy show NAME</c> for the selected row and shows it; a newer selection supersedes an older read.</summary>
    private async Task LoadDetailAsync(PolicyRow row)
    {
        var generation = ++_detailGeneration;
        if (!row.HasSafeName)
        {
            DetailError = "This name cannot be passed to the CLI safely, so its detail is not read.";
            return;
        }

        IsDetailLoading = true;
        try
        {
            var read = await ReadDetailAsync(row.Name).ConfigureAwait(true);
            if (generation != _detailGeneration)
            {
                return;
            }

            if (read.Detail is null)
            {
                DetailError = read.Error;
                return;
            }

            Detail = read.Detail;
            DetailRawText = read.RawText;
        }
        finally
        {
            if (generation == _detailGeneration)
            {
                IsDetailLoading = false;
                NotifyTrust();
            }
        }
    }

    private readonly record struct DetailRead(PolicyDetail? Detail, string RawText, string Error);

    private async Task<DetailRead> ReadDetailAsync(string name)
    {
        if (_details.TryGetValue(name, out var cached))
        {
            return new DetailRead(cached, _rawText.GetValueOrDefault(name, string.Empty), string.Empty);
        }

        CliInvocation invocation;
        try
        {
            invocation = await RunReadAsync(_backend.ShowArgv(name)).ConfigureAwait(true);
        }
        catch (CliNotFoundException ex)
        {
            return new DetailRead(null, string.Empty, $"The defenseclaw CLI was not found. {ex.Message}");
        }

        if (invocation.FailureReason is { Length: > 0 } failure)
        {
            return new DetailRead(null, string.Empty, failure);
        }

        if (invocation.ExitCode != 0)
        {
            return new DetailRead(null, string.Empty, $"policy show failed: {Summarize(invocation)}");
        }

        var stdout = Stdout(invocation);
        if (!_backend.TryParseDetail(stdout, out var detail, out var error) || detail is null)
        {
            return new DetailRead(null, stdout, error);
        }

        _details[name] = detail;
        _rawText[name] = stdout;
        return new DetailRead(detail, stdout, string.Empty);
    }

    private readonly Dictionary<string, string> _rawText = new(StringComparer.Ordinal);

    // ---- Selection and Esc ---------------------------------------------------------------------------------------------

    [RelayCommand]
    private void ClearSelection() => SelectedRow = null;

    [RelayCommand]
    private void CopyName()
    {
        if (SelectedRow is { } row)
        {
            _ = Views.Controls.DcClipboard.TrySetText(row.Name);
        }
    }

    /// <summary>
    /// Esc closes the topmost transient surface: the review, then the form, then the output and notice, then the detail. True when it
    /// closed something.
    /// </summary>
    public bool HandleEscape()
    {
        if (Review.HandleEscape())
        {
            return true;
        }

        if (Form.IsOpen)
        {
            Form.Close();
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

    // ---- Helpers -------------------------------------------------------------------------------------------------------

    private static string Stdout(CliInvocation invocation) =>
        string.Join('\n', invocation.OutputLines.Where(l => l.Stream == CliStream.StandardOutput).Select(l => l.Text));

    /// <summary>The whole transcript, both streams in the order they arrived: what validate and test show.</summary>
    private static string Transcript(CliInvocation invocation) =>
        string.Join('\n', invocation.OutputLines.Select(l => l.Text)).TrimEnd();

    private static string Summarize(CliInvocation invocation)
    {
        if (invocation.FailureReason is { Length: > 0 } reason)
        {
            return reason;
        }

        var lines = invocation.OutputLines;
        var text = lines.Where(l => l.Stream == CliStream.StandardError && !string.IsNullOrWhiteSpace(l.Text)).Select(l => l.Text.Trim()).LastOrDefault()
                   ?? lines.Where(l => l.Stream == CliStream.StandardOutput && !string.IsNullOrWhiteSpace(l.Text)).Select(l => l.Text.Trim()).LastOrDefault();
        return text ?? $"Exit code {invocation.ExitCode?.ToString() ?? "unknown"}.";
    }

    private static void TraceFault(string what, Exception ex) => Trace.TraceError($"Policies panel: {what}: {ex}");
}
