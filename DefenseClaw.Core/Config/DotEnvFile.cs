namespace DefenseClaw.Core.Config;

/// <summary>
/// Minimal <c>KEY=VALUE</c> reader matching what the Python CLI accepts in
/// <c>~/.defenseclaw/.env</c>: blank lines and <c>#</c> comments skipped, an optional
/// <c>export</c> prefix tolerated, and surrounding single or double quotes stripped.
/// </summary>
public static class DotEnvFile
{
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

            if (line.StartsWith("export ", StringComparison.Ordinal))
            {
                line = line["export ".Length..].TrimStart();
            }

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

            result[key] = Unquote(line[(separator + 1)..].Trim());
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
            return Parse(File.ReadAllText(path));
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
