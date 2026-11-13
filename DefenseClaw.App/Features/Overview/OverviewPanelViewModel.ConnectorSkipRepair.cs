using DefenseClaw.Core.Gateway.Models;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// The repair the gateway sends beside a skipped connector's reason (<c>connectors_not_started_repairs</c>, runtime 1.0.9: a plugin folder another account can
/// write). Before 1.0.9 the repair was part of the reason text. It goes on the connector's existing row: the fix as the row's copyable command (the app never
/// builds a command from <c>/health</c> text and never runs it), and the manual step as words. Without the key the row is exactly what it was.
/// </summary>
public sealed partial class OverviewPanelViewModel
{
    /// <summary>The words added to the row's detail when the gateway left part of the repair out.</summary>
    internal const string RepairTooLongText = "Part of the repair is too long to show; the gateway log has it.";

    /// <summary>Adds <paramref name="skip"/>'s repair to <paramref name="row"/>; the row itself when the gateway sent none.</summary>
    internal static AttentionRow WithRepair(AttentionRow row, ConnectorSkip skip)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(skip);

        if (skip.Repair is not { HasContent: true } repair)
        {
            return row;
        }

        var detail = row.Detail;
        if (repair.Fix is not null)
        {
            detail += " The command below is the gateway's fix.";
        }

        if (repair.ByHand is not null)
        {
            detail += " By hand: " + (repair.ByHand.EndsWith('.') ? repair.ByHand : repair.ByHand + ".");
        }

        if (repair.OmittedBytes > 0)
        {
            detail += " " + RepairTooLongText;
        }

        if (repair.Fix is not null && row.Command is { Length: > 0 } after)
        {
            detail += $" Then run: {after}.";
        }

        return row with { Detail = detail, Command = repair.Fix ?? row.Command };
    }
}
