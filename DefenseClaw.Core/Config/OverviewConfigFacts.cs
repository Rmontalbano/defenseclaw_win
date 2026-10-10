using System.Globalization;
using DefenseClaw.Core.Logs;
using DefenseClaw.Core.Observability;
using DefenseClaw.Core.Text;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace DefenseClaw.Core.Config;

/// <summary>
/// Human approval as the TUI's Configuration box prints it (<c>ON (min HIGH)</c> or <c>OFF</c>): the human-in-the-loop switch of the guardrail
/// (<c>guardrail.hilt</c>, spelled <c>hitl</c> by older files) and the lowest severity that asks a person.
/// </summary>
/// <param name="Enabled">Whether blocks of at least <paramref name="MinSeverity"/> wait for a person.</param>
/// <param name="MinSeverity">Upper-cased; <see cref="DefaultMinSeverity"/> when config.yaml does not say.</param>
public sealed record HumanApproval(bool Enabled, string MinSeverity)
{
    /// <summary>The runtime's own default for <c>hilt.min_severity</c> (<c>_merge_hilt</c>, and the TUI's <c>or 'HIGH'</c>).</summary>
    public const string DefaultMinSeverity = "HIGH";

    /// <summary>What a block that is absent, empty or says nothing means: off, at the default severity.</summary>
    public static HumanApproval Off { get; } = new(false, DefaultMinSeverity);

    /// <summary>The TUI's words: <c>ON (min HIGH)</c>, <c>OFF</c>.</summary>
    public string Text => Enabled ? $"ON (min {MinSeverity})" : "OFF";
}

