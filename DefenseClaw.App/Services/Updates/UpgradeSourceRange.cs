using System.Globalization;

namespace DefenseClaw.App.Services.Updates;

/// <summary>
/// The source versions a release's notes support upgrading from. Only a release that states a range has an entry (CHANGELOG.md@1.0.8:85: "Upgrades are
/// supported from 1.0.2 to 1.0.7; upgrades from 0.8.x are not."; docs upgrade.mdx@1.0.8:15 and :121). install.ps1 refuses no source version, so this only warns.
/// </summary>
public static class UpgradeSourceRange
{
    public const string RowId = "upgrade-source";

    /// <summary>Exact target version -> first and last supported source.</summary>
    private static readonly Dictionary<string, (Version From, Version To)> Ranges = new(StringComparer.Ordinal)
    {
        ["1.0.8"] = (new Version(1, 0, 2), new Version(1, 0, 7)),
    };

    /// <summary>A warning row, or null: no entry for the target, an installed version that is unknown or inside the range, or a reinstall of the target.</summary>
    public static PreflightRow? Row(string? targetVersion, string? installedVersion)
    {
        if (!InstallScriptPlan.TryNormalizeVersion(targetVersion, out var target) || !Ranges.TryGetValue(target, out var range) ||
            !InstallScriptPlan.TryNormalizeVersion(installedVersion, out var installed) || !Version.TryParse(installed, out var from) ||
            (from >= range.From && from <= range.To) || from == Version.Parse(target))
        {
            return null;
        }

        var text = string.Create(CultureInfo.InvariantCulture,
            $"The DefenseClaw {target} release notes support upgrades from {range.From} to {range.To}, not from {installed}. The upgrade still runs, but this path is no longer tested.");
        if (from.Major == 0)
        {
            text += " End every agent session before you upgrade and start new ones afterwards: the 0.8.x Claude Code hook can let tool calls run without a check while the upgrade runs.";
        }

        return new PreflightRow(RowId, "Upgrade path", PreflightStatus.Warn, text);
    }
}
