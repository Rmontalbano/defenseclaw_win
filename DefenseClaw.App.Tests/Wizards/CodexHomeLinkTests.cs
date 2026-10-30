using System.Diagnostics;
using System.IO;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels.Wizards;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.Tests.Wizards;

/// <summary>
/// CUST-401: a Codex home that is a link or junction. 1.0.5's <c>setup codex</c> refuses it before it changes anything
/// (<c>codexHomeLinkRefusal</c>, codex_config_dir_windows.go:56); the wizard says so before the review, with the fix that refusal names, only on a runtime
/// that refuses it. The attribute look is injected; one test makes a real junction in a temp folder.
/// </summary>
public sealed class CodexHomeLinkTests
{
    private const string Profile = @"C:\Users\operator";

    [Fact]
    public void A_plain_folder_or_one_that_does_not_exist_is_not_a_link()
    {
        Assert.Null(CodexHomeLink.Find(null, Profile, _ => false));
        Assert.Null(CodexHomeLink.Find("  ", Profile, _ => false));
    }

    [Fact]
    public void The_default_home_that_is_a_link_gets_the_make_it_a_real_folder_fix()
    {
        var finding = CodexHomeLink.Find(null, Profile, path => path == Profile + @"\.codex")!;

        Assert.Equal(Profile + @"\.codex", finding.Home);
        Assert.Equal(finding.Home, finding.Link);
        Assert.Equal("Make " + finding.Home + " a real folder", finding.Fix);
        Assert.StartsWith("Codex home " + finding.Home + " goes through a link or junction.", finding.Message, StringComparison.Ordinal);
        Assert.Contains("stops before it changes anything", finding.Message, StringComparison.Ordinal);
        Assert.EndsWith("Make " + finding.Home + " a real folder, then run this setup again.", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_codex_home_setting_that_is_the_default_folder_is_the_default()
    {
        var finding = CodexHomeLink.Find(Profile + @"\.codex\", Profile, path => path == Profile + @"\.codex")!;

        Assert.StartsWith("Make ", finding.Fix, StringComparison.Ordinal);
    }

    [Fact]
    public void Another_codex_home_that_is_a_link_is_the_users_own_choice_to_undo()
    {
        var finding = CodexHomeLink.Find(@"D:\agents\codex", Profile, path => path == @"D:\agents\codex")!;

        Assert.Equal(@"D:\agents\codex", finding.Home);
        Assert.Equal("Set CODEX_HOME to the real folder, or unset it to use " + Profile + @"\.codex", finding.Fix);
    }

    [Fact]
    public void A_link_further_up_the_path_is_named_because_the_runtime_walks_the_whole_chain()
    {
        var finding = CodexHomeLink.Find(null, Profile, path => path == Profile)!;

        Assert.Equal(Profile, finding.Link);
        Assert.Equal(Profile + @"\.codex", finding.Home);
        Assert.Contains("(" + Profile + ")", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_real_junction_is_found_and_a_real_folder_is_not()
    {
        using var temp = new TempDirectory();
        var target = Directory.CreateDirectory(Path.Combine(temp.Path, "real-codex")).FullName;
        var link = Path.Combine(temp.Path, ".codex");
        if (!MakeJunction(link, target))
        {
            // No junction could be made here (policy); the injected cases above hold the logic.
            return;
        }

        try
        {
            Assert.True(CodexHomeLink.IsReparsePoint(link));
            Assert.False(CodexHomeLink.IsReparsePoint(target));
            Assert.False(CodexHomeLink.IsReparsePoint(Path.Combine(temp.Path, "missing")));

            // Only the folders under the temp folder are asked, so a link somewhere above it on this machine cannot change the answer.
            bool InTemp(string path) => path.StartsWith(temp.Path, StringComparison.OrdinalIgnoreCase) && CodexHomeLink.IsReparsePoint(path);
            var finding = CodexHomeLink.Find(link, temp.Path, InTemp);
            Assert.NotNull(finding);
            Assert.Equal(link, finding!.Link);

            Assert.Null(CodexHomeLink.Find(target, temp.Path, InTemp));
        }
        finally
        {
            // Removes the junction itself, never what it points at.
            Directory.Delete(link, recursive: false);
        }
    }

    private static bool MakeJunction(string link, string target)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process is null)
            {
                return false;
            }

            _ = process.StandardOutput.ReadToEnd();
            _ = process.StandardError.ReadToEnd();
            return process.WaitForExit(15_000) && process.ExitCode == 0 && Directory.Exists(link);
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ the wizard

    private static RuntimeProbeRunner Versioned(string version)
    {
        var pin = RuntimeFixtureRunner.For("95159fd");
        return (arguments, ct) => string.Join(' ', arguments) == "--version-json"
            ? Task.FromResult(RuntimeProbeOutput.Ok($"{{\"name\":\"defenseclaw-cli\",\"schema_version\":1,\"version\":\"{version}\"}}"))
            : pin(arguments, ct);
    }

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

    private static void ToReview(WizardViewModel vm)
    {
        while (!vm.IsReview)
        {
            vm.Next();
            Assert.False(vm.HasValidationSummary, vm.ValidationSummary);
        }
    }

    [Theory]
    [InlineData("1.0.5", "codex", true)]
    [InlineData("1.0.4", "codex", false)]
    [InlineData("1.0.5", "claude-code", false)]
    public async Task The_wizard_warns_before_the_review_and_on_it_only_for_setup_codex_on_a_runtime_that_refuses_a_linked_home(string version, string target, bool warned)
    {
        using var temp = new TempDirectory();
        var finding = new CodexHomeLinkFinding(Profile + @"\.codex", Profile + @"\.codex", "Make " + Profile + @"\.codex a real folder");
        using var services = TestServices.Create(temp, runtimeProbeRunner: Versioned(version));
        _ = await services.Runtime.RefreshAsync();

        StaThread.Run(() =>
        {
            using var vm = new WizardViewModel(services, Definition(target)) { CodexHomeProbe = () => finding };

            Assert.Equal(warned, vm.HasPreflightWarning);
            Assert.Equal(warned ? finding.Message : string.Empty, vm.PreflightWarning);

            ToReview(vm);
            Assert.Equal(warned, vm.CommandReview!.Warnings.Any(w => w.Title == CodexHomeLinkFinding.Title && w.Message == finding.Message));
        });
    }

    [Fact]
    public async Task A_plain_home_adds_no_warning_and_the_probe_is_read_once()
    {
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp, runtimeProbeRunner: Versioned("1.0.5"));
        _ = await services.Runtime.RefreshAsync();
        var looks = 0;

        StaThread.Run(() =>
        {
            using var vm = new WizardViewModel(services, Definition("codex")) { CodexHomeProbe = () => { looks++; return null; } };

            Assert.False(vm.HasPreflightWarning);
            ToReview(vm);
            Assert.DoesNotContain(vm.CommandReview!.Warnings, w => w.Title == CodexHomeLinkFinding.Title);
            Assert.Equal(1, looks);
        });
    }
}
