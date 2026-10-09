using DefenseClaw.Core.Audit;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// "An oversized row is unavailable, not empty." Wherever a reader limits a payload (<c>details</c>, <c>structured_json</c>,
/// <c>payload_json</c>, a change's before / after / diff), a value over the limit is left in the database - never cut off mid-document -
/// and the row comes back listed with it, its column empty and the other columns intact, so a screen can say "too large to display" and
/// why. A page of rows that are all oversized is a page of rows, not a page with nothing in it. Synthetic databases from the real DDL only.
/// </summary>
public sealed class OversizedPayloadTests : IDisposable
{
    private static readonly DateTimeOffset Base = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A limit small enough to cross with a few kilobytes.</summary>
    private const int SmallLimit = 1024;

    private readonly TestAuditDatabase _database = new();
    private int _minute;

    public void Dispose() => _database.Dispose();

    private AuditReader Reader(long limit = SmallLimit) => new(_database.Path, payloadLimitBytes: limit);

    private void Add(string id, string? details = "small", string? structured = "{\"k\":1}", string? runId = null, string? target = "")
    {
        using var connection = _database.OpenWritable();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO audit_events (id, timestamp, action, target, actor, details, severity, structured_json, bucket, connector, event_name, run_id)
            VALUES ($id, $timestamp, 'hook_decision', $target, 'audit_logger', $details, 'HIGH', $structured, 'guardrail.evaluation', 'claudecode', 'evt', $run)
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$timestamp", TestAuditDatabase.FormatTimestamp(Base.AddMinutes(++_minute)));
        command.Parameters.AddWithValue("$target", (object?)target ?? DBNull.Value);
        command.Parameters.AddWithValue("$details", (object?)details ?? DBNull.Value);
        command.Parameters.AddWithValue("$structured", (object?)structured ?? DBNull.Value);
        command.Parameters.AddWithValue("$run", (object?)runId ?? DBNull.Value);
        _ = command.ExecuteNonQuery();
    }

    private static string Json(int bytes)
    {
        // {"pad":"xxx..."} with exactly this many bytes in all.
        var overhead = "{\"pad\":\"\"}".Length;
        return "{\"pad\":\"" + new string('x', bytes - overhead) + "\"}";
    }

    // ------------------------------------------------------------------ the Audit reader

    [Fact]
    public async Task A_value_over_the_limit_is_left_in_the_database_and_listed_with_its_size()
    {
        Add("big", details: "kept", structured: Json(5000));

        var evt = Assert.Single((await Reader().QueryAsync(new AuditQuery())).Events);

        Assert.Null(evt.StructuredJsonRaw);
        var oversized = Assert.Single(evt.Oversized);
        Assert.Equal("structured_json", oversized.Column);
        Assert.Equal(5000, oversized.Bytes);
        Assert.Equal(SmallLimit, oversized.LimitBytes);
        Assert.Equal("structured_json is 5,000 bytes, over the 1 KB limit", oversized.Reason);
        Assert.True(evt.IsOversized);

        // The rest of the row is intact.
        Assert.Equal("big", evt.Id);
        Assert.Equal("kept", evt.Details);
        Assert.Equal("hook_decision", evt.Action);
        Assert.Equal("HIGH", evt.Severity);
        Assert.Equal("claudecode", evt.Connector);
        Assert.Equal("guardrail.evaluation", evt.Bucket);
    }

    [Fact]
    public async Task The_limit_itself_is_loaded_and_one_byte_more_is_not()
    {
        Add("exact", structured: Json(SmallLimit));
        Add("over", structured: Json(SmallLimit + 1));

        var events = (await Reader().QueryAsync(new AuditQuery())).Events;

        var over = events.Single(e => e.Id == "over");
        var exact = events.Single(e => e.Id == "exact");
        Assert.False(exact.IsOversized);
        Assert.Equal(SmallLimit, exact.StructuredJsonRaw!.Length);
        Assert.True(over.IsOversized);
        Assert.Null(over.StructuredJsonRaw);
    }

    [Fact]
    public async Task The_limit_counts_bytes_not_characters()
    {
        // 600 two-byte characters are 1,200 bytes: over a 1,024-byte limit though only 600 characters long. 500 are 1,000 bytes: within it.
        Add("wide", details: new string('é', 600));
        Add("narrow", details: new string('é', 500));

        var events = (await Reader().QueryAsync(new AuditQuery())).Events;

        var wide = events.Single(e => e.Id == "wide");
        Assert.Null(wide.Details);
        Assert.Equal(1200, Assert.Single(wide.Oversized).Bytes);
        Assert.Equal(500, events.Single(e => e.Id == "narrow").Details!.Length);
    }

