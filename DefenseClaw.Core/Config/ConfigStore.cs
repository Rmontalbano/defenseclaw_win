using DefenseClaw.Core.Paths;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DefenseClaw.Core.Config;

/// <summary>Thrown when config.yaml exists but cannot be parsed as YAML.</summary>
public sealed class ConfigParseException : Exception
{
    public ConfigParseException(string path, string message, Exception? inner = null)
        : base(message, inner)
    {
        Path = path;
    }

    public string Path { get; }
}

/// <summary>
/// Loads <c>config.yaml</c> into a typed model while retaining the raw text, so callers
/// can present a form view and still write the file back non-destructively.
/// </summary>
public sealed class ConfigStore
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private readonly DefenseClawPaths _paths;

    public ConfigStore(DefenseClawPaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    public string ConfigFilePath => _paths.ConfigFilePath;

    /// <summary>
    /// Reads config.yaml. A missing file yields a document with defaults and empty raw
    /// text — the "installed but not initialized" case, not an error.
    /// </summary>
    public ConfigDocument Load()
    {
        var path = _paths.ConfigFilePath;
        if (!File.Exists(path))
        {
            return new ConfigDocument(path, string.Empty, new DefenseClawConfig(),
                new Dictionary<string, string>(StringComparer.Ordinal), DateTimeOffset.UtcNow);
        }

        return Parse(File.ReadAllText(path), path);
    }

    public async Task<ConfigDocument> LoadAsync(CancellationToken cancellationToken = default)
    {
        var path = _paths.ConfigFilePath;
        if (!File.Exists(path))
        {
            return new ConfigDocument(path, string.Empty, new DefenseClawConfig(),
                new Dictionary<string, string>(StringComparer.Ordinal), DateTimeOffset.UtcNow);
        }

        var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return Parse(text, path);
    }

    /// <summary>Parses YAML text without touching the filesystem.</summary>
    public static ConfigDocument Parse(string yaml, string path = "<memory>")
    {
        ArgumentNullException.ThrowIfNull(yaml);

        DefenseClawConfig config;
        try
        {
            config = Deserializer.Deserialize<DefenseClawConfig>(yaml) ?? new DefenseClawConfig();
        }
        catch (YamlException ex)
        {
            throw new ConfigParseException(path, $"config.yaml is not valid YAML: {ex.Message}", ex);
        }

        return new ConfigDocument(path, yaml, config, SplitTopLevelSections(yaml), DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Splits YAML into raw top-level blocks keyed by section name. Purely textual: a
    /// section starts at a line whose first character is non-whitespace and which
    /// contains a colon, and runs until the next such line.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> SplitTopLevelSections(string yaml)
    {
        var sections = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(yaml))
        {
            return sections;
        }

        // Keep line terminators so blocks concatenate back into the original text.
        var lines = SplitKeepingLineEndings(yaml);
        string? currentKey = null;
        var buffer = new List<string>();

        foreach (var line in lines)
        {
            var key = TopLevelKey(line);
            if (key is not null)
            {
                Flush(sections, currentKey, buffer);
                currentKey = key;
                buffer.Clear();
            }

            if (currentKey is not null)
            {
                buffer.Add(line);
            }
        }

        Flush(sections, currentKey, buffer);
        return sections;
    }

    private static void Flush(Dictionary<string, string> sections, string? key, List<string> buffer)
    {
        if (key is null || buffer.Count == 0)
        {
            return;
        }

        sections[key] = string.Concat(buffer);
    }

    private static string? TopLevelKey(string line)
    {
        if (line.Length == 0 || char.IsWhiteSpace(line[0]) || line[0] == '#' || line[0] == '-')
        {
            return null;
        }

        var colon = line.IndexOf(':');
        if (colon <= 0)
        {
            return null;
        }

        var key = line[..colon].Trim();
        return key.Length == 0 ? null : key;
    }

    private static List<string> SplitKeepingLineEndings(string text)
    {
        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                lines.Add(text[start..(i + 1)]);
                start = i + 1;
            }
        }

        if (start < text.Length)
        {
            lines.Add(text[start..]);
        }

        return lines;
    }
}
