using System.Windows.Controls;

namespace DefenseClaw.App.Views.Panels;

/// <summary>
/// View for the Setup panel. Paired with
/// <see cref="ViewModels.SetupPanelViewModel"/>; the shell binds the two together in
/// <see cref="Services.PanelCatalog"/>.
/// </summary>
public partial class SetupPanel : UserControl
{
    public SetupPanel()
    {
        InitializeComponent();
    }
}