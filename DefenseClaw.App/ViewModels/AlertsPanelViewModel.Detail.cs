using System.Diagnostics;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.Core.Audit;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.App.ViewModels;

/// <summary>The kinds the Alerts panel's Kind popup offers (the Mac's: All kinds / Blocks / Audit / Scans / Egress).</summary>
public static class AlertKinds
{
    public const string All = "all";
    public const string Blocks = "blocks";
    public const string Audit = "audit";
    public const string Scan = "scan";
    public const string Egress = "egress";

    /// <summary>The popup's choices, in the Mac's order.</summary>
    public static IReadOnlyList<AlertKindChoice> Choices { get; } = new[]
    {
        new AlertKindChoice(All, "All kinds"),
        new AlertKindChoice(Blocks, "Blocks"),
        new AlertKindChoice(Audit, "Audit"),
        new AlertKindChoice(Scan, "Scans"),
        new AlertKindChoice(Egress, "Egress"),
    };

    /// <summary>The canonical value for a name a deep link or a person might spell ("Scans", "scan", "BLOCKS"); null for one this panel does not know.</summary>
    public static string? Normalize(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "all" or "all kinds" => All,
        "blocks" or "block" => Blocks,
        "audit" or "findings" => Audit,
        "scan" or "scans" => Scan,
        "egress" => Egress,
        _ => null,
    };
}

/// <summary>One entry of the Kind popup.</summary>
public sealed record AlertKindChoice(string Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One finding card of the inspector's "Findings" section: severity pill, title, what it is, where, and what to do.</summary>
public sealed record AlertFindingCard(string Severity, string SeverityKey, string Title, string Description, string Location, string Remediation)
{
    public bool HasDescription => Description.Length > 0;

    public bool HasLocation => Location.Length > 0;

    public bool HasRemediation => Remediation.Length > 0;

    public override string ToString() => $"{Severity} finding. {Title}";
}

/// <summary>One row of "History for this target": when, what, how severe.</summary>
public sealed record AlertHistoryRow(string TimeText, string Action, string Severity, string SeverityKey)
{
    public override string ToString() => $"{TimeText}. {Action}. {Severity}";
}

/// <summary>
/// What the inspector shows beyond the alert row itself, read when the row is selected: the findings of its scans and the target's
/// earlier events. A section is visible while it is loading, has something to say, or has rows; a lookup that found nothing leaves
/// its section out (the Mac's behaviour), and one that failed says so instead of looking empty.
/// </summary>
public sealed partial class AlertDetail : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFindings), nameof(FindingsVisible))]
    private IReadOnlyList<AlertFindingCard> _findings = Array.Empty<AlertFindingCard>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FindingsVisible))]
    private string _findingsNote = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHistory), nameof(HistoryVisible))]
    private IReadOnlyList<AlertHistoryRow> _history = Array.Empty<AlertHistoryRow>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HistoryVisible))]
    private string _historyNote = string.Empty;

    public bool HasFindings => Findings.Count > 0;

    public bool FindingsVisible => HasFindings || FindingsNote.Length > 0;

    public bool HasHistory => History.Count > 0;

    public bool HistoryVisible => HasHistory || HistoryNote.Length > 0;

    /// <summary>True once both lookups answered (or failed); a detail still loading is not reused for a second selection.</summary>
    internal bool IsComplete { get; set; }
}

public sealed partial class AlertsPanelViewModel
{
    /// <summary>How many selected rows' details are kept; the list is a few hundred rows and a person looks at a handful.</summary>
    private const int DetailCacheLimit = 64;

    private readonly Dictionary<string, AlertDetail> _details = new(StringComparer.Ordinal);
    private CancellationTokenSource? _detailCts;
    private AlertDetailReader? _detailReader;
    private NetworkEgressReader? _egressReader;

    /// <summary>Why the egress feed could not be read, as of the last queue read; empty while it can.</summary>
    private string _egressProblem = string.Empty;

