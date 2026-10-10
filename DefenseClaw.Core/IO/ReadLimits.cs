namespace DefenseClaw.Core.IO;

/// <summary>
/// The one place that says how much of anything this app reads before it refuses or truncates (CUST-250). Every read of a file, an HTTP body or a
/// child's output that something else controls goes through one of these, so a hostile or broken peer costs a clear message instead of memory or
/// minutes. Each is far above what a real install produces; none changes the normal case.
/// </summary>
public static class ReadLimits
{
    /// <summary>The most bytes of <c>config.yaml</c> parsed (1 MiB; a real one is a few KB). YamlDotNet is quadratic on nested flow collections.</summary>
    public const long ConfigYamlBytes = 1024 * 1024;

    /// <summary>The deepest <c>[</c> / <c>{</c> nesting <c>config.yaml</c> may have (a real one has none beyond two or three).</summary>
    public const int ConfigYamlFlowDepth = 32;

    /// <summary>The most bytes of <c>.env</c> read (1 MiB).</summary>
    public const long DotEnvBytes = 1024 * 1024;

    /// <summary>The most bytes of any other state, cache or discovery file read whole through <see cref="SharedFile"/> (16 MiB).</summary>
    public const long StateFileBytes = 16 * 1024 * 1024;

    /// <summary>The most bytes of an HTTP response body buffered whole (<c>HttpClient.MaxResponseContentBufferSize</c>, 16 MiB).</summary>
    public const long HttpBodyBytes = 16 * 1024 * 1024;

    /// <summary>The most characters of one line of a child's output kept (4 Mi chars); the rest of that line is dropped and the line says so.</summary>
    public const int CliLineChars = 4 * 1024 * 1024;

    /// <summary>What ends a line the child output reader cut short.</summary>
    public const string CliLineTruncatedMarker = " [... line truncated by DefenseClaw for Windows ...]";

    /// <summary>Applies <see cref="HttpBodyBytes"/> to a client that has not been used yet (the property cannot change after the first request).</summary>
    public static HttpClient WithBodyCap(this HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        client.MaxResponseContentBufferSize = HttpBodyBytes;
        return client;
    }

    /// <summary>A short size for a message: <c>1 MiB</c>, <c>16 MiB</c>, <c>512 KiB</c>.</summary>
    public static string Describe(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / (1024 * 1024)} MiB" : bytes >= 1024 ? $"{bytes / 1024} KiB" : $"{bytes} bytes";
}

/// <summary>A file is larger than the limit its reader works to. An <see cref="IOException"/>, so every existing "could not read it" handler already treats it as one.</summary>
public sealed class FileTooLargeException : IOException
{
    public FileTooLargeException(string path, long length, long limit)
        : base($"{System.IO.Path.GetFileName(path)} is {ReadLimits.Describe(length)}, over the {ReadLimits.Describe(limit)} limit this app reads.")
    {
        FilePath = path;
        Length = length;
        Limit = limit;
    }

    public string FilePath { get; }

    public long Length { get; }

    public long Limit { get; }
}
