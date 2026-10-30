using System.Globalization;
using System.Text.Json;
using DefenseClaw.Core.Gateway;

namespace DefenseClaw.App.Services.Wizards;

/// <summary>
/// What the ACP Guard wizard opens with (CUST-294): the editors and agents it may offer, the profiles the gateway has loaded, and what the installed
/// CLI's <c>acp setup</c> accepts. Everything here was read; nothing was run that changes anything.
/// </summary>
/// <param name="Clients">The <c>--client</c> choices; empty when neither the CLI nor the gateway named any (the field is then typed).</param>
/// <param name="Agents">The <c>--agent</c> choices; empty when neither named any.</param>
/// <param name="Profiles">The gateway's loaded ACP policy, or null when it did not say (the profile is then typed).</param>
/// <param name="HelpText"><c>defenseclaw acp setup --help</c> as the CLI printed it; empty when it could not be read.</param>
/// <param name="HasManagedFlags">The CLI's <c>acp setup</c> has <c>--managed</c>, <c>--runtime-data-dir</c> and <c>--token-file</c> (true when its help was not read: 1.0.4's does).</param>
/// <param name="ChoiceNote">Where the editor and agent lists came from, and what the catalog lists that this CLI would refuse.</param>
/// <param name="ProfileNote">Where the profile list came from.</param>
/// <param name="Status"><c>acp status --json</c> as read on a 1.0.5 runtime (CUST-401); null where it was not read (an older runtime).</param>
public sealed record AcpGuardContext(
    IReadOnlyList<WizardChoice> Clients,
    IReadOnlyList<WizardChoice> Agents,
    AcpProfiles? Profiles,
    string HelpText,
    bool HasManagedFlags,
    string ChoiceNote,
    string ProfileNote,
    AcpRead<AcpGuardStatus>? Status = null)
{
    /// <summary>
    /// The profile the wizard starts on: the gateway's <c>acp.default_profile</c> when it is one of the profiles, else the CLI's own default
    /// (<c>default</c>) when there is no list or the list has it, else the first profile listed.
    /// </summary>
    public string StartingProfile
    {
        get
        {
            var names = Profiles?.Profiles.Select(p => p.Name).ToArray() ?? Array.Empty<string>();
            if (Profiles is { DefaultProfile.Length: > 0 } loaded && names.Contains(loaded.DefaultProfile, StringComparer.Ordinal))
            {
                return loaded.DefaultProfile;
            }

            return names.Length == 0 || names.Contains(AcpGuardArgv.DefaultProfile, StringComparer.Ordinal) ? AcpGuardArgv.DefaultProfile : names[0];
        }
    }

    /// <summary>The profile list is known and not empty, so the profile is a choice.</summary>
    public bool HasProfileChoices => Profiles is { Profiles.Count: > 0 };

    /// <summary>The page's one line on where its lists came from (the wizard's <see cref="WizardDefinition.BaselineNote"/>).</summary>
    public string SourceNote => ChoiceNote + " " + ProfileNote;
}

/// <summary>
/// The read-only look the ACP Guard wizard takes before it opens: <c>defenseclaw acp setup --help</c> (through the Setup help probe: cached, no Activity
/// entry, never a mutation) for what the installed CLI accepts, and the gateway's <c>GET /v1/acp/catalog</c> and <c>GET /v1/acp/profiles</c> through the
/// app's verified-peer client (the token goes only to the process that owns the gateway port, CUST-247/249) for the names, support levels and profiles.
/// <para>
/// Each source falls back on its own and none is an error: a gateway that is stopped, refuses the token or predates the routes (404/405) leaves the
/// CLI's own choices and a typed profile; a help screen that cannot be read leaves the gateway's catalog; with neither, the editor and the agent are
/// typed and the CLI checks them. The CLI's choices win over the catalog's (they are what <c>acp setup</c> validates), and the catalog only labels them.
/// </para>
/// </summary>
public static class AcpGuardDiscovery
{
    /// <summary>The words of the help screen read: <c>defenseclaw acp setup --help</c>.</summary>
    public static IReadOnlyList<string> HelpPath => AcpGuardArgv.CommandWords;

