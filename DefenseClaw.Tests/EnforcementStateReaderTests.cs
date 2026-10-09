using DefenseClaw.Core.Audit;
using DefenseClaw.Tests.TestSupport;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.Tests;

/// <summary>
/// "Current state" (CUST-261): what DefenseClaw is doing to a skill, MCP server, plugin or tool right now, read from one row of the <c>actions</c> table,
/// read-only. The vocabulary is the Govern panels' (<c>actions{file,runtime,install}</c>); the connector rule is the CLI's (a connector's own fields win,
/// the global entry fills in the rest). Synthetic rows from the real DDL.
/// </summary>
public sealed class EnforcementStateReaderTests : IDisposable
{
    private readonly TestAuditDatabase _database = new();
    private readonly EnforcementStateReader _reader;

    // The suite's ceiling for a read that has to finish: the app's own 8 s is a bound on how long a person waits, not on how long a busy machine needs.
    public EnforcementStateReaderTests() => _reader = new EnforcementStateReader(_database.Path, TestTimeouts.Ceiling);

    public void Dispose() => _database.Dispose();

    private void Put(string type, string name, string json, string connector = "")
    {
        using var connection = _database.OpenWritable();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO actions (id, target_type, target_name, source_path, actions_json, reason, updated_at, connector)
            VALUES ($id, $type, $name, NULL, $json, 'synthetic', '2026-09-01T12:00:00Z', $connector)
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("n"));
        command.Parameters.AddWithValue("$type", type);
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$json", json);
        command.Parameters.AddWithValue("$connector", connector);
        _ = command.ExecuteNonQuery();
    }

    // ------------------------------------------------------------------ the three dimensions, in the Govern panels' words

    [Fact]
    public async Task A_blocked_skill_reads_blocked()
    {
        Put("skill", "evil-skill", """{"install":"block"}""");

        var state = await _reader.ReadAsync("skill", "evil-skill");

        Assert.NotNull(state);
        Assert.True(state.IsBlocked);
        Assert.Equal("blocked", state.Summary);
        Assert.Equal("Bad", state.Tone);
        Assert.False(state.IsEmpty);
    }

    [Theory]
    [InlineData("""{"install":"allow"}""", "allowed", "Ok")]
    [InlineData("""{"file":"quarantine"}""", "quarantined", "Bad")]
    [InlineData("""{"runtime":"disable"}""", "disabled", "Warn")]
    [InlineData("""{"install":"block","file":"quarantine","runtime":"disable"}""", "blocked, quarantined, disabled", "Bad")]
    [InlineData("""{"install":"allow","runtime":"disable"}""", "allowed, disabled", "Warn")]
    [InlineData("""{"file":"quarantine","runtime":"disable"}""", "quarantined, disabled", "Bad")]
    [InlineData("""{"install":"BLOCK"}""", "blocked", "Bad")]
    [InlineData("{}", "none", "Neutral")]
    [InlineData("""{"install":"custom"}""", "none", "Neutral")]
    [InlineData("null", "none", "Neutral")]
    [InlineData("[]", "none", "Neutral")]
    [InlineData("not json at all", "none", "Neutral")]
    public async Task The_actions_json_reads_as_the_govern_panels_read_it(string json, string summary, string tone)
    {
        Put("plugin", "p", json);

        var state = await _reader.ReadAsync("plugin", "p");

        Assert.NotNull(state);
        Assert.Equal(summary, state.Summary);
        Assert.Equal(tone, state.Tone);
    }

    [Fact]
    public async Task An_entry_with_nothing_set_is_an_entry_with_nothing_being_done()
    {
        Put("mcp", "m", "{}");

        var state = await _reader.ReadAsync("mcp", "m");

        Assert.NotNull(state);
        Assert.True(state.IsEmpty);
        Assert.Equal("none", state.Summary);
    }

    // ------------------------------------------------------------------ no entry, no answer

    [Fact]
    public async Task No_entry_is_no_answer_and_the_type_and_the_name_must_both_match()
    {
        Put("skill", "evil-skill", """{"install":"block"}""");

        Assert.Null(await _reader.ReadAsync("skill", "another-skill"));
        Assert.Null(await _reader.ReadAsync("plugin", "evil-skill"));
        Assert.Null(await _reader.ReadAsync("skill", "EVIL-SKILL"));
        Assert.Null(await _reader.ReadAsync("skill", "evil"));
    }

    [Fact]
    public async Task A_database_that_is_not_there_or_has_no_actions_table_has_nothing_to_say()
    {
        using var directory = new TempDirectory("dcw-state");
        var missing = new EnforcementStateReader(directory.File("nowhere.db"), TestTimeouts.Ceiling);
        Assert.Null(await missing.ReadAsync("skill", "x"));

        var bare = directory.File("bare.db");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = bare, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE audit_events (id TEXT PRIMARY KEY)";
            _ = command.ExecuteNonQuery();
        }

        Assert.Null(await new EnforcementStateReader(bare, TestTimeouts.Ceiling).ReadAsync("skill", "x"));
        SqlitePools.Release(directory.Path);
    }

    [Fact]
    public async Task A_database_from_before_connector_scoping_has_one_entry_per_target()
    {
        using var directory = new TempDirectory("dcw-state");
        var old = directory.File("old.db");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = old, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE actions (id TEXT PRIMARY KEY, target_type TEXT NOT NULL, target_name TEXT NOT NULL, actions_json TEXT NOT NULL DEFAULT '{}');
                INSERT INTO actions VALUES ('1', 'skill', 's', '{"install":"block"}');
                """;
            _ = command.ExecuteNonQuery();
        }

        var state = await new EnforcementStateReader(old, TestTimeouts.Ceiling).ReadAsync("skill", "s", "codex");
        SqlitePools.Release(directory.Path);

        Assert.NotNull(state);
        Assert.Equal("blocked", state.Summary);
    }

    // ------------------------------------------------------------------ connectors: the most specific wins, field by field

    [Fact]
    public async Task A_global_entry_applies_to_every_connector()
    {
        Put("skill", "s", """{"install":"block"}""");

        Assert.Equal("blocked", (await _reader.ReadAsync("skill", "s"))!.Summary);
        Assert.Equal("blocked", (await _reader.ReadAsync("skill", "s", "codex"))!.Summary);
        Assert.Equal("blocked", (await _reader.ReadAsync("skill", "s", "  codex  "))!.Summary);
    }

    [Fact]
    public async Task A_connectors_own_entry_applies_to_that_connector_alone()
    {
        Put("skill", "s", """{"install":"block"}""", connector: "codex");

        Assert.Equal("blocked", (await _reader.ReadAsync("skill", "s", "codex"))!.Summary);
        Assert.Null(await _reader.ReadAsync("skill", "s", "claudecode"));
        Assert.Null(await _reader.ReadAsync("skill", "s"));
    }

    [Fact]
    public async Task A_connectors_allow_overrides_a_global_block_for_it_and_the_global_fields_it_does_not_set_fall_through()
    {
        Put("skill", "s", """{"install":"block","file":"quarantine"}""");
        Put("skill", "s", """{"install":"allow"}""", connector: "codex");

        var codex = await _reader.ReadAsync("skill", "s", "codex");
        var other = await _reader.ReadAsync("skill", "s", "claudecode");

        // codex: its own install=allow, and the quarantine nothing overrides. Everyone else: the global entry.
        Assert.Equal("allowed, quarantined", codex!.Summary);
        Assert.False(codex.IsBlocked);
        Assert.Equal("blocked, quarantined", other!.Summary);
    }

    // ------------------------------------------------------------------ read-only, bound, stoppable

    [Fact]
    public async Task A_name_that_looks_like_sql_is_only_a_name()
    {
        Put("skill", "x' OR '1'='1", """{"install":"block"}""");
        Put("skill", "plain", """{"install":"allow"}""");

        Assert.Equal("blocked", (await _reader.ReadAsync("skill", "x' OR '1'='1"))!.Summary);
        Assert.Null(await _reader.ReadAsync("skill", "' OR '1'='1"));
        Assert.Null(await _reader.ReadAsync("skill", "plain'; DROP TABLE actions; --"));
        Assert.Equal("allowed", (await _reader.ReadAsync("skill", "plain"))!.Summary);
    }

    [Fact]
    public async Task Reading_changes_nothing_in_the_database()
    {
        Put("skill", "s", """{"install":"block"}""");
        SqlitePools.Release(_database.Path);
        var before = File.ReadAllBytes(_database.Path);
        var stamp = File.GetLastWriteTimeUtc(_database.Path);

        _ = await _reader.ReadAsync("skill", "s");
        _ = await _reader.ReadAsync("skill", "nobody", "codex");
        SqlitePools.Release(_database.Path);

        Assert.Equal(before, File.ReadAllBytes(_database.Path));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(_database.Path));
        Assert.Equal(2, _reader.ReadCount);
    }

    [Fact]
    public async Task A_cancelled_lookup_is_cancelled_and_empty_arguments_are_refused()
    {
        Put("skill", "s", """{"install":"block"}""");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _reader.ReadAsync("skill", "s", cancellationToken: cancelled.Token));
        await Assert.ThrowsAsync<ArgumentException>(() => _reader.ReadAsync(string.Empty, "s"));
        await Assert.ThrowsAsync<ArgumentException>(() => _reader.ReadAsync("skill", string.Empty));
    }

    [Fact]
    public void The_limit_is_the_apps_unless_a_composition_gives_another()
    {
        Assert.Equal(TimeSpan.FromSeconds(8), EnforcementStateReader.DefaultTimeout);
        Assert.Equal(EnforcementStateReader.DefaultTimeout, new EnforcementStateReader(_database.Path).ReadTimeout);
        Assert.Equal(TimeSpan.FromSeconds(3), new EnforcementStateReader(_database.Path, TimeSpan.FromSeconds(3)).ReadTimeout);
        Assert.Equal(Timeout.InfiniteTimeSpan, new EnforcementStateReader(_database.Path, Timeout.InfiniteTimeSpan).ReadTimeout);
        Assert.Equal(TestTimeouts.Ceiling, _reader.ReadTimeout);
        Assert.Throws<ArgumentOutOfRangeException>(() => new EnforcementStateReader(_database.Path, TimeSpan.Zero));
    }

    [Fact]
    public void Merging_prefers_the_connectors_fields_and_keeps_a_missing_side_as_it_is()
    {
        var global = new EnforcementState { Install = "block", Runtime = "disable" };
        var narrowed = new EnforcementState { Install = "allow" };

        var merged = EnforcementStateReader.Merge(global, narrowed)!;
        Assert.Equal("allow", merged.Install);
        Assert.Equal("disable", merged.Runtime);
        Assert.Same(global, EnforcementStateReader.Merge(global, null));
        Assert.Same(narrowed, EnforcementStateReader.Merge(null, narrowed));
        Assert.Null(EnforcementStateReader.Merge(null, null));
    }
}
