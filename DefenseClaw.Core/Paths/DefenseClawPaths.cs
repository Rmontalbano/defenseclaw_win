using System.Collections.Concurrent;

namespace DefenseClaw.Core.Paths;

/// <summary>Where <see cref="DefenseClawPaths.DataDirectory"/> came from.</summary>
public enum DataDirectorySource
{
    /// <summary><c>%USERPROFILE%\.defenseclaw</c>: nothing overrode it.</summary>
    Default,

    /// <summary>The <c>DEFENSECLAW_HOME</c> environment variable, exactly as the defenseclaw CLI reads it.</summary>
    Environment,

    /// <summary>Handed to the constructor (tests, harnesses).</summary>
    Explicit,
}

/// <summary>The resolved data directory, where it came from, and anything odd about the environment that produced it.</summary>
/// <param name="Path">Absolute path of the data directory.</param>
/// <param name="Source">Which rung of the precedence ladder supplied it.</param>
/// <param name="Note">Null unless something in the environment deserves a sentence (an unusable value, a variable this app ignores).</param>
public sealed record DataDirectoryResolution(string Path, DataDirectorySource Source, string? Note)
{
    /// <summary>One or two sentences for the UI: the source, then the note if there is one.</summary>
    public string Description
    {
        get
        {
            var source = Source switch
            {
                DataDirectorySource.Environment =>
                    $"From {DefenseClawPaths.HomeVariableName}, the override the defenseclaw CLI honours.",
                DataDirectorySource.Explicit => "Set explicitly.",
                _ => $"Default location ({DefenseClawPaths.HomeVariableName} is not set).",
            };

            return Note is { Length: > 0 } ? source + " " + Note : source;
        }
    }
}

/// <summary>
/// Every filesystem location the companion app touches. All roots are constructor
/// injectable so tests can point at a temp directory instead of the live install.
/// <para>
/// <b>The data directory follows the CLI.</b> <c>defenseclaw</c> reads <c>DEFENSECLAW_HOME</c>
/// (an empty value means unset) and otherwise uses <c>~\.defenseclaw</c>; CLI children spawned by
/// this app inherit that variable, so the app must resolve the same directory or it would read one
/// install's <c>config.yaml</c> while the CLI writes another's. <c>DEFENSECLAW_DATA_DIR</c> is set by
/// the CLI only for the gateway daemon it starts (always alongside a matching
/// <c>DEFENSECLAW_HOME</c>) and is never read by the CLI, so it does not take part; when it is set to
/// something different, <see cref="DataDirectoryOrigin"/> says so rather than guessing which one the
/// gateway would prefer.
/// </para>
/// <para>
/// <b>Executable lookups never block a caller that has an answer.</b> <see cref="FindExecutable"/>
/// walks every PATH entry and probes two file names in each; one dead network entry (an unreachable
/// UNC path) makes a single <c>File.Exists</c> take ~40 s. So a scan runs on the thread pool, one at a
/// time per name (concurrent callers share it), and its answer is remembered for
/// <see cref="FoundLookupLifetime"/> (or <see cref="MissingLookupLifetime"/> when nothing was found, so
/// a fresh install is noticed promptly). A caller that arrives while the remembered answer is stale gets
/// that answer immediately and the refresh happens behind it, except for a remembered "missing", where
/// the caller that starts the refresh waits for it (it is the one who just installed something). UI code
/// uses <see cref="TryGetKnownExecutable"/> (never touches the filesystem) and
/// <see cref="FindExecutableAsync"/>. A directory that took longer than <see cref="SlowDirectoryThreshold"/>
/// to probe is skipped for <see cref="SlowDirectoryQuarantine"/>, so a dead entry costs one slow scan, not
/// one per refresh. A cached hit is confirmed with a single <c>File.Exists</c> before it is trusted, so a
/// deleted or moved executable is never returned as fresh.
/// </para>
/// <para>
/// <b>PATH.</b> The list is snapshotted at construction (quotes around an entry are stripped, as the shell
/// does). <see cref="InvalidateExecutableCache"/> — a manual Refresh — also re-reads the machine and user
/// PATH from the registry and adds any entry the process does not already have, so an app started at
/// login (with the login-time PATH) sees an install made since.
/// </para>
/// </summary>
public sealed class DefenseClawPaths
{
    /// <summary>The variable the defenseclaw CLI reads for its data directory.</summary>
    public const string HomeVariableName = "DEFENSECLAW_HOME";

