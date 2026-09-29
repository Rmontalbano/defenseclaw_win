using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.Core.Audit;
using DefenseClaw.Core.Gateway.Models;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// Live security findings: the last <see cref="GatewayMonitor.AlertLimit"/> entries from
/// <c>/alerts</c>, with the audit database as a fallback when the gateway cannot serve
/// them.
/// <para>
/// <b>Why the filters are not optional.</b> On a development box the alert stream is
/// dominated by the agent that is operating the box — every <c>$env:</c> reference trips
/// <c>CMD-ENV-DUMP</c> at HIGH. So this panel ships three affordances that make that
/// stream readable: severity toggles, a substring filter across rule id / title / action /
/// evidence, and a collapse-repeats switch that folds identical signature+action pairs
/// into one row with a count.
/// </para>
/// <para>
/// <b>What re-projects, and when.</b> The health poll runs every five seconds but
/// <c>/alerts</c> is only re-read every thirty, and <see cref="GatewayMonitor"/> hands back
/// the same <see cref="GatewaySnapshot.RecentAlerts"/> instance whenever a fresh answer shows
/// the same findings. So a poll that brings that same list re-projects nothing — it only
/// refreshes the "refreshed …" note — and a poll that brings a different list projects only
/// the alerts it has not seen (an alert id names an immutable audit row, so an already
/// projected row is reused, which also skips its JSON flattening). The bound list is then
/// merged by alert key rather than cleared and refilled, so scroll position and the selected
/// row survive. None of this runs while the panel is not on screen: it listens to
/// <see cref="GatewayMonitor.PollCompleted"/> and runs its relative-time clock only between
/// <see cref="OnActivated"/> and <see cref="OnDeactivated"/>.
/// </para>
/// <para>
/// <b>What "refreshed" means.</b> The health poll's <see cref="GatewaySnapshot.PolledAt"/>
/// says nothing about the alert list, which is re-read on its own slower cadence. The note
/// above the list therefore prints <see cref="GatewaySnapshot.AlertsFetchedAt"/> — when
/// <c>/alerts</c> last answered — and the Refresh button forces that read
/// (<see cref="GatewayMonitor.RefreshAlertsNowAsync"/>) instead of re-running a health poll
/// that would skip it. While the list came from the audit fallback, the note keeps saying
/// so on every poll, not only on the poll that loaded it.
/// </para>
/// <para>
/// <b>Filters act on the normalized severity.</b> A finding's stored severity can be a
/// spelling the toggles do not have (<c>WARN</c>, <c>FATAL</c>, <c>MODERATE</c>);
/// <see cref="AlertItem.SeverityKey"/> folds those into the bucket the row is coloured as,
/// and the toggles filter on that same key, so a row is always hidden by exactly the toggle
/// its chip looks like.
/// </para>
/// </summary>
public sealed partial class AlertsPanelViewModel : PanelViewModelBase
{
    /// <summary>How many findings to pull from audit.db when /alerts cannot answer.</summary>
    private const int FallbackLimit = 100;

    private readonly List<AlertItem> _all = new();
    private readonly DispatcherTimer _clock;
    private int _loadingFallback;
    private DateTimeOffset _lastFallbackLoad = DateTimeOffset.MinValue;

    /// <summary>
    /// Why <c>/alerts</c> is not being served, as of the last snapshot applied; null while it
    /// is. The fallback load reads it when it finishes, so a load that outlives the outage
    /// does not decorate a note that no longer describes one.
    /// </summary>
    private string? _alertsUnavailableReason;

    /// <summary>
    /// The empty-state text the current <i>source</i> calls for. What the overlay actually
    /// shows also depends on whether the filters are hiding loaded alerts; see
    /// <see cref="ShowEmptyText"/>.
    /// </summary>
    private string _sourceEmptyTitle = "No alerts yet";

    private string _sourceEmptyDetail = "The first alert poll has not completed.";

