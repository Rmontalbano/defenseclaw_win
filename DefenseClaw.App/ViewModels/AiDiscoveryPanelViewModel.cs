using DefenseClaw.App.Services;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// View-model for the AI Discovery panel.
/// <para>
/// Placeholder: it renders a title and a "coming soon" card. A later milestone replaces
/// this file and <c>Views\Panels\AiDiscoveryPanel.xaml</c> - the pair is the whole contract.
/// Load data in <see cref="PanelViewModelBase.InitializeAsync"/>, which the shell calls
/// once, the first time this panel is navigated to.
/// </para>
/// </summary>
public sealed partial class AiDiscoveryPanelViewModel : PanelViewModelBase
{
    public AiDiscoveryPanelViewModel(AppServices services)
        : base(services)
    {
    }

    public override string Title => "AI Discovery";

    public override string Description => "Discovered agents, with the evidence and confidence behind each one.";
}