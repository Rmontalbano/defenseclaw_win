using System.Runtime.CompilerServices;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Paths;

namespace DefenseClaw.App.Tests.Shell;

/// <summary>
/// What may run with no review step is an explicit allow-list of known reads, not "whatever <see cref="CommandTiers"/> calls
/// read-only": the classifier reads the first verb of the path, so <c>plan apply</c> and <c>validate fix</c> are reads to it, and the palette's
/// argv comes off the installed CLI's own help, so a verb a newer CLI adds would run unreviewed. Nothing here starts a process: the
/// runner is the isolated one (no CLI on its PATH), and the review is a test seam.
/// </summary>
public sealed class UnreviewedAllowListTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private AppServices? _services;

    public void Dispose()
    {
        _services?.Dispose();
        _temp.Dispose();
    }

    private (ShellActions Actions, List<string> Toasts) Actions()
    {
        var services = TestServices.Create(_temp);
        var tray = (TrayIconService)RuntimeHelpers.GetUninitializedObject(typeof(TrayIconService));
        var toasts = new List<string>();
        var actions = new ShellActions(services, new PanelCatalog(services), tray, () => null)
        {
            Toast = (title, message) => toasts.Add(title + ": " + message),
            ClipboardWriter = _ => { },
        };
        _services = services;
        return (actions, toasts);
    }

    private static CuratedCommand Curated(string commandLine) =>
        new(commandLine.Split(' '), "Other", "What it does.", string.Empty, Array.Empty<string>());

    // ---------------------------------------------------------------------------------- the palette

    [Theory]
    [InlineData("plan apply")]
    [InlineData("validate fix")]
    [InlineData("skill list purge")]
    [InlineData("registry entries purge")]
    [InlineData("agent frobnicate")]
    public async Task A_command_off_the_list_is_reviewed_before_it_runs_whatever_the_classifier_says(string commandLine)
    {
        var (actions, toasts) = Actions();
        var command = Curated(commandLine);
        CommandReview? shown = null;
        actions.Confirmer = review => { shown = review; return false; };

        await actions.RunCuratedAsync(command);

        Assert.False(command.RunsWithoutReview);
        Assert.NotNull(shown);
        Assert.NotEqual(CommandTier.ReadOnly, shown!.Tier);
        Assert.Equal(commandLine.Split(' '), Assert.Single(shown.Steps).Argv);
        Assert.Empty(toasts);
        Assert.Empty(_services!.Cli.Activity);
    }

    [Theory]
    [InlineData("plan apply")]
    [InlineData("validate fix")]
    [InlineData("skill list purge")]
    public void The_classifier_alone_would_have_run_these_unreviewed_which_is_why_the_list_decides(string commandLine)
    {
        var argv = commandLine.Split(' ');

        Assert.Equal(CommandTier.ReadOnly, CommandTiers.Classify(argv));
        Assert.Equal(CommandTier.StateChanging, Curated(commandLine).Tier);
        Assert.False(CommandReview.MayRunUnreviewed(argv));
    }

    [Fact]
    public async Task A_declined_review_of_a_command_the_classifier_calls_read_only_says_why_it_was_asked_about()
    {
        var (actions, _) = Actions();
        CommandReview? shown = null;
        actions.Confirmer = review => { shown = review; return false; };

        await actions.RunCuratedAsync(Curated("plan apply"));

        Assert.Contains("not on DefenseClaw for Windows' list of commands known to be read-only", shown!.Summary, StringComparison.Ordinal);
        Assert.StartsWith("What it does.", shown.Summary, StringComparison.Ordinal);

        // A command that is a change by its own verb is reviewed as one, and gets no sentence about a list.
        CommandReview? setup = null;
        actions.Confirmer = review => { setup = review; return false; };
        await actions.RunCuratedAsync(Curated("setup claude-code"));
        Assert.DoesNotContain("list of commands", setup!.Summary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("doctor")]
    [InlineData("status")]
    [InlineData("version")]
    [InlineData("config validate")]
    [InlineData("skill list")]
    [InlineData("agent confidence policy show")]
    public async Task A_listed_read_runs_straight_away_with_no_review(string commandLine)
    {
        var (actions, toasts) = Actions();
        var command = Curated(commandLine);
        var asked = false;
        actions.Confirmer = _ => { asked = true; return true; };

        await actions.RunCuratedAsync(command);

        Assert.True(command.RunsWithoutReview);
        Assert.Equal(CommandTier.ReadOnly, command.Tier);
        Assert.False(asked);

        // It went to the runner: this machine has no CLI on its isolated PATH, which is the one thing that can stop it here.
        Assert.Contains("Could not run it", Assert.Single(toasts), StringComparison.Ordinal);
    }

    [Fact]
    public void The_palette_row_says_read_only_and_runs_straight_away_only_for_a_listed_read()
    {
        var (actions, _) = Actions();
        var rows = ShellCommandRegistry.BuildCliCommands(new[] { Curated("doctor"), Curated("plan apply"), Curated("setup claude-code") }, actions);

        var doctor = new PaletteItem(rows[0]);
        Assert.Equal("Read-only", doctor.TierLabel);
        Assert.Contains("runs straight away", doctor.RunNote, StringComparison.Ordinal);

        foreach (var row in rows.Skip(1))
        {
            var item = new PaletteItem(row);
            Assert.Equal("Changes state", item.TierLabel);
            Assert.Equal("You review the exact command before it runs.", item.RunNote);
        }
    }

    [Fact]
    public async Task A_destructive_verb_is_still_destructive_and_is_reviewed()
    {
        var (actions, _) = Actions();
        CommandReview? shown = null;
        actions.Confirmer = review => { shown = review; return false; };

        await actions.RunCuratedAsync(Curated("skill remove"));

        Assert.Equal(CommandTier.Destructive, shown!.Tier);
    }

    // ---------------------------------------------------------------------------------- Background diagnose and the Overview

    [Fact]
    public void Background_diagnose_and_the_overview_doctor_are_listed_reads()
    {
        Assert.True(CommandReview.MayRunUnreviewed(ShellActions.DiagnoseArgv));
        Assert.True(CommandReview.MayRunUnreviewed(OverviewPanelViewModel.DoctorArgv));
        Assert.Contains("doctor", CommandTiers.UnreviewedReadPaths);
    }

    [Fact]
    public void Every_overview_diagnostic_is_a_listed_read_on_its_own_executable()
    {
        Assert.All(
            OverviewPanelViewModel.DiagnosticCommands,
            c => Assert.True(CommandReview.MayRunUnreviewed(c.Executable, c.Argv), c.CommandText));
    }

    [Fact]
    public void A_listed_path_is_listed_only_for_the_executable_it_belongs_to()
    {
        Assert.True(CommandReview.MayRunUnreviewed("defenseclaw", new[] { "status" }));
        Assert.True(CommandReview.MayRunUnreviewed(@"C:\Users\operator\.local\bin\defenseclaw.exe", new[] { "status" }));
        Assert.True(CommandReview.MayRunUnreviewed("defenseclaw-gateway", new[] { "provenance", "show" }));
        Assert.False(CommandReview.MayRunUnreviewed("defenseclaw", new[] { "provenance", "show" }));
        Assert.False(CommandReview.MayRunUnreviewed("defenseclaw-gateway", new[] { "doctor" }));
        Assert.False(CommandReview.MayRunUnreviewed("defenseclaw-gateway", new[] { "restart" }));
        Assert.False(CommandReview.MayRunUnreviewed("cmd.exe", new[] { "status" }));
        Assert.False(CommandReview.MayRunUnreviewed(@"C:\evil\powershell.exe", new[] { "status" }));
    }

    // ---------------------------------------------------------------------------------- nouns that are really options

    [Theory]
    [InlineData("--help")]
    [InlineData("-x")]
    [InlineData("-")]
    [InlineData("--")]
    [InlineData("--version")]
    [InlineData("-doctor")]
    [InlineData("")]
    [InlineData("Doctor")]
    [InlineData("a b")]
    [InlineData("a;b")]
    public void A_name_that_starts_with_a_dash_or_is_not_a_plain_word_is_not_a_noun(string name) =>
        Assert.False(CuratedCommandCatalog.IsPlausibleNoun(name));

    [Theory]
    [InlineData("doctor")]
    [InlineData("rotate-token")]
    [InlineData("v2")]
    [InlineData("list_all")]
    [InlineData("3d")]
    public void An_ordinary_command_word_is_a_noun(string name) =>
        Assert.True(CuratedCommandCatalog.IsPlausibleNoun(name));

    [Theory]
    [InlineData("--help")]
    [InlineData("-x")]
    [InlineData("--json")]
    [InlineData("--yes")]
    public void An_argv_with_any_option_in_it_is_refused(string option)
    {
        Assert.True(CuratedCommandCatalog.Refuses(new[] { "doctor", option }));
        Assert.True(CuratedCommandCatalog.Refuses(new[] { option }));
        Assert.False(CuratedCommandCatalog.Refuses(new[] { "doctor" }));
    }

    private const string RootWithOptionLikeCommands = """
        Usage: defenseclaw [OPTIONS] COMMAND [ARGS]...

          DefenseClaw CLI.

        Options:
          --help  Show this message and exit.

        Commands:
          --evil  Looks like an option.
          -x      Also an option.
          doctor  Check the install.
        """;

    [Fact]
    public async Task A_help_line_that_names_an_option_as_a_command_is_never_walked_or_offered()
    {
        var asked = new List<string>();
        Task<HelpProbeResult> Help(string executable, IReadOnlyList<string> path, CancellationToken cancellationToken)
        {
            var key = string.Join(' ', path);
            lock (asked)
            {
                asked.Add(key);
            }

            return Task.FromResult(key == SetupHelpProbe.RootMarker
                ? new HelpProbeResult(LineEndings.Normalize(RootWithOptionLikeCommands), null)
                : key == SetupHelpProbe.RootMarker + " doctor"
                    ? new HelpProbeResult(LineEndings.Normalize("Usage: defenseclaw doctor [OPTIONS]\n\n  Check the install.\n\nOptions:\n  --help  Show this message and exit.\n"), null)
                    : new HelpProbeResult(string.Empty, "defenseclaw exited 2."));
        }

        var paths = new DefenseClawPaths(binDirectory: @"C:\fake\bin", searchPath: Array.Empty<string>(), fileExists: _ => true);
        var catalog = new CuratedCommandCatalog(new SetupHelpProbe(paths, diskCache: null, Help));

        await catalog.EnsureLoaded();

        Assert.Equal(new[] { "defenseclaw doctor" }, catalog.Commands.Select(c => c.Title).ToArray());
        lock (asked)
        {
            Assert.DoesNotContain(asked, key => key.Contains("--evil", StringComparison.Ordinal) || key.Contains("-x", StringComparison.Ordinal));
        }
    }
}
