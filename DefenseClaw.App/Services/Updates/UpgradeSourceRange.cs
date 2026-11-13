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

    private static readonly Version FirstTargetWithSessionAdvice = new(1, 0, 8);

    private const string AgentSessionAdvice =
        "End every agent session before you upgrade and start new ones afterwards: the 0.8.x Claude Code hook can let tool calls run without a check while the upgrade runs.";

    /// <summary>A warning row, or null: no entry for the target, an installed version that is unknown or inside the range, or a reinstall of the target.</summary>
    public static PreflightRow? Row(string? targetVersion, string? installedVersion)
    {
        if (!InstallScriptPlan.TryNormalizeVersion(targetVersion, out var target) ||
            !InstallScriptPlan.TryNormalizeVersion(installedVersion, out var installed) || !Version.TryParse(installed, out var from) ||
            from == Version.Parse(target))
        {
            return null;
        }

        if (!Ranges.TryGetValue(target, out var range))
        {
            // No stated range for this target (1.0.9 states none, and none is invented). A 0.8.x source still gets the agent-session advice, which the docs keep
            // for every upgrade from 0.8.x (docs upgrade.mdx@1.0.9:121). Targets before 1.0.8 never had this row, and keep not having it.
            return from.Major == 0 && Version.TryParse(target, out var to) && to >= FirstTargetWithSessionAdvice
                ? new PreflightRow(RowId, "Upgrade path", PreflightStatus.Warn, string.Create(CultureInfo.InvariantCulture, $"You are upgrading from {installed}. ") + AgentSessionAdvice)
                : null;
        }

        if (from >= range.From && from <= range.To)
        {
            return null;
        }

        var text = string.Create(CultureInfo.InvariantCulture,
            $"The DefenseClaw {target} release notes support upgrades from {range.From} to {range.To}, not from {installed}. The upgrade still runs, but this path is no longer tested.");
        if (from.Major == 0)
        {
            text += " " + AgentSessionAdvice;
        }

        return new PreflightRow(RowId, "Upgrade path", PreflightStatus.Warn, text);
    }
}
