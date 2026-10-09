using System.Collections;
using System.Globalization;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace DefenseClaw.Core.Config;

/// <summary>
/// Read-only lookup into config.yaml's text by key path, for the settings the typed model (<see cref="DefenseClawConfig"/>) does not carry or
/// cannot tell from "absent" (a bool the file does not name is <c>false</c> on the model, but the runtime's own default may be <c>true</c>).
/// Nothing is opened: the text is what <see cref="ConfigDocument.RawText"/> already holds. A text that is not YAML, is not a mapping, or is
/// larger than <see cref="MaxLength"/> reads as an empty document - every question is then "absent" - never as an error.
/// <para>
/// <b>Booleans are the runtime's.</b> The loader is PyYAML (YAML 1.1), which reads <c>true</c>, <c>yes</c> and <c>on</c> as true and
/// <c>false</c>, <c>no</c> and <c>off</c> as false, in any of three letter cases; any other word is not a boolean, and <see cref="Bool"/>
/// answers null for it so the caller falls back to the default instead of guessing.
/// </para>
/// </summary>
public sealed class ConfigYamlReader
{
    /// <summary>The longest config.yaml read (it is a few KiB; the same bound the other config readers use).</summary>
    public const int MaxLength = 1024 * 1024;

    private static readonly IDeserializer Yaml = new DeserializerBuilder().Build();

    private static readonly string[] TrueWords = { "true", "yes", "on" };
    private static readonly string[] FalseWords = { "false", "no", "off" };

    private readonly IDictionary<object, object?>? _root;

    private ConfigYamlReader(IDictionary<object, object?>? root)
    {
        _root = root;
    }

    /// <summary>A document that says nothing.</summary>
    public static ConfigYamlReader Empty { get; } = new(null);

    /// <summary>The reader of <paramref name="yaml"/>; <see cref="Empty"/> for anything it cannot read as a mapping.</summary>
    public static ConfigYamlReader Parse(string? yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml) || yaml.Length > MaxLength)
        {
            return Empty;
        }

        try
        {
            return new ConfigYamlReader(Map(Yaml.Deserialize<object?>(yaml)));
        }
        catch (Exception ex) when (ex is YamlException or InvalidCastException or InvalidOperationException or ArgumentException or FormatException)
        {
            return Empty;
        }
    }

    /// <summary>The reader of a loaded document's text.</summary>
    public static ConfigYamlReader From(ConfigDocument? document) => document is null ? Empty : Parse(document.RawText);

    /// <summary>True when the key path names anything at all (a scalar, a list or a mapping; a key with no value counts).</summary>
    public bool Has(params string[] path) => TryFind(path, out _);

    /// <summary>The scalar at <paramref name="path"/> as trimmed text; null when absent or not a scalar.</summary>
    public string? Text(params string[] path) =>
        TryFind(path, out var node) && node is string text ? text.Trim() : null;

    /// <summary>The boolean at <paramref name="path"/> the way the runtime's loader reads it; null when absent or not one of its boolean words.</summary>
    public bool? Bool(params string[] path)
    {
        var text = Text(path);
        if (text is null)
        {
            return null;
        }

        if (TrueWords.Contains(text, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        return FalseWords.Contains(text, StringComparer.OrdinalIgnoreCase) ? false : null;
    }

    /// <summary>The whole number at <paramref name="path"/>; null when absent or not one.</summary>
    public long? Integer(params string[] path) =>
        Text(path) is { } text && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    /// <summary>
    /// The strings at <paramref name="path"/>: a list of scalars, or one string split on commas (the CLI accepts scan roots either way). Empty
    /// items are dropped. Null when the path is absent or holds something else (a mapping); a key with no value is an empty list.
    /// </summary>
    public IReadOnlyList<string>? List(params string[] path)
    {
        if (!TryFind(path, out var node))
        {
            return null;
        }

        if (node is null)
        {
            return Array.Empty<string>();
        }

        if (node is string text)
        {
            return text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        if (Map(node) is null && node is IEnumerable items)
        {
            return items.Cast<object?>()
                .OfType<string>()
                .Select(static item => item.Trim())
                .Where(static item => item.Length > 0)
                .ToArray();
        }

        return null;
    }

    /// <summary>The keys of the mapping at <paramref name="path"/> (the root when the path is empty), in file order; empty when it is not one.</summary>
    public IReadOnlyList<string> Keys(params string[] path)
    {
        object? node = _root;
        if (path.Length > 0 && !TryFind(path, out node))
        {
            return Array.Empty<string>();
        }

        return Map(node) is { } map
            ? map.Keys.Select(static key => Convert.ToString(key, CultureInfo.InvariantCulture) ?? string.Empty).ToArray()
            : Array.Empty<string>();
    }

    /// <summary>Walks the mapping keys; true when every segment names a key, and <paramref name="node"/> is what the last one holds (null for a key with no value).</summary>
    private bool TryFind(string[] path, out object? node)
    {
        node = _root;
        foreach (var segment in path)
        {
            if (Map(node) is not { } map)
            {
                node = null;
                return false;
            }

            var found = false;
            object? next = null;
            foreach (var (key, value) in map)
            {
                if (string.Equals(Convert.ToString(key, CultureInfo.InvariantCulture), segment, StringComparison.Ordinal))
                {
                    next = value;
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                node = null;
                return false;
            }

            node = next;
        }

        return path.Length > 0 || _root is not null;
    }

    private static IDictionary<object, object?>? Map(object? node) =>
        node switch
        {
            IDictionary<object, object?> map => map,
            IDictionary<string, object?> typed => typed.ToDictionary(static p => (object)p.Key, static p => p.Value),
            _ => null,
        };
}
