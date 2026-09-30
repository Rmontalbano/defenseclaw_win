using System.Diagnostics;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;

namespace DefenseClaw.App.Tests.Overview;

/// <summary>
/// The Scanners box asks where <c>skill-scanner</c> and <c>mcp-scanner</c> are, and it does so from <c>Apply</c> on the UI
/// thread. One dead entry on PATH (an unreachable UNC path) makes one <c>File.Exists</c> take ~40 s, so the answer must
/// come back later, from the pool, and the rows say "checking" until it does.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class ScannerProbeTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly TempDirectory _warmTemp = new();
    private readonly DeadPathProbe _probe = new();

    public void Dispose()
    {
        _probe.Dispose();
        _temp.Dispose();
        _warmTemp.Dispose();
    }

    private static ScannerRow Row(OverviewPanelViewModel vm, string name) => vm.ScannerRows.Single(r => r.Name == name);

    [Fact]
    public void Applying_a_snapshot_on_the_UI_thread_does_not_wait_for_the_PATH_lookup()
    {
        var bin = _temp.File("bin");
        var skillScanner = Path.Combine(bin, "skill-scanner.exe");
        _ = _probe.Install(skillScanner);

        using var services = AppServices.CreateIsolated(
            _probe.PathsFor(_temp, bin),
            claudeSettingsPath: _temp.File("claude-settings.json"));
        using var warmServices = TestServices.Create(_warmTemp);

        var vm = UiThread.Run(() =>
        {
            // JIT and first-use costs belong to the process, not to the lookup being measured.
            _ = new OverviewPanelViewModel(warmServices);

            var stopwatch = Stopwatch.StartNew();
            var panel = new OverviewPanelViewModel(services);
            panel.Apply(services.Monitor.Current);
            stopwatch.Stop();

            Assert.True(
                stopwatch.ElapsedMilliseconds < 100,
                $"Apply took {stopwatch.ElapsedMilliseconds} ms with a dead entry on PATH; it must not wait for the lookup");

            // Until the lookup answers the rows do not claim a scanner is missing.
            Assert.Equal("checking", Row(panel, "skill-scanner").StateText);
            Assert.Equal("checking", Row(panel, "mcp-scanner").StateText);
            return panel;
        });

        // The lookup really is stuck behind the dead entry at this point.
        _probe.WaitUntilBlocked();

        _probe.Release();
        UiThread.WaitFor(() => Row(vm, "skill-scanner").StateText == "installed", "the skill-scanner row to resolve");

        UiThread.Run(() =>
        {
            Assert.Equal(skillScanner, Row(vm, "skill-scanner").Detail);
            Assert.Equal("not found", Row(vm, "mcp-scanner").StateText);
            Assert.Equal("Warn", Row(vm, "mcp-scanner").StateKey);
        });
    }

    [Fact]
    public void A_later_snapshot_in_the_same_minute_does_not_look_again()
    {
        var bin = _temp.File("bin");
        _ = _probe.Install(Path.Combine(bin, "skill-scanner.exe"));
        _probe.Release();

        using var services = AppServices.CreateIsolated(
            _probe.PathsFor(_temp, bin),
            claudeSettingsPath: _temp.File("claude-settings.json"));

        var vm = UiThread.Run(() => new OverviewPanelViewModel(services));
        UiThread.WaitFor(() => Row(vm, "skill-scanner").StateText == "installed", "the skill-scanner row to resolve");

        // Applying again (every poll does) keeps the resolved rows rather than resetting them to "checking".
        UiThread.Run(() =>
        {
            vm.Apply(services.Monitor.Current);
            Assert.Equal("installed", Row(vm, "skill-scanner").StateText);
            Assert.Equal("not found", Row(vm, "mcp-scanner").StateText);
        });
    }

    [Fact]
    public void The_data_directory_box_says_where_the_directory_came_from()
    {
        var overridden = new DefenseClaw.Core.Paths.DefenseClawPaths(
            environment: name => name == "DEFENSECLAW_HOME" ? _temp.Path : null,
            searchPath: Array.Empty<string>(),
            binDirectory: _temp.File("no-such-bin"));

        using var services = AppServices.CreateIsolated(overridden, claudeSettingsPath: _temp.File("claude-settings.json"));

        var vm = UiThread.Run(() => new OverviewPanelViewModel(services));

        Assert.Equal(_temp.Path, vm.DataDirectoryText);
        Assert.Contains("DEFENSECLAW_HOME", vm.DataDirectorySourceText, StringComparison.Ordinal);
    }
}
