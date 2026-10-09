using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Audit;

namespace DefenseClaw.App.Tests.Startup;

/// <summary>
/// The read limits of the composition (CUST-323): the app's own are 10 s for the alert queue, 8 s for the inspector lookups and 5 s for the
/// counts, an isolated composition keeps them unless it is handed others, and the readers the composition and the view-models build are bounded
/// by what it holds - so a test over a busy machine can lift all of them in one place, and nothing in the app moves.
/// </summary>
public sealed class ReaderTimeoutsWiringTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void An_isolated_composition_keeps_the_apps_limits_unless_it_is_handed_others()
    {
        using var services = AppServices.CreateIsolated(TestServices.IsolatedPaths(_temp.Path), claudeSettingsPath: _temp.File("claude-settings.json"));

        Assert.Equal(ReaderTimeouts.Production, services.ReaderTimeouts);
        Assert.Equal(TimeSpan.FromSeconds(10), services.AlertQueue.ReadTimeout);
        Assert.Equal(TimeSpan.FromSeconds(8), services.ReaderTimeouts.Audit);
        Assert.Equal(TimeSpan.FromSeconds(5), services.ReaderTimeouts.HookTotals);
    }

    [Fact]
    public void The_suites_composition_holds_its_ceiling_for_every_kind_of_read_and_its_alert_queue_obeys_it()
    {
        using var services = TestServices.Create(_temp);

        Assert.Equal(ReaderTimeouts.Uniform(TestTimeouts.Ceiling), services.ReaderTimeouts);
        Assert.Equal(TestTimeouts.Ceiling, services.AlertQueue.ReadTimeout);
    }

    [Fact]
    public void Limits_handed_to_a_composition_reach_the_queue_reader_the_alerts_inspector_and_the_view_models_that_ask()
    {
        var odd = new ReaderTimeouts(TimeSpan.FromSeconds(41), TimeSpan.FromSeconds(42), TimeSpan.FromSeconds(43));
        using var services = AppServices.CreateIsolated(
            TestServices.IsolatedPaths(_temp.Path),
            claudeSettingsPath: _temp.File("claude-settings.json"),
            readerTimeouts: odd);

        Assert.Equal(odd, services.ReaderTimeouts);
        Assert.Equal(TimeSpan.FromSeconds(41), services.AlertQueue.ReadTimeout);
        StaThread.Run(() =>
        {
            var alerts = new AlertsPanelViewModel(services);
            Assert.Equal(TimeSpan.FromSeconds(42), alerts.DetailReader.ReadTimeout);
        });
    }
}
