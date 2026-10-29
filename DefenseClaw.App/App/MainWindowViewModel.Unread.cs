using CommunityToolkit.Mvvm.ComponentModel;
using DefenseClaw.App.Services;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// The sidebar's "new since last visit" capsules (CUST-265) on the Audit, Activity and AI Discovery entries: what <see cref="UnreadCountsService"/> counts,
/// worded by <see cref="UnreadPresentation"/> ("7", "99+", "7 new since last visit"). Fed by the service's one <c>Changed</c>, which the window's
/// view-model owns for as long as the window lives - that subscription is what starts the service, and disposing the view-model is what stops it.
/// </summary>
public sealed partial class MainWindowViewModel
{
    private static readonly IReadOnlyDictionary<string, UnreadBadgeState> NoUnread = new Dictionary<string, UnreadBadgeState>();

    private IReadOnlyDictionary<string, UnreadBadgeState> _unreadBadges = NoUnread;

    /// <summary>
    /// The capsule of each tracked panel (<see cref="UnreadCountsService.TrackedPanels"/>) by its id: empty for nothing new, and always empty for the panel
    /// on screen. Replaced as a whole when any of them changes. Internal: it is made of internal types, and the window reads it in code, not by binding.
    /// </summary>
    internal IReadOnlyDictionary<string, UnreadBadgeState> UnreadBadges
    {
        get => _unreadBadges;
        private set => SetProperty(ref _unreadBadges, value);
    }

    /// <summary>The capsule of <paramref name="panelId"/>; none for a panel that has no count.</summary>
    internal UnreadBadgeState UnreadFor(string panelId) =>
        _unreadBadges.TryGetValue(panelId, out var state) ? state : UnreadBadgeState.None;

    /// <summary>The service raises on the UI thread, so the property below is set where the window reads it.</summary>
    private void OnUnreadChanged(object? sender, UnreadChangedEventArgs e) => ApplyUnread(e.Counts);

    private void ApplyUnread(IReadOnlyDictionary<string, int> counts)
    {
        var states = new Dictionary<string, UnreadBadgeState>(counts.Count, StringComparer.Ordinal);
        foreach (var (panelId, count) in counts)
        {
            states[panelId] = UnreadPresentation.StateOf(count);
        }

        UnreadBadges = states;
    }
}
