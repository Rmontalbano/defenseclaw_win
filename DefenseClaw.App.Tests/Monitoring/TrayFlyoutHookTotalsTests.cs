using System.Globalization;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;

namespace DefenseClaw.App.Tests.Monitoring;

/// <summary>
/// CUST-278: the tray flyout shows the Overview's all-time hook totals, from the one reader the app shares, and a flyout nobody opened has not
/// started that reader's catch-up. Synthetic data only (<see cref="FlyoutScene"/>).
/// </summary>
[Collection(UiCollection.Name)]
public sealed class TrayFlyoutHookTotalsTests
{
    private static TrayFlyoutViewModel Create(FlyoutScene scene) =>
        new(scene.Services, () => { }, () => { }, metricsReader: null, timeProvider: new ManualClock());

    [Fact]
    public void The_flyout_and_the_overview_read_through_the_one_shared_reader()
    {
        using var scene = new FlyoutScene();

        UiThread.Run(() =>
        {
            using var flyout = Create(scene);
            var overview = new OverviewPanelViewModel(scene.Services);

            Assert.Same(scene.Services.HookTotals, flyout.MetricsReader);
            Assert.Same(scene.Services.HookTotals, overview.MetricsReader);
        });
    }

    [Fact]
    public void A_flyout_that_was_never_opened_has_not_started_the_catch_up()
    {
        using var scene = new FlyoutScene();
        scene.Populate();

        UiThread.Run(() =>
        {
            using var flyout = Create(scene);
            flyout.Apply(FlyoutScene.RunningSnapshot());

            Assert.Equal(0, scene.Services.HookTotals.ReadCount);
            Assert.Equal("—", flyout.Metrics[0].Value);
        });
    }

    [Fact]
    public void Hook_calls_and_blocks_are_all_time_not_the_newest_500_rows()
    {
        using var scene = new FlyoutScene();

        // Four enforced blocks, then 600 allows after them: the blocks are older than the newest 500 rows.
        for (var i = 0; i < 4; i++)
        {
            scene.AddHookCall(FlyoutScene.Now.AddHours(-3).AddSeconds(i), "block");
        }

        for (var i = 0; i < 600; i++)
        {
            scene.AddHookCall(FlyoutScene.Now.AddSeconds(-i), "allow");
        }

        var flyout = UiThread.Run(() => Create(scene));
        try
        {
            UiThread.Run(() => flyout.SetVisible(true));
            UiThread.WaitFor(() => flyout.Metrics[0].Value != "—", "the flyout to read the totals");

            UiThread.Run(() =>
            {
                Assert.Equal(604.ToString("N0", CultureInfo.CurrentCulture), flyout.Metrics[0].Value);
                Assert.Equal("4", flyout.Metrics[1].Value);
            });
        }
        finally
        {
            UiThread.Run(flyout.Dispose);
        }
    }

    [Fact]
    public async Task The_flyout_shows_the_same_hook_calls_and_blocks_as_the_overview()
    {
        using var scene = new FlyoutScene();
        scene.Populate(hookCalls: 30, blocks: 5, other: 10, findings: 2);

        var overview = UiThread.Run(() => new OverviewPanelViewModel(scene.Services));
        await overview.RefreshMetricsAsync(force: true, CancellationToken.None);

        var flyout = UiThread.Run(() => Create(scene));
        try
        {
            UiThread.Run(() => flyout.SetVisible(true));
            UiThread.WaitFor(() => flyout.Metrics[0].Value != "—", "the flyout to read the totals");

            UiThread.Run(() =>
            {
                Assert.Equal(overview.HookCallsTile.Value, flyout.Metrics[0].Value);
                Assert.Equal(overview.BlocksTile.Value, flyout.Metrics[1].Value);
                Assert.Equal("5", flyout.Metrics[1].Value);
            });
        }
        finally
        {
            UiThread.Run(flyout.Dispose);
        }
    }
}
