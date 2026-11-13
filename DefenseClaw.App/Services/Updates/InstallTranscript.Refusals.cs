using System.Text;
using System.Text.RegularExpressions;
using DefenseClaw.Core.Gateway.Durability;

namespace DefenseClaw.App.Services.Updates;

/// <summary>
/// The refusals install.ps1 1.0.6 adds to the one of CUST-399 (CUST-424), and the plain "x ...; nothing was changed" of any release. Exit 1 alone reads as
/// "did not finish, restored", so a refusal is keyed on the installer's own sentence in its last <c>x</c> line, never on the exit code or a marker.
/// <para>
/// The reason of an OpenCode refusal is a CLI line above that <c>x</c> line (<c>ux.err</c>, cli/defenseclaw/ux.py:600 at tag 1.0.6): marked <c>✗</c>, or <c>X</c>
/// on the pipe install.ps1 reads it from (ux.py:192-195), and possibly garbled by the console code page. So the marker is never required. install.ps1
/// prints the rollback's line through <c>Out-String</c> (scripts/install.ps1:503,649), which may cut it at the console's width, even inside a word, with
/// nothing to mark the cut; so a line and the ones after it are joined as they are, and the sentence is matched with the spaces optional. What the card
/// shows is the sentence's own wording rebuilt from what varies, so a cut never reaches it. None of this has been seen in a real 1.0.6 transcript.
/// </para>
/// </summary>
public static partial class InstallTranscript
{
    /// <summary>The step of the upgrade refusal, word for word (cli/defenseclaw/migrations.py:3339-3340 at tag 1.0.6).</summary>
    internal const string OpenCodeV1NextStep =
        "Install OpenCode V2 (" + OpenCodeInstallEvidence.InstallV2Command + "), then upgrade again. To upgrade without OpenCode instead, run 'defenseclaw setup remove opencode' first.";

    /// <summary>The step of the rollback refusal, word for word (migrations.py:3429-3441).</summary>
    internal const string OpenCodeV2NextStep =
        "The rollback restores that release's own configuration, so removing OpenCode here does not help: to roll back, put OpenCode V1 back first (" +
        OpenCodeInstallEvidence.PutV1BackCommand + "), then run 'defenseclaw agent discover --refresh' so DefenseClaw sees it, or stay on this release.";

    /// <summary>The next step when the rollback refusal's CLI line could not be read.</summary>
    internal const string OpenCodeV2FallbackStep = "Releases before 1.0.6 cannot guard OpenCode V2. The lines above it in the install log say what to put back first.";

    /// <summary>The next step of a refusal that names none of its own.</summary>
    internal const string GenericNextStep = "Do what the installer's line says, then run it again. The install log has the lines before it.";

    private const string ServiceStopClause = " (OpenCode V2 keeps a background server after its window closes: stop it with 'opencode service stop')";
    private const string CloseAgents = "Close OpenCode and Amp completely";
    private const string CloseAgentsRest = " before installing or upgrading DefenseClaw";
    private const string RunAgain = ", then run this installer again";

    private const string V1Middle = " guards OpenCode V2 only; nothing was changed, and the DefenseClaw you have keeps guarding it.";
    private const string V2Middle = "): its gateway would not start, and OpenCode would run unguarded; nothing was changed.";

    /// <summary>How far above the <c>x</c> line its CLI line can be, and how many lines one wrapped line can take.</summary>
    private const int CliLineReach = 40;
    private const int MaxWrappedLines = 16;

    /// <summary>A line shorter than this was not cut at the console's width.</summary>
    private const int MinWrapWidth = 60;

    // Its own field, declared before the patterns: the order of static initializers across the parts of a partial class is not defined.
    private static readonly TimeSpan RefusalTimeout = TimeSpan.FromSeconds(1);

    private static readonly Regex MigrateRefused = new(
        "^" + Flex("Your configuration cannot be migrated to ") + @"\S+?" + Flex("; nothing was changed (see above)") + "$",
        RegexOptions.CultureInvariant,
        RefusalTimeout);

    private static readonly Regex GuardedRollbackRefused = new("^" + Flex("Rollback cancelled; nothing was changed (see above)") + "$", RegexOptions.CultureInvariant, RefusalTimeout);

