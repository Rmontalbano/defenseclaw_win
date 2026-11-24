namespace DefenseClaw.Core.Cli;

/// <summary>
/// What this app leaves out of the command palette's 1.0.10 catalogue (<see cref="TuiRegistryCatalogues.WithRoutingCli"/>), and why. These are
/// the APP's choices, not the runtime's: the runtime's own registry has no Windows reason for any of them, so none of this is ever worded
/// as one (<see cref="TuiRegistryEntry.WindowsUnavailable"/> stays the runtime's sentence) and the palette says so under its own heading
/// (<see cref="TuiRegistryCatalogue.LeftOutReasons"/>).
/// <list type="bullet">
/// <item><description><c>substrate ...</c> (8 entries): <c>cmd_substrate.py:20-28</c> at 1.0.10 describes the group as running coding agents on Agent Substrate in the
/// organization's cluster (the launcher itself runs on Windows, macOS and Linux). That is an organization's tool, not part of one person's
/// DefenseClaw install, which is all this app manages.</description></item>
/// <item><description><c>routing ...</c> that changes anything: the app shows routing state only. A command is offered only when it is on the allow-list below;
/// every other <c>routing</c> argv is left out, so a verb a later registry adds stays out until someone has read what it does.</description></item>
/// <item><description>Any argv <see cref="TuiRegistryCatalogues.ArgvProblem"/> refuses (a word with a space or shell syntax, an option that has not been
/// reviewed): never offered, whatever the registry lists.</description></item>
/// </list>
/// </summary>
public static class RoutingCliExclusions
{
    /// <summary>The sentence for the <c>substrate</c> group, as the palette words it.</summary>
    public const string SubstrateReason =
        "Agent Substrate runs coding agents in your organization's cluster, which this app does not manage. Run these from a terminal.";

    /// <summary>The sentence for a <c>routing</c> command that is not on the read-only list (it changes routing, or it has not been read).</summary>
    public const string RoutingChangeReason =
        "This app shows routing state only. Run commands that change routing from a terminal.";

    /// <summary>The sentence for a command whose argv fails <see cref="TuiRegistryCatalogues.ArgvProblem"/>.</summary>
    public const string ArgvReason =
        "This app does not pass this command's words on as they are written. Run it from a terminal.";

    /// <summary>
    /// The <c>routing</c> commands the app offers, each the whole argv, each read in <c>cli/defenseclaw/commands/cmd_routing.py</c> at 1.0.10
    /// (tag 1.0.10, commit a64bfdb93). None writes config.yaml or other persistent state and none starts, restarts or reloads anything:
    /// <list type="bullet">
    /// <item><description><c>routing status</c> (:548-590): reads config.yaml (<c>_document</c>, :104-136, <c>read_bytes</c> only) and the gateway's
    /// GET <c>/api/v1/routing/effective</c> and <c>/health</c>.</description></item>
    /// <item><description><c>routing models list</c> (:745-770), <c>routing rules list</c> (:912-953): read config.yaml (rules list may ask GET
    /// <c>/api/v1/routing/effective</c> on a managed device).</description></item>
    /// <item><description><c>routing budgets list</c> (:1153-1199): config.yaml and GET <c>/api/v1/routing/budgets</c>
    /// (<c>internal/gateway/api_routing.go:263-265</c>, GET only).</description></item>
    /// <item><description><c>routing pricing show</c> (:1262-1283): config.yaml only.</description></item>
    /// <item><description><c>routing explain</c> (:1440-1466): GET <c>/api/v1/routing/explain/{id}</c> (<c>api_routing.go:354-356</c>, GET only).</description></item>
    /// <item><description><c>routing usage</c> (:1475-1517): GET <c>/usage</c> (<c>internal/gateway/api_usage.go:92</c>, GET only).</description></item>
    /// <item><description><c>routing test</c> (:1370-1415): a dry run, POST <c>/api/v1/routing/test</c> (<c>api_routing.go:409-474</c>). The handler does not call
    /// <c>recordRoutingDecision</c> (the recent-decisions ring), charges no budget and writes no usage row; it asks the routing engine to decide
    /// (<c>internal/routing/engine/decide.go:31</c>), which may ask the classifier and keeps the classifier's answer in an in-memory cache
    /// (<c>decide.go:228-247</c>). The configured classifier receives the prompt and can be a hosted model: the <c>systemone</c> classifier POSTs
    /// <c>{"user_request": prompt, ...}</c> to its decision-model endpoint (<c>model_router_systemone.go:138</c>) and the remote one POSTs it to
    /// <c>&lt;endpoint&gt;/api/v1/classify/intent</c> (<c>model_router_remote.go:105</c>). The CLI's "No model is called" (<c>cmd_routing.py:1377</c>) means the
    /// model the prompt would be routed to; nothing persists. It is the one command here that is not a plain
    /// GET; it needs the prompt typed, so the palette copies it for the operator to finish.</description></item>
    /// </list>
    /// </summary>
    private static readonly HashSet<string> ReadOnlyRoutingCommands = new(StringComparer.Ordinal)
    {
        "routing status",
        "routing models list",
        "routing rules list",
        "routing budgets list",
        "routing pricing show",
        "routing test",
        "routing explain",
        "routing usage",
    };

    /// <summary>The <c>routing</c> argv the app offers, as the joined words, for a test to pin.</summary>
    public static IReadOnlyCollection<string> OfferedRoutingCommands => ReadOnlyRoutingCommands;

    /// <summary>
    /// The app's reason for leaving <paramref name="entry"/> out of the palette, or null when it is offered (as far as this app is concerned: the
    /// runtime's own Windows verdict is separate). The checks are in this order: <c>substrate</c>, <c>routing</c> off the allow-list, then an argv the
    /// hygiene check refuses.
    /// </summary>
    public static string? Reason(TuiRegistryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (entry.Argv.Count > 0 && string.Equals(entry.Argv[0], "substrate", StringComparison.Ordinal))
        {
            return SubstrateReason;
        }

        if (entry.Argv.Count > 0 && string.Equals(entry.Argv[0], "routing", StringComparison.Ordinal) &&
            !(entry.Binary == TuiBinary.Cli && ReadOnlyRoutingCommands.Contains(string.Join(' ', entry.Argv))))
        {
            return RoutingChangeReason;
        }

        return TuiRegistryCatalogues.ArgvProblem(entry.Argv) is not null ? ArgvReason : null;
    }
}
