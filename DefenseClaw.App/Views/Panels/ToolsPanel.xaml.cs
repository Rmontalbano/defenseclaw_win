using System.Windows.Controls;
using DefenseClaw.App.Views.Panels.Govern;

namespace DefenseClaw.App.Views.Panels;

/// <summary>
/// View for the Tools panel. Paired with
/// <see cref="ViewModels.ToolsPanelViewModel"/>; the shell binds the two together in
/// <see cref="Services.PanelCatalog"/>.
/// </summary>
public partial class ToolsPanel : UserControl
{
    public ToolsPanel()
    {
        InitializeComponent();
        GovernPanelKeys.Attach(this, () => PageToolbar.SearchBox);
    }
}
