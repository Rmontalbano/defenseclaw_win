using System.Globalization;
using DefenseClaw.Core.Logs;
using DefenseClaw.Core.Text;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace DefenseClaw.Core.Config;

/// <summary>
/// The small amount of YAML plumbing the policy readers share (<see cref="AdmissionOverrides"/>, <see cref="OperatorAssetLists"/>): config.yaml's text is
/// refused by <see cref="ConfigYamlGuard"/> before the parser sees it, read as plain dictionaries and lists, and every scalar that is drawn is cut and
/// made safe to show. A text that cannot be read is "says nothing", never an exception.
/// </summary>
internal static class PolicyYamlTree
{
    /// <summary>The longest config.yaml read; the same limit as the Overview's other config readers.</summary>
    public const int MaxLength = 1024 * 1024;

    /// <summary>The longest scalar kept for display.</summary>
    public const int MaxFieldLength = 256;

    private static readonly IDeserializer Yaml = new DeserializerBuilder().Build();

    /// <summary>The top-level mapping of <paramref name="yaml"/>, or null when it is blank, too large or deep, or not a mapping.</summary>
    public static IDictionary<object, object?>? Root(string? yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml) || yaml.Length > MaxLength || ConfigYamlGuard.Refusal(yaml) is not null)
        {
            return null;
        }

        try
        {
            return Map(Yaml.Deserialize<object?>(yaml));
        }
        catch (Exception ex) when (ex is YamlException or InvalidCastException or InvalidOperationException or ArgumentException or FormatException)
        {
            return null;
        }
    }

    public static IDictionary<object, object?>? Map(object? node) =>
        node switch
        {
            IDictionary<object, object?> map => map,
            IDictionary<string, object?> typed => typed.ToDictionary(static pair => (object)pair.Key, static pair => pair.Value),
            _ => null,
        };

    public static object? Get(IDictionary<object, object?>? map, string key) =>
        map is not null && map.TryGetValue(key, out var value) ? value : null;

    public static IDictionary<object, object?>? Child(IDictionary<object, object?>? map, string key) =>
        map is not null && map.TryGetValue(key, out var value) ? Map(value) : null;

    public static IReadOnlyList<object?> Sequence(IDictionary<object, object?>? map, string key) =>
        map is not null && map.TryGetValue(key, out var value) && value is IEnumerable<object?> items
            ? items.ToList()
            : Array.Empty<object?>();

    /// <summary>A scalar as trimmed raw text (no display clean-up): what a comparison or a switch reads.</summary>
    public static string Raw(object? node) =>
        node is string text ? text.Trim() : node is IFormattable number ? number.ToString(null, CultureInfo.InvariantCulture).Trim() : string.Empty;

    /// <summary>A scalar as text that is safe to draw: trimmed, control characters written out, secrets masked, cut to a length.</summary>
    public static string Shown(object? node)
    {
        var text = DisplayRedaction.Prose(DisplayNames.Visible(Raw(node)), MaxFieldLength);
        return text.Length <= MaxFieldLength ? text : text[..MaxFieldLength];
    }

    public static string Shown(IDictionary<object, object?>? map, string key) =>
        map is not null && map.TryGetValue(key, out var value) ? Shown(value) : string.Empty;
}
