using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using DefenseClaw.Core.Observability;

namespace DefenseClaw.App.Services.Updates;

/// <summary>How install.ps1 says it checked the release bytes it installed (its own <c>+</c> lines in the transcript).</summary>
public enum TranscriptAssetCheck
{
    /// <summary>The transcript says nothing about it: no transcript, an older installer, or a run that stopped before it got that far.</summary>
    None,

    /// <summary><c>+ Assets match checksums.txt</c> with no signature line: checked against checksums.txt only (cosign was not installed).</summary>
    ChecksumsOnly,

    /// <summary><c>+ Release signature verified</c> and <c>+ Assets match checksums.txt</c>.</summary>
    SignatureVerified,
}

/// <summary>
/// What one install.ps1 transcript says, reduced to the facts the result card needs (CUST-359). <see cref="Reason"/> is sanitized: it never carries
/// the transcript header (user, machine), a home folder, the trailing <c>Log: &lt;path&gt;</c> or a credential.
/// </summary>
public sealed record InstallTranscriptFacts
{
    public static InstallTranscriptFacts Empty { get; } = new();

    /// <summary>The installer's failure in one line (<c>&lt;cause&gt; — &lt;outcome&gt;</c>), capped at <see cref="InstallScriptPlan.MaxFailureReasonChars"/>; null without an <c>x</c> line.</summary>
    public string? Reason { get; init; }

    /// <summary>The installer says it put the previous install back (<c>Your previous install is back</c>).</summary>
    public bool PreviousRestored { get; init; }

    /// <summary>The installer says the previous install is not fully back (<c>not fully back</c>, <c>not proven back</c>).</summary>
    public bool RestoreIncomplete { get; init; }

    /// <summary>The new gateway did not become healthy (or a rolled-back install's gateway is not running).</summary>
    public bool GatewayUnhealthy { get; init; }

    /// <summary>The gateway was not running before the upgrade, so the installer did not start it (GAP-1496: <c>! The gateway is not running, so agent hooks are not guarded</c>).</summary>
    public bool GatewayLeftStopped { get; init; }

    public TranscriptAssetCheck AssetCheck { get; init; }

    /// <summary>The run took its assets from a local folder (<c>-Local</c>), not from the release download.</summary>
    public bool LocalAssets { get; init; }

    /// <summary>
    /// The installer refused before it changed anything, in one of the sentences that say so and that only 1.0.5 and later print (CUST-399); null
    /// otherwise. A refusal is exit 1, which alone reads as "did not finish, restored".
    /// </summary>
    public InstallerRefusal? Refusal { get; init; }

    /// <summary>
    /// <c>! Protection is off in the kept config (guardrail.enabled = false)</c>: the gateway was not running before the upgrade and the guardrail is
    /// off, so the installer left it stopped on purpose (it prints this instead of the <see cref="GatewayLeftStopped"/> line).
    /// </summary>
    public bool GuardrailOff { get; init; }

    /// <summary>What the installer told the operator after it finished, in its own words (CUST-399): the outage notice, protection off, Codex setup.</summary>
    public IReadOnlyList<InstallerNotice> Notices { get; init; } = Array.Empty<InstallerNotice>();
}

/// <summary>Which refusal the installer printed.</summary>
public enum InstallerRefusalKind
{
    /// <summary>
    /// install.ps1 1.0.5 <c>Invoke-Rollback</c>: Codex changed since the older install set it up, the installer could not select the updated program for
    /// it, and the target is 1.0.4 or later, whose gateway does not start without the program it set up (GAP-2299, GAP-2513).
    /// </summary>
    StaleAgentRollback,
}

/// <summary>A refusal: the installer's line (sanitized, capped) and the next step it named.</summary>
public sealed record InstallerRefusal(InstallerRefusalKind Kind, string Reason, string NextStep);

/// <summary>Which notice the installer printed.</summary>
public enum InstallerNoticeKind
{
    /// <summary>After "Upgraded from" a release before 1.0.4: a connector with no saved fail mode now lets calls run during a gateway outage.</summary>
    OutageOpen,

