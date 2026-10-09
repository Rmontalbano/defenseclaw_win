using System.Windows;
using System.Windows.Controls;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.Views.Controls;

namespace DefenseClaw.App.Tests.Tables;

/// <summary>
/// <see cref="DcGridColumns"/>, the star sizing every dense table uses, with a column that comes and goes (CUST-261: the Alerts and Audit tables' Connector
/// column, present only while more than one connector is active): a column that is not visible takes no share of the room, a column that joins the grid is
/// learned with the weight it was declared with, and a column that leaves and comes back has that weight again rather than the width it was last given.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class DcGridColumnsTests
{
    private sealed record Row(string Name);

    private static DataGridTextColumn Column(string header, double star, double minimum, Visibility visibility = Visibility.Visible) =>
        new() { Header = header, Width = new DataGridLength(star, DataGridLengthUnitType.Star), MinWidth = minimum, Binding = new System.Windows.Data.Binding(nameof(Row.Name)), Visibility = visibility };

    private static (DataGrid Grid, OffscreenHost Host) Open(double width, params DataGridColumn[] columns)
    {
        var grid = new DataGrid
        {
            AutoGenerateColumns = false,
            CanUserAddRows = false,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            ItemsSource = new[] { new Row("one"), new Row("two") },
        };
        foreach (var column in columns)
        {
            grid.Columns.Add(column);
        }

        DcGridColumns.SetFit(grid, true);
        var host = new OffscreenHost(grid, width, 200);
        host.Relayout();
        return (grid, host);
    }

    private static double Room(DataGrid grid) => Math.Floor(VisualTree.Find<ScrollViewer>(grid)!.ViewportWidth) - 1;

    private static double Shown(DataGrid grid) => grid.Columns.Where(c => c.Visibility == Visibility.Visible).Sum(c => c.ActualWidth);

    [Fact]
    public void The_columns_share_the_room_by_their_weights_as_they_always_did()
    {
        UiThread.Run(() =>
        {
            var (grid, host) = Open(600, Column("A", 1, 50), Column("C", 2, 50));
            using (host)
            {
                var room = Room(grid);

                Assert.Equal(room, Shown(grid), 1.5);
                Assert.Equal(room / 3, grid.Columns[0].ActualWidth, 1.5);
                Assert.Equal(room * 2 / 3, grid.Columns[1].ActualWidth, 1.5);
            }
        });
    }

    [Fact]
    public void A_column_that_is_not_visible_takes_no_share_and_is_left_as_it_is()
    {
        UiThread.Run(() =>
        {
            var hidden = Column("B", 1, 50, Visibility.Collapsed);
            var (grid, host) = Open(600, Column("A", 1, 50), hidden, Column("C", 2, 50));
            using (host)
            {
                var room = Room(grid);

                // Only A and C share the room, one to two; B has not been given a width of its own (it is still a star, with its weight).
                Assert.Equal(room, Shown(grid), 1.5);
                Assert.Equal(room / 3, grid.Columns[0].ActualWidth, 1.5);
                Assert.Equal(room * 2 / 3, grid.Columns[2].ActualWidth, 1.5);
                Assert.True(hidden.Width.IsStar);
                Assert.Equal(1, hidden.Width.Value);
            }
        });
    }

    [Fact]
    public void A_column_made_visible_shares_the_room_again_by_its_weight()
    {
        UiThread.Run(() =>
        {
            var late = Column("B", 1, 50, Visibility.Collapsed);
            var (grid, host) = Open(600, Column("A", 1, 50), late, Column("C", 2, 50));
            using (host)
            {
                late.Visibility = Visibility.Visible;
                DcGridColumns.Refit(grid);
                host.Relayout();

                var room = Room(grid);
                Assert.Equal(room, Shown(grid), 1.5);
                Assert.Equal(room / 4, grid.Columns[0].ActualWidth, 1.5);
                Assert.Equal(room / 4, late.ActualWidth, 1.5);
                Assert.Equal(room / 2, grid.Columns[2].ActualWidth, 1.5);
            }
        });
    }

    [Fact]
    public void A_column_that_joins_later_takes_the_weight_it_was_declared_with_and_one_that_returns_takes_it_again()
    {
        UiThread.Run(() =>
        {
            var (grid, host) = Open(600, Column("A", 1, 50), Column("C", 2, 50));
            using (host)
            {
                var joiner = Column("B", 1, 50);

                for (var round = 0; round < 3; round++)
                {
                    // In: the columns have long been given plain widths, and this one still has its star weight.
                    grid.Columns.Insert(1, joiner);
                    DcGridColumns.Refit(grid);
                    host.Relayout();
                    var room = Room(grid);
                    Assert.Equal(room, Shown(grid), 1.5);
                    Assert.Equal(room / 4, joiner.ActualWidth, 1.5);
                    Assert.Equal(room / 2, grid.Columns[2].ActualWidth, 1.5);

                    // Out: the others are back to one to two.
                    _ = grid.Columns.Remove(joiner);
                    DcGridColumns.Refit(grid);
                    host.Relayout();
                    room = Room(grid);
                    Assert.Equal(room, Shown(grid), 1.5);
                    Assert.Equal(room / 3, grid.Columns[0].ActualWidth, 1.5);
                    Assert.Equal(room * 2 / 3, grid.Columns[1].ActualWidth, 1.5);
                }
            }
        });
    }

    [Fact]
    public void The_floors_still_hold_with_a_column_that_comes_and_goes()
    {
        UiThread.Run(() =>
        {
            var (grid, host) = Open(260, Column("A", 1, 100), Column("C", 1, 100));
            using (host)
            {
                var joiner = Column("B", 1, 100);
                grid.Columns.Insert(1, joiner);
                DcGridColumns.Refit(grid);
                host.Relayout();

                // Three floors of 100 do not fit in 260: every column is at its floor and the table scrolls.
                Assert.All(grid.Columns, c => Assert.Equal(100.0, c.ActualWidth, 1.0));
            }
        });
    }
}