    /// <summary>
    /// The monitor's alert list that <see cref="_all"/> was last projected from; null when
    /// <see cref="_all"/> came from audit.db or has not been filled. A poll whose
    /// <see cref="GatewaySnapshot.RecentAlerts"/> is this very instance has nothing to project.
    /// </summary>
    private IReadOnlyList<GatewayAlert>? _appliedAlerts;

    /// <summary>
    /// True while <see cref="_all"/> holds audit.db rows. The two sources project the same
    /// alert to slightly different rows (the audit path has no tags or confidence), so rows
    /// are never reused, and the bound list never merged, across a switch of source.
    /// </summary>
    private bool _allFromAudit;

    [ObservableProperty]
    private string _filterText = string.Empty;

    [ObservableProperty]
    private bool _collapseRepeats;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private AlertItem? _selectedAlert;

    [ObservableProperty]
    private string _sourceNote = "Waiting for the first alert poll…";

    [ObservableProperty]
    private string _countSummary = string.Empty;

    [ObservableProperty]
    private bool _isEmpty = true;

    [ObservableProperty]
    private string _emptyTitle = "No alerts yet";

    [ObservableProperty]
    private string _emptyDetail = "The first alert poll has not completed.";

    public AlertsPanelViewModel(AppServices services)
        : base(services)
    {
        foreach (var severity in new[] { "CRITICAL", "HIGH", "MEDIUM", "LOW", "INFO" })
        {
            var filter = new SeverityFilter(severity);
            filter.PropertyChanged += (_, _) => ApplyFilters();
            SeverityFilters.Add(filter);
        }

        // Relative timestamps go stale silently, which is the worst way for a monitoring
        // panel to lie. One timer re-stamps the rows - but only while someone can read them;
        // OnActivated starts it and re-stamps once so a returning operator never sees old text.
        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _clock.Tick += (_, _) => RestampTimes();
    }

    public override string Title => "Alerts";

    public override string Description =>
        "Security findings from the gateway, newest first, with severity and signature filters.";

    /// <summary>The filtered, optionally collapsed view bound to the list.</summary>
    public ObservableCollection<AlertItem> Alerts { get; } = new();

    public ObservableCollection<SeverityFilter> SeverityFilters { get; } = new();

    /// <summary>Drives the detail pane's placeholder; no converter needed for the inverse.</summary>
    public bool HasSelection => SelectedAlert is not null;

    public override Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Apply(Services.Monitor.Current);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Catch-up first, then listen: <c>Apply</c> compares against what is already projected,
    /// so returning to an unchanged alert list re-projects nothing, and a list that moved on
    /// while the panel was away is rebuilt before it is seen.
    /// </summary>
    protected override void OnActivated()
    {
        Apply(Services.Monitor.Current);
        RestampTimes();

        Services.Monitor.PollCompleted += OnPollCompleted;
        _clock.Start();
    }

    protected override void OnDeactivated()
    {
        Services.Monitor.PollCompleted -= OnPollCompleted;
        _clock.Stop();
    }

    partial void OnFilterTextChanged(string value) => ApplyFilters();

    partial void OnCollapseRepeatsChanged(bool value) => ApplyFilters();

