using DefenseClaw.Core.Audit;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// Egress decisions from <c>audit.db</c>: the <c>network.egress</c> bucket decoded tolerantly (the attribute spelling has moved),
/// the older <c>network_egress_events</c> table, and the Mac's "silent bypass" count (allowed + LLM-shaped, last 300 s). Synthetic rows
/// from the real DDL.
/// </summary>
public sealed class NetworkEgressReaderTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    private readonly TestAuditDatabase _database = new();

    public void Dispose() => _database.Dispose();

    private NetworkEgressReader Reader() => new(_database.Path);

    private void Egress(string id, DateTimeOffset at, string? structured, string? payload = null, string? action = "network-egress", string? bucket = "network.egress")
    {
        using var connection = _database.OpenWritable();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO audit_events (id, timestamp, action, target, actor, severity, structured_json, payload_json, bucket, connector, event_name, source)
            VALUES ($id, $ts, $action, '', 'gateway', 'INFO', $structured, $payload, $bucket, 'claudecode', 'network.egress', 'gateway')
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$ts", TestAuditDatabase.FormatTimestamp(at));
        command.Parameters.AddWithValue("$action", (object?)action ?? DBNull.Value);
        command.Parameters.AddWithValue("$structured", (object?)structured ?? DBNull.Value);
        command.Parameters.AddWithValue("$payload", (object?)payload ?? DBNull.Value);
        command.Parameters.AddWithValue("$bucket", (object?)bucket ?? DBNull.Value);
        _ = command.ExecuteNonQuery();
    }

    private static string Attrs(string decision, string branch, bool llm, string target = "api.example.test") =>
        $$"""{"defenseclaw.network.decision":"{{decision}}","defenseclaw.network.branch":"{{branch}}","defenseclaw.network.looks_like_llm":{{(llm ? "true" : "false")}},"defenseclaw.network.target_ref":"{{target}}","defenseclaw.network.reason":"because","defenseclaw.network.target_path":"/v1/chat","defenseclaw.network.body_shape":"messages"}""";

    // ---- decoding ----

    [Fact]
    public async Task The_attributes_decode_into_the_event_and_a_block_or_an_llm_shape_raises_the_severity()
    {
        Egress("a", Now.AddSeconds(-30), Attrs("block", "passthrough", false));
        Egress("b", Now.AddSeconds(-20), Attrs("allow", "shape", true));
        Egress("c", Now.AddSeconds(-10), Attrs("allow", "passthrough", false));

        var rows = await Reader().ReadRecentAsync();

        Assert.Equal(new[] { "audit:c", "audit:b", "audit:a" }, rows.Select(r => r.Id).ToArray());
        var block = rows[2];
        Assert.Equal("block", block.Decision);
        Assert.Equal("api.example.test", block.Target);
        Assert.Equal("because", block.Reason);
        Assert.Equal("/v1/chat", block.TargetPath);
        Assert.Equal("messages", block.BodyShape);
        Assert.Equal("claudecode", block.Connector);
        Assert.Equal("MEDIUM", block.Severity);
        Assert.Equal("MEDIUM", rows[1].Severity);
        Assert.Equal("INFO", rows[0].Severity);
        Assert.True(rows[1].IsAlertWorthy);
        Assert.False(rows[0].IsAlertWorthy);
    }

    [Theory]
    [InlineData("""{"attributes":{"defenseclaw.network.decision":"Allowed ","defenseclaw.network.branch":"Passthrough","defenseclaw.network.looks_like_llm":"true"}}""")]
    [InlineData("""{"network.decision":"allow","network.branch":"passthrough","network.looks_like_llm":1}""")]
    [InlineData("""{"DefenseClaw.Network.Decision":"allow","DEFENSECLAW.NETWORK.BRANCH":"passthrough","defenseclaw.network.looks_like_llm":true}""")]
    public async Task The_attribute_spelling_is_tolerated_in_either_document(string document)
    {
        Egress("a", Now.AddSeconds(-5), structured: null, payload: document);
        Egress("b", Now.AddSeconds(-6), structured: document);

        var rows = await Reader().ReadRecentAsync();

        Assert.All(rows, r => Assert.True(r.IsSilentBypass, r.Id));
    }

    [Fact]
    public async Task A_row_that_does_not_decode_is_still_a_row_and_the_action_stands_in_for_the_decision()
    {
        Egress("junk", Now.AddSeconds(-5), structured: "{not json", action: "egress-block");
        Egress("none", Now.AddSeconds(-6), structured: null, action: "");

        var rows = await Reader().ReadRecentAsync();

        Assert.Equal("block", rows.Single(r => r.Id == "audit:junk").Decision);
        Assert.Equal(string.Empty, rows.Single(r => r.Id == "audit:none").Decision);
    }

    [Fact]
    public async Task The_older_table_is_read_too_and_a_blocked_flag_is_a_block()
    {
        using (var connection = _database.OpenWritable())
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO network_egress_events (id, timestamp, hostname, url, policy_outcome, blocked, severity, details, connector)
                VALUES ('n1', $ts, 'host.example.test', 'https://host.example.test/x', 'deny', 1, 'HIGH', 'denied by policy', 'codex'),
                       ('n2', $ts2, 'ok.example.test', NULL, 'allow', 0, 'INFO', NULL, NULL)
                """;
            command.Parameters.AddWithValue("$ts", TestAuditDatabase.FormatTimestamp(Now.AddSeconds(-10)));
            command.Parameters.AddWithValue("$ts2", TestAuditDatabase.FormatTimestamp(Now.AddSeconds(-20)));
            _ = command.ExecuteNonQuery();
        }

        var rows = await Reader().ReadRecentAsync();

        Assert.Equal("block", rows[0].Decision);
        Assert.Equal("host.example.test", rows[0].Target);
        Assert.Equal("denied by policy", rows[0].Reason);
        Assert.Equal("codex", rows[0].Connector);
        Assert.True(rows[1].IsAllowed);
    }

    // ---- the silent bypass count ----

    [Fact]
    public async Task Silent_bypass_counts_allowed_llm_shaped_events_of_the_last_five_minutes_only()
    {
        Egress("passthrough-llm", Now.AddSeconds(-60), Attrs("allow", "passthrough", true));      // counts
        Egress("shape", Now.AddSeconds(-120), Attrs("allowed", "shape", false));                  // counts
        Egress("passthrough-plain", Now.AddSeconds(-30), Attrs("allow", "passthrough", false));   // no
        Egress("blocked", Now.AddSeconds(-30), Attrs("block", "shape", true));                    // no: not allowed
        Egress("too-old", Now.AddSeconds(-301), Attrs("allow", "passthrough", true));             // no
        Egress("future", Now.AddMinutes(10), Attrs("allow", "passthrough", true));                // no: not yet
        Egress("other-bucket", Now.AddSeconds(-10), Attrs("allow", "passthrough", true), bucket: "platform.health");

        Assert.Equal(2, await Reader().CountSilentBypassAsync(Now));
    }

    [Fact]
    public async Task Nothing_recorded_or_no_database_is_zero_not_an_error()
    {
        Assert.Equal(0, await Reader().CountSilentBypassAsync(Now));
        Assert.Empty(await Reader().ReadRecentAsync());
        Assert.Equal(0, await new NetworkEgressReader(Path.Combine(Path.GetTempPath(), "dcw-missing-" + Guid.NewGuid().ToString("n"), "audit.db")).CountSilentBypassAsync(Now));
    }

    [Fact]
    public async Task The_bypass_window_is_a_bucket_timestamp_range_search()
    {
        // The statement is the one in ReadAuditAsync; this pins that the bucket index serves a range on the timestamp (the live table
        // has 577 k rows, of which the bucket holds a few).
        using var connection = _database.OpenWritable();
        using var command = connection.CreateCommand();
        command.CommandText =
            "EXPLAIN QUERY PLAN SELECT id FROM audit_events WHERE bucket = 'network.egress' AND timestamp >= '2026-09-30T11:55:00' ORDER BY timestamp DESC, rowid DESC LIMIT 5000";
        using var reader = await command.ExecuteReaderAsync();
        var plan = new List<string>();
        while (await reader.ReadAsync())
        {
            plan.Add(reader.GetString(3));
        }

        Assert.Contains(plan, l => l.Contains("idx_audit_bucket_timestamp", StringComparison.Ordinal));
        Assert.DoesNotContain(plan, l => l.Contains("TEMP B-TREE", StringComparison.Ordinal));
    }
}
