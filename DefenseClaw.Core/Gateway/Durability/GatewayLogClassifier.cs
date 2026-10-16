using System.Text;
using System.Text.RegularExpressions;
using DefenseClaw.Core.Logs;
using DefenseClaw.Core.Text;

namespace DefenseClaw.Core.Gateway.Durability;

/// <summary>
/// Reads the end of <c>gateway.log</c> and says why the most recent start attempt failed (<see cref="GatewayExitReason"/>), or that it did not.
/// <para>
/// <b>Bounded.</b> At most <see cref="TailBytes"/> are read from the end, through a handle that shares with the gateway's own writes, and a
/// line longer than <see cref="MaxLineChars"/> keeps its head and its tail (the path a failure names is at the end). Only the last
/// <see cref="ClassifiedLines"/> lines are classified. Everything shown to a person is masked and cut (<see cref="DisplayRedaction"/>).
/// </para>
/// <para>
/// <b>The start attempt.</b> The 1.0 gateway prints <c>[sidecar-api] listening on</c> early in a start and, when a connector's setup fails, the
/// failure about a second later. So the latest start failed when a failure line follows the latest "listening" line (or there is a failure and
/// no "listening" in the window); it succeeded when the latest "listening" line is the last word. A log with neither says nothing.
/// </para>
/// </summary>
public static partial class GatewayLogClassifier
{
    /// <summary>The most bytes read from the end of the log.</summary>
    public const int TailBytes = 256 * 1024;

    /// <summary>The most lines kept from that window.</summary>
    public const int KeptLines = 2000;

    /// <summary>How many of the last lines are classified.</summary>
    public const int ClassifiedLines = 200;

    /// <summary>A longer line keeps this many characters of its head and the same of its tail.</summary>
    public const int MaxLineChars = 4096;

    private const int ShownChars = 300;

    private const int RegexTimeoutMilliseconds = 250;

