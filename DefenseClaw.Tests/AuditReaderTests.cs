using DefenseClaw.Core.Audit;
using DefenseClaw.Tests.TestSupport;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.Tests;

/// <summary>
/// Runs against a temp database created from the real DDL with synthetic rows only.
/// No production audit data is used.
/// <para>
/// The DDL includes <c>idx_retention_audit_events_timestamp(retention_timestamp_unix_nano, id)</c>
/// and the triggers that fill the column, so the query-plan assertions below check the same
/// index the live 2.7 GB database relies on. A plan regression (a window predicate that stops
/// being sargable, an ORDER BY that needs a temp B-tree) is a multi-second stall on that
/// database and invisible on ten synthetic rows — which is why the plans are asserted directly.
/// </para>
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
    public async Task From_only_and_to_only_windows_are_inclusive_and_exclusive_respectively()
    {
        var since = await _reader.ListAsync(new AuditQuery { From = Base.AddMinutes(9) });
        var before = await _reader.ListAsync(new AuditQuery { To = Base.AddMinutes(2) });

        Assert.Equal(new[] { "evt-10", "evt-09" }, since.Select(e => e.Id));
        Assert.Equal(new[] { "evt-01" }, before.Select(e => e.Id));
        Assert.Equal(2, await _reader.CountAsync(new AuditQuery { From = Base.AddMinutes(9) }));
        Assert.Equal(1, await _reader.CountAsync(new AuditQuery { To = Base.AddMinutes(2) }));
    }

    [Fact]
    public async Task CountBySeverity_counts_every_stored_spelling_and_still_sums_equivalent_ones()
    {
        // Each stored spelling (high, High, HIGH, warning, ...) is counted on its own now, from the index;
        // AuditSeverityExtensions.Parse is case-insensitive, so nothing may be lost or split.
        _database.InsertEvent("case-1", Base.AddMinutes(20), "scan", "high", "asset.scan");
        _database.InsertEvent("case-2", Base.AddMinutes(21), "scan", "High", "asset.scan");
        _database.InsertEvent("case-3", Base.AddMinutes(22), "scan", "warning", "asset.scan");

        var counts = await _reader.CountBySeverityAsync(new AuditQuery { From = Base });

        Assert.Equal(4, counts[AuditSeverity.High]);
        Assert.Equal(2, counts[AuditSeverity.Warn]);
        Assert.Equal(13, counts.Values.Sum());
    }

    // ---- Keyset paging: every row exactly once, in one total order, ties included. ----

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(7)]
    public async Task Keyset_pagination_across_a_timestamp_tie_visits_every_row_exactly_once(int pageSize)
    {
        // Five rows share one timestamp and are the newest: with small pages the cursor lands
        // in the middle of the tie, which is where a (timestamp, id) keyset goes wrong if the
        // id comparison is missing or points the wrong way.
        var shared = Base.AddHours(1);
        foreach (var id in new[] { "tie-a", "tie-b", "tie-c", "tie-d", "tie-e" })
        {
            _database.InsertEvent(id, shared, "scan", "INFO", "asset.scan");
        }

        var expectedDescending = new[]
        {
            "tie-e", "tie-d", "tie-c", "tie-b", "tie-a",
            "evt-10", "evt-09", "evt-08", "evt-07", "evt-06", "evt-05", "evt-04", "evt-03", "evt-02", "evt-01",
        };

        Assert.Equal(expectedDescending, await WalkAsync(pageSize, ascending: false));
        Assert.Equal(Enumerable.Reverse(expectedDescending), await WalkAsync(pageSize, ascending: true));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Keyset_pagination_inside_a_window_stays_inside_it_and_keeps_tie_order(int pageSize)
    {
        var shared = Base.AddMinutes(4);
        _database.InsertEvent("tie-a", shared, "scan", "INFO", "asset.scan");
        _database.InsertEvent("tie-b", shared, "scan", "INFO", "asset.scan");

        var window = await WalkAsync(pageSize, ascending: false, from: Base.AddMinutes(3), to: Base.AddMinutes(6));

        // evt-04 shares its minute with the two tie rows; ids break the tie: tie-b > tie-a > evt-04.
        Assert.Equal(new[] { "evt-05", "tie-b", "tie-a", "evt-04", "evt-03" }, window);
    }

    [Fact]
    public async Task A_row_whose_retention_key_is_null_keeps_a_total_order_and_stays_visible()
    {
        // The retention triggers write NULL for a timestamp matching none of their patterns, and
        // a database migrated in from before the column existed could hold NULLs the triggers
        // never touched. The live database has none, but the reader must not skip or repeat such
        // a row: while any exist it drops to the COALESCE ordering (slow, correct).
        NullOutRetentionKey("evt-05");
        NullOutRetentionKey("evt-06");

        var expectedDescending = new[]
        {
            "evt-10", "evt-09", "evt-08", "evt-07", "evt-06", "evt-05", "evt-04", "evt-03", "evt-02", "evt-01",
        };

        foreach (var pageSize in new[] { 1, 2, 3, 100 })
        {
            Assert.Equal(expectedDescending, await WalkAsync(pageSize, ascending: false));
            Assert.Equal(Enumerable.Reverse(expectedDescending), await WalkAsync(pageSize, ascending: true));
        }

        var window = new AuditQuery { From = Base.AddMinutes(5), To = Base.AddMinutes(7) };
        Assert.Equal(new[] { "evt-06", "evt-05" }, (await _reader.ListAsync(window)).Select(e => e.Id));
        Assert.Equal(2, await _reader.CountAsync(window));

        var counts = await _reader.CountBySeverityAsync(window);
        Assert.Equal(1, counts[AuditSeverity.Critical]);
        Assert.Equal(1, counts[AuditSeverity.Info]);
    }

    // ---- Query plans: the regression guard for the 1.6-1.7 s full scans (live 2.7 GB audit.db). ----

    [Fact]
    public async Task Newest_page_walks_the_retention_index_with_no_sort()
    {
        var plan = await _reader.ExplainAsync(new AuditQuery { Limit = 50 }, AuditQueryShape.Page);

        AssertUsesRetentionIndex(plan);
        AssertNoSortForOrderBy(plan);
    }

    [Fact]
    public async Task Windowed_page_seeks_the_retention_index_with_no_sort()
    {
        var plan = await _reader.ExplainAsync(
            new AuditQuery { Limit = 50, From = Base.AddMinutes(3), To = Base.AddMinutes(9) },
            AuditQueryShape.Page);

        AssertUsesRetentionIndex(plan);
        AssertNoSortForOrderBy(plan);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Next_page_seeks_the_retention_index_at_any_depth(bool ascending)
    {
        var first = await _reader.QueryAsync(new AuditQuery { Limit = 2, Ascending = ascending });
        Assert.NotNull(first.NextCursor);

        var plan = await _reader.ExplainAsync(
            new AuditQuery { Limit = 2, Ascending = ascending, After = first.NextCursor, From = Base },
            AuditQueryShape.Page);

        AssertUsesRetentionIndex(plan);
        AssertNoSortForOrderBy(plan);
    }

    [Fact]
    public async Task Window_count_is_an_index_search_not_a_scan()
    {
        var from = new AuditQuery { From = Base.AddMinutes(3) };
        var both = new AuditQuery { From = Base.AddMinutes(3), To = Base.AddMinutes(9) };

        AssertUsesRetentionIndex(await _reader.ExplainAsync(from, AuditQueryShape.Count));
        AssertUsesRetentionIndex(await _reader.ExplainAsync(both, AuditQueryShape.Count));
    }

    [Fact]
    public async Task Window_count_stays_sargable_when_a_row_lacks_the_retention_key()
    {
        // The null-safe form is (range OR (col IS NULL AND text-derived range)) — the planner
        // still answers it with two index probes (MULTI-INDEX OR), not a scan.
        NullOutRetentionKey("evt-05");

        var plan = await _reader.ExplainAsync(
            new AuditQuery { From = Base.AddMinutes(3), To = Base.AddMinutes(9) },
            AuditQueryShape.Count);

        AssertUsesRetentionIndex(plan);
    }

    [Fact]
    public async Task Severity_tiles_for_a_window_are_counted_from_the_severity_index_and_the_window_total_from_the_retention_index()
    {
        var plan = await _reader.ExplainAsync(
            new AuditQuery { From = Base.AddMinutes(3) },
            AuditQueryShape.CountBySeverity);

        // This used to assert the opposite: GROUP BY UPPER(severity) could not use idx_audit_severity_timestamp, so the
        // plan was the retention range plus a temp B-tree and a fetch of every row in the window (614 MB per Overview
        // refresh on the live table). The tiles are now one covering count per spelling; AuditSeverityTests holds the
        // detailed plan and equivalence checks.
        Assert.Contains(plan, line => line.Contains("COVERING INDEX idx_audit_severity_timestamp", StringComparison.Ordinal));
        Assert.Contains(plan, line => line.Contains("idx_retention_audit_events_timestamp", StringComparison.Ordinal));
        Assert.DoesNotContain(plan, line => line.Contains("TEMP B-TREE", StringComparison.Ordinal));
    }

    // ---- Connector filter: seek for a rare connector, walk for a common one (live audit.db: 3.5-5.9 s -> 1 ms). ----
    //
    // The fixture has no sqlite_stat1, like the live database, so the planner reproduces the live
    // plans: "e.connector = $c" is always idx_audit_connector (+ a sort), "+e.connector = $c" is
    // always the retention index. The seed has five claudecode rows, one codex row and four
    // connector-less rows; lowering the reader's threshold puts the line between them.

    private const string ConnectorIndex = "idx_audit_connector";

    private static readonly AuditQueryShape[] AllShapes =
    {
        AuditQueryShape.Page,
        AuditQueryShape.Count,
        AuditQueryShape.CountBySeverity,
    };

    [Fact]
    public async Task A_rare_connector_is_sought_on_the_connector_index()
    {
        // The default threshold is 10,000 rows: every connector in a ten-row fixture is rare.
        foreach (var shape in AllShapes)
        {
            var plan = await _reader.ExplainAsync(new AuditQuery { Connector = "codex", From = Base }, shape);

            Assert.Contains(plan, line => line.Contains(ConnectorIndex, StringComparison.Ordinal));
            Assert.DoesNotContain(
                plan, line => line.Contains("idx_retention_audit_events_timestamp", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task A_common_connector_walks_the_retention_index_and_never_seeks_the_connector_index()
    {
        // claudecode has five rows, so a threshold of three makes it common.
        var reader = new AuditReader(_database.Path, commonRowThreshold: 3);

        foreach (var shape in AllShapes)
        {
            var plan = await reader.ExplainAsync(new AuditQuery { Connector = "claudecode", From = Base }, shape);

            AssertUsesRetentionIndex(plan);
            Assert.DoesNotContain(plan, line => line.Contains(ConnectorIndex, StringComparison.Ordinal));
        }

        // The point of the walk: the newest page needs no sort, with or without a window.
        AssertNoSortForOrderBy(await reader.ExplainAsync(
            new AuditQuery { Connector = "claudecode", From = Base, Limit = 50 }, AuditQueryShape.Page));

        var unbounded = await reader.ExplainAsync(new AuditQuery { Connector = "claudecode", Limit = 50 }, AuditQueryShape.Page);
        AssertUsesRetentionIndex(unbounded);
        AssertNoSortForOrderBy(unbounded);
        Assert.DoesNotContain(unbounded, line => line.Contains(ConnectorIndex, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_next_page_for_a_common_connector_still_walks_the_retention_index()
    {
        var reader = new AuditReader(_database.Path, commonRowThreshold: 3);
        var first = await reader.QueryAsync(new AuditQuery { Connector = "claudecode", Limit = 2 });
        Assert.NotNull(first.NextCursor);

        var plan = await reader.ExplainAsync(
            new AuditQuery { Connector = "claudecode", Limit = 2, After = first.NextCursor, From = Base },
            AuditQueryShape.Page);

        AssertUsesRetentionIndex(plan);
        AssertNoSortForOrderBy(plan);
        Assert.DoesNotContain(plan, line => line.Contains(ConnectorIndex, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_threshold_is_the_row_count_at_which_a_connector_becomes_common()
    {
        // claudecode has exactly five rows: five makes it common, six leaves it rare.
        var atThreshold = new AuditReader(_database.Path, commonRowThreshold: 5);
        var aboveIt = new AuditReader(_database.Path, commonRowThreshold: 6);
        var query = new AuditQuery { Connector = "claudecode", From = Base };

        Assert.DoesNotContain(
            await atThreshold.ExplainAsync(query, AuditQueryShape.Count),
            line => line.Contains(ConnectorIndex, StringComparison.Ordinal));
        Assert.Contains(
            await aboveIt.ExplainAsync(query, AuditQueryShape.Count),
            line => line.Contains(ConnectorIndex, StringComparison.Ordinal));
    }

    [Fact]
    public async Task With_platform_rows_included_a_common_connector_drops_the_multi_index_or_and_a_rare_one_keeps_it()
    {
        // claudecode has five rows (common at a threshold of five, rare at six).
        var busy = new AuditQuery { Connector = "claudecode", IncludeNullConnector = true, From = Base };
        var common = new AuditReader(_database.Path, commonRowThreshold: 5);
        var rare = new AuditReader(_database.Path, commonRowThreshold: 6);

        foreach (var shape in AllShapes)
        {
            var walk = await common.ExplainAsync(busy, shape);
            AssertUsesRetentionIndex(walk);
            Assert.DoesNotContain(walk, line => line.Contains("MULTI-INDEX OR", StringComparison.Ordinal));
            Assert.DoesNotContain(walk, line => line.Contains(ConnectorIndex, StringComparison.Ordinal));

            var seek = await rare.ExplainAsync(busy, shape);
            Assert.Contains(seek, line => line.Contains("MULTI-INDEX OR", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task The_probe_counts_only_the_named_connector_not_the_platform_rows_that_are_also_kept()
    {
        // codex has one row and four rows have no connector: the predicate selects five, but the
        // connector itself is rare. Counting the platform rows would make it "common" and send the
        // no-window severity tiles for "codex + platform rows" down a full table scan (1.1 s
        // against 0.18 s on the live database), so the probe must ignore them.
        var reader = new AuditReader(_database.Path, commonRowThreshold: 5);
        var query = new AuditQuery { Connector = "codex", IncludeNullConnector = true, From = Base };

        foreach (var shape in AllShapes)
        {
            var plan = await reader.ExplainAsync(query, shape);

            Assert.Contains(plan, line => line.Contains("MULTI-INDEX OR", StringComparison.Ordinal));
            Assert.Contains(plan, line => line.Contains(ConnectorIndex, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task A_query_with_no_connector_is_untouched_by_the_connector_strategy()
    {
        // Nothing to decide, so the probe must not run and the plan must be the plain retention walk
        // even for a reader whose threshold would call every connector common.
        var reader = new AuditReader(_database.Path, commonRowThreshold: 1);

        var plan = await reader.ExplainAsync(new AuditQuery { From = Base, Limit = 50 }, AuditQueryShape.Page);

        AssertUsesRetentionIndex(plan);
        AssertNoSortForOrderBy(plan);
        Assert.DoesNotContain(plan, line => line.Contains(ConnectorIndex, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Both_connector_strategies_select_exactly_the_same_rows()
    {
        // Threshold 1 walks for any connector that has a row; int.MaxValue always seeks. Whatever
        // the plan, the rows, their order, the cursor, the total and the tiles must not differ.
        var walk = new AuditReader(_database.Path, commonRowThreshold: 1);
        var seek = new AuditReader(_database.Path, commonRowThreshold: int.MaxValue);

        var queries = new[]
        {
            new AuditQuery { Connector = "claudecode", Limit = 2 },
            new AuditQuery { Connector = "claudecode", Ascending = true, From = Base.AddMinutes(3), To = Base.AddMinutes(9) },
            new AuditQuery { Connector = "claudecode", MinimumSeverity = AuditSeverity.High },
            new AuditQuery { Connector = "codex", IncludeNullConnector = true, Limit = 3 },
            new AuditQuery { Connector = "claudecode", IncludeNullConnector = true, MinimumSeverity = AuditSeverity.Warn },
            new AuditQuery { Connector = "claudecode", IncludeNullConnector = true, From = Base.AddMinutes(4), To = Base.AddMinutes(9) },
            new AuditQuery { Connector = "nobody" },
            new AuditQuery { Connector = "nobody", IncludeNullConnector = true },
        };

        foreach (var query in queries)
        {
            var viaWalk = await walk.QueryAsync(query);
            var viaSeek = await seek.QueryAsync(query);

            Assert.Equal(viaSeek.Events.Select(e => e.Id), viaWalk.Events.Select(e => e.Id));
            Assert.Equal(viaSeek.HasMore, viaWalk.HasMore);
            Assert.Equal(viaSeek.NextCursor, viaWalk.NextCursor);
            Assert.Equal(await seek.CountAsync(query), await walk.CountAsync(query));
            Assert.Equal(
                DescribeTiles(await seek.CountBySeverityAsync(query)),
                DescribeTiles(await walk.CountBySeverityAsync(query)));
        }

        // The keyset walk under the walk strategy visits each connector row once, newest first.
        var seen = new List<string>();
        AuditCursor? cursor = null;
        for (var guard = 0; guard < 20; guard++)
        {
            var page = await walk.QueryAsync(new AuditQuery { Connector = "claudecode", Limit = 2, After = cursor });
            seen.AddRange(page.Events.Select(e => e.Id));
            if (!page.HasMore)
            {
                break;
            }

            cursor = page.NextCursor;
        }

        Assert.Equal(new[] { "evt-09", "evt-07", "evt-04", "evt-03", "evt-02" }, seen);
    }

    [Fact]
    public void The_common_connector_threshold_must_be_positive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AuditReader(_database.Path, commonRowThreshold: 0));
    }

    // ---- Bucket filter: same probe, same threshold; a walk only where it pays (live telemetry.ingest page 1.3-1.9 s -> 1 ms). ----
    //
    // Seed buckets: security.finding 3 rows, platform.health 2, five single-row buckets. A threshold of
    // three therefore makes security.finding common and everything else rare.

    private const string BucketIndex = "idx_audit_bucket_timestamp";

    [Fact]
    public async Task A_rare_bucket_is_sought_on_the_bucket_index()
    {
        // The default threshold is 10,000 rows.
        foreach (var shape in AllShapes)
        {
            var plan = await _reader.ExplainAsync(new AuditQuery { Bucket = "security.finding", From = Base }, shape);

            Assert.Contains(plan, line => line.Contains(BucketIndex, StringComparison.Ordinal));
            Assert.DoesNotContain(
                plan, line => line.Contains("idx_retention_audit_events_timestamp", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task A_common_bucket_walks_the_retention_index_for_the_page_and_for_windowed_aggregates()
    {
        var reader = new AuditReader(_database.Path, commonRowThreshold: 3);

        foreach (var shape in AllShapes)
        {
            var plan = await reader.ExplainAsync(new AuditQuery { Bucket = "security.finding", From = Base }, shape);

            AssertUsesRetentionIndex(plan);
            Assert.DoesNotContain(plan, line => line.Contains(BucketIndex, StringComparison.Ordinal));
        }

        // The point of the walk: the newest page needs no sort, with or without a window.
        var windowed = await reader.ExplainAsync(new AuditQuery { Bucket = "security.finding", From = Base, Limit = 50 }, AuditQueryShape.Page);
        AssertNoSortForOrderBy(windowed);

        var unbounded = await reader.ExplainAsync(new AuditQuery { Bucket = "security.finding", Limit = 50 }, AuditQueryShape.Page);
        AssertUsesRetentionIndex(unbounded);
        AssertNoSortForOrderBy(unbounded);
        Assert.DoesNotContain(unbounded, line => line.Contains(BucketIndex, StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_aggregate_with_no_lower_bound_stays_on_the_index_even_for_a_common_bucket_or_connector()
    {
        // Walking reads the whole table, the index only the matching rows, so without a From bound the
        // COUNT and the severity tiles must not take the walk (live, tool.activity all-time tiles:
        // 0.29 s on the index, 1.37 s walking). Threshold 3 makes both filters below common.
        var reader = new AuditReader(_database.Path, commonRowThreshold: 3);

        foreach (var shape in new[] { AuditQueryShape.Count, AuditQueryShape.CountBySeverity })
        {
            var bucket = await reader.ExplainAsync(new AuditQuery { Bucket = "security.finding" }, shape);
            Assert.Contains(bucket, line => line.Contains(BucketIndex, StringComparison.Ordinal));
            Assert.DoesNotContain(bucket, line => line.StartsWith("SCAN", StringComparison.Ordinal));

            var connector = await reader.ExplainAsync(new AuditQuery { Connector = "claudecode" }, shape);
            Assert.Contains(connector, line => line.Contains(ConnectorIndex, StringComparison.Ordinal));
            Assert.DoesNotContain(connector, line => line.StartsWith("SCAN", StringComparison.Ordinal));
        }

        // ...while the same filters with a lower bound do walk (covered above), and a page always does.
        var page = await reader.ExplainAsync(new AuditQuery { Bucket = "security.finding" }, AuditQueryShape.Page);
        Assert.DoesNotContain(page, line => line.Contains(BucketIndex, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_bucket_threshold_is_the_row_count_at_which_a_bucket_becomes_common()
    {
        // security.finding has exactly three rows.
        var atThreshold = new AuditReader(_database.Path, commonRowThreshold: 3);
        var aboveIt = new AuditReader(_database.Path, commonRowThreshold: 4);
        var query = new AuditQuery { Bucket = "security.finding", From = Base };

        Assert.DoesNotContain(
            await atThreshold.ExplainAsync(query, AuditQueryShape.Page),
            line => line.Contains(BucketIndex, StringComparison.Ordinal));
        Assert.Contains(
            await aboveIt.ExplainAsync(query, AuditQueryShape.Page),
            line => line.Contains(BucketIndex, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Several_buckets_are_probed_together_from_Bucket_and_Buckets()
    {
        // platform.health (2) + security.finding (3) = 5 rows, however the names reach the query.
        var query = new AuditQuery
        {
            Bucket = "platform.health",
            Buckets = new[] { "security.finding", " ", string.Empty },
            From = Base,
        };
        var common = new AuditReader(_database.Path, commonRowThreshold: 5);
        var rare = new AuditReader(_database.Path, commonRowThreshold: 6);

        var walk = await common.ExplainAsync(query, AuditQueryShape.Page);
        AssertUsesRetentionIndex(walk);
        AssertNoSortForOrderBy(walk);
        Assert.DoesNotContain(walk, line => line.Contains(BucketIndex, StringComparison.Ordinal));

        Assert.Contains(
            await rare.ExplainAsync(query, AuditQueryShape.Page),
            line => line.Contains(BucketIndex, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Blank_bucket_names_are_no_filter_at_all()
    {
        var reader = new AuditReader(_database.Path, commonRowThreshold: 1);
        var query = new AuditQuery { Bucket = " ", Buckets = new[] { string.Empty }, From = Base, Limit = 50 };

        var plan = await reader.ExplainAsync(query, AuditQueryShape.Page);

        AssertUsesRetentionIndex(plan);
        Assert.DoesNotContain(plan, line => line.Contains(BucketIndex, StringComparison.Ordinal));
        Assert.Equal(10, (await reader.QueryAsync(query with { Limit = 100 })).Events.Count);
    }

    [Fact]
    public async Task A_common_bucket_and_a_common_connector_are_both_walked()
    {
        // security.finding (3 rows) and claudecode (5 rows) are both common at a threshold of three.
        var reader = new AuditReader(_database.Path, commonRowThreshold: 3);

        var plan = await reader.ExplainAsync(
            new AuditQuery { Bucket = "security.finding", Connector = "claudecode", From = Base, Limit = 50 },
            AuditQueryShape.Page);

        AssertUsesRetentionIndex(plan);
        AssertNoSortForOrderBy(plan);
        Assert.DoesNotContain(plan, line => line.Contains(BucketIndex, StringComparison.Ordinal));
        Assert.DoesNotContain(plan, line => line.Contains(ConnectorIndex, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Both_bucket_strategies_select_exactly_the_same_rows()
    {
        // Threshold 1 walks for any bucket that has a row; int.MaxValue always seeks.
        var walk = new AuditReader(_database.Path, commonRowThreshold: 1);
        var seek = new AuditReader(_database.Path, commonRowThreshold: int.MaxValue);

        var queries = new[]
        {
            new AuditQuery { Bucket = "security.finding", Limit = 2 },
            new AuditQuery { Bucket = "security.finding", Ascending = true, From = Base.AddMinutes(3), To = Base.AddMinutes(9) },
            new AuditQuery { Bucket = "platform.health", Buckets = new[] { "security.finding", "asset.scan" } },
            new AuditQuery { Buckets = new[] { "security.finding", "tool.activity" }, MinimumSeverity = AuditSeverity.High },
            new AuditQuery { Bucket = "security.finding", Connector = "claudecode" },
            new AuditQuery { Bucket = "security.finding", Connector = "codex", IncludeNullConnector = true },
            new AuditQuery { Bucket = "no.such.bucket" },
        };

        foreach (var query in queries)
        {
            var viaWalk = await walk.QueryAsync(query);
            var viaSeek = await seek.QueryAsync(query);

            Assert.Equal(viaSeek.Events.Select(e => e.Id), viaWalk.Events.Select(e => e.Id));
            Assert.Equal(viaSeek.HasMore, viaWalk.HasMore);
            Assert.Equal(viaSeek.NextCursor, viaWalk.NextCursor);
            Assert.Equal(await seek.CountAsync(query), await walk.CountAsync(query));
            Assert.Equal(
                DescribeTiles(await seek.CountBySeverityAsync(query)),
                DescribeTiles(await walk.CountBySeverityAsync(query)));

            // The windowed aggregates take the walk under the walk reader; their answers must not move.
            var windowed = query with { From = Base.AddMinutes(2) };
            Assert.Equal(await seek.CountAsync(windowed), await walk.CountAsync(windowed));
            Assert.Equal(
                DescribeTiles(await seek.CountBySeverityAsync(windowed)),
                DescribeTiles(await walk.CountBySeverityAsync(windowed)));
        }

        // The keyset walk under the walk strategy visits each security.finding row once, newest first.
        var seen = new List<string>();
        AuditCursor? cursor = null;
        for (var guard = 0; guard < 20; guard++)
        {
            var page = await walk.QueryAsync(new AuditQuery { Bucket = "security.finding", Limit = 2, After = cursor });
            seen.AddRange(page.Events.Select(e => e.Id));
            if (!page.HasMore)
            {
                break;
            }

            cursor = page.NextCursor;
        }

        Assert.Equal(new[] { "evt-09", "evt-05", "evt-03" }, seen);
    }

    // ---- Threading: Microsoft.Data.Sqlite "async" completes synchronously, so the reader must hop to the pool itself. ----

    [Fact]
    public async Task Every_query_method_returns_a_pending_task_instead_of_running_the_query_on_the_callers_thread()
    {
        // Deterministic, no sleeps: an EXCLUSIVE lock on this rollback-journal database makes every
        // read wait in SQLite's busy handler (BusyTimeoutSeconds = 10). A method that ran its body
        // inline on the caller's thread would sit in that handler and only come back after 10 s,
        // already failed; one that offloads to the pool returns immediately with a task that cannot
        // finish until the lock is released below.
        using var lockHolder = _database.OpenWritable();
        RunSql(lockHolder, "BEGIN EXCLUSIVE");

        var calls = new (string Name, Func<Task> Call)[]
        {
            (nameof(AuditReader.QueryAsync), () => _reader.QueryAsync(new AuditQuery())),
            (nameof(AuditReader.ListAsync), () => _reader.ListAsync(new AuditQuery())),
            (nameof(AuditReader.GetByIdAsync), () => _reader.GetByIdAsync("evt-01")),
            (nameof(AuditReader.CountAsync), () => _reader.CountAsync(new AuditQuery())),
            (nameof(AuditReader.CountBySeverityAsync), () => _reader.CountBySeverityAsync(new AuditQuery())),
            (nameof(AuditReader.ExplainAsync), () => _reader.ExplainAsync(new AuditQuery(), AuditQueryShape.Page)),
            (nameof(AuditReader.ListBucketsAsync), () => _reader.ListBucketsAsync()),
            (nameof(AuditReader.ListConnectorsAsync), () => _reader.ListConnectorsAsync()),
            (nameof(AuditReader.ListActionsAsync), () => _reader.ListActionsAsync()),
        };

        var started = new List<Task>();
        foreach (var (name, call) in calls)
        {
            var task = call();
            Assert.False(task.IsCompleted, $"{name} ran its query on the caller's thread (it returned only after the lock timed out).");
            started.Add(task);
        }

        RunSql(lockHolder, "ROLLBACK");

        // Now the queries can run; every one of them completes normally.
        await Task.WhenAll(started).WaitAsync(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task A_token_cancelled_before_the_call_never_starts_the_query()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _reader.CountAsync(new AuditQuery(), cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _reader.QueryAsync(new AuditQuery(), cancelled.Token));
    }

    [Fact]
    public async Task Argument_errors_still_arrive_through_the_returned_task()
    {
        // The methods used to be async, so a bad argument faulted the task rather than throwing at the call.
        await Assert.ThrowsAsync<ArgumentNullException>(() => _reader.QueryAsync(null!));
        await Assert.ThrowsAsync<ArgumentException>(() => _reader.GetByIdAsync(string.Empty));
    }

    private static void RunSql(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string DescribeTiles(IReadOnlyDictionary<AuditSeverity, int> counts) =>
        string.Join(",", counts.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}={pair.Value}"));

    // ---- Read-only access against a WAL database (perf/durability eval finding #15). ----

    [Fact]
    public void Connection_string_sets_an_explicit_busy_timeout()
    {
        // Microsoft.Data.Sqlite retries SQLITE_BUSY until the command timeout; the default is an
        // implicit 30 s. The reader pins it, so a wedged writer fails a refresh in seconds.
        var parsed = new SqliteConnectionStringBuilder(AuditReader.BuildReadOnlyConnectionString(@"C:\x\audit.db"));

        Assert.Equal(AuditReader.BusyTimeoutSeconds, parsed.DefaultTimeout);
        Assert.Equal(SqliteOpenMode.ReadOnly, parsed.Mode);
    }

    [Fact]
    public async Task Reads_a_hot_wal_with_no_shm_file_using_the_read_only_connection()
    {
        // Finding #15 assumed a read-only connection cannot recover a WAL database whose -shm is
        // gone (a killed gateway, a copied file) and proposed a ReadWrite fallback. Measured
        // otherwise: only the main file is opened read-only, SQLite builds the -shm itself. This
        // pins that, so nobody adds a write-capable fallback on the strength of the old theory.
        using var directory = new TempDirectory("dcw-wal");
        var livePath = directory.File("live.db");
        var snapshotPath = directory.File("snapshot.db");

        var writerString = new SqliteConnectionStringBuilder
        {
            DataSource = livePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();

        await using (var writer = new SqliteConnection(writerString))
        {
            await writer.OpenAsync();

            await using (var setup = writer.CreateCommand())
            {
                // No checkpoint: the rows below live only in the -wal while the writer is open.
                setup.CommandText = "PRAGMA journal_mode = WAL; PRAGMA wal_autocheckpoint = 0; "
                    + FixtureFiles.ReadText(FixtureFiles.AuditSchema);
                await setup.ExecuteNonQueryAsync();
            }

            await using (var insert = writer.CreateCommand())
            {
                insert.CommandText = """
                    INSERT INTO audit_events (id, timestamp, action, actor, severity)
                    VALUES ('wal-1', '2026-07-28T12:01:00.0000000Z', 'scan', 'audit_logger', 'INFO'),
                           ('wal-2', '2026-07-28T12:02:00.0000000Z', 'scan', 'audit_logger', 'HIGH'),
                           ('wal-3', '2026-07-28T12:03:00.0000000Z', 'scan', 'audit_logger', 'INFO')
                    """;
                await insert.ExecuteNonQueryAsync();
            }

            // A crashed writer leaves db + wal (+ a stale shm); a file copy leaves db + wal only.
            CopySharedFile(livePath, snapshotPath);
            CopySharedFile(livePath + "-wal", snapshotPath + "-wal");
            Assert.False(File.Exists(snapshotPath + "-shm"));

            var reader = new AuditReader(snapshotPath);
            var events = await reader.ListAsync(new AuditQuery());

            Assert.Equal(new[] { "wal-3", "wal-2", "wal-1" }, events.Select(e => e.Id));
            Assert.Equal(3, await reader.CountAsync(new AuditQuery()));
        }

        SqliteConnection.ClearAllPools();
    }

    private static void CopySharedFile(string from, string to)
    {
        // The writer still has the file open; ReadWrite sharing lets us read it anyway.
        using var source = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var target = new FileStream(to, FileMode.Create, FileAccess.Write, FileShare.None);
        source.CopyTo(target);
    }

    private async Task<List<string>> WalkAsync(
        int pageSize,
        bool ascending,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null)
    {
        var seen = new List<string>();
        AuditCursor? cursor = null;

        for (var guard = 0; guard < 200; guard++)
        {
            var page = await _reader.QueryAsync(new AuditQuery
            {
                Limit = pageSize,
                Ascending = ascending,
                After = cursor,
                From = from,
                To = to,
            });

            seen.AddRange(page.Events.Select(e => e.Id));
            if (!page.HasMore)
            {
                return seen;
            }

            cursor = page.NextCursor;
        }

        throw new InvalidOperationException("Paging did not terminate.");
    }

    /// <summary>
    /// Simulates a row the retention triggers never filled. Setting only the column does not
    /// re-fire the AFTER UPDATE OF timestamp trigger.
    /// </summary>
    private void NullOutRetentionKey(string id)
    {
        using var connection = _database.OpenWritable();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE audit_events SET retention_timestamp_unix_nano = NULL WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    private static void AssertUsesRetentionIndex(IReadOnlyList<string> plan)
    {
        Assert.Contains(plan, line => line.Contains("idx_retention_audit_events_timestamp", StringComparison.Ordinal));

        // A bare "SCAN e" (no USING INDEX) is the full table scan this guards against.
        Assert.DoesNotContain(
            plan,
            line => line.StartsWith("SCAN", StringComparison.Ordinal) && !line.Contains("USING", StringComparison.Ordinal));
    }

    private static void AssertNoSortForOrderBy(IReadOnlyList<string> plan) =>
        Assert.DoesNotContain(plan, line => line.Contains("TEMP B-TREE FOR ORDER BY", StringComparison.Ordinal));

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
