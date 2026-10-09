using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Text;
using Wpf.Ui.Controls;

namespace DefenseClaw.App.ViewModels;

/// <summary>Where a Govern list is in its life. "Nothing found" and "could not look" are different states.</summary>
public enum GovernState
{
    /// <summary>First read in progress, nothing to show yet.</summary>
    Loading,

    /// <summary>At least one row.</summary>
    Loaded,

    /// <summary>The CLI answered and there is nothing of this kind (a normal state, not an error).</summary>
    Empty,

    /// <summary>The CLI was found but the read failed (non-zero exit, timeout, unparseable output).</summary>
    Error,

    /// <summary>The <c>defenseclaw</c> executable could not be found.</summary>
    CliUnavailable,
}

/// <summary>A state-changing command a Govern panel is about to ask the operator to confirm.</summary>
public sealed class GovernPlan
{
    public required string Heading { get; init; }

    /// <summary>Argument vector without the executable; the target comes last, after <c>--</c>.</summary>
    public required IReadOnlyList<string> Argv { get; init; }

    public string SuccessMessage { get; init; } = "Done.";

    /// <summary>A consequence worth stating next to the command; null hides it.</summary>
    public string? Note { get; init; }

    /// <summary>Raises the review tier (never lowers it): e.g. <c>plugin install --force</c> overwrites files.</summary>
    public CommandTier? MinimumTier { get; init; }

    /// <summary>Runs on the UI thread after the command exits 0 and before the list is re-read.</summary>
    public Action? OnSuccess { get; init; }

    /// <summary>A ceiling for a command that legitimately runs long (a scan); null keeps the runner's default.</summary>
    public TimeSpan? Timeout { get; init; }
}

/// <summary>
/// Shared machinery of the four Govern panels (Skills, MCPs, Plugins, Tools).
/// <para>
/// <b>Data.</b> Every list is one read-only <c>defenseclaw &lt;noun&gt; list --json [--connector C]</c> run — the REST
/// <c>/skills</c>, <c>/mcps</c> and <c>/tools/catalog</c> answer "not connected" on a standalone install, and the CLI
/// is what the operator would run anyway. One run per activation (when the data is older than
/// <see cref="StaleAfter"/>) and per Refresh or scope change; never on a timer. A failed refresh keeps the last good
/// rows, labelled with their age.
/// </para>
/// <para>
/// <b>Mutations.</b> Every row action builds an exact argv, classifies it with
/// <see cref="CommandTiers"/> and shows it in a confirm overlay first (destructive verbs are marked and get a
/// danger-styled button). The target is always the last argument, after <c>--</c>, so a name that starts with a
/// dash can never be read as an option — and the leading verb path stays the only thing the tier depends on.
/// The list is re-read only after the command exited 0.
/// </para>
/// </summary>
public abstract partial class GovernPanelViewModelBase : PanelViewModelBase, IGovernRowHost
{
    protected const string AllConnectorsLabel = "All configured connectors";
    protected const string StatusAll = "All";

    /// <summary>Activation re-reads the list only when the last read is at least this old.</summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(60);

    private static readonly string[] DefaultStatusFilters = { StatusAll, "Blocked", "Allowed", "Quarantined", "Disabled", "Needs attention" };

    private readonly List<GovernRow> _allRows = new();
    private readonly List<GovernRow> _artifacts = new();
    private bool _loadRunning;
    private bool _reloadRequested;
    private bool _everRequested;
    private bool _scopeReloadEnabled;
    private DateTimeOffset? _lastLoadedAt;
    private string? _loadedScopeKey;
    private GovernPlan? _pendingPlan;
    private bool _scannerProbeStarted;

