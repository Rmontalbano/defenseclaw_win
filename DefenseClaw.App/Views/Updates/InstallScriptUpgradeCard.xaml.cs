using System.Windows.Controls;

namespace DefenseClaw.App.Views.Updates;

/// <summary>The install.ps1 upgrade card (CUST-346). All state lives in the bound <see cref="ViewModels.Updates.InstallScriptUpgradeViewModel"/>.</summary>
public partial class InstallScriptUpgradeCard : UserControl
{
    public InstallScriptUpgradeCard()
    {
        InitializeComponent();
    }
}