    // install.ps1:1174 at 1.0.6, and :1143 at 1.0.5 without the clause about the background server.
    private static readonly Regex AgentsRunningStop = new(
        "^" + Flex(CloseAgents + CloseAgentsRest) + "(?<clause>" + Flex(ServiceStopClause) + ")?" + Flex(RunAgain + "; gateway still running: ") + "(?<list>.*)$",
        RegexOptions.CultureInvariant,
        RefusalTimeout);

    // "...; nothing was changed", "... (see above)", ". Nothing was changed." and "; nothing was changed. Run the installer again" (install.ps1:3244): the sentence ends the line.
    private static readonly Regex EndsNothingChanged = new(
        @"(?:;|\.)\s*" + Flex("nothing was changed").Replace("nothing", "[Nn]othing", StringComparison.Ordinal) + @"(?:\s*" + Flex("(see above)") + @")?(?:\.\s*" + Flex("Run the installer again") + @")?\.?$",
        RegexOptions.CultureInvariant,
        RefusalTimeout);

    // install.ps1@1.0.9:1914-1920: "The uv cache folder <path> is N characters long. uv builds one package (win-unicode-console) from source in it and needs 151 more, ..."
    private static readonly Regex UvCacheLine = new(
        "^" + Flex("The uv cache folder ") + ".+?" + Flex(" is ") + @"\d+" + Flex(" characters long. uv builds one package"),
        RegexOptions.CultureInvariant,
        RefusalTimeout);

    private static readonly Regex OpenCodeV1Line = new(
        Flex("OpenCode ") + "(?<raw>.{0,120}?)" + Flex(" at ") + "(?<exe>.{1,1024}?)" + Flex(" is OpenCode V1, and DefenseClaw ") + @"(?<to>\d[\d.]{0,20}?)" + Flex(V1Middle + " " + OpenCodeV1NextStep),
        RegexOptions.CultureInvariant,
        RefusalTimeout);

    private static readonly Regex OpenCodeV2Line = new(
        Flex("DefenseClaw ") + @"(?<to>\d[\d.]{0,20}?)" + Flex(" guards OpenCode V1 only and the OpenCode here is V2 (") + "(?<raw>.{0,120}?)" + Flex(V2Middle + " " + OpenCodeV2NextStep),
        RegexOptions.CultureInvariant,
        RefusalTimeout);

    // The line an OpenCode refusal's sentence starts on, after an optional marker of any bytes.
    private static readonly Regex OpenCodeStart = new(@"^\s*(?:\S{1,8}\s+)?OpenCode\s", RegexOptions.CultureInvariant, RefusalTimeout);
    private static readonly Regex ClawStart = new(@"^\s*(?:\S{1,8}\s+)?DefenseClaw\s", RegexOptions.CultureInvariant, RefusalTimeout);

    // ux.err's line, for a "(see above)" the sentences here do not name.
    private static readonly Regex CliError = new(@"^\s*(?:X|✗|✘)\s+(?<r>\S.*)$", RegexOptions.CultureInvariant, RefusalTimeout);

    // A line that starts something of its own, so it is not the rest of the line before it.
    private static readonly Regex StartsOwnLine = new(
        @"^(?:\s*[xX+>!]\s|\s*✗\s|\s*✘\s|---|\*{5}|At\s|\+\s|Windows PowerShell transcript)",
        RegexOptions.CultureInvariant,
        RefusalTimeout);

    /// <summary>A literal sentence as a pattern whose spaces are optional and may be line breaks: a wrap can fall on any of them.</summary>
    private static string Flex(string literal) => Regex.Escape(literal).Replace(@"\ ", @"\s*", StringComparison.Ordinal);

