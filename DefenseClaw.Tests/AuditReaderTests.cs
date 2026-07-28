using DefenseClaw.Core.Audit;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// Runs against a temp database created from the real DDL with synthetic rows only.
/// No production audit data is used.
/// </summary>
public class AuditReaderTests : IDisposable
{
    private static readonly DateTimeOffset Base = new(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);

    private readonly TestAuditDatabase _database = new();
    private readonly AuditReader _reader;

    public AuditReaderTests()
    {
        _reader = new AuditReader(_database.Path);
        Seed();
    }

    private void Seed()
    {
        // Ten rows, one minute apart, spread across buckets/severities/connectors.
        _database.InsertEvent("evt-01", Base.AddMinutes(1), "sidecar-start", "INFO", "platform.health", null, details: "sidecar up");
        _database.InsertEvent("evt-02", Base.AddMinutes(2), "scan", "LOW", "asset.scan", "claudecode", details: "scan started");
        _database.InsertEvent("evt-03", Base.AddMinutes(3), "scan-finding", "HIGH", "security.finding", "claudecode",
            details: "finding.observed",
            structuredJson: """{"defenseclaw.finding.rule_id":"CMD-ENV-DUMP","defenseclaw.finding.confidence":0.8}""");
        _database.InsertEvent("evt-04", Base.AddMinutes(4), "hook_decision", "MEDIUM", "guardrail.evaluation", "claudecode", toolName: "Bash");
        _database.InsertEvent("evt-05", Base.AddMinutes(5), "scan-finding", "CRITICAL", "security.finding", "codex",
            details: "finding.observed", structuredJson: "{ this is not json ");
        _database.InsertEvent("evt-06", Base.AddMinutes(6), "ai_discovery", "INFO", "inventory.ai_discovery", null);
        _database.InsertEvent("evt-07", Base.AddMinutes(7), "tool_invocation", "INFO", "tool.activity", "claudecode",
            toolName: "Read", sessionId: "session-a");
        _database.InsertEvent("evt-08", Base.AddMinutes(8), "config.change.applied", "WARN", "compliance.activity", null);
        _database.InsertEvent("evt-09", Base.AddMinutes(9), "scan-finding", "HIGH", "security.finding", "claudecode");
        _database.InsertEvent("evt-10", Base.AddMinutes(10), "sidecar-stop", "INFO", "platform.health", null);
    }

    [Fact]
    public async Task Returns_newest_first_by_default()
    {
        var page = await _reader.QueryAsync(new AuditQuery { Limit = 3 });

        Assert.Equal(new[] { "evt-10", "evt-09", "evt-08" }, page.Events.Select(e => e.Id));
        Assert.True(page.HasMore);
        Assert.NotNull(page.NextCursor);
    }

    [Fact]
    public async Task Ascending_order_is_available()
    {
        var page = await _reader.QueryAsync(new AuditQuery { Limit = 3, Ascending = true });

        Assert.Equal(new[] { "evt-01", "evt-02", "evt-03" }, page.Events.Select(e => e.Id));
    }

    [Fact]
    public async Task Keyset_pagination_walks_the_whole_table_without_gaps_or_repeats()
    {
        var seen = new List<string>();
        AuditCursor? cursor = null;

        for (var pageIndex = 0; pageIndex < 10; pageIndex++)
        {
            var page = await _reader.QueryAsync(new AuditQuery { Limit = 3, After = cursor });
            seen.AddRange(page.Events.Select(e => e.Id));

            if (!page.HasMore)
            {
                break;
            }

            cursor = page.NextCursor;
        }

        Assert.Equal(10, seen.Count);
        Assert.Equal(seen.Count, seen.Distinct().Count());
        Assert.Equal(
            new[] { "evt-10", "evt-09", "evt-08", "evt-07", "evt-06", "evt-05", "evt-04", "evt-03", "evt-02", "evt-01" },
            seen);
    }

    [Fact]
    public async Task Keyset_pagination_breaks_ties_on_id()
    {
        // Same timestamp for three rows: the id tiebreak must still produce a total order.
        var shared = Base.AddHours(1);
        _database.InsertEvent("tie-a", shared, "scan", "INFO", "asset.scan");
        _database.InsertEvent("tie-b", shared, "scan", "INFO", "asset.scan");
        _database.InsertEvent("tie-c", shared, "scan", "INFO", "asset.scan");

        var first = await _reader.QueryAsync(new AuditQuery { Limit = 2 });
        var second = await _reader.QueryAsync(new AuditQuery { Limit = 2, After = first.NextCursor });

        Assert.Equal(new[] { "tie-c", "tie-b" }, first.Events.Select(e => e.Id));
        Assert.Equal("tie-a", second.Events[0].Id);
    }