    /// <summary>
    /// Forces a real <c>/alerts</c> read. <see cref="GatewayMonitor.RefreshAsync"/> would only
    /// re-run the health poll, which skips <c>/alerts</c> until its 30 s throttle expires — the
    /// button would then relabel a list that was never re-fetched. The fallback's own 30 s
    /// throttle is reset too, so a gateway that is still down re-reads audit.db now.
    /// </summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        var snapshot = await Services.Monitor.RefreshAlertsNowAsync();
        _lastFallbackLoad = DateTimeOffset.MinValue;
        Apply(snapshot);
    }

    [RelayCommand]
    private void ClearFilters()
    {
        FilterText = string.Empty;
        CollapseRepeats = false;
        foreach (var filter in SeverityFilters)
        {
            filter.IsEnabled = true;
        }
    }

    /// <summary>
    /// Every poll, not just StateChanged: the fallback's 30 s reload cadence and the
    /// "refreshed …" note both ride the poll, and StateChanged is silent on an idle box.
    /// <c>Apply</c> decides what, if anything, actually needs rebuilding.
    /// </summary>
    private void OnPollCompleted(object? sender, GatewaySnapshotEventArgs e) => Apply(e.Snapshot);

    /// <summary>
    /// Brings the master list in line with one snapshot. <c>NotConnected</c> and
    /// <c>Unauthorized</c> are informational: the panel says why the stream is quiet and
    /// falls back to the audit database rather than showing an error.
    /// </summary>
    private void Apply(GatewaySnapshot snapshot)
    {
        if (snapshot.AlertsUnavailable is { Length: > 0 } reason)
        {
            _alertsUnavailableReason = reason;

            // Rebuilt on every poll, so the audit suffix has to be part of the rebuild: the
            // fallback only reloads every 30 s, and a note that dropped its suffix for the
            // five polls in between claimed the list below had no source.
            SourceNote = WithFallbackNote(reason);

            // The master list no longer mirrors /alerts, so the next answer must re-project.
            _appliedAlerts = null;

            if (Services.Audit.Exists && DateTimeOffset.UtcNow - _lastFallbackLoad > TimeSpan.FromSeconds(30))
            {
                _ = LoadFallbackAsync();
            }
            else if (_all.Count == 0)
            {
                SetEmpty("No alerts to show", reason);
                ApplyFilters();
            }

            return;
        }

        _alertsUnavailableReason = null;

        // The Initial snapshot: no poll has completed, so there is no answer to describe (its
        // AlertsUnavailable is null, which would otherwise read as "the gateway reported none").
        if (snapshot.PolledAt == DateTimeOffset.MinValue)
        {
            SourceNote = "Waiting for the first alert poll…";
            SetEmpty("No alerts yet", "The first alert poll has not completed.");
            return;
        }

        // AlertsFetchedAt, not PolledAt: the health poll runs every few seconds but /alerts is
        // re-read on its own slower cadence, so PolledAt would say "just now" about a list
        // that is up to 30 s old. Null means /alerts has never answered.
        var fetched = snapshot.AlertsFetchedAt is { } at ? Relative(at) : "never";
        SourceNote = $"GET /alerts · newest {GatewayMonitor.AlertLimit} · fetched {fetched}";
        SetEmpty(
            "No alerts in the last poll",
            "The gateway is answering and reported no findings in the most recent window.");

        // The same list instance means the same findings (see GatewayMonitor), and the
        // filters re-run themselves when they change - nothing else can alter the result.
        if (ReferenceEquals(_appliedAlerts, snapshot.RecentAlerts))
        {
            return;
        }

        ProjectGatewayAlerts(snapshot.RecentAlerts);
        ApplyFilters();
    }

    /// <summary>
    /// Rebuilds the master list from a /alerts answer, reusing the row for any alert already
    /// projected: an alert id is an immutable audit row, and the row is where the cost is
    /// (the attribute bag is sorted, flattened and pretty-printed twice). Only alerts new
    /// since the last projection pay for it. An alert without an id gets a fresh identity per
    /// projection and is never reused.
    /// </summary>
    private void ProjectGatewayAlerts(IReadOnlyList<GatewayAlert> alerts)
    {
        Dictionary<string, AlertItem>? reusable = null;
        if (_allFromAudit)
        {
            // Different source, different row shape; the bound list starts over too.
            if (Alerts.Count > 0)
            {
                Alerts.Clear();
            }
        }
        else if (_all.Count > 0)
        {
            reusable = new Dictionary<string, AlertItem>(_all.Count, StringComparer.Ordinal);
            foreach (var item in _all)
            {
                _ = reusable.TryAdd(item.Key, item);
            }
        }

        var projected = new List<AlertItem>(alerts.Count);
        foreach (var alert in alerts)
        {
            if (alert.Id.Length > 0 && reusable is not null && reusable.Remove(alert.Id, out var existing))
            {
                projected.Add(existing);
            }
            else
            {
                projected.Add(AlertItem.FromGateway(alert));
            }
        }

        _all.Clear();
        _all.AddRange(projected);
        _allFromAudit = false;
        _appliedAlerts = alerts;
    }

    /// <summary>
    /// Fallback source. <c>security.finding</c> rows carry the same dotted attribute keys
    /// as the REST payload, so the same item shape renders either way.
    /// </summary>
    private async Task LoadFallbackAsync()
    {
        if (Interlocked.Exchange(ref _loadingFallback, 1) == 1)
        {
            return;
        }

        try
        {
            var query = new AuditQuery
            {
                Buckets = new[] { "security.finding" },
                Limit = FallbackLimit,
            };

            var page = await Services.Audit.QueryAsync(query, CancellationToken.None);
            _lastFallbackLoad = DateTimeOffset.UtcNow;

            if (!_allFromAudit && Alerts.Count > 0)
            {
                // Different source, different row shape; see _allFromAudit.
                Alerts.Clear();
            }

            _all.Clear();
            foreach (var row in page.Events)
            {
                _all.Add(AlertItem.FromAudit(row));
            }

            _allFromAudit = true;
            _appliedAlerts = null;

            // Rebuilt from the reason rather than appended to whatever the note is now: a poll
            // may have replaced it while the query ran, and the gateway may even have come
            // back (then the next poll re-projects /alerts and the note is not this one's).
            if (_alertsUnavailableReason is { } reason)
            {
                SourceNote = WithFallbackNote(reason);
            }

            SetEmpty(
                "No findings recorded",
                "The gateway is not serving alerts and audit.db holds no security.finding rows yet.");
            ApplyFilters();
        }
#pragma warning disable CA1031 // The fallback is a nicety; a locked DB must not fault the panel.
        catch (Exception ex) when (ex is SqliteException or IOException or InvalidOperationException)
        {
            SetEmpty("Alerts unavailable", ex.Message);
            ApplyFilters();
        }
#pragma warning restore CA1031
        finally
        {
            Interlocked.Exchange(ref _loadingFallback, 0);
        }
    }

    /// <summary>
    /// The source note plus, while <see cref="_all"/> holds audit.db rows, the statement that
    /// the list below came from there. The one place the suffix is built, so every poll and
    /// the fallback load agree on it.
    /// </summary>
    private string WithFallbackNote(string reason) =>
        _allFromAudit ? $"{reason} · showing {_all.Count} finding(s) from audit.db instead" : reason;

    /// <summary>Records what the source says about an empty list, then shows the right text.</summary>
    private void SetEmpty(string title, string detail)
    {
        _sourceEmptyTitle = title;
        _sourceEmptyDetail = detail;
        ShowEmptyText();
    }

    /// <summary>
    /// "No alerts" and "alerts exist but the filters hide them" are different states with
    /// different remedies; the source's own message ("the gateway reported no findings")
    /// would be false in the second. Runs after every filter pass and every
    /// <see cref="SetEmpty"/>, so neither ordering leaves the wrong text up.
    /// </summary>
    private void ShowEmptyText()
    {
        if (_all.Count > 0 && Alerts.Count == 0)
        {
            EmptyTitle = "No alerts match the current filters";
            EmptyDetail =
                $"{_all.Count} alert(s) are loaded but hidden by the severity toggles or the text filter. " +
                "Use Clear to show them again.";
            return;
        }

        EmptyTitle = _sourceEmptyTitle;
        EmptyDetail = _sourceEmptyDetail;
    }

    /// <summary>
    /// Severity toggles, then substring match, then optional collapse. Collapsing keys on
    /// signature+action, which is exactly the axis the agent-generated noise repeats on.
    /// <para>
    /// The toggles compare <see cref="AlertItem.SeverityKey"/>, not the stored spelling: the
    /// toggles only spell five severities, and <c>WARN</c> / <c>WARNING</c> / <c>MODERATE</c> /
    /// <c>FATAL</c> rows (which the chip colours as Medium / Critical) would otherwise match
    /// none of them and vanish the moment any toggle was switched off, with no toggle able to
    /// bring them back. Anything the key does not recognise is Info, so the five keys
    /// partition every alert.
    /// </para>
    /// </summary>
    private void ApplyFilters()
    {
        var anyToggleOff = SeverityFilters.Any(f => !f.IsEnabled);
        var allowed = SeverityFilters
            .Where(f => f.IsEnabled)
            .Select(f => f.SeverityKey)
            .ToHashSet(StringComparer.Ordinal);

        var needle = FilterText.Trim();
        var selectedKey = SelectedAlert?.Key;

        IEnumerable<AlertItem> query = _all;

        if (anyToggleOff)
        {
            query = query.Where(item => allowed.Contains(item.SeverityKey));
        }

        if (needle.Length > 0)
        {
            query = query.Where(item => item.Matches(needle));
        }

        var filtered = query.ToList();
        var rows = CollapseRepeats ? Collapse(filtered) : filtered;

        // Merged by alert key, not cleared and refilled: the ListView keeps the containers,
        // the scroll offset and the selected row for every alert that is still listed. Within
        // one source a key names one immutable finding, so a matched row is always current
        // - the only field that varies under a key is a collapsed group's repeat count, and
        // that is copied across in place.
        SyncCollection(
            Alerts,
            rows,
            static alert => alert.Key,
            static (_, _) => true,
            static (existing, wanted) => existing.RepeatCount = wanted.RepeatCount);

        IsEmpty = Alerts.Count == 0;
        ShowEmptyText();
        CountSummary = _all.Count == 0
            ? string.Empty
            : CollapseRepeats
                ? $"{Alerts.Count} group(s) · {filtered.Count} of {_all.Count} alerts"
                : $"{Alerts.Count} of {_all.Count} alerts";

        SelectedAlert = selectedKey is null
            ? null
            : Alerts.FirstOrDefault(a => string.Equals(a.Key, selectedKey, StringComparison.Ordinal));
    }

    private static List<AlertItem> Collapse(IReadOnlyList<AlertItem> items)
    {
        var groups = new List<AlertItem>();
        var index = new Dictionary<string, AlertItem>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            if (index.TryGetValue(item.GroupKey, out var existing))
            {
                existing.RepeatCount++;
                continue;
            }

            // The newest occurrence represents the group, and counts itself.
            var clone = item.CloneForGroup();
            index[item.GroupKey] = clone;
            groups.Add(clone);
        }

        return groups;
    }

    /// <summary>
    /// Re-stamps every row, not just the listed ones: rows are now reused across polls, so a
    /// row a filter is hiding would otherwise carry its old text back into view. (Collapsed
    /// group rows are not in <see cref="_all"/>, hence both passes.)
    /// </summary>
    private void RestampTimes()
    {
        foreach (var item in _all)
        {
            item.Restamp();
        }

        foreach (var item in Alerts)
        {
            item.Restamp();
        }
    }

    internal static string Relative(DateTimeOffset value)
    {
        if (value == DateTimeOffset.MinValue)
        {
            return "never";
        }

        var delta = DateTimeOffset.UtcNow - value;
        return delta switch
        {
            { TotalSeconds: < 5 } => "just now",
            { TotalSeconds: < 60 } => $"{(int)delta.TotalSeconds}s ago",
            { TotalMinutes: < 60 } => $"{(int)delta.TotalMinutes}m ago",
            { TotalHours: < 24 } => $"{(int)delta.TotalHours}h ago",
            _ => $"{(int)delta.TotalDays}d ago",
        };
    }
}

