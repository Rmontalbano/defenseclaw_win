using System.Windows.Controls;
using DefenseClaw.App.Services.Updates;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.Views.Updates;
using DefenseClaw.Core.Gateway.Durability;

namespace DefenseClaw.App.Tests.Updates;

/// <summary>CUST-424 through the view model and the card: install.ps1 1.0.6's OpenCode refusals and pre-flight rows, from synthetic transcripts.</summary>
public sealed partial class InstallScriptUpgradeViewTests
{
    private const string V1CliLine =
        @"  X OpenCode 1.18.34 at C:\Users\operator\AppData\Roaming\npm\opencode.cmd is OpenCode V1, and DefenseClaw 1.0.6 guards OpenCode V2 only; nothing was changed, and the DefenseClaw you have keeps " +
        "guarding it. Install OpenCode V2 (npm uninstall -g opencode-ai && npm install -g @opencode/cli@2), then upgrade again. To upgrade without OpenCode instead, run 'defenseclaw setup remove opencode' first.";

    private const string V2CliLine =
        "  X DefenseClaw 1.0.5 guards OpenCode V1 only and the OpenCode here is V2 (opencode v2.2.3): its gateway would not start, and OpenCode would run unguarded; nothing was changed. " +
        "The rollback restores that release's own configuration, so removing OpenCode here does not help: to roll back, put OpenCode V1 back first (npm uninstall -g @opencode/cli && npm install -g opencode-ai@1.18), " +
        "then run 'defenseclaw agent discover --refresh' so DefenseClaw sees it, or stay on this release.";

    private const string CloseAgentsLine =
        "  x Close OpenCode and Amp completely before installing or upgrading DefenseClaw (OpenCode V2 keeps a background server after its window closes: stop it with 'opencode service stop'), " +
        "then run this installer again; gateway still running: opencode (PID 4242)";

    private const string MigrateRefusedLine = "  x Your configuration cannot be migrated to 1.0.6; nothing was changed (see above)";

    /// <summary>The window's check for the 1.0.6 upgrade, up to its pre-flight rows.</summary>
    private static void ApplyTo106(Rig rig)
    {
        UiThread.Run(() => rig.Vm.ApplyCheck(Check("1.0.6", "1.0.5", withSetupExe: false), UpgradeChannel.SetupInstaller));
        UiThread.WaitFor(() => rig.Vm.PreflightRows.Any(r => r.Id == InstallScriptPreflight.PluginAgentsId) && !rig.Vm.IsCheckingPreflight, "the pre-flight rows");
    }

    private static async Task RunRefusedUpgradeAsync(Rig rig)
    {
        ApplyTo106(rig);
        await StageAsync(rig);
        rig.Launcher.Exit = 1;
        UiThread.Run(() => rig.Vm.OpenUpgradeReviewCommand.Execute(null));
        await UiThread.Run(() => rig.Vm.ConfirmReviewAsync());
        UiThread.Run(UiThread.Settle);
        UiThread.WaitFor(() => rig.Vm.HasPostFlight && !rig.Vm.IsReadingPostFlight, "the post-run read");
    }

    private static async Task RunRefusedRollbackAsync(Rig rig)
    {
        await StageAsync(rig);
        rig.Launcher.Exit = 1;
        UiThread.Run(() => rig.Vm.OpenRollbackReviewCommand.Execute(null));
        await UiThread.Run(() => rig.Vm.ConfirmReviewAsync());
        UiThread.Run(UiThread.Settle);
        UiThread.WaitFor(() => rig.Vm.HasPostFlight && !rig.Vm.IsReadingPostFlight, "the post-run read");
    }

    /// <summary>On 1.0.6 with 1.0.5 kept, and the window's check on it.</summary>
    private void On106With105Kept(Rig rig)
    {
        KeepPrevious("1.0.5");
        UiThread.Run(() => rig.Vm.ApplyCheck(Check("1.0.6", "1.0.6", withSetupExe: false, update: false), UpgradeChannel.SetupInstaller));
        UiThread.WaitFor(() => rig.PostFlight.Reads >= 1 && !rig.Vm.IsReadingPostFlight, "the install state read");
    }

    private void GuardOpenCodeV2OnRecord()
    {
        File.WriteAllText(Path.Combine(_temp.Path, "config.yaml"), "guardrail:\n  connectors:\n    opencode:\n      mode: observe\n");
        var binary = Path.Combine(Directory.CreateDirectory(Path.Combine(_temp.Path, "opencode-bin")).FullName, "opencode.exe");
        File.WriteAllText(binary, "synthetic");
        File.WriteAllText(Path.Combine(_temp.Path, "agent_discovery.json"), System.Text.Json.JsonSerializer.Serialize(new
        {
            agents = new { opencode = new { installed = true, version = "2.2.4", binary_path = binary } },
        }));
    }

