using System.Globalization;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.Core.Audit;

/// <summary>
/// What <see cref="AuditChangeProbe.Sample"/> read: an opaque value that is the same for two samples only if nothing was committed
/// to the database between them. Compare with <see cref="Matches"/>, never with <c>==</c>: an <see cref="Unknown"/> stamp (no
/// database, a probe that could not read, a probe that was disposed) matches nothing, itself included, so "I could not tell" is
/// always read as "changed".
/// </summary>
public readonly struct AuditStamp : IEquatable<AuditStamp>
{
    internal AuditStamp(long epoch, long version)
    {
        Epoch = epoch;
        Version = version;
    }

    /// <summary>"Could not tell": matches nothing.</summary>
    public static AuditStamp Unknown => default;

    /// <summary>True when the stamp was read from a database. An unknown stamp must not be remembered as a baseline.</summary>
    public bool IsKnown => Epoch != 0;

    /// <summary>Which connection (or, for an immutable file, which version of the file) the stamp is from; a stamp from another epoch never matches.</summary>
    public long Epoch { get; }

    /// <summary>The <c>PRAGMA data_version</c> of that connection (0 for an immutable file).</summary>
    public long Version { get; }

    /// <summary>True when this stamp and <paramref name="other"/> were both read from the same place and nothing was committed in between.</summary>
    public bool Matches(AuditStamp other) => IsKnown && other.IsKnown && Epoch == other.Epoch && Version == other.Version;

    /// <summary>Structural equality, for a dictionary key or a test; "nothing changed" is <see cref="Matches"/>, under which an unknown stamp is never equal to anything.</summary>
    public bool Equals(AuditStamp other) => Epoch == other.Epoch && Version == other.Version;

    public override bool Equals(object? obj) => obj is AuditStamp other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Epoch, Version);

    public override string ToString() =>
        IsKnown ? string.Create(CultureInfo.InvariantCulture, $"epoch {Epoch}, version {Version}") : "unknown";
}

/// <summary>
/// The cheapest honest answer to "has <c>audit.db</c> changed since I last looked?" - one trivial statement, or one file stat -
/// shared by every reader of the file (<see cref="AuditReader"/>, <see cref="AlertQueueReader"/>, <see cref="MutationReader"/>,
/// <see cref="EventStreamReader"/>, <see cref="NetworkEgressReader"/>) so a refresh that finds nothing new reuses the last result and
/// skips the query, the row decoding and the diff, and available to any panel that wants to poll without reading (the Audit live
/// refresh asks <see cref="SampleAsync"/> every few seconds and fetches only when the stamp moved).
/// <code>
/// var before = probe.Sample();
/// var rows = Read();                      // the stamp is taken BEFORE the read: a commit that lands in between only costs one redundant refresh
/// // ... later ...
/// if (!probe.Sample().Matches(before)) { /* something was committed */ }
/// </code>
/// <para>
/// <b>Live database: <c>PRAGMA data_version</c> on one kept read-only connection.</b> SQLite documents it as a value that differs
/// between two calls on the same connection exactly when another connection committed in between, in rollback-journal and WAL mode
/// alike. It never reads a table page, so it costs the same on a 0.6 GB file as on a 10.4 GB one (a whole unchanged read, probe included, is
/// 0.08-0.13 ms at both sizes; the open and the first sample are ~3 ms, the steady sample tens of microseconds), and sampled every 25 ms
/// against a separate process committing in WAL mode as fast as it could, and again every 20 ms, it moved for every one of 241 changes.
/// The alternative, the size and modified time of the database and its <c>-wal</c>, was measured against the same writers and is not
/// exact: the WAL file is reused after a checkpoint, so its <em>size</em> stops moving while the content keeps changing (73 of 241
/// commits left the length unchanged), and its modified time is a coarse clock tick, so two commits inside one tick look like one
/// (2 of 214 changes, commits 20 ms apart, left length and time both unchanged). A probe that answers "unchanged" wrongly leaves a
/// screen stale until the next change, so it has to be exact; being wrong the other way only costs a read.
/// </para>
/// <para>
/// <b>Why a kept connection is acceptable.</b> The value is only meaningful between two samples of the <em>same</em> connection, so
/// the connection has to live between samples (it has its own <see cref="AuditStamp.Epoch"/>: a reopened connection starts a new one,
/// and stamps of different epochs never match). It is read-only, it holds no transaction between statements (so it never holds
/// back a WAL checkpoint), and it adds no pin on the file that was not already there: the readers' pooled connections keep the
/// file open for the life of the process too (measured: still held 200 s after <c>Close()</c>, released only by <c>ClearAllPools</c>).
/// <see cref="Release"/> closes it for a caller that wants the handle back.
/// </para>
/// <para>
/// <b>Archive (immutable) database.</b> Opened <c>mode=ro&amp;immutable=1</c> a file is promised never to change, so nothing is opened:
/// the stamp is the file's size, modified time and creation time (a replaced file moves them). No sidecar is created, no lock taken.
/// </para>
/// <para>
/// <b>Never throws, never writes.</b> A database that is missing, locked past a second or unreadable gives <see cref="AuditStamp.Unknown"/>,
/// which every consumer reads as "changed", so the reader then runs its normal path and reports its own error. No timer: it does
/// something only when asked.
/// </para>
/// </summary>
public sealed class AuditChangeProbe : IDisposable
{
    /// <summary>How long a sample waits for a lock before it gives up and answers "unknown" (the reader that follows has its own, longer, patience).</summary>
    public const int BusyTimeoutSeconds = 1;

