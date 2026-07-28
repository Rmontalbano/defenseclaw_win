using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace DefenseClaw.App.Views;

/// <summary>
/// The frameless left-click flyout.
/// <para>
/// Windows gives no supported way to ask "where is my tray icon", so the flyout anchors
/// to the cursor — which is over the icon at the moment of the click — clamped to the
/// work area. That also puts it on the right monitor and on the right side of a taskbar
/// the user may have moved, which a hard-coded bottom-right corner would not.
/// </para>
/// </summary>
public partial class TrayFlyoutWindow : Window
{
    private bool _reallyClose;

    public TrayFlyoutWindow()
    {
        InitializeComponent();

        // Click-away dismiss, the behaviour every other tray flyout on the OS has.
        Deactivated += (_, _) => Hide();
    }

    /// <summary>Shows the flyout next to the cursor and takes focus.</summary>
    public void ShowNearCursor()
    {
        if (!IsVisible)
        {
            // Lay out offscreen first: SizeToContent means ActualHeight is not known
            // until after a pass, and positioning before that produces a visible jump.
            Left = -10000;
            Top = -10000;
            Show();
        }

        UpdateLayout();
        PositionNearCursor();

        _ = Activate();
        _ = Focus();
    }

    /// <summary>Closes for real, bypassing the hide-on-deactivate behaviour.</summary>
    public void ForceClose()
    {
        _reallyClose = true;
        Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Key == Key.Escape)
        {
            Hide();
            e.Handled = true;
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_reallyClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }

    private void PositionNearCursor()
    {
        if (!GetCursorPos(out var point))
        {
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var cursorX = point.X / dpi.DpiScaleX;
        var cursorY = point.Y / dpi.DpiScaleY;

        var work = SystemParameters.WorkArea;
        var width = ActualWidth;
        var height = ActualHeight;

        var left = cursorX - (width / 2);
        var top = cursorY > work.Top + (work.Height / 2)
            ? cursorY - height          // taskbar along the bottom: open upwards
            : cursorY;                  // taskbar along the top: open downwards

        Left = Math.Clamp(left, work.Left, Math.Max(work.Left, work.Right - width));
        Top = Math.Clamp(top, work.Top, Math.Max(work.Top, work.Bottom - height));
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }
}
