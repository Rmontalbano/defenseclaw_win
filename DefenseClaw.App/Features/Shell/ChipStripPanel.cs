using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace DefenseClaw.App.Views.Shell;

/// <summary>
/// Lays the status strip's chips out in one line and hides the ones that do not fit, lowest priority first (CUST-273), so a narrow window never
/// clips or overflows: the arithmetic is <see cref="ChipStripLayout"/>'s, this is its WPF half.
/// <para>
/// <b>Children.</b> In the order they are drawn: one flexible element (the gateway's detail sentence, <see cref="IsFlexibleProperty"/>), which
/// is trimmed first, down to <see cref="FlexMinWidth"/>; the chips, each with a <see cref="PriorityProperty"/> (lower stays longer); and one
/// <c>+N</c> slot (<see cref="IsOverflowSlotProperty"/>) that shows only while some chip is hidden. The chips are packed against the right edge, next to the
/// buttons; the sentence is at the left. A child with nothing to say (collapsed by its own binding) takes no room and is never counted as hidden.
/// </para>
/// <para>
/// <b>Hidden is not collapsed.</b> A hidden chip is arranged with no room and clipped to it, and <see cref="GetIsCollapsed"/> says so; its
/// <c>Visibility</c> is never touched. That is what lets the panel measure every chip at its full width on every pass - a chip that is gone cannot
/// be measured, so a layout built on collapsing would have to guess its width back, and flip chips in and out as it guessed. The cost is that a hidden
/// chip is still in the tree (a zero-size, clipped element; the strip's container style marks it off-screen for UI Automation, which does not do so for
/// a zero-size element by itself); the <c>+N</c> chip lists what it holds.
/// </para>
/// <para>
/// <b>Telling the view-model.</b> After a layout that changed which chips are hidden, <see cref="HiddenCommand"/> is run (at background priority,
/// never inside the layout pass) with the hidden chips' content; the strip's view-model draws the <c>+N</c> chip from it.
/// </para>
/// </summary>
public sealed class ChipStripPanel : Panel
{
    /// <summary>A child's place in the collapse order: the higher the number, the sooner it is hidden; 0 or less is never hidden.</summary>
    public static readonly DependencyProperty PriorityProperty = DependencyProperty.RegisterAttached(
        "Priority",
        typeof(int),
        typeof(ChipStripPanel),
        new FrameworkPropertyMetadata(100, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    /// <summary>The one child that shrinks before any chip is hidden (the detail sentence).</summary>
    public static readonly DependencyProperty IsFlexibleProperty = DependencyProperty.RegisterAttached(
        "IsFlexible",
        typeof(bool),
        typeof(ChipStripPanel),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    /// <summary>The child that stands for the hidden chips (<c>+N</c>): drawn, with its room reserved, only while something is hidden.</summary>
    public static readonly DependencyProperty IsOverflowSlotProperty = DependencyProperty.RegisterAttached(
        "IsOverflowSlot",
        typeof(bool),
        typeof(ChipStripPanel),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    private static readonly DependencyPropertyKey IsCollapsedPropertyKey = DependencyProperty.RegisterAttachedReadOnly(
        "IsCollapsed",
        typeof(bool),
        typeof(ChipStripPanel),
        new PropertyMetadata(false));

    /// <summary>Set by the panel: true on a child that is hidden because it does not fit (the <c>+N</c> slot included, while nothing is hidden).</summary>
    public static readonly DependencyProperty IsCollapsedProperty = IsCollapsedPropertyKey.DependencyProperty;

    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing),
        typeof(double),
        typeof(ChipStripPanel),
        new FrameworkPropertyMetadata(6.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty FlexMinWidthProperty = DependencyProperty.Register(
        nameof(FlexMinWidth),
        typeof(double),
        typeof(ChipStripPanel),
        new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty HiddenCommandProperty = DependencyProperty.Register(
        nameof(HiddenCommand),
        typeof(ICommand),
        typeof(ChipStripPanel),
        new PropertyMetadata(null));

    /// <summary>What the panel knows about a child between its measure and its arrange.</summary>
    private sealed record Entry(UIElement Element, double Width, int Priority, bool Flexible, bool OverflowSlot);

    private readonly List<Entry> _entries = new();
    private List<object?> _reported = new();
    private List<object?>? _pending;

    public static int GetPriority(DependencyObject element) => (int)element.GetValue(PriorityProperty);

    public static void SetPriority(DependencyObject element, int value) => element.SetValue(PriorityProperty, value);

    public static bool GetIsFlexible(DependencyObject element) => (bool)element.GetValue(IsFlexibleProperty);

    public static void SetIsFlexible(DependencyObject element, bool value) => element.SetValue(IsFlexibleProperty, value);

    public static bool GetIsOverflowSlot(DependencyObject element) => (bool)element.GetValue(IsOverflowSlotProperty);

    public static void SetIsOverflowSlot(DependencyObject element, bool value) => element.SetValue(IsOverflowSlotProperty, value);

    public static bool GetIsCollapsed(DependencyObject element) => (bool)element.GetValue(IsCollapsedProperty);

    /// <summary>The gap between neighbours, and between the sentence and the first chip.</summary>
    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    /// <summary>The least the flexible element is trimmed to before a chip is hidden.</summary>
    public double FlexMinWidth
    {
        get => (double)GetValue(FlexMinWidthProperty);
        set => SetValue(FlexMinWidthProperty, value);
    }

    /// <summary>Run (with an <see cref="IReadOnlyList{T}"/> of the hidden children's content) after a layout that changed which chips are hidden.</summary>
    public ICommand? HiddenCommand
    {
        get => (ICommand?)GetValue(HiddenCommandProperty);
        set => SetValue(HiddenCommandProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var unbounded = new Size(double.PositiveInfinity, availableSize.Height);
        _entries.Clear();

        var height = 0.0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(unbounded);
            height = Math.Max(height, child.DesiredSize.Height);
            _entries.Add(new Entry(child, child.DesiredSize.Width, GetPriority(child), GetIsFlexible(child), GetIsOverflowSlot(child)));
        }

        var fit = Decide(availableSize.Width);

        // The sentence is measured again at the width it was given, so it trims (an ellipsis) instead of overflowing its slot.
        foreach (var entry in _entries.Where(static e => e.Flexible))
        {
            entry.Element.Measure(new Size(fit.FlexWidth, availableSize.Height));
        }

        var width = double.IsInfinity(availableSize.Width)
            ? fit.FlexWidth + (fit.ChipsWidth > 0 && fit.FlexWidth > 0 ? Spacing : 0) + fit.ChipsWidth
            : availableSize.Width;
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        // The room given can differ from the room asked about; decide again over the widths already measured.
        var fit = Decide(finalSize.Width);
        var chips = ChipEntries().ToList();
        var flexible = _entries.FirstOrDefault(static e => e.Flexible);
        var overflow = _entries.FirstOrDefault(static e => e.OverflowSlot);

        // A second flexible child (there should be none) gets no room rather than overlapping the first.
        foreach (var entry in _entries.Where(static e => e.Flexible))
        {
            entry.Element.Arrange(ReferenceEquals(entry, flexible) ? new Rect(0, 0, fit.FlexWidth, finalSize.Height) : new Rect(0, 0, 0, 0));
        }

        var start = Math.Max(
            fit.FlexWidth + (fit.FlexWidth > 0 && fit.ChipsWidth > 0 ? Spacing : 0),
            finalSize.Width - fit.ChipsWidth);
        var x = start;

        var hiddenContent = new List<object?>();
        for (var i = 0; i < chips.Count; i++)
        {
            var entry = chips[i];
            if (entry.Width <= 0)
            {
                entry.Element.Arrange(new Rect(x, 0, 0, 0));
                SetCollapsed(entry.Element, false);
                continue;
            }

            if (fit.Hidden[i])
            {
                entry.Element.Arrange(new Rect(x, 0, 0, 0));
                SetCollapsed(entry.Element, true);
                hiddenContent.Add(ContentOf(entry.Element));
                continue;
            }

            entry.Element.Arrange(new Rect(x, 0, entry.Width, finalSize.Height));
            SetCollapsed(entry.Element, false);
            x += entry.Width + Spacing;
        }

        // The +N slot follows the last chip that shows; it has room only while something is hidden.
        foreach (var entry in _entries.Where(static e => e.OverflowSlot))
        {
            var shows = fit.ShowOverflow && ReferenceEquals(entry, overflow);
            entry.Element.Arrange(shows ? new Rect(x, 0, entry.Width, finalSize.Height) : new Rect(x, 0, 0, 0));
            SetCollapsed(entry.Element, !shows);
        }

        Report(hiddenContent);
        return finalSize;
    }

    /// <summary>The chips proper, in drawing order: everything but the sentence and the <c>+N</c> slot.</summary>
    private IEnumerable<Entry> ChipEntries() => _entries.Where(static e => !e.Flexible && !e.OverflowSlot);

    private ChipStripLayout.Fit Decide(double width)
    {
        var flexible = _entries.FirstOrDefault(static e => e.Flexible);
        var overflow = _entries.FirstOrDefault(static e => e.OverflowSlot);
        var slots = ChipEntries().Select(static e => new ChipStripLayout.Slot(e.Priority, e.Width)).ToList();
        return ChipStripLayout.Compute(width, flexible?.Width ?? 0, FlexMinWidth, slots, Spacing, overflow?.Width ?? 0);
    }

    private static void SetCollapsed(UIElement element, bool collapsed)
    {
        if (GetIsCollapsed(element) != collapsed)
        {
            element.SetValue(IsCollapsedPropertyKey, collapsed);
        }
    }

    /// <summary>What the child shows: the item an <see cref="ItemsControl"/> wrapped in a <see cref="ContentPresenter"/>, else the child's data context.</summary>
    private static object? ContentOf(UIElement element) =>
        element is ContentPresenter presenter ? presenter.Content : (element as FrameworkElement)?.DataContext;

    /// <summary>
    /// Hands the hidden chips to <see cref="HiddenCommand"/> when they are not the ones last handed over. Posted, never run inside the layout
    /// pass: the view-model's answer changes the <c>+N</c> chip, which is another layout.
    /// </summary>
    private void Report(List<object?> hidden)
    {
        if (hidden.SequenceEqual(_pending ?? _reported, ReferenceEqualityComparer.Instance))
        {
            return;
        }

        var scheduled = _pending is not null;
        _pending = hidden;
        if (scheduled)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            var latest = _pending;
            _pending = null;
            if (latest is null || latest.SequenceEqual(_reported, ReferenceEqualityComparer.Instance))
            {
                return;
            }

            // Only what the command was given counts as reported: with nothing bound yet (no data context at the first layout), the next layout tries again.
            if (HiddenCommand is { } command && command.CanExecute(latest))
            {
                _reported = latest;
                command.Execute(latest);
            }
        }));
    }
}
