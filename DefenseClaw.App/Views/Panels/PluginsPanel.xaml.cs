using System.Windows.Controls;

namespace DefenseClaw.App.Views.Panels;

/// <summary>
/// View for the Plugins panel. Paired with
/// <see cref="ViewModels.PluginsPanelViewModel"/>; the shell binds the two together in
/// <see cref="Services.PanelCatalog"/>.
/// </summary>
public partial class PluginsPanel : UserControl
{
    public PluginsPanel()
    {
        InitializeComponent();
    }
}