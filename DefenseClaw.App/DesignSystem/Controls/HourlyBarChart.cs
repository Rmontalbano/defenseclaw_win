using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using DefenseClaw.Core.Audit;

namespace DefenseClaw.App.Views.Controls;

/// <summary>
/// The Overview's "Activity - last 24 h" chart: one stacked bar per hour, allowed (the Ok tone) under blocked (the Critical tone), a scale on
/// the right, a faint grid, hour labels every six hours and a tooltip per bar. Drawn by hand in <see cref="OnRender"/> from the tone
/// tokens, so it follows a live style or light/dark switch like every other mark in the app; there is no chart library behind it.
/// <para>
/// <b>Reading it without seeing it.</b> The control is one picture to UI Automation (an <c>Image</c> carrying the name the view gives it) whose
/// help text is <see cref="Summary"/>: the totals, the busiest hour and every hour that blocked something. The numbers behind each bar are in the
/// tooltip for a pointer, and the same totals are printed as text in the card above the chart.
/// </para>
/// <para>
/// <b>Cost.</b> A render walks at most a few dozen buckets and draws a few dozen rectangles and a dozen lines of text; it happens when the
/// buckets change, the size changes, the pointer moves to another bar, or a token changes. Nothing ticks.
/// </para>
/// </summary>
public sealed class HourlyBarChart : FrameworkElement
{
    /// <summary>The hours to draw, oldest first. Null or empty draws nothing.</summary>
    public static readonly DependencyProperty BucketsProperty = DependencyProperty.Register(
        nameof(Buckets),
        typeof(IReadOnlyList<HourlyBucket>),
        typeof(HourlyBarChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnBucketsChanged));

    private static readonly DependencyProperty AllowedBrushProperty = BrushProperty("AllowedBrush");
    private static readonly DependencyProperty BlockedBrushProperty = BrushProperty("BlockedBrush");
    private static readonly DependencyProperty GridBrushProperty = BrushProperty("GridBrush");
    private static readonly DependencyProperty TextBrushProperty = BrushProperty("TextBrush");
    private static readonly DependencyProperty HoverBrushProperty = BrushProperty("HoverBrush");

