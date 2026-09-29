using System.Windows.Controls;
using System.Windows.Input;
using DefenseClaw.App.ViewModels;

namespace DefenseClaw.App.Views.Panels;

/// <summary>
/// View for the Inventory panel. Paired with
/// <see cref="ViewModels.InventoryPanelViewModel"/>; the shell binds the two together in
/// <see cref="Services.PanelCatalog"/>.
/// </summary>
public partial class InventoryPanel : UserControl
{
    public InventoryPanel()
    {
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>Ctrl+F focuses the search box; Esc closes the review dialog.</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (DataContext is InventoryPanelViewModel { Review.IsOpen: true })
            {
                return;
            }

            _ = SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && DataContext is InventoryPanelViewModel viewModel && viewModel.HandleEscape())
        {
            e.Handled = true;
        }
    }
}
