using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.Tests.Installation;

/// <summary>
/// While an in-app runtime upgrade runs (CUST-346, CUST-378) the installation guard says every change is off with the upgrade's reason, its
/// generation moves so reads started before are dropped, and the runner refuses changes. The Mac's <c>runtimeUpgradeRunID</c> and
/// <c>installationGeneration</c>.
/// </summary>
public sealed class InstallationUpgradeRunTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly AppServices _services;

    public InstallationUpgradeRunTests()
    {
        _services = TestServices.Create(_temp, installation: TestInstallations.UserDefault());
    }

    public void Dispose()
    {
        _services.Dispose();
        _temp.Dispose();
    }

    [Fact]
    public void An_upgrade_turns_every_change_off_with_its_reason_and_ends_with_it()
    {
        var guard = _services.Installation;
        Assert.True(guard.IsMutable);
        var before = guard.Generation;

        var scope = _services.Cli.EnterUpgradeRun();

        Assert.False(guard.IsMutable);
        Assert.True(guard.UpgradeRunning);
        Assert.Equal(CliRunner.UpgradeInProgressReason, guard.BlockedReason);
        Assert.Equal(CliRunner.UpgradeInProgressReason, guard.ReasonFor(new[] { "skill", "block", "--", "pdf-tools" }));
        Assert.Null(guard.ReasonFor(new[] { "skill", "list" }));
        Assert.Equal(CliRunner.UpgradeInProgressReason, guard.ReasonFor(CommandTier.StateChanging));
        Assert.Contains("already in progress", guard.UpgradeBlockedReason, StringComparison.Ordinal);
        Assert.False(guard.CanUpgrade);

        scope.Dispose();

        Assert.True(guard.IsMutable);
        Assert.Null(guard.BlockedReason);
        Assert.True(guard.CanUpgrade);
        Assert.True(guard.Generation >= before + 2);
    }

    [Fact]
    public void A_read_started_before_the_upgrade_is_not_current_afterwards()
    {
        var guard = _services.Installation;
        var started = guard.Generation;
        Assert.True(guard.IsCurrent(started));

        _services.Cli.EnterUpgradeRun().Dispose();

        Assert.False(guard.IsCurrent(started));
        Assert.True(guard.IsCurrent(guard.Generation));
    }

    [Fact]
    public void The_guard_announces_the_upgrade_to_the_panels()
    {
        var raised = 0;
        _services.Installation.Changed += (_, _) => raised++;

        var scope = _services.Cli.EnterUpgradeRun();
        scope.Dispose();

        // Once the shared UI thread exists (an earlier test in the process starts it) the announcement is posted to it: let it run.
        System.Windows.Application.Current?.Dispatcher.Invoke(static () => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        for (var i = 0; i < 500 && Volatile.Read(ref raised) < 2; i++)
        {
            Thread.Sleep(10);
        }

        Assert.True(Volatile.Read(ref raised) >= 1);
    }
}
