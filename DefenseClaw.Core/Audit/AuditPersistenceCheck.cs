using System.Globalization;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.Core.Audit;

/// <summary>What the post-upgrade check found in <c>audit.db</c> (CUST-359).</summary>
public enum AuditPersistenceState
{
    /// <summary>Not looked at: there was no gateway start to look after.</summary>
    NotChecked,

    /// <summary>At least one event was written after the gateway started: audit persistence works.</summary>
    Persisted,

    /// <summary>The database answered, and holds nothing written since the gateway started.</summary>
    NoNewRow,

    /// <summary>There is no <c>audit.db</c> yet.</summary>
    Missing,

    /// <summary>The database was held by a writer for longer than the check waits (<c>SQLITE_BUSY</c>/<c>SQLITE_LOCKED</c>): retention cleanup or a burst of writes.</summary>
    Busy,

    /// <summary>The read failed for another reason; the SQLite error code is in the text. Never read as damage.</summary>
    Unreadable,
}

/// <summary>The answer, with one sentence to show and, when found, the newest event since the start.</summary>
public sealed record AuditPersistence(AuditPersistenceState State, string Text, DateTimeOffset? NewestAt = null, string? NewestAction = null)
{
    public static AuditPersistence NotChecked(string why) => new(AuditPersistenceState.NotChecked, why);

    /// <summary>True when an event written after the gateway started was seen.</summary>
    public bool IsVerified => State == AuditPersistenceState.Persisted;

    /// <summary>True when the check ran and did not see one (the result card calls that degraded).</summary>
    public bool IsDegraded => State is not (AuditPersistenceState.Persisted or AuditPersistenceState.NotChecked);
}

/// <summary>
/// Does the gateway actually keep its audit history? After an upgrade a gateway can answer <c>/health</c> with 200 while nothing reaches
/// <c>audit.db</c>, so the post-flight asks the database itself (CUST-359): is there an event at or after the gateway's start (it writes
/// <c>sidecar-start</c> when it starts)? Read-only (<c>Mode=ReadOnly</c>), unpooled (no handle stays open after it), and patient only for
/// <paramref name="busyTimeoutSeconds"/>: a writer that holds the file longer - the retention reaper on a large database starves the audit
/// writer this way - is reported as busy, never as damage. This changes nothing in the database: no pragma, no cleanup, no journal change.
/// </summary>
public static class AuditPersistenceCheck
{
    /// <summary>The action the gateway records when it starts (<c>audit.ActionSidecarStart</c>).</summary>
    public const string SidecarStartAction = "sidecar-start";

    /// <summary>How long the check waits for a lock.</summary>
    public const int DefaultBusyTimeoutSeconds = 2;

    /// <summary>An allowance for the gateway's clock against the stamp it writes.</summary>
    private static readonly TimeSpan Slack = TimeSpan.FromSeconds(2);

    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;

    public static async Task<AuditPersistence> CheckAsync(
        string databasePath,
        DateTimeOffset since,
        int busyTimeoutSeconds = DefaultBusyTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(databasePath);

        try
        {
            if (!File.Exists(databasePath))
            {
                return new AuditPersistence(AuditPersistenceState.Missing, "audit.db does not exist yet, so no event of the new gateway can be seen.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new AuditPersistence(AuditPersistenceState.Unreadable, "audit.db could not be looked at (" + ex.GetType().Name + ").");
        }

        var from = since - Slack;
        var nanos = (from.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) * 100;
        var text = from.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = Math.Max(1, busyTimeoutSeconds),
        }.ToString();

        try
        {
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandTimeout = Math.Max(1, busyTimeoutSeconds);

            // The retention column is the indexed one; a row its trigger missed is compared on the text stamp (RFC 3339 UTC sorts as text).
            command.CommandText = """
                SELECT action, timestamp,
                       EXISTS (SELECT 1 FROM audit_events WHERE action = $start AND (retention_timestamp_unix_nano >= $nanos
                               OR (retention_timestamp_unix_nano IS NULL AND timestamp >= $text)))
                FROM audit_events
                WHERE retention_timestamp_unix_nano >= $nanos OR (retention_timestamp_unix_nano IS NULL AND timestamp >= $text)
                ORDER BY retention_timestamp_unix_nano DESC
                LIMIT 1
                """;
            command.Parameters.AddWithValue("$nanos", nanos);
            command.Parameters.AddWithValue("$text", text);
            command.Parameters.AddWithValue("$start", SidecarStartAction);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return new AuditPersistence(
                    AuditPersistenceState.NoNewRow,
                    "audit.db answered, but holds no event written since the gateway started at " + Clock(since) + " (not even its start record).");
            }

            var action = reader.IsDBNull(0) ? null : reader.GetString(0);
            DateTimeOffset? at = !reader.IsDBNull(1) &&
                                 DateTimeOffset.TryParse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed
                : null;
            var started = !reader.IsDBNull(2) && reader.GetInt64(2) != 0;
            var newest = action is { Length: > 0 and <= 64 } a ? a : "an event";
            return new AuditPersistence(
                AuditPersistenceState.Persisted,
                (started ? "The gateway's start was recorded in audit.db" : "audit.db has events written since the gateway started") +
                $"; the newest is {newest}" + (at is { } when ? " at " + Clock(when) : string.Empty) + ".",
                at,
                action);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is SqliteBusy or SqliteLocked)
        {
            return new AuditPersistence(
                AuditPersistenceState.Busy,
                $"audit.db was held by a writer for more than {Math.Max(1, busyTimeoutSeconds).ToString(CultureInfo.InvariantCulture)} s (SQLite busy), so this " +
                "check could not see the newest events. The gateway's retention cleanup can hold it that long; that is contention, not damage. Refresh to look again.");
        }
        catch (SqliteException ex) when (ex.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase))
        {
            return new AuditPersistence(AuditPersistenceState.NoNewRow, "audit.db has no audit_events table yet, so nothing has been recorded in it.");
        }
        catch (SqliteException ex)
        {
            return new AuditPersistence(
                AuditPersistenceState.Unreadable,
                $"audit.db could not be read for this check (SQLite error {ex.SqliteErrorCode.ToString(CultureInfo.InvariantCulture)}). One failed read says nothing " +
                "about the state of the database, and this app changes nothing in it.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new AuditPersistence(AuditPersistenceState.Unreadable, "audit.db could not be opened for this check (" + ex.GetType().Name + ").");
        }
    }

    private static string Clock(DateTimeOffset when) => when.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
}
