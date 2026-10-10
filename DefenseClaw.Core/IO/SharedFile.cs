using System.Text;

namespace DefenseClaw.Core.IO;

/// <summary>
/// Reads a file that another process owns and rewrites (config.yaml and .env from the CLI, ai_discovery_state.json and the doctor cache from
/// the gateway and CLI, Claude Code's settings.json, Docker's settings) without getting in that process's way. <see cref="File.ReadAllText(string)"/>
/// opens with <see cref="FileShare.Read"/>, so a writer that saves during our read fails with a sharing violation (the CLI's
/// <c>open(path, "w")</c> raises PermissionError). These open with read/write/delete sharing instead; a reader racing a writer may see a
/// partly written file, which every caller already treats as a parse failure and retries on the next change.
/// <para>
/// Every read is bounded (CUST-250): a file over the limit (<see cref="ReadLimits.StateFileBytes"/> unless the caller passes its own) throws
/// <see cref="FileTooLargeException"/>, an <see cref="IOException"/>, without being read - and a file that grows past it mid-read stops there.
/// </para>
/// </summary>
public static class SharedFile
{
    private const FileShare ShareWithWriters = FileShare.ReadWrite | FileShare.Delete;

    /// <summary>The whole file as text: UTF-8 unless a byte-order mark says otherwise, as <see cref="File.ReadAllText(string)"/> does.</summary>
    public static string ReadAllText(string path, long maxBytes = ReadLimits.StateFileBytes)
    {
        using var stream = Open(path, FileOptions.SequentialScan);
        EnsureWithin(stream, path, maxBytes);
        using var reader = new StreamReader(new LimitedStream(stream, path, maxBytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <inheritdoc cref="ReadAllText(string, long)"/>
    public static async Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken = default, long maxBytes = ReadLimits.StateFileBytes)
    {
        var stream = Open(path, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using (stream.ConfigureAwait(false))
        {
            EnsureWithin(stream, path, maxBytes);
            using var reader = new StreamReader(new LimitedStream(stream, path, maxBytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The whole file as bytes.</summary>
    public static byte[] ReadAllBytes(string path, long maxBytes = ReadLimits.StateFileBytes)
    {
        using var stream = Open(path, FileOptions.SequentialScan);
        EnsureWithin(stream, path, maxBytes);
        using var buffer = new MemoryStream();
        new LimitedStream(stream, path, maxBytes).CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <inheritdoc cref="ReadAllBytes(string, long)"/>
    public static async Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken = default, long maxBytes = ReadLimits.StateFileBytes)
    {
        var stream = Open(path, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using (stream.ConfigureAwait(false))
        {
            EnsureWithin(stream, path, maxBytes);
            using var buffer = new MemoryStream();
            await new LimitedStream(stream, path, maxBytes).CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            return buffer.ToArray();
        }
    }

    /// <summary>The read-only handle the methods above use, shared with writers. Internal so a test can hold it open across a write.</summary>
    internal static FileStream Open(string path, FileOptions options) =>
        new(path, FileMode.Open, FileAccess.Read, ShareWithWriters, bufferSize: 4096, options);

    private static void EnsureWithin(FileStream stream, string path, long maxBytes)
    {
        if (stream.Length > maxBytes)
        {
            throw new FileTooLargeException(path, stream.Length, maxBytes);
        }
    }

    /// <summary>Stops a read at the limit: a file that grows while it is read (a writer appending) is not trusted to stay as long as it was.</summary>
    private sealed class LimitedStream(Stream inner, string path, long limit) : Stream
    {
        private long _read;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

        public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Count(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int Count(int read)
        {
            _read += read;
            return _read > limit ? throw new FileTooLargeException(path, _read, limit) : read;
        }
    }
}
