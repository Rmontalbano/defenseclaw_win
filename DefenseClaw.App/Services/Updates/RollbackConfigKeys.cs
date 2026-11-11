using DefenseClaw.Core.Config;

namespace DefenseClaw.App.Services.Updates;

/// <summary>
/// What a rollback leaves behind in config.yaml. install.ps1 -Rollback swaps the whole data folder (install.ps1@1.0.8 Invoke-Rollback -> Switch-WithPrevious):
/// the config.yaml saved at upgrade time comes back, and today's data folder becomes the new <c>previous\</c>. So the older release never meets a key it
/// refuses; settings made since the upgrade are what is left behind, and a second <c>defenseclaw rollback</c> brings them back. Read-only.
/// </summary>
public static class RollbackConfigKeys
{
    /// <summary>The sentence for the rollback note and the full text of the review warning.</summary>
    public sealed record Result(IReadOnlyList<string> Keys, string Note, string Warning);

    public const string WarningTitle = "Settings made since the upgrade";

    /// <param name="configText">Today's config.yaml text.</param>
    /// <param name="backTo">The version in <c>previous\VERSION</c>.</param>
    public static Result? Evaluate(string? configText, string? backTo)
    {
        if (string.IsNullOrWhiteSpace(configText) ||
            !InstallScriptPlan.TryNormalizeVersion(backTo, out var normalized) || !Version.TryParse(normalized, out var target))
        {
            return null;
        }

        var keys = ConfigKeyFloors.RefusedBy(ConfigYamlReader.Parse(configText), target);
        if (keys.Count == 0)
        {
            return null;
        }

        var note = "DefenseClaw " + normalized + " does not accept " + string.Join(", ", keys) + ", so those settings are not in the config.yaml it gets back. " +
                   "Today's config.yaml is kept in previous\\data: 'defenseclaw rollback' again brings it back.";
        return new Result(keys, note, "Rollback puts back the config.yaml saved when you upgraded. " + note);
    }
}
