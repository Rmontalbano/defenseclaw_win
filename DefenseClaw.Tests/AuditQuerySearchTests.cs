using DefenseClaw.Core.Audit;
using DefenseClaw.Tests.TestSupport;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.Tests;

/// <summary>
/// The Audit panel's <c>field:value</c> tokens as the database filters they become (CUST-261): which rows each token selects, that a value is always a bound
/// parameter (a quote, a percent sign or a statement typed into the box is just text), and that the totals, the remembered pages and the fast severity counts
/// all know about the filters. Synthetic rows from the real DDL.
/// </summary>
public sealed class AuditQuerySearchTests : IDisposable
{
    private static readonly DateTimeOffset Base = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly TestAuditDatabase _database = new();
    private readonly AuditReader _reader;

    public AuditQuerySearchTests()
    {
        _reader = new AuditReader(_database.Path);
        Seed();
    }

    public void Dispose() => _database.Dispose();

    /// <summary>Everything a token can name, on rows that differ in one way each. Newest last in the table, first in a page.</summary>
    private void Seed()
    {
        Add("a-skill", 1, "skill-block", "evil-skill", "cli", "HIGH", "enforcement.action", "claudecode", "install=block", "run-aaa-111", "trace-aaaa-1111", "req-aaaa-1", "ses-aaaa-1");
        Add("b-hook", 2, "connector-hook", "preToolUse", "gateway", "INFO", "guardrail.evaluation", "codex", "connector=codex action=allow", "run-bbb-222", "trace-bbbb-2222", "req-bbbb-2", "ses-bbbb-2");
        Add("c-scan", 3, "scan-finding", "skills/my skill", "scanner", "MEDIUM", "security.finding", "claudecode", "found a thing", "run-ccc-333", null, null, null);
        Add("d-config", 4, "config.change.applied", "config.yaml", "cli", "INFO", "compliance.activity", null, null, null, null, null, null);
        Add("e-literal", 5, "tool_invocation", "100%_done\\path", "agent", "LOW", "tool.activity", "codex", "it's \"fine\"", null, null, null, null);
        Add("f-mcp", 6, "mcp-set", "docs-server", "cli", "WARN", "enforcement.action", "claudecode", "set", "run-aaa-999", null, null, null);
    }

    private void Add(string id, int minutes, string action, string target, string actor, string severity, string bucket, string? connector, string? details, string? run, string? trace, string? request, string? session)
    {
        using var connection = _database.OpenWritable();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO audit_events (id, timestamp, action, target, actor, details, severity, bucket, connector, event_name, run_id, trace_id, request_id, session_id)
            VALUES ($id, $ts, $action, $target, $actor, $details, $severity, $bucket, $connector, 'evt', $run, $trace, $request, $session)
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$ts", TestAuditDatabase.FormatTimestamp(Base.AddMinutes(minutes)));
        command.Parameters.AddWithValue("$action", action);
        command.Parameters.AddWithValue("$target", target);
        command.Parameters.AddWithValue("$actor", actor);
        command.Parameters.AddWithValue("$details", (object?)details ?? DBNull.Value);
        command.Parameters.AddWithValue("$severity", severity);
        command.Parameters.AddWithValue("$bucket", bucket);
        command.Parameters.AddWithValue("$connector", (object?)connector ?? DBNull.Value);
        command.Parameters.AddWithValue("$run", (object?)run ?? DBNull.Value);
        command.Parameters.AddWithValue("$trace", (object?)trace ?? DBNull.Value);
        command.Parameters.AddWithValue("$request", (object?)request ?? DBNull.Value);
        command.Parameters.AddWithValue("$session", (object?)session ?? DBNull.Value);
        _ = command.ExecuteNonQuery();
    }

    private async Task<string[]> Ids(string typed, AuditQuery? basis = null)
    {
        var query = (basis ?? new AuditQuery()).WithSearch(SearchQuery.Parse(typed));
        var page = await _reader.QueryAsync(query);
        return page.Events.Select(e => e.Id).ToArray();
    }

    // ------------------------------------------------------------------ one token each

