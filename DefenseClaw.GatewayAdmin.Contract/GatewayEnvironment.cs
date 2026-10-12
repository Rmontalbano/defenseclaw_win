namespace DefenseClaw.GatewayAdmin.Contract;

/// <summary>
/// The environment the elevated gateway is started with: built from scratch, never inherited. Nothing the unelevated app (or whatever started
/// it) had in its environment - tokens, API keys, proxy settings, <c>PATH</c> entries, loader or runtime switches - reaches an administrator
/// process. The gateway reads its own secrets from the user's <c>~\.defenseclaw\.env</c>, which <c>DEFENSECLAW_HOME</c> points it at.
/// </summary>
public static class GatewayEnvironment
{
    /// <summary>The variables the child gets and no others. A test pins this list.</summary>
    public static IReadOnlyList<string> Names { get; } = new[]
    {
        "SystemRoot", "windir", "SystemDrive", "ComSpec", "PATHEXT", "PATH",
        "USERPROFILE", "HOME", "HOMEDRIVE", "HOMEPATH", "USERNAME", "LOCALAPPDATA", "APPDATA", "TEMP", "TMP",
        "DEFENSECLAW_HOME", "NO_COLOR",
    };

    /// <summary>
    /// <paramref name="systemRoot"/> is the Windows folder (read from the system, not from an environment variable the caller could set);
    /// <paramref name="userName"/> is the account name of the invoking user.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Build(AdminLayout layout, string systemRoot, string userName)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrWhiteSpace(systemRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);

        var root = Path.TrimEndingDirectorySeparator(systemRoot);
        var profile = layout.ProfileDirectory;
        var drive = Path.GetPathRoot(profile) is { Length: >= 2 } r ? r[..2] : "C:";
        var homePath = profile.Length > 2 ? profile[2..] : @"\";
        var local = Path.Combine(profile, "AppData", "Local");
        var temp = Path.Combine(local, "Temp");

        // System folders first. The user's own .local\bin comes last, as on the Mac helper, so the gateway can find the scanners the installer
        // put there; it is the one place a user-writable folder is on the elevated PATH, and the doc says so ("like sudo, it trusts the account's installation").
        var path = string.Join(
            ';',
            Path.Combine(root, "System32"),
            root,
            Path.Combine(root, "System32", "Wbem"),
            Path.Combine(root, "System32", "WindowsPowerShell", "v1.0"),
            Path.Combine(profile, ".local", "bin"));

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemRoot"] = root,
            ["windir"] = root,
            ["SystemDrive"] = drive,
            ["ComSpec"] = Path.Combine(root, "System32", "cmd.exe"),
            ["PATHEXT"] = ".COM;.EXE;.BAT;.CMD",
            ["PATH"] = path,
            ["USERPROFILE"] = profile,
            ["HOME"] = profile,
            ["HOMEDRIVE"] = drive,
            ["HOMEPATH"] = homePath,
            ["USERNAME"] = userName,
            ["LOCALAPPDATA"] = local,
            ["APPDATA"] = Path.Combine(profile, "AppData", "Roaming"),
            ["TEMP"] = temp,
            ["TMP"] = temp,
            ["DEFENSECLAW_HOME"] = layout.DataDirectory,
            ["NO_COLOR"] = "1",
        };
    }

    /// <summary>
    /// Variables that make the .NET runtime or the loader run code the user chose, in an elevated process, before any of the helper's code runs.
    /// The helper refuses to run when one is set. (<c>DOTNET_STARTUP_HOOKS</c> is also switched off in the helper's runtime configuration.)
    /// </summary>
    public static IReadOnlyList<string> DangerousPrefixes { get; } = new[]
    {
        "DOTNET_STARTUP_HOOKS", "DOTNET_ADDITIONAL_DEPS", "DOTNET_SHARED_STORE", "CORECLR_", "COR_", "COMPlus_",
    };

    /// <summary>The names in <paramref name="environment"/> that <see cref="DangerousPrefixes"/> forbids.</summary>
    public static IReadOnlyList<string> DangerousVariables(IEnumerable<string> environmentNames)
    {
        ArgumentNullException.ThrowIfNull(environmentNames);

        return environmentNames
            .Where(name => DangerousPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(static n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
