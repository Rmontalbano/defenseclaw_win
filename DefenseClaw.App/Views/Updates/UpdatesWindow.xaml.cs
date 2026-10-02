using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Input;
using System.Windows.Threading;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Appearance;
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
/// resolver console (a selectable, copyable <c>DcCommandOutput</c> that follows its newest line unless the
/// operator pauses auto-scroll).
/// </para>
/// <para>
/// <b>Closing during an upgrade.</b> The installer has no journal, so nothing that closes this
/// window may end a run — and none does: the run is started with no cancellation token at all (see
/// <c>UpgradeSectionViewModel.RunUpgradeAsync</c>) and disposing the view-model leaves it alone. On
/// top of that, <see cref="OnClosing"/> refuses a close while <c>Upgrade.IsRunning</c>, because
/// closing would only throw away the live view of an install that carries on regardless. Refusing is
/// non-modal (a banner, no dialog) and can never block an app exit. Checked with a throwaway WPF
/// harness (Windows PowerShell 5.1, i.e. .NET Framework's WPF, whose <c>Window</c> close logic .NET
/// inherited) against WPF's own close paths:
/// <list type="bullet">
/// <item><description>The title-bar X, Alt+F4 and the system menu reach <see cref="OnClosing"/> and are refused.</description></item>
/// <item><description>The owner <c>MainWindow</c> closing (tray Exit) closes its owned windows
/// <i>without</i> raising <c>Closing</c> on them, so this window is simply closed — <see cref="OnClosed"/>
/// disposes the view-model, which does not cancel the run — and <c>App.ExitApplication</c> goes on to
/// <c>Shutdown()</c>.</description></item>
/// <item><description><c>Application.Shutdown()</c> raises <c>Closing</c> on a window it has to close
/// itself but ignores <c>Cancel</c>, so a refusal cannot hold the process open. The installer is
/// exempt from <c>CliRunner.Shutdown</c>, so it survives the exit.</description></item>
/// </list>
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
        Icon = ShieldIconFactory.CreateWindowIcon();
        AppearanceService.Current?.Attach(this);
        DataContext = viewModel;

        _viewModel.Upgrade.PropertyChanged += OnUpgradePropertyChanged;
        PreviewKeyDown += OnWindowPreviewKeyDown;
        Closed += OnClosed;
    }

    /// <summary>True while the confirm overlay is open (or was, until the last property change) — decides whether focus is handed back on close.</summary>
    private bool _confirmWasOpen;

    /// <summary>Esc cancels the confirm overlay — the safe answer, and the same as the Cancel button. Nothing else in this window uses Esc.</summary>
    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _viewModel.Upgrade.IsConfirmVisible)
        {
            _viewModel.Upgrade.CancelConfirmCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>
    /// Keyboard focus follows the overlay: into it (on Cancel, the safe default) when it opens — the page
    /// behind it is disabled, so focus would otherwise be dropped — and back to the button that opened it
    /// when it closes.
    /// </summary>
    private void OnUpgradePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(UpgradeSectionViewModel.IsConfirmVisible))
        {
            return;
        }

        var open = _viewModel.Upgrade.IsConfirmVisible;
        if (open == _confirmWasOpen)
        {
            return;
        }

        _confirmWasOpen = open;
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() =>
            {
                _ = open ? ConfirmCancelButton.Focus() : RunUpgradeButton.Focus();
            }));
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
    /// Refuses a close while an upgrade run is in flight. Cancels rather than blocks: it returns at
    /// once, shows nothing modal, and leaves the explanation to the banner the XAML pins under the
    /// title bar (<c>NoteCloseRefused</c> makes that banner acknowledge the click). A close that
    /// WPF forces regardless — <c>Application.Shutdown</c> ignores <c>Cancel</c> — goes through
    /// untouched, and once the dispatcher has begun shutting down this does not even try to refuse.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (_viewModel.Upgrade.IsRunning && !Dispatcher.HasShutdownStarted)
        {
            e.Cancel = true;
            _viewModel.Upgrade.NoteCloseRefused();

            // The banner is the message, so make sure the window is in front for it to be read.
            _ = Activate();
        }

        base.OnClosing(e);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Closed -= OnClosed;
        PreviewKeyDown -= OnWindowPreviewKeyDown;
        _viewModel.Upgrade.PropertyChanged -= OnUpgradePropertyChanged;
        _viewModel.Dispose();

        if (ReferenceEquals(_current, this))
        {
            _current = null;
        }
    }
}
