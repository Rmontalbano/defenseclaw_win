using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Security.Cryptography;
using System.Text.Json;

namespace DefenseClaw.App.Services.Wizards;

/// <summary>
/// Which build of the CLI a set of help screens was read from. Two identities match only when
/// every part matches, so an upgrade, a reinstall to another path, or a swapped executable each
/// retire the cache.
/// <para>
/// The executable's own size and timestamp are not enough on their own: <c>defenseclaw.exe</c> is a
/// generic ~2.6 MB launcher stub (every entry-point script in the bin directory is the same size),
/// so a package upgrade can leave it byte-identical. The version the CLI reports for itself
/// (<c>--version-json</c>) is what actually moves with a release, and the file stamps catch the
/// cases where the version string does not (a reinstall over the top, an editable install).
/// </para>
/// </summary>
internal sealed record CliIdentity(string ExePath, long Length, long LastWriteUtcTicks, string Version)
{
    public bool SameAs(CliIdentity? other) =>
        other is not null &&
        string.Equals(ExePath, other.ExePath, StringComparison.OrdinalIgnoreCase) &&
        Length == other.Length &&
        LastWriteUtcTicks == other.LastWriteUtcTicks &&
        string.Equals(Version, other.Version, StringComparison.Ordinal);
}

/// <summary>
/// Persists <see cref="SetupHelpProbe"/>'s successful help screens under the app's own local data
/// directory, so a relaunch against the same CLI build skips the phase-two fan-out (thirty-plus
/// ~0.8 s Python start-ups behind six at a time) instead of repeating it from zero every process.
/// <para>
/// <b>What makes an entry trustworthy.</b> The file records the <see cref="CliIdentity"/> it was
/// read from, and screens are served only when the CLI now installed has the same identity, resolved
/// with one <c>--version-json</c> call. A different identity, an unreadable or partial file, an
/// unknown <see cref="SchemaVersion"/>, or a CLI too old to report a version all mean "no cache":
/// nothing is served, nothing is trusted, the probes run exactly as they did before this cache
/// existed, and the next successful probe rewrites the file. A cache problem can therefore cost a
/// re-probe but never show a stale card.
/// </para>
/// <para>
/// <b>Only successes are stored,</b> the same rule the in-memory cache keeps: a failed probe is an
/// answer about a moment, not about the CLI. <see cref="Invalidate"/> (the hub's "Re-read catalog")
/// forgets everything, deletes the file, and stops reading it for the rest of the process, so a
/// refresh really does re-ask the CLI.
/// </para>
/// <para>
/// <b>Writes are batched and atomic.</b> Stores only mark the cache dirty; one write follows a
/// quiet <c>flushDelay</c>, so the fan-out costs one file write rather than thirty. The file is written
/// to a fixed temporary name and swapped into place, so a crash mid-write leaves the old file or none, never
/// half of one. Lines lost to a quit inside the delay are simply probed again next time. Nothing is
/// ever written under <c>~/.defenseclaw</c>: this is the app's own cache.
/// </para>
/// </summary>
internal sealed class SetupHelpDiskCache
{
    /// <summary>Bump when the file's shape changes; a file with any other value is discarded.</summary>
    public const int SchemaVersion = 2;

    private const int KeyBytes = 32;

    private static readonly byte[] KeyEntropy = Encoding.UTF8.GetBytes("DefenseClaw.App.SetupHelpDiskCache.v1");

    /// <summary>How long stores are coalesced before the file is rewritten.</summary>
    public static readonly TimeSpan DefaultFlushDelay = TimeSpan.FromSeconds(1);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _filePath;
    private readonly Func<string, CancellationToken, Task<string?>> _resolveVersion;
    private readonly TimeSpan _flushDelay;
    private readonly object _gate = new();
    private readonly object _writeGate = new();
    private readonly object _keyGate = new();
    private readonly Func<byte[]?>? _macKeyOverride;
    private byte[]? _macKey;
    private bool _macKeyResolved;

    private bool _loaded;
    private bool _readEnabled = true;
    private int _generation;

    // What the file said when it was read. Not trusted until its identity matches the installed CLI.
    private CliIdentity? _fileIdentity;
    private Dictionary<string, string>? _fileScreens;

    // What this process would write: screens proven against _writeIdentity, plus the file's own
    // screens once that identity has been shown to match.
    private CliIdentity? _writeIdentity;
    private Dictionary<string, string>? _writeScreens;
    private bool _dirty;
    private bool _flushScheduled;

    private Lazy<Task<CliIdentity?>>? _identity;
    private string? _identityExe;

    private readonly ConcurrentDictionary<Task, byte> _pendingStores = new();

