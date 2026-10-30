using System.Text.Json;
using DefenseClaw.Core.AiRuntime;

namespace DefenseClaw.Core.Gateway;

/// <summary>One editor and agent pair the ACP guard is bound to, as <c>defenseclaw acp status --json</c> reports it at 1.0.5.</summary>
/// <param name="Client">The editor (<c>zed</c>, <c>jetbrains</c>).</param>
/// <param name="Agent">The agent (<c>kiro</c>, ...).</param>
/// <param name="Healthy">True only when the editor entry starts the guard with this pair's contract lock and the lock's agent (<c>_verify_binding</c> found no problem).</param>
/// <param name="Problems">What <c>_verify_binding</c> found, in its words: the entries that start the guard with the lock, a changed entry, a stale lock, a missing file. Empty when healthy.</param>
/// <param name="Profile">The policy profile the pair resolves to.</param>
/// <param name="Mode"><c>observe</c> or <c>action</c>, or empty when the CLI did not say.</param>
/// <param name="ProfileInherited">True when the pair has no profile of its own and takes the default (<c>profile_source: inherited</c>).</param>
public sealed record AcpGuardBinding(
    string Client,
    string Agent,
    bool Healthy,
    IReadOnlyList<string> Problems,
    string Profile,
    string Mode,
    bool ProfileInherited)
{
    /// <summary><c>zed/kiro</c>.</summary>
    public string Pair => Client + "/" + Agent;
}

/// <summary>The ACP guard's posture: whether it is on and, for every bound pair, whether it is healthy.</summary>
/// <param name="Enabled"><c>acp.enabled</c>.</param>
/// <param name="Bindings">Every bound pair, in the order the CLI sorts them.</param>
public sealed record AcpGuardStatus(bool Enabled, IReadOnlyList<AcpGuardBinding> Bindings)
{
    /// <summary>True when at least one pair needs attention.</summary>
    public bool NeedsAttention => Bindings.Any(static b => !b.Healthy);
}

/// <summary>
/// Reads <c>defenseclaw acp status --json</c> (CUST-401). At 1.0.5 <c>_status</c> (<c>cli/defenseclaw/commands/cmd_acp.py:1814</c>) prints
/// <c>{enabled, mode, default_profile, clients, configured_clients, configured_agents, bindings}</c> with sorted keys, where <c>bindings</c> maps
/// <c>"client/agent"</c> to <c>{healthy, problems, profile, mode, profile_source}</c>; <c>healthy</c> is <c>not _verify_binding(...)</c>, which 1.0.5 judges
/// against the lock and the agent the guard starts. The command only reads (no gateway call, no file written), and <c>acp verify</c> has no JSON form: it
/// reports the same problems and exits non-zero, so the status document is the one this reads.
/// <para>
/// Everything is cleaned and cut like every other CLI string; a pair name that is not a stable id is dropped, so a name can never reach a command line.
/// </para>
/// </summary>
public static class AcpGuardStatusReader
{
    /// <summary>The command, without the executable: it only reads, so the app runs it as a background read.</summary>
    public static IReadOnlyList<string> Argv { get; } = new[] { "acp", "status", "--json" };

    private const int MaxBindings = 64;
    private const int MaxProblems = 8;
    private const int MaxProblemLength = 480;

    /// <summary>
    /// Turns a finished run into a read. A CLI without the command ("No such command") is <see cref="AcpReadStatus.Unsupported"/>, never a fault; any other
    /// failure, or output that is not the document, is <see cref="AcpReadStatus.Unavailable"/> with a sentence.
    /// </summary>
    public static AcpRead<AcpGuardStatus> FromRun(int exitCode, string standardOutput, string standardError)
    {
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(standardError);

        if (exitCode != 0)
        {
            return standardError.Contains("No such command", StringComparison.OrdinalIgnoreCase) || standardOutput.Contains("No such command", StringComparison.OrdinalIgnoreCase)
                ? AcpRead<AcpGuardStatus>.Unsupported
                : AcpRead<AcpGuardStatus>.Unavailable("The command failed: " + FirstLine(standardError.Length > 0 ? standardError : standardOutput) + ".");
        }

        return Parse(standardOutput);
    }

    /// <summary>Reads the document out of the command's standard output (any line before the first <c>{</c>, a warning say, is not part of it).</summary>
    public static AcpRead<AcpGuardStatus> Parse(string standardOutput)
    {
        ArgumentNullException.ThrowIfNull(standardOutput);

        var start = standardOutput.IndexOf('{', StringComparison.Ordinal);
        var end = standardOutput.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return AcpRead<AcpGuardStatus>.Unavailable("The command did not print a status document.");
        }

        try
        {
            using var document = JsonDocument.Parse(standardOutput.Substring(start, end - start + 1));
            return Parse(document.RootElement) is { } status
                ? new AcpRead<AcpGuardStatus>(AcpReadStatus.Ok, status, string.Empty)
                : AcpRead<AcpGuardStatus>.Unavailable("The status document is not one this app can read.");
        }
        catch (JsonException)
        {
            return AcpRead<AcpGuardStatus>.Unavailable("The command did not print a status document.");
        }
    }

    /// <summary>Reads a status document; null when it is not one (no <c>enabled</c> flag).</summary>
    public static AcpGuardStatus? Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("enabled", out var enabled) ||
            enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return null;
        }

        var bindings = new List<AcpGuardBinding>();
        if (root.TryGetProperty("bindings", out var map) && map.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in map.EnumerateObject().OrderBy(static p => p.Name, StringComparer.Ordinal))
            {
                if (bindings.Count >= MaxBindings || entry.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var slash = entry.Name.IndexOf('/', StringComparison.Ordinal);
                if (slash <= 0 || !AcpGuardReader.IsStableName(entry.Name[..slash]) || !AcpGuardReader.IsStableName(entry.Name[(slash + 1)..]))
                {
                    continue;
                }

                var problems = Problems(entry.Value);

                // A binding is healthy when the CLI says so and names no problem: a "healthy" beside a problem is not a verdict to show as one.
                var healthy = entry.Value.TryGetProperty("healthy", out var flag) && flag.ValueKind == JsonValueKind.True && problems.Count == 0;
                var mode = Text(entry.Value, "mode", 16);
                bindings.Add(new AcpGuardBinding(
                    entry.Name[..slash],
                    entry.Name[(slash + 1)..],
                    healthy,
                    problems,
                    Text(entry.Value, "profile", 80),
                    mode is "observe" or "action" ? mode : string.Empty,
                    string.Equals(Text(entry.Value, "profile_source", 16), "inherited", StringComparison.Ordinal)));
            }
        }

        return new AcpGuardStatus(enabled.ValueKind == JsonValueKind.True, bindings);
    }

    private static IReadOnlyList<string> Problems(JsonElement binding) =>
        binding.TryGetProperty("problems", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray()
                .Where(static i => i.ValueKind == JsonValueKind.String)
                .Select(static i => AiRuntimeJson.Prose(i.GetString(), MaxProblemLength))
                .Where(static p => p.Length > 0)
                .Take(MaxProblems)
                .ToArray()
            : Array.Empty<string>();

    private static string Text(JsonElement element, string name, int limit) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? AiRuntimeJson.Prose(value.GetString(), limit)
            : string.Empty;

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault(static l => l.Length > 0) ?? "no message";
        return AiRuntimeJson.Prose(line, 160).TrimEnd('.');
    }
}
