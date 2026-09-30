using DefenseClaw.Core.Audit;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// <see cref="AuditReader.GetByIdsAsync"/>: the lookup that turns the alert queue's ids into full rows for the Alerts panel.
/// Synthetic rows from the real DDL only.
/// </summary>
public sealed class AuditReaderByIdsTests : IDisposable
{
    private static readonly DateTimeOffset Base = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    private readonly TestAuditDatabase _database = new();

    public void Dispose() => _database.Dispose();

    private AuditReader Reader() => new(_database.Path);

    private void Row(string id, int minute, string severity = "HIGH", string? structured = null) =>
        _database.InsertEvent(id, Base.AddMinutes(minute), "scan-finding", severity, "security.finding", "claudecode", structuredJson: structured, eventName: "finding.observed");

    [Fact]
    public async Task It_returns_the_rows_asked_for_with_their_heavy_columns_and_nothing_else()
    {
        Row("a", 1, structured: """{"defenseclaw.rule_id":"R-1"}""");
        Row("b", 2);
        Row("c", 3);

        var rows = await Reader().GetByIdsAsync(new[] { "a", "c" });

        Assert.Equal(new[] { "a", "c" }, rows.Select(r => r.Id).OrderBy(id => id, StringComparer.Ordinal));
        Assert.Equal("R-1", rows.Single(r => r.Id == "a").StructuredString("defenseclaw.rule_id"));
        Assert.Equal("claudecode", rows.Single(r => r.Id == "a").Connector);
    }

    [Fact]
    public async Task An_id_with_no_row_is_absent_and_blank_or_repeated_ids_do_no_harm()
    {
        Row("a", 1);

        var rows = await Reader().GetByIdsAsync(new[] { "a", "a", "", "no-such-row" });

        Assert.Equal("a", Assert.Single(rows).Id);
    }

    [Fact]
    public async Task No_ids_or_no_database_is_an_empty_answer_not_an_error()
    {
        Assert.Empty(await Reader().GetByIdsAsync(Array.Empty<string>()));
        Assert.Empty(await new AuditReader(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(_database.Path)!, "absent.db")).GetByIdsAsync(new[] { "a" }));
    }

    [Fact]
    public async Task More_ids_than_one_statement_carries_are_read_in_batches()
    {
        for (var i = 0; i < 850; i++)
        {
            Row($"row-{i:D4}", i % 600);
        }

        var ids = Enumerable.Range(0, 850).Select(i => $"row-{i:D4}").Append("missing").ToArray();
        var rows = await Reader().GetByIdsAsync(ids);

        Assert.Equal(850, rows.Count);
        Assert.Equal(850, rows.Select(r => r.Id).Distinct().Count());
    }

    [Fact]
    public async Task An_id_that_looks_like_sql_is_a_bound_value()
    {
        Row("a", 1);

        var rows = await Reader().GetByIdsAsync(new[] { "a' OR '1'='1", "x'); DROP TABLE audit_events; --" });

        Assert.Empty(rows);
        Assert.Equal("a", Assert.Single(await Reader().GetByIdsAsync(new[] { "a" })).Id);
    }

    [Fact]
    public async Task A_cancelled_token_stops_the_read()
    {
        Row("a", 1);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Reader().GetByIdsAsync(new[] { "a" }, cts.Token));
    }
}
