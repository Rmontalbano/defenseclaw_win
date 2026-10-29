using System.Runtime.InteropServices;

namespace DefenseClaw.App.Views;

/// <summary>A rectangle in physical (device) pixels, as Win32 reports monitor geometry.</summary>
internal readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;
}

/// <summary>The monitor a point falls on: its work area (taskbar excluded), in the process's pixel space.</summary>
internal readonly record struct MonitorGeometry(PixelRect WorkArea);

/// <summary>
/// Where the tray flyout goes, worked out in pixels on the monitor the cursor is on.
/// <para>
/// <c>SystemParameters.WorkArea</c> is the <i>primary</i> monitor's work area, so a flyout opened
/// from a tray icon on a second screen was clamped onto the first. The tray icon lives on whichever
/// monitor holds the taskbar; the cursor is over it at the moment of the click. So:
/// <c>MonitorFromPoint</c> for that monitor and <c>GetMonitorInfo</c> for its work area. The cursor,
/// the work area, the window's own size (<c>GetWindowRect</c>) and the move (<c>SetWindowPos</c>) are
/// all in the process's one pixel space, so nothing here converts DIPs or guesses which DPI WPF
/// will apply — which is what goes wrong when a per-monitor-aware window lands on a screen with a
/// different scale.
/// </para>
/// </summary>
internal static class FlyoutPlacement
{
    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    /// <summary>
    /// Top-left of a <paramref name="widthPx"/> x <paramref name="heightPx"/> flyout opened by a
    /// click at (<paramref name="cursorX"/>, <paramref name="cursorY"/>), clamped into
    /// <paramref name="work"/>: centred on the cursor, opening upwards when the cursor is in the
    /// lower half of the work area (taskbar along the bottom) and downwards otherwise.
    /// </summary>
    public static (int X, int Y) Compute(int cursorX, int cursorY, PixelRect work, int widthPx, int heightPx)
    {
        var left = cursorX - (widthPx / 2);
        var top = cursorY > work.Top + (work.Height / 2)
            ? cursorY - heightPx
            : cursorY;

        return (
            Math.Clamp(left, work.Left, Math.Max(work.Left, work.Right - widthPx)),
            Math.Clamp(top, work.Top, Math.Max(work.Top, work.Bottom - heightPx)));
    }

    /// <summary>The monitor nearest to a screen point (physical pixels), or null when Win32 cannot say.</summary>
    public static MonitorGeometry? MonitorAt(int x, int y)
    {
        var monitor = MonitorFromPoint(new NativePoint { X = x, Y = y }, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return null;
        }

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return null;
        }

        return new MonitorGeometry(new PixelRect(info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom));
    }

    /// <summary>Moves <paramref name="window"/> to (<paramref name="x"/>, <paramref name="y"/>) in screen pixels, size untouched.</summary>
    public static bool MoveTo(IntPtr window, int x, int y) =>
        SetWindowPos(window, IntPtr.Zero, x, y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);

    /// <summary>The window's current outer rectangle in screen pixels.</summary>
    public static PixelRect? BoundsOf(IntPtr window) =>
        GetWindowRect(window, out var rect) ? new PixelRect(rect.Left, rect.Top, rect.Right, rect.Bottom) : null;

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }
}