/// <summary>One severity toggle above the list.</summary>
public sealed partial class SeverityFilter : ObservableObject
{
    [ObservableProperty]
    private bool _isEnabled = true;

    public SeverityFilter(string severity)
    {
        Severity = severity;
    }

    /// <summary>Stored spelling, e.g. <c>HIGH</c>.</summary>
    public string Severity { get; }

    public string SeverityKey => AlertItem.KeyFor(Severity);
}

/// <summary>One key/value row in the detail pane.</summary>
public sealed record AlertField(string Name, string Value);

/// <summary>
/// One alert row. Built from either <c>/alerts</c> or a <c>security.finding</c> audit row —
/// both carry the same dotted attribute names, so the view never has to know which.
/// </summary>
public sealed partial class AlertItem : ObservableObject
{
    [ObservableProperty]
    private string _relativeTime = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRepeated))]
    [NotifyPropertyChangedFor(nameof(RepeatText))]
    private int _repeatCount = 1;

    private AlertItem(string key, DateTimeOffset timestamp)
    {
        Key = key;
        Timestamp = timestamp;
        RelativeTime = AlertsPanelViewModel.Relative(timestamp);
    }

    /// <summary>Stable identity, used to keep the selection across refreshes.</summary>
    public string Key { get; }

    public DateTimeOffset Timestamp { get; }

    public string TimestampText => Timestamp.ToLocalTime().ToString("MMM d HH:mm:ss", CultureInfo.CurrentCulture);

    public string Severity { get; private init; } = "INFO";

    /// <summary>Critical / High / Medium / Low / Info — the view's colour key.</summary>
    public string SeverityKey => KeyFor(Severity);

    public string RuleId { get; private init; } = string.Empty;

    public string Headline { get; private init; } = string.Empty;

    public string Action { get; private init; } = string.Empty;

    public string TargetRef { get; private init; } = string.Empty;

    public string Evidence { get; private init; } = string.Empty;

    public string Scanner { get; private init; } = string.Empty;

    public string Source { get; private init; } = string.Empty;

    public string Tags { get; private init; } = string.Empty;

    public string ConfidenceText { get; private init; } = string.Empty;

    /// <summary>Full attribute bag, pretty-printed for the detail pane.</summary>
    public string StructuredText { get; private init; } = string.Empty;

    public IReadOnlyList<AlertField> Fields { get; private init; } = Array.Empty<AlertField>();

    /// <summary>Collapse axis: the same signature firing on the same action.</summary>
    public string GroupKey => $"{RuleId}|{Action}";

    public bool HasEvidence => Evidence.Length > 0;

    public bool IsRepeated => RepeatCount > 1;

    public string RepeatText => "x" + RepeatCount.ToString(CultureInfo.CurrentCulture);

    public static AlertItem FromGateway(GatewayAlert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);

        var structured = alert.Structured is null
            ? new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            : new Dictionary<string, JsonElement>(alert.Structured, StringComparer.Ordinal);

        return new AlertItem(alert.Id.Length > 0 ? alert.Id : Guid.NewGuid().ToString("n"), alert.Timestamp)
        {
            Severity = Normalize(alert.Severity),
            RuleId = alert.RuleId ?? string.Empty,
            Headline = alert.Title ?? alert.Details ?? alert.Action ?? "(finding)",
            Action = alert.Action ?? string.Empty,
            TargetRef = alert.TargetRef ?? alert.Target ?? string.Empty,
            Evidence = alert.EvidenceSummary ?? string.Empty,
            Scanner = alert.Scanner ?? string.Empty,
            Source = alert.Actor ?? string.Empty,
            Tags = string.Join(", ", alert.Tags.Concat(alert.DataAxes)),
            ConfidenceText = alert.Confidence is { } confidence
                ? confidence.ToString("P0", CultureInfo.CurrentCulture)
                : string.Empty,
            StructuredText = Pretty(structured),
            Fields = ToFields(structured),
        };
    }

    public static AlertItem FromAudit(AuditEvent row)
    {
        ArgumentNullException.ThrowIfNull(row);

        var structured = row.StructuredJson;

        return new AlertItem(row.Id, row.Timestamp)
        {
            Severity = Normalize(row.Severity),
            RuleId = row.StructuredString(GatewayAlert.Keys.RuleId) ?? string.Empty,
            Headline = row.StructuredString(GatewayAlert.Keys.Title) ?? row.Details ?? row.Action,
            Action = row.Action,
            TargetRef = row.StructuredString(GatewayAlert.Keys.TargetRef) ?? row.Target ?? string.Empty,
            Evidence = row.StructuredString(GatewayAlert.Keys.EvidenceSummary) ?? string.Empty,
            Scanner = row.StructuredString(GatewayAlert.Keys.Scanner) ?? string.Empty,
            Source = row.Actor ?? row.Source ?? string.Empty,
            Tags = string.Empty,
            ConfidenceText = string.Empty,
            StructuredText = Pretty(structured),
            Fields = ToFields(structured),
        };
    }

    /// <summary>Copy used as a collapsed group's representative row.</summary>
    public AlertItem CloneForGroup() => new(Key, Timestamp)
    {
        Severity = Severity,
        RuleId = RuleId,
        Headline = Headline,
        Action = Action,
        TargetRef = TargetRef,
        Evidence = Evidence,
        Scanner = Scanner,
        Source = Source,
        Tags = Tags,
        ConfidenceText = ConfidenceText,
        StructuredText = StructuredText,
        Fields = Fields,
    };

    public static string KeyFor(string? severity) => severity?.Trim().ToUpperInvariant() switch
    {
        "CRITICAL" or "FATAL" => "Critical",
        "HIGH" => "High",
        "MEDIUM" or "MODERATE" or "WARN" or "WARNING" => "Medium",
        "LOW" => "Low",
        _ => "Info",
    };

    public bool Matches(string needle) =>
        Contains(RuleId, needle) ||
        Contains(Headline, needle) ||
        Contains(Action, needle) ||
        Contains(TargetRef, needle) ||
        Contains(Evidence, needle) ||
        Contains(Scanner, needle) ||
        Contains(Tags, needle);

    public void Restamp() => RelativeTime = AlertsPanelViewModel.Relative(Timestamp);

    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string? severity) =>
        string.IsNullOrWhiteSpace(severity) ? "INFO" : severity.Trim().ToUpperInvariant();

    private static IReadOnlyList<AlertField> ToFields(IReadOnlyDictionary<string, JsonElement> structured)
    {
        var fields = new List<AlertField>(structured.Count);
        foreach (var (name, value) in structured.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            fields.Add(new AlertField(Shorten(name), Flatten(value)));
        }

        return fields;
    }

    /// <summary>Drops the <c>defenseclaw.</c> prefix every attribute name carries.</summary>
    private static string Shorten(string name) =>
        name.StartsWith("defenseclaw.", StringComparison.Ordinal) ? name["defenseclaw.".Length..] : name;

    private static string Flatten(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Array => string.Join(", ", value.EnumerateArray().Select(Flatten)),
        _ => value.ToString(),
    };

    private static string Pretty(IReadOnlyDictionary<string, JsonElement> structured)
    {
        if (structured.Count == 0)
        {
            return "{}";
        }

        var builder = new StringBuilder("{\n");
        foreach (var (name, value) in structured.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            builder.Append("  \"").Append(name).Append("\": ").Append(value.ToString()).Append(",\n");
        }

        builder.Length -= 2;
        return builder.Append("\n}").ToString();
    }
}
