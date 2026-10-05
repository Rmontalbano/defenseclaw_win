using DefenseClaw.Core.Paths;

namespace DefenseClaw.Core.Config;

/// <summary>Identifies which watched file moved.</summary>
public enum ConfigFileKind
{
    Config,
    DotEnv,
}

public sealed class ConfigChangedEventArgs : EventArgs
{
    public ConfigChangedEventArgs(ConfigFileKind kind, string path)
    {
        Kind = kind;
        Path = path;
    }

    public ConfigFileKind Kind { get; }

    public string Path { get; }
}

/// <summary>
/// Immutable snapshot of a file's identity. Compared to spot real edits and to ignore
/// the duplicate events <see cref="FileSystemWatcher"/> emits for a single save.
/// <para>
/// Length plus last-write time is not enough: plenty of meaningful config edits preserve
/// the byte count (<c>mode: observe</c> → <c>mode: enforce</c>, <c>config_version: 8</c> →
/// <c>9</c>), and the Windows clock only advances every ~15ms, so two quick saves can
/// share a timestamp. Small files therefore also carry a content hash.
/// </para>
/// </summary>
public readonly record struct FileSignature(bool Exists, long Length, DateTime LastWriteUtc, ulong ContentHash)
{
    /// <summary>Files above this size are compared on length and timestamp alone.</summary>
    private const long MaxHashedBytes = 4 * 1024 * 1024;

    public static FileSignature Capture(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            return new FileSignature(false, 0, default, 0);
        }

        return new FileSignature(true, info.Length, info.LastWriteTimeUtc, HashContents(path, info.Length));
    }

    /// <summary>
    /// True when <paramref name="bytes"/> are the file this signature was captured from: the same length and, where the
    /// capture hashed the content, the same hash. The check a save makes on the exact bytes it is about to back up, so a
    /// write that landed after the signature was taken (the CLI's) cannot be overwritten unseen.
    /// </summary>
    public bool DescribesContent(ReadOnlySpan<byte> bytes) =>
        Exists && Length == bytes.Length && (ContentHash == 0 || ContentHash == Hash(bytes));

    private static ulong Hash(ReadOnlySpan<byte> bytes) =>
        BitConverter.ToUInt64(System.Security.Cryptography.SHA256.HashData(bytes), 0);

    private static ulong HashContents(string path, long length)
    {
        if (length > MaxHashedBytes)
        {
            return 0;
        }

        try
        {
            return Hash(DefenseClaw.Core.IO.SharedFile.ReadAllBytes(path));
        }
        catch (IOException)
        {
            // Mid-write; the next poll will see a settled file.
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }
}

