using System.Runtime.CompilerServices;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Install;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.Tests.Shell;

/// <summary>
/// The palette's CLI commands as the connected runtime's TUI command registry (CUST-282): which registry is shown, one row per entry, and
/// what running a row does. Nothing here starts a process: the runner is the isolated one (no CLI on its PATH, so a run that is not stopped
/// first ends in "could not run it"), the review, the clipboard and the toast are test seams, and the runtime probe is answered from the
/// fixtures.
/// </summary>
public sealed class TuiRegistryPaletteTests : IDisposable
{
    private static readonly TuiRegistryCatalogue Baseline = TuiRegistryCatalogues.Baseline;
    private static readonly TuiRegistryCatalogue Extended = TuiRegistryCatalogues.Extended;

    private readonly TempDirectory _temp = new();
    private readonly List<AppServices> _services = new();

    public void Dispose()
    {
        foreach (var services in _services)
        {
            services.Dispose();
        }

        _temp.Dispose();
    }

    private static RuntimeProbeRunner FixtureRunner(string set) => (arguments, _) =>
    {
        var file = string.Join(' ', arguments) switch
        {
            "--version-json" => "version.json",
            "--help" => "root.txt",
            "setup --help" => "setup.txt",
            "guardrail --help" => "guardrail.txt",
            "config --help" => "config.txt",
            "sandbox --help" => "sandbox.txt",
            "acp --help" => "acp.txt",
            "setup redaction --help" => "setup-redaction.txt",
            _ => null,
        };

        var path = file is null ? null : System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", "runtime-" + set, file);
        return Task.FromResult(path is not null && File.Exists(path)
            ? RuntimeProbeOutput.Ok(File.ReadAllText(path))
            : RuntimeProbeOutput.Fail("exit 2"));
    };

    private sealed record Harness(ShellActions Actions, AppServices Services, List<string> Toasts, List<string> Clipboard, List<CommandReview> Reviews, List<GatewayAction> Lifecycle);

    /// <summary>
    /// Actions over isolated services. The review is declined (and recorded), the gateway's lifecycle runner is a recorder, and the clipboard and
    /// toast are lists - so nothing a row does can get further than the runner, which has no CLI to start.
    /// </summary>
    private Harness Create(RuntimeProbeRunner? runner = null)
    {
        var services = AppServices.CreateIsolated(
            TestServices.IsolatedPaths(_temp.Path),
            claudeSettingsPath: _temp.File("claude-settings.json"),
            runtimeProbeRunner: runner);
        _services.Add(services);

        var tray = (TrayIconService)RuntimeHelpers.GetUninitializedObject(typeof(TrayIconService));
        var toasts = new List<string>();
        var clipboard = new List<string>();
        var reviews = new List<CommandReview>();
        var lifecycle = new List<GatewayAction>();
        var actions = new ShellActions(services, new PanelCatalog(services), tray, () => null)
        {
            Toast = (title, message) => toasts.Add(title + ": " + message),
            ClipboardWriter = clipboard.Add,
            Confirmer = review =>
            {
                reviews.Add(review);
                return false;
            },
            GatewayActionRunner = action =>
            {
                lifecycle.Add(action);
                return Task.CompletedTask;
            },
        };

        return new Harness(actions, services, toasts, clipboard, reviews, lifecycle);
    }

    private static CuratedCommand Command(TuiRegistryCatalogue catalogue, string name) =>
        CuratedCommand.FromRegistry(catalogue.Find(name) ?? throw new InvalidOperationException($"No entry '{name}'."));

    // ------------------------------------------------------------------ which registry