    /// <summary>The gateway daemon's variable. The CLI does not read it; see the type documentation.</summary>
    public const string DataDirVariableName = "DEFENSECLAW_DATA_DIR";

    /// <summary>The CLI's name as every lookup spells it; the one executable <see cref="SetCliPathOverride"/> can pin.</summary>
    public const string CliExecutableName = "defenseclaw";

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

    /// <summary>A PATH directory whose probes took at least this long during a scan is treated as unreachable.</summary>
    public static readonly TimeSpan SlowDirectoryThreshold = TimeSpan.FromSeconds(2);

    /// <summary>How long a slow PATH directory is skipped by later scans before it is tried again.</summary>
    public static readonly TimeSpan SlowDirectoryQuarantine = TimeSpan.FromMinutes(5);

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
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, ExecutableLookup> _lookups = new(StringComparer.OrdinalIgnoreCase);

    // One scan in flight per name. Lazy so two racing callers cannot both start it.
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _flights = new(StringComparer.OrdinalIgnoreCase);

    // Directory -> when a scan found it slow. Only ever a hint to skip it for a while.
    private readonly ConcurrentDictionary<string, long> _slowDirectories = new(StringComparer.OrdinalIgnoreCase);

    private readonly IReadOnlyList<string> _initialSearchPath;
    private readonly Func<IEnumerable<string>>? _persistedSearchPath;
    private readonly object _pathGate = new();
    private volatile IReadOnlyList<string> _searchPath;
    private int _pathRefreshRequested;

    // Bumped by InvalidateExecutableCache. An answer from an older generation is stale by definition and a
    // scan that started before the bump does not get to publish.
    private int _generation;

    /// <summary>
    /// The operator's "use this defenseclaw.exe" choice (the app's <c>connection.cliPathOverride</c>), or null. Volatile: scans read
    /// it on the pool while the settings page writes it on the UI thread. See <see cref="SetCliPathOverride"/>.
    /// </summary>
    private volatile string? _cliPathOverride;

    /// <summary>One remembered <see cref="FindExecutable"/> answer, when it was taken, and under which generation.</summary>
    private readonly record struct ExecutableLookup(string? Path, long Timestamp, int Generation);

    /// <param name="dataDirectory">Defaults to <c>%DEFENSECLAW_HOME%</c>, else <c>%USERPROFILE%\.defenseclaw</c> (see <see cref="ResolveDataDirectory"/>).</param>
    /// <param name="binDirectory">Defaults to <c>%LOCALAPPDATA%\Programs\DefenseClaw\bin</c>.</param>
    /// <param name="searchPath">PATH entries probed before <paramref name="binDirectory"/>. Defaults to this process's PATH.</param>
    /// <param name="fileExists">Filesystem probe; overridable for tests.</param>
    /// <param name="timeProvider">Clock for the lookup cache; overridable for tests.</param>
    /// <param name="environment">Environment reader (PATH, DEFENSECLAW_HOME); overridable for tests.</param>
    /// <param name="persistedSearchPath">
    /// The machine+user PATH as the registry holds it now, read when <see cref="InvalidateExecutableCache"/> asks for a
    /// fresh look. Defaults to the real registry — but only when <paramref name="searchPath"/> is not injected, so a
    /// test that fixes the PATH never has it changed under it.
    /// </param>
    public DefenseClawPaths(
        string? dataDirectory = null,
        string? binDirectory = null,
        IEnumerable<string>? searchPath = null,
        Func<string, bool>? fileExists = null,
        TimeProvider? timeProvider = null,
        Func<string, string?>? environment = null,
        Func<IEnumerable<string>>? persistedSearchPath = null)
    {
        var getEnvironment = environment ?? System.Environment.GetEnvironmentVariable;

        DataDirectoryOrigin = dataDirectory is null
            ? ResolveDataDirectory(getEnvironment)
            : new DataDirectoryResolution(dataDirectory, DataDirectorySource.Explicit, null);
        DataDirectory = DataDirectoryOrigin.Path;
        BinDirectory = binDirectory ?? DefaultBinDirectory();

        _initialSearchPath = MergeSearchPaths(searchPath?.Select(NormalizePathEntry) ?? SplitPathList(getEnvironment("PATH")), []);
        _searchPath = _initialSearchPath;
        _persistedSearchPath = persistedSearchPath ?? (searchPath is null ? ReadPersistedSearchPath : null);

        _fileExists = fileExists ?? File.Exists;
        _time = timeProvider ?? TimeProvider.System;
    }

