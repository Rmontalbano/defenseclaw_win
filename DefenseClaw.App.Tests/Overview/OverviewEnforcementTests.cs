using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Audit;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.App.Tests.Overview;

/// <summary>
/// The Enforcement card (CUST-205): four tiles, each a button into the panel its number comes from. The numbers are the TUI's (persisted all-time totals,
/// <see cref="ConnectorHookTotalsReader"/>) and the one unacknowledged-findings count (<see cref="AlertCountsService"/>); scoped to
/// a connector they narrow to it and say what the fleet has. Synthetic data only (<see cref="OverviewScene"/>).
/// </summary>
[Collection(UiCollection.Name)]
public sealed class OverviewEnforcementTests : IDisposable
{
    private readonly OverviewScene _scene = OverviewScene.Create();

    public void Dispose() => _scene.Dispose();

    private OverviewPanelViewModel Panel() => new(_scene.Services);

    // ---- The four tiles ----

    [Fact]
    public void There_are_four_tiles_in_reading_order_each_with_a_spoken_name_and_a_hint()
    {
        var vm = Panel();

        var expected = new[] { "Hook Calls", "Blocks", "Findings", "Guardrail" };
        Assert.Equal(4, vm.EnforcementCards.Count);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.StartsWith(expected[i], vm.EnforcementCards[i].Title, StringComparison.Ordinal);
        }

        Assert.Same(vm.HookCallsTile, vm.EnforcementCards[0]);
        Assert.Same(vm.GuardrailTile, vm.EnforcementCards[3]);

