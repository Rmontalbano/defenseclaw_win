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
}

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
            };
        }
        catch (RegexMatchTimeoutException)
        {
            return InstallTranscriptFacts.Empty;
        }
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
