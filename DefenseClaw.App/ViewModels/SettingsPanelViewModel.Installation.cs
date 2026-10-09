using CommunityToolkit.Mvvm.ComponentModel;
using DefenseClaw.Core.Paths;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// Settings → Connection → Installation (CUST-308): which DefenseClaw installation this run of the app drives, what chose it, whether the app
/// may change it, and why not when it may not. Read-only: the selection is made once, at start, from the environment
/// (<c>DEFENSECLAW_CONFIG</c>, <c>DEFENSECLAW_HOME</c>), the developer runtime selector (Settings → Advanced), the Windows managed layout, or
/// the user default (<see cref="InstallationContext"/>), so there is nothing to apply here: the "override" row only shows the developer
/// selector's folder, which is changed under Advanced and takes effect the next time the app starts.
/// </summary>
public sealed partial class SettingsPanelViewModel
{
    /// <summary>What chose the installation: "User default", "DEFENSECLAW_HOME", "Managed installation (Cisco Secure Client)"…</summary>
    [ObservableProperty]
    private string _installationSelectedBy = string.Empty;

    /// <summary>"Unmanaged — setup changes allowed", "Managed enterprise — read only" or "Invalid installation selection — read only".</summary>
    [ObservableProperty]
    private string _installationAccessText = string.Empty;

    /// <summary>The access badge's tone: Ok for a writable installation, Medium for a managed one, High for an invalid selection.</summary>
    [ObservableProperty]
    private string _installationAccessTone = "Ok";

    /// <summary>One sentence on why the installation is read-only; empty while it is writable (the row's caption is hidden then).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInstallationReason))]
    private string _installationReason = string.Empty;

    public bool HasInstallationReason => InstallationReason.Length > 0;

    /// <summary>The developer selector's folder this run was started against, or "None (automatic)".</summary>
    [ObservableProperty]
    private string _installationOverride = string.Empty;

    /// <summary>Why the Start-the-gateway-automatically switch is off (the installation is read-only); null while it works.</summary>
    public string? AutoStartBlockedReason => Services.Installation.BlockedReason;

    /// <summary>The switch can be changed: a start is a change, so it is not offered for a read-only installation.</summary>
    public bool CanChangeAutoStart => AutoStartBlockedReason is null;

    /// <summary>
    /// The switch's tooltip: why it is off, else what it does. A tooltip on a disabled switch is how the page says why; an enabled one
    /// keeps the caption beside it as its only explanation, as before.
    /// </summary>
    public string? AutoStartToolTip => AutoStartBlockedReason;

    /// <summary>Copies what the app resolved at start (and what config.yaml has said since) into the block. From memory: no I/O.</summary>
    private void ShowInstallation()
    {
        var context = Services.Installation.Context;
        InstallationSelectedBy = context.SourceLabel;
        InstallationAccessText = context.AccessLabel;
        InstallationAccessTone = context.Access switch
        {
            InstallationAccess.ManagedReadOnly => "Medium",
            InstallationAccess.InvalidReadOnly => "High",
            _ => "Ok",
        };

        InstallationReason = context.BlockedReason ?? string.Empty;
        InstallationOverride = context.OverridePath ?? "None (automatic)";

        OnPropertyChanged(nameof(AutoStartBlockedReason));
        OnPropertyChanged(nameof(CanChangeAutoStart));
        OnPropertyChanged(nameof(AutoStartToolTip));
    }

    /// <summary>
    /// The installation changed while the page is open (config.yaml was edited, or fixed): the block, the switch and the update sentence (which
    /// says whether the runtime is upgraded from here) are drawn again.
    /// </summary>
    protected override void OnInstallationChanged()
    {
        ShowInstallation();
        ShowUpdates();
    }
}
