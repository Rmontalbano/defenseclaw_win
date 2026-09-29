namespace DefenseClaw.App.Views.Panels;

/// <summary>
/// Star sizing with a floor per column, resolved to plain widths.
/// <para>
/// A <c>DataGrid</c> can do this itself (<c>Width="2*" MinWidth="150"</c>), but it works the widths out only when it is
/// first laid out and when its width changes afterwards - and a grid that is first laid out empty, as the Inventory
/// components grid is (its rows arrive after the panel is on screen), keeps the widths it computed for no rows: every
/// column at its minimum, bunched at the left with dead space beside them, until the window happens to be resized. So
/// the panel resolves the widths itself, whenever the room the columns have changes, with the one rule the DataGrid
/// would have applied - plus one it has no notion of: which columns matter most when there is not room for all of them.
/// </para>
/// </summary>
internal static class ColumnSizing
{
    /// <summary>
    /// Divides <paramref name="available"/> between columns in proportion to <paramref name="weights"/>, never giving one
    /// less than its entry in <paramref name="minimums"/>: a column whose share would fall below its floor takes the floor,
    /// and what that leaves is shared out again between the rest, until every share is at least its floor. When the floors
    /// alone add up to more than <paramref name="available"/> every column is at its floor, and when they add up to less
    /// the widths add up to <paramref name="available"/> exactly.
    /// <para>
    /// <b>Leading columns.</b> When the floors of <em>all</em> the columns do not fit, the grid has to scroll sideways
    /// whatever is done, and the question is which columns are worth showing. The first <paramref name="leadingColumns"/>
    /// are the ones the grid is read for: they share the whole of <paramref name="available"/> (so they stay as wide as the
    /// room lets them, rather than sitting at their floors beside columns that are off screen anyway) and the rest stay at
    /// their floors past the edge, reached by scrolling. Once everything fits, every column takes part in the sharing.
    /// </para>
    /// </summary>
    public static double[] Distribute(
        double available,
        IReadOnlyList<double> weights,
        IReadOnlyList<double> minimums,
        int leadingColumns = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(weights);
        ArgumentNullException.ThrowIfNull(minimums);
        if (weights.Count != minimums.Count)
        {
            throw new ArgumentException("Every column needs a weight and a minimum.", nameof(minimums));
        }

        var count = weights.Count;
        var leading = Math.Clamp(leadingColumns, 0, count);
        if (leading == count || available >= minimums.Sum())
        {
            return Share(available, weights, minimums);
        }

        var widths = new double[count];
        Share(available, weights.Take(leading).ToArray(), minimums.Take(leading).ToArray()).CopyTo(widths, 0);
        for (var i = leading; i < count; i++)
        {
            widths[i] = minimums[i];
        }

        return widths;
    }

    private static double[] Share(double available, IReadOnlyList<double> weights, IReadOnlyList<double> minimums)
    {
        var widths = new double[weights.Count];
        var settled = new bool[weights.Count];
        for (var i = 0; i < widths.Length; i++)
        {
            widths[i] = minimums[i];
        }

        var remaining = available;
        while (true)
        {
            var totalWeight = 0.0;
            for (var i = 0; i < widths.Length; i++)
            {
                if (!settled[i])
                {
                    totalWeight += weights[i];
                }
            }

            if (totalWeight <= 0 || remaining <= 0)
            {
                return widths;
            }

            // Every share in this pass is judged against the same room and the same total weight; only then are the
            // floors taken out of the room for the next pass.
            var room = remaining;
            var clamped = false;
            for (var i = 0; i < widths.Length; i++)
            {
                if (!settled[i] && room * weights[i] / totalWeight < minimums[i])
                {
                    settled[i] = true;
                    remaining -= minimums[i];
                    clamped = true;
                }
            }

            if (clamped)
            {
                continue;
            }

            for (var i = 0; i < widths.Length; i++)
            {
                if (!settled[i])
                {
                    widths[i] = remaining * weights[i] / totalWeight;
                }
            }

            return widths;
        }
    }
}
