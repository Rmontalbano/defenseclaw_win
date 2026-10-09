using DefenseClaw.App.Views.Shell;

namespace DefenseClaw.App.Tests.Shell;

/// <summary>
/// The arithmetic of the status strip's collapse (CUST-273), with no WPF in it: which chips are hidden, in what order, and what the detail sentence
/// gets. The same rules are checked on a real view, at several widths, in <see cref="StatusStripLayoutTests"/>.
/// </summary>
public sealed class ChipStripLayoutTests
{
    private const double Gap = 8;
    private const double More = 40;

    private static ChipStripLayout.Slot S(int priority, double width) => new(priority, width);

    private static ChipStripLayout.Fit Fit(double width, IReadOnlyList<ChipStripLayout.Slot> slots, double flexNatural = 0, double flexMin = 0) =>
        ChipStripLayout.Compute(width, flexNatural, flexMin, slots, Gap, More);

    [Fact]
    public void With_room_to_spare_nothing_is_hidden_and_the_plus_slot_is_not_shown()
    {
        var fit = Fit(1000, new[] { S(10, 100), S(20, 100), S(30, 100) });

        Assert.Equal(new[] { false, false, false }, fit.Hidden);
        Assert.False(fit.ShowOverflow);
        Assert.Equal(316, fit.ChipsWidth);
    }

    [Fact]
    public void The_chip_with_the_highest_priority_number_goes_first_then_the_next()
    {
        var slots = new[] { S(10, 100), S(20, 100), S(30, 100) };

        // All three need 316; with the +N slot the first two need 100 + 8 + 100 + 8 + 40 = 256; the first alone 100 + 8 + 40 = 148.
        Assert.Equal(new[] { false, false, true }, Fit(300, slots).Hidden);
        Assert.Equal(new[] { false, true, true }, Fit(250, slots).Hidden);
        Assert.Equal(new[] { true, true, true }, Fit(100, slots).Hidden);
    }

    [Fact]
    public void The_order_is_the_priority_not_the_position()
    {
        // Drawn left to right: version (300), guardrail (10), connector (50). Narrow: the version goes first, though it is drawn first.
        var slots = new[] { S(300, 100), S(10, 100), S(50, 100) };

        Assert.Equal(new[] { true, false, false }, Fit(300, slots).Hidden);
        Assert.Equal(new[] { true, false, true }, Fit(200, slots).Hidden);
    }

    [Fact]
    public void Among_equal_priorities_the_later_chip_goes_first()
    {
        var slots = new[] { S(40, 100), S(40, 100), S(40, 100) };

        Assert.Equal(new[] { false, false, true }, Fit(300, slots).Hidden);
    }

    [Fact]
    public void A_priority_of_zero_never_goes_even_when_nothing_fits()
    {
        var slots = new[] { S(0, 300), S(10, 100), S(20, 100) };

        var fit = Fit(200, slots);

        Assert.Equal(new[] { false, true, true }, fit.Hidden);
        Assert.True(fit.ShowOverflow);
    }

    [Fact]
    public void A_chip_with_nothing_to_say_takes_no_room_and_is_never_counted_as_hidden()
    {
        var slots = new[] { S(10, 100), S(20, 0), S(30, 100) };

        // Absent chips cost nothing: 100 + 8 + 100 = 208 fits in 210.
        var fit = Fit(210, slots);

        Assert.Equal(new[] { false, false, false }, fit.Hidden);
        Assert.False(fit.ShowOverflow);
        Assert.Equal(208, fit.ChipsWidth);

        // Squeezed, the one that goes is a chip with something to say.
        Assert.Equal(new[] { false, false, true }, Fit(207, slots).Hidden);
    }

    [Fact]
    public void The_plus_slots_room_is_counted_before_deciding_so_showing_it_never_pushes_a_chip_back_out()
    {
        var slots = new[] { S(10, 100), S(20, 100) };

        // The two fit exactly in 208; one less and the second goes, and the +N slot (8 + 40) must fit beside the first: 148.
        Assert.Equal(new[] { false, false }, Fit(208, slots).Hidden);
        var squeezed = Fit(207, slots);
        Assert.Equal(new[] { false, true }, squeezed.Hidden);
        Assert.Equal(148, squeezed.ChipsWidth);
    }

