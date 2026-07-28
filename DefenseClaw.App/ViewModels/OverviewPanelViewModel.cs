using DefenseClaw.App.Services;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// View-model for the Overview panel.
/// <para>
/// Placeholder: it renders a title and a "coming soon" card. A later milestone replaces
/// this file and <c>Views\Panels\OverviewPanel.xaml</c> - the pair is the whole contract.
/// Load data in <see cref="PanelViewModelBase.InitializeAsync"/>, which the shell calls
/// once, the first time this panel is navigated to.
/// </para>
/// </summary>
public sealed partial class OverviewPanelViewModel : PanelViewModelBase
{
    public OverviewPanelViewModel(AppServices services)
        : base(services)
    {
    }

    public override string Title => "Overview";

    public override string Description => "Service health, scanners, enforcement posture and doctor findings at a glance.";
}