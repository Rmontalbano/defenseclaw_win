using System.Text;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace DefenseClaw.Core.Config;

/// <summary>
/// Which registry source promoted a skill or an MCP server into the allow policy (CUST-276): the <c>registry:&lt;id&gt;</c> badge of the
/// Skills and MCPs lists. A registry sync (<c>defenseclaw registry sync</c>, or an approval) writes one rule per promoted entry into
/// <c>asset_policy.skill.registry</c> / <c>asset_policy.mcp.registry</c> of config.yaml and stamps it <c>reason: registry:&lt;source id&gt;</c>
/// (<c>registries/sync.py</c> <c>_promote_to_asset_policy</c>); a rule an operator wrote by hand has some other reason, or none, and is not a
/// registry's. The <c>skill list</c> / <c>mcp list</c> output says nothing of this, so the attribution is read from the rules.
/// <para>
/// This is the TUI's <c>_registry_attribution_from_config</c> (<c>tui/app.py</c>) and <c>registry_attribution_from_rules</c> / <c>parse_registry_source_id</c>
/// (<c>tui/services/catalog_state.py</c>) of 0.8.10, rule for rule: a rule counts only when its trimmed name and its trimmed reason are not empty,
/// the reason starts with <c>registry:</c> (case-sensitive) and an id is left once the prefix is cut and the rest trimmed; names are matched
/// exactly (case-sensitive); a rule's <c>connector</c> is not looked at; when several rules carry one name the last one wins. It is read from the raw
/// <c>asset_policy</c> section of the loaded <see cref="ConfigDocument"/> (the typed model has no such section), so nothing is re-read from disk.
/// </para>
/// <para>
/// Nothing here fails: a section that is missing, is not YAML, is not a mapping, or whose <c>registry</c> is not a list of mappings reads as "no
/// attribution", like a machine that never synced a registry.
/// </para>
/// </summary>
public sealed class RegistryAttribution
{
    /// <summary>The kind of an entry a registry promotes as a skill (the cache's and the CLI's <c>--type</c> word).</summary>
    public const string SkillKind = "skill";

    /// <summary>The kind of an entry a registry promotes as an MCP server.</summary>
    public const string McpKind = "mcp";

    /// <summary>What a promoted rule's <c>reason</c> starts with, followed by the source id.</summary>
    public const string ReasonPrefix = "registry:";

    /// <summary>The TUI's <c>registry_badge</c> shows at most this many characters of the id (the rest is cut and ends in <c>...</c>).</summary>
    public const int BadgeMaxIdChars = 18;

    private static readonly IReadOnlyDictionary<string, string> NoRules = new Dictionary<string, string>(StringComparer.Ordinal);

    private readonly IReadOnlyDictionary<string, string> _skills;
    private readonly IReadOnlyDictionary<string, string> _mcps;

    private RegistryAttribution(IReadOnlyDictionary<string, string> skills, IReadOnlyDictionary<string, string> mcps)
    {
        _skills = skills;
        _mcps = mcps;
    }

    /// <summary>No rule is a registry's.</summary>
    public static RegistryAttribution Empty { get; } = new(NoRules, NoRules);

    /// <summary>True when no skill and no MCP server is attributed to a registry.</summary>
    public bool IsEmpty => _skills.Count == 0 && _mcps.Count == 0;

    /// <summary>How many skills are attributed to a registry.</summary>
    public int SkillCount => _skills.Count;

    /// <summary>How many MCP servers are attributed to a registry.</summary>
    public int McpCount => _mcps.Count;

    /// <summary>Reads the attribution from the <c>asset_policy</c> section of <paramref name="document"/>.</summary>
    public static RegistryAttribution From(ConfigDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return FromAssetPolicy(document.SectionText("asset_policy"));
    }

    /// <summary>
    /// Reads the attribution from the raw text of the <c>asset_policy</c> section (the key line included, as
    /// <see cref="ConfigDocument.SectionText"/> returns it). Null, blank, unreadable or oddly shaped text is <see cref="Empty"/>.
    /// </summary>
    public static RegistryAttribution FromAssetPolicy(string? sectionYaml)
    {
        if (string.IsNullOrWhiteSpace(sectionYaml))
        {
            return Empty;
        }

        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(sectionYaml));
            if (stream.Documents.Count == 0
                || stream.Documents[0].RootNode is not YamlMappingNode root
                || Child(root, "asset_policy") is not YamlMappingNode policy)
            {
                return Empty;
            }

