namespace DefenseClaw.Core.Paths;

/// <summary>
/// Every filesystem location the companion app touches. All roots are constructor
/// injectable so tests can point at a temp directory instead of the live install.
/// </summary>
public sealed class DefenseClawPaths
{
    /// <summary>Executables shipped by the DefenseClaw installer.</summary>
    public static readonly IReadOnlyList<string> KnownExecutables = new[]
    {
        "defenseclaw",
        "defenseclaw-gateway",
        "defenseclaw-hook",
        "defenseclaw-observability",
        "defenseclaw-startup",
        "skill-scanner",
        "mcp-scanner",
    };

    private readonly Func<string, bool> _fileExists;
    private readonly IReadOnlyList<string> _searchPath;

    /// <param name="dataDirectory">Defaults to <c>%USERPROFILE%\.defenseclaw</c>.</param>
    /// <param name="binDirectory">Defaults to <c>%LOCALAPPDATA%\Programs\DefenseClaw\bin</c>.</param>
    /// <param name="searchPath">PATH entries probed before <paramref name="binDirectory"/>.</param>
    /// <param name="fileExists">Filesystem probe; overridable for tests.</param>
    public DefenseClawPaths(
        string? dataDirectory = null,
        string? binDirectory = null,
        IEnumerable<string>? searchPath = null,
        Func<string, bool>? fileExists = null)
    {
        DataDirectory = dataDirectory ?? DefaultDataDirectory();
        BinDirectory = binDirectory ?? DefaultBinDirectory();
        _searchPath = (searchPath ?? SplitEnvironmentPath()).ToArray();
        _fileExists = fileExists ?? File.Exists;
    }

    public string DataDirectory { get; }

    /// <summary>Installer bin directory used as the fallback when PATH misses.</summary>
    public string BinDirectory { get; }

    public string ConfigFilePath => Path.Combine(DataDirectory, "config.yaml");

    public string EnvFilePath => Path.Combine(DataDirectory, ".env");

    public string AuditDatabasePath => Path.Combine(DataDirectory, "audit.db");

    public string InventoryDatabasePath => Path.Combine(DataDirectory, "inventory.db");

    public string JudgeBodiesDatabasePath => Path.Combine(DataDirectory, "judge_bodies.db");

    public string GatewayLogPath => Path.Combine(DataDirectory, "gateway.log");

    public string WatchdogLogPath => Path.Combine(DataDirectory, "watchdog.log");

    /// <summary>Structured gateway stream; not guaranteed to exist on Windows 0.8.7.</summary>
    public string GatewayJsonlPath => Path.Combine(DataDirectory, "gateway.jsonl");

    public string GatewayPidPath => Path.Combine(DataDirectory, "gateway.pid");

    public string WatchdogPidPath => Path.Combine(DataDirectory, "watchdog.pid");

    public string PoliciesDirectory => Path.Combine(DataDirectory, "policies");

    public string HooksDirectory => Path.Combine(DataDirectory, "hooks");

    public string PluginsDirectory => Path.Combine(DataDirectory, "plugins");

    public string QuarantineDirectory => Path.Combine(DataDirectory, "quarantine");

    public string AgentDiscoveryStatePath => Path.Combine(DataDirectory, "agent_discovery.json");

    public string AiDiscoveryStatePath => Path.Combine(DataDirectory, "ai_discovery_state.json");

    public string DoctorCachePath => Path.Combine(DataDirectory, "doctor_cache.json");

    public string ActiveConnectorPath => Path.Combine(DataDirectory, "active_connector.json");

    /// <summary>True once <c>defenseclaw init</c> has produced a config.yaml.</summary>
    public bool IsInitialized => _fileExists(ConfigFilePath);

    public bool DataDirectoryExists => Directory.Exists(DataDirectory);

    public string? CliPath => FindExecutable("defenseclaw");

    public string? GatewayCliPath => FindExecutable("defenseclaw-gateway");

    public string? SkillScannerPath => FindExecutable("skill-scanner");

    public string? McpScannerPath => FindExecutable("mcp-scanner");

    /// <summary>
    /// Resolves an executable by bare name. PATH entries win; the installer bin
    /// directory is the documented fallback. Returns null when nothing is found.
    /// </summary>
    public string? FindExecutable(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        foreach (var candidate in CandidatesFor(name))
        {
            if (_fileExists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Ordered probe list for <paramref name="name"/>; useful for diagnostics UI.</summary>
    public IEnumerable<string> CandidatesFor(string name)
    {
        var fileNames = OperatingSystem.IsWindows() && !HasExecutableExtension(name)
            ? new[] { name + ".exe", name }
            : new[] { name };

        foreach (var directory in _searchPath.Append(BinDirectory))
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            foreach (var fileName in fileNames)
            {
                yield return Path.Combine(directory, fileName);
            }
        }
    }

    public static string DefaultDataDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".defenseclaw");

    public static string DefaultBinDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "DefenseClaw",
            "bin");

    private static bool HasExecutableExtension(string name) =>
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> SplitEnvironmentPath()
    {
        var raw = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(raw))
        {
            return Array.Empty<string>();
        }

        return raw.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
