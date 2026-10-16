using System.Globalization;
using System.Text;
using System.Text.Json;
using DefenseClaw.Core.Correlation;
using DefenseClaw.Core.Gateway;

namespace DefenseClaw.Tests.Correlation;

/// <summary>
/// The correlation and directory readers (CUST-355): the shapes of the 1.0.x handlers, read leniently and with a limit on everything. The fixtures
/// are synthetic and written from the Go structs; nothing here talks to a gateway.
/// </summary>
public sealed class CorrelationReaderTests
{
    private static CorrelationGraphPage Graph(string fixture)
    {
        var read = CorrelationReader.ParseGraphText(CorrelationFixtures.Read(fixture));
        Assert.True(read.IsOk, read.Message);
        return read.Value!;
    }

    // ------------------------------------------------------------------ the graph

    [Fact]
    public void The_first_page_is_read_whole_with_its_cursor_kept_to_the_nanosecond()
    {
        var page = Graph("correlation-graph.root.page1.synthetic.json");

        Assert.Equal(8, page.Events.Count);
        Assert.Equal(9, page.Observations.Count);
        Assert.Equal(6, page.Relationships.Count);
        Assert.Equal(2, page.Evidence.Count);
        Assert.True(page.Truncated);
        Assert.True(page.HasNext);
        Assert.Equal("2026-10-01T10:00:07.000000000Z", page.NextAfterTimeText);
        Assert.Equal("01950000-0000-7000-8000-0000000000e8", page.NextAfterId);
        Assert.Equal(0, page.Skipped);
    }

    [Fact]
    public void An_unknown_field_is_ignored_and_a_missing_optional_one_is_empty()
    {
        var page = Graph("correlation-graph.root.page1.synthetic.json");

        // e2 carries a member this app has never heard of and no digest or request id.
        var e2 = page.Events.Single(e => e.EventName == "turn_start");
        Assert.Equal("hook", e2.Rail);
        Assert.Equal("complete", e2.Completeness);

        // e3 has no source_time, logical group ids it does have; the record e1 has no turn.
        var record = page.Observations.Single(o => o.RecordId == "rec-e1");
        Assert.Equal(string.Empty, record.TurnId);
        Assert.Equal("session-0001", record.SessionId);
    }

    [Fact]
    public void Gos_zero_time_is_no_time_and_a_nil_slice_is_an_empty_list()
    {
        var first = Graph("correlation-graph.root.page1.synthetic.json");
        Assert.Null(first.Events.Single(e => e.EventName == "turn_start").SourceTime);

        var last = Graph("correlation-graph.root.page2.synthetic.json");
        Assert.Empty(last.Relationships);
        Assert.Empty(last.Evidence);
        Assert.False(last.HasNext);
        Assert.Null(last.NextAfterTimeText);
        Assert.Null(last.NextAfterId);

        var empty = Graph("correlation-graph.empty.synthetic.json");
        Assert.Empty(empty.Events);
        Assert.Empty(empty.Observations);
        Assert.Null(empty.IngestWatermark);
        Assert.NotNull(empty.AsOf);
    }

    [Fact]
    public void A_cursor_with_only_one_half_is_not_a_next_page()
    {
        var read = CorrelationReader.ParseGraphText("""{"events":[],"truncated":true,"next_after_time":"2026-10-01T10:00:07Z","next_after_id":""}""");

        Assert.True(read.IsOk);
        Assert.True(read.Value!.Truncated);
        Assert.False(read.Value.HasNext);
    }

    [Fact]
    public void Methods_and_statuses_map_and_a_word_this_app_does_not_know_is_kept_as_the_gateways()
    {
        var page = Graph("correlation-graph.root.page1.synthetic.json");
        var byId = page.Relationships.ToDictionary(r => r.RelationshipId);

        Assert.Equal(RelationshipMethod.Reported, byId["rel-root-c1"].Method);
        Assert.Equal(RelationshipStatus.Active, byId["rel-root-c1"].Status);
        Assert.Equal(RelationshipMethod.Inferred, byId["rel-root-c2"].Method);
        Assert.Equal(RelationshipStatus.Candidate, byId["rel-root-c2"].Status);
        Assert.Equal(RelationshipMethod.Derived, byId["rel-root-c3"].Method);
        Assert.Equal(RelationshipStatus.Conflicted, byId["rel-root-c3"].Status);
        Assert.Equal(3, byId["rel-root-c3"].EvidenceCount);

        var weird = byId["rel-weird"];
        Assert.Equal(RelationshipMethod.Unknown, weird.Method);
        Assert.Equal("telepathy", weird.MethodText);
        Assert.Equal(RelationshipStatus.Unknown, weird.Status);
        Assert.Equal("newstatus", weird.StatusText);
        Assert.Equal(0, weird.EvidenceCount);
    }

