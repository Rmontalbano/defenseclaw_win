using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Policy;
using DefenseClaw.Core.Policy.Model;
using DefenseClaw.Core.Runtime;

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
/// What each command does is in <see cref="IPolicyBackend"/>, so the newer runtime's Policies surface swaps in behind the same panel.
/// <para>
/// <b>Two surfaces, one panel (CUST-293).</b> When the connected runtime has the Mac's policy model
/// (<see cref="DefenseClaw.Core.Runtime.RuntimeCapability.PolicyModel"/>, from the runtime probe) the panel is <see cref="Model"/>, the
/// <see cref="PolicyModelViewModel"/>: posture per scope, opt-in packs, chains, rule families, named policies and rule packs. Everything
/// else - 0.8.10, a runtime that has not been probed, a probe that failed - is the table of named policies described above, unchanged: the
/// model is never offered on a guess. The choice is made when the panel is first used (after the first probe has answered), and again
/// whenever the probe's answer changes (an upgrade, the developer runtime selector) while the panel is on screen.
/// </para>
/// </summary>
public sealed partial class PoliciesPanelViewModel : PanelViewModelBase
{
    /// <summary>A read this old is read again when the panel comes back into view.</summary>
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(2);

    private const string ClassicDescription =
        "Security policies for skills, MCP servers and plugins: which are built in, which you made, and which one is active.";

