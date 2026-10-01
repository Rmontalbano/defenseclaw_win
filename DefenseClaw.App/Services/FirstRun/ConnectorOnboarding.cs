using System.Text.Json;
using DefenseClaw.App.Services.Wizards;

namespace DefenseClaw.App.Services.FirstRun;

/// <summary>One connector the first-run window or an "Add" button can offer.</summary>
/// <param name="Id">The connector's id as config.yaml and <c>init --connector</c> spell it (<c>claudecode</c>).</param>
/// <param name="Label">Friendly name (<c>Claude Code</c>).</param>
/// <param name="Alias">The <c>defenseclaw setup &lt;alias&gt;</c> subcommand (<c>claude-code</c>: the only hyphenated one).</param>
/// <param name="Platform">What the CLI says about the connector on Windows; <see cref="PlatformStatus.Unknown"/> before its help was read.</param>
public sealed record OfferedConnector(string Id, string Label, string Alias, PlatformStatus Platform)
{
    /// <summary>Why the operator should hesitate (not certified on Windows), or empty.</summary>
    public string Caution => Platform == PlatformStatus.NotCertified
        ? $"{Label} is not certified on Windows. It can be added; expect rough edges."
        : string.Empty;

    public bool HasCaution => Caution.Length > 0;
}

/// <summary>
/// The connector rules of first-run setup and the per-row "Add": which connectors are hook connectors this app may configure, how their
/// names map onto the CLI's two spellings, which are offered on Windows, and which of the detected ones are not configured yet. All pure.
/// <para>
/// Mirrors the Mac's <c>ConnectorOnboarding</c> (first run is hook-only: <c>openclaw</c> and <c>zeptoclaw</c> are proxy connectors with
/// their own setup and never enter this plan), with the Windows filter on top: <see cref="WizardWindowsPolicy"/> and the CLI's own
/// <c>Platform status on windows</c> line, which the Setup catalog has already parsed from <c>setup &lt;target&gt; --help</c>.
/// </para>
/// </summary>
public static class ConnectorOnboarding
{
    /// <summary>The proxy connectors: configured by their own wizards, never by first run or an "Add" row.</summary>
    private static readonly HashSet<string> ProxyConnectors = new(StringComparer.Ordinal) { "openclaw", "zeptoclaw" };

    /// <summary>Setup targets in the Connectors group that are not connectors to add.</summary>
    private static readonly HashSet<string> NotAConnector = new(StringComparer.Ordinal) { "remove" };

    /// <summary>
    /// The hook connectors <c>defenseclaw setup</c> has a subcommand for (0.8.10 <c>setup --help</c>), in the order the first-run window lists
    /// them. Used when the catalog has not been read; once it has, the CLI's own list wins.
    /// </summary>
    private static readonly (string Id, string Label)[] KnownHookConnectors =
    {
        ("codex", "Codex"),
        ("claudecode", "Claude Code"),
        ("hermes", "Hermes"),
        ("cursor", "Cursor"),
        ("windsurf", "Windsurf"),
        ("geminicli", "Gemini CLI"),
        ("copilot", "GitHub Copilot CLI"),
        ("antigravity", "Antigravity"),
        ("opencode", "OpenCode"),
        ("openhands", "OpenHands"),
        ("omnigent", "OmniGent"),
    };

    /// <summary>The two the 0.8.10 CLI declares <c>unsupported on windows</c>; only consulted while the catalog is unread.</summary>
    private static readonly HashSet<string> UnsupportedBeforeCatalog = new(StringComparer.Ordinal) { "openhands", "omnigent" };

    /// <summary>Lower-cased and trimmed, with <c>claude-code</c> folded into <c>claudecode</c>: the form config.yaml and discovery use.</summary>
    public static string Normalize(string? connector)
    {
        var value = (connector ?? string.Empty).Trim().ToLowerInvariant();
        return value == "claude-code" ? "claudecode" : value;
    }

    /// <summary>The <c>defenseclaw setup</c> subcommand for a connector: only Claude Code is hyphenated.</summary>
    public static string SetupAlias(string? connector)
    {
        var id = Normalize(connector);
        return id == "claudecode" ? "claude-code" : id;
    }

    public static bool IsProxy(string? connector) => ProxyConnectors.Contains(Normalize(connector));

    public static string Label(string? connector)
    {
        var id = Normalize(connector);
        foreach (var (known, label) in KnownHookConnectors)
        {
            if (known == id)
            {
                return label;
            }
        }

        return id;
    }

