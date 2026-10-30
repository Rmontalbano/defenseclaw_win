using System.Text.Json;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Gateway;

namespace DefenseClaw.Tests;

/// <summary>
/// CUST-401: <c>defenseclaw acp status --json</c> as 1.0.5 prints it (synthetic fixtures built from <c>_status</c>, cmd_acp.py:1814): a binding is healthy only
/// when the CLI says so and names no problem, the entries that start the guard with the lock arrive as the CLI's own words, a command a runtime does not have
/// is "not supported" and never a fault, and a pair name that is not a stable id never reaches a command line.
/// </summary>
public sealed class AcpGuardStatusTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "runtime-1.0.5", "acp", name));

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void A_healthy_status_lists_every_pair_with_its_profile_and_mode_with_LF_or_CRLF(string newline)
    {
        var read = AcpGuardStatusReader.Parse(Fixture("acp-status.healthy.synthetic.json").Replace("\r\n", "\n").Replace("\n", newline));

        Assert.True(read.IsOk);
        var status = read.Value!;
        Assert.True(status.Enabled);
        Assert.False(status.NeedsAttention);
        Assert.Equal(new[] { "jetbrains/copilot", "zed/kiro" }, status.Bindings.Select(b => b.Pair));

        var zed = status.Bindings.Single(b => b.Client == "zed");
        Assert.Equal("kiro", zed.Agent);
        Assert.True(zed.Healthy);
        Assert.Empty(zed.Problems);
        Assert.Equal("default", zed.Profile);
        Assert.Equal("observe", zed.Mode);
        Assert.True(zed.ProfileInherited);

        var jetbrains = status.Bindings.Single(b => b.Client == "jetbrains");
        Assert.Equal("action", jetbrains.Mode);
        Assert.False(jetbrains.ProfileInherited);
    }

    [Fact]
    public void A_pair_whose_entries_changed_is_not_healthy_and_carries_the_CLIs_findings_whole()
    {
        var read = AcpGuardStatusReader.Parse(Fixture("acp-status.attention.synthetic.json"));

        var status = read.Value!;
        Assert.True(status.NeedsAttention);
        var kiro = status.Bindings.Single(b => b.Agent == "kiro");
        Assert.False(kiro.Healthy);
        Assert.Equal(2, kiro.Problems.Count);
        Assert.Contains("'kiro-2' starts the guard with this pair's contract lock", kiro.Problems[0], StringComparison.Ordinal);
        Assert.Contains("which pins only the entries setup wrote", kiro.Problems[0], StringComparison.Ordinal);
        Assert.True(status.Bindings.Single(b => b.Agent == "copilot").Healthy);
    }

    [Fact]
    public void A_healthy_flag_beside_a_problem_is_not_a_healthy_pair()
    {
        var read = AcpGuardStatusReader.Parse("""{"enabled":true,"bindings":{"zed/kiro":{"healthy":true,"problems":["editor entry 'x' is not an object"],"profile":"default","mode":"observe","profile_source":"binding"}}}""");

        Assert.False(read.Value!.Bindings.Single().Healthy);
    }

    [Fact]
    public void A_pair_name_that_is_not_a_stable_id_is_dropped_and_a_guard_with_no_pairs_is_an_empty_list()
    {
        var read = AcpGuardStatusReader.Parse("""{"enabled":false,"bindings":{"zed/ki ro":{"healthy":true},"../zed/kiro":{"healthy":true},"nopair":{"healthy":true},"zed/kiro":{"healthy":true,"problems":[],"profile":"p","mode":"weird"}}}""");

        var only = Assert.Single(read.Value!.Bindings);
        Assert.Equal("zed/kiro", only.Pair);
        Assert.Equal(string.Empty, only.Mode);
        Assert.False(read.Value.Enabled);

        Assert.Empty(AcpGuardStatusReader.Parse("""{"enabled":true,"bindings":{}}""").Value!.Bindings);
        Assert.Empty(AcpGuardStatusReader.Parse("""{"enabled":true}""").Value!.Bindings);
    }

    [Fact]
    public void A_warning_line_before_the_document_does_not_hide_it()
    {
        var read = AcpGuardStatusReader.FromRun(0, "warning: config was migrated\n" + Fixture("acp-status.healthy.synthetic.json"), string.Empty);

        Assert.True(read.IsOk);
        Assert.Equal(2, read.Value!.Bindings.Count);
    }

    [Fact]
    public void A_runtime_without_the_command_is_unsupported_and_any_other_failure_is_unavailable_with_a_sentence()
    {
        Assert.Equal(AcpReadStatus.Unsupported, AcpGuardStatusReader.FromRun(2, string.Empty, "Error: No such command 'status'.").Status);

        var failed = AcpGuardStatusReader.FromRun(1, string.Empty, "Error: configuration is unavailable\n");
        Assert.Equal(AcpReadStatus.Unavailable, failed.Status);
        Assert.Contains("configuration is unavailable", failed.Message, StringComparison.Ordinal);

        Assert.Equal(AcpReadStatus.Unavailable, AcpGuardStatusReader.FromRun(0, "not json", string.Empty).Status);
        Assert.Equal(AcpReadStatus.Unavailable, AcpGuardStatusReader.FromRun(0, "{\"bindings\":{}}", string.Empty).Status);
        Assert.Equal(AcpReadStatus.Unavailable, AcpGuardStatusReader.FromRun(0, "{broken", string.Empty).Status);
    }

    [Fact]
    public void The_command_is_a_read_so_the_app_runs_it_without_a_review()
    {
        Assert.Equal(new[] { "acp", "status", "--json" }, AcpGuardStatusReader.Argv);
        Assert.Equal(CommandTier.ReadOnly, CommandTiers.Classify(AcpGuardStatusReader.Argv));
    }

    [Fact]
    public void The_parser_reads_a_document_element_too()
    {
        using var document = JsonDocument.Parse(Fixture("acp-status.attention.synthetic.json"));

        Assert.NotNull(AcpGuardStatusReader.Parse(document.RootElement));
        using var array = JsonDocument.Parse("[]");
        Assert.Null(AcpGuardStatusReader.Parse(array.RootElement));
    }
}