    public string DataDirectory { get; }

    /// <summary>Where <see cref="DataDirectory"/> came from, for showing the operator which install the app is reading.</summary>
    public DataDirectoryResolution DataDirectoryOrigin { get; }

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

    public string? CliPath => FindExecutable(CliExecutableName);

    public string? GatewayCliPath => FindExecutable("defenseclaw-gateway");

    /// <summary>
    /// The full path the operator chose for <c>defenseclaw.exe</c> (Settings → Connection), or null when the CLI is looked up as usual.
    /// What is set is not necessarily usable: while the file is missing the lookup ignores it (see <see cref="SetCliPathOverride"/>).
    /// </summary>
    public string? CliPathOverride => _cliPathOverride;

    /// <summary>
    /// Pins (or, with null or blank, releases) the <c>defenseclaw</c> executable. While the file exists it is the answer to every
    /// lookup of <c>defenseclaw</c> — <see cref="CliPath"/>, <see cref="FindExecutable"/>, <see cref="FindExecutableAsync"/>,
    /// <see cref="TryGetKnownExecutable"/> and so what <c>CliRunner</c> starts and what every review shows — ahead of PATH and the
    /// install directory. While it does not (deleted, drive gone), the lookup carries on as if nothing were set: a stale
    /// choice must never leave the app with no CLI at all. Only <c>defenseclaw</c> is affected: the gateway binary and the scanners
    /// are still found by name. The remembered answers are dropped, so the next lookup sees the change at once.
    /// <para>The caller validates (<see cref="CheckCliPathOverride"/>); this holds whatever it is given.</para>
    /// </summary>
    public void SetCliPathOverride(string? path)
    {
        var chosen = string.IsNullOrWhiteSpace(path) ? null : path.Trim();
        if (string.Equals(_cliPathOverride, chosen, StringComparison.Ordinal))
        {
            return;
        }

        _cliPathOverride = chosen;

        // A scan already running answers for the old choice and must not publish: the bump sees to that. And what is remembered for the CLI is
        // forgotten outright, not left to be served stale while a refresh runs behind it (which is what a remembered answer that is merely
        // out of date gets): the choice is the operator's, and the very next lookup has to honour it.
        _ = Interlocked.Increment(ref _generation);
        _flights.Clear();
        _ = _lookups.TryRemove(CliExecutableName, out _);
        _ = _lookups.TryRemove(CliExecutableName + ".exe", out _);
    }

    /// <summary>
    /// Why <paramref name="path"/> cannot be the CLI override, in a sentence for the operator, or null when it can (and null for a
    /// blank one, which clears the override): an absolute path to an existing file named <c>defenseclaw.exe</c> (or
    /// <c>defenseclaw</c>). The name is what makes it the CLI and not any program at all.
    /// </summary>
    /// <param name="path">What the operator chose.</param>
    /// <param name="fileExists">Filesystem probe; <see cref="File.Exists(string)"/> when null.</param>
    public static string? CheckCliPathOverride(string? path, Func<string, bool>? fileExists = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var candidate = path.Trim();
        string name;
        try
        {
            if (!Path.IsPathRooted(candidate) || Path.GetPathRoot(candidate) is not { Length: > 0 } root || root is "\\" or "/")
            {
                return "Use the full path to defenseclaw.exe, starting with a drive letter or a network share.";
            }

            name = Path.GetFileName(candidate);
        }
        catch (ArgumentException)
        {
            return "That is not a valid file path.";
        }

        if (!string.Equals(name, CliExecutableName + ".exe", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(name, CliExecutableName, StringComparison.OrdinalIgnoreCase))
        {
            return name.Length == 0
                ? "Choose the defenseclaw.exe file itself, not a folder."
                : $"The file must be named defenseclaw.exe (this one is \"{name}\").";
        }

        return (fileExists ?? File.Exists)(candidate) ? null : "That file does not exist.";
    }

    public string? SkillScannerPath => FindExecutable("skill-scanner");

    public string? McpScannerPath => FindExecutable("mcp-scanner");

    /// <summary>
    /// Resolves an executable by bare name. PATH entries win; the installer bin
    /// directory is the documented fallback. Returns null when nothing is found.
    /// <para>
    /// Cached and single-flight — see the type documentation. Safe to call from any thread, but
    /// <b>not for the UI thread</b>: with nothing remembered (or a remembered "missing" gone stale) the
    /// caller that starts the scan waits for it, and one dead PATH entry makes that ~40 s. UI code calls
    /// <see cref="TryGetKnownExecutable"/> and <see cref="FindExecutableAsync"/>.
    /// </para>
    /// </summary>
    public string? FindExecutable(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        var (path, flight) = Begin(name, waitForFresh: false);
        return flight is null ? path : flight.GetAwaiter().GetResult();
    }

    /// <summary>
    /// The freshest answer for <paramref name="name"/> without occupying the caller: the lookup (including the
    /// <c>File.Exists</c> that confirms a remembered hit) runs on the thread pool, and a scan already in flight is
    /// joined rather than repeated. Completes once a lookup that is not stale has answered.
    /// </summary>
    public async Task<string?> FindExecutableAsync(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        var (path, flight) = await Task.Run(() => Begin(name, waitForFresh: true)).ConfigureAwait(false);
        return flight is null ? path : await flight.ConfigureAwait(false);
    }

    /// <summary>
    /// The last answer for <paramref name="name"/>, without touching the filesystem or waiting for anything: the
    /// call for code on the UI thread. Returns false when nothing has been resolved yet (a scan has been started;
    /// <see cref="FindExecutableAsync"/> tells the caller when it is done). A true return with a null
    /// <paramref name="path"/> means "looked, not found". The answer may be stale — a refresh is started behind it —
    /// and is not re-confirmed against the disk.
    /// </summary>
    public bool TryGetKnownExecutable(string name, out string? path)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        if (_lookups.TryGetValue(name, out var cached))
        {
            path = cached.Path;
            if (!IsFresh(cached))
            {
                _ = StartScan(name, out _);
            }

            return true;
        }

        path = null;
        _ = StartScan(name, out _);
        return false;
    }