    [Theory]
    [InlineData("connector:claudecode", new[] { "f-mcp", "c-scan", "a-skill" })]
    [InlineData("connector:CODEX", new[] { "e-literal", "b-hook" })]
    [InlineData("severity:high", new[] { "a-skill" })]
    [InlineData("severity:info", new[] { "d-config", "b-hook" })]
    [InlineData("severity:warn", new[] { "f-mcp" })]
    [InlineData("run:aaa", new[] { "f-mcp", "a-skill" })]
    [InlineData("run:bbb-222", new[] { "b-hook" })]
    [InlineData("run_id:CCC", new[] { "c-scan" })]
    [InlineData("id:c-s", new[] { "c-scan" })]
    [InlineData("id:-", new[] { "f-mcp", "e-literal", "d-config", "c-scan", "b-hook", "a-skill" })]
    [InlineData("actor:cli", new[] { "f-mcp", "d-config", "a-skill" })]
    [InlineData("actor:gate", new[] { "b-hook" })]
    [InlineData("target:evil", new[] { "a-skill" })]
    [InlineData("target:\"my skill\"", new[] { "c-scan" })]
    [InlineData("target:skill", new[] { "c-scan", "a-skill" })]
    [InlineData("action:block", new[] { "a-skill" })]
    [InlineData("action:CONNECTOR-", new[] { "b-hook" })]
    [InlineData("details:allow", new[] { "b-hook" })]
    [InlineData("trace:bbbb", new[] { "b-hook" })]
    [InlineData("trace_id:TRACE-AAAA-1111", new[] { "a-skill" })]
    [InlineData("request:req-aaaa", new[] { "a-skill" })]
    [InlineData("session:ses-bbbb-2", new[] { "b-hook" })]
    public async Task Each_token_selects_its_column(string typed, string[] expected) => Assert.Equal(expected, await Ids(typed));

    [Theory]
    [InlineData("type:skill", new[] { "a-skill" })]
    [InlineData("type:mcp", new[] { "f-mcp" })]
    [InlineData("type:tool", new[] { "e-literal" })]
    [InlineData("type:scan", new[] { "c-scan" })]
    [InlineData("type:config", new[] { "d-config" })]
    [InlineData("type:security", new[] { "c-scan" })]
    [InlineData("type:enforcement.action", new[] { "f-mcp", "a-skill" })]
    [InlineData("type:nothing-like-this", new string[0])]
    public async Task Type_is_the_kind_of_thing_the_action_names_or_the_bucket_it_was_recorded_in(string typed, string[] expected)
    {
        // The TUI's TYPE is decided from the action (skill-block is a skill, scan-finding a scan - a target that says "skills/" does not make c-scan one);
        // this app's Type column is the bucket, so a bucket (security.finding) is found by its words too.
        Assert.Equal(expected, await Ids(typed));
    }

    [Fact]
    public async Task Free_text_is_the_search_it_always_was_and_tokens_narrow_it()
    {
        Assert.Equal(new[] { "b-hook" }, await Ids("allow"));
        Assert.Equal(new[] { "c-scan", "a-skill" }, await Ids("skill"));

        // The free words are one phrase, as they were; a token beside them narrows.
        Assert.Equal(new[] { "c-scan" }, await Ids("skill connector:claudecode severity:medium"));
        Assert.Equal(new[] { "a-skill" }, await Ids("connector:claudecode skill severity:high"));
        Assert.Empty(await Ids("skill connector:codex"));
    }

    [Fact]
    public async Task A_search_with_nothing_in_it_changes_nothing()
    {
        var query = new AuditQuery { Limit = 3 };

        Assert.Same(query, query.WithSearch(SearchQuery.Empty));
        Assert.Same(query, query.WithSearch(SearchQuery.Parse("connector:")));
        Assert.Equal(6, (await _reader.QueryAsync(new AuditQuery().WithSearch(SearchQuery.Parse("  ")))).Events.Count);
    }

    // ------------------------------------------------------------------ the mapping

