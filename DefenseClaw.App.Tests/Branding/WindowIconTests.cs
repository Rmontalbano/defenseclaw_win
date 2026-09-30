using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;

namespace DefenseClaw.App.Tests.Branding;

/// <summary>
/// The window icon end to end: a real (never shown) window handed <see cref="ShieldIconFactory.CreateWindowIcon"/>
/// gets both Win32 icons from it, and each is one of the frames drawn for its size rather than a bitmap WPF
/// rescaled — which is the whole point of handing WPF an icon decoder instead of one big image.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class WindowIconTests
{
    private const int WmGetIcon = 0x007F;
    private const int IconSmall = 0;
    private const int IconBig = 1;

    [Fact]
    public void A_window_given_the_icon_gets_title_bar_and_taskbar_icons_drawn_for_their_size()
    {
        UiThread.Run(() =>
        {
            var window = new Window { Icon = ShieldIconFactory.CreateWindowIcon(), ShowInTaskbar = false };
            try
            {
                var handle = new WindowInteropHelper(window).EnsureHandle();

                var small = IconWidth(SendMessage(handle, WmGetIcon, IconSmall, IntPtr.Zero));
                var big = IconWidth(SendMessage(handle, WmGetIcon, IconBig, IntPtr.Zero));

                Assert.Contains(small, ShieldArtwork.WindowIconSizes);
                Assert.Contains(big, ShieldArtwork.WindowIconSizes);
                Assert.True(small < big, $"small icon {small} px, large icon {big} px");
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static int IconWidth(IntPtr icon)
    {
        Assert.NotEqual(IntPtr.Zero, icon);
        using var managed = System.Drawing.Icon.FromHandle(icon);
        return managed.Width;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr window, int message, int wParam, IntPtr lParam);
}