    /// <summary>
    /// The refusal of the transcript's last <c>x</c> line, or null. <paramref name="text"/> says what else the run did: a run that says it restored or left
    /// something (<see cref="TouchedTheInstall"/>) did not refuse before it changed anything, whatever its last line says.
    /// </summary>
    private static InstallerRefusal? RefusalOfLastLine(IReadOnlyList<string> lines, string text, string host)
    {
        var at = LastFailureLine(lines);
        if (at < 0 || TouchedTheInstall(text))
        {
            return null;
        }

        // The line as it stands first: joining can only be wrong about a line that was not wrapped.
        foreach (var candidate in new[] { lines[at], Unwrapped(lines, at) })
        {
            var line = Sanitize(FailureLine.Match(candidate).Groups["r"].Value);
            if (line.Length == 0)
            {
                continue;
            }

            if (MigrateRefused.IsMatch(line))
            {
                return UpgradeCheckRefusal(lines, at, line);
            }

            if (GuardedRollbackRefused.IsMatch(line))
            {
                return RollbackCheckRefusal(lines, at, line);
            }

            if (AgentsRunningStop.Match(line) is { Success: true } running)
            {
                return AgentsRunningRefusal(running, text, host);
            }

            if (UvCacheLine.IsMatch(line))
            {
                // The sentence up to "Nothing was changed.", with the path whole so the length it states is the one shown. The installer's own fix after it
                // (`$env:UV_CACHE_DIR = ...` and run again) only reaches a terminal session, not this app's run, so the next step is the app's alone.
                var whole = Sanitize(FailureLine.Match(candidate).Groups["r"].Value, shortenHome: false);
                return new InstallerRefusal(InstallerRefusalKind.UvCacheTooDeep, UvCacheReason(whole), UvCacheDepth.NextStep);
            }

            if (EndsNothingChanged.IsMatch(line))
            {
                return new InstallerRefusal(InstallerRefusalKind.NothingChanged, Cap(line), GenericNextStep);
            }
        }

        return null;
    }

    /// <summary>The longest uv cache refusal shown: the installer's sentence to "Nothing was changed." is about 330 characters plus the cache path (up to 32,767).</summary>
    internal const int MaxUvCacheReasonChars = 1200;

    private const string NothingWasChanged = "Nothing was changed.";

    /// <summary>The uv cache refusal up to and including "Nothing was changed.", capped at <see cref="MaxUvCacheReasonChars"/>.</summary>
    internal static string UvCacheReason(string sentence)
    {
        var end = sentence.IndexOf(NothingWasChanged, StringComparison.Ordinal);
        var reason = end >= 0 ? sentence[..(end + NothingWasChanged.Length)] : sentence;
        return reason.Length <= MaxUvCacheReasonChars ? reason : reason[..(MaxUvCacheReasonChars - 1)].TrimEnd() + "…";
    }

    /// <summary>
    /// install.ps1 1.0.6 <c>Assert-NoLoadedPluginAgents</c>: the upgrade stops here with nothing live changed (it runs before the lifecycle record, the hook swap and
    /// the gateway stop: :5730-5746, and the rollback's :5254-5259). The line itself does not say so, hence the one way it would not be true: a run told to accept
    /// the current install (<c>-AcceptCurrentInstall</c>) has closed the older hooks and stopped its gateway by the second look (:5733, :5746). That run is not called a refusal.
    /// </summary>
    private static InstallerRefusal? AgentsRunningRefusal(Match running, string text, string host)
    {
        if (host.Contains(InstallScriptPlan.AcceptCurrentInstallFlag, StringComparison.OrdinalIgnoreCase) ||
            text.Contains("(" + InstallScriptPlan.AcceptCurrentInstallFlag + ")", StringComparison.Ordinal))
        {
            return null;
        }

        var clause = running.Groups["clause"].Success ? ServiceStopClause : string.Empty;
        var reason = CloseAgents + CloseAgentsRest + clause + RunAgain + "; gateway still running: " + running.Groups["list"].Value.Trim();
        return new InstallerRefusal(InstallerRefusalKind.AgentsRunning, Cap(reason), CloseAgents + clause + RunAgain);
    }

