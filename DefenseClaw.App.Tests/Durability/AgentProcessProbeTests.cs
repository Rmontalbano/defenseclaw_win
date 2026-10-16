using DefenseClaw.App.Services;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.Tests.Durability;

/// <summary>The one process look behind the upgrade checklist and the "Codex is running" warning (CUST-352), over injected process names.</summary>
public sealed class AgentProcessProbeTests
{
    [Theory]
    [InlineData("codex", true)]
    [InlineData("Codex", true)]
    [InlineData("codex-windows-sandbox-setup", true)]
    [InlineData("claude", false)]
    [InlineData("notcodex", false)]
    [InlineData("", false)]
    public void A_codex_process_is_one_whose_name_starts_with_codex(string name, bool codex)
    {
        Assert.Equal(codex, AgentProcessProbe.IsCodexName(name));
        Assert.Equal(codex, new AgentProcessProbe(() => new[] { "explorer", name }).CodexRunning());
    }

    [Theory]
    [InlineData("claude", true)]
    [InlineData("Claude", true)]
    [InlineData("claude-helper", false)]
    [InlineData("codex", false)]
    public void Claude_is_exactly_claude(string name, bool claude)
    {
        Assert.Equal(claude, AgentProcessProbe.IsClaudeName(name));
        Assert.Equal(claude, new AgentProcessProbe(() => new[] { name }).ClaudeRunning());
    }

    [Fact]
    public void The_warning_is_the_required_sentence_while_codex_runs_and_nothing_otherwise()
    {
        var warning = new AgentProcessProbe(() => new[] { "codex" }).CodexWarning();

        Assert.NotNull(warning);
        Assert.Equal("Codex is running", warning!.Title);
        Assert.Equal(
            "Codex is running. DefenseClaw rewrites Codex's config.toml when the gateway starts, and Codex rewrites it too; a collision can leave a stuck pending write " +
            "that stops the gateway. Quit Codex first.",
            warning.Message);
        Assert.Null(new AgentProcessProbe(() => new[] { "explorer" }).CodexWarning());
        Assert.Null(AgentProcessProbe.None.CodexWarning());
    }

    [Fact]
    public void A_machine_that_will_not_list_its_processes_is_none_not_a_fault()
    {
        var probe = new AgentProcessProbe(() => throw new System.ComponentModel.Win32Exception("access denied"));

        Assert.Empty(probe.Names());
        Assert.False(probe.CodexRunning());
    }

    [Fact]
    public void An_answer_is_reused_for_the_cache_time_when_there_is_one_and_never_when_there_is_not()
    {
        var calls = 0;
        var cached = new AgentProcessProbe(() => { calls++; return new[] { "codex" }; }, TimeSpan.FromMinutes(5));
        _ = cached.CodexRunning();
        _ = cached.CodexRunning();
        _ = cached.CodexRunning();
        Assert.Equal(1, calls);

        calls = 0;
        var live = new AgentProcessProbe(() => { calls++; return new[] { "codex" }; });
        _ = live.CodexRunning();
        _ = live.CodexRunning();
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData("defenseclaw", "setup codex --yes --mode observe", true)]
    [InlineData("defenseclaw", "setup codex", true)]
    [InlineData("defenseclaw", "guardrail fail-mode open --yes", true)]
    [InlineData("defenseclaw", "setup claude-code --yes", false)]
    [InlineData("defenseclaw", "setup observability list", false)]
    [InlineData("defenseclaw", "guardrail status", false)]
    [InlineData("defenseclaw", "doctor", false)]
    [InlineData("defenseclaw-gateway", "start", true)]
    [InlineData("defenseclaw-gateway", "restart", true)]
    [InlineData("defenseclaw-gateway", "stop", true)]
    [InlineData("defenseclaw-gateway", "status", false)]
    [InlineData("C:\\tools\\other.exe", "setup codex", false)]
    public void Only_the_commands_that_rewrite_codex_s_config_carry_the_warning(string executable, string argv, bool touches)
    {
        Assert.Equal(touches, AgentProcessProbe.TouchesCodexConfig(executable, argv.Split(' ')));
    }
}
