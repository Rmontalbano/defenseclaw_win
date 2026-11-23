using System.Text.RegularExpressions;

namespace DefenseClaw.App.Services.Updates;

/// <summary>
/// install.ps1 1.0.10 <c>Convert-HooksForRollback</c> (scripts/install.ps1@1.0.10:5063-5084): when <c>defenseclaw-gateway connector prepare-rollback</c> fails, whether
/// the hook entries could not be rewritten or the route-only model settings could not be put back, the installer prints one <c>!</c> line (:5080-5082). The CLI's
/// own per-agent lines (<c>put back the model settings route-only changed</c> on stdout, and on failure <c>route-only model settings were not put back</c>) print
/// for agents whose settings were never touched too, so nothing here keys on them.
/// </summary>
public static partial class InstallTranscript
{
    // Its own field, declared before the pattern: the order of static initializers across the parts of a partial class is not defined.
    private static readonly TimeSpan RouteOnlyTimeout = TimeSpan.FromSeconds(1);

    private static readonly Regex RouteOnlyNotPutBack = new(
        @"^\s*! (?<text>\S.*A route-only agent then gets 401 until the DefenseClaw base URL and route header are removed from its settings.*)$",
        RegexOptions.CultureInvariant,
        RouteOnlyTimeout);

    /// <summary>The notice for one transcript line, or null when it is not the installer's route-only warning.</summary>
    private static InstallerNotice? RouteOnlyNotice(string line)
    {
        if (RouteOnlyNotPutBack.Match(line) is not { Success: true } match)
        {
            return null;
        }

        var text = Sanitize(match.Groups["text"].Value).Replace("(see above)", "(see the install log)", StringComparison.Ordinal);
        return new InstallerNotice(InstallerNoticeKind.AgentSettingsNotPutBack, "Agent settings", Cap(text));
    }
}
