using DefenseClaw.App.Services;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// View-model for the Audit panel.
/// <para>
/// Placeholder: it renders a title and a "coming soon" card. A later milestone replaces
/// this file and <c>Views\Panels\AuditPanel.xaml</c> - the pair is the whole contract.
/// Load data in <see cref="PanelViewModelBase.InitializeAsync"/>, which the shell calls
/// once, the first time this panel is navigated to.
/// </para>
/// </summary>
public sealed partial class AuditPanelViewModel : PanelViewModelBase
{
    public AuditPanelViewModel(AppServices services)
        : base(services)
    {
    }

    public override string Title => "Audit";

    public override string Description => "Browse audit_events by bucket, severity, connector and time.";
}