    /// <summary>
    /// Marks every remembered executable lookup stale so the next <see cref="FindExecutable"/> walks PATH again, and
    /// asks the next scan to re-read the machine and user PATH from the registry first. Called by a manual refresh:
    /// someone who just ran the installer and pressed Refresh must not wait out <see cref="MissingLookupLifetime"/>.
    /// The old answers stay readable (see <see cref="TryGetKnownExecutable"/>) until the new ones land.
    /// </summary>
    public void InvalidateExecutableCache()
    {
        // The flag first: a scan that starts under the new generation must see it.
        Interlocked.Exchange(ref _pathRefreshRequested, 1);
        Interlocked.Increment(ref _generation);
        _flights.Clear();
    }

    /// <summary>
    /// Decides what a caller gets. Returns an answer, or a scan to wait for, or (for a caller that may not wait and has
    /// a previous answer) the previous answer with the refresh left running behind it.
    /// </summary>
    private (string? Path, Task<string?>? Flight) Begin(string name, bool waitForFresh)
    {
        var havePrevious = false;

        if (_lookups.TryGetValue(name, out var cached))
        {
            var fresh = IsFresh(cached);

            if (cached.Path is null)
            {
                if (fresh)
                {
                    return (null, null);
                }

                havePrevious = true;
            }
            else if (_fileExists(cached.Path))
            {
                if (fresh)
                {
                    return (cached.Path, null);
                }

                if (!waitForFresh)
                {
                    // Stale but still there: only PATH precedence could have moved. Answer now, refresh behind.
                    _ = StartScan(name, out _);
                    return (cached.Path, null);
                }
            }

            // Otherwise the remembered path has gone: it is not an answer, so the caller waits for a scan.
        }

        var flight = StartScan(name, out var started);
        if (havePrevious && !waitForFresh && !started)
        {
            // Someone else is already scanning and all we have is the old "not found": say that, in milliseconds.
            return (null, null);
        }

        return (null, flight);
    }

    private bool IsFresh(in ExecutableLookup lookup) =>
        lookup.Generation == Volatile.Read(ref _generation) &&
        _time.GetElapsedTime(lookup.Timestamp) < (lookup.Path is null ? MissingLookupLifetime : FoundLookupLifetime);

