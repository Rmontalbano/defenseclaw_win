using DefenseClaw.App.Services;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// View-model for the Plugins panel.
/// <para>
/// Placeholder: it renders a title and a "coming soon" card. A later milestone replaces
/// this file and <c>Views\Panels\PluginsPanel.xaml</c> - the pair is the whole contract.
/// Load data in <see cref="PanelViewModelBase.InitializeAsync"/>, which the shell calls
/// once, the first time this panel is navigated to.
/// </para>
/// </summary>
public sealed partial class PluginsPanelViewModel : PanelViewModelBase
{
    public PluginsPanelViewModel(AppServices services)
        : base(services)
    {
    }

    public override string Title => "Plugins";

    public override string Description => "Discovered plugins and their governance state.";
}