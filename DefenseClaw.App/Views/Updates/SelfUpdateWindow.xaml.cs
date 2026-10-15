using System.Windows.Input;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Appearance;
using DefenseClaw.App.Services.Updates.SelfUpdate;
using Wpf.Ui.Controls;

namespace DefenseClaw.App.Views.Updates;

/// <summary>
/// The review before this app updates itself. One at a time; the view-model (the same one the banner and Settings use) decides everything,
/// this only hosts it: <see cref="SelfUpdateViewModel.ReviewRequested"/> opens it, <see cref="SelfUpdateViewModel.ReviewClosed"/> closes it.
/// </summary>
public sealed partial class SelfUpdateWindow : FluentWindow
{
    private static SelfUpdateWindow? _current;

    private readonly SelfUpdateViewModel _viewModel;

    private SelfUpdateWindow(SelfUpdateViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        Icon = ShieldIconFactory.CreateWindowIcon();
        AppearanceService.Current?.Attach(this);
        DataContext = viewModel;
        PreviewKeyDown += OnPreviewKeyDown;
        Closed += OnClosed;
        viewModel.ReviewClosed += OnReviewClosed;
    }

    internal static void Show(SelfUpdateViewModel viewModel)
    {
        if (_current is { } existing)
        {
            _ = existing.Activate();
            return;
        }

        _current = new SelfUpdateWindow(viewModel);
        _current.Show();
        _ = _current.Activate();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _viewModel.CancelCommand.CanExecute(null))
        {
            _viewModel.CancelCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnReviewClosed(object? sender, EventArgs e) => Dispatcher.BeginInvoke(Close);

    private void OnClosed(object? sender, EventArgs e)
    {
        _viewModel.ReviewClosed -= OnReviewClosed;
        _current = null;

        // Closed with the title-bar X during the review: the same as Cancel (nothing was installed). After Install it is a no-op.
        if (_viewModel.Phase is SelfUpdatePhase.Review or SelfUpdatePhase.Loading or SelfUpdatePhase.Failed)
        {
            _viewModel.CancelCommand.Execute(null);
        }
    }
}