    /// <summary>
    /// Reads the three sources in parallel, and the guard's status when <paramref name="readStatus"/> is given (a 1.0.5 runtime: <c>acp status --json</c>, a
    /// background read). Never throws for a failed source; <paramref name="cancellationToken"/> stops the wait.
    /// </summary>
    public static async Task<AcpGuardContext> ReadAsync(
        Func<IReadOnlyList<string>, CancellationToken, Task<HelpProbeResult>> readHelp,
        Func<string, int, CancellationToken, Task<GatewayResult<JsonDocument>>> fetch,
        CancellationToken cancellationToken = default,
        Func<CancellationToken, Task<AcpRead<AcpGuardStatus>>>? readStatus = null)
    {
        ArgumentNullException.ThrowIfNull(readHelp);
        ArgumentNullException.ThrowIfNull(fetch);

        var help = readHelp(HelpPath, cancellationToken);
        var catalog = fetch(AcpGuardReader.CatalogRoute, AcpGuardReader.MaxBytes, cancellationToken);
        var profiles = fetch(AcpGuardReader.ProfilesRoute, AcpGuardReader.MaxBytes, cancellationToken);
        var status = readStatus?.Invoke(cancellationToken);

        var helpResult = await help.ConfigureAwait(false);
        var catalogResult = await catalog.ConfigureAwait(false);
        var profilesResult = await profiles.ConfigureAwait(false);
        var statusResult = status is null ? null : await status.ConfigureAwait(false);
        try
        {
            return From(helpResult, AcpGuardReader.Catalog(catalogResult), AcpGuardReader.Profiles(profilesResult), statusResult);
        }
        finally
        {
            catalogResult.Value?.Dispose();
            profilesResult.Value?.Dispose();
        }
    }

    /// <summary>Puts the three answers together. Pure: what a test hands it is all it looks at.</summary>
    public static AcpGuardContext From(HelpProbeResult help, AcpRead<AcpCatalog> catalog, AcpRead<AcpProfiles> profiles, AcpRead<AcpGuardStatus>? status = null)
    {
        ArgumentNullException.ThrowIfNull(help);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(profiles);

        var parsed = help.Succeeded ? SetupHelpParser.Parse(help.Text, commandDepth: 1) : null;
        var helpClients = Accepted(parsed, AcpGuardArgv.ClientFlag);
        var helpAgents = Accepted(parsed, AcpGuardArgv.AgentFlag);
        var cataloged = catalog.IsOk ? catalog.Value : null;

        var clients = Merge(helpClients, cataloged?.Clients.Select(c => (c.Id, Label: ClientLabel(c))).ToArray(), out var clientSource, out var clientsRefused);
        var agents = Merge(helpAgents, cataloged?.Agents.Select(a => (a.Id, Label: AgentLabel(a))).ToArray(), out var agentSource, out var agentsRefused);
        var choiceNote = ChoiceNote(clientSource, agentSource) +
                         Refused("editor", clientsRefused) +
                         Refused("agent", agentsRefused);

        // 1.0.4's acp setup has the managed flags; an older acp setup without them cannot enroll, and says so by not listing them.
        var hasManaged = parsed is null ||
                         (parsed.Option(AcpGuardArgv.ManagedFlag) is not null &&
                          parsed.Option(AcpGuardArgv.RuntimeDataDirFlag) is not null &&
                          parsed.Option(AcpGuardArgv.TokenFileFlag) is not null);

        var loaded = profiles.IsOk ? profiles.Value : null;
        var profileNote = loaded switch
        {
            { Profiles.Count: > 0 } => "Profiles: the ones the running gateway has loaded; choose Another profile to give this pair one of its own.",
            { } => "Profiles: the running gateway has none yet, so setup creates the one you name.",
            _ when profiles.Status == AcpReadStatus.Unsupported => "Profiles: this gateway does not list them, so type the name (the CLI's default is \"default\").",
            _ => "Profiles: not read from the gateway (" + profiles.Message.TrimEnd('.') + "), so type the name (the CLI's default is \"default\").",
        };

        return new AcpGuardContext(clients, agents, loaded, help.Succeeded ? help.Text : string.Empty, hasManaged, choiceNote, profileNote, status);
    }

