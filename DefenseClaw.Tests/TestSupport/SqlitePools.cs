using DefenseClaw.Core.Audit;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.Tests.TestSupport;

/// <summary>
/// Lets go of the SQLite connections a test left in Microsoft.Data.Sqlite's pools, for the database files it names and for no one else's.
/// <para>
/// <b>Why not <c>SqliteConnection.ClearAllPools()</c>.</b> A pooled connection stays open after <c>Close()</c> and keeps its file open, so a
/// test that removes or replaces its scratch directory has to clear the pool first. <c>ClearAllPools()</c> does that for <em>every</em> pool in
/// the process, and xunit runs test classes in parallel: a connection another test's reader was just taking from its pool is disposed under it,
/// and the reader fails with <c>ObjectDisposedException: Cannot access a disposed object. Object name: 'SQLitePCL.sqlite3'</c> inside
/// <c>sqlite3_prepare_v2</c> (measured with the Microsoft.Data.Sqlite this suite uses: about one statement in 500 with one reader thread and one
/// clearing thread, none at all with <c>Pooling=False</c>). The readers swallow it into a status note or an empty list, so the test that was
/// reading sees "there was nothing there" (CUST-323). A pool is keyed by connection string and a test's database path is its own, so clearing
/// <em>that</em> pool touches no one else's (same probe: 404,000 statements against 2,400,000 clears of another file's pool, no failure).
/// </para>
/// <para>
/// Only two connection strings are ever pooled against a test's file: the one every product reader uses
/// (<see cref="AuditReader.BuildReadOnlyConnectionString"/>) and the one a fixture writes through (<see cref="WritableConnectionString"/>).
/// Everything else a test opens says <c>Pooling=False</c>. A test that adds a third pooled string has to clear it itself;
/// <see cref="SqlitePoolHygieneTests"/> keeps <c>ClearAllPools()</c> out of the test sources.
/// </para>
/// </summary>
public static class SqlitePools
{
    /// <summary>The connection string a fixture writes through: read-write, created if missing, and pooled like any other.</summary>
    public static string WritableConnectionString(string databasePath) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();

    /// <summary>
    /// Closes the pooled connections to <paramref name="path"/> - a database file, or a directory: every file under it - so it can be deleted,
    /// replaced or renamed. A path that is not there is nothing to release. Never touches a pool of any other file.
    /// </summary>
    public static void Release(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (Directory.Exists(path))
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                ReleaseFile(file);
            }

            return;
        }

        ReleaseFile(path);
    }

    private static void ReleaseFile(string file)
    {
        foreach (var connectionString in new[] { AuditReader.BuildReadOnlyConnectionString(file), WritableConnectionString(file) })
        {
            // A connection that was never opened still finds the pool its string names.
            using var connection = new SqliteConnection(connectionString);
            SqliteConnection.ClearPool(connection);
        }
    }
}
