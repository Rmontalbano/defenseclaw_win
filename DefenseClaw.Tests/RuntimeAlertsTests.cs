using System.Text.Json;
using DefenseClaw.Core.Audit;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Runtime;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// The runtime's alert list (CUST-374): <c>alerts --json</c> parsed from fixtures built to the shape upstream's <c>_alerts_json</c> prints (indent 2, sorted
/// keys), the capability and read-only classification behind it, and the detection-only findings the database queue leaves out on such a runtime.
/// </summary>
public sealed class RuntimeAlertsTests : IDisposable
{
    private static readonly DateTimeOffset Base = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    private readonly TestAuditDatabase _database = new();

    public void Dispose() => _database.Dispose();

    // ---- The shape upstream prints ----

    /// <summary>A list as <c>json.dumps(rows, indent=2, sort_keys=True)</c> prints it.</summary>
    private static string Printed(params Dictionary<string, object>[] rows) =>
        JsonSerializer.Serialize(
            rows.Select(r => new SortedDictionary<string, object>(r, StringComparer.Ordinal)),
            new JsonSerializerOptions { WriteIndented = true });

    private static Dictionary<string, object> Row(string id, string severity = "HIGH", string action = "scan-finding", int minute = 0) => new()
    {
        ["id"] = id,
        ["timestamp"] = Base.AddMinutes(minute).ToString("o", System.Globalization.CultureInfo.InvariantCulture),
        ["severity"] = severity,
        ["action"] = action,
        ["target"] = "/synthetic/" + id,
        ["actor"] = "audit_logger",
        ["connector"] = "claudecode",
        ["details"] = "finding.observed",
    };