    /// <summary>"Zed (zed) · tested".</summary>
    internal static string ClientLabel(AcpCatalogClient client) =>
        client.Name + " (" + client.Id + ")" + (client.Support.Length > 0 ? " · " + client.Support : string.Empty);

    /// <summary>"Kiro (kiro) · tested · runs kiro-cli".</summary>
    internal static string AgentLabel(AcpCatalogAgent agent) =>
        agent.Name + " (" + agent.Id + ")" + (agent.Support.Length > 0 ? " · " + agent.Support : string.Empty) +
        (agent.Command.Length > 0 ? " · runs " + agent.Command : string.Empty);

    private static IReadOnlyList<string>? Accepted(ParsedHelp? parsed, string flag) =>
        parsed?.Option(flag) is { Choices.Count: > 0 } option
            ? option.Choices.Where(AcpGuardReader.IsStableName).ToArray()
            : null;

    /// <summary>Where a list came from.</summary>
    private enum ChoiceSource
    {
        /// <summary>What the CLI accepts, labelled from the catalog.</summary>
        HelpAndCatalog,

        /// <summary>What the CLI accepts; the gateway's catalog was not there.</summary>
        Help,

        /// <summary>The gateway's catalog; the CLI's help could not be read.</summary>
        Catalog,

        /// <summary>Nothing named them: the field is typed.</summary>
        None,
    }

    /// <summary>
    /// The choices: what the CLI accepts, labelled from the catalog where it names them; the catalog alone when the CLI did not say; nothing (a typed
    /// field) when neither did. <paramref name="refused"/> is what the catalog lists that this CLI's <c>acp setup</c> would refuse (never offered).
    /// </summary>
    private static IReadOnlyList<WizardChoice> Merge(
        IReadOnlyList<string>? accepted,
        IReadOnlyList<(string Id, string Label)>? cataloged,
        out ChoiceSource source,
        out IReadOnlyList<string> refused)
    {
        refused = Array.Empty<string>();
        if (accepted is { Count: > 0 })
        {
            var labels = (cataloged ?? Array.Empty<(string Id, string Label)>()).ToDictionary(c => c.Id, c => c.Label, StringComparer.Ordinal);
            refused = (cataloged ?? Array.Empty<(string Id, string Label)>()).Where(c => !accepted.Contains(c.Id, StringComparer.Ordinal)).Select(c => c.Id).ToArray();
            source = cataloged is null ? ChoiceSource.Help : ChoiceSource.HelpAndCatalog;
            return accepted.Select(id => new WizardChoice(id, labels.TryGetValue(id, out var label) ? label : id)).ToArray();
        }

        if (cataloged is { Count: > 0 })
        {
            source = ChoiceSource.Catalog;
            return cataloged.Select(c => new WizardChoice(c.Id, c.Label)).ToArray();
        }

        source = ChoiceSource.None;
        return Array.Empty<WizardChoice>();
    }

    private static string ChoiceNote(ChoiceSource clients, ChoiceSource agents) => clients == agents
        ? "Editors and agents: " + Describe(clients)
        : "Editors: " + Describe(clients) + " Agents: " + Describe(agents);

    private static string Describe(ChoiceSource source) => source switch
    {
        ChoiceSource.HelpAndCatalog => "what this CLI's acp setup accepts, named and rated (tested or cataloged) by the gateway's catalog.",
        ChoiceSource.Help => "what this CLI's acp setup accepts.",
        ChoiceSource.Catalog => "the gateway's catalog (the CLI's help could not be read).",
        _ => "type the id (defenseclaw acp catalog lists them); acp setup checks it.",
    };

    private static string Refused(string what, IReadOnlyList<string> ids) => ids.Count == 0
        ? string.Empty
        : string.Create(CultureInfo.CurrentCulture, $" The catalog also lists the {what} {string.Join(", ", ids)}, which this CLI's acp setup does not accept, so it is not offered.");
}
