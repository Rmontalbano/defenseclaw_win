using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Appearance;
using DefenseClaw.App.ViewModels.ConfigEditor;
using ICSharpCode.AvalonEdit.Search;
using Wpf.Ui.Controls;

namespace DefenseClaw.App.Views.ConfigEditor;

/// <summary>
/// The config.yaml editor window: RAW (AvalonEdit) and FORM (generated sections) tabs
/// over the same <see cref="ConfigEditorWindowViewModel"/>.
/// <para>
/// <b>Entry point.</b> <see cref="Show"/> is the whole public surface this window
/// exposes to the rest of the app — the orchestrator wires it up from wherever the
/// Configure/Setup panel ends up launching it from. Calling it twice reuses the same
/// window (brought to front) instead of opening a second editor over the same file.
/// </para>
/// <para>
/// AvalonEdit's <c>TextEditor.Text</c> is a plain CLR property, not a dependency
/// property, so it cannot be bound from XAML — this code-behind is the standard,
/// AvalonEdit-recommended way to keep it in sync with the view-model's
/// <see cref="ConfigEditorWindowViewModel.RawText"/> in both directions without an
/// infinite update loop.
/// </para>
/// <para>
/// <b>Closing with unsaved edits.</b> <see cref="OnClosing"/> holds a close that would drop edits (RAW or FORM),
/// asks save / discard / cancel through <see cref="UnsavedChangesDialog"/>, and closes for real only once the
/// view-model says so (Discard, or a Save that wrote <i>and</i> validated). The app's tray Exit cannot rely on
/// <c>Closing</c> — <c>Application.Shutdown</c> ignores <c>Cancel</c> — so it asks first through
/// <see cref="CloseForExitAsync"/>.
/// </para>
/// </summary>
public partial class ConfigEditorWindow : FluentWindow
{
    private static ConfigEditorWindow? _current;

    private readonly ConfigEditorWindowViewModel _viewModel;

    /// <summary>The look controls, whose changes re-colour the YAML; null when the app was built without them (a test host).</summary>
    private readonly AppearanceService? _appearance = AppearanceService.Current;

    private bool _syncingEditorText;

    /// <summary>Set once the view-model has said the close may go ahead, so the <c>Close()</c> that follows is not asked about again.</summary>
    private bool _closeApproved;

    /// <summary>True while a close question is being asked or acted on; a second X click or Alt+F4 meanwhile is not a second answer.</summary>
    private bool _closeFlowRunning;

    private bool _closed;

    public ConfigEditorWindow(AppServices services)
    {
        ArgumentNullException.ThrowIfNull(services);

        InitializeComponent();
        _appearance?.Attach(this);

        _viewModel = new ConfigEditorWindowViewModel(services);
        DataContext = _viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.UnsavedChangesPrompt = request => Task.FromResult(UnsavedChangesDialog.Ask(this, request));
        _viewModel.CommitPendingEdits = CommitPendingFormEdit;
        _viewModel.WatchDiskChanges();

        ApplyHighlighting();
        if (_appearance is not null)
        {
            _appearance.Changed += OnAppearanceChanged;
        }

        RawEditor.ShowLineNumbers = true;
        RawEditor.Options.EnableHyperlinks = false;
        RawEditor.Options.ShowTabs = false;
        SearchPanel.Install(RawEditor);
        RawEditor.TextChanged += OnEditorTextChanged;

        RootTabs.SelectionChanged += OnTabSelectionChanged;
        PreviewKeyDown += OnWindowPreviewKeyDown;

        Loaded += OnLoaded;
        Closed += (_, _) =>
        {
            _closed = true;
            if (_appearance is not null)
            {
                _appearance.Changed -= OnAppearanceChanged;
            }

            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.Dispose();
            if (ReferenceEquals(_current, this))
            {
                _current = null;
            }
        };
    }

    /// <summary>The YAML palette that reads on the look on screen (see <see cref="YamlHighlighting"/>): the style's own hues, light or dark.</summary>
    private void ApplyHighlighting() =>
        RawEditor.SyntaxHighlighting = YamlHighlighting.ForCurrent();

    private void OnAppearanceChanged(object? sender, EventArgs e) =>
        _ = Dispatcher.BeginInvoke(new Action(ApplyHighlighting));

    /// <summary>Opens the config editor, or brings the already-open one to the front.</summary>
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

