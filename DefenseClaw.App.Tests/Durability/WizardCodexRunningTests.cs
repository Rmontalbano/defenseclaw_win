using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels.Wizards;

namespace DefenseClaw.App.Tests.Durability;

/// <summary>The setup wizard's review page carries the "Codex is running" bar for <c>setup codex</c> and no other wizard does (CUST-352).</summary>
public sealed class WizardCodexRunningTests
{
    private static void ToReview(WizardViewModel vm)
    {
        while (!vm.IsReview)
        {
            vm.Next();
            Assert.False(vm.HasValidationSummary, vm.ValidationSummary);
        }
    }

    /// <summary>The claude-code sample's help under another connector's name: the wizard's command is then <c>setup &lt;target&gt; ...</c>.</summary>
    private static WizardDefinition Definition(string target)
    {
        var help = SetupHelpParser.Parse(WizardSamples.ClaudeCodeHelp);
        var (steps, curated) = WizardStepFactory.Build(target, help);
        return new WizardDefinition
        {
            Target = target,
            Title = target,
            Group = WizardGroups.Connectors,
            Description = help.Summary,
            Steps = steps,
            PlatformStatus = help.PlatformStatus,
            PlatformNote = help.PlatformNote,
            IsCurated = curated,
            IsDetailLoaded = true,
        };
    }

    [Theory]
    [InlineData("codex", true, true)]
    [InlineData("codex", false, false)]
    [InlineData("claude-code", true, false)]
    public void The_review_of_setup_codex_warns_while_codex_runs_and_only_then(string target, bool codexRunning, bool warned)
    {
        using var temp = new TempDirectory();
        StaThread.Run(() =>
        {
            using var services = TestServices.Create(temp);
            services.AgentProcesses = new AgentProcessProbe(() => codexRunning ? new[] { "Codex" } : Array.Empty<string>());
            using var vm = new WizardViewModel(services, Definition(target));
            ToReview(vm);

            Assert.Contains($"setup {target}", vm.CommandReview!.CommandText, StringComparison.Ordinal);
            Assert.Equal(warned, vm.CommandReview.Warnings.Any(w => w.Title == "Codex is running" && w.Message.EndsWith("Quit Codex first.", StringComparison.Ordinal)));
        });
    }

    private const string DesktopApp = @"C:\Program Files\WindowsApps\OpenAI.Codex_26.1021.3705.0_x64__2p2nqsd0c76g0\app";

    [Theory]
    [InlineData("sandbox-service", false)]
    [InlineData("sandbox-service-and-setup", false)]
    [InlineData("codex-exe", true)]
    [InlineData("desktop-main-process", true)]
    public void The_review_of_setup_codex_ignores_the_codex_desktop_apps_always_on_sandbox_service(string running, bool warned)
    {
        // CUST-358: the Codex desktop app's LocalSystem service runs with Codex closed. Injected processes only; the real machine is never listed.
        var processes = running switch
        {
            "sandbox-service" => new[] { new RunningProcess("explorer"), new RunningProcess("codex-windows-sandbox-service", DesktopApp + @"\resources\codex-windows-sandbox-service.exe") },
            "sandbox-service-and-setup" => new[]
            {
                new RunningProcess("codex-windows-sandbox-service", DesktopApp + @"\resources\codex-windows-sandbox-service.exe"),
                new RunningProcess("codex-windows-sandbox-setup", DesktopApp + @"\resources\codex-windows-sandbox-setup.exe"),
            },
            "codex-exe" => new[] { new RunningProcess("codex-windows-sandbox-service"), new RunningProcess("codex", DesktopApp + @"\resources\codex.exe") },
            _ => new[] { new RunningProcess("codex-windows-sandbox-service"), new RunningProcess("ChatGPT", DesktopApp + @"\ChatGPT.exe") },
        };

        using var temp = new TempDirectory();
        StaThread.Run(() =>
        {
            using var services = TestServices.Create(temp);
            services.AgentProcesses = AgentProcessProbe.FromProcesses(() => processes);
            using var vm = new WizardViewModel(services, Definition("codex"));
            ToReview(vm);

            Assert.Equal(warned, vm.CommandReview!.Warnings.Any(w => w.Title == "Codex is running"));
        });
    }
}
