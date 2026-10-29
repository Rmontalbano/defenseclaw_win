using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.Core.Cli;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace DefenseClaw.App.ViewModels;

/// <summary>One key/value pair of a source's raw JSON, for the generic detail expander.</summary>
public sealed record RegistryFieldRow(string Key, string Value)
{
    public override string ToString() => $"{Key}: {Value}";
}

/// <summary>
/// One configured registry source, as returned by <c>defenseclaw registry list --json</c>
/// (0.8.10 shape, from the CLI source: <c>id, kind, url, content, auth_env, enabled, auto_sync,
/// sync_interval_hours, last_sync, last_status, entries{total, clean, warning, blocked, error}</c>).
/// Every field beyond the id is optional and every raw field is kept in <see cref="Fields"/>, so a
/// different release's extra keys still show.
/// </summary>
public sealed class RegistrySourceRow
{
    public required string Id { get; init; }

    public string? Kind { get; init; }

    public string? Location { get; init; }

    public string? EntriesSummary { get; init; }

    public required IReadOnlyList<RegistryFieldRow> Fields { get; init; }

    /// <summary>declared content: skill, mcp or both.</summary>
    public string? Content { get; init; }

    public string? AuthEnv { get; init; }

    public bool? Enabled { get; init; }

    public string? LastSync { get; init; }

    public string? LastStatus { get; init; }

    /// <summary>The element's raw JSON, so a refresh can tell a changed row from an unchanged one.</summary>
    public string Signature { get; init; } = string.Empty;

    public string EnabledDisplay => Enabled switch
    {
        true => "Enabled",
        false => "Disabled",
        _ => "—",
    };

    public string EnabledKey => Enabled == true ? "Ok" : "Neutral";

    /// <summary>The row menu's on/off item: what choosing it does ("Disable…" for a source that is on).</summary>
    public string ToggleText => Enabled == true ? "Disable…" : "Enable…";

    public string KindDisplay => string.IsNullOrWhiteSpace(Kind) ? "—" : Kind;

    public string ContentDisplay => string.IsNullOrWhiteSpace(Content) ? "—" : Content;

    public string EntriesDisplay => string.IsNullOrWhiteSpace(EntriesSummary) ? "not synced" : EntriesSummary;

    public string LastSyncDisplay => FormatTimestamp(LastSync) ?? "never";

    public string StatusDisplay => string.IsNullOrWhiteSpace(LastStatus) ? "—" : LastStatus;

    public override string ToString() =>
        $"Registry source {Id}, {KindDisplay}, {EnabledDisplay}, entries {EntriesDisplay}, last sync {LastSyncDisplay}";

    internal static string? FormatTimestamp(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            raw,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            : raw;
    }
}

/// <summary>
/// One cached entry of a source, read from that source's <c>index.json</c> (an <c>EntryVerdict</c>).
/// <para>
/// <b>What the cache says about a scan.</b> An entry can carry an <see cref="Error"/> — <c>str(exc)[:240]</c> of the
/// failure that stopped its scan or fetch (<c>registries/sync.py:292</c>), cleared by the next good scan (<c>:304</c>) —
/// and a <c>findings</c> <i>count</i> (<c>len(scan_result.findings)</c>, <c>sync.py:301</c>; <c>cache.py:56</c> types it
/// <c>int</c> and <c>to_dict</c> omits it when zero) next to a <c>severity</c> that is the worst of those findings
/// (<c>max_severity()</c>, <c>sync.py:303</c>). The cache does <b>not</b> keep the findings themselves — they belong to the
/// scan (<see cref="ScanId"/>) — so the row shows the count and the worst severity, and lists findings only if a later
/// index writes them as an array (see <see cref="FindingLines"/>).
/// </para>
/// </summary>
public sealed class RegistryEntryRow
{
    /// <summary>The tooltip lists at most this many findings; the rest are counted.</summary>
    internal const int MaxFindingLines = 12;

    public required string Name { get; init; }

    public string? Type { get; init; }

    public string? Status { get; init; }

    public bool Approved { get; init; }

    public bool Rejected { get; init; }

    public string? Severity { get; init; }

    public string? Location { get; init; }

    /// <summary>Why the entry's fetch or scan failed (<c>error</c>), as one line. Null when the index has none.</summary>
    public string? Error { get; init; }

    /// <summary>How many findings the last scan raised (<c>findings</c>: a count, or the length of a list). Zero when absent.</summary>
    public int Findings { get; init; }

    /// <summary>
    /// One line per finding, when the index carries them as a list rather than a count (the 0.8.10 writer does not:
    /// <c>cache.py:56</c>). Capped at <see cref="MaxFindingLines"/>; empty for a count.
    /// </summary>
    public IReadOnlyList<string> FindingLines { get; init; } = Array.Empty<string>();

    /// <summary>The worst severity among findings that were listed with one; null for a count.</summary>
    public string? FindingSeverity { get; init; }

    public string? ScanId { get; init; }

    public string? LastScanned { get; init; }

    /// <summary>
    /// The source whose <c>index.json</c> this row was read from. Set on the rows of the Entries and Approved tabs, which mix sources (the TUI's
    /// <c>RegistryEntryRow.source_id</c>); the rows of the selected source's own list leave it null, as that list always has.
    /// </summary>
    public string? SourceId { get; init; }

    /// <summary>MCP entries: <c>stdio</c>, <c>sse</c> and so on (<c>transport</c>).</summary>
    public string? Transport { get; init; }

    /// <summary>MCP entries that run a local process: the command (<c>command</c>), with <see cref="Args"/> after it.</summary>
    public string? Command { get; init; }

    public IReadOnlyList<string> Args { get; init; } = Array.Empty<string>();

    public string SourceDisplay => string.IsNullOrWhiteSpace(SourceId) ? "—" : SourceId;

    private string? _key;

    /// <summary>Identity of the entry across reads: its source, type and name (an entry is unique by type and name within a source). Built once: a read compares thousands of them.</summary>
    public string Key => _key ??= string.Join('\u001f', SourceId, Type, Name);