    /// <summary>Set once the scanner lookup has answered; null until then. See <see cref="ScannerExecutable"/>.</summary>
    private bool? _scannerFound;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyPropertyChangedFor(nameof(CanScan))]
    private bool _isBusy;

    [ObservableProperty] private string _filterText = string.Empty;
    [ObservableProperty] private string _selectedStatusFilter = StatusAll;
    [ObservableProperty] private string? _selectedConnector;
    [ObservableProperty] private string _reason = string.Empty;

    [ObservableProperty] private GovernState _state = GovernState.Loading;
    [ObservableProperty] private string _errorTitle = string.Empty;
    [ObservableProperty] private string _errorMessage = string.Empty;
    [ObservableProperty] private string? _refreshWarning;

    [ObservableProperty] private bool _isConfirmOpen;

    /// <summary>
    /// What the confirm overlay shows. Set before <see cref="IsConfirmOpen"/> flips, and kept after the overlay
    /// closes (the overlay is collapsed by then).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConfirmHeading))]
    [NotifyPropertyChangedFor(nameof(ConfirmNote))]
    [NotifyPropertyChangedFor(nameof(ConfirmCommandText))]
    [NotifyPropertyChangedFor(nameof(ConfirmTierText))]
    [NotifyPropertyChangedFor(nameof(IsConfirmDestructive))]
    private CommandReview? _confirmReview;

    [ObservableProperty] private bool _isResultOpen;
    [ObservableProperty] private string _resultTitle = string.Empty;
    [ObservableProperty] private string _resultMessage = string.Empty;
    [ObservableProperty] private InfoBarSeverity _resultSeverity = InfoBarSeverity.Informational;

    [ObservableProperty] private bool _isOutputOpen;
    [ObservableProperty] private string _outputTitle = string.Empty;
    [ObservableProperty] private string _outputText = string.Empty;

    protected GovernPanelViewModelBase(AppServices services)
        : base(services)
    {
        Trust = CatalogTrust.Watching(services.Paths);

        Rows.CollectionChanged += (_, _) => NotifyStateFlags();
        ArtifactRows.CollectionChanged += (_, _) => NotifyStateFlags();

        // The rows' Scan menu item follows the panel's CanScan (idle, scanner present).
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CanScan))
            {
                foreach (var row in _allRows.Concat(Rows))
                {
                    row.RefreshScanEnabled();
                }
            }
        };
    }

    bool IGovernRowHost.IsScanAvailable => CanScan;

    bool IGovernRowHost.AreChangesAllowed => IsDataTrusted;

    string? IGovernRowHost.ChangesBlockedReason => DataUntrustedReason;

    // ---- Catalog safety: may this list authorize a change? -------------------------------------------------------------

    /// <summary>
    /// What the last read amounted to (complete, partial, failed, old, or read before config.yaml / .env changed). Every state-changing
    /// command of the panel asks it, at the moment it is reviewed and again when it is confirmed. Reusable as is by any panel with a list that
    /// actions are taken on (Policies).
    /// </summary>
    public CatalogTrust Trust { get; internal set; }

    /// <summary>
    /// True when the list is a complete, recent read of an installation that may be changed; false while it is partial, failed, being read for
    /// the first time or old, and for a read-only (managed or invalid) installation.
    /// </summary>
    public bool IsDataTrusted => Trust.IsTrusted && Services.Installation.IsMutable;

    /// <summary>Why <see cref="IsDataTrusted"/> is false, as a tooltip sentence; null when it is true.</summary>
    public string? DataUntrustedReason => Services.Installation.BlockedReason ?? Trust.Reason;

    /// <summary>The installation turned read-only (or writable) while the panel was open: every control that follows the trust is drawn again.</summary>
    protected override void OnInstallationChanged() => NotifyTrust();

    /// <summary>What the toolbar's state-changing buttons bind to: nothing running and the list is trusted.</summary>
    public bool CanChange => IsIdle && IsDataTrusted;

    /// <summary>The bulk Scan button: <see cref="CanScan"/> (idle, scanner present) and a trusted list.</summary>
    public bool CanScanAll => CanScan && IsDataTrusted;

    /// <summary>True when the last read listed rows but not every source; <see cref="PartialDiscoveryMessage"/> says which.</summary>
    public bool HasPartialDiscovery => Trust.IsPartial;

    /// <summary>The banner text of a partial read: what it means for the rows, then the CLI's own diagnostics (one per line).</summary>
    public string PartialDiscoveryMessage => Trust.IsPartial
        ? "These are the entries the CLI could read; others may be missing. Changes are off until discovery completes.\n"
          + string.Join('\n', Trust.PartialDiagnostics.Select(DisplayNames.Visible))
        : string.Empty;

    /// <summary>What the trust looked like when the rows were last told about it; they are re-notified only when it changes.</summary>
    private (bool Trusted, string? Reason) _trustSeen = (true, null);

    private void NotifyTrust()
    {
        OnPropertyChanged(nameof(IsDataTrusted));
        OnPropertyChanged(nameof(DataUntrustedReason));
        OnPropertyChanged(nameof(CanChange));
        OnPropertyChanged(nameof(CanScanAll));
        OnPropertyChanged(nameof(HasPartialDiscovery));
        OnPropertyChanged(nameof(PartialDiscoveryMessage));

        var now = (IsDataTrusted, DataUntrustedReason);
        if (now != _trustSeen)
        {
            _trustSeen = now;
            foreach (var row in _allRows.Concat(Rows).Concat(ArtifactRows))
            {
                row.RefreshChangesEnabled();
            }
        }
    }

    /// <summary>
    /// Refuses (with the reason in the result bar) when the list may not authorize a change; true when the caller must stop. The check is
    /// made against the clock and the config files now (<see cref="CatalogTrust.ReasonNow"/>), not against what a button looked like when it
    /// was drawn or what the watcher has reported so far. For a <paramref name="confirmed"/> plan - a review the operator has just said yes
    /// to - the command that was therefore not started is also recorded in Activity, as refused.
    /// </summary>
    private bool RefuseUntrustedChange(GovernPlan? confirmed = null)
    {
        // A read-only installation (managed or invalid) comes first: whatever the list says, nothing can be changed, and the operator is told
        // that one reason rather than a stale-data one that a refresh would not cure.
        if ((Services.Installation.BlockedReason ?? Trust.ReasonNow()) is not { } reason)
        {
            return false;
        }

        ShowResult("Changes are off", reason, InfoBarSeverity.Warning);
        if (confirmed is not null)
        {
            _ = DiscoverCli.RecordRefusal(Services, CommandReview.DefaultExecutable, confirmed.Argv, reason);
        }

        NotifyTrust();
        return true;
    }

    // ---- Config changes (CUST-312) -------------------------------------------------------------------------------------

    /// <summary>
    /// config.yaml or .env were rewritten (the existing watcher raises this for both). Whether that postdates the read on screen is the trust's
    /// to say (a panel that re-read straight after its own change has nothing to be told); the rows stay either way.
    /// </summary>
    private void OnConfigReloaded(object? sender, EventArgs e) => NoteConfigMoved();

    /// <summary>Compares the files with what the rows were read under; when they differ the rows lose their say, with the reason as the tooltip.</summary>
    private void NoteConfigMoved()
    {
        if (Trust.CheckConfig())
        {
            NotifyTrust();
        }
    }

    /// <summary>Test seam: runs a command instead of <c>Services.Cli.RunAsync</c>, so a test feeds exact output and never starts a process.</summary>
    internal Func<IReadOnlyList<string>, CliRunOptions, Task<CliInvocation>>? RunCli { get; set; }

    private Task<CliInvocation> RunCliAsync(IReadOnlyList<string> argv, CliRunOptions options) =>
        RunCli is { } run ? run(argv, options) : Services.Cli.RunAsync(argv, options: options);

    // ---- What the concrete panel says about itself ---------------------------------------------------------------

    /// <summary>The CLI group: <c>skill</c>, <c>mcp</c>, <c>plugin</c> or <c>tool</c>.</summary>
    protected abstract string Noun { get; }

    /// <summary>The noun in a sentence: "skill", "MCP server", "plugin", "tool rule".</summary>
    protected abstract string NounLabel { get; }

    /// <summary>Plural for counts and empty states: "skills", "MCP servers", …</summary>
    protected abstract string NounPlural { get; }

    /// <summary>The key of the item array inside a per-connector group object of <c>list --json</c>.</summary>
    protected abstract string ItemsKey { get; }

    /// <summary>The read-only detail verb: <c>info</c> (skills, plugins) or <c>status</c> (tools).</summary>
    protected virtual string InfoVerb => "info";

    /// <summary>Turns one JSON item into a row; null skips it.</summary>
    protected abstract GovernRow? ParseRow(JsonElement item, string? groupConnector);

    protected abstract string BuildEmptyTitle(string scope);

    protected abstract string BuildEmptyDetail(string scope);

    protected virtual IReadOnlyList<string> StatusFilterChoices => DefaultStatusFilters;

    /// <summary>
    /// The scanner executable <c>&lt;noun&gt; scan</c> shells out to (<c>skill-scanner</c>, <c>mcp-scanner</c>); null when this
    /// panel has none to look for. The scanner is looked up once per visit, off the UI thread, and a scan is only refused
    /// once the lookup has answered "not installed" - until then the button works, as the Overview's Scanners box does.
    /// </summary>
    protected virtual string? ScannerExecutable => null;

    /// <summary>The bulk scan this panel offers (<c>skill scan --all</c>); null when there is none.</summary>
    protected virtual IReadOnlyList<string>? ScanAllArgv => null;

    // ---- Bindable surface ----------------------------------------------------------------------------------------

    /// <summary>The rows the filter lets through (real items only).</summary>
    public ObservableCollection<GovernRow> Rows { get; } = new();

    /// <summary>Entries the CLI listed that are not real items of this kind (see the Plugins panel).</summary>
    public ObservableCollection<GovernRow> ArtifactRows { get; } = new();

    public ObservableCollection<string> Connectors { get; } = new();

    public IReadOnlyList<string> StatusFilters => StatusFilterChoices;

    public bool IsIdle => !IsBusy;

    /// <summary>The review's fields, flat, for callers that only need one of them.</summary>
    public string ConfirmHeading => ConfirmReview?.Title ?? string.Empty;

    public string ConfirmNote => ConfirmReview?.Summary ?? string.Empty;

    public string ConfirmCommandText => ConfirmReview?.CommandText ?? string.Empty;

    public string ConfirmTierText => ConfirmReview?.TierLabel ?? string.Empty;

    public bool IsConfirmDestructive => ConfirmReview?.IsDestructive ?? false;

    public bool ShowLoading => State == GovernState.Loading;

    public bool ShowList => State == GovernState.Loaded && Rows.Count > 0;

    /// <summary>Rows exist but the filter hides every one.</summary>
    public bool ShowNoMatch => State == GovernState.Loaded && Rows.Count == 0;

    public bool ShowEmpty => State == GovernState.Empty;

    public bool ShowError => State == GovernState.Error;

    public bool ShowCliMissing => State == GovernState.CliUnavailable;

    public bool HasRefreshWarning => !string.IsNullOrEmpty(RefreshWarning);

    public bool HasArtifacts => ArtifactRows.Count > 0;

    /// <summary>False while a command runs or when the scanner is known to be missing; what the Scan buttons bind to.</summary>
    public bool CanScan => IsIdle && ScanUnavailableReason is null;

    /// <summary>Why Scan cannot run (the scanner is not installed); null when it can. Shown inline, not only in a tooltip.</summary>
    public string? ScanUnavailableReason =>
        ScannerExecutable is { } exe && _scannerFound == false
            ? $"Scan is unavailable: {exe} was not found on this machine. Install it and press Refresh."
            : null;

    public bool HasScanUnavailableReason => ScanUnavailableReason is not null;

    public bool HasScanAll => ScanAllArgv is not null;

    /// <summary>"2 entries the CLI lists that are not plugins" — heads the collapsed artifact section.</summary>
    public string ArtifactHeader => $"{ArtifactRows.Count} listing artifact{(ArtifactRows.Count == 1 ? string.Empty : "s")} (not {NounPlural})";

    public string ScopeLabel => ToolbarConnector() ?? ConfiguredConnectorsText();

    public string EmptyTitle => BuildEmptyTitle(ScopeLabel);

    public string EmptyDetail => BuildEmptyDetail(ScopeLabel);

    public string CountText
    {
        get
        {
            if (State is not (GovernState.Loaded or GovernState.Empty))
            {
                return string.Empty;
            }

            var filtered = FilterText.Trim().Length > 0 || SelectedStatusFilter != StatusAll;
            return filtered
                ? $"Showing {Rows.Count} of {_allRows.Count} {NounPlural}"
                : $"{_allRows.Count} {NounPlural}";
        }
    }

    /// <summary>The header caption: what the numbers are as of, or that a read is in flight.</summary>
    public string HeaderStatusText =>
        IsBusy && _loadRunning ? "Reading…"
        : _lastLoadedAt is { } at ? $"as of {at.LocalDateTime:HH:mm}"
        : string.Empty;

    /// <summary>"12 skills · Read with 'defenseclaw skill list --json'": the count, and where the numbers come from.</summary>
    public string CaptionText
    {
        get
        {
            var source = $"Read with 'defenseclaw {Noun} list --json'";
            return CountText.Length == 0 ? source : CountText + " · " + source;
        }
    }

    // ---- Lifecycle -----------------------------------------------------------------------------------------------

    public override async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        RefreshConnectorList();
        StartScannerProbe();
        await LoadAsync().ConfigureAwait(true);
    }

    protected override void OnActivated()
    {
        // Listen while on screen; a change made while the panel was away is found by comparing the files with what the rows were read under.
        Services.ConfigReloaded += OnConfigReloaded;
        NoteConfigMoved();

        RefreshConnectorList();
        StartScannerProbe();

        // One catch-up read per visit, and only when the data is old (or never arrived, or read before the config changed). Not a timer.
        var stale = _lastLoadedAt is null
                    || DefenseClaw.Core.Time.WallClock.Elapsed(_lastLoadedAt.Value) >= StaleAfter
                    || !string.Equals(_loadedScopeKey, ScopeKey(), StringComparison.Ordinal)
                    || Trust.IsStale;
        if (!_everRequested || stale)
        {
            _ = LoadAsync();
        }
    }

    protected override void OnDeactivated() => Services.ConfigReloaded -= OnConfigReloaded;

    /// <summary>Re-read the list (Refresh button, F5). Also looks for the scanner again, so installing it needs no restart.</summary>
    [RelayCommand]
    private Task RefreshAsync()
    {
        _scannerProbeStarted = false;
        StartScannerProbe();
        return LoadAsync();
    }

    /// <summary>
    /// Looks for <see cref="ScannerExecutable"/> without waiting for it on the UI thread (a dead PATH entry stalls a lookup for
    /// tens of seconds). A cached answer is used at once; otherwise the lookup runs on the pool and the answer is applied back here.
    /// </summary>
    private void StartScannerProbe()
    {
        if (ScannerExecutable is not { } exe || _scannerProbeStarted)
        {
            return;
        }

        _scannerProbeStarted = true;
        if (ScannerFinder is null && Services.Paths.TryGetKnownExecutable(exe, out var known))
        {
            SetScannerFound(known is not null);
            return;
        }

        _ = ProbeScannerAsync(exe);
    }

    private async Task ProbeScannerAsync(string exe)
    {
        try
        {
            var found = await (ScannerFinder?.Invoke(exe) ?? Services.Paths.FindExecutableAsync(exe)).ConfigureAwait(true);
            SetScannerFound(found is not null);
        }
        catch (Exception)
        {
            // An unanswered lookup leaves Scan enabled; the command itself says what is wrong if it cannot run.
            _scannerProbeStarted = false;
        }
    }

    /// <summary>Test seam: answers "where is this scanner" instead of the real PATH lookup, so a test does not depend on the machine.</summary>
    internal Func<string, Task<string?>>? ScannerFinder { get; set; }

    private void SetScannerFound(bool found)
    {
        _scannerFound = found;
        OnPropertyChanged(nameof(CanScan));
        OnPropertyChanged(nameof(CanScanAll));
        OnPropertyChanged(nameof(ScanUnavailableReason));
        OnPropertyChanged(nameof(HasScanUnavailableReason));
    }

    [RelayCommand]
    private void ClearFilter()
    {
        FilterText = string.Empty;
        SelectedStatusFilter = StatusAll;
    }

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    partial void OnSelectedStatusFilterChanged(string value) => ApplyFilter();

    partial void OnStateChanged(GovernState value) => NotifyStateFlags();

    partial void OnIsBusyChanged(bool value) => NotifyStateFlags();

    partial void OnRefreshWarningChanged(string? value) => OnPropertyChanged(nameof(HasRefreshWarning));

    partial void OnSelectedConnectorChanged(string? value)
    {
        // The scope combo and the shared connector scope (the toolbar chip) are one choice: picking here narrows the whole app. Not while the
        // list is being rebuilt (_scopeReloadEnabled is off) or while following the shared scope (_followingScope).
        if (_scopeReloadEnabled && !_followingScope && value is not null)
        {
            _ = Services.ConnectorScope.Set(ToolbarConnector());
        }

        OnPropertyChanged(nameof(ScopeLabel));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyDetail));

        // The scope decides what the CLI is asked, so changing it re-reads (one run per change).
        if (_scopeReloadEnabled && value is not null && !string.Equals(_loadedScopeKey, ScopeKey(), StringComparison.Ordinal))
        {
            _ = LoadAsync(queueIfBusy: true);
        }
    }

    // ---- Reading -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Reads the list. A request that arrives while a read is running is dropped (activation and Refresh: the
    /// running read is about to deliver the same data), or queued when <paramref name="queueIfBusy"/> is set (a
    /// scope change: the running read asked for the wrong scope).
    /// </summary>
    protected async Task LoadAsync(bool queueIfBusy = false)
    {
        _everRequested = true;
        Trust.MarkPending();

        if (_loadRunning)
        {
            _reloadRequested |= queueIfBusy;
            return;
        }

        _loadRunning = true;
        try
        {
            do
            {
                _reloadRequested = false;
                IsBusy = true;
                NotifyStateFlags();
                await LoadOnceAsync().ConfigureAwait(true);
            }
            while (_reloadRequested);
        }
        catch (Exception ex)
        {
            // Nothing here may escape: this runs fire-and-forget on activation.
            FailLoad(GovernState.Error, $"Could not read the {NounPlural}", ex.Message);
        }
        finally
        {
            _loadRunning = false;
            IsBusy = false;
            NotifyStateFlags();
        }
    }

    private async Task LoadOnceAsync()
    {
        // What config.yaml and .env look like as this read starts: the rows it produces were read under that.
        Trust.BeginRead();

        var scope = ToolbarConnector();
        var scopeKey = ScopeKey();
        var argv = new List<string> { Noun, "list", "--json" };
        if (scope is not null)
        {
            argv.Add("--connector");
            argv.Add(scope);
        }

        CliInvocation invocation;
        try
        {
            // Machine-parsed: JsonRead lifts the per-invocation retention cap so a long list can't be
            // truncated at the head and fail to parse.
            invocation = await RunCliAsync(argv, CliRunOptions.JsonRead).ConfigureAwait(true);
        }
        catch (CliNotFoundException ex)
        {
            FailLoad(GovernState.CliUnavailable, "The defenseclaw CLI was not found", $"{NounPlural} cannot be listed without it. {ex.Message}", scopeKey);
            return;
        }

        var command = "defenseclaw " + string.Join(' ', argv);
        if (invocation.FailureReason is { } failure)
        {
            // FailureReason also carries "timed out" and "cancelled", where the process did start.
            FailLoad(GovernState.Error, $"'{command}' did not complete", failure, scopeKey);
            return;
        }

        // Exit != 0 with every readable row on stdout as valid JSON and a known "could not read this source" line on stderr is a
        // PARTIAL read (the rows are real, the list is not complete): shown with a warning, changes off. Any other non-zero exit
        // - garbage output, an unknown stderr, a timeout - is a failure, as before.
        CatalogPartialRead? partial = null;
        if (invocation.ExitCode != 0 && !CatalogPartialReads.TryClassify(invocation, out partial))
        {
            FailLoad(GovernState.Error, $"'{command}' failed", Summarize(invocation), scopeKey);
            return;
        }

        // A transcript keeps only its newest lines. For JSON that means the opening brackets are gone and the
        // rest cannot be parsed, so say why instead of reporting "unexpected output".
        if (invocation.IsOutputTruncated)
        {
            FailLoad(
                GovernState.Error,
                $"The {NounLabel} list is too large to read here",
                $"'{command}' printed more than the {CliInvocation.MaxRetainedOutputLines}-line / {CliInvocation.MaxRetainedOutputBytes / 1024} KiB " +
                "of output this app keeps for one command, so the start of the JSON was dropped. Narrow the list with Connector scope, or run the command in a terminal.",
                scopeKey);
            return;
        }

        var stdout = partial?.Stdout ?? string.Concat(invocation.OutputLines
            .Where(l => l.Stream == CliStream.StandardOutput)
            .Select(l => l.Text + "\n"));

        List<GovernRow> rows;
        try
        {
            rows = ParseRows(stdout);
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            FailLoad(GovernState.Error, $"Unexpected output from '{command}'", ex.Message, scopeKey);
            return;
        }

        // A partial read with nothing in it says nothing about what is installed: not "no entries", an incomplete look.
        if (partial is not null && rows.Count == 0)
        {
            FailLoad(
                GovernState.Error,
                "Discovery was incomplete",
                $"'{command}' could not read every source and found no {NounPlural} in the rest. " + string.Join(' ', partial.Diagnostics.Select(DisplayNames.Visible)),
                scopeKey);
            return;
        }

        _allRows.Clear();
        _allRows.AddRange(rows.Where(r => !r.IsArtifact));
        _artifacts.Clear();
        _artifacts.AddRange(rows.Where(r => r.IsArtifact));

        _lastLoadedAt = DateTimeOffset.Now;
        _loadedScopeKey = scopeKey;
        if (partial is not null)
        {
            Trust.MarkPartial(partial.Diagnostics);
        }
        else
        {
            Trust.MarkComplete();
        }

        RefreshWarning = null;
        ErrorTitle = string.Empty;
        ErrorMessage = string.Empty;
        State = _allRows.Count == 0 ? GovernState.Empty : GovernState.Loaded;
        ApplyFilter();
        NotifyStateFlags();
    }

    /// <summary>
    /// Keeps the last good rows (labelled with their age) when a refresh of the same scope fails, and
    /// switches to the error state when there is nothing trustworthy to keep.
    /// </summary>
    private void FailLoad(GovernState failedState, string title, string message, string? scopeKey = null)
    {
        // Whatever rows stay on screen no longer authorize a change: the list could not be confirmed.
        Trust.MarkFailed($"{title}: {message}");
        NotifyTrust();

        var haveGoodData = _lastLoadedAt is not null
                           && (scopeKey is null || string.Equals(_loadedScopeKey, scopeKey, StringComparison.Ordinal));
        if (haveGoodData)
        {
            var age = _lastLoadedAt is { } at ? $"as of {at.LocalDateTime:HH:mm}" : "earlier";
            RefreshWarning = $"{title}: {message} Showing the last good read ({age}); changes are off until the list is read again.";
            return;
        }

        _allRows.Clear();
        _artifacts.Clear();
        _lastLoadedAt = null;
        _loadedScopeKey = null;
        ErrorTitle = title;
        ErrorMessage = message;
        State = failedState;
        ApplyFilter();
    }

    /// <summary>
    /// Turns the stdout of <c>list --json</c> into rows: whichever list shape it is (see
    /// <see cref="GovernJson.Flatten"/>), one row per item the concrete panel accepts, the first of any
    /// repeated <see cref="GovernRow.Key"/> kept. Split out of <see cref="LoadOnceAsync"/> unchanged so the
    /// parsing can be exercised without running the CLI.
    /// </summary>
    /// <exception cref="JsonException">The text is not JSON.</exception>
    /// <exception cref="FormatException">The text is empty, or JSON of a shape no list has.</exception>
    internal List<GovernRow> ParseRows(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
        {
            throw new FormatException("The command printed nothing.");
        }

        // With no connector configured the CLI prints a sentence (exit 0), not JSON; say what it means.
        if (GovernJson.IsNoConnectorMessage(stdout))
        {
            throw new FormatException(
                $"No connector is configured, so there is nothing to list. The CLI said: {stdout.Trim()}");
        }

        var rows = new List<GovernRow>();
        using var document = JsonDocument.Parse(stdout);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (item, groupConnector) in GovernJson.Flatten(document.RootElement, ItemsKey))
        {
            var row = ParseRow(item, groupConnector);
            if (row is not null && seen.Add(row.Key))
            {
                rows.Add(row);
            }
        }

        return rows;
    }

    private string ScopeKey() => ToolbarConnector() ?? "*";

    // ---- Filtering -----------------------------------------------------------------------------------------------

    protected void ApplyFilter()
    {
        var text = FilterText.Trim().ToLowerInvariant();
        var desired = _allRows.Where(r => Matches(r, text)).ToList();
        SyncCollection(Rows, desired, r => r.Key, static (a, b) => a.RawJson == b.RawJson && a.Verbs == b.Verbs);
        SyncCollection(ArtifactRows, _artifacts, r => r.Key, static (a, b) => a.RawJson == b.RawJson && a.Verbs == b.Verbs);
        NotifyStateFlags();
    }

    private bool Matches(GovernRow row, string text)
    {
        var statusOk = SelectedStatusFilter switch
        {
            "Blocked" => row.IsBlocked,
            "Allowed" => row.IsAllowed,
            "Quarantined" => row.IsQuarantined,
            "Disabled" => row.IsDisabled,
            "Needs attention" => row.NeedsAttention,
            _ => true,
        };

        return statusOk && (text.Length == 0 || row.SearchText.Contains(text, StringComparison.Ordinal));
    }

    private void NotifyStateFlags()
    {
        OnPropertyChanged(nameof(ShowLoading));
        OnPropertyChanged(nameof(ShowList));
        OnPropertyChanged(nameof(ShowNoMatch));
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(ShowError));
        OnPropertyChanged(nameof(ShowCliMissing));
        OnPropertyChanged(nameof(HasArtifacts));
        OnPropertyChanged(nameof(ArtifactHeader));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(CaptionText));
        OnPropertyChanged(nameof(HeaderStatusText));
        OnPropertyChanged(nameof(ScopeLabel));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyDetail));
        NotifyTrust();
    }

    // ---- Connector scope -----------------------------------------------------------------------------------------

    private bool _followingScope;

    /// <summary>
    /// The shared connector scope changed (chip, Ctrl+Shift+M, an Overview row): the combo follows it, which re-reads the list for that
    /// connector (or for all of them) like a pick in the combo does. The scope is the one source of truth; with one connector it refuses
    /// to narrow and the combo keeps its own choice.
    /// </summary>
    protected override void OnConnectorScopeChanged()
    {
        var scope = Services.ConnectorScope.Current;
        string wanted;
        if (scope is null)
        {
            wanted = AllConnectorsLabel;
        }
        else
        {
            wanted = Connectors.FirstOrDefault(c => string.Equals(c, scope, StringComparison.OrdinalIgnoreCase)) ?? scope;
            if (!Connectors.Contains(wanted))
            {
                // A live connector config.yaml does not name is still one the operator can scope to; the list keeps it while it is the scope.
                Connectors.Add(wanted);
            }
        }

        _followingScope = true;
        try
        {
            SelectedConnector = wanted;
        }
        finally
        {
            _followingScope = false;
        }
    }

    /// <summary>The toolbar's connector, or null for "All configured connectors" / nothing chosen.</summary>
    protected string? ToolbarConnector() =>
        !string.IsNullOrWhiteSpace(SelectedConnector) && !string.Equals(SelectedConnector, AllConnectorsLabel, StringComparison.Ordinal)
            ? SelectedConnector
            : null;

    /// <summary>The row's own connector when it has one (the operator clicked that connector's item), else the toolbar's, else null (all).</summary>
    protected string? EffectiveConnector(string? rowConnector) =>
        string.IsNullOrWhiteSpace(rowConnector) ? ToolbarConnector() : rowConnector.Trim();

    /// <summary>
    /// The connector to record on a row: what the item says, else the group it was listed under, else the only
    /// configured connector (a single-connector install lists bare items), else null.
    /// </summary>
    protected string? ResolveConnector(string? fromItem, string? fromGroup)
    {
        if (!string.IsNullOrWhiteSpace(fromItem))
        {
            return fromItem.Trim();
        }

        if (!string.IsNullOrWhiteSpace(fromGroup))
        {
            return fromGroup.Trim();
        }

        return Connectors.Count == 2 ? Connectors[1] : null;
    }

    protected static string ScopeText(string? connector) =>
        connector is null ? "ALL configured connectors" : $"connector “{connector}”";

    private string ConfiguredConnectorsText()
    {
        var names = Connectors.Skip(1).ToArray();
        return names.Length == 0 ? "the configured connector" : string.Join(", ", names);
    }

    /// <summary>Fills the scope combo from config.yaml without clearing it (a cleared items source nulls the selection).</summary>
    private void RefreshConnectorList()
    {
        var desired = new List<string> { AllConnectorsLabel };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var config = Services.Config.Config;

        void Add(string? name)
        {
            if (!string.IsNullOrWhiteSpace(name) && seen.Add(name.Trim()))
            {
                desired.Add(name.Trim());
            }
        }

        Add(config.Claw.Mode);
        Add(config.Guardrail.Connector);
        foreach (var key in config.Guardrail.Connectors.Keys)
        {
            Add(key);
        }

        Add(Services.ConnectorScope.Current);

        SyncCollection(Connectors, desired, s => s, static (_, _) => true);

        _scopeReloadEnabled = false;
        if (string.IsNullOrWhiteSpace(SelectedConnector) || !Connectors.Contains(SelectedConnector))
        {
            SelectedConnector = AllConnectorsLabel;
        }

        _scopeReloadEnabled = true;
        NotifyStateFlags();
    }

    // ---- Row actions ---------------------------------------------------------------------------------------------

    void IGovernRowHost.OnRowAction(GovernRow row, GovernVerbs verb)
    {
        if (verb == GovernVerbs.CopyName)
        {
            CopyToClipboard(row.Name);
            return;
        }

        if (IsBusy)
        {
            ShowResult("Please wait", "Another command is still running. Try again when it finishes.", InfoBarSeverity.Informational);
            return;
        }

        if (verb == GovernVerbs.Info)
        {
            _ = ShowInfoAsync(row);
            return;
        }

        if (verb == GovernVerbs.Scan && ScanUnavailableReason is { } unavailable)
        {
            ShowResult("Scan unavailable", unavailable, InfoBarSeverity.Warning);
            return;
        }

        if (PlanFor(row, verb) is { } plan)
        {
            BeginReview(plan);
        }
    }

    /// <summary>
    /// Scan every item (<c>skill scan --all</c>, the Overview's Scan Skills): the scanner runs over all of them and records
    /// results and an audit event, so it is reviewed like any other command. Scoped to the toolbar's connector when one is chosen.
    /// </summary>
    [RelayCommand]
    private void ScanAll()
    {
        if (ScanAllArgv is not { } baseArgv || IsBusy)
        {
            return;
        }

        if (ScanUnavailableReason is { } unavailable)
        {
            ShowResult("Scan unavailable", unavailable, InfoBarSeverity.Warning);
            return;
        }

        var argv = new List<string>(baseArgv);
        var connector = ToolbarConnector();
        if (connector is not null)
        {
            argv.Add("--connector");
            argv.Add(connector);
        }

        BeginReview(new GovernPlan
        {
            Heading = $"Scan all {NounPlural} for {ScopeText(connector)}?",
            Argv = argv,
            Note = ScanNote(NounPlural),
            SuccessMessage = $"Scanned all {NounPlural}.",
            Timeout = CliRunner.ExtendedTimeout,
        });
    }

    private string ScanNote(string what) =>
        $"Runs the {NounLabel} scanner over {what} and records the results and an audit event. It changes scan results and findings; it does not change any {NounLabel}. A scan can take minutes.";

    /// <summary>Builds the reviewed command for a mutating row verb; null when the verb does not apply.</summary>
    protected virtual GovernPlan? PlanFor(GovernRow row, GovernVerbs verb)
    {
        var word = VerbWord(verb);
        if (word is null)
        {
            return null;
        }

        // 'skill scan all' means every skill, not a skill called "all": there is no way to scan that one by name.
        if (verb == GovernVerbs.Scan && string.Equals(row.Name, "all", StringComparison.OrdinalIgnoreCase))
        {
            ShowResult("Scan not offered", $"A {NounLabel} called “all” cannot be scanned by name: the CLI reads that name as every {NounLabel}. Use Scan all.", InfoBarSeverity.Warning);
            return null;
        }

        var options = new List<string>();
        if (UsesReason(verb) && !string.IsNullOrWhiteSpace(Reason))
        {
            options.Add("--reason");
            options.Add(Reason.Trim());
        }

        var scope = AppendRowScope(options, row);
        var name = row.Name;
        return new GovernPlan
        {
            Heading = HeadingFor(verb, row, scope),
            Argv = BuildArgv(Noun, word, options, name),
            Note = NoteFor(verb, row),
            SuccessMessage = SuccessFor(verb, row),
            Timeout = verb == GovernVerbs.Scan ? CliRunner.ExtendedTimeout : null,
        };
    }

    /// <summary>Adds the scope flag(s) for a row-level command and returns the words the heading uses for it.</summary>
    protected virtual string AppendRowScope(List<string> options, GovernRow row)
    {
        var connector = EffectiveConnector(row.Connector);
        if (connector is not null)
        {
            options.Add("--connector");
            options.Add(connector);
        }

        return ScopeText(connector);
    }

    protected virtual string HeadingFor(GovernVerbs verb, GovernRow row, string scope)
    {
        var name = row.Name;
        return verb switch
        {
            GovernVerbs.Block => $"Block {NounLabel} “{name}” for {scope}?",
            GovernVerbs.Allow => $"Allow {NounLabel} “{name}” for {scope}?",
            GovernVerbs.Unblock => $"Clear the block/allow decision for {NounLabel} “{name}” on {scope}?",
            GovernVerbs.Disable => $"Disable {NounLabel} “{name}” at runtime for {scope}?",
            GovernVerbs.Enable => $"Enable {NounLabel} “{name}” at runtime for {scope}?",
            GovernVerbs.Quarantine => $"Quarantine {NounLabel} “{name}” on {scope}?",
            GovernVerbs.Restore => $"Restore quarantined {NounLabel} “{name}” on {scope}?",
            GovernVerbs.Remove => $"Remove {NounLabel} “{name}” from {scope}?",
            GovernVerbs.Unset => $"Remove {NounLabel} “{name}” from the config of {scope}?",
            GovernVerbs.Scan => $"Scan {NounLabel} “{name}” for {scope}?",
            _ => $"Run {verb} on {NounLabel} “{name}”?",
        };
    }

    /// <summary>A consequence worth stating in the review, taken from the verb's <c>--help</c>.</summary>
    protected virtual string? NoteFor(GovernVerbs verb, GovernRow row) => verb switch
    {
        GovernVerbs.Disable =>
            "Runtime only: the files stay where they are. Claude Code and Codex enforce it at their prompt hooks; other connectors only record it.",
        GovernVerbs.Enable => "Clears the runtime-disable record. Nothing is installed or restored.",
        GovernVerbs.Quarantine =>
            $"Moves the {NounLabel}'s files into DefenseClaw's quarantine area so they stop loading. Undo with Restore.",
        GovernVerbs.Restore => "Moves the files back to the path recorded when they were quarantined.",
        GovernVerbs.Scan => ScanNote($"this {NounLabel}"),
        GovernVerbs.Unblock => "Clears block, file and runtime decisions without adding an allow entry. Quarantined files are not restored.",
        _ => null,
    };

    protected virtual string SuccessFor(GovernVerbs verb, GovernRow row)
    {
        var name = row.Name;
        return verb switch
        {
            GovernVerbs.Block => $"Blocked “{name}”.",
            GovernVerbs.Allow => $"Allowed “{name}”.",
            GovernVerbs.Unblock => $"Cleared the block/allow decision for “{name}”.",
            GovernVerbs.Disable => $"Disabled “{name}”.",
            GovernVerbs.Enable => $"Enabled “{name}”.",
            GovernVerbs.Quarantine => $"Quarantined “{name}”.",
            GovernVerbs.Restore => $"Restored “{name}”.",
            GovernVerbs.Remove => $"Removed “{name}”.",
            GovernVerbs.Unset => $"Removed “{name}” from the config.",
            GovernVerbs.Scan => $"Scanned “{name}”; its Scan result is read again below.",
            _ => "Done.",
        };
    }

    private static string? VerbWord(GovernVerbs verb) => verb switch
    {
        GovernVerbs.Block => "block",
        GovernVerbs.Allow => "allow",
        GovernVerbs.Unblock => "unblock",
        GovernVerbs.Disable => "disable",
        GovernVerbs.Enable => "enable",
        GovernVerbs.Quarantine => "quarantine",
        GovernVerbs.Restore => "restore",
        GovernVerbs.Remove => "remove",
        GovernVerbs.Unset => "unset",
        GovernVerbs.Scan => "scan",
        _ => null,
    };

    /// <summary>Only the verbs whose <c>--help</c> lists <c>--reason</c>.</summary>
    private static bool UsesReason(GovernVerbs verb) =>
        verb is GovernVerbs.Block or GovernVerbs.Allow or GovernVerbs.Disable or GovernVerbs.Quarantine;

    /// <summary>
    /// <c>noun verb [options] -- target</c>. The target is a name that came from outside (a directory name, a
    /// server key), so it goes after <c>--</c>: a name beginning with a dash is then a name, not an option.
    /// </summary>
    protected static List<string> BuildArgv(string noun, string verb, IEnumerable<string> options, string target)
    {
        var argv = new List<string> { noun, verb };
        argv.AddRange(options);
        argv.Add("--");
        argv.Add(target);
        return argv;
    }

    // ---- Read-only detail (skill info, plugin info, tool status) -------------------------------------------------

    private async Task ShowInfoAsync(GovernRow row)
    {
        var options = new List<string> { "--json" };
        AppendInfoScope(options, row);
        var argv = BuildArgv(Noun, InfoVerb, options, row.Name);

        // The card's heading shows the name of an item that came from outside: its control and format characters are spelled out.
        var title = $"defenseclaw {Noun} {InfoVerb} — {DisplayNames.Visible(row.Name)}";

        // Info for a name the CLI would rewrite is info about some other item.
        if (RefuseExpandingTarget(argv))
        {
            return;
        }

        // The tier decides whether this may run without review; a verb that is not read-only never gets here.
        if (TierFor(argv) != CommandTier.ReadOnly)
        {
            BeginReview(new GovernPlan { Heading = $"Run {Noun} {InfoVerb} for “{row.Name}”?", Argv = argv });
            return;
        }

        IsBusy = true;
        try
        {
            var invocation = await RunCliAsync(argv, ExactTargetsJson).ConfigureAwait(true);
            if (invocation.ExitCode == 0 && invocation.FailureReason is null)
            {
                var stdout = string.Join('\n', invocation.OutputLines
                    .Where(l => l.Stream == CliStream.StandardOutput)
                    .Select(l => l.Text));
                OutputTitle = title;
                OutputText = GovernJson.PrettyText(stdout);
            }
            else
            {
                OutputTitle = title + " (failed)";
                OutputText = Summarize(invocation);
            }

            IsOutputOpen = true;
        }
        catch (CliNotFoundException ex)
        {
            ShowResult("Command failed", $"The defenseclaw CLI could not be found. {ex.Message}", InfoBarSeverity.Error);
        }
        catch (ArgumentExpansionException ex)
        {
            ShowResult("Command refused", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Scope flags for the read-only detail verb. Default: the row's connector.</summary>
    protected virtual void AppendInfoScope(List<string> options, GovernRow row)
    {
        var connector = EffectiveConnector(row.Connector);
        if (connector is not null)
        {
            options.Add("--connector");
            options.Add(connector);
        }
    }

    [RelayCommand]
    private void CloseOutput() => IsOutputOpen = false;

    [RelayCommand]
    private void CopyOutput() => CopyToClipboard(OutputText);

    // ---- Review, confirm, run ------------------------------------------------------------------------------------

    /// <summary>
    /// The tier of a command line. Only the leading verb path counts — the two tokens before the first
    /// flag — so a target, a reason or any other operator-typed value can never make a mutation look read-only.
    /// </summary>
    protected static CommandTier TierFor(IReadOnlyList<string> argv) =>
        CommandTiers.Classify(argv.TakeWhile(a => !a.StartsWith('-')).Take(2).ToList());

    /// <summary>
    /// Every Govern command names a target that came from outside - a folder name, a server key - after the
    /// <c>--</c>. The runner refuses (<see cref="CliRunOptions.RefuseExpandingTargets"/>) to run one the CLI would
    /// rewrite (a skill folder called <c>a*</c> becoming <c>a1 a2</c>), so the action never runs on something else than
    /// what was confirmed; this is the same check made early enough to say so instead of opening a review.
    /// </summary>
    private static readonly CliRunOptions ExactTargets = new() { RefuseExpandingTargets = true };

    private static readonly CliRunOptions ExactTargetsJson = CliRunOptions.JsonRead with { RefuseExpandingTargets = true };

    /// <summary>Shows the refusal and returns true when the CLI would act on a different target than <paramref name="argv"/> names.</summary>
    private bool RefuseExpandingTarget(IReadOnlyList<string> argv)
    {
        var changes = ArgvHazards.FindChangedTargets(argv, CliWorkingDirectory.DefaultPath);
        if (changes.Count == 0)
        {
            return false;
        }

        ShowResult("Command refused", ArgumentExpansionException.BuildMessage(changes), InfoBarSeverity.Error);
        return true;
    }

    /// <summary>Opens the confirm overlay for <paramref name="plan"/>. Nothing runs until the operator confirms.</summary>
    protected void BeginReview(GovernPlan plan)
    {
        // Every state-changing command of every Govern panel passes through here: a partial, failed, unfinished or old list
        // never gets as far as a review, whatever button or menu item asked.
        if (RefuseUntrustedChange() || RefuseExpandingTarget(plan.Argv))
        {
            return;
        }

        // Everything reviewed here is meant to change something, so it is never shown as harmless; the plan can
        // only raise the tier from there. The verb path alone (TierFor) is a floor of its own, so an operator-typed
        // flag value that spells --help cannot lower what the review derives from the whole argv.
        var floor = CommandReview.Stricter(
            CommandReview.Stricter(CommandTier.StateChanging, TierFor(plan.Argv)),
            plan.MinimumTier ?? CommandTier.ReadOnly);

        ConfirmReview = new CommandReview
        {
            Title = plan.Heading,
            Summary = plan.Note ?? string.Empty,
            Steps = new[] { new CommandReviewStep(plan.Argv, floor: floor) },
        };
        _pendingPlan = plan;
        IsConfirmOpen = true;
    }

    [RelayCommand]
    private async Task ConfirmYesAsync()
    {
        IsConfirmOpen = false;
        var plan = _pendingPlan;
        _pendingPlan = null;

        if (plan is not null)
        {
            // The review may have been open while a refresh finished partial or failed, or while config.yaml / .env changed: it was authorized
            // by data that is gone.
            if (RefuseUntrustedChange(plan))
            {
                return;
            }

            await RunMutationAsync(plan).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private void ConfirmNo()
    {
        IsConfirmOpen = false;
        _pendingPlan = null;
    }

    /// <summary>
    /// Closes the topmost transient surface (confirm overlay, then the detail card, then the panel's own
    /// form). Returns true when something was closed, so the view can mark Esc as handled.
    /// </summary>
    public bool HandleEscape()
    {
        if (IsConfirmOpen)
        {
            ConfirmNo();
            return true;
        }

        if (IsOutputOpen)
        {
            IsOutputOpen = false;
            return true;
        }

        return CloseTransientUi();
    }

    /// <summary>Closes the panel's own form or drawer; true when one was open.</summary>
    protected virtual bool CloseTransientUi() => false;

    private async Task RunMutationAsync(GovernPlan plan)
    {
        var succeeded = false;
        IsBusy = true;
        try
        {
            var options = plan.Timeout is { } timeout ? ExactTargets with { Timeout = timeout } : ExactTargets;
            var invocation = await RunCliAsync(plan.Argv, options).ConfigureAwait(true);
            if (invocation.ExitCode == 0 && invocation.FailureReason is null)
            {
                succeeded = true;
                ShowResult("Done", DisplayNames.Visible(plan.SuccessMessage) + " Recorded in Activity.", InfoBarSeverity.Success);
            }
            else
            {
                ShowResult(
                    "Command failed",
                    $"{Summarize(invocation)} The list was not re-read; press Refresh to see the current state. Full output is in Activity.",
                    InfoBarSeverity.Error);
            }
        }
        // CliRunner throws these two before any process exists. Uncaught, they would reach the dispatcher's fault
        // handler and replace the dashboard with an error dialog; here they are just a failed command.
        catch (CliNotFoundException ex)
        {
            ShowResult("Command failed", $"The defenseclaw CLI could not be found. {ex.Message}", InfoBarSeverity.Error);
        }
        catch (Exception ex) when (ex is SecretInArgumentException or ArgumentExpansionException)
        {
            ShowResult("Command refused", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            IsBusy = false;
        }

        // Follow-up reads are gated on the exit code: a failed command leaves the list as it was.
        if (succeeded)
        {
            plan.OnSuccess?.Invoke();
            await LoadAsync(queueIfBusy: true).ConfigureAwait(true);
        }
    }

    protected void ShowResult(string title, string message, InfoBarSeverity severity)
    {
        ResultTitle = title;
        ResultMessage = message;
        ResultSeverity = severity;
        IsResultOpen = true;
    }

    /// <summary>The last meaningful line of what a command said, or why it did not finish.</summary>
    protected static string Summarize(CliInvocation invocation)
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

    protected static void CopyToClipboard(string text)
    {
        _ = Views.Controls.DcClipboard.TrySetText(text);
    }

    // ---- Small helpers for the parsers ---------------------------------------------------------------------------

    /// <summary>How many entries the last read classified as not-really-items (Plugins only).</summary>
    protected int ArtifactCount => _artifacts.Count;

    /// <summary>"origin: x · version: y", skipping blanks; null when nothing is left.</summary>
    protected static string? JoinMeta(params (string Label, string? Value)[] parts)
    {
        var text = string.Join(
            " · ",
            parts.Where(p => !string.IsNullOrWhiteSpace(p.Value)).Select(p => $"{p.Label}: {p.Value}"));
        return text.Length == 0 ? null : text;
    }

    /// <summary>A CLI timestamp shown in local time, or the raw text when it does not parse.</summary>
    protected static string? FormatTimestamp(string? raw) =>
        DateTimeOffset.TryParse(raw, out var at) ? at.LocalDateTime.ToString("yyyy-MM-dd HH:mm") : raw;

    /// <summary>Adds a label/value pair to a details list unless the value is blank.</summary>
    protected static void AddField(List<GovernField> fields, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            fields.Add(new GovernField(label, value));
        }
    }

    // ---- Verbs by state (shared by the parsers) ------------------------------------------------------------------

    /// <summary>
    /// The buttons a skill or plugin offers for its current state: enforcement decisions first, then runtime
    /// disable/enable, then quarantine/restore.
    /// </summary>
    protected static GovernVerbs StandardVerbs(GovernItemState s, bool canDisable, bool canQuarantine)
    {
        var verbs = GovernVerbs.Info | GovernVerbs.CopyName;

        if (s.Quarantined)
        {
            verbs |= GovernVerbs.Restore;
            if (s.Blocked || s.Allowed)
            {
                verbs |= GovernVerbs.Unblock;
            }

            return verbs;
        }

        if (s.Blocked)
        {
            verbs |= GovernVerbs.Unblock | GovernVerbs.Allow;
        }
        else if (s.Allowed)
        {
            verbs |= GovernVerbs.Block | GovernVerbs.Unblock;
        }
        else
        {
            verbs |= GovernVerbs.Block | GovernVerbs.Allow;
        }

        if (canDisable)
        {
            verbs |= s.Disabled ? GovernVerbs.Enable : GovernVerbs.Disable;
        }

        if (canQuarantine)
        {
            verbs |= GovernVerbs.Quarantine;
        }

        return verbs;
    }
}
