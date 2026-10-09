using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.App.Tests.Alerts;

/// <summary>
/// CUST-284 on the Alerts panel. A refresh over a database that did not change reads no queue row, hydrates no finding and rebuilds
/// nothing; a refresh over a database that changed for reasons that are not findings (it moves all day) reads the queue again, finds the
/// same findings, and still rebuilds nothing; and a finding whose audit row is too large to load is listed with the reason and counted,
/// never dropped. Synthetic findings from the real DDL; the panel runs on a plain STA thread.
/// </summary>
public sealed class AlertsPanelSnapshotTests : IDisposable
{
    private static readonly DateTimeOffset Base = DateTimeOffset.UtcNow.AddHours(-3);

    private readonly TempDirectory _temp = new();
    private readonly AppServices _services;
    private readonly AlertQueueDatabase _database;

    public AlertsPanelSnapshotTests()
    {
        _services = TestServices.Create(_temp);
        _database = new AlertQueueDatabase(_services.Paths.AuditDatabasePath);
    }

    public void Dispose()
    {
        _services.Dispose();
        SqlitePools.Release(_temp.Path);
        _temp.Dispose();
    }

    private void Finding(string id, int minute, string severity = "HIGH", string? structured = null)
    {
        using var connection = new SqliteConnection($"Data Source={_services.Paths.AuditDatabasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO audit_events (id, timestamp, action, target, actor, severity, structured_json, bucket, connector, event_name)
            VALUES ($id, $timestamp, 'scan-finding', $target, 'audit_logger', $severity, $structured, 'security.finding', 'claudecode', 'finding.observed')
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$timestamp", AlertQueueDatabase.Format(Base.AddMinutes(minute)));
        command.Parameters.AddWithValue("$target", "/synthetic/" + id);
        command.Parameters.AddWithValue("$severity", severity);
        command.Parameters.AddWithValue("$structured", (object?)structured ?? DBNull.Value);
        _ = command.ExecuteNonQuery();
    }

    private static string[] Ids(AlertsPanelViewModel vm) => vm.Alerts.Select(a => a.Key).ToArray();

    private static string OverLimit(string key = "pad") => "{\"" + key + "\":\"" + new string('d', 270_000) + "\"}";

    // ------------------------------------------------------------------ nothing changed: nothing read, nothing rebuilt

    [Fact]
    public void A_second_refresh_with_nothing_new_reads_no_queue_row_hydrates_no_finding_and_rebuilds_nothing()
    {
        Finding("a", 1);
        Finding("b", 2);
        Finding("c", 3);

        StaThread.Run(async () =>
        {
            var vm = new AlertsPanelViewModel(_services);
            await vm.InitializeAsync();
            var kept = vm.Alerts.ToArray();
            Assert.Equal(new[] { "c", "b", "a" }, Ids(vm));

            var queueDecoded = _services.AlertQueue.RowsDecoded;
            var auditDecoded = _services.Audit.RowsDecoded;
            var unchanged = _services.AlertQueue.UnchangedReads;
            Assert.True(queueDecoded >= 3);

            await vm.RefreshCommand.ExecuteAsync(null);
            await vm.RefreshCommand.ExecuteAsync(null);

            Assert.Equal(queueDecoded, _services.AlertQueue.RowsDecoded);
            Assert.Equal(auditDecoded, _services.Audit.RowsDecoded);
            Assert.Equal(unchanged + 2, _services.AlertQueue.UnchangedReads);
            Assert.True(kept.SequenceEqual(vm.Alerts));
            Assert.StartsWith("Unacknowledged findings · 3 · read ", vm.SourceNote, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void A_database_change_that_is_not_a_finding_reads_the_queue_again_but_rebuilds_nothing()
    {
        Finding("a", 1);
        Finding("b", 2);

        StaThread.Run(async () =>
        {
            var vm = new AlertsPanelViewModel(_services);
            await vm.InitializeAsync();
            var kept = vm.Alerts.ToArray();
            vm.SelectedAlert = kept[1];
            var queueDecoded = _services.AlertQueue.RowsDecoded;
            var auditDecoded = _services.Audit.RowsDecoded;

            // Telemetry-ish, not a finding: the database moved (the probe knows) and the queue is the same two.
            _database.AddEvent("noise", Base.AddMinutes(5), "/synthetic/noise", "otel.ingest.logs");
            await vm.RefreshCommand.ExecuteAsync(null);

            Assert.True(_services.AlertQueue.RowsDecoded > queueDecoded, "the queue had to be read again");
            Assert.Equal(auditDecoded, _services.Audit.RowsDecoded);
            Assert.True(kept.SequenceEqual(vm.Alerts));
            Assert.Same(kept[1], vm.SelectedAlert);
        });
    }

    [Fact]
    public void A_new_finding_hydrates_only_its_own_row_and_joins_the_list_in_place()
    {
        Finding("a", 1);
        Finding("b", 2);
        Finding("c", 3);

        StaThread.Run(async () =>
        {
            var vm = new AlertsPanelViewModel(_services);
            await vm.InitializeAsync();
            var kept = vm.Alerts.ToArray();
            var auditDecoded = _services.Audit.RowsDecoded;

            Finding("d", 4, "CRITICAL");
            await vm.RefreshCommand.ExecuteAsync(null);

            Assert.Equal(new[] { "d", "c", "b", "a" }, Ids(vm));
            Assert.Equal(auditDecoded + 1, _services.Audit.RowsDecoded);
            Assert.Same(kept[0], vm.Alerts[1]);
        });
    }

    [Fact]
    public void An_acknowledgement_takes_the_finding_out_on_the_next_read()
    {
        Finding("a", 1);
        Finding("b", 2);

        StaThread.Run(async () =>
        {
            var vm = new AlertsPanelViewModel(_services);
            await vm.InitializeAsync();
            Assert.Equal(new[] { "b", "a" }, Ids(vm));

            // The same two findings read again are "nothing new" ...
            await vm.RefreshCommand.ExecuteAsync(null);
            Assert.Equal(new[] { "b", "a" }, Ids(vm));

            // ... but an acknowledgement is a change, and the list follows it.
            _database.Acknowledge("b");
            await vm.RefreshCommand.ExecuteAsync(null);
            Assert.Equal(new[] { "a" }, Ids(vm));
        });
    }

    [Fact]
    public void The_same_egress_decisions_are_not_decoded_twice()
    {
        _database.AddEgress("x1", Base.AddMinutes(1), "block", "passthrough", looksLikeLlm: false);
        _database.AddEgress("x2", Base.AddMinutes(2), "block", "shape", looksLikeLlm: true);
        Finding("a", 3);

        StaThread.Run(async () =>
        {
            var vm = new AlertsPanelViewModel(_services);
            await vm.InitializeAsync();
            var kept = vm.Alerts.ToArray();
            Assert.Equal(3, kept.Length);
            var decoded = vm.EgressReader.RowsDecoded;
            Assert.True(decoded >= 2);

            await vm.RefreshCommand.ExecuteAsync(null);

            Assert.Equal(decoded, vm.EgressReader.RowsDecoded);
            Assert.True(vm.EgressReader.UnchangedReads >= 1);
            Assert.True(kept.SequenceEqual(vm.Alerts));
        });
    }

    // ------------------------------------------------------------------ too large to load

    [Fact]
    public void A_finding_whose_attributes_are_too_large_is_listed_with_the_reason_and_counted()
    {
        Finding("ok", 1, structured: "{\"defenseclaw.finding.rule_id\":\"R-1\",\"defenseclaw.finding.title\":\"Fine\"}");
        Finding("big", 2, structured: OverLimit());

        StaThread.Run(async () =>
        {
            var vm = new AlertsPanelViewModel(_services);
            await vm.InitializeAsync();

            // Never dropped, never an empty list: the queue knows the finding, and the row says what it could not load.
            Assert.Equal(new[] { "big", "ok" }, Ids(vm));
            var big = vm.Alerts[0];
            Assert.True(big.IsOversized);
            Assert.Equal("Too large to display: structured_json is 270,010 bytes, over the 256 KB limit", big.OversizedNotice);
            Assert.Equal(big.OversizedNotice, big.Headline);
            Assert.Equal("scan-finding", big.Action);
            Assert.Equal("HIGH", big.Severity);
            Assert.Equal("/synthetic/big", big.TargetRef);
            Assert.Equal(("unavailable", big.OversizedNotice), (big.Fields[0].Name, big.Fields[0].Value));
            Assert.Contains("over the 256 KB limit", big.StructuredText, StringComparison.Ordinal);

            Assert.False(vm.Alerts[1].IsOversized);
            Assert.Equal("Fine", vm.Alerts[1].Headline);
            Assert.Contains("1 finding too large to display", vm.SourceNote, StringComparison.Ordinal);
            Assert.Equal("2 of 2 alerts", vm.CountSummary);
        });
    }

    [Fact]
    public void A_queue_of_findings_that_are_all_too_large_is_not_the_empty_state()
    {
        Finding("big-1", 1, structured: OverLimit());
        Finding("big-2", 2, structured: OverLimit("other"));

        StaThread.Run(async () =>
        {
            var vm = new AlertsPanelViewModel(_services);
            await vm.InitializeAsync();

            Assert.Equal(2, vm.Alerts.Count);
            Assert.False(vm.IsEmpty);
            Assert.All(vm.Alerts, alert => Assert.True(alert.IsOversized));
            Assert.Contains("2 findings too large to display", vm.SourceNote, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void A_collapsed_group_keeps_the_reason_of_the_finding_that_represents_it()
    {
        Finding("big-1", 1, structured: OverLimit());
        Finding("big-2", 2, structured: OverLimit());

        StaThread.Run(async () =>
        {
            var vm = new AlertsPanelViewModel(_services);
            await vm.InitializeAsync();

            vm.CollapseRepeats = true;

            var group = Assert.Single(vm.Alerts);
            Assert.True(group.IsOversized);
            Assert.Equal(2, group.RepeatCount);
        });
    }
}
