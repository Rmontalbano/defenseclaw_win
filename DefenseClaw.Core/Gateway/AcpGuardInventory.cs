using System.Text.Json;
using System.Text.RegularExpressions;
using DefenseClaw.Core.AiRuntime;

namespace DefenseClaw.Core.Gateway;

/// <summary>How one read of the gateway's ACP inventory ended.</summary>
public enum AcpReadStatus
{
    /// <summary>The gateway answered with an inventory this app could read.</summary>
    Ok,

    /// <summary>The gateway has no such route (HTTP 404 or 405): a runtime without it. A limit of the version, never an error.</summary>
    Unsupported,

    /// <summary>Nothing answered, the token was refused, or the answer could not be read: there is nothing to show, and nothing to report as a fault.</summary>
    Unavailable,
}

/// <summary>One editor the ACP guard can configure, as <c>GET /v1/acp/catalog</c> lists it (<c>acp.Client</c>, <c>internal/acp/catalog.go</c> at 1.0.4).</summary>
/// <param name="Id">The <c>--client</c> value (<c>zed</c>, <c>jetbrains</c>).</param>
/// <param name="Name">What the editor is called.</param>
/// <param name="Support"><c>certified</c>, <c>tested</c> or <c>cataloged</c>.</param>
public sealed record AcpCatalogClient(string Id, string Name, string Support);

/// <summary>One ACP agent the guard can wrap, as <c>GET /v1/acp/catalog</c> lists it (<c>acp.Agent</c>, <c>internal/acp/catalog.go</c> at 1.0.4).</summary>
/// <param name="Id">The <c>--agent</c> value (<c>kiro</c>, <c>copilot</c>, ...).</param>
/// <param name="Name">What the agent is called.</param>
/// <param name="Support"><c>certified</c>, <c>tested</c> or <c>cataloged</c>.</param>
/// <param name="Command">The executable setup looks for on PATH (<c>kiro-cli</c>).</param>
public sealed record AcpCatalogAgent(string Id, string Name, string Support, string Command);

/// <summary>The compiled client and agent catalog the gateway ships.</summary>
public sealed record AcpCatalog(IReadOnlyList<AcpCatalogClient> Clients, IReadOnlyList<AcpCatalogAgent> Agents, string SchemaVersion);

/// <summary>One ACP policy profile (<c>config.ACPProfile</c>, <c>internal/config/config.go</c> at 1.0.4).</summary>
/// <param name="Name">The key under <c>acp.profiles</c>.</param>
/// <param name="Mode"><c>observe</c>, <c>action</c>, or empty (the global <c>acp.mode</c> applies).</param>
/// <param name="FailMode"><c>open</c>, <c>closed</c>, or empty.</param>
/// <param name="AllowedClients">The editors the profile admits.</param>
/// <param name="AllowedAgents">The agents the profile admits.</param>
public sealed record AcpProfile(string Name, string Mode, string FailMode, IReadOnlyList<string> AllowedClients, IReadOnlyList<string> AllowedAgents);

/// <summary>The ACP policy the running gateway has loaded, as <c>GET /v1/acp/profiles</c> answers it.</summary>
/// <param name="Enabled"><c>acp.enabled</c>.</param>
/// <param name="Mode">The effective global mode (<c>observe</c> unless <c>acp.mode</c> is <c>action</c>).</param>
/// <param name="DefaultProfile"><c>acp.default_profile</c>; empty when none is set.</param>
/// <param name="Profiles">Every profile, by name.</param>
public sealed record AcpProfiles(bool Enabled, string Mode, string DefaultProfile, IReadOnlyList<AcpProfile> Profiles);

/// <summary>The outcome of one read: the value, or why there is none (never an error banner: see <see cref="AcpReadStatus"/>).</summary>
public sealed record AcpRead<T>(AcpReadStatus Status, T? Value, string Message)
    where T : class
{
    public static AcpRead<T> Unsupported { get; } = new(AcpReadStatus.Unsupported, null, "This DefenseClaw's gateway has no such route.");

    public static AcpRead<T> Unavailable(string message) => new(AcpReadStatus.Unavailable, null, message);

    public bool IsOk => Status == AcpReadStatus.Ok && Value is not null;
}

/// <summary>
/// Reads the gateway's two operator ACP routes (CUST-294): <c>GET /v1/acp/catalog</c> (<c>handleACPCatalog</c>, writing <c>acp.BuiltinCatalog()</c>) and
/// <c>GET /v1/acp/profiles</c> (<c>handleACPProfiles</c>, writing <c>{enabled, mode, default_profile, profiles}</c>), both in
/// <c>internal/gateway/acp.go</c> and routed at <c>internal/gateway/api.go:1233-1234</c> at 1.0.4. Both are reads behind the gateway token
/// (<c>acp_operator_reads_auth_test.go</c>: the ACP-scoped credential gets 401 there), answer 405 for anything but GET, and are absent (404) on a
/// runtime that predates them. The CLI's own <c>acp setup</c> waits on the second one in the same shape (<c>_wait_for_gateway_acp</c>).
/// <para>
/// Every id read here becomes a command-line value, so only an id the runtime itself would accept is kept (<c>^[a-z0-9][a-z0-9_-]{0,63}$</c>, the
/// pattern of <c>internal/config/acp.go</c>); names are shown text and are cleaned and cut like every other gateway string.
/// </para>
/// </summary>
public static class AcpGuardReader
{
    /// <summary>The catalog route, relative to the gateway's base address.</summary>
    public const string CatalogRoute = "v1/acp/catalog";