    [Fact]
    public async Task Details_and_structured_json_are_limited_separately_and_both_can_be_over()
    {
        Add("details-only", details: new string('d', 3000));
        Add("both", details: new string('d', 3000), structured: Json(4000));

        var events = (await Reader().QueryAsync(new AuditQuery())).Events;

        var detailsOnly = events.Single(e => e.Id == "details-only");
        Assert.Equal(new[] { "details" }, detailsOnly.Oversized.Select(o => o.Column));
        Assert.Equal("{\"k\":1}", detailsOnly.StructuredJsonRaw);

        var both = events.Single(e => e.Id == "both");
        Assert.Equal(new[] { "details", "structured_json" }, both.Oversized.Select(o => o.Column));
        Assert.Equal(new long[] { 3000, 4000 }, both.Oversized.Select(o => o.Bytes));
    }

    [Fact]
    public async Task A_page_whose_rows_are_all_oversized_is_still_a_page_of_rows()
    {
        // The Mac's lastQuerySucceeded = !rows.isEmpty: an oversized first row is unavailable data, never a successful empty history.
        Add("a", structured: Json(5000));
        Add("b", details: new string('d', 5000));
        Add("c", structured: Json(9000));

        var page = await Reader().QueryAsync(new AuditQuery());

        Assert.Equal(3, page.Events.Count);
        Assert.Equal(3, page.OversizedCount);
        Assert.True(page.AllOversized);
        Assert.Equal(new[] { "c", "b", "a" }, page.Events.Select(e => e.Id));
        Assert.Equal(3, await Reader().CountAsync(new AuditQuery()));
    }

    [Fact]
    public async Task A_mixed_page_counts_only_the_oversized_rows()
    {
        Add("fine-1");
        Add("big", structured: Json(5000));
        Add("fine-2");

        var page = await Reader().QueryAsync(new AuditQuery());

        Assert.Equal(1, page.OversizedCount);
        Assert.False(page.AllOversized);
        Assert.Equal(3, page.Events.Count);
    }

    [Fact]
    public async Task An_empty_page_is_not_all_oversized()
    {
        var page = await Reader().QueryAsync(new AuditQuery());

        Assert.Empty(page.Events);
        Assert.False(page.AllOversized);
        Assert.Equal(0, page.OversizedCount);
    }

    [Fact]
    public async Task The_default_limit_is_256_KiB()
    {
        Assert.Equal(256 * 1024, AuditReader.DefaultPayloadLimitBytes);
        Add("exact", structured: Json(256 * 1024));
        Add("over", structured: Json(256 * 1024 + 1));

        var reader = new AuditReader(_database.Path);
        var events = (await reader.QueryAsync(new AuditQuery())).Events;

        Assert.Equal(AuditReader.DefaultPayloadLimitBytes, reader.PayloadLimitBytes);
        Assert.False(events.Single(e => e.Id == "exact").IsOversized);
        var over = events.Single(e => e.Id == "over");
        Assert.Equal("structured_json is 262,145 bytes, over the 256 KB limit", Assert.Single(over.Oversized).Reason);
    }

    [Fact]
    public async Task A_query_can_lift_the_limit_for_an_export_that_must_not_drop_text()
    {
        Add("big", details: new string('d', 5000), structured: Json(6000));

        var whole = Assert.Single((await Reader().QueryAsync(new AuditQuery { PayloadLimitBytes = AuditQuery.NoPayloadLimit })).Events);
        var limited = Assert.Single((await Reader().QueryAsync(new AuditQuery())).Events);

        Assert.Empty(whole.Oversized);
        Assert.Equal(5000, whole.Details!.Length);
        Assert.Equal(6000, whole.StructuredJsonRaw!.Length);
        Assert.Equal(2, limited.Oversized.Count);
    }

    [Fact]
    public async Task A_row_read_by_id_obeys_the_limit_too()
    {
        Add("one", structured: Json(5000));
        Add("two", details: new string('d', 5000));
        Add("three");
        var reader = Reader();

        var single = await reader.GetByIdAsync("one");
        var several = await reader.GetByIdsAsync(new[] { "one", "two", "three" });

        Assert.Equal("structured_json", Assert.Single(single!.Oversized).Column);
        Assert.Equal(3, several.Count);
        Assert.Equal(2, several.Count(e => e.IsOversized));
        Assert.Null(several.Single(e => e.Id == "two").Details);
    }

