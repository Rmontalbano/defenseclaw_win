using System.Collections.Specialized;
using System.Globalization;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Audit;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.App.Tests.Logs;

/// <summary>
/// CUST-284 on the Logs panel's database streams. The five-second poll over a database that did not change projects no row (the reader
/// hands back its last list); over one that moved for another reason it reads again, finds the same rows and leaves the list alone; and a
/// row whose payload is over the limit is a row that says why its body is missing, not a missing row. Synthetic rows from the real DDL.
/// </summary>
public sealed class LogsSnapshotTests : IDisposable
{
    private static readonly DateTimeOffset Base = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory _temp = new();
    private readonly AppServices _services;

    public LogsSnapshotTests()
    {
        _services = TestServices.Create(_temp);
    }

    public void Dispose()
    {
        _services.Dispose();
        SqliteConnection.ClearAllPools();
        _temp.Dispose();
    }

    private string Database(Action<Action<string, int, string, string, string?>> seed)
    {
        var path = _temp.File("stream-audit.db");
        AuditTestDatabase.Create(path, rows: 0);
        Add(path, seed);
        return path;
    }

    private static void Add(string path, Action<Action<string, int, string, string, string?>> seed)
    {
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()))
        {
            connection.Open();
            seed((id, minute, bucket, eventName, payload) =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO audit_events (id, timestamp, action, target, actor, details, severity, bucket, event_name, connector, source, signal, payload_json)
                    VALUES ($id, $timestamp, 'act', '', 'gateway', 'details', 'INFO', $bucket, $eventName, 'claudecode', 'sidecar', 'logs', $payload)
                    """;
                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$timestamp", Base.AddMinutes(minute).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture) + "Z");
                command.Parameters.AddWithValue("$bucket", bucket);
                command.Parameters.AddWithValue("$eventName", eventName);
                command.Parameters.AddWithValue("$payload", (object?)payload ?? DBNull.Value);
                _ = command.ExecuteNonQuery();
            });
        }

        SqliteConnection.ClearAllPools();
    }

    private LogsPanelViewModel VerdictsPanel(EventStreamReader reader)
    {
        var panel = new LogsPanelViewModel(_services) { StreamReader = reader };
        panel.SetActive(true);
        panel.ActiveSource = "Verdicts";
        panel.SelectedPreset = "all";
        return panel;
    }

    [Fact]
    public async Task A_poll_of_an_unchanged_database_projects_no_row_and_changes_nothing()
    {
        var path = Database(add =>
        {
            add("v1", 1, "guardrail.evaluation", "guardrail.evaluated", "{\"defenseclaw.guardrail.decision\":\"allow\"}");
            add("v2", 2, "enforcement.action", "enforcement.applied", null);
            add("s1", 3, "asset.scan", "scan.completed", "{\"defenseclaw.scan.id\":\"abc\"}");
        });
        using var probe = new AuditChangeProbe(path);
        var reader = new EventStreamReader(path, probe: probe);
        var panel = VerdictsPanel(reader);
        await panel.LoadStructuredAsync();
        panel.SelectedEntry = panel.DisplayedLines[1];
        var decoded = reader.RowsDecoded;
        Assert.Equal(3, decoded);
        var seen = new List<NotifyCollectionChangedAction>();
        panel.DisplayedLines.CollectionChanged += (_, e) => seen.Add(e.Action);

        await panel.LoadStructuredAsync();
        await panel.LoadStructuredAsync();

        Assert.Equal(decoded, reader.RowsDecoded);
        Assert.Equal(2, reader.UnchangedReads);
        Assert.Empty(seen);
        Assert.NotNull(panel.SelectedEntry);
        Assert.Equal(3, panel.DisplayedLines.Count);
    }

    [Fact]
    public async Task A_poll_after_the_database_moved_for_another_reason_reads_again_and_still_changes_nothing()
    {
        var path = Database(add =>
        {
            add("v1", 1, "guardrail.evaluation", "guardrail.evaluated", null);
            add("v2", 2, "enforcement.action", "enforcement.applied", null);
        });
        using var probe = new AuditChangeProbe(path);
        var reader = new EventStreamReader(path, probe: probe);
        var panel = VerdictsPanel(reader);
        await panel.LoadStructuredAsync();
        panel.SelectedEntry = panel.DisplayedLines[0];
        var decoded = reader.RowsDecoded;
        var unchanged = reader.UnchangedReads;
        var seen = new List<NotifyCollectionChangedAction>();
        panel.DisplayedLines.CollectionChanged += (_, e) => seen.Add(e.Action);

        // Telemetry, which the Verdicts stream does not list: the database moved, the answer did not.
        Add(path, add => add("noise", 3, "telemetry.ingest", "span.received", null));
        await panel.LoadStructuredAsync();

        // The statement ran again (the poll was not answered from memory), found the rows it had projected before, and handed them back as they were.
        Assert.Equal(unchanged, reader.UnchangedReads);
        Assert.Equal(decoded, reader.RowsDecoded);
        Assert.Empty(seen);
        Assert.NotNull(panel.SelectedEntry);

        // A real new verdict does arrive.
        Add(path, add => add("v3", 4, "guardrail.evaluation", "guardrail.evaluated", null));
        await panel.LoadStructuredAsync();
        Assert.Equal(3, panel.DisplayedLines.Count);
        Assert.Equal("v3", panel.DisplayedLines[^1].Fields.Single(f => f.Name == "id").Value);
    }

    [Fact]
    public async Task A_row_with_a_payload_over_the_limit_is_shown_with_the_reason_its_body_is_missing()
    {
        var big = "{\"k\":\"" + new string('a', EventStreamReader.PayloadByteLimit + 100) + "\"}";
        var path = Database(add =>
        {
            // Different event names: the panel folds lines that read the same.
            add("big", 1, "guardrail.evaluation", "guardrail.evaluated", big);
            add("small", 2, "enforcement.action", "enforcement.applied", "{\"k\":1}");
        });
        using var probe = new AuditChangeProbe(path);
        var panel = VerdictsPanel(new EventStreamReader(path, probe: probe));

        await panel.LoadStructuredAsync();

        // Both rows are listed (the stream is not empty because of the big one), and the big one says why it has no body.
        Assert.Equal(2, panel.DisplayedLines.Count);
        var row = panel.DisplayedLines.Single(l => l.Fields.Any(f => f is { Name: "id", Value: "big" }));
        Assert.Contains(
            string.Create(CultureInfo.InvariantCulture, $"payload_json is {big.Length:N0} bytes, over the 64 KB limit"),
            row.Raw,
            StringComparison.Ordinal);
        Assert.DoesNotContain("aaaaaaaaaa", row.Raw, StringComparison.Ordinal);
    }
}
