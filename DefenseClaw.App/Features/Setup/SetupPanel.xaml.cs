using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using DefenseClaw.App.ViewModels;

namespace DefenseClaw.App.Views.Panels;

/// <summary>
/// View for the Setup panel. Paired with
/// <see cref="ViewModels.SetupPanelViewModel"/>; the shell binds the two together in
/// <see cref="Services.PanelCatalog"/>.
/// <para>
/// Keyboard: <b>Ctrl+F</b> moves focus to the setup search box (inside the config editor it is the editor's own search); <b>Esc</b> closes the
/// Connector Setup chooser, the guardrail review and the other dialogs while one is open. Both are handled on the preview phase so they work wherever
/// focus is inside the panel. F5 is bound by the shell to the view-model's <c>RefreshCommand</c>.
/// </para>
/// <para>
/// A tile or a deep link that asks for a section further down the page (<see cref="SetupPanelViewModel.SectionRequested"/>: the Credentials card, the
/// guardrail quick controls, the inline config editor) has it scrolled to the top of the page here.
/// </para>
/// </summary>
public sealed partial class SetupPanel : UserControl
{
    public SetupPanel()
    {
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += OnDataContextChanged;
    }

    private Window? _window;

    // Coming back to the window after a console was opened for `keys set` / `fill-missing` re-reads the credential list once
    // (the view-model does nothing unless a console was opened). Attached only while the panel is on screen; no timer.
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _window = Window.GetWindow(this);
        if (_window is not null)
        {
            _window.Activated += OnWindowActivated;
        }

        // A section asked for before the page was on screen (a deep link on the first visit).
        ShowPendingSection();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_window is not null)
        {
            _window.Activated -= OnWindowActivated;
            _window = null;
        }
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is SetupPanelViewModel old)
        {
            old.SectionRequested -= OnSectionRequested;
        }

        if (e.NewValue is SetupPanelViewModel current)
        {
            current.SectionRequested += OnSectionRequested;
        }
    }

    private void OnSectionRequested(object? sender, EventArgs e) => ShowPendingSection();

    /// <summary>
    /// Scrolls the asked-for section to the top of the page, once the layout has caught up with whatever the request unfolded. Nothing else happens: no
    /// focus moves, nothing runs.
    /// </summary>
    private void ShowPendingSection()
    {
        if (!IsLoaded || DataContext is not SetupPanelViewModel { PendingSection: { } section })
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (DataContext is not SetupPanelViewModel viewModel || viewModel.PendingSection is null)
            {
                return;
            }

            viewModel.PendingSection = null;
            FrameworkElement? target = section switch
            {
                SetupPanelViewModel.CredentialsSection => CredentialsSection,
                SetupPanelViewModel.GuardrailSection => GuardrailSection,
                SetupPanelViewModel.ConfigEditorSection => ConfigEditorSection,
                _ => null,
            };

            ScrollToTop(target);
        }));
    }

    private void ScrollToTop(FrameworkElement? target)
    {
        if (target is null || !target.IsVisible)
        {
            return;
        }

        try
        {
            var top = target.TransformToAncestor(PageContent).Transform(new Point(0, 0)).Y;
            PageScroll.ScrollToVerticalOffset(Math.Max(0, top - 8));
        }
        catch (InvalidOperationException)
        {
            target.BringIntoView();
        }
    }

    private void OnWindowActivated(object? sender, EventArgs e)
    {
        if (DataContext is SetupPanelViewModel viewModel)
        {
            viewModel.Credentials.NotifyReturned();
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Inside the config editor, Ctrl+F is the editor's (its setting search, or AvalonEdit's search in RAW).
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control && !InlineEditor.IsKeyboardFocusWithin)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && DataContext is SetupPanelViewModel chooser && chooser.HandleConnectorSetupEscape())
        {
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && DataContext is SetupPanelViewModel { IsGuardrailReviewOpen: true } viewModel)
        {
            viewModel.CancelGuardrailReviewCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && DataContext is SetupPanelViewModel setup && (setup.Review.HandleEscape() || setup.Routing.HandleEscape() || setup.Batch.HandleEscape() || setup.Monitoring.HandleEscape()))
        {
            e.Handled = true;
        }
    }
}
