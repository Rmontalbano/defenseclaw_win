using DefenseClaw.Core.Audit;
using Drawing = System.Drawing;

namespace DefenseClaw.App.Services;

/// <summary>What <see cref="TrayShieldPresenter.Update"/> changed on the tray.</summary>
/// <param name="Icon">The icon was replaced.</param>
/// <param name="Tooltip">The tooltip text was replaced.</param>
internal readonly record struct TrayShieldChange(bool Icon, bool Tooltip);

/// <summary>
/// What the tray shows, decided in one place: the icon (<see cref="ShieldIconFactory.KeyFor"/>) and the tooltip
/// (<see cref="AlertCountPresentation.TrayTooltip"/>) from the three things the app knows, the gateway's snapshot, the unacknowledged
/// findings (<see cref="AlertCounts"/>) and whether a scan is running (<see cref="ScanActivity"/>). The tray calls <see cref="Update"/> from
/// each of the notifications it already has - <see cref="GatewayMonitor.StateChanged"/>, <see cref="AlertCountsService.Changed"/>,
/// <see cref="ScanActivity.Changed"/> - and nothing here polls or keeps a timer.
/// <para>
/// <b>Change, not churn.</b> The icon is replaced only when its <see cref="TrayShieldKey"/> differs from the one showing, and the
/// tooltip only when its text does. A notification that leaves both as they were (a snapshot that differs in a detail the shield and
/// the tooltip do not show) makes no icon, no GDI handle and no call into the shell; one that moves only the number past nine (11 findings
/// to 12 is "9+" either way) makes a new sentence and no icon. Findings come and go all day; the shield changes only when a bucket, a
/// state or a scan does.
/// </para>
/// <para>
/// <b>Ownership.</b> Every icon comes from the <c>create</c> delegate, new and the presenter's to dispose
/// (<see cref="ShieldIconFactory.CreateIcon(TrayShieldKey)"/> never shares one). It is handed to <c>assignIcon</c>, which gives it to the tray
/// library; H.NotifyIcon.Wpf 2.3.2 disposes the icon it had when it is given another, and this disposes the replaced one as well, after the
/// swap, so no version of the library, past or future, can leave a handle behind (<c>Icon.Dispose</c> is idempotent). The one showing is
/// disposed by <see cref="Dispose"/>, which the tray calls once the native icon is gone. UI thread only.
/// </para>
/// </summary>
internal sealed class TrayShieldPresenter : IDisposable
{
    private readonly Func<TrayShieldKey, Drawing.Icon> _create;
    private readonly Action<Drawing.Icon> _assignIcon;
    private readonly Action<string> _assignTooltip;

    private TrayShieldKey? _key;
    private Drawing.Icon? _owned;
    private string? _tooltip;
    private bool _disposed;

    /// <param name="create">Makes the icon for a key: a new one, every call.</param>
    /// <param name="assignIcon">Shows an icon; takes it over (the tray library disposes the one it replaces).</param>
    /// <param name="assignTooltip">Shows the tooltip text.</param>
    public TrayShieldPresenter(Func<TrayShieldKey, Drawing.Icon> create, Action<Drawing.Icon> assignIcon, Action<string> assignTooltip)
    {
        _create = create ?? throw new ArgumentNullException(nameof(create));
        _assignIcon = assignIcon ?? throw new ArgumentNullException(nameof(assignIcon));
        _assignTooltip = assignTooltip ?? throw new ArgumentNullException(nameof(assignTooltip));
    }

    /// <summary>The icon showing, or null before the first <see cref="Update"/>.</summary>
    public TrayShieldKey? Key => _key;

    /// <summary>The tooltip showing, or null before the first <see cref="Update"/>.</summary>
    public string? Tooltip => _tooltip;

    /// <summary>
    /// Brings the tray up to date with what the app knows now, touching only what differs.
    /// </summary>
    /// <param name="snapshot">The gateway state, including whether monitoring is paused.</param>
    /// <param name="counts">The unacknowledged findings, or null while <see cref="AlertCountsService.HasData"/> is false.</param>
    /// <param name="scanning">True while a scan is running.</param>
    /// <returns>What was replaced.</returns>
    public TrayShieldChange Update(GatewaySnapshot snapshot, AlertCounts? counts, bool scanning)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (_disposed)
        {
            return default;
        }

        // The words first: if drawing the icon fails (WPF not able to render), the tooltip is still right.
        var tooltip = AlertCountPresentation.TrayTooltip(snapshot, counts, scanning);
        var tooltipChanged = !string.Equals(tooltip, _tooltip, StringComparison.Ordinal);
        if (tooltipChanged)
        {
            _assignTooltip(tooltip);
            _tooltip = tooltip;
        }

        var key = ShieldIconFactory.KeyFor(snapshot, counts?.Total, scanning);
        var iconChanged = _key != key;
        if (iconChanged)
        {
            var fresh = _create(key);
            try
            {
                _assignIcon(fresh);
            }
            catch
            {
                // The tray refused it, so nobody else owns it.
                fresh.Dispose();
                throw;
            }

            var replaced = _owned;
            _owned = fresh;
            _key = key;

            // After the swap, so the shell never sees a handle destroyed while it is still the one registered.
            replaced?.Dispose();
        }

        return new TrayShieldChange(iconChanged, tooltipChanged);
    }

    /// <summary>Disposes the icon showing. Call it once the native tray icon has been torn down; later updates do nothing.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _owned?.Dispose();
        _owned = null;
    }
}
