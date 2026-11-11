using System.Text.Json;

namespace DefenseClaw.Core.Inventory;

/// <summary>
/// The optional <c>claude_desktop</c> block of one AI Discovery signal (<c>ai_discovery_claude_desktop.go@1.0.8:98-123</c>): what the
/// scan found about the Claude Desktop app, its Cowork feature and the plugins Claude Desktop lists. Discovery reports no protection state.
/// </summary>
public sealed record ClaudeDesktopInfo
{
    /// <summary>True when the app was found (<c>installed</c>).</summary>
    public bool Installed { get; init; }

    /// <summary>How the app was found: app_bundle, install_folder, msix_package or data_folder. Empty when not reported.</summary>
    public string InstalledSource { get; init; } = "";

    /// <summary>The app version. Empty when not reported, which is always so for an MSIX install.</summary>
    public string Version { get; init; } = "";

    /// <summary>Whether Cowork is on. Null means the scan could not tell.</summary>
    public bool? CoworkEnabled { get; init; }

    /// <summary>How Cowork's state was read: cowork_sessions, vm_bundle or vm_support_probe. Empty when not reported.</summary>
    public string CoworkEnabledSource { get; init; } = "";

    /// <summary>The Claude Code engine version bundled with this Claude Desktop. Empty when not reported.</summary>
    public string EngineVersion { get; init; } = "";

    /// <summary>True when the Cowork virtual machine bundle exists on disk.</summary>
    public bool VmBundlePresent { get; init; }

    /// <summary>The size of the VM bundle in bytes. Zero when not reported.</summary>
    public long VmBundleBytes { get; init; }

    /// <summary>The Cowork plugin names Claude Desktop lists, as the runtime sorted them.</summary>
    public IReadOnlyList<string> CoworkPlugins { get; init; } = [];

    /// <summary>"1p" (a Claude account), "3p" (third-party inference) or empty when not reported.</summary>
    public string Deployment { get; init; } = "";

    /// <summary>True for a third-party inference deployment.</summary>
    public bool IsThirdParty => Deployment == "3p";

