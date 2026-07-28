using System.Windows.Controls;

namespace DefenseClaw.App.Views.Panels;

/// <summary>
/// View for the Audit panel. Paired with
/// <see cref="ViewModels.AuditPanelViewModel"/>; the shell binds the two together in
/// <see cref="Services.PanelCatalog"/>.
/// </summary>
public partial class AuditPanel : UserControl
{
    public AuditPanel()
    {
        InitializeComponent();
    }
}