using DefenseClaw.Core.Runtime;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// <see cref="RuntimeDetector"/>: which probes it sends, that they are all read-only, that the answer is cached by the CLI's
/// fingerprint and re-probed when that moves, and that a timeout or garbage is "unknown" and never a hang or a throw.
/// </summary>
public class RuntimeDetectorTests
{
    /// <summary>Answers each probe from a fixture set, and records what was asked.</summary>
    private sealed class FixtureRuntime
    {
        private readonly string _set;
        private readonly List<string> _asked = [];

        public FixtureRuntime(string set) => _set = set;

        public IReadOnlyList<string> Asked
        {
            get
            {
                lock (_asked)
                {
                    return _asked.ToArray();
                }
            }
        }

        public Task<RuntimeProbeOutput> Run(IReadOnlyList<string> arguments, CancellationToken token)
        {
            lock (_asked)
            {
                _asked.Add(string.Join(' ', arguments));
            }

            var file = string.Join(' ', arguments) switch
            {
                "--version-json" => "version.json",
                "--help" => "root.txt",
                "setup --help" => "setup.txt",
                "guardrail --help" => "guardrail.txt",
                "config --help" => "config.txt",
                "sandbox --help" => "sandbox.txt",
                "acp --help" => "acp.txt",
                "setup redaction --help" => "setup-redaction.txt",
                _ => null,
            };

            var path = file is null ? null : Path.Combine(FixtureFiles.Directory, "runtime-" + _set, file);
            return Task.FromResult(path is not null && File.Exists(path)
                ? RuntimeProbeOutput.Ok(File.ReadAllText(path))
                : RuntimeProbeOutput.Fail("exit 2"));
        }
    }

    private static RuntimeDetector Detector(RuntimeProbeRunner runner, Func<string?> fingerprint, TimeProvider? time = null) =>
        new(runner, fingerprint, "defenseclaw.exe", time)
        {
            ProbeTimeout = TimeSpan.FromSeconds(30),
            TotalTimeout = TimeSpan.FromSeconds(60),
        };

    [Fact]
    public async Task Against_0_8_10_it_asks_six_questions_and_never_about_acp_or_redaction()
    {
        var runtime = new FixtureRuntime("0.8.10");
        var detector = Detector(runtime.Run, () => "fp1");

        var snapshot = await detector.RefreshAsync();

        Assert.True(snapshot.IsKnown);
        Assert.Equal("0.8.10", snapshot.Identity!.Version);
        Assert.Empty(snapshot.Capabilities.Present);
        Assert.Equal(
            new[] { "--help", "--version-json", "config --help", "guardrail --help", "sandbox --help", "setup --help" },
            runtime.Asked.Order(StringComparer.Ordinal).ToArray());
        Assert.Same(snapshot, detector.Current);
    }

    [Fact]
    public async Task Against_the_pin_it_follows_what_the_first_round_found_and_every_probe_is_read_only()
    {
        var runtime = new FixtureRuntime("95159fd");
        var detector = Detector(runtime.Run, () => "fp1");

        var snapshot = await detector.RefreshAsync();

        Assert.True(snapshot.IsKnown);
        Assert.Equal(RuntimeCapabilityCatalog.All, snapshot.Capabilities.Present.ToArray());
        Assert.Contains("acp --help", runtime.Asked);
        Assert.Contains("setup redaction --help", runtime.Asked);
        Assert.Equal(8, runtime.Asked.Count);

        // Read-only by construction: help screens and the version document, nothing else.
        Assert.All(runtime.Asked, probe => Assert.True(
            probe == "--version-json" || probe.EndsWith(" --help", StringComparison.Ordinal) || probe == "--help", probe));
    }

    [Fact]
    public async Task An_unchanged_fingerprint_is_not_probed_again_and_a_changed_one_is()
    {
        var runtime = new FixtureRuntime("0.8.10");
        var fingerprint = "fp1";
        var detector = Detector(runtime.Run, () => fingerprint);

        var first = await detector.RefreshAsync();
        var asked = runtime.Asked.Count;
        var second = await detector.RefreshAsync();

        Assert.Same(first, second);
        Assert.Equal(asked, runtime.Asked.Count);

        fingerprint = "fp2"; // the CLI file changed: an upgrade, a reinstall
        var third = await detector.RefreshAsync();

        Assert.NotSame(first, third);
        Assert.Equal("fp2", third.Fingerprint);
        Assert.Equal(asked * 2, runtime.Asked.Count);

        _ = await detector.RefreshAsync(force: true);
        Assert.Equal(asked * 3, runtime.Asked.Count);
    }

