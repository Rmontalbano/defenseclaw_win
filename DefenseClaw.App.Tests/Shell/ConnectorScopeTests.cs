using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.Core.Audit;

namespace DefenseClaw.App.Tests.Shell;

/// <summary>The shared connector filter: the Mac's rules (All / one connector, exact match, unattributed rows hidden, never trapped).</summary>
public sealed class ConnectorScopeTests
{
    private static ConnectorScope WithRoster(params string[] connectors)
    {
        var scope = new ConnectorScope();
        scope.UpdateRoster(connectors);
        return scope;
    }

    // ------------------------------------------------------------------ All

    [Fact]
    public void A_new_scope_is_all_and_allows_everything_including_rows_with_no_connector()
    {
        var scope = WithRoster("claudecode", "codex");

        Assert.Null(scope.Current);
        Assert.False(scope.IsScoped);
        Assert.True(scope.Allows("claudecode"));
        Assert.True(scope.Allows("anything-at-all"));
        Assert.True(scope.Allows(null));
        Assert.True(scope.Allows(string.Empty));
    }

    // ------------------------------------------------------------------ Allows

    [Fact]
    public void A_scope_allows_only_an_exact_match_ignoring_case_and_padding()
    {
        var scope = WithRoster("claudecode", "codex");
        Assert.True(scope.Set("claudecode"));

        Assert.True(scope.Allows("claudecode"));
        Assert.True(scope.Allows("ClaudeCode"));
        Assert.True(scope.Allows("  claudecode\t"));
        Assert.False(scope.Allows("codex"));
        Assert.False(scope.Allows("claude"));
        Assert.False(scope.Allows("claudecode2"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_scope_hides_rows_that_belong_to_no_connector(string? connector)
    {
        var scope = WithRoster("claudecode", "codex");
        _ = scope.Set("codex");

        Assert.False(scope.Allows(connector));
    }

    [Fact]
    public void Allows_is_the_predicate_the_alert_counts_take()
    {
        var counts = new AlertCounts(
            new[]
            {
                new AlertQueueItem("a", AuditSeverity.High, "scan-finding", null, "claudecode", DateTimeOffset.UnixEpoch),
                new AlertQueueItem("b", AuditSeverity.Critical, "scan-finding", null, "codex", DateTimeOffset.UnixEpoch),
                new AlertQueueItem("c", AuditSeverity.Low, "scan-finding", null, null, DateTimeOffset.UnixEpoch),
            },
            hasMore: false);
        var scope = WithRoster("claudecode", "codex");

        Assert.Equal(3, counts.TallyFor(scope.Allows).Total);

        _ = scope.Set("codex");
        Assert.Equal(new SeverityTally(1, 0, 0, 0), counts.TallyFor(scope.Allows));

        _ = scope.Set("claudecode");
        Assert.Equal(new SeverityTally(0, 1, 0, 0), counts.TallyFor(scope.Allows));
    }

    // ------------------------------------------------------------------ Set

    [Fact]
    public void Set_keeps_the_rosters_spelling_and_null_or_blank_means_all()
    {
        var scope = WithRoster("ClaudeCode", "Codex");

        Assert.True(scope.Set("  claudecode "));
        Assert.Equal("ClaudeCode", scope.Current);
        Assert.True(scope.IsScoped);

        Assert.True(scope.Set(null));
        Assert.Null(scope.Current);

        _ = scope.Set("codex");
        Assert.True(scope.Set("   "));
        Assert.Null(scope.Current);
    }

    [Fact]
    public void A_connector_that_is_not_on_the_roster_cannot_be_chosen()
    {
        var scope = WithRoster("claudecode", "codex");
        _ = scope.Set("codex");
        var raised = 0;
        scope.Changed += (_, _) => raised++;

        Assert.False(scope.Set("openclaw"));

        Assert.Equal("codex", scope.Current);
        Assert.Equal(0, raised);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void With_one_connector_or_none_there_is_nothing_to_scope_to(int connectors)
    {
        var scope = WithRoster(new[] { "claudecode" }.Take(connectors).ToArray());

        Assert.False(scope.CanScope);
        Assert.False(scope.Set("claudecode"));
        Assert.Null(scope.Current);
        Assert.Null(scope.Cycle());
    }

    [Fact]
    public void Setting_the_scope_it_already_has_raises_nothing()
    {
        var scope = WithRoster("claudecode", "codex");
        _ = scope.Set("codex");
        var raised = 0;
        scope.Changed += (_, _) => raised++;

        Assert.True(scope.Set("CODEX"));
        Assert.True(scope.Set("codex"));

        Assert.Equal(0, raised);
    }

    // ------------------------------------------------------------------ Cycle

    [Fact]
    public void Cycle_steps_all_then_each_connector_in_roster_order_then_back_to_all()
    {
        var scope = WithRoster("claudecode", "codex", "openclaw");

        Assert.Equal("claudecode", scope.Cycle());
        Assert.Equal("codex", scope.Cycle());
        Assert.Equal("openclaw", scope.Cycle());
        Assert.Null(scope.Cycle());
        Assert.Null(scope.Current);
        Assert.Equal("claudecode", scope.Cycle());
    }

    [Fact]
    public void Cycle_from_a_scope_that_left_the_roster_starts_again_from_the_first_connector()
    {
        var scope = WithRoster("claudecode", "codex");
        _ = scope.Set("codex");

        // The roster changes without the scope being told yet (it is told by UpdateRoster, which resets it); Cycle copes regardless.
        scope.UpdateRoster(new[] { "claudecode", "openclaw" });
        Assert.Null(scope.Current);
        Assert.Equal("claudecode", scope.Cycle());
    }

    [Fact]
    public void Cycle_raises_changed_for_every_step()
    {
        var scope = WithRoster("a", "b");
        var raised = 0;
        scope.Changed += (_, _) => raised++;

        _ = scope.Cycle();
        _ = scope.Cycle();
        _ = scope.Cycle();

        Assert.Equal(3, raised);
    }

    // ------------------------------------------------------------------ the roster and the automatic reset

    [Fact]
    public void The_scope_resets_to_all_when_its_connector_leaves_the_roster()
    {
        var scope = WithRoster("claudecode", "codex", "openclaw");
        _ = scope.Set("codex");
        var raised = 0;
        scope.Changed += (_, _) => raised++;

        scope.UpdateRoster(new[] { "claudecode", "openclaw" });

        Assert.Null(scope.Current);
        Assert.True(scope.Allows("claudecode"));
        Assert.Equal(1, raised);
    }

    [Fact]
    public void The_scope_resets_to_all_when_the_roster_shrinks_to_one_connector()
    {
        var scope = WithRoster("claudecode", "codex");
        _ = scope.Set("claudecode");

        scope.UpdateRoster(new[] { "claudecode" });

        Assert.Null(scope.Current);
        Assert.False(scope.CanScope);
    }

    [Fact]
    public void The_scope_resets_to_all_when_the_roster_empties()
    {
        var scope = WithRoster("claudecode", "codex");
        _ = scope.Set("claudecode");

        scope.UpdateRoster(Array.Empty<string>());

        Assert.Null(scope.Current);
        Assert.Empty(scope.Connectors);
    }

    [Fact]
    public void The_scope_survives_a_roster_change_that_keeps_its_connector_and_follows_a_respelling()
    {
        var scope = WithRoster("claudecode", "codex");
        _ = scope.Set("codex");

        scope.UpdateRoster(new[] { "openclaw", "claudecode", "Codex" });

        Assert.Equal("Codex", scope.Current);
        Assert.Equal(new[] { "openclaw", "claudecode", "Codex" }, scope.Connectors);
        Assert.True(scope.Allows("codex"));
    }

    [Fact]
    public void A_roster_update_that_changes_nothing_raises_nothing()
    {
        var scope = WithRoster("claudecode", "codex");
        _ = scope.Set("codex");
        var raised = 0;
        scope.Changed += (_, _) => raised++;

        scope.UpdateRoster(new[] { "claudecode", "codex" });
        scope.UpdateRoster(new[] { " claudecode ", "codex", "CODEX", "", "  " });

        Assert.Equal(0, raised);
        Assert.Equal("codex", scope.Current);
    }

    [Fact]
    public void A_roster_change_raises_changed_so_a_chip_can_appear_and_disappear()
    {
        var scope = new ConnectorScope();
        var raised = 0;
        scope.Changed += (_, _) => raised++;

        scope.UpdateRoster(new[] { "claudecode" });
        Assert.False(scope.CanScope);

        scope.UpdateRoster(new[] { "claudecode", "codex" });
        Assert.True(scope.CanScope);

        Assert.Equal(2, raised);
    }

    [Fact]
    public void The_roster_is_trimmed_deduplicated_without_case_and_a_snapshot()
    {
        var scope = WithRoster("  claudecode ", "ClaudeCode", "", "codex");
        var roster = scope.Connectors;

        Assert.Equal(new[] { "claudecode", "codex" }, roster);

        scope.UpdateRoster(new[] { "other", "another" });
        Assert.Equal(new[] { "claudecode", "codex" }, roster);
    }

    // ------------------------------------------------------------------ following the monitor

    [Fact]
    public void It_reads_the_monitors_roster_now_and_follows_every_published_snapshot()
    {
        var source = new FakeSnapshotSource();
        source.Current = source.Current with { ActiveConnectors = new[] { "claudecode", "codex" } };

        using var scope = new ConnectorScope(source);
        Assert.Equal(new[] { "claudecode", "codex" }, scope.Connectors);
        Assert.Equal(1, source.StateChangedSubscribers);

        Assert.True(scope.Set("codex"));

        source.PublishConnectors("claudecode", "codex", "openclaw");
        Assert.Equal("codex", scope.Current);
        Assert.Equal(3, scope.Connectors.Count);

        // The connector disappears from ActiveConnectors: the scope resets by itself, and says so.
        var raised = 0;
        scope.Changed += (_, _) => raised++;
        source.PublishConnectors("claudecode", "openclaw");

        Assert.Null(scope.Current);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void Disposing_lets_go_of_the_monitor()
    {
        var source = new FakeSnapshotSource();
        var scope = new ConnectorScope(source);

        scope.Dispose();
        scope.Dispose();

        Assert.Equal(0, source.StateChangedSubscribers);
    }

    // ------------------------------------------------------------------ events and threads

    [Fact]
    public void A_throwing_subscriber_does_not_stop_the_change_or_the_others()
    {
        var scope = WithRoster("a", "b");
        var second = 0;
        scope.Changed += (_, _) => throw new InvalidOperationException("a subscriber that always throws");
        scope.Changed += (_, _) => second++;

        Assert.True(scope.Set("b"));

        Assert.Equal("b", scope.Current);
        Assert.Equal(1, second);
    }

    [Fact]
    public async Task Changed_is_raised_outside_the_lock_so_a_subscriber_can_call_back_in()
    {
        var scope = WithRoster("a", "b", "c");
        string? seenInside = null;
        scope.Changed += (_, _) =>
        {
            seenInside = scope.Current;
            _ = scope.Allows("a");
            _ = scope.Connectors;
        };

        var done = Task.Run(() => scope.Set("b"));

        _ = await done.WaitAsync(TimeSpan.FromSeconds(10));   // a TimeoutException here means Set deadlocked on a subscriber that read the scope
        Assert.Equal("b", seenInside);
    }

    [Fact]
    public async Task Concurrent_changes_leave_a_consistent_scope_and_never_throw()
    {
        var scope = WithRoster("a", "b", "c");
        var barrier = new Barrier(4);
        var workers = new[]
        {
            Task.Run(() => { barrier.SignalAndWait(); for (var i = 0; i < 500; i++) { _ = scope.Cycle(); } }),
            Task.Run(() => { barrier.SignalAndWait(); for (var i = 0; i < 500; i++) { _ = scope.Set(i % 2 == 0 ? "a" : null); } }),
            Task.Run(() => { barrier.SignalAndWait(); for (var i = 0; i < 500; i++) { scope.UpdateRoster(i % 3 == 0 ? new[] { "a" } : new[] { "a", "b", "c" }); } }),
            Task.Run(() => { barrier.SignalAndWait(); for (var i = 0; i < 2000; i++) { _ = scope.Allows("b"); _ = scope.Connectors; } }),
        };

        await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(30));

        var current = scope.Current;
        Assert.True(current is null || scope.Connectors.Contains(current, StringComparer.OrdinalIgnoreCase) || scope.Connectors.Count <= 1);
    }
}
