using System.Collections;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace DefenseClaw.App.Views.Controls;

/// <summary>
/// The flush inspector pane the Mac opens beside a list (InspectorLayoutPolicy: a fixed pane against a divider, not a card):
/// a full-height column on the right with a header - <see cref="Title"/>, <see cref="Badges"/>, a close button - and a
/// scrolling body, which is the control's <see cref="ContentControl.Content"/> (key/value grids, section heads, a mono
/// <c>DcCodeBlock</c>).
/// <para>
/// <b>Placement.</b> Put it in a two-column grid next to the list (column 0 the list, column 1 <c>Auto</c>): the default
/// style puts it in column 1 at 360 DIPs (the Mac's 320 floor, a little wider for Windows' type; <c>MinWidth</c> 320,
/// <c>MaxWidth</c> 400). When the panel is compact (<c>CompactLayout.IsCompact</c>, under 960 DIPs - a pane that wide beside a
/// usable list does not fit) it takes the whole row (column 0 spanning both) and the panel hides the list while it is open.
/// The Mac has no narrow case; this is the "detail replaces the list" rule these panels already had.
/// </para>
/// <para>
/// <b>Open and close.</b> <see cref="IsOpen"/> (bind it to "something is selected") shows it with a ~120 ms fade - none when
/// the user has turned client-area animation off (<see cref="SystemParameters.ClientAreaAnimation"/>) - and collapses it at
/// once when it goes false. <see cref="CloseCommand"/> is what the X and Esc inside the pane run; the host panel also
/// handles Esc from its list. The control itself is never focusable, so opening it never takes focus from the list; the X is
/// an ordinary tab stop inside it.
/// </para>
/// <para>
/// <b>Automation.</b> It is a pane named by <c>AutomationProperties.Name</c> (else <see cref="Title"/>), so its contents are
/// read as one group, like the Mac's <c>.accessibilityLabel("Details")</c>.
/// </para>
/// </summary>
public sealed class DcInspector : ContentControl
{
    /// <summary>How long the pane takes to fade in.</summary>
    public static readonly Duration FadeDuration = new(TimeSpan.FromMilliseconds(120));

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(DcInspector), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty BadgesProperty = DependencyProperty.Register(
        nameof(Badges), typeof(object), typeof(DcInspector), new PropertyMetadata(null, OnBadgesChanged));

    public static readonly DependencyProperty CloseCommandProperty = DependencyProperty.Register(
        nameof(CloseCommand), typeof(ICommand), typeof(DcInspector), new PropertyMetadata(null));

    public static readonly DependencyProperty CloseAutomationNameProperty = DependencyProperty.Register(
        nameof(CloseAutomationName), typeof(string), typeof(DcInspector), new PropertyMetadata("Close details"));

    public static readonly DependencyProperty IsOpenProperty = DependencyProperty.Register(
        nameof(IsOpen), typeof(bool), typeof(DcInspector), new PropertyMetadata(false, OnIsOpenChanged));

    static DcInspector()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(DcInspector), new FrameworkPropertyMetadata(typeof(DcInspector)));
        IsTabStopProperty.OverrideMetadata(typeof(DcInspector), new FrameworkPropertyMetadata(false));
        FocusableProperty.OverrideMetadata(typeof(DcInspector), new FrameworkPropertyMetadata(false));
    }

    /// <summary>The pane's heading ("Alert details").</summary>
    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>Badges after the title (a severity badge): any element.</summary>
    public object? Badges
    {
        get => GetValue(BadgesProperty);
        set => SetValue(BadgesProperty, value);
    }

    /// <summary>Run by the X (and by Esc while focus is inside the pane). The panel binds it to its "clear selection" command.</summary>
    public ICommand? CloseCommand
    {
        get => (ICommand?)GetValue(CloseCommandProperty);
        set => SetValue(CloseCommandProperty, value);
    }

    /// <summary>The close button's UI Automation name ("Close alert details").</summary>
    public string CloseAutomationName
    {
        get => (string)GetValue(CloseAutomationNameProperty);
        set => SetValue(CloseAutomationNameProperty, value);
    }

    /// <summary>Shown while true (the style collapses the control otherwise).</summary>
    public bool IsOpen
    {
        get => (bool)GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    /// <summary>The close button, once the template is applied.</summary>
    public FrameworkElement? CloseButton
    {
        get
        {
            _ = ApplyTemplate();
            return GetTemplateChild("PART_Close") as FrameworkElement;
        }
    }

    /// <summary>Esc inside the pane closes it, unless something inside (a text box, an open list) used it first.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && e.Key == Key.Escape && CloseCommand is { } close && close.CanExecute(null))
        {
            close.Execute(null);
            e.Handled = true;
        }
    }

    protected override IEnumerator LogicalChildren
    {
        get
        {
            var children = new List<object>(2);
            if (Content is not null)
            {
                children.Add(Content);
            }

            if (Badges is not null)
            {
                children.Add(Badges);
            }

            return children.GetEnumerator();
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new InspectorPeer(this);

    private static void OnBadgesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var inspector = (DcInspector)d;
        if (e.OldValue is not null)
        {
            inspector.RemoveLogicalChild(e.OldValue);
        }

        if (e.NewValue is not null)
        {
            inspector.AddLogicalChild(e.NewValue);
        }
    }

    private static void OnIsOpenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var inspector = (DcInspector)d;
        if (e.NewValue is true && SystemParameters.ClientAreaAnimation)
        {
            inspector.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, FadeDuration) { FillBehavior = FillBehavior.Stop });
        }
        else
        {
            inspector.BeginAnimation(OpacityProperty, null);
        }
    }

    private sealed class InspectorPeer : FrameworkElementAutomationPeer
    {
        public InspectorPeer(DcInspector owner)
            : base(owner)
        {
        }

        protected override string GetClassNameCore() => nameof(DcInspector);

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Pane;

        protected override string GetNameCore()
        {
            var name = AutomationProperties.GetName(Owner);
            if (!string.IsNullOrEmpty(name))
            {
                return name;
            }

            var title = ((DcInspector)Owner).Title;
            return string.IsNullOrEmpty(title) ? "Details" : title;
        }

        protected override bool IsControlElementCore() => true;
    }
}
