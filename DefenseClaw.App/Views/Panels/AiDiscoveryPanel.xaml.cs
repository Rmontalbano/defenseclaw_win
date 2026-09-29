using System.Windows.Controls;
using System.Windows.Input;
using DefenseClaw.App.ViewModels;

namespace DefenseClaw.App.Views.Panels;

/// <summary>
/// View for the AI Discovery panel. Paired with
/// <see cref="ViewModels.AiDiscoveryPanelViewModel"/>; the shell binds the two together in
/// <see cref="Services.PanelCatalog"/>.
/// </summary>
public partial class AiDiscoveryPanel : UserControl
{
    public AiDiscoveryPanel()
    {
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>Ctrl+F focuses the search box; Esc closes the review dialog.</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (DataContext is AiDiscoveryPanelViewModel { Review.IsOpen: true })
            {
                return;
            }

            _ = SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && DataContext is AiDiscoveryPanelViewModel viewModel && viewModel.HandleEscape())
        {
            e.Handled = true;
        }
    }
}
