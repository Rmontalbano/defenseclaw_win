using System.Text;

namespace DefenseClaw.Core.IO;

/// <summary>
/// Reads a file that another process owns and rewrites (config.yaml and .env from the CLI, ai_discovery_state.json and the doctor cache from
/// the gateway and CLI, Claude Code's settings.json, Docker's settings) without getting in that process's way. <see cref="File.ReadAllText(string)"/>
/// opens with <see cref="FileShare.Read"/>, so a writer that saves during our read fails with a sharing violation (the CLI's
/// <c>open(path, "w")</c> raises PermissionError). These open with read/write/delete sharing instead; a reader racing a writer may see a
/// partly written file, which every caller already treats as a parse failure and retries on the next change.
/// </summary>
public static class SharedFile
{
    private const FileShare ShareWithWriters = FileShare.ReadWrite | FileShare.Delete;

    /// <summary>The whole file as text: UTF-8 unless a byte-order mark says otherwise, as <see cref="File.ReadAllText(string)"/> does.</summary>
    public static string ReadAllText(string path)
    {
        using var stream = Open(path, FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <inheritdoc cref="ReadAllText(string)"/>
    public static async Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken = default)
    {
        var stream = Open(path, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using (stream.ConfigureAwait(false))
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The whole file as bytes.</summary>
    public static byte[] ReadAllBytes(string path)
    {
        using var stream = Open(path, FileOptions.SequentialScan);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <inheritdoc cref="ReadAllBytes(string)"/>
    public static async Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken = default)
    {
        var stream = Open(path, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using (stream.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            return buffer.ToArray();
        }
    }

    /// <summary>The read-only handle the methods above use, shared with writers. Internal so a test can hold it open across a write.</summary>
    internal static FileStream Open(string path, FileOptions options) =>
        new(path, FileMode.Open, FileAccess.Read, ShareWithWriters, bufferSize: 4096, options);
}
