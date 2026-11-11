using System.IO;
using DefenseClaw.App.Services.Updates;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.ViewModels.Updates;

/// <summary>
/// Read-only advisories around the upgrade and the rollback: the settings a rollback leaves behind (<see cref="RollbackConfigKeys"/>) and the Cowork plugin
/// that 'defenseclaw uninstall' cannot remove (<see cref="CoworkUninstall"/>). Files are read; nothing is run or changed.
/// </summary>
public sealed partial class InstallScriptUpgradeViewModel
{
    // What the last install-state read found in today's config.yaml that the version in previous\VERSION refuses; null when nothing.
    private RollbackConfigKeys.Result? _rollbackConfig;

    /// <summary>Whether the app's uninstall copy should add the Cowork plugin sentence: the runtime knows Cowork and its plugin-state file exists.</summary>
    private bool CoworkPluginLeft() =>
        CoworkUninstall.Applies(_services.Runtime.Capabilities.Has(RuntimeCapability.CoworkProtection), _services.Paths.DataDirectory);

    /// <summary>Reads today's config.yaml and the kept version; null when unreadable or nothing is left behind.</summary>
    private RollbackConfigKeys.Result? ReadRollbackConfig(string? backTo)
    {
        try
        {
            var path = _services.Paths.ConfigFilePath;
            var info = new FileInfo(path);
            return info.Exists && info.Length <= 1024 * 1024 ? RollbackConfigKeys.Evaluate(File.ReadAllText(path), backTo) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
