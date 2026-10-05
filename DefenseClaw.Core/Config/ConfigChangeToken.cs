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

    public ConfigChangeToken(DefenseClawPaths paths, TimeSpan? pollInterval = null)
        : this(paths?.ConfigFilePath!, paths?.EnvFilePath!, pollInterval)
    {
    }

    public ConfigChangeToken(string configPath, string envPath, TimeSpan? pollInterval = null)
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

        var interval = pollInterval ?? TimeSpan.FromSeconds(2);
        _poll = new Timer(_ => CheckAll(), null, interval, interval);
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

    /// <summary>Forces an immediate signature comparison instead of waiting for the poll.</summary>
    public void Poll() => CheckAll();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _poll.Dispose();
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
            CheckAll();
        }
    }

    private bool Concerns(FileSystemEventArgs e) =>
        e.Name is not { } name ||
        _watchedNames.Contains(name) ||
        (e is RenamedEventArgs { OldName: { } oldName } && _watchedNames.Contains(oldName));

    private void CheckAll()
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
                    continue;
                }

                _signatures[kind] = current;
                HasChanged = true;
                args = new ConfigChangedEventArgs(kind, path);
            }

            Raise(args);
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
