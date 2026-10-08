using System.Text.Json;
using System.Text.RegularExpressions;

namespace DefenseClaw.Core.Cli;

/// <summary>
/// A catalog listing (<c>mcp list --json</c>, <c>plugin list --json</c>, ...) that printed every row it could read, said on
/// stderr which discovery source it could not, and exited non-zero: a <i>partial</i> read. The rows are real; the list is not
/// complete, so nothing may be changed on the strength of it.
/// </summary>
/// <param name="Stdout">The complete standard output, untouched: valid JSON, never mixed with the diagnostic.</param>
/// <param name="Diagnostics">The known source diagnostics from standard error, one entry each, in the order printed.</param>
public sealed record CatalogPartialRead(string Stdout, IReadOnlyList<string> Diagnostics);

/// <summary>
/// Tells a partial catalog read from a failed one. The rule is deliberately narrow: <b>all</b> of (the process ran to an exit,
/// the exit code is not 0, the output was not truncated, standard output is one complete JSON array or object, and standard
/// error carries at least one <i>known</i> source diagnostic) must hold. Anything else is a failure and stays one: garbage
/// output with exit 1, a timeout, a traceback, valid JSON with a stderr line this app does not recognise.
/// <para>
/// <b>Which CLI says this.</b> DefenseClaw 0.8.10 never does: its list commands treat an unreadable or malformed source as
/// "no entries" and exit 0 with nothing on stderr (<c>connector_paths.py</c> swallows <c>OSError</c>). Later CLIs name the source
/// instead and exit 1 after printing the readable rows, which is the case this type exists for. The known wordings, taken from
/// that source:
/// </para>
/// <list type="bullet">
/// <item><c>error: MCP discovery source is unreadable|malformed for connector='X': PATH</c> (<c>mcp list</c>)</item>
/// <item><c>Plugin discovery source [X]: PATH — unsafe/unreadable|malformed|unsupported; entries=N</c> and
/// <c>error: plugin discovery could not safely read every existing registry source</c> (<c>plugin list</c>)</item>
/// <item>the same plugin failure as JSON on stderr: <c>{"error": "plugin_discovery_failed", "discovery": [{...}]}</c></item>
/// </list>
/// A CLI that never prints these (0.8.10) is therefore unaffected: its non-zero exits are all failures, exactly as before.
/// </summary>
public static partial class CatalogPartialReads
{
    /// <summary>How many diagnostic lines are kept; a runaway stderr must not turn a banner into a page.</summary>
    public const int MaxDiagnostics = 12;

    [GeneratedRegex(@"^error:\s+MCP discovery source is (?:unreadable|malformed)\s+for connector=\S+:\s+\S.*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex McpSourcePattern();

    [GeneratedRegex(@"^Plugin discovery source \[[^\]]+\]:\s+\S.*\s[—-]+\s+(?:unsafe/unreadable|unreadable|malformed|unsupported)\b.*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PluginSourcePattern();

    [GeneratedRegex(@"^error:\s+plugin discovery could not safely read every existing registry source\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PluginSummaryPattern();

    /// <summary>
    /// True (with the stdout and the diagnostics) when <paramref name="invocation"/> is a partial read as described on
    /// <see cref="CatalogPartialReads"/>.
    /// </summary>
    public static bool TryClassify(CliInvocation invocation, out CatalogPartialRead? partial)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        partial = null;

        if (invocation.FailureReason is not null || invocation.ExitCode is null or 0 || invocation.IsOutputTruncated)
        {
            return false;
        }

        var lines = invocation.OutputLines;
        var stdout = string.Concat(lines.Where(l => l.Stream == CliStream.StandardOutput).Select(l => l.Text + "\n"));
        if (!IsCompleteJsonContainer(stdout))
        {
            return false;
        }

        var stderr = lines.Where(l => l.Stream == CliStream.StandardError).Select(l => l.Text).ToList();
        var diagnostics = KnownDiagnostics(stderr);
        if (diagnostics.Count == 0)
        {
            return false;
        }

        partial = new CatalogPartialRead(stdout, diagnostics);
        return true;
    }

    private static bool IsCompleteJsonContainer(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.ValueKind is JsonValueKind.Array or JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The recognised diagnostics among standard error's lines; empty when there is none.</summary>
    internal static List<string> KnownDiagnostics(IReadOnlyList<string> stderrLines)
    {
        var found = new List<string>();

        void Add(string text)
        {
            if (found.Count < MaxDiagnostics && !found.Contains(text, StringComparer.Ordinal))
            {
                found.Add(text);
            }
        }

        foreach (var raw in stderrLines)
        {
            var line = raw.Trim();
            if (McpSourcePattern().IsMatch(line) || PluginSourcePattern().IsMatch(line) || PluginSummaryPattern().IsMatch(line))
            {
                Add(line);
            }
        }

        // The JSON form is printed over several lines; it is only meaningful whole.
        var joined = string.Join('\n', stderrLines);
        if (joined.Contains("plugin_discovery_failed", StringComparison.Ordinal))
        {
            foreach (var line in DescribePluginJson(joined))
            {
                Add(line);
            }
        }

        return found;
    }

    private static IEnumerable<string> DescribePluginJson(string text)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            yield break;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("error", out var error)
                || error.ValueKind != JsonValueKind.String
                || error.GetString() != "plugin_discovery_failed")
            {
                yield break;
            }

            yield return "error: plugin discovery could not safely read every existing registry source";
            if (root.TryGetProperty("discovery", out var probes) && probes.ValueKind == JsonValueKind.Array)
            {
                foreach (var probe in probes.EnumerateArray())
                {
                    if (probe.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var source = Text(probe, "source");
                    var state = Text(probe, "state");
                    if (source.Length > 0 && state.Length > 0)
                    {
                        var connector = Text(probe, "connector");
                        yield return $"Plugin discovery source [{(connector.Length > 0 ? connector : "?")}]: {source} — {state}";
                    }
                }
            }
        }
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
}
