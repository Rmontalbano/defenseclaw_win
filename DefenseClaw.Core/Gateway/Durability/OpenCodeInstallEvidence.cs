using System.Text.Json;
using System.Text.RegularExpressions;
using DefenseClaw.Core.Config;
using DefenseClaw.Core.IO;
using DefenseClaw.Core.Setup;
using DefenseClaw.Core.Text;

namespace DefenseClaw.Core.Gateway.Durability;

/// <summary>An OpenCode found on this account without running it: its version as recorded (written out for display, cut short) and where it was found.</summary>
/// <param name="Version">The version as the discovery record or the package says it.</param>
/// <param name="Location">The recorded program, or the folder of the npm package.</param>
public sealed record OpenCodeInstall(string Version, string Location);

/// <summary>What the files say about OpenCode here (CUST-424): whether DefenseClaw guards it, and the V2 or V1 it would guard.</summary>
/// <param name="Configured">OpenCode is in this install's active connectors (upstream's <c>_opencode_configured</c>).</param>
/// <param name="V2">An OpenCode V2 in the known places; null when none was found or OpenCode is not configured.</param>
/// <param name="V1">An OpenCode V1, and no V2 beside it; null otherwise.</param>
public sealed record OpenCodeInstallFacts(bool Configured, OpenCodeInstall? V2, OpenCodeInstall? V1)
{
    public static OpenCodeInstallFacts NotConfigured { get; } = new(false, null, null);
}

/// <summary>
/// DefenseClaw guards OpenCode V2 only since 1.0.6 and earlier releases guard V1 only (cli/defenseclaw/migrations.py:3276-3279 at tag 1.0.6), so its upgrade
/// refuses a guarded V1 and its rollback refuses a guarded V2. This reads the same files as upstream's <c>_opencode_v2_in_known_places</c>
/// (migrations.py:3344) so the app can say so up front. Upstream also runs the program (<c>_installed_opencode</c>); this never does, so it can miss an
/// OpenCode the installer finds and the installer stays the judge. Reads only, each file size-capped; a missing or malformed file is "nothing found", never an error.
/// </summary>
public static class OpenCodeInstallEvidence
{
    /// <summary>The first release that guards OpenCode V2 (<c>_OPENCODE_V2_SINCE</c>).</summary>
    public static readonly Version FirstV2Release = new(1, 0, 6);

    /// <summary>What 1.0.6 tells the operator to run to move to OpenCode V2 (<c>_OPENCODE_V2_INSTALL</c>).</summary>
    public const string InstallV2Command = "npm uninstall -g opencode-ai && npm install -g @opencode/cli@2";

    /// <summary>What 1.0.6 tells the operator to run to put OpenCode V1 back before a rollback (migrations.py:3430).</summary>
    public const string PutV1BackCommand = "npm uninstall -g @opencode/cli && npm install -g opencode-ai@1.18";