    private readonly IPolicyBackend _backend;
    private readonly bool _surfaceFixed;
    private readonly object _surfaceGate = new();
    private bool _surfaceDecided;
    private readonly List<PolicyRow> _all = new();
    private readonly Dictionary<string, PolicyDetail> _details = new(StringComparer.Ordinal);
    private PolicyModelViewModel? _model;
    private IPolicyDataFiles? _dataFiles;
    private Func<IReadOnlyList<string>, CliRunOptions, Task<CliInvocation>>? _runCli;
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
        // A backend handed in (a test of one generation of the runtime) fixes the surface; otherwise the connected runtime's probe decides.
        _surfaceFixed = backend is not null;
        _backend = backend is { Surface: PolicySurface.NamedPolicies } ? backend : Release0810PolicyBackend.Instance;
        Trust = CatalogTrust.Watching(services.Paths);
        Review = new DiscoverActionReview(services) { RunGuard = ReasonToRefuseRun };
        Form = new PolicyFormViewModel();
        Rows = new ObservableCollection<PolicyRow>();
        ApplySurface(backend?.Surface ?? SurfaceOfRuntime());
    }

    public override string Title => "Policies";

    public override string Description => Model?.Description ?? ClassicDescription;

    /// <summary>
    /// The panel for a runtime that has the policy model; null on 0.8.10 and on a runtime that is not known to have it. While it is not
    /// null it is the whole panel (<see cref="UsesModel"/>) and the members below that belong to the table of named policies are idle.
    /// </summary>
    public PolicyModelViewModel? Model
    {
        get => _model;
        private set
        {
            if (SetProperty(ref _model, value))
            {
                OnPropertyChanged(nameof(UsesModel));
                OnPropertyChanged(nameof(UsesClassic));
                OnPropertyChanged(nameof(Description));
            }
        }
    }

    /// <summary>The connected runtime has the policy model: <see cref="Model"/> is the panel.</summary>
    public bool UsesModel => Model is not null;

    /// <summary>The table of named policies (0.8.10) is the panel.</summary>
    public bool UsesClassic => Model is null;

    public ObservableCollection<PolicyRow> Rows { get; }

    /// <summary>The confirm-and-run overlay every change goes through.</summary>
    public DiscoverActionReview Review { get; }

    /// <summary>The create / edit form (open: <see cref="PolicyFormViewModel.IsOpen"/>).</summary>
    public PolicyFormViewModel Form { get; }

    /// <summary>Whether the list may authorize a change; see <see cref="CatalogTrust"/>.</summary>
    public CatalogTrust Trust { get; internal set; }

    /// <summary>Test seam: the data files the policy model reads (<see cref="PolicyModelViewModel.DataFiles"/>); handed on to the model, now or when it is created.</summary>
    internal IPolicyDataFiles? DataFiles
    {
        get => _dataFiles;
        set
        {
            _dataFiles = value;
            if (value is not null && Model is { } model)
            {
                model.DataFiles = value;
            }
        }
    }

    /// <summary>
    /// Test seam: runs a command instead of <c>Services.Cli.RunAsync</c>, so a test feeds exact output and never starts a process. Handed on to
    /// the policy model when the panel is one (<see cref="Model"/>).
    /// </summary>
    internal Func<IReadOnlyList<string>, CliRunOptions, Task<CliInvocation>>? RunCli
    {
        get => _runCli;
        set
        {
            _runCli = value;
            if (Model is { } model)
            {
                model.RunCli = value;
            }
        }
    }

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
            if (Services.Installation.BlockedReason is { } installation)
            {
                return installation;
            }

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

    protected override void OnInstallationChanged() => NotifyTrust();

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

    public override async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        // Which surface this is depends on what the runtime is, and on the very first visit the app's own probe may not have answered yet:
        // wait for it (it is short, never throws, and is bounded by its own timeout). Until it has answered, and whenever it could not tell,
        // nothing newer than 0.8.10 is assumed. A service nothing started has nothing to wait for and answers at once.
        if (!_surfaceFixed)
        {
            try
            {
                _ = await Services.Runtime.WhenProbedAsync(cancellationToken).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                // The shell stopped waiting; whatever the probe found is applied below.
            }

            SyncSurface();
        }

        _surfaceDecided = true;
        if (Model is { } model)
        {
            await model.InitializeAsync(cancellationToken).ConfigureAwait(true);
            return;
        }

        await LoadAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// The app's first probe is still on its way, so what this panel is is not known yet: the first visit's <see cref="InitializeAsync"/> is waiting
    /// for it and will decide, and read. Until then nothing may be read - a table of named policies read from a runtime that turns out to have
    /// the model would be a read nobody asked for.
    /// </summary>
    private bool SurfacePending =>
        !_surfaceFixed && !_surfaceDecided && Services.Runtime.IsStarted && ReferenceEquals(Services.Runtime.Current, RuntimeSnapshot.NotProbed);

    protected override void OnActivated()
    {
        // Listen for a config reload while on screen (the table's trust is the one that matters; the model panel listens for its own).
        Services.ConfigReloaded += OnConfigReloaded;

        if (!_surfaceFixed)
        {
            Services.Runtime.Changed += OnRuntimeChanged;

            // An upgrade while the panel was away was not heard: ask again.
            SyncSurface();
        }

        if (SurfacePending)
        {
            return;
        }

        if (Model is { } model)
        {
            model.SetActive(true);
            return;
        }

        CatchUpClassic();
    }

    protected override void OnDeactivated()
    {
        Services.ConfigReloaded -= OnConfigReloaded;
        Services.Runtime.Changed -= OnRuntimeChanged;
        Model?.SetActive(false);
    }

    /// <summary>
    /// config.yaml or .env were rewritten (the existing watcher raises this for both). Whether that postdates the read on screen is the trust's
    /// to say; the rows stay either way. With the policy model as the surface this table is not on screen and has nothing to mark.
    /// </summary>
    private void OnConfigReloaded(object? sender, EventArgs e)
    {
        if (Model is null && Trust.CheckConfig())
        {
            NotifyTrust();
        }
    }

    private void CatchUpClassic()
    {
        // A change to config.yaml / .env while the panel was away was not heard: compare the files with what the rows were read under.
        _ = Trust.CheckConfig();

        // The bound values only update when something raises PropertyChanged: do it now, so an old list is not offered as fresh.
        NotifyTrust();

        // One catch-up read per visit, and only when the data is old or was read before the config changed. Not a timer.
        var stale = _lastLoadedAt is null || DefenseClaw.Core.Time.WallClock.Elapsed(_lastLoadedAt.Value) >= StaleAfter || Trust.IsStale;
        if (stale && !_loadRunning && !Review.IsOpen && !Form.IsOpen)
        {
            _ = LoadAsync();
        }
    }

    // ---- Which surface -------------------------------------------------------------------------------------------------

    private PolicySurface SurfaceOfRuntime() => PolicyBackends.For(Services.Runtime.Capabilities).Surface;

    private void SyncSurface()
    {
        if (!_surfaceFixed)
        {
            ApplySurface(SurfaceOfRuntime());
        }
    }

    /// <summary>
    /// Makes the panel the surface of <paramref name="surface"/>: creates the policy model when the runtime has it, drops it (and goes back to
    /// the table of named policies, read again if the panel is on screen) when it no longer does. Does nothing when it already is.
    /// </summary>
    private void ApplySurface(PolicySurface surface)
    {
        // Everything here runs on the UI thread in the app; the lock only keeps a second caller (a test, which runs the first visit off the UI
        // thread while the probe's notification is marshalled to it) from building a second model next to the first.
        lock (_surfaceGate)
        {
            if (surface == PolicySurface.SevenViewModel)
            {
                if (Model is not null)
                {
                    return;
                }

                var model = new PolicyModelViewModel(Services) { RunCli = _runCli };
                if (_dataFiles is not null)
                {
                    model.DataFiles = _dataFiles;
                }

                Model = model;
                if (IsActive)
                {
                    model.SetActive(true);
                }

                return;
            }

            if (Model is not { } gone)
            {
                return;
            }

            Model = null;
            gone.SetActive(false);
            gone.Dispose();

            // Whatever the table last read may predate what the model panel changed meanwhile: read it again, not when it next goes stale.
            _lastLoadedAt = null;
            if (IsActive)
            {
                CatchUpClassic();
            }
        }
    }

    private void OnRuntimeChanged(object? sender, EventArgs e)
    {
        // Raised on the UI thread when there is one; with none (a test) it is whichever thread finished the probe.
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            SyncSurface();
        }
        else if (!dispatcher.HasShutdownStarted)
        {
            _ = dispatcher.BeginInvoke(SyncSurface);
        }
    }

    [RelayCommand]
    private Task RefreshAsync() => Model is { } model ? model.RefreshCommand.ExecuteAsync(null) : LoadAsync();

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
        Trust.BeginRead();
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
        if (Model is { } model)
        {
            return model.HandleEscape();
        }

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
