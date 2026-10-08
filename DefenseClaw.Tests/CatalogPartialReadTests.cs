using DefenseClaw.Core.Cli;

namespace DefenseClaw.Tests;

/// <summary>
/// What counts as a PARTIAL catalog read (rows shown with a warning, changes off) and what stays a failure. Every fixture is
/// synthetic: invocations are built in memory, nothing is run, and no path or name comes from a real install.
/// </summary>
public sealed class CatalogPartialReadTests
{
    private const string Rows = """[{"name": "srv-a", "transport": "stdio", "verdict": "clean"}]""";

    private const string McpDiagnostic = "error: MCP discovery source is unreadable for connector='claudecode': C:\\fixture-home\\.claude\\settings.json";

    private static CliInvocation Run(int? exit, string? failure = null, params (CliStream Stream, string Text)[] lines)
    {
        var invocation = new CliInvocation("defenseclaw", new[] { "mcp", "list", "--json" }, DateTimeOffset.UtcNow, retainFullOutput: true);
        foreach (var (stream, text) in lines)
        {
            invocation.Append(new CliOutputLine(DateTimeOffset.UtcNow, stream, text));
        }

        invocation.ExitCode = exit;
        invocation.FailureReason = failure;
        invocation.FinishedAt = DateTimeOffset.UtcNow;
        return invocation;
    }

    private static (CliStream, string) Out(string text) => (CliStream.StandardOutput, text);

    private static (CliStream, string) Err(string text) => (CliStream.StandardError, text);

