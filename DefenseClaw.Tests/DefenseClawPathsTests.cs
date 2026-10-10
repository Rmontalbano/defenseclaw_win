using System.Diagnostics;
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
        // No override in the environment: a developer machine with DEFENSECLAW_HOME set must not change what this asserts.
        var paths = new DefenseClawPaths(environment: _ => null);

        Assert.EndsWith(@"\.defenseclaw", paths.DataDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(DataDirectorySource.Default, paths.DataDirectoryOrigin.Source);
        Assert.EndsWith(@"\Programs\DefenseClaw\bin", paths.BinDirectory, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_installer_bin_directory_wins_over_path_entries()
    {
        var onPath = Path.Combine(FakePathEntry, "defenseclaw.exe");
        var inBin = Path.Combine(FakeBin, "defenseclaw.exe");

        var paths = new DefenseClawPaths(
            binDirectory: FakeBin,
            searchPath: new[] { FakePathEntry },
            fileExists: p => p == onPath || p == inBin);

        Assert.Equal(inBin, paths.CliPath);
    }

    [Fact]
    public void Path_is_the_fallback_when_the_installer_bin_directory_misses()
    {
        var onPath = Path.Combine(FakePathEntry, "defenseclaw.exe");

        var paths = new DefenseClawPaths(
            binDirectory: FakeBin,
            searchPath: new[] { FakePathEntry },
            fileExists: p => p == onPath);

        Assert.Equal(onPath, paths.CliPath);
    }

    [Fact]
    public void A_bare_extensionless_name_is_never_a_candidate_or_a_match()
    {
        var bareInBin = Path.Combine(FakeBin, "defenseclaw");
        var paths = new DefenseClawPaths(
            binDirectory: FakeBin,
            searchPath: new[] { FakePathEntry },
            fileExists: p => p == bareInBin || p == Path.Combine(FakePathEntry, "defenseclaw"));

        Assert.Null(paths.CliPath);
        Assert.All(paths.CandidatesFor("defenseclaw"), c => Assert.EndsWith(".exe", c, StringComparison.OrdinalIgnoreCase));

        // A name that already carries an executable extension is probed exactly as given.
        Assert.Equal(new[] { Path.Combine(FakeBin, "tool.cmd"), Path.Combine(FakePathEntry, "tool.cmd") }, paths.CandidatesFor("tool.cmd").ToArray());
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
    public void Candidate_list_probes_bin_before_path()
    {
        var paths = new DefenseClawPaths(
            binDirectory: FakeBin,
            searchPath: new[] { FakePathEntry },
            fileExists: _ => false);

        var candidates = paths.CandidatesFor("defenseclaw").ToList();

        Assert.Equal(Path.Combine(FakeBin, "defenseclaw.exe"), candidates[0]);
        Assert.True(
            candidates.IndexOf(Path.Combine(FakeBin, "defenseclaw.exe")) <
            candidates.IndexOf(Path.Combine(FakePathEntry, "defenseclaw.exe")));
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
        var inBin = Path.Combine(FakePathEntry, "defenseclaw.exe");
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
        Assert.True(scanCost > 1, "the first lookup should have missed in the bin directory before the PATH entry");

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
    public async Task A_found_lookup_is_rescanned_once_its_lifetime_ends()
    {
        var clock = new ManualTimeProvider();
        var inBin = Path.Combine(FakeBin, "defenseclaw.exe");
        var onPath = Path.Combine(FakePathEntry, "defenseclaw.exe");
        var binCopyInstalled = false;
        var paths = new DefenseClawPaths(
            binDirectory: FakeBin,
            searchPath: new[] { FakePathEntry },
            fileExists: p => p == onPath || (binCopyInstalled && p == inBin),
            timeProvider: clock);
        var changes = new List<ExecutableResolutionChangedEventArgs>();
        paths.ExecutableResolutionChanged += (_, e) => changes.Add(e);

        Assert.Equal(onPath, paths.CliPath);

        // The installer's own copy appears. The trusted result stands until the lifetime is up...
        binCopyInstalled = true;
        clock.Advance(DefenseClawPaths.FoundLookupLifetime - TimeSpan.FromSeconds(1));
        Assert.Equal(onPath, paths.CliPath);

        // ...and the installer directory's precedence is honoured again after it. The caller is answered at once with the copy that is
        // still there, and the rescan that corrects the precedence runs behind it.
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(onPath, paths.CliPath);
        Assert.Equal(inBin, await paths.FindExecutableAsync("defenseclaw"));
        Assert.Equal(inBin, paths.CliPath);

        // The switch is reported, once, with both paths: the app never changes what it runs without saying so.
        var change = Assert.Single(changes);
        Assert.Equal("defenseclaw", change.Name);
        Assert.Equal(onPath, change.Previous);
        Assert.Equal(inBin, change.Current);
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

    // ------------------------------------------------------------------ a dead PATH entry must not stall callers

    /// <summary>TEST-NET-1: never routable. Nothing here touches it; the fake probe only recognises the prefix.</summary>
    private const string DeadEntry = @"\\192.0.2.1\tools";

    /// <summary>
    /// A filesystem where <see cref="DeadEntry"/> answers "not there" only after being released (a UNC path to a
    /// host that is down blocks for ~40 s), and where one file can be "installed".
    /// </summary>
    private sealed class DeadDirectoryProbe
    {
        private readonly ManualResetEventSlim _release = new(false);
        private readonly ManualResetEventSlim _entered = new(false);
        private int _deadProbes;
        private int _gaveUp;
        private ParkedProbe? _parkNext;

        public volatile bool Blocks;

        public string? Installed { get; set; }

        public int DeadProbes => Volatile.Read(ref _deadProbes);

        /// <summary>
        /// True when a blocked probe stopped waiting because its bound (a hang's ceiling, far longer than any wait of a test) ran out and not
        /// because the test let it go. A lookup that "returned at once" while this is still false really did not wait for the blocked probe -
        /// which is the guarantee, and needs no stopwatch: how long a call takes on a busy machine is not what it promises.
        /// </summary>
        public bool GaveUp => Volatile.Read(ref _gaveUp) != 0;

        public bool Exists(string path)
        {
            if (path.StartsWith(DeadEntry, StringComparison.OrdinalIgnoreCase))
            {
                _ = Interlocked.Increment(ref _deadProbes);
                if (Blocks)
                {
                    _entered.Set();
                    if (!_release.Wait(TestTimeouts.Ceiling))
                    {
                        _ = Interlocked.Exchange(ref _gaveUp, 1);
                    }
                }

                return false;
            }

            if (path == Installed && Interlocked.Exchange(ref _parkNext, null) is { } parked)
            {
                parked.Entered.Set();
                _ = parked.Resume.Wait(TestTimeouts.Ceiling);
            }

            return path == Installed;
        }

        /// <summary>Holds the next probe of the installed file, whoever makes it, until <see cref="ParkedProbe.Resume"/> is set.</summary>
        public ParkedProbe ParkNextProbeOfInstalled()
        {
            var parked = new ParkedProbe();
            Volatile.Write(ref _parkNext, parked);
            return parked;
        }

        public void WaitUntilBlocked() => Assert.True(_entered.Wait(TestTimeouts.Ceiling), "the scan never reached the dead entry");

        public void Release() => _release.Set();
    }

    /// <summary>A probe held in place by a test: it has been reached (<see cref="Entered"/>) and goes on when told (<see cref="Resume"/>).</summary>
    private sealed class ParkedProbe
    {
        public ManualResetEventSlim Entered { get; } = new(false);

        public ManualResetEventSlim Resume { get; } = new(false);

        public void WaitUntilEntered() => Assert.True(Entered.Wait(TestTimeouts.Ceiling), "the caller never reached its probe of the remembered file");
    }

    [Fact]
    public async Task A_lookup_during_a_slow_scan_gets_the_previous_answer_at_once_and_the_probe_runs_once()
    {
        var clock = new ManualTimeProvider();
        var inBin = Path.Combine(FakePathEntry, "defenseclaw.exe");
        var probe = new DeadDirectoryProbe { Installed = inBin };
        var paths = new DefenseClawPaths(
            binDirectory: FakeBin,
            searchPath: new[] { DeadEntry, FakePathEntry },
            fileExists: probe.Exists,
            timeProvider: clock);

        Assert.Equal(inBin, paths.CliPath);
        var deadProbesInFirstScan = probe.DeadProbes;
        Assert.Equal(1, deadProbesInFirstScan);

        // The remembered answer goes stale and the dead entry stops answering.
        probe.Blocks = true;
        clock.Advance(DefenseClawPaths.FoundLookupLifetime + TimeSpan.FromSeconds(1));

        // Answered from memory, with the refresh left running behind it: the call returned while the probe was still blocked (nothing has
        // let it go yet), which is the promise - not how many milliseconds a busy machine took to return it.
        var first = paths.CliPath;
        Assert.Equal(inBin, first);

        probe.WaitUntilBlocked();

        var second = paths.CliPath;
        Assert.Equal(inBin, second);
        Assert.False(probe.GaveUp, "a lookup waited for the blocked scan instead of answering from memory");

        // One scan is in flight, blocked in the first probe of the dead entry: nothing started a second one.
        Assert.Equal(deadProbesInFirstScan + 1, probe.DeadProbes);

        // Let it go and wait for it. Its answer is stored before the wait ends, and a lookup that comes in while it finishes - after it has
        // published, before its flight is gone, or after both - must not start a scan of its own: the dead entry is probed by this one scan
        // (one file name) and no other, however the lookup and the end of the scan interleave. (It used to be: 3 probes on CI, not 2.)
        probe.Release();
        Assert.Equal(inBin, await paths.FindExecutableAsync("defenseclaw"));
        Assert.Equal(deadProbesInFirstScan + 1, probe.DeadProbes);
    }

    [Fact]
    public async Task A_caller_that_looked_before_a_scan_finished_takes_the_answer_it_published_instead_of_scanning_again()
    {
        var clock = new ManualTimeProvider();
        var inBin = Path.Combine(FakePathEntry, "defenseclaw.exe");
        var probe = new DeadDirectoryProbe { Installed = inBin };
        var paths = new DefenseClawPaths(
            binDirectory: FakeBin,
            searchPath: new[] { DeadEntry, FakePathEntry },
            fileExists: probe.Exists,
            timeProvider: clock);

        Assert.Equal(inBin, paths.CliPath);
        var deadProbesInFirstScan = probe.DeadProbes;

        // The remembered answer goes stale, a lookup starts the refresh, and the refresh is blocked in the dead entry.
        probe.Blocks = true;
        clock.Advance(DefenseClawPaths.FoundLookupLifetime + TimeSpan.FromSeconds(1));
        Assert.Equal(inBin, paths.CliPath);
        probe.WaitUntilBlocked();

        // A second caller (the one that waits for a fresh answer) looks: it holds the stale answer, and is held in its check that the
        // remembered file is still there - after it looked, before it decides whether to scan or to join the refresh.
        var parked = probe.ParkNextProbeOfInstalled();
        var late = paths.FindExecutableAsync("defenseclaw");
        parked.WaitUntilEntered();

        // The refresh finishes and publishes while it is held. A third caller waits for the refresh, so when it has its answer the refresh
        // is over and published - the interleaving CI hit by chance, made certain.
        probe.Release();
        Assert.Equal(inBin, await paths.FindExecutableAsync("defenseclaw"));
        Assert.Equal(deadProbesInFirstScan + 1, probe.DeadProbes);

        // The held caller goes on with the stale answer in hand, finds no scan in flight and something newer than it has published:
        // that is its answer. A scan of its own would probe the dead entry twice more.
        parked.Resume.Set();
        Assert.Equal(inBin, await late);
        Assert.Equal(deadProbesInFirstScan + 1, probe.DeadProbes);
    }

    [Fact]
    public async Task A_lookup_during_a_scan_that_began_from_a_stale_miss_gets_the_old_miss_at_once()
    {
        var clock = new ManualTimeProvider();
        var probe = new DeadDirectoryProbe();
        var paths = new DefenseClawPaths(
            binDirectory: FakeBin,
            searchPath: new[] { DeadEntry, FakePathEntry },
            fileExists: probe.Exists,
            timeProvider: clock);

        Assert.Null(paths.CliPath);

        probe.Blocks = true;
        clock.Advance(DefenseClawPaths.MissingLookupLifetime + TimeSpan.FromSeconds(1));

        // The caller who starts the rescan of a "missing" waits for it (it is the one who just installed something)...
        var starter = Task.Run(() => paths.CliPath);
        probe.WaitUntilBlocked();

        // ...the others are told what was last known, immediately: the call returns while the probe is still blocked, not after waiting for it.
        var other = paths.CliPath;
        Assert.Null(other);
        Assert.False(probe.GaveUp, "a lookup waited for the blocked scan instead of answering with what was last known");
        Assert.False(starter.IsCompleted);

        probe.Release();
        Assert.Null(await starter);
        Assert.Equal(2, probe.DeadProbes);
    }

    [Fact]
    public async Task First_ever_lookups_share_one_scan()
    {
        var probe = new DeadDirectoryProbe { Blocks = true, Installed = Path.Combine(FakePathEntry, "defenseclaw.exe") };
        var paths = new DefenseClawPaths(
            binDirectory: FakeBin,
            searchPath: new[] { DeadEntry, FakePathEntry },
            fileExists: probe.Exists,
            timeProvider: new ManualTimeProvider());

        var first = Task.Run(() => paths.CliPath);
        probe.WaitUntilBlocked();

        // With nothing remembered there is no answer to give early: the second caller joins the same scan.
        var second = Task.Run(() => paths.CliPath);
        await Task.Delay(200);
        Assert.False(second.IsCompleted);

        probe.Release();
        Assert.Equal(Path.Combine(FakePathEntry, "defenseclaw.exe"), await first);
        Assert.Equal(Path.Combine(FakePathEntry, "defenseclaw.exe"), await second);
        Assert.Equal(1, probe.DeadProbes);
    }

    [Fact]
    public async Task TryGetKnownExecutable_never_waits_for_the_filesystem()
    {
        var inBin = Path.Combine(FakePathEntry, "defenseclaw.exe");
        var probe = new DeadDirectoryProbe { Blocks = true, Installed = inBin };
        var paths = new DefenseClawPaths(
            binDirectory: FakeBin,
            searchPath: new[] { DeadEntry, FakePathEntry },
            fileExists: probe.Exists,
            timeProvider: new ManualTimeProvider());

        var known = paths.TryGetKnownExecutable("defenseclaw", out var path);

        Assert.False(known);
        Assert.Null(path);

        // The scan it started is still stuck on the dead entry; a second peek does not queue another. (The peek came back with the probe
        // not yet reached, let alone released: it did not wait for the file system.)
        probe.WaitUntilBlocked();
        Assert.False(paths.TryGetKnownExecutable("defenseclaw", out _));
        Assert.False(probe.GaveUp, "a peek waited for the blocked scan");

        probe.Release();
        Assert.Equal(inBin, await paths.FindExecutableAsync("defenseclaw"));

        Assert.True(paths.TryGetKnownExecutable("defenseclaw", out path));
        Assert.Equal(inBin, path);
        Assert.Equal(1, probe.DeadProbes);
    }

    [Fact]
    public async Task InvalidateExecutableCache_keeps_the_old_answer_readable_until_the_rescan_lands()
    {
        var inBin = Path.Combine(FakeBin, "defenseclaw.exe");
        var onPath = Path.Combine(FakePathEntry, "defenseclaw.exe");
        var binCopyInstalled = false;
        var paths = new DefenseClawPaths(
            binDirectory: FakeBin,
            searchPath: new[] { FakePathEntry },
            fileExists: p => p == onPath || (binCopyInstalled && p == inBin),
            timeProvider: new ManualTimeProvider());

        Assert.Equal(onPath, paths.CliPath);

        binCopyInstalled = true;
        paths.InvalidateExecutableCache();

        Assert.True(paths.TryGetKnownExecutable("defenseclaw", out var stale));
        Assert.Equal(onPath, stale);

        Assert.Equal(inBin, await paths.FindExecutableAsync("defenseclaw"));
        Assert.True(paths.TryGetKnownExecutable("defenseclaw", out var fresh));
        Assert.Equal(inBin, fresh);
    }

    [Fact]
    public async Task A_directory_that_was_slow_to_probe_is_skipped_until_the_quarantine_ends()
    {
        var clock = new ManualTimeProvider();
        var onPath = Path.Combine(FakePathEntry, "defenseclaw.exe");
        var deadProbes = 0;
        var paths = new DefenseClawPaths(
            binDirectory: FakeBin,
            searchPath: new[] { DeadEntry, FakePathEntry },
            fileExists: p =>
            {
                if (p.StartsWith(DeadEntry, StringComparison.OrdinalIgnoreCase))
                {
                    deadProbes++;
                    clock.Advance(TimeSpan.FromSeconds(42));
                    return false;
                }

                return p == onPath;
            },
            timeProvider: clock);

        Assert.Equal(onPath, paths.CliPath);
        Assert.Equal(1, deadProbes);

        // The next scan does not pay for it again...
        clock.Advance(DefenseClawPaths.FoundLookupLifetime + TimeSpan.FromSeconds(1));
        Assert.Equal(onPath, await paths.FindExecutableAsync("defenseclaw"));
        Assert.Equal(1, deadProbes);

        // ...until the quarantine is over.
        clock.Advance(DefenseClawPaths.SlowDirectoryQuarantine + TimeSpan.FromSeconds(1));
        Assert.Equal(onPath, await paths.FindExecutableAsync("defenseclaw"));
        Assert.Equal(2, deadProbes);
    }

    [Fact]
    public async Task The_installer_bin_directory_is_never_skipped_for_being_slow()
    {
        var clock = new ManualTimeProvider();
        var slowBin = Path.Combine(FakeBin, "defenseclaw.exe");
        var installed = false;
        var binProbes = 0;
        var paths = new DefenseClawPaths(
            binDirectory: FakeBin,
            searchPath: Array.Empty<string>(),
            fileExists: p =>
            {
                binProbes++;
                clock.Advance(TimeSpan.FromSeconds(30));
                return installed && p == slowBin;
            },
            timeProvider: clock);

        Assert.Null(paths.CliPath);
        installed = true;

        clock.Advance(DefenseClawPaths.MissingLookupLifetime + TimeSpan.FromSeconds(1));
        Assert.Equal(slowBin, await paths.FindExecutableAsync("defenseclaw"));
        Assert.True(binProbes >= 2);
    }

    // ------------------------------------------------------------------ PATH handling

    [Fact]
    public void SplitPathList_strips_the_quotes_the_shell_strips_and_drops_blanks()
    {
        var entries = DefenseClawPaths.SplitPathList("\"C:\\Program Files\\Tool\";C:\\plain;;  \"D:\\quoted\"  ;\"\"; ").ToList();

        Assert.Equal(new[] { @"C:\Program Files\Tool", @"C:\plain", @"D:\quoted" }, entries);
    }

    [Fact]
    public void A_quoted_path_entry_is_matched()
    {
        var onPath = Path.Combine(FakePathEntry, "defenseclaw.exe");

        var fromInjectedList = new DefenseClawPaths(
            binDirectory: FakeBin,
            searchPath: new[] { "\"" + FakePathEntry + "\"" },
            fileExists: p => p == onPath);

        var fromEnvironment = new DefenseClawPaths(
            binDirectory: FakeBin,
            environment: name => name == "PATH" ? "\"" + FakePathEntry + "\";C:\\elsewhere" : null,
            fileExists: p => p == onPath);

        Assert.Equal(onPath, fromInjectedList.CliPath);
        Assert.Equal(onPath, fromEnvironment.CliPath);
    }

    [Fact]
    public void A_refresh_picks_up_a_directory_added_to_the_persisted_path_since_the_process_started()
    {
        var installDir = @"C:\fake\new-install";
        var installed = Path.Combine(installDir, "defenseclaw.exe");
        var persisted = new List<string>();
        var paths = new DefenseClawPaths(
            binDirectory: FakeBin,
            searchPath: new[] { FakePathEntry },
            fileExists: p => p == installed,
            timeProvider: new ManualTimeProvider(),
            persistedSearchPath: () => persisted);

        Assert.Null(paths.CliPath);

        // An installer appends to the user PATH in the registry after this process was started at login.
        persisted.Add(installDir);
        Assert.Null(paths.CliPath);

        paths.InvalidateExecutableCache();
        Assert.Equal(installed, paths.CliPath);
    }

    [Fact]
    public void The_persisted_path_only_adds_to_the_process_path_and_never_reorders_it()
    {
        var paths = new DefenseClawPaths(
            binDirectory: FakeBin,
            searchPath: new[] { @"C:\venv\Scripts", FakePathEntry },
            fileExists: _ => false,
            timeProvider: new ManualTimeProvider(),
            persistedSearchPath: () => new[] { FakePathEntry.ToUpperInvariant() + @"\", @"C:\machine\bin", "\"C:\\user\\bin\"" });

        paths.InvalidateExecutableCache();
        _ = paths.CliPath;

        var directories = paths.CandidatesFor("defenseclaw").Where(c => c.EndsWith("defenseclaw.exe", StringComparison.Ordinal)).Select(Path.GetDirectoryName).ToList();

        Assert.Equal(new[] { FakeBin, @"C:\venv\Scripts", FakePathEntry, @"C:\machine\bin", @"C:\user\bin" }, directories);
    }

    [Fact]
    public void A_fixed_search_path_is_never_replaced_by_the_registry()
    {
        var paths = new DefenseClawPaths(
            binDirectory: FakeBin,
            searchPath: new[] { FakePathEntry },
            fileExists: _ => false);

        paths.InvalidateExecutableCache();
        _ = paths.CliPath;

        Assert.Equal(
            new[] { Path.Combine(FakeBin, "defenseclaw.exe"), Path.Combine(FakePathEntry, "defenseclaw.exe") },
            paths.CandidatesFor("defenseclaw").ToArray());
    }

    // ------------------------------------------------------------------ the data directory follows the CLI

    private static Func<string, string?> EnvVars(params (string Name, string Value)[] variables) =>
        name => variables.Where(v => v.Name == name).Select(v => v.Value).FirstOrDefault();

    [Fact]
    public void With_nothing_set_the_data_directory_is_the_profile_default()
    {
        var resolved = DefenseClawPaths.ResolveDataDirectory(EnvVars(), userProfile: @"C:\Users\me");

        Assert.Equal(@"C:\Users\me\.defenseclaw", resolved.Path);
        Assert.Equal(DataDirectorySource.Default, resolved.Source);
        Assert.Null(resolved.Note);
        Assert.Contains("DEFENSECLAW_HOME is not set", resolved.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void DEFENSECLAW_HOME_moves_the_data_directory_exactly_as_it_moves_the_CLIs()
    {
        var resolved = DefenseClawPaths.ResolveDataDirectory(
            EnvVars(("DEFENSECLAW_HOME", @"D:\dc\home\")),
            userProfile: @"C:\Users\me");

        Assert.Equal(@"D:\dc\home", resolved.Path);
        Assert.Equal(DataDirectorySource.Environment, resolved.Source);
        Assert.Contains("DEFENSECLAW_HOME", resolved.Description, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_DEFENSECLAW_HOME_is_unset(string value)
    {
        var resolved = DefenseClawPaths.ResolveDataDirectory(EnvVars(("DEFENSECLAW_HOME", value)), userProfile: @"C:\Users\me");

        Assert.Equal(@"C:\Users\me\.defenseclaw", resolved.Path);
        Assert.Equal(DataDirectorySource.Default, resolved.Source);
    }

    [Fact]
    public void A_relative_DEFENSECLAW_HOME_is_made_absolute()
    {
        var resolved = DefenseClawPaths.ResolveDataDirectory(EnvVars(("DEFENSECLAW_HOME", @"sandbox\dc")));

        Assert.True(Path.IsPathRooted(resolved.Path));
        Assert.EndsWith(@"sandbox\dc", resolved.Path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_unusable_DEFENSECLAW_HOME_falls_back_to_the_default_and_says_so()
    {
        var resolved = DefenseClawPaths.ResolveDataDirectory(
            EnvVars(("DEFENSECLAW_HOME", "D:\\bad\0dir")),
            userProfile: @"C:\Users\me");

        Assert.Equal(@"C:\Users\me\.defenseclaw", resolved.Path);
        Assert.Equal(DataDirectorySource.Default, resolved.Source);
        Assert.Contains("not a usable path", resolved.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void DEFENSECLAW_DATA_DIR_is_not_followed_because_the_CLI_does_not_read_it_but_a_disagreement_is_reported()
    {
        var disagreeing = DefenseClawPaths.ResolveDataDirectory(
            EnvVars(("DEFENSECLAW_HOME", @"D:\dc\home"), ("DEFENSECLAW_DATA_DIR", @"E:\elsewhere")),
            userProfile: @"C:\Users\me");

        Assert.Equal(@"D:\dc\home", disagreeing.Path);
        Assert.Contains(@"E:\elsewhere", disagreeing.Note, StringComparison.Ordinal);
        Assert.Contains("does not read it", disagreeing.Note, StringComparison.Ordinal);

        var alone = DefenseClawPaths.ResolveDataDirectory(
            EnvVars(("DEFENSECLAW_DATA_DIR", @"E:\elsewhere")),
            userProfile: @"C:\Users\me");

        Assert.Equal(@"C:\Users\me\.defenseclaw", alone.Path);
        Assert.Equal(DataDirectorySource.Default, alone.Source);
        Assert.NotNull(alone.Note);

        var agreeing = DefenseClawPaths.ResolveDataDirectory(
            EnvVars(("DEFENSECLAW_HOME", @"D:\dc\home"), ("DEFENSECLAW_DATA_DIR", @"d:\DC\home\")),
            userProfile: @"C:\Users\me");

        Assert.Null(agreeing.Note);
    }

    [Fact]
    public void Every_data_file_follows_DEFENSECLAW_HOME()
    {
        var paths = new DefenseClawPaths(
            environment: EnvVars(("DEFENSECLAW_HOME", @"D:\dc\home")),
            searchPath: Array.Empty<string>());

        Assert.Equal(@"D:\dc\home", paths.DataDirectory);
        Assert.Equal(DataDirectorySource.Environment, paths.DataDirectoryOrigin.Source);
        Assert.Equal(@"D:\dc\home\config.yaml", paths.ConfigFilePath);
        Assert.Equal(@"D:\dc\home\.env", paths.EnvFilePath);
        Assert.Equal(@"D:\dc\home\audit.db", paths.AuditDatabasePath);
        Assert.Equal(@"D:\dc\home\gateway.log", paths.GatewayLogPath);
        Assert.Equal(@"D:\dc\home\doctor_cache.json", paths.DoctorCachePath);
    }

    [Fact]
    public void An_explicit_data_directory_beats_the_environment()
    {
        var paths = new DefenseClawPaths(
            dataDirectory: @"C:\data\.defenseclaw",
            environment: EnvVars(("DEFENSECLAW_HOME", @"D:\dc\home")),
            searchPath: Array.Empty<string>());

        Assert.Equal(@"C:\data\.defenseclaw", paths.DataDirectory);
        Assert.Equal(DataDirectorySource.Explicit, paths.DataDirectoryOrigin.Source);
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
