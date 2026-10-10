using System.Globalization;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace DefenseClaw.Core.Config;

/// <summary>
/// The few config.yaml settings the Setup readiness checklist reads that <see cref="DefenseClawConfig"/> does not map
/// (it models only the sections the app edits): <c>llm</c>, <c>scanners</c> and <c>asset_policy</c>. Read from the raw section
/// text of a loaded <see cref="ConfigDocument"/>, so nothing is re-read from disk and the file is never touched.
/// Mirrors the paths the 0.8.10 TUI's <c>build_readiness_checks</c> asks for.
/// </summary>
public sealed record ReadinessConfig(
    string LlmProvider,
    string LlmModel,
    string LlmInstanceName,
    string LlmRegion,
    string AzureEndpoint,
    bool ScannerConfigured,
    bool AssetPolicyEnabled,
    bool RegistryRequiredButEmpty)
{
    /// <summary>The TUI's <c>REGIONAL_PROVIDERS</c>: providers that cannot route without a region (Azure: or an endpoint).</summary>
    public static readonly IReadOnlySet<string> RegionalProviders =
        new HashSet<string>(StringComparer.Ordinal) { "bedrock", "vertex_ai", "azure" };

    /// <summary>The TUI maps the provider id to its config sub-block (<c>vertex_ai</c> is persisted as <c>llm.vertex</c>).</summary>
    private static readonly IReadOnlyDictionary<string, string> RegionalBlock =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["bedrock"] = "bedrock", ["vertex_ai"] = "vertex", ["azure"] = "azure" };

    private static readonly IDeserializer Yaml = new DeserializerBuilder().Build();

    public static ReadinessConfig Empty { get; } = new(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, false, false, false);

    /// <summary>Reads the settings from <paramref name="document"/>; a section that is absent or unreadable reads as empty.</summary>
    public static ReadinessConfig From(ConfigDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var llm = Section(document, "llm");
        var provider = Str(llm, "provider");
        var block = RegionalBlock.TryGetValue(provider, out var name) ? Map(llm, name) : null;
        var region = Str(block, "region");
        if (region.Length == 0)
        {
            region = Str(llm, "region");
        }

        var scanners = Section(document, "scanners");
        var scannerConfigured =
            Str(Map(scanners, "skill_scanner"), "binary").Length > 0 ||
            Str(Map(scanners, "mcp_scanner"), "binary").Length > 0 ||
            Str(scanners, "codeguard").Length > 0;

        var policy = Section(document, "asset_policy");
        var registryEmpty = new[] { "skill", "mcp", "plugin" }.Any(kind =>
        {
            var entry = Map(policy, kind);
            return Truthy(entry, "registry_required") && !HasItems(entry, "registry");
        });

        return new ReadinessConfig(
            provider,
            Str(llm, "model"),
            Str(llm, "instance_name"),
            region,
            Str(Map(llm, "azure"), "endpoint"),
            scannerConfigured,
            Truthy(policy, "enabled"),
            registryEmpty);
    }

    private static Dictionary<object, object>? Section(ConfigDocument document, string name)
    {
        if (document.SectionText(name) is not { Length: > 0 } text || ConfigYamlGuard.Refusal(text) is not null)
        {
            return null;
        }

        try
        {
            return Yaml.Deserialize<Dictionary<object, object>>(text) is { } root ? Map(root, name) : null;
        }
        catch (YamlException)
        {
            // A section the app cannot read is "not configured" for the checklist; the config editor is where it gets fixed.
            return null;
        }
    }

    private static Dictionary<object, object>? Map(Dictionary<object, object>? parent, string key) =>
        parent is not null && parent.TryGetValue(key, out var value) ? value as Dictionary<object, object> : null;

    private static string Str(Dictionary<object, object>? map, string key) =>
        map is not null && map.TryGetValue(key, out var value) && value is not null
            ? Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty
            : string.Empty;

    private static bool Truthy(Dictionary<object, object>? map, string key) =>
        Str(map, key).Equals("true", StringComparison.OrdinalIgnoreCase);

    private static bool HasItems(Dictionary<object, object>? map, string key) =>
        map is not null && map.TryGetValue(key, out var value) && value switch
        {
            System.Collections.ICollection { Count: > 0 } => true,
            string text => text.Length > 0,
            _ => false,
        };
}
