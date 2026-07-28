using System.Globalization;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.Core.Inventory;

/// <summary>A table or view discovered in the database.</summary>
public sealed record InventoryTable(string Name, string Type)
{
    public bool IsView => string.Equals(Type, "view", StringComparison.OrdinalIgnoreCase);
}

/// <summary>One column, as reported by <c>PRAGMA table_info</c>.</summary>
public sealed record InventoryColumn(int Ordinal, string Name, string DeclaredType, bool NotNull, bool IsPrimaryKey);

/// <summary>A generic result set: ordered column names plus rows of boxed values.</summary>
public sealed record InventoryRows(IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows);

/// <summary>
/// Read-only browser over <c>~/.defenseclaw/inventory.db</c>. Its schema is not pinned by
/// any published contract and changes between 0.8.x releases, so this discovers the
/// schema at runtime and offers generic browsing rather than baking in table shapes.
/// On the live 0.8.7 install it holds <c>ai_signals</c>, <c>ai_scans</c>,
/// <c>ai_confidence_snapshots</c>, <c>schema_version</c> and the <c>ai_components_v</c> view.
/// </summary>
public sealed class InventoryReader
{
    private readonly string _connectionString;

    public InventoryReader(string databasePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(databasePath);
        DatabasePath = databasePath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();
    }

    public string DatabasePath { get; }

    public bool Exists => File.Exists(DatabasePath);

    /// <summary>Discovers tables and views, excluding SQLite internals.</summary>
    public async Task<IReadOnlyList<InventoryTable>> ListTablesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name, type FROM sqlite_master
            WHERE type IN ('table', 'view') AND name NOT LIKE 'sqlite_%'
            ORDER BY name
            """;

        var tables = new List<InventoryTable>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            tables.Add(new InventoryTable(reader.GetString(0), reader.GetString(1)));
        }

        return tables;
    }

    public async Task<IReadOnlyList<InventoryColumn>> ListColumnsAsync(string table, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var resolved = await ResolveTableNameAsync(connection, table, cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({Quote(resolved)})";

        var columns = new List<InventoryColumn>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            columns.Add(new InventoryColumn(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                reader.GetInt32(3) != 0,
                reader.GetInt32(5) != 0));
        }

        return columns;
    }

    public async Task<int> CountAsync(string table, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var resolved = await ResolveTableNameAsync(connection, table, cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {Quote(resolved)}";
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is null or DBNull ? 0 : Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Reads a page of rows. <paramref name="table"/> is validated against the discovered
    /// schema and quoted — it is never concatenated straight into SQL.
    /// </summary>
    public async Task<InventoryRows> BrowseAsync(
        string table,
        int limit = 200,
        int offset = 0,
        string? orderByColumn = null,
        bool descending = false,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var resolved = await ResolveTableNameAsync(connection, table, cancellationToken).ConfigureAwait(false);

        var orderBy = string.Empty;
        if (!string.IsNullOrWhiteSpace(orderByColumn))
        {
            var columns = await ListColumnsAsync(resolved, cancellationToken).ConfigureAwait(false);
            var match = columns.FirstOrDefault(c => string.Equals(c.Name, orderByColumn, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"Unknown column '{orderByColumn}' on '{resolved}'.", nameof(orderByColumn));
            orderBy = $" ORDER BY {Quote(match.Name)} {(descending ? "DESC" : "ASC")}";
        }

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM {Quote(resolved)}{orderBy} LIMIT $limit OFFSET $offset";
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 10_000));
        command.Parameters.AddWithValue("$offset", Math.Max(0, offset));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var names = new List<string>(reader.FieldCount);
        for (var i = 0; i < reader.FieldCount; i++)
        {
            names.Add(reader.GetName(i));
        }

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var row = new Dictionary<string, object?>(names.Count, StringComparer.Ordinal);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[names[i]] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }

            rows.Add(row);
        }

        return new InventoryRows(names, rows);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Maps a caller-supplied name onto a real object in sqlite_master. Anything that does
    /// not match exactly (case-insensitively) is rejected, which is what keeps
    /// interpolating the name into SQL safe.
    /// </summary>
    private static async Task<string> ResolveTableNameAsync(SqliteConnection connection, string table, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(table);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name FROM sqlite_master
            WHERE type IN ('table', 'view') AND name NOT LIKE 'sqlite_%' AND name = $name COLLATE NOCASE
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$name", table);

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result as string ?? throw new ArgumentException($"Unknown table or view '{table}'.", nameof(table));
    }

    private static string Quote(string identifier) =>
        "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
