using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// The Overview's row for a queued gateway restart (CUST-267): the app-wide <see cref="RestartQueue"/> in "What needs attention", with <b>Restart now</b>
/// (the reviewed <c>defenseclaw-gateway restart</c>, the same review the Setup hub's banner and the gateway button open) and <b>Clear</b>. The row is
/// drawn from the queue every time the list is built, so it is right whenever the panel comes back; while the panel is on screen it follows the
/// queue's <see cref="RestartQueue.Changed"/>, and the monitor's poll that sees the restart (which clears the queue) rebuilds it as it does every row.
/// </summary>
public sealed partial class OverviewPanelViewModel
{
    private string? _restartPendingNowBlocked;

    /// <summary>
    /// Adds the "Gateway restart pending" row while something is queued: Medium, the readiness checklist's "warn", with what was queued, when, and what
    /// it means. Derived from the queue alone - no I/O.
    /// </summary>
    private void AppendRestartPending(List<AttentionRow> rows)
    {
        var queue = Services.RestartQueue;
        if (!queue.IsPending)
        {
            return;
        }

        rows.Add(new AttentionRow
        {
            Title = RestartQueueText.Title,
            Detail = RestartQueueText.Detail(queue),
            SeverityKey = "Medium",
            OffersRestart = true,
        });
    }

    /// <summary>
    /// Why the row's Restart now is off - the installation is managed or invalid (its sentence, first), or the gateway this panel is showing cannot be
    /// asked - or null while it may be pressed.
    /// </summary>
    public string? RestartPendingNowBlockedReason => RestartQueueReview.BlockedReason(Services, _snapshot);

    /// <summary>Restart now's tooltip: what it does, or why it is off.</summary>
    public string RestartPendingNowTip => RestartPendingNowBlockedReason ??
        "Review defenseclaw-gateway restart. The gateway is not restarted until you confirm it there.";

    /// <summary>Opens the review of <c>defenseclaw-gateway restart</c>; nothing restarts until it is confirmed.</summary>
    [RelayCommand(CanExecute = nameof(CanRestartPendingNow))]
    private void RestartPendingNow() => _ = RestartQueueReview.Open(Review, Services, () => RefreshAfterActionAsync(), _snapshot);

    private bool CanRestartPendingNow() => RestartPendingNowBlockedReason is null;

    /// <summary>Forgets the queued restart without restarting (what was saved stays saved and applies at the next restart).</summary>
    [RelayCommand]
    private void ClearPendingRestart() => _ = Services.RestartQueue.Clear();

    /// <summary>The queue changed (UI thread): the list is built again, which draws or drops the row.</summary>
    private void OnRestartQueueChanged(object? sender, EventArgs e) => BuildAttention(_snapshot);

    /// <summary>
    /// Tells the bindings the buttons' answer may have moved. The list is rebuilt on every poll, so this runs often and says nothing unless the reason
    /// changed: a poll that finds the gateway as it was costs a comparison.
    /// </summary>
    private void RefreshRestartPendingActions()
    {
        var blocked = RestartPendingNowBlockedReason;
        if (string.Equals(blocked, _restartPendingNowBlocked, StringComparison.Ordinal))
        {
            return;
        }

        _restartPendingNowBlocked = blocked;
        OnPropertyChanged(nameof(RestartPendingNowBlockedReason));
        OnPropertyChanged(nameof(RestartPendingNowTip));
        RestartPendingNowCommand.NotifyCanExecuteChanged();
    }
}