            var skills = ReadRules(policy, SkillKind);
            var mcps = ReadRules(policy, McpKind);
            return skills.Count == 0 && mcps.Count == 0 ? Empty : new RegistryAttribution(skills, mcps);
        }
        catch (YamlException)
        {
            // A section the app cannot read attributes nothing; the config editor is where it gets fixed.
            return Empty;
        }
    }

    /// <summary>
    /// The id of the registry source that promoted the <paramref name="kind"/> (<see cref="SkillKind"/> or <see cref="McpKind"/>) called
    /// <paramref name="name"/>, or null when no registry did (any other kind has none).
    /// </summary>
    public string? SourceOf(string kind, string name)
    {
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(name);

        var rules = kind switch
        {
            SkillKind => _skills,
            McpKind => _mcps,
            _ => NoRules,
        };

        return rules.TryGetValue(name, out var source) ? source : null;
    }

    /// <summary>
    /// The source id in a policy rule's <c>reason</c>: what follows <c>registry:</c>, trimmed. Null when the reason is not a registry's (no
    /// prefix, which is case-sensitive, or nothing after it).
    /// </summary>
    public static string? SourceIdFromReason(string? reason)
    {
        var text = reason?.Trim();
        if (string.IsNullOrEmpty(text) || !text.StartsWith(ReasonPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var id = text[ReasonPrefix.Length..].Trim();
        return id.Length == 0 ? null : id;
    }

    /// <summary>
    /// The badge text for a source id, as the TUI's <c>registry_badge</c> writes it: <c>registry:corp-skills</c>, the id cut to
    /// <paramref name="maxIdChars"/> characters with a trailing <c>...</c> when it is longer (by characters, not UTF-16 units: a surrogate pair is
    /// never split). An empty or blank id has no badge.
    /// </summary>
    public static string Badge(string? sourceId, int maxIdChars = BadgeMaxIdChars)
    {
        var id = sourceId?.Trim();
        if (string.IsNullOrEmpty(id))
        {
            return string.Empty;
        }

        var runes = id.EnumerateRunes().ToList();
        if (runes.Count > maxIdChars)
        {
            var keep = Math.Max(0, maxIdChars - 3);
            var cut = new StringBuilder(ReasonPrefix.Length + maxIdChars);
            foreach (var rune in runes.Take(keep))
            {
                _ = cut.Append(rune.ToString());
            }

            id = cut.Append("...").ToString();
        }

        return ReasonPrefix + id;
    }

    private static IReadOnlyDictionary<string, string> ReadRules(YamlMappingNode policy, string kind)
    {
        if (Child(policy, kind) is not YamlMappingNode typed || Child(typed, "registry") is not YamlSequenceNode rules)
        {
            return NoRules;
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rule in rules.Children)
        {
            if (rule is not YamlMappingNode fields)
            {
                continue;
            }

            var name = Text(Child(fields, "name"));
            var source = SourceIdFromReason(Text(Child(fields, "reason")));
            if (name.Length > 0 && source is not null)
            {
                // The TUI assigns in order, so a later rule for the same name replaces an earlier one.
                map[name] = source;
            }
        }

        return map.Count == 0 ? NoRules : map;
    }

    private static YamlNode? Child(YamlMappingNode map, string key) =>
        map.Children.TryGetValue(new YamlScalarNode(key), out var node) ? node : null;

    /// <summary>A scalar's trimmed text; empty for a missing node, a YAML null (<c>~</c>, <c>null</c>, nothing) or a list/mapping.</summary>
    private static string Text(YamlNode? node)
    {
        if (node is not YamlScalarNode { Value: { } value } scalar)
        {
            return string.Empty;
        }

        // YAML's own nulls, which Python's loader turns into None (an unquoted ~ or null); a quoted "null" is the text.
        if (scalar.Style == ScalarStyle.Plain && (value is "~" or "null" or "Null" or "NULL"))
        {
            return string.Empty;
        }

        return value.Trim();
    }
}
