using DefenseClaw.Core.Audit;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// CUST-262: the Audit panel's "Actionable only" page against a database built from the real DDL with synthetic rows. The rule itself is
/// <see cref="ActionableRuleTests"/>; here it is the paging around it - a full page of what matters, a cursor that skips nothing and repeats nothing,
/// a bounded scan, and a repeat over a database that did not change that decodes nothing.
/// </summary>
public sealed class ActionableAuditPagingTests : IDisposable
{
    private static readonly DateTimeOffset Base = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly TestAuditDatabase _database = new();

    public void Dispose() => _database.Dispose();

    /// <summary>
    /// <paramref name="rows"/> events, row <c>i</c> a second after row <c>i-1</c> (so the newest is the last), ids <c>r000</c>... Row <c>i</c> is
    /// HIGH when <paramref name="loud"/> says so and a quiet INFO hook decision otherwise.
    /// </summary>
    private void Seed(int rows, Func<int, bool> loud, Func<int, string?>? connectorFor = null)
    {
        for (var i = 0; i < rows; i++)
        {
            _database.InsertEvent(
                $"r{i:000}",
                Base.AddSeconds(i),
                "hook_decision",
                loud(i) ? "HIGH" : "INFO",
                "guardrail.evaluation",
                connectorFor is null ? "claudecode" : connectorFor(i),
                details: loud(i) ? "a loud one" : "quiet and allowed");
        }
    }

    private static string Id(int i) => $"r{i:000}";