    [Fact]
    public async Task Cursor_round_trips_through_a_token()
    {
        var page = await _reader.QueryAsync(new AuditQuery { Limit = 1 });
        var token = page.NextCursor!.Value.ToToken();

        Assert.True(AuditCursor.TryParse(token, out var parsed));
        Assert.Equal(page.NextCursor.Value, parsed);

        var next = await _reader.QueryAsync(new AuditQuery { Limit = 1, After = parsed });
        Assert.Equal("evt-09", next.Events[0].Id);
    }

    [Fact]
    public async Task Filters_by_bucket()
    {
        var events = await _reader.ListAsync(new AuditQuery { Bucket = "security.finding" });

        Assert.Equal(new[] { "evt-09", "evt-05", "evt-03" }, events.Select(e => e.Id));
    }

    [Fact]
    public async Task Filters_by_multiple_buckets()
    {
        var events = await _reader.ListAsync(new AuditQuery
        {
            Buckets = new[] { "platform.health", "compliance.activity" },
        });

        Assert.Equal(new[] { "evt-10", "evt-08", "evt-01" }, events.Select(e => e.Id));
    }

    [Fact]
    public async Task Filters_by_minimum_severity()
    {
        var events = await _reader.ListAsync(new AuditQuery { MinimumSeverity = AuditSeverity.High });

        Assert.Equal(new[] { "evt-09", "evt-05", "evt-03" }, events.Select(e => e.Id));
    }

    [Fact]
    public async Task Minimum_severity_includes_intermediate_levels()
    {
        var events = await _reader.ListAsync(new AuditQuery { MinimumSeverity = AuditSeverity.Warn });

        Assert.Equal(new[] { "evt-09", "evt-08", "evt-05", "evt-04", "evt-03" }, events.Select(e => e.Id));
    }

    [Fact]
    public async Task Filters_by_connector()
    {
        var events = await _reader.ListAsync(new AuditQuery { Connector = "codex" });

        Assert.Equal(new[] { "evt-05" }, events.Select(e => e.Id));
    }

    [Fact]
    public async Task Connector_filter_can_include_platform_scoped_rows()
    {
        // Plenty of rows have a NULL connector; the global connector scope selector needs
        // to be able to keep them visible.
        var events = await _reader.ListAsync(new AuditQuery { Connector = "codex", IncludeNullConnector = true });

        Assert.Equal(new[] { "evt-10", "evt-08", "evt-06", "evt-05", "evt-01" }, events.Select(e => e.Id));
    }

    [Fact]
    public async Task Filters_by_action_substring()
    {
        var events = await _reader.ListAsync(new AuditQuery { ActionContains = "finding" });

        Assert.Equal(new[] { "evt-09", "evt-05", "evt-03" }, events.Select(e => e.Id));
    }

    [Fact]
    public async Task Action_substring_treats_wildcards_literally()
    {
        var events = await _reader.ListAsync(new AuditQuery { ActionContains = "%" });

        Assert.Empty(events);
    }

    [Fact]
    public async Task Filters_by_time_range()
    {
        var events = await _reader.ListAsync(new AuditQuery
        {
            From = Base.AddMinutes(3),
            To = Base.AddMinutes(6),
        });

        // From is inclusive, To is exclusive.
        Assert.Equal(new[] { "evt-05", "evt-04", "evt-03" }, events.Select(e => e.Id));
    }

    [Fact]
    public async Task Filters_combine()
    {
        var events = await _reader.ListAsync(new AuditQuery
        {
            Bucket = "security.finding",
            Connector = "claudecode",
            MinimumSeverity = AuditSeverity.High,
        });

        Assert.Equal(new[] { "evt-09", "evt-03" }, events.Select(e => e.Id));
    }

    [Fact]
    public async Task CountBySeverity_powers_the_dashboard_tiles()
    {
        var counts = await _reader.CountBySeverityAsync(new AuditQuery());

        Assert.Equal(4, counts[AuditSeverity.Info]);
        Assert.Equal(1, counts[AuditSeverity.Low]);
        Assert.Equal(1, counts[AuditSeverity.Warn]);
        Assert.Equal(1, counts[AuditSeverity.Medium]);
        Assert.Equal(2, counts[AuditSeverity.High]);
        Assert.Equal(1, counts[AuditSeverity.Critical]);
    }

    [Fact]
    public async Task CountBySeverity_honours_filters()
    {
        var counts = await _reader.CountBySeverityAsync(new AuditQuery { Connector = "claudecode" });

        Assert.Equal(2, counts[AuditSeverity.High]);
        Assert.False(counts.ContainsKey(AuditSeverity.Critical));
    }

