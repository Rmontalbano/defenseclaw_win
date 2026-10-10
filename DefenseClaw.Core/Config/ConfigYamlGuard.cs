using System.Globalization;
using DefenseClaw.Core.IO;

namespace DefenseClaw.Core.Config;

/// <summary>
/// Refuses a <c>config.yaml</c> that would make YamlDotNet hang before it is handed over (CUST-250): the parser is quadratic on nested flow
/// collections (<c>[[[[...]]]]</c>, <c>{a: {a: {...}}}</c>) - a 200 KB file took 53 s, and startup waits on the config read. A real file is a few KB
/// with no flow nesting deeper than two or three, so the limits (<see cref="ReadLimits.ConfigYamlBytes"/>, <see cref="ReadLimits.ConfigYamlFlowDepth"/>)
/// never touch one.
/// <para>
/// The depth scan is linear and deliberately forgiving about YAML it does not understand: it skips comments and quoted scalars (where a
/// bracket is text), and treats everything else that is <c>[</c> or <c>{</c> as an opener. A false alarm needs 32 nested brackets in one file.
/// </para>
/// </summary>
public static class ConfigYamlGuard
{
    /// <summary>Why this text must not be parsed, or null when it may be.</summary>
    public static string? Refusal(string yaml, long maxBytes = ReadLimits.ConfigYamlBytes, int maxDepth = ReadLimits.ConfigYamlFlowDepth)
    {
        ArgumentNullException.ThrowIfNull(yaml);

        // Characters, not bytes: UTF-8 is never shorter than that, so a text over the limit in characters is over it in bytes too, and one under
        // it in characters but over in bytes was already refused by the file read.
        if (yaml.Length > maxBytes)
        {
            return string.Create(CultureInfo.InvariantCulture, $"config.yaml is {ReadLimits.Describe(yaml.Length)}, over the {ReadLimits.Describe(maxBytes)} limit this app parses. Edit it down by hand.");
        }

        var depth = FlowDepth(yaml, stopAbove: maxDepth);
        return depth > maxDepth
            ? string.Create(CultureInfo.InvariantCulture, $"config.yaml nests flow collections ([ or {{) more than {maxDepth} deep, which this app does not parse. Edit it by hand.")
            : null;
    }

    /// <summary>The deepest <c>[</c> / <c>{</c> nesting in the text, counting up to <paramref name="stopAbove"/> + 1 and then stopping.</summary>
    public static int FlowDepth(string yaml, int stopAbove = int.MaxValue - 1)
    {
        ArgumentNullException.ThrowIfNull(yaml);

        var depth = 0;
        var deepest = 0;

        // The last significant character on this line: a quote only opens a quoted scalar where a value can start, so an apostrophe inside plain
        // text (don't) is not taken for one.
        var previous = '\n';
        for (var i = 0; i < yaml.Length; i++)
        {
            var c = yaml[i];
            switch (c)
            {
                case '\n':
                    previous = '\n';
                    break;
                case ' ':
                case '\t':
                case '\r':
                    break;
                case '#' when i == 0 || yaml[i - 1] is ' ' or '\t' or '\n' or '\r':
                    while (i + 1 < yaml.Length && yaml[i + 1] != '\n')
                    {
                        i++;
                    }

                    break;
                case '"' or '\'' when previous is '\n' or ':' or '-' or ',' or '[' or '{' or '?':
                    i = SkipQuoted(yaml, i, c);
                    previous = c;
                    break;
                case '[' or '{':
                    depth++;
                    if (depth > deepest)
                    {
                        deepest = depth;
                        if (deepest > stopAbove)
                        {
                            return deepest;
                        }
                    }

                    previous = c;
                    break;
                case ']' or '}':
                    depth = Math.Max(0, depth - 1);
                    previous = c;
                    break;
                default:
                    previous = c;
                    break;
            }
        }

        return deepest;
    }

    /// <summary>The index of the closing quote (or the last character when it never closes).</summary>
    private static int SkipQuoted(string text, int open, char quote)
    {
        for (var i = open + 1; i < text.Length; i++)
        {
            if (quote == '"' && text[i] == '\\')
            {
                i++;
            }
            else if (text[i] == quote)
            {
                if (quote == '\'' && i + 1 < text.Length && text[i + 1] == '\'')
                {
                    i++;
                }
                else
                {
                    return i;
                }
            }
        }

        return text.Length - 1;
    }
}
