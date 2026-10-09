using CommunityToolkit.Mvvm.Input;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// The Overview's answer to a read-only installation (CUST-308): a banner, "State-changing actions disabled: &lt;reason&gt;", with a
/// "Review Installation" button that opens Settings → Connection → Installation, and every Quick Action that changes something turned off with
/// the reason as its tooltip (<see cref="PanelViewModelBase.InstallationBlockedReason"/>). The Mac shows the same banner
/// (<c>OverviewView.swift</c>), in the Services card. Nothing is shown for a writable installation, which is every installation unless a managed
/// layout, <c>deployment_mode: managed_enterprise</c> or an invalid selection says otherwise.
/// </summary>
public sealed partial class OverviewPanelViewModel
{
    /// <summary>"State-changing actions disabled: &lt;reason&gt;", or null while the installation may be changed.</summary>
    public string? InstallationBanner => Services.Installation.BannerText;

    public bool HasInstallationBanner => InstallationBanner is not null;

    /// <summary>Scan Skills' tooltip: what it does, or why it is off.</summary>
    public string ScanSkillsTip => InstallationBlockedReason ??
        "Scan every configured skill: defenseclaw skill scan --all. Asks for confirmation first.";

    /// <summary>The banner's button: Settings, where the Installation block says what was selected and why it is read-only.</summary>
    [RelayCommand]
    private void ReviewInstallation() => RequestNavigation("settings");

    /// <summary>
    /// The installation turned read-only (or writable) while the page was open: the banner, the gateway buttons and the Quick Actions are drawn
    /// again (the commands themselves are asked again by <see cref="PanelViewModelBase"/>).
    /// </summary>
    protected override void OnInstallationChanged()
    {
        OnPropertyChanged(nameof(InstallationBanner));
        OnPropertyChanged(nameof(HasInstallationBanner));
        OnPropertyChanged(nameof(ScanSkillsTip));
        ApplyGatewayActions(_snapshot);
        ApplyQuickActions();
        RefreshRestartPendingActions();
    }
}