    private readonly object _gate = new();
    private readonly string _connectionString;
    private SqliteConnection? _connection;
    private (long Length, long Written, long Created)? _identity;
    private long _epoch;
    private long _samples;
    private long _opens;
    private bool _disposed;

    /// <param name="databasePath">Path to <c>audit.db</c> (or to the archive).</param>
    /// <param name="immutable">True for an archived copy opened <c>immutable=1</c>: the stamp is then the file's identity, and no connection is made.</param>
    public AuditChangeProbe(string databasePath, bool immutable = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(databasePath);
        DatabasePath = databasePath;
        IsImmutable = immutable;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,

            // Pooling off: the whole point is one connection of our own that stays the same connection.
            Pooling = false,
            DefaultTimeout = BusyTimeoutSeconds,
        }.ToString();
    }

    public string DatabasePath { get; }

    /// <summary>True when the stamp is the file's identity and no connection is ever made.</summary>
    public bool IsImmutable { get; }

    /// <summary>How many samples were taken; a diagnostic seam for the idle-cost tests.</summary>
    public long SampleCount => Interlocked.Read(ref _samples);

    /// <summary>How many connections the probe has opened: one for as long as it works, one more after each <see cref="Release"/> or failure.</summary>
    public long OpenCount => Interlocked.Read(ref _opens);

    /// <summary>True while the probe holds a connection open.</summary>
    public bool IsOpen
    {
        get
        {
            lock (_gate)
            {
                return _connection is not null;
            }
        }
    }

    /// <summary>
    /// Takes a stamp now. Synchronous and quick once the connection is open, but the first call opens it (milliseconds) and a locked
    /// database can hold it for up to <see cref="BusyTimeoutSeconds"/>: callers on the UI thread use <see cref="SampleAsync"/>.
    /// Safe from any thread (calls take turns).
    /// </summary>
    public AuditStamp Sample()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return AuditStamp.Unknown;
            }

            _ = Interlocked.Increment(ref _samples);
            return IsImmutable ? SampleFile() : SampleVersion();
        }
    }

    /// <summary><see cref="Sample"/> on a pool thread, so the caller's thread is free whatever the database is doing.</summary>
    public Task<AuditStamp> SampleAsync(CancellationToken cancellationToken = default) =>
        ReaderOffload.Run(() => Task.FromResult(Sample()), cancellationToken);

    /// <summary>
    /// Closes the kept connection and gives the file handle back. The probe still works: the next <see cref="Sample"/> opens a new
    /// connection with a new epoch, so every stamp taken before is stale (a reader that compares them re-reads once).
    /// </summary>
    public void Release()
    {
        lock (_gate)
        {
            CloseConnection();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            CloseConnection();
        }
    }

    private AuditStamp SampleVersion()
    {
        try
        {
            if (!File.Exists(DatabasePath))
            {
                // Nothing to watch yet (the gateway creates it with the first event), or it was removed: the old connection is of no use.
                CloseConnection();
                return AuditStamp.Unknown;
            }

            if (_connection is null)
            {
                var connection = new SqliteConnection(_connectionString);
                try
                {
                    connection.Open();
                }
                catch
                {
                    connection.Dispose();
                    throw;
                }

                _connection = connection;
                _epoch++;
                _ = Interlocked.Increment(ref _opens);
            }

            using var command = _connection.CreateCommand();
            command.CommandText = "PRAGMA data_version";
            var version = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            return new AuditStamp(_epoch, version);
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException or IOException or UnauthorizedAccessException or FormatException or OverflowException)
        {
            // Locked, unreadable, not a database: say "unknown" and start from a fresh connection next time.
            CloseConnection();
            return AuditStamp.Unknown;
        }
    }

    private AuditStamp SampleFile()
    {
        try
        {
            var info = new FileInfo(DatabasePath);
            if (!info.Exists)
            {
                _identity = null;
                return AuditStamp.Unknown;
            }

            var identity = (info.Length, info.LastWriteTimeUtc.Ticks, info.CreationTimeUtc.Ticks);
            if (_identity != identity)
            {
                // A different file (or the first look): a new epoch, so nothing remembered about the old one is reused.
                _identity = identity;
                _epoch++;
            }

            return new AuditStamp(_epoch, 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _identity = null;
            return AuditStamp.Unknown;
        }
    }

    private void CloseConnection()
    {
        var connection = _connection;
        _connection = null;
        connection?.Dispose();
    }
}
