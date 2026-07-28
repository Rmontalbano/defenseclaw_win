namespace DefenseClaw.Core.Config;

/// <summary>
/// A parsed config.yaml plus the exact bytes it came from. The app edits by replacing
/// whole top-level section blocks in <see cref="RawText"/>, so unmapped settings — and
/// comments, ordering and formatting — survive untouched.
/// </summary>
public sealed class ConfigDocument
{
    internal ConfigDocument(
        string path,
        string rawText,
        DefenseClawConfig config,
        IReadOnlyDictionary<string, string> sections,
        DateTimeOffset loadedAt)
    {
        Path = path;
        RawText = rawText;
        Config = config;
        Sections = sections;
        LoadedAt = loadedAt;
    }

    public string Path { get; }

    /// <summary>Verbatim file contents.</summary>
    public string RawText { get; }

    public DefenseClawConfig Config { get; }

    /// <summary>Raw YAML text of each top-level section, keyed by section name.</summary>
    public IReadOnlyDictionary<string, string> Sections { get; }

    public DateTimeOffset LoadedAt { get; }

    /// <summary>Sections with no typed counterpart — preserved so nothing is dropped.</summary>
    public IReadOnlyDictionary<string, string> UnknownSections =>
        Sections
            .Where(kv => !DefenseClawConfig.KnownSections.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

    public string? SectionText(string name) => Sections.TryGetValue(name, out var text) ? text : null;

    /// <summary>
    /// Returns <see cref="RawText"/> with one top-level section's block swapped for
    /// <paramref name="replacement"/>, leaving every other byte alone. Appends the
    /// section when it is absent.
    /// </summary>
    public string WithSectionReplaced(string name, string replacement)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(replacement);

        if (!Sections.TryGetValue(name, out var existing))
        {
            var separator = RawText.Length == 0 || RawText.EndsWith('\n') ? string.Empty : "\n";
            return RawText + separator + EnsureTrailingNewline(replacement);
        }

        var index = RawText.IndexOf(existing, StringComparison.Ordinal);
        if (index < 0)
        {
            return RawText;
        }

        return string.Concat(
            RawText.AsSpan(0, index),
            EnsureTrailingNewline(replacement),
            RawText.AsSpan(index + existing.Length));
    }

    private static string EnsureTrailingNewline(string text) =>
        text.EndsWith('\n') ? text : text + "\n";
}
