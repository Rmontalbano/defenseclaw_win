using System.IO;

namespace DefenseClaw.App.Services.Updates;

/// <summary>
/// From 1.0.7 the per-user launcher refuses a data folder (or DEFENSECLAW_HOME) that is a reparse point (cmd/defenseclaw/peruser_launcher.go@1.0.7:49-58), and the
/// gateway does not start in that state (docs paths-troubleshooting.mdx@1.0.8:81-83). install.ps1 itself does not refuse, so this is a warning.
/// </summary>
public static class DataFolderLink
{
    public const string RowId = "data-folder-link";

    public static readonly Version FirstRefusingRelease = new(1, 0, 7);

    public static PreflightRow? Row(IUpgradeProbes probes, string dataDirectory, string? targetVersion)
    {
        if (!InstallScriptPlan.TryNormalizeVersion(targetVersion, out var target) || !Version.TryParse(target, out var version) || version < FirstRefusingRelease)
        {
            return null;
        }

        string? linked;
        try
        {
            linked = new[] { dataDirectory, probes.DefenseClawHome() }
                .Where(static p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(p => probes.IsReparsePoint(p!));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }

        return linked is null
            ? null
            : new PreflightRow(RowId, "Data folder", PreflightStatus.Warn,
                linked + " is a link (junction or symbolic link). From DefenseClaw 1.0.7 the defenseclaw command refuses a data folder that is a link, and the gateway does not start. " +
                "Restore it as a regular folder at that path, then upgrade.");
    }
}