    /// <summary>The inspector's findings and history lookups (read-only, cancellable). Settable so a test can aim it at its own database.</summary>
    internal AlertDetailReader DetailReader
    {
        get => _detailReader ??= new AlertDetailReader(Services.Paths.AuditDatabasePath);
        set => _detailReader = value;
    }

    /// <summary>The egress decisions feed; see <see cref="DetailReader"/>.</summary>
    internal NetworkEgressReader EgressReader
    {
        get => _egressReader ??= new NetworkEgressReader(Services.Paths.AuditDatabasePath, Services.AuditChanges);
        set => _egressReader = value;
    }

    // ---- The kind filter ---------------------------------------------------------------------------------

    /// <summary>The Kind popup's choices.</summary>
    public IReadOnlyList<AlertKindChoice> KindChoices => AlertKinds.Choices;

    /// <summary>Which kind of row the list shows: an <see cref="AlertKinds"/> value.</summary>
    [ObservableProperty]
    private string _kindFilter = AlertKinds.All;

    partial void OnKindFilterChanged(string value) => ApplyFilters();

    // ---- The severity tiles ------------------------------------------------------------------------------

    private IReadOnlyList<SeverityFilter>? _tiles;

    /// <summary>The four tiles above the list: CRITICAL, HIGH, MEDIUM, LOW, each the same object as the severity filter of that name.</summary>
    public IReadOnlyList<SeverityFilter> Tiles => _tiles ??= SeverityFilters.Take(4).ToList();

    /// <summary>INFO has no tile (the Mac's four are CRITICAL to LOW); egress rows that only look like an LLM are INFO, so it keeps a small chip that shows when it has rows.</summary>
    public SeverityFilter InfoFilter => SeverityFilters[4];

    public bool ShowInfoFilter => InfoFilter.Count > 0 || !InfoFilter.IsEnabled;

    /// <summary>
    /// A tile press, the Mac's "filter to this severity": with nothing narrowed it shows only that severity; on the one severity that is
    /// showing alone it shows everything again; otherwise it switches that severity on or off, so a deep link's "High and above" can be
    /// refined by pressing tiles.
    /// </summary>
    [RelayCommand]
    private void SelectSeverity(SeverityFilter? filter)
    {
        if (filter is null)
        {
            return;
        }

        var narrowed = SeverityFilters.Any(f => !f.IsEnabled);
        var onlyThis = filter.IsEnabled && SeverityFilters.Count(f => f.IsEnabled) == 1;

        _settingToggles = true;
        try
        {
            foreach (var other in SeverityFilters)
            {
                other.IsEnabled = !narrowed
                    ? ReferenceEquals(other, filter)
                    : onlyThis ? true : ReferenceEquals(other, filter) ? !other.IsEnabled : other.IsEnabled;
            }
        }
        finally
        {
            _settingToggles = false;
        }

        ApplyFilters();
    }

    /// <summary>Marks the tiles that are narrowing the list (on while another is off); called by every filter pass.</summary>
    private void RefreshTileState()
    {
        var narrowed = SeverityFilters.Any(f => !f.IsEnabled);
        foreach (var filter in SeverityFilters)
        {
            filter.IsPicked = narrowed && filter.IsEnabled;
        }

        OnPropertyChanged(nameof(ShowInfoFilter));
    }

    private bool KindAllows(AlertItem item) => KindFilter switch
    {
        AlertKinds.Blocks => item.IsBlock,
        AlertKinds.Audit or AlertKinds.Scan or AlertKinds.Egress => string.Equals(item.Kind, KindFilter, StringComparison.Ordinal),
        _ => true,
    };

    // ---- Acknowledge selection ---------------------------------------------------------------------------

    /// <summary>True while a row is selected: what the toolbar's "Acknowledge selection" waits for (the Mac's disabled-until-selected).</summary>
    public bool HasActionRows => ActionRows.Count > 0;

