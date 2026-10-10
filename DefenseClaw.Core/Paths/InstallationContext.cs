using System.Text.Json;
using DefenseClaw.Core.IO;
using DefenseClaw.Core.Runtime;
using DefenseClaw.Core.Text;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace DefenseClaw.Core.Paths;

/// <summary>Which rung of the precedence ladder chose the installation the app drives.</summary>
public enum InstallationSource
{
    /// <summary><c>DEFENSECLAW_CONFIG</c>: the config.yaml the CLI itself would read (the 0.8.10 CLI and the pinned source both honour it).</summary>
    EnvironmentConfig,

    /// <summary>
    /// The app's own override: the developer runtime selector (Settings → Advanced), the Windows analogue of the Mac app's "config path override".
    /// It applies at the next start, like the selector does.
    /// </summary>
    AppOverride,

    /// <summary><c>DEFENSECLAW_HOME</c>, exactly as the CLI reads it.</summary>
    EnvironmentHome,

    /// <summary>The Windows managed (enterprise) layout the pinned installer lays down: an install root under Program Files and a state root under ProgramData.</summary>
    ManagedLayout,

    /// <summary><c>%USERPROFILE%\.defenseclaw</c>: nothing selected anything else.</summary>
    UserDefault,
}

/// <summary>What the app may do to the selected installation.</summary>
public enum InstallationAccess
{
    /// <summary>An ordinary, user-owned installation: every reviewed change is allowed.</summary>
    UnmanagedMutable,

    /// <summary>An administrator-managed installation (<c>deployment_mode: managed_enterprise</c>, or a managed layout): the app only reads it.</summary>
    ManagedReadOnly,

    /// <summary>The selection is contradictory or unusable (a relative path, a missing config, a managed layout the selection disagrees with): the app only reads it.</summary>
    InvalidReadOnly,
}

/// <summary>
/// One Windows managed (enterprise) DefenseClaw layout, as the pinned installer (<c>packaging/windows/install-enterprise.ps1</c>, source commit
/// 95159fd) lays it down: <c>&lt;ProgramFiles&gt;\&lt;vendor&gt;\DefenseClaw</c> holds the programs, <c>&lt;ProgramData&gt;\&lt;vendor&gt;\DefenseClaw</c> the
/// administrator-owned state, and <c>&lt;vendor&gt;</c> is <c>Cisco\Cisco Secure Client</c> or <c>Cisco</c> (the standalone profile). The two profiles are
/// mutually exclusive on one PC. Pure data: nothing here touches the disk.
/// </summary>
/// <param name="Profile"><see cref="ManagedProfiles.SecureClient"/> or <see cref="ManagedProfiles.Standalone"/>.</param>
/// <param name="InstallRoot">The program folder (<c>bin</c> and <c>libexec</c> live in it).</param>
/// <param name="StateRoot">The state folder (<c>etc</c>, <c>runtime</c>, <c>install</c> and <c>logs</c> live in it).</param>
public sealed record ManagedLayout(string Profile, string InstallRoot, string StateRoot)
{
    /// <summary>The managed config.yaml: <c>&lt;StateRoot&gt;\etc\config.yaml</c>, the <c>DEFENSECLAW_CONFIG</c> the managed services are pinned to.</summary>
    public string ConfigPath => Path.Combine(StateRoot, "etc", "config.yaml");

    /// <summary>The managed data directory: <c>&lt;StateRoot&gt;\runtime</c>, the <c>DEFENSECLAW_HOME</c> the managed services are pinned to.</summary>
    public string RuntimeDirectory => Path.Combine(StateRoot, "runtime");

    /// <summary>The lifecycle's own record of the deployment (<c>installed</c>, <c>product_version</c>): <c>&lt;StateRoot&gt;\install\deployment.json</c>.</summary>
    public string MetadataPath => Path.Combine(StateRoot, "install", "deployment.json");

    /// <summary>Where the managed programs live: <c>&lt;InstallRoot&gt;\bin</c>.</summary>
    public string BinDirectory => Path.Combine(InstallRoot, "bin");

    /// <summary>The managed <c>defenseclaw.exe</c>.</summary>
    public string CliPath => Path.Combine(BinDirectory, DefenseClawPaths.CliExecutableName + ".exe");

    /// <summary>"Cisco Secure Client" or "standalone", for a sentence.</summary>
    public string ProfileLabel => Profile == ManagedProfiles.Standalone ? "standalone" : "Cisco Secure Client";

