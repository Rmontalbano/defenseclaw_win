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

    /// <summary>A clock the test moves by hand; timestamps are plain ticks.</summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks;

        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }

    [Fact]
    public void A_cached_hit_is_revalidated_with_one_probe_instead_of_walking_path()
    {
        var inBin = Path.Combine(FakeBin, "defenseclaw.exe");
        var probes = new List<string>();
        var paths = new DefenseClawPaths(
            binDirectory: FakeBin,
            searchPath: new[] { FakePathEntry },
            fileExists: p =>
            {
                probes.Add(p);
                return p == inBin;
            },
            timeProvider: new ManualTimeProvider());

        Assert.Equal(inBin, paths.CliPath);
        var scanCost = probes.Count;
        Assert.True(scanCost > 1, "the first lookup should have walked the PATH entry before the bin directory");

        probes.Clear();
        Assert.Equal(inBin, paths.CliPath);
        Assert.Equal(new[] { inBin }, probes);
    }

    [Fact]
    public void A_cached_path_that_has_disappeared_is_never_returned()
    {
        var inBin = Path.Combine(FakeBin, "defenseclaw.exe");
        var exists = true;
        var paths = new DefenseClawPaths(
            binDirectory: FakeBin,
            searchPath: Array.Empty<string>(),
            fileExists: p => exists && p == inBin,
            timeProvider: new ManualTimeProvider());

        Assert.Equal(inBin, paths.CliPath);

        exists = false;
        Assert.Null(paths.CliPath);
    }

    [Fact]
    public void A_found_lookup_is_rescanned_once_its_lifetime_ends()
    {
        var clock = new ManualTimeProvider();
        var inBin = Path.Combine(FakeBin, "defenseclaw.exe");
        var onPath = Path.Combine(FakePathEntry, "defenseclaw.exe");
        var pathCopyInstalled = false;
        var paths = new DefenseClawPaths(
            binDirectory: FakeBin,
            searchPath: new[] { FakePathEntry },
            fileExists: p => p == inBin || (pathCopyInstalled && p == onPath),
            timeProvider: clock);

        Assert.Equal(inBin, paths.CliPath);

        // A copy earlier on PATH appears. The trusted result stands until the lifetime is up...
        pathCopyInstalled = true;
        clock.Advance(DefenseClawPaths.FoundLookupLifetime - TimeSpan.FromSeconds(1));
        Assert.Equal(inBin, paths.CliPath);

        // ...and PATH precedence is honoured again after it.
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(onPath, paths.CliPath);
    }

    [Fact]
    public void A_missing_lookup_is_trusted_briefly_then_retried()
    {
        var clock = new ManualTimeProvider();
        var inBin = Path.Combine(FakeBin, "defenseclaw.exe");
        var installed = false;
        var probes = 0;
        var paths = new DefenseClawPaths(
            binDirectory: FakeBin,
            searchPath: new[] { FakePathEntry },
            fileExists: p =>
            {
                probes++;
                return installed && p == inBin;
            },
            timeProvider: clock);

        Assert.Null(paths.CliPath);
        var scanCost = probes;

        installed = true;
        Assert.Null(paths.CliPath);
        Assert.Equal(scanCost, probes);

        clock.Advance(DefenseClawPaths.MissingLookupLifetime + TimeSpan.FromSeconds(1));
        Assert.Equal(inBin, paths.CliPath);
    }

    [Fact]
    public void Invalidating_the_cache_makes_a_fresh_install_visible_immediately()
    {
        var inBin = Path.Combine(FakeBin, "defenseclaw.exe");
        var installed = false;
        var paths = new DefenseClawPaths(
            binDirectory: FakeBin,
            searchPath: Array.Empty<string>(),
            fileExists: p => installed && p == inBin,
            timeProvider: new ManualTimeProvider());

        Assert.Null(paths.CliPath);

        installed = true;
        Assert.Null(paths.CliPath);

        paths.InvalidateExecutableCache();
        Assert.Equal(inBin, paths.CliPath);
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
