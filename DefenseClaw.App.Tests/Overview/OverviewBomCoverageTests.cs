using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Inventory;

namespace DefenseClaw.App.Tests.Overview;

/// <summary>
/// The Overview's AI BOM coverage card (CUST-329): one row per connector the Inventory page's last AI BOM covered, read from the snapshot that
/// run left (CUST-275) and never from a scan of its own. With no run yet it says so; a narrowed run counts only what it covered; skipped
/// connectors are named. The CLI is never started here: a run, where there is one, comes through <see cref="DiscoverActionReview.RunStep"/>.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class OverviewBomCoverageTests : IDisposable
{
    private readonly OverviewScene _scene = OverviewScene.Create(seedAudit: false);

    public void Dispose() => _scene.Dispose();

    private static InventoryBomSnapshot Read(string fixture)
    {
        var result = InventoryBomSnapshot.Parse(PayloadFixtures.Read(fixture));
        Assert.True(result.Succeeded, result.Message);
        return result.Snapshot!;
    }

    private OverviewPanelViewModel Panel() => new(_scene.Services);

    [Fact]
    public void With_no_ai_bom_yet_the_card_says_so_and_runs_nothing()
    {
        var vm = Panel();

        vm.RefreshBomCoverage();

        Assert.False(vm.HasBomCoverage);
        Assert.Empty(vm.BomCoverageRows);
        Assert.Equal(OverviewPanelViewModel.NoBomYet, vm.BomCoverageNote);
        Assert.Contains("does not run one", vm.BomCoverageNote, StringComparison.Ordinal);
        Assert.Empty(_scene.Services.Cli.Activity);
    }

    [Fact]
    public void A_published_scan_gives_one_row_per_connector_with_its_counts_and_when_it_was_made()
    {
        var snapshot = Read("aibom-scan.multi-connector.json");
        var capturedAt = DateTimeOffset.Now.AddMinutes(-4);
        _scene.Services.InventoryBom.Publish(snapshot, Array.Empty<InventoryBomKind>(), capturedAt);
        var vm = Panel();

        vm.RefreshBomCoverage();

        Assert.True(vm.HasBomCoverage);
        Assert.Equal(snapshot.Connectors.Select(c => c.Name).ToArray(), vm.BomCoverageRows.Select(r => r.Connector).ToArray());
        foreach (var connector in snapshot.Connectors)
        {
            var row = vm.BomCoverageRows.Single(r => r.Connector == connector.Name);
            var skills = connector.Count(InventoryBomKind.Skills)!.Count;
            Assert.StartsWith(skills + (skills == 1 ? " skill" : " skills"), row.Counts, StringComparison.Ordinal);
        }

        Assert.StartsWith("Generated ", vm.BomCoverageSummary, StringComparison.Ordinal);
        Assert.EndsWith("every category", vm.BomCoverageSummary, StringComparison.Ordinal);
        Assert.Equal(string.Empty, vm.BomCoverageNote);
        Assert.Empty(_scene.Services.Cli.Activity);
    }

    [Fact]
    public void A_scan_limited_to_some_categories_counts_only_those_and_says_so()
    {
        var snapshot = Read("aibom-scan.multi-connector.json");
        _scene.Services.InventoryBom.Publish(snapshot, new[] { InventoryBomKind.Skills }, DateTimeOffset.Now);
        var vm = Panel();

        vm.RefreshBomCoverage();

        Assert.All(vm.BomCoverageRows, row =>
        {
            Assert.Contains("skill", row.Counts, StringComparison.Ordinal);
            Assert.DoesNotContain("plugin", row.Counts, StringComparison.Ordinal);
            Assert.DoesNotContain("MCP", row.Counts, StringComparison.Ordinal);
        });
        Assert.EndsWith("only skills", vm.BomCoverageSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void A_newer_scan_replaces_the_rows_and_a_connector_the_scan_could_not_read_is_named()
    {
        _scene.Services.InventoryBom.Publish(Read("aibom-scan.multi-connector.json"), Array.Empty<InventoryBomKind>(), DateTimeOffset.Now);
        var vm = Panel();
        vm.RefreshBomCoverage();
        Assert.Equal(2, vm.BomCoverageRows.Count);

        var partial = Read("aibom-scan.partial.synthetic.json");
        _scene.Services.InventoryBom.Publish(partial, Array.Empty<InventoryBomKind>(), DateTimeOffset.Now);
        vm.RefreshBomCoverage();

        Assert.Equal(partial.Connectors.Select(c => c.Name).ToArray(), vm.BomCoverageRows.Select(r => r.Connector).ToArray());
        Assert.NotEmpty(partial.Skipped);
        foreach (var skipped in partial.Skipped)
        {
            Assert.Contains(skipped.Name, vm.BomCoverageNote, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_run_on_the_inventory_page_is_what_the_overview_then_shows()
    {
        var inventory = new InventoryPanelViewModel(_scene.Services);
        inventory.Review.RunStep = (_, argv, _) =>
        {
            var invocation = InvocationFactory.Create(true, argv.ToArray());
            foreach (var line in PayloadFixtures.Read("aibom-scan.multi-connector.json").Split('\n'))
            {
                InvocationFactory.Append(invocation, line.TrimEnd('\r'));
            }

            InvocationFactory.Finish(invocation, 0);
            return Task.FromResult(invocation);
        };
        var vm = Panel();
        vm.RefreshBomCoverage();
        Assert.False(vm.HasBomCoverage);

        inventory.GenerateAiBomCommand.Execute(null);
        await inventory.Review.ConfirmCommand.ExecuteAsync(null);
        vm.RefreshBomCoverage();

        Assert.True(vm.HasBomCoverage);
        Assert.Equal(new[] { "claudecode", "codex" }, vm.BomCoverageRows.Select(r => r.Connector).ToArray());
    }

    [Fact]
    public void A_connector_whose_inventory_commands_failed_or_could_not_inventory_something_carries_a_warning_note()
    {
        var snapshot = Read("aibom-scan.95159fd.synthetic.json");
        _scene.Services.InventoryBom.Publish(snapshot, Array.Empty<InventoryBomKind>(), DateTimeOffset.Now);
        var vm = Panel();

        vm.RefreshBomCoverage();

        foreach (var connector in snapshot.Connectors)
        {
            var row = vm.BomCoverageRows.Single(r => r.Connector == connector.Name);
            var expectsNote = connector.ErrorCount > 0 || connector.LimitationCount > 0;
            Assert.Equal(expectsNote, row.Note.Length > 0);
            Assert.Equal(expectsNote ? "Warn" : "Neutral", row.NoteKey);
        }
    }
}