    /// <summary>The layout of <paramref name="profile"/> under the given machine roots.</summary>
    public static ManagedLayout For(string profile, string programFiles, string programData)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(programFiles);
        ArgumentException.ThrowIfNullOrWhiteSpace(programData);

        var vendor = profile == ManagedProfiles.Standalone ? "Cisco" : Path.Combine("Cisco", "Cisco Secure Client");
        return new ManagedLayout(
            profile,
            Path.Combine(programFiles, vendor, "DefenseClaw"),
            Path.Combine(programData, vendor, "DefenseClaw"));
    }
}

/// <summary>The two Windows enterprise profiles (<c>internal/managed/profile.go</c> of the pinned source).</summary>
public static class ManagedProfiles
{
    public const string SecureClient = "secure_client";

    public const string Standalone = "standalone";
}

/// <summary>
/// Everything <see cref="InstallationContext.Resolve"/> looks at, so a test can hand it a whole machine: the environment, the developer
/// runtime selection, the roots a managed layout would live under, and the disk. The defaults are the real ones.
/// </summary>
public sealed record InstallationInputs
{
    /// <summary>The environment reader. The process environment by default.</summary>
    public Func<string, string?> Environment { get; init; } = System.Environment.GetEnvironmentVariable;

    /// <summary>The runtime the developer selector chose (the effective one: an incomplete choice is already <see cref="RuntimeSelection.Installed"/>). Not default means the app override is on.</summary>
    public RuntimeSelection Runtime { get; init; } = RuntimeSelection.Installed;

    /// <summary>The profile folder the user default hangs off.</summary>
    public string UserProfile { get; init; } = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);

    /// <summary>Program Files, where a managed install root would be; null looks for no managed layout.</summary>
    public string? ProgramFiles { get; init; } = System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFiles);

    /// <summary>ProgramData, where a managed state root would be; null looks for no managed layout.</summary>
    public string? ProgramData { get; init; } = System.Environment.GetFolderPath(System.Environment.SpecialFolder.CommonApplicationData);

    public Func<string, bool> FileExists { get; init; } = File.Exists;

    public Func<string, bool> DirectoryExists { get; init; } = Directory.Exists;

    /// <summary>A file's text, or null when it is not there or cannot be read. Never throws.</summary>
    public Func<string, string?> ReadText { get; init; } = InstallationContext.ReadConfigText;
}

/// <summary>
/// The one DefenseClaw installation this run of the app drives, resolved once at start, and what the app may do to it. This is the Windows
/// port of the Mac app's <c>InstallationContext</c> (v1.1.26), limited to what DefenseClaw 0.8.10 and the pinned source (commit 95159fd)
/// actually honour.
/// <para>
/// <b>Precedence</b>, highest first: <c>DEFENSECLAW_CONFIG</c> &gt; the app override (the developer runtime selector, which carries its own
/// <c>DEFENSECLAW_HOME</c>) &gt; <c>DEFENSECLAW_HOME</c> &gt; the managed layout &gt; the user default. The 0.8.10 CLI reads <c>DEFENSECLAW_CONFIG</c>
/// (<c>config.py</c>: <c>config_path()</c>, authoritative over the data directory), <c>DEFENSECLAW_HOME</c> (<c>default_data_path()</c>) and
/// <c>DEFENSECLAW_DEPLOYMENT_MODE</c> (<c>_assert_config_write_allowed</c>), and so do the pinned gateway and CLI, so all three are claimed. The
/// <c>DEFENSECLAW_CONFIG</c> and <c>DEFENSECLAW_HOME</c> values must be full Windows paths (a drive letter or a network share): the CLI accepts a
/// relative one and resolves it against its own working directory, which is not this app's, so a relative value would mean two different
/// installations. When both <c>DEFENSECLAW_CONFIG</c> and a developer selection are present, the environment wins the label and the selection
/// must name the same folder or the installation is invalid.
/// </para>
/// <para>
/// <b>Access</b> is <see cref="InstallationAccess.UnmanagedMutable"/> unless something says otherwise. It is
/// <see cref="InstallationAccess.ManagedReadOnly"/> when the config says <c>deployment_mode: managed_enterprise</c> (<c>managed</c> is an alias,
/// as in the CLI), <c>DEFENSECLAW_DEPLOYMENT_MODE</c> pins that, the selected config is the managed one, or a Windows managed layout is present on
/// the PC. It is <see cref="InstallationAccess.InvalidReadOnly"/> when the selection cannot be trusted: a path that is not absolute, an explicitly
/// selected config that is missing, unreadable or not YAML, a <c>deployment_mode</c> the CLI would refuse, a pin that disagrees with the config, or a
/// managed layout that the (non-managed) selection contradicts. Every read-only classification carries a one-sentence <see cref="Reason"/> that
/// every surface shows. A read-only installation is never writable by accident: the one gate is <c>CliRunner</c> (see <c>InstallationGate</c>).
/// </para>
/// <para>
/// <b>Managed layout detection.</b> A layout counts when its state root or its <c>bin\defenseclaw.exe</c> exists (the state root's own ACL may
/// hide its files from a standard user, so the folder is the evidence, not the files in it) and its <c>install\deployment.json</c> does not say
/// <c>installed: false</c> (an uninstall's tombstone; an unreadable record counts as installed, as the pinned lifecycle treats it). Both profiles at
/// once is invalid: the pinned installer refuses that too.
/// </para>
/// <para>
/// <b>Protected environment.</b> The three variables that define an installation (<see cref="ProtectedVariables"/>) are applied last to every
/// DefenseClaw child, so a call's own environment cannot move it to another installation. A variable the selection pins is set; one it does not
/// pin keeps the value the app itself would pass on.
/// </para>
/// </summary>
public sealed record InstallationContext
{
    public const string ConfigVariable = "DEFENSECLAW_CONFIG";

