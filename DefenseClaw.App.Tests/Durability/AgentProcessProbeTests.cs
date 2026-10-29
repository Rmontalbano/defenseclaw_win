using DefenseClaw.App.Services;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.Tests.Durability;

/// <summary>The one process look behind the upgrade checklist and the "Codex is running" warning (CUST-352), over injected process names.</summary>
public sealed class AgentProcessProbeTests
{
    // The Codex desktop app's package, as installed (a synthetic version): the app's main process, its bundled codex.exe app-server, and the helpers.
    private const string DesktopApp = @"C:\Program Files\WindowsApps\OpenAI.Codex_26.1021.3705.0_x64__2p2nqsd0c76g0\app";

    [Theory]
    [InlineData("codex", true)]
    [InlineData("Codex", true)]
    [InlineData("CODEX", true)]
    [InlineData("codex-windows-sandbox-service", false)]
    [InlineData("Codex-Windows-Sandbox-Service", false)]
    [InlineData("codex-windows-sandbox-setup", false)]
    [InlineData("codex-command-runner", false)]
    [InlineData("codex-code-mode-host", false)]
    [InlineData("codex-computer-use", false)]
    [InlineData("codex-anything-else", false)]
    [InlineData("claude", false)]
    [InlineData("notcodex", false)]
    [InlineData("ChatGPT", false)]
    [InlineData("", false)]
    public void A_codex_process_is_codex_exe_and_no_other_name_that_starts_with_codex(string name, bool codex)
    {
        Assert.Equal(codex, AgentProcessProbe.IsCodexName(name));
        Assert.Equal(codex, new AgentProcessProbe(() => new[] { "explorer", name }).CodexRunning());
    }

    [Fact]
    public void The_always_on_sandbox_service_alone_is_not_codex_running_but_with_codex_it_is()
    {
        var service = new AgentProcessProbe(() => new[] { "explorer", "codex-windows-sandbox-service" });
        Assert.False(service.CodexRunning());
        Assert.Null(service.CodexWarning());

        var both = new AgentProcessProbe(() => new[] { "codex-windows-sandbox-service", "codex" });
        Assert.True(both.CodexRunning());
        Assert.NotNull(both.CodexWarning());
    }

    [Fact]
    public void Every_helper_the_codex_desktop_app_leaves_running_is_not_codex_running()
    {
        // The state CUST-358 reports: Codex closed, the app's LocalSystem service (and whatever else it keeps) still up.
        var idle = AgentProcessProbe.FromProcesses(() => new[]
        {
            new RunningProcess("explorer"),
            new RunningProcess("codex-windows-sandbox-service", DesktopApp + @"\resources\codex-windows-sandbox-service.exe"),
            new RunningProcess("codex-windows-sandbox-setup", DesktopApp + @"\resources\codex-windows-sandbox-setup.exe"),
            new RunningProcess("codex-command-runner", DesktopApp + @"\resources\codex-command-runner.exe"),
        });

        Assert.False(idle.CodexRunning());
        Assert.Null(idle.CodexWarning());
    }

    [Fact]
    public void The_codex_cli_counts_wherever_it_runs_from_and_so_does_the_desktop_apps_launcher()
    {
        foreach (var (name, path) in new[]
                 {
                     ("codex", @"C:\Users\operator\AppData\Local\OpenAI\Codex\bin\0123456789abcdef\codex.exe"),
                     ("codex", DesktopApp + @"\resources\codex.exe"),
                     ("Codex", DesktopApp + @"\Codex.exe"),
                     ("codex", (string?)null),
                 })
        {
            var probe = AgentProcessProbe.FromProcesses(() => new[]
            {
                new RunningProcess("codex-windows-sandbox-service"),
                new RunningProcess(name, path),
            });

            Assert.True(probe.CodexRunning(), name + " " + path);
        }
    }

    [Theory]
    [InlineData("ChatGPT", @"C:\Program Files\WindowsApps\OpenAI.Codex_26.1021.3705.0_x64__2p2nqsd0c76g0\app\ChatGPT.exe", true)]
    [InlineData("chatgpt", @"c:\program files\windowsapps\openai.codex_26.1021.3705.0_x64__2p2nqsd0c76g0\app\chatgpt.exe", true)]
    [InlineData("ChatGPT", @"C:\Program Files\WindowsApps\OpenAI.CodexBeta_26.1.1.0_x64__2p2nqsd0c76g0\app\ChatGPT.exe", true)]
    [InlineData("ChatGPT", @"C:\Program Files\WindowsApps\OpenAI.ChatGPT-Desktop_1.2025.1.0_x64__2p2nqsd0c76g0\app\ChatGPT.exe", false)]
    [InlineData("ChatGPT", @"C:\Program Files\WindowsApps\OpenAI.CodexExtras_1.0.0.0_x64__2p2nqsd0c76g0\app\ChatGPT.exe", false)]
    [InlineData("ChatGPT", @"C:\Users\operator\Downloads\OpenAI.Codex_1.0\ChatGPT.exe", false)]
    [InlineData("ChatGPT", @"C:\Program Files\ChatGPT\ChatGPT.exe", false)]
    [InlineData("ChatGPT", @"C:\Program Files\WindowsApps", false)]
    [InlineData("ChatGPT", null, false)]
    [InlineData("ChatGPT", "", false)]
    [InlineData("chrome_proxy", @"C:\Program Files\WindowsApps\OpenAI.Codex_26.1021.3705.0_x64__2p2nqsd0c76g0\app\chrome_proxy.exe", false)]
    [InlineData("explorer", @"C:\Windows\explorer.exe", false)]
    public void The_codex_desktop_apps_main_process_is_chatgpt_exe_from_the_codex_package_and_only_that(string name, string? path, bool codex)
    {
        Assert.Equal(codex, AgentProcessProbe.IsCodexDesktopMain(name, path));
        Assert.Equal(codex, AgentProcessProbe.IsCodexProcess(new RunningProcess(name, path)));
        Assert.Equal(codex, AgentProcessProbe.FromProcesses(() => new[] { new RunningProcess(name, path) }).CodexRunning());
        Assert.Equal(codex, AgentProcessProbe.FromProcesses(() => new[] { new RunningProcess(name, path) }).CodexWarning() is not null);
    }

    [Fact]
    public void A_names_only_probe_has_no_paths_so_a_chatgpt_process_is_not_codex()
    {
        Assert.False(new AgentProcessProbe(() => new[] { "ChatGPT" }).CodexRunning());
        Assert.All(new AgentProcessProbe(() => new[] { "ChatGPT" }).Processes(), p => Assert.Null(p.ImagePath));
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
