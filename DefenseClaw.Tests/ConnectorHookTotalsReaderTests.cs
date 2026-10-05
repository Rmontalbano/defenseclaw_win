using System.Globalization;
using DefenseClaw.Core.Audit;
using DefenseClaw.Core.Paths;
using DefenseClaw.Tests.TestSupport;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.Tests;

/// <summary>
/// The Overview's Hook Calls / Blocks totals (CUST-258): all-time, per connector, the way the 0.8.10 TUI counts them, on synthetic
/// databases built from the real DDL. Counts are held against <c>COUNT(*)</c> over more than the old 500-row window; the plans are
/// asserted because what the live 10 GB file needs is an index, not a table scan.
/// </summary>
public sealed class ConnectorHookTotalsReaderTests : IDisposable
{
    private static readonly DateTimeOffset Base = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    private readonly TestAuditDatabase _database = new();
    private int _n;

    public void Dispose() => _database.Dispose();

    private ConnectorHookTotalsReader Reader(int chunk = 10_000) => new(_database.Path, chunk);

    private static string Hook(string action, string mode = "action", string? raw = null) =>
        $"connector=claudecode result=ok action={action} raw_action={raw ?? action} severity=NONE mode={mode}";

    /// <summary>Inserts <paramref name="count"/> rows one second apart in one transaction (a per-row connection would take minutes).</summary>
    private void Bulk(int count, string action, string connector, string? details, long? enforced = null, string target = "")
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _database.Path, Pooling = false }.ToString());
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO audit_events (id, timestamp, action, target, actor, details, connector, enforced) VALUES ($id, $ts, $a, $t, 'x', $d, $c, $e)";
        var id = command.Parameters.Add("$id", SqliteType.Text);
        var ts = command.Parameters.Add("$ts", SqliteType.Text);
        _ = command.Parameters.AddWithValue("$a", action);
        _ = command.Parameters.AddWithValue("$t", target);
        _ = command.Parameters.AddWithValue("$d", (object?)details ?? DBNull.Value);
        _ = command.Parameters.AddWithValue("$c", connector);
        _ = command.Parameters.AddWithValue("$e", (object?)enforced ?? DBNull.Value);
        for (var i = 0; i < count; i++)
        {
            var at = Base.AddSeconds(_n);
            id.Value = $"row-{_n++:D6}";
            ts.Value = at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture) + "Z";
            _ = command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private long Count(string where)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _database.Path, Pooling = false, Mode = SqliteOpenMode.ReadOnly }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM audit_events WHERE " + where;
        return (long)command.ExecuteScalar()!;
    }

    // ---- Beyond the 500-row window ----

    [Fact]
    public async Task Hook_calls_are_the_all_time_count_not_the_newest_500()
    {
        Bulk(900, "connector-hook", "claudecode", Hook("allow"));
        Bulk(450, "connector-hook", "ClaudeCode ", Hook("allow"));   // the same connector, with case and padding
        Bulk(300, "connector-hook", "codex", Hook("allow"));
        Bulk(700, "tool_invocation", "claudecode", null);             // not hooks: must not count

        var totals = await Reader().ReadAsync();

        Assert.Equal(ConnectorHookTotalsStatus.Ok, totals.Status);
        Assert.Equal(Count("action = 'connector-hook'"), totals.Fleet.Calls);
        Assert.Equal(1650, totals.Fleet.Calls);
        Assert.Equal(1350, totals.For("claudecode").Calls);
        Assert.Equal(300, totals.For("CODEX").Calls);
        Assert.Equal(0, totals.For("hermes").Calls);
    }

    [Fact]
    public async Task Blocks_are_the_enforced_hook_decisions_across_all_time_per_connector()
    {
        Bulk(600, "connector-hook", "claudecode", Hook("allow"));
        Bulk(550, "connector-hook", "claudecode", Hook("block"));                       // enforced: mode=action
        Bulk(40, "connector-hook", "codex", Hook("deny"));
        Bulk(300, "connector-hook", "claudecode", Hook("block", mode: "observe"));      // a would-block: an alert, not a block
        Bulk(20, "connector-hook", "claudecode", Hook("allow", raw: "block"));          // raw_action is not action
        Bulk(7, "connector-hook", "codex", Hook("allow"), enforced: 1);                  // the column is authoritative
        Bulk(5, "connector-hook", "claudecode", Hook("block"), enforced: 0);            // explicitly not enforced
        Bulk(60, "guardrail-block", "claudecode", null);                                 // not a hook row: the TUI does not count it

        var totals = await Reader().ReadAsync();

        Assert.True(totals.BlocksComplete);
        Assert.Equal(550, totals.For("claudecode").Blocks);
        Assert.Equal(47, totals.For("codex").Blocks);
        Assert.Equal(597, totals.Fleet.Blocks);
        Assert.Equal(totals.HookRows, totals.BlocksScanned);
    }

    [Theory]
    [InlineData("action=block mode=action", null, true)]
    [InlineData("action=deny mode=enforce", null, true)]
    [InlineData("action=block mode=observe", null, false)]
    [InlineData("action=block mode=action", 0L, false)]
    [InlineData("action=allow", 1L, true)]
    [InlineData("raw_action=block action=allow", null, false)]
    [InlineData("action=\"block\" mode=action", null, true)]
    [InlineData("action=BLOCK", null, true)]
    [InlineData("xaction=block", null, false)]
    [InlineData(null, null, false)]
    public void The_enforced_block_rule_is_the_TUIs(string? details, long? enforced, bool expected) =>
        Assert.Equal(expected, ConnectorHookTotalsReader.IsEnforcedBlock(details, enforced));

    // ---- Incremental and resumable ----

    [Fact]
    public async Task A_catch_up_that_does_not_fit_one_read_resumes_and_ends_on_the_same_totals()
    {
        Bulk(400, "connector-hook", "claudecode", Hook("allow"));
        Bulk(120, "connector-hook", "claudecode", Hook("block"));
        Bulk(300, "connector-hook", "claudecode", Hook("allow"));
        var reader = Reader(chunk: 100);
        reader.Budget = TimeSpan.Zero;   // one chunk per read

        var first = await reader.ReadAsync();
        Assert.False(first.BlocksComplete);
        Assert.Equal(100, first.BlocksScanned);
        Assert.Equal(820, first.Fleet.Calls);   // hook calls are exact from the first read

        ConnectorHookTotals last = first;
        for (var i = 0; i < 20 && !last.BlocksComplete; i++)
        {
            last = await reader.ReadAsync();
        }

        Assert.True(last.BlocksComplete);
        Assert.Equal(120, last.Fleet.Blocks);
        Assert.Equal(820, last.BlocksScanned);
    }

    [Fact]
    public async Task A_later_read_scans_only_what_arrived_after_the_watermark()
    {
        Bulk(500, "connector-hook", "claudecode", Hook("block"));
        var reader = Reader(chunk: 100);
        _ = await reader.ReadAsync();

        Bulk(30, "connector-hook", "claudecode", Hook("block"));
        Bulk(10, "connector-hook", "claudecode", Hook("allow"));
        var next = await reader.ReadAsync();

        Assert.True(next.BlocksComplete);
        Assert.Equal(540, next.Fleet.Calls);
        Assert.Equal(530, next.Fleet.Blocks);
    }

    [Fact]
    public async Task Rows_pruned_by_retention_rebuild_the_tally_instead_of_overstating_it()
    {
        Bulk(300, "connector-hook", "claudecode", Hook("block"));
        Bulk(100, "connector-hook", "claudecode", Hook("allow"));
        var reader = Reader(chunk: 50);
        var before = await reader.ReadAsync();
        Assert.Equal(300, before.Fleet.Blocks);

        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _database.Path, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM audit_events WHERE id < 'row-000100'";   // the oldest 100 blocks
            _ = command.ExecuteNonQuery();
        }

        var after = await reader.ReadAsync();

        Assert.Equal(300, after.Fleet.Calls);
        Assert.Equal(200, after.Fleet.Blocks);
        Assert.True(after.BlocksComplete);
    }

    // ---- The captions' window ----

    [Fact]
    public async Task The_recent_split_and_tops_come_from_the_newest_500_hook_rows_per_connector()
    {
        Bulk(2000, "connector-hook", "claudecode", Hook("allow"), target: "old-target");          // older than the window
        Bulk(300, "connector-hook", "claudecode", Hook("allow"), target: "Read");
        Bulk(100, "connector-hook", "claudecode", Hook("block", mode: "observe"), target: "Bash");
        Bulk(30, "connector-hook", "claudecode", Hook("block"), target: "rm -rf");
        Bulk(20, "connector-hook", "claudecode", Hook("block"), target: "curl");
        Bulk(50, "connector-hook", "codex", Hook("allow"), target: "exec");

        var totals = await Reader().ReadAsync();

        var recent = totals.Recent;
        Assert.Equal(500, recent.Total);
        Assert.Equal(350, recent.Allow);   // 300 + 50 of the newest 500
        Assert.Equal(100, recent.Alert);
        Assert.Equal(50, recent.Block);
        Assert.Equal("Read", recent.TopHook);
        Assert.Equal("rm -rf", recent.TopBlockedTarget);
        Assert.Equal(30, recent.TopBlockedCount);

        var codex = totals.RecentFor("codex");
        Assert.Equal(50, codex.Allow);
        Assert.Equal("exec", codex.TopHook);
        Assert.Equal(string.Empty, codex.TopBlockedTarget);
        Assert.Equal(RecentHookSplit.Empty, totals.RecentFor("hermes"));
    }

    // ---- Shape, plans, and a missing database ----

    [Fact]
    public async Task A_missing_or_unrelated_database_reads_as_nothing_recorded()
    {
        using var temp = new TempDirectory();
        var missing = await new ConnectorHookTotalsReader(temp.File("audit.db")).ReadAsync();
        Assert.Equal(ConnectorHookTotalsStatus.NoDatabase, missing.Status);
        Assert.Equal(0, missing.Fleet.Calls);
        Assert.Empty(await new ConnectorHookTotalsReader(temp.File("audit.db")).ExplainAsync());
    }

    [Fact]
    public async Task No_statement_is_a_table_scan_or_a_sort_of_the_table()
    {
        Bulk(50, "connector-hook", "claudecode", Hook("allow"));

        var plan = await Reader().ExplainAsync();

        Assert.NotEmpty(plan);
        Assert.DoesNotContain(plan, line => line.StartsWith("SCAN audit_events", StringComparison.Ordinal));
        Assert.Contains(plan, line => line.Contains("idx_audit_action", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reads_are_counted_so_an_idle_panel_can_be_held_still()
    {
        var reader = Reader();
        Assert.Equal(0, reader.ReadCount);
        _ = await reader.ReadAsync();
        Assert.Equal(1, reader.ReadCount);
    }
}
