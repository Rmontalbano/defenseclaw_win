using DefenseClaw.App.ViewModels.Wizards;
using Wpf.Ui.Controls;

namespace DefenseClaw.App.Views.Wizards;

/// <summary>
/// Host for one wizard run. Owns nothing but the window lifetime: pages, validation, the
/// review screen and the CLI invocation all live in <see cref="WizardViewModel"/>.
/// </summary>
public sealed partial class WizardWindow : FluentWindow
{
    private readonly WizardViewModel _viewModel;

    public WizardWindow(WizardViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

        InitializeComponent();
        DataContext = viewModel;

        _viewModel.CloseRequested += OnCloseRequested;
        Closed += OnClosed;
    }

    private void OnCloseRequested(object? sender, EventArgs e) => Close();

    private void OnClosed(object? sender, EventArgs e)
    {
        _viewModel.CloseRequested -= OnCloseRequested;
        Closed -= OnClosed;
    }
}
