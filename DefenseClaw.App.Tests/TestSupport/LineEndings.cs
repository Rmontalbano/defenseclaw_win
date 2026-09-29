namespace DefenseClaw.App.Tests.TestSupport;

/// <summary>
/// Keeps these tests independent of how the source was checked out. A multi-line literal in a <c>.cs</c> file carries
/// whatever line ending the file has: LF on the machine that wrote it, CRLF after <c>actions/checkout</c> on
/// <c>windows-latest</c> (or under <c>core.autocrlf=true</c>). A test that says <c>Replace("  mode: observe\n", ...)</c>
/// against such a literal silently matches nothing on the CRLF checkout. So every multi-line sample is normalised to
/// LF here, and a test that cares about line endings asks for <c>"\n"</c> or <c>"\r\n"</c> explicitly — see
/// <see cref="Both"/> — rather than inheriting one.
/// </summary>
public static class LineEndings
{
    public const string Lf = "\n";

    public const string Crlf = "\r\n";

    /// <summary>Theory data: the two line endings a config.yaml or CLI screen can arrive with.</summary>
    public static TheoryData<string> Both { get; } = new() { Lf, Crlf };

    /// <summary>Theory data: each of <paramref name="values"/> under each line ending, as <c>(value, eol)</c>.</summary>
    public static TheoryData<string, string> Each(params string[] values)
    {
        var data = new TheoryData<string, string>();
        foreach (var value in values)
        {
            data.Add(value, Lf);
            data.Add(value, Crlf);
        }

        return data;
    }

    /// <summary>Every line break — CRLF or a lone CR — as LF, whatever the literal was checked out with.</summary>
    public static string Normalize(string text) =>
        text.Replace(Crlf, Lf, StringComparison.Ordinal).Replace('\r', '\n');

    /// <summary><paramref name="text"/> with every line break as <paramref name="eol"/>.</summary>
    public static string With(string text, string eol) =>
        Normalize(text).Replace(Lf, eol, StringComparison.Ordinal);

    /// <summary>
    /// True when every line break in <paramref name="text"/> is exactly <paramref name="eol"/> — no bare LF in a CRLF
    /// text, no CR at all in an LF one. What "the file's line endings were preserved" means.
    /// </summary>
    public static bool IsUniform(string text, string eol) =>
        eol == Crlf
            ? With(text, Crlf) == text
            : !text.Contains('\r', StringComparison.Ordinal);
}
