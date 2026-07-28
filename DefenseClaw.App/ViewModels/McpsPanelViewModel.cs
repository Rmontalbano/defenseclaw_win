using DefenseClaw.App.Services;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// View-model for the MCPs panel.
/// <para>
/// Placeholder: it renders a title and a "coming soon" card. A later milestone replaces
/// this file and <c>Views\Panels\McpsPanel.xaml</c> - the pair is the whole contract.
/// Load data in <see cref="PanelViewModelBase.InitializeAsync"/>, which the shell calls
/// once, the first time this panel is navigated to.
/// </para>
/// </summary>
public sealed partial class McpsPanelViewModel : PanelViewModelBase
{
    public McpsPanelViewModel(AppServices services)
        : base(services)
    {
    }

    public override string Title => "MCPs";

    public override string Description => "Configured MCP servers, their transports and scan results.";
}