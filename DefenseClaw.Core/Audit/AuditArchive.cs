using System.Text;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.Core.Audit;

/// <summary>
/// What <see cref="AuditArchive.InspectAsync"/> found out about a candidate archive.
/// </summary>
/// <param name="FullPath">The absolute path that was checked (empty when the input was blank).</param>
/// <param name="Problem">Why the file cannot be used as an archive, in a sentence an operator can act on; null when it can.</param>
/// <param name="Newest">The timestamp of the newest event in the archive; null when it has none (or when it is unusable).</param>
/// <param name="SchemaVersion">The highest <c>schema_version.version</c> row, when the file has one (a 0.8.10 database is 29); informational only.</param>
public sealed record AuditArchiveCheck(string FullPath, string? Problem, DateTimeOffset? Newest, int? SchemaVersion)
{
    /// <summary>The file passed every check.</summary>
    public bool IsUsable => Problem is null;
}

/// <summary>
/// Checks and opens an <b>archived</b> audit database: the <c>audit.db</c> of an earlier DefenseClaw (a 0.8.10 database, schema 29),
/// kept outside the live data folder so an upgrade that purges the live history cannot touch it.
/// <para>
/// <b>The archive is never written.</b> Every open here is <c>mode=ro&amp;immutable=1</c>
/// (<see cref="AuditReader.BuildImmutableConnectionString"/>), so SQLite takes no lock and creates no <c>-wal</c>, <c>-shm</c> or
/// <c>-journal</c> beside it; the checks before that read the file's first sixteen bytes and nothing else.
/// </para>
/// <para>
/// <b>A path inside the live data folder is refused</b> (the folder the app is reading right now, wherever <c>DEFENSECLAW_HOME</c>
/// put it): the live <c>audit.db</c> is being written by the gateway, which an immutable open must never be pointed at, and a
/// file kept there is exactly what an upgrade purges.
/// </para>
/// </summary>
public static class AuditArchive
{
    /// <summary>The first sixteen bytes of every SQLite database file.</summary>
    private static readonly byte[] SqliteHeader = Encoding.ASCII.GetBytes("SQLite format 3\0");

    /// <summary>
    /// The checks that need no database: a usable absolute path, outside <paramref name="dataDirectory"/>, naming an existing
    /// file that starts with the SQLite header. Returns the reason it is refused, or null.
    /// </summary>
    public static string? ValidatePath(string? path, string dataDirectory, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return "Choose the archived audit database file.";
        }

        var trimmed = path.Trim().Trim('"');
        try
        {
            if (!Path.IsPathFullyQualified(trimmed))
            {
                return "Use the full path to the archive (for example C:\\Users\\you\\DefenseClaw-archive\\audit.db).";
            }

            fullPath = Path.GetFullPath(trimmed);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return $"That is not a usable file path: {ex.Message}";
        }

        if (IsInside(fullPath, dataDirectory) || IsInside(Resolve(fullPath), dataDirectory))
        {
            return $"The archive must be kept outside the live DefenseClaw folder ({dataDirectory}): an upgrade purges history there, and the live database is being written. Copy it somewhere else first.";
        }

        if (Directory.Exists(fullPath))
        {
            return "That is a folder. Choose the archived audit.db file itself.";
        }

        if (!File.Exists(fullPath))
        {
            return "That file does not exist.";
        }

        try
        {
            var header = new byte[SqliteHeader.Length];
            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var read = 0;
            while (read < header.Length)
            {
                var n = stream.Read(header, read, header.Length - read);
                if (n == 0)
                {
                    break;
                }

                read += n;
            }

            if (read < header.Length || !header.AsSpan().SequenceEqual(SqliteHeader))
            {
                return "That file is not a SQLite database.";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"That file could not be read: {ex.Message}";
        }

        return null;
    }

    /// <summary>A reader over the archive, opened immutable. The caller has validated the path (a bad one fails when it is queried).</summary>
    public static AuditReader OpenReader(string fullPath) => new(fullPath, immutable: true);

    /// <summary>
    /// <see cref="ValidatePath"/>, then a read of the newest event through an immutable <see cref="AuditReader"/> (which is the
    /// schema check: it selects every column the Audit panel shows, so a file that is not a DefenseClaw audit database, or is
    /// corrupt, fails here with the engine's own message). Never throws for a bad file; a cancelled token propagates.
    /// </summary>
    public static async Task<AuditArchiveCheck> InspectAsync(string? path, string dataDirectory, CancellationToken cancellationToken = default)
    {
        if (ValidatePath(path, dataDirectory, out var fullPath) is { } problem)
        {
            return new AuditArchiveCheck(fullPath, problem, null, null);
        }

        try
        {
            var reader = OpenReader(fullPath);
            var page = await reader.QueryAsync(new AuditQuery { Limit = 1 }, cancellationToken).ConfigureAwait(false);
            DateTimeOffset? newest = null;
            if (page.Events.Count > 0 && page.Events[0].Timestamp != DateTimeOffset.MinValue)
            {
                newest = page.Events[0].Timestamp;
            }

            return new AuditArchiveCheck(fullPath, null, newest, await ReadSchemaVersionAsync(fullPath, cancellationToken).ConfigureAwait(false));
        }
#pragma warning disable CA1031 // Whatever the engine says about a bad file becomes the reason, never a crash.
        catch (Exception ex) when (ex is SqliteException or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return new AuditArchiveCheck(fullPath, Describe(ex), null, null);
        }
#pragma warning restore CA1031
    }

    /// <summary>The reason an archive read failed, in words: locked, not a database, or missing the audit tables.</summary>
    public static string Describe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is SqliteException sqlite)
        {
            return sqlite.SqliteErrorCode switch
            {
                5 or 6 => $"The archive is locked by another program ({sqlite.Message}).",
                11 or 26 => $"The archive is corrupt or is not a SQLite database ({sqlite.Message}).",
                _ => $"The archive could not be read as a DefenseClaw audit database ({sqlite.Message}).",
            };
        }

        return $"The archive could not be read: {exception.Message}";
    }

    private static async Task<int?> ReadSchemaVersionAsync(string fullPath, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new SqliteConnection(AuditReader.BuildImmutableConnectionString(fullPath));
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT MAX(version) FROM schema_version";
            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return value is null or DBNull ? null : Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
        }
#pragma warning disable CA1031 // The version is a nicety; an archive without the table is still an archive.
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException or FormatException or OverflowException)
        {
            return null;
        }
#pragma warning restore CA1031
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="directory"/> or below it (ordinal, case-insensitive: Windows paths).</summary>
    private static bool IsInside(string path, string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        string root;
        try
        {
            root = Path.GetFullPath(directory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        root = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The file a link points at (so a shortcut-like link into the live folder is caught); the path itself when it is not a link.</summary>
    private static string Resolve(string fullPath)
    {
        try
        {
            return new FileInfo(fullPath).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? fullPath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return fullPath;
        }
    }
}