    public const string HomeVariable = DefenseClawPaths.HomeVariableName;

    public const string DeploymentModeVariable = "DEFENSECLAW_DEPLOYMENT_MODE";

    /// <summary>The value <c>DEFENSECLAW_DEPLOYMENT_MODE</c> is pinned to for a managed installation.</summary>
    public const string ManagedEnterprise = "managed_enterprise";

    /// <summary>The config.yaml read cap: far past any real configuration, so a runaway file cannot be turned into a read.</summary>
    public const long MaxConfigBytes = 2 * 1024 * 1024;

    /// <summary>The variables <see cref="ProtectedEnvironment"/> speaks for.</summary>
    public static IReadOnlyList<string> ProtectedVariables { get; } = new[] { HomeVariable, ConfigVariable, DeploymentModeVariable };

    public required InstallationSource Source { get; init; }

    public required InstallationAccess Access { get; init; }

    /// <summary>One sentence on why the installation is read-only; null while it is <see cref="InstallationAccess.UnmanagedMutable"/>.</summary>
    public string? Reason { get; init; }

    /// <summary>The <c>DEFENSECLAW_HOME</c> of the selected installation: its data directory.</summary>
    public required string HomeRoot { get; init; }

    /// <summary>The selected installation's config.yaml.</summary>
    public required string ConfigPath { get; init; }

    /// <summary>The value <c>DEFENSECLAW_HOME</c> is pinned to for DefenseClaw children; null when the selection does not pin it (the CLI's own default applies).</summary>
    public string? PinnedHome { get; init; }

    /// <summary>The value <c>DEFENSECLAW_CONFIG</c> is pinned to for DefenseClaw children; null when the selection does not pin it.</summary>
    public string? PinnedConfig { get; init; }

    /// <summary>The developer selector's folder when it is the app override (the "override path" Settings shows); null otherwise.</summary>
    public string? OverridePath { get; init; }

    /// <summary>True when the developer runtime selector is driving a runtime other than the one Setup installed (a side-by-side CLI, or a container).</summary>
    public bool UsesDeveloperRuntime { get; init; }

    /// <summary>The config's <c>deployment_mode</c>, normalised (<c>managed</c> is <c>managed_enterprise</c>); null when the config names none or it is not usable.</summary>
    public string? DeploymentMode { get; init; }

    /// <summary>The managed layout found on this PC, if exactly one was; null otherwise.</summary>
    public ManagedLayout? Layout { get; init; }

    public bool IsMutable => Access == InstallationAccess.UnmanagedMutable;

    public bool IsManaged => Access == InstallationAccess.ManagedReadOnly;

    public bool IsInvalid => Access == InstallationAccess.InvalidReadOnly;

    /// <summary>Why state-changing actions are off, as a sentence for a banner or a tooltip; null while the installation is writable.</summary>
    public string? BlockedReason => IsMutable ? null : Reason is { Length: > 0 } reason ? reason : "This installation is read only.";

