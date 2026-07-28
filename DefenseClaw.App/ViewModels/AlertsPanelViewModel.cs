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
/// </summary>
public sealed partial class AlertsPanelViewModel : PanelViewModelBase
{
    /// <summary>How many findings to pull from audit.db when /alerts cannot answer.</summary>
    private const int FallbackLimit = 100;

    private readonly List<AlertItem> _all = new();
    private readonly DispatcherTimer _clock;
    private int _loadingFallback;
    private DateTimeOffset _lastFallbackLoad = DateTimeOffset.MinValue;

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

        Services.Monitor.StateChanged += OnStateChanged;

        // Relative timestamps go stale silently, which is the worst way for a monitoring
        // panel to lie. One timer re-stamps the visible rows.
        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _clock.Tick += (_, _) => RestampTimes();
        _clock.Start();
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

    partial void OnFilterTextChanged(string value) => ApplyFilters();

    partial void OnCollapseRepeatsChanged(bool value) => ApplyFilters();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        var snapshot = await Services.Monitor.RefreshAsync();
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

    private void OnStateChanged(object? sender, GatewaySnapshotEventArgs e) => Apply(e.Snapshot);

    /// <summary>
    /// Rebuilds the master list from one snapshot. <c>NotConnected</c> and
    /// <c>Unauthorized</c> are informational: the panel says why the stream is quiet and
    /// falls back to the audit database rather than showing an error.
    /// </summary>
    private void Apply(GatewaySnapshot snapshot)
    {
        if (snapshot.AlertsUnavailable is { Length: > 0 } reason)
        {
            SourceNote = reason;

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

        _all.Clear();
        foreach (var alert in snapshot.RecentAlerts)
        {
            _all.Add(AlertItem.FromGateway(alert));
        }

        SourceNote = $"GET /alerts · newest {GatewayMonitor.AlertLimit} · refreshed {Relative(snapshot.PolledAt)}";
        SetEmpty(
            "No alerts in the last poll",
            "The gateway is answering and reported no findings in the most recent window.");
        ApplyFilters();
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

            _all.Clear();
            foreach (var row in page.Events)
            {
                _all.Add(AlertItem.FromAudit(row));
            }

            SourceNote += $" · showing {_all.Count} finding(s) from audit.db instead";
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

    private void SetEmpty(string title, string detail)
    {
        EmptyTitle = title;
        EmptyDetail = detail;
    }

    /// <summary>
    /// Severity toggles, then substring match, then optional collapse. Collapsing keys on
    /// signature+action, which is exactly the axis the agent-generated noise repeats on.
    /// </summary>
    private void ApplyFilters()
    {
        var allowed = SeverityFilters
            .Where(f => f.IsEnabled)
            .Select(f => f.Severity)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var needle = FilterText.Trim();
        var selectedKey = SelectedAlert?.Key;

        IEnumerable<AlertItem> query = _all;

        if (allowed.Count != SeverityFilters.Count)
        {
            query = query.Where(item => allowed.Contains(item.Severity));
        }

        if (needle.Length > 0)
        {
            query = query.Where(item => item.Matches(needle));
        }

        var filtered = query.ToList();
        var rows = CollapseRepeats ? Collapse(filtered) : filtered;

        Alerts.Clear();
        foreach (var row in rows)
        {
            Alerts.Add(row);
        }

        IsEmpty = Alerts.Count == 0;
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

    private void RestampTimes()
    {
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
