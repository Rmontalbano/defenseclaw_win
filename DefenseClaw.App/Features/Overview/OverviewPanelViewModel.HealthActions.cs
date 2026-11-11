namespace DefenseClaw.App.ViewModels;

/// <summary>
/// The Overview's health rows of CUST-448 (the audit store cap, connectors the gateway did not start, the old PowerShell hook bridge) in one place:
/// <see cref="BuildAttention"/> calls <see cref="AppendOverviewHealthAttention"/> once, and <see cref="RunAttentionAction"/> hands their button ids to
/// <see cref="RunOverviewHealthActionAsync"/>. Each row's logic lives in its own file.
/// </summary>
public sealed partial class OverviewPanelViewModel
{
    /// <summary>Adds the rows, in this order.</summary>
    private void AppendOverviewHealthAttention(List<AttentionRow> rows)
    {
        AppendAuditCapAttention(rows);
        AppendConnectorSkipAttention(rows);
        AppendHookBridgeAttention(rows);
    }

    /// <summary>True for the ids of the buttons of the rows <see cref="AppendOverviewHealthAttention"/> adds.</summary>
    private static bool IsOverviewHealthAction(string id) =>
        id is OpenAuditCapConfigActionId or ConnectorSkipRestartActionId ||
        id.StartsWith(ConnectorSkipRemoveActionPrefix, StringComparison.Ordinal) ||
        id.StartsWith("hook-bridge-setup:", StringComparison.Ordinal);

    /// <summary>Runs one of those buttons: a navigation or the opening of a review. Nothing here runs a command.</summary>
    private Task RunOverviewHealthActionAsync(string id)
    {
        if (id == OpenAuditCapConfigActionId)
        {
            OpenAuditCapConfig();
        }
        else if (id == ConnectorSkipRestartActionId)
        {
            _ = RestartQueueReview.Open(Review, Services, () => RefreshAfterActionAsync(), _snapshot);
        }
        else if (id.StartsWith(ConnectorSkipRemoveActionPrefix, StringComparison.Ordinal))
        {
            RemoveSkippedConnector(id[ConnectorSkipRemoveActionPrefix.Length..]);
        }
        else if (id.StartsWith("hook-bridge-setup:", StringComparison.Ordinal))
        {
            ReviewHookBridgeRepair(id);
        }

        return Task.CompletedTask;
    }
}
