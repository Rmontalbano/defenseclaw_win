using System.Windows.Controls;

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
    }
}