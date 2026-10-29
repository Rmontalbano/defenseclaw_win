using System.Windows.Controls;

namespace DefenseClaw.App.Views.Panels;

/// <summary>
/// View for the Sandboxes panel (CUST-383). Paired with <see cref="ViewModels.SandboxesPanelViewModel"/>; the shell binds the two together in
/// <see cref="Services.PanelCatalog"/>. It explains and runs nothing.
/// </summary>
public partial class SandboxesPanel : UserControl
{
    public SandboxesPanel()
    {
        InitializeComponent();
    }
}
