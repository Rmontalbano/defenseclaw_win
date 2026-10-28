using System.Globalization;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.Core.Audit;

/// <summary>
/// Detection-only findings: a rule matched but could not decide the call, so the runtime records the finding and does not list it as an alert
/// (<c>db.py</c> <c>_detection_only_clause</c>, #693). They are tagged <c>detection-only</c> in the row's <c>payload_json</c>
/// (<c>defenseclaw.finding.tags</c>, an array or the bare string). Two uses: the alert queue leaves them out on a runtime that does
/// (<see cref="AlertQueueReader"/>), and <see cref="CountAsync"/> says how many the last day held, which the Alerts panel states
/// ("N detection-only findings of the last 24 hours are not listed"), as <c>defenseclaw alerts</c> and the Mac do.
/// </summary>
public static class DetectionOnlyFindings
{
    /// <summary>
    /// SQL that is true for a row tagged detection-only: an array tag or the whole scalar, any case or padding (whitespace is the six characters
    /// SQLite's <c>CHAR(9, 10, 11, 12, 13, 32)</c> names). Refers to the <c>payload_json</c> column of the row in scope; the caller checks the
    /// column exists. The <c>LIKE</c> in front is the cheap test that skips the JSON functions for nearly every row.
    /// </summary>
    internal const string Clause =
        """
        (
            COALESCE(payload_json, '') LIKE '%detection-only%'
            AND (
                EXISTS (
                    SELECT 1 FROM json_each(
                        CASE WHEN json_valid(COALESCE(payload_json, ''))
                              AND json_type(payload_json, '$."defenseclaw.finding.tags"') = 'array'
                             THEN json_extract(payload_json, '$."defenseclaw.finding.tags"')
                             ELSE '[]' END
                    ) AS finding_tag
                    WHERE LOWER(TRIM(CAST(finding_tag.value AS TEXT), CHAR(9, 10, 11, 12, 13, 32))) = 'detection-only'
                )
                OR LOWER(TRIM(COALESCE(
                    CASE WHEN json_valid(COALESCE(payload_json, '')) THEN json_extract(payload_json, '$."defenseclaw.finding.tags"') END, ''
                ), CHAR(9, 10, 11, 12, 13, 32))) = 'detection-only'
            )
        )
        """;

    /// <summary>
    /// How many detection-only findings were recorded at or after <paramref name="since"/> (UTC; the stored timestamps compare as text to the
    /// second, as the runtime's own count does). 0 for a missing database, one without <c>payload_json</c> (before schema 8), or none. Read-only,
    /// off the caller's thread, stoppable.
    /// </summary>
    /// <exception cref="SqliteException">The database is locked past the busy timeout, corrupt, or unreadable.</exception>
    public static Task<int> CountAsync(string databasePath, DateTimeOffset since, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(databasePath);
        return ReaderOffload.Run(() => CountCoreAsync(databasePath, since, cancellationToken), cancellationToken);
    }

    private static async Task<int> CountCoreAsync(string databasePath, DateTimeOffset since, CancellationToken cancellationToken)
    {
        if (!File.Exists(databasePath))
        {
            return 0;
        }

        await using var connection = new SqliteConnection(AuditReader.BuildReadOnlyConnectionString(databasePath));
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var interrupt = ReaderOffload.InterruptOnCancel(connection, cancellationToken);

        await using (var probe = connection.CreateCommand())
        {
            probe.CommandText = "SELECT COUNT(*) FROM pragma_table_info('audit_events') WHERE name = 'payload_json'";
            if (Convert.ToInt32(await probe.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) == 0)
            {
                return 0;
            }
        }

        await using var command = connection.CreateCommand();
        // nosemgrep: csharp-sqli -- Clause is a constant; the cutoff is bound
        command.CommandText = $"SELECT COUNT(*) FROM audit_events WHERE timestamp >= $since AND {Clause}";
        command.Parameters.AddWithValue("$since", since.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture));
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }
}
