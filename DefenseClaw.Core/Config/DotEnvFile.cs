namespace DefenseClaw.Core.Config;

/// <summary>
/// Minimal <c>KEY=VALUE</c> reader matching what the Python CLI (python-dotenv) and the Go
/// gateway (godotenv) accept in <c>~/.defenseclaw/.env</c>:
/// <list type="bullet">
/// <item><description>blank lines and whole-line <c>#</c> comments are skipped;</description></item>
/// <item><description>an optional <c>export</c> prefix is tolerated, followed by any run of
/// spaces or tabs;</description></item>
/// <item><description>surrounding single or double quotes are stripped, and everything inside
/// them — <c>#</c> included — is the value;</description></item>
/// <item><description>an <b>unquoted</b> value ends at the first <c>#</c> that is preceded by
/// whitespace: <c>TOKEN=abc   # rotated</c> is <c>abc</c>, while <c>TOKEN=abc#1</c> keeps its
/// <c>#</c>. A comment may also follow a quoted value.</description></item>
/// </list>
/// <para>
/// The comment rule is not cosmetic. This file is where the gateway bearer token lives, and
/// <see cref="TokenResolver"/> sends whatever is parsed here as the <c>Authorization</c> header:
/// a value that kept its trailing comment is a wrong token, and the gateway answers every
/// authenticated call with 401 while the file looks perfectly fine to its owner and to both
/// other readers.
/// </para>
/// <para>
/// Escape sequences inside double quotes are <b>not</b> interpreted (<c>\n</c> stays two
/// characters); an escaped quote is only honoured for the purpose of finding where the value
/// ends. Multi-line quoted values are not supported — the reader is line-based.
/// </para>
/// </summary>
public static class DotEnvFile
{
    private const string ExportKeyword = "export";

    public static IReadOnlyDictionary<string, string> Parse(string? contents)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(contents))
        {
            return result;
        }

        foreach (var rawLine in contents.Split('\n'))
        {
            var line = rawLine.Trim('\r', ' ', '\t');
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            line = StripExportPrefix(line);

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            if (key.Length == 0)
            {
                continue;
            }

            result[key] = ParseValue(line[(separator + 1)..]);
        }

        return result;
    }

    /// <summary>Reads and parses the file; a missing file yields an empty map.</summary>
    public static IReadOnlyDictionary<string, string> Load(string path)
    {
        if (!File.Exists(path))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        try
        {
            return Parse(DefenseClaw.Core.IO.SharedFile.ReadAllText(path, DefenseClaw.Core.IO.ReadLimits.DotEnvBytes));
        }
        catch (IOException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (UnauthorizedAccessException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Removes a leading <c>export</c> followed by whitespace — a space <i>or a tab</i>, and any
    /// number of them, as both other readers accept. The keyword only counts as a prefix when
    /// something that could be a key follows it: <c>export=1</c> and <c>exports=1</c> are keys
    /// named <c>export</c> and <c>exports</c>, and <c>export = 1</c> is a key named <c>export</c>
    /// with a spaced <c>=</c>, not an empty key.
    /// </summary>
    private static string StripExportPrefix(string line)
    {
        if (line.Length <= ExportKeyword.Length ||
            !line.StartsWith(ExportKeyword, StringComparison.Ordinal) ||
            !IsBlank(line[ExportKeyword.Length]))
        {
            return line;
        }

        var rest = line[ExportKeyword.Length..].TrimStart(' ', '\t');
        return rest.Length == 0 || rest[0] == '=' ? line : rest;
    }

    /// <summary>
    /// Turns what follows the <c>=</c> into the value: quoted values keep everything inside the
    /// quotes; unquoted ones lose a trailing <c>#</c> comment. See the class remarks.
    /// </summary>
    private static string ParseValue(string afterEquals)
    {
        var value = afterEquals.TrimStart(' ', '\t');

        if (value.Length > 0 && value[0] is '"' or '\'')
        {
            var close = IndexOfClosingQuote(value);
            if (close > 0 && IsBlankOrComment(value.AsSpan(close + 1)))
            {
                return value[1..close];
            }

            // A quote that never closes cleanly (`KEY="a"b`, `KEY="abc`) is not a quoted value:
            // fall through and treat it like any other text, exactly as before.
        }

        return Unquote(StripInlineComment(afterEquals).Trim());
    }

    /// <summary>
    /// Index of the quote that closes the one at <c>value[0]</c>, or -1. Inside double quotes a
    /// backslash makes the next character inert, so <c>"a\"b"</c> closes at the last quote;
    /// single quotes have no escapes.
    /// </summary>
    private static int IndexOfClosingQuote(string value)
    {
        var quote = value[0];
        for (var i = 1; i < value.Length; i++)
        {
            if (quote == '"' && value[i] == '\\')
            {
                i++;
                continue;
            }

            if (value[i] == quote)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// True for nothing at all, or whitespace followed by <c>#</c> and anything: what may legally
    /// trail a closing quote.
    /// </summary>
    private static bool IsBlankOrComment(ReadOnlySpan<char> rest)
    {
        var i = 0;
        while (i < rest.Length && IsBlank(rest[i]))
        {
            i++;
        }

        // A '#' counts as a comment only after whitespace, same as for an unquoted value.
        return i == rest.Length || (i > 0 && rest[i] == '#');
    }

    /// <summary>
    /// Cuts an unquoted value at the first <c>#</c> that has whitespace immediately before it.
    /// <paramref name="afterEquals"/> is passed untrimmed on purpose: in <c>KEY=   # note</c> the
    /// whitespace after the <c>=</c> is what makes the <c>#</c> a comment rather than a value, and
    /// trimming first would lose it.
    /// </summary>
    private static string StripInlineComment(string afterEquals)
    {
        for (var i = 1; i < afterEquals.Length; i++)
        {
            if (afterEquals[i] == '#' && IsBlank(afterEquals[i - 1]))
            {
                return afterEquals[..i];
            }
        }

        return afterEquals;
    }

    private static bool IsBlank(char c) => c is ' ' or '\t';

    private static string Unquote(string value)
    {
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1];
        }

        return value;
    }
}