    /// <summary>The scan for <paramref name="name"/> that is in flight, or a new one; <paramref name="started"/> says which.</summary>
    private Task<string?> StartScan(string name, out bool started)
    {
        var generation = Volatile.Read(ref _generation);
        var mine = new Lazy<Task<string?>>(
            () => Task.Run(() => ScanAndPublish(name, generation)),
            LazyThreadSafetyMode.ExecutionAndPublication);

        var entry = _flights.GetOrAdd(name, mine);

        // A flight that has finished but whose removal (the continuation below) has not run yet is not a scan in flight: joining
        // it would hand back its old answer — the "not found" a caller is trying to look past once that answer went stale.
        while (!ReferenceEquals(entry, mine) && entry.IsValueCreated && entry.Value.IsCompleted)
        {
            entry = _flights.TryUpdate(name, mine, entry) ? mine : _flights.GetOrAdd(name, mine);
        }

        started = ReferenceEquals(entry, mine);
        var task = entry.Value;

        if (started)
        {
            // Removes this very flight and nothing newer: an invalidation may already have replaced it.
            _ = task.ContinueWith(
                _ => _flights.TryRemove(new KeyValuePair<string, Lazy<Task<string?>>>(name, entry)),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        return task;
    }

    private string? ScanAndPublish(string name, int generation)
    {
        var found = ScanForExecutable(name);

        // A scan that began before an invalidation describes a PATH the caller asked us to forget.
        if (generation == Volatile.Read(ref _generation))
        {
            _lookups[name] = new ExecutableLookup(found, _time.GetTimestamp(), generation);
        }

        return found;
    }

    private string? ScanForExecutable(string name)
    {
        // The operator's choice comes first, while it is there (see SetCliPathOverride).
        if (IsCliName(name) && _cliPathOverride is { } pinned && _fileExists(pinned))
        {
            return pinned;
        }

        var fileNames = FileNamesFor(name);

        foreach (var (directory, isBinDirectory) in SearchDirectories())
        {
            // The installer's own directory is local by construction and the documented fallback: never skipped.
            if (!isBinDirectory && IsQuarantined(directory))
            {
                continue;
            }

            var started = _time.GetTimestamp();
            foreach (var fileName in fileNames)
            {
                var candidate = Path.Combine(directory, fileName);
                if (_fileExists(candidate))
                {
                    return candidate;
                }
            }

            if (!isBinDirectory && _time.GetElapsedTime(started) >= SlowDirectoryThreshold)
            {
                _slowDirectories[directory] = _time.GetTimestamp();
            }
        }

        return null;
    }

    private bool IsQuarantined(string directory) =>
        _slowDirectories.TryGetValue(directory, out var stamp) &&
        _time.GetElapsedTime(stamp) < SlowDirectoryQuarantine;

    /// <summary>Ordered probe list for <paramref name="name"/>; useful for diagnostics UI.</summary>
    public IEnumerable<string> CandidatesFor(string name)
    {
        if (IsCliName(name) && _cliPathOverride is { } pinned)
        {
            yield return pinned;
        }

        var fileNames = FileNamesFor(name);

        foreach (var (directory, _) in SearchDirectories())
        {
            foreach (var fileName in fileNames)
            {
                yield return Path.Combine(directory, fileName);
            }
        }
    }

    /// <summary>True when <paramref name="name"/> asks for the CLI itself (<c>defenseclaw</c>, with or without <c>.exe</c>), which is what an override replaces.</summary>
    private static bool IsCliName(string name) =>
        string.Equals(name, CliExecutableName, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, CliExecutableName + ".exe", StringComparison.OrdinalIgnoreCase);

    private static string[] FileNamesFor(string name) =>
        OperatingSystem.IsWindows() && !HasExecutableExtension(name)
            ? new[] { name + ".exe", name }
            : new[] { name };

    /// <summary>PATH entries in order, then the installer bin directory (flagged), blanks dropped.</summary>
    private IEnumerable<(string Directory, bool IsBinDirectory)> SearchDirectories()
    {
        foreach (var directory in CurrentSearchPath())
        {
            if (!string.IsNullOrWhiteSpace(directory))
            {
                yield return (directory, false);
            }
        }

        if (!string.IsNullOrWhiteSpace(BinDirectory))
        {
            yield return (BinDirectory, true);
        }
    }

    /// <summary>The PATH to scan, re-read from the registry first if <see cref="InvalidateExecutableCache"/> asked for that.</summary>
    private IReadOnlyList<string> CurrentSearchPath()
    {
        if (_persistedSearchPath is not null && Interlocked.Exchange(ref _pathRefreshRequested, 0) == 1)
        {
            lock (_pathGate)
            {
                try
                {
                    // Rebuilt from the construction-time list every time, so an entry the registry no longer
                    // holds does not stay because an earlier merge added it.
                    _searchPath = MergeSearchPaths(_initialSearchPath, _persistedSearchPath().Select(NormalizePathEntry));
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
                {
                    // The registry could not be read: keep the list we have.
                }
            }
        }

        return _searchPath;
    }

    /// <summary>
    /// The data directory the defenseclaw CLI would use: <c>DEFENSECLAW_HOME</c> when set (a blank value is
    /// unset), otherwise <c>~\.defenseclaw</c>. <c>DEFENSECLAW_DATA_DIR</c> is deliberately not consulted — the CLI
    /// never reads it (see the type documentation) — but a value that disagrees is reported in the result's note.
    /// </summary>
    /// <param name="getEnvironmentVariable">Environment reader; defaults to the process environment.</param>
    /// <param name="userProfile">Home directory; defaults to <c>%USERPROFILE%</c>.</param>
    public static DataDirectoryResolution ResolveDataDirectory(
        Func<string, string?>? getEnvironmentVariable = null,
        string? userProfile = null)
    {
        var get = getEnvironmentVariable ?? System.Environment.GetEnvironmentVariable;
        var defaultPath = Path.Combine(
            userProfile ?? System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
            ".defenseclaw");

        var path = defaultPath;
        var source = DataDirectorySource.Default;
        string? note = null;

        var home = get(HomeVariableName)?.Trim();
        if (!string.IsNullOrEmpty(home))
        {
            if (TryFullPath(home, out var full))
            {
                path = full;
                source = DataDirectorySource.Environment;
            }
            else
            {
                note = $"{HomeVariableName} is set to \"{home}\", which is not a usable path, so the default location is used.";
            }
        }

        var dataDir = get(DataDirVariableName)?.Trim();
        if (!string.IsNullOrEmpty(dataDir) &&
            TryFullPath(dataDir, out var dataFull) &&
            !string.Equals(dataFull, path, StringComparison.OrdinalIgnoreCase))
        {
            var ignored = $"{DataDirVariableName} is also set ({dataFull}); the defenseclaw CLI does not read it, so it is ignored here.";
            note = note is null ? ignored : note + " " + ignored;
        }

        return new DataDirectoryResolution(path, source, note);
    }

    private static bool TryFullPath(string value, out string fullPath)
    {
        try
        {
            var full = Path.GetFullPath(value);
            fullPath = Path.GetPathRoot(full) == full ? full : Path.TrimEndingDirectorySeparator(full);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            fullPath = string.Empty;
            return false;
        }
    }

    /// <summary>The profile default, ignoring every override. <see cref="ResolveDataDirectory"/> is what the app uses.</summary>
    public static string DefaultDataDirectory() =>
        Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), ".defenseclaw");

    public static string DefaultBinDirectory() =>
        Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "DefenseClaw",
            "bin");

