using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.Core.Cli;
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
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
        Rows.CollectionChanged += (_, _) => NotifyStateFlags();
        ArtifactRows.CollectionChanged += (_, _) => NotifyStateFlags();
    }

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
        await LoadAsync().ConfigureAwait(true);
    }

    protected override void OnActivated()
    {
        RefreshConnectorList();

        // One catch-up read per visit, and only when the data is old (or never arrived). Not a timer.
        var stale = _lastLoadedAt is null
                    || DateTimeOffset.Now - _lastLoadedAt.Value >= StaleAfter
                    || !string.Equals(_loadedScopeKey, ScopeKey(), StringComparison.Ordinal);
        if (!_everRequested || stale)
        {
            _ = LoadAsync();
        }
    }

    /// <summary>Re-read the list (Refresh button, F5).</summary>
    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();

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
            invocation = await Services.Cli.RunAsync(argv, options: CliRunOptions.JsonRead).ConfigureAwait(true);
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

        if (invocation.ExitCode != 0)
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

        var stdout = string.Concat(invocation.OutputLines
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

        _allRows.Clear();
        _allRows.AddRange(rows.Where(r => !r.IsArtifact));
        _artifacts.Clear();
        _artifacts.AddRange(rows.Where(r => r.IsArtifact));

        _lastLoadedAt = DateTimeOffset.Now;
        _loadedScopeKey = scopeKey;
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
        var haveGoodData = _lastLoadedAt is not null
                           && (scopeKey is null || string.Equals(_loadedScopeKey, scopeKey, StringComparison.Ordinal));
        if (haveGoodData)
        {
            var age = _lastLoadedAt is { } at ? $"as of {at.LocalDateTime:HH:mm}" : "earlier";
            RefreshWarning = $"{title}: {message} Showing the last good read ({age}).";
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
    }

    // ---- Connector scope -----------------------------------------------------------------------------------------

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

        if (PlanFor(row, verb) is { } plan)
        {
            BeginReview(plan);
        }
    }

    /// <summary>Builds the reviewed command for a mutating row verb; null when the verb does not apply.</summary>
    protected virtual GovernPlan? PlanFor(GovernRow row, GovernVerbs verb)
    {
        var word = VerbWord(verb);
        if (word is null)
        {
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
        var title = $"defenseclaw {Noun} {InfoVerb} — {row.Name}";

        // The tier decides whether this may run without review; a verb that is not read-only never gets here.
        if (TierFor(argv) != CommandTier.ReadOnly)
        {
            BeginReview(new GovernPlan { Heading = $"Run {Noun} {InfoVerb} for “{row.Name}”?", Argv = argv });
            return;
        }

        IsBusy = true;
        try
        {
            var invocation = await Services.Cli.RunAsync(argv, options: CliRunOptions.JsonRead).ConfigureAwait(true);
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

    /// <summary>Opens the confirm overlay for <paramref name="plan"/>. Nothing runs until the operator confirms.</summary>
    protected void BeginReview(GovernPlan plan)
    {
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
            var invocation = await Services.Cli.RunAsync(plan.Argv).ConfigureAwait(true);
            if (invocation.ExitCode == 0 && invocation.FailureReason is null)
            {
                succeeded = true;
                ShowResult("Done", plan.SuccessMessage + " Recorded in Activity.", InfoBarSeverity.Success);
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
        catch (SecretInArgumentException ex)
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
        try
        {
            System.Windows.Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // The clipboard is locked by another process; nothing useful to do.
        }
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