    [Fact]
    public void A_connector_token_is_the_queries_own_indexed_connector_and_the_rest_are_filters()
    {
        var query = new AuditQuery().WithSearch(SearchQuery.Parse("Connector:Codex target:x skill"));

        Assert.Equal("codex", query.Connector);
        Assert.Equal("skill", query.SearchText);
        var filter = Assert.Single(query.FieldFilters!);
        Assert.Equal(new AuditFieldFilter(AuditField.Target, "x"), filter);
    }

    [Fact]
    public async Task A_connector_the_query_already_has_is_anded_with_the_one_typed()
    {
        var filterBar = new AuditQuery { Connector = "claudecode" };

        // The same connector: still its rows. Another: nothing - both must hold.
        Assert.Equal(new[] { "f-mcp", "c-scan", "a-skill" }, await Ids("connector:claudecode", filterBar));
        Assert.Empty(await Ids("connector:codex", filterBar));

        // "claudecode + platform rows" still means the typed connector alone when one is typed.
        var withPlatform = new AuditQuery { Connector = "claudecode", IncludeNullConnector = true };
        Assert.Equal(new[] { "f-mcp", "d-config", "c-scan", "a-skill" }, await Ids(string.Empty, withPlatform));
        Assert.Equal(new[] { "f-mcp", "c-scan", "a-skill" }, await Ids("connector:claudecode", withPlatform));

        // Two typed: both hold, so two different ones match nothing, and the first is the queries own indexed connector.
        var two = new AuditQuery().WithSearch(SearchQuery.Parse("connector:claudecode connector:codex"));
        Assert.Equal("claudecode", two.Connector);
        Assert.Equal(new AuditFieldFilter(AuditField.Connector, "codex", AuditFieldMatch.Exact), Assert.Single(two.FieldFilters!));
        Assert.Empty((await _reader.QueryAsync(two)).Events);
    }

    [Fact]
    public async Task The_filters_a_query_has_are_kept_and_the_typed_ones_added_to_them()
    {
        var basis = new AuditQuery { FieldFilters = new[] { new AuditFieldFilter(AuditField.Actor, "cli") } };

        Assert.Equal(new[] { "f-mcp", "a-skill" }, await Ids("run:aaa", basis));
        Assert.Equal(2, basis.WithSearch(SearchQuery.Parse("run:aaa")).FieldFilters!.Count);
        Assert.Single(basis.FieldFilters!);
    }

    // ------------------------------------------------------------------ what is typed is only ever a value

    [Theory]
    [InlineData("target:%")]
    [InlineData("target:_")]
    [InlineData("target:\"%_\"")]
    [InlineData("actor:\\")]
    public async Task A_percent_sign_an_underscore_or_a_backslash_is_text_not_a_wildcard(string typed)
    {
        // Only the row whose target really has the characters can match; "%" alone does not match every row.
        var ids = await Ids(typed);
        Assert.True(ids.Length <= 1, string.Join(",", ids));
    }

    [Fact]
    public async Task A_target_with_wildcard_characters_is_found_by_exactly_those_characters()
    {
        Assert.Equal(new[] { "e-literal" }, await Ids("target:\"100%_done\\path\""));
        Assert.Equal(new[] { "e-literal" }, await Ids("target:%_done"));
        Assert.Empty(await Ids("target:100x_done"));
    }

    [Theory]
    [InlineData("target:\"x'; DROP TABLE audit_events; --\"")]
    [InlineData("actor:\"' OR '1'='1\"")]
    [InlineData("details:\"\\\"; DELETE FROM audit_events; --\"")]
    [InlineData("connector:\"codex' OR 1=1 --\"")]
    [InlineData("type:\"') OR 1=1 --\"")]
    [InlineData("\"'; DROP TABLE audit_events; --\"")]
    public async Task A_statement_typed_into_the_box_is_a_value_that_matches_nothing_and_runs_nothing(string typed)
    {
        Assert.Empty(await Ids(typed));

        // The table is as it was.
        Assert.Equal(6, (await _reader.QueryAsync(new AuditQuery())).Events.Count);
    }

