using DefenseClaw.App.Services;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// The Agents panel's deep link (CUST-381): the AI BOM's agent identities open here on their own directory row. Both panels read
/// <c>GET /api/v1/agents/identities</c>, so the identity's agent id is the row's key; selecting the row opens it exactly as a click does.
/// </summary>
public sealed partial class AgentsPanelViewModel
{
    /// <summary>The line shown when a link names an agent the directory on screen does not list. Quiet: no error, nothing is selected.</summary>
    public const string AgentNotInViewMessage = "That agent is not in the current view. It may be on a later page of the directory, or no longer reported.";

    private string? _pendingAgentFocus;

    /// <summary>
    /// A deep link (<see cref="IAcceptsNavigation"/>): an <see cref="AgentFocus"/> selects that agent's directory row. Applied now when no read of the
    /// directory is under way, otherwise when that read ends. A payload of another type, or one without an id, is ignored.
    /// </summary>
    public void Accept(object payload)
    {
        if (payload is not AgentFocus { AgentId: var id } || string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        _pendingAgentFocus = id.Trim();
        if (!_loadRunning && _directory is not null)
        {
            ApplyPendingAgentFocus();
        }
    }

    private void ApplyPendingAgentFocus()
    {
        if (_pendingAgentFocus is not { } id)
        {
            return;
        }

        _pendingAgentFocus = null;
        var row = Agents.FirstOrDefault(r => string.Equals(r.AgentId, id, StringComparison.Ordinal));
        if (row is null)
        {
            OpenMessage = AgentNotInViewMessage;
            return;
        }

        OpenMessage = string.Empty;
        FilterText = string.Empty;
        SelectedAgent = row;
    }
}
