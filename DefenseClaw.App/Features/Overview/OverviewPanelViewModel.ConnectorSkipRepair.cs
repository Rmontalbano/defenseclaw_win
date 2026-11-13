using DefenseClaw.Core.Gateway.Models;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// The repair the gateway sends beside a skipped connector's reason (<c>connectors_not_started_repairs</c>, runtime 1.0.9: a plugin folder another account can
/// write). Before 1.0.9 the repair was part of the reason text. It goes on the connector's existing row: the fix as the row's copyable command (the app never
/// builds a command from <c>/health</c> text and never runs it), and the manual step as words. Without the key the row is exactly what it was.
/// </summary>
public sealed partial class OverviewPanelViewModel
{
    /// <summary>
    /// The words added to the row's detail when the gateway left part of the repair out, or the app did not show all of it (a fix over
    /// <see cref="ConnectorStartHealth.MaxFixDisplayLength"/> or not plain text is dropped, a manual step over <see cref="ConnectorStartHealth.MaxByHandDisplayLength"/> is cut).
    /// </summary>
    internal const string RepairTooLongText = "Part of the repair is too long or not plain text to show here; the gateway log has it.";

    /// <summary>What the row says about the box when it holds the gateway's repair.</summary>
    internal const string RepairBoxSentence = "The command below is the gateway's repair; the app never runs it.";

    /// <summary>What is left of the setup-failed row's closing words when the box holds the gateway's repair.</summary>
    internal const string SetupFailedLogSentence = " Read gateway.log for the cause.";

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
            // The box will hold the gateway's repair, so the row must not also send the reader to "the command below" as if it were setup.
            if (detail.EndsWith(SetupFailedRunSentence, StringComparison.Ordinal))
            {
                detail = detail[..^SetupFailedRunSentence.Length] + SetupFailedLogSentence;
            }

            detail += " " + RepairBoxSentence;
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
            detail += $" After that, run: {after}.";
        }

        return row with { Detail = detail, Command = repair.Fix ?? row.Command };
    }
}
