using System.Text;
using DefenseClaw.Core.IO;

namespace DefenseClaw.Core.Codex;

/// <summary>What one read of Codex's <c>config.toml</c> said about its hooks switch.</summary>
/// <param name="ConfigPath">The file read.</param>
/// <param name="Exists">False when the file is absent: Codex then runs with its defaults, hooks on.</param>
/// <param name="DisabledKey">
/// <c>hooks</c> or <c>codex_hooks</c> when the <c>[features]</c> table sets it to <c>false</c>: Codex then runs no hook, DefenseClaw's included. Null when
/// neither is false, and when the file could not be read or does not parse (what Codex does with such a file is not this reader's to guess).
/// </param>
/// <param name="ReadError">Why the read failed, when it did; null for a clean read and for an absent file.</param>
public sealed record CodexHooksSwitch(string ConfigPath, bool Exists, string? DisabledKey, string? ReadError)
{
    /// <summary>True when config.toml turns Codex's hooks off.</summary>
    public bool HooksOff => DisabledKey is not null;
}

/// <summary>
/// Reads the one switch in Codex's own <c>config.toml</c> that stops every Codex hook from running: <c>[features] hooks = false</c> (or the older
/// <c>codex_hooks = false</c>). DefenseClaw 1.0.5's <c>status</c> shows such a connector DEGRADED (<c>agent_hook_switch_problems</c> in hook_integrity.py),
/// and the app says so on the Overview.
/// <para>
/// <b>Read-only and small.</b> The app never writes this file (Codex and DefenseClaw's setup already do). At most <see cref="MaxBytes"/> are read, the
/// runtime's own limit for an agent config (<c>_CONFIG_LIMIT</c>). This is not a TOML parser: it follows tables, keys, strings, comments and values that
/// span lines just far enough to find that one key, and a line it cannot place makes the whole file "does not parse", so no claim is made from it.
/// </para>
/// </summary>
public sealed class CodexConfigReader
{
    /// <summary>The variable Codex reads for its home folder; <c>%USERPROFILE%\.codex</c> without it.</summary>
    public const string HomeVariableName = "CODEX_HOME";

    /// <summary>The most bytes read (2 MiB, hook_integrity.py <c>_CONFIG_LIMIT</c>).</summary>
    public const long MaxBytes = 2 * 1024 * 1024;

    /// <param name="configPath">Defaults to <see cref="DefaultConfigPath"/>. Injectable so tests never touch the operator's own Codex.</param>
    public CodexConfigReader(string? configPath = null)
    {
        ConfigPath = configPath ?? DefaultConfigPath();
    }

    public string ConfigPath { get; }