    [Fact]
    public void The_detail_sentence_gives_way_first_and_only_down_to_its_minimum()
    {
        var slots = new[] { S(10, 100), S(20, 100) };

        // The chips need 208. Sentence wants 230, least 120, plus a gap before the chips.
        Assert.Equal(230, Fit(1000, slots, 230, 120).FlexWidth);

        // 208 + 8 + 230 = 446 fits exactly; below that the sentence is trimmed, down to 120 at 336.
        Assert.Equal(230, Fit(446, slots, 230, 120).FlexWidth);
        Assert.Equal(200, Fit(416, slots, 230, 120).FlexWidth);
        var atMinimum = Fit(336, slots, 230, 120);
        Assert.Equal(120, atMinimum.FlexWidth);
        Assert.Equal(new[] { false, false }, atMinimum.Hidden);

        // Below that a chip goes, and the sentence takes what it leaves.
        var narrower = Fit(335, slots, 230, 120);
        Assert.Equal(new[] { false, true }, narrower.Hidden);
        Assert.Equal(335 - 148 - Gap, narrower.FlexWidth);
    }

    [Fact]
    public void The_sentence_never_exceeds_its_full_width_or_goes_below_a_minimum_larger_than_itself()
    {
        Assert.Equal(60, Fit(1000, new[] { S(10, 100) }, 60, 120).FlexWidth);
        Assert.Equal(0, Fit(1000, new[] { S(10, 100) }, 0, 120).FlexWidth);
    }

    [Fact]
    public void With_no_limit_nothing_is_hidden_and_the_sentence_is_full()
    {
        var fit = ChipStripLayout.Compute(double.PositiveInfinity, 230, 120, new[] { S(10, 500), S(20, 500) }, Gap, More);

        Assert.Equal(new[] { false, false }, fit.Hidden);
        Assert.Equal(230, fit.FlexWidth);
        Assert.Equal(1008, fit.ChipsWidth);
    }

    [Fact]
    public void With_no_chips_the_sentence_has_the_room()
    {
        var fit = Fit(300, Array.Empty<ChipStripLayout.Slot>(), 230, 120);

        Assert.Equal(230, fit.FlexWidth);
        Assert.Equal(0, fit.ChipsWidth);
        Assert.False(fit.ShowOverflow);
    }

    [Fact]
    public void Whatever_the_widths_the_hidden_chips_are_always_the_lowest_priorities_and_a_wider_strip_never_hides_more()
    {
        var random = new Random(273);
        for (var round = 0; round < 400; round++)
        {
            var count = random.Next(1, 11);
            var slots = Enumerable.Range(0, count)
                .Select(_ => S(random.Next(0, 6) * 10, random.Next(0, 4) == 0 ? 0 : random.Next(30, 260)))
                .ToArray();
            var flexNatural = random.Next(0, 3) == 0 ? 0 : random.Next(40, 300);

            bool[]? previous = null;
            for (var width = 1200; width >= 0; width -= 25)
            {
                var fit = Fit(width, slots, flexNatural, 120);

                // Hidden is a priority suffix: nothing that stays has a larger number than something that went.
                var hiddenPriorities = Enumerable.Range(0, count).Where(i => fit.Hidden[i]).Select(i => slots[i].Priority).ToArray();
                var shownHideable = Enumerable.Range(0, count)
                    .Where(i => !fit.Hidden[i] && slots[i].Priority > 0 && slots[i].Width > 0)
                    .Select(i => slots[i].Priority)
                    .ToArray();
                if (hiddenPriorities.Length > 0 && shownHideable.Length > 0)
                {
                    Assert.True(shownHideable.Max() <= hiddenPriorities.Min(), $"round {round}, width {width}: a higher number stayed while a lower one went");
                }

                // Never a chip that is not there, never a priority-0 chip.
                for (var i = 0; i < count; i++)
                {
                    Assert.False(fit.Hidden[i] && (slots[i].Width <= 0 || slots[i].Priority <= 0), $"round {round}, width {width}: chip {i} cannot be hidden");
                }

                // Narrower than before: at least as many are hidden, and the ones hidden before are still hidden.
                if (previous is not null)
                {
                    for (var i = 0; i < count; i++)
                    {
                        Assert.True(!previous[i] || fit.Hidden[i], $"round {round}, width {width}: chip {i} came back at a narrower width");
                    }
                }

                // What is shown fits, unless nothing more can go.
                var canStillHide = Enumerable.Range(0, count).Any(i => !fit.Hidden[i] && slots[i].Priority > 0 && slots[i].Width > 0);
                if (canStillHide)
                {
                    var least = Math.Min(flexNatural, 120);
                    Assert.True(
                        fit.ChipsWidth + (least > 0 ? least + Gap : 0) <= width + 0.0001 || fit.ChipsWidth <= 0,
                        $"round {round}, width {width}: the chips that stay do not fit");
                }

                previous = fit.Hidden;
            }
        }
    }
}
