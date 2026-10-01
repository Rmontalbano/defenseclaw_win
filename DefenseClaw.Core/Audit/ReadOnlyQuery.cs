using System.Globalization;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.Core.Audit;

/// <summary>
/// The shape every small <c>audit.db</c> reader of the Alerts inspector and the egress feed shares: the connection is
/// <c>Mode=ReadOnly</c> (the file is the gateway's), the work runs through <see cref="ReaderOffload"/> so the caller's thread
/// is free, and the caller's token <em>and</em> a timeout both end the running statement (<c>sqlite3_interrupt</c>).
/// A caller's cancellation surfaces as <see cref="OperationCanceledException"/>, a timeout as <see cref="TimeoutException"/>.
/// </summary>
internal static class ReadOnlyQuery
{
    /// <summary>How long a detail query may run unless the caller says otherwise.</summary>
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(8);

    /// <summary>
    /// Opens <paramref name="databasePath"/> read-only and runs <paramref name="body"/> on a pool thread. Returns
    /// <paramref name="whenMissing"/> without opening anything when the file does not exist.
    /// </summary>
    internal static async Task<T> RunAsync<T>(
        string databasePath,
        TimeSpan? timeout,
        CancellationToken cancellationToken,
        T whenMissing,
        Func<SqliteConnection, CancellationToken, Task<T>> body)
    {
        var limit = timeout ?? DefaultTimeout;
        if (limit <= TimeSpan.Zero && limit != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), limit, "The timeout must be positive.");
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (limit != Timeout.InfiniteTimeSpan)
        {
            linked.CancelAfter(limit);
        }

        try
        {
            return await ReaderOffload.Run(
                async () =>
                {
                    if (!File.Exists(databasePath))
                    {
                        return whenMissing;
                    }

                    await using var connection = new SqliteConnection(AuditReader.BuildReadOnlyConnectionString(databasePath));
                    await connection.OpenAsync(linked.Token).ConfigureAwait(false);
                    using var interrupt = ReaderOffload.InterruptOnCancel(connection, linked.Token);
                    return await body(connection, linked.Token).ConfigureAwait(false);
                },
                linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && linked.IsCancellationRequested)
        {
            throw new TimeoutException(
                string.Create(CultureInfo.InvariantCulture, $"The query did not finish within {limit.TotalSeconds:0.##} s and was stopped."));
        }
    }

    /// <summary>The table's column names, without case; empty when the table does not exist (<c>pragma_table_info</c> of a missing table is no rows).</summary>
    internal static async Task<HashSet<string>> ColumnsOfAsync(SqliteConnection connection, string table, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_info($table)";
        command.Parameters.AddWithValue("$table", table);

        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            _ = columns.Add(reader.GetString(0));
        }

        return columns;
    }

    /// <summary>The text of column <paramref name="ordinal"/>, or null for NULL (any other storage class is read as its text).</summary>
    internal static string? Text(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    /// <summary>A stored timestamp as an instant; <see cref="DateTimeOffset.MinValue"/> when the text is not one.</summary>
    internal static DateTimeOffset Timestamp(string? raw) =>
        DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;
}
