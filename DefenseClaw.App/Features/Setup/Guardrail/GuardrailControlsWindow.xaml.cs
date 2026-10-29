using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Appearance;
using DefenseClaw.App.ViewModels;
using Wpf.Ui.Controls;

namespace DefenseClaw.App.Views.Guardrail;

/// <summary>
/// HILT, block message and judge gate in a window of their own. The entry point for the Setup tile grid and the Overview guardrail
/// tile is <see cref="Open"/> (or <see cref="CreateOpenCommand"/> for a button / palette binding): one window at a time, brought
/// forward when it is already open, and re-read each time it is opened so it never shows a stale posture.
/// </summary>
public sealed partial class GuardrailControlsWindow : FluentWindow
{
    private static GuardrailControlsWindow? _current;

    private readonly GuardrailControlsViewModel _viewModel;

    private GuardrailControlsWindow(AppServices services)
    {
        InitializeComponent();
        Icon = ShieldIconFactory.CreateWindowIcon();

        _viewModel = new GuardrailControlsViewModel(services);
        Controls.DataContext = _viewModel;

        // Backdrop, title bar and colours follow the app's look like the dashboard's do.
        AppearanceService.Current?.Attach(this);

        Loaded += (_, _) => _ = _viewModel.RefreshCommand.ExecuteAsync(null);
        Closed += (_, _) =>
        {
            if (ReferenceEquals(_current, this))
            {
                _current = null;
            }
        };

        // Esc closes the window, unless it is there to dismiss an open review (the view handles that one).
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !Controls.HandlesEscape && !e.Handled)
            {
                Close();
                e.Handled = true;
            }
        };
    }

    /// <summary>The window on screen, if any.</summary>
    internal static GuardrailControlsWindow? Current => _current;

    internal GuardrailControlsViewModel ViewModel => _viewModel;

    /// <summary>Opens the guardrail controls, or brings the open window forward and re-reads it. UI thread only.</summary>
    /// <param name="owner">The dashboard when it is showing; null or hidden centres the window on screen.</param>
    public static void Open(AppServices services, Window? owner = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (_current is { } existing)
        {
            if (existing.WindowState == WindowState.Minimized)
            {
                existing.WindowState = WindowState.Normal;
            }

            _ = existing.Activate();
            _ = existing._viewModel.RefreshCommand.ExecuteAsync(null);
            return;
        }

        var window = new GuardrailControlsWindow(services);
        _current = window;

        if (owner is { IsVisible: true, WindowState: not WindowState.Minimized })
        {
            window.Owner = owner;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        window.Show();
    }

    /// <summary>
    /// The one command a tile or menu item binds to: it opens (or fronts) the controls. <paramref name="owner"/> is asked at
    /// execute time, so pass <c>() =&gt; Application.Current?.MainWindow</c>.
    /// </summary>
    public static System.Windows.Input.ICommand CreateOpenCommand(AppServices services, Func<Window?>? owner = null) =>
        new RelayCommand(() => Open(services, owner?.Invoke()));
}
