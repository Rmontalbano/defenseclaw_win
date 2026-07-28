using System.Windows.Controls;

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
    }
}