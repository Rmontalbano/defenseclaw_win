using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views;

namespace DefenseClaw.App.Tests.Monitoring;

/// <summary>
/// The tray flyout is built once at startup and lives for the whole process, but is on screen for
/// seconds. Its view-model may follow the volatile poll stream (<c>PollCompleted</c>, one dispatcher hop per
/// poll per subscriber) only while it is visible, or a hidden flyout defeats the idle optimisation for every
/// poll of the day.
/// </summary>
[Collection(UiCollection.Name)]
public class TrayFlyoutTests
{
    /// <summary>
    /// The config points at a listener that answers, not at a closed port: Windows retries a refused
    /// loopback connection for about two seconds, per poll.
    /// </summary>
    private static RawHttpListener Quiet() => new(_ => RawHttpListener.Response(200, "{}"));

    private static AppServices Create(TempDirectory temp, RawHttpListener listener)
    {
        _ = temp.WriteFile("config.yaml", $"config_version: 8\ngateway:\n  api_port: {listener.Port}\n");
        return AppServices.CreateIsolated(
            TestServices.IsolatedPaths(temp.Path),
            claudeSettingsPath: temp.File("claude-settings.json"));
    }

    [Fact]
    public void A_hidden_flyout_is_not_subscribed_to_the_poll_stream()
    {
        using var temp = new TempDirectory();
        using var listener = Quiet();
        using var services = Create(temp, listener);
        using var viewModel = new TrayFlyoutViewModel(services, () => { }, () => { });

        Assert.False(viewModel.IsTrackingPolls);
        Assert.Equal(0, services.Monitor.PollCompletedSubscriberCount);
    }

    [Fact]
    public void Showing_subscribes_and_hiding_unsubscribes()
    {
        using var temp = new TempDirectory();
        using var listener = Quiet();
        using var services = Create(temp, listener);
        using var viewModel = new TrayFlyoutViewModel(services, () => { }, () => { });

        viewModel.SetVisible(true);
        Assert.True(viewModel.IsTrackingPolls);
        Assert.Equal(1, services.Monitor.PollCompletedSubscriberCount);

        // Shown twice (a second tray click while it is up) must not stack subscriptions.
        viewModel.SetVisible(true);
        Assert.Equal(1, services.Monitor.PollCompletedSubscriberCount);

        viewModel.SetVisible(false);
        Assert.False(viewModel.IsTrackingPolls);
        Assert.Equal(0, services.Monitor.PollCompletedSubscriberCount);

        viewModel.SetVisible(false);
        Assert.Equal(0, services.Monitor.PollCompletedSubscriberCount);
    }

    [Fact]
    public async Task Showing_catches_up_with_the_polls_that_went_by_while_hidden()
    {
        using var temp = new TempDirectory();
        using var listener = Quiet();
        using var services = Create(temp, listener);
        using var viewModel = new TrayFlyoutViewModel(services, () => { }, () => { });
        Assert.Equal("never", viewModel.LastPolled);

        // The first poll is a material change (StateChanged), so even a hidden flyout renders it.
        var first = await services.Monitor.RefreshAsync();
        Assert.Equal(Format(first.PolledAt), viewModel.LastPolled);

        // The next one changes nothing but the time. Nothing is subscribed to tell the flyout, which is the
        // point; a second later it would show a stale "polled at" if showing did not re-read the snapshot.
        await Task.Delay(1100);
        var second = await services.Monitor.RefreshAsync();
        Assert.NotEqual(Format(first.PolledAt), Format(second.PolledAt));
        Assert.Equal(Format(first.PolledAt), viewModel.LastPolled);

        viewModel.SetVisible(true);

        Assert.Equal(Format(second.PolledAt), viewModel.LastPolled);
        Assert.Equal(services.Monitor.Current.StateLabel, viewModel.StateLabel);
    }

    private static string Format(DateTimeOffset polledAt) =>
        polledAt.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.CurrentCulture);

    [Fact]
    public void Disposing_drops_whatever_is_still_subscribed_and_ignores_later_visibility()
    {
        using var temp = new TempDirectory();
        using var listener = Quiet();
        using var services = Create(temp, listener);
        var viewModel = new TrayFlyoutViewModel(services, () => { }, () => { });
        viewModel.SetVisible(true);

        viewModel.Dispose();
        Assert.Equal(0, services.Monitor.PollCompletedSubscriberCount);

        viewModel.SetVisible(true);
        Assert.Equal(0, services.Monitor.PollCompletedSubscriberCount);
    }

    [Fact]
    public void The_window_drives_the_subscription_from_its_own_visibility()
    {
        using var temp = new TempDirectory();
        using var listener = Quiet();
        using var services = Create(temp, listener);

        UiThread.Run(() =>
        {
            using var viewModel = new TrayFlyoutViewModel(services, () => { }, () => { });
            var window = new TrayFlyoutWindow
            {
                DataContext = viewModel,
                ShowActivated = false,
                Left = -32000,
                Top = -32000,
            };

            try
            {
                Assert.False(viewModel.IsTrackingPolls);

                window.Show();
                Assert.True(viewModel.IsTrackingPolls);
                Assert.Equal(1, services.Monitor.PollCompletedSubscriberCount);

                window.Hide();
                Assert.False(viewModel.IsTrackingPolls);
                Assert.Equal(0, services.Monitor.PollCompletedSubscriberCount);

                // Escape and the focus-loss dismissal both end in Hide(); showing again picks it back up.
                window.Show();
                Assert.True(viewModel.IsTrackingPolls);
            }
            finally
            {
                window.ForceClose();
            }

            Assert.False(viewModel.IsTrackingPolls);
        });
    }
}
