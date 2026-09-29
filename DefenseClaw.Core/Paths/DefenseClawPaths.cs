using System.Collections.Concurrent;

namespace DefenseClaw.Core.Paths;

/// <summary>
/// Every filesystem location the companion app touches. All roots are constructor
/// injectable so tests can point at a temp directory instead of the live install.
/// <para>
/// <b>Executable lookups are cached.</b> <see cref="FindExecutable"/> walks every PATH entry
/// and probes two file names in each, and the gateway poll asks for two executables every
/// five seconds — on a machine with a long PATH that is dozens of <c>File.Exists</c> calls per
/// poll to learn something that changes once per install. The result is remembered per name
/// for <see cref="FoundLookupLifetime"/> (and, when nothing was found, for the shorter
/// <see cref="MissingLookupLifetime"/> so a fresh install is noticed promptly), and a cached
/// hit is confirmed with a single <c>File.Exists</c> so a deleted or moved executable is never
/// returned. A user-initiated refresh calls <see cref="InvalidateExecutableCache"/>. The
/// PATH list itself is still snapshotted once at construction, exactly as before.
/// </para>
/// </summary>
public sealed class DefenseClawPaths
{
    /// <summary>
    /// How long a successful executable lookup is trusted without re-walking PATH. Only the
    /// <i>which copy wins</i> question is cached this long: existence of the cached path is
    /// re-checked on every use, so this bounds how stale PATH precedence can be, nothing else.
    /// </summary>
    public static readonly TimeSpan FoundLookupLifetime = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long "not found" is trusted. Deliberately much shorter than
    /// <see cref="FoundLookupLifetime"/>: the not-installed case is the expensive one (every
    /// candidate is probed) but also the one a user is actively trying to fix by running the
    /// installer, and a banner that lingers for a minute after the install finishes reads as
    /// broken.
    /// </summary>
    public static readonly TimeSpan MissingLookupLifetime = TimeSpan.FromSeconds(10);

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
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, ExecutableLookup> _lookups = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>One remembered <see cref="FindExecutable"/> answer and when it was taken.</summary>
    private readonly record struct ExecutableLookup(string? Path, long Timestamp);

    /// <param name="dataDirectory">Defaults to <c>%USERPROFILE%\.defenseclaw</c>.</param>
    /// <param name="binDirectory">Defaults to <c>%LOCALAPPDATA%\Programs\DefenseClaw\bin</c>.</param>
    /// <param name="searchPath">PATH entries probed before <paramref name="binDirectory"/>.</param>
    /// <param name="fileExists">Filesystem probe; overridable for tests.</param>
    /// <param name="timeProvider">Clock for the lookup cache; overridable for tests.</param>
    public DefenseClawPaths(
        string? dataDirectory = null,
        string? binDirectory = null,
        IEnumerable<string>? searchPath = null,
        Func<string, bool>? fileExists = null,
        TimeProvider? timeProvider = null)
    {
        DataDirectory = dataDirectory ?? DefaultDataDirectory();
        BinDirectory = binDirectory ?? DefaultBinDirectory();
        _searchPath = (searchPath ?? SplitEnvironmentPath()).ToArray();
        _fileExists = fileExists ?? File.Exists;
        _time = timeProvider ?? TimeProvider.System;
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
    /// <para>
    /// Cached — see the type documentation. Safe to call from any thread.
    /// </para>
    /// </summary>
    public string? FindExecutable(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        if (_lookups.TryGetValue(name, out var cached))
        {
            var age = _time.GetElapsedTime(cached.Timestamp);
            var usable = cached.Path is null
                ? age < MissingLookupLifetime
                : age < FoundLookupLifetime && _fileExists(cached.Path);

            if (usable)
            {
                return cached.Path;
            }
        }

        var found = ScanForExecutable(name);
        _lookups[name] = new ExecutableLookup(found, _time.GetTimestamp());
        return found;
    }

    /// <summary>
    /// Forgets every remembered executable lookup so the next <see cref="FindExecutable"/>
    /// walks PATH again. Called by a manual refresh: someone who just ran the installer and
    /// pressed Refresh must not wait out <see cref="MissingLookupLifetime"/>.
    /// </summary>
    public void InvalidateExecutableCache() => _lookups.Clear();

    private string? ScanForExecutable(string name)
    {
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
