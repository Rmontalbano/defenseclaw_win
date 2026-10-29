using System.ComponentModel;
using DefenseClaw.App.ViewModels.Wizards;
using Wpf.Ui.Controls;

namespace DefenseClaw.App.Views.Wizards;

/// <summary>
/// Host for one wizard run. Owns nothing but the window lifetime: pages, validation, the
/// review screen and the CLI invocation all live in <see cref="WizardViewModel"/>.
/// <para>
/// <b>Closing while the command runs.</b> The X, Alt+F4 and the Cancel button all mean "stop it",
/// and stopping a <c>setup</c> verb can leave configuration half-applied, so <see cref="OnClosing"/>
/// does not let a close through while <see cref="WizardViewModel.IsRunning"/>: it cancels the close
/// and has the view-model put up its stop-confirmation overlay. Only if the operator confirms is the
/// run's token cancelled (the runner then kills the whole process tree), and the window closes itself
/// once the run has finished reporting. The overlay is not a modal dialog, so nothing here can hold
/// up an app exit — and the closes this cannot refuse (the owner closing on tray Exit, and
/// <c>Application.Shutdown</c>, which ignores <c>Cancel</c>) end up in <see cref="OnClosed"/>, which
/// disposes the view-model and with it any run still in flight. Unlike the upgrade installer, a
/// wizard's child must not outlive its wizard; <c>CliRunner.Shutdown</c> would kill it at exit anyway.
/// </para>
/// </summary>
public sealed partial class WizardWindow : FluentWindow
{
    private readonly WizardViewModel _viewModel;

    public WizardWindow(WizardViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

        InitializeComponent();
        Icon = Services.ShieldIconFactory.CreateWindowIcon();
        DataContext = viewModel;

        _viewModel.CloseRequested += OnCloseRequested;
        Closed += OnClosed;
    }

    private void OnCloseRequested(object? sender, EventArgs e) => Close();

    /// <summary>
    /// Esc closes the wizard — or, while a command runs, raises the "stop it?" question, and dismisses that
    /// question if it is already up. Handled on the bubbling phase so a combo box that is open closes its
    /// own drop-down with the first Esc and the wizard only sees the second.
    /// </summary>
    protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Key == System.Windows.Input.Key.Escape && !e.Handled)
        {
            e.Handled = true;
            _viewModel.HandleEscape();
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Once the dispatcher is shutting down the close is going to happen whatever this says, and
        // asking a question nobody will be around to answer helps no one.
        if (_viewModel.IsRunning && !Dispatcher.HasShutdownStarted)
        {
            e.Cancel = true;
            _viewModel.RequestStopAndClose();
        }

        base.OnClosing(e);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _viewModel.CloseRequested -= OnCloseRequested;
        Closed -= OnClosed;
        _viewModel.Dispose();
    }
}
