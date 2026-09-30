using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace DefenseClaw.App.Views.Controls;

/// <summary>
/// A single-select segmented control: an inset track with one segment per option and the chosen one lifted onto an
/// accent thumb. It is the page filter the Mac app uses where the options are few (six or fewer: Logs' source, Audit's
/// view, Registries' Sources / Entries / Approved); past six a <c>ComboBox</c> reads better and is what a panel uses.
/// <para>
/// It is a <see cref="ListBox"/> in single-selection mode with a restyled track and <see cref="DcSegment"/> containers, so
/// the standard machinery does the work: the list's automation peer is a selection provider (one item selected, none
/// multiple) and every segment's peer is a selection item, so a screen reader says "gateway.log, selected, 1 of 2"; the
/// whole control is one tab stop; and <c>SelectedValue</c> binds two-way like any selector. The arrow keys, Home and End
/// are handled here (see <see cref="OnKeyDown"/>) so the selection follows the keyboard the way a radio group's does.
/// </para>
/// <para>
/// <b>Declaring segments.</b> <c>&lt;ctl:DcSegment Value="Gateway" Content="gateway.log" /&gt;</c> inside the control, or
/// any <c>ItemsSource</c> (its items are wrapped in a <see cref="DcSegment"/>; set <c>SelectedValuePath</c> to bind
/// <c>SelectedValue</c>). <see cref="DcSegment.Count"/> adds the "(35)" suffix of an Inventory tab.
/// </para>
/// <para>
/// <b>Window state.</b> Like the Mac's, the thumb is the accent while the window is the active one and a neutral grey
/// (<c>DcSelected</c>) while it is not; <see cref="IsWindowActive"/> follows the window's Activated / Deactivated.
/// </para>
/// </summary>
public sealed class DcSegmented : ListBox
{
    /// <summary>True while the window that holds the control is the active one (and always, when there is no window).</summary>
    public static readonly DependencyProperty IsWindowActiveProperty = DependencyProperty.Register(
        nameof(IsWindowActive),
        typeof(bool),
        typeof(DcSegmented),
        new PropertyMetadata(true));

    private Window? _window;

    static DcSegmented()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(DcSegmented), new FrameworkPropertyMetadata(typeof(DcSegmented)));
    }

    public DcSegmented()
    {
        SelectionMode = SelectionMode.Single;
        SelectedValuePath = nameof(DcSegment.Value);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>Whether the thumb wears the accent (the window is active) or the neutral selected fill (it is not).</summary>
    public bool IsWindowActive
    {
        get => (bool)GetValue(IsWindowActiveProperty);
        private set => SetValue(IsWindowActiveProperty, value);
    }

    protected override bool IsItemItsOwnContainerOverride(object item) => item is DcSegment;

    protected override DependencyObject GetContainerForItemOverride() => new DcSegment();

    /// <summary>
    /// Left / Right (and Up / Down) move the selection to the neighbouring segment and focus it; Home and End go to the
    /// ends. A disabled segment is skipped, nothing wraps. The list's own navigation would do much of this, but it leans on
    /// the geometry of whichever panel hosts the items; a segmented control's order is simply its item order.
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Handled)
        {
            return;
        }

        var forward = FlowDirection == FlowDirection.RightToLeft ? Key.Left : Key.Right;
        var backward = FlowDirection == FlowDirection.RightToLeft ? Key.Right : Key.Left;
        int? target = null;
        if (e.Key == forward || e.Key == Key.Down)
        {
            target = Neighbour(+1);
        }
        else if (e.Key == backward || e.Key == Key.Up)
        {
            target = Neighbour(-1);
        }
        else if (e.Key == Key.Home)
        {
            target = FirstEnabled(0, +1);
        }
        else if (e.Key == Key.End)
        {
            target = FirstEnabled(Items.Count - 1, -1);
        }
        else
        {
            base.OnKeyDown(e);
            return;
        }

        e.Handled = true;
        if (target is { } index)
        {
            Choose(index);
        }
    }

    /// <summary>Selects the segment at <paramref name="index"/> and gives it keyboard focus.</summary>
    private void Choose(int index)
    {
        SelectedIndex = index;
        if (ItemContainerGenerator.ContainerFromIndex(index) is UIElement container)
        {
            _ = container.Focus();
        }
    }

    /// <summary>The next enabled segment from the one that holds focus (or is selected) in a direction, or null at the end.</summary>
    private int? Neighbour(int step)
    {
        var current = -1;
        for (var i = 0; i < Items.Count; i++)
        {
            if (ItemContainerGenerator.ContainerFromIndex(i) is UIElement { IsKeyboardFocusWithin: true })
            {
                current = i;
                break;
            }
        }

        if (current < 0)
        {
            current = SelectedIndex;
        }

        if (current < 0)
        {
            return FirstEnabled(step > 0 ? 0 : Items.Count - 1, step);
        }

        return FirstEnabled(current + step, step);
    }

    private int? FirstEnabled(int from, int step)
    {
        for (var i = from; i >= 0 && i < Items.Count; i += step)
        {
            if (ItemContainerGenerator.ContainerFromIndex(i) is not UIElement { IsEnabled: false })
            {
                return i;
            }
        }

        return null;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _window = Window.GetWindow(this);
        if (_window is null)
        {
            IsWindowActive = true;
            return;
        }

        _window.Activated += OnWindowActivated;
        _window.Deactivated += OnWindowDeactivated;
        IsWindowActive = _window.IsActive;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_window is not null)
        {
            _window.Activated -= OnWindowActivated;
            _window.Deactivated -= OnWindowDeactivated;
            _window = null;
        }
    }

    private void OnWindowActivated(object? sender, EventArgs e) => IsWindowActive = true;

    private void OnWindowDeactivated(object? sender, EventArgs e) => IsWindowActive = false;
}

