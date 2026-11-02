using System.Globalization;
using DefenseClaw.Core.Audit;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// "What needs attention" while protected-file alerts wait to be acknowledged (CUST-422, runtime 1.0.6): one High row naming the newest file, whose button opens
/// the Alerts panel on that kind. The list itself stays on the Alerts panel; the count comes from <see cref="AlertCounts"/>, the same one the badge and the tray show.
/// </summary>
public sealed partial class OverviewPanelViewModel
{
    /// <summary>The id of the row's button: the Alerts panel, on the protected-file kind.</summary>
    internal const string OpenProtectedFileAlertsActionId = "open-protected-file-alerts";

    private string _protectedFileMark = string.Empty;

    /// <summary>What the row would say depends on these; a change rebuilds the list (the alert counts' change event, not the gateway poll, is what carries them).</summary>
    private void RefreshProtectedFileAttention()
    {
        var changes = Services.AlertCounts.Current.ProtectedFileChanges;
        var mark = changes.Count == 0 ? string.Empty : changes.Count.ToString(CultureInfo.InvariantCulture) + "|" + changes[0].Id;
        if (!string.Equals(mark, _protectedFileMark, StringComparison.Ordinal))
        {
            _protectedFileMark = mark;
            BuildAttention(_snapshot);
        }
    }

    /// <summary>Adds the row. Called once from <see cref="BuildAttention"/>. Nothing on a runtime before 1.0.6, which has no such alert.</summary>
    private void AppendProtectedFileAttention(List<AttentionRow> rows)
    {
        if (!Services.Runtime.Capabilities.Has(RuntimeCapability.ProtectedFileWatch))
        {
            return;
        }

        var changes = Services.AlertCounts.Current.ProtectedFileChanges;
        if (changes.Count == 0)
        {
            return;
        }

        var newest = changes[0];
        var agent = string.IsNullOrWhiteSpace(newest.Connector) ? "an agent" : newest.Connector;
        var more = changes.Count switch
        {
            1 => string.Empty,
            2 => " One more change is waiting too.",
            _ => $" {(changes.Count - 1).ToString("N0", CultureInfo.CurrentCulture)} more changes are waiting too.",
        };

        rows.Add(new AttentionRow
        {
            Title = changes.Count == 1
                ? "A protected file changed during an agent call"
                : $"{changes.Count.ToString("N0", CultureInfo.CurrentCulture)} protected-file changes during agent calls",
            Detail = $"{ProtectedFileAlert.FileLabel(newest.Target)} changed while a tool call of {agent} was running.{more} " +
                     ProtectedFileWatchFacts.Attribution + " " + ProtectedFileWatchFacts.BlocksNothing,
            SeverityKey = "High",
            Actions = new AttentionActions(new[]
            {
                new AttentionAction(
                    OpenProtectedFileAlertsActionId,
                    "Review in Alerts",
                    "Opens the Alerts panel on protected-file changes: the file, the agent, the session, the tool call and the hashes before and after."),
            }),
        });
    }
}
