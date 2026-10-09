using DefenseClaw.Core.Paths;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.Tests.TestSupport;

/// <summary>
/// Synthetic installations (CUST-308) for the tests that need the app to be read-only (or not). Each one is what
/// <see cref="InstallationContext.Resolve"/> makes of a whole made-up PC - an environment, a developer selection, a disk that is a dictionary - so
/// the context carries the real labels and sentences, and nothing is read from, or written to, Program Files, ProgramData or the real profile. A
/// managed layout is "installed" by listing a folder and a config in the dictionary, never by creating one.
/// </summary>
internal static class TestInstallations
{
    public const string UserProfile = @"C:\Users\synthetic";

    public const string ProgramFiles = @"C:\Program Files";

    public const string ProgramData = @"C:\ProgramData";

    public const string DefaultHome = UserProfile + @"\.defenseclaw";

    /// <summary>The side-by-side runtime folder the developer selector names in <see cref="DeveloperRuntime"/>.</summary>
    public const string DeveloperHome = @"C:\Synthetic\dev-runtime";

    /// <summary>The sentence a managed installation gives for every disabled control.</summary>
    public const string ManagedReason = "This installation is administrator managed. Use enterprise deployment tooling to change it.";

    private sealed class Machine
    {
        public Dictionary<string, string?> Environment { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> Directories { get; } = new(StringComparer.OrdinalIgnoreCase);

        public RuntimeSelection Runtime { get; set; } = RuntimeSelection.Installed;

        public string ProgramFilesRoot { get; set; } = ProgramFiles;

        public string ProgramDataRoot { get; set; } = ProgramData;

        public InstallationContext Resolve() => InstallationContext.Resolve(new InstallationInputs
        {
            Environment = name => Environment.TryGetValue(name, out var value) ? value : null,
            Runtime = Runtime,
            UserProfile = UserProfile,
            ProgramFiles = ProgramFilesRoot,
            ProgramData = ProgramDataRoot,
            FileExists = path => Files.ContainsKey(path),
            DirectoryExists = path => Directories.Contains(path) || Files.Keys.Any(f => f.StartsWith(path.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)),
            ReadText = path => Files.TryGetValue(path, out var text) ? text : null,
        });
    }

    /// <summary>A PC with no managed layout and nothing selected: the user's own <c>.defenseclaw</c>, which the app may change.</summary>
    public static InstallationContext UserDefault() => new Machine().Resolve();

    /// <summary>
    /// The Cisco Secure Client managed layout is on the PC (its state folder and its administrator-owned config), and nothing else is selected:
    /// managed, read only.
    /// </summary>
    /// <param name="root">
    /// Null puts the layout under the made-up Program Files and ProgramData of this class. A test that builds a whole composition over the context
    /// passes its scratch folder instead, so that even a read of the managed config.yaml stays inside it.
    /// </param>
    public static InstallationContext Managed(string? root = null)
    {
        var programFiles = root is null ? ProgramFiles : System.IO.Path.Combine(root, "ProgramFiles");
        var programData = root is null ? ProgramData : System.IO.Path.Combine(root, "ProgramData");
        var state = System.IO.Path.Combine(programData, "Cisco", "Cisco Secure Client", "DefenseClaw");
        var machine = new Machine { ProgramFilesRoot = programFiles, ProgramDataRoot = programData };
        _ = machine.Directories.Add(state);
        machine.Files[System.IO.Path.Combine(state, "etc", "config.yaml")] = "config_version: 8\ndeployment_mode: managed_enterprise\n";
        return machine.Resolve();
    }

    /// <summary>A user-owned config that says <c>deployment_mode: managed</c> (the CLI's alias for managed_enterprise), selected by <c>DEFENSECLAW_HOME</c>: managed, read only.</summary>
    public static InstallationContext ManagedByConfigMode() => ManagedAt(@"C:\Synthetic\home");

    /// <summary>
    /// <see cref="ManagedByConfigMode"/> at <paramref name="home"/>: the data folder and the config.yaml it names are the ones an isolated composition
    /// over that folder (a test's scratch directory) already uses, so a control that reads the config and the guard that refuses it agree.
    /// </summary>
    public static InstallationContext ManagedAt(string home)
    {
        var machine = new Machine();
        machine.Environment["DEFENSECLAW_HOME"] = home;
        machine.Files[home.TrimEnd('\\') + @"\config.yaml"] = "config_version: 8\ndeployment_mode: managed\n";
        return machine.Resolve();
    }

    /// <summary><c>DEFENSECLAW_HOME</c> is a relative path, which the CLI would resolve against a folder the app does not know: invalid, read only.</summary>
    public static InstallationContext Invalid()
    {
        var machine = new Machine();
        machine.Environment["DEFENSECLAW_HOME"] = @"relative\folder";
        return machine.Resolve();
    }

    /// <summary>
    /// A config.yaml at <paramref name="home"/> whose <c>deployment_mode</c> the CLI would refuse, selected by <c>DEFENSECLAW_HOME</c>: invalid, read
    /// only, with the folder agreeing with an isolated composition over it (see <see cref="ManagedAt"/>).
    /// </summary>
    public static InstallationContext InvalidAt(string home)
    {
        var machine = new Machine();
        machine.Environment["DEFENSECLAW_HOME"] = home;
        machine.Files[home.TrimEnd('\\') + @"\config.yaml"] = "config_version: 8\ndeployment_mode: not_a_mode\n";
        return machine.Resolve();
    }

    /// <summary>
    /// The developer runtime selector (Settings → Advanced) drives a side-by-side CLI against its own folder: writable, but not the runtime Setup
    /// installed, so the runtime is not upgraded from here.
    /// </summary>
    public static InstallationContext DeveloperRuntime()
    {
        var machine = new Machine { Runtime = RuntimeSelection.ForCli(@"C:\Synthetic\dev\defenseclaw.exe", DeveloperHome, null) };
        return machine.Resolve();
    }

    /// <summary>The same developer runtime selection, as the runtime the guard was started with.</summary>
    public static RuntimeSelection DeveloperSelection { get; } = RuntimeSelection.ForCli(@"C:\Synthetic\dev\defenseclaw.exe", DeveloperHome, null);
}
