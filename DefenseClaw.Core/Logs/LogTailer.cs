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

    /// <summary>
    /// How long <see cref="LogTailer.StartWatching"/> waits after its first fault before trying again; it doubles with
    /// every consecutive fault up to <see cref="MaxFaultBackoff"/>, and resets once an iteration succeeds.
    /// </summary>
    public TimeSpan FaultBackoff { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Ceiling of the fault backoff, so a persistent fault costs one retry every half minute rather than a busy loop.</summary>
    public TimeSpan MaxFaultBackoff { get; init; } = TimeSpan.FromSeconds(30);
}

public sealed class LogLinesEventArgs : EventArgs
{
    public LogLinesEventArgs(IReadOnlyList<LogLine> lines)
    {
        Lines = lines;
    }

    public IReadOnlyList<LogLine> Lines { get; }
}

/// <summary>What <see cref="LogTailer.TailFaulted"/> reports: the fault, how many in a row, and when the next attempt is.</summary>
public sealed class LogTailFaultedEventArgs : EventArgs
{
    public LogTailFaultedEventArgs(Exception exception, int consecutiveFaults, TimeSpan retryIn)
    {
        Exception = exception;
        ConsecutiveFaults = consecutiveFaults;
        RetryIn = retryIn;
    }

    public Exception Exception { get; }

    /// <summary>1 for the first fault after a healthy stretch; counts up while the tail keeps failing.</summary>
    public int ConsecutiveFaults { get; }

    /// <summary>The backoff before the tail tries again.</summary>
    public TimeSpan RetryIn { get; }
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
/// <b>Rotation to a file that is already longer than the offset</b> is not a shrink, so length alone
/// cannot see it, and a slow reader (or a rotation right after a burst) makes it real. Three cheap
/// checks catch it instead: the creation time, the first <see cref="AnchorBytes"/> bytes of the file, and
/// the last bytes consumed (the ones just before the offset). NTFS "tunneling" gives a file recreated
/// within about 15 seconds of the old one's name the old creation time, and a gateway log opens with the
/// same banner every time, so no single check is trusted on its own.
/// </para>
/// <para>
/// A partial trailing line (no newline yet) is held back and re-emitted once the writer
/// completes it, so consumers never see half a line. A UTF-8 byte order mark at the start of the file
/// is not part of the first line.
/// </para>
/// </summary>
public sealed class LogTailer : IDisposable
{
    private readonly LogTailerOptions _options;
    private readonly object _gate = new();
    /// <summary>Upper bound on a single read, so one huge append cannot stall the caller.</summary>
    private const int MaxReadBytes = 1 << 20;

    /// <summary>How many bytes at the start of the file, and just before the offset, are remembered to spot a replaced file.</summary>
    private const int AnchorBytes = 64;

    private static readonly byte[] Utf8Bom = { 0xEF, 0xBB, 0xBF };

    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly CancellationTokenSource _stop = new();
    private FileSystemWatcher? _watcher;
    private long _sequence;
    private volatile bool _disposed;
    private int _watchStarted;

    // Identity of the file the offset belongs to. Guarded by _gate.
    private byte[] _head = Array.Empty<byte>();
    private byte[] _tail = Array.Empty<byte>();
    private DateTime _createdUtc;

    public LogTailer(string path, LogTailerOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        Path = path;
        _options = options ?? new LogTailerOptions();

        if (_options.StartAtEnd)
        {
            var info = new FileInfo(path);
            Offset = info.Exists ? info.Length : 0;
            if (Offset > 0)
            {
                _createdUtc = info.CreationTimeUtc;
                CaptureAnchorsAtOffset();
            }
        }
    }

    public string Path { get; }

    /// <summary>Byte offset of the next unread line.</summary>
    public long Offset { get; private set; }

    /// <summary>Raised for every non-empty batch produced by <see cref="StartWatching"/>.</summary>
    public event EventHandler<LogLinesEventArgs>? LinesReceived;

    /// <summary>Raised when the file shrinks — truncation or rotation. Offset is already reset.</summary>
    public event EventHandler? Truncated;

    /// <summary>
    /// Raised by <see cref="StartWatching"/> when an iteration throws — a subscriber of <see cref="LinesReceived"/>,
    /// or an unexpected read error. The tail is not over: it waits out the backoff and carries on. Raised on the
    /// background thread; an exception from a handler of this event is swallowed.
    /// </summary>
    public event EventHandler<LogTailFaultedEventArgs>? TailFaulted;

    /// <summary>Raised on the first clean iteration after <see cref="TailFaulted"/>: the tail is healthy again.</summary>
    public event EventHandler? TailRecovered;