    [Fact]
    public async Task An_upgrade_refused_for_opencode_v1_says_refused_names_the_remedy_and_offers_no_rollback()
    {
        var rig = Build();
        WriteLogOnStart(rig, "-Version 1.0.6", "  > Installed: 1.0.5", V1CliLine, MigrateRefusedLine);

        await RunRefusedUpgradeAsync(rig);

        UiThread.Run(() =>
        {
            Assert.Equal("Refused (exit 1)", rig.Vm.ExitLabel);
            Assert.Equal("Warn", rig.Vm.ExitToneKey);
            Assert.Equal("Refused; nothing changed", rig.Vm.VerdictLabel);
            Assert.StartsWith(@"The installer said: OpenCode 1.18.34 at ~\AppData\Roaming\npm\opencode.cmd is OpenCode V1", rig.Vm.ExitMessage, StringComparison.Ordinal);
            Assert.EndsWith("The upgrade was refused before it changed anything (exit 1). Nothing was changed.", rig.Vm.ExitMessage, StringComparison.Ordinal);
            Assert.Equal(
                "Nothing was changed. The installer's next step: Install OpenCode V2 (npm uninstall -g opencode-ai && npm install -g @opencode/cli@2), then upgrade again. " +
                "To upgrade without OpenCode instead, run 'defenseclaw setup remove opencode' first.",
                rig.Vm.ExitHint);
            Assert.False(rig.Vm.CanRollback);
            Assert.Equal("Nothing was changed, so there is nothing to roll back.", rig.Vm.RollbackNote);
            foreach (var text in new[] { rig.Vm.ExitMessage, rig.Vm.ExitHint, rig.Vm.VerdictSummary, rig.Vm.RollbackNote, rig.Vm.LastInstallText })
            {
                Assert.DoesNotContain("did not finish", text, StringComparison.Ordinal);
                Assert.DoesNotContain("restores", text, StringComparison.Ordinal);
                Assert.DoesNotContain(@"C:\Users\operator", text, StringComparison.OrdinalIgnoreCase);
            }
        });
    }