    /// <summary>
    /// The connectors to offer on this machine. From the Setup catalog when it has been read (connector cards, minus the proxy connectors and
    /// anything <see cref="WizardWindowsPolicy"/> says cannot work here, such as a connector the CLI declares unsupported on Windows);
    /// from the built-in list otherwise.
    /// </summary>
    public static IReadOnlyList<OfferedConnector> Offerable(IEnumerable<WizardDefinition>? definitions)
    {
        var connectorCards = (definitions ?? Enumerable.Empty<WizardDefinition>())
            .Where(static d => d.Group == WizardGroups.Connectors && !NotAConnector.Contains(d.Target) && !IsProxy(d.Target))
            .ToList();

        if (connectorCards.Count == 0)
        {
            return KnownHookConnectors
                .Where(static k => !UnsupportedBeforeCatalog.Contains(k.Id))
                .Select(static k => new OfferedConnector(k.Id, k.Label, SetupAlias(k.Id), PlatformStatus.Unknown))
                .ToArray();
        }

        var offered = new List<OfferedConnector>();
        foreach (var card in connectorCards)
        {
            if (WizardWindowsPolicy.UnavailableReason(card.Target, card.PlatformStatus) is not null)
            {
                continue;
            }

            var id = Normalize(card.Target);
            offered.Add(new OfferedConnector(id, Label(id) is { } known && known != id ? known : card.Title, SetupAlias(id), card.PlatformStatus));
        }

        // The window's order, not the catalog's alphabetical one: known connectors first in their own order, new ones after.
        return offered
            .OrderBy(static o => Array.FindIndex(KnownHookConnectors, k => k.Id == o.Id) is var i && i < 0 ? int.MaxValue : i)
            .ThenBy(static o => o.Label, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// The detected connectors that are offered here and not configured yet, in the offered order. <paramref name="configured"/> is every
    /// connector the app already knows (configured, live or active); names compare normalized, so <c>claude-code</c> is <c>claudecode</c>.
    /// </summary>
    public static IReadOnlyList<OfferedConnector> Unconfigured(
        IEnumerable<string> detected,
        IEnumerable<string> configured,
        IReadOnlyList<OfferedConnector> offerable)
    {
        ArgumentNullException.ThrowIfNull(detected);
        ArgumentNullException.ThrowIfNull(configured);
        ArgumentNullException.ThrowIfNull(offerable);

        var have = new HashSet<string>(configured.Select(Normalize), StringComparer.Ordinal);
        var found = new HashSet<string>(detected.Select(Normalize).Where(static d => d.Length > 0), StringComparer.Ordinal);
        return offerable.Where(o => found.Contains(o.Id) && !have.Contains(o.Id)).ToArray();
    }

    /// <summary>
    /// The review for adding one detected connector alongside the others: <c>defenseclaw setup &lt;alias&gt; --yes --mode observe</c>.
    /// Observe records and never blocks; with other connectors configured, <c>--yes</c> adds rather than replaces (the CLI's default
    /// without <c>--replace</c>). Verified against <c>setup claude-code --help</c> and <c>setup cursor --help</c> (0.8.10).
    /// </summary>
    public static IReadOnlyList<string> AddArgv(string connector) =>
        new[] { "setup", SetupAlias(connector), "--yes", "--mode", "observe" };

    /// <summary>
    /// The connectors the AI-discovery scan has seen (<c>ai_discovery_state.json</c>, written by the gateway's scanner): each agent signal
    /// that maps to a supported connector (<c>supported_connector</c>) and is not <c>gone</c>. Local-model signals are not agents. Reading the
    /// file executes nothing, which is why first run can use it where the Mac runs <c>agent discover</c>.
    /// </summary>
    /// <exception cref="JsonException">Not JSON, or not a JSON object.</exception>
    public static IReadOnlyList<string> ParseDetected(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("ai_discovery_state.json is not a JSON object.");
        }

        if (!root.TryGetProperty("signals", out var list))
        {
            return Array.Empty<string>();
        }

        var items = list.ValueKind switch
        {
            JsonValueKind.Object => list.EnumerateObject().Select(static p => p.Value),
            JsonValueKind.Array => list.EnumerateArray(),
            _ => Enumerable.Empty<JsonElement>(),
        };

        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (item.ValueKind != JsonValueKind.Object ||
                string.Equals(Text(item, "category"), "local_model", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Text(item, "state"), "gone", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var connector = Normalize(Text(item, "supported_connector"));
            if (connector.Length > 0 && !IsProxy(connector))
            {
                found.Add(connector);
            }
        }

        return found
            .OrderBy(static c => Array.FindIndex(KnownHookConnectors, k => k.Id == c) is var i && i < 0 ? int.MaxValue : i)
            .ThenBy(static c => c, StringComparer.Ordinal)
            .ToArray();
    }

    private static string Text(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? (value.GetString() ?? string.Empty).Trim()
            : string.Empty;
}