    /// <summary>The profiles route, relative to the gateway's base address.</summary>
    public const string ProfilesRoute = "v1/acp/profiles";

    /// <summary>The longest answer read from either route; 1.0.4's catalog is about 2 KiB and a profile list is capped at 128 entries.</summary>
    public const int MaxBytes = 256 * 1024;

    private const int MaxEntries = 128;
    private const int MaxNameLength = 80;

    private static readonly Regex StableName = new("^[a-z0-9][a-z0-9_-]{0,63}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>True when <paramref name="value"/> is a name the runtime accepts for a client, an agent or a profile.</summary>
    public static bool IsStableName(string? value) => value is { Length: > 0 } && StableName.IsMatch(value);

    /// <summary>Turns the gateway's answer to <see cref="CatalogRoute"/> into a read.</summary>
    public static AcpRead<AcpCatalog> Catalog(GatewayResult<JsonDocument> result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return Read(result, ParseCatalog);
    }

    /// <summary>Turns the gateway's answer to <see cref="ProfilesRoute"/> into a read.</summary>
    public static AcpRead<AcpProfiles> Profiles(GatewayResult<JsonDocument> result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return Read(result, ParseProfiles);
    }

    /// <summary>Reads a catalog answer; null when it is not one.</summary>
    public static AcpCatalog? ParseCatalog(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var clients = new List<AcpCatalogClient>();
        foreach (var item in Items(root, "clients"))
        {
            if (Id(item) is { } id && clients.All(c => c.Id != id))
            {
                clients.Add(new AcpCatalogClient(id, Name(item, id), Text(item, "support")));
            }
        }

        var agents = new List<AcpCatalogAgent>();
        foreach (var item in Items(root, "agents"))
        {
            if (Id(item) is { } id && agents.All(a => a.Id != id))
            {
                agents.Add(new AcpCatalogAgent(id, Name(item, id), Text(item, "support"), Text(item, "command")));
            }
        }

        return clients.Count == 0 && agents.Count == 0 ? null : new AcpCatalog(clients, agents, Text(root, "schema_version"));
    }

    /// <summary>Reads a profiles answer; null when it is not one. A <c>null</c> or missing <c>profiles</c> is an empty list (Go writes a nil map as null).</summary>
    public static AcpProfiles? ParseProfiles(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("enabled", out var enabled) ||
            enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return null;
        }

        var profiles = new List<AcpProfile>();
        if (root.TryGetProperty("profiles", out var map) && map.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in map.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                if (profiles.Count >= MaxEntries || !IsStableName(entry.Name) || entry.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                profiles.Add(new AcpProfile(
                    entry.Name,
                    Mode(Text(entry.Value, "mode")),
                    FailMode(Text(entry.Value, "fail_mode")),
                    Names(entry.Value, "allowed_clients"),
                    Names(entry.Value, "allowed_agents")));
            }
        }

        var defaultProfile = Text(root, "default_profile");
        return new AcpProfiles(
            enabled.ValueKind == JsonValueKind.True,
            Mode(Text(root, "mode")) is { Length: > 0 } mode ? mode : "observe",
            IsStableName(defaultProfile) ? defaultProfile : string.Empty,
            profiles);
    }

    private static AcpRead<T> Read<T>(GatewayResult<JsonDocument> result, Func<JsonElement, T?> parse)
        where T : class
    {
        switch (result.Status)
        {
            case GatewayStatus.Ok when result.Value is { } document:
                return parse(document.RootElement) is { } value
                    ? new AcpRead<T>(AcpReadStatus.Ok, value, string.Empty)
                    : AcpRead<T>.Unavailable("The gateway's answer is not one this app can read.");

            case GatewayStatus.Unreachable:
                return AcpRead<T>.Unavailable("The gateway is not running.");

            case GatewayStatus.Unauthorized:
                return AcpRead<T>.Unavailable("The gateway refused this app's API token.");

            default:
                return result.HttpStatusCode is 404 or 405
                    ? AcpRead<T>.Unsupported
                    : AcpRead<T>.Unavailable("The gateway could not answer.");
        }
    }

    private static IEnumerable<JsonElement> Items(JsonElement root, string name) =>
        root.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object).Take(MaxEntries)
            : Enumerable.Empty<JsonElement>();

    private static string? Id(JsonElement item)
    {
        var id = item.TryGetProperty("id", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return IsStableName(id) ? id : null;
    }

    private static string Name(JsonElement item, string fallback) => Text(item, "name") is { Length: > 0 } name ? name : fallback;

    private static IReadOnlyList<string> Names(JsonElement element, string name) =>
        element.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray()
                .Where(i => i.ValueKind == JsonValueKind.String)
                .Select(i => i.GetString())
                .Where(IsStableName)
                .Select(i => i!)
                .Distinct(StringComparer.Ordinal)
                .Take(MaxEntries)
                .ToArray()
            : Array.Empty<string>();

    private static string Mode(string value) => value is "observe" or "action" ? value : string.Empty;

    private static string FailMode(string value) => value is "open" or "closed" ? value : string.Empty;

    private static string Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? Cut(AiRuntimeJson.Prose(value.GetString(), MaxNameLength))
            : string.Empty;

    private static string Cut(string text) => text.Length <= MaxNameLength ? text : text[..MaxNameLength];
}
