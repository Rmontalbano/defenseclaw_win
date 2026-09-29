using DefenseClaw.App.Views.Panels;

namespace DefenseClaw.App.Tests.Inventory;

/// <summary>Star widths with a floor per column, resolved to plain widths (the Inventory components grid's column sizing).</summary>
public class ColumnSizingTests
{
    [Fact]
    public void Room_is_shared_in_proportion_to_the_weights()
    {
        var widths = ColumnSizing.Distribute(600, new[] { 1.0, 2.0, 3.0 }, new[] { 10.0, 10.0, 10.0 });

        Assert.Equal(new[] { 100.0, 200.0, 300.0 }, widths, new ToleranceComparer(0.001));
    }

    [Fact]
    public void When_the_floors_fit_the_widths_add_up_to_the_room_exactly()
    {
        var widths = ColumnSizing.Distribute(1147, new[] { 1.3, 2.2, 0.8, 1.2, 0.7, 0.5, 1.1, 1.1, 1.2 }, new[] { 110.0, 140, 100, 104, 80, 80, 112, 112, 140 });

        Assert.Equal(1147, widths.Sum(), 6);
    }

    [Fact]
    public void A_column_whose_share_is_below_its_floor_takes_the_floor_and_the_rest_share_what_is_left()
    {
        // 300 between weights 1 : 1 : 8 would give the first two 30 each; their floors of 80 leave 140 for the third.
        var widths = ColumnSizing.Distribute(300, new[] { 1.0, 1.0, 8.0 }, new[] { 80.0, 80.0, 50.0 });

        Assert.Equal(new[] { 80.0, 80.0, 140.0 }, widths, new ToleranceComparer(0.001));
    }

    [Fact]
    public void Clamping_one_column_can_push_another_below_its_floor_and_that_is_settled_too()
    {
        // Equal weights, 300 to share: the first floor (150) leaves 150 for two columns, 75 each - below the second's floor of 100.
        var widths = ColumnSizing.Distribute(300, new[] { 1.0, 1.0, 1.0 }, new[] { 150.0, 100.0, 20.0 });

        Assert.Equal(new[] { 150.0, 100.0, 50.0 }, widths, new ToleranceComparer(0.001));
        Assert.All(new[] { 150.0, 100.0, 20.0 }.Zip(widths), pair => Assert.True(pair.Second >= pair.First));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-50)]
    [InlineData(400)]
    [InlineData(960)]
    public void When_the_floors_do_not_fit_every_column_stays_at_its_floor(double available)
    {
        var minimums = new[] { 110.0, 140, 100, 104, 80, 80, 112, 112, 140 };

        var widths = ColumnSizing.Distribute(available, new[] { 1.3, 2.2, 0.8, 1.2, 0.7, 0.5, 1.1, 1.1, 1.2 }, minimums);

        Assert.Equal(minimums, widths);
    }

    [Fact]
    public void When_not_everything_fits_the_leading_columns_take_the_whole_room_and_the_rest_wait_at_their_floors()
    {
        var weights = new[] { 1.0, 1.0, 1.0, 1.0 };
        var minimums = new[] { 100.0, 100.0, 100.0, 100.0 };

        // 300 cannot hold four floors of 100, so the two that matter fill it and the other two sit past the edge.
        var widths = ColumnSizing.Distribute(300, weights, minimums, leadingColumns: 2);

        Assert.Equal(new[] { 150.0, 150.0, 100.0, 100.0 }, widths, new ToleranceComparer(0.001));
    }

    [Fact]
    public void When_everything_fits_the_trailing_columns_share_the_room_too()
    {
        var weights = new[] { 1.0, 1.0, 1.0, 1.0 };
        var minimums = new[] { 100.0, 100.0, 100.0, 100.0 };

        var widths = ColumnSizing.Distribute(800, weights, minimums, leadingColumns: 2);

        Assert.Equal(new[] { 200.0, 200.0, 200.0, 200.0 }, widths, new ToleranceComparer(0.001));
    }

    [Fact]
    public void Leading_columns_that_do_not_fit_either_stay_at_their_floors()
    {
        var widths = ColumnSizing.Distribute(150, new[] { 1.0, 1.0, 1.0 }, new[] { 100.0, 100.0, 100.0 }, leadingColumns: 2);

        Assert.Equal(new[] { 100.0, 100.0, 100.0 }, widths);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    [InlineData(99)]
    public void The_leading_count_is_clamped_to_the_columns_there_are(int leading)
    {
        var widths = ColumnSizing.Distribute(250, new[] { 1.0, 1.0, 1.0 }, new[] { 100.0, 100.0, 100.0 }, leading);

        Assert.Equal(3, widths.Length);
        Assert.All(widths, width => Assert.True(width >= 100));
    }

    [Fact]
    public void No_columns_is_no_widths()
    {
        Assert.Empty(ColumnSizing.Distribute(500, Array.Empty<double>(), Array.Empty<double>()));
    }

    [Fact]
    public void A_weight_and_a_floor_are_needed_for_every_column()
    {
        Assert.Throws<ArgumentException>(() => ColumnSizing.Distribute(500, new[] { 1.0, 1.0 }, new[] { 10.0 }));
    }

    private sealed class ToleranceComparer : IEqualityComparer<double>
    {
        private readonly double _tolerance;

        public ToleranceComparer(double tolerance) => _tolerance = tolerance;

        public bool Equals(double x, double y) => Math.Abs(x - y) <= _tolerance;

        public int GetHashCode(double obj) => 0;
    }
}
