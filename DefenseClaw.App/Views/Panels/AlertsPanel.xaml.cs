using System.Windows.Controls;

namespace DefenseClaw.App.Views.Panels;

/// <summary>
/// View for the Alerts panel. Paired with
/// <see cref="ViewModels.AlertsPanelViewModel"/>; the shell binds the two together in
/// <see cref="Services.PanelCatalog"/>.
/// </summary>
public partial class AlertsPanel : UserControl
{
    public AlertsPanel()
    {
        InitializeComponent();
    }
}