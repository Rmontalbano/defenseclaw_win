namespace DefenseClaw.Core.Gateway.Durability;

/// <summary>
/// Why the gateway's latest start attempt ended (CUST-352), read from <c>gateway.log</c>. DefenseClaw 1.0 treats one connector's setup failure
/// as fatal at start: the sidecar listens for about a second and then exits, so the app sees "stopped" with nothing to say why. These are the
/// causes the two 2026-10-15 incidents showed, plus a generic connector failure and the last error line for anything else.
/// </summary>
public abstract record GatewayExitReason
{
    private GatewayExitReason()
    {
    }

    /// <summary>The connector the failure names (<c>codex</c>), or <c>unknown</c>.</summary>
    public abstract string Connector { get; }

    /// <summary>One plain sentence for the Overview row and the tray flyout, e.g. "Codex's executable moved after a Codex update." The detail (a path) is in the record.</summary>
    public abstract string Describe();

    /// <summary>The agent's executable that setup pinned is gone: the agent updated itself into a new folder and removed the old one.</summary>
    public sealed record StaleAgentExecutable(string ConnectorName, string Path) : GatewayExitReason
    {
        public override string Connector => ConnectorName;

        public override string Describe() =>
            $"{GatewayLogClassifier.FriendlyConnector(ConnectorName)}'s executable moved after a {GatewayLogClassifier.FriendlyConnector(ConnectorName)} update.";
    }

    /// <summary>A guarded write of the agent's config file never finished and now blocks every later write of it.</summary>
    public sealed record StuckConfigWrite(string ConnectorName, string Path) : GatewayExitReason
    {
        public override string Connector => ConnectorName;

        public override string Describe() =>
            $"A stuck pending write to {GatewayLogClassifier.FriendlyConnector(ConnectorName)}'s config file makes every start fail.";
    }

    /// <summary>A connector's setup failed for a reason this app has no name for; <paramref name="Message"/> is the gateway's own text.</summary>
    public sealed record ConnectorSetupFailed(string ConnectorName, string Message) : GatewayExitReason
    {
        public override string Connector => ConnectorName;

        public override string Describe() =>
            $"{GatewayLogClassifier.FriendlyConnector(ConnectorName)}'s connector setup failed: {Message}";
    }

    /// <summary>The start ended with an error that is not a connector setup failure; <paramref name="Line"/> is the last error line, as written.</summary>
    public sealed record Other(string Line) : GatewayExitReason
    {
        public override string Connector => "unknown";

        public override string Describe() => $"The gateway's last start ended with an error: {Line}";
    }
}
