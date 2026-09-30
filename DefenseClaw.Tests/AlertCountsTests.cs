using System.Text.Json;
using DefenseClaw.Core.Audit;
using DefenseClaw.Core.Gateway.Models;

namespace DefenseClaw.Tests;

/// <summary>The counts value type: tallies, the connector breakdown, change detection and the gateway fallback.</summary>
public sealed class AlertCountsTests
{
    private static readonly DateTimeOffset Base = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    private static AlertQueueItem Item(string id, AuditSeverity severity, string? connector = "claudecode", int minute = 0) =>
        new(id, severity, "scan-finding", "target", connector, Base.AddMinutes(minute));

    [Fact]
    public void Empty_is_zero_everywhere()
    {
        var empty = AlertCounts.Empty;

        Assert.Equal(0, empty.Total);
        Assert.Equal(SeverityTally.Zero, empty.Tally);
        Assert.Empty(empty.Newest);
        Assert.Empty(empty.ByConnector);
        Assert.False(empty.HasMore);
    }

    [Fact]
    public void The_tally_is_indexable_by_severity_and_zero_for_levels_the_queue_never_holds()
    {
        var tally = new SeverityTally(1, 2, 3, 4);

        Assert.Equal(1, tally[AuditSeverity.Critical]);
        Assert.Equal(2, tally[AuditSeverity.High]);
        Assert.Equal(3, tally[AuditSeverity.Medium]);
        Assert.Equal(4, tally[AuditSeverity.Low]);
        Assert.Equal(0, tally[AuditSeverity.Info]);
        Assert.Equal(0, tally[AuditSeverity.Warn]);
        Assert.Equal(0, tally[AuditSeverity.Unknown]);
        Assert.Equal(10, tally.Total);
        Assert.Equal(new SeverityTally(2, 4, 6, 8), tally + tally);
    }

    [Fact]
    public void Rows_below_low_and_warn_are_not_counted_even_if_handed_in()
    {
        var counts = new AlertCounts(
            new[]
            {
                Item("c", AuditSeverity.Critical),
                Item("i", AuditSeverity.Info),
                Item("w", AuditSeverity.Warn),
                Item("u", AuditSeverity.Unknown),
                Item("l", AuditSeverity.Low),
            },
            hasMore: false);

        Assert.Equal(2, counts.Total);
        Assert.Equal(new[] { "c", "l" }, counts.Newest.Select(i => i.Id));
    }

    [Fact]
    public void The_breakdown_is_case_insensitive_and_keeps_unattributed_rows_under_the_empty_key()
    {
        var counts = new AlertCounts(
            new[]
            {
                Item("a", AuditSeverity.High, "ClaudeCode"),
                Item("b", AuditSeverity.High, "claudecode"),
                Item("c", AuditSeverity.Low, null),
                Item("d", AuditSeverity.Low, "  "),
            },
            hasMore: false);

        Assert.Equal(2, counts.ByConnector["claudecode"].High);
        Assert.Equal(2, counts.ByConnector[string.Empty].Low);
        Assert.Equal(2, counts.ByConnector.Count);
    }