    [Fact]
    public async Task The_palette_lists_the_0_8_10_registry_until_the_runtime_shows_it_has_the_larger_one()
    {
        var h = Create(FixtureRunner("95159fd"));

        // Before the runtime has answered: what 0.8.10 has is never gated, so it is the baseline - 231 entries, 210 of them run here.
        Assert.Same(Baseline, h.Actions.CliCatalogue.Source);
        Assert.Equal(210, h.Actions.CliCommands.Count);
        Assert.Equal("21 hidden on Windows", h.Actions.HiddenCommands.Note);

        _ = await h.Services.Runtime.RefreshAsync();

        Assert.Same(Extended, h.Actions.CliCatalogue.Source);
        Assert.Equal(232, h.Actions.CliCommands.Count);
        Assert.Equal("21 hidden on Windows", h.Actions.HiddenCommands.Note);
        Assert.Contains("Sandboxes run on Linux and macOS only.", h.Actions.HiddenCommands.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_0_8_10_runtime_keeps_the_baseline_and_one_that_does_not_answer_does_too()
    {
        var installed = Create(FixtureRunner("0.8.10"));
        _ = await installed.Services.Runtime.RefreshAsync();
        Assert.Same(Baseline, installed.Actions.CliCatalogue.Source);

        var silent = Create((_, _) => Task.FromResult(RuntimeProbeOutput.Fail("timed out")));
        _ = await silent.Services.Runtime.RefreshAsync();
        Assert.Same(Baseline, silent.Actions.CliCatalogue.Source);
        Assert.Equal(210, silent.Actions.CliCommands.Count);
    }

    [Fact]
    public void No_commands_are_listed_while_DefenseClaw_is_known_not_to_be_installed()
    {
        var unknown = new GatewaySnapshot();
        var notInstalled = new GatewaySnapshot { Install = InstallState.NotInstalled, State = AppGatewayState.NotInstalled };
        var stopped = new GatewaySnapshot { Install = InstallState.GatewayStopped, State = AppGatewayState.GatewayStopped };
        var uninitialized = new GatewaySnapshot { Install = InstallState.InstalledNotInitialized };

        Assert.True(ShellActions.OffersCliCommands(unknown));
        Assert.True(ShellActions.OffersCliCommands(stopped));
        Assert.True(ShellActions.OffersCliCommands(uninitialized));
        Assert.False(ShellActions.OffersCliCommands(notInstalled));
    }

    // ------------------------------------------------------------------ one row per entry

    [Theory]
    [InlineData("baseline", 210)]
    [InlineData("extended", 232)]
    public void Every_entry_Windows_runs_is_a_row_with_a_stable_unique_id_and_a_tier(string runtime, int rows)
    {
        var catalogue = new CuratedCommandCatalog(runtime == "baseline" ? Baseline : Extended);
        var h = Create();

        var built = ShellCommandRegistry.BuildCliCommands(catalogue.Commands, h.Actions);

        Assert.Equal(rows, built.Count);
        Assert.Equal(built.Count, built.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(built, row =>
        {
            Assert.StartsWith("cli.", row.Id, StringComparison.Ordinal);
            Assert.NotNull(row.Cli);
            Assert.Contains(row.Category, CuratedCommandCatalog.Categories);
            Assert.True(Enum.IsDefined(row.Cli!.Tier), row.Title);
            Assert.NotNull(row.Copy);
            Assert.Contains(row.Cli.CommandLineText, row.Keywords, StringComparison.Ordinal);
            Assert.Contains(row.Description, row.Keywords, StringComparison.Ordinal);
        });

        // The id is the TUI's name with its spaces as dots, so "skill list" is still cli.skill.list, and a remembered one finds its row again.
        Assert.Equal("cli.skill.list", Assert.Single(built, r => r.Title == "skill list").Id);
        Assert.Equal("cli.scan.skill.--all", Assert.Single(built, r => r.Title == "scan skill --all").Id);
        Assert.Same(catalogue.Find("cli.skill.list"), catalogue.Commands.Single(c => c.Id == "cli.skill.list"));
        Assert.Null(catalogue.Find("cli.sandbox.status"));
    }

    [Fact]
    public void The_registrys_names_and_descriptions_are_what_the_row_says_and_the_aliases_are_all_there()
    {
        var h = Create();
        var rows = ShellCommandRegistry.BuildCliCommands(CuratedCommandCatalog.For(null).Commands, h.Actions);

        var scan = Assert.Single(rows, r => r.Title == "scan skill --all");
        Assert.Equal("Scan", scan.Category);
        Assert.Equal("Scan all skills", scan.Description);
        Assert.Equal("defenseclaw skill scan --all", scan.Cli!.CommandLineText);

        // Three registry names for one command are three rows: the TUI keeps them so the names operators know still work.
        Assert.Equal(new[] { "list skills", "skill list", "skills" }, rows.Where(r => r.Cli!.Argv.SequenceEqual(new[] { "skill", "list" })).Select(r => r.Title).Order(StringComparer.Ordinal));
        Assert.Equal(new[] { "doctor", "doctor run", "readiness" }, rows.Where(r => r.Cli!.Argv.SequenceEqual(new[] { "doctor" })).Select(r => r.Title).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_gateways_own_verbs_run_as_defenseclaw_gateway_and_start_stop_restart_follow_the_Gateway_rows_rule()
    {
        var h = Create();
        var rows = ShellCommandRegistry.BuildCliCommands(CuratedCommandCatalog.For(null).Commands, h.Actions);

        var watchdog = Assert.Single(rows, r => r.Title == "watchdog status").Cli!;
        Assert.Equal("defenseclaw-gateway", watchdog.Executable);
        Assert.Equal("defenseclaw-gateway watchdog status", watchdog.CommandLineText);
        Assert.Null(watchdog.LifecycleAction);

        // Not known yet whether the gateway is up (no poll in an isolated composition): the same sentence the Gateway rows say.
        foreach (var (title, action) in new[] { ("start", GatewayAction.Start), ("stop", GatewayAction.Stop), ("restart", GatewayAction.Restart), ("restart gateway", GatewayAction.Restart) })
        {
            var row = Assert.Single(rows, r => r.Title == title);
            Assert.Equal(action, row.Cli!.LifecycleAction);
            Assert.False(row.IsEnabled);
            Assert.Equal(GatewayControl.Availability(action, h.Actions.Snapshot).Reason, row.DisabledReason);
        }

        Assert.True(Assert.Single(rows, r => r.Title == "watchdog stop").IsEnabled);
    }

    // ------------------------------------------------------------------ the policy over every entry

    [Fact]
    public void The_listed_reads_are_the_only_rows_that_run_without_a_review_and_they_are_read_only()
    {
        foreach (var catalogue in new[] { Baseline, Extended })
        {
            var commands = catalogue.OnWindows.Select(CuratedCommand.FromRegistry).ToArray();
            var direct = commands.Where(c => c.RunsWithoutReview).ToArray();

            Assert.All(direct, c =>
            {
                Assert.Equal(CommandTier.ReadOnly, c.Tier);
                Assert.False(c.NeedsArguments, c.Title);
                Assert.DoesNotContain(c.Argv, a => a.StartsWith('-'));
            });

            // The 36 bare reads on the allow-list, less the seven that name a target (a name typed after `--` is never a listed read).
            Assert.Equal(29, direct.Length);
            Assert.All(commands.Where(c => !c.RunsWithoutReview), c => Assert.NotEqual(CommandTier.ReadOnly, c.Tier));
        }
    }

    [Theory]
    [InlineData("baseline")]
    [InlineData("extended")]
    public async Task No_entry_runs_without_a_review_unless_it_is_a_listed_read_and_none_starts_a_process(string runtime)
    {
        var catalogue = runtime == "baseline" ? Baseline : Extended;
        var h = Create();

        foreach (var entry in catalogue.OnWindows)
        {
            var command = CuratedCommand.FromRegistry(entry);
            h.Toasts.Clear();
            h.Clipboard.Clear();
            h.Reviews.Clear();
            h.Lifecycle.Clear();

            await h.Actions.RunCuratedAsync(command);

            if (command.LifecycleAction is { } action)
            {
                // The tray's path: its own availability, review and toast. Not a bare command.
                Assert.Equal(new[] { action }, h.Lifecycle);
                Assert.Empty(h.Reviews);
            }
            else if (command.NeedsTerminal || command.NeedsArguments)
            {
                // Nothing runs: the command is on the clipboard for the operator to finish or to paste into a terminal.
                Assert.Empty(h.Reviews);
                Assert.Equal(command.ClipboardText, Assert.Single(h.Clipboard));
                _ = Assert.Single(h.Toasts);
            }
            else if (command.RunsWithoutReview)
            {
                // A listed read goes straight to the runner, which has no CLI here, and says so.
                Assert.Empty(h.Reviews);
                Assert.Contains("Could not run it", Assert.Single(h.Toasts), StringComparison.Ordinal);
            }
            else
            {
                // Everything else is shown first, with the exact argv and on the executable it is for; and the review is declined.
                var review = Assert.Single(h.Reviews);
                var step = Assert.Single(review.Steps);
                Assert.Equal(entry.Argv, step.Argv);
                Assert.Equal(entry.Executable, step.Executable);
                Assert.NotEqual(CommandTier.ReadOnly, review.Tier);
                Assert.Empty(h.Toasts);
            }
        }

        Assert.Empty(h.Services.Cli.Activity);
    }

    [Fact]
    public async Task What_ends_something_is_reviewed_as_destructive_and_a_setup_that_restarts_the_gateway_says_so()
    {
        var h = Create();

        foreach (var name in new[] { "uninstall --yes", "uninstall --all --yes", "reset --yes", "connector teardown", "setup local-observability reset" })
        {
            h.Reviews.Clear();
            await h.Actions.RunCuratedAsync(Command(Baseline, name));
            Assert.Equal(CommandTier.Destructive, Assert.Single(h.Reviews).Tier);
            Assert.Equal("Run destructive command", h.Reviews[0].ConfirmLabel);
        }

        h.Reviews.Clear();
        await h.Actions.RunCuratedAsync(Command(Baseline, "setup codex"));
        var setup = Assert.Single(h.Reviews);
        Assert.True(setup.RestartsGateway);
        Assert.Contains(setup.Warnings, w => w.Title == "Gateway restart");
        Assert.Equal("defenseclaw setup codex --yes", setup.Steps[0].CommandText);
    }

    [Fact]
    public async Task A_read_the_classifier_calls_read_only_but_the_list_does_not_have_is_reviewed_and_says_why()
    {
        var h = Create();

        // `keys list --json` and `config show --effective ...` read, but carry an option, so they are not the bare reads the list names.
        await h.Actions.RunCuratedAsync(Command(Baseline, "keys list --json"));

        var review = Assert.Single(h.Reviews);
        Assert.NotEqual(CommandTier.ReadOnly, review.Tier);
        Assert.Contains("not on DefenseClaw for Windows' list of commands known to be read-only", review.Summary, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ a value typed for a command

    [Fact]
    public async Task A_name_typed_for_a_command_goes_after_the_terminator_and_the_command_is_reviewed_before_anything_runs()
    {
        var h = Create();

        await h.Actions.RunCuratedAsync(Command(Baseline, "block skill"), "  pdf-tools ");

        var review = Assert.Single(h.Reviews);
        var step = Assert.Single(review.Steps);
        Assert.Equal(new[] { "skill", "block", "--", "pdf-tools" }, step.Argv);
        Assert.Equal("defenseclaw skill block -- pdf-tools", step.CommandText);
        Assert.Equal(CommandTier.StateChanging, review.Tier);
        Assert.Empty(h.Toasts);
        Assert.Empty(h.Clipboard);
        Assert.Empty(h.Services.Cli.Activity);
    }

    [Fact]
    public async Task A_destructive_command_with_a_typed_name_is_still_destructive()
    {
        var h = Create();

        await h.Actions.RunCuratedAsync(Command(Baseline, "quarantine skill"), "pdf-tools");
        await h.Actions.RunCuratedAsync(Command(Baseline, "remove plugin"), "x");

        Assert.Equal(new[] { CommandTier.Destructive, CommandTier.Destructive }, h.Reviews.Select(r => r.Tier));
    }

    [Fact]
    public async Task A_read_whose_bare_command_is_listed_is_reviewed_once_it_has_a_name_on_it()
    {
        var h = Create();

        foreach (var name in new[] { "info skill", "skill info", "info plugin", "tool status", "policy show", "skill search" })
        {
            h.Reviews.Clear();
            await h.Actions.RunCuratedAsync(Command(Baseline, name), "x");

            // Listed bare, so it would run unreviewed; with a target on it, it is never one of the bare reads.
            Assert.True(CommandTiers.IsUnreviewedRead(Command(Baseline, name).Argv), name);
            var review = Assert.Single(h.Reviews);
            Assert.Contains("names something you typed", review.Summary, StringComparison.Ordinal);
            Assert.DoesNotContain("not on DefenseClaw for Windows' list", review.Summary, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_command_whose_description_says_it_restarts_the_gateway_carries_the_restart_warning()
    {
        var h = Create();

        await h.Actions.RunCuratedAsync(Command(Baseline, "agent discovery enable"));
        await h.Actions.RunCuratedAsync(Command(Baseline, "agent discovery scan"));

        Assert.True(h.Reviews[0].RestartsGateway);
        Assert.Contains(h.Reviews[0].Warnings, w => w.Title == "Gateway restart");
        Assert.False(h.Reviews[1].RestartsGateway);
    }

    [Fact]
    public async Task A_command_for_the_gateway_with_a_path_typed_is_reviewed_on_the_gateway_executable()
    {
        var h = Create();

        await h.Actions.RunCuratedAsync(Command(Baseline, "scan code"), @"C:\work\project");

        var step = Assert.Single(Assert.Single(h.Reviews).Steps);
        Assert.Equal("defenseclaw-gateway", step.Executable);
        Assert.Equal(new[] { "scan", "code", "--", @"C:\work\project" }, step.Argv);
        Assert.Equal(@"defenseclaw-gateway scan code -- C:\work\project", step.CommandText);
    }

    [Fact]
    public async Task A_choice_takes_the_word_in_the_spelling_the_command_knows_and_nothing_else()
    {
        var h = Create();
        var mode = Command(Extended, "guardrail mode");

        await h.Actions.RunCuratedAsync(mode, "ACTION");
        Assert.Equal(new[] { "guardrail", "mode", "--", "action" }, Assert.Single(Assert.Single(h.Reviews).Steps).Argv);

        h.Reviews.Clear();
        await h.Actions.RunCuratedAsync(mode, "enforce");
        Assert.Empty(h.Reviews);
        Assert.Contains("observe or action", Assert.Single(h.Toasts), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("one\ntwo")]
    [InlineData("tab\there")]
    public async Task A_value_the_form_refuses_runs_nothing_and_says_why(string value)
    {
        var h = Create();

        await h.Actions.RunCuratedAsync(Command(Baseline, "block skill"), value);

        Assert.Empty(h.Reviews);
        Assert.Empty(h.Clipboard);
        _ = Assert.Single(h.Toasts);
        Assert.Empty(h.Services.Cli.Activity);
    }

    [Fact]
    public async Task A_value_longer_than_a_name_can_be_is_refused()
    {
        var h = Create();

        await h.Actions.RunCuratedAsync(Command(Baseline, "block skill"), new string('a', ArgumentForm.MaxLength + 1));

        Assert.Empty(h.Reviews);
        Assert.Contains("longer than", Assert.Single(h.Toasts), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_value_the_CLI_would_rewrite_is_refused_before_any_review_opens()
    {
        var h = Create();

        // The CLI expands ~ in every argument on Windows, even after `--`: what would be reviewed is not what would run.
        await h.Actions.RunCuratedAsync(Command(Baseline, "block skill"), "~");

        Assert.Empty(h.Reviews);
        Assert.Contains("expands", Assert.Single(h.Toasts), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_value_can_never_be_read_as_an_option()
    {
        var h = Create();

        await h.Actions.RunCuratedAsync(Command(Baseline, "block skill"), "--yes");

        var argv = Assert.Single(Assert.Single(h.Reviews).Steps).Argv;
        Assert.Equal(new[] { "skill", "block", "--", "--yes" }, argv);
        Assert.Equal(CommandTier.StateChanging, h.Reviews[0].Tier);
    }

    [Fact]
    public async Task A_command_that_needs_more_than_one_value_is_copied_to_be_completed()
    {
        var h = Create();

        foreach (var name in new[] { "setup webhook add", "mcp set", "alerts acknowledge", "alerts dismiss", "setup notifications", "setup observability add" })
        {
            h.Toasts.Clear();
            h.Clipboard.Clear();
            var command = Command(Baseline, name);
            Assert.True(command.NeedsArguments);
            Assert.Null(command.Form);

            await h.Actions.RunCuratedAsync(command, "anything");

            Assert.Equal(command.ClipboardText, Assert.Single(h.Clipboard));
            Assert.Contains("Needs " + command.ArgumentHint, Assert.Single(h.Toasts), StringComparison.Ordinal);
        }

        Assert.Empty(h.Reviews);
    }

    [Fact]
    public async Task A_command_that_asks_questions_is_copied_for_a_terminal_whatever_else_is_known_about_it()
    {
        var h = Create();

        // keys set / fill-missing read a hidden prompt, bare setup is the connector picker, and the registry calls the others interactive.
        foreach (var name in new[] { "keys set", "keys fill-missing", "fix credentials", "setup", "open setup", "setup connector", "setup llm", "setup gateway", "reset", "uninstall", "agent discovery setup", "setup skill-scanner" })
        {
            h.Toasts.Clear();
            h.Clipboard.Clear();
            var command = Command(Baseline, name);
            Assert.True(command.NeedsTerminal, name);
            Assert.Null(command.Form);

            await h.Actions.RunCuratedAsync(command, "ENV_NAME");

            Assert.Equal(command.ClipboardText, Assert.Single(h.Clipboard));
            Assert.Contains("terminal", Assert.Single(h.Toasts), StringComparison.Ordinal);
        }

        Assert.Empty(h.Reviews);
        Assert.Empty(h.Services.Cli.Activity);
    }

    [Fact]
    public async Task The_gateways_start_stop_and_restart_go_the_tray_way_and_nothing_else_does()
    {
        var h = Create();

        await h.Actions.RunCuratedAsync(Command(Baseline, "start"));
        await h.Actions.RunCuratedAsync(Command(Baseline, "stop"));
        await h.Actions.RunCuratedAsync(Command(Baseline, "restart gateway"));
        await h.Actions.RunCuratedAsync(Command(Baseline, "watchdog stop"));

        Assert.Equal(new[] { GatewayAction.Start, GatewayAction.Stop, GatewayAction.Restart }, h.Lifecycle);

        // A watchdog is not the gateway: it is an ordinary reviewed command.
        Assert.Equal("defenseclaw-gateway watchdog stop", Assert.Single(h.Reviews).Steps[0].CommandText);
    }

    [Fact]
    public async Task A_row_is_refused_before_any_review_if_its_argv_would_carry_an_option_nobody_reviewed()
    {
        var h = Create();
        var bad = new CuratedCommand(new[] { "setup", "llm", "--api-key" }, "Setup", "x", string.Empty, Array.Empty<string>(), TuiName: "setup llm key");

        await h.Actions.RunCuratedAsync(bad);

        Assert.Empty(h.Reviews);
        Assert.StartsWith("setup llm key: Refused", Assert.Single(h.Toasts), StringComparison.Ordinal);
        Assert.Empty(ShellCommandRegistry.BuildCliCommands(new[] { bad }, h.Actions));
    }

    // ------------------------------------------------------------------ the palette's own state

    private static (CommandPaletteViewModel Palette, List<ShellCommand> Chosen, List<string> Log) Palette(IReadOnlyList<ShellCommand> rows, string note = "", string detail = "")
    {
        var palette = new CommandPaletteViewModel();
        var chosen = new List<ShellCommand>();
        var log = new List<string>();
        palette.CommandChosen += (_, command) => chosen.Add(command);
        palette.CloseRequested += (_, _) => log.Add("close");
        palette.ArgumentRequested += (_, _) => log.Add("argument");
        palette.Load(rows, note, detail);
        return (palette, chosen, log);
    }

    [Fact]
    public void A_row_that_takes_a_value_asks_for_it_first_and_runs_only_with_a_value_the_form_accepts()
    {
        var h = Create();
        var rows = ShellCommandRegistry.BuildCliCommands(new[] { Command(Baseline, "block skill") }, h.Actions);
        var (palette, chosen, log) = Palette(rows);

        var item = Assert.Single(palette.Results);
        Assert.True(item.HasArgumentForm);
        Assert.Equal("skill-name", item.ArgumentPlaceholder);
        Assert.Equal("defenseclaw skill block -- <skill-name>", item.ArgvPreview);
        Assert.Equal("Run", item.RunLabel);
        Assert.Contains("Type the value above", item.RunNote, StringComparison.Ordinal);

        // Enter with nothing typed: the palette stays open and the caret goes to the box.
        Assert.True(palette.ChooseSelected());
        Assert.Empty(chosen);
        Assert.Equal(new[] { "argument" }, log);

        item.ArgumentText = "  pdf-tools ";
        Assert.Equal("defenseclaw skill block -- pdf-tools", item.ArgvPreview);

        Assert.True(palette.ChooseSelected());
        _ = Assert.Single(chosen);
        Assert.Equal(new[] { "argument", "close" }, log);
        Assert.Empty(h.Reviews);
    }

    [Fact]
    public void Running_the_chosen_row_hands_the_checked_value_to_the_run_and_only_then()
    {
        var h = Create();
        var rows = ShellCommandRegistry.BuildCliCommands(new[] { Command(Baseline, "block skill") }, h.Actions);
        var (palette, chosen, _) = Palette(rows);

        palette.Results[0].ArgumentText = " pdf-tools ";
        Assert.True(palette.ChooseSelected());

        // The run is the one the row was built with, bound to the trimmed value; the declined review is the proof that it reached the review
        // (the review is shown before the first await, so it is there when Run returns).
        chosen[0].Run();
        Assert.Equal(new[] { "skill", "block", "--", "pdf-tools" }, Assert.Single(h.Reviews).Steps[0].Argv);
    }

    [Fact]
    public void A_value_the_form_refuses_is_explained_and_the_palette_stays_open()
    {
        var h = Create();
        var rows = ShellCommandRegistry.BuildCliCommands(new[] { Command(Extended, "guardrail mode") }, h.Actions);
        var (palette, chosen, log) = Palette(rows);
        var item = palette.Results[0];

        Assert.Equal("observe or action", item.ArgumentPlaceholder);
        item.ArgumentText = "enforce";
        Assert.Equal(string.Empty, item.ArgumentProblem);

        Assert.True(palette.ChooseSelected());

        Assert.Empty(chosen);
        Assert.Equal(new[] { "argument" }, log);
        Assert.Equal("Use observe or action.", item.ArgumentProblem);

        // Editing clears the complaint; a good word runs.
        item.ArgumentText = "observe";
        Assert.Equal(string.Empty, item.ArgumentProblem);
        Assert.True(palette.ChooseSelected());
        _ = Assert.Single(chosen);
    }

    [Fact]
    public void A_value_half_typed_survives_the_rows_being_swapped_under_it()
    {
        var h = Create();
        var (palette, _, _) = Palette(ShellCommandRegistry.BuildCliCommands(new[] { Command(Baseline, "block skill"), Command(Baseline, "doctor") }, h.Actions));
        palette.Results[0].ArgumentText = "pdf-to";

        // The runtime answered while the palette was open: the rows are built again, and what was typed is still there.
        palette.Reload(ShellCommandRegistry.BuildCliCommands(new[] { Command(Baseline, "block skill"), Command(Baseline, "doctor") }, h.Actions), "1 hidden on Windows");

        Assert.Equal("block skill", palette.Selected!.Title);
        Assert.Equal("pdf-to", palette.Selected.ArgumentText);
        Assert.Equal("1 hidden on Windows", palette.HiddenNote);
    }

    [Fact]
    public void A_row_with_nothing_to_type_runs_on_the_first_Enter_as_before()
    {
        var h = Create();
        var rows = ShellCommandRegistry.BuildCliCommands(new[] { Command(Baseline, "doctor") }, h.Actions);
        var (palette, chosen, log) = Palette(rows);

        var item = Assert.Single(palette.Results);
        Assert.False(item.HasArgumentForm);
        Assert.Equal("Read-only", item.TierLabel);

        Assert.True(palette.ChooseSelected());

        _ = Assert.Single(chosen);
        Assert.Equal(new[] { "close" }, log);
    }

    [Fact]
    public void Copy_command_includes_the_typed_value_only_when_the_form_accepts_it()
    {
        var h = Create();
        var rows = ShellCommandRegistry.BuildCliCommands(new[] { Command(Baseline, "block skill") }, h.Actions);
        var (palette, _, _) = Palette(rows);

        Assert.True(palette.CopySelected());
        Assert.Equal("defenseclaw skill block", h.Clipboard[^1]);

        palette.Results[0].ArgumentText = "pdf-tools";
        Assert.True(palette.CopySelected());
        Assert.Equal("defenseclaw skill block -- pdf-tools", h.Clipboard[^1]);

        palette.Results[0].ArgumentText = "bad\nvalue";
        Assert.True(palette.CopySelected());
        Assert.Equal("defenseclaw skill block", h.Clipboard[^1]);
    }

    [Fact]
    public void Rows_that_cannot_take_a_value_in_a_form_say_what_pressing_Run_does()
    {
        var h = Create();
        var rows = ShellCommandRegistry.BuildCliCommands(
            new[] { Command(Baseline, "setup webhook add"), Command(Baseline, "keys set"), Command(Baseline, "setup llm"), Command(Baseline, "setup codex") },
            h.Actions);

        var items = rows.Select(r => new PaletteItem(r)).ToArray();

        Assert.Equal("Copy to complete", items[0].RunLabel);
        Assert.Equal("Needs <name> --type <slack|teams|pagerduty|generic>: Run copies the command for you to complete.", items[0].RunNote);
        Assert.Equal("Copy to run in a terminal", items[1].RunLabel);
        Assert.Contains("Needs a terminal", items[1].RunNote, StringComparison.Ordinal);
        Assert.Equal("Copy to run in a terminal", items[2].RunLabel);
        Assert.Equal("Run", items[3].RunLabel);
        Assert.Equal("You review the exact command before it runs.", items[3].RunNote);
        Assert.All(items, item => Assert.False(item.HasArgumentForm));
    }

    [Fact]
    public void The_palette_says_how_many_entries_Windows_does_not_run()
    {
        var h = Create();
        var catalogue = CuratedCommandCatalog.For(null);
        var rows = ShellCommandRegistry.BuildCliCommands(catalogue.Commands, h.Actions);
        var (palette, _, _) = Palette(rows, catalogue.HiddenNote, catalogue.HiddenDetail);

        Assert.Equal("21 hidden on Windows", palette.HiddenNote);
        Assert.StartsWith("Not offered on Windows", palette.HiddenDetail, StringComparison.Ordinal);
        Assert.Contains("10 commands: Sandboxes run on Linux and macOS only.", palette.HiddenDetail, StringComparison.Ordinal);
        Assert.Contains("OpenClaw", palette.HiddenDetail, StringComparison.Ordinal);
        Assert.Equal($"{rows.Count} of {rows.Count} commands", palette.CountLine);

        // A reload that swaps the registry keeps the search and brings its own note.
        palette.Query = "skill";
        palette.Reload(rows, "3 hidden on Windows", "x");
        Assert.Equal("3 hidden on Windows", palette.HiddenNote);
        Assert.Equal("skill", palette.Query);

        palette.Load(rows);
        Assert.Equal(string.Empty, palette.HiddenNote);
        Assert.Equal(string.Empty, palette.HiddenDetail);
    }

    [Theory]
    [InlineData("skills", "Go to Skills")]
    [InlineData("mcps", "Go to MCPs")]
    [InlineData("plugins", "Go to Plugins")]
    [InlineData("alerts", "Go to Alerts")]
    [InlineData("setup", "Go to Setup")]
    [InlineData("audit", "Go to Audit")]
    [InlineData("restart", "Restart gateway")]
    [InlineData("config", "Open config editor")]
    public void A_panel_or_action_that_goes_by_the_same_word_ranks_above_the_TUI_command_of_that_name(string query, string expected)
    {
        var h = Create();
        var catalog = new PanelCatalog(h.Services);
        var rows = ShellCommandRegistry.Build(catalog, h.Actions, _ => { }, () => { }, curated: CuratedCommandCatalog.For(null).Commands);

        var ranked = CommandPaletteViewModel.Rank(rows, query);

        Assert.Equal(expected, ranked[0].Title);
        Assert.Contains(ranked, r => r.Cli is not null);
    }

    [Fact]
    public void A_command_with_no_panel_or_action_of_its_name_comes_first_on_its_own_name()
    {
        var h = Create();
        var catalog = new PanelCatalog(h.Services);
        var rows = ShellCommandRegistry.Build(catalog, h.Actions, _ => { }, () => { }, curated: CuratedCommandCatalog.For(null).Commands);

        Assert.Equal("doctor", CommandPaletteViewModel.Rank(rows, "doctor")[0].Title);
        Assert.Equal("watchdog start", CommandPaletteViewModel.Rank(rows, "watchdog")[0].Title);
        Assert.Equal("scan skill --all", CommandPaletteViewModel.Rank(rows, "scan skill --all")[0].Title);
    }

    [Fact]
    public void Typing_a_registry_name_a_command_line_or_a_word_of_its_description_finds_the_row()
    {
        var h = Create();
        var rows = ShellCommandRegistry.BuildCliCommands(CuratedCommandCatalog.For(null).Commands, h.Actions);

        Assert.Equal("scan skill --all", CommandPaletteViewModel.Rank(rows, "scan skill --all")[0].Title);
        Assert.Contains(CommandPaletteViewModel.Rank(rows, "defenseclaw skill block"), r => r.Title == "block skill");
        Assert.Contains(CommandPaletteViewModel.Rank(rows, "quarantined"), r => r.Title == "restore skill");
        Assert.Contains(CommandPaletteViewModel.Rank(rows, "credentials"), r => r.Title == "keys list");
        Assert.Equal("doctor", CommandPaletteViewModel.Rank(rows, "doctor")[0].Title);
    }

    // ------------------------------------------------------------------ the argument form

    [Theory]
    [InlineData("<skill-name>", "skill-name", false)]
    [InlineData("<url>", "url", false)]
    [InlineData("<ENV_NAME>", "ENV_NAME", false)]
    [InlineData("<observe|action>", "one of observe, action", true)]
    [InlineData("<CRITICAL|HIGH|MEDIUM|LOW|inherit>", "one of CRITICAL, HIGH, MEDIUM, LOW, inherit", true)]
    public void A_hint_that_is_one_word_or_one_choice_is_a_form(string hint, string label, bool choice)
    {
        var form = ArgumentForm.Parse(hint);

        Assert.NotNull(form);
        Assert.Equal(label, form!.Label);
        Assert.Equal(choice, form.IsChoice);
    }

    [Fact]
    public void A_choice_reads_as_a_sentence_whatever_its_length()
    {
        Assert.Equal("observe or action", ArgumentForm.Parse("<observe|action>")!.Placeholder);
        Assert.Equal("CRITICAL, HIGH, MEDIUM, LOW or inherit", ArgumentForm.Parse("<CRITICAL|HIGH|MEDIUM|LOW|inherit>")!.Placeholder);
        Assert.Equal("skill-name", ArgumentForm.Parse("<skill-name>")!.Placeholder);

        var form = ArgumentForm.Parse("<CRITICAL|HIGH|MEDIUM|LOW|inherit>")!;
        Assert.Null(form.Check(" medium ", out var value));
        Assert.Equal("MEDIUM", value);
        Assert.Equal("Use CRITICAL, HIGH, MEDIUM, LOW or inherit.", form.Check("none", out _));
        Assert.Equal("Type CRITICAL, HIGH, MEDIUM, LOW or inherit.", form.Check("", out _));
        Assert.Equal("Type the skill-name.", ArgumentForm.Parse("<skill-name>")!.Check(null, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("--severity HIGH")]
    [InlineData("--payload-file <path>")]
    [InlineData("<name> --type <slack|teams|pagerduty|generic>")]
    [InlineData("<name> [--url <url>]")]
    [InlineData("<status|on|off> [--yes]")]
    [InlineData("<host> --sandbox <name>")]
    [InlineData("<preset> [flags]")]
    [InlineData("<>")]
    [InlineData("<a|>")]
    [InlineData("name")]
    public void Any_other_hint_is_not_a_form_and_the_command_is_copied_to_complete(string hint)
    {
        Assert.Null(ArgumentForm.Parse(hint));
    }

    [Fact]
    public void Every_hint_of_both_registries_is_either_a_form_or_one_of_the_seven_that_need_more()
    {
        foreach (var catalogue in new[] { Baseline, Extended })
        {
            var unformed = catalogue.OnWindows
                .Where(e => e.NeedsArgument && ArgumentForm.Parse(e.ArgumentHint) is null)
                .Select(e => e.Name)
                .Order(StringComparer.Ordinal)
                .ToArray();

            var expected = new List<string> { "alerts acknowledge", "alerts dismiss", "mcp set", "setup notifications", "setup observability add", "setup webhook add" };
            if (catalogue == Baseline)
            {
                expected.Add("audit log-activity");
            }

            Assert.Equal(expected.Order(StringComparer.Ordinal), unformed);
        }
    }
}
