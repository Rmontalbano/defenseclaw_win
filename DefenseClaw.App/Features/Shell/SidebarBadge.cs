using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using Wpf.Ui.Controls;

namespace DefenseClaw.App.Views.Shell;

/// <summary>
/// WPF-UI's <see cref="InfoBadge"/>, made decorative for UI Automation: the sidebar entry it sits on already says what it
/// counts in its own name ("Alerts, 441 unacknowledged findings"), so the capsule must not be announced a second time as a
/// nameless control reading "441". Same idea as <c>DcSymbolIcon</c>.
/// </summary>
public sealed class DcInfoBadge : InfoBadge
{
    public DcInfoBadge()
    {
        // WPF-UI styles its badge with an implicit style (TargetType InfoBadge), and an implicit style is matched on the element's
        // exact type, so a subclass gets no template and draws nothing. Point at the base type's style, as DcNavigationHeader does;
        // being a resource reference, it follows the theme.
        SetResourceReference(StyleProperty, typeof(InfoBadge));
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new DecorativePeer(this);

    private sealed class DecorativePeer : FrameworkElementAutomationPeer
    {
        public DecorativePeer(DcInfoBadge owner)
            : base(owner)
        {
        }

        protected override string GetClassNameCore() => nameof(DcInfoBadge);

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;

        protected override bool IsControlElementCore() => false;

        protected override bool IsContentElementCore() => false;

        protected override List<AutomationPeer>? GetChildrenCore() => null;
    }
}

/// <summary>
/// The badge on one sidebar entry: a count (red capsule, the Alerts entry) or a caution mark (the Overview entry while the
/// gateway is degraded), shown or hidden as the view-model says, with the entry's accessible name and tooltip following it.
/// <para>
/// Built once per entry and attached to <see cref="NavigationViewItem.InfoBadge"/>; after that only its text, visibility and the
/// entry's name change, so nothing has to be rebuilt when the window's look changes: the capsule's colours are WPF-UI's
/// <c>InfoBadge*SeverityBackgroundBrush</c> resources, which the appearance bridge maps onto the tone tokens (and updates live).
/// It holds no subscription of its own: the window feeds it from its view-model, which owns the app-lifetime ones.
/// </para>
/// </summary>
internal sealed class SidebarBadge
{
    /// <summary>
    /// The folded pane's dot, 8 DIPs across. WPF-UI puts a folded entry's badge at the top-right of its 40 DIP slot; this margin moves
    /// the dot onto the top-right corner of the centred 16 DIP icon, where it ends just before the 14 DIP lane of the pane's scroll bar.
    /// </summary>
    private const double DotSize = 8;

    private static readonly Thickness DotMargin = new(0, 9, 12, 0);

    private readonly DcNavigationItem _item;
    private readonly string _title;
    private readonly string _baseToolTip;
    private readonly DcInfoBadge _badge;
    private readonly System.Windows.Controls.ControlTemplate _dot;

    /// <param name="item">The sidebar entry.</param>
    /// <param name="title">The entry's name without the badge ("Alerts").</param>
    /// <param name="baseToolTip">The entry's tooltip without the badge ("Alerts (Ctrl+2)").</param>
    /// <param name="severity">The capsule's colour: Critical (red) for the count, Caution (amber) for the gateway.</param>
    public SidebarBadge(DcNavigationItem item, string title, string baseToolTip, InfoBadgeSeverity severity)
    {
        _item = item ?? throw new ArgumentNullException(nameof(item));
        _title = title;
        _baseToolTip = baseToolTip;

        _badge = new DcInfoBadge
        {
            Severity = severity,
            Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        item.InfoBadge = _badge;
        _dot = DotTemplate(severity);
    }

    /// <summary>The capsule itself (for a test or a render).</summary>
    internal InfoBadge Badge => _badge;

    /// <summary>
    /// Shows the badge with <paramref name="text"/> ("441"; "!" for the caution mark, which WPF-UI's template draws as text only)
    /// when <paramref name="description"/> is not empty, and hides it otherwise. The description is what a screen reader hears
    /// after the title and what the tooltip adds: "441 unacknowledged findings", "gateway stopped".
    /// </summary>
    public void Set(string text, string description)
    {
        var shown = description.Length > 0;

        _badge.Value = text;
        _badge.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;

        // The capsule is decorative to a screen reader (the entry's name below says it all), but a tool that inspects the element itself finds the
        // same sentence on it: "7 new since last visit".
        if (shown)
        {
            AutomationProperties.SetName(_badge, description);
        }
        else
        {
            _badge.ClearValue(AutomationProperties.NameProperty);
        }

        var name = shown ? $"{_title}, {description}" : _title;
        var before = AutomationProperties.GetName(_item);
        AutomationProperties.SetName(_item, name);
        _item.ToolTip = shown ? $"{_baseToolTip} — {description}" : _baseToolTip;

        // The entry's name is what changed; a screen reader that is listening hears it without re-reading the sidebar.
        if (!string.Equals(before, name, StringComparison.Ordinal) &&
            UIElementAutomationPeer.FromElement(_item) is { } peer)
        {
            peer.RaisePropertyChangedEvent(AutomationElementIdentifiers.NameProperty, before, name);
        }
    }

    /// <summary>
    /// Folded to its icons, the pane has no room for the capsule beside the icon (WPF-UI's capsule is at least 16 DIPs wide and covered
    /// half the icon): the badge becomes a dot in its colour on the icon's corner, and the count stays in the entry's name and tooltip.
    /// Opened, it is WPF-UI's capsule again.
    /// </summary>
    public void Fold(bool folded)
    {
        if (folded)
        {
            _badge.Template = _dot;
            _badge.HorizontalAlignment = HorizontalAlignment.Right;
            _badge.VerticalAlignment = VerticalAlignment.Top;
            _badge.Margin = DotMargin;
        }
        else
        {
            _badge.ClearValue(System.Windows.Controls.Control.TemplateProperty);
            _badge.ClearValue(FrameworkElement.MarginProperty);
            _badge.HorizontalAlignment = HorizontalAlignment.Center;
            _badge.VerticalAlignment = VerticalAlignment.Center;
        }
    }

    /// <summary>A disc filled with the brush WPF-UI's capsule uses for <paramref name="severity"/>, by resource reference so it follows the look live.</summary>
    private static System.Windows.Controls.ControlTemplate DotTemplate(InfoBadgeSeverity severity)
    {
        var disc = new FrameworkElementFactory(typeof(System.Windows.Controls.Border));
        disc.SetValue(FrameworkElement.WidthProperty, DotSize);
        disc.SetValue(FrameworkElement.HeightProperty, DotSize);
        disc.SetValue(System.Windows.Controls.Border.CornerRadiusProperty, new CornerRadius(DotSize / 2));
        disc.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, $"InfoBadge{severity}SeverityBackgroundBrush");
        return new System.Windows.Controls.ControlTemplate(typeof(InfoBadge)) { VisualTree = disc };
    }
}
