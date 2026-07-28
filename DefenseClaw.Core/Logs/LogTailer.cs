using System.Runtime.CompilerServices;
using System.Text;

namespace DefenseClaw.Core.Logs;

public sealed class LogTailerOptions
{
    /// <summary>Backstop poll when the watcher misses an event. The mac app uses the same cadence.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Start at EOF (live tail) instead of replaying the whole file.</summary>
    public bool StartAtEnd { get; init; }

    /// <summary>Cap on lines returned per read, so a huge append cannot stall the UI thread.</summary>
    public int MaxLinesPerBatch { get; init; } = 2000;

    public Encoding Encoding { get; init; } = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
}

public sealed class LogLinesEventArgs : EventArgs
{
    public LogLinesEventArgs(IReadOnlyList<LogLine> lines)
    {
        Lines = lines;
    }

    public IReadOnlyList<LogLine> Lines { get; }
}

/// <summary>
/// Incremental tail of a log file.
/// <para>
/// Remembers a byte offset and only reads forward from it. If the file shrinks below the
/// remembered offset the file was truncated or rotated, so the offset resets to 0 and
/// <see cref="Truncated"/> fires — the app must clear its buffer rather than show a
/// spliced-together history.
/// </para>
/// <para>
/// A partial trailing line (no newline yet) is held back and re-emitted once the writer
/// completes it, so consumers never see half a line.
/// </para>
/// </summary>
public sealed class LogTailer : IDisposable
{
    private readonly LogTailerOptions _options;
    private readonly object _gate = new();
    /// <summary>Upper bound on a single read, so one huge append cannot stall the caller.</summary>
    private const int MaxReadBytes = 1 << 20;

    private readonly SemaphoreSlim _signal = new(0, 1);
    private FileSystemWatcher? _watcher;
    private long _sequence;
    private bool _disposed;

    public LogTailer(string path, LogTailerOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        Path = path;
        _options = options ?? new LogTailerOptions();

        if (_options.StartAtEnd)
        {
            var info = new FileInfo(path);
            Offset = info.Exists ? info.Length : 0;
        }
    }

    public string Path { get; }

    /// <summary>Byte offset of the next unread line.</summary>
    public long Offset { get; private set; }

    /// <summary>Raised for every non-empty batch produced by <see cref="StartWatching"/>.</summary>
    public event EventHandler<LogLinesEventArgs>? LinesReceived;

    /// <summary>Raised when the file shrinks — truncation or rotation. Offset is already reset.</summary>
    public event EventHandler? Truncated;

    /// <summary>Rewinds so the next read replays the file from the beginning.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            Offset = 0;
        }
    }

    /// <summary>
    /// Reads whatever has been appended since the last call. Returns an empty list when
    /// there is nothing new or the file does not exist yet.
    /// </summary>
    public IReadOnlyList<LogLine> ReadNewLines()
    {
        lock (_gate)
        {
            var info = new FileInfo(Path);
            if (!info.Exists)
            {
                // File removed: next appearance starts from the top.
                if (Offset != 0)
                {
                    Offset = 0;
                    RaiseTruncated();
                }

                return Array.Empty<LogLine>();
            }

            var truncated = false;
            if (info.Length < Offset)
            {
                Offset = 0;
                truncated = true;
            }

            if (info.Length == Offset)
            {
                if (truncated)
                {
                    RaiseTruncated();
                }

                return Array.Empty<LogLine>();
            }

            List<LogLine> lines;
            try
            {
                lines = ReadFrom(info);
            }
            catch (IOException)
            {
                // The writer holds the file momentarily; the next poll picks it up.
                return Array.Empty<LogLine>();
            }
            catch (UnauthorizedAccessException)
            {
                return Array.Empty<LogLine>();
            }

            if (truncated)
            {
                RaiseTruncated();
            }

            return lines;
        }
    }

    /// <summary>
    /// Streams batches until cancelled. Wakes on watcher events and, failing that, on the
    /// poll interval.
    /// </summary>
    public async IAsyncEnumerable<IReadOnlyList<LogLine>> TailAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EnsureWatcher();

        while (!cancellationToken.IsCancellationRequested)
        {
            var lines = ReadNewLines();
            if (lines.Count > 0)
            {
                yield return lines;
                continue;
            }

            try
            {
                await _signal.WaitAsync(_options.PollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
        }
    }

    /// <summary>Event-based alternative to <see cref="TailAsync"/>, for the WPF layer.</summary>
    public void StartWatching()
    {
        EnsureWatcher();
        _ = Task.Run(async () =>
        {
            while (!_disposed)
            {
                var lines = ReadNewLines();
                if (lines.Count > 0)
                {
                    LinesReceived?.Invoke(this, new LogLinesEventArgs(lines));
                    continue;
                }

                try
                {
                    await _signal.WaitAsync(_options.PollInterval).ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
            }
        });
    }

    private List<LogLine> ReadFrom(FileInfo info)
    {
        var lines = new List<LogLine>();

        using var stream = new FileStream(
            Path,
            FileMode.Open,
            FileAccess.Read,
            // The gateway keeps the file open for append; anything stricter fails on Windows.
            FileShare.ReadWrite | FileShare.Delete);

        stream.Seek(Offset, SeekOrigin.Begin);

        var available = Math.Max(0, info.Length - Offset);
        var buffer = new byte[(int)Math.Min(available, MaxReadBytes)];
        var read = stream.Read(buffer, 0, buffer.Length);
        if (read <= 0)
        {
            return lines;
        }

        // Work in bytes and cut on the last newline: decoding a buffer that ends mid
        // UTF-8 sequence would corrupt the character (gateway.log opens with a
        // box-drawing banner, so this is not hypothetical).
        var consumedBytes = 0;
        var newlines = 0;
        for (var i = 0; i < read; i++)
        {
            if (buffer[i] != (byte)'\n')
            {
                continue;
            }

            consumedBytes = i + 1;
            if (++newlines >= _options.MaxLinesPerBatch)
            {
                break;
            }
        }

        if (consumedBytes == 0)
        {
            // No complete line yet. Only force-emit if a single line has outgrown the
            // read window, otherwise wait for the writer to finish it.
            if (read < MaxReadBytes)
            {
                return lines;
            }

            consumedBytes = read;
        }

        var text = _options.Encoding.GetString(buffer, 0, consumedBytes);
        var parts = text.Split('\n');

        // Split leaves a trailing empty element when the text ends with '\n'.
        var count = text.EndsWith('\n') ? parts.Length - 1 : parts.Length;
        for (var i = 0; i < count; i++)
        {
            lines.Add(LogLine.Parse(parts[i].TrimEnd('\r'), _sequence++));
        }

        Offset += consumedBytes;
        return lines;
    }

    private void EnsureWatcher()
    {
        if (_watcher is not null || _disposed)
        {
            return;
        }

        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path));
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return;
        }

        _watcher = new FileSystemWatcher(directory, System.IO.Path.GetFileName(Path))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            IncludeSubdirectories = false,
        };

        _watcher.Changed += (_, _) => Wake();
        _watcher.Created += (_, _) => Wake();
        _watcher.Deleted += (_, _) => Wake();
        _watcher.Renamed += (_, _) => Wake();
        _watcher.Error += (_, _) => { };
        _watcher.EnableRaisingEvents = true;
    }

    private void Wake()
    {
        try
        {
            if (_signal.CurrentCount == 0)
            {
                _signal.Release();
            }
        }
        catch (SemaphoreFullException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void RaiseTruncated() => Truncated?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }

        _signal.Dispose();
    }
}
