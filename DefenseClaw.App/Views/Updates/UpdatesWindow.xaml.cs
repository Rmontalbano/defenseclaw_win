using System.Collections.Specialized;
using DefenseClaw.App.Services;
using DefenseClaw.App.ViewModels.Updates;
using Wpf.Ui.Controls;

namespace DefenseClaw.App.Views.Updates;

/// <summary>
/// Install/update surface: installed-vs-latest version, an honest trust panel (Authenticode +
/// sigstore + stub-asset sanity read straight off the GitHub release), copyable upgrade
/// commands, and the in-app upgrade flow.
/// <para>
/// <b>The upgrade flow is the one mutation here, and it is deliberately unhurried.</b> Checking,
/// staging and running are three separate clicks; the run is gated behind a confirm overlay that
/// prints the exact argv, what the resolver will replace, and how it rolls back. The command
/// itself goes through <see cref="DefenseClaw.Core.Cli.CliRunner"/> — same as every other change
/// this app makes — so the Activity panel records the argv, the streaming output and the exit
/// code. Nothing runs on its own, and nothing runs that was not first size-checked and
/// hash-verified against the release's signed checksums.txt.
/// </para>
/// <para>
/// This class owns only window concerns: lifetime, single-instance behaviour, and keeping the
/// resolver console pinned to its newest line.
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

        ((INotifyCollectionChanged)_viewModel.Upgrade.Output).CollectionChanged += OnUpgradeOutputChanged;
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

    /// <summary>
    /// Keeps the console on its newest line while the resolver runs. Honours the pause toggle:
    /// an operator reading back through a long upgrade should not be yanked to the bottom every
    /// quarter second. Lines are still collected while paused — only the scrolling stops.
    /// </summary>
    private void OnUpgradeOutputChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || _viewModel.Upgrade.IsOutputPaused)
        {
            return;
        }

        UpgradeOutputScroll.ScrollToEnd();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Closed -= OnClosed;
        ((INotifyCollectionChanged)_viewModel.Upgrade.Output).CollectionChanged -= OnUpgradeOutputChanged;
        _viewModel.Dispose();

        if (ReferenceEquals(_current, this))
        {
            _current = null;
        }
    }
}
