using System.Collections.Concurrent;
using System.Text.Json;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.Core.Paths;

namespace DefenseClaw.App.Tests.Wizards;

/// <summary>
/// <see cref="SetupHelpProbe"/>'s on-disk cache. Every "launch" here is a fresh probe plus a fresh
/// <see cref="SetupHelpDiskCache"/> over the same file, which is exactly what a second app process is: empty
/// in-memory state, the file the previous one left. The CLI is a stub file and its help a fake runner, so no
/// process starts and nothing outside the scratch directory is read or written.
/// </summary>
public sealed class SetupHelpDiskCacheTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly string _exe;
    private readonly string _cacheFile;
    private readonly ConcurrentQueue<string> _probed = new();
    private int _versionCalls;

    public SetupHelpDiskCacheTests()
    {
        var bin = Path.Combine(_temp.Path, "bin");
        _ = Directory.CreateDirectory(bin);
        _exe = Path.Combine(bin, "defenseclaw.exe");
        File.WriteAllText(_exe, "stub launcher v1");
        _cacheFile = Path.Combine(_temp.Path, "local-data", "cache", "setup-help-cache.json");
    }

    public void Dispose() => _temp.Dispose();

    private DefenseClawPaths Paths() =>
        new(dataDirectory: _temp.Path, binDirectory: Path.GetDirectoryName(_exe), searchPath: Array.Empty<string>());

    private static HelpProbeResult Answer(IReadOnlyList<string> path) =>
        new($"Usage: defenseclaw setup {string.Join(' ', path)} [OPTIONS]\n\n  Help for '{string.Join(' ', path)}'.\n", null);

    /// <summary>A new app process: empty memory, the same file on disk.</summary>
    private (SetupHelpProbe Probe, SetupHelpDiskCache Cache) Launch(
        string? version = "0.8.10",
        Func<IReadOnlyList<string>, HelpProbeResult>? answer = null,
        TimeSpan? flushDelay = null,
        string? cacheFile = null)
    {
        var cache = new SetupHelpDiskCache(
            cacheFile ?? _cacheFile,
            (_, _) =>
            {
                _ = Interlocked.Increment(ref _versionCalls);
                return Task.FromResult(version);
            },
            flushDelay ?? Timeout.InfiniteTimeSpan);

        var probe = new SetupHelpProbe(
            Paths(),
            cache,
            (_, path, _) =>
            {
                _probed.Enqueue(string.Join(' ', path));
                return Task.FromResult((answer ?? Answer)(path));
            });

        return (probe, cache);
    }

    private static Task<HelpProbeResult> Help(SetupHelpProbe probe, params string[] path) => probe.HelpAsync(path);

    /// <summary>Stores are filed in the background; wait for them, then write the file now.</summary>
    private static async Task<bool> FlushAsync(SetupHelpDiskCache cache)
    {
        await cache.WhenStoresCompleteAsync();
        return cache.Flush();
    }

    private JsonDocument ReadFile() => JsonDocument.Parse(File.ReadAllText(_cacheFile));

    private string[] KeysInFile()
    {
        using var document = ReadFile();
        return document.RootElement.GetProperty("screens").EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
    }

    // ------------------------------------------------------------------ the happy path

    [Fact]
    public async Task The_first_launch_probes_everything_then_writes_the_file()
    {
        var (probe, cache) = Launch();

        var top = await Help(probe);
        var claude = await Help(probe, "claude-code");
        var llm = await Help(probe, "llm");

        Assert.All(new[] { top, claude, llm }, r => Assert.True(r.Succeeded));
        Assert.Equal(new[] { "", "claude-code", "llm" }, _probed.OrderBy(k => k, StringComparer.Ordinal).ToArray());
        Assert.False(File.Exists(_cacheFile), "nothing is written until the coalesced flush");

        Assert.True(await FlushAsync(cache));

        using var document = ReadFile();
        var root = document.RootElement;
        Assert.Equal(SetupHelpDiskCache.SchemaVersion, root.GetProperty("schema").GetInt32());
        Assert.Equal("0.8.10", root.GetProperty("cli").GetProperty("version").GetString());
        Assert.Equal(_exe, root.GetProperty("cli").GetProperty("exe").GetString());
        Assert.Equal(new FileInfo(_exe).Length, root.GetProperty("cli").GetProperty("length").GetInt64());
        Assert.Equal(new[] { "", "claude-code", "llm" }, KeysInFile());
    }

    [Fact]
    public async Task A_relaunch_against_the_same_cli_answers_every_screen_from_disk_without_a_probe()
    {
        var (first, firstCache) = Launch();
        var firstTop = await Help(first);
        var firstClaude = await Help(first, "claude-code");
        Assert.True(await FlushAsync(firstCache));
        _probed.Clear();

        var (second, _) = Launch();
        var top = await Help(second);
        var claude = await Help(second, "claude-code");

        Assert.Empty(_probed);
        Assert.True(top.Succeeded);
        Assert.Equal(firstTop.Text, top.Text);
        Assert.Equal(firstClaude.Text, claude.Text);
        Assert.Equal(2, second.CachedProbeCount);
    }

    [Fact]
    public async Task The_cli_identity_is_resolved_once_however_many_screens_ask()
    {
        var (first, firstCache) = Launch();
        await Task.WhenAll(Enumerable.Range(0, 31).Select(i => Help(first, "target-" + i)));
        Assert.True(await FlushAsync(firstCache));
        Assert.Equal(1, _versionCalls);
        Assert.Equal(31, _probed.Count);

        _probed.Clear();
        _versionCalls = 0;

        var (second, _) = Launch();
        var results = await Task.WhenAll(Enumerable.Range(0, 31).Select(i => Help(second, "target-" + i)));

        Assert.All(results, r => Assert.True(r.Succeeded));
        Assert.Empty(_probed);
        Assert.Equal(1, _versionCalls);
    }

    [Fact]
    public async Task A_warm_up_that_was_cut_short_is_added_to_rather_than_replaced()
    {
        var (first, firstCache) = Launch();
        await Help(first);
        await Help(first, "a");
        Assert.True(await FlushAsync(firstCache));
        _probed.Clear();

        var (second, secondCache) = Launch();
        await Help(second);
        await Help(second, "a");
        await Help(second, "b");
        Assert.Equal(new[] { "b" }, _probed.ToArray());
        Assert.True(await FlushAsync(secondCache));
        Assert.Equal(new[] { "", "a", "b" }, KeysInFile());
        _probed.Clear();

        var (third, _) = Launch();
        await Help(third);
        await Help(third, "a");
        await Help(third, "b");
        Assert.Empty(_probed);
    }

    [Fact]
    public async Task The_coalesced_flush_writes_the_file_on_its_own_after_the_delay()
    {
        var (probe, _) = Launch(flushDelay: TimeSpan.FromMilliseconds(50));

        await Help(probe, "claude-code");
        await Help(probe, "llm");

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!File.Exists(_cacheFile) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.True(File.Exists(_cacheFile));
        await Task.Delay(200);
        Assert.Equal(new[] { "claude-code", "llm" }, KeysInFile());
    }

    [Fact]
    public async Task On_a_cold_cache_a_slow_identity_does_not_hold_back_the_probe_answer()
    {
        // First launch: nothing on disk to validate, so the caller's answer must not wait for the CLI to
        // report its version (a second Python start-up) - only the bookkeeping does.
        var identity = new TaskCompletionSource<string?>();
        var cache = new SetupHelpDiskCache(_cacheFile, (_, _) => identity.Task, Timeout.InfiniteTimeSpan);
        var probe = new SetupHelpProbe(Paths(), cache, (_, path, _) => Task.FromResult(Answer(path)));

        var result = await Help(probe, "claude-code").WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(result.Succeeded);
        Assert.False(File.Exists(_cacheFile));

        identity.SetResult("0.8.10");
        Assert.True(await FlushAsync(cache));
        Assert.Equal(new[] { "claude-code" }, KeysInFile());
    }

    [Fact]
    public async Task A_version_probe_that_throws_costs_the_cache_but_never_the_answer()
    {
        var cache = new SetupHelpDiskCache(_cacheFile, (_, _) => throw new InvalidOperationException("boom"), Timeout.InfiniteTimeSpan);
        var probe = new SetupHelpProbe(Paths(), cache, (_, path, _) => Task.FromResult(Answer(path)));

        var result = await Help(probe, "claude-code");

        Assert.True(result.Succeeded);
        Assert.False(await FlushAsync(cache));
        Assert.False(File.Exists(_cacheFile));
    }

    // ------------------------------------------------------------------ invalidation by identity

    [Fact]
    public async Task A_different_cli_version_retires_the_file_and_reprobes_everything()
    {
        var (old, oldCache) = Launch(version: "0.8.10");
        await Help(old, "claude-code");
        await Help(old, "llm");
        Assert.True(await FlushAsync(oldCache));
        _probed.Clear();

        var (upgraded, upgradedCache) = Launch(version: "0.8.11");
        await Help(upgraded, "claude-code");
        await Help(upgraded, "llm");

        Assert.Equal(new[] { "claude-code", "llm" }, _probed.OrderBy(k => k, StringComparer.Ordinal).ToArray());
        Assert.True(await FlushAsync(upgradedCache));

        using var document = ReadFile();
        Assert.Equal("0.8.11", document.RootElement.GetProperty("cli").GetProperty("version").GetString());
    }

    [Fact]
    public async Task A_stale_screen_from_the_old_version_is_never_carried_into_the_new_file()
    {
        var (old, oldCache) = Launch(version: "0.8.10");
        await Help(old, "removed-in-next-release");
        await Help(old, "claude-code");
        Assert.True(await FlushAsync(oldCache));

        var (upgraded, upgradedCache) = Launch(version: "0.8.11");
        await Help(upgraded, "claude-code");
        Assert.True(await FlushAsync(upgradedCache));

        Assert.Equal(new[] { "claude-code" }, KeysInFile());
    }

    [Fact]
    public async Task A_replaced_executable_retires_the_file_even_when_the_version_string_is_unchanged()
    {
        var (before, beforeCache) = Launch();
        await Help(before, "claude-code");
        Assert.True(await FlushAsync(beforeCache));
        _probed.Clear();

        // Same reported version, different launcher: reinstalled over the top, or an editable install.
        File.WriteAllText(_exe, "stub launcher v1 - rebuilt, and longer");

        var (after, _) = Launch();
        await Help(after, "claude-code");

        Assert.Equal(new[] { "claude-code" }, _probed.ToArray());
    }

    [Fact]
    public async Task A_touched_executable_retires_the_file_even_at_the_same_size_and_version()
    {
        var (before, beforeCache) = Launch();
        await Help(before, "claude-code");
        Assert.True(await FlushAsync(beforeCache));
        _probed.Clear();

        File.SetLastWriteTimeUtc(_exe, File.GetLastWriteTimeUtc(_exe).AddMinutes(-7));

        var (after, _) = Launch();
        await Help(after, "claude-code");

        Assert.Equal(new[] { "claude-code" }, _probed.ToArray());
    }

    [Fact]
    public async Task A_cli_that_cannot_report_a_version_gets_no_cache_in_either_direction()
    {
        var (old, oldCache) = Launch(version: null);
        await Help(old, "claude-code");

        Assert.False(await FlushAsync(oldCache));
        Assert.False(File.Exists(_cacheFile));

        // And a file written by a CLI that could is not served to one that cannot.
        var (capable, capableCache) = Launch(version: "0.8.10");
        await Help(capable, "claude-code");
        Assert.True(await FlushAsync(capableCache));
        _probed.Clear();

        var (mute, _) = Launch(version: null);
        await Help(mute, "claude-code");

        Assert.Equal(new[] { "claude-code" }, _probed.ToArray());
    }

    // ------------------------------------------------------------------ only successes, and Refresh

    [Fact]
    public async Task A_failed_probe_is_not_persisted_and_is_retried_next_launch()
    {
        HelpProbeResult Flaky(IReadOnlyList<string> path) =>
            path.Count == 1 && path[0] == "broken" ? new HelpProbeResult(string.Empty, "The help probe timed out.") : Answer(path);

        var (first, firstCache) = Launch(answer: Flaky);
        Assert.False((await Help(first, "broken")).Succeeded);
        Assert.True((await Help(first, "fine")).Succeeded);
        Assert.True(await FlushAsync(firstCache));
        Assert.Equal(new[] { "fine" }, KeysInFile());
        _probed.Clear();

        var (second, _) = Launch();
        await Help(second, "broken");
        await Help(second, "fine");

        Assert.Equal(new[] { "broken" }, _probed.ToArray());
    }

    [Fact]
    public async Task Refresh_deletes_the_file_and_reprobes_even_though_the_identity_is_unchanged()
    {
        var (probe, cache) = Launch();
        await Help(probe, "claude-code");
        Assert.True(await FlushAsync(cache));
        Assert.True(File.Exists(_cacheFile));
        _probed.Clear();

        var (relaunched, relaunchedCache) = Launch();
        relaunched.Clear();

        Assert.False(File.Exists(_cacheFile));
        await Help(relaunched, "claude-code");
        Assert.Equal(new[] { "claude-code" }, _probed.ToArray());

        // The fresh answer is persisted again for the launch after.
        Assert.True(await FlushAsync(relaunchedCache));
        Assert.Equal(new[] { "claude-code" }, KeysInFile());
    }

    [Fact]
    public async Task An_answer_that_lands_after_a_refresh_is_not_written_into_the_fresh_cache()
    {
        var gate = new TaskCompletionSource();
        var cache = new SetupHelpDiskCache(_cacheFile, (_, _) => Task.FromResult<string?>("0.8.10"), Timeout.InfiniteTimeSpan);
        var probe = new SetupHelpProbe(
            Paths(),
            cache,
            async (_, path, _) =>
            {
                _probed.Enqueue(string.Join(' ', path));
                await gate.Task;
                return Answer(path);
            });

        var slow = Help(probe, "claude-code");
        while (_probed.IsEmpty)
        {
            await Task.Delay(5);
        }

        probe.Clear();
        gate.SetResult();
        Assert.True((await slow).Succeeded);

        Assert.False(await FlushAsync(cache));
        Assert.False(File.Exists(_cacheFile));
    }

    // ------------------------------------------------------------------ a bad file is a miss, never an error

    public static TheoryData<string, string> BadFiles => new()
    {
        { "empty", "" },
        { "not json", "this is not json at all" },
        { "truncated mid-write", "{\"schema\":1,\"cli\":{\"exe\":\"C:\\\\x\\\\defenseclaw.exe\",\"length\":1,\"lastWriteUtcTicks\":1,\"version\":\"0.8.10\"},\"screens\":{\"claude-c" },
        { "json null", "null" },
        { "json array", "[1,2,3]" },
        { "unknown schema", "{\"schema\":99,\"cli\":{\"exe\":\"x\",\"length\":1,\"lastWriteUtcTicks\":1,\"version\":\"0.8.10\"},\"screens\":{\"claude-code\":\"Usage: stale\"}}" },
        { "no schema", "{\"cli\":{\"exe\":\"x\",\"length\":1,\"lastWriteUtcTicks\":1,\"version\":\"0.8.10\"},\"screens\":{\"claude-code\":\"Usage: stale\"}}" },
        { "no cli", "{\"schema\":1,\"screens\":{\"claude-code\":\"Usage: stale\"}}" },
        { "no screens", "{\"schema\":1,\"cli\":{\"exe\":\"x\",\"length\":1,\"lastWriteUtcTicks\":1,\"version\":\"0.8.10\"}}" },
        { "blank version", "{\"schema\":1,\"cli\":{\"exe\":\"x\",\"length\":1,\"lastWriteUtcTicks\":1,\"version\":\" \"},\"screens\":{\"claude-code\":\"Usage: stale\"}}" },
        { "wrong types", "{\"schema\":\"one\",\"cli\":7,\"screens\":[]}" },
        { "binary junk", "\u0000\u0001\u0002\uFFFD\uFFFD" },
    };

    [Theory]
    [MemberData(nameof(BadFiles))]
    public async Task A_corrupt_partial_or_foreign_file_is_ignored_and_replaced_by_a_good_one(string label, string content)
    {
        _ = Directory.CreateDirectory(Path.GetDirectoryName(_cacheFile)!);
        File.WriteAllText(_cacheFile, content);

        var (probe, cache) = Launch();
        var result = await Help(probe, "claude-code");

        Assert.True(result.Succeeded, label);
        Assert.Equal(new[] { "claude-code" }, _probed.ToArray());
        Assert.DoesNotContain("stale", result.Text, StringComparison.Ordinal);

        Assert.True(await FlushAsync(cache), label);
        Assert.Equal(new[] { "claude-code" }, KeysInFile());
        _probed.Clear();

        var (next, _) = Launch();
        await Help(next, "claude-code");
        Assert.Empty(_probed);
    }

    [Fact]
    public async Task A_file_whose_identity_matches_but_whose_screen_is_blank_is_reprobed()
    {
        var (seed, seedCache) = Launch();
        await Help(seed, "claude-code");
        await Help(seed, "llm");
        Assert.True(await FlushAsync(seedCache));

        // Hand-damage one entry, leaving the identity valid.
        var text = File.ReadAllText(_cacheFile);
        using (var document = JsonDocument.Parse(text))
        {
            var llm = document.RootElement.GetProperty("screens").GetProperty("llm").GetString()!;
            File.WriteAllText(_cacheFile, text.Replace(JsonSerializer.Serialize(llm).Trim('"'), string.Empty, StringComparison.Ordinal));
        }

        _probed.Clear();
        var (after, _) = Launch();
        await Help(after, "claude-code");
        await Help(after, "llm");

        Assert.Equal(new[] { "llm" }, _probed.ToArray());
    }

    // ------------------------------------------------------------------ writing

    [Fact]
    public async Task The_write_is_a_swap_and_leaves_no_temporary_file_behind()
    {
        var (probe, cache) = Launch();
        await Help(probe, "claude-code");
        Assert.True(await FlushAsync(cache));
        await Help(probe, "llm");
        Assert.True(await FlushAsync(cache));

        Assert.Equal(new[] { "claude-code", "llm" }, KeysInFile());
        Assert.Equal(new[] { _cacheFile }, Directory.GetFiles(Path.GetDirectoryName(_cacheFile)!));
    }

    [Fact]
    public async Task A_temporary_file_left_by_a_crash_is_overwritten_and_does_not_disturb_loading()
    {
        _ = Directory.CreateDirectory(Path.GetDirectoryName(_cacheFile)!);
        File.WriteAllText(_cacheFile + ".tmp", "{\"schema\":1,\"half a fi");

        var (probe, cache) = Launch();
        await Help(probe, "claude-code");
        Assert.True(await FlushAsync(cache));

        Assert.Equal(new[] { "claude-code" }, KeysInFile());
        Assert.Equal(new[] { _cacheFile }, Directory.GetFiles(Path.GetDirectoryName(_cacheFile)!));
    }

    [Fact]
    public async Task A_write_that_cannot_happen_is_swallowed_and_the_probe_still_answers()
    {
        // A directory where the file should go: every way of replacing it fails.
        _ = Directory.CreateDirectory(_cacheFile);

        var (probe, cache) = Launch();
        var result = await Help(probe, "claude-code");

        Assert.True(result.Succeeded);
        Assert.False(await FlushAsync(cache));
        Assert.True(Directory.Exists(_cacheFile));
    }

    [Fact]
    public async Task The_cache_directory_is_created_on_the_first_write()
    {
        var deep = Path.Combine(_temp.Path, "does", "not", "exist", "yet", "cache.json");
        var (probe, cache) = Launch(cacheFile: deep);

        await Help(probe, "claude-code");

        Assert.True(await FlushAsync(cache));
        Assert.True(File.Exists(deep));
    }

    [Fact]
    public async Task Without_a_disk_cache_the_probe_behaves_as_it_did_before()
    {
        var probe = new SetupHelpProbe(
            Paths(),
            diskCache: null,
            (_, path, _) =>
            {
                _probed.Enqueue(string.Join(' ', path));
                return Task.FromResult(Answer(path));
            });

        await Help(probe, "claude-code");
        await Help(probe, "claude-code");

        Assert.Equal(new[] { "claude-code" }, _probed.ToArray());
        Assert.Equal(1, probe.CachedProbeCount);
        Assert.False(File.Exists(_cacheFile));
    }

    // ------------------------------------------------------------------ where it lives

    [Fact]
    public void The_default_location_is_under_the_apps_own_local_data_directory_never_the_defenseclaw_home()
    {
        var path = SetupHelpDiskCache.DefaultFilePath();
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Assert.StartsWith(Path.Combine(localAppData, "DefenseClaw.App") + Path.DirectorySeparatorChar, path, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.Combine(userHome, ".defenseclaw"), path, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(".json", path, StringComparison.OrdinalIgnoreCase);
    }
}