    /// <summary>What the Installation block shows after "Selected by".</summary>
    public string SourceLabel => Source switch
    {
        InstallationSource.EnvironmentConfig => ConfigVariable,
        InstallationSource.AppOverride => "Developer runtime selector (Settings → Advanced)",
        InstallationSource.EnvironmentHome => HomeVariable,
        InstallationSource.ManagedLayout => Layout is { } layout ? $"Managed installation ({layout.ProfileLabel})" : "Managed installation",
        _ => "User default",
    };

    /// <summary>What the Installation block shows after "Access".</summary>
    public string AccessLabel => Access switch
    {
        InstallationAccess.ManagedReadOnly => "Managed enterprise — read only",
        InstallationAccess.InvalidReadOnly => "Invalid installation selection — read only",
        _ => "Unmanaged — setup changes allowed",
    };

    /// <summary>One line: "User default · Unmanaged — setup changes allowed".</summary>
    public string Describe() => SourceLabel + " · " + AccessLabel;

    /// <summary>
    /// What a DefenseClaw child is given for each variable in <see cref="ProtectedVariables"/>, applied after the call's own environment: a value
    /// to set, or null for "the value this app itself would pass on" (the runner restores it). Always three entries.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, string?>> ProtectedEnvironment() => new[]
    {
        new KeyValuePair<string, string?>(HomeVariable, PinnedHome),
        new KeyValuePair<string, string?>(ConfigVariable, PinnedConfig),
        new KeyValuePair<string, string?>(DeploymentModeVariable, IsManaged ? ManagedEnterprise : null),
    };

    /// <summary>
    /// The permissive context of a composition nobody resolved an installation for (tests, harnesses): the data directory is as given, the
    /// installation is user-owned and writable, and nothing is pinned, so the runner's behaviour is exactly what it was before this type existed.
    /// </summary>
    public static InstallationContext Unmanaged(string homeRoot, string? configPath = null) => new()
    {
        Source = InstallationSource.UserDefault,
        Access = InstallationAccess.UnmanagedMutable,
        HomeRoot = homeRoot,
        ConfigPath = configPath ?? Path.Combine(homeRoot, "config.yaml"),
    };

    // ------------------------------------------------------------------------------------------------------------ resolution

