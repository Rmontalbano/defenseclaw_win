using System.Windows.Input;
using DefenseClaw.Core.Inventory;

namespace DefenseClaw.App.ViewModels;

/// <summary>One Cowork plugin name Claude Desktop lists, and whether it is the plugin DefenseClaw builds.</summary>
public sealed record ClaudeDesktopPluginChip(string Name, bool IsDefenseClaw)
{
    /// <summary>The chip text: the DefenseClaw plugin is named as such, the rest are shown as listed.</summary>
    public string Label => IsDefenseClaw ? Name + " (DefenseClaw plugin)" : Name;

    public bool IsOther => !IsDefenseClaw;
}

/// <summary>
/// What one Claude Desktop data folder reports (the <c>claude_desktop</c> block of a discovery signal), worded as the CLI words it:
/// "Claude account" (1p) or "third-party inference" (3p). It reports no Cowork protection state and no provider; neither is shown.
/// </summary>
public sealed record ClaudeDesktopEntry
{
    public required string Title { get; init; }

    /// <summary>"data folder Claude-3p", or empty when the signal's evidence names none.</summary>
    public string DataFolderText { get; init; } = string.Empty;

    public required string AppText { get; init; }

    public required string CoworkText { get; init; }

    public required string EngineText { get; init; }

    public IReadOnlyList<ClaudeDesktopPluginChip> Plugins { get; init; } = [];

    public bool HasPlugins => Plugins.Count > 0;

    public bool HasNoPlugins => Plugins.Count == 0;

    /// <summary>The Cowork plugins, or "no Cowork plugins reported".</summary>
    public string PluginsText => Plugins.Count == 0
        ? "no Cowork plugins reported"
        : string.Join(", ", Plugins.Select(static p => p.Name));

    /// <summary>True when the DefenseClaw plugin is among the listed plugins.</summary>
    public bool HasDefenseClawPlugin => Plugins.Any(static p => p.IsDefenseClaw);

    /// <summary>The line under the title: app, Cowork, engine and data folder.</summary>
    public string DetailText => string.Join(" · ", new[] { AppText, CoworkText, EngineText, DataFolderText }.Where(static t => t.Length > 0));

    /// <summary>
    /// "Set up Cowork protection": opens the Cowork wizard. Supplied by the panel only on a runtime with Cowork protection (1.0.8) while the
    /// Cowork store does not report Cowork protected; null (no button) otherwise.
    /// </summary>
    public ICommand? OpenCoworkSetupCommand { get; init; }

    public bool HasOpenCoworkSetup => OpenCoworkSetupCommand is not null;

    /// <summary>The button's text: "Rebuild the Cowork plugin" for a plugin built by an older DefenseClaw (1.0.9 <c>cowork_plugin_stale</c>), as the Overview row says; else "Set up Cowork protection".</summary>
    public string OpenCoworkSetupLabel { get; init; } = SetUpCoworkLabel;

    /// <summary>The button's text while Cowork is not set up.</summary>
    public const string SetUpCoworkLabel = "Set up Cowork protection";

    /// <summary>The button's text for a stale plugin (the Overview row's action).</summary>
    public const string RebuildCoworkLabel = "Rebuild the Cowork plugin";

    /// <summary>A line for a screen reader.</summary>
    public override string ToString() =>
        $"{Title}: {DetailText}. Cowork plugins: {PluginsText}.";

    public static ClaudeDesktopEntry From(ClaudeDesktopInfo info, string? dataFolder, ICommand? openCoworkSetup = null, string? openCoworkSetupLabel = null)
    {
        ArgumentNullException.ThrowIfNull(info);

        var deployment = ClaudeDesktopDiscovery.DeploymentText(info.Deployment);
        var app = !info.Installed
            ? "app not found"
            : string.IsNullOrWhiteSpace(info.Version)
                ? "app installed (version not reported)"
                : "app " + info.Version.Trim();
        var folder = string.IsNullOrWhiteSpace(dataFolder) ? string.Empty : "data folder " + dataFolder.Trim();

        return new ClaudeDesktopEntry
        {
            Title = "Claude Desktop · " + deployment,
            DataFolderText = folder,
            AppText = app,
            CoworkText = "Cowork " + ClaudeDesktopDiscovery.CoworkText(info.CoworkEnabled),
            EngineText = string.IsNullOrWhiteSpace(info.EngineVersion) ? "no engine reported" : "engine " + info.EngineVersion.Trim(),
            Plugins = info.CoworkPlugins
                .Select(static name => new ClaudeDesktopPluginChip(
                    name,
                    string.Equals(name.Trim(), ClaudeDesktopDiscovery.DefenseClawPluginName, StringComparison.OrdinalIgnoreCase)))
                .ToList(),
            OpenCoworkSetupCommand = openCoworkSetup,
            OpenCoworkSetupLabel = openCoworkSetupLabel ?? SetUpCoworkLabel,
        };
    }

    /// <summary>One entry per signal that has a block: 1p first, then 3p, then the rest, then by data folder name.</summary>
    public static IReadOnlyList<ClaudeDesktopEntry> For(IEnumerable<DiscoverySignalRecord> signals, ICommand? openCoworkSetup = null, string? openCoworkSetupLabel = null)
    {
        ArgumentNullException.ThrowIfNull(signals);

        return signals
            .Where(static s => s.ClaudeDesktop is not null)
            .Select(static s => (Info: s.ClaudeDesktop!, Folder: FolderOf(s)))
            .OrderBy(static pair => pair.Info.Deployment switch { "1p" => 0, "3p" => 1, _ => 2 })
            .ThenBy(static pair => pair.Folder, StringComparer.OrdinalIgnoreCase)
            .Select(pair => From(pair.Info, pair.Folder, openCoworkSetup, openCoworkSetupLabel))
            .ToList();
    }

    private static string FolderOf(DiscoverySignalRecord signal) =>
        signal.Evidence
            .FirstOrDefault(static e => string.Equals(e.Type, ClaudeDesktopDiscovery.DetectorName, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(e.Basename))
            ?.Basename?.Trim() ?? string.Empty;
}
