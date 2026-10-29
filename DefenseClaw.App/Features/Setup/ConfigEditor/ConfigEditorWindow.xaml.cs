using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Appearance;
using DefenseClaw.App.ViewModels.ConfigEditor;
using ICSharpCode.AvalonEdit;
using Wpf.Ui.Controls;

namespace DefenseClaw.App.Views.ConfigEditor;

/// <summary>The answer <see cref="ConfigEditorWindow.CloseForExitAsync(AppServices)"/> gives the tray Exit.</summary>
public enum ConfigEditorExitResult
{
    /// <summary>No edits are left to lose: there were none, the operator discarded, or saved; any detached editor window is closed.</summary>
    Closed,

    /// <summary>The operator cancelled or the save failed; the editor stays as it was and says why.</summary>
    Declined,

    /// <summary>The editor is already closing, asking or saving, so this request was not a new question and nothing was asked.</summary>
    Busy,
}

/// <summary>
/// The detached config.yaml editor: <see cref="ConfigEditorView"/> (the same control the Setup panel shows inline) in a window of its own, over the
/// app's one draft (<see cref="ConfigEditorSession"/>, CUST-385). An edit made here is the edit in Setup and the other way round; there is one file
/// watcher and one save pipeline whichever shows it.
/// <para>
/// <b>Entry point.</b> <see cref="Show"/> is the whole public surface this window exposes to the rest of the app (the palette's "Open config editor",
/// <c>ShellActions.OpenConfigEditor</c>, and the inline editor's "Open in window"). Calling it twice reuses the same window (brought to front) instead of
/// opening a second window over the same draft.
/// </para>
/// <para>
/// <b>Closing.</b> The draft does not live in this window, so closing it drops nothing: the edits stay in the Setup panel's editor and the window just goes.
/// The tray Exit is different - it ends the app, and with it the draft - so it asks first through <see cref="CloseForExitAsync(AppServices)"/>, over this
/// window when it is open and over the dashboard when it is not. <c>Application.Shutdown</c> ignores <c>Cancel</c>, which is why the exit asks before it shuts
/// down rather than in <see cref="OnClosing"/>.
/// </para>
/// </summary>
public partial class ConfigEditorWindow : FluentWindow
{
    private static ConfigEditorWindow? _current;

    /// <summary>
    /// How the post-save bar reaches the tray's gateway control: opens the reviewed <c>defenseclaw-gateway restart</c> over the
    /// given window and runs it only if the operator confirms. Set once by the dashboard window, which is built with the tray; null in a
    /// test host, where the bar then carries no restart button. The shared session (<see cref="ConfigEditorSession"/>) wires it to the draft.
    /// </summary>
    internal static Func<Window, Task>? GatewayRestart { get; set; }

    private readonly ConfigEditorSession _session;
    private readonly ConfigEditorWindowViewModel _viewModel;

    /// <summary>The look controls; null when the app was built without them (a test host).</summary>
    private readonly AppearanceService? _appearance = AppearanceService.Current;

    /// <summary>Set once the exit's question has been answered, so the <c>Close()</c> that follows is not held again.</summary>
    private bool _closeApproved;

    /// <summary>True while the exit's question is being asked or acted on; a second exit meanwhile is told the editor is busy.</summary>
    private bool _closeFlowRunning;

    private bool _closed;

    public ConfigEditorWindow(AppServices services)
    {
        ArgumentNullException.ThrowIfNull(services);

        InitializeComponent();
        Icon = ShieldIconFactory.CreateWindowIcon();
        _appearance?.Attach(this);

        _session = ConfigEditorSession.For(services);
        _viewModel = _session.Editor;
        DataContext = _viewModel;
        EditorView.Session = _session;

        Closed += (_, _) =>
        {
            _closed = true;

            // The draft is the session's: the view lets go of it (it detaches when unloaded), and nothing is disposed.
            EditorView.Session = null;
            if (ReferenceEquals(_current, this))
            {
                _current = null;
            }
        };
    }