    [Fact]
    public async Task The_related_events_of_a_run_obey_the_default_limit_too()
    {
        Add("anchor", runId: "run-1");
        Add("huge", runId: "run-1", structured: Json((int)AuditReader.DefaultPayloadLimitBytes + 10));

        var related = await new AuditCorrelationReader(_database.Path).RelatedAsync("anchor", "run-1", null, 0);

        var huge = Assert.Single(related.Events);
        Assert.Equal("huge", huge.Id);
        Assert.True(huge.IsOversized);
        Assert.Null(huge.StructuredJsonRaw);
    }

    [Fact]
    public async Task A_NULL_value_is_not_oversized_just_absent()
    {
        Add("bare", details: null, structured: null);

        var evt = Assert.Single((await Reader().QueryAsync(new AuditQuery())).Events);

        Assert.Null(evt.Details);
        Assert.Null(evt.StructuredJsonRaw);
        Assert.Empty(evt.Oversized);
    }

    // ------------------------------------------------------------------ the mutation history

    private void Activity(string id, int minute, string? reason = null, string? before = null, string? after = null, string? diff = null)
    {
        using var connection = _database.OpenWritable();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO activity_events (id, timestamp, actor, action, target_type, target_id, reason, before_json, after_json, diff_json, version_from, version_to)
            VALUES ($id, $timestamp, 'admin', 'policy.update', 'policy', 'default', $reason, $before, $after, $diff, '3', '4')
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$timestamp", TestAuditDatabase.FormatTimestamp(Base.AddMinutes(minute)));
        command.Parameters.AddWithValue("$reason", (object?)reason ?? DBNull.Value);
        command.Parameters.AddWithValue("$before", (object?)before ?? DBNull.Value);
        command.Parameters.AddWithValue("$after", (object?)after ?? DBNull.Value);
        command.Parameters.AddWithValue("$diff", (object?)diff ?? DBNull.Value);
        _ = command.ExecuteNonQuery();
    }

    [Fact]
    public async Task A_before_image_over_the_limit_is_unavailable_and_the_change_is_still_listed()
    {
        Activity("big", 1, reason: "tighten", before: Json(MutationReader.PayloadLimit + 500), after: "{\"mode\":\"action\"}", diff: "[]");

        var result = await new MutationReader(_database.Path).ReadAsync();

        var item = Assert.Single(result.Items);
        Assert.Equal(MutationStatus.Ok, result.Status);
        Assert.Equal(string.Empty, item.BeforeJson);
        var oversized = Assert.Single(item.Oversized);
        Assert.Equal("before_json", oversized.Column);
        Assert.Equal(MutationReader.PayloadLimit + 500, oversized.Bytes);
        Assert.Equal(MutationReader.PayloadLimit, oversized.LimitBytes);
        Assert.True(item.IsOversized);

        // Everything else about the change is intact.
        Assert.Equal("tighten", item.Reason);
        Assert.Equal("{\"mode\":\"action\"}", item.AfterJson);
        Assert.Equal("admin", item.Actor);
        Assert.Equal("3", item.VersionFrom);
    }

    [Fact]
    public async Task A_change_exactly_at_the_limit_is_loaded_whole()
    {
        Activity("edge", 1, after: Json(MutationReader.PayloadLimit));

        var item = Assert.Single((await new MutationReader(_database.Path).ReadAsync()).Items);

        Assert.Empty(item.Oversized);
        Assert.Equal(MutationReader.PayloadLimit, item.AfterJson.Length);
    }

    [Fact]
    public async Task A_canonical_change_row_reports_an_oversized_details_or_structured_json()
    {
        _database.InsertEvent(
            "audit-change",
            Base.AddMinutes(2),
            "config.change.applied",
            "INFO",
            "compliance.activity",
            null,
            details: new string('d', MutationReader.PayloadLimit + 1),
            structuredJson: Json(MutationReader.PayloadLimit + 1),
            actor: "gateway_api");

        var item = Assert.Single((await new MutationReader(_database.Path).ReadAsync()).Items);

        Assert.Equal(MutationSource.Audit, item.Source);
        Assert.Equal(new[] { "details", "structured_json" }, item.Oversized.Select(o => o.Column));
        Assert.Equal(string.Empty, item.Reason);
        Assert.Equal(string.Empty, item.StructuredJson);
        Assert.Equal("gateway_api", item.Actor);
    }

