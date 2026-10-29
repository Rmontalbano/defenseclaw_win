using System.Windows.Controls;
using System.Windows.Input;
using DefenseClaw.App.ViewModels;

namespace DefenseClaw.App.Views.Panels.Govern;

/// <summary>
/// The page-level keys every Govern panel shares: Ctrl+F focuses the filter box, Esc closes the topmost
/// overlay (confirm dialog, result card, the panel's form) and otherwise clears a typed filter.
/// F5 is bound by the shell to the view-model's <c>RefreshCommand</c>; nothing to do for it here.
/// </summary>
public static class GovernPanelKeys
{
    public static void Attach(UserControl panel, Func<Control?> filterBox)
    {
        ArgumentNullException.ThrowIfNull(panel);
        ArgumentNullException.ThrowIfNull(filterBox);

        panel.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (filterBox() is { IsVisible: true, IsEnabled: true } box)
                {
                    box.Focus();
                    Keyboard.Focus(box);
                    (box as TextBox)?.SelectAll();
                    e.Handled = true;
                }

                return;
            }

            if (e.Key != Key.Escape || panel.DataContext is not GovernPanelViewModelBase viewModel)
            {
                return;
            }

            if (viewModel.HandleEscape())
            {
                e.Handled = true;
            }
            else if (filterBox() is { IsKeyboardFocusWithin: true } && viewModel.FilterText.Length > 0)
            {
                viewModel.ClearFilterCommand.Execute(null);
                e.Handled = true;
            }
        };
    }
}
