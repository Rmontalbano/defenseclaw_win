using System.Globalization;
using DefenseClaw.Core.Config;

namespace DefenseClaw.Core.Setup;

/// <summary>
/// The <c>ai_discovery:</c> settings the TUI's AI Discovery wizard edits (<c>tui/panels/setup.py: ai_discovery_wizard_fields</c>), as the runtime
/// holds them: <c>AIDiscoveryConfig</c>'s defaults (<c>config.py</c>) for any key config.yaml does not name.
/// </summary>
/// <param name="Enabled"><c>ai_discovery.enabled</c> (off unless config.yaml says so).</param>
/// <param name="Mode"><c>passive</c> or <c>enhanced</c>: where model discovery looks. A stored value the CLI would refuse reads as <c>enhanced</c>, like the TUI.</param>
/// <param name="ScanIntervalMin">Minutes between full discovery scans.</param>
/// <param name="ProcessIntervalS">Seconds between the cheap process-list polls.</param>
/// <param name="ScanRoots">The folders a scan walks; <c>~</c> is the home folder (the sidecar expands it).</param>
/// <param name="MaxFilesPerScan">The most files one scan inspects.</param>
/// <param name="MaxFileBytes">The largest file it reads.</param>
/// <param name="IncludeShellHistory">Look in shell history for AI command-line tools.</param>
/// <param name="IncludePackageManifests">Look in package manifests (package.json, pyproject, Cargo.toml, ...) for AI SDKs.</param>
/// <param name="IncludeEnvVarNames">Look at environment variable <i>names</i> for AI provider keys.</param>
/// <param name="IncludeNetworkDomains">Look at hosts and SSH config for AI provider domains, and ask vetted loopback model servers what they host.</param>
/// <param name="AllowWorkspaceSignatures">Honour signature packs found inside scanned workspaces.</param>
/// <param name="StoreRawLocalPaths">Keep raw local paths in the discovery state file (off for privacy).</param>
public sealed record AiDiscoverySettings(
    bool Enabled,
    string Mode,
    int ScanIntervalMin,
    int ProcessIntervalS,
    IReadOnlyList<string> ScanRoots,
    int MaxFilesPerScan,
    long MaxFileBytes,
    bool IncludeShellHistory,
    bool IncludePackageManifests,
    bool IncludeEnvVarNames,
    bool IncludeNetworkDomains,
    bool AllowWorkspaceSignatures,
    bool StoreRawLocalPaths)
{
    /// <summary>The runtime's defaults (<c>AIDiscoveryConfig</c> in <c>config.py</c>; the CLI's help repeats them).</summary>
    public static AiDiscoverySettings Defaults { get; } = new(
        Enabled: false,
        Mode: AiDiscoveryTuning.EnhancedMode,
        ScanIntervalMin: 5,
        ProcessIntervalS: 60,
        ScanRoots: new[] { "~" },
        MaxFilesPerScan: 1000,
        MaxFileBytes: 512 * 1024,
        IncludeShellHistory: true,
        IncludePackageManifests: true,
        IncludeEnvVarNames: true,
        IncludeNetworkDomains: true,
        AllowWorkspaceSignatures: false,
        StoreRawLocalPaths: false);

    /// <summary>What a loaded config.yaml says; a missing document, section or key is the runtime's default.</summary>
    public static AiDiscoverySettings Read(ConfigDocument? document) => FromYaml(document?.RawText);

    /// <summary>What config.yaml's text says. Never throws: text that is not YAML says nothing, and every setting is then its default.</summary>
    public static AiDiscoverySettings FromYaml(string? yaml)
    {
        var reader = ConfigYamlReader.Parse(yaml);
        var d = Defaults;

        bool Flag(string key, bool fallback) => reader.Bool("ai_discovery", key) ?? fallback;

        int Whole(string key, int fallback) =>
            reader.Integer("ai_discovery", key) is { } value && value is >= int.MinValue and <= int.MaxValue ? (int)value : fallback;

        var mode = reader.Text("ai_discovery", "mode")?.ToLowerInvariant();
        var roots = reader.List("ai_discovery", "scan_roots");

        return new AiDiscoverySettings(
            Enabled: Flag("enabled", d.Enabled),
            Mode: mode is AiDiscoveryTuning.PassiveMode or AiDiscoveryTuning.EnhancedMode ? mode : d.Mode,
            ScanIntervalMin: Whole("scan_interval_min", d.ScanIntervalMin),
            ProcessIntervalS: Whole("process_interval_s", d.ProcessIntervalS),
            ScanRoots: roots ?? d.ScanRoots,
            MaxFilesPerScan: Whole("max_files_per_scan", d.MaxFilesPerScan),
            MaxFileBytes: reader.Integer("ai_discovery", "max_file_bytes") ?? d.MaxFileBytes,
            IncludeShellHistory: Flag("include_shell_history", d.IncludeShellHistory),
            IncludePackageManifests: Flag("include_package_manifests", d.IncludePackageManifests),
            IncludeEnvVarNames: Flag("include_env_var_names", d.IncludeEnvVarNames),
            IncludeNetworkDomains: Flag("include_network_domains", d.IncludeNetworkDomains),
            AllowWorkspaceSignatures: Flag("allow_workspace_signatures", d.AllowWorkspaceSignatures),
            StoreRawLocalPaths: Flag("store_raw_local_paths", d.StoreRawLocalPaths));
    }
}