    private static string WithLineEndings(string text, bool crlf) => crlf ? text.ReplaceLineEndings("\r\n") : text.ReplaceLineEndings("\n");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_printed_list_reads_with_its_facts_in_either_line_ending(bool crlf)
    {
        var finding = Row("a1", "CRITICAL", minute: 3);
        finding["decision"] = "blocked";
        finding["rule"] = "CMD-ENV-DUMP: Environment dump";
        finding["scanner"] = "hook-rule";
        finding["user"] = "operator";
        finding["agent_depth"] = 1;
        finding["agent_instance_id"] = "inst-1";
        var enforcement = Row("b2", "HIGH", "quarantine", minute: 2);
        enforcement["moved_to"] = "/synthetic/quarantine/skill";

        var result = RuntimeAlertSnapshotParser.Parse(WithLineEndings(Printed(finding, enforcement), crlf));

        Assert.Equal(RuntimeAlertParseStatus.Ok, result.Status);
        Assert.Equal(["a1", "b2"], result.Rows.Select(r => r.Id));
        var first = result.Rows[0];
        Assert.Equal(AuditSeverity.Critical, first.Severity);
        Assert.Equal("claudecode", first.Connector);
        Assert.Equal(Base.AddMinutes(3), first.Timestamp);
        Assert.Equal(
            [("decision", "blocked"), ("rule", "CMD-ENV-DUMP: Environment dump"), ("scanner", "hook-rule"), ("user", "operator"), ("agent_instance_id", "inst-1"), ("agent_depth", "1")],
            first.Facts.Select(f => (f.Name, f.Value)));
        Assert.Equal("/synthetic/quarantine/skill", Assert.Single(result.Rows[1].Facts).Value);
        Assert.Equal(first.Id, first.ToQueueItem().Id);
        Assert.Equal(AuditSeverity.Critical, first.ToQueueItem().Severity);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Warning_lines_before_the_list_are_skipped(bool crlf)
    {
        var text = "[warn] config.yaml has an unknown key\nNote: audit store opened read-only\n" + Printed(Row("w1"));

        var result = RuntimeAlertSnapshotParser.Parse(WithLineEndings(text, crlf));

        Assert.Equal(RuntimeAlertParseStatus.Ok, result.Status);
        Assert.Equal("w1", Assert.Single(result.Rows).Id);
    }

    [Fact]
    public void An_empty_list_is_a_list()
    {
        var result = RuntimeAlertSnapshotParser.Parse("[]\n");

        Assert.Equal(RuntimeAlertParseStatus.Ok, result.Status);
        Assert.Empty(result.Rows);
    }

    [Fact]
    public void ERROR_is_a_high_alert_and_WARNING_keeps_its_level_with_the_runtime_word_kept_as_a_fact()
    {
        var result = RuntimeAlertSnapshotParser.Parse(Printed(Row("e1", "ERROR", "health-failure"), Row("w1", "WARNING", "egress-block")));

        Assert.Equal(AuditSeverity.High, result.Rows[0].Severity);
        Assert.Equal("ERROR", result.Rows[0].Facts.Single(f => f.Name == "runtime_severity").Value);
        Assert.Equal(AuditSeverity.Warn, result.Rows[1].Severity);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n")]
    [InlineData("No audit store available. Run 'defenseclaw init' first.")]
    [InlineData("{\"id\": \"not-a-list\"}")]
    public void Output_without_a_list_is_not_json(string output)
    {
        Assert.Equal(RuntimeAlertParseStatus.NotJson, RuntimeAlertSnapshotParser.Parse(output).Status);
    }

    [Fact]
    public void A_list_with_a_bad_row_is_refused_whole()
    {
        var noId = Row("x");
        noId["id"] = string.Empty;
        var badTime = Row("y");
        badTime["timestamp"] = "yesterday-ish";

        Assert.Equal(RuntimeAlertParseStatus.Invalid, RuntimeAlertSnapshotParser.Parse(Printed(Row("ok"), noId)).Status);
        Assert.Equal(RuntimeAlertParseStatus.Invalid, RuntimeAlertSnapshotParser.Parse(Printed(Row("ok"), badTime)).Status);
        Assert.Equal(RuntimeAlertParseStatus.Invalid, RuntimeAlertSnapshotParser.Parse(Printed(Row("dup"), Row("dup"))).Status);
        Assert.Equal(RuntimeAlertParseStatus.Invalid, RuntimeAlertSnapshotParser.Parse("[1, 2]").Status);
    }

    [Fact]
    public void More_than_the_requested_limit_is_refused()
    {
        var rows = Enumerable.Range(0, RuntimeAlertSnapshotParser.Limit + 1).Select(i => Row("r" + i)).ToArray();

        Assert.Equal(RuntimeAlertParseStatus.Invalid, RuntimeAlertSnapshotParser.Parse(Printed(rows)).Status);
        Assert.Equal(RuntimeAlertParseStatus.Ok, RuntimeAlertSnapshotParser.Parse(Printed(rows.Take(RuntimeAlertSnapshotParser.Limit).ToArray())).Status);
    }

    // ---- The capability and the read-only command ----

    private static RuntimeSnapshot Probe(string version) =>
        RuntimeProbe.Evaluate(
            new RuntimeProbeScreens("{\"name\":\"defenseclaw-cli\",\"version\":\"" + version + "\",\"schema_version\":1}", "Commands:\n  setup  Configure.\n", "Commands:\n  amp  x\n", null, null, null, null, null),
            "test",
            "fp",
            DateTimeOffset.UnixEpoch);

    [Theory]
    [InlineData("1.0.0", true)]
    [InlineData("1.0.4", true)]
    [InlineData("0.8.10", false)]
    public void The_capability_follows_the_reported_version(string version, bool expected)
    {
        Assert.Equal(expected, Probe(version).Capabilities.Has(RuntimeCapability.RuntimeAlerts));
        Assert.False(RuntimeGate.Check(RuntimeCapabilities.Unknown, RuntimeCapability.RuntimeAlerts).IsAvailable);
        Assert.False(string.IsNullOrWhiteSpace(RuntimeCapabilityCatalog.DisplayName(RuntimeCapability.RuntimeAlerts)));
        Assert.False(string.IsNullOrWhiteSpace(RuntimeCapabilityCatalog.Marker(RuntimeCapability.RuntimeAlerts)));
    }

    [Fact]
    public void The_alert_list_command_is_read_only_and_the_alert_verbs_are_not()
    {
        Assert.Equal(CommandTier.ReadOnly, CommandTiers.Classify(["alerts", "--json", "--limit", "500"]));
        Assert.Equal(CommandTier.ReadOnly, CommandTiers.Classify(["alerts", "--limit", "500", "--json"]));
        Assert.NotEqual(CommandTier.ReadOnly, CommandTiers.Classify(["alerts", "acknowledge", "--id", "a", "--json"]));
        Assert.NotEqual(CommandTier.ReadOnly, CommandTiers.Classify(["alerts", "dismiss", "--severity", "HIGH", "--json"]));
        Assert.NotEqual(CommandTier.ReadOnly, CommandTiers.Classify(["alerts"]));
    }

    // ---- Detection-only findings ----

    private void Finding(string id, int minute, string? payload)
    {
        _database.InsertEvent(id, Base.AddMinutes(minute), "scan-finding", "HIGH", "security.finding", "claudecode", eventName: "finding.observed");
        if (payload is null)
        {
            return;
        }

        using var connection = _database.OpenWritable();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE audit_events SET payload_json = $payload WHERE id = $id";
        command.Parameters.AddWithValue("$payload", payload);
        command.Parameters.AddWithValue("$id", id);
        _ = command.ExecuteNonQuery();
    }

    private void SeedDetectionOnly()
    {
        Finding("plain", 1, null);
        Finding("array-tag", 2, "{\"defenseclaw.finding.tags\":[\"pii\",\"Detection-Only\"]}");
        Finding("scalar-tag", 3, "{\"defenseclaw.finding.tags\":\"  detection-only \"}");
        Finding("other-tags", 4, "{\"defenseclaw.finding.tags\":[\"pii\"]}");
        Finding("mentions-it", 5, "{\"note\":\"detection-only is discussed here\"}");
        Finding("not-json", 6, "detection-only but not JSON");
    }

    [Fact]
    public async Task Detection_only_findings_of_the_window_are_counted()
    {
        SeedDetectionOnly();

        Assert.Equal(2, await DetectionOnlyFindings.CountAsync(_database.Path, Base));
        Assert.Equal(1, await DetectionOnlyFindings.CountAsync(_database.Path, Base.AddMinutes(2.5)));
        Assert.Equal(0, await DetectionOnlyFindings.CountAsync(_database.Path, Base.AddHours(1)));
        Assert.Equal(0, await DetectionOnlyFindings.CountAsync(Path.Combine(Path.GetTempPath(), "no-such-audit-" + Guid.NewGuid().ToString("n") + ".db"), Base));
    }

    [Fact]
    public async Task The_queue_leaves_detection_only_findings_out_only_when_asked()
    {
        SeedDetectionOnly();

        var listed = new AlertQueueReader(_database.Path, readTimeout: TestTimeouts.Ceiling);
        var leaving = new AlertQueueReader(_database.Path, readTimeout: TestTimeouts.Ceiling, excludeDetectionOnly: () => true);
        var notLeaving = new AlertQueueReader(_database.Path, readTimeout: TestTimeouts.Ceiling, excludeDetectionOnly: () => false);

        var all = (await listed.ReadAsync(100)).Counts.Newest.Select(i => i.Id).Order().ToArray();
        var without = (await leaving.ReadAsync(100)).Counts.Newest.Select(i => i.Id).Order().ToArray();

        Assert.Equal(["array-tag", "mentions-it", "not-json", "other-tags", "plain", "scalar-tag"], all);
        Assert.Equal(["mentions-it", "not-json", "other-tags", "plain"], without);
        Assert.Equal(all, (await notLeaving.ReadAsync(100)).Counts.Newest.Select(i => i.Id).Order().ToArray());
    }

    [Fact]
    public async Task The_plan_with_detection_only_left_out_still_walks_an_index_per_arm()
    {
        SeedDetectionOnly();

        var plan = await new AlertQueueReader(_database.Path, readTimeout: TestTimeouts.Ceiling, excludeDetectionOnly: () => true).ExplainAsync();

        Assert.Contains(plan, line => line.Contains("idx_audit_event_name_timestamp", StringComparison.Ordinal));
        Assert.Contains(plan, line => line.Contains("idx_audit_bucket_timestamp", StringComparison.Ordinal));
    }
}
