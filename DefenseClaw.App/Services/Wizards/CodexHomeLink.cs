using System.IO;

namespace DefenseClaw.App.Services.Wizards;

/// <summary>A Codex home that goes through a link (CUST-401): which folder is the link, which home was meant, and the fix 1.0.5 names.</summary>
/// <param name="Home">The Codex home setup would write to: <c>CODEX_HOME</c> when it names a folder, else <c>%USERPROFILE%\.codex</c>.</param>
/// <param name="Link">The folder on the way to <paramref name="Home"/> (or <paramref name="Home"/> itself) that is a symbolic link or junction.</param>
/// <param name="Fix">The step 1.0.5's refusal names for this case.</param>
public sealed record CodexHomeLinkFinding(string Home, string Link, string Fix)
{
    /// <summary>The heading of the warning that carries <see cref="Message"/>.</summary>
    public const string Title = "Codex home is a link";

    /// <summary>The sentence the wizard shows, before the review and on it.</summary>
    public string Message =>
        "Codex home " + Home + " goes through a link or junction" + (string.Equals(Link, Home, StringComparison.OrdinalIgnoreCase) ? "." : " (" + Link + ").") +
        " DefenseClaw 1.0.5 does not write Codex settings through one on Windows, and setup codex stops before it changes anything. " +
        Fix + ", then run this setup again.";
}

/// <summary>
/// Whether the Codex home is a link (CUST-401). 1.0.5's Codex setup refuses, before it changes anything, a per-user Codex home that goes through a symbolic
/// link or junction (<c>codexHomeLinkRefusal</c>, <c>internal/gateway/connector/codex_config_dir_windows.go:56</c>, called from <c>checkAgentSettings</c>,
/// <c>codex.go:386</c>): it walks the home and every folder above it (<c>safefile.RejectReparseChain</c>, <c>protect_windows.go:427</c>) for the reparse-point
/// attribute, and a path that does not exist yet is not a link. The fix it names is the home's own: <c>make &lt;home&gt; a real folder</c>, or, when
/// <c>CODEX_HOME</c> names another folder, <c>set CODEX_HOME to the real folder, or unset it to use &lt;default&gt;</c>. This is the same look, read-only, so
/// the wizard can say so before the review instead of after a refused run. The attribute check is injectable so a test needs no real link.
/// </summary>
public static class CodexHomeLink
{
    /// <summary>The finding for this machine, or null when the home is a plain folder (or does not exist yet).</summary>
    public static CodexHomeLinkFinding? Find() =>
        Find(Environment.GetEnvironmentVariable("CODEX_HOME"), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), IsReparsePoint);

    /// <summary>The finding for a <paramref name="codexHome"/> setting and <paramref name="userProfile"/>, judged by <paramref name="isReparsePoint"/>.</summary>
    public static CodexHomeLinkFinding? Find(string? codexHome, string userProfile, Func<string, bool> isReparsePoint)
    {
        ArgumentNullException.ThrowIfNull(userProfile);
        ArgumentNullException.ThrowIfNull(isReparsePoint);

        if (userProfile.Length == 0 && string.IsNullOrWhiteSpace(codexHome))
        {
            return null;
        }

        var defaultHome = Path.Combine(userProfile, ".codex");
        var overridden = !string.IsNullOrWhiteSpace(codexHome) && !SamePath(codexHome.Trim(), defaultHome);
        var home = string.IsNullOrWhiteSpace(codexHome) ? defaultHome : codexHome.Trim();

        string full;
        try
        {
            full = Path.GetFullPath(home);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return null;
        }

        // Home first, then each folder above it: the nearest link is the one to name.
        for (var current = full; current is { Length: > 0 }; current = Path.GetDirectoryName(current))
        {
            if (isReparsePoint(current))
            {
                var fix = overridden
                    ? "Set CODEX_HOME to the real folder, or unset it to use " + defaultHome
                    : "Make " + full + " a real folder";
                return new CodexHomeLinkFinding(full, current, fix);
            }
        }

        return null;
    }

    /// <summary>True when <paramref name="path"/> exists and is a reparse point (a symbolic link or junction); false for a missing path or one that cannot be read.</summary>
    public static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return false;
        }
    }
}
