namespace DefenseClaw.App.Services;

/// <summary>
/// What the shell needs from the dashboard window, so <see cref="DashboardHost"/> can be tested
/// without building a real one. <see cref="MainWindow"/> is the only implementation.
/// </summary>
internal interface IDashboardWindow
{
    bool IsVisible { get; }

    /// <summary>Shows the window and brings it to the front; restores it from minimized.</summary>
    void ShowAndActivate();

    void Hide();

    /// <summary>Lets the next <see cref="Close"/> actually close instead of hiding to the tray.</summary>
    void AllowClose();

    void Close();
}

/// <summary>
/// Owns the dashboard window's lifetime: it is not built until something asks to show it.
/// <para>
/// The app autostarts with <c>--minimized</c> and can then run for a whole session without the
/// dashboard ever being opened. Building the window up front (XAML parse, the WPF-UI chrome, the
/// view-model and its monitor subscription, a 256 px icon render) put all of that on the login
/// startup path for a window that mostly stays hidden. Now the first tray click on "Open
/// Dashboard", or a second launch, pays for it; every later show reuses the same instance, and
/// closing the window still only hides it (see <see cref="MainWindow"/>).
/// </para>
/// <para>
/// Everything that used to reach for the window through a field now goes through here, and
/// none of it creates the window as a side effect: <see cref="Current"/>,
/// <see cref="HideIfVisible"/> and <see cref="CloseForExit"/> only touch a window that exists.
/// Only <see cref="Show"/> builds one. UI thread only.
/// </para>
/// </summary>
internal sealed class DashboardHost
{
    private readonly Func<IDashboardWindow> _create;
    private IDashboardWindow? _window;
    private bool _exiting;

    /// <param name="create">
    /// Builds the window. Called at most once successfully: a factory that throws leaves nothing
    /// cached, so the next <see cref="Show"/> tries again.
    /// </param>
    public DashboardHost(Func<IDashboardWindow> create)
    {
        _create = create ?? throw new ArgumentNullException(nameof(create));
    }

    /// <summary>The window if it has been built, otherwise null. Never builds it.</summary>
    public IDashboardWindow? Current => _window;

    /// <summary>True once the window has been built.</summary>
    public bool IsCreated => _window is not null;

    /// <summary>
    /// Builds the window on first use and brings it to the front. Returns false, without building
    /// anything, once <see cref="CloseForExit"/> has run: a second launch that lands in the middle
    /// of shutdown must not conjure a window the exit path will never close.
    /// </summary>
    public bool Show()
    {
        if (_exiting)
        {
            return false;
        }

        _window ??= _create();
        _window.ShowAndActivate();
        return true;
    }

    /// <summary>Hides the window if it is on screen. Does nothing when it was never built.</summary>
    public void HideIfVisible()
    {
        if (_window is { IsVisible: true } window)
        {
            window.Hide();
        }
    }

    /// <summary>
    /// The exit path: really closes the window if there is one, and refuses to build one from here
    /// on. A window that was never built has nothing to close.
    /// </summary>
    public void CloseForExit()
    {
        _exiting = true;

        if (_window is { } window)
        {
            window.AllowClose();
            window.Close();
        }
    }
}