    /// <summary><c>%CODEX_HOME%\config.toml</c> when that variable is set (a blank value is "not set"), else <c>%USERPROFILE%\.codex\config.toml</c>.</summary>
    public static string DefaultConfigPath(Func<string, string?>? getEnvironmentVariable = null)
    {
        var configured = (getEnvironmentVariable ?? Environment.GetEnvironmentVariable)(HomeVariableName)?.Trim();
        return configured is { Length: > 0 }
            ? Path.Combine(configured, "config.toml")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "config.toml");
    }

    /// <summary>Reads the file once. Never throws for a missing, locked, oversized or malformed file.</summary>
    public CodexHooksSwitch Read()
    {
        if (!File.Exists(ConfigPath))
        {
            return new CodexHooksSwitch(ConfigPath, false, null, null);
        }

        try
        {
            return new CodexHooksSwitch(ConfigPath, true, HooksOffKey(SharedFile.ReadAllText(ConfigPath, MaxBytes)), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new CodexHooksSwitch(ConfigPath, File.Exists(ConfigPath), null, ex.Message);
        }
    }

    /// <summary>
    /// <c>hooks</c> or <c>codex_hooks</c> when <paramref name="toml"/> sets <c>features.hooks</c> or <c>features.codex_hooks</c> to the boolean <c>false</c>
    /// (as a <c>[features]</c> table, a dotted key or an inline table); null otherwise, and null for text this reader cannot follow.
    /// </summary>
    public static string? HooksOffKey(string toml)
    {
        ArgumentNullException.ThrowIfNull(toml);

        var lines = toml.TrimStart('﻿').Split('\n');
        var table = string.Empty;
        string? found = null;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = StripComment(lines[i].TrimEnd('\r')).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("[[", StringComparison.Ordinal))
            {
                // An array of tables is never [features]; its keys belong to it.
                if (!line.EndsWith("]]", StringComparison.Ordinal) || Key(line[2..^2]) is null)
                {
                    return null;
                }

                table = "\0array";
                continue;
            }

            if (line[0] == '[')
            {
                if (!line.EndsWith(']') || Key(line[1..^1]) is not { } name)
                {
                    return null;
                }

                table = name;
                continue;
            }

            var equals = IndexOutsideQuotes(line, '=');
            if (equals <= 0 || Key(line[..equals]) is not { } key)
            {
                return null;
            }

            var value = line[(equals + 1)..].Trim();
            if (value.Length == 0)
            {
                return null;
            }

            // A value that goes on below (a multi-line string, an array or inline table left open) takes its lines with it.
            if (!SkipContinuation(lines, ref i, value))
            {
                return null;
            }

            var full = table.Length == 0 ? key : table + "." + key;
            if (full is "features.hooks" or "features.codex_hooks" && value == "false")
            {
                found = full["features.".Length..];
            }
            else if (full == "features" && value.StartsWith('{'))
            {
                found = InlineFalse(value) ?? found;
            }
        }

        return found;
    }

    /// <summary>A key or table name with its parts trimmed and unquoted (<c>"features" . hooks</c> is <c>features.hooks</c>); null when a part is not a key.</summary>
    private static string? Key(string text)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        char quote = '\0';
        foreach (var c in text)
        {
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }

                current.Append(c);
            }
            else if (c is '"' or '\'')
            {
                quote = c;
                current.Append(c);
            }
            else if (c == '.')
            {
                parts.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        if (quote != '\0')
        {
            return null;
        }

        parts.Add(current.ToString());
        for (var i = 0; i < parts.Count; i++)
        {
            var part = parts[i].Trim();
            if (part.Length >= 2 && (part[0] == '"' || part[0] == '\'') && part[^1] == part[0])
            {
                part = part[1..^1];
            }
            else if (part.Length == 0 || !part.All(static c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
            {
                return null;
            }

            parts[i] = part;
        }

        return string.Join('.', parts);
    }

    /// <summary>The line up to a <c>#</c> that is not inside a string.</summary>
    private static string StripComment(string line)
    {
        var at = IndexOutsideQuotes(line, '#');
        return at < 0 ? line : line[..at];
    }

    /// <summary>The first <paramref name="target"/> outside a basic (<c>"</c>, with escapes) or literal (<c>'</c>) string, or -1.</summary>
    private static int IndexOutsideQuotes(string line, char target)
    {
        char quote = '\0';
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quote == '"' && c == '\\')
            {
                i++;
            }
            else if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == target)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Moves <paramref name="index"/> past the lines a value continues on: a multi-line string until its closing delimiter, an array or inline table until
    /// its brackets close. False when the file ends first.
    /// </summary>
    private static bool SkipContinuation(string[] lines, ref int index, string value)
    {
        foreach (var delimiter in new[] { "\"\"\"", "'''" })
        {
            if (value.StartsWith(delimiter, StringComparison.Ordinal))
            {
                if (value.IndexOf(delimiter, delimiter.Length, StringComparison.Ordinal) >= 0)
                {
                    return true;
                }

                while (++index < lines.Length)
                {
                    if (lines[index].Contains(delimiter, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        var depth = Depth(value);
        while (depth > 0)
        {
            if (++index >= lines.Length)
            {
                return false;
            }

            depth += Depth(StripComment(lines[index].TrimEnd('\r')));
        }

        return depth == 0;
    }

    /// <summary>Opening minus closing brackets and braces outside strings.</summary>
    private static int Depth(string text)
    {
        var depth = 0;
        char quote = '\0';
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quote == '"' && c == '\\')
            {
                i++;
            }
            else if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c is '[' or '{')
            {
                depth++;
            }
            else if (c is ']' or '}')
            {
                depth--;
            }
        }

        return depth;
    }

    /// <summary><c>hooks</c> or <c>codex_hooks</c> when the one-line inline table <c>{ hooks = false, ... }</c> sets it false; else null.</summary>
    private static string? InlineFalse(string value)
    {
        if (!value.EndsWith('}'))
        {
            return null;
        }

        string? found = null;
        foreach (var pair in value[1..^1].Split(','))
        {
            var equals = IndexOutsideQuotes(pair, '=');
            if (equals > 0 && Key(pair[..equals]) is "hooks" or "codex_hooks" && pair[(equals + 1)..].Trim() == "false")
            {
                found = Key(pair[..equals]);
            }
        }

        return found;
    }
}
