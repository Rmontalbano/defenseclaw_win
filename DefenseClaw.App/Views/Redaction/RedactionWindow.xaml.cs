using System.Windows;
using System.Windows.Input;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Appearance;
using DefenseClaw.App.ViewModels.Redaction;
using DefenseClaw.Core.Runtime;
using Wpf.Ui.Controls;

namespace DefenseClaw.App.Views.Redaction;

/// <summary>
/// The redaction policy in a window of its own. The entry points are Setup's tile and Logs' button, both of which exist only on a runtime that
/// has <c>setup redaction</c>; <see cref="Open"/> checks it again and does nothing on one that does not (0.8.10), so no other route can
/// open what the runtime cannot do. One window at a time, brought forward when it is already open, and read again each time it is opened so
/// it never shows a stale policy.
/// </summary>
public sealed partial class RedactionWindow : FluentWindow
{
    private static RedactionWindow? _current;

    private readonly RedactionViewModel _viewModel;

    private RedactionWindow(AppServices services)
    {
        InitializeComponent();
        Icon = ShieldIconFactory.CreateWindowIcon();

        _viewModel = new RedactionViewModel(services);
        Editor.DataContext = _viewModel;

        // Backdrop, title bar and colours follow the app's look like the dashboard's do.
        AppearanceService.Current?.Attach(this);

        Loaded += (_, _) => _ = _viewModel.RefreshCommand.ExecuteAsync(null);
        Closed += (_, _) =>
        {
            _viewModel.Dispose();
            if (ReferenceEquals(_current, this))
            {
                _current = null;
            }
        };

        // Esc closes the window, unless it is there to dismiss an open review (the view handles that one).
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !Editor.HandlesEscape && !e.Handled)
            {
                Close();
                e.Handled = true;
            }
        };
    }

    /// <summary>The window on screen, if any.</summary>
    internal static RedactionWindow? Current => _current;

    internal RedactionViewModel ViewModel => _viewModel;

    /// <summary>
    /// True when the connected runtime has the advanced redaction editor. The entry points show only while it is, and
    /// <see cref="Open"/> refuses while it is not.
    /// </summary>
    internal static bool CanOpen(AppServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.Runtime.Check(RuntimeCapability.RedactionAdvanced).IsAvailable;
    }

    /// <summary>
    /// Opens the redaction policy, or brings the open window forward and re-reads it. Returns false, and opens nothing, when the runtime
    /// does not have the editor. UI thread only.
    /// </summary>
    /// <param name="owner">The dashboard when it is showing; null or hidden centres the window on screen.</param>
    public static bool Open(AppServices services, Window? owner = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (!CanOpen(services))
        {
            return false;
        }

        if (_current is { } existing)
        {
            if (existing.WindowState == WindowState.Minimized)
            {
                existing.WindowState = WindowState.Normal;
            }

            // Opened again: the editor goes back to a preview (the decision to apply is not carried over) and the policy is read again.
            _ = existing.Activate();
            _ = existing._viewModel.ReopenAsync();
            return true;
        }

        var window = new RedactionWindow(services);
        _current = window;

        if (owner is { IsVisible: true, WindowState: not WindowState.Minimized })
        {
            window.Owner = owner;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        window.Show();
        return true;
    }
}