/// <summary>
/// One option of a <see cref="DcSegmented"/>: its <see cref="ListBoxItem.Content"/> is the label, <see cref="Value"/> what
/// the control's <c>SelectedValue</c> reports, and <see cref="Count"/> an optional number shown after the label as
/// "(35)". Its UI Automation name is the label plus the count ("Skills (35)") unless one is set explicitly.
/// </summary>
public sealed class DcSegment : ListBoxItem
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value),
        typeof(object),
        typeof(DcSegment),
        new PropertyMetadata(null));

    public static readonly DependencyProperty CountProperty = DependencyProperty.Register(
        nameof(Count),
        typeof(int?),
        typeof(DcSegment),
        new PropertyMetadata(null, OnCountChanged));

    private static readonly DependencyPropertyKey CountTextPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(CountText),
        typeof(string),
        typeof(DcSegment),
        new PropertyMetadata(string.Empty));

    /// <summary>The count as the template shows it: "(35)", or empty when there is none.</summary>
    public static readonly DependencyProperty CountTextProperty = CountTextPropertyKey.DependencyProperty;

    /// <summary>The automation name <see cref="UpdateAutomationName"/> last derived (so it can tell it from one an author set).</summary>
    private string? _derivedName;

    static DcSegment()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(DcSegment), new FrameworkPropertyMetadata(typeof(DcSegment)));
    }

    /// <summary>What the control's <c>SelectedValue</c> is while this segment is the selected one.</summary>
    public object? Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>A number shown after the label, e.g. the rows behind a view. Null: no suffix.</summary>
    public int? Count
    {
        get => (int?)GetValue(CountProperty);
        set => SetValue(CountProperty, value);
    }

    public string CountText => (string)GetValue(CountTextProperty);

    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);
        UpdateAutomationName();
    }

    private static void OnCountChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var segment = (DcSegment)d;
        segment.SetValue(CountTextPropertyKey, e.NewValue is int count ? $"({count})" : string.Empty);
        segment.UpdateAutomationName();
    }

    /// <summary>"Skills (35)": the label and the count a screen reader should say, unless the author named the segment.</summary>
    private void UpdateAutomationName()
    {
        // A name that is not the one this method set last is the author's: leave it alone.
        var local = ReadLocalValue(AutomationProperties.NameProperty);
        if (local != DependencyProperty.UnsetValue && !Equals(local, _derivedName))
        {
            return;
        }

        var label = Content as string;
        if (string.IsNullOrEmpty(label))
        {
            return;
        }

        _derivedName = Count is { } count ? $"{label} ({count})" : label;
        SetCurrentValue(AutomationProperties.NameProperty, _derivedName);
    }
}
