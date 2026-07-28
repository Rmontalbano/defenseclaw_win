using System.Windows.Controls;

namespace DefenseClaw.App.Views.Panels;

/// <summary>
/// View for the Logs panel. Paired with
/// <see cref="ViewModels.LogsPanelViewModel"/>; the shell binds the two together in
/// <see cref="Services.PanelCatalog"/>.
/// </summary>
public partial class LogsPanel : UserControl
{
    public LogsPanel()
    {
        InitializeComponent();
    }
}