    /// <summary>A stdio MCP entry's launch line, <c>npx -y @example/docs-mcp</c>; null for an entry that runs nothing.</summary>
    public string? LaunchLine => string.IsNullOrWhiteSpace(Command)
        ? null
        : Args.Count == 0 ? Command : Command + " " + string.Join(' ', Args);

    /// <summary>
    /// What the Location column of the cross-source tables shows: where the entry is fetched from (<see cref="Location"/>), else, for an MCP entry
    /// that runs a local process, what it runs (the TUI's <c>location</c> is its url, else its command, else its source URL).
    /// </summary>
    public string LocationDisplay => Location ?? LaunchLine ?? string.Empty;

    public string TypeDisplay => string.IsNullOrWhiteSpace(Type) ? "—" : Type;

    public string StatusDisplay => string.IsNullOrWhiteSpace(Status) ? "—" : Status;

    public string SeverityDisplay => string.IsNullOrWhiteSpace(Severity) ? "—" : Severity;

    public string ReviewDisplay => Approved ? "Approved" : Rejected ? "Rejected" : "—";

    public string ReviewKey => Approved ? "Ok" : Rejected ? "Bad" : "Neutral";

    public string StatusKey => (Status ?? string.Empty).ToLowerInvariant() switch
    {
        "clean" => "Ok",
        "warning" => "Warn",
        "blocked" or "error" => "Bad",
        _ => "Neutral",
    };

    /// <summary>approve / reject take <c>--type {skill,mcp}</c> only.</summary>
    public bool CanReview => string.Equals(Type, "skill", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Type, "mcp", StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ error and findings

    public bool HasError => !string.IsNullOrWhiteSpace(Error);

    public bool HasFindings => Findings > 0;

    /// <summary>True when the row has a second line to show — an error, findings, or both. Otherwise nothing extra is drawn.</summary>
    public bool HasDetail => HasError || HasFindings;

    /// <summary>The visible error line, shown in the Bad tone: <c>Error: fetch failed: HTTP 404</c>.</summary>
    public string ErrorText => HasError ? "Error: " + Error : string.Empty;

    /// <summary>What a screen reader announces for the error line (and part of the row's own name).</summary>
    public string ErrorAutomationName => HasError ? "error: " + Error : string.Empty;

    public string FindingsBadgeText => Findings == 1
        ? "1 finding"
        : Findings.ToString("N0", CultureInfo.CurrentCulture) + " findings";

    /// <summary>The worst severity known for the findings (the entry's own <c>severity</c> or the listed ones), upper-cased; null when none is given.</summary>
    public string? WorstSeverity
    {
        get
        {
            var entry = NormalizeSeverity(Severity);
            var listed = NormalizeSeverity(FindingSeverity);
            return SeverityRank(entry) >= SeverityRank(listed) ? entry ?? listed : listed;
        }
    }

    /// <summary>
    /// The tone of the findings badge: the worst severity when it is a known one (Critical / High / Medium are the
    /// coloured tones; Low and Info read neutral), and <c>High</c> when no severity is given — findings are never
    /// drawn calmer than the cache lets us prove.
    /// </summary>
    public string FindingsKey => SeverityRank(WorstSeverity) switch
    {
        4 => "Critical",
        3 => "High",
        2 => "Medium",
        1 => "Low",
        0 => "Info",
        _ => "High",
    };

    /// <summary>What a screen reader announces for the badge: <c>2 findings, worst severity MEDIUM</c>.</summary>
    public string FindingsAutomationName => WorstSeverity is { } worst
        ? $"{FindingsBadgeText}, worst severity {worst}"
        : FindingsBadgeText;

    /// <summary>
    /// The badge's tooltip: the count and worst severity, the findings themselves when the index lists them (and
    /// otherwise a plain statement that the cache keeps only the count), then which scan they came from.
    /// </summary>
    public string FindingsToolTip
    {
        get
        {
            var lines = new List<string> { WorstSeverity is { } worst ? $"{FindingsBadgeText} — worst severity {worst}" : FindingsBadgeText };

            foreach (var finding in FindingLines)
            {
                lines.Add("• " + finding);
            }

            if (FindingLines.Count == 0)
            {
                lines.Add("The cached index keeps the count and the worst severity, not the individual findings.");
            }
            else if (Findings > FindingLines.Count)
            {
                lines.Add($"… and {(Findings - FindingLines.Count).ToString("N0", CultureInfo.CurrentCulture)} more");
            }

            var scan = string.Join(
                " · ",
                new[] { string.IsNullOrWhiteSpace(ScanId) ? null : "Scan " + ScanId, RegistrySourceRow.FormatTimestamp(LastScanned) is { } at ? "scanned " + at : null }
                    .Where(part => part is not null));
            if (scan.Length > 0)
            {
                lines.Add(scan);
            }

            return string.Join('\n', lines);
        }
    }

    /// <summary>Critical 4 … Info 0; -1 for anything else, including nothing.</summary>
    internal static int SeverityRank(string? severity) => severity switch
    {
        "CRITICAL" => 4,
        "HIGH" => 3,
        "MEDIUM" => 2,
        "LOW" => 1,
        "INFO" => 0,
        _ => -1,
    };

    internal static string? NormalizeSeverity(string? severity) =>
        string.IsNullOrWhiteSpace(severity) ? null : severity.Trim().ToUpperInvariant();

    /// <summary>
    /// The row's name for a screen reader (a <c>DataGridRow</c> announces its item's text): the entry, its status and
    /// review state, then — only when present — <c>error: …</c> and <c>N findings, worst severity …</c>.
    /// </summary>
    public override string ToString()
    {
        // A row of the cross-source tables also says which source it is from; the selected source's own list does not repeat it.
        var from = string.IsNullOrWhiteSpace(SourceId) ? string.Empty : $" from {SourceId}";
        var text = $"{TypeDisplay} entry {Name}{from}, status {StatusDisplay}, review {ReviewDisplay}";
        if (HasError)
        {
            text += ", " + ErrorAutomationName;
        }

        if (HasFindings)
        {
            text += ", " + FindingsAutomationName;
        }

        return text;
    }
}

/// <summary>
/// View-model for the Registries panel: external skill / MCP catalog sources DefenseClaw can sync from.
/// <para>
/// <b>Reads</b> come from the read-only <c>defenseclaw registry list --json</c> (one CLI call per
/// activation or Refresh, cached with an "as of" time) and, for the selected source's entries, its
/// <c>registries\&lt;id&gt;\index.json</c> cache file. <b>Every change</b> — add, sync, enable/disable,
/// remove, approve/reject, require — is a reviewed CLI command (see <see cref="DiscoverActionReview"/>):
/// the exact argv, its tier, and an exit-code-gated follow-up; nothing here edits config.yaml itself.
/// A machine with no registry sources is a normal state (this one has none), so it gets a designed
/// empty state that explains what a registry is, not an error.
/// </para>
/// </summary>
public sealed partial class RegistriesPanelViewModel : PanelViewModelBase
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(60);

