using System.Text.RegularExpressions;

namespace DefenseClaw.App.ViewModels;

/// <summary>What doctor found about the Windows hook launcher or hook guard of one agent (runtime 1.0.9, hook_integrity.py).</summary>
/// <param name="Agent">The agent as doctor labels it (<c>Codex</c>, <c>Claude Code</c>, ...).</param>
/// <param name="Program">The file doctor names: <c>launcher</c> or <c>guard</c>.</param>
/// <param name="Mismatch">True for the FAIL (the file is not the copy the installer recorded); false for the WARN (the installer recorded no copy to compare with).</param>
internal sealed record HookLauncherFinding(string Agent, string Program, bool Mismatch)
{
    /// <summary>One row per agent, file and result.</summary>
    public string Key => $"{Agent}|{Program}|{(Mismatch ? "fail" : "warn")}";
}

/// <summary>
/// Reads doctor's "&lt;Agent&gt; hooks" row for a hook launcher (or hook guard) that is not the file the installer recorded, or that has no recorded copy to
/// compare with. Only the installer can put the launcher back (setup cannot recreate it), so this has no command to rebuild: it only recognises the row. Pure.
/// Both phrases are new in 1.0.9; a doctor result from an older runtime holds neither, so it is nothing.
/// </summary>
internal static class HookLauncherDoctorFix
{
    /// <summary>The defect doctor names for a launcher or guard that differs from the installer's record (hook_integrity.py, 1.0.9).</summary>
    internal const string MismatchPhrase = "does not match the copy the installer recorded";

    /// <summary>Why a launcher is only unverified (hook_integrity.py, 1.0.9).</summary>
    internal const string UnverifiedPhrase = "is not verified: the installer recorded no copy of it to compare with";

    private const string LauncherSubject = "the DefenseClaw hook launcher ";
    private const string GuardSubject = "the DefenseClaw hook guard ";

    private static readonly Regex Label = new(
        "^(?<agent>[A-Za-z][A-Za-z0-9 ._-]{0,40}) hooks( \\[[a-z0-9_-]{1,64}\\])?$",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        TimeSpan.FromMilliseconds(250));

    /// <summary>The finding this check holds, or null when it is not one.</summary>
    internal static HookLauncherFinding? From(DoctorCheckRow check)
    {
        ArgumentNullException.ThrowIfNull(check);

        try
        {
            if (check.Detail is not { Length: > 0 } detail ||
                Label.Match(check.Label.Trim()) is not { Success: true } label)
            {
                return null;
            }

            var agent = label.Groups["agent"].Value;
            if (string.Equals(check.Status, "fail", StringComparison.Ordinal) && detail.Contains(MismatchPhrase, StringComparison.Ordinal))
            {
                if (detail.Contains(LauncherSubject, StringComparison.Ordinal))
                {
                    return new HookLauncherFinding(agent, "launcher", true);
                }

                return detail.Contains(GuardSubject, StringComparison.Ordinal) ? new HookLauncherFinding(agent, "guard", true) : null;
            }

            return string.Equals(check.Status, "warn", StringComparison.Ordinal) &&
                   detail.Contains(LauncherSubject, StringComparison.Ordinal) &&
                   detail.Contains(UnverifiedPhrase, StringComparison.Ordinal)
                ? new HookLauncherFinding(agent, "launcher", false)
                : null;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }
}
