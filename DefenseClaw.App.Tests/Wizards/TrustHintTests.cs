using System.Windows;
using System.Windows.Controls;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels.Wizards;
using DefenseClaw.App.Views.Wizards;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.Tests.Wizards;

/// <summary>
/// CUST-270, the folder a failed connector setup asks the operator to trust. The text is what 0.8.10 prints
/// (<c>cmd_setup._emit_untrusted_prefix_setup_hints</c> after a setup that could not probe a connector's program, and the <c>agent discover</c> summary); the
/// wizard offers a "Trust this folder..." button on its result bar that opens the trusted-folder editor with the folder filled in, as the TUI does when it routes
/// such a setup to its editor. Every path is a made-up one under <c>C:\Users\synthetic</c>.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class TrustHintTests : IDisposable
{
    private const string Folder = @"C:\Users\synthetic\.local\bin";

    /// <summary>What <c>_emit_untrusted_prefix_setup_hints</c> prints, line by line.</summary>
    private static readonly string[] Printed =
    {
        @"  Binary resolves to: C:\Users\synthetic\.local\bin\claude.exe",
        @"  Trust it with: defenseclaw setup trusted-paths add " + Folder,
        "  `trusted-paths add` writes ~/.defenseclaw/config.yaml (ai_discovery.trusted_binary_prefixes).",
    };

    private readonly TempDirectory _temp = new();
    private readonly List<AppServices> _services = new();
    private readonly List<WizardViewModel> _viewModels = new();

    public void Dispose()
    {
        // A wizard holds a dispatcher timer: it goes with the thread that made it.
        UiThread.Run(() =>
        {
            foreach (var vm in _viewModels)
            {
                vm.Dispose();
            }
        });

        foreach (var services in _services)
        {
            services.Dispose();
        }

        _temp.Dispose();
    }

    private AppServices Services()
    {
        var services = TestServices.Create(_temp);
        _services.Add(services);
        return services;
    }

    private static CliInvocation Run(int exitCode, params string[] lines)
    {
        var invocation = InvocationFactory.Create(retainFullOutput: false, "setup", "claude-code", "--mode", "action", "--yes");
        foreach (var line in lines)
        {
            InvocationFactory.Append(invocation, line);
        }

        InvocationFactory.Finish(invocation, exitCode);
        return invocation;
    }

    // ---- reading the line ----

    [Theory]
    [MemberData(nameof(LineEndings.Both), MemberType = typeof(LineEndings))]
    public void The_folder_is_the_rest_of_the_line_the_cli_prints_whatever_its_line_ends_are(string eol)
    {
        // The runner hands over one line at a time, but a stray carriage return can still end one on Windows.
        var hint = TrustHint.Find(Printed.Select(l => l + (eol == "\r\n" ? "\r" : string.Empty)));

        Assert.Equal(Folder, hint?.Directory);
    }

    [Theory]
    [InlineData(@"C:\Program Files\Synthetic Tools\bin")]
    [InlineData(@"D:\agents\cursor\bin")]
    [InlineData(@"\\server\share\tools")]
    [InlineData(@"C:\Users\synthetic\.local\bin\")]
    public void A_folder_with_spaces_or_on_a_share_is_taken_whole_because_the_cli_does_not_quote_it(string folder)
    {
        var hint = TrustHint.Find(new[] { "  Trust it with: defenseclaw setup trusted-paths add " + folder + "  " });

        Assert.Equal(folder, hint?.Directory);
    }

    [Fact]
    public void The_first_folder_wins_when_agent_discover_lists_several()
    {
        var hint = TrustHint.Find(new[]
        {
            "  2 directories hold connector binaries outside a trusted install prefix; they were skipped during version discovery.",
            @"  Trust it with: defenseclaw setup trusted-paths add C:\Users\synthetic\.local\bin",
            @"  Trust it with: defenseclaw setup trusted-paths add D:\agents\cursor\bin",
        });

        Assert.Equal(Folder, hint?.Directory);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Trust it with: defenseclaw setup trusted-paths add ")]
    [InlineData(@"Trust it with: defenseclaw setup trusted-paths remove C:\Users\synthetic\.local\bin")]
    [InlineData(@"Trust it with: defenseclaw setup trusted-paths add tools\bin")] // not an absolute path
    [InlineData(@"Trust it with: defenseclaw setup trusted-paths add ~\.local\bin")]
    [InlineData("Trust it with: defenseclaw setup trusted-paths add --help")] // would be read as an option
    [InlineData("Trust it with: defenseclaw setup trusted-paths add -C:\\x")]
    [InlineData("Trust it with: defenseclaw setup trusted-paths add C:\\x\u202Ebin")] // a direction override
    [InlineData("Trust it with: defenseclaw setup trusted-paths add C:\\x\tbin")]
    [InlineData("Trust it with: defenseclaw setup trusted-paths add C:\\x\0bin")]
    [InlineData("Trust it with: defenseclaw setup trusted-paths add C:")]
    [InlineData("Binary resolves to: C:\\Users\\synthetic\\.local\\bin\\claude.exe")]
    [InlineData("Trust it with defenseclaw setup trusted-paths add C:\\x")]
    public void A_line_that_is_not_that_sentence_or_a_folder_nobody_would_type_is_not_offered(string line) =>
        Assert.Null(TrustHint.Find(new[] { line }));

    [Fact]
    public void A_folder_longer_than_a_path_can_be_is_not_offered()
    {
        Assert.Null(TrustHint.Find(new[] { @"Trust it with: defenseclaw setup trusted-paths add C:\" + new string('d', 600) }));
        Assert.NotNull(TrustHint.Find(new[] { @"Trust it with: defenseclaw setup trusted-paths add C:\" + new string('d', 200) }));
    }

    [Fact]
    public void No_output_is_no_hint()
    {
        Assert.Null(TrustHint.Find(Array.Empty<string>()));
        Assert.Throws<ArgumentNullException>(() => TrustHint.Find(null!));
    }

    // ---- the wizard's result ----

    /// <summary>A claude-code wizard whose "trust this folder" button records what it was asked to open. UI thread only.</summary>
    private (WizardViewModel Vm, List<(string Directory, string Context)> Opened) OpenWizard(AppServices services)
    {
        var vm = new WizardViewModel(services, WizardSamples.ClaudeCode());
        _viewModels.Add(vm);
        var opened = new List<(string, string)>();
        vm.OpenTrustedPaths = (directory, context) => opened.Add((directory, context));
        return (vm, opened);
    }

    [Fact]
    public void A_failed_run_that_printed_the_hint_offers_to_trust_the_folder_and_the_button_opens_the_editor_with_it()
    {
        var services = Services();

        UiThread.Run(() =>
        {
            var (vm, opened) = OpenWizard(services);
            Assert.False(vm.HasTrustHint);
            Assert.False(vm.TrustFolderCommand.CanExecute(null));

            vm.ApplyResult(Run(1, Printed), preview: false);

            Assert.True(vm.HasTrustHint);
            Assert.Equal(Folder, vm.TrustHintDirectory);
            Assert.Contains(Folder, vm.TrustHintMessage, StringComparison.Ordinal);
            Assert.Contains("press Execute again", vm.TrustHintMessage, StringComparison.Ordinal);
            Assert.Equal("exit 1", vm.ExitBadgeText);
            Assert.False(vm.LastRunSucceeded); // Execute stays available: the operator trusts the folder and goes on
            Assert.True(vm.TrustFolderCommand.CanExecute(null));

            vm.TrustFolderCommand.Execute(null);

            var call = Assert.Single(opened);
            Assert.Equal(Folder, call.Directory);
            Assert.Contains("Claude Code", call.Context, StringComparison.Ordinal);
            Assert.Contains(Folder, call.Context, StringComparison.Ordinal);
            Assert.Contains("not trusted", call.Context, StringComparison.Ordinal);

            // Nothing was run from here: the editor reviews the add command and runs it itself.
            Assert.Empty(services.Cli.Activity);
        });
    }

    [Fact]
    public void A_preview_that_failed_the_same_way_offers_it_too()
    {
        var services = Services();

        UiThread.Run(() =>
        {
            var (vm, _) = OpenWizard(services);

            vm.ApplyResult(Run(1, Printed), preview: true);

            Assert.True(vm.HasTrustHint);
            Assert.Equal("preview · exit 1", vm.ExitBadgeText);
        });
    }

    [Fact]
    public void A_run_that_succeeded_or_failed_for_another_reason_or_was_stopped_offers_nothing()
    {
        var services = Services();

        UiThread.Run(() =>
        {
            var (vm, _) = OpenWizard(services);

            vm.ApplyResult(Run(0, Printed), preview: false); // the line is there, and the run did not fail
            Assert.False(vm.HasTrustHint);

            vm.ApplyResult(Run(1, "Error: gateway did not start"), preview: false);
            Assert.False(vm.HasTrustHint);

            vm.ApplyResult(Run(1, Printed), preview: false);
            Assert.True(vm.HasTrustHint);

            // The next outcome replaces the last one.
            vm.ApplyResult(Run(1, "Error: gateway did not start"), preview: false);
            Assert.False(vm.HasTrustHint);
            Assert.Equal(string.Empty, vm.TrustHintMessage);
        });
    }

    [Fact]
    public void Going_back_forgets_the_hint_with_the_rest_of_the_run()
    {
        var services = Services();

        UiThread.Run(() =>
        {
            var (vm, _) = OpenWizard(services);
            for (var i = 0; i < 12 && !vm.IsReview; i++)
            {
                vm.Next();
            }

            Assert.True(vm.IsReview, vm.ValidationSummary);
            vm.ApplyResult(Run(1, Printed), preview: false);
            Assert.True(vm.HasTrustHint);

            vm.Back();

            Assert.False(vm.HasTrustHint);
            Assert.False(vm.TrustFolderCommand.CanExecute(null));
        });
    }

    [Fact]
    public void A_wizard_with_no_editor_to_open_says_so_and_runs_nothing()
    {
        var services = Services();

        UiThread.Run(() =>
        {
            var vm = new WizardViewModel(services, WizardSamples.ClaudeCode());
            _viewModels.Add(vm);
            vm.ApplyResult(Run(1, Printed), preview: false);

            vm.TrustFolderCommand.Execute(null);

            Assert.Contains("cannot be opened from here", vm.ResultMessage, StringComparison.Ordinal);
            Assert.Contains(Folder, vm.ResultMessage, StringComparison.Ordinal);
            Assert.Empty(services.Cli.Activity);
        });
    }

    [Fact]
    public void The_result_bar_shows_the_sentence_and_the_button_only_while_there_is_a_hint()
    {
        var services = Services();

        UiThread.Run(() =>
        {
            var (vm, opened) = OpenWizard(services);

            // The window's content, lifted out: the window is never shown (and so never touches the desktop).
            var window = new WizardWindow(vm);
            var content = (Grid)window.Content;
            window.Content = null;
            foreach (var bar in content.Children.OfType<Wpf.Ui.Controls.TitleBar>().ToArray())
            {
                content.Children.Remove(bar);
            }

            content.DataContext = vm;
            using var host = new OffscreenHost(content, 1000, 800);
            for (var i = 0; i < 12 && !vm.IsReview; i++)
            {
                vm.Next();
            }

            host.Relayout();
            Assert.DoesNotContain("Trust this folder…", VisibleButtons(content));
            Assert.DoesNotContain(Texts(content), text => text.Contains("does not trust", StringComparison.Ordinal));

            vm.ApplyResult(Run(1, Printed), preview: false);
            host.Relayout();

            Assert.Contains("Trust this folder…", VisibleButtons(content));
            Assert.Contains(vm.TrustHintMessage, Texts(content));
            var button = VisualTree.Descendants<Wpf.Ui.Controls.Button>(content).Single(b => b.IsVisible && Equals(b.Content, "Trust this folder…"));
            Assert.True(button.IsEnabled);
            RenderTo.Png(host, "cust270-wizard-trust-hint");

            button.Command.Execute(null);
            Assert.Equal(Folder, Assert.Single(opened).Directory);

            vm.Back();
            host.Relayout();
            Assert.DoesNotContain("Trust this folder…", VisibleButtons(content));
        });
    }

    private static IReadOnlyList<string> VisibleButtons(FrameworkElement root) =>
        VisualTree.Descendants<Wpf.Ui.Controls.Button>(root).Where(b => b.IsVisible).Select(b => b.Content?.ToString() ?? string.Empty).ToArray();

    private static IReadOnlyList<string> Texts(FrameworkElement root) =>
        VisualTree.Descendants<TextBlock>(root).Where(t => t.IsVisible).Select(t => t.Text).Where(t => t.Length > 0).ToArray();
}
