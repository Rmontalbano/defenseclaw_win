using DefenseClaw.App.Services;
using DefenseClaw.App.ViewModels.Updates;
using Wpf.Ui.Controls;

namespace DefenseClaw.App.Views.Updates;

/// <summary>
/// Install/update awareness surface: installed-vs-latest version, an honest trust panel
/// (Authenticode + sigstore + stub-asset sanity read straight off the GitHub release), and
/// copyable — never executed — upgrade commands.
/// <para>
/// <b>This window never mutates anything.</b> Every other write path in this app goes through
/// <see cref="DefenseClaw.Core.Cli.CliRunner"/> so the Activity panel can record exact argv;
/// this window has no such path at all, deliberately: an update/upgrade is consequential
/// enough that the operator should run it themselves, with the exact command in front of
/// them, not have this companion app run it on their behalf.
/// </para>
/// </summary>
public sealed partial class UpdatesWindow : FluentWindow
{
    private static UpdatesWindow? _current;

    private readonly UpdatesWindowViewModel _viewModel;

    private UpdatesWindow(UpdatesWindowViewModel viewModel)
    {
        _viewModel = viewModel;

        InitializeComponent();
        DataContext = viewModel;

        Closed += OnClosed;
    }

    /// <summary>
    /// Opens the Updates window, or brings the existing one to front if it is already open.
    /// The orchestrator (tray menu / Configure panel) calls this; it is the only entry point
    /// into this feature.
    /// </summary>
    public static UpdatesWindow Show(AppServices services)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (_current is not null)
        {
            _current.Activate();
            return _current;
        }

        var viewModel = new UpdatesWindowViewModel(services);
        var window = new UpdatesWindow(viewModel);

        // Application.Current.MainWindow can (a) be null this early, or (b) — if nothing
        // has claimed it yet — resolve to this very window once WPF notices it is the first
        // one ever shown, which makes Owner = self throw. Either way, "no owner" is a fine
        // fallback: the window still shows, just without centering over a parent.
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

    private void OnClosed(object? sender, EventArgs e)
    {
        Closed -= OnClosed;
        _viewModel.Dispose();

        if (ReferenceEquals(_current, this))
        {
            _current = null;
        }
    }
}