/// <summary>
/// One setting that is to change: what it is called, its flag, and the value before and after as text.
/// </summary>
/// <param name="Key">A stable name (<c>scan_interval_min</c>, the config key).</param>
/// <param name="Label">What the dialog calls it.</param>
/// <param name="Flag">The long flag of <c>agent discovery enable</c> that carries it (the positive form for a switch).</param>
/// <param name="Before">The current value, as the dialog shows it.</param>
/// <param name="After">The new value.</param>
/// <param name="Args">What goes on the command line for it, flag first.</param>
public sealed record TuningChange(string Key, string Label, string Flag, string Before, string After, IReadOnlyList<string> Args)
{
    /// <summary>"Scan interval: 5 to 10" - for the review's list of what changes.</summary>
    public string Describe() => $"{Label}: {Before} to {After}";
}

/// <summary>How the change reaches the running gateway.</summary>
/// <param name="Restart">Restart the gateway after the change (the sidecar reads <c>ai_discovery</c> once, when it starts).</param>
/// <param name="ScanAfter">Ask the restarted gateway for a first scan; meaningless without a restart.</param>
public sealed record TuningRollout(bool Restart = true, bool ScanAfter = true);

/// <summary>
/// AI Discovery tuning (the TUI's AI Discovery wizard, the Mac's <c>aiDiscoveryCommands</c>) over the flags of
/// <c>defenseclaw agent discovery enable</c> as the installed 0.8.10 CLI prints them: mode, cadence, scope and sources. Pure: it validates,
/// diffs and builds the argv, and runs nothing.
/// <para>
/// <b>Only what changed is sent.</b> Every flag defaults to <c>None</c>, "leave as is" (<c>_build_discovery_overrides</c>), and an
/// <c>enable</c> on an install where it is already enabled applies exactly the flags it is given (<c>discovery_enable</c>: "Updating AI discovery
/// settings"). So a cadence change is <c>agent discovery enable --yes --scan-interval-min 10</c> and nothing else; the TUI and the Mac send all
/// of the form, which makes every untouched value a claim that it was what the operator meant.
/// </para>
/// <para>
/// <b>Nothing to apply is not a run.</b> The CLI answers an unchanged enable with "already enabled" - and then still asks for a scan when
/// <c>--scan</c> and <c>--restart</c> are on - so a plan with no change is not built at all (<see cref="Argv"/> returns null).
/// </para>
/// </summary>
public static class AiDiscoveryTuning
{
    /// <summary><c>passive</c>: known model stores and the narrower roots the operator lists.</summary>
    public const string PassiveMode = "passive";

    /// <summary><c>enhanced</c> (the default): passive, plus the broad home traversal and bounded application-storage roots.</summary>
    public const string EnhancedMode = "enhanced";

    /// <summary>The values of <c>--mode</c>.</summary>
    public static IReadOnlyList<string> Modes { get; } = new[] { PassiveMode, EnhancedMode };

    /// <summary><c>--scan-interval-min</c>: <c>click.IntRange(1, 24 * 60)</c>.</summary>
    public static (int Min, int Max) ScanIntervalRange { get; } = (1, 24 * 60);

    /// <summary><c>--process-interval-s</c>: <c>click.IntRange(5, 60 * 60)</c>.</summary>
    public static (int Min, int Max) ProcessIntervalRange { get; } = (5, 60 * 60);

    /// <summary><c>--max-files-per-scan</c>: <c>click.IntRange(10, 100_000)</c>.</summary>
    public static (int Min, int Max) MaxFilesRange { get; } = (10, 100_000);