    /// <summary>
    /// "Your configuration cannot be migrated": <c>migrate --check</c> failed on the staged CLI before the swap (install.ps1:5689-5692), and the CLI's line above
    /// it says why. A guarded OpenCode V1 is <c>_check_opencode_v2</c> (migrations.py:3319); any other check keeps its own line as the reason.
    /// </summary>
    private static InstallerRefusal UpgradeCheckRefusal(IReadOnlyList<string> lines, int at, string line)
    {
        foreach (var joined in CliLinesAbove(lines, at, OpenCodeStart))
        {
            if (OpenCodeV1Line.Match(joined) is { Success: true } m)
            {
                var raw = m.Groups["raw"].Value.Trim();
                var reason = $"OpenCode {raw} at {m.Groups["exe"].Value.Trim()} is OpenCode V1, and DefenseClaw {m.Groups["to"].Value}{V1Middle}";
                return new InstallerRefusal(InstallerRefusalKind.OpenCodeV1Upgrade, Cap(reason), OpenCodeV1NextStep);
            }
        }

        for (var i = at - 1; i >= Math.Max(0, at - CliLineReach); i--)
        {
            if (CliError.IsMatch(lines[i]) && CliError.Match(Sanitize(Unwrapped(lines, i))) is { Success: true } error)
            {
                return new InstallerRefusal(InstallerRefusalKind.NothingChanged, Cap(error.Groups["r"].Value.Trim()), GenericNextStep);
            }
        }

        return new InstallerRefusal(InstallerRefusalKind.NothingChanged, Cap(line), GenericNextStep);
    }

    /// <summary>
    /// "Rollback cancelled; nothing was changed (see above)": <c>Assert-OlderReleaseGuardsAgents</c> (install.ps1:639-652, called at :5218), whose only cause in 1.0.6 is
    /// an OpenCode V2 with a target before 1.0.6 (<c>check_opencode_rollback</c>, migrations.py:3400).
    /// </summary>
    private static InstallerRefusal RollbackCheckRefusal(IReadOnlyList<string> lines, int at, string line)
    {
        foreach (var joined in CliLinesAbove(lines, at, ClawStart))
        {
            if (OpenCodeV2Line.Match(joined) is { Success: true } m)
            {
                var reason = $"DefenseClaw {m.Groups["to"].Value} guards OpenCode V1 only and the OpenCode here is V2 ({m.Groups["raw"].Value.Trim()}{V2Middle}";
                return new InstallerRefusal(InstallerRefusalKind.OpenCodeV2Rollback, Cap(reason), OpenCodeV2NextStep);
            }
        }

        return new InstallerRefusal(InstallerRefusalKind.OpenCodeV2Rollback, Cap(line), OpenCodeV2FallbackStep);
    }

    /// <summary>Each line above <paramref name="at"/> that starts as <paramref name="start"/> says, nearest first, sanitized and with its wrapped rest joined on.</summary>
    private static IEnumerable<string> CliLinesAbove(IReadOnlyList<string> lines, int at, Regex start)
    {
        for (var i = at - 1; i >= Math.Max(0, at - CliLineReach); i--)
        {
            if (start.IsMatch(lines[i]))
            {
                yield return Sanitize(Unwrapped(lines, i));
            }
        }
    }

    /// <summary>The line at <paramref name="start"/> and the lines that look like the rest of it, joined exactly as they stand (a wrap may fall inside a word).</summary>
    private static string Unwrapped(IReadOnlyList<string> lines, int start)
    {
        var joined = new StringBuilder(lines[start]);
        var previous = lines[start].Length;
        for (var i = start + 1; i < lines.Count && i <= start + MaxWrappedLines; i++)
        {
            if (previous < MinWrapWidth || lines[i].Trim().Length == 0 || StartsOwnLine.IsMatch(lines[i]))
            {
                break;
            }

            joined.Append(lines[i]);
            previous = lines[i].Length;
        }

        return joined.ToString();
    }

    private static int LastFailureLine(IReadOnlyList<string> lines)
    {
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            if (FailureLine.IsMatch(lines[i]))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The installer's words for a run that restored something or left the install unfinished (<c>Get-RestoredNote</c>, <c>Close-UnfinishedInstallHooks</c>, <c>Finish-InstallLock</c>).</summary>
    private static bool TouchedTheInstall(string text) =>
        text.Contains("Your previous install is back", StringComparison.Ordinal) ||
        text.Contains("previous install is not fully back", StringComparison.Ordinal) ||
        text.Contains("previous install is not proven back", StringComparison.Ordinal) ||
        text.Contains("The install is not finished", StringComparison.Ordinal) ||
        text.Contains("The earlier install is not finished", StringComparison.Ordinal);
}