    /// <summary>The kept config has the guardrail off, so the gateway was not started and agent hooks are not guarded.</summary>
    ProtectionOff,

    /// <summary>A rollback to 1.0.3 or earlier finished, but Codex changed since that release set it up: it starts without Codex hooks.</summary>
    CodexNeedsSetup,
}

/// <summary>One installer notice: a short title for the row and the installer's sentence (sanitized), with the command it named.</summary>
public sealed record InstallerNotice(InstallerNoticeKind Kind, string Title, string Text);

/// <summary>Reads install.ps1's PowerShell transcript (<c>~\.defenseclaw\logs\install-*.log</c>). Pure apart from <see cref="Read"/>; never throws.</summary>
public static class InstallTranscript
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    // Write-Err: "  x <message>". Write-Ok: "  + <message>". Write-Info: "  > <message>". Write-Warn: "  ! <message>".
    private static readonly Regex FailureLine = new(@"^  x (?<r>\S.*)$", RegexOptions.Multiline | RegexOptions.CultureInvariant, RegexTimeout);

    // The lines that say what happened to the run rather than why: the reason is the line before the first of them.
    private static readonly Regex Wrapper = new(
        @"^(?:DefenseClaw \S+ was not installed\.|DefenseClaw \S+ is not proven installed; restoring|Installing \S+ failed; restoring|DefenseClaw was not installed; see above)",
        RegexOptions.CultureInvariant,
        RegexTimeout);

    private static readonly Regex SignatureVerified = new(@"^\s*\+ Release signature verified\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex AssetsMatch = new(@"^\s*\+ Assets match checksums\.txt\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex LocalVerify = new(@"^\s*> Verifying the release assets in ", RegexOptions.Multiline | RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex HostLine = new(@"^Host Application:(?<line>.*)$", RegexOptions.Multiline | RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex LocalSwitch = new(@"(?:^|\s)-Local(?:\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex LeftStopped = new(@"^\s*! The gateway is not running, so agent hooks are not guarded", RegexOptions.Multiline | RegexOptions.CultureInvariant, RegexTimeout);

    // install.ps1 1.0.5 Invoke-Rollback (scripts/install.ps1:5204-5205): the refusal after Get-StaleAgentEvidence, thrown by Die and printed as "  x ...".
    private static readonly Regex StaleAgentRefusal = new(
        @"\. DefenseClaw \d+\.\d+\.\d+ does not start with an agent program it did not set up, so the rollback would stop half-way\. Nothing was changed\. " +
        @"(?<next>Put that version back and run 'defenseclaw rollback' again, or stay on DefenseClaw \S+?)\.?$",
        RegexOptions.CultureInvariant,
        RegexTimeout);

    // install.ps1 1.0.5 (scripts/install.ps1:5923-5926): printed once, right after "Upgraded from <version>.", when the old version is before 1.0.4.
    private const string OutageNoticeText =
        "Gateway outages now let calls run with a warning (the default and permissive policies). Run `defenseclaw guardrail fail-mode closed` to keep blocking them.";

    // install.ps1 (scripts/install.ps1:5933-5934 in 1.0.5): Write-Warn, then the command on its own line.
    private static readonly Regex ProtectionOffLine = new(
        @"^\s*! (?<text>Protection is off in the kept config \(guardrail\.enabled = false\), so agent hooks are not guarded)\s*$",
        RegexOptions.CultureInvariant,
        RegexTimeout);

    private static readonly Regex TurnBackOnLine = new(@"^\s*(?<text>Turn it back on with: \S.*?)\s*$", RegexOptions.CultureInvariant, RegexTimeout);

    // install.ps1 1.0.5 Invoke-Rollback (scripts/install.ps1:5191-5192): a rollback to 0.8.10 or 1.0.0-1.0.3 after a Codex update finishes with this warning.
    private static readonly Regex CodexNeedsSetupLine = new(
        @"^\s*! (?<text>.+\. DefenseClaw \d+\.\d+\.\d+ starts without Codex hooks until you run 'defenseclaw setup codex' after the rollback)\s*$",
        RegexOptions.CultureInvariant,
        RegexTimeout);

    // " Log: C:\Users\...\install-....log" at the end of the installer's last line: the card shows the log on its own.
    private static readonly Regex LogTail = new(@"\s*Log:\s+(?:[A-Za-z]:\\|\\\\|~[\\/]|%USERPROFILE%).*$", RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex UsersFolder = new(@"\b[A-Za-z]:\\Users\\[^\\\s'""]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);

    /// <summary>Reads <paramref name="path"/> (size-capped, shared) and parses it; <see cref="InstallTranscriptFacts.Empty"/> on any read problem.</summary>
    public static InstallTranscriptFacts Read(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return InstallTranscriptFacts.Empty;
        }

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > InstallRunReader.MaxLogBytes)
            {
                return InstallTranscriptFacts.Empty;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return Parse(reader.ReadToEnd());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return InstallTranscriptFacts.Empty;
        }
    }

    /// <summary>The facts of one transcript's text (LF or CRLF).</summary>
    public static InstallTranscriptFacts Parse(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return InstallTranscriptFacts.Empty;
        }

        try
        {
            text = text.Replace("\r\n", "\n").Replace('\r', '\n');
            var lines = text.Split('\n');
            var failures = FailureLine.Matches(text).Select(m => m.Groups["r"].Value.Trim()).Where(l => l.Length > 0).ToArray();
            var host = HostLine.Match(text) is { Success: true } h ? h.Groups["line"].Value : string.Empty;
            var signature = SignatureVerified.IsMatch(text);
            var assets = AssetsMatch.IsMatch(text);

            return new InstallTranscriptFacts
            {
                Reason = ReasonOf(failures),
                PreviousRestored = text.Contains("Your previous install is back", StringComparison.Ordinal),
                RestoreIncomplete = text.Contains("previous install is not fully back", StringComparison.Ordinal) ||
                                    text.Contains("previous install is not proven back", StringComparison.Ordinal),
                GatewayUnhealthy = failures.Any(l => l.Contains("did not become healthy", StringComparison.Ordinal) ||
                                                     l.Contains("is installed, but its gateway is not running", StringComparison.Ordinal)),
                GatewayLeftStopped = LeftStopped.IsMatch(text),
                AssetCheck = assets ? signature ? TranscriptAssetCheck.SignatureVerified : TranscriptAssetCheck.ChecksumsOnly : TranscriptAssetCheck.None,
                LocalAssets = LocalVerify.IsMatch(text) || LocalSwitch.IsMatch(host),
                Refusal = RefusalOf(failures),
                GuardrailOff = lines.Any(l => ProtectionOffLine.IsMatch(l)),
                Notices = NoticesOf(lines),
            };
        }
        catch (RegexMatchTimeoutException)
        {
            return InstallTranscriptFacts.Empty;
        }
    }

    /// <summary>The refusal in the installer's <c>x</c> lines (the last one wins), or null. Keyed on the exact sentence, so older installers never match.</summary>
    private static InstallerRefusal? RefusalOf(IReadOnlyList<string> failures)
    {
        for (var i = failures.Count - 1; i >= 0; i--)
        {
            var line = Sanitize(failures[i]);
            if (StaleAgentRefusal.Match(line) is { Success: true } m)
            {
                return new InstallerRefusal(InstallerRefusalKind.StaleAgentRollback, Cap(line), m.Groups["next"].Value);
            }
        }

        return null;
    }

    /// <summary>The installer's notices, in the order it printed them, each once.</summary>
    private static IReadOnlyList<InstallerNotice> NoticesOf(IReadOnlyList<string> lines)
    {
        var notices = new List<InstallerNotice>();
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (string.Equals(line.Trim(), OutageNoticeText, StringComparison.Ordinal) && PreviousText(lines, i).Contains("Upgraded from ", StringComparison.Ordinal))
            {
                Add(new InstallerNotice(InstallerNoticeKind.OutageOpen, "Gateway outages", OutageNoticeText));
            }
            else if (ProtectionOffLine.Match(line) is { Success: true } off)
            {
                var text = off.Groups["text"].Value + ".";
                if (i + 1 < lines.Count && TurnBackOnLine.Match(lines[i + 1]) is { Success: true } next)
                {
                    text += " " + next.Groups["text"].Value;
                }

                Add(new InstallerNotice(InstallerNoticeKind.ProtectionOff, "Protection off", text));
            }
            else if (CodexNeedsSetupLine.Match(line) is { Success: true } codex)
            {
                Add(new InstallerNotice(InstallerNoticeKind.CodexNeedsSetup, "Codex hooks", Cap(Sanitize(codex.Groups["text"].Value)) + "."));
            }
        }

        return notices;

        void Add(InstallerNotice notice)
        {
            if (!notices.Any(n => n.Kind == notice.Kind))
            {
                notices.Add(notice);
            }
        }
    }

    /// <summary>The nearest non-empty line before <paramref name="index"/>, or empty.</summary>
    private static string PreviousText(IReadOnlyList<string> lines, int index)
    {
        for (var i = index - 1; i >= 0; i--)
        {
            if (lines[i].Trim().Length > 0)
            {
                return lines[i];
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// The reason in one line: the last <c>x</c> line before the first "was not installed / restoring" line (the cause), then the installer's last word
    /// on it (<c>DefenseClaw 1.0.4 was not installed. Your previous install is back.</c>); or, without such a line, the last <c>x</c> line. Sanitized.
    /// </summary>
    private static string? ReasonOf(IReadOnlyList<string> failures)
    {
        if (failures.Count == 0)
        {
            return null;
        }

        var lines = failures.Select(Sanitize).Where(l => l.Length > 0).ToArray();
        if (lines.Length == 0)
        {
            return null;
        }

        var firstWrapper = Array.FindIndex(lines, l => Wrapper.IsMatch(l));
        var last = lines[^1];
        string reason;
        if (firstWrapper < 0)
        {
            reason = last;
        }
        else
        {
            var cause = lines.Take(firstWrapper).LastOrDefault(l => !Wrapper.IsMatch(l));
            var outcome = lines.Last(l => Wrapper.IsMatch(l));
            reason = cause is null ? outcome : string.Equals(cause, outcome, StringComparison.Ordinal) ? cause : cause + " — " + outcome;
        }

        return Cap(reason);
    }

    private static string Cap(string reason)
    {
        if (reason.Length <= InstallScriptPlan.MaxFailureReasonChars)
        {
            return reason;
        }

        // The retry command is at the end of the line, so a cut would drop the switch the hint keys on: say it after the cut.
        var keepFlag = reason.Contains(InstallScriptPlan.AcceptCurrentInstallFlag, StringComparison.Ordinal);
        var suffix = keepFlag ? " (" + InstallScriptPlan.AcceptCurrentInstallFlag + ")" : string.Empty;
        return reason[..(InstallScriptPlan.MaxFailureReasonChars - 1 - suffix.Length)].TrimEnd() + "…" + suffix;
    }

    /// <summary>
    /// One installer line made fit to show: URLs cut to their host and credentials masked (<see cref="EndpointDisplay.ScrubLine"/>), the trailing
    /// <c>Log: &lt;path&gt;</c> dropped, and a home folder (<c>C:\Users\&lt;name&gt;</c>) written as <c>~</c>.
    /// </summary>
    public static string Sanitize(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return string.Empty;
        }

        try
        {
            var text = EndpointDisplay.ScrubLine(line);
            text = LogTail.Replace(text, string.Empty);

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (home.Length > 3)
            {
                text = Regex.Replace(text, Regex.Escape(Path.TrimEndingDirectorySeparator(home)), "~", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
            }

            return UsersFolder.Replace(text, "~").Trim();
        }
        catch (RegexMatchTimeoutException)
        {
            return string.Empty;
        }
    }
}
