using DefenseClaw.Core.Paths;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.Tests.TestSupport;

/// <summary>
/// A whole synthetic PC for <see cref="InstallationContext.Resolve"/>: an environment, a developer runtime selection, and a disk that is a
/// dictionary. Nothing here touches the real machine, so a managed layout can be "installed" without a single write under Program Files or
/// ProgramData, and a developer machine that has <c>DEFENSECLAW_HOME</c> set cannot change a result.
/// </summary>
public sealed class InstallationMachine
{
    public const string UserProfile = @"C:\Users\synthetic";

    public const string ProgramFiles = @"C:\Program Files";

    public const string ProgramData = @"C:\ProgramData";

    /// <summary>Where a user default lives on this PC.</summary>
    public const string DefaultHome = UserProfile + @"\.defenseclaw";

    public static readonly string SecureClientState = ProgramData + @"\Cisco\Cisco Secure Client\DefenseClaw";

    public static readonly string SecureClientInstall = ProgramFiles + @"\Cisco\Cisco Secure Client\DefenseClaw";

    public static readonly string StandaloneState = ProgramData + @"\Cisco\DefenseClaw";

    public static readonly string StandaloneInstall = ProgramFiles + @"\Cisco\DefenseClaw";

    public Dictionary<string, string?> Environment { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Files that exist but cannot be read (an administrator-only ACL, a lock).</summary>
    public HashSet<string> Unreadable { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> Directories { get; } = new(StringComparer.OrdinalIgnoreCase);

    public RuntimeSelection Runtime { get; set; } = RuntimeSelection.Installed;

    /// <summary>Adds a file (and so its folders) with the text of a fixture under <c>Fixtures\installation</c>.</summary>
    public InstallationMachine WithFixture(string path, string fixture)
    {
        Files[path] = FixtureFiles.ReadText(@"installation\" + fixture);
        return this;
    }

    public InstallationMachine WithFile(string path, string text)
    {
        Files[path] = text;
        return this;
    }

    public InstallationMachine WithEnvironment(string name, string? value)
    {
        Environment[name] = value;
        return this;
    }

    /// <summary>The Cisco Secure Client managed layout: its state folder and its config, from the synthetic fixture.</summary>
    public InstallationMachine WithSecureClientLayout(string fixture = "managed-secureclient-config.yaml")
    {
        Directories.Add(SecureClientState);
        return WithFixture(SecureClientState + @"\etc\config.yaml", fixture);
    }

    /// <summary>The standalone-profile managed layout.</summary>
    public InstallationMachine WithStandaloneLayout(string fixture = "managed-standalone-config.yaml")
    {
        Directories.Add(StandaloneState);
        return WithFixture(StandaloneState + @"\etc\config.yaml", fixture);
    }

    public InstallationInputs Inputs => new()
    {
        Environment = name => Environment.TryGetValue(name, out var value) ? value : null,
        Runtime = Runtime,
        UserProfile = UserProfile,
        ProgramFiles = ProgramFiles,
        ProgramData = ProgramData,
        FileExists = path => Files.ContainsKey(path) || Unreadable.Contains(path),
        DirectoryExists = path => Directories.Contains(path) || Files.Keys.Any(f => f.StartsWith(path.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)),
        ReadText = path => Files.TryGetValue(path, out var text) ? text : null,
    };

    public InstallationContext Resolve() => InstallationContext.Resolve(Inputs);
}
