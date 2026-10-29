using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DefenseClaw.App.ViewModels;

namespace DefenseClaw.App.Views.Panels;

/// <summary>
/// View for the Agents panel. Paired with <see cref="AgentsPanelViewModel"/>; the shell binds the two together in <see cref="Services.PanelCatalog"/>.
/// Ctrl+F is <see cref="ApplicationCommands.Find"/>'s own gesture; the panel handles that command by focusing the filter box. Esc clears the filter.
/// </summary>
public sealed partial class AgentsPanel : UserControl
{
    public AgentsPanel()
    {
        InitializeComponent();

        _ = CommandBindings.Add(new CommandBinding(ApplicationCommands.Find, OnFind, OnCanFind));
        KeyDown += OnKeyDown;
    }

    /// <summary>Moves keyboard focus to the filter box (the toolbar's search) and selects its text, ready to type over.</summary>
    public void FocusFilter() => PageToolbar.FocusSearch();

    private void OnCanFind(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = true;

    private void OnFind(object sender, ExecutedRoutedEventArgs e)
    {
        FocusFilter();
        e.Handled = true;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !e.Handled && DataContext is AgentsPanelViewModel viewModel && viewModel.HandleEscape())
        {
            e.Handled = true;
        }
    }

    private void OnTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is AgentTreeNodeVm node && DataContext is AgentsPanelViewModel viewModel)
        {
            viewModel.FocusAgent(node.AgentId);
        }
    }
}
