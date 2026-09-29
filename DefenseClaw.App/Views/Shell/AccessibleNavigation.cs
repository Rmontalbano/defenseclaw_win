using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Threading;
using Wpf.Ui.Controls;

namespace DefenseClaw.App.Views.Shell;

/// <summary>
/// A sidebar entry that says what it is to UI Automation.
/// <para>
/// WPF-UI's own <c>NavigationViewItem</c> peer reports a name and a SelectionItem pattern, but the
/// sidebar's items host (an <c>ItemsControl</c>) wraps every item in an <i>ItemAutomationPeer</i>
/// ("DataItem"), and that wrapper forwards only the container's name and children — never its
/// patterns. Measured on the live app: every entry was a nameless-pattern DataItem, so a screen
/// reader could read "Overview" but not invoke or select it, and a UIA test could not drive the
/// sidebar at all. <see cref="DcNavigationItemPeer"/> therefore exposes Invoke and SelectionItem on
/// the item itself (what a host that does not wrap sees) <i>and</i> hangs an equivalent action peer
/// off it as a child (what the wrapper's children show); see the peer for the rule.
/// </para>
/// <para>
/// Visually and behaviourally it is a plain <see cref="NavigationViewItem"/>: same template (the
/// default style key is inherited from the base), same click handling. <see cref="InvokeNavigation"/> is the
/// exact code path of a mouse click, so navigation, the page provider and page caching are untouched.
/// </para>
/// </summary>
public sealed class DcNavigationItem : NavigationViewItem
{
    /// <summary>Navigates to this item's page exactly as a click would.</summary>
    internal void InvokeNavigation() => OnClick();

    protected override AutomationPeer OnCreateAutomationPeer() => new DcNavigationItemPeer(this);
}

/// <summary>
/// A sidebar group heading ("Monitor", "Govern", …) that announces its own text.
/// Without a peer the items host fell back to the type name, so a screen reader read
/// "Wpf.Ui.Controls.NavigationViewItemHeader".
/// </summary>
public sealed class DcNavigationHeader : NavigationViewItemHeader
{
    public DcNavigationHeader()
    {
        // WPF-UI styles its header with an *implicit* style (TargetType NavigationViewItemHeader), and an
        // implicit style is matched on the element's exact type. A subclass therefore got no template at
        // all and measured 0 x 0: the heading text silently disappeared while its accessibility name
        // survived. (DcNavigationItem does not have this problem: NavigationViewItem is styled through its
        // default style key, which a subclass inherits.) Pointing at the base type's style keeps the look
        // WPF-UI ships and, being a DynamicResource, follows the theme.
        SetResourceReference(StyleProperty, typeof(NavigationViewItemHeader));

        // A group label, not a fourth kind of entry: smaller and quieter than the items under it.
        FontSize = 12;
        FontWeight = FontWeights.SemiBold;
        SetResourceReference(ForegroundProperty, "TextFillColorSecondaryBrush");
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new DcNavigationHeaderPeer(this);
}

internal sealed class DcNavigationHeaderPeer : FrameworkElementAutomationPeer
{
    public DcNavigationHeaderPeer(DcNavigationHeader owner)
        : base(owner)
    {
    }

    private DcNavigationHeader Header => (DcNavigationHeader)Owner;

    protected override string GetNameCore()
    {
        var name = AutomationProperties.GetName(Header);
        return !string.IsNullOrEmpty(name) ? name : Header.Text ?? string.Empty;
    }

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;

    protected override string GetClassNameCore() => "NavigationGroupHeader";

    protected override bool IsControlElementCore() => true;

    protected override bool IsContentElementCore() => true;

    /// <summary>The header's own TextBlock would repeat the name; the peer is the whole story.</summary>
    protected override List<AutomationPeer>? GetChildrenCore() => null;
}

/// <summary>
/// UIA peer of <see cref="DcNavigationItem"/>: a list item with Invoke and SelectionItem.
/// <para>
/// <b>The wrapped-host rule.</b> When an <see cref="ItemAutomationPeer"/> wraps this peer, WPF sets
/// <see cref="AutomationPeer.EventsSource"/> on it, and the wrapper's children are this peer's
/// children. In that case the only child is a <see cref="DcNavigationActionPeer"/> carrying the
/// patterns, so <c>DataItem "Overview"</c> contains <c>ListItem "Overview" [Invoke, SelectionItem]</c>.
/// When nothing wraps it (no <c>EventsSource</c>) the peer has no children: it is already a
/// ListItem with the patterns, and the inner icon/label text blocks would only repeat the name.
/// </para>
/// </summary>
internal sealed class DcNavigationItemPeer : FrameworkElementAutomationPeer, IInvokeProvider, ISelectionItemProvider
{
    private DcNavigationActionPeer? _action;