/// <summary>
/// Watches <c>config.yaml</c> and <c>.env</c> and raises <see cref="Changed"/> when either
/// actually changes. Uses <see cref="FileSystemWatcher"/> for latency and a periodic
/// signature poll as the backstop — editors that save via rename, and network or
/// virtualized paths, routinely defeat the watcher on its own.
/// <para>
/// <b>Threading.</b> This type is deliberately thread-agnostic and has no idea a UI exists
/// — it lives in Core, which never takes a WPF dependency. Notifications are raised on
/// whichever thread noticed the edit: a thread-pool thread for the poll timer, the
/// watcher's own callback thread for <see cref="FileSystemWatcher"/> events. Subscribers
/// that touch thread-affine state (bound collections, WPF properties) own the marshalling.
/// See <see cref="Changed"/>.
/// </para>
/// </summary>
public sealed class ConfigChangeToken : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<ConfigFileKind, string> _paths;
    private readonly Dictionary<ConfigFileKind, FileSignature> _signatures = new();
    private readonly List<Action<ConfigChangedEventArgs>> _callbacks = new();
    private readonly FileSystemWatcher? _watcher;
    private readonly Timer _poll;

    /// <summary>The file names (config.yaml, .env) a watcher event has to name to be worth a signature check.</summary>
    private readonly HashSet<string> _watchedNames = new(StringComparer.OrdinalIgnoreCase);
    private int _checkCount;
    private bool _disposed;

    /// <summary>How long a new signature has to hold, across two reads, before it counts as a change.</summary>
    public static readonly TimeSpan DefaultSettleDelay = TimeSpan.FromMilliseconds(300);

    private readonly TimeSpan _settleDelay;
    private readonly Timer _settle;
    private readonly Dictionary<ConfigFileKind, (FileSignature Signature, long SeenAt)> _pending = new();

    public ConfigChangeToken(DefenseClawPaths paths, TimeSpan? pollInterval = null, TimeSpan? settleDelay = null)
        : this(paths?.ConfigFilePath!, paths?.EnvFilePath!, pollInterval, settleDelay)
    {
    }

    public ConfigChangeToken(string configPath, string envPath, TimeSpan? pollInterval = null, TimeSpan? settleDelay = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(configPath);
        ArgumentException.ThrowIfNullOrEmpty(envPath);

        _paths = new Dictionary<ConfigFileKind, string>
        {
            [ConfigFileKind.Config] = configPath,
            [ConfigFileKind.DotEnv] = envPath,
        };

        foreach (var (kind, path) in _paths)
        {
            _signatures[kind] = FileSignature.Capture(path);
            _ = _watchedNames.Add(Path.GetFileName(path));
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(configPath));
        if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
        {
            _watcher = new FileSystemWatcher(directory)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime,
                IncludeSubdirectories = false,
            };
            _watcher.Changed += OnFileSystemEvent;
            _watcher.Created += OnFileSystemEvent;
            _watcher.Deleted += OnFileSystemEvent;
            _watcher.Renamed += OnFileSystemEvent;
            // A watcher that dies (buffer overflow, directory removed) must not silence
            // the token: the poll timer keeps running regardless.
            _watcher.Error += (_, _) => { };
            _watcher.EnableRaisingEvents = true;
        }

        _settleDelay = settleDelay ?? DefaultSettleDelay;
        _settle = new Timer(_ => CheckAll(settle: true), null, Timeout.Infinite, Timeout.Infinite);

        var interval = pollInterval ?? TimeSpan.FromSeconds(2);
        _poll = new Timer(_ => CheckAll(settle: true), null, interval, interval);
    }

    /// <summary>True once any watched file has changed since construction or the last reset.</summary>
    public bool HasChanged { get; private set; }

    /// <summary>
    /// How many signature comparisons (each two file reads and hashes) this token has run, from the poll timer,
    /// <see cref="Poll"/> or a watcher event about a watched file. Diagnostics: a token whose data directory is busy
    /// with other files must not see this climb with the writes.
    /// </summary>
    public int CheckCount => Volatile.Read(ref _checkCount);

    /// <summary>
    /// Raised once per watched file that actually changed.
    /// <para>
    /// <b>Never raised on a UI thread.</b> The handler runs on the poll timer's thread-pool
    /// thread or on the <see cref="FileSystemWatcher"/> callback thread, whichever spotted
    /// the edit first. Anything downstream that mutates thread-affine state must marshal;
    /// in this app that happens once, at <c>AppServices.RaiseConfigReloaded</c>, so panel
    /// view-models never see the watcher thread.
    /// </para>
    /// <para>
    /// <b>Not deduplicated across sources.</b> A single save can be spotted by the watcher
    /// and the poll timer close enough together that both raise for it, and
    /// <see cref="Raise"/> runs outside the signature lock, so two notifications can be in
    /// flight concurrently. Handlers must be idempotent and safe to re-enter — reloading
    /// twice must cost nothing worse than a redundant read.
    /// </para>
    /// </summary>
    public event EventHandler<ConfigChangedEventArgs>? Changed;

    /// <summary>
    /// Subscribes a callback; dispose the return value to unsubscribe. Callbacks carry the
    /// same threading contract as <see cref="Changed"/>: any thread, possibly concurrent.
    /// </summary>
    public IDisposable RegisterChangeCallback(Action<ConfigChangedEventArgs> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (_gate)
        {
            _callbacks.Add(callback);
        }

        return new Unsubscriber(this, callback);
    }

    /// <summary>Clears <see cref="HasChanged"/> after the caller has reloaded.</summary>
    public void Reset() => HasChanged = false;

    /// <summary>
    /// Forces an immediate signature comparison instead of waiting for the poll. Unlike the poll and the watcher it
    /// does not wait for the new signature to settle: the caller asked to know now.
    /// </summary>
    public void Poll() => CheckAll(settle: false);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _poll.Dispose();
        _settle.Dispose();
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
        }
    }

    /// <summary>
    /// The watcher sees the whole data directory, and most of what moves there is not config: audit.db and its -wal
    /// (a few writes a second on a busy gateway), gateway.log, the state files. Each of those used to run a full
    /// <see cref="CheckAll"/> - two reads and two SHA-256s under the lock, contending with the editor's
    /// <c>File.Replace</c> - so anything that does not name <c>config.yaml</c> or <c>.env</c> is dropped here. The poll
    /// timer stays as the backstop for editors and volumes whose events do not carry the name.
    /// </summary>
    private void OnFileSystemEvent(object sender, FileSystemEventArgs e)
    {
        if (Concerns(e))
        {
            CheckAll(settle: true);
        }
    }

    private bool Concerns(FileSystemEventArgs e) =>
        e.Name is not { } name ||
        _watchedNames.Contains(name) ||
        (e is RenamedEventArgs { OldName: { } oldName } && _watchedNames.Contains(oldName));

    /// <summary>
    /// Compares each file with the last signature raised for it. With <paramref name="settle"/>, a different signature
    /// is only a candidate: it is raised once a later read, at least the settle delay after it was first seen, still
    /// shows the same signature. An editor that truncates and then writes (or a CLI mid-rewrite) is seen in its
    /// half-written state first, and that state is never raised - it would parse as an error and flash a banner for a
    /// file that is about to be fine.
    /// </summary>
    private void CheckAll(bool settle)
    {
        if (_disposed)
        {
            return;
        }

        _ = Interlocked.Increment(ref _checkCount);

        foreach (var (kind, path) in _paths)
        {
            ConfigChangedEventArgs? args = null;
            lock (_gate)
            {
                var current = FileSignature.Capture(path);
                if (!_signatures.TryGetValue(kind, out var previous) || previous == current)
                {
                    _ = _pending.Remove(kind);
                    continue;
                }

                if (settle)
                {
                    var now = System.Diagnostics.Stopwatch.GetTimestamp();
                    if (!_pending.TryGetValue(kind, out var candidate) || candidate.Signature != current)
                    {
                        _pending[kind] = (current, now);
                        ArmSettle(_settleDelay);
                        continue;
                    }

                    var waited = System.Diagnostics.Stopwatch.GetElapsedTime(candidate.SeenAt, now);
                    if (waited < _settleDelay)
                    {
                        // A second event for the same state, too soon to count as the second read.
                        ArmSettle(_settleDelay - waited);
                        continue;
                    }
                }

                _ = _pending.Remove(kind);
                _signatures[kind] = current;
                HasChanged = true;
                args = new ConfigChangedEventArgs(kind, path);
            }

            Raise(args);
        }
    }

    private void ArmSettle(TimeSpan due)
    {
        try
        {
            _ = _settle.Change(due < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : due, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // Disposed between the check and here; nothing left to settle.
        }
    }

    private void Raise(ConfigChangedEventArgs args)
    {
        Action<ConfigChangedEventArgs>[] callbacks;
        lock (_gate)
        {
            callbacks = _callbacks.ToArray();
        }

        Changed?.Invoke(this, args);
        foreach (var callback in callbacks)
        {
            callback(args);
        }
    }

    private sealed class Unsubscriber : IDisposable
    {
        private readonly ConfigChangeToken _owner;
        private readonly Action<ConfigChangedEventArgs> _callback;

        public Unsubscriber(ConfigChangeToken owner, Action<ConfigChangedEventArgs> callback)
        {
            _owner = owner;
            _callback = callback;
        }

        public void Dispose()
        {
            lock (_owner._gate)
            {
                _owner._callbacks.Remove(_callback);
            }
        }
    }
}

