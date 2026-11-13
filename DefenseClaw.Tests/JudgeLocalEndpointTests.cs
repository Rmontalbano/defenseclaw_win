using System.Text.Json;
using DefenseClaw.Core.Config;
using DefenseClaw.Core.Gateway.Models;

namespace DefenseClaw.Tests;

/// <summary>
/// The judge's local-endpoint test (llm_judge.go <c>localJudgeEndpoint</c>, config.go <c>IsLocalProvider</c>, runtime 1.0.9) and the watcher's admission report
/// (sidecar.go <c>publishWatcherHealth</c>), synthetic and verified from source only.
/// </summary>
public sealed class JudgeLocalEndpointTests
{
    [Theory]
    [InlineData("unix:/run/judge.sock", true)]
    [InlineData("UNIX:/run/judge.sock", true)]
    [InlineData("http://localhost:11434/v1", true)]
    [InlineData("http://LOCALHOST.:11434", true)]
    [InlineData("http://judge.localhost:8000", true)]
    [InlineData("http://localhost.localdomain/v1", true)]
    [InlineData("http://a.localhost.localdomain/v1", true)]
    [InlineData("http://127.0.0.1:8000", true)]
    [InlineData("http://[::1]:8000", true)]
    [InlineData("http://10.1.2.3", true)]
    [InlineData("http://172.16.0.9", true)]
    [InlineData("http://172.31.255.1", true)]
    [InlineData("http://192.168.1.5:8080", true)]
    [InlineData("http://169.254.10.10", true)]
    [InlineData("http://[fd12:3456::1]", true)]
    [InlineData("http://[fe80::1]", true)]
    [InlineData("http://[::ffff:10.0.0.1]", true)]
    [InlineData("http://127.1", false)]
    [InlineData("http://2130706433:8000", false)]
    [InlineData("http://127.000.000.001", false)]
    [InlineData("http://172.32.0.1", false)]
    [InlineData("http://172.15.0.1", false)]
    [InlineData("http://8.8.8.8", false)]
    [InlineData("http://192.169.1.1", false)]
    [InlineData("https://api.openai.com/v1", false)]
    [InlineData("http://notlocalhost:1", false)]
    [InlineData("http://example.localhost.com", false)]
    [InlineData("localhost:11434", false)]
    [InlineData("not a url", false)]
    public void The_runtimes_endpoint_test(string endpoint, bool local) =>
        Assert.Equal(local, JudgeLocalEndpoint.LocalEndpoint(endpoint));

    [Theory]
    [InlineData("llm:\n  provider: Ollama\n", true)]
    [InlineData("llm:\n  provider: lm_studio\n", true)]
    [InlineData("llm:\n  provider: local\n", true)]
    [InlineData("llm:\n  model: vllm/mistral\n", true)]
    [InlineData("llm:\n  provider: openai\n  model: ollama/x\n", false)]
    [InlineData("llm:\n  model: openai/gpt-4o\n", false)]
    [InlineData("llm:\n  model: gpt-4o\n", false)]
    [InlineData("llm:\n  model: anthropic.claude-haiku-4-5-20251001-v1:0\n", false)]
    [InlineData("llm:\n  provider: openai\n  base_url: http://10.0.0.4:8000/v1\n", true)]
    [InlineData("llm:\n  provider: ollama\nguardrail:\n  judge:\n    llm:\n      provider: openai\n      base_url: https://api.openai.com/v1\n", false)]
    [InlineData("llm:\n  provider: openai\nguardrail:\n  judge:\n    model: ollama/qwen\n    llm:\n      provider: ollama\n", true)]
    [InlineData("llm:\n  provider: openai\nguardrail:\n  judge:\n    api_base: http://localhost:1234\n", true)]
    [InlineData("", false)]
    public void The_judge_llm_is_the_judge_block_over_the_top_level(string yaml, bool local) =>
        Assert.Equal(local, JudgeLocalEndpoint.IsLocal(ConfigYamlReader.Parse(yaml)));

    [Fact]
    public void A_custom_instance_without_an_address_cannot_be_told()
    {
        Assert.Null(JudgeLocalEndpoint.IsLocal(ConfigYamlReader.Parse("llm:\n  provider: custom\n  instance_name: team\n")));
        Assert.True(JudgeLocalEndpoint.IsLocal(ConfigYamlReader.Parse("llm:\n  instance_name: team\n  base_url: http://127.0.0.1:1\n")));
    }

    private static ServiceState Watcher(string detailsJson, string? lastError = null) =>
        JsonSerializer.Deserialize<ServiceState>(
            "{\"state\":\"degraded\"" + (lastError is null ? string.Empty : ",\"last_error\":\"" + lastError + "\"") + ",\"details\":" + detailsJson + "}",
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    [Fact]
    public void The_watcher_report_reads_the_count_the_connectors_and_the_folders()
    {
        var report = WatcherAdmissionHealth.From(Watcher(
            "{\"skill_dirs\":2,\"watch_roots_unavailable\":3,\"admission_scanning_off\":[\"codex\",\"ClaudeCode\"],\"admission_root_writable_folders\":[\"C:\\\\Users\\\\operator\\\\shared\"]}",
            "admission scanning is off for one or more connector roots; the gateway log names each root"));

        Assert.NotNull(report);
        Assert.Equal(3, report.RootsUnavailable);
        Assert.Equal(new[] { "claudecode", "codex" }, report.Connectors);
        Assert.Equal("claudecode and codex", report.ConnectorLabel);
        Assert.Equal(new[] { @"C:\Users\operator\shared" }, report.WritableFolders);
        Assert.StartsWith("admission scanning is off", report.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void The_lists_and_paths_are_bounded_and_wrong_types_are_read_as_empty()
    {
        var names = string.Join(",", Enumerable.Range(0, 30).Select(static i => "\"c" + i.ToString("D2", System.Globalization.CultureInfo.InvariantCulture) + "\""));
        var report = WatcherAdmissionHealth.From(Watcher("{\"watch_roots_unavailable\":30,\"admission_scanning_off\":[" + names + "],\"admission_root_writable_folders\":[\"" + new string('p', 1000) + "\"]}"))!;

        Assert.Equal(WatcherAdmissionHealth.MaxNames, report.Connectors.Count);
        Assert.Equal(18, report.ConnectorsOmitted);
        Assert.Equal(WatcherAdmissionHealth.MaxFolderLength + 1, report.WritableFolders[0].Length);

        var odd = WatcherAdmissionHealth.From(Watcher("{\"watch_roots_unavailable\":1,\"admission_scanning_off\":\"codex\",\"admission_root_writable_folders\":{\"a\":1}}"))!;
        Assert.Empty(odd.Connectors);
        Assert.Empty(odd.WritableFolders);
        Assert.Equal("configured connectors", odd.ConnectorLabel);
    }

    [Theory]
    [InlineData("{\"skill_dirs\":2}")]
    [InlineData("{\"watch_roots_unavailable\":0}")]
    [InlineData("{\"watch_roots_unavailable\":-1}")]
    [InlineData("{\"watch_roots_unavailable\":\"2\"}")]
    [InlineData("{\"watch_roots_unavailable\":1.5}")]
    [InlineData("{\"watch_roots_unavailable\":null}")]
    [InlineData("[]")]
    public void Anything_but_a_whole_count_above_zero_is_no_report(string details) =>
        Assert.Null(WatcherAdmissionHealth.From(Watcher(details)));

    [Fact]
    public void No_watcher_is_no_report() => Assert.Null(WatcherAdmissionHealth.From(null));
}
