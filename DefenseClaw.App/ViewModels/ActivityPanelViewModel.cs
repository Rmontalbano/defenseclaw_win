using DefenseClaw.App.Services;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// View-model for the Activity panel.
/// <para>
/// Placeholder: it renders a title and a "coming soon" card. A later milestone replaces
/// this file and <c>Views\Panels\ActivityPanel.xaml</c> - the pair is the whole contract.
/// Load data in <see cref="PanelViewModelBase.InitializeAsync"/>, which the shell calls
/// once, the first time this panel is navigated to.
/// </para>
/// </summary>
public sealed partial class ActivityPanelViewModel : PanelViewModelBase
{
    public ActivityPanelViewModel(AppServices services)
        : base(services)
    {
    }

    public override string Title => "Activity";

    public override string Description => "Every CLI invocation this app made: exact argv, live output and exit code.";
}