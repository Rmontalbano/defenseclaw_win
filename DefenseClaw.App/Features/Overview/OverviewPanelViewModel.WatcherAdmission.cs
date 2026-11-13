using System.Globalization;
using DefenseClaw.Core.Gateway.Models;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// "What needs attention" while the watcher says admission scanning is off for some connector roots (runtime 1.0.9, <see cref="WatcherAdmissionHealth"/>). New
/// skills and plugins for those connectors are then not scanned before they are admitted, and the Watchdog tile only counted directories. On Windows the usual
/// cause is a skill or plugin folder that is a junction or symbolic link: the runtime refuses every linked root there. One Medium row, in the runtime's words
/// plus that Windows cause; no button, because the app has no action that opens one gateway log line (the roots are named only in the gateway log). Nothing
/// without the watcher's <c>watch_roots_unavailable</c> key, which older runtimes do not send.
/// </summary>
public sealed partial class OverviewPanelViewModel
{
    /// <summary>The row title starts with this; the connectors follow.</summary>
    internal const string WatcherAdmissionTitlePrefix = "Admission scanning is off for ";

    /// <summary>Adds the row. Called from <see cref="AppendOverviewHealthAttention"/>.</summary>
    private void AppendWatcherAdmissionAttention(List<AttentionRow> rows)
    {
        if (WatcherAdmissionHealth.From(_snapshot.Health?.Watcher) is { } admission)
        {
            rows.Add(WatcherAdmissionRow(admission));
        }
    }

    internal static AttentionRow WatcherAdmissionRow(WatcherAdmissionHealth admission)
    {
        ArgumentNullException.ThrowIfNull(admission);

        var roots = admission.RootsUnavailable.ToString("N0", CultureInfo.CurrentCulture);
        var detail = $"The watcher cannot watch {roots} skill or plugin folder{(admission.RootsUnavailable == 1 ? string.Empty : "s")} for {admission.ConnectorLabel}, " +
                     "so new skills and plugins there are not scanned before they are admitted. " +
                     "On Windows a skill or plugin folder that is a junction or a symbolic link is not scanned (a linked folder is refused); use a real folder instead. " +
                     "The gateway log names each folder.";

        if (admission.WritableFolders.Count > 0)
        {
            var more = admission.FoldersOmitted > 0 ? $" and {admission.FoldersOmitted.ToString("N0", CultureInfo.CurrentCulture)} more" : string.Empty;
            detail += " Folders other accounts can write, as the gateway reports them: " + string.Join("; ", admission.WritableFolders) + more + ".";
        }

        return new AttentionRow
        {
            Title = WatcherAdmissionTitlePrefix + admission.ConnectorLabel,
            Detail = detail,
            SeverityKey = "Medium",
        };
    }
}