    [GeneratedRegex(@"\[sidecar-api\]\s+listening on\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeoutMilliseconds)]
    private static partial Regex Listening();

    [GeneratedRegex(@"setup failed|exited with error|\bfatal\b|\bpanic:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeoutMilliseconds)]
    private static partial Regex Failure();

    [GeneratedRegex(@"GetFileAttributesEx\s+(?<path>.+?):\s*The system cannot find", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeoutMilliseconds)]
    private static partial Regex StatFailure();

    [GeneratedRegex(@"inspect selected .{0,40}executable", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeoutMilliseconds)]
    private static partial Regex InspectExecutable();

    [GeneratedRegex(@"cannot find the (?:path|file)|no such file|does not exist|not found", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeoutMilliseconds)]
    private static partial Regex Missing();

    [GeneratedRegex(@"\b(?<path>[A-Za-z]:\\[^\r\n]*?\.exe)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeoutMilliseconds)]
    private static partial Regex ExePath();

    [GeneratedRegex(@"unrecognized inode:\s*(?<path>.+?)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeoutMilliseconds)]
    private static partial Regex StuckWrite();

    [GeneratedRegex(@"connector\s+(?<name>[A-Za-z0-9_-]+)\s+setup failed", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeoutMilliseconds)]
    private static partial Regex SetupFailed();

    [GeneratedRegex(@"^\s*\d{4}-\d\d-\d\d[T ][\d:.]+(?:Z|[+-]\d\d:?\d\d)?\s*(?:\[[^\]]*\]\s*)?", RegexOptions.CultureInvariant, RegexTimeoutMilliseconds)]
    private static partial Regex LinePrefix();

    [GeneratedRegex(@"policy_mode=(?<mode>observe|action)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeoutMilliseconds)]
    private static partial Regex PolicyMode();

    /// <summary>
    /// The last lines of <paramref name="path"/> (at most <see cref="KeptLines"/>, from at most <see cref="TailBytes"/>), oldest first. An empty list for a
    /// file that is missing or unreadable: the log is evidence, never a requirement.
    /// </summary>
    public static IReadOnlyList<string> ReadTail(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = stream.Length;
            var start = Math.Max(0, length - TailBytes);
            _ = stream.Seek(start, SeekOrigin.Begin);

            var buffer = new byte[(int)(length - start)];
            var read = 0;
            while (read < buffer.Length)
            {
                var n = stream.Read(buffer, read, buffer.Length - read);
                if (n <= 0)
                {
                    break;
                }

                read += n;
            }

            var text = Encoding.UTF8.GetString(buffer, 0, read);
            var lines = text.Split('\n');

            // A window that starts mid-file starts mid-line: that line is not evidence of anything.
            var first = start > 0 ? 1 : 0;
            var kept = new List<string>(Math.Min(lines.Length, KeptLines));
            for (var i = Math.Max(first, lines.Length - KeptLines - 1); i < lines.Length; i++)
            {
                var line = lines[i].TrimEnd('\r');
                if (line.Length > 0)
                {
                    kept.Add(Clip(line));
                }
            }

            return kept;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>The start attempt's outcome read from <paramref name="lines"/> (oldest first), or null when the latest start reached "listening" and nothing failed after it.</summary>
    public static GatewayExitReason? Classify(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var from = Math.Max(0, lines.Count - ClassifiedLines);
        var lastListening = -1;
        var lastFailure = -1;
        for (var i = from; i < lines.Count; i++)
        {
            var line = Clip(lines[i] ?? string.Empty);
            if (IsMatch(Listening(), line))
            {
                lastListening = i;
            }
            else if (IsMatch(Failure(), line))
            {
                lastFailure = i;
            }
        }

        if (lastFailure < 0 || lastFailure < lastListening)
        {
            return null;
        }

        // Everything after the latest "listening" (all of the window when there is none) belongs to the failed attempt. The most specific cause wins;
        // among equals the latest line does.
        GatewayExitReason? best = null;
        var bestRank = 0;
        string? lastLine = null;
        for (var i = Math.Max(from, lastListening + 1); i < lines.Count; i++)
        {
            var line = Clip(lines[i] ?? string.Empty);
            if (!IsMatch(Failure(), line))
            {
                continue;
            }

            lastLine = line;
            var (reason, rank) = Describe(line);
            if (rank >= bestRank)
            {
                best = reason;
                bestRank = rank;
            }
        }

        return best ?? new GatewayExitReason.Other(Shown(lastLine ?? "The gateway exited."));
    }

    /// <summary><see cref="ReadTail"/> then <see cref="Classify"/>.</summary>
    public static GatewayExitReason? ClassifyFile(string path) => Classify(ReadTail(path));

    /// <summary>
    /// The policy mode (<c>observe</c> or <c>action</c>) the gateway last logged for <paramref name="connector"/>
    /// (<c>direct-upstream mode: codex policy_mode=action enforcement=true</c>), or null when the window has none.
    /// </summary>
    public static string? LatestPolicyMode(IReadOnlyList<string> lines, string connector)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentException.ThrowIfNullOrEmpty(connector);

        for (var i = lines.Count - 1; i >= 0; i--)
        {
            var line = Clip(lines[i] ?? string.Empty);
            if (line.Contains(connector, StringComparison.OrdinalIgnoreCase) && Run(PolicyMode(), line) is { Success: true } mode)
            {
                return mode.Groups["mode"].Value.ToLowerInvariant();
            }
        }

        return null;
    }

    /// <summary>How a person writes a connector name ("codex" becomes "Codex").</summary>
    public static string FriendlyConnector(string connector) => connector.Trim().ToLowerInvariant() switch
    {
        "codex" => "Codex",
        "claudecode" => "Claude Code",
        "unknown" or "" => "A connector",
        { Length: > 0 } other => char.ToUpperInvariant(other[0]) + other[1..],
        _ => "A connector",
    };

    private static (GatewayExitReason Reason, int Rank) Describe(string line)
    {
        var connector = Run(SetupFailed(), line) is { Success: true } named ? named.Groups["name"].Value.ToLowerInvariant() : string.Empty;

        // Incident A: the pinned executable is gone.
        if (Run(StatFailure(), line) is { Success: true } stat)
        {
            return (new GatewayExitReason.StaleAgentExecutable(ConnectorOr(connector, line), Shown(stat.Groups["path"].Value)), 3);
        }

        if (IsMatch(InspectExecutable(), line) && IsMatch(Missing(), line))
        {
            var exe = Run(ExePath(), line) is { Success: true } found ? Shown(found.Groups["path"].Value) : "(path not logged)";
            return (new GatewayExitReason.StaleAgentExecutable(ConnectorOr(connector, line), exe), 3);
        }

        // Incident B: a guarded write that never finished.
        if (Run(StuckWrite(), line) is { Success: true } stuck)
        {
            var path = Shown(stuck.Groups["path"].Value);
            return (new GatewayExitReason.StuckConfigWrite(ConnectorFromPath(path) ?? ConnectorOr(connector, line), path), 3);
        }

        if (connector.Length > 0)
        {
            var at = line.LastIndexOf("setup failed:", StringComparison.OrdinalIgnoreCase);
            var message = at >= 0 ? line[(at + "setup failed:".Length)..].Trim() : line;
            return (new GatewayExitReason.ConnectorSetupFailed(connector, Shown(message)), 2);
        }

        return (new GatewayExitReason.Other(Shown(LinePrefix().Replace(line, string.Empty))), 1);
    }

    private static string ConnectorOr(string connector, string line) =>
        connector.Length > 0 ? connector : line.Contains("codex", StringComparison.OrdinalIgnoreCase) ? "codex" : "unknown";

    private static string? ConnectorFromPath(string path)
    {
        if (path.Contains(@"\.codex\", StringComparison.OrdinalIgnoreCase) || path.Contains("/.codex/", StringComparison.OrdinalIgnoreCase))
        {
            return "codex";
        }

        return path.Contains(@"\.claude\", StringComparison.OrdinalIgnoreCase) || path.Contains("/.claude/", StringComparison.OrdinalIgnoreCase)
            ? "claudecode"
            : null;
    }

    private static string Shown(string text) => DisplayRedaction.Text(DisplayNames.Visible(text.Trim()), ShownChars);

    private static string Clip(string line) =>
        line.Length <= MaxLineChars
            ? line
            : string.Concat(line.AsSpan(0, MaxLineChars / 2), " … ", line.AsSpan(line.Length - MaxLineChars / 2));

    private static bool IsMatch(Regex regex, string line) => Run(regex, line).Success;

    // A pattern that takes too long on a hostile line is "no match", not a fault in the monitor.
    private static Match Run(Regex regex, string line)
    {
        try
        {
            return regex.Match(line);
        }
        catch (RegexMatchTimeoutException)
        {
            return System.Text.RegularExpressions.Match.Empty;
        }
    }
}