        Assert.All(vm.EnforcementCards, tile =>
        {
            Assert.False(string.IsNullOrWhiteSpace(tile.AutomationName));
            Assert.False(string.IsNullOrWhiteSpace(tile.AutomationHint));
            Assert.False(string.IsNullOrWhiteSpace(tile.ToolTipText));
            Assert.NotNull(tile.Command);

            // What a screen reader announces carries the number, not just the caption.
            Assert.Contains(tile.Value, tile.AutomationName, StringComparison.Ordinal);
            Assert.Contains(tile.Title, tile.AutomationName, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Each_tile_opens_the_panel_its_number_comes_from()
    {
        var vm = Panel();
        var navigation = _scene.Services.Navigation;

        vm.HookCallsTile.Command.Execute(null);
        Assert.Equal(new NavigationRequest("logs", new LogsPreset("hooks")), navigation.Pending);

        vm.BlocksTile.Command.Execute(null);
        Assert.Equal(new NavigationRequest("audit", new AuditPreset("blocks")), navigation.Pending);

        vm.FindingsTile.Command.Execute(null);
        Assert.Equal("alerts", navigation.Pending!.PanelId);
        Assert.Equal(new AlertsFilter(Kind: AlertsFilter.KindAll), navigation.Pending.Payload);

        vm.GuardrailTile.Command.Execute(null);
        Assert.Equal(new NavigationRequest("setup"), navigation.Pending);
    }

    [Fact]
    public void Before_anything_is_read_the_counts_say_so_instead_of_claiming_zero()
    {
        var vm = Panel();

        Assert.Equal("—", vm.HookCallsTile.Value);
        Assert.Equal("—", vm.BlocksTile.Value);
        Assert.Equal("—", vm.FindingsTile.Value);
        Assert.Equal(string.Empty, vm.EnforcementUpdatedText);
    }

    // ---- Hook calls and blocks: all-time totals, the TUI's (CUST-258) ----

    [Fact]
    public async Task Hook_calls_and_blocks_are_the_persisted_totals_with_the_TUIs_captions()
    {
        var vm = Panel();
        _scene.Publish(OverviewScene.Snapshot());
        vm.Apply(OverviewScene.Snapshot());

        await vm.RefreshMetricsAsync(force: true, CancellationToken.None);

        Assert.Equal("10", vm.HookCallsTile.Value);
        Assert.Equal("1", vm.BlocksTile.Value);
        Assert.Equal("Hook Calls (2 connectors)", vm.HookCallsTile.Title);
        Assert.Equal("recent a9 w0 b1", vm.HookCallsTile.Caption);
        Assert.Equal("top: (unknown) ×1", vm.BlocksTile.Caption);

        // Blue for the count that is neither good nor bad news, red once something was blocked.
        Assert.Equal("Accent", vm.HookCallsTile.ToneKey);
        Assert.Equal("Bad", vm.BlocksTile.ToneKey);
        Assert.Equal("Updated just now", vm.EnforcementUpdatedText);
    }

    [Fact]
    public async Task A_database_with_no_blocks_reads_grey_and_zero()
    {
        using var empty = OverviewScene.Create(seedAudit: false);
        var vm = new OverviewPanelViewModel(empty.Services);

        await vm.RefreshMetricsAsync(force: true, CancellationToken.None);

        Assert.Equal("0", vm.HookCallsTile.Value);
        Assert.Equal("0", vm.BlocksTile.Value);
        Assert.Equal("No audit database yet", vm.HookCallsTile.Caption);
        Assert.Equal("Neutral", vm.BlocksTile.ToneKey);
    }

    [Fact]
    public async Task A_read_that_fails_shows_a_dash_and_the_reason_not_the_last_number()
    {
        var vm = Panel();
        await vm.RefreshMetricsAsync(force: true, CancellationToken.None);
        Assert.Equal("10", vm.HookCallsTile.Value);

        // The file stops being a database: the next read fails, and the tile must not keep claiming 10.
        SqliteConnection.ClearAllPools();
        File.WriteAllText(_scene.Temp.File("audit.db"), "this is not a sqlite file");
        await vm.RefreshMetricsAsync(force: true, CancellationToken.None);

        Assert.Equal("—", vm.HookCallsTile.Value);
        Assert.Equal("audit.db could not be read", vm.HookCallsTile.Caption);
        Assert.Equal("Neutral", vm.HookCallsTile.ToneKey);
    }

    [Fact]
    public async Task Scoped_to_a_connector_the_window_narrows_to_it_and_the_fleet_total_is_the_caption()
    {
        var vm = Panel();
        _scene.Publish(OverviewScene.Snapshot());
        vm.Apply(OverviewScene.Snapshot());
        await vm.RefreshMetricsAsync(force: true, CancellationToken.None);

        Assert.True(_scene.Services.ConnectorScope.Set("hermes"));
        vm.RenderEnforcementCards();

        Assert.Equal("2", vm.HookCallsTile.Value);
        Assert.Equal("Hook Calls (hermes)", vm.HookCallsTile.Title);
        Assert.Equal("recent a2 w0 b0 · fleet 10", vm.HookCallsTile.Caption);
        Assert.Equal("0", vm.BlocksTile.Value);
        Assert.Equal("Neutral", vm.BlocksTile.ToneKey);
        Assert.Equal("Blocks (hermes)", vm.BlocksTile.Title);
        Assert.Equal("no blocks yet · fleet 1", vm.BlocksTile.Caption);

        Assert.True(_scene.Services.ConnectorScope.Set("claudecode"));
        vm.RenderEnforcementCards();
        Assert.Equal("8", vm.HookCallsTile.Value);
        Assert.Equal("1", vm.BlocksTile.Value);
    }

    [Fact]
    public async Task The_totals_go_past_the_old_500_row_window()
    {
        // 1,200 hook rows on top of the scene's ten: the old tile would have stopped at 500.
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _scene.Temp.File("audit.db"), Pooling = false }.ToString()))
        {
            connection.Open();
            using var transaction = connection.BeginTransaction();
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO audit_events (id, timestamp, action, target, actor, details, connector) VALUES ($id, $ts, 'connector-hook', '', 'x', 'action=allow mode=observe', 'claudecode')";
            var id = insert.Parameters.Add("$id", SqliteType.Text);
            var ts = insert.Parameters.Add("$ts", SqliteType.Text);
            for (var i = 0; i < 1200; i++)
            {
                id.Value = "bulk-" + i;
                ts.Value = "2026-01-01T00:00:00." + i.ToString("D7", System.Globalization.CultureInfo.InvariantCulture) + "Z";
                _ = insert.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        SqliteConnection.ClearAllPools();
        var vm = Panel();
        await vm.RefreshMetricsAsync(force: true, CancellationToken.None);

        Assert.Equal(1210.ToString("N0", System.Globalization.CultureInfo.CurrentCulture), vm.HookCallsTile.Value);
        Assert.StartsWith("recent a499", vm.HookCallsTile.Caption, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_window_is_read_when_due_not_on_every_poll()
    {
        var vm = Panel();

        await vm.RefreshMetricsAsync(force: false, CancellationToken.None);
        await vm.RefreshMetricsAsync(force: false, CancellationToken.None);
        await vm.RefreshMetricsAsync(force: false, CancellationToken.None);
        Assert.Equal(1, vm.MetricsReader.ReadCount);

        await vm.RefreshMetricsAsync(force: true, CancellationToken.None);
        Assert.Equal(2, vm.MetricsReader.ReadCount);
    }

    [Fact]
    public async Task A_cancelled_read_changes_nothing_and_the_next_one_is_not_held_back()
    {
        var vm = Panel();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await vm.RefreshMetricsAsync(force: true, cancelled.Token);

        Assert.Equal("—", vm.HookCallsTile.Value);

        // The gate was reopened: a read that never happened is not "done this quarter minute".
        await vm.RefreshMetricsAsync(force: false, CancellationToken.None);
        Assert.Equal("10", vm.HookCallsTile.Value);
    }

    // ---- Findings: the one number ----

    [Fact]
    public async Task Findings_are_the_unacknowledged_count_the_badge_and_the_tray_show()
    {
        var vm = Panel();
        _scene.Publish(OverviewScene.Snapshot());
        vm.Apply(OverviewScene.Snapshot());
        Assert.Equal("—", vm.FindingsTile.Value);

        await _scene.Services.AlertCounts.RefreshAsync();
        vm.RenderEnforcementCards();

        Assert.Equal("2", vm.FindingsTile.Value);
        Assert.Equal("High", vm.FindingsTile.ToneKey);
        Assert.Equal("C0 H1 M1 L0", vm.FindingsTile.Caption);

        _scene.Services.ConnectorScope.Set("hermes");
        vm.RenderEnforcementCards();
        Assert.Equal("1", vm.FindingsTile.Value);
        Assert.Equal("Findings (hermes)", vm.FindingsTile.Title);
        Assert.Equal("C0 H0 M1 L0 · fleet 2", vm.FindingsTile.Caption);
    }

    // ---- Guardrail ----

    [Fact]
    public void The_guardrail_tile_is_on_or_off_as_config_yaml_says_with_the_mode_in_its_title()
    {
        var vm = Panel();
        vm.Apply(OverviewScene.Snapshot());

        Assert.Equal("ON", vm.GuardrailTile.Value);
        Assert.Equal("Ok", vm.GuardrailTile.ToneKey);
        Assert.Equal("Guardrail - observe", vm.GuardrailTile.Title);
        Assert.Equal("Current configuration", vm.GuardrailTile.Caption);

        using var off = OverviewScene.Create(seedAudit: false);
        File.WriteAllText(off.Temp.File("config.yaml"), "gateway:\n  api_port: " + OverviewScene.Port + "\nguardrail:\n  enabled: false\n");
        off.Services.ReloadConfig();
        var offVm = new OverviewPanelViewModel(off.Services);

        Assert.Equal("OFF", offVm.GuardrailTile.Value);
        Assert.Equal("Neutral", offVm.GuardrailTile.ToneKey);
    }

    // ---- Activation: nothing runs for a panel nobody is looking at ----

    [Fact]
    public void While_the_panel_is_away_nothing_is_subscribed_and_no_query_runs()
    {
        var vm = Panel();
        var monitor = _scene.Services.Monitor;
        Assert.Equal(0, monitor.PollCompletedSubscriberCount);
        Assert.False(_scene.Services.AlertCounts.IsRunning);

        // Away: the roster and the scope move, the panel does not notice and does not read.
        _scene.Publish(OverviewScene.Snapshot());
        _scene.Services.ConnectorScope.Set("hermes");
        Assert.Equal("Scanners", vm.ScannersTitle);
        Assert.Equal(0, vm.MetricsReader.ReadCount);
        Assert.Equal(0, vm.HourlyReader.ReadCount);
    }

    [Fact]
    public void On_screen_it_follows_the_scope_and_the_poll_and_on_leaving_it_lets_go_of_both()
    {
        // On the UI thread, as the shell activates a panel: the alert counts call back on it.
        UiThread.Run(() =>
        {
            var vm = Panel();
            _scene.Publish(OverviewScene.Snapshot());
            var monitor = _scene.Services.Monitor;

            vm.SetActive(true);
            Assert.Equal(1, monitor.PollCompletedSubscriberCount);
            Assert.True(_scene.Services.AlertCounts.IsRunning);

            _scene.Services.ConnectorScope.Set("hermes");
            Assert.Equal("Scanners · hermes", vm.ScannersTitle);
            Assert.Equal("Enforcement · hermes", vm.EnforcementTitle);
            Assert.Equal("Configuration · hermes", vm.ConfigurationTitle);

            vm.SetActive(false);
            Assert.Equal(0, monitor.PollCompletedSubscriberCount);
            Assert.False(_scene.Services.AlertCounts.IsRunning);

            // Away again: a scope change is not seen until the next visit catches up.
            _scene.Services.ConnectorScope.Set(null);
            Assert.Equal("Scanners · hermes", vm.ScannersTitle);
            vm.SetActive(true);
            Assert.Equal("Scanners", vm.ScannersTitle);
            vm.SetActive(false);
        });

        // What the visits started has been stopped by leaving; nothing is left reading for a disposed scene.
        Assert.True(SpinWait.SpinUntil(() => !_scene.Services.AlertCounts.IsRunning, TimeSpan.FromSeconds(5)));
    }
}
