using DefenseClaw.Core.Gateway.Durability;

namespace DefenseClaw.App.Services;

/// <summary>
/// The words for an agent that is blocked: its hooks fail closed (<c>.hookcfg</c>) and the gateway is not running, so every action it takes is
/// refused until the gateway is back or the fail mode is changed (CUST-352). One place for the sentence the Overview row, the tray flyout and the
/// toast share.
/// </summary>
internal static class AgentBlock
{
    /// <summary>What follows the agent's name in the title.</summary>
    public const string Suffix = "blocked: fail mode is closed and the gateway is down";

    /// <summary>"Codex is blocked: fail mode is closed and the gateway is down"; several agents are named together.</summary>
    public static string Title(DurabilityFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        var names = facts.FailClosedConnectors.Select(GatewayLogClassifier.FriendlyConnector).ToArray();
        var subject = names.Length switch
        {
            0 => "An agent is",
            1 => names[0] + " is",
            _ => string.Join(", ", names[..^1]) + " and " + names[^1] + " are",
        };
        return $"{subject} {Suffix}";
    }

    /// <summary>The line under the title: what it means and the two ways out.</summary>
    public static string Body(DurabilityFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        return "The hooks block every action while the gateway is not running. Start the gateway, or switch to fail open, from the Overview.";
    }
}

/// <summary>
/// Announces <see cref="AgentBlock"/> once when it begins: the first look that finds an agent blocked, and again only after it was unblocked in between.
/// Unlike the gateway-offline toast it also speaks for a launch into an already-blocked state - an agent that cannot work is news whenever the app learns of it.
/// </summary>
internal sealed class AgentBlockedToastTracker
{
    private bool _announced;

    /// <param name="snapshot">The state just published.</param>
    /// <param name="suppress">True while the operator's own gateway action is running (the state bounces and that action has its own toast).</param>
    /// <param name="enabled">The "gateway" toggle in the notification settings, read now.</param>
    public GatewayToast? Observe(GatewaySnapshot snapshot, bool suppress, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!snapshot.IsAgentBlocked)
        {
            // A poll that could not say (before the first one, a WSL relay) leaves the episode as it was; an answer ends it, whether the gateway
            // is back or the hooks no longer fail closed.
            if (snapshot.State is AppGatewayState.Running or AppGatewayState.Degraded or AppGatewayState.GatewayStopped)
            {
                _announced = false;
            }

            return null;
        }

        if (_announced || suppress || !enabled)
        {
            return null;
        }

        _announced = true;
        return new GatewayToast(AgentBlock.Title(snapshot.Durability!), AgentBlock.Body(snapshot.Durability!), ToastLevel.Error);
    }
}
