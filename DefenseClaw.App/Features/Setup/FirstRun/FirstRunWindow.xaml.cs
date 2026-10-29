using System.ComponentModel;
using System.Windows.Input;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Appearance;
using DefenseClaw.App.ViewModels.FirstRun;
using Wpf.Ui.Controls;

namespace DefenseClaw.App.Views.FirstRun;

/// <summary>
/// The first-run guided setup window (see <see cref="FirstRunViewModel"/>). It owns window concerns only: single instance, the owner, Esc,
/// and refusing a close while the setup commands run. Closing mid-run would cancel nothing (the runs have their own lifetime in
/// <c>CliRunner</c>) and hide a half-finished setup, so it is refused until the run reports.
/// </summary>
public sealed partial class FirstRunWindow : FluentWindow
{
    private static FirstRunWindow? _current;

    private readonly FirstRunViewModel _viewModel;

    public FirstRunWindow(FirstRunViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

        InitializeComponent();
        Icon = ShieldIconFactory.CreateWindowIcon();
        AppearanceService.Current?.Attach(this);
        DataContext = viewModel;

        _viewModel.CloseRequested += OnCloseRequested;
        Closed += OnClosed;
    }

    /// <summary>
    /// Opens the window, or brings the existing one to the front. The not-initialized banner and the Setup panel both come here.
    /// Nothing runs on open: the state lines are read from the monitor and the setup catalog (read-only help probes) fills the connector list.
    /// </summary>
    public static FirstRunWindow Show(AppServices services)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (_current is not null)
        {
            _current.Activate();
            return _current;
        }

        var viewModel = new FirstRunViewModel(services);
        var window = new FirstRunWindow(viewModel);

        var owner = System.Windows.Application.Current?.MainWindow;
        if (owner is not null && !ReferenceEquals(owner, window))
        {
            window.Owner = owner;
        }

        _current = window;
        window.Show();
        _ = viewModel.InitializeAsync();
        return window;
    }

    private void OnCloseRequested(object? sender, EventArgs e) => Close();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Key != Key.Escape || e.Handled)
        {
            return;
        }

        e.Handled = true;
        if (!_viewModel.HandleEscape() && !_viewModel.IsBusy)
        {
            Close();
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_viewModel.IsBusy && !Dispatcher.HasShutdownStarted)
        {
            e.Cancel = true;
            _ = Activate();
        }

        base.OnClosing(e);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Closed -= OnClosed;
        _viewModel.CloseRequested -= OnCloseRequested;
        _viewModel.Dispose();

        if (ReferenceEquals(_current, this))
        {
            _current = null;
        }
    }
}
