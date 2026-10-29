using System.Windows;
using System.Windows.Input;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Appearance;
using DefenseClaw.App.ViewModels;
using Wpf.Ui.Controls;

namespace DefenseClaw.App.Views.JudgeHistory;

/// <summary>
/// The retained LLM-judge responses in a window of their own (the TUI's <c>J</c> on Logs → Verdicts). The entry point is
/// <see cref="Open"/>: one window at a time, brought forward and read again when it is already open. The window owns only window
/// concerns; <see cref="JudgeHistoryViewModel"/> reads the databases (read-only) and masks what it shows.
/// </summary>
public sealed partial class JudgeHistoryWindow : FluentWindow
{
    private static JudgeHistoryWindow? _current;

    private readonly JudgeHistoryViewModel _viewModel;

    private JudgeHistoryWindow(AppServices services)
    {
        InitializeComponent();
        Icon = ShieldIconFactory.CreateWindowIcon();

        _viewModel = new JudgeHistoryViewModel(services);
        DataContext = _viewModel;

        // Backdrop, title bar and colours follow the app's look like the dashboard's do.
        AppearanceService.Current?.Attach(this);

        Loaded += (_, _) => _ = _viewModel.InitializeAsync();
        Closed += (_, _) =>
        {
            if (ReferenceEquals(_current, this))
            {
                _current = null;
            }
        };

        PreviewKeyDown += (_, e) =>
        {
            if (e.Handled)
            {
                return;
            }

            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
            else if (e.Key == Key.F5)
            {
                _ = _viewModel.RefreshCommand.ExecuteAsync(null);
                e.Handled = true;
            }
        };
    }

    /// <summary>The window on screen, if any.</summary>
    internal static JudgeHistoryWindow? Current => _current;

    internal JudgeHistoryViewModel ViewModel => _viewModel;

    /// <summary>Opens the judge responses, or brings the open window forward and reads it again. UI thread only.</summary>
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

        var window = new JudgeHistoryWindow(services);
        _current = window;

        if (owner is { IsVisible: true, WindowState: not WindowState.Minimized })
        {
            window.Owner = owner;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        window.Show();
    }
}