    [Fact]
    public async Task A_quote_in_a_value_still_finds_the_row_that_has_it()
    {
        // The details of e-literal are: it's "fine".
        Assert.Equal(new[] { "e-literal" }, await Ids("details:it's"));
    }

    // ------------------------------------------------------------------ the rest of the reader knows about them

    [Fact]
    public async Task The_total_counts_the_filtered_rows_including_under_a_minimum_severity_and_a_window()
    {
        // A minimum severity with only a window is the index-only count; a field filter beside it must not be skipped by that shortcut.
        var windowed = new AuditQuery { MinimumSeverity = AuditSeverity.Info, From = Base.AddMinutes(-5) };
        var filtered = windowed.WithSearch(SearchQuery.Parse("actor:cli"));

        Assert.Equal(6, await _reader.CountAsync(windowed));
        Assert.Equal(3, await _reader.CountAsync(filtered));
        Assert.Equal(3, (await _reader.QueryAsync(filtered with { Limit = 50 })).Events.Count);

        var tiles = await _reader.CountBySeverityAsync(new AuditQuery { From = Base.AddMinutes(-5) }.WithSearch(SearchQuery.Parse("actor:cli")));
        Assert.Equal(3, tiles.Values.Sum());
    }

    [Fact]
    public async Task Pages_are_remembered_per_filter_and_a_different_filter_is_a_different_page()
    {
        using var probe = new AuditChangeProbe(_database.Path);
        var reader = new AuditReader(_database.Path, probe: probe);
        var cli = new AuditQuery().WithSearch(SearchQuery.Parse("actor:cli"));
        var gateway = new AuditQuery().WithSearch(SearchQuery.Parse("actor:gateway"));

        var first = await reader.QueryAsync(cli);
        var other = await reader.QueryAsync(gateway);
        var again = await reader.QueryAsync(cli);

        Assert.Equal(new[] { "f-mcp", "d-config", "a-skill" }, first.Events.Select(e => e.Id));
        Assert.Equal(new[] { "b-hook" }, other.Events.Select(e => e.Id));

        // The unchanged database answers the same query with the very page it gave, and the other filter did not poison it.
        Assert.Same(first, again);
        Assert.NotSame(first, other);
    }

    [Fact]
    public async Task A_filter_pages_without_gaps_or_repeats()
    {
        var query = new AuditQuery { Limit = 2 }.WithSearch(SearchQuery.Parse("connector:claudecode"));

        var first = await _reader.QueryAsync(query);
        var second = await _reader.QueryAsync(query with { After = first.NextCursor });

        Assert.Equal(new[] { "f-mcp", "c-scan" }, first.Events.Select(e => e.Id));
        Assert.True(first.HasMore);
        Assert.Equal(new[] { "a-skill" }, second.Events.Select(e => e.Id));
        Assert.False(second.HasMore);
    }

    [Fact]
    public async Task A_connector_token_reads_the_connector_index_as_the_connector_filter_does()
    {
        var typed = new AuditQuery().WithSearch(SearchQuery.Parse("connector:codex"));
        var filter = new AuditQuery { Connector = "codex" };

        var typedPlan = await _reader.ExplainAsync(typed, AuditQueryShape.Page);
        var filterPlan = await _reader.ExplainAsync(filter, AuditQueryShape.Page);

        Assert.Equal(filterPlan, typedPlan);
    }

    [Fact]
    public void Type_is_decided_by_the_same_table_in_code_and_in_sql()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        var actions = new[]
        {
            "skill-block", "scan-skill", "mcp-set", "plugin-install", "tool_invocation", "tool.invocation.completed", "scan", "scan-finding", "finding.observed",
            "key-rotation", "token-refresh", "credential-set", "secret-read", "alert-dismiss", "config.change.applied", "setup-run", "init", "connector-hook",
            "install-blocked", "hook_decision", "", "SKILL-BLOCK", "Plugin-Remove", "monkey",
        };

        foreach (var action in actions)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {AuditTargetType.SqlCase("$action")}";
            command.Parameters.AddWithValue("$action", action);
            Assert.Equal(AuditTargetType.FromAction(action), (string)command.ExecuteScalar()!);
        }
    }
}
