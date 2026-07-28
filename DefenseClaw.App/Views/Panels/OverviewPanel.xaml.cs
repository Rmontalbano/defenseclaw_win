using System.Windows.Controls;

namespace DefenseClaw.App.Views.Panels;

/// <summary>
/// View for the Overview panel. Paired with
/// <see cref="ViewModels.OverviewPanelViewModel"/>; the shell binds the two together in
/// <see cref="Services.PanelCatalog"/>.
/// </summary>
public sealed partial class OverviewPanel : UserControl
{
    public OverviewPanel()
    {
        InitializeComponent();
    }
}