    [Fact]
    public void Valid_json_with_exit_1_and_a_known_mcp_source_diagnostic_is_partial_and_keeps_the_streams_apart()
    {
        var invocation = Run(1, null, Out(Rows), Err(McpDiagnostic));

        Assert.True(CatalogPartialReads.TryClassify(invocation, out var partial));

        Assert.NotNull(partial);
        Assert.Equal(Rows + "\n", partial!.Stdout);
        Assert.Equal(new[] { McpDiagnostic }, partial.Diagnostics);
        Assert.DoesNotContain("error:", partial.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void A_malformed_source_and_a_multi_connector_object_list_are_partial_too()
    {
        var groups = """[{"connector": "claudecode", "mcp_servers": []}, {"connector": "codex", "mcp_servers": [{"name": "x"}]}]""";
        var invocation = Run(
            1,
            null,
            Out(groups),
            Err("error: MCP discovery source is malformed for connector='claudecode': /fixture/.claude.json"),
            Err("error: MCP discovery source is unreadable for connector='codex': /fixture/.codex/config.toml"));

        Assert.True(CatalogPartialReads.TryClassify(invocation, out var partial));
        Assert.Equal(2, partial!.Diagnostics.Count);
    }

    [Fact]
    public void The_enveloped_object_shape_of_an_explicit_connector_is_complete_json_too()
    {
        var invocation = Run(1, null, Out("""{"connector": "claudecode", "mcp_servers": [{"name": "x"}]}"""), Err(McpDiagnostic));

        Assert.True(CatalogPartialReads.TryClassify(invocation, out _));
    }

    [Fact]
    public void Plugin_source_lines_and_the_summary_line_are_known_diagnostics()
    {
        var invocation = Run(
            1,
            null,
            Out("""[{"id": "p1"}]"""),
            Err("Plugin discovery source [claudecode]: /fixture/installed_plugins.json \u2014 unsafe/unreadable; entries=0 (permission denied)"),
            Err("error: plugin discovery could not safely read every existing registry source"));

        Assert.True(CatalogPartialReads.TryClassify(invocation, out var partial));
        Assert.Equal(2, partial!.Diagnostics.Count);
        Assert.StartsWith("Plugin discovery source [claudecode]", partial.Diagnostics[0], StringComparison.Ordinal);
    }

    [Fact]
    public void The_plugin_json_failure_on_stderr_is_described_source_by_source_next_to_the_empty_array()
    {
        var invocation = Run(
            1,
            null,
            Out("[]"),
            Err("{"),
            Err("  \"error\": \"plugin_discovery_failed\","),
            Err("  \"discovery\": [{\"connector\": \"claudecode\", \"source\": \"/fixture/installed_plugins.json\", \"state\": \"malformed\", \"entries\": 0}]"),
            Err("}"));

        Assert.True(CatalogPartialReads.TryClassify(invocation, out var partial));
        Assert.Contains(partial!.Diagnostics, d => d.StartsWith("error: plugin discovery could not safely read", StringComparison.Ordinal));
        Assert.Contains(partial.Diagnostics, d => d.Contains("/fixture/installed_plugins.json", StringComparison.Ordinal) && d.Contains("malformed", StringComparison.Ordinal));
    }

    [Fact]
    public void Garbage_output_with_exit_1_is_a_failure_even_with_a_known_diagnostic()
    {
        var invocation = Run(1, null, Out("Traceback (most recent call last):"), Err(McpDiagnostic));

        Assert.False(CatalogPartialReads.TryClassify(invocation, out var partial));
        Assert.Null(partial);
    }

    [Fact]
    public void Valid_json_with_exit_1_and_an_unknown_stderr_is_a_failure()
    {
        Assert.False(CatalogPartialReads.TryClassify(Run(1, null, Out(Rows), Err("error: something else went wrong")), out _));
        Assert.False(CatalogPartialReads.TryClassify(Run(1, null, Out(Rows)), out _));
    }

    [Fact]
    public void An_exit_of_zero_is_never_partial_and_neither_is_a_run_that_did_not_finish()
    {
        // 0.8.10 swallows an unreadable source: exit 0, nothing on stderr. A diagnostic on a clean exit is not this case either.
        Assert.False(CatalogPartialReads.TryClassify(Run(0, null, Out(Rows)), out _));
        Assert.False(CatalogPartialReads.TryClassify(Run(0, null, Out(Rows), Err(McpDiagnostic)), out _));
        Assert.False(CatalogPartialReads.TryClassify(Run(null, "timed out after 120 s", Out(Rows), Err(McpDiagnostic)), out _));
        Assert.False(CatalogPartialReads.TryClassify(Run(1, "cancelled", Out(Rows), Err(McpDiagnostic)), out _));
    }

    [Theory]
    [InlineData("123")]
    [InlineData("\"text\"")]
    [InlineData("null")]
    [InlineData("")]
    [InlineData("[{\"name\": ")]
    public void Output_that_is_not_one_complete_json_array_or_object_is_a_failure(string stdout)
    {
        Assert.False(CatalogPartialReads.TryClassify(Run(1, null, Out(stdout), Err(McpDiagnostic)), out _));
    }

    [Fact]
    public void A_transcript_that_dropped_its_head_is_a_failure()
    {
        var invocation = new CliInvocation("defenseclaw", new[] { "mcp", "list", "--json" }, DateTimeOffset.UtcNow);
        for (var i = 0; i < CliInvocation.MaxRetainedOutputLines + 10; i++)
        {
            invocation.Append(new CliOutputLine(DateTimeOffset.UtcNow, CliStream.StandardOutput, "{}"));
        }

        invocation.Append(new CliOutputLine(DateTimeOffset.UtcNow, CliStream.StandardError, McpDiagnostic));
        invocation.ExitCode = 1;

        Assert.True(invocation.IsOutputTruncated);
        Assert.False(CatalogPartialReads.TryClassify(invocation, out _));
    }

    [Fact]
    public void Repeated_diagnostics_are_listed_once_and_a_flood_is_capped()
    {
        var lines = new List<(CliStream, string)> { Out(Rows) };
        lines.Add(Err(McpDiagnostic));
        lines.Add(Err(McpDiagnostic));
        for (var i = 0; i < 40; i++)
        {
            lines.Add(Err($"error: MCP discovery source is unreadable for connector='c{i}': /fixture/{i}.json"));
        }

        Assert.True(CatalogPartialReads.TryClassify(Run(1, null, lines.ToArray()), out var partial));
        Assert.Equal(CatalogPartialReads.MaxDiagnostics, partial!.Diagnostics.Count);
        Assert.Equal(partial.Diagnostics.Count, partial.Diagnostics.Distinct().Count());
    }
}