    /// <summary>The RAW editor of this window's view (tests read its options).</summary>
    internal TextEditor RawEditor => EditorView.RawEditor;

    /// <summary>The detached editor window when one is open; null otherwise.</summary>
    internal static ConfigEditorWindow? OpenWindow => _current;

    /// <summary>Opens the detached config editor on the app's one draft, or brings the already-open one to the front.</summary>
    public static void Show(AppServices services)
    {
        if (_current is { } existing)
        {
            existing.ShowAndActivate();
            return;
        }

        _current = new ConfigEditorWindow(services);
        _current.Show();
    }

    /// <summary>The tray Exit's question about the detached window alone (no composition to look up the draft by): kept for callers that only know the window.</summary>
    public static Task<ConfigEditorExitResult> CloseForExitAsync() => CloseForExitAsync(null);

    /// <summary>
    /// The tray Exit's question, asked before the app shuts down. With the detached window open it is asked over that window, which closes once the answer
    /// allows it; without one, the draft is the Setup panel's and the question is asked there (<see cref="ConfigEditorSession"/>).
    /// <see cref="ConfigEditorExitResult.Closed"/> means nothing is left to lose (there were no edits, the operator discarded, or the edits were saved and
    /// validated) and the exit may go ahead; <see cref="ConfigEditorExitResult.Declined"/> means the operator cancelled or the save failed, and
    /// <see cref="ConfigEditorExitResult.Busy"/> that a question or save is already in flight - either way the exit must be abandoned, and only the last is
    /// one the operator has not just been shown.
    /// <para>
    /// It closes the window itself, rather than approving a later close, so an approval can never outlive an exit that something else then calls off. Call
    /// it as the last check before <c>Shutdown</c>.
    /// </para>
    /// </summary>
    /// <param name="services">The composition whose draft to ask about when no window is open; null asks about the window only.</param>
    public static async Task<ConfigEditorExitResult> CloseForExitAsync(AppServices? services)
    {
        if (_current is { } window)
        {
            if (window._closeFlowRunning)
            {
                // A question is already open (its dialog is modal, so on screen) or a save is finishing: a second request
                // is not a second answer, and CloseAfterConfirmationAsync would only say "no" without a word.
                return ConfigEditorExitResult.Busy;
            }

            return await window.CloseAfterConfirmationAsync(bringToFront: true).ConfigureAwait(true)
                ? ConfigEditorExitResult.Closed
                : ConfigEditorExitResult.Declined;
        }

        return services is not null && ConfigEditorSession.TryGet(services) is { } session
            ? await session.ConfirmExitAsync().ConfigureAwait(true)
            : ConfigEditorExitResult.Closed;
    }

    /// <summary>
    /// Asks whatever must be asked about the draft and, if the answer allows it, closes the window. True when the window is closed
    /// (or needed no question), false when it stays open.
    /// </summary>
    private async Task<bool> CloseAfterConfirmationAsync(bool bringToFront)
    {
        if (_closeFlowRunning)
        {
            return false;
        }

        _closeFlowRunning = true;
        try
        {
            if (_viewModel.CloseNeedsConfirmation())
            {
                if (bringToFront)
                {
                    // The question is about the draft this window shows; make sure it is on screen to be read.
                    ShowAndActivate();
                }

                if (!await _viewModel.ConfirmCloseAsync().ConfigureAwait(true))
                {
                    return false;
                }
            }

            if (!_closed)
            {
                _closeApproved = true;
                Close();
            }

            return true;
        }
        finally
        {
            _closeFlowRunning = false;
        }
    }

    /// <summary>
    /// The title-bar X, Alt+F4 and the system menu land here. Closing drops nothing (the draft stays in the Setup panel's editor), so the window goes -
    /// except while the exit's question is open or its save is finishing, when a second close is not a second answer and is held.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!e.Cancel && !_closeApproved && !Dispatcher.HasShutdownStarted && _closeFlowRunning)
        {
            e.Cancel = true;
        }

        base.OnClosing(e);
    }

    private void ShowAndActivate()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        base.Show();
        _ = Activate();
        _ = Focus();
    }
}