    [Fact]
    public async Task A_rollback_refused_for_opencode_v2_explains_the_release_limit_and_can_be_run_again()
    {
        var rig = Build();
        GuardOpenCodeV2OnRecord();
        On106With105Kept(rig);
        WriteLogOnStart(rig, "-Rollback", "--- Rolling back to DefenseClaw 1.0.5  [12:00:01Z]", V2CliLine, "  x Rollback cancelled; nothing was changed (see above)");

        await RunRefusedRollbackAsync(rig);

        UiThread.Run(() =>
        {
            Assert.Equal("Rollback refused; nothing changed", rig.Vm.VerdictLabel);
            Assert.StartsWith("Nothing was changed. Releases before 1.0.6 cannot guard OpenCode V2. The installer's next step: The rollback restores that release's own configuration", rig.Vm.ExitHint, StringComparison.Ordinal);
            Assert.Contains("npm install -g opencode-ai@1.18", rig.Vm.ExitHint, StringComparison.Ordinal);
            Assert.True(rig.Vm.CanRollback);
            Assert.Contains("OpenCode V2 (2.2.4) is set up here", rig.Vm.RollbackNote, StringComparison.Ordinal);
            Assert.DoesNotContain("The rollback has run", rig.Vm.RollbackNote, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task The_stop_for_a_running_opencode_says_refused_not_restored()
    {
        var rig = Build();
        WriteLogOnStart(rig, "-Version 1.0.6", "  > Installed: 1.0.5", CloseAgentsLine);

        await RunRefusedUpgradeAsync(rig);

        UiThread.Run(() =>
        {
            Assert.Equal("Refused; nothing changed", rig.Vm.VerdictLabel);
            Assert.StartsWith("Nothing was changed. The installer's next step: Close OpenCode and Amp completely (OpenCode V2 keeps a background server", rig.Vm.ExitHint, StringComparison.Ordinal);
            Assert.DoesNotContain("restores", rig.Vm.ExitMessage, StringComparison.Ordinal);
            Assert.False(rig.Vm.CanRollback);
        });
    }

    [Fact]
    public void The_rollback_note_says_up_front_that_a_guarded_v2_is_refused_only_on_an_installer_that_checks()
    {
        GuardOpenCodeV2OnRecord();

        var checking = Build();
        On106With105Kept(checking);
        UiThread.Run(() =>
        {
            Assert.StartsWith("The installer will swap back to the previous install. OpenCode V2 (2.2.4) is set up here", checking.Vm.RollbackNote, StringComparison.Ordinal);
            Assert.Contains("DefenseClaw 1.0.5 guards OpenCode V1 only", checking.Vm.RollbackNote, StringComparison.Ordinal);
        });

        var older = Build();
        On105With104Kept(older);
        UiThread.Run(() => Assert.Equal("The installer will swap back to the previous install.", older.Vm.RollbackNote));
    }

    [Theory]
    [MemberData(nameof(Renders))]
    public async Task The_opencode_rows_and_refused_cards_render_at_both_review_sizes(string size, string mode)
    {
        var parts = size.Split('x');
        var (width, height) = (int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture), int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
        using var fixture = new DefenseClaw.App.Tests.Appearance.AppearanceFixture(
            new DefenseClaw.App.Services.Appearance.AppearanceSettings(
                DefenseClaw.App.Services.Appearance.AppearanceStyle.Default, Enum.Parse<DefenseClaw.App.Services.Appearance.AppearanceMode>(mode)));
        var tag = $"{size}-{mode.ToLowerInvariant()}";

        // 1. The pre-flight of a 1.0.6 upgrade with OpenCode and Amp running and a guarded OpenCode V1.
        var first = Build();
        first.Probes.Processes.AddRange(["opencode", "amp"]);
        first.Probes.OpenCodeFacts = new OpenCodeInstallFacts(true, null, new OpenCodeInstall("1.18.34", @"C:\synthetic\opencode.exe"));
        WriteLogOnStart(first, "-Version 1.0.6", "  > Installed: 1.0.5", V1CliLine, MigrateRefusedLine);
        ApplyTo106(first);

        UiThread.Run(() =>
        {
            using var host = Page(new InstallScriptUpgradeCard { DataContext = first.Vm }, width, height, out var scroll);
            ScrollTo(host, scroll, e => e is TextBlock { Text: "OpenCode and Amp" });
            RenderTo.Png(host, $"cust424-preflight-{tag}");
            var texts = VisibleTexts(host);
            Assert.Contains("OpenCode version", texts);
            Assert.Contains(texts, t => t.Contains("opencode service stop", StringComparison.Ordinal));
            Assert.Contains(texts, t => t.Contains("@opencode/cli@2", StringComparison.Ordinal));
            Assert.True(scroll.ExtentWidth <= scroll.ViewportWidth + 0.5, $"{size} {mode}: the card overflows sideways");
        });

        // 2. That upgrade, refused for the OpenCode V1.
        await RunRefusedUpgradeAsync(first);
        RenderCard(first, width, height, $"cust424-refused-v1-{tag}", "Refused; nothing changed", "@opencode/cli@2");

        // 3. The stop for a running OpenCode.
        DeleteLogs();
        var stopped = Build();
        WriteLogOnStart(stopped, "-Version 1.0.6", "  > Installed: 1.0.5", CloseAgentsLine);
        await RunRefusedUpgradeAsync(stopped);
        RenderCard(stopped, width, height, $"cust424-refused-agents-{tag}", "Refused; nothing changed", "opencode service stop");

        // 4. The rollback refused for OpenCode V2, with its up-front note.
        DeleteLogs();
        GuardOpenCodeV2OnRecord();
        var rollback = Build();
        On106With105Kept(rollback);
        WriteLogOnStart(rollback, "-Rollback", "--- Rolling back to DefenseClaw 1.0.5  [12:00:01Z]", V2CliLine, "  x Rollback cancelled; nothing was changed (see above)");
        await RunRefusedRollbackAsync(rollback);
        RenderCard(rollback, width, height, $"cust424-refused-v2-{tag}", "Rollback refused; nothing changed", "opencode-ai@1.18");
    }

    private void DeleteLogs()
    {
        foreach (var file in Directory.EnumerateFiles(Path.Combine(_temp.Path, "logs")))
        {
            File.Delete(file);
        }
    }

    private static void RenderCard(Rig rig, int width, int height, string name, string label, string step) =>
        UiThread.Run(() =>
        {
            using var host = Page(new InstallScriptUpgradeCard { DataContext = rig.Vm }, width, height, out var scroll);
            ScrollTo(host, scroll, e => e is TextBlock { Text: "Install state (read-only)" });
            RenderTo.Png(host, name);
            var texts = VisibleTexts(host);
            Assert.Contains("Refused (exit 1)", texts);
            Assert.Contains(label, texts);
            Assert.Contains(texts, t => t.Contains(step, StringComparison.Ordinal));
            Assert.True(scroll.ExtentWidth <= scroll.ViewportWidth + 0.5, $"{name}: the card overflows sideways");
        });
}
