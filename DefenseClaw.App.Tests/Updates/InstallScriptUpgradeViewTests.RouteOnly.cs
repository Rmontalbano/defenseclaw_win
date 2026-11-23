using DefenseClaw.App.Services.Updates;
using DefenseClaw.App.Tests.TestSupport;

namespace DefenseClaw.App.Tests.Updates;

/// <summary>G10I-02 through the view model: the rollback review's route-only warning, read from config.yaml and previous\VERSION.</summary>
public sealed partial class InstallScriptUpgradeViewTests
{
    private async Task<(string[] Titles, string? RouteOnlyText)> RollbackWarningsAsync(string previous, string config)
    {
        var rig = Build();
        KeepPrevious(previous);
        File.WriteAllText(Path.Combine(_temp.Path, "config.yaml"), config);
        ApplyOn1_0AndWait(rig, 1);
        await StageAsync(rig);

        var review = UiThread.Run(() => rig.Vm.BuildRollbackReview());
        return (review.Warnings.Select(w => w.Title).ToArray(), review.Warnings.FirstOrDefault(w => w.Title == RouteOnlyRollback.WarningTitle)?.Message);
    }

    [Fact]
    public async Task The_rollback_review_warns_about_route_only_agents_when_the_older_release_has_no_route_only()
    {
        var (titles, text) = await RollbackWarningsAsync("1.0.9", "routing:\n  enabled: true\n  route_only:\n    connectors:\n      - claudecode\n      - codex\n");

        Assert.Contains(RouteOnlyRollback.WarningTitle, titles);
        Assert.StartsWith("Route-only is on for claudecode and codex.", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_owners_shape_a_1_0_10_config_without_routing_and_1_0_9_kept_has_the_review_it_had_before()
    {
        var (titles, text) = await RollbackWarningsAsync("1.0.9", "config_version: 10\nguardrail:\n  mode: observe\n");

        Assert.Null(text);
        Assert.Equal(new[] { "Gateway restart", "Runs in its own window" }, titles);
    }
}