    [Fact]
    public async Task Changed_is_raised_when_the_answer_moves_and_not_when_a_reprobe_finds_the_same_runtime()
    {
        var runtime = new FixtureRuntime("0.8.10");
        var detector = Detector(runtime.Run, () => "fp1");
        var raised = 0;
        detector.Changed += (_, _) => Interlocked.Increment(ref raised);

        _ = await detector.RefreshAsync();
        Assert.Equal(1, raised);

        _ = await detector.RefreshAsync(force: true);
        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task A_missing_cli_is_unknown_and_no_probe_is_started()
    {
        var runtime = new FixtureRuntime("95159fd");
        var detector = Detector(runtime.Run, () => null);

        var snapshot = await detector.RefreshAsync();

        Assert.False(snapshot.IsKnown);
        Assert.Equal("The DefenseClaw CLI was not found.", snapshot.UnknownReason);
        Assert.Empty(runtime.Asked);
        Assert.Empty(detector.Capabilities.Present);
    }

    [Fact]
    public async Task A_probe_that_hangs_is_cut_off_and_the_runtime_is_unknown()
    {
        static async Task<RuntimeProbeOutput> Hang(IReadOnlyList<string> _, CancellationToken token)
        {
            await Task.Delay(Timeout.Infinite, token);
            return RuntimeProbeOutput.Fail("unreachable");
        }

        var detector = new RuntimeDetector(Hang, () => "fp1", "defenseclaw.exe")
        {
            ProbeTimeout = TimeSpan.FromMilliseconds(50),
            TotalTimeout = TimeSpan.FromSeconds(30),
        };

        var snapshot = await detector.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(snapshot.IsKnown);
        Assert.Empty(snapshot.Capabilities.Present);
    }

    [Fact]
    public async Task A_round_that_runs_past_the_total_budget_is_unknown()
    {
        var calls = 0;
        async Task<RuntimeProbeOutput> Slow(IReadOnlyList<string> _, CancellationToken token)
        {
            Interlocked.Increment(ref calls);
            await Task.Delay(Timeout.Infinite, token);
            return RuntimeProbeOutput.Fail("unreachable");
        }

        var detector = new RuntimeDetector(Slow, () => "fp1", "defenseclaw.exe")
        {
            ProbeTimeout = TimeSpan.FromSeconds(30),
            TotalTimeout = TimeSpan.FromMilliseconds(100),
        };

        var snapshot = await detector.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(snapshot.IsKnown);
        Assert.Equal("Probing the runtime timed out.", snapshot.UnknownReason);
        Assert.True(calls >= 1);
    }

    [Fact]
    public async Task A_runner_that_throws_or_returns_garbage_is_unknown_not_an_exception()
    {
        var thrower = Detector((_, _) => throw new InvalidOperationException("boom"), () => "fp1");
        var garbage = Detector((_, _) => Task.FromResult(RuntimeProbeOutput.Ok("\u0001\u0002 not a version")), () => "fp1");

        Assert.False((await thrower.RefreshAsync()).IsKnown);
        Assert.False((await garbage.RefreshAsync()).IsKnown);
        Assert.Empty(thrower.Capabilities.Present);
        Assert.Empty(garbage.Capabilities.Present);
    }

    [Fact]
    public async Task An_unknown_answer_is_retried_only_after_the_retry_window()
    {
        var clock = new ManualClock();
        var calls = 0;
        var good = false;
        var inner = new FixtureRuntime("0.8.10");
        Task<RuntimeProbeOutput> Flaky(IReadOnlyList<string> arguments, CancellationToken token)
        {
            Interlocked.Increment(ref calls);
            return good ? inner.Run(arguments, token) : Task.FromResult(RuntimeProbeOutput.Fail("busy"));
        }

        var detector = Detector(Flaky, () => "fp1", clock);

        Assert.False((await detector.RefreshAsync()).IsKnown);
        var afterFirst = calls;

        good = true;
        Assert.False((await detector.RefreshAsync()).IsKnown);
        Assert.Equal(afterFirst, calls);

        clock.Advance(RuntimeDetector.DefaultUnknownRetryAfter + TimeSpan.FromSeconds(1));
        var healed = await detector.RefreshAsync();

        Assert.True(healed.IsKnown);
        Assert.True(calls > afterFirst);
    }

    [Fact]
    public async Task Concurrent_refreshes_share_one_round()
    {
        var gate = new TaskCompletionSource();
        var calls = 0;
        var inner = new FixtureRuntime("0.8.10");
        async Task<RuntimeProbeOutput> Gated(IReadOnlyList<string> arguments, CancellationToken token)
        {
            Interlocked.Increment(ref calls);
            await gate.Task;
            return await inner.Run(arguments, token);
        }

        var detector = Detector(Gated, () => "fp1");
        var a = detector.RefreshAsync();
        var b = detector.RefreshAsync();
        gate.SetResult();

        Assert.Same(await a, await b);
        Assert.Equal(6, calls);
    }

    [Fact]
    public void Before_the_first_probe_nothing_is_known_and_reading_does_not_block()
    {
        var detector = Detector((_, _) => Task.FromResult(RuntimeProbeOutput.Fail("x")), () => "fp1");

        Assert.False(detector.Current.IsKnown);
        Assert.Same(RuntimeSnapshot.NotProbed, detector.Current);
        Assert.False(detector.Capabilities.Has(RuntimeCapability.Sandbox));
    }

    [Fact]
    public void The_fingerprint_moves_with_the_files_size_and_time()
    {
        using var temp = new TempDirectory();
        var file = temp.Write("defenseclaw.exe", "one");
        var before = RuntimeFingerprint.ForFile(file);

        Assert.NotNull(before);
        Assert.Equal(before, RuntimeFingerprint.ForFile(file));

        File.WriteAllText(file, "three");
        Assert.NotEqual(before, RuntimeFingerprint.ForFile(file));

        var stamped = RuntimeFingerprint.ForFile(file);
        File.SetLastWriteTimeUtc(file, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.NotEqual(stamped, RuntimeFingerprint.ForFile(file));

        Assert.Null(RuntimeFingerprint.ForFile(temp.File("missing.exe")));
        Assert.Null(RuntimeFingerprint.ForFile(null));
        Assert.Null(RuntimeFingerprint.ForFile("  "));
    }

    [Theory]
    [InlineData("--version-json", true)]
    [InlineData("--help", true)]
    [InlineData("setup --help", true)]
    [InlineData("setup redaction --help", true)]
    [InlineData("setup redaction apply", false)]
    [InlineData("setup --yes --help", false)]
    [InlineData("keys set --help", true)]
    [InlineData("--version-json extra", false)]
    [InlineData("", false)]
    public void The_production_runner_will_only_ever_launch_help_and_version_probes(string joined, bool expected)
    {
        var args = joined.Length == 0 ? Array.Empty<string>() : joined.Split(' ');
        Assert.Equal(expected, RuntimeEnvironment.IsReadOnlyProbe(args));
    }
}

/// <summary>
/// The production probe runner against the CLI really installed on this machine (skipped where there is none, as on CI). Read-only by
/// construction: the runner launches nothing but <c>--version-json</c> and <c>--help</c>.
/// </summary>
public class RuntimeDetectorLiveTests
{
    [LiveFact]
    public async Task The_installed_cli_is_identified_and_its_capabilities_match_its_version()
    {
        var paths = new DefenseClaw.Core.Paths.DefenseClawPaths();
        var detector = new RuntimeDetector(
            RuntimeEnvironment.CreateProbeRunner(paths),
            () => RuntimeEnvironment.Fingerprint(paths),
            () => RuntimeEnvironment.Source(paths));

        var snapshot = await detector.RefreshAsync().WaitAsync(TimeSpan.FromMinutes(2));

        Assert.True(snapshot.IsKnown, snapshot.UnknownReason);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.Identity!.Version));

        // A 0.x runtime (0.8.10 is what this app targets) has none of the newer capabilities.
        if (snapshot.Identity.ParsedVersion is { Major: 0 })
        {
            Assert.Empty(snapshot.Capabilities.Present);
        }
    }
}