    /// <summary><c>--max-file-bytes</c>: <c>click.IntRange(4 KiB, 16 MiB)</c>.</summary>
    public static (long Min, long Max) MaxFileBytesRange { get; } = (4 * 1024, 16 * 1024 * 1024);

    /// <summary>The flags this builder can send, in the order it sends them (the TUI's order: mode, cadence, scope, sources, privacy).</summary>
    public static IReadOnlyList<string> Flags { get; } = new[]
    {
        "--mode",
        "--scan-interval-min",
        "--process-interval-s",
        "--scan-roots",
        "--max-files-per-scan",
        "--max-file-bytes",
        "--include-shell-history",
        "--include-package-manifests",
        "--include-env-var-names",
        "--include-network-domains",
        "--allow-workspace-signatures",
        "--store-raw-local-paths",
    };

    /// <summary>The long flag, and its negative form, of each switch.</summary>
    private static string Negative(string flag) => "--no-" + flag[2..];

    /// <summary>
    /// What is wrong with <paramref name="settings"/> as the CLI would see it (an out-of-range number, a scan root it would split in two), as
    /// one sentence per setting, keyed by <see cref="TuningChange.Key"/>. Empty when the CLI would take every value. Only meaningful while
    /// discovery is on: the settings of a disabled one are not sent.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Problems(AiDiscoverySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var problems = new Dictionary<string, string>(StringComparer.Ordinal);

        if (!Modes.Contains(settings.Mode, StringComparer.Ordinal))
        {
            problems["mode"] = "Mode must be passive or enhanced.";
        }

        Range(problems, "scan_interval_min", "Scan interval", settings.ScanIntervalMin, ScanIntervalRange.Min, ScanIntervalRange.Max);
        Range(problems, "process_interval_s", "Process poll", settings.ProcessIntervalS, ProcessIntervalRange.Min, ProcessIntervalRange.Max);
        Range(problems, "max_files_per_scan", "Max files per scan", settings.MaxFilesPerScan, MaxFilesRange.Min, MaxFilesRange.Max);
        Range(problems, "max_file_bytes", "Max bytes per file", settings.MaxFileBytes, MaxFileBytesRange.Min, MaxFileBytesRange.Max);

        if (settings.ScanRoots.Count == 0)
        {
            problems["scan_roots"] = "Give at least one folder to scan (~ is your home folder). With none, a scan has nothing to look at.";
        }
        else if (settings.ScanRoots.FirstOrDefault(static r => r.Contains(',', StringComparison.Ordinal)) is { } comma)
        {
            problems["scan_roots"] = $"A comma separates folders on the command line, so \"{comma}\" would be two. Put each folder on its own line without one.";
        }
        else if (settings.ScanRoots.Any(static r => r.Any(char.IsControl)))
        {
            problems["scan_roots"] = "A folder cannot contain a control character.";
        }

