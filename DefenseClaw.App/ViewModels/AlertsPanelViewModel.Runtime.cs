using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DefenseClaw.App.Services;
using DefenseClaw.Core.Audit;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// The runtime's own alert list in the Alerts panel (CUST-374), on a runtime that has <c>alerts --json</c> (<see cref="DefenseClaw.Core.Runtime.RuntimeCapability.RuntimeAlerts"/>).
/// <para>
/// <b>What it adds to the database queue.</b> The queue (<see cref="AlertQueueReader"/>) is still the source and still decides what is acknowledged. The
/// runtime's list (<see cref="RuntimeAlertsService"/>) is merged into it by alert id: a runtime alert the queue does not hold (an enforcement action, a
/// connector hook block, a health failure) is added, one both hold is listed once, and each row gains the runtime's facts in the inspector (decision, rule,
/// route, scanner, location, the agent columns). The queue itself leaves detection-only findings out on such a runtime, as the runtime's list does, and
/// the panel says how many of the last 24 hours that hides.
/// </para>
/// <para>
/// <b>Never in the way.</b> The list is asked for in the background about once a minute while the panel is open (and at once after Refresh or an
/// acknowledge); the panel shows what it has and re-reads the queue when an answer arrives. A run that fails keeps the last good list and puts a
/// "Alerts may be stale" line above the table; it does not block, hide or empty anything. On a runtime without the capability none of this runs.
/// </para>
/// </summary>
public sealed partial class AlertsPanelViewModel
{
    /// <summary>The line above the list while the runtime's list could not be refreshed: "Alerts may be stale: the runtime's alert command failed: ...". Empty otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRuntimeStale))]
    private string _runtimeStaleText = string.Empty;

    /// <summary>"3 detection-only findings of the last 24 hours are not listed: ..."; empty when there are none or the runtime does not leave them out.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetectionOnly))]
    private string _detectionOnlyText = string.Empty;

    public bool HasRuntimeStale => RuntimeStaleText.Length > 0;

    public bool HasDetectionOnly => DetectionOnlyText.Length > 0;

    private void OnRuntimeAlertsChanged(object? sender, EventArgs e) => _ = RefreshQueueAsync();

    /// <summary>
    /// The runtime's list for this queue read, or null on a runtime without the capability (the notes are cleared then). Asks for a fresh one when it is
    /// due; what is returned is what is known now, never a wait.
    /// </summary>
    private RuntimeAlertState? RuntimeForQueue()
    {
        var service = Services.RuntimeAlerts;
        if (!service.IsEnabled)
        {
            UpdateRuntimeNotes(null);
            return null;
        }

        service.Request(force: false);
        var state = service.Current;
        UpdateRuntimeNotes(state);
        return state;
    }

    private void UpdateRuntimeNotes(RuntimeAlertState? state)
    {
        RuntimeStaleText = state?.StaleReason is { Length: > 0 } reason ? "Alerts may be stale: " + reason : string.Empty;
        DetectionOnlyText = state is { DetectionOnly24h: > 0 } counted ? DetectionOnlySentence(counted.DetectionOnly24h!.Value) : string.Empty;
    }

    /// <summary>The detection-only note as the empty state ends with it (a leading space), or nothing.</summary>
    private string DetectionOnlySuffix() => DetectionOnlyText.Length > 0 ? " " + DetectionOnlyText : string.Empty;

    /// <summary>The sentence <c>defenseclaw alerts</c> prints under its table (db.py's count, cmd_alerts.py <c>_detection_only_note</c>).</summary>
    internal static string DetectionOnlySentence(int count)
    {
        var what = count == 1
            ? "1 detection-only finding of the last 24 hours is"
            : count.ToString("N0", CultureInfo.CurrentCulture) + " detection-only findings of the last 24 hours are";
        return $"{what} not listed: a rule matched but could not decide the call. `defenseclaw audit export --since 24h` lists them.";
    }

    /// <summary>
    /// The queue's rows plus the runtime's that the queue does not hold, newest first, at most <see cref="AlertQueueReader.DefaultWindowLimit"/>.
    /// A row both hold is the queue's (it is the same audit row); a queue row the runtime's list lacks stays, because the runtime's list is one minute
    /// older than the database at best and an alert acknowledged since is already out of the queue.
    /// </summary>
    internal static IReadOnlyList<AlertQueueItem> MergeRuntime(IReadOnlyList<AlertQueueItem> queue, IReadOnlyList<RuntimeAlertRow> runtime)
    {
        if (runtime.Count == 0)
        {
            return queue;
        }

        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in queue)
        {
            if (item.Id.Length > 0)
            {
                _ = known.Add(item.Id);
            }
        }

