using System.Windows.Controls;

namespace DefenseClaw.App.Views.Panels;

/// <summary>
/// View for the Activity panel. Paired with
/// <see cref="ViewModels.ActivityPanelViewModel"/>; the shell binds the two together in
/// <see cref="Services.PanelCatalog"/>.
/// </summary>
public partial class ActivityPanel : UserControl
{
    public ActivityPanel()
    {
        InitializeComponent();
    }
}