using DefenseClaw.App.Services;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// View-model for the Inventory panel.
/// <para>
/// Placeholder: it renders a title and a "coming soon" card. A later milestone replaces
/// this file and <c>Views\Panels\InventoryPanel.xaml</c> - the pair is the whole contract.
/// Load data in <see cref="PanelViewModelBase.InitializeAsync"/>, which the shell calls
/// once, the first time this panel is navigated to.
/// </para>
/// </summary>
public sealed partial class InventoryPanelViewModel : PanelViewModelBase
{
    public InventoryPanelViewModel(AppServices services)
        : base(services)
    {
    }

    public override string Title => "Inventory";

    public override string Description => "AI components and SDK rollup read from inventory.db.";
}