        return problems;
    }

    /// <summary>
    /// The settings that differ between <paramref name="before"/> and <paramref name="after"/>, in <see cref="Flags"/> order. The enabled
    /// switch is not one of them (it chooses <c>enable</c> or <c>disable</c>; see <see cref="Argv"/>). Scan roots compare as the CLI does,
    /// element by element after splitting.
    /// </summary>
    public static IReadOnlyList<TuningChange> Diff(AiDiscoverySettings before, AiDiscoverySettings after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var changes = new List<TuningChange>();

        if (!string.Equals(before.Mode, after.Mode, StringComparison.Ordinal))
        {
            changes.Add(new("mode", "Mode", "--mode", before.Mode, after.Mode, new[] { "--mode", after.Mode }));
        }

        Number(changes, "scan_interval_min", "Scan interval (minutes)", "--scan-interval-min", before.ScanIntervalMin, after.ScanIntervalMin);
        Number(changes, "process_interval_s", "Process poll (seconds)", "--process-interval-s", before.ProcessIntervalS, after.ProcessIntervalS);

        if (!before.ScanRoots.SequenceEqual(after.ScanRoots, StringComparer.Ordinal))
        {
            changes.Add(new(
                "scan_roots",
                "Scan folders",
                "--scan-roots",
                string.Join(", ", before.ScanRoots),
                string.Join(", ", after.ScanRoots),
                new[] { "--scan-roots", string.Join(',', after.ScanRoots) }));
        }

        Number(changes, "max_files_per_scan", "Max files per scan", "--max-files-per-scan", before.MaxFilesPerScan, after.MaxFilesPerScan);
        Number(changes, "max_file_bytes", "Max bytes per file", "--max-file-bytes", before.MaxFileBytes, after.MaxFileBytes);

        Switch(changes, "include_shell_history", "Shell history", "--include-shell-history", before.IncludeShellHistory, after.IncludeShellHistory);
        Switch(changes, "include_package_manifests", "Package manifests", "--include-package-manifests", before.IncludePackageManifests, after.IncludePackageManifests);
        Switch(changes, "include_env_var_names", "Environment variable names", "--include-env-var-names", before.IncludeEnvVarNames, after.IncludeEnvVarNames);
        Switch(changes, "include_network_domains", "Network domains", "--include-network-domains", before.IncludeNetworkDomains, after.IncludeNetworkDomains);
        Switch(changes, "allow_workspace_signatures", "Honour workspace signatures", "--allow-workspace-signatures", before.AllowWorkspaceSignatures, after.AllowWorkspaceSignatures);
        Switch(changes, "store_raw_local_paths", "Store raw local paths", "--store-raw-local-paths", before.StoreRawLocalPaths, after.StoreRawLocalPaths);

        return changes;
    }

    /// <summary>
    /// The one command that takes <paramref name="before"/> to <paramref name="after"/>, or null when there is nothing to apply.
    /// <list type="bullet">
    ///   <item>Off to on, or on to on with a change: <c>agent discovery enable --yes</c> and the changed flags (<paramref name="changes"/>).</item>
    ///   <item>On to off: <c>agent discovery disable --yes</c>; the settings are not sent (the verb takes none).</item>
    ///   <item>Off to off: null, whatever else was edited - the CLI cannot write a setting while leaving discovery off.</item>
    /// </list>
    /// Without a restart the sidecar has no service to answer a scan, so <c>--no-scan</c> goes with <c>--no-restart</c> (the Setup panel's own
    /// <c>agent discovery enable</c> does the same).
    /// </summary>
    /// <param name="changes">The changes to send; <see cref="Diff"/> of the same two settings.</param>
    public static IReadOnlyList<string>? Argv(AiDiscoverySettings before, AiDiscoverySettings after, IReadOnlyList<TuningChange> changes, TuningRollout rollout)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(rollout);

        if (!after.Enabled)
        {
            if (!before.Enabled)
            {
                return null;
            }

            var disable = new List<string> { "agent", "discovery", "disable", "--yes" };
            if (!rollout.Restart)
            {
                disable.Add("--no-restart");
            }

            return disable;
        }

        if (before.Enabled && changes.Count == 0)
        {
            return null;
        }

        var argv = new List<string> { "agent", "discovery", "enable", "--yes" };
        foreach (var change in changes)
        {
            argv.AddRange(change.Args);
        }

        if (!rollout.Restart)
        {
            argv.Add("--no-restart");
            argv.Add("--no-scan");
        }
        else if (!rollout.ScanAfter)
        {
            argv.Add("--no-scan");
        }

        return argv;
    }

    /// <summary><see cref="Argv(AiDiscoverySettings, AiDiscoverySettings, IReadOnlyList{TuningChange}, TuningRollout)"/> for every change <see cref="Diff"/> finds.</summary>
    public static IReadOnlyList<string>? Argv(AiDiscoverySettings before, AiDiscoverySettings after, TuningRollout rollout) =>
        Argv(before, after, Diff(before, after), rollout);

    /// <summary>The flag a switch sends for <paramref name="on"/>: <c>--include-shell-history</c>, or <c>--no-include-shell-history</c>.</summary>
    public static string SwitchFlag(string positive, bool on) => on ? positive : Negative(positive);

    private static void Number(List<TuningChange> changes, string key, string label, string flag, long before, long after)
    {
        if (before != after)
        {
            changes.Add(new(
                key,
                label,
                flag,
                before.ToString(CultureInfo.InvariantCulture),
                after.ToString(CultureInfo.InvariantCulture),
                new[] { flag, after.ToString(CultureInfo.InvariantCulture) }));
        }
    }

    private static void Switch(List<TuningChange> changes, string key, string label, string flag, bool before, bool after)
    {
        if (before != after)
        {
            changes.Add(new(key, label, flag, before ? "on" : "off", after ? "on" : "off", new[] { SwitchFlag(flag, after) }));
        }
    }

    private static void Range(Dictionary<string, string> problems, string key, string label, long value, long min, long max)
    {
        if (value < min || value > max)
        {
            problems[key] = string.Create(
                CultureInfo.InvariantCulture,
                $"{label} must be a whole number from {min:N0} through {max:N0}.");
        }
    }
}
