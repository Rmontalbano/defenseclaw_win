using System.Diagnostics;
using System.Net;
using System.Text;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Config;
using DefenseClaw.Core.Gateway;
using DefenseClaw.Core.IO;

namespace DefenseClaw.Tests;

/// <summary>
/// CUST-250: nothing a peer controls is read without a bound. Caps are injectable (or the scanner is called directly) so no test writes
/// more than a few KB - except the one that proves the real config.yaml limit end to end.
/// </summary>
public sealed class ReadLimitsTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "dcw-readlimits-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Write(string name, string text)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, text);
        return path;
    }

    // --- SharedFile ---------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_file_within_the_limit_reads_whole()
    {
        var path = Write("ok.txt", "hello");

        Assert.Equal("hello", SharedFile.ReadAllText(path, maxBytes: 5));
        Assert.Equal(5, SharedFile.ReadAllBytes(path, maxBytes: 5).Length);
    }

    [Fact]
    public async Task A_file_over_the_limit_is_refused_unread_by_every_overload()
    {
        var path = Write("big.txt", new string('x', 100));

        var sync = Assert.Throws<FileTooLargeException>(() => SharedFile.ReadAllText(path, maxBytes: 99));
        Assert.Equal(100, sync.Length);
        Assert.Equal(99, sync.Limit);
        Assert.Contains("big.txt", sync.Message, StringComparison.Ordinal);
        Assert.IsAssignableFrom<IOException>(sync);
        _ = Assert.Throws<FileTooLargeException>(() => SharedFile.ReadAllBytes(path, maxBytes: 99));
        _ = await Assert.ThrowsAsync<FileTooLargeException>(() => SharedFile.ReadAllTextAsync(path, maxBytes: 99));
        _ = await Assert.ThrowsAsync<FileTooLargeException>(() => SharedFile.ReadAllBytesAsync(path, maxBytes: 99));
    }

    [Fact]
    public void The_default_limit_is_the_shared_constant()
    {
        Assert.Equal(16L * 1024 * 1024, ReadLimits.StateFileBytes);
        Assert.Equal(16L * 1024 * 1024, ReadLimits.HttpBodyBytes);
        Assert.Equal(1024L * 1024, ReadLimits.ConfigYamlBytes);
    }

    // --- config.yaml --------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("a: [1, 2, {b: 3}]\nc: {d: [x]}\n", 2)]
    [InlineData("a: \"[[[[[[\"\nb: '{{{{{'\n", 0)]
    [InlineData("a: x # [[[[[[[[\n", 0)]
    [InlineData("a: don't [x]\n", 1)]
    [InlineData("a: 'it''s [[' \nb: [[1]]\n", 2)]
    [InlineData("a: \"esc \\\" [[[\"\nb: [1]\n", 1)]
    [InlineData("a: ]]]]\nb: [[1]]\n", 2)]
    public void Flow_depth_counts_brackets_that_are_syntax_and_not_text(string yaml, int expected)
    {
        Assert.Equal(expected, ConfigYamlGuard.FlowDepth(yaml));
    }

    [Fact]
    public void A_deeply_nested_flow_collection_is_refused_in_milliseconds()
    {
        var yaml = "a: " + new string('[', 5000) + new string(']', 5000) + "\n";

        var clock = Stopwatch.StartNew();
        var ex = Assert.Throws<ConfigParseException>(() => ConfigStore.Parse(yaml, "config.yaml"));

        Assert.Contains("nests flow collections", ex.Message, StringComparison.Ordinal);
        Assert.True(clock.Elapsed < TestSupport.TestTimeouts.Ceiling, "the refusal must not run the parser");
    }

    [Fact]
    public void A_mapping_nested_past_the_limit_is_refused_and_one_at_the_limit_parses()
    {
        string Nest(int depth) => "a: " + string.Concat(Enumerable.Repeat("{x: ", depth)) + "1" + new string('}', depth) + "\n";

        _ = Assert.Throws<ConfigParseException>(() => ConfigStore.Parse(Nest(ReadLimits.ConfigYamlFlowDepth + 1)));
        Assert.Null(ConfigYamlGuard.Refusal(Nest(ReadLimits.ConfigYamlFlowDepth)));
    }

    [Fact]
    public void Oversize_text_is_refused_by_the_guard_with_its_sizes()
    {
        var refusal = ConfigYamlGuard.Refusal(new string('a', 2048), maxBytes: 1024);

        Assert.NotNull(refusal);
        Assert.Contains("2 KiB", refusal, StringComparison.Ordinal);
        Assert.Contains("1 KiB", refusal, StringComparison.Ordinal);
        Assert.Null(ConfigYamlGuard.Refusal(new string('a', 1024), maxBytes: 1024));
    }

    [Fact]
    public void A_normal_config_is_untouched()
    {
        var fixture = File.ReadAllText(TestSupport.FixtureFiles.PathTo(TestSupport.FixtureFiles.ConfigYaml));

        Assert.Null(ConfigYamlGuard.Refusal(fixture));
        _ = ConfigStore.Parse(fixture, "config.yaml");
    }

    [Fact]
    public void A_config_file_over_one_mebibyte_is_a_parse_error_not_a_read()
    {
        var paths = new DefenseClaw.Core.Paths.DefenseClawPaths(dataDirectory: _dir);
        File.WriteAllText(paths.ConfigFilePath, "# " + new string('x', (int)ReadLimits.ConfigYamlBytes) + "\n");
        var store = new ConfigStore(paths);

        var ex = Assert.Throws<ConfigParseException>(() => store.Load());

        Assert.Equal(paths.ConfigFilePath, ex.Path);
        Assert.Contains("over the 1 MiB limit", ex.Message, StringComparison.Ordinal);
    }

    // --- .env ---------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void An_oversize_dotenv_reads_as_empty_like_any_unreadable_one()
    {
        var path = Write(".env", "KEY=" + new string('v', (int)ReadLimits.DotEnvBytes) + "\n");

        Assert.Empty(DotEnvFile.Load(path));
        File.WriteAllText(path, "KEY=value\n");
        Assert.Equal("value", DotEnvFile.Load(path)["KEY"]);
    }

    // --- HTTP ---------------------------------------------------------------------------------------------------------------------------------

    private sealed class Fixed(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond());
    }

    [Fact]
    public void The_gateway_client_and_the_body_cap_helper_use_the_shared_http_limit()
    {
        using var http = new HttpClient().WithBodyCap();

        Assert.Equal(ReadLimits.HttpBodyBytes, http.MaxResponseContentBufferSize);
    }

    [Fact]
    public async Task A_buffered_gateway_answer_over_the_cap_is_an_unreachable_result_not_a_giant_string()
    {
        using var http = new HttpClient(new Fixed(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[" + new string(' ', 5000) + "]", Encoding.UTF8, "application/json"),
        }))
        {
            BaseAddress = new Uri("http://127.0.0.1:18970/"),
            MaxResponseContentBufferSize = 1024,
        };
        using var client = new GatewayClient(http, () => new SecretValue("fixture-bearer-token-0123456789"));

        var result = await client.GetSkillsAsync();

        Assert.False(result.IsOk);
        Assert.Equal(GatewayStatus.Unreachable, result.Status);
    }

    // --- child output lines -------------------------------------------------------------------------------------------------------------------

    private static async Task<List<string>> ReadAll(string text, int maxChars)
    {
        var reader = new BoundedLineReader(new StringReader(text), maxChars);
        var lines = new List<string>();
        while (await reader.ReadLineAsync() is { } line)
        {
            lines.Add(line);
        }

        return lines;
    }

    [Theory]
    [InlineData("a\nb\nc", new[] { "a", "b", "c" })]
    [InlineData("a\r\nb\r\n", new[] { "a", "b" })]
    [InlineData("a\rb\n\nc\n", new[] { "a", "b", "", "c" })]
    [InlineData("", new string[0])]
    [InlineData("\n", new[] { "" })]
    public async Task Lines_split_the_way_Process_splits_them(string text, string[] expected)
    {
        Assert.Equal(expected, await ReadAll(text, 100));
    }

    [Fact]
    public async Task A_line_longer_than_the_cap_keeps_a_prefix_and_the_marker_and_the_next_line_is_intact()
    {
        var lines = await ReadAll(string.Concat(Enumerable.Repeat("z ", 25_000)) + "\nnext\n", maxChars: 10);

        Assert.Equal(2, lines.Count);
        Assert.Equal("z z z z z " + ReadLimits.CliLineTruncatedMarker, lines[0]);
        Assert.Equal("next", lines[1]);
    }

    [Fact]
    public async Task A_newline_less_stream_is_capped_at_end_of_input_and_a_line_at_the_cap_is_not_marked()
    {
        var capped = await ReadAll(string.Concat(Enumerable.Repeat("q ", 15_000)), maxChars: 100);
        var exact = await ReadAll(new string('q', 100) + "\n", maxChars: 100);

        Assert.Equal(string.Concat(Enumerable.Repeat("q ", 50)) + ReadLimits.CliLineTruncatedMarker, Assert.Single(capped));
        Assert.Equal(new string('q', 100), Assert.Single(exact));
    }

    // CUST-341: a secret straddling the cut must not leave its front behind.

    [Fact]
    public async Task A_token_like_tail_at_the_cut_is_dropped_whole()
    {
        // 'note: ' then a token-like run that the cut lands inside.
        var lines = await ReadAll("note: " + new string('t', 40) + "\nnext\n", maxChars: 16);

        Assert.Equal("note: " + ReadLimits.CliLineTruncatedMarker, lines[0]);
        Assert.Equal("next", lines[1]);
    }

    [Fact]
    public async Task A_token_run_longer_than_the_bound_is_kept_rather_than_emptying_the_line()
    {
        var run = new string('a', BoundedLineReader.MaxTokenTailChars + 50);
        var reader = new BoundedLineReader(new StringReader("x " + run + "\n"), maxChars: run.Length);

        var line = await reader.ReadLineAsync();

        Assert.NotNull(line);
        Assert.Contains("aaaa", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_cut_text_goes_through_the_sanitizer_before_the_tail_is_dropped()
    {
        var reader = new BoundedLineReader(
            new StringReader("pw is s3cret! and more padding here\n"),
            maxChars: 16,
            sanitizeCut: kept => kept.Replace("s3cret!", "<x>", StringComparison.Ordinal));

        var line = await reader.ReadLineAsync();

        // "pw is s3cret! an" -> "pw is <x> an" -> the token tail "an" is dropped.
        Assert.Equal("pw is <x> " + ReadLimits.CliLineTruncatedMarker, line);
    }

    [Theory]
    [InlineData("see: p@ss:w", "see: ")]            // partial secret of non-token characters is dropped by prefix
    [InlineData("see: p@ss:w0rd!", "see: ***REDACTED***")]  // a whole secret is scrubbed
    [InlineData("see: p", "see: ")]
    [InlineData("see: x", "see: x")]
    public void The_runner_drops_a_trailing_partial_secret_and_scrubs_a_whole_one(string kept, string expected)
    {
        var runner = new CliRunner(new DefenseClaw.Core.Paths.DefenseClawPaths(dataDirectory: _dir));
        runner.RegisterSecret(new SecretValue("p@ss:w0rd!"));

        Assert.Equal(expected, runner.ScrubCut(kept, Array.Empty<SecretValue>()));
    }

    [Fact]
    public void The_runner_drops_a_partial_per_call_secret_too()
    {
        var runner = new CliRunner(new DefenseClaw.Core.Paths.DefenseClawPaths(dataDirectory: _dir));

        Assert.Equal("a ", runner.ScrubCut("a (!", new[] { new SecretValue("(!tail-end") }));
    }

    // --- every YamlDotNet entry point refuses a too-deep text (CUST-341) ----------------------------------------------------------------------

    private static string TooDeep() => "a: " + new string('[', 5000) + new string(']', 5000) + "\n";

    [Fact]
    public void Every_core_yaml_reader_refuses_a_too_deep_text_quickly_like_an_unparseable_one()
    {
        var yaml = TooDeep();
        var section = "asset_policy:\n  skill:\n    registry_required: " + new string('[', 5000) + new string(']', 5000) + "\n";
        var clock = Stopwatch.StartNew();

        Assert.Same(ConfigYamlReader.Empty, ConfigYamlReader.Parse(yaml));
        Assert.Same(OverviewConfigFacts.Empty, OverviewConfigFacts.FromYaml(yaml));
        Assert.Same(DefenseClaw.Core.Observability.ObservabilityConfigFacts.Empty, DefenseClaw.Core.Observability.ObservabilityConfigFacts.FromYaml(yaml));
        Assert.Same(RegistryAttribution.Empty, RegistryAttribution.FromAssetPolicy(section));

        // ConfigStore.Parse refuses such a text itself, so the document is built around the sections directly.
        var document = new ConfigDocument(
            "config.yaml",
            section,
            new DefenseClawConfig(),
            new Dictionary<string, string> { ["asset_policy"] = section, ["llm"] = "llm:\n  provider: " + new string('[', 5000) + new string(']', 5000) + "\n" },
            DateTimeOffset.UtcNow);
        Assert.Equal(ReadinessConfig.Empty, ReadinessConfig.From(document));
        Assert.True(clock.Elapsed < TestSupport.TestTimeouts.Ceiling, "refusals must not run the parser");
    }

    [Fact]
    public async Task A_crlf_split_across_reads_is_one_terminator()
    {
        var text = new string('a', 8191) + "\r\n" + "b";

        var lines = await ReadAll(text, 100_000);

        Assert.Equal(new[] { new string('a', 8191), "b" }, lines);
    }
}
