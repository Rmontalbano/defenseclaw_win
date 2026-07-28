using DefenseClaw.Core.Paths;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

public class DefenseClawPathsTests
{
    private const string FakeBin = @"C:\fake\Programs\DefenseClaw\bin";
    private const string FakePathEntry = @"C:\fake\on-path";

    [Fact]
    public void Derives_every_data_file_from_the_data_directory()
    {
        var paths = new DefenseClawPaths(dataDirectory: @"C:\data\.defenseclaw");

        Assert.Equal(@"C:\data\.defenseclaw\config.yaml", paths.ConfigFilePath);
        Assert.Equal(@"C:\data\.defenseclaw\.env", paths.EnvFilePath);
        Assert.Equal(@"C:\data\.defenseclaw\audit.db", paths.AuditDatabasePath);
        Assert.Equal(@"C:\data\.defenseclaw\inventory.db", paths.InventoryDatabasePath);
        Assert.Equal(@"C:\data\.defenseclaw\gateway.log", paths.GatewayLogPath);
        Assert.Equal(@"C:\data\.defenseclaw\watchdog.log", paths.WatchdogLogPath);
        Assert.Equal(@"C:\data\.defenseclaw\gateway.pid", paths.GatewayPidPath);
        Assert.Equal(@"C:\data\.defenseclaw\policies", paths.PoliciesDirectory);
    }

    [Fact]
    public void Defaults_point_at_the_documented_locations()
    {
        var paths = new DefenseClawPaths();

        Assert.EndsWith(@"\.defenseclaw", paths.DataDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(@"\Programs\DefenseClaw\bin", paths.BinDirectory, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Path_entries_win_over_the_installer_bin_directory()
    {
        var onPath = Path.Combine(FakePathEntry, "defenseclaw.exe");
        var inBin = Path.Combine(FakeBin, "defenseclaw.exe");

        var paths = new DefenseClawPaths(
            binDirectory: FakeBin,
            searchPath: new[] { FakePathEntry },
            fileExists: p => p == onPath || p == inBin);

        Assert.Equal(onPath, paths.CliPath);
    }

    [Fact]
    public void Falls_back_to_the_installer_bin_directory()
    {
        var inBin = Path.Combine(FakeBin, "defenseclaw.exe");

        var paths = new DefenseClawPaths(
            binDirectory: FakeBin,
            searchPath: new[] { FakePathEntry },
            fileExists: p => p == inBin);

        Assert.Equal(inBin, paths.CliPath);
    }

    [Fact]
    public void Resolves_the_other_shipped_binaries()
    {
        var paths = new DefenseClawPaths(
            binDirectory: FakeBin,
            searchPath: Array.Empty<string>(),
            fileExists: p => p.StartsWith(FakeBin, StringComparison.OrdinalIgnoreCase));

        Assert.Equal(Path.Combine(FakeBin, "defenseclaw-gateway.exe"), paths.GatewayCliPath);
        Assert.Equal(Path.Combine(FakeBin, "skill-scanner.exe"), paths.SkillScannerPath);
        Assert.Equal(Path.Combine(FakeBin, "mcp-scanner.exe"), paths.McpScannerPath);
    }

    [Fact]
    public void Returns_null_when_nothing_is_installed()
    {
        var paths = new DefenseClawPaths(
            binDirectory: FakeBin,
            searchPath: Array.Empty<string>(),
            fileExists: _ => false);

        Assert.Null(paths.CliPath);
        Assert.Null(paths.GatewayCliPath);
    }

    [Fact]
    public void Candidate_list_probes_path_before_bin()
    {
        var paths = new DefenseClawPaths(
            binDirectory: FakeBin,
            searchPath: new[] { FakePathEntry },
            fileExists: _ => false);

        var candidates = paths.CandidatesFor("defenseclaw").ToList();

        Assert.Equal(Path.Combine(FakePathEntry, "defenseclaw.exe"), candidates[0]);
        Assert.Contains(Path.Combine(FakeBin, "defenseclaw.exe"), candidates);
        Assert.True(
            candidates.IndexOf(Path.Combine(FakePathEntry, "defenseclaw.exe")) <
            candidates.IndexOf(Path.Combine(FakeBin, "defenseclaw.exe")));
    }

    [Fact]
    public void IsInitialized_tracks_the_presence_of_config_yaml()
    {
        using var temp = new TempDirectory();
        var paths = new DefenseClawPaths(dataDirectory: temp.Path);

        Assert.False(paths.IsInitialized);
        Assert.True(paths.DataDirectoryExists);

        temp.Write("config.yaml", "config_version: 8\n");
        Assert.True(paths.IsInitialized);
    }
}
