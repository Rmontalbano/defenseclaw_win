using System.Windows.Controls;

namespace DefenseClaw.App.Views.Panels;

/// <summary>
/// View for the Settings panel. Paired with <see cref="ViewModels.SettingsPanelViewModel"/>; the shell binds the two together in
/// <see cref="Services.PanelCatalog"/>. Reached from the footer of the sidebar, from Ctrl+, and from the tray flyout's gear.
/// </summary>
public sealed partial class SettingsPanel : UserControl
{
    public SettingsPanel()
    {
        InitializeComponent();
    }
}