    /// <summary>
    /// The audit ids of the rows a selection action names: the table's selection (a folded group of repeats stands for every repeat it
    /// folds), sorted and unique, and how many of those rows had no audit id to name (gateway-only rows the CLI's <c>--id</c> cannot take).
    /// </summary>
    internal (List<string> Ids, int Skipped) SelectedIds()
    {
        var rows = new List<AlertItem>();
        foreach (var row in ActionRows)
        {
            if (CollapseRepeats && row.RepeatCount > 1)
            {
                rows.AddRange(_filtered.Where(f => string.Equals(f.GroupKey, row.GroupKey, StringComparison.OrdinalIgnoreCase)));
            }
            else
            {
                rows.Add(row);
            }
        }

        return IdsOf(rows);
    }

    /// <summary>The ids of every row the filters leave (text, kind, scope, severity), repeats included, and how many had no audit id.</summary>
    internal (List<string> Ids, int Skipped) FilteredIds() => IdsOf(_filtered);

    private static (List<string> Ids, int Skipped) IdsOf(IEnumerable<AlertItem> rows)
    {
        var distinct = rows.DistinctBy(r => r.Key, StringComparer.Ordinal).ToList();
        var ids = distinct.Where(r => r.HasAuditId).Select(r => r.Key).Order(StringComparer.Ordinal).ToList();
        return (ids, distinct.Count - ids.Count);
    }

    /// <summary>The toolbar strip's readout: how many alerts the selection actions would name (and how many cannot be named).</summary>
    public string SelectionText
    {
        get
        {
            var (ids, skipped) = SelectedIds();
            if (ids.Count == 0 && skipped == 0)
            {
                return "No alerts selected";
            }

            var text = $"{ids.Count.ToString("N0", CultureInfo.CurrentCulture)} selected";
            return skipped > 0 ? $"{text} ({skipped} gateway-only, no audit id)" : text;
        }
    }

    /// <summary>What "Dismiss filtered" names: the alerts the filters leave on screen.</summary>
    public string FilteredText => $"Dismiss filtered ({FilteredIds().Ids.Count.ToString("N0", CultureInfo.CurrentCulture)})";

    private bool HasFiltered => _filtered.Count > 0;

    /// <summary>"Acknowledge selection": exactly the selected alerts, by id, with the CLI's dry run first; nothing runs until it is confirmed.</summary>
    [RelayCommand(CanExecute = nameof(HasActionRows))]
    private Task OpenAcknowledgeSelectionAsync() => OpenIdReviewAsync(AcknowledgeVerb, SelectedIds(), "selected");

    /// <summary>"Dismiss selection": exactly the selected alerts, by id.</summary>
    [RelayCommand(CanExecute = nameof(HasActionRows))]
    private Task OpenDismissSelectionAsync() => OpenIdReviewAsync(DismissVerb, SelectedIds(), "selected");

    /// <summary>"Dismiss filtered": every alert the filters leave on screen, by id (the TUI's <c>c</c>).</summary>
    [RelayCommand(CanExecute = nameof(HasFiltered))]
    private Task OpenDismissFilteredAsync() => OpenIdReviewAsync(DismissVerb, FilteredIds(), "filtered");

