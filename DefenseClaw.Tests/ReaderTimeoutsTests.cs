using DefenseClaw.Core.Audit;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// The limits the app gives its <c>audit.db</c> readers (CUST-323): what they are in the app, which is what each reader's own default says, and
/// that a test composition can be given others without the app's moving.
/// </summary>
public class ReaderTimeoutsTests
{
    [Fact]
    public void The_apps_limits_are_ten_seconds_for_the_alert_queue_eight_for_the_inspector_lookups_and_five_for_the_counts()
    {
        var production = ReaderTimeouts.Production;

        Assert.Equal(TimeSpan.FromSeconds(10), production.AlertQueue);
        Assert.Equal(TimeSpan.FromSeconds(8), production.Audit);
        Assert.Equal(TimeSpan.FromSeconds(5), production.HookTotals);
    }

    [Fact]
    public void Each_limit_is_the_default_of_the_reader_it_bounds_so_the_two_cannot_drift()
    {
        var production = ReaderTimeouts.Production;

        Assert.Equal(AlertQueueReader.DefaultTimeout, production.AlertQueue);
        Assert.Equal(AlertDetailReader.DefaultTimeout, production.Audit);
        Assert.Equal(ReadOnlyQuery.DefaultTimeout, production.Audit);
        Assert.Equal(ConnectorHookTotalsReader.DefaultTimeout, production.HookTotals);
    }

    [Fact]
    public void A_uniform_set_gives_every_kind_of_read_the_same_limit_and_changes_nothing_else()
    {
        var ceiling = ReaderTimeouts.Uniform(TestTimeouts.Ceiling);

        Assert.Equal(new ReaderTimeouts(TestTimeouts.Ceiling, TestTimeouts.Ceiling, TestTimeouts.Ceiling), ceiling);
        Assert.NotEqual(ReaderTimeouts.Production, ceiling);
        Assert.Equal(TimeSpan.FromSeconds(10), ReaderTimeouts.Production.AlertQueue);
    }

    [Fact]
    public void A_detail_reader_without_a_limit_of_its_own_has_the_apps_and_one_given_a_limit_has_that()
    {
        using var database = new TestAuditDatabase();

        Assert.Equal(TimeSpan.FromSeconds(8), new AlertDetailReader(database.Path).ReadTimeout);
        Assert.Equal(TestTimeouts.Ceiling, new AlertDetailReader(database.Path, readTimeout: TestTimeouts.Ceiling).ReadTimeout);
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new AlertDetailReader(database.Path, readTimeout: TimeSpan.Zero));
    }
}
