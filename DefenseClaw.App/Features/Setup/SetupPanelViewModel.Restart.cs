using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// The Setup hub's banner for a queued gateway restart (CUST-267; the TUI's "Restart pending: ...  [G] restart now  [C] clear"): the app-wide
/// <see cref="RestartQueue"/> shown at the top of the page, with <b>Restart now</b> (the reviewed <c>defenseclaw-gateway restart</c>, off with the
/// installation's sentence first) and <b>Clear</b>. It reads the queue, never copies it, so it is right when the panel comes back after any
/// number of panel switches, and it follows the queue only while the panel is on screen (<see cref="OnActivated"/> / <see cref="OnDeactivated"/>).
/// The readiness checklist's "Restart Pending" row is the same queue (<see cref="ReadinessViewModel"/>).
/// </summary>
public sealed partial class SetupPanelViewModel
{
    /// <summary>A gateway restart is queued: the banner is drawn.</summary>
    public bool HasRestartPending => Services.RestartQueue.IsPending;

    /// <summary>The banner's heading.</summary>
    public string RestartPendingTitle => RestartQueueText.Title;

    /// <summary>The queued reasons, the time the first was queued and what a queued restart means; empty while nothing is queued.</summary>
    public string RestartPendingDetail => RestartQueueText.Detail(Services.RestartQueue);

    /// <summary>
    /// Why Restart now is off - the installation is managed or invalid (its sentence, which comes first), or the gateway cannot be asked yet or at
    /// all - or null while it may be pressed.
    /// </summary>
    public string? RestartNowBlockedReason => RestartQueueReview.BlockedReason(Services);

    /// <summary>Restart now's tooltip: what it does, or why it is off.</summary>
    public string RestartNowTip => RestartNowBlockedReason ??
        "Review defenseclaw-gateway restart. The gateway is not restarted until you confirm it there.";

    /// <summary>Opens the review of <c>defenseclaw-gateway restart</c>; nothing restarts until it is confirmed.</summary>
    [RelayCommand(CanExecute = nameof(CanRestartNow))]
    private void RestartNow() => _ = RestartQueueReview.Open(Review, Services);

    private bool CanRestartNow() => HasRestartPending && RestartNowBlockedReason is null;

    /// <summary>Forgets the queued restart without restarting (what was saved stays saved and applies at the next restart).</summary>
    [RelayCommand(CanExecute = nameof(HasRestartPending))]
    private void ClearRestart() => _ = Services.RestartQueue.Clear();

    /// <summary>The queue changed (UI thread): the banner and the readiness row are drawn again.</summary>
    private void OnRestartQueueChanged(object? sender, EventArgs e)
    {
        RefreshRestartBanner();
        Readiness.Rebuild();
    }

    /// <summary>Tells the bindings the banner may have changed; also what the gateway state and the installation call when they move, since they decide Restart now.</summary>
    private void RefreshRestartBanner()
    {
        OnPropertyChanged(nameof(HasRestartPending));
        OnPropertyChanged(nameof(RestartPendingDetail));
        OnPropertyChanged(nameof(RestartNowBlockedReason));
        OnPropertyChanged(nameof(RestartNowTip));
        RestartNowCommand.NotifyCanExecuteChanged();
        ClearRestartCommand.NotifyCanExecuteChanged();
    }
}
