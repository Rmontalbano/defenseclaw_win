using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DefenseClaw.App.ViewModels;

namespace DefenseClaw.App.Views.Panels;

/// <summary>
/// View for the Policies panel (CUST-281), paired with <see cref="PoliciesPanelViewModel"/>. Ctrl+F is
/// <see cref="ApplicationCommands.Find"/>'s own gesture, handled by focusing the toolbar's filter box; Esc closes the topmost
/// transient surface (review, form, output, detail) through the view-model.
/// </summary>
public sealed partial class PoliciesPanel : UserControl
{
    public PoliciesPanel()
    {
        InitializeComponent();

        _ = CommandBindings.Add(new CommandBinding(ApplicationCommands.Find, OnFind, OnCanFind));
        KeyDown += OnKeyDown;
    }

    /// <summary>Moves keyboard focus to the filter box and selects its text.</summary>
    public void FocusFilter() => PageToolbar.FocusSearch();

    private void OnCanFind(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = true;

    private void OnFind(object sender, ExecutedRoutedEventArgs e)
    {
        FocusFilter();
        e.Handled = true;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !e.Handled && DataContext is PoliciesPanelViewModel viewModel && viewModel.HandleEscape())
        {
            e.Handled = true;
        }
    }
}