    /// <summary>
    /// Opens the review for an exact set of ids. Nothing to name (an empty set, or only gateway-only rows) is said on the result banner and
    /// opens nothing; a set above <see cref="IdChunkSize"/> becomes several reviewed runs.
    /// </summary>
    private async Task OpenIdReviewAsync(string verb, (List<string> Ids, int Skipped) set, string setName)
    {
        var (ids, skipped) = set;
        if (ids.Count == 0)
        {
            ShowActionResult(
                skipped > 0
                    ? $"Nothing to {verb}: the {skipped} {setName} alert(s) are gateway-only rows with no audit id for the CLI to name."
                    : $"No {setName} alerts to {verb}.",
                isError: true);
            return;
        }

        ShowActionSuccess = false;
        ShowActionError = false;
        _reviewVerb = verb;
        _reviewSetName = setName;
        _reviewIdTotal = ids.Count;
        _idChunks = ids.Chunk(IdChunkSize).Select(c => c.ToList()).ToList();
        _chunkIndex = 0;
        _appliedSoFar = 0;
        _anyChunkApplied = false;

        var dismissing = string.Equals(verb, DismissVerb, StringComparison.Ordinal);
        var total = ids.Count.ToString("N0", CultureInfo.CurrentCulture);
        _openingReview = true;
        try
        {
            IsSeverityReview = false;
            ReviewHeading = $"{(dismissing ? "Dismiss" : "Acknowledge")} {total} {setName} {(ids.Count == 1 ? "alert" : "alerts")}";
            ReviewIntro = "This applies to exactly the alerts named in the command below, by id - never to the rest of their severity. The preview below is the CLI's own dry run and changes nothing."
                + (skipped > 0 ? $" {skipped} {setName} gateway-only row(s) have no audit id and are left out." : string.Empty);
            CommandNote = "This is the exact command that will run. Nothing happens until you confirm. --yes is the CLI's own confirmation for more than one id; this review is yours.";
            SetChunkText();
            SetReviewError(string.Empty);
            IsReviewOpen = true;
        }
        finally
        {
            _openingReview = false;
        }

        await PreviewAsync();
    }

    partial void OnSelectedAlertChanged(AlertItem? value)
    {
        NotifySelectionChanged();
        _ = LoadDetailAsync(value);
    }

    // ---- Inspector: findings and history -----------------------------------------------------------------

    /// <summary>Stops a detail lookup that is still running (the panel left the screen, another row was picked).</summary>
    private void CancelDetail()
    {
        var running = Interlocked.Exchange(ref _detailCts, null);
        if (running is null)
        {
            return;
        }

        try
        {
            running.Cancel();
        }
        finally
        {
            running.Dispose();
        }
    }

    /// <summary>Forgets what was read for rows (Refresh), and reads the selected row again.</summary>
    private void ResetDetails()
    {
        CancelDetail();
        _details.Clear();
        if (SelectedAlert is { } selected)
        {
            _ = LoadDetailAsync(selected);
        }
    }

    /// <summary>
    /// Reads the selected row's findings and target history, once per row (the result is kept for the next time that row is picked, and
    /// shared with the copy a collapsed group makes of it). Both lookups run at once, off the UI thread, and are stopped when the
    /// selection moves or the panel is hidden; a failure of either shows in its own section and leaves the other alone. Never throws.
    /// </summary>
    internal async Task LoadDetailAsync(AlertItem? item)
    {
        CancelDetail();
        if (item is null)
        {
            return;
        }

        if (_details.TryGetValue(item.Key, out var cached) && cached.IsComplete)
        {
            item.Detail = cached;
            return;
        }

        if (_details.Count >= DetailCacheLimit)
        {
            _details.Clear();
        }

        var detail = new AlertDetail();
        item.Detail = detail;
        _details[item.Key] = detail;

        var cts = new CancellationTokenSource();
        _detailCts = cts;
        try
        {
            await Task.WhenAll(
                FillFindingsAsync(item, detail, cts.Token),
                FillHistoryAsync(item, detail, cts.Token)).ConfigureAwait(true);
            detail.IsComplete = true;
        }
        catch (OperationCanceledException)
        {
            // Another row was picked, or the panel went away: what was half read is not kept.
            if (_details.TryGetValue(item.Key, out var current) && ReferenceEquals(current, detail))
            {
                _ = _details.Remove(item.Key);
            }
        }
    }