    /// <summary>
    /// The tray Exit's question, asked before the app shuts down: closes the editor if it can be closed without
    /// losing edits and says so. <see langword="true"/> means no editor is left open (there was none, it was clean,
    /// the operator discarded, or the edits were saved and validated) and the exit may go ahead;
    /// <see langword="false"/> means the operator cancelled or the save failed and the exit must be abandoned.
    /// <para>
    /// It closes the window itself, rather than approving a later close, so an approval can never outlive an exit
    /// that something else then calls off. Call it as the last check before <c>Shutdown</c>.
    /// </para>
    /// </summary>
    public static async Task<bool> CloseForExitAsync()
    {
        if (_current is not { } window)
        {
            return true;
        }

        return await window.CloseAfterConfirmationAsync(bringToFront: true).ConfigureAwait(true);
    }

    /// <summary>
    /// Asks whatever must be asked and, if the answer allows it, closes the window. True when the window is closed
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
                    // The question is about this window's edits; make sure it is on screen to be read.
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
    /// Holds a close that would lose edits. The title-bar X, Alt+F4 and the system menu land here; the window stays
    /// open while <see cref="CloseAfterConfirmationAsync"/> asks, then closes itself only if the answer allows it.
    /// A close WPF forces regardless — <c>Application.Shutdown</c> ignores <c>Cancel</c> — is let through untouched
    /// (a question nobody could answer would only be left on screen), which is why the app asks first, via
    /// <see cref="CloseForExitAsync"/>.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!e.Cancel && !_closeApproved && !Dispatcher.HasShutdownStarted)
        {
            if (_closeFlowRunning)
            {
                // A question is already open (or a save is finishing): a second close request is not a second answer.
                e.Cancel = true;
            }
            else if (_viewModel.CloseNeedsConfirmation())
            {
                e.Cancel = true;
                _ = CloseAfterConfirmationAsync(bringToFront: false);
            }
        }

        base.OnClosing(e);
    }

    /// <summary>
    /// Lets a FORM edit that is still inside its box reach the view-model. FORM boxes commit on <c>LostFocus</c>, and
    /// a title-bar click or a tray Exit does not move keyboard focus, so without this the typed value would be
    /// invisible to the "any unsaved edits?" check. Focus is put back afterwards so typing can continue.
    /// </summary>
    private void CommitPendingFormEdit()
    {
        if (Keyboard.FocusedElement is UIElement focused && IsInside(FormScroll, focused))
        {
            _ = Keyboard.Focus(null);
            _ = focused.Focus();
        }
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

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await _viewModel.LoadAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Window-level shortcuts. <b>Ctrl+S</b> runs Save when Save is enabled (it is the same command the
    /// button uses, so the same CanExecute applies). <b>Esc</b> closes the on-disk preview when it is open
    /// and otherwise does nothing — it deliberately does not close the window, which could throw away
    /// unsaved edits.
    /// </summary>
    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _viewModel.ShowOnDiskPreview)
        {
            _viewModel.DismissOnDiskPreviewCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            SaveFromKeyboard();
        }
    }

    /// <summary>
    /// FORM boxes commit on <c>LostFocus</c>, so a value typed into the focused box has not reached the
    /// view-model when Ctrl+S is pressed — and a Save would then miss it. When focus is inside the FORM
    /// tab, clearing it first makes WPF commit the pending edit (synchronously), then focus is put back.
    /// The RAW editor needs none of this: its text is pushed to the view-model on every change.
    /// </summary>
    private void SaveFromKeyboard()
    {
        if (!_viewModel.SaveCommand.CanExecute(null))
        {
            return;
        }

        UIElement? restoreFocusTo = null;
        if (Keyboard.FocusedElement is UIElement focused && IsInside(FormScroll, focused))
        {
            restoreFocusTo = focused;
            _ = Keyboard.Focus(null);
        }

        if (_viewModel.SaveCommand.CanExecute(null))
        {
            _viewModel.SaveCommand.Execute(null);
        }

        _ = restoreFocusTo?.Focus();
    }

    private static bool IsInside(DependencyObject ancestor, DependencyObject? node)
    {
        while (node is not null)
        {
            if (ReferenceEquals(node, ancestor))
            {
                return true;
            }

            node = node is Visual
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }

        return false;
    }

    private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, RootTabs))
        {
            return;
        }

        if (RootTabs.SelectedItem is TabItem { Name: "FormTabItem" })
        {
            _viewModel.NotifyFormTabSelected();
        }
    }

    private void OnEditorTextChanged(object? sender, EventArgs e)
    {
        if (_syncingEditorText)
        {
            return;
        }

        _viewModel.RawText = RawEditor.Text;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ConfigEditorWindowViewModel.RawText))
        {
            return;
        }

        if (RawEditor.Text == _viewModel.RawText)
        {
            return;
        }

        _syncingEditorText = true;
        var caret = Math.Min(RawEditor.CaretOffset, _viewModel.RawText.Length);
        RawEditor.Text = _viewModel.RawText;
        RawEditor.CaretOffset = caret;
        _syncingEditorText = false;
    }
}
