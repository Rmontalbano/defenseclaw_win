using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace DefenseClaw.App.Views.Controls;

/// <summary>
/// The one compact row at the top of a panel (the Mac's unified toolbar): the panel's tinted glyph, its name in 20 px
/// semibold with an optional caption beside it (counts, "updated 12:04"), then on the right icon-only buttons, a slot for
/// the shared connector picker and a search box. It replaces the 26 px title and subtitle block plus the text-labelled
/// buttons that used to sit beside it, so a panel gets back the rows they took.
/// <para>
/// <b>Slots.</b> <see cref="Leading"/> follows the title (a busy ring); <see cref="Actions"/> holds the page's actions as
/// icon-only buttons (put them in a <c>StackPanel Style="{StaticResource DcToolbarActions}"</c>, whose buttons wear the
/// <c>DcToolbarIconButton</c> style: a ghost button with a 150 ms tooltip; give each an
/// <c>AutomationProperties.Name</c> - there is no label to fall back on - and an <c>AcceleratorKey</c> where a shortcut
/// exists); <see cref="ConnectorSlot"/> is reserved for the connector picker a later change shares between panels and is
/// empty for now.
/// </para>
/// <para>
/// <b>The panel's subtitle</b> is not shown: bind it to <see cref="TitleToolTip"/>, where it is one hover away, and say
/// what a panel with nothing to show is for in its empty state.
/// </para>
/// <para>
/// <b>Search.</b> <see cref="HasSearch"/> shows the box; its text is <see cref="SearchText"/> (two-way, pushed on every
/// change, or after <see cref="SearchDelay"/> milliseconds of quiet for a filter that is costly to re-run). Ctrl+F is the
/// panel's (<c>ApplicationCommands.Find</c> or its own preview handler), which calls <see cref="FocusSearch"/>; Esc in the
/// box clears it, and a further Esc is the panel's (close the detail pane). The glyph is <c>ctl:DcIcon.Symbol</c> and its
/// tint <c>ctl:DcIcon.Section</c>, set on the toolbar the way they were on the page header.
/// </para>
/// </summary>
public sealed class DcPageToolbar : Control
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(DcPageToolbar), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty CaptionProperty = DependencyProperty.Register(
        nameof(Caption), typeof(string), typeof(DcPageToolbar), new PropertyMetadata(string.Empty, OnCaptionChanged));

    private static readonly DependencyPropertyKey HasCaptionPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(HasCaption), typeof(bool), typeof(DcPageToolbar), new PropertyMetadata(false));

    /// <summary>True while <see cref="Caption"/> has text (the template collapses the caption otherwise).</summary>
    public static readonly DependencyProperty HasCaptionProperty = HasCaptionPropertyKey.DependencyProperty;

    public static readonly DependencyProperty TitleToolTipProperty = DependencyProperty.Register(
        nameof(TitleToolTip), typeof(object), typeof(DcPageToolbar), new PropertyMetadata(null));

    public static readonly DependencyProperty LeadingProperty = DependencyProperty.Register(
        nameof(Leading), typeof(object), typeof(DcPageToolbar), new PropertyMetadata(null, OnSlotChanged));

    public static readonly DependencyProperty ActionsProperty = DependencyProperty.Register(
        nameof(Actions), typeof(object), typeof(DcPageToolbar), new PropertyMetadata(null, OnSlotChanged));

    public static readonly DependencyProperty ConnectorSlotProperty = DependencyProperty.Register(
        nameof(ConnectorSlot), typeof(object), typeof(DcPageToolbar), new PropertyMetadata(null, OnSlotChanged));

    public static readonly DependencyProperty HasSearchProperty = DependencyProperty.Register(
        nameof(HasSearch), typeof(bool), typeof(DcPageToolbar), new PropertyMetadata(false));

    public static readonly DependencyProperty SearchTextProperty = DependencyProperty.Register(
        nameof(SearchText),
        typeof(string),
        typeof(DcPageToolbar),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.Journal, null, CoerceSearchText, false, UpdateSourceTrigger.PropertyChanged));

    public static readonly DependencyProperty SearchPlaceholderProperty = DependencyProperty.Register(
        nameof(SearchPlaceholder), typeof(string), typeof(DcPageToolbar), new PropertyMetadata("Search"));

    public static readonly DependencyProperty SearchAutomationNameProperty = DependencyProperty.Register(
        nameof(SearchAutomationName), typeof(string), typeof(DcPageToolbar), new PropertyMetadata("Search"));

    public static readonly DependencyProperty SearchHelpTextProperty = DependencyProperty.Register(
        nameof(SearchHelpText), typeof(string), typeof(DcPageToolbar), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty SearchWidthProperty = DependencyProperty.Register(
        nameof(SearchWidth), typeof(double), typeof(DcPageToolbar), new PropertyMetadata(240d));

    public static readonly DependencyProperty SearchDelayProperty = DependencyProperty.Register(
        nameof(SearchDelay), typeof(int), typeof(DcPageToolbar), new PropertyMetadata(0, (d, _) => ((DcPageToolbar)d).BindSearchBox()));

    private TextBox? _searchBox;
    private TextBlock? _caption;

    static DcPageToolbar()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(DcPageToolbar), new FrameworkPropertyMetadata(typeof(DcPageToolbar)));
        IsTabStopProperty.OverrideMetadata(typeof(DcPageToolbar), new FrameworkPropertyMetadata(false));
        FocusableProperty.OverrideMetadata(typeof(DcPageToolbar), new FrameworkPropertyMetadata(false));
    }

    /// <summary>The panel's name, in 20 px semibold at the left. Bind it to the view-model's <c>Title</c>.</summary>
    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>A quiet line beside the title: counts, "updated 12:04". Empty: nothing is shown.</summary>
    public string Caption
    {
        get => (string)GetValue(CaptionProperty);
        set => SetValue(CaptionProperty, value);
    }

    public bool HasCaption => (bool)GetValue(HasCaptionProperty);

    /// <summary>What hovering the title says: the panel's one-sentence description.</summary>
    public object? TitleToolTip
    {
        get => GetValue(TitleToolTipProperty);
        set => SetValue(TitleToolTipProperty, value);
    }

    /// <summary>Content right after the title (a busy ring).</summary>
    public object? Leading
    {
        get => GetValue(LeadingProperty);
        set => SetValue(LeadingProperty, value);
    }

    /// <summary>The page's actions, right-aligned: icon-only buttons in a <c>DcToolbarActions</c> stack.</summary>
    public object? Actions
    {
        get => GetValue(ActionsProperty);
        set => SetValue(ActionsProperty, value);
    }

    /// <summary>Where the shared connector picker will go (between the actions and the search box). Empty for now.</summary>
    public object? ConnectorSlot
    {
        get => GetValue(ConnectorSlotProperty);
        set => SetValue(ConnectorSlotProperty, value);
    }

    /// <summary>Shows the search box at the right end.</summary>
    public bool HasSearch
    {
        get => (bool)GetValue(HasSearchProperty);
        set => SetValue(HasSearchProperty, value);
    }

    /// <summary>The search box's text. Two-way; a panel binds it to its filter text.</summary>
    public string SearchText
    {
        get => (string)GetValue(SearchTextProperty);
        set => SetValue(SearchTextProperty, value);
    }

    public string SearchPlaceholder
    {
        get => (string)GetValue(SearchPlaceholderProperty);
        set => SetValue(SearchPlaceholderProperty, value);
    }

    /// <summary>The box's UI Automation name ("Filter alerts").</summary>
    public string SearchAutomationName
    {
        get => (string)GetValue(SearchAutomationNameProperty);
        set => SetValue(SearchAutomationNameProperty, value);
    }

    /// <summary>The box's UI Automation help text: what it matches, and the shortcuts.</summary>
    public string SearchHelpText
    {
        get => (string)GetValue(SearchHelpTextProperty);
        set => SetValue(SearchHelpTextProperty, value);
    }

    /// <summary>Width of the search box (the Mac's is about a fifth of the window; this is what a 940 px window can spare).</summary>
    public double SearchWidth
    {
        get => (double)GetValue(SearchWidthProperty);
        set => SetValue(SearchWidthProperty, value);
    }

    /// <summary>Milliseconds of quiet before typing reaches <see cref="SearchText"/>; 0 pushes every keystroke.</summary>
    public int SearchDelay
    {
        get => (int)GetValue(SearchDelayProperty);
        set => SetValue(SearchDelayProperty, value);
    }

    /// <summary>The search box, once the template has been applied (null before, and when the template has none).</summary>
    public TextBox? SearchBox
    {
        get
        {
            _ = ApplyTemplate();
            return _searchBox;
        }
    }

    /// <summary>Moves keyboard focus to the search box and selects its text, ready to type over. False when there is no visible box.</summary>
    public bool FocusSearch()
    {
        if (SearchBox is not { IsVisible: true, IsEnabled: true } box)
        {
            return false;
        }

        _ = box.Focus();
        _ = Keyboard.Focus(box);
        box.SelectAll();
        return true;
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _searchBox = GetTemplateChild("PART_Search") as TextBox;
        BindSearchBox();

        if (GetTemplateChild("Glyph") is DcSymbolIcon glyph)
        {
            _ = BindingOperations.SetBinding(glyph, Wpf.Ui.Controls.SymbolIcon.SymbolProperty, new Binding { Source = this, Path = new PropertyPath(DcIcon.SymbolProperty) });
        }

        if (_caption is not null)
        {
            _caption.ToolTipOpening -= OnCaptionToolTipOpening;
        }

        _caption = GetTemplateChild("CaptionText") as TextBlock;
        if (_caption is not null)
        {
            _caption.ToolTipOpening += OnCaptionToolTipOpening;
        }
    }

    /// <summary>The caption says it all in its tooltip, but only when it is cut short: a whole caption needs no second copy.</summary>
    private void OnCaptionToolTipOpening(object sender, ToolTipEventArgs e)
    {
        if (sender is TextBlock caption && !IsTrimmed(caption))
        {
            e.Handled = true;
        }
    }

    /// <summary>True when <paramref name="block"/> shows less of its text than it has (an ellipsis is drawn).</summary>
    private static bool IsTrimmed(TextBlock block)
    {
        if (string.IsNullOrEmpty(block.Text))
        {
            return false;
        }

        var typeface = new Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch);
        var text = new FormattedText(
            block.Text,
            CultureInfo.CurrentUICulture,
            block.FlowDirection,
            typeface,
            block.FontSize,
            Brushes.Black,
            VisualTreeHelper.GetDpi(block).PixelsPerDip);
        return text.WidthIncludingTrailingWhitespace > block.ActualWidth + 0.5;
    }

    /// <summary>Esc in the search box clears it (and is used up); with nothing to clear it passes on to the panel.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && e.Key == Key.Escape && SearchText.Length > 0 && FromSearchBox(e.OriginalSource))
        {
            SearchText = string.Empty;
            e.Handled = true;
        }
    }

    /// <summary>True when a routed event began in the search box (the box itself, or something drawn inside it).</summary>
    private bool FromSearchBox(object? origin) =>
        _searchBox is not null && origin is DependencyObject node && (ReferenceEquals(node, _searchBox) || (node is Visual visual && _searchBox.IsAncestorOf(visual)));

    protected override AutomationPeer OnCreateAutomationPeer() => new ToolbarPeer(this);

    /// <summary>
    /// The three slots are the toolbar's logical children (as a <c>HeaderedContentControl</c>'s header is): a walk of the
    /// logical tree - an automation or test tool looking for a busy ring, a resource or name lookup - reaches the buttons
    /// that the template only shows through a presenter.
    /// </summary>
    protected override IEnumerator LogicalChildren
    {
        get
        {
            var slots = new List<object>(3);
            foreach (var slot in new[] { Leading, Actions, ConnectorSlot })
            {
                if (slot is not null)
                {
                    slots.Add(slot);
                }
            }

            return slots.GetEnumerator();
        }
    }

    private static void OnSlotChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var toolbar = (DcPageToolbar)d;
        if (e.OldValue is not null)
        {
            toolbar.RemoveLogicalChild(e.OldValue);
        }

        if (e.NewValue is not null)
        {
            toolbar.AddLogicalChild(e.NewValue);
        }
    }

    /// <summary>
    /// The box's text is bound here rather than in the template so the delay can be a property: typing reaches
    /// <see cref="SearchText"/> after <see cref="SearchDelay"/> ms of quiet, while a clear (Esc, a reset) goes the other way
    /// at once.
    /// </summary>
    private void BindSearchBox()
    {
        if (_searchBox is null)
        {
            return;
        }

        _ = BindingOperations.SetBinding(
            _searchBox,
            TextBox.TextProperty,
            new Binding(nameof(SearchText))
            {
                Source = this,
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
                Delay = SearchDelay,
            });
    }

    private static object CoerceSearchText(DependencyObject d, object? value) => value ?? string.Empty;

    private static void OnCaptionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        d.SetValue(HasCaptionPropertyKey, !string.IsNullOrWhiteSpace(e.NewValue as string));

    /// <summary>A toolbar to UI Automation, named after the panel ("Alerts toolbar"), so its buttons are read as its own.</summary>
    private sealed class ToolbarPeer : FrameworkElementAutomationPeer
    {
        public ToolbarPeer(DcPageToolbar owner)
            : base(owner)
        {
        }

        protected override string GetClassNameCore() => nameof(DcPageToolbar);

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ToolBar;

        protected override string GetNameCore()
        {
            var name = base.GetNameCore();
            if (!string.IsNullOrEmpty(name))
            {
                return name;
            }

            var title = ((DcPageToolbar)Owner).Title;
            return string.IsNullOrEmpty(title) ? "Toolbar" : title + " toolbar";
        }
    }
}
