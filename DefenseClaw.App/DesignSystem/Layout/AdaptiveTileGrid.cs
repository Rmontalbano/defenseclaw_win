using System.Windows;
using System.Windows.Controls;

namespace DefenseClaw.App.Views.Panels;

/// <summary>
/// A grid of equal-width tiles that fills its row: as many columns as fit at <see cref="MinColumnWidth"/> (a tile with its gutter), then the
/// columns share the whole width, so the last tile ends at the right edge instead of a band being left empty (a <see cref="WrapPanel"/> of fixed
/// tiles stopped at three columns and left the rest of a wide page blank). Rows are as tall as their tallest tile, in the order of the children, which
/// keeps the keyboard and screen-reader order the same as the reading order. A child that measures to no height (a collapsed tile the search hid)
/// takes no slot, as in a <see cref="WrapPanel"/>.
/// <para>
/// The tile keeps its own gutter as a margin (the Mac's 12 DIP between tiles); give the hosting control a negative right margin of the same size so
/// the last column's gutter falls outside the page edge.
/// </para>
/// </summary>
public sealed class AdaptiveTileGrid : Panel
{
    /// <summary>The narrowest a column may be, gutter included. The column count is the page width divided by this.</summary>
    public static readonly DependencyProperty MinColumnWidthProperty = DependencyProperty.Register(
        nameof(MinColumnWidth),
        typeof(double),
        typeof(AdaptiveTileGrid),
        new FrameworkPropertyMetadata(245d, FrameworkPropertyMetadataOptions.AffectsMeasure),
        value => value is double d && d > 0 && !double.IsNaN(d) && !double.IsInfinity(d));

    public double MinColumnWidth
    {
        get => (double)GetValue(MinColumnWidthProperty);
        set => SetValue(MinColumnWidthProperty, value);
    }

    /// <summary>How many columns a row of this width holds (at least one).</summary>
    internal static int ColumnsFor(double width, double minColumnWidth) =>
        Math.Max(1, (int)Math.Floor((width + 0.01) / minColumnWidth));

    protected override Size MeasureOverride(Size availableSize)
    {
        var unbounded = double.IsInfinity(availableSize.Width);
        var columns = unbounded ? Math.Max(1, InternalChildren.Count) : ColumnsFor(availableSize.Width, MinColumnWidth);
        var slot = unbounded ? MinColumnWidth : availableSize.Width / columns;

        var height = 0d;
        var inRow = 0;
        var rowHeight = 0d;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(slot, double.PositiveInfinity));
            if (child.DesiredSize.Height <= 0)
            {
                continue;
            }

            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            if (++inRow == columns)
            {
                height += rowHeight;
                inRow = 0;
                rowHeight = 0;
            }
        }

        height += rowHeight;
        return new Size(unbounded ? slot * columns : availableSize.Width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var columns = ColumnsFor(finalSize.Width, MinColumnWidth);
        var slot = finalSize.Width / columns;

        var present = InternalChildren.Cast<UIElement>().Where(c => c.DesiredSize.Height > 0).ToArray();
        var y = 0d;
        for (var start = 0; start < present.Length; start += columns)
        {
            var row = present.Skip(start).Take(columns).ToArray();
            var rowHeight = row.Max(c => c.DesiredSize.Height);
            for (var i = 0; i < row.Length; i++)
            {
                row[i].Arrange(new Rect(i * slot, y, slot, rowHeight));
            }

            y += rowHeight;
        }

        // Collapsed children are arranged to nothing so they hold no stale rectangle.
        foreach (UIElement child in InternalChildren)
        {
            if (child.DesiredSize.Height <= 0)
            {
                child.Arrange(new Rect(0, 0, 0, 0));
            }
        }

        return finalSize;
    }
}