    private static readonly DependencyProperty ChartFontProperty = DependencyProperty.Register(
        "ChartFont",
        typeof(FontFamily),
        typeof(HourlyBarChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Text on the chart: never below the app's 12 px floor.</summary>
    private const double LabelSize = 12;

    private const double TopPadding = 8;
    private const double LabelBand = 22;
    private const double AxisGap = 8;
    private const double MinBarFraction = 0.7;
    private const double MinNonZeroBar = 2;

    private readonly ToolTip _tip = new() { Placement = PlacementMode.Relative, StaysOpen = true };
    private int _hover = -1;

    public HourlyBarChart()
    {
        SetResourceReference(AllowedBrushProperty, "DcToneOkBrush");
        SetResourceReference(BlockedBrushProperty, "DcToneCriticalBrush");
        SetResourceReference(GridBrushProperty, "DcBorderBrush");
        SetResourceReference(TextBrushProperty, "DcTextSecondaryBrush");
        SetResourceReference(HoverBrushProperty, "DcSubtleBrush");
        SetResourceReference(ChartFontProperty, "DcUiFontFamily");

        SnapsToDevicePixels = true;
        Focusable = false;
        _tip.PlacementTarget = this;

        // A tip left open over a panel that has been switched away from would hang there.
        Unloaded += (_, _) => _tip.IsOpen = false;
    }

    public IReadOnlyList<HourlyBucket>? Buckets
    {
        get => (IReadOnlyList<HourlyBucket>?)GetValue(BucketsProperty);
        set => SetValue(BucketsProperty, value);
    }

    /// <summary>
    /// The totals, the busiest hour and the hours that blocked something, in words: what a screen reader is told about the chart.
    /// Empty when there is nothing to draw.
    /// </summary>
    public string Summary
    {
        get
        {
            var buckets = Buckets;
            if (buckets is null || buckets.Count == 0)
            {
                return string.Empty;
            }

            var allowed = buckets.Sum(static b => b.Allowed);
            var blocked = buckets.Sum(static b => b.Blocked);
            var text = string.Create(
                CultureInfo.CurrentCulture,
                $"Hook decisions per hour over {buckets.Count} hours: {allowed:N0} allowed, {blocked:N0} blocked.");

            var busiest = buckets.MaxBy(static b => b.Total);
            if (busiest.Total > 0)
            {
                text += string.Create(CultureInfo.CurrentCulture, $" Busiest hour {HourText(busiest.HourStart)} with {busiest.Total:N0}.");
            }

            var blockedHours = buckets.Where(static b => b.Blocked > 0).Select(b => $"{HourText(b.HourStart)} ({b.Blocked:N0})").Take(6).ToList();
            if (blockedHours.Count > 0)
            {
                text += " Hours with blocks: " + string.Join(", ", blockedHours) + ".";
            }

            return text;
        }
    }

    /// <summary>What the tooltip says about one bar: <c>11 PM: 3,388 allowed, 0 blocked</c>.</summary>
    public static string Describe(HourlyBucket bucket) =>
        string.Create(CultureInfo.CurrentCulture, $"{HourText(bucket.HourStart)}: {bucket.Allowed:N0} allowed, {bucket.Blocked:N0} blocked");

    /// <summary>
    /// The scale for a peak: the smallest "nice" top (1, 2, 5 × 10ⁿ) that holds it in at most four steps, and the step.
    /// Always at least one step, so an all-zero chart still has a scale.
    /// </summary>
    public static (int Top, int Step) Scale(int peak)
    {
        if (peak <= 0)
        {
            return (4, 1);
        }

        var magnitude = 1;
        while (true)
        {
            foreach (var factor in new[] { 1, 2, 5 })
            {
                var step = factor * magnitude;
                if ((peak + step - 1) / step <= 4)
                {
                    return (((peak + step - 1) / step) * step, step);
                }
            }

            magnitude *= 10;
        }
    }

    /// <summary>The bar under a pointer at <paramref name="x"/> in a plot that starts at <paramref name="left"/> and is <paramref name="width"/> wide, or -1.</summary>
    public static int SlotAt(double x, double left, double width, int count)
    {
        if (count <= 0 || width <= 0 || x < left || x >= left + width)
        {
            return -1;
        }

        return Math.Min(count - 1, (int)((x - left) / (width / count)));
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(
            double.IsInfinity(availableSize.Width) ? 400 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 150 : availableSize.Height);

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;

        // Transparent fill, so the whole area is a hit target for the tooltip.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, Math.Max(0, width), Math.Max(0, height)));

        var buckets = Buckets;
        if (buckets is null || buckets.Count == 0 || width < 80 || height < 60)
        {
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(GetValue(ChartFontProperty) as FontFamily ?? SystemFonts.MessageFontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var textBrush = GetValue(TextBrushProperty) as Brush ?? Brushes.Gray;
        var gridBrush = GetValue(GridBrushProperty) as Brush ?? Brushes.LightGray;
        var allowedBrush = GetValue(AllowedBrushProperty) as Brush ?? Brushes.SeaGreen;
        var blockedBrush = GetValue(BlockedBrushProperty) as Brush ?? Brushes.IndianRed;

        var peak = buckets.Max(static b => b.Total);
        var (top, step) = Scale(peak);

        // The scale is on the right, as the Mac's is; its width is the widest label.
        var tickLabels = new List<(int Value, FormattedText Text)>();
        for (var value = 0; value <= top; value += step)
        {
            tickLabels.Add((value, Text(value.ToString("N0", CultureInfo.CurrentCulture), typeface, textBrush, dpi)));
        }

        var axisWidth = tickLabels.Max(static t => t.Text.Width) + AxisGap;
        var plotLeft = 0.0;
        var plotRight = Math.Max(plotLeft + 40, width - axisWidth);
        var plotTop = TopPadding;
        var plotBottom = height - LabelBand;
        var plotHeight = plotBottom - plotTop;
        if (plotHeight < 20)
        {
            return;
        }

        var gridPen = new Pen(gridBrush, 1);
        double YOf(double value) => plotBottom - (value / top * plotHeight);

        // Horizontal grid and the scale.
        foreach (var (value, text) in tickLabels)
        {
            var y = Math.Round(YOf(value)) + 0.5;
            dc.DrawLine(gridPen, new Point(plotLeft, y), new Point(plotRight, y));
            dc.DrawText(text, new Point(plotRight + AxisGap, Math.Clamp(y - (text.Height / 2), 0, height - text.Height)));
        }

        // The hover highlight sits behind the bars.
        var slot = (plotRight - plotLeft) / buckets.Count;
        if (_hover >= 0 && _hover < buckets.Count && GetValue(HoverBrushProperty) as Brush is { } hoverBrush)
        {
            dc.DrawRectangle(hoverBrush, null, new Rect(plotLeft + (_hover * slot), plotTop, slot, plotHeight));
        }

        // Bars: allowed from the floor up, blocked stacked on it. A bar with anything in it is at least a couple of pixels tall, so one
        // blocked decision in a busy hour is still there to be seen.
        var barWidth = Math.Max(2, slot * MinBarFraction);
        for (var i = 0; i < buckets.Count; i++)
        {
            var bucket = buckets[i];
            var x = plotLeft + (i * slot) + ((slot - barWidth) / 2);
            var allowedHeight = BarHeight(bucket.Allowed, top, plotHeight);
            var blockedHeight = BarHeight(bucket.Blocked, top, plotHeight);

            if (allowedHeight > 0)
            {
                dc.DrawRectangle(allowedBrush, null, new Rect(x, plotBottom - allowedHeight, barWidth, allowedHeight));
            }

            if (blockedHeight > 0)
            {
                dc.DrawRectangle(blockedBrush, null, new Rect(x, plotBottom - allowedHeight - blockedHeight, barWidth, blockedHeight));
            }
        }

        // Hour labels, every six hours when they fit (every twelve or twenty-four when they do not), with faint verticals.
        var every = 6;
        var sample = Text(LabelFor(buckets[0].HourStart, withDate: true), typeface, textBrush, dpi);
        while (every < buckets.Count && slot * every < sample.Width + 12)
        {
            every *= 2;
        }

        DateTime? previousDay = null;
        for (var i = 0; i < buckets.Count; i += every)
        {
            var local = buckets[i].HourStart.ToLocalTime();
            var withDate = previousDay is null || previousDay.Value != local.Date;
            previousDay = local.Date;

            var label = Text(LabelFor(buckets[i].HourStart, withDate), typeface, textBrush, dpi);
            var center = plotLeft + (i * slot) + (slot / 2);
            var labelX = Math.Clamp(center - (label.Width / 2), 0, Math.Max(0, width - label.Width));
            var tickX = Math.Round(plotLeft + (i * slot)) + 0.5;
            dc.DrawLine(gridPen, new Point(tickX, plotBottom), new Point(tickX, plotBottom + 4));
            dc.DrawText(label, new Point(labelX, plotBottom + 4));
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        UpdateHover(e.GetPosition(this));
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        SetHover(-1, default);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new ChartPeer(this);

    /// <summary>Where the plot is, in the chart's own coordinates, as <see cref="OnRender"/> lays it out (same arithmetic, for the pointer).</summary>
    private (double Left, double Width)? PlotExtent()
    {
        var buckets = Buckets;
        if (buckets is null || buckets.Count == 0 || ActualWidth < 80)
        {
            return null;
        }

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(GetValue(ChartFontProperty) as FontFamily ?? SystemFonts.MessageFontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var (top, step) = Scale(buckets.Max(static b => b.Total));
        var widest = 0.0;
        for (var value = 0; value <= top; value += step)
        {
            widest = Math.Max(widest, Text(value.ToString("N0", CultureInfo.CurrentCulture), typeface, Brushes.Black, dpi).Width);
        }

        return (0, Math.Max(40, ActualWidth - (widest + AxisGap)));
    }

    private void UpdateHover(Point position)
    {
        var buckets = Buckets;
        if (buckets is null || PlotExtent() is not { } plot)
        {
            SetHover(-1, position);
            return;
        }

        var slot = SlotAt(position.X, plot.Left, plot.Width, buckets.Count);
        SetHover(slot, position);
    }

    private void SetHover(int slot, Point position)
    {
        if (slot != _hover)
        {
            _hover = slot;
            InvalidateVisual();
        }

        if (slot < 0 || Buckets is not { } buckets || slot >= buckets.Count)
        {
            _tip.IsOpen = false;
            return;
        }

        _tip.Content = Describe(buckets[slot]);
        _tip.HorizontalOffset = position.X + 14;
        _tip.VerticalOffset = position.Y + 18;
        _tip.IsOpen = true;
    }

    private static void OnBucketsChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var chart = (HourlyBarChart)sender;
        chart._hover = -1;
        chart._tip.IsOpen = false;
    }

    private static string HourText(DateTimeOffset hourStart) =>
        hourStart.ToLocalTime().ToString(HourPattern(), CultureInfo.CurrentCulture);

    private static string LabelFor(DateTimeOffset hourStart, bool withDate)
    {
        var local = hourStart.ToLocalTime();
        var hour = local.ToString(HourPattern(), CultureInfo.CurrentCulture);
        return withDate ? $"{local.ToString("MMM d", CultureInfo.CurrentCulture)} {hour}" : hour;
    }

    /// <summary>The hour as the operator's clock writes it: <c>11 PM</c> on a 12-hour clock, <c>23:00</c> on a 24-hour one.</summary>
    private static string HourPattern() =>
        CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern.Contains('H', StringComparison.Ordinal) ? "HH:mm" : "h tt";

    private static double BarHeight(int value, int top, double plotHeight) =>
        value <= 0 ? 0 : Math.Max(MinNonZeroBar, value / (double)top * plotHeight);

    private static FormattedText Text(string text, Typeface typeface, Brush brush, double pixelsPerDip) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, LabelSize, brush, pixelsPerDip);

    private static DependencyProperty BrushProperty(string name) => DependencyProperty.Register(
        name,
        typeof(Brush),
        typeof(HourlyBarChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>One picture, named by the view and described by <see cref="Summary"/>.</summary>
    private sealed class ChartPeer : FrameworkElementAutomationPeer
    {
        public ChartPeer(HourlyBarChart owner)
            : base(owner)
        {
        }

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;

        protected override string GetClassNameCore() => nameof(HourlyBarChart);

        protected override string GetNameCore()
        {
            var name = AutomationProperties.GetName(Owner);
            return string.IsNullOrEmpty(name) ? "Hourly activity chart" : name;
        }

        protected override string GetHelpTextCore()
        {
            var help = AutomationProperties.GetHelpText(Owner);
            return string.IsNullOrEmpty(help) ? ((HourlyBarChart)Owner).Summary : help;
        }

        protected override bool IsContentElementCore() => true;

        protected override bool IsControlElementCore() => true;
    }
}