    [Fact]
    public void Evidence_that_failed_its_integrity_check_is_kept()
    {
        var page = Graph("correlation-graph.root.page1.synthetic.json");

        var failed = Assert.Single(page.Evidence, e => e.Integrity == "failed");
        Assert.Equal("conflicting", failed.Role);
        Assert.Equal("rel-root-c3", failed.RelationshipId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("<html>502</html>")]
    [InlineData("[]")]
    [InlineData("\"events\"")]
    [InlineData("42")]
    [InlineData("null")]
    public void Garbage_is_malformed_never_an_empty_page(string body)
    {
        var read = CorrelationReader.ParseGraphText(body);

        Assert.False(read.IsOk);
        Assert.Equal(CorrelationReadStatus.Malformed, read.Status);
        Assert.Null(read.Value);
        Assert.NotEmpty(read.Message);
    }

    [Fact]
    public void Members_of_the_wrong_kind_and_entries_that_are_not_objects_are_dropped_and_counted()
    {
        var json = """
            {
              "events": "oops",
              "observations": [1, "x", null, {"record_id": 7}, {"record_id": "r1", "semantic_event_id": 5, "observed_at": 12}],
              "relationships": {"not": "a list"},
              "evidence": [[]],
              "truncated": "yes"
            }
            """;

        var read = CorrelationReader.ParseGraphText(json);

        Assert.True(read.IsOk);
        var page = read.Value!;
        Assert.Empty(page.Events);
        var only = Assert.Single(page.Observations);
        Assert.Equal("r1", only.RecordId);
        Assert.Equal(string.Empty, only.SemanticEventId);
        Assert.Null(only.ObservedAt);
        Assert.Empty(page.Relationships);
        Assert.False(page.Truncated);

        // 1, "x", null, {record_id: 7} (no usable id), and the evidence's inner array.
        Assert.Equal(5, page.Skipped);
    }

    [Fact]
    public void A_page_keeps_at_most_the_per_page_caps_and_counts_the_rest()
    {
        var events = new StringBuilder("{\"events\":[");
        for (var i = 0; i < CorrelationReader.MaxEvents + 25; i++)
        {
            _ = events.Append(i > 0 ? "," : string.Empty)
                .Append(CultureInfo.InvariantCulture, $"{{\"semantic_event_id\":\"e{i}\",\"event_name\":\"tool_start\"}}");
        }

        _ = events.Append("]}");

        var read = CorrelationReader.ParseGraphText(events.ToString());

        Assert.True(read.IsOk);
        Assert.Equal(CorrelationReader.MaxEvents, read.Value!.Events.Count);
        Assert.Equal(25, read.Value.Skipped);
    }

    [Fact]
    public void A_very_long_identifier_is_cut_and_a_control_character_in_one_is_spelled_out()
    {
        var longId = new string('x', 5000);
        var json = "{\"events\":[{\"semantic_event_id\":\"" + longId + "\"},{\"semantic_event_id\":\"a\\u202eb\\nc\"}]}";

        var events = CorrelationReader.ParseGraphText(json).Value!.Events;

        Assert.True(events[0].SemanticEventId.Length <= 256);
        Assert.DoesNotContain('‮', events[1].SemanticEventId);
        Assert.DoesNotContain('\n', events[1].SemanticEventId);
    }

    // ------------------------------------------------------------------ conflicts and the directory

    [Fact]
    public void The_conflicts_answer_gives_the_conflicted_edges_and_the_contradicting_deliveries()
    {
        var read = CorrelationReader.ParseConflictsText(CorrelationFixtures.Read("correlation-conflicts.synthetic.json"));

        Assert.True(read.IsOk);
        var edge = Assert.Single(read.Value!.Relationships);
        Assert.Equal(RelationshipStatus.Conflicted, edge.Status);
        Assert.Equal(2, read.Value.Receipts.Count);
        Assert.Equal(2, read.Value.Receipts[0].DeliveryCount);
        Assert.NotNull(read.Value.Receipts[0].FirstReceivedAt);
        Assert.Null(read.Value.Receipts[1].FirstReceivedAt);
        Assert.False(read.Value.Truncated);
        Assert.Equal(CorrelationReadStatus.Malformed, CorrelationReader.ParseConflictsText("[]").Status);
    }

    [Fact]
    public void The_directory_lists_identities_with_their_last_session_and_marks_a_retired_account()
    {
        var read = CorrelationReader.ParseDirectoryText(CorrelationFixtures.Read("agents-identities.synthetic.json"));

        Assert.True(read.IsOk);
        var page = read.Value!;
        Assert.True(page.Enabled);
        Assert.True(page.Persisted);
        Assert.Equal(3, page.Total);
        Assert.Equal(string.Empty, page.NextCursor);
        Assert.Equal("session-0001", page.Rows[0].LastSessionId);
        Assert.Equal(14, page.Rows[0].SessionsSeen);
        Assert.Equal("claudecode", page.Rows[1].Connector);
        Assert.True(page.Rows[2].Retired);
        Assert.Equal(string.Empty, page.Rows[2].LastSessionId);
        Assert.NotNull(page.Rows[0].LastSeen);
    }

    [Fact]
    public void A_gateway_without_a_directory_says_so_and_a_reply_without_identities_is_not_an_empty_directory()
    {
        var disabled = CorrelationReader.ParseDirectoryText(CorrelationFixtures.Read("agents-identities.disabled.synthetic.json"));
        Assert.True(disabled.IsOk);
        Assert.False(disabled.Value!.Enabled);
        Assert.Empty(disabled.Value.Rows);

        var nil = CorrelationReader.ParseDirectoryText("""{"enabled": true, "identities": null, "total": 0}""");
        Assert.True(nil.IsOk);
        Assert.Empty(nil.Value!.Rows);

        var missing = CorrelationReader.ParseDirectoryText("""{"enabled": true, "total": 0}""");
        Assert.Equal(CorrelationReadStatus.Malformed, missing.Status);
        Assert.Equal(CorrelationReadStatus.Malformed, CorrelationReader.ParseDirectoryText("nope").Status);
    }

    [Fact]
    public void The_total_never_reads_lower_than_the_rows_and_rows_without_an_id_are_skipped()
    {
        var read = CorrelationReader.ParseDirectoryText("""{"identities":[{"agent_id":"a"},{"connector":"codex"},{"agent_id":"b","sessions_seen":-4}],"total":1}""");

        var page = read.Value!;
        Assert.Equal(2, page.Rows.Count);
        Assert.Equal(2, page.Total);
        Assert.Equal(1, page.Skipped);
        Assert.Equal(0, page.Rows[1].SessionsSeen);
    }

    // ------------------------------------------------------------------ the gateway's answer

    private static CorrelationRead<CorrelationGraphPage> Translate(GatewayResult<JsonDocument> result) =>
        CorrelationReader.FromGateway(result, CorrelationReader.ParseGraph);

    [Fact]
    public void Every_way_the_gateway_can_answer_has_its_own_status_and_a_sentence()
    {
        Assert.Equal(CorrelationReadStatus.Unreachable, Translate(GatewayResult<JsonDocument>.Unreachable()).Status);
        Assert.Equal(CorrelationReadStatus.Unauthorized, Translate(GatewayResult<JsonDocument>.Unauthorized()).Status);
        Assert.Equal(CorrelationReadStatus.NotReady, Translate(GatewayResult<JsonDocument>.NotConnected()).Status);
        Assert.Equal(CorrelationReadStatus.Unsupported, Translate(GatewayResult<JsonDocument>.Error("gateway returned HTTP 404", 404)).Status);
        Assert.Equal(CorrelationReadStatus.Unsupported, Translate(GatewayResult<JsonDocument>.Error("method not allowed", 405)).Status);
        Assert.Equal(CorrelationReadStatus.NotReady, Translate(GatewayResult<JsonDocument>.Error("correlation ledger is not available", 503)).Status);
        Assert.Equal(CorrelationReadStatus.Failed, Translate(GatewayResult<JsonDocument>.Error("correlation query failed", 500)).Status);
        Assert.Equal(CorrelationReadStatus.Failed, Translate(GatewayResult<JsonDocument>.Error("the peer is not the gateway")).Status);
        Assert.Equal(CorrelationReadStatus.TooLarge, Translate(GatewayResult<JsonDocument>.Error(GatewayClient.TooLargeMessage(1024), 200)).Status);

        var rejected = Translate(GatewayResult<JsonDocument>.Error("exactly one correlation anchor is required", 400));
        Assert.Equal(CorrelationReadStatus.Rejected, rejected.Status);
        Assert.Contains("exactly one correlation anchor", rejected.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_failure_that_says_nothing_about_the_version_keeps_what_was_on_screen()
    {
        Assert.True(Translate(GatewayResult<JsonDocument>.Unreachable()).KeepsLastGood);
        Assert.True(Translate(GatewayResult<JsonDocument>.Error("x", 500)).KeepsLastGood);
        Assert.True(Translate(GatewayResult<JsonDocument>.Error(GatewayClient.TooLargeMessage(5), 200)).KeepsLastGood);
        Assert.False(Translate(GatewayResult<JsonDocument>.Error("x", 404)).KeepsLastGood);
        Assert.False(Translate(GatewayResult<JsonDocument>.Unauthorized()).KeepsLastGood);
    }

    [Fact]
    public void An_ok_answer_is_parsed_and_one_that_is_not_json_text_is_malformed()
    {
        using var document = JsonDocument.Parse(CorrelationFixtures.Read("correlation-graph.root.page2.synthetic.json"));

        var ok = Translate(GatewayResult<JsonDocument>.Ok(document));

        Assert.True(ok.IsOk);
        Assert.Equal(3, ok.Value!.Events.Count);

        using var scalar = JsonDocument.Parse("42");
        Assert.Equal(CorrelationReadStatus.Malformed, Translate(GatewayResult<JsonDocument>.Ok(scalar)).Status);
    }
}
