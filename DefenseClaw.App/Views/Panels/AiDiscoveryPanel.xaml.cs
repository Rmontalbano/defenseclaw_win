using System.Windows.Controls;

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
    }
}