    /// <summary>
    /// Resolves the installation. Pure apart from reading <paramref name="inputs"/>' disk functions; never throws, whatever is on disk. Called once
    /// per start (and again when config.yaml changes, so a file fixed or edited to managed is noticed without a restart).
    /// </summary>
    public static InstallationContext Resolve(InstallationInputs? inputs = null)
    {
        inputs ??= new InstallationInputs();

        var environment = inputs.Environment;
        var defaultHome = Path.Combine(inputs.UserProfile, ".defenseclaw");
        string? invalid = null;

        var layouts = DetectLayouts(inputs);
        var layout = layouts.Count == 1 ? layouts[0] : null;
        if (layouts.Count > 1)
        {
            invalid = "Both a Cisco Secure Client and a standalone managed DefenseClaw installation are present; DefenseClaw supports only one at a time.";
        }

        var rawConfig = NonBlank(environment(ConfigVariable));
        var rawHome = NonBlank(environment(HomeVariable));
        var rawMode = NonBlank(environment(DeploymentModeVariable));
        var selection = inputs.Runtime ?? RuntimeSelection.Installed;
        var developerFolder = selection.IsDefault ? null : selection.DataDirectoryOverride;

        InstallationSource source;
        string home;
        string config;
        string? pinnedHome = null;
        string? pinnedConfig = null;
        string? overridePath = null;

        if (rawConfig is not null)
        {
            source = InstallationSource.EnvironmentConfig;
            if (TryFullPath(rawConfig, out var fullConfig))
            {
                config = fullConfig;
            }
            else
            {
                invalid ??= $"{ConfigVariable} must be an absolute path (a drive letter or a network share).";
                config = BestEffortFullPath(rawConfig);
            }

            pinnedConfig = config;

            if (rawHome is null)
            {
                // A config that is a managed layout's own was written for that layout's runtime folder, not for the user's profile.
                home = layouts.FirstOrDefault(l => SamePath(l.ConfigPath, config))?.RuntimeDirectory ?? defaultHome;
                pinnedHome = SamePath(home, defaultHome) ? null : home;
            }
            else if (TryFullPath(rawHome, out var fullHome))
            {
                home = fullHome;
                pinnedHome = home;
            }
            else
            {
                invalid ??= $"{HomeVariable} must be an absolute path (a drive letter or a network share).";
                home = BestEffortFullPath(rawHome);
                pinnedHome = home;
            }

            // Two explicit selections that name different folders are not a precedence question: running a side-by-side CLI against the
            // installed runtime's files is exactly what the developer selector refuses elsewhere.
            if (developerFolder is not null &&
                (!SamePath(home, developerFolder) || !SamePath(config, Path.Combine(developerFolder, "config.yaml"))))
            {
                invalid ??= $"{ConfigVariable} is set and the developer runtime selector names a different folder; remove one of them.";
            }
        }
        else if (developerFolder is not null)
        {
            source = InstallationSource.AppOverride;
            if (TryFullPath(developerFolder, out var fullFolder))
            {
                home = fullFolder;
            }
            else
            {
                invalid ??= "The developer runtime's folder must be an absolute path (a drive letter or a network share).";
                home = BestEffortFullPath(developerFolder);
            }

            config = Path.Combine(home, "config.yaml");
            pinnedHome = home;
            overridePath = home;
        }
        else if (rawHome is not null)
        {
            source = InstallationSource.EnvironmentHome;
            if (TryFullPath(rawHome, out var fullHome))
            {
                home = fullHome;
            }
            else
            {
                invalid ??= $"{HomeVariable} must be an absolute path (a drive letter or a network share).";
                home = BestEffortFullPath(rawHome);
            }

            config = Path.Combine(home, "config.yaml");
            pinnedHome = home;
        }
        else if (layouts.Count > 0)
        {
            source = InstallationSource.ManagedLayout;
            var chosen = layouts[0];
            home = chosen.RuntimeDirectory;
            config = chosen.ConfigPath;
            pinnedHome = home;
            pinnedConfig = config;
        }
        else
        {
            source = InstallationSource.UserDefault;
            home = defaultHome;
            config = Path.Combine(home, "config.yaml");
        }

        var configIsManagedFile = layouts.Any(l => SamePath(l.ConfigPath, config));

        // ---- the config itself
        var exists = inputs.FileExists(config);
        var text = exists ? inputs.ReadText(config) : null;
        if (rawConfig is not null && !exists)
        {
            invalid ??= "The config.yaml that DEFENSECLAW_CONFIG names does not exist.";
        }
        else if (exists && text is null && source != InstallationSource.ManagedLayout && !configIsManagedFile)
        {
            invalid ??= "config.yaml exists but could not be read, so what it says about this installation is unknown.";
        }

        var facts = ConfigFacts.Read(text);
        invalid ??= facts.Problem;

        var configMode = facts.DeploymentMode is null ? null : NormalizeDeploymentMode(facts.DeploymentMode);
        if (facts.DeploymentMode is not null && configMode is null)
        {
            invalid ??= $"config.yaml deployment_mode has an unsupported value: {DisplayNames.Visible(facts.DeploymentMode)}.";
        }

        var pinnedMode = rawMode is null ? null : NormalizeDeploymentMode(rawMode);
        if (rawMode is not null && pinnedMode is null)
        {
            invalid ??= $"{DeploymentModeVariable} has an unsupported value: {DisplayNames.Visible(rawMode)}.";
        }

        if (pinnedMode is not null && configMode is not null && pinnedMode != configMode)
        {
            invalid ??= $"{DeploymentModeVariable} conflicts with the deployment_mode in config.yaml.";
        }

        // ---- a managed layout the selection contradicts
        var layoutPresent = layouts.Count > 0;
        if (layoutPresent && source != InstallationSource.ManagedLayout && !configIsManagedFile &&
            (configMode != ManagedEnterprise || (pinnedMode is not null && pinnedMode != ManagedEnterprise)))
        {
            invalid ??= "A managed (enterprise) DefenseClaw installation is present on this PC and conflicts with the selected non-managed installation.";
        }

        var managed = source == InstallationSource.ManagedLayout || configIsManagedFile || layoutPresent ||
                      pinnedMode == ManagedEnterprise || configMode == ManagedEnterprise;

        InstallationAccess access;
        string? reason;
        if (invalid is not null)
        {
            access = InstallationAccess.InvalidReadOnly;
            reason = invalid;
        }
        else if (managed)
        {
            access = InstallationAccess.ManagedReadOnly;
            reason = text is null && (source == InstallationSource.ManagedLayout || configIsManagedFile)
                ? "The administrator-owned managed config could not be read, so state-changing actions stay off."
                : "This installation is administrator managed. Use enterprise deployment tooling to change it.";
        }
        else
        {
            access = InstallationAccess.UnmanagedMutable;
            reason = null;
        }

        return new InstallationContext
        {
            Source = source,
            Access = access,
            Reason = reason,
            HomeRoot = home,
            ConfigPath = config,
            PinnedHome = pinnedHome,
            PinnedConfig = pinnedConfig,
            OverridePath = overridePath,
            UsesDeveloperRuntime = !selection.IsDefault,
            DeploymentMode = configMode,
            Layout = layout ?? (source == InstallationSource.ManagedLayout && layouts.Count > 0 ? layouts[0] : null),
        };
    }

    /// <summary>
    /// <c>deployment_mode</c> as the CLI reads it: trimmed, the legacy words mapped (<c>managed</c> is <c>managed_enterprise</c>, <c>standalone</c> is
    /// <c>unmanaged_byod</c>, <c>ci</c> is <c>ci_cd</c>, <c>edge</c> is <c>server</c>), then one of the six modes. Case does not matter here (the
    /// Go side compares without case, and being stricter than either only ever makes an installation read-only). Null when the value is not a mode.
    /// </summary>
    public static string? NormalizeDeploymentMode(string? raw)
    {
        var value = raw?.Trim().ToLowerInvariant();
        return value switch
        {
            "managed" or "managed_enterprise" => ManagedEnterprise,
            "standalone" or "unmanaged_byod" => "unmanaged_byod",
            "ci" or "ci_cd" => "ci_cd",
            "edge" or "server" => "server",
            "sandboxed" => "sandboxed",
            "saas" => "saas",
            _ => null,
        };
    }

    /// <summary>True for a full Windows path: a drive letter or a network share, not <c>C:name</c>, <c>\name</c> or <c>name</c>.</summary>
    public static bool IsAbsoluteWindowsPath(string? path) => TryFullPath(path, out _);

    // ------------------------------------------------------------------------------------------------------------ disk and paths

    /// <summary>
    /// The production file reader: the whole file as text (read/write/delete sharing, like every other read of a file the CLI rewrites), one retry
    /// for a momentary sharing violation, null for a missing, unreadable or oversized file. Never throws.
    /// </summary>
    public static string? ReadConfigText(string path)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                if (new FileInfo(path) is not { Exists: true } info || info.Length > MaxConfigBytes)
                {
                    return null;
                }