    [Fact]
    public async Task Count_is_independent_of_paging()
    {
        Assert.Equal(10, await _reader.CountAsync(new AuditQuery { Limit = 2 }));
    }

    [Fact]
    public async Task Structured_json_is_parsed_lazily_and_correctly()
    {
        var evt = await _reader.GetByIdAsync("evt-03");

        Assert.NotNull(evt);
        Assert.Equal("CMD-ENV-DUMP", evt!.StructuredString("defenseclaw.finding.rule_id"));
        Assert.Equal(0.8, evt.StructuredJson["defenseclaw.finding.confidence"].GetDouble(), 3);
    }

    [Fact]
    public async Task Malformed_structured_json_yields_an_empty_map_instead_of_throwing()
    {
        var evt = await _reader.GetByIdAsync("evt-05");

        Assert.NotNull(evt);
        Assert.Empty(evt!.StructuredJson);
        Assert.Equal(AuditSeverity.Critical, evt.SeverityLevel);
    }

    [Fact]
    public async Task Rows_without_structured_json_expose_an_empty_map()
    {
        var evt = await _reader.GetByIdAsync("evt-01");

        Assert.Null(evt!.StructuredJsonRaw);
        Assert.Empty(evt.StructuredJson);
    }

    [Fact]
    public async Task Maps_every_column_the_panels_read()
    {
        var evt = await _reader.GetByIdAsync("evt-07");

        Assert.NotNull(evt);
        Assert.Equal("tool_invocation", evt!.Action);
        Assert.Equal("INFO", evt.Severity);
        Assert.Equal("tool.activity", evt.Bucket);
        Assert.Equal("claudecode", evt.Connector);
        Assert.Equal("Read", evt.ToolName);
        Assert.Equal("session-a", evt.SessionId);
        Assert.Equal("audit_logger", evt.Actor);
        Assert.Equal(Base.AddMinutes(7), evt.Timestamp);
        Assert.Equal(TestAuditDatabase.FormatTimestamp(Base.AddMinutes(7)), evt.RawTimestamp);
        Assert.True(evt.TimestampNanos > 0);
    }

    [Fact]
    public async Task GetById_returns_null_for_an_unknown_id()
    {
        Assert.Null(await _reader.GetByIdAsync("does-not-exist"));
    }

    [Fact]
    public async Task Lists_distinct_filter_values()
    {
        Assert.Equal(
            new[]
            {
                "asset.scan", "compliance.activity", "guardrail.evaluation",
                "inventory.ai_discovery", "platform.health", "security.finding", "tool.activity",
            },
            await _reader.ListBucketsAsync());

        Assert.Equal(new[] { "claudecode", "codex" }, await _reader.ListConnectorsAsync());
        Assert.Contains("scan-finding", await _reader.ListActionsAsync());
    }

    [Fact]
    public void Connection_string_is_read_only()
    {
        // The gateway writes this file continuously; the app must never take a write lock.
        var connectionString = AuditReader.BuildReadOnlyConnectionString(@"C:\x\audit.db");

        Assert.Contains("Mode=ReadOnly", connectionString, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Reader_cannot_write_to_the_database()
    {
        var reader = new AuditReader(_database.Path);
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            AuditReader.BuildReadOnlyConnectionString(reader.DatabasePath));
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM audit_events";

        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => command.ExecuteNonQueryAsync());
    }

    [Fact]
    public void Limit_is_clamped()
    {
        Assert.Equal(1, new AuditQuery { Limit = 0 }.EffectiveLimit);
        Assert.Equal(AuditQuery.MaxLimit, new AuditQuery { Limit = int.MaxValue }.EffectiveLimit);
    }

    [Theory]
    [InlineData("INFO", AuditSeverity.Info)]
    [InlineData("low", AuditSeverity.Low)]
    [InlineData("WARN", AuditSeverity.Warn)]
    [InlineData("WARNING", AuditSeverity.Warn)]
    [InlineData("MEDIUM", AuditSeverity.Medium)]
    [InlineData("HIGH", AuditSeverity.High)]
    [InlineData("CRITICAL", AuditSeverity.Critical)]
    [InlineData("nonsense", AuditSeverity.Unknown)]
    [InlineData(null, AuditSeverity.Unknown)]
    public void Severity_parsing_covers_the_observed_values(string? stored, AuditSeverity expected)
    {
        Assert.Equal(expected, AuditSeverityExtensions.Parse(stored));
    }

    public void Dispose() => _database.Dispose();
}
