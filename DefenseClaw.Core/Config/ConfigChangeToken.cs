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

    private static ulong HashContents(string path, long length)
    {
        if (length > MaxHashedBytes)
        {
            return 0;
        }

        try
        {
            var bytes = File.ReadAllBytes(path);
            var hash = System.Security.Cryptography.SHA256.HashData(bytes);
            return BitConverter.ToUInt64(hash, 0);
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
/// </summary>
public sealed class ConfigChangeToken : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<ConfigFileKind, string> _paths;
    private readonly Dictionary<ConfigFileKind, FileSignature> _signatures = new();
    private readonly List<Action<ConfigChangedEventArgs>> _callbacks = new();
    private readonly FileSystemWatcher? _watcher;
    private readonly Timer _poll;
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

    public event EventHandler<ConfigChangedEventArgs>? Changed;

    /// <summary>Subscribes a callback; dispose the return value to unsubscribe.</summary>
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

    private void OnFileSystemEvent(object sender, FileSystemEventArgs e) => CheckAll();

    private void CheckAll()
    {
        if (_disposed)
        {
            return;
        }

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