/// <summary>
/// The few config.yaml settings the Overview's Configuration card, its notices and its Quick Actions read that <see cref="DefenseClawConfig"/> does
/// not model (it models only the sections the app edits): the human-approval switch, the policy folder, the LLM provider and model, the AI Defense
/// endpoint, the runtime's notification switch, and whether the connector map can be read as a roster. They are the values the 0.8.10 TUI's
/// <c>OverviewConfig</c> carries (<c>tui/app.py: _overview_config</c>), resolved the way its loader resolves them, so the card says what the TUI says.
/// <para>
/// <b>Read from the text the app already holds</b> (<see cref="ConfigDocument.RawText"/>): no file is opened and no process runs. A document that
/// cannot be read as a mapping is "says nothing" (<see cref="Empty"/>), never an error. <b>The raw endpoint is never kept:</b> the AI Defense address
/// is reduced to <c>host[:port]</c> (<see cref="EndpointDisplay"/>) while it is read, so nothing built on this record can show a credential that was
/// in a URL; no key, token or <c>*_env</c> value is read at all.
/// </para>
/// </summary>
/// <param name="GlobalApproval"><c>guardrail.hilt</c>: the switch every connector inherits.</param>
/// <param name="ConnectorApproval">A connector's own <c>hilt</c> block, which replaces the global one for it (the runtime's <c>effective_hilt</c>); keyed by the connector's normalised name, and only for connectors that have one.</param>
/// <param name="ConnectorNames"><c>guardrail.connectors</c>' keys, normalised, distinct and sorted (the runtime's <c>active_connectors()</c> when the map is not empty).</param>
/// <param name="PolicyDirectory"><c>policy_dir</c> as written; empty when the key is there and empty; null when config.yaml is silent (the runtime then uses <c>&lt;data dir&gt;\policies</c>).</param>
/// <param name="LlmProvider"><c>llm.provider</c>, else <c>inspect_llm.provider</c>; empty when neither is set.</param>
/// <param name="LlmModel"><c>llm.model</c>, else <c>default_llm_model</c>, else <c>inspect_llm.model</c> (the loader's migration order); empty when none is set.</param>
/// <param name="AiDefenseHost"><c>host[:port]</c> of <c>cisco_ai_defense.endpoint</c>; <see cref="EndpointDisplay.Unreadable"/> for an address that has no host this can vouch for; empty when the key is there and empty (the TUI then shows no row); null when config.yaml is silent (the runtime's default endpoint applies).</param>
/// <param name="NotificationsEnabled"><c>notifications.enabled</c>; null when config.yaml is silent (the runtime then defaults to on, on Windows).</param>
/// <param name="RosterProblem">One sentence when the connector map has a name the runtime's own check rejects, so the roster it builds is not what config.yaml says; null when it reads cleanly.</param>
public sealed record OverviewConfigFacts(
    HumanApproval GlobalApproval,
    IReadOnlyDictionary<string, HumanApproval> ConnectorApproval,
    IReadOnlyList<string> ConnectorNames,
    string? PolicyDirectory,
    string LlmProvider,
    string LlmModel,
    string? AiDefenseHost,
    bool? NotificationsEnabled,
    string? RosterProblem)
{
    /// <summary>The longest config.yaml read (it is a few KiB).</summary>
    public const int MaxLength = ObservabilityConfigFacts.MaxLength;

    private const int MaxFieldLength = 512;
    private const int MaxNameLength = 64;

    /// <summary>The YAML 1.1 words for true that the runtime's loader (PyYAML) reads as a boolean; anything else is false.</summary>
    private static readonly string[] TrueWords = { "true", "yes", "on" };

    private static readonly IDeserializer Yaml = new DeserializerBuilder().Build();

    /// <summary>A config.yaml that says nothing about any of this.</summary>
    public static OverviewConfigFacts Empty { get; } = new(
        HumanApproval.Off,
        new Dictionary<string, HumanApproval>(StringComparer.Ordinal),
        Array.Empty<string>(),
        null,
        string.Empty,
        string.Empty,
        null,
        null,
        null);

    /// <summary>
    /// The approval the TUI shows: a connector's own block when the install has exactly one connector and that block exists (the TUI's
    /// <c>len(actives) == 1</c> branch), the global block otherwise. With several connectors the global block is the only one it shows.
    /// </summary>
    public HumanApproval Approval =>
        ConnectorNames.Count == 1 && ConnectorApproval.TryGetValue(ConnectorNames[0], out var own) ? own : GlobalApproval;

    /// <summary>The facts in <paramref name="document"/>; <see cref="Empty"/> for a null one.</summary>
    public static OverviewConfigFacts FromConfig(ConfigDocument? document) => document is null ? Empty : FromYaml(document.RawText);

    /// <summary>The facts in config.yaml's text. Never throws: text that is not a YAML mapping, or is too large, is <see cref="Empty"/>.</summary>
    public static OverviewConfigFacts FromYaml(string? yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml) || yaml.Length > MaxLength || ConfigYamlGuard.Refusal(yaml) is not null)
        {
            return Empty;
        }

        Dictionary<string, object?>? root;
        try
        {
            root = Yaml.Deserialize<Dictionary<string, object?>>(yaml);
        }
        catch (Exception ex) when (ex is YamlException or InvalidCastException or InvalidOperationException or ArgumentException or FormatException)
        {
            return Empty;
        }

        if (root is null)
        {
            return Empty;
        }

        var guardrail = Map(root.GetValueOrDefault("guardrail"));
        var connectors = Map(guardrail?.GetValueOrDefault("connectors"));

        var approval = new Dictionary<string, HumanApproval>(StringComparer.Ordinal);
        var rawNames = new List<string>();
        if (connectors is not null)
        {
            foreach (var (key, value) in connectors)
            {
                var name = Convert.ToString(key, CultureInfo.InvariantCulture) ?? string.Empty;
                rawNames.Add(name);
                if (!string.IsNullOrWhiteSpace(name) && ReadApproval(Map(value)) is { } own)
                {
                    approval[Normalize(name)] = own;
                }
            }
        }

        var names = rawNames
            .Where(static n => !string.IsNullOrWhiteSpace(n))
            .Select(Normalize)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static n => n, StringComparer.Ordinal)
            .ToArray();

        var llm = Map(root.GetValueOrDefault("llm"));
        var inspect = Map(root.GetValueOrDefault("inspect_llm"));
        var cisco = Map(root.GetValueOrDefault("cisco_ai_defense"));
        var notifications = Map(root.GetValueOrDefault("notifications"));

        return new OverviewConfigFacts(
            ReadApproval(guardrail) ?? HumanApproval.Off,
            approval,
            names,
            root.ContainsKey("policy_dir") ? Shown(root["policy_dir"]) : null,
            FirstOf(Scalar(llm, "provider"), Scalar(inspect, "provider")),
            FirstOf(Scalar(llm, "model"), Scalar(root, "default_llm_model"), Scalar(inspect, "model")),
            cisco is not null && cisco.ContainsKey("endpoint") ? EndpointDisplay.Host(Text(cisco["endpoint"])) : null,
            notifications is not null && notifications.ContainsKey("enabled") ? IsTrue(notifications["enabled"]) : null,
            RosterProblemOf(rawNames));
    }

    /// <summary>
    /// A connector name as the runtime keys it (<c>connector_paths.normalize</c>): trimmed and lower-cased, and the two spellings of OpenHands are
    /// one. An empty name is the runtime's <c>openclaw</c> default.
    /// </summary>
    public static string Normalize(string? connector)
    {
        var name = connector?.Trim().ToLowerInvariant() ?? string.Empty;
        if (name.Length == 0)
        {
            return "openclaw";
        }

        return name is "open-hands" or "open_hands" ? "openhands" : name;
    }

    /// <summary>
    /// The runtime's own check of the connector map (<c>GuardrailConfig.validate</c>, run when it loads config.yaml), for the one thing that
    /// decides the roster: a name that is empty, or two names that mean the same connector (<c>Codex</c> and <c>codex</c>). Its messages, first
    /// violation first, in the order it walks the names. Nothing else it checks (modes, severities) is about the roster.
    /// </summary>
    private static string? RosterProblemOf(IReadOnlyList<string> names)
    {
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in names.OrderBy(static n => n, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return "guardrail.connectors: empty connector name is not allowed";
            }

            var normal = Normalize(name);
            if (seen.TryGetValue(normal, out var first))
            {
                return $"guardrail.connectors: '{Shown(first)}' and '{Shown(name)}' refer to the same connector '{Shown(normal)}'; keep only one";
            }

            seen[normal] = name;
        }

        return null;
    }

    /// <summary>
    /// A <c>hilt</c> (or <c>hitl</c>) block of <paramref name="owner"/> (the guardrail, or one connector's entry), the way <c>_merge_hilt</c> reads
    /// it; null when <paramref name="owner"/> has none, which means "inherit". A block that is empty is the default (off), not "inherit".
    /// </summary>
    private static HumanApproval? ReadApproval(IDictionary<object, object?>? owner)
    {
        if (owner is null)
        {
            return null;
        }

        var node = owner.GetValueOrDefault("hilt") ?? owner.GetValueOrDefault("hitl");
        if (node is null)
        {
            return null;
        }

        if (Map(node) is not { Count: > 0 } block)
        {
            return HumanApproval.Off;
        }

        var severity = Text(block.GetValueOrDefault("min_severity")).ToUpperInvariant();
        return new HumanApproval(IsTrue(block.GetValueOrDefault("enabled")), severity.Length == 0 ? HumanApproval.DefaultMinSeverity : Cut(DisplayNames.Visible(severity), MaxNameLength));
    }

    private static string FirstOf(params string[] values) => values.FirstOrDefault(static v => v.Length > 0) ?? string.Empty;

    /// <summary>A mapping node, whichever key type the deserializer produced; null for anything else.</summary>
    private static IDictionary<object, object?>? Map(object? node) =>
        node switch
        {
            IDictionary<object, object?> map => map,
            IDictionary<string, object?> typed => typed.ToDictionary(static pair => (object)pair.Key, static pair => pair.Value),
            _ => null,
        };

    private static string Scalar(Dictionary<string, object?> map, string key) => Shown(map.GetValueOrDefault(key));

    private static string Scalar(IDictionary<object, object?>? map, string key) => map is null ? string.Empty : Shown(map.GetValueOrDefault(key));

    /// <summary>A scalar as text that is safe to draw: trimmed, control and bidirectional characters written out, cut to a sensible length; a node that is not a scalar is empty.</summary>
    private static string Shown(object? node) => Cut(DisplayRedaction.Prose(DisplayNames.Visible(Text(node)), MaxFieldLength), MaxFieldLength);

    private static string Text(object? node) =>
        node is string text ? text.Trim() : node is IFormattable number ? number.ToString(null, CultureInfo.InvariantCulture).Trim() : string.Empty;

    private static bool IsTrue(object? node) => node is string text && TrueWords.Contains(text.Trim(), StringComparer.OrdinalIgnoreCase);

    private static string Cut(string text, int limit) => text.Length <= limit ? text : text[..limit];
}