        List<AlertQueueItem>? merged = null;
        foreach (var row in runtime)
        {
            if (known.Contains(row.Id))
            {
                continue;
            }

            merged ??= new List<AlertQueueItem>(queue);
            merged.Add(row.ToQueueItem());
        }

        if (merged is null)
        {
            return queue;
        }

        // Stable: rows of one instant keep the queue's order, and the queue's come first.
        return merged.OrderByDescending(item => item.Timestamp).Take(AlertQueueReader.DefaultWindowLimit).ToList();
    }

    private static IReadOnlyDictionary<string, RuntimeAlertRow> RuntimeRowsById(IReadOnlyList<RuntimeAlertRow> rows)
    {
        var byId = new Dictionary<string, RuntimeAlertRow>(rows.Count, StringComparer.Ordinal);
        foreach (var row in rows)
        {
            _ = byId.TryAdd(row.Id, row);
        }

        return byId;
    }

    /// <summary>A marker for the list's ids and facts: a read that finds the same queue and the same runtime list has nothing to rebuild.</summary>
    private static string RuntimeSignature(IReadOnlyList<RuntimeAlertRow> rows)
    {
        var hash = new HashCode();
        foreach (var row in rows)
        {
            hash.Add(row.Id, StringComparer.Ordinal);
            hash.Add(row.Facts.Count);
        }

        return "\0runtime:" + rows.Count.ToString(CultureInfo.InvariantCulture) + ":" + hash.ToHashCode().ToString(CultureInfo.InvariantCulture);
    }
}

public sealed partial class AlertItem
{
    /// <summary>True once the runtime's facts have been added to this row (so a rebuild does not add them twice).</summary>
    public bool HasRuntimeFacts { get; private set; }

    /// <summary>
    /// This row with the runtime's facts at the top of its attributes (<c>runtime.decision</c>, <c>runtime.rule</c> ...), and the runtime's rule as
    /// its headline and the runtime's target as its target where the audit row left them empty (a finding row's own are blank or <c>finding.observed</c>).
    /// A copy: the rows already shown are not changed under the list.
    /// </summary>
    public AlertItem WithRuntime(RuntimeAlertRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        var copy = CloneForGroup();
        copy.HasRuntimeFacts = true;
        copy.Fields = row.Facts.Count == 0
            ? Fields
            : row.Facts.Select(f => new AlertField("runtime." + f.Name, f.Value)).Concat(Fields).ToList();

        if (RuntimeRule(row) is { Length: > 0 } rule && (Headline.Length == 0 || Headline == Action || Headline == "finding.observed"))
        {
            copy.Headline = rule;
        }

        if (copy.TargetRef.Length == 0 && row.Target is { Length: > 0 } target)
        {
            copy.TargetRef = target;
        }

        return copy;
    }

    /// <summary>A row for an alert only the runtime listed and whose audit row could not be read: what the runtime's list says, nothing invented.</summary>
    public static AlertItem FromRuntime(RuntimeAlertRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return new AlertItem(row.Id, row.Timestamp)
        {
            HasAuditId = HasUsableId(row.Id),
            HasRuntimeFacts = true,
            Severity = row.Severity.ToStoredValue(),
            Kind = KindOf(row.Action, FactOf(row, "scanner")),
            RawTarget = row.Target ?? string.Empty,
            RuleId = FactOf(row, "rule").Split(':')[0].Trim(),
            Headline = RuntimeRule(row) is { Length: > 0 } rule ? rule : row.Details.Length > 0 ? row.Details : row.Action,
            Action = row.Action,
            TargetRef = row.Target ?? string.Empty,
            Scanner = FactOf(row, "scanner"),
            Source = row.Actor.Length > 0 ? row.Actor : row.Connector ?? string.Empty,
            Connector = row.Connector,
            Details = row.Details,
            SessionId = FactOf(row, "session_id"),
            StructuredText = "{}",
            Fields = row.Facts.Select(f => new AlertField("runtime." + f.Name, f.Value)).ToList(),
        };
    }

    private static string FactOf(RuntimeAlertRow row, string name) =>
        row.Facts.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.Ordinal))?.Value ?? string.Empty;

    private static string RuntimeRule(RuntimeAlertRow row) => FactOf(row, "rule");
}
