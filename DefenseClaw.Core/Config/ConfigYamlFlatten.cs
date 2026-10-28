using System.Text;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace DefenseClaw.Core.Config;

/// <summary>
/// A config.yaml text as its keys and values (CUST-377): every scalar, list and empty map at its dotted path, so two texts are compared by what the
/// gateway reads and not by comments, spacing, key order or the way a list is written. Shared by the reload classification
/// (<see cref="ConfigReloadRules"/>) and the draft rebase (the config editor).
/// <para>
/// A list is one value at its key (the writer's change paths carry no index, <c>config_writer.restart_required</c> drops the integers), and a value's
/// canonical text keeps whether a scalar was plain or quoted (<c>true</c> and <c>"true"</c> are a bool and a string). A text that does not parse, or that
/// <see cref="ConfigYamlGuard"/> refuses, flattens to null; so does one whose root is not a map.
/// </para>
/// </summary>
public static class ConfigYamlFlatten
{
    /// <summary>YAML nesting past this is not walked: the text is called not comparable.</summary>
    private const int MaxDepth = 64;

    /// <summary>The path separator: a key with a dot in it is read as two, which only ever puts it under another section's rule.</summary>
    public const char Separator = '.';

    /// <summary>The keys and values of <paramref name="yaml"/> by path, or null when it cannot be compared.</summary>
    public static IReadOnlyDictionary<string, string>? TryFlatten(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);

        if (string.IsNullOrWhiteSpace(yaml))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        if (ConfigYamlGuard.Refusal(yaml) is not null)
        {
            return null;
        }

        YamlNode? root;
        try
        {
            var stream = new YamlStream();
            using var reader = new StringReader(yaml);
            stream.Load(reader);
            root = stream.Documents.Count == 0 ? null : stream.Documents[0].RootNode;
        }
        catch (YamlException)
        {
            return null;
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root is null)
        {
            return result;
        }

        if (root is not YamlMappingNode map)
        {
            return null;
        }

        return Walk(map, string.Empty, result, 0) ? result : null;
    }

    private static bool Walk(YamlMappingNode map, string prefix, Dictionary<string, string> into, int depth)
    {
        if (depth > MaxDepth)
        {
            return false;
        }

        if (map.Children.Count == 0 && prefix.Length > 0)
        {
            into[prefix] = "{}";
            return true;
        }

        foreach (var (keyNode, valueNode) in map.Children)
        {
            var key = keyNode is YamlScalarNode { Value: { } text } ? text : string.Empty;
            var path = prefix.Length == 0 ? key : prefix + Separator + key;
            if (valueNode is YamlMappingNode child)
            {
                if (!Walk(child, path, into, depth + 1))
                {
                    return false;
                }

                continue;
            }

            var canonical = new StringBuilder();
            if (!Canonical(valueNode, canonical, 0))
            {
                return false;
            }

            into[path] = canonical.ToString();
        }

        return true;
    }

    /// <summary>A scalar, list or map written in one form whatever its layout: the text two equal values share.</summary>
    private static bool Canonical(YamlNode node, StringBuilder into, int depth)
    {
        if (depth > MaxDepth)
        {
            return false;
        }

        switch (node)
        {
            case YamlScalarNode scalar:
                into.Append(scalar.Style == ScalarStyle.Plain ? 'p' : 's').Append(':').Append(scalar.Value?.Length ?? 0).Append(':').Append(scalar.Value);
                return true;

            case YamlSequenceNode sequence:
                into.Append('[');
                foreach (var item in sequence.Children)
                {
                    if (!Canonical(item, into, depth + 1))
                    {
                        return false;
                    }

                    into.Append(',');
                }

                into.Append(']');
                return true;

            case YamlMappingNode map:
                into.Append('{');
                foreach (var (key, value) in map.Children.OrderBy(static kv => (kv.Key as YamlScalarNode)?.Value, StringComparer.Ordinal))
                {
                    if (!Canonical(key, into, depth + 1))
                    {
                        return false;
                    }

                    into.Append('=');
                    if (!Canonical(value, into, depth + 1))
                    {
                        return false;
                    }

                    into.Append(',');
                }

                into.Append('}');
                return true;

            default:
                into.Append('?');
                return true;
        }
    }

    /// <summary>The value's text for a person: the scalar as written, a list as <c>[a, b]</c>; used only where it is masked before it is shown.</summary>
    public static string Display(string canonical)
    {
        ArgumentNullException.ThrowIfNull(canonical);

        if (canonical.Length >= 2 && canonical[0] is 'p' or 's' && canonical[1] == ':')
        {
            var colon = canonical.IndexOf(':', 2);
            if (colon > 0 && int.TryParse(canonical.AsSpan(2, colon - 2), out var length) && colon + 1 + length == canonical.Length)
            {
                return canonical.Substring(colon + 1, length);
            }
        }

        return canonical switch
        {
            "{}" => "{}",
            _ => "(a list or map: see the file)",
        };
    }
}