    [Fact]
    public async Task A_history_whose_newest_changes_are_all_oversized_is_not_an_empty_history()
    {
        Activity("a", 1, before: Json(MutationReader.PayloadLimit + 1));
        Activity("b", 2, after: Json(MutationReader.PayloadLimit + 1));

        var result = await new MutationReader(_database.Path).ReadAsync();

        Assert.Equal(2, result.Items.Count);
        Assert.All(result.Items, item => Assert.True(item.IsOversized));
    }

    // ------------------------------------------------------------------ the Logs streams

    private void Stream(string id, string payload)
    {
        using var connection = _database.OpenWritable();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO audit_events (id, timestamp, action, target, actor, details, severity, bucket, event_name, connector, source, signal, payload_json)
            VALUES ($id, $timestamp, 'act', '', 'gateway', 'details', 'INFO', 'asset.scan', 'scan.completed', 'claudecode', 'sidecar', 'logs', $payload)
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$timestamp", TestAuditDatabase.FormatTimestamp(Base.AddMinutes(++_minute)));
        command.Parameters.AddWithValue("$payload", payload);
        _ = command.ExecuteNonQuery();
    }

    [Fact]
    public async Task A_stream_payload_over_64_KiB_names_its_size_and_the_limit_and_the_row_is_still_listed()
    {
        Stream("big", Json(EventStreamReader.PayloadByteLimit + 1000));
        Stream("small", "{\"k\":1}");

        var rows = (await new EventStreamReader(_database.Path).ReadAsync(EventStreamKind.Verdicts)).Rows;

        var big = rows.Single(r => r.Id == "big");
        Assert.True(big.PayloadOmitted);
        var oversized = Assert.Single(big.Oversized);
        Assert.Equal("payload_json", oversized.Column);
        Assert.Equal(EventStreamReader.PayloadByteLimit + 1000, oversized.Bytes);
        Assert.Equal(EventStreamReader.PayloadByteLimit, oversized.LimitBytes);
        Assert.Contains("\"payload_unavailable\": \"payload_json is 66,536 bytes, over the 64 KB limit\"", big.RawJson, StringComparison.Ordinal);
        Assert.Empty(rows.Single(r => r.Id == "small").Oversized);
    }

    [Fact]
    public async Task A_stream_of_rows_that_all_have_an_oversized_payload_is_not_an_empty_stream()
    {
        Stream("a", Json(EventStreamReader.PayloadByteLimit + 1));
        Stream("b", Json(EventStreamReader.PayloadByteLimit + 1));

        var result = await new EventStreamReader(_database.Path).ReadAsync(EventStreamKind.Verdicts);

        Assert.Equal(EventStreamStatus.Ok, result.Status);
        Assert.Equal(2, result.Rows.Count);
        Assert.All(result.Rows, row => Assert.True(row.PayloadOmitted));
    }

    // ------------------------------------------------------------------ the egress feed

    private void Egress(string id, string? structured, string? payload)
    {
        using var connection = _database.OpenWritable();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO audit_events (id, timestamp, action, target, actor, severity, structured_json, payload_json, bucket, connector, event_name, source)
            VALUES ($id, $timestamp, 'network-egress', '', 'gateway', 'INFO', $structured, $payload, 'network.egress', 'claudecode', 'network.egress', 'gateway')
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$timestamp", TestAuditDatabase.FormatTimestamp(Base.AddMinutes(++_minute)));
        command.Parameters.AddWithValue("$structured", (object?)structured ?? DBNull.Value);
        command.Parameters.AddWithValue("$payload", (object?)payload ?? DBNull.Value);
        _ = command.ExecuteNonQuery();
    }

