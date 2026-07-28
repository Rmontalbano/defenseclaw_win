using DefenseClaw.App.Services;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// View-model for the Logs panel.
/// <para>
/// Placeholder: it renders a title and a "coming soon" card. A later milestone replaces
/// this file and <c>Views\Panels\LogsPanel.xaml</c> - the pair is the whole contract.
/// Load data in <see cref="PanelViewModelBase.InitializeAsync"/>, which the shell calls
/// once, the first time this panel is navigated to.
/// </para>
/// </summary>
public sealed partial class LogsPanelViewModel : PanelViewModelBase
{
    public LogsPanelViewModel(AppServices services)
        : base(services)
    {
    }

    public override string Title => "Logs";

    public override string Description => "Tail of gateway.log and watchdog.log, with level filtering.";
}