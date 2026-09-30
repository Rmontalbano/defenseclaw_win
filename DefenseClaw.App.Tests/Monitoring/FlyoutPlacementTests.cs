using DefenseClaw.App.Views;

namespace DefenseClaw.App.Tests.Monitoring;

/// <summary>
/// The tray flyout opens where the cursor is, clamped to the work area of the monitor the cursor is on. It used
/// to clamp to the primary monitor's, so a taskbar on a second screen opened it on the first.
/// </summary>
public class FlyoutPlacementTests
{
    private const int Width = 360;
    private const int Height = 400;

    /// <summary>A primary 1920x1080 screen with a 40 px taskbar along the bottom.</summary>
    private static readonly PixelRect Primary = new(0, 0, 1920, 1040);

    /// <summary>A second screen to its right, same size, same taskbar.</summary>
    private static readonly PixelRect Right = new(1920, 0, 3840, 1040);

    /// <summary>A second screen to its left: negative coordinates.</summary>
    private static readonly PixelRect Left = new(-1920, 0, 0, 1040);

    private static void AssertInside(PixelRect work, (int X, int Y) at)
    {
        Assert.InRange(at.X, work.Left, work.Right - Width);
        Assert.InRange(at.Y, work.Top, work.Bottom - Height);
    }

    [Fact]
    public void A_taskbar_along_the_bottom_opens_the_flyout_upwards_centred_on_the_cursor()
    {
        var at = FlyoutPlacement.Compute(1000, 1050, Primary, Width, Height);

        Assert.Equal((1000 - (Width / 2), 640), at);
    }

    [Fact]
    public void A_taskbar_along_the_top_opens_it_downwards()
    {
        var at = FlyoutPlacement.Compute(1000, 10, Primary, Width, Height);

        Assert.Equal((1000 - (Width / 2), 10), at);
    }

    [Fact]
    public void A_tray_icon_at_the_corner_is_clamped_inside_the_work_area()
    {
        var at = FlyoutPlacement.Compute(1915, 1050, Primary, Width, Height);

        Assert.Equal((Primary.Right - Width, 640), at);
        AssertInside(Primary, at);
    }

    [Fact]
    public void A_click_on_the_second_monitor_stays_on_the_second_monitor()
    {
        // The cursor at the bottom right of the screen to the right of the primary one.
        var at = FlyoutPlacement.Compute(3830, 1050, Right, Width, Height);

        Assert.Equal((Right.Right - Width, 640), at);
        AssertInside(Right, at);
        Assert.True(at.X >= Primary.Right, "the flyout landed on the primary monitor");
    }

    [Fact]
    public void A_click_on_a_monitor_to_the_left_of_the_primary_keeps_its_negative_coordinates()
    {
        var at = FlyoutPlacement.Compute(-100, 1050, Left, Width, Height);

        Assert.Equal((0 - Width, 640), at);
        AssertInside(Left, at);
        Assert.True(at.X < 0, "the flyout was pulled onto the primary monitor");
    }

    [Fact]
    public void A_work_area_smaller_than_the_flyout_pins_it_to_the_top_left_instead_of_throwing()
    {
        var tiny = new PixelRect(100, 100, 200, 200);

        Assert.Equal((100, 100), FlyoutPlacement.Compute(150, 190, tiny, Width, Height));
    }

    [Fact]
    public void The_monitor_under_a_point_reports_its_work_area()
    {
        // Any desktop has a monitor at or nearest to (0, 0); the point is that Win32 answers with geometry.
        var monitor = FlyoutPlacement.MonitorAt(0, 0);

        Assert.NotNull(monitor);
        Assert.True(monitor!.Value.WorkArea.Width > 0);
        Assert.True(monitor.Value.WorkArea.Height > 0);
    }
}