    public DcNavigationItemPeer(DcNavigationItem owner)
        : base(owner)
    {
    }

    private DcNavigationItem Item => (DcNavigationItem)Owner;

    /// <summary>True while the page behind this entry is the one shown.</summary>
    internal bool IsCurrent => Item.IsActive;

    protected override string GetClassNameCore() => "NavigationItem";

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ListItem;

    protected override string GetNameCore()
    {
        var name = AutomationProperties.GetName(Item);
        return !string.IsNullOrEmpty(name) ? name : Item.Content as string ?? string.Empty;
    }

    protected override List<AutomationPeer>? GetChildrenCore()
    {
        if (EventsSource is null)
        {
            return null;
        }

        _action ??= new DcNavigationActionPeer(this);
        return new List<AutomationPeer> { _action };
    }

    public override object? GetPattern(PatternInterface patternInterface) =>
        patternInterface is PatternInterface.Invoke or PatternInterface.SelectionItem
            ? this
            : base.GetPattern(patternInterface);

    /// <summary>Marshals like a button's Invoke: return to the caller, navigate on the UI thread.</summary>
    internal void Navigate()
    {
        if (!IsEnabled())
        {
            throw new ElementNotEnabledException();
        }

        _ = Item.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(Item.InvokeNavigation));
    }

    void IInvokeProvider.Invoke() => Navigate();

    void ISelectionItemProvider.Select()
    {
        if (!IsCurrent)
        {
            Navigate();
        }
    }

    void ISelectionItemProvider.AddToSelection()
    {
        if (!IsCurrent)
        {
            Navigate();
        }
    }

    void ISelectionItemProvider.RemoveFromSelection()
    {
        // A sidebar always shows exactly one page: there is no "none selected" state to move to.
    }

    bool ISelectionItemProvider.IsSelected => IsCurrent;

    IRawElementProviderSimple? ISelectionItemProvider.SelectionContainer => null;
}

/// <summary>
/// The Invoke / SelectionItem carrier that hangs under a wrapped sidebar item (see
/// <see cref="DcNavigationItemPeer"/>). It has no element of its own — every property is the item
/// peer's, so name, bounds, focus and enabled state can never drift from the real control.
/// </summary>
internal sealed class DcNavigationActionPeer : AutomationPeer, IInvokeProvider, ISelectionItemProvider
{
    private readonly DcNavigationItemPeer _item;

    public DcNavigationActionPeer(DcNavigationItemPeer item)
    {
        _item = item;
    }

    protected override string GetNameCore() => _item.GetName();

    protected override string GetClassNameCore() => "NavigationItem";

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ListItem;

    protected override string GetAutomationIdCore() => string.Empty;

    protected override Rect GetBoundingRectangleCore() => _item.GetBoundingRectangle();

    protected override List<AutomationPeer>? GetChildrenCore() => null;

    protected override Point GetClickablePointCore() => _item.GetClickablePoint();

    protected override string GetHelpTextCore() => _item.GetHelpText();

    protected override string GetAcceleratorKeyCore() => _item.GetAcceleratorKey();

    protected override string GetAccessKeyCore() => _item.GetAccessKey();

    protected override string GetItemStatusCore() => _item.GetItemStatus();

    protected override string GetItemTypeCore() => _item.GetItemType();

    protected override AutomationPeer? GetLabeledByCore() => null;

    protected override AutomationOrientation GetOrientationCore() => AutomationOrientation.Vertical;

    protected override bool HasKeyboardFocusCore() => _item.HasKeyboardFocus();

    protected override bool IsContentElementCore() => true;

    protected override bool IsControlElementCore() => true;

    protected override bool IsEnabledCore() => _item.IsEnabled();

    protected override bool IsKeyboardFocusableCore() => _item.IsKeyboardFocusable();

    protected override bool IsOffscreenCore() => _item.IsOffscreen();

    protected override bool IsPasswordCore() => false;

    protected override bool IsRequiredForFormCore() => false;

    protected override void SetFocusCore() => _item.SetFocus();

    public override object? GetPattern(PatternInterface patternInterface) =>
        patternInterface is PatternInterface.Invoke or PatternInterface.SelectionItem ? this : null;

    void IInvokeProvider.Invoke() => _item.Navigate();

    void ISelectionItemProvider.Select()
    {
        if (!_item.IsCurrent)
        {
            _item.Navigate();
        }
    }

    void ISelectionItemProvider.AddToSelection()
    {
        if (!_item.IsCurrent)
        {
            _item.Navigate();
        }
    }

    void ISelectionItemProvider.RemoveFromSelection()
    {
    }

    bool ISelectionItemProvider.IsSelected => _item.IsCurrent;

    IRawElementProviderSimple? ISelectionItemProvider.SelectionContainer => null;
}
