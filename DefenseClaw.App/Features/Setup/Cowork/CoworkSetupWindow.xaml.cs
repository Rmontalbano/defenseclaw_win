using System.ComponentModel;
using System.Windows.Input;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Appearance;
using DefenseClaw.App.ViewModels;
using Wpf.Ui.Controls;

namespace DefenseClaw.App.Views.Cowork;

/// <summary>
/// The Claude Cowork setup window (CUST-445): a frame around <see cref="CoworkSetupViewModel"/> and the shared review overlay. All state lives in the
/// view-model. Esc closes an open review first and then the window; the window does not close while the build command runs.
/// </summary>
public sealed partial class CoworkSetupWindow : FluentWindow
{
    private readonly CoworkSetupViewModel _viewModel;

    public CoworkSetupWindow(CoworkSetupViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

        InitializeComponent();
        Icon = ShieldIconFactory.CreateWindowIcon();
        DataContext = viewModel;
        AppearanceService.Current?.Attach(this);

        Loaded += OnLoaded;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        _ = _viewModel.LoadAsync();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || e.Handled)
        {
            return;
        }

        e.Handled = true;
        if (!_viewModel.HandleEscape() && !_viewModel.Review.IsRunning)
        {
            Close();
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // The command is a state change that must finish reporting; let it end before the window goes (an app exit is not refused).
        if (_viewModel.Review.IsRunning && !Dispatcher.HasShutdownStarted)
        {
            e.Cancel = true;
        }

        base.OnClosing(e);
    }
}
