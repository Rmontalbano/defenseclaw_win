namespace DefenseClaw.Core.Audit;

/// <summary>
/// The one rule for putting the runtime's alert list (<c>alerts --json</c>) together with the audit database's queue (CUST-374), shared by the
/// Alerts panel's list and the kept-fresh counts behind the badge, the tray and Overview (CUST-380), so a row in both sources is one row everywhere.
/// </summary>
public static class RuntimeAlertMerge
{
    /// <summary>
    /// The queue's rows plus the runtime's that the queue does not hold, newest first, at most <see cref="AlertQueueReader.DefaultWindowLimit"/>.
    /// A row both hold (same <c>audit_events.id</c>) is the queue's; a queue row the runtime's list lacks stays, because the runtime's list is one
    /// minute older than the database at best and an alert acknowledged since is already out of the queue. Detection-only findings are left out of
    /// the queue itself on such a runtime (<see cref="DetectionOnlyFindings"/>) and the runtime's list never carries them.
    /// </summary>
    public static IReadOnlyList<AlertQueueItem> Merge(IReadOnlyList<AlertQueueItem> queue, IReadOnlyList<RuntimeAlertRow> runtime)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(runtime);

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
}