/// <summary>
/// Retry policy for reloading a config generation that failed to load, mirroring the TUI watcher (0.8.10
/// <c>services/config_watch.py</c>): the same bad generation is retried on a doubling delay capped at
/// <see cref="MaxDelay"/>, and only the first failure of a generation is worth a banner. A new generation (a different
/// generation key) bypasses the wait. Not thread-safe on its own; the caller serializes.
/// </summary>
public sealed class ConfigReloadBackoff
{
    public static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(4);

    /// <summary>Retries stop here for one generation; a file that still fails has not become readable by waiting.</summary>
    public const int MaxAttempts = 6;

    /// <summary>A retry timer may fire a few milliseconds before its due time; that still counts as due.</summary>
    private static readonly TimeSpan TimerSlack = TimeSpan.FromMilliseconds(50);

    private readonly TimeSpan _baseDelay;
    private string? _failedKey;
    private int _attempts;
    private DateTime _retryAtUtc;

    public ConfigReloadBackoff(TimeSpan? baseDelay = null)
    {
        _baseDelay = baseDelay ?? ConfigChangeToken.DefaultSettleDelay;
    }

    /// <summary>False while <paramref name="generationKey"/> is the failed generation and its retry time has not come.</summary>
    public bool ShouldAttempt(string generationKey, DateTime nowUtc) =>
        !string.Equals(generationKey, _failedKey, StringComparison.Ordinal) || nowUtc + TimerSlack >= _retryAtUtc;

    /// <summary>
    /// Records a failed load. <paramref name="first"/> is true for the first failure of this generation (show the banner);
    /// the returned delay is when to try again, or null once the attempts are used up.
    /// </summary>
    public TimeSpan? RecordFailure(string generationKey, DateTime nowUtc, out bool first)
    {
        first = !string.Equals(generationKey, _failedKey, StringComparison.Ordinal);
        if (first)
        {
            _failedKey = generationKey;
            _attempts = 0;
        }

        _attempts++;
        var delay = TimeSpan.FromTicks(Math.Min(_baseDelay.Ticks << Math.Min(_attempts - 1, 20), MaxDelay.Ticks));
        _retryAtUtc = nowUtc + delay;
        return _attempts >= MaxAttempts ? null : delay;
    }

    /// <summary>A load worked: nothing is pending any more.</summary>
    public void RecordSuccess()
    {
        _failedKey = null;
        _attempts = 0;
        _retryAtUtc = default;
    }
}