                return SharedFile.ReadAllText(path);
            }
            catch (IOException) when (attempt == 0)
            {
                Thread.Sleep(50);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException)
            {
                return null;
            }
        }

        return null;
    }

    private static string? NonBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool TryFullPath(string? value, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\0', StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            if (!Path.IsPathFullyQualified(value))
            {
                return false;
            }

            var full = Path.GetFullPath(value);
            fullPath = Path.GetPathRoot(full) == full ? full : Path.TrimEndingDirectorySeparator(full);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>What a value that failed <see cref="TryFullPath"/> would mean to the CLI, resolved the way .NET does, so a read-only installation still names a folder.</summary>
    private static string BestEffortFullPath(string value)
    {
        try
        {
            return Path.GetFullPath(value);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            return value;
        }
    }

    private static bool SamePath(string? left, string? right)
    {
        if (left is null || right is null)
        {
            return false;
        }

        var a = TryFullPath(left, out var fullLeft) ? fullLeft : left.TrimEnd('\\', '/');
        var b = TryFullPath(right, out var fullRight) ? fullRight : right.TrimEnd('\\', '/');
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private static List<ManagedLayout> DetectLayouts(InstallationInputs inputs)
    {
        var found = new List<ManagedLayout>();
        if (!TryFullPath(inputs.ProgramFiles, out var programFiles) || !TryFullPath(inputs.ProgramData, out var programData))
        {
            return found;
        }

        foreach (var profile in new[] { ManagedProfiles.SecureClient, ManagedProfiles.Standalone })
        {
            var layout = ManagedLayout.For(profile, programFiles, programData);
            var present = inputs.DirectoryExists(layout.StateRoot) ||
                          inputs.FileExists(layout.CliPath) ||
                          inputs.FileExists(layout.ConfigPath) ||
                          inputs.FileExists(layout.MetadataPath);
            if (present && !IsTombstone(inputs, layout))
            {
                found.Add(layout);
            }
        }

        return found;
    }

    /// <summary>True when the lifecycle's record says the deployment was uninstalled (<c>installed: false</c>). A record that is missing, unreadable or not JSON says nothing, so the layout stays.</summary>
    private static bool IsTombstone(InstallationInputs inputs, ManagedLayout layout)
    {
        var text = inputs.FileExists(layout.MetadataPath) ? inputs.ReadText(layout.MetadataPath) : null;
        if (text is null)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(text.TrimStart('﻿'));
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("installed", out var installed) &&
                   installed.ValueKind == JsonValueKind.False;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // ------------------------------------------------------------------------------------------------------------ config.yaml

    /// <summary>What the resolution needs from config.yaml: one problem sentence if it cannot be used, and the raw <c>deployment_mode</c>.</summary>
    private sealed record ConfigFacts(string? Problem, string? DeploymentMode)
    {
        public static readonly ConfigFacts Empty = new(null, null);

        public static ConfigFacts Read(string? text)
        {
            if (text is null)
            {
                return Empty;
            }

            if (DefenseClaw.Core.Config.ConfigYamlGuard.Refusal(text) is { } refusal)
            {
                return new ConfigFacts(refusal, null);
            }

            var stream = new YamlStream();
            try
            {
                stream.Load(new StringReader(text));
            }
            catch (YamlException ex)
            {
                return new ConfigFacts($"config.yaml is not valid YAML: {FirstLine(ex.Message)}", null);
            }

            // Comments or nothing at all: the CLI reads that as an empty mapping.
            if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is YamlScalarNode { Value: null or "" or "~" or "null" })
            {
                return Empty;
            }

            if (stream.Documents[0].RootNode is not YamlMappingNode root)
            {
                return new ConfigFacts("config.yaml must contain a top-level mapping.", null);
            }

            string? mode = null;
            if (Child(root, "deployment_mode") is { } modeNode)
            {
                if (modeNode is not YamlScalarNode modeScalar)
                {
                    return new ConfigFacts("config.yaml deployment_mode must be a string.", null);
                }

                mode = NonBlank(modeScalar.Value);
            }

            if (Child(root, "data_dir") is { } dataDir && AbsolutePathProblem(dataDir, "data_dir") is { } dataDirProblem)
            {
                return new ConfigFacts(dataDirProblem, mode);
            }

            if (Child(root, "observability") is { } observability)
            {
                if (observability is not YamlMappingNode observabilityMap)
                {
                    return new ConfigFacts("config.yaml observability must be a mapping.", mode);
                }

                if (Child(observabilityMap, "local") is { } local)
                {
                    if (local is not YamlMappingNode localMap)
                    {
                        return new ConfigFacts("config.yaml observability.local must be a mapping.", mode);
                    }

                    if (Child(localMap, "path") is { } path && AbsolutePathProblem(path, "observability.local.path") is { } pathProblem)
                    {
                        return new ConfigFacts(pathProblem, mode);
                    }
                }
            }

            return new ConfigFacts(null, mode);
        }

        /// <summary>The value under a top-level key; null for an absent key or an empty (null) value, which the CLI reads as absent.</summary>
        private static YamlNode? Child(YamlMappingNode map, string key)
        {
            foreach (var (name, value) in map.Children)
            {
                if (name is YamlScalarNode { Value: { } text } && string.Equals(text, key, StringComparison.Ordinal))
                {
                    return value is YamlScalarNode { Value: null or "" or "~" or "null" } ? null : value;
                }
            }

            return null;
        }

        private static string? AbsolutePathProblem(YamlNode node, string key)
        {
            if (node is not YamlScalarNode scalar)
            {
                return $"config.yaml {key} must be a string.";
            }

            // A leading ~ is the user's profile, which both the CLI and the app expand; anything else must be a full path.
            var value = scalar.Value?.Trim();
            if (string.IsNullOrEmpty(value) || value == "~" || value.StartsWith("~/", StringComparison.Ordinal) || value.StartsWith("~\\", StringComparison.Ordinal))
            {
                return null;
            }

            return TryFullPath(value, out _) ? null : $"config.yaml {key} must be an absolute path.";
        }

        private static string FirstLine(string message)
        {
            var line = message.Split('\n', 2)[0].Trim();
            return DisplayNames.Visible(line.Length > 160 ? line[..160] : line);
        }
    }
}
