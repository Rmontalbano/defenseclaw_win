using System.Windows.Controls;
using System.Windows.Input;
using DefenseClaw.App.ViewModels;

namespace DefenseClaw.App.Views.Panels;

/// <summary>
/// View for the Registries panel. Paired with
/// <see cref="ViewModels.RegistriesPanelViewModel"/>; the shell binds the two together in
/// <see cref="Services.PanelCatalog"/>.
/// </summary>
public partial class RegistriesPanel : UserControl
{
    public RegistriesPanel()
    {
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>Esc closes the add form (the review dialog handles its own Esc).</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is RegistriesPanelViewModel viewModel && viewModel.HandleEscape())
        {
            e.Handled = true;
        }
    }
}
