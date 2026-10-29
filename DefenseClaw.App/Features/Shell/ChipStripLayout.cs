namespace DefenseClaw.App.Views.Shell;

/// <summary>
/// The arithmetic behind <see cref="ChipStripPanel"/>, with no WPF in it: given the room, the width each chip wants and where each stands in the
/// collapse order, which chips are hidden and how wide the flexible element (the gateway's detail sentence) gets.
/// <para>
/// <b>The rules.</b> The sentence is the first to give way, but only down to a minimum (it trims with an ellipsis; its full text is the pill's
/// tooltip). Then chips are hidden, the highest <c>Priority</c> number first, one at a time, until what is left fits; among equals the later chip goes first.
/// A priority of 0 never goes. A chip that is not there at all (no width: nothing to say) takes no room and is never "hidden". When any chip is hidden
/// the <c>+N</c> slot is shown, and its room is counted before the others are decided, so showing it cannot push a chip back out. Whatever room is
/// left after the chips goes to the sentence, up to its full width. With room to spare nothing is hidden, and with none at all every chip that can go has
/// gone, rather than any being squeezed.
/// </para>
/// </summary>
internal static class ChipStripLayout
{
    /// <summary>One chip that wants room.</summary>
    /// <param name="Priority">Its place in the collapse order: the higher the number, the sooner it goes; 0 or less never goes.</param>
    /// <param name="Width">The width it wants. Zero or less: it is not there (nothing to say), and takes no room.</param>
    internal readonly record struct Slot(int Priority, double Width);

    /// <summary>What <see cref="Compute"/> decided.</summary>
    /// <param name="Hidden">Aligned with the slots: true for a chip that is hidden. A chip that is not there is false.</param>
    /// <param name="FlexWidth">How wide the detail sentence is: between its minimum and its full width, as the chips leave room.</param>
    /// <param name="ChipsWidth">The width of the chips that show, their gaps and the <c>+N</c> slot when it shows.</param>
    /// <param name="ShowOverflow">True when at least one chip is hidden, so the <c>+N</c> slot shows.</param>
    internal readonly record struct Fit(bool[] Hidden, double FlexWidth, double ChipsWidth, bool ShowOverflow);

    /// <param name="width">The room in DIPs. Infinite: there is no limit, and nothing is hidden.</param>
    /// <param name="flexNatural">The width the sentence wants at full length (0 when it has no text).</param>
    /// <param name="flexMin">The least the sentence is trimmed to; never more than its full width.</param>
    /// <param name="slots">The chips, in the order they are drawn.</param>
    /// <param name="spacing">The gap between neighbours (between the sentence and the first chip too).</param>
    /// <param name="overflowWidth">The room the <c>+N</c> slot takes when it shows.</param>
    public static Fit Compute(double width, double flexNatural, double flexMin, IReadOnlyList<Slot> slots, double spacing, double overflowWidth)
    {
        ArgumentNullException.ThrowIfNull(slots);

        var flexFull = Math.Max(0, flexNatural);
        var flexLeast = Math.Min(flexFull, Math.Max(0, flexMin));
        var hidden = new bool[slots.Count];

        // The sentence keeps its minimum and a gap before the chips; what the chips get is the rest.
        bool Fits(double chips) => chips <= 0 || chips + (flexLeast > 0 ? flexLeast + spacing : 0) <= width;

        if (!double.IsInfinity(width))
        {
            var group = GroupWidth(slots, hidden, spacing, overflowWidth);
            if (!Fits(group))
            {
                // Highest priority number first; among equals the later chip. A chip that is not there, or never goes, is not a candidate.
                var order = Enumerable.Range(0, slots.Count)
                    .Where(i => slots[i].Priority > 0 && slots[i].Width > 0)
                    .OrderByDescending(i => slots[i].Priority)
                    .ThenByDescending(i => i);
                foreach (var index in order)
                {
                    hidden[index] = true;
                    if (Fits(GroupWidth(slots, hidden, spacing, overflowWidth)))
                    {
                        break;
                    }
                }
            }
        }

        var chipsWidth = GroupWidth(slots, hidden, spacing, overflowWidth);
        var anyHidden = hidden.Any(static h => h);

        double flex;
        if (flexFull <= 0)
        {
            flex = 0;
        }
        else if (double.IsInfinity(width))
        {
            flex = flexFull;
        }
        else
        {
            var left = width - chipsWidth - (chipsWidth > 0 ? spacing : 0);
            flex = Math.Clamp(left, flexLeast, flexFull);
        }

        return new Fit(hidden, flex, chipsWidth, anyHidden);
    }

    /// <summary>The width of the chips that show, the gaps between them and - when any is hidden - the gap and the <c>+N</c> slot.</summary>
    private static double GroupWidth(IReadOnlyList<Slot> slots, bool[] hidden, double spacing, double overflowWidth)
    {
        var shown = 0;
        var total = 0.0;
        var anyHidden = false;
        for (var i = 0; i < slots.Count; i++)
        {
            if (slots[i].Width <= 0)
            {
                continue;
            }

            if (hidden[i])
            {
                anyHidden = true;
                continue;
            }

            total += slots[i].Width + (shown > 0 ? spacing : 0);
            shown++;
        }

        if (anyHidden)
        {
            total += overflowWidth + (shown > 0 ? spacing : 0);
        }

        return total;
    }
}