    [Fact]
    public void Scoping_sums_the_connectors_the_predicate_allows_and_offers_unattributed_rows_as_null()
    {
        var counts = new AlertCounts(
            new[]
            {
                Item("a", AuditSeverity.High, "claudecode"),
                Item("b", AuditSeverity.Critical, "codex"),
                Item("c", AuditSeverity.Low, null),
            },
            hasMore: false);

        Assert.Equal(new SeverityTally(0, 1, 0, 0), counts.TallyFor(c => string.Equals(c, "claudecode", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(new SeverityTally(1, 0, 0, 0), counts.TallyFor(c => c == "codex"));
        Assert.Equal(new SeverityTally(0, 0, 0, 1), counts.TallyFor(c => c is null));
        Assert.Equal(counts.Tally, counts.TallyFor(_ => true));
        Assert.Equal(SeverityTally.Zero, counts.TallyFor(_ => false));
    }

    [Fact]
    public void Newest_keeps_the_first_rows_only_and_zero_keeps_none()
    {
        var window = Enumerable.Range(0, 10).Select(i => Item("r" + i, AuditSeverity.High)).ToList();

        Assert.Equal(3, new AlertCounts(window, false, newestLimit: 3).Newest.Count);
        Assert.Equal("r0", new AlertCounts(window, false, newestLimit: 3).Newest[0].Id);
        Assert.Empty(new AlertCounts(window, false, newestLimit: 0).Newest);
        Assert.Equal(10, new AlertCounts(window, false, newestLimit: 0).Total);
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new AlertCounts(window, false, newestLimit: -1));
    }

    [Fact]
    public void Same_as_is_true_for_equal_counts_and_false_for_any_visible_difference()
    {
        AlertCounts Make(params AlertQueueItem[] items) => new(items, hasMore: false);
        var a = Make(Item("x", AuditSeverity.High), Item("y", AuditSeverity.Low));

        Assert.True(a.SameAs(Make(Item("x", AuditSeverity.High), Item("y", AuditSeverity.Low))));
        Assert.True(a.SameAs(a));
        Assert.False(a.SameAs(null));

        // A different count, a different severity, a different connector, a different "more", a different source.
        Assert.False(a.SameAs(Make(Item("x", AuditSeverity.High))));
        Assert.False(a.SameAs(Make(Item("x", AuditSeverity.High), Item("y", AuditSeverity.Medium))));
        Assert.False(a.SameAs(Make(Item("x", AuditSeverity.High, "codex"), Item("y", AuditSeverity.Low))));
        Assert.False(a.SameAs(new AlertCounts(new[] { Item("x", AuditSeverity.High), Item("y", AuditSeverity.Low) }, hasMore: true)));
        Assert.False(a.SameAs(new AlertCounts(new[] { Item("x", AuditSeverity.High), Item("y", AuditSeverity.Low) }, false, source: AlertCountsSource.Gateway)));
    }

    [Fact]
    public void A_new_newest_row_is_a_change_even_when_every_count_stays_put()
    {
        // A window at its cap: a new finding displaces the oldest, the tallies do not move, but the list does.
        var before = new AlertCounts(new[] { Item("new-1", AuditSeverity.High), Item("old", AuditSeverity.High) }, hasMore: true);
        var after = new AlertCounts(new[] { Item("new-2", AuditSeverity.High), Item("new-1", AuditSeverity.High) }, hasMore: true);

        Assert.Equal(before.Tally, after.Tally);
        Assert.False(before.SameAs(after));
    }

    // ---- The gateway fallback ----

    private static GatewayAlert Alert(string id, string? severity, string? action = "scan-finding", int minute = 0, string? connector = null)
    {
        var json = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["timestamp"] = Base.AddMinutes(minute),
            ["action"] = action,
            ["target"] = "/some/target",
            ["severity"] = severity,
        };
        if (connector is not null)
        {
            json["connector"] = connector;
        }

        return JsonSerializer.Deserialize<GatewayAlert>(JsonSerializer.Serialize(json))!;
    }

    [Fact]
    public void The_gateway_list_is_held_to_the_same_rules_as_the_queue()
    {
        var alerts = new[]
        {
            Alert("c", "CRITICAL", minute: 5, connector: "claudecode"),
            Alert("h", "high", minute: 4),
            Alert("w", "WARN", minute: 3),
            Alert("i", "INFO", minute: 2),
            Alert("d", "HIGH", action: "dismiss-alert", minute: 1),
            Alert("m", "MEDIUM", minute: 0),
            Alert("n", null, minute: 6),
        };

        var counts = AlertCounts.FromGateway(alerts, requestedLimit: 25);

        Assert.Equal(AlertCountsSource.Gateway, counts.Source);
        Assert.Equal(new SeverityTally(1, 1, 1, 0), counts.Tally);
        Assert.Equal(new[] { "c", "h", "m" }, counts.Newest.Select(i => i.Id));
        Assert.Equal("claudecode", counts.Newest[0].Connector);
        Assert.Null(counts.Newest[1].Connector);
        Assert.False(counts.HasMore);
    }

    [Theory]
    [InlineData(2, 3, true)]
    [InlineData(3, 3, true)]
    [InlineData(4, 3, false)]
    public void More_is_reported_when_the_gateway_list_filled_the_page_it_was_asked_for(int requestedLimit, int listed, bool expectedMore)
    {
        var alerts = Enumerable.Range(0, listed).Select(i => Alert("a" + i, "HIGH", minute: i)).ToArray();

        Assert.Equal(expectedMore, AlertCounts.FromGateway(alerts, requestedLimit).HasMore);
    }

    [Fact]
    public void The_gateway_list_is_ordered_newest_first_whatever_order_it_arrived_in()
    {
        var alerts = new[] { Alert("old", "HIGH", minute: 1), Alert("new", "HIGH", minute: 9) };

        Assert.Equal(new[] { "new", "old" }, AlertCounts.FromGateway(alerts, 25).Newest.Select(i => i.Id));
    }

    [Fact]
    public void An_empty_gateway_list_is_empty_counts()
    {
        var counts = AlertCounts.FromGateway(Array.Empty<GatewayAlert>(), 25);

        Assert.Equal(0, counts.Total);
        Assert.False(counts.HasMore);
    }

    [Fact]
    public void Text_shows_the_total_with_a_plus_when_the_window_was_full()
    {
        var counts = new AlertCounts(new[] { Item("a", AuditSeverity.Critical), Item("b", AuditSeverity.Low) }, hasMore: true);

        Assert.Equal("2+ unacknowledged (C1 H0 M0 L1, Database)", counts.ToString());
    }
}
