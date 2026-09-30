using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DefenseClaw.App.Services.Appearance;

namespace DefenseClaw.App.Views;

/// <summary>
/// The frameless left-click flyout.
/// <para>
/// Windows gives no supported way to ask "where is my tray icon", so the flyout anchors
/// to the cursor — which is over the icon at the moment of the click — clamped to the
/// work area. That also puts it on the right monitor and on the right side of a taskbar
/// the user may have moved, which a hard-coded bottom-right corner would not.
/// </para>
/// <para>
/// <b>Dismissing with the tray icon.</b> The flyout hides itself when it loses focus, and the
/// press on the tray icon is what takes focus away — so by the time the icon's mouse-up
/// reaches the toggle handler the flyout is already hidden, and the toggle would show it
/// again: the click meant to close it re-opens it. The window therefore remembers when a
/// focus-loss hide happened (<see cref="WasJustHiddenByFocusLoss"/>) and the toggle treats a
/// click that close behind one as the dismissal it was.
/// </para>
/// </summary>
public partial class TrayFlyoutWindow : Window
{
    /// <summary>
    /// How long after a focus-loss hide a tray click still counts as the click that caused
    /// it. A click is a press and a release, typically 50-150 ms apart; 300 ms covers a slow
    /// one without swallowing a genuinely separate second click.
    /// </summary>
    private const long RecentHideWindowMilliseconds = 300;

    private bool _reallyClose;

    /// <summary><see cref="Environment.TickCount64"/> of the last focus-loss hide; 0 = never.</summary>
    private long _hiddenByFocusLossAt;

    public TrayFlyoutWindow()
    {
        InitializeComponent();
        AppearanceService.Current?.Attach(this);

        // Click-away dismiss, the behaviour every other tray flyout on the OS has.
        Deactivated += (_, _) =>
        {
            if (IsVisible)
            {
                _hiddenByFocusLossAt = Environment.TickCount64;
                Hide();
            }
        };
    }

    /// <summary>
    /// True when the flyout hid itself because it lost focus within the last few hundred
    /// milliseconds — i.e. the tray click that is arriving now is very likely the one that
    /// took the focus, and should be read as "dismiss", not "open". Only a focus-loss hide
    /// counts: Escape, the buttons and a toggle-hide do not. UI thread only.
    /// </summary>
    public bool WasJustHiddenByFocusLoss =>
        !IsVisible &&
        _hiddenByFocusLossAt != 0 &&
        Environment.TickCount64 - _hiddenByFocusLossAt < RecentHideWindowMilliseconds;

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