    private async Task FillFindingsAsync(AlertItem item, AlertDetail detail, CancellationToken cancellationToken)
    {
        // An egress decision is not a scan: it has no findings to look up.
        if (string.Equals(item.Kind, AlertKinds.Egress, StringComparison.Ordinal))
        {
            return;
        }

        detail.FindingsNote = "Reading findings…";
        try
        {
            var target = item.TargetRef.Length > 0 ? item.TargetRef : item.RawTarget;
            var rows = await DetailReader.ReadFindingsAsync(item.RunId, target, cancellationToken: cancellationToken).ConfigureAwait(true);
            detail.Findings = rows
                .Select(f => new AlertFindingCard(
                    f.Severity.Length > 0 ? f.Severity : "INFO",
                    AlertItem.KeyFor(f.Severity),
                    f.Title.Length > 0 ? f.Title : f.RuleId.Length > 0 ? f.RuleId : "Finding",
                    f.Description,
                    f.Location,
                    f.Remediation))
                .ToList();
            detail.FindingsNote = string.Empty;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Trace.TraceWarning($"alerts: findings could not be read: {ex.GetType().Name}: {ex.Message}");
            detail.Findings = Array.Empty<AlertFindingCard>();
            detail.FindingsNote = ex is TimeoutException
                ? "The findings lookup took too long and was stopped."
                : "The findings could not be read from the audit database.";
        }
    }

    private async Task FillHistoryAsync(AlertItem item, AlertDetail detail, CancellationToken cancellationToken)
    {
        if (item.TargetRef.Length == 0 && item.RawTarget.Length == 0)
        {
            return;
        }

        detail.HistoryNote = "Reading history…";
        try
        {
            // One extra row: the alert's own audit row is excluded, and five are shown (the Mac's prefix(5)).
            var rows = await DetailReader.ReadHistoryAsync(
                new[] { item.TargetRef, item.RawTarget },
                item.AuditId,
                limit: HistoryRows,
                cancellationToken: cancellationToken).ConfigureAwait(true);
            detail.History = rows
                .Select(h => new AlertHistoryRow(
                    h.Timestamp == DateTimeOffset.MinValue ? "—" : h.Timestamp.ToLocalTime().ToString("MMM d HH:mm", CultureInfo.CurrentCulture),
                    h.Action,
                    h.Severity.Length > 0 ? h.Severity : "INFO",
                    AlertItem.KeyFor(h.Severity)))
                .ToList();
            detail.HistoryNote = rows.Count > 0
                ? $"From the newest {AlertDetailReader.HistoryWindow.ToString("N0", CultureInfo.CurrentCulture)} audit events."
                : string.Empty;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Trace.TraceWarning($"alerts: target history could not be read: {ex.GetType().Name}: {ex.Message}");
            detail.History = Array.Empty<AlertHistoryRow>();
            detail.HistoryNote = ex is TimeoutException
                ? "The history lookup took too long and was stopped."
                : "The history could not be read from the audit database.";
        }
    }

    /// <summary>How many earlier events the inspector lists.</summary>
    internal const int HistoryRows = 5;

    // ---- Egress ------------------------------------------------------------------------------------------

    /// <summary>
    /// The newest egress decisions the reader holds (the same list object while the database has not changed). A feed that cannot be read
    /// is null and a note, never a failed queue read.
    /// </summary>
    private async Task<IReadOnlyList<EgressEvent>?> ReadEgressEventsAsync()
    {
        try
        {
            var events = await EgressReader.ReadRecentAsync(NetworkEgressReader.DefaultLimit, TimeSpan.FromSeconds(5)).ConfigureAwait(true);
            _egressProblem = string.Empty;
            return events;
        }
#pragma warning disable CA1031 // The egress feed is an addition to the queue; a locked or older database must not take the queue down with it.
        catch (Exception ex) when (ex is SqliteException or IOException or TimeoutException or InvalidOperationException)
#pragma warning restore CA1031
        {
            Trace.TraceWarning($"alerts: the egress feed could not be read: {ex.GetType().Name}: {ex.Message}");
            _egressProblem = "egress could not be read";
            return null;
        }
    }

    /// <summary>The egress decisions that deserve a row (not allowed, or LLM-shaped), as alert rows; rows already shown are reused.</summary>
    private static List<AlertItem> EgressRows(IReadOnlyList<EgressEvent>? events, Dictionary<string, AlertItem> shown) =>
        events is null
            ? new List<AlertItem>()
            : events
                .Where(e => e.IsAlertWorthy)
                .Select(e => shown.TryGetValue(e.Id, out var existing) ? existing : AlertItem.FromEgress(e))
                .ToList();
}