    [Fact]
    public async Task An_egress_document_over_the_limit_is_listed_not_decoded_from_a_cut_copy()
    {
        var decision = "{\"defenseclaw.network.decision\":\"allow\",\"defenseclaw.network.target_ref\":\"api.example.test\"}";
        Egress("ok", decision, null);
        Egress("big", Json(NetworkEgressReader.PayloadLimit + 100), decision);

        var events = await new NetworkEgressReader(_database.Path).ReadRecentAsync();

        var ok = events.Single(e => e.Id == "audit:ok");
        Assert.Empty(ok.Oversized);
        Assert.Equal("allow", ok.Decision);

        var big = events.Single(e => e.Id == "audit:big");
        var oversized = Assert.Single(big.Oversized);
        Assert.Equal("structured_json", oversized.Column);
        Assert.Equal(NetworkEgressReader.PayloadLimit + 100, oversized.Bytes);

        // The decision lives in the document that fits (the payload), so it is still read; the one that does not fit is not parsed.
        Assert.Equal("allow", big.Decision);
        Assert.Equal("api.example.test", big.Target);
    }

    // ------------------------------------------------------------------ the words

    [Theory]
    [InlineData(0L, "0 bytes")]
    [InlineData(812L, "812 bytes")]
    [InlineData(1024L, "1 KB")]
    [InlineData(65_536L, "64 KB")]
    [InlineData(262_144L, "256 KB")]
    [InlineData(262_145L, "262,145 bytes")]
    [InlineData(1_048_575L, "1,048,575 bytes")]
    [InlineData(1_048_576L, "1 MB")]
    [InlineData(3_355_443L, "3.2 MB")]
    [InlineData(5_368_709_120L, "5 GB")]
    public void A_size_reads_in_words(long bytes, string expected) =>
        Assert.Equal(expected, OversizedValue.FormatSize(bytes));

    [Fact]
    public void A_reason_names_the_column_the_size_and_the_limit_and_several_join_into_one_line()
    {
        var one = new OversizedValue("structured_json", 3_355_443, 262_144);
        var two = new OversizedValue("details", 300_000, 262_144);

        Assert.Equal("structured_json is 3.2 MB, over the 256 KB limit", one.Reason);
        Assert.Equal("structured_json is 3.2 MB, over the 256 KB limit; details is 300,000 bytes, over the 256 KB limit", OversizedValue.Describe(new[] { one, two }));
        Assert.Equal(string.Empty, OversizedValue.Describe(Array.Empty<OversizedValue>()));
    }

    // ------------------------------------------------------------------ the placeholder in a code box

    private static readonly OversizedValue Before = new("before_json", 270_010, 262_144);

    [Theory]
    [InlineData(18, "(not shown:|before_json is|270,010 bytes,|over the 256 KB|limit)")]
    [InlineData(40, "(not shown: before_json is|270,010 bytes, over the 256 KB limit)")]
    [InlineData(int.MaxValue, "(not shown: before_json is 270,010 bytes, over the 256 KB limit)")]
    public void A_placeholder_is_broken_at_spaces_into_lines_that_fit_a_box_that_scrolls_sideways(int width, string expected)
    {
        Assert.Equal(expected.Split('|'), Before.PlaceholderLines(width));
        Assert.Equal(expected.Replace('|', '\n'), Before.Placeholder(width));
    }

    [Fact]
    public void A_placeholder_never_splits_a_size_from_its_unit_and_never_loses_a_word()
    {
        foreach (var value in new[] { Before, new OversizedValue("structured_json", 3_355_443, 262_144), new OversizedValue("details", 300_000, 262_144), new OversizedValue("payload_json", 700, 512) })
        {
            var oneLine = $"(not shown: {value.Reason})";

            for (var width = 1; width <= 80; width++)
            {
                var lines = value.PlaceholderLines(width);

                // Joined again, nothing is added or lost.
                Assert.Equal(oneLine, string.Join(' ', lines));

                // "270,010 bytes" and "3.2 MB" stay on one line, so a line never opens with a unit.
                Assert.DoesNotContain(lines, line => line.StartsWith("bytes", StringComparison.Ordinal) || line.StartsWith("KB", StringComparison.Ordinal)
                    || line.StartsWith("MB", StringComparison.Ordinal) || line.StartsWith("GB", StringComparison.Ordinal));

                // A line is within the width unless it is one word (or one size) that cannot be broken.
                Assert.All(lines, line => Assert.True(line.Length <= width || !line.Contains(' ') || line.Count(c => c == ' ') == 1 && char.IsAsciiDigit(line[line.IndexOf(' ') - 1]),
                    $"width {width}: '{line}'"));
            }
        }
    }

    [Fact]
    public void A_placeholder_wide_enough_for_it_is_one_line_and_a_width_below_one_is_refused()
    {
        Assert.Single(Before.PlaceholderLines(200));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => Before.PlaceholderLines(0));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => Before.Placeholder(-5));
    }
}