    private static bool HasExecutableExtension(string name) =>
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Splits a PATH-style value into directories: entries trimmed, blanks dropped, and the quotes the shell strips
    /// (<c>"C:\Program Files\Tool"</c>) removed — a quoted entry never matches a file otherwise. An entry that still
    /// carries <c>%VAR%</c> is expanded, as <c>cmd</c> does when it searches.
    /// </summary>
    public static IEnumerable<string> SplitPathList(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return Array.Empty<string>();
        }

        return raw
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizePathEntry)
            .Where(entry => entry.Length > 0);
    }

    private static string NormalizePathEntry(string entry)
    {
        var text = entry.Trim().Trim('"').Trim();
        return text.Contains('%') ? System.Environment.ExpandEnvironmentVariables(text) : text;
    }

    /// <summary><paramref name="first"/> then whatever of <paramref name="then"/> it lacks, compared ignoring case and trailing separators.</summary>
    private static IReadOnlyList<string> MergeSearchPaths(IEnumerable<string> first, IEnumerable<string> then)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged = new List<string>();

        foreach (var entry in first.Concat(then))
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }

            if (seen.Add(Path.TrimEndingDirectorySeparator(entry)))
            {
                merged.Add(entry);
            }
        }

        return merged;
    }

    /// <summary>
    /// The machine PATH then the user PATH, as the registry holds them now — the order Windows builds a fresh
    /// logon PATH in. <c>GetEnvironmentVariable</c> with a target reads the registry and expands <c>REG_EXPAND_SZ</c>.
    /// </summary>
    private static IEnumerable<string> ReadPersistedSearchPath()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Array.Empty<string>();
        }

        return SplitPathList(System.Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine))
            .Concat(SplitPathList(System.Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User)));
    }
}