    [Fact]
    public async Task A_page_is_filled_with_actionable_rows_newest_first_and_counts_what_it_left_out()
    {
        // Every fourth row matters; the newest rows are 99, 98, 97, 96 ...: 96, 92, 88, 84, 80 are the first five that do.
        Seed(100, i => i % 4 == 0);
        var reader = new AuditReader(_database.Path);

        var page = await reader.QueryActionableAsync(new AuditQuery { Limit = 5 });

        Assert.Equal(new[] { Id(96), Id(92), Id(88), Id(84), Id(80) }, page.Events.Select(e => e.Id));
        Assert.Equal(15, page.Hidden);
        Assert.True(page.HasMore);
        Assert.Equal(page.Events[^1].Cursor, page.NextCursor);

        // The rows that were read and left out are named: the newest twenty, less the five kept.
        var hidden = page.HiddenRows.Select(c => c.Id).ToArray();
        Assert.Equal(15, hidden.Length);
        Assert.Equal(
            Enumerable.Range(80, 20).Select(Id).Except(page.Events.Select(e => e.Id)).Order(StringComparer.Ordinal),
            hidden.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Walking_the_cursor_visits_every_row_once_shows_every_actionable_one_and_counts_the_rest()
    {
        Seed(100, i => i % 4 == 0);
        var reader = new AuditReader(_database.Path);

        var shown = new List<string>();
        var hidden = 0;
        AuditCursor? cursor = null;
        for (var calls = 0; ; calls++)
        {
            Assert.True(calls < 50, "the walk does not end");
            var page = await reader.QueryActionableAsync(new AuditQuery { Limit = 5, After = cursor });
            shown.AddRange(page.Events.Select(e => e.Id));
            hidden += page.Hidden;

            // A cursor comes with "more" and only with it.
            Assert.Equal(page.HasMore, page.NextCursor is not null);
            if (!page.HasMore)
            {
                break;
            }

            cursor = page.NextCursor;
        }

        Assert.Equal(Enumerable.Range(0, 25).Select(n => Id((24 - n) * 4)), shown);
        Assert.Equal(75, hidden);
        Assert.Equal(shown.Count, shown.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task A_scan_is_bounded_and_the_next_call_carries_on_from_the_last_row_read()
    {
        // The only rows that matter are the ten oldest of 400: with a page of 5 a call reads at most ScanPages x 5 = 100 rows, and finds none.
        Seed(400, i => i < 10);
        var reader = new AuditReader(_database.Path);

        var first = await reader.QueryActionableAsync(new AuditQuery { Limit = 5 });

        Assert.Empty(first.Events);
        Assert.Equal(ActionableAuditPaging.ScanPages * 5, first.Hidden);
        Assert.True(first.HasMore);
        Assert.NotNull(first.NextCursor);

        // Carrying on from its cursor reaches them: nothing was skipped by stopping early.
        var shown = new List<string>();
        var cursor = first.NextCursor;
        for (var calls = 0; calls < 50 && cursor is not null; calls++)
        {
            var page = await reader.QueryActionableAsync(new AuditQuery { Limit = 5, After = cursor });
            shown.AddRange(page.Events.Select(e => e.Id));
            cursor = page.HasMore ? page.NextCursor : null;
        }

        Assert.Equal(Enumerable.Range(0, 10).Select(n => Id(9 - n)), shown);
    }

    [Fact]
    public async Task A_window_with_fewer_rows_than_a_page_ends_without_a_cursor()
    {
        Seed(7, i => i == 3);
        var reader = new AuditReader(_database.Path);

        var page = await reader.QueryActionableAsync(new AuditQuery { Limit = 5 });

        Assert.Equal(new[] { Id(3) }, page.Events.Select(e => e.Id));
        Assert.Equal(6, page.Hidden);
        Assert.False(page.HasMore);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task A_full_page_that_ends_exactly_at_the_end_of_the_window_has_no_more()
    {
        Seed(5, _ => true);
        var reader = new AuditReader(_database.Path);

        var page = await reader.QueryActionableAsync(new AuditQuery { Limit = 5 });

        Assert.Equal(5, page.Events.Count);
        Assert.Equal(0, page.Hidden);
        Assert.False(page.HasMore);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task An_empty_database_is_an_empty_page()
    {
        var reader = new AuditReader(_database.Path);

        var page = await reader.QueryActionableAsync(new AuditQuery { Limit = 5 });

        Assert.Empty(page.Events);
        Assert.Equal(0, page.Hidden);
        Assert.False(page.HasMore);
    }

    [Fact]
    public async Task The_other_filters_apply_to_the_rows_that_are_read()
    {
        // Rows alternate between two connectors; every row of both is loud. Asking for one reads only that one's rows.
        Seed(40, _ => true, i => i % 2 == 0 ? "codex" : "claudecode");
        var reader = new AuditReader(_database.Path);

        var page = await reader.QueryActionableAsync(new AuditQuery { Limit = 30, Connector = "codex" });

        Assert.Equal(20, page.Events.Count);
        Assert.All(page.Events, e => Assert.Equal("codex", e.Connector));
        Assert.Equal(0, page.Hidden);
    }

    [Fact]
    public async Task A_row_the_rule_cannot_read_is_kept_not_hidden()
    {
        // 64-byte payload limit: the 300-character details of the quiet row are left in the database, so what they say is unknown.
        _database.InsertEvent("big", Base.AddSeconds(2), "hook_decision", "INFO", "guardrail.evaluation", "claudecode", details: new string('x', 300));
        _database.InsertEvent("quiet", Base.AddSeconds(1), "hook_decision", "INFO", "guardrail.evaluation", "claudecode", details: "fine");
        var reader = new AuditReader(_database.Path, payloadLimitBytes: 64);

        var page = await reader.QueryActionableAsync(new AuditQuery { Limit = 10 });

        var kept = Assert.Single(page.Events);
        Assert.Equal("big", kept.Id);
        Assert.True(kept.IsOversized);
        Assert.Equal(1, page.Hidden);
    }

    [Fact]
    public async Task Repeating_a_call_over_a_database_that_did_not_change_decodes_no_row_at_all()
    {
        Seed(100, i => i % 4 == 0);
        using var probe = new AuditChangeProbe(_database.Path);
        var reader = new AuditReader(_database.Path, probe: probe);
        var query = new AuditQuery { Limit = 5 };

        var first = await reader.QueryActionableAsync(query);
        var decoded = reader.RowsDecoded;
        var unchanged = reader.UnchangedReads;
        Assert.True(decoded > 0);

        var second = await reader.QueryActionableAsync(query);

        // The four raw pages the first call read are the four the reader remembers: the second is four probes and no statement.
        Assert.Equal(decoded, reader.RowsDecoded);
        Assert.Equal(unchanged + 4, reader.UnchangedReads);
        Assert.Equal(first.Events.Select(e => e.Id), second.Events.Select(e => e.Id));
        Assert.Equal(first.Hidden, second.Hidden);
    }

    [Fact]
    public async Task A_longer_scan_than_the_reader_remembers_reads_again_and_says_the_same()
    {
        // The only rows that matter are the ten oldest of 100: finding five takes nineteen pages of five, and the memo holds four.
        Seed(100, i => i < 10);
        using var probe = new AuditChangeProbe(_database.Path);
        var reader = new AuditReader(_database.Path, probe: probe);
        var query = new AuditQuery { Limit = 5 };

        var first = await reader.QueryActionableAsync(query);
        var decoded = reader.RowsDecoded;
        var second = await reader.QueryActionableAsync(query);

        Assert.Equal(new[] { Id(9), Id(8), Id(7), Id(6), Id(5) }, first.Events.Select(e => e.Id));
        Assert.Equal(first.Events.Select(e => e.Id), second.Events.Select(e => e.Id));
        Assert.Equal(first.Hidden, second.Hidden);
        Assert.Equal(first.NextCursor, second.NextCursor);
        Assert.True(reader.RowsDecoded > decoded, "the pages that did not fit the memo were read again");
    }

    [Fact]
    public async Task A_cancelled_token_ends_the_read()
    {
        Seed(10, _ => true);
        var reader = new AuditReader(_database.Path);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.QueryActionableAsync(new AuditQuery { Limit = 5 }, cancelled.Token));
    }

    [Fact]
    public async Task Missing_arguments_are_errors()
    {
        var reader = new AuditReader(_database.Path);

        _ = await Assert.ThrowsAsync<ArgumentNullException>(() => ActionableAuditPaging.QueryActionableAsync(null!, new AuditQuery()));
        _ = await Assert.ThrowsAsync<ArgumentNullException>(() => reader.QueryActionableAsync(null!));
    }

    [Fact]
    public async Task The_page_is_the_ordinary_page_of_the_reader_with_one_more_fact()
    {
        // With every row loud nothing is hidden, and the page is the ordinary one.
        Seed(12, _ => true);
        var reader = new AuditReader(_database.Path);

        var ordinary = await reader.QueryAsync(new AuditQuery { Limit = 5 });
        var actionable = await reader.QueryActionableAsync(new AuditQuery { Limit = 5 });

        Assert.Equal(ordinary.Events.Select(e => e.Id), actionable.Events.Select(e => e.Id));
        Assert.Equal(ordinary.HasMore, actionable.HasMore);
        Assert.Equal(ordinary.NextCursor, actionable.NextCursor);
        Assert.Equal(0, ordinary.Hidden);
        Assert.Equal(0, actionable.Hidden);
    }
}
