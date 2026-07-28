using DefenseClaw.App.Services;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// View-model for the Setup panel.
/// <para>
/// Placeholder: it renders a title and a "coming soon" card. A later milestone replaces
/// this file and <c>Views\Panels\SetupPanel.xaml</c> - the pair is the whole contract.
/// Load data in <see cref="PanelViewModelBase.InitializeAsync"/>, which the shell calls
/// once, the first time this panel is navigated to.
/// </para>
/// </summary>
public sealed partial class SetupPanelViewModel : PanelViewModelBase
{
    public SetupPanelViewModel(AppServices services)
        : base(services)
    {
    }

    public override string Title => "Setup";

    public override string Description => "Launch the defenseclaw setup wizards. The config editor arrives here too.";
}