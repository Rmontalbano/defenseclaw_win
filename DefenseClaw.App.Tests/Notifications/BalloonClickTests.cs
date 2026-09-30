using System.Reflection;
using System.Windows;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.Core.Audit;
using H.NotifyIcon;
using H.NotifyIcon.Core;

namespace DefenseClaw.App.Tests.Notifications;

/// <summary>
/// A click on a finding toast opens the dashboard on Alerts, narrowed to what the toast announced. The balloon is a native
/// notification, so the one thing the library reports is "the balloon was clicked" (<c>TrayBalloonTipClicked</c>): whichever
/// toast was shown last is the one the click belongs to.
/// </summary>
public sealed class BalloonClickTargetTests
{
    private static NavigationRequest Alerts(AuditSeverity floor) => new("alerts", new AlertsFilter(floor));

    [Fact]
    public void A_click_asks_the_shell_for_the_armed_target_once()
    {
        var target = new BalloonClickTarget();
        var inbox = new ShellNavigation();
        var seen = new List<NavigationRequest>();
        inbox.Requested += (_, e) => seen.Add(e.Request);
        target.Arm(Alerts(AuditSeverity.Critical));

        Assert.True(target.Click(inbox));
        Assert.False(target.Click(inbox));

        var request = Assert.Single(seen);
        Assert.Equal("alerts", request.PanelId);
        Assert.Equal(new AlertsFilter(AuditSeverity.Critical), request.Payload);
        Assert.Equal(request, inbox.Pending);
    }

    [Fact]
    public void A_click_with_nothing_armed_goes_nowhere()
    {
        var target = new BalloonClickTarget();
        var inbox = new ShellNavigation();

        Assert.False(target.Click(inbox));
        Assert.Null(inbox.Pending);
    }

    [Fact]
    public void The_toast_shown_last_owns_the_click_and_a_toast_that_goes_nowhere_takes_the_earlier_ones_target_away()
    {
        var target = new BalloonClickTarget();
        var inbox = new ShellNavigation();

        target.Arm(Alerts(AuditSeverity.Critical));
        target.Arm(Alerts(AuditSeverity.High));
        Assert.True(target.Click(inbox));
        Assert.Equal(new AlertsFilter(AuditSeverity.High), inbox.Pending!.Payload);

        inbox.TryTake("alerts");
        target.Arm(Alerts(AuditSeverity.Critical));
        target.Arm(null);
        Assert.False(target.IsArmed);
        Assert.False(target.Click(inbox));
        Assert.Null(inbox.Pending);
    }

    [Fact]
    public void A_finding_toasts_click_target_is_alerts_on_the_severity_it_announced()
    {
        var toast = new AlertToast("CRITICAL finding", "Target: x", AuditSeverity.Critical, ToastLevel.Error);
        var target = new BalloonClickTarget();
        var inbox = new ShellNavigation();

        target.Arm(new NavigationRequest("alerts", toast.Filter));
        _ = target.Click(inbox);

        Assert.Equal(new AlertsFilter(AuditSeverity.Critical), inbox.Pending!.Payload);
    }
}

/// <summary>
/// Pins what the tray relies on in the pinned H.NotifyIcon.Wpf 2.3.2: the native balloon-click message
/// (<c>MouseEvent.BalloonToolTipClicked</c>, what the shell sends when the user clicks a notification) is raised by the library as
/// <see cref="TaskbarIcon.TrayBalloonTipClicked"/>. Driven through the library's own handler with the message it receives, since a
/// real click needs a real notification; if an upgrade renames or rewires this, the test says so before a click silently stops working.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class TrayBalloonEventTests
{
    [Fact]
    public void The_librarys_balloon_click_message_is_raised_as_TrayBalloonTipClicked()
    {
        UiThread.Run(() =>
        {
            // Never created in the notification area (no ForceCreate): only its event plumbing is exercised.
            using var icon = new TaskbarIcon();
            var clicks = 0;
            icon.TrayBalloonTipClicked += (_, _) => clicks++;

            var handler = typeof(TaskbarIcon).GetMethod("OnMouseEvent", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                ?? throw new MissingMethodException("TaskbarIcon.OnMouseEvent: the library changed; revisit how balloon clicks reach the tray.");
            var args = Activator.CreateInstance(
                typeof(MessageWindow.MouseEventReceivedEventArgs),
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                args: new object[] { MouseEvent.BalloonToolTipClicked, default(System.Drawing.Point) },
                culture: null)!;

            _ = handler.Invoke(icon, new[] { icon, args });

            Assert.Equal(1, clicks);

            // And an ordinary left click on the icon is not a balloon click.
            var other = Activator.CreateInstance(
                typeof(MessageWindow.MouseEventReceivedEventArgs),
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                args: new object[] { MouseEvent.MouseMove, default(System.Drawing.Point) },
                culture: null)!;
            _ = handler.Invoke(icon, new[] { icon, other });
            Assert.Equal(1, clicks);
        });
    }
}