    /// <summary>True when the DefenseClaw Cowork plugin is among the listed plugins (case-insensitive, trimmed).</summary>
    public bool HasDefenseClawPlugin
    {
        get
        {
            foreach (var plugin in CoworkPlugins)
            {
                if (string.Equals(plugin?.Trim(), ClaudeDesktopDiscovery.DefenseClawPluginName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>Value equality that compares <see cref="CoworkPlugins"/> by sequence, not by reference.</summary>
    public bool Equals(ClaudeDesktopInfo? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return Installed == other.Installed
            && InstalledSource == other.InstalledSource
            && Version == other.Version
            && CoworkEnabled == other.CoworkEnabled
            && CoworkEnabledSource == other.CoworkEnabledSource
            && EngineVersion == other.EngineVersion
            && VmBundlePresent == other.VmBundlePresent
            && VmBundleBytes == other.VmBundleBytes
            && Deployment == other.Deployment
            && CoworkPlugins.SequenceEqual(other.CoworkPlugins, StringComparer.Ordinal);
    }

    /// <summary>A hash that agrees with <see cref="Equals(ClaudeDesktopInfo?)"/>.</summary>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Installed);
        hash.Add(InstalledSource, StringComparer.Ordinal);
        hash.Add(Version, StringComparer.Ordinal);
        hash.Add(CoworkEnabled);
        hash.Add(CoworkEnabledSource, StringComparer.Ordinal);
        hash.Add(EngineVersion, StringComparer.Ordinal);
        hash.Add(VmBundlePresent);
        hash.Add(VmBundleBytes);
        hash.Add(Deployment, StringComparer.Ordinal);
        foreach (var plugin in CoworkPlugins)
        {
            hash.Add(plugin, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }
}

/// <summary>
/// Reads the <c>claude_desktop</c> block that AI Discovery 1.0.8 adds to a signal (<c>ai_discovery.go@1.0.8:415</c>), and words it the way
/// the CLI does (<c>claude_desktop.py@1.0.8:252</c>). The block lives in <c>ai_discovery_state.json</c> and <c>GET /api/v1/ai-usage</c>, not in the inventory database.
/// </summary>
public static class ClaudeDesktopDiscovery
{
    /// <summary>The key of the block inside a signal.</summary>
    public const string BlockKey = "claude_desktop";

    /// <summary>The detector name and evidence type of the signal that carries the block.</summary>
    public const string DetectorName = "claude_desktop";

    /// <summary>The signature id of the Claude Desktop signal.</summary>
    public const string SignatureId = "claude-desktop";

    /// <summary>The name of the Cowork plugin DefenseClaw builds (<c>cowork.py@1.0.8:49</c>).</summary>
    public const string DefenseClawPluginName = "defenseclaw";

    /// <summary>The <c>source</c> value in <c>agent_discovery.json</c> for an engine that Claude Desktop bundles.</summary>
    public const string EngineSource = "claude-desktop";

    /// <summary>The most plugin names the runtime reports (<c>ai_discovery_claude_desktop.go@1.0.8:98-123</c>).</summary>
    public const int MaxPlugins = 64;

    /// <summary>
    /// Reads one block. Returns null when the element is not a JSON object (absent, null or another type). A value of the wrong type
    /// counts as missing and never throws; a deployment other than 1p or 3p is empty; plugin names keep strings only, trimmed,
    /// without blanks or duplicates, in order, at most <see cref="MaxPlugins"/>.
    /// </summary>
    public static ClaudeDesktopInfo? Parse(JsonElement block)
    {
        if (block.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var deployment = Text(block, "deployment");
        return new ClaudeDesktopInfo
        {
            Installed = Flag(block, "installed") ?? false,
            InstalledSource = Text(block, "installed_source"),
            Version = Text(block, "version"),
            CoworkEnabled = Flag(block, "cowork_enabled"),
            CoworkEnabledSource = Text(block, "cowork_enabled_source"),
            EngineVersion = Text(block, "engine_version"),
            VmBundlePresent = Flag(block, "vm_bundle_present") ?? false,
            VmBundleBytes = block.TryGetProperty("vm_bundle_bytes", out var bytes)
                && bytes.ValueKind == JsonValueKind.Number
                && bytes.TryGetInt64(out var n) && n > 0 ? n : 0,
            CoworkPlugins = Plugins(block),
            Deployment = deployment is "1p" or "3p" ? deployment : "",
        };
    }

    /// <summary>The CLI's wording: "Claude account" for 1p, "third-party inference" for 3p, "deployment not reported" otherwise.</summary>
    public static string DeploymentText(string? deployment) => deployment switch
    {
        "1p" => "Claude account",
        "3p" => "third-party inference",
        _ => "deployment not reported",
    };

    /// <summary>"on", "off" or "unknown".</summary>
    public static string CoworkText(bool? enabled) => enabled switch
    {
        true => "on",
        false => "off",
        null => "unknown",
    };

    /// <summary>
    /// The version text of an <c>agent_discovery.json</c> agent (<c>agent_discovery.py@1.0.8:1599</c>): "&lt;version&gt; (Claude Desktop engine)"
    /// when the source is <see cref="EngineSource"/>, "Claude Desktop engine" when that source has no version, otherwise the version as given.
    /// </summary>
    public static string? EngineVersionText(string? version, string? source)
    {
        if (!string.Equals(source, EngineSource, StringComparison.Ordinal))
        {
            return version;
        }

        return string.IsNullOrEmpty(version) ? "Claude Desktop engine" : version + " (Claude Desktop engine)";
    }

    private static string Text(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "").Trim() : "";

    private static bool? Flag(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v))
        {
            return null;
        }

        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    private static List<string> Plugins(JsonElement obj)
    {
        var result = new List<string>();
        if (!obj.TryGetProperty("cowork_plugins", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var name = (item.GetString() ?? "").Trim();
            if (name.Length == 0 || !seen.Add(name))
            {
                continue;
            }

            result.Add(name);
            if (result.Count >= MaxPlugins)
            {
                break;
            }
        }

        return result;
    }
}
