using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DefenseClaw.App.ViewModels;

namespace DefenseClaw.App.Views.Panels;

/// <summary>
/// View for the Setup panel. Paired with
/// <see cref="ViewModels.SetupPanelViewModel"/>; the shell binds the two together in
/// <see cref="Services.PanelCatalog"/>.
/// <para>
/// Keyboard: <b>Ctrl+F</b> moves focus to the wizard search box; <b>Esc</b> dismisses the guardrail
/// review while it is open. Both are handled on the preview phase so they work wherever focus is
/// inside the panel. F5 is bound by the shell to the view-model's <c>RefreshCommand</c>.
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
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_window is not null)
        {
            _window.Activated -= OnWindowActivated;
            _window = null;
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
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && DataContext is SetupPanelViewModel { IsGuardrailReviewOpen: true } viewModel)
        {
            viewModel.CancelGuardrailReviewCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && DataContext is SetupPanelViewModel setup && (setup.Review.HandleEscape() || setup.Routing.HandleEscape() || setup.Batch.HandleEscape()))
        {
            e.Handled = true;
        }
    }
}