    private const long MaxIndexBytes = 4L * 1024 * 1024;
    private const int MaxEntries = 5_000;

    // 2-64 chars of the kebab/underscore alphabet; the CLI's own check is the authority, this only
    // keeps a value that could escape the registries folder (or start with '-') out of a path or argv.
    private static readonly Regex SourceIdPattern = new("^[a-z0-9][a-z0-9_-]{1,63}$", RegexOptions.Compiled);

    // What the CLI accepts for a source id is wider than that: ^[a-z0-9][a-z0-9._-]{1,63}$ (cmd_registry.py:82), so
    // 'corp.skills' is a valid id whose cache this panel must be able to read. A '.' is safe in a folder name as long as
    // it never appears twice in a row (the CLI's own source_dir refuses '..' and '/', registries/cache.py:133-139). Upper case
    // is allowed here too: a hand-edited config.yaml can carry it, and the check only has to keep the path in its folder.
    private static readonly Regex CachedSourceIdPattern = new("^[A-Za-z0-9][A-Za-z0-9._-]{1,63}$", RegexOptions.Compiled);

    private bool _loadRunning;
    private DateTimeOffset? _loadedAt;
    private int _entriesSequence;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCliError))]
    [NotifyPropertyChangedFor(nameof(ShowNotConfigured))]
    [NotifyPropertyChangedFor(nameof(ShowUnavailable))]
    private string? _cliErrorMessage;

    [ObservableProperty]
    private string? _statusMessage;

    // HasConfigSection is derived from this and drives the "config.yaml does have a registry section"
    // block. Without the notification the binding read the empty initial value once and never again.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConfigSection))]
    private string? _configSectionYaml;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoSources))]
    [NotifyPropertyChangedFor(nameof(ShowNotConfigured))]
    [NotifyPropertyChangedFor(nameof(ShowUnavailable))]
    [NotifyPropertyChangedFor(nameof(ShowPolicyCard))]
    [NotifyPropertyChangedFor(nameof(CanSyncAll))]
    [NotifyPropertyChangedFor(nameof(ShowSourcesView))]
    [NotifyPropertyChangedFor(nameof(ShowEntriesView))]
    [NotifyPropertyChangedFor(nameof(ShowTabEmpty))]
    private bool _hasSources;

    /// <summary>False until the first read finishes, so the panel does not flash "no sources" while it is still looking.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNotConfigured))]
    [NotifyPropertyChangedFor(nameof(ShowUnavailable))]
    private bool _hasLoaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedSource))]
    [NotifyPropertyChangedFor(nameof(HasNoSelectedSource))]
    [NotifyPropertyChangedFor(nameof(ToggleEnabledText))]
    [NotifyPropertyChangedFor(nameof(CanChangeSelectedSource))]
    private RegistrySourceRow? _selectedSource;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedEntry))]
    [NotifyPropertyChangedFor(nameof(CanReviewEntry))]
    private RegistryEntryRow? _selectedEntry;

    [ObservableProperty]
    private string? _entriesMessage;

    [ObservableProperty]
    private bool _isEntriesLoading;

    /// <summary>Result line of the last reviewed action ("Synced corp-skills"), shown above the list.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActionMessage))]
    private string? _actionMessage;

    [ObservableProperty]
    private Wpf.Ui.Controls.InfoBarSeverity _actionSeverity = Wpf.Ui.Controls.InfoBarSeverity.Success;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SkillsRegistryOptional))]
    [NotifyPropertyChangedFor(nameof(SkillsRegistryText))]
    [NotifyPropertyChangedFor(nameof(ShowPolicyCard))]
    private bool _skillsRegistryRequired;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(McpsRegistryOptional))]
    [NotifyPropertyChangedFor(nameof(McpsRegistryText))]
    [NotifyPropertyChangedFor(nameof(ShowPolicyCard))]
    private bool _mcpsRegistryRequired;

    public RegistriesPanelViewModel(AppServices services)
        : base(services)
    {
        Trust = CatalogTrust.Watching(services.Paths);
        Review = new DiscoverActionReview(services) { RunGuard = ReasonToRefuseRun };
    }

    public override string Title => "Registries";

    public override string Description =>
        "External skill / MCP catalog sources DefenseClaw can sync from: add, sync, approve and require them.";

    /// <summary>The shared confirm-and-run dialog every change on this panel goes through.</summary>
    public DiscoverActionReview Review { get; }

    public bool HasCliError => !string.IsNullOrEmpty(CliErrorMessage);

    public bool HasConfigSection => !string.IsNullOrEmpty(ConfigSectionYaml);

    public bool HasNoSources => !HasSources;

    /// <summary>The normal standalone state: the read worked and there is simply nothing configured.</summary>
    public bool ShowNotConfigured => HasLoaded && HasNoSources && !HasCliError;

    /// <summary>The read failed and there is nothing cached to show: a different message from "not configured".</summary>
    public bool ShowUnavailable => HasLoaded && HasNoSources && HasCliError;

    public bool HasSelectedSource => SelectedSource is not null;

    public bool HasNoSelectedSource => SelectedSource is null;

    public bool HasSelectedEntry => SelectedEntry is not null;

    public bool CanReviewEntry => SelectedEntry is { CanReview: true } && IsDataTrusted;

    // ---- Catalog safety: may this list authorize a change? ---------------------------------------

    /// <summary>
    /// What the last read of the sources amounted to. A failed refresh keeps the old rows on screen; they no longer authorize a sync, a
    /// removal, an approval or a policy switch, and neither does a list that has gone old, has not been read yet, or was read before
    /// config.yaml or .env changed (see <see cref="CatalogTrust"/>).
    /// </summary>
    public CatalogTrust Trust { get; }

    public bool IsDataTrusted => Trust.IsTrusted && Services.Installation.IsMutable;

    /// <summary>Why changes are off (the buttons' tooltip); null while they are on. A read-only installation says so before the list does.</summary>
    public string? DataUntrustedReason => Services.Installation.BlockedReason ?? Trust.Reason;

    protected override void OnInstallationChanged() => NotifyTrust();

    /// <summary>A source the toolbar's source actions apply to is chosen (the selected source, or on the Entries and Approved tabs the selected entry's), and changes are allowed.</summary>
    public bool CanChangeSelectedSource => ActionSource is not null && IsDataTrusted;

    public bool CanSyncAll => HasSources && IsDataTrusted;

    internal void NotifyTrust()
    {
        OnPropertyChanged(nameof(IsDataTrusted));
        OnPropertyChanged(nameof(DataUntrustedReason));
        OnPropertyChanged(nameof(CanChangeSelectedSource));
        OnPropertyChanged(nameof(CanSyncAll));
        OnPropertyChanged(nameof(CanReviewEntry));
        OnPropertyChanged(nameof(CanReviewTabEntry));
    }

    /// <summary>
    /// True (and says why in the result bar) when the list may not authorize a change, checked against the clock and the config files at the
    /// moment the command runs (<see cref="CatalogTrust.ReasonNow"/>). Every command that opens the review calls it first.
    /// </summary>
    private bool RefuseUntrustedChange()
    {
        // A read-only installation comes first: one reason, and the one a refresh would not cure.
        if ((Services.Installation.BlockedReason ?? Trust.ReasonNow()) is not { } reason)
        {
            return false;
        }

        ActionMessage = reason;
        ActionSeverity = Wpf.Ui.Controls.InfoBarSeverity.Warning;
        NotifyTrust();
        return true;
    }

    /// <summary>
    /// The review's last question, asked when the operator confirms: the list it was opened on may have gone old, or config.yaml / .env may
    /// have changed, while the dialog was up. The panel says so too, so the reason is still there when the dialog is closed.
    /// </summary>
    private string? ReasonToRefuseRun()
    {
        if ((Services.Installation.BlockedReason ?? Trust.ReasonNow()) is not { } reason)
        {
            return null;
        }

        ActionMessage = reason;
        ActionSeverity = Wpf.Ui.Controls.InfoBarSeverity.Warning;
        NotifyTrust();
        return reason;
    }

    /// <summary>
    /// config.yaml or .env were rewritten (the existing watcher raises this for both). Whether that postdates the read on screen is the trust's
    /// to say; the rows stay either way. Nothing here reads: Refresh does.
    /// </summary>
    private void OnConfigReloaded(object? sender, EventArgs e) => NoteConfigMoved();

    private void NoteConfigMoved()
    {
        if (Trust.CheckConfig())
        {
            NotifyTrust();
        }
    }

    public bool HasActionMessage => !string.IsNullOrEmpty(ActionMessage);

    public string ToggleEnabledText => SelectedSource?.Enabled == false ? "Enable…" : "Disable…";

    public bool SkillsRegistryOptional => !SkillsRegistryRequired;

    public bool McpsRegistryOptional => !McpsRegistryRequired;

    public string SkillsRegistryText => SkillsRegistryRequired
        ? "Skills: registry required (a skill must match an approved registry entry)"
        : "Skills: registry optional";

    public string McpsRegistryText => McpsRegistryRequired
        ? "MCP servers: registry required (a server must match an approved registry entry)"
        : "MCP servers: registry optional";

    /// <summary>The default-deny switches only matter once there are sources (or one is already on and needs turning off).</summary>
    public bool ShowPolicyCard => HasSources || SkillsRegistryRequired || McpsRegistryRequired;

    public ObservableCollection<RegistrySourceRow> Sources { get; } = new();

    public ObservableCollection<RegistryEntryRow> Entries { get; } = new();

    public override async Task InitializeAsync(CancellationToken cancellationToken = default) =>
        await LoadAsync(cancellationToken).ConfigureAwait(true);

    /// <summary>
    /// One-shot catch-up when the panel comes back on screen after the data has gone stale (old, or read before config.yaml / .env changed);
    /// no timer. The panel listens for a config reload only while it is on screen: a change made while it was away is found by comparing the
    /// files with what the rows were read under.
    /// </summary>
    protected override void OnActivated()
    {
        Services.ConfigReloaded += OnConfigReloaded;
        NoteConfigMoved();

        if (_loadRunning || (_loadedAt is { } at && DefenseClaw.Core.Time.WallClock.Elapsed(at) < StaleAfter && !Trust.IsStale))
        {
            return;
        }

        _ = LoadSafelyAsync();
    }

    protected override void OnDeactivated() => Services.ConfigReloaded -= OnConfigReloaded;

    [RelayCommand]
    private async Task RefreshAsync()
    {
        ActionMessage = null;
        await LoadAsync(CancellationToken.None).ConfigureAwait(true);
    }

    /// <summary>Esc: closes the add form. (The review dialog handles its own Esc.)</summary>
    public bool HandleEscape()
    {
        if (Review.IsOpen)
        {
            return Review.HandleEscape();
        }

        if (IsAddFormOpen)
        {
            IsAddFormOpen = false;
            return true;
        }

        return ClearEntryFocusOnEscape();
    }

    private async Task LoadSafelyAsync()
    {
        try
        {
            await LoadAsync(CancellationToken.None).ConfigureAwait(true);
        }
#pragma warning disable CA1031 // A background catch-up must not take the panel down; the message is shown in the banner.
        catch (Exception ex)
        {
            Trace.TraceError($"Registries catch-up failed: {ex}");
            CliErrorMessage = $"Could not refresh registry sources: {ex.Message}";
            Trust.MarkFailed(CliErrorMessage);
            NotifyTrust();
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// Test seam: answers <c>registry list --json</c> instead of the CLI, so a test feeds the sources exactly (and can hold the read open) without a
    /// process. Null: the CLI, through <see cref="DiscoverCli.RunReadOnlyAsync"/>.
    /// </summary>
    internal Func<IReadOnlyList<string>, CancellationToken, Task<CliInvocation>>? ReadSources { get; set; }

    /// <summary>
    /// True from the moment a read of the sources starts until its entries have been read too (the selected source's and every source's, which
    /// the read ends with): what the Entries tab holds is then about to be replaced, so an "Open in Registries" link waits for it
    /// (<see cref="ApplyPendingFocus"/>) rather than answering from rows that are about to change.
    /// </summary>
    private bool _refreshing;

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (_loadRunning)
        {
            return;
        }

        _refreshing = true;
        try
        {
            await LoadCoreAsync(cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            _refreshing = false;
            ApplyPendingFocus();
        }
    }

    private async Task LoadCoreAsync(CancellationToken cancellationToken)
    {
        _loadRunning = true;
        IsLoading = true;
        Trust.BeginRead();
        NotifyTrust();
        CliErrorMessage = null;
        var previousId = SelectedSource?.Id;
        var rows = new List<RegistrySourceRow>();

        // try/finally, not a trailing assignment: a cancelled or faulted read must not leave the
        // progress ring spinning over a panel that has already stopped trying.
        try
        {
            try
            {
                var listArgv = new[] { "registry", "list", "--json" };
                var invocation = await (ReadSources?.Invoke(listArgv, cancellationToken)
                    ?? DiscoverCli.RunReadOnlyAsync(Services, listArgv, cancellationToken)).ConfigureAwait(true);

                if (invocation.FailureReason is { Length: > 0 } reason)
                {
                    // FailureReason is not only "could not start": it also says "timed out after
                    // 120 s" and "cancelled", so the sentence must not claim a start failure.
                    CliErrorMessage = $"'defenseclaw registry list' did not complete: {reason}";
                }
                else if (invocation.ExitCode != 0)
                {
                    var stderr = DiscoverCli.Stderr(invocation);
                    CliErrorMessage = string.IsNullOrWhiteSpace(stderr)
                        ? $"'defenseclaw registry list' exited {invocation.ExitCode}."
                        : $"'defenseclaw registry list' exited {invocation.ExitCode}: {stderr}";
                }
                else
                {
                    ParseSources(DiscoverCli.Stdout(invocation), rows);
                }
            }
            catch (CliNotFoundException ex)
            {
                CliErrorMessage = $"'defenseclaw' was not found on PATH: {ex.Message}";
            }

            // A failed read keeps the previous rows (stale but useful) instead of blanking the list.
            if (!HasCliError)
            {
                SyncCollection(
                    Sources,
                    rows,
                    r => r.Id,
                    (existing, wanted) => string.Equals(existing.Signature, wanted.Signature, StringComparison.Ordinal));
            }

            HasSources = Sources.Count > 0;
            _loadedAt = DateTimeOffset.Now;

            // A failed read leaves the previous rows on screen; from here they are information, not authority.
            if (HasCliError)
            {
                Trust.MarkFailed(CliErrorMessage!);
            }
            else
            {
                Trust.MarkComplete();
            }

            NotifyTrust();
            var asOf = DateTimeOffset.Now.ToString("HH:mm", CultureInfo.InvariantCulture);
            StatusMessage = HasCliError
                ? $"Could not read sources · tried {asOf}"
                : Sources.Count == 0
                    ? $"No registry sources · as of {asOf}"
                    : $"{Sources.Count} registry source{(Sources.Count == 1 ? string.Empty : "s")} · as of {asOf}";

            LoadConfigFallback();
            LoadRegistryRequired();

            if (previousId is not null)
            {
                SelectedSource = Sources.FirstOrDefault(s => string.Equals(s.Id, previousId, StringComparison.Ordinal));
            }

            HasLoaded = true;
        }
        finally
        {
            IsLoading = false;
            _loadRunning = false;
        }

        // Entries change on disk after a sync/approve; re-read them for whatever is selected, and for the Entries and Approved tabs.
        await LoadEntriesAsync(SelectedSource).ConfigureAwait(true);
        await LoadAllEntriesSafelyAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Falls back to whatever raw registry section survives in config.yaml — this model has no typed
    /// section for it (see <c>DefenseClawConfig.KnownSections</c>), so it round-trips as verbatim YAML
    /// text. The key the CLI writes is <c>registries</c>; <c>registry</c> is kept as a second look.
    /// </summary>
    private void LoadConfigFallback()
    {
        ConfigSectionYaml = Services.Config.SectionText("registries") ?? Services.Config.SectionText("registry");
    }

    private void LoadRegistryRequired()
    {
        var text = Services.Config.SectionText("asset_policy");
        SkillsRegistryRequired = ReadRegistryRequired(text, "skill");
        McpsRegistryRequired = ReadRegistryRequired(text, "mcp");
    }

    /// <summary><c>asset_policy.&lt;type&gt;.registry_required</c> from the raw section text; absent means off.</summary>
    internal static bool ReadRegistryRequired(string? sectionYaml, string type)
    {
        if (string.IsNullOrWhiteSpace(sectionYaml) || DefenseClaw.Core.Config.ConfigYamlGuard.Refusal(sectionYaml) is not null)
        {
            return false;
        }

        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(sectionYaml));
            if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
            {
                return false;
            }

            return TryChild(root, "asset_policy") is YamlMappingNode policy
                && TryChild(policy, type) is YamlMappingNode typed
                && TryChild(typed, "registry_required") is YamlScalarNode flag
                && bool.TryParse(flag.Value, out var required)
                && required;
        }
        catch (YamlException)
        {
            return false;
        }
    }

    private static YamlNode? TryChild(YamlMappingNode map, string key) =>
        map.Children.TryGetValue(new YamlScalarNode(key), out var node) ? node : null;

    partial void OnSelectedSourceChanged(RegistrySourceRow? value)
    {
        SelectedEntry = null;

        // What the details say about the source (publisher, fetched at, counts) is read with its entries; until that lands they are not the last source's.
        SourceFacts = RegistrySourceFacts.None;
        _ = LoadEntriesSafelyAsync(value);
    }

    private async Task LoadEntriesSafelyAsync(RegistrySourceRow? source)
    {
        try
        {
            await LoadEntriesAsync(source).ConfigureAwait(true);
        }
#pragma warning disable CA1031 // Entries are a convenience view; a failure is reported in the entries message.
        catch (Exception ex)
        {
            Trace.TraceError($"Registry entries read failed: {ex}");
            EntriesMessage = $"Could not read the cached entries: {ex.Message}";
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// Reads <c>registries\&lt;id&gt;\index.json</c> for <paramref name="source"/> (capped at 4 MiB /
    /// 5,000 entries). The id is checked against <see cref="CachedSourceIdPattern"/> first, so a value from
    /// the CLI's JSON can never point outside the registries folder.
    /// </summary>
    internal async Task LoadEntriesAsync(RegistrySourceRow? source)
    {
        var sequence = ++_entriesSequence;

        if (source is null || !IsPlainSourceId(source.Id))
        {
            Entries.Clear();
            SourceFacts = RegistrySourceFacts.None;
            EntriesMessage = source is null ? null : NotPlainMessage;
            IsEntriesLoading = false;
            return;
        }

        IsEntriesLoading = true;
        var read = await ReadIndexAsync(Services.Paths.DataDirectory, source.Id, stampSource: false).ConfigureAwait(true);

        if (sequence != _entriesSequence)
        {
            return;
        }

        Entries.Clear();
        foreach (var row in read.Rows)
        {
            Entries.Add(row);
        }

        SourceFacts = new RegistrySourceFacts(read.Info);
        EntriesMessage = read.Message;
        IsEntriesLoading = false;
    }

    private const string NotPlainMessage = "This source id is not a plain name, so its cache is not read here.";

    private const string TruncatedPrefix = "Showing the first ";

    /// <summary>
    /// Reads <c>registries\&lt;id&gt;\index.json</c> of one source into rows, the facts about the source (publisher, fetch time, counts) and a
    /// sentence for what the operator should know (nothing cached, unreadable, too large, cut at the row limit); the same read for the selected
    /// source's list and for every source of the Entries and Approved tabs. Never throws for what is on disk. The id is checked against
    /// <see cref="CachedSourceIdPattern"/> first, so a value from the CLI's JSON can never point outside the registries folder; the file is
    /// capped at 4 MiB and 5,000 entries. The read and the parse run off the caller's context (nothing here touches a control), so a source with a
    /// large cache does not hold the UI thread.
    /// </summary>
    /// <param name="stampSource">True for rows that go into a table mixing sources: each carries its <see cref="RegistryEntryRow.SourceId"/>.</param>
    internal static async Task<RegistryIndexRead> ReadIndexAsync(string dataDirectory, string sourceId, bool stampSource)
    {
        if (!IsPlainSourceId(sourceId))
        {
            return new RegistryIndexRead(Array.Empty<RegistryEntryRow>(), NotPlainMessage, null, IsProblem: true);
        }

        var rows = new List<RegistryEntryRow>();
        RegistryIndexInfo? info = null;
        string? message;
        var problem = false;
        var path = Path.Combine(dataDirectory, "registries", sourceId, "index.json");

        try
        {
            if (!File.Exists(path))
            {
                message = "Nothing cached yet. Sync this source to fetch and scan its entries.";
            }
            else if (new FileInfo(path).Length > MaxIndexBytes)
            {
                message = "The cached index is larger than 4 MiB, so it is not shown here. Use 'defenseclaw registry entries' in a terminal.";
                problem = true;
            }
            else
            {
                var text = await DefenseClaw.Core.IO.SharedFile.ReadAllTextAsync(path).ConfigureAwait(false);
                message = ParseIndex(text, rows, stampSource ? sourceId : null, out info);
                problem = message is not null && message.StartsWith(TruncatedPrefix, StringComparison.Ordinal);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            message = $"Could not read the cached index: {ex.Message}";
            problem = true;
        }

        return new RegistryIndexRead(rows, message, info, problem);
    }

    /// <summary>A name that is one folder under registries\: the CLI's id alphabet, and never '..' or a trailing '.'.</summary>
    internal static bool IsPlainSourceId(string id) =>
        CachedSourceIdPattern.IsMatch(id) && !id.Contains("..", StringComparison.Ordinal) && !id.EndsWith('.');

    /// <summary>
    /// Fills <paramref name="rows"/> from an index.json body; returns a message when there is nothing to show.
    /// The file is <c>SourceIndex.to_dict()</c> (registries/cache.py:120-123, written sorted by <c>save_index</c>):
    /// source_id, schema_version, fetched_at, publisher, the five counts, and <c>verdicts</c>, each an
    /// <c>EntryVerdict.to_dict()</c> (cache.py:70-89) that always has name/type/status/approved/rejected and omits every
    /// other field when it is empty.
    /// </summary>
    internal static string? ParseIndex(string json, List<RegistryEntryRow> rows) => ParseIndex(json, rows, null, out _);

    /// <summary>
    /// <see cref="ParseIndex(string, List{RegistryEntryRow})"/>, also stamping each row with <paramref name="sourceId"/> (null: none) and handing back
    /// the source's own facts: <c>publisher</c>, <c>fetched_at</c> and the five counts. The counts are the file's own when it has them and otherwise
    /// counted from the entries, as the TUI's <c>load_registry_index</c> does (<c>tui/services/registry_cache.py</c>); an index that is a bare list
    /// has no header, so only the counts are known.
    /// </summary>
    internal static string? ParseIndex(string json, List<RegistryEntryRow> rows, string? sourceId, out RegistryIndexInfo? info)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        info = null;

        JsonElement list = default;
        var found = false;
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in new[] { "verdicts", "entries" })
            {
                if (root.TryGetProperty(key, out var candidate) && candidate.ValueKind == JsonValueKind.Array)
                {
                    list = candidate;
                    found = true;
                    break;
                }
            }
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            list = root;
            found = true;
        }

        if (!found)
        {
            info = IndexInfo(root, rows);
            return "The cached index has no entry list yet. Sync this source.";
        }

        foreach (var element in list.EnumerateArray())
        {
            if (rows.Count >= MaxEntries)
            {
                info = IndexInfo(root, rows);
                return $"{TruncatedPrefix}{MaxEntries:N0} entries.";
            }

            if (element.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var name = JsonString(element, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var (findings, findingLines, findingSeverity) = ReadFindings(element);
            var args = JsonStrings(element, "args");

            rows.Add(new RegistryEntryRow
            {
                Name = name,
                SourceId = sourceId,
                Type = JsonString(element, "type"),
                Status = JsonString(element, "status"),
                Approved = JsonBool(element, "approved"),
                Rejected = JsonBool(element, "rejected"),
                Severity = JsonString(element, "severity"),
                Location = JsonString(element, "source_url") ?? JsonString(element, "url") ?? JsonString(element, "target"),
                Transport = JsonString(element, "transport"),
                Command = JsonString(element, "command"),
                Args = args,
                Error = OneLine(JsonString(element, "error")),
                Findings = findings,
                FindingLines = findingLines,
                FindingSeverity = findingSeverity,
                ScanId = JsonString(element, "scan_id"),
                LastScanned = JsonString(element, "last_scanned_at"),
            });
        }

        info = IndexInfo(root, rows);
        return rows.Count == 0 ? "The last sync cached no entries." : null;
    }

    /// <summary>
    /// <c>findings</c> of one verdict. 0.8.10 writes an integer (<c>cache.py:56, 84-85</c>: only when non-zero); a later
    /// index might list them, so an array is read too — its length is the count, and each item becomes a line (its
    /// <c>severity</c>, then a title, message, description or rule id). Anything else, and a negative number, is no findings.
    /// </summary>
    private static (int Count, IReadOnlyList<string> Lines, string? Severity) ReadFindings(JsonElement element)
    {
        if (!element.TryGetProperty("findings", out var value))
        {
            return (0, Array.Empty<string>(), null);
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                return (value.TryGetInt64(out var number) ? (int)Math.Clamp(number, 0, int.MaxValue) : 0, Array.Empty<string>(), null);

            case JsonValueKind.String:
                return (int.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var text) ? text : 0, Array.Empty<string>(), null);

            case JsonValueKind.Array:
                var lines = new List<string>();
                string? worst = null;
                var count = 0;
                foreach (var item in value.EnumerateArray())
                {
                    count++;
                    var severity = item.ValueKind == JsonValueKind.Object ? RegistryEntryRow.NormalizeSeverity(JsonString(item, "severity")) : null;
                    if (RegistryEntryRow.SeverityRank(severity) > RegistryEntryRow.SeverityRank(worst))
                    {
                        worst = severity;
                    }

                    if (lines.Count < RegistryEntryRow.MaxFindingLines && FindingLine(item, severity) is { } line)
                    {
                        lines.Add(line);
                    }
                }

                return (count, lines, worst);

            default:
                return (0, Array.Empty<string>(), null);
        }
    }

    private static string? FindingLine(JsonElement item, string? severity)
    {
        if (item.ValueKind == JsonValueKind.String)
        {
            return OneLine(item.GetString());
        }

        if (item.ValueKind != JsonValueKind.Object)
        {
            return OneLine(item.GetRawText());
        }

        var what = new[] { "title", "message", "description", "rule_id", "id", "name", "rule" }
            .Select(key => OneLine(JsonString(item, key)))
            .FirstOrDefault(text => text is not null);

        // An object with nothing recognisable in it is shown as its JSON; an empty one says nothing at all.
        var described = what ?? (item.EnumerateObject().Any() ? OneLine(item.GetRawText()) : null);

        return severity is null ? described : what is null ? severity : $"{severity}: {what}";
    }

    /// <summary>
    /// Collapses a value from the cache to one printable line: every run of whitespace or control characters becomes a
    /// single space, and anything past 400 characters is cut. A scan error can carry a multi-line exception text.
    /// </summary>
    internal static string? OneLine(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var builder = new System.Text.StringBuilder(raw.Length);
        var pendingSpace = false;
        foreach (var c in raw)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                _ = builder.Append(' ');
                pendingSpace = false;
            }

            _ = builder.Append(c);
            if (builder.Length >= 400)
            {
                _ = builder.Append('…');
                break;
            }
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    private static string? JsonString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool JsonBool(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    /// <summary>A JSON array of an entry as text (an MCP entry's <c>args</c>): a string as it is, null as nothing, anything else as its JSON; not an array: none.</summary>
    private static IReadOnlyList<string> JsonStrings(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return value.EnumerateArray()
            .Select(item => item.ValueKind switch
            {
                JsonValueKind.String => item.GetString() ?? string.Empty,
                JsonValueKind.Null => string.Empty,
                _ => item.GetRawText(),
            })
            .ToArray();
    }

    /// <summary>One of an index's counts: a number or numeric text, never below zero; null when the index does not have it (or it is something else).</summary>
    private static int? JsonCount(JsonElement root, string property)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(property, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var whole) => (int)Math.Clamp(whole, 0, int.MaxValue),
            JsonValueKind.Number when value.TryGetDouble(out var fraction) && double.IsFinite(fraction) => (int)Math.Clamp(Math.Truncate(fraction), 0, int.MaxValue),
            JsonValueKind.String when long.TryParse(value.GetString()?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var text) => (int)Math.Clamp(text, 0, int.MaxValue),
            _ => null,
        };
    }

    /// <summary>
    /// The facts about the source an index states: its publisher, when it was fetched and the five counts - the file's own count when it has one,
    /// otherwise what the entries add up to (the TUI's <c>_int(payload.get("clean_count"), _count_status(verdicts, "clean"))</c>).
    /// </summary>
    private static RegistryIndexInfo IndexInfo(JsonElement root, IReadOnlyList<RegistryEntryRow> rows)
    {
        int Counted(string status) => rows.Count(row => string.Equals(row.Status, status, StringComparison.Ordinal));

        var header = root.ValueKind == JsonValueKind.Object;
        return new RegistryIndexInfo(
            header ? OneLine(JsonString(root, "publisher")) : null,
            header ? OneLine(JsonString(root, "fetched_at")) : null,
            JsonCount(root, "entry_count") ?? rows.Count,
            JsonCount(root, "clean_count") ?? Counted("clean"),
            JsonCount(root, "warning_count") ?? Counted("warning"),
            JsonCount(root, "blocked_count") ?? Counted("blocked"),
            JsonCount(root, "error_count") ?? Counted("error"));
    }

    /// <summary>
    /// Reads <c>registry list --json</c>: always a JSON array, <c>[]</c> when nothing is configured
    /// (cmd_registry.py:504-535). Anything else is reported, not read as "no sources".
    /// </summary>
    internal void ParseSources(string json, List<RegistrySourceRow> rows)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            CliErrorMessage = "'defenseclaw registry list --json' printed nothing (expected a JSON array, [] when there are no sources).";
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(DiscoverCli.TrimToJson(json));
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                CliErrorMessage = "'defenseclaw registry list --json' did not return a JSON array of sources.";
                return;
            }

            var index = 0;
            foreach (var element in document.RootElement.EnumerateArray())
            {
                index++;
                rows.Add(MapSource(element, index));
            }
        }
        catch (JsonException ex)
        {
            CliErrorMessage = $"'defenseclaw registry list --json' did not return valid JSON: {ex.Message}";
        }
    }

    private static RegistrySourceRow MapSource(JsonElement element, int fallbackIndex)
    {
        var fields = new List<RegistryFieldRow>();
        string? id = null;
        string? kind = null;
        string? location = null;
        string? entries = null;
        string? content = null;
        string? authEnv = null;
        string? lastSync = null;
        string? lastStatus = null;
        bool? enabled = null;

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                var display = FormatValue(property.Value);
                fields.Add(new RegistryFieldRow(property.Name, display));

                switch (property.Name.ToLowerInvariant())
                {
                    case "id":
                    case "name":
                        id ??= display;
                        break;
                    case "type":
                    case "kind":
                        kind ??= display;
                        break;
                    case "url":
                    case "source":
                    case "location":
                    case "repo":
                        location ??= display;
                        break;
                    case "entries":
                    case "entry_count":
                    case "count":
                        entries ??= SummarizeEntries(property.Value) ?? display;
                        break;
                    case "content":
                        content ??= display;
                        break;
                    case "auth_env":
                        authEnv ??= string.IsNullOrEmpty(display) || display == "—" ? null : display;
                        break;
                    case "enabled":
                        if (enabled is null)
                        {
                            enabled = property.Value.ValueKind switch
                            {
                                JsonValueKind.True => true,
                                JsonValueKind.False => false,
                                _ => (bool?)null,
                            };
                        }

                        break;
                    case "last_sync":
                        lastSync ??= property.Value.ValueKind == JsonValueKind.String ? display : null;
                        break;
                    case "last_status":
                        lastStatus ??= property.Value.ValueKind == JsonValueKind.String ? display : null;
                        break;
                }
            }
        }
        else
        {
            fields.Add(new RegistryFieldRow("value", FormatValue(element)));
        }

        // The CLI's own table shows "-" for a source with no cached entries and no last_sync (cmd_registry.py:557-560);
        // its JSON carries a zeroed entries object for that, which must not read as "synced, found 0".
        if (entries == "0" && string.IsNullOrWhiteSpace(lastSync))
        {
            entries = null;
        }

        return new RegistrySourceRow
        {
            Id = id ?? $"source-{fallbackIndex.ToString(CultureInfo.InvariantCulture)}",
            Kind = kind,
            Location = location,
            EntriesSummary = entries,
            Fields = fields,
            Content = content,
            AuthEnv = authEnv,
            Enabled = enabled,
            LastSync = lastSync,
            LastStatus = lastStatus,
            Signature = element.GetRawText(),
        };
    }

    /// <summary><c>{"total":12,"clean":9,"warning":2,"blocked":1,"error":0}</c> → "12 (9 clean, 2 warning, 1 blocked)"; a bare number stays a number.</summary>
    private static string? SummarizeEntries(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        static long Count(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

        var total = Count(value, "total");
        if (total == 0)
        {
            return "0";
        }

        var parts = new List<string>();
        foreach (var (name, label) in new[] { ("clean", "clean"), ("warning", "warning"), ("blocked", "blocked"), ("error", "error") })
        {
            var n = Count(value, name);
            if (n > 0)
            {
                parts.Add($"{n.ToString(CultureInfo.InvariantCulture)} {label}");
            }
        }

        var head = total.ToString(CultureInfo.InvariantCulture);
        return parts.Count == 0 ? head : $"{head} ({string.Join(", ", parts)})";
    }

    private static string FormatValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Null => "—",
        JsonValueKind.Object or JsonValueKind.Array => value.GetRawText(),
        _ => value.GetRawText(),
    };
}
