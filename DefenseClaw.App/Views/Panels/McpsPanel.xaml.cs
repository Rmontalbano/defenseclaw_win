using System.Windows.Controls;
using DefenseClaw.App.Views.Panels.Govern;

namespace DefenseClaw.App.Views.Panels;

/// <summary>
/// View for the MCPs panel. Paired with
/// <see cref="ViewModels.McpsPanelViewModel"/>; the shell binds the two together in
/// <see cref="Services.PanelCatalog"/>.
/// </summary>
public partial class McpsPanel : UserControl
{
    public McpsPanel()
    {
        InitializeComponent();
        GovernPanelKeys.Attach(this, () => PageToolbar.SearchBox);
    }
}
