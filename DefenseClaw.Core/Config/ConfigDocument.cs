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
    /// The line terminator this file uses: <c>"\r\n"</c> when its first line break is a CRLF (a file
    /// Windows tooling wrote — the DefenseClaw CLI's own <c>config.yaml</c> is one), otherwise
    /// <c>"\n"</c>, which is also the answer for text with no line break at all. Anything the app
    /// has to add to the file itself — the newline that closes a last line, the one that separates
    /// an appended section — uses this, so a CRLF file is never left with a stray bare LF.
    /// </summary>
    public string LineEnding => DetectLineEnding(RawText) ?? "\n";

    /// <summary>
    /// <paramref name="text"/> (a section block bound for this document) with its last line
    /// terminated, using the block's own line ending when it has one and the file's otherwise.
    /// Text that already ends in a line break is returned unchanged.
    /// </summary>
    public string WithTrailingLineEnding(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return text.EndsWith('\n') ? text : text + (DetectLineEnding(text) ?? LineEnding);
    }

    /// <summary>
    /// Returns <see cref="RawText"/> with one top-level section's block swapped for
    /// <paramref name="replacement"/>, leaving every other byte alone. Appends the
    /// section when it is absent. Any line break this has to add (see
    /// <see cref="WithTrailingLineEnding"/>) matches the file's own, so a CRLF file stays CRLF.
    /// </summary>
    public string WithSectionReplaced(string name, string replacement)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(replacement);

        if (!Sections.TryGetValue(name, out var existing))
        {
            var separator = RawText.Length == 0 || RawText.EndsWith('\n') ? string.Empty : LineEnding;
            return RawText + separator + WithTrailingLineEnding(replacement);
        }

        var index = RawText.IndexOf(existing, StringComparison.Ordinal);
        if (index < 0)
        {
            return RawText;
        }

        return string.Concat(
            RawText.AsSpan(0, index),
            WithTrailingLineEnding(replacement),
            RawText.AsSpan(index + existing.Length));
    }

    /// <summary>The terminator of the first line break in <paramref name="text"/>, or <see langword="null"/> when there is none.</summary>
    private static string? DetectLineEnding(string text)
    {
        var lf = text.IndexOf('\n');
        if (lf < 0)
        {
            return null;
        }

        return lf > 0 && text[lf - 1] == '\r' ? "\r\n" : "\n";
    }
}