    /// <param name="filePath">The cache file; its directory is created on the first write.</param>
    /// <param name="resolveVersion">
    /// (executable, token) → the CLI's own version string, or null when it cannot say. Null disables
    /// persistence for that CLI.
    /// </param>
    /// <param name="flushDelay">
    /// Coalescing window for writes. <see cref="Timeout.InfiniteTimeSpan"/> never flushes on its
    /// own; the caller must call <see cref="Flush"/> (tests).
    /// </param>
    /// <param name="macKey">
    /// The HMAC key source; a test replaces it. Default: a DPAPI-protected key file beside <paramref name="filePath"/>. Null from the
    /// source means no key: nothing is served from disk.
    /// </param>
    public SetupHelpDiskCache(
        string filePath,
        Func<string, CancellationToken, Task<string?>> resolveVersion,
        TimeSpan? flushDelay = null,
        Func<byte[]?>? macKey = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        ArgumentNullException.ThrowIfNull(resolveVersion);

        _macKeyOverride = macKey;
        _filePath = filePath;
        _resolveVersion = resolveVersion;
        _flushDelay = flushDelay ?? DefaultFlushDelay;
    }

    /// <summary><c>%LOCALAPPDATA%\DefenseClaw.App\cache\setup-help-cache.json</c>, beside the crash logs and update cache.</summary>
    public static string DefaultFilePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DefenseClaw.App",
        "cache",
        "setup-help-cache.json");

    public string FilePath => _filePath;

    /// <summary>
    /// Bumped by <see cref="Invalidate"/>. A probe records it when it starts and hands it back with
    /// its result, so an answer that lands after a refresh is not written into the fresh cache.
    /// </summary>
    public int Generation
    {
        get
        {
            lock (_gate)
            {
                return _generation;
            }
        }
    }

    /// <summary>
    /// The persisted help text for <paramref name="key"/>, or null when there is none worth trusting.
    /// Starts resolving the CLI's identity in the background either way, so on a miss it is ready by
    /// the time the probe that follows needs it for <see cref="StoreAsync"/>; only a possible hit
    /// waits for it.
    /// </summary>
    public async Task<string?> TryGetAsync(string executable, string key)
    {
        var identity = IdentityFor(executable);

        string? candidate;
        int generation;
        lock (_gate)
        {
            EnsureLoaded();
            generation = _generation;
            candidate = _readEnabled && _fileScreens is not null && _fileScreens.TryGetValue(key, out var text)
                ? text
                : null;
        }

        if (candidate is null)
        {
            return null;
        }

        var installed = await identity.ConfigureAwait(false);

        lock (_gate)
        {
            if (generation != _generation || _fileScreens is null)
            {
                return null;
            }

            if (installed is null || !installed.SameAs(_fileIdentity))
            {
                // A different build (or one that cannot say what it is): nothing in the file is
                // about this CLI, so stop looking at it. The write side starts a fresh set.
                _fileScreens = null;
                _fileIdentity = null;
                return null;
            }

            AdoptFileScreens(installed);
            return candidate;
        }
    }

    /// <summary>
    /// Records a successful probe, without making the caller wait: the answer it belongs to is already
    /// in hand, and on a cold cache the CLI's identity - a second Python start-up, running alongside the
    /// first probe - may not be known yet. Ignored when the CLI has no identity to key it by, or when
    /// <paramref name="generation"/> is no longer current (the catalog was refreshed while the probe
    /// ran). Never throws; <see cref="WhenStoresCompleteAsync"/> is for tests that need to know it landed.
    /// </summary>
    public void Store(string executable, string key, string text, int generation)
    {
        var pending = StoreCoreAsync(executable, key, text, generation);
        if (pending.IsCompleted)
        {
            return;
        }

        _ = _pendingStores.TryAdd(pending, 0);
        _ = pending.ContinueWith(
            done => _pendingStores.TryRemove(done, out _),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>Completes when every <see cref="Store"/> made so far has been filed (not necessarily flushed).</summary>
    public Task WhenStoresCompleteAsync() => Task.WhenAll(_pendingStores.Keys);

    private async Task StoreCoreAsync(string executable, string key, string text, int generation)
    {
        if (string.IsNullOrWhiteSpace(text) || generation != Generation)
        {
            return;
        }

        var installed = await IdentityFor(executable).ConfigureAwait(false);
        if (installed is null)
        {
            return;
        }

        lock (_gate)
        {
            if (generation != _generation)
            {
                return;
            }

            AdoptFileScreens(installed);
            if (_writeIdentity is null || !_writeIdentity.SameAs(installed))
            {
                _writeIdentity = installed;
                _writeScreens = new Dictionary<string, string>(StringComparer.Ordinal);
            }

            _writeScreens![key] = text;
            _dirty = true;
        }

        ScheduleFlush();
    }

    /// <summary>
    /// Forgets everything: the loaded file, this process's pending screens, the resolved identity, and
    /// the file itself. Nothing is read from disk again in this process; the next stores start a new file.
    /// </summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _generation++;
            _readEnabled = false;
            _loaded = true;
            _fileIdentity = null;
            _fileScreens = null;
            _writeIdentity = null;
            _writeScreens = null;
            _dirty = false;
            _identity = null;
            _identityExe = null;
        }

        lock (_writeGate)
        {
            TryDelete(_filePath);
        }
    }

    /// <summary>Writes the pending screens now, if there are any. Returns true when a file was written.</summary>
    public bool Flush()
    {
        CliIdentity identity;
        Dictionary<string, string> screens;
        int generation;
        lock (_gate)
        {
            _flushScheduled = false;
            if (!_dirty || _writeIdentity is null || _writeScreens is null)
            {
                return false;
            }

            identity = _writeIdentity;
            screens = new Dictionary<string, string>(_writeScreens, StringComparer.Ordinal);
            generation = _generation;
            _dirty = false;
        }

        lock (_writeGate)
        {
            // A refresh that landed between the snapshot and here has already deleted the file
            // and cleared the pending set; do not put the forgotten screens back.
            if (Generation != generation)
            {
                return false;
            }

            return TryWrite(identity, screens);
        }
    }

    // ------------------------------------------------------------------ identity

    private Task<CliIdentity?> IdentityFor(string executable)
    {
        lock (_gate)
        {
            if (_identity is null || !string.Equals(_identityExe, executable, StringComparison.OrdinalIgnoreCase))
            {
                _identityExe = executable;
                _identity = new Lazy<Task<CliIdentity?>>(
                    () => Task.Run(() => ResolveIdentityAsync(executable)),
                    LazyThreadSafetyMode.ExecutionAndPublication);
            }

            return _identity.Value;
        }
    }

    private async Task<CliIdentity?> ResolveIdentityAsync(string executable)
    {
        try
        {
            var info = new FileInfo(executable);
            if (!info.Exists)
            {
                return null;
            }

            var version = await _resolveVersion(executable, CancellationToken.None).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(version)
                ? null
                : new CliIdentity(info.FullName, info.Length, info.LastWriteTimeUtc.Ticks, version.Trim());
        }
#pragma warning disable CA1031 // A cache is an optimisation: whatever goes wrong here means "no identity", never a failed probe.
        catch (Exception)
        {
            return null;
        }
#pragma warning restore CA1031
    }

    // ------------------------------------------------------------------ load / write (call under _gate for the load)

    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        var model = TryRead(_filePath);
        if (model is null)
        {
            return;
        }

        var identity = new CliIdentity(model.Cli!.Exe!, model.Cli.Length, model.Cli.LastWriteUtcTicks, model.Cli.Version!);

        // Only an entry whose MAC verifies under this user's key is believed: a file edited or dropped in by anything that
        // does not hold the key (another program writing the file, a copy from another account) is a cache miss, entry by entry.
        var key = MacKey();
        var screens = new Dictionary<string, string>(StringComparer.Ordinal);
        if (key is not null && model.Macs is { } macs)
        {
            foreach (var (screenKey, text) in model.Screens!)
            {
                if (!string.IsNullOrWhiteSpace(text) &&
                    macs.TryGetValue(screenKey, out var mac) &&
                    MacMatches(key, identity, screenKey, text, mac))
                {
                    screens[screenKey] = text;
                }
            }
        }

        if (screens.Count == 0)
        {
            return;
        }

        _fileIdentity = identity;
        _fileScreens = screens;
    }

    // ------------------------------------------------------------------ integrity (CUST-251)

    private static string ComputeMac(byte[] key, CliIdentity identity, string screenKey, string text)
    {
        // Length-prefixed fields: no value can be shifted into its neighbour to make two different entries hash alike.
        var payload = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{SchemaVersion}|{identity.ExePath.Length}:{identity.ExePath}|{identity.Length}|{identity.LastWriteUtcTicks}|{identity.Version.Length}:{identity.Version}|{screenKey.Length}:{screenKey}|{text.Length}:{text}");
        return Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(payload)));
    }

    private static bool MacMatches(byte[] key, CliIdentity identity, string screenKey, string text, string? claimed) =>
        claimed is not null &&
        CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(ComputeMac(key, identity, screenKey, text)),
            Encoding.ASCII.GetBytes(claimed.ToUpperInvariant()));

    /// <summary>
    /// The HMAC key: 32 random bytes, protected for the current Windows user with DPAPI and kept beside the cache file. Created on first use;
    /// a key file that cannot be unprotected (a copy from another account, a forged file) is replaced, which retires every entry that
    /// carried the old one. Null when no key can be had at all - nothing is then trusted on read, and entries are written without a MAC.
    /// <para>
    /// This stops a file that was merely edited or planted (by a script, a sync tool, another account) from putting forged help text in front of the
    /// operator. It does not stop code running as this same user, which can ask DPAPI for the key just as this app does.
    /// </para>
    /// </summary>
    private byte[]? MacKey()
    {
        lock (_keyGate)
        {
            if (_macKeyResolved)
            {
                return _macKey;
            }

            _macKeyResolved = true;
            _macKey = _macKeyOverride is not null ? _macKeyOverride() : LoadOrCreateKey(_filePath + ".key");
            return _macKey;
        }
    }

    private static byte[]? LoadOrCreateKey(string keyPath)
    {
        try
        {
            if (File.Exists(keyPath))
            {
                try
                {
                    var key = ProtectedData.Unprotect(File.ReadAllBytes(keyPath), KeyEntropy, DataProtectionScope.CurrentUser);
                    if (key.Length == KeyBytes)
                    {
                        return key;
                    }
                }
                catch (CryptographicException)
                {
                    // Not ours (or damaged): replaced below.
                }
            }

            var fresh = RandomNumberGenerator.GetBytes(KeyBytes);
            var directory = Path.GetDirectoryName(keyPath);
            if (!string.IsNullOrEmpty(directory))
            {
                _ = Directory.CreateDirectory(directory);
            }

            File.WriteAllBytes(keyPath, ProtectedData.Protect(fresh, KeyEntropy, DataProtectionScope.CurrentUser));
            return fresh;
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Once the file has been shown to describe the installed CLI, its screens join the set this
    /// process rewrites, so a partial warm-up adds to the file instead of replacing it.
    /// </summary>
    private void AdoptFileScreens(CliIdentity installed)
    {
        if (_fileScreens is null || !installed.SameAs(_fileIdentity))
        {
            return;
        }

        if (_writeIdentity is null || !_writeIdentity.SameAs(installed))
        {
            _writeIdentity = installed;
            _writeScreens = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        foreach (var (key, text) in _fileScreens)
        {
            _ = _writeScreens!.TryAdd(key, text);
        }
    }

    private void ScheduleFlush()
    {
        if (_flushDelay == Timeout.InfiniteTimeSpan)
        {
            return;
        }

        lock (_gate)
        {
            if (_flushScheduled)
            {
                return;
            }

            _flushScheduled = true;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(_flushDelay).ConfigureAwait(false);
            _ = Flush();
        });
    }

    private static FileModel? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var model = JsonSerializer.Deserialize<FileModel>(DefenseClaw.Core.IO.SharedFile.ReadAllText(path), JsonOptions);
            return IsUsable(model) ? model : null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static bool IsUsable(FileModel? model) =>
        model is { Schema: SchemaVersion, Screens: not null, Cli: { } cli } &&
        !string.IsNullOrWhiteSpace(cli.Exe) &&
        !string.IsNullOrWhiteSpace(cli.Version);

    private bool TryWrite(CliIdentity identity, Dictionary<string, string> screens)
    {
        var temp = _filePath + ".tmp";
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                _ = Directory.CreateDirectory(directory);
            }

            var model = new FileModel
            {
                Schema = SchemaVersion,
                Cli = new FileCli
                {
                    Exe = identity.ExePath,
                    Length = identity.Length,
                    LastWriteUtcTicks = identity.LastWriteUtcTicks,
                    Version = identity.Version,
                },
                Screens = screens,
                Macs = MacKey() is { } key
                    ? screens.ToDictionary(pair => pair.Key, pair => ComputeMac(key, identity, pair.Key, pair.Value), StringComparer.Ordinal)
                    : null,
            };

            File.WriteAllText(temp, JsonSerializer.Serialize(model, JsonOptions), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            if (File.Exists(_filePath))
            {
                File.Replace(temp, _filePath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temp, _filePath);
            }

            return true;
        }
        catch (IOException)
        {
            // A cache that cannot be written is a cache that is not there; the probes already ran.
            TryDelete(temp);
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            TryDelete(temp);
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // What the file holds. Nullable throughout: the file is read from disk, and a hand-edited or
    // truncated one must fail IsUsable rather than a null dereference.
    private sealed class FileModel
    {
        public int Schema { get; set; }

        public FileCli? Cli { get; set; }

        public Dictionary<string, string>? Screens { get; set; }

        /// <summary>Hex HMAC-SHA256 per screen key (see <see cref="MacKey"/>); an entry without a matching one is not served.</summary>
        public Dictionary<string, string>? Macs { get; set; }
    }

    private sealed class FileCli
    {
        public string? Exe { get; set; }

        public long Length { get; set; }

        public long LastWriteUtcTicks { get; set; }

        public string? Version { get; set; }
    }
}
