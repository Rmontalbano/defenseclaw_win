namespace DefenseClaw.App.Services;

/// <summary>
/// The <c>defenseclaw-gateway</c> verbs the command palette offers besides start, stop, restart and status (which have their own controls,
/// <see cref="GatewayControl"/>): a fixed list of exact argv, never a typed one and never a bare <c>defenseclaw-gateway</c>, which starts a second
/// sidecar daemon. Each was read from the DefenseClaw 0.8.10 gateway's own help screens
/// (<c>defenseclaw-gateway watchdog | connector | policy [verb] --help</c>; the screens are kept in <c>Fixtures/runtime-0.8.10/gateway</c> and a test
/// holds this list to them), so a verb the installed gateway does not have is not offered.
/// <para>
/// The palette's rows for them come from the TUI command registry like every other (<see cref="CuratedCommandCatalog"/>); this is what the registry
/// does not say. <b>The tier is not here:</b> it is whatever <c>CommandTiers</c> says of the argv (a read runs as it is, <c>watchdog start | stop</c>
/// and <c>policy reload</c> are changes, <c>connector teardown</c> is destructive), so there is one place that decides it. What is here is what the
/// review says the verb does (the help's words, so the operator does not have to know that "teardown" restores a backup) and when the verb makes
/// sense at all (<c>policy reload</c> asks the running sidecar, and fails when there is none).
/// </para>
/// </summary>
internal static class GatewayVerbs
{
    /// <param name="Argv">The arguments without the executable; always two words, and the first is a group of the gateway.</param>
    /// <param name="Summary">What the verb does, in one or two sentences from the gateway's help; the review shows it for a verb that is reviewed.</param>
    /// <param name="NeedsRunningGateway">True when the verb is answered by the running sidecar, so there is nothing to do while it is down.</param>
    internal sealed record Verb(IReadOnlyList<string> Argv, string Summary, bool NeedsRunningGateway = false)
    {
        public string CommandText => CommandReview.CommandLine(GatewayControl.Executable, Argv);
    }

    /// <summary>The eight verbs, in the order the palette would list them within their groups.</summary>
    public static IReadOnlyList<Verb> All { get; } = new Verb[]
    {
        new(
            new[] { "watchdog", "start" },
            "Starts the health watchdog as a background daemon. It polls the gateway's /health endpoint and sends desktop notifications when the sidecar is unreachable or degraded."),
        new(
            new[] { "watchdog", "stop" },
            "Stops the running watchdog daemon. The gateway keeps running, but nothing watches its health or sends a notification when it goes down."),
        new(
            new[] { "watchdog", "status" },
            "Shows whether the watchdog daemon is running."),
        new(
            new[] { "connector", "verify" },
            "Checks that the connector left no residual state behind (hooks, env files, config patches, shims). Exit code 1 means residual state was found, 2 that the connector is unknown."),
        new(
            new[] { "connector", "list-backups" },
            "Lists the pristine connector backups under the data directory: the rollback points connector teardown restores from."),
        new(
            new[] { "connector", "teardown" },
            "Tears down the active connector: restores the agent framework's config from its pristine backup, removes the hook scripts the connector wrote into the DefenseClaw folder and clears the environment shims it installed. " +
            "The connector is marked inactive first. It does not touch the gateway's own service, its token or the audit database."),
        new(
            new[] { "policy", "reload" },
            "Tells the running gateway to reload its OPA policies. The gateway has to be running.",
            NeedsRunningGateway: true),
        new(
            new[] { "policy", "domains" },
            "Lists the firewall domain allowlist and blocklist of the active policy."),
    };

    /// <summary>The verb whose argv is exactly <paramref name="argv"/> (no option, no target), or null.</summary>
    public static Verb? Find(IReadOnlyList<string> argv)
    {
        ArgumentNullException.ThrowIfNull(argv);

        return argv.Count == 2
            ? All.FirstOrDefault(v => string.Equals(v.Argv[0], argv[0], StringComparison.Ordinal) && string.Equals(v.Argv[1], argv[1], StringComparison.Ordinal))
            : null;
    }

    /// <summary>What the review of <paramref name="argv"/> says the verb does, or an empty string when it is not one of these.</summary>
    public static string SummaryOf(IReadOnlyList<string> argv) => Find(argv)?.Summary ?? string.Empty;

    /// <summary>
    /// Whether the palette row for <paramref name="argv"/> can be used for <paramref name="snapshot"/>, and if not, why. True for anything that is
    /// not one of the verbs that needs the running sidecar (the CLI answers the rest for itself, and says so when there is nothing to do).
    /// </summary>
    public static (bool Allowed, string? Reason) Availability(IReadOnlyList<string> argv, GatewaySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return Find(argv) is { NeedsRunningGateway: true }
            ? GatewayControl.AvailabilityWhileRunning(snapshot, "The gateway is not running, so there is no sidecar to reload the policies in.")
            : (true, null);
    }
}