public sealed partial class AlertItem
{
    /// <summary>The detail the inspector shows for this row; set when the row is selected.</summary>
    [ObservableProperty]
    private AlertDetail? _detail;

    /// <summary>What the row is, as the Kind popup knows it: <see cref="AlertKinds.Audit"/>, <see cref="AlertKinds.Scan"/> or <see cref="AlertKinds.Egress"/>.</summary>
    public string Kind { get; private init; } = AlertKinds.Audit;

    /// <summary>"audit", "scan" or "egress": the Kind column's word.</summary>
    public string KindLabel => Kind;

    /// <summary>The audit row's own <c>target</c> column, when it differs from the target the finding's attributes name (history is keyed on both).</summary>
    public string RawTarget { get; private init; } = string.Empty;

    /// <summary>The id of the <c>audit_events</c> row behind this one (an egress row's key carries a prefix), left out of its own history.</summary>
    public string AuditId => Key.StartsWith("audit:", StringComparison.Ordinal) ? Key["audit:".Length..] : Key;

    /// <summary>The Mac's "Blocks" kind: the action or the details name a block, reject, deny or quarantine.</summary>
    public bool IsBlock
    {
        get
        {
            var text = $"{Action} {Headline}";
            return Contains(text, "block") || Contains(text, "reject") || Contains(text, "deny") || Contains(text, "quarantine");
        }
    }

    private static string KindOf(string action, string scanner) =>
        scanner.Length > 0 || action.Contains("scan", StringComparison.OrdinalIgnoreCase) ? AlertKinds.Scan : AlertKinds.Audit;

    /// <summary>An egress decision as an alert row: the decision is the action, the host the target, the network attributes the attribute list.</summary>
    public static AlertItem FromEgress(EgressEvent egress)
    {
        ArgumentNullException.ThrowIfNull(egress);

        var decision = egress.Decision.Length > 0 ? egress.Decision : "decision unknown";
        var fields = egress.Attributes
            .Select(p => new AlertField(Shorten(p.Key), p.Value))
            .ToList();
        var headline = egress.Reason.Length > 0
            ? egress.Reason
            : egress.Branch.Length > 0 ? $"{decision} · {egress.Branch}" : decision;
        var pretty = string.Join(Environment.NewLine, fields.Select(f => $"{f.Name}: {f.Value}"));

        // A document the reader left in the database for its size means the decision above may be read from less than the row says.
        var notice = egress.Oversized.Count == 0 ? string.Empty : "Too large to display: " + OversizedValue.Describe(egress.Oversized);
        if (notice.Length > 0)
        {
            fields.Insert(0, new AlertField("unavailable", notice));
            pretty = notice + Environment.NewLine + pretty;
        }

        return new AlertItem(egress.Id, egress.Timestamp)
        {
            Kind = AlertKinds.Egress,
            Severity = Normalize(egress.Severity),
            RuleId = "network.egress",
            Headline = headline,
            Action = "egress " + decision,
            TargetRef = egress.Target,
            Evidence = egress.Reason,
            Source = egress.Connector,
            Connector = egress.Connector.Length > 0 ? egress.Connector : null,
            Tags = egress.LooksLikeLlm ? "llm-shaped" : string.Empty,
            StructuredText = pretty,
            Fields = fields,
            OversizedNotice = notice,
        };
    }
}

public sealed partial class SeverityFilter
{
    /// <summary>True while this severity is one of those the list is narrowed to (some severity is off and this one is on): the tile's selected look.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TileCaption))]
    private bool _isPicked;

    /// <summary>The small line under a tile's number: "filtering" while it narrows the list, "hidden" while it is off, otherwise "alerts".</summary>
    public string TileCaption => IsPicked ? "filtering" : IsEnabled ? "alerts" : "hidden";
}
