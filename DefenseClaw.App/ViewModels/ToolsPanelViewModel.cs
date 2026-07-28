using DefenseClaw.App.Services;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// View-model for the Tools panel.
/// <para>
/// Placeholder: it renders a title and a "coming soon" card. A later milestone replaces
/// this file and <c>Views\Panels\ToolsPanel.xaml</c> - the pair is the whole contract.
/// Load data in <see cref="PanelViewModelBase.InitializeAsync"/>, which the shell calls
/// once, the first time this panel is navigated to.
/// </para>
/// </summary>
public sealed partial class ToolsPanelViewModel : PanelViewModelBase
{
    public ToolsPanelViewModel(AppServices services)
        : base(services)
    {
    }

    public override string Title => "Tools";

    public override string Description => "Tool catalog with capability classes and enforcement decisions.";
}