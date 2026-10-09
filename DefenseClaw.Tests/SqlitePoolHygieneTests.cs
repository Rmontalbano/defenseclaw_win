using System.Text.RegularExpressions;
using DefenseClaw.Core.Audit;
using DefenseClaw.Tests.TestSupport;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.Tests;

/// <summary>
/// How the test suites let go of their SQLite files (CUST-323). xunit runs test classes in parallel in one process and Microsoft.Data.Sqlite's
/// pools are process-wide, so a test that clears <em>every</em> pool can dispose a connection another test's reader is in the middle of taking
/// from its own: <c>ObjectDisposedException: Cannot access a disposed object. Object name: 'SQLitePCL.sqlite3'</c> from <c>sqlite3_prepare_v2</c>,
/// swallowed by the reader into an empty list or a status note, so the other test sees "there was nothing there". <see cref="SqlitePools"/> clears
/// the pools of the files a test names and nobody else's; these tests hold that in place.
/// </summary>
public class SqlitePoolHygieneTests
{
    // Built in two pieces so that this file does not contain the call it forbids.
    private static readonly Regex Call = new(@"\bClear" + @"AllPools\s*\(", RegexOptions.Compiled);

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DefenseClaw.Win.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException($"DefenseClaw.Win.sln was not found above {AppContext.BaseDirectory}.");
    }

    [Fact]
    public void No_test_source_clears_every_pool_in_the_process()
    {
        var root = FindRepositoryRoot();
        var offenders = new List<string>();
        var scanned = 0;

        foreach (var project in new[] { "DefenseClaw.Tests", "DefenseClaw.App.Tests" })
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, project), "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file);
                if (relative.Contains(@"\obj\", StringComparison.Ordinal) || relative.Contains(@"\bin\", StringComparison.Ordinal))
                {
                    continue;
                }

                scanned++;
                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    // A comment may say why the call is not made; only code is held to it.
                    if (!lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal) && Call.IsMatch(lines[i]))
                    {
                        offenders.Add($"{relative}:{i + 1}");
                    }
                }
            }
        }

        Assert.True(scanned > 100, $"only {scanned} test sources were found under {root}; the scan is not looking in the right place");
        Assert.True(
            offenders.Count == 0,
            "These tests clear every SQLite pool in the process, which disposes connections other tests' readers are taking from theirs; " +
            "use SqlitePools.Release(<the file or folder>) instead: " + string.Join(", ", offenders));
    }

    /// <summary>Creates <paramref name="path"/> with one table and reads it once the way every product reader does: pooled, so the file stays open after Dispose.</summary>
    private static void CreateAndReadPooled(string path)
    {
        using (var setup = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            setup.Open();
            using var create = setup.CreateCommand();
            create.CommandText = "CREATE TABLE t (id INTEGER PRIMARY KEY)";
            _ = create.ExecuteNonQuery();
        }

        using var reader = new SqliteConnection(AuditReader.BuildReadOnlyConnectionString(path));
        reader.Open();
        using var count = reader.CreateCommand();
        count.CommandText = "SELECT count(*) FROM t";
        _ = count.ExecuteScalar();
    }

    [Fact]
    public void Releasing_a_file_lets_it_be_deleted_and_releasing_a_folder_does_so_for_every_file_in_it()
    {
        using var directory = new TempDirectory("dcw-pools");
        var single = directory.File("single.db");
        var nested = directory.File(Path.Combine("sub", "nested.db"));
        _ = Directory.CreateDirectory(Path.GetDirectoryName(nested)!);
        CreateAndReadPooled(single);
        CreateAndReadPooled(nested);

        SqlitePools.Release(single);
        DeleteWhenFree(single);
        Assert.False(File.Exists(single), "the released file is still there");

        SqlitePools.Release(directory.Path);
        DeleteWhenFree(nested);
        Assert.False(File.Exists(nested), "a file under the released folder is still there");

        // Nothing there is nothing to release.
        SqlitePools.Release(single);
        SqlitePools.Release(directory.File("never-existed"));
    }

    /// <summary>
    /// Deletes the file, trying again while something has it open. A pooled SQLite connection keeps its file open for the life of the process,
    /// so a file that is still held after its pool was released stays held and this ends in the IOException that says so; but a virus scanner
    /// that opens a file as it is created lets go of it a moment later, and that is not a failure of the release.
    /// </summary>
    private static void DeleteWhenFree(string path)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            try
            {
                File.Delete(path);
                return;
            }
            catch (IOException) when (clock.Elapsed < TestTimeouts.Ceiling)
            {
                Thread.Sleep(50);
            }
        }
    }

    [Fact]
    public async Task Releasing_one_files_pool_never_breaks_a_reader_that_is_taking_connections_from_another_files()
    {
        using var directory = new TempDirectory("dcw-pools");
        var mine = directory.File("mine.db");
        var theirs = directory.File("theirs.db");
        CreateAndReadPooled(mine);
        CreateAndReadPooled(theirs);

        // What ClearAllPools() does to the reader: about one statement in five hundred fails with ObjectDisposedException. Releasing only
        // the other file's pool, as fast as it can be called, must not fail any. The run lasts for a number of statements, not for a time:
        // on a busy machine a fixed window holds few of them, and the check is only as good as the statements it covers. (The loops watch a
        // clock rather than a timer, which would need a thread-pool thread to fire, and the threads are their own, not the pool's.)
        const int Statements = 6000;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
        long statements = 0;

        bool Running() => Interlocked.Read(ref statements) < Statements && clock.Elapsed < TestTimeouts.Ceiling;

        Task Reader() => Task.Factory.StartNew(
            () =>
            {
                while (Running())
                {
                    try
                    {
                        using var connection = new SqliteConnection(AuditReader.BuildReadOnlyConnectionString(mine));
                        connection.Open();
                        using var command = connection.CreateCommand();
                        command.CommandText = "SELECT count(*) FROM t";
                        _ = command.ExecuteScalar();
                        _ = Interlocked.Increment(ref statements);
                    }
                    catch (Exception ex)
                    {
                        failures.Add(ex.GetType().Name + ": " + ex.Message);
                    }
                }
            },
            TaskCreationOptions.LongRunning);

        var releaser = Task.Factory.StartNew(
            () =>
            {
                while (Running())
                {
                    SqlitePools.Release(theirs);
                }
            },
            TaskCreationOptions.LongRunning);

        try
        {
            await Task.WhenAll(Reader(), Reader(), Reader(), releaser);
        }
        finally
        {
            SqlitePools.Release(directory.Path);
        }

        Assert.True(failures.IsEmpty, $"{failures.Count} of {statements} reads failed while another file's pool was released: {failures.FirstOrDefault()}");
        Assert.True(statements >= Statements, $"only {statements} of {Statements} reads were made in {clock.Elapsed.TotalSeconds:0} s");
    }
}
