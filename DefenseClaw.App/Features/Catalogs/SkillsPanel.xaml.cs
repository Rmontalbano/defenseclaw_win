using System.Windows.Controls;
using DefenseClaw.App.Views.Panels.Govern;

namespace DefenseClaw.App.Views.Panels;

/// <summary>
/// View for the Skills panel. Paired with
/// <see cref="ViewModels.SkillsPanelViewModel"/>; the shell binds the two together in
/// <see cref="Services.PanelCatalog"/>.
/// </summary>
public partial class SkillsPanel : UserControl
{
    public SkillsPanel()
    {
        InitializeComponent();
        GovernPanelKeys.Attach(this, () => PageToolbar.SearchBox);
    }
}
