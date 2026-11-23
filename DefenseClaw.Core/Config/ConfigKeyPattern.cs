using System.Collections;
using System.Globalization;

namespace DefenseClaw.Core.Config;

/// <summary>What a pattern segment matches.</summary>
internal enum ConfigKeySegmentKind
{
    /// <summary>A mapping key of this name.</summary>
    Name,

    /// <summary><c>*</c>: every value of a mapping.</summary>
    EveryValue,

    /// <summary><c>[]</c>: every item of a list.</summary>
    EveryItem,
}

internal readonly record struct ConfigKeySegment(ConfigKeySegmentKind Kind, string Name);

/// <summary>The keys found so far by one <see cref="ConfigKeyFloors.RefusedBy"/> call, and what is left of its visit budget.</summary>
internal sealed class ConfigKeyWalk
{
    /// <summary>Node visits and mapping entries scanned, over all rows of one call. Aliased YAML shares nodes, so this bounds the work, not the file size.</summary>
    public const int MaxVisits = 200_000;

    /// <summary>The most keys one call reports.</summary>
    public const int MaxReported = 64;

    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    public int Visits { get; set; } = MaxVisits;

    public List<string> Found { get; } = [];

    public bool Full => Found.Count >= MaxReported;

    public bool Spent => Visits <= 0 || Full;

    /// <summary>Adds a key once, in the order found; ignored when the list is full.</summary>
    public void Report(string key)
    {
        if (!Full && _seen.Add(key))
        {
            Found.Add(key);
        }
    }
}

/// <summary>
/// A config key path with a small path language, the one the runtime's upgrade shim uses: segments split at '.', <c>[]</c> after a name is every item of
/// a list, a <c>*</c> segment is every value of a mapping. A concrete key is named <c>name[i]</c> for a list item.
/// <para>
/// A row matches when the whole path exists; a key named at all counts, even with no value (the runtime's shim skips a null or empty value, this app does not).
/// A mapping name is skipped when it is empty or has a dot. It is echoed only when it is 1 to 64 characters of letters, digits, '_' and '-'; any other
/// name is reported under the pattern's own text, so a hostile name never reaches the UI.
/// </para>
/// </summary>
internal sealed class ConfigKeyPattern
{
    private const int MaxNameLength = 64;

    private readonly ConfigKeySegment[] _segments;

    private ConfigKeyPattern(string text, ConfigKeySegment[] segments)
    {
        Text = text;
        _segments = segments;
        IsPlain = segments.All(static s => s.Kind == ConfigKeySegmentKind.Name);
    }

    /// <summary>The pattern as written.</summary>
    public string Text { get; }

    /// <summary>True when no segment is <c>*</c> or <c>[]</c>: a literal dotted path.</summary>
    public bool IsPlain { get; }

    public static ConfigKeyPattern Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var segments = new List<ConfigKeySegment>();
        foreach (var part in text.Split('.'))
        {
            var open = part.IndexOf('[', StringComparison.Ordinal);
            var name = open < 0 ? part : part[..open];
            if (name.Length > 0)
            {
                segments.Add(name == "*"
                    ? new ConfigKeySegment(ConfigKeySegmentKind.EveryValue, name)
                    : new ConfigKeySegment(ConfigKeySegmentKind.Name, name));
            }

            if (open >= 0)
            {
                for (var i = open; i + 1 < part.Length && part[i] == '[' && part[i + 1] == ']'; i += 2)
                {
                    segments.Add(new ConfigKeySegment(ConfigKeySegmentKind.EveryItem, string.Empty));
                }
            }
        }

        return new ConfigKeyPattern(text, segments.ToArray());
    }

    /// <summary>Adds each concrete key of <paramref name="root"/> this pattern names to <paramref name="walk"/>. Never throws; stops when the budget is spent.</summary>
    public void Walk(object? root, ConfigKeyWalk walk)
    {
        ArgumentNullException.ThrowIfNull(walk);
        Step(root, 0, string.Empty, false, walk);
    }

    private void Step(object? node, int index, string path, bool hostile, ConfigKeyWalk walk)
    {
        if (walk.Spent)
        {
            return;
        }

        if (index == _segments.Length)
        {
            walk.Report(hostile ? Text : path);
            return;
        }

        walk.Visits--;
        var segment = _segments[index];
        switch (segment.Kind)
        {
            case ConfigKeySegmentKind.Name:
                StepName(node, segment.Name, index, path, hostile, walk);
                return;
            case ConfigKeySegmentKind.EveryValue:
                StepEveryValue(node, index, path, hostile, walk);
                return;
            default:
                StepEveryItem(node, index, path, hostile, walk);
                return;
        }
    }

    private void StepName(object? node, string name, int index, string path, bool hostile, ConfigKeyWalk walk)
    {
        if (ConfigYamlReader.AsMap(node) is not { } map)
        {
            return;
        }

        foreach (var (key, value) in map)
        {
            if (--walk.Visits < 0)
            {
                return;
            }

            if (string.Equals(Convert.ToString(key, CultureInfo.InvariantCulture), name, StringComparison.Ordinal))
            {
                Step(value, index + 1, path.Length == 0 ? name : path + "." + name, hostile, walk);
                return;
            }
        }
    }

    private void StepEveryValue(object? node, int index, string path, bool hostile, ConfigKeyWalk walk)
    {
        if (ConfigYamlReader.AsMap(node) is not { } entries)
        {
            return;
        }

        walk.Visits -= entries.Count;
        if (walk.Spent)
        {
            return;
        }

        var names = new List<string>();
        foreach (var key in entries.Keys)
        {
            if (key is string candidate && candidate.Length > 0 && !candidate.Contains('.', StringComparison.Ordinal))
            {
                names.Add(candidate);
            }
        }

        names.Sort(StringComparer.Ordinal);
        foreach (var name in names)
        {
            if (walk.Spent)
            {
                return;
            }

            walk.Visits--;
            if (!entries.TryGetValue(name, out var child))
            {
                continue;
            }

            if (IsEchoable(name))
            {
                Step(child, index + 1, path.Length == 0 ? name : path + "." + name, hostile, walk);
            }
            else
            {
                Step(child, index + 1, path, true, walk);
            }
        }
    }

    private void StepEveryItem(object? node, int index, string path, bool hostile, ConfigKeyWalk walk)
    {
        if (node is not IList items)
        {
            return;
        }

        for (var i = 0; i < items.Count; i++)
        {
            if (walk.Spent)
            {
                return;
            }

            walk.Visits--;
            Step(items[i], index + 1, path + "[" + i.ToString(CultureInfo.InvariantCulture) + "]", hostile, walk);
        }
    }

    private static bool IsEchoable(string name)
    {
        if (name.Length is 0 or > MaxNameLength)
        {
            return false;
        }

        foreach (var c in name)
        {
            if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }
}