    private const long MaxBytes = 2 * 1024 * 1024;
    private const int MaxVersionChars = 32;
    private const string ConnectorId = "opencode";

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    // connector_contracts.py: _VERSION_RE and _GIT_BUILD_RE.
    private static readonly Regex VersionNumber = new(@"(?:^|[^0-9])[vV]?(?<major>[0-9]+)", RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex GitBuild = new(@"(?:^|[^a-z0-9])v?git[.+-]?[0-9a-f]{7,40}(?:[^a-z0-9]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);

    /// <summary>The major version of a raw version text (<c>opencode v2.2.3</c> is 2), or null when it has none.</summary>
    public static int? MajorOf(string? rawVersion)
    {
        try
        {
            return VersionNumber.Match(rawVersion ?? string.Empty) is { Success: true } m && int.TryParse(m.Groups["major"].Value, out var major) ? major : null;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    /// <summary>True for a version 1.0.6 refuses (<c>opencode_requires_v2_upgrade</c>): a source build, or a readable version whose major is not 2.</summary>
    public static bool RequiresV2Upgrade(string? rawVersion)
    {
        try
        {
            return GitBuild.IsMatch(rawVersion ?? string.Empty) || MajorOf(rawVersion) is { } major && major != 2;
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>Whether OpenCode is in <c>config.yaml</c>'s active connectors. An unreadable config is not configured, as upstream has it (a managed install never gets this far: the app does not offer it these runs).</summary>
    public static bool IsConfigured(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataDirectory);
        var text = ReadText(Path.Combine(dataDirectory, "config.yaml"), ReadLimits.StateFileBytes);
        return text is not null && SetupStateLines.ActiveConnectors(ConfigYamlReader.Parse(text)).Contains(ConnectorId, StringComparer.Ordinal);
    }

    /// <summary>The whole reading. Nothing else is read for an install that does not guard OpenCode.</summary>
    /// <param name="dataDirectory">The DefenseClaw data directory.</param>
    /// <param name="npmPrefixes">The npm prefixes to look under; the account's own when null (<see cref="DefaultNpmPrefixes"/>).</param>
    /// <param name="exists">Whether a path is a file; the real file system when null.</param>
    public static OpenCodeInstallFacts Read(string dataDirectory, IReadOnlyList<string>? npmPrefixes = null, Func<string, bool>? exists = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataDirectory);
        if (!IsConfigured(dataDirectory))
        {
            return OpenCodeInstallFacts.NotConfigured;
        }

        var v2 = FindV2(dataDirectory, npmPrefixes, exists);
        return new OpenCodeInstallFacts(true, v2, v2 is null ? FindV1(dataDirectory, npmPrefixes, exists) : null);
    }

    /// <summary>The OpenCode V2 in the known places, or null: the program <c>agent_discovery.json</c> records, if it is still there; then the <c>@opencode/cli</c> package under the npm prefixes.</summary>
    public static OpenCodeInstall? FindV2(string dataDirectory, IReadOnlyList<string>? npmPrefixes = null, Func<string, bool>? exists = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataDirectory);
        if (Recorded(dataDirectory, exists ?? File.Exists) is { } recorded && MajorOf(recorded.Version) == 2)
        {
            return recorded;
        }

        return Package(npmPrefixes, ["@opencode", "cli"], static version => MajorOf(version) == 2);
    }

    /// <summary>The OpenCode V1 in the known places, or null: the program <c>agent_discovery.json</c> records, if it is still there; then the <c>opencode-ai</c> package under the npm prefixes.</summary>
    public static OpenCodeInstall? FindV1(string dataDirectory, IReadOnlyList<string>? npmPrefixes = null, Func<string, bool>? exists = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataDirectory);
        if (Recorded(dataDirectory, exists ?? File.Exists) is { } recorded && RequiresV2Upgrade(recorded.Version))
        {
            return recorded;
        }

        return Package(npmPrefixes, ["opencode-ai"], RequiresV2Upgrade);
    }

    /// <summary>The npm prefixes of this account on Windows, in upstream's order (the Unix ones dropped).</summary>
    public static IReadOnlyList<string> DefaultNpmPrefixes()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var prefixes = new List<string>();
        if (home.Length > 0)
        {
            prefixes.Add(Path.Combine(home, ".npm-global"));
            prefixes.Add(Path.Combine(home, ".local"));
            prefixes.Add(Path.Combine(home, "npm"));
        }

        if (appData.Length > 0)
        {
            prefixes.Add(Path.Combine(appData, "npm"));
        }

        return prefixes;
    }

    private static OpenCodeInstall? Recorded(string dataDirectory, Func<string, bool> exists)
    {
        var text = ReadText(Path.Combine(dataDirectory, "agent_discovery.json"), MaxBytes);
        if (text is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            if (Member(Member(document.RootElement, "agents"), ConnectorId) is not { ValueKind: JsonValueKind.Object } record ||
                Member(record, "installed") is not { ValueKind: JsonValueKind.True })
            {
                return null;
            }

            var binary = Text(record, "binary_path");
            var version = Text(record, "version");
            return binary.Length > 0 && version.Length > 0 && Exists(exists, binary) ? new OpenCodeInstall(Shown(version), binary) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static OpenCodeInstall? Package(IReadOnlyList<string>? npmPrefixes, string[] package, Func<string, bool> matches)
    {
        foreach (var prefix in npmPrefixes ?? DefaultNpmPrefixes())
        {
            foreach (var modules in new[] { Path.Combine("lib", "node_modules"), "node_modules" })
            {
                try
                {
                    var meta = Path.Combine([prefix, modules, .. package, "package.json"]);
                    var text = ReadText(meta, MaxBytes);
                    if (text is null)
                    {
                        continue;
                    }

                    using var document = JsonDocument.Parse(text);
                    var version = document.RootElement.ValueKind == JsonValueKind.Object ? Text(document.RootElement, "version") : string.Empty;
                    if (matches(version))
                    {
                        return new OpenCodeInstall(Shown(version), Path.GetDirectoryName(meta) ?? meta);
                    }
                }
                catch (Exception ex) when (ex is JsonException or ArgumentException)
                {
                    // Not a package file this can read: the next place.
                }
            }
        }

        return null;
    }

    private static string? ReadText(string path, long maxBytes)
    {
        try
        {
            return File.Exists(path) ? SharedFile.ReadAllText(path, maxBytes) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool Exists(Func<string, bool> exists, string path)
    {
        try
        {
            return exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static string Shown(string version)
    {
        var visible = DisplayNames.Visible(version.Trim());
        return visible.Length <= MaxVersionChars ? visible : visible[..MaxVersionChars];
    }

    private static JsonElement? Member(JsonElement? parent, string name)
    {
        if (parent is not { ValueKind: JsonValueKind.Object } element)
        {
            return null;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.Ordinal))
            {
                return property.Value;
            }
        }

        return null;
    }

    private static string Text(JsonElement parent, string name) =>
        Member(parent, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() ?? string.Empty : string.Empty;
}