    /// <summary>Rewinds so the next read replays the file from the beginning.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            Offset = 0;
            ForgetIdentity();
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
                ForgetIdentity();
                if (Offset != 0)
                {
                    Offset = 0;
                    RaiseTruncated();
                }

                return Array.Empty<LogLine>();
            }

            var truncated = false;
            if (info.Length < Offset || (Offset > 0 && _createdUtc != default && info.CreationTimeUtc != _createdUtc))
            {
                Offset = 0;
                ForgetIdentity();
                truncated = true;
            }

            if (Offset == 0)
            {
                _createdUtc = info.CreationTimeUtc;
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
                lines = ReadFrom(info, ref truncated);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The writer holds the file momentarily; the next poll picks it up. A rotation already
                // noticed is still announced: the offset is back at 0, so the next read would not see it again.
                if (truncated)
                {
                    RaiseTruncated();
                }

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

    /// <summary>
    /// Event-based alternative to <see cref="TailAsync"/>, for the WPF layer. Safe to call more than once: only the
    /// first call starts a loop (a second one would deliver every line twice).
    /// <para>
    /// The loop survives faults. A subscriber that throws, or a read that fails unexpectedly, raises
    /// <see cref="TailFaulted"/>, waits out a growing backoff (<see cref="LogTailerOptions.FaultBackoff"/>) and carries
    /// on from the same offset; before, the first exception ended the tail without a word and the log went quiet.
    /// </para>
    /// </summary>
    public void StartWatching()
    {
        if (_disposed || Interlocked.Exchange(ref _watchStarted, 1) == 1)
        {
            return;
        }

        EnsureWatcher();
        _ = Task.Run(WatchLoopAsync);
    }

    private async Task WatchLoopAsync()
    {
        var faults = 0;
        while (!_disposed)
        {
            bool hadLines;
            try
            {
                var lines = ReadNewLines();
                hadLines = lines.Count > 0;
                if (hadLines)
                {
                    LinesReceived?.Invoke(this, new LogLinesEventArgs(lines));
                }
            }
            catch (Exception ex)
            {
                if (_disposed)
                {
                    return;
                }

                faults++;
                var delay = BackoffFor(faults);
                RaiseTailFaulted(ex, faults, delay);

                try
                {
                    await Task.Delay(delay, _stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            if (faults > 0)
            {
                faults = 0;
                RaiseTailRecovered();
            }

            if (hadLines)
            {
                continue;
            }

            try
            {
                await _signal.WaitAsync(_options.PollInterval, _stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ObjectDisposedException or OperationCanceledException)
            {
                return;
            }
        }
    }

    private TimeSpan BackoffFor(int consecutiveFaults)
    {
        var ticks = _options.FaultBackoff.Ticks;
        for (var i = 1; i < consecutiveFaults && ticks < _options.MaxFaultBackoff.Ticks; i++)
        {
            ticks *= 2;
        }

        return TimeSpan.FromTicks(Math.Min(ticks, _options.MaxFaultBackoff.Ticks));
    }

    private void RaiseTailFaulted(Exception exception, int consecutiveFaults, TimeSpan retryIn)
    {
        try
        {
            TailFaulted?.Invoke(this, new LogTailFaultedEventArgs(exception, consecutiveFaults, retryIn));
        }
        catch (Exception)
        {
            // Reporting a fault must not become the fault that ends the loop.
        }
    }

    private void RaiseTailRecovered()
    {
        try
        {
            TailRecovered?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception)
        {
        }
    }

    private List<LogLine> ReadFrom(FileInfo info, ref bool truncated)
    {
        var lines = new List<LogLine>();

        using var stream = new FileStream(
            Path,
            FileMode.Open,
            FileAccess.Read,
            // The gateway keeps the file open for append; anything stricter fails on Windows.
            FileShare.ReadWrite | FileShare.Delete);

        if (Offset > 0 && !StillTheSameFile(stream))
        {
            // Replaced by a file that is already longer than the offset: read the new one from the top.
            Offset = 0;
            ForgetIdentity();
            _createdUtc = info.CreationTimeUtc;
            truncated = true;
        }

        var startOffset = Offset;
        stream.Seek(startOffset, SeekOrigin.Begin);

        var available = Math.Max(0, info.Length - startOffset);
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

        // A byte order mark belongs to the file, not to its first line: left in, it prefixes "[gateway]" and the
        // component parse misses it. It still counts as consumed, so the offset moves past it.
        var skip = startOffset == 0 && _options.Encoding.CodePage == Encoding.UTF8.CodePage && StartsWithBom(buffer, consumedBytes)
            ? Utf8Bom.Length
            : 0;

        var text = _options.Encoding.GetString(buffer, skip, consumedBytes - skip);
        var parts = text.Split('\n');

        // Split leaves a trailing empty element when the text ends with '\n'.
        var count = text.EndsWith('\n') ? parts.Length - 1 : parts.Length;
        for (var i = 0; i < count; i++)
        {
            lines.Add(LogLine.Parse(parts[i].TrimEnd('\r'), _sequence++));
        }

        Offset += consumedBytes;
        RememberAnchors(startOffset, buffer, consumedBytes);
        return lines;
    }

    private static bool StartsWithBom(byte[] buffer, int length) =>
        length >= Utf8Bom.Length && buffer.AsSpan(0, Utf8Bom.Length).SequenceEqual(Utf8Bom);

    /// <summary>Drops what identifies the current file, so the next read starts a new identity (offset 0, or a file seen for the first time).</summary>
    private void ForgetIdentity()
    {
        _head = Array.Empty<byte>();
        _tail = Array.Empty<byte>();
        _createdUtc = default;
    }

    /// <summary>
    /// True while the file on disk still starts with the bytes remembered from its start and still holds the
    /// last bytes consumed just before the offset. A tail with nothing remembered yet (the tailer started at the
    /// end of the file and could not read it then) takes what is there now as its baseline.
    /// </summary>
    private bool StillTheSameFile(FileStream stream)
    {
        if (_head.Length == 0)
        {
            CaptureAnchors(stream);
            return true;
        }

        return BytesAt(stream, 0, _head) && BytesAt(stream, Offset - _tail.Length, _tail);
    }

    private static bool BytesAt(FileStream stream, long position, byte[] expected)
    {
        if (expected.Length == 0)
        {
            return true;
        }

        var actual = new byte[expected.Length];
        _ = stream.Seek(position, SeekOrigin.Begin);
        var got = 0;
        while (got < actual.Length)
        {
            var n = stream.Read(actual, got, actual.Length - got);
            if (n <= 0)
            {
                return false;
            }

            got += n;
        }

        return actual.AsSpan().SequenceEqual(expected);
    }

    /// <summary>Records the head and tail anchors for a tailer that started at the end of an existing file.</summary>
    private void CaptureAnchorsAtOffset()
    {
        try
        {
            using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            CaptureAnchors(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // No anchors yet; the first read takes them (StillTheSameFile).
        }
    }

    private void CaptureAnchors(FileStream stream)
    {
        var headLength = (int)Math.Min(AnchorBytes, Offset);
        var head = new byte[headLength];
        _ = stream.Seek(0, SeekOrigin.Begin);
        var got = stream.ReadAtLeast(head, headLength, throwOnEndOfStream: false);
        if (got < headLength)
        {
            return;
        }

        var tail = new byte[headLength];
        _ = stream.Seek(Offset - headLength, SeekOrigin.Begin);
        if (stream.ReadAtLeast(tail, headLength, throwOnEndOfStream: false) < headLength)
        {
            return;
        }

        _head = head;
        _tail = tail;
    }

    /// <summary>Keeps <c>_head</c> at the first 64 consumed bytes of the file and <c>_tail</c> at the last 64.</summary>
    private void RememberAnchors(long startOffset, byte[] buffer, int consumedBytes)
    {
        var expected = (int)Math.Min(AnchorBytes, startOffset);
        if (_head.Length != expected || _tail.Length != expected)
        {
            // Out of step (an anchor read failed earlier): start over, the next read takes a baseline.
            _head = Array.Empty<byte>();
            _tail = Array.Empty<byte>();
            return;
        }

        if (startOffset < AnchorBytes)
        {
            var take = Math.Min(AnchorBytes - (int)startOffset, consumedBytes);
            var head = new byte[_head.Length + take];
            Buffer.BlockCopy(_head, 0, head, 0, _head.Length);
            Buffer.BlockCopy(buffer, 0, head, _head.Length, take);
            _head = head;
        }

        var tailLength = Math.Min(AnchorBytes, _tail.Length + consumedBytes);
        var tail = new byte[tailLength];
        var fromBuffer = Math.Min(tailLength, consumedBytes);
        Buffer.BlockCopy(buffer, consumedBytes - fromBuffer, tail, tailLength - fromBuffer, fromBuffer);
        if (fromBuffer < tailLength)
        {
            var fromOld = tailLength - fromBuffer;
            Buffer.BlockCopy(_tail, _tail.Length - fromOld, tail, 0, fromOld);
        }

        _tail = tail;
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
        _stop.Cancel();
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }

        _signal.Dispose();
    }
}
