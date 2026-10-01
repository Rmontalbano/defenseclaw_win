using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Paths;

namespace DefenseClaw.App.Tests.Shell;

/// <summary>
/// CUST-224: the Mac's Monitor / Commands menu chords and the curated CLI commands in the palette. Nothing here starts a process: the
/// help probe is a fake that answers from synthetic screens (and records what it was asked), and the palette actions are driven through
/// the test seams (clipboard, toast, confirmation) on paths that stop before the runner.
/// </summary>
public sealed class PaletteParityTests : IDisposable
{
    private const string Root = """
        Usage: defenseclaw [OPTIONS] COMMAND [ARGS]...

          DefenseClaw CLI.

        Options:
          --help  Show this message and exit.

        Commands:
          agent     Agent tools.
          doctor    Check the install.
          sandbox   Manage the sandbox.
          setup     Configure DefenseClaw.
          skill     Skill tools.
          tunnel    Needs Docker to run the stack.
        """;

    private static readonly Dictionary<string, string> Screens = new(StringComparer.Ordinal)
    {
        ["agent"] = """
            Usage: defenseclaw agent [OPTIONS] COMMAND [ARGS]...

              Agent tools.

            Options:
              --help  Show this message and exit.

            Commands:
              discovery  Discovery tools.
            """,
        ["agent discovery"] = """
            Usage: defenseclaw agent discovery [OPTIONS] COMMAND [ARGS]...

              Discovery tools.

            Options:
              --help  Show this message and exit.

            Commands:
              scan  Trigger one immediate AI discovery scan.
            """,
        ["agent discovery scan"] = """
            Usage: defenseclaw agent discovery scan [OPTIONS]

              Trigger one immediate AI discovery scan.

            Options:
              --help  Show this message and exit.
            """,
        ["doctor"] = """
            Usage: defenseclaw doctor [OPTIONS]

              Check the install.

            Options:
              --fix   Repair what can be repaired.
              --help  Show this message and exit.
            """,
        ["setup"] = """
            Usage: defenseclaw setup [OPTIONS] COMMAND [ARGS]...

              Configure DefenseClaw.

            Options:
              --help  Show this message and exit.

            Commands:
              claude-code          Configure DefenseClaw hooks for Claude Code.
              local-observability  Run the local stack.
            """,
        ["setup claude-code"] = """
            Usage: defenseclaw setup claude-code [OPTIONS]

              Configure DefenseClaw hooks for Claude Code.

            Options:
              --token TEXT  A token.
              --help        Show this message and exit.
            """,
        ["skill"] = """
            Usage: defenseclaw skill [OPTIONS] COMMAND [ARGS]...

              Skill tools.

            Options:
              --help  Show this message and exit.

            Commands:
              show  Show one skill.
            """,
        ["skill show"] = """
            Usage: defenseclaw skill show [OPTIONS] NAME

              Show one skill.

            Options:
              --help  Show this message and exit.
            """,
    };

    private readonly TempDirectory _temp = new();
    private readonly List<string> _asked = new();
    private readonly object _askedGate = new();

    public void Dispose()
    {
        _services?.Dispose();
        _temp.Dispose();
    }

    private Task<HelpProbeResult> FakeHelp(string executable, IReadOnlyList<string> path, CancellationToken cancellationToken)
    {
        var key = string.Join(' ', path);
        lock (_askedGate)
        {
            _asked.Add(key);
        }

        Assert.Equal(SetupHelpProbe.RootMarker, path[0]);
        var screen = key == SetupHelpProbe.RootMarker ? Root : Screens.GetValueOrDefault(key[(SetupHelpProbe.RootMarker.Length + 1)..]);
        return Task.FromResult(screen is null
            ? new HelpProbeResult(string.Empty, "defenseclaw exited 2.")
            : new HelpProbeResult(LineEndings.Normalize(screen), null));
    }

    private CuratedCommandCatalog CatalogOverFake()
    {
        var paths = new DefenseClawPaths(binDirectory: @"C:\fake\bin", searchPath: Array.Empty<string>(), fileExists: _ => true);
        return new CuratedCommandCatalog(new SetupHelpProbe(paths, diskCache: null, FakeHelp));
    }

    private async Task<IReadOnlyList<CuratedCommand>> DiscoverAsync()
    {
        var catalog = CatalogOverFake();
        await catalog.EnsureLoaded();
        return catalog.Commands;
    }

    // ------------------------------------------------------------------ discovery from help

    [Fact]
    public async Task Commands_are_read_from_the_help_screens_down_to_the_leaves()
    {
        var commands = await DiscoverAsync();
        var titles = commands.Select(c => c.Title).ToArray();

        Assert.Contains("defenseclaw doctor", titles);
        Assert.Contains("defenseclaw agent discovery scan", titles);
        Assert.Contains("defenseclaw skill show", titles);
        Assert.Contains("defenseclaw setup claude-code", titles);

        // A group is a door to commands, not a command.
        Assert.DoesNotContain("defenseclaw agent", titles);
        Assert.DoesNotContain("defenseclaw agent discovery", titles);
    }

    [Fact]
    public async Task Sandbox_docker_and_local_stack_commands_are_hidden_and_never_even_probed()
    {
        var commands = await DiscoverAsync();
        var titles = commands.Select(c => c.Title).ToArray();

        Assert.DoesNotContain(titles, t => t.Contains("sandbox", StringComparison.Ordinal));
        Assert.DoesNotContain(titles, t => t.Contains("tunnel", StringComparison.Ordinal));
        Assert.DoesNotContain(titles, t => t.Contains("local-observability", StringComparison.Ordinal));

        lock (_askedGate)
        {
            Assert.DoesNotContain(_asked, key => key.Contains("sandbox", StringComparison.Ordinal) || key.Contains("tunnel", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Only_help_screens_are_asked_for_and_every_entry_is_a_plain_noun_path()
    {
        var commands = await DiscoverAsync();

        lock (_askedGate)
        {
            // The fake asserts the marker on every call: the probe can only ever be asked for a --help screen.
            Assert.All(_asked, key => Assert.StartsWith(SetupHelpProbe.RootMarker, key, StringComparison.Ordinal));
        }

        Assert.All(commands, c =>
        {
            Assert.False(CuratedCommandCatalog.Refuses(c.Argv), c.Title);
            Assert.DoesNotContain(c.Argv, token => token.StartsWith('-'));
        });
    }

    [Fact]
    public async Task A_command_that_needs_an_argument_says_which_and_the_others_do_not()
    {
        var commands = await DiscoverAsync();

        var show = Assert.Single(commands, c => c.Title == "defenseclaw skill show");
        Assert.True(show.NeedsArguments);
        Assert.Equal(new[] { "name" }, show.RequiredArguments);

        Assert.False(Assert.Single(commands, c => c.Title == "defenseclaw doctor").NeedsArguments);
    }

    [Fact]
    public async Task Categories_follow_the_Mac_sheet_and_tiers_follow_the_command_tier_policy()
    {
        var commands = await DiscoverAsync();

        Assert.Equal("Scan", Assert.Single(commands, c => c.Title == "defenseclaw agent discovery scan").Category);
        Assert.Equal("Setup", Assert.Single(commands, c => c.Title == "defenseclaw setup claude-code").Category);
        Assert.All(commands, c => Assert.Contains(c.Category, CuratedCommandCatalog.Categories));
        Assert.DoesNotContain(commands, c => c.Category == "Sandbox");

        Assert.Equal(CommandTier.ReadOnly, Assert.Single(commands, c => c.Title == "defenseclaw doctor").Tier);
        Assert.NotEqual(CommandTier.ReadOnly, Assert.Single(commands, c => c.Title == "defenseclaw setup claude-code").Tier);
    }

    [Fact]
    public async Task No_CLI_means_no_commands_and_a_reason()
    {
        var paths = new DefenseClawPaths(binDirectory: @"C:\fake\bin", searchPath: Array.Empty<string>(), fileExists: _ => false);
        var catalog = new CuratedCommandCatalog(new SetupHelpProbe(paths, diskCache: null, FakeHelp));

        await catalog.EnsureLoaded();

        Assert.Empty(catalog.Commands);
        Assert.NotNull(catalog.LoadError);
    }

    [Fact]
    public async Task Changed_is_raised_once_the_commands_arrive()
    {
        var catalog = CatalogOverFake();
        var raised = 0;
        catalog.Changed += (_, _) => Interlocked.Increment(ref raised);

        await catalog.EnsureLoaded();
        await catalog.EnsureLoaded();

        Assert.Equal(1, raised);
        Assert.NotEmpty(catalog.Commands);
    }

    // ------------------------------------------------------------------ secret and shell refusal

    [Theory]
    [InlineData("--token")]
    [InlineData("--api-key")]
    [InlineData("--api-key=abc")]
    [InlineData("--value")]
    [InlineData("--client-secret")]
    [InlineData("--password")]
    public void An_argv_that_carries_a_secret_flag_is_refused(string flag)
    {
        Assert.True(CuratedCommandCatalog.Refuses(new[] { "setup", "llm", flag, "x" }));
        Assert.Null(CuratedCommandCatalog.Tokenize("defenseclaw setup llm " + flag + " x"));
    }

    [Theory]
    [InlineData("doctor; calc")]
    [InlineData("doctor | more")]
    [InlineData("doctor `x`")]
    [InlineData("doctor $env:X")]
    [InlineData("doctor %PATH%")]
    public void Shell_syntax_is_refused_rather_than_interpreted(string commandLine)
    {
        Assert.Null(CuratedCommandCatalog.Tokenize(commandLine));
    }

    [Fact]
    public void A_plain_command_line_tokenizes_to_argv_without_the_executable()
    {
        Assert.Equal(new[] { "agent", "discovery", "scan" }, CuratedCommandCatalog.Tokenize("defenseclaw  agent discovery   scan"));
        Assert.Null(CuratedCommandCatalog.Tokenize("   "));
        Assert.True(CuratedCommandCatalog.Refuses(Array.Empty<string>()));
    }

    [Fact]
    public void The_palette_leaves_out_an_entry_whose_argv_would_carry_a_secret()
    {
        var (actions, _, _) = Actions();
        var bad = new CuratedCommand(new[] { "setup", "--token" }, "Setup", "x", string.Empty, Array.Empty<string>());
        var good = new CuratedCommand(new[] { "doctor" }, "Info", "x", string.Empty, Array.Empty<string>());

        var rows = ShellCommandRegistry.BuildCliCommands(new[] { bad, good }, actions);

        Assert.Equal("cli.doctor", Assert.Single(rows).Id);
    }

    // ------------------------------------------------------------------ the palette rows

    private (ShellActions Actions, List<string> Toasts, List<string> Clipboard) Actions()
    {
        var services = TestServices.Create(_temp);
        var catalog = new PanelCatalog(services);
        var tray = (TrayIconService)RuntimeHelpers.GetUninitializedObject(typeof(TrayIconService));
        var toasts = new List<string>();
        var clipboard = new List<string>();
        var actions = new ShellActions(services, catalog, tray, () => null)
        {
            Toast = (title, message) => toasts.Add(title + ": " + message),
            ClipboardWriter = clipboard.Add,
        };
        _services = services;
        _catalog = catalog;
        return (actions, toasts, clipboard);
    }

    private AppServices? _services;
    private PanelCatalog? _catalog;

    [Fact]
    public void The_palette_has_the_Mac_menu_entries_with_their_chords()
    {
        var (actions, _, _) = Actions();
        var commands = ShellCommandRegistry.Build(_catalog!, actions, _ => { }, () => { });

        string? ChordOf(string id) => Assert.Single(commands, c => c.Id == id).Shortcut;

        Assert.Equal("Ctrl+Shift+H", ChordOf("app.run-doctor"));
        Assert.Equal("Ctrl+Shift+A", ChordOf("app.scan-ai"));
        Assert.Equal("Ctrl+Shift+D", ChordOf("app.diagnose"));
        Assert.Equal("Ctrl+Shift+Y", ChordOf("app.copy-last-output"));
        Assert.Equal("Ctrl+Shift+E", ChordOf("app.export-last-output"));
        Assert.Equal("F5", ChordOf("app.refresh-panel"));
        Assert.Equal("Ctrl+,", ChordOf("nav.settings"));
        Assert.All(new[] { "app.run-doctor", "app.scan-ai", "app.diagnose", "app.copy-last-output", "app.export-last-output" }, id =>
            Assert.Equal(ShellCommandRegistry.MonitorCategory, Assert.Single(commands, c => c.Id == id).Category));
    }

    [Fact]
    public void Run_health_check_and_scan_open_their_panels_with_a_payload_and_run_nothing()
    {
        var (actions, toasts, _) = Actions();

        actions.RunHealthCheck();
        Assert.Equal("overview", _services!.Navigation.Pending!.PanelId);
        Assert.Equal(new OverviewFocus(OverviewFocus.DoctorSection), _services.Navigation.Pending.Payload);

        actions.ScanAiComponents();
        Assert.Equal("ai-discovery", _services.Navigation.Pending!.PanelId);
        Assert.IsType<AiDiscoveryScan>(_services.Navigation.Pending.Payload);

        Assert.Empty(toasts);
        Assert.Empty(_services.Cli.Activity);
    }

    [Fact]
    public void Background_diagnose_is_the_plain_doctor_and_a_read()
    {
        Assert.Equal(new[] { "doctor" }, ShellActions.DiagnoseArgv);
        Assert.Equal(CommandTier.ReadOnly, CommandReview.ResolveTier(ShellActions.DiagnoseArgv));
    }

    [Fact]
    public void Copy_and_export_with_nothing_run_yet_say_so_and_touch_nothing()
    {
        var (actions, toasts, clipboard) = Actions();
        var picked = false;
        actions.SavePathPicker = _ => { picked = true; return null; };

        Assert.False(actions.CopyLastOutput());
        Assert.False(actions.ExportLastOutput());

        Assert.Empty(clipboard);
        Assert.False(picked);
        Assert.Equal(2, toasts.Count);
    }

    [Fact]
    public async Task A_curated_command_that_needs_arguments_is_copied_not_run()
    {
        var (actions, toasts, clipboard) = Actions();
        var needs = new CuratedCommand(new[] { "skill", "show" }, "Enforce", "Show one skill.", string.Empty, new[] { "name" });
        var confirmed = false;
        actions.Confirmer = _ => { confirmed = true; return true; };

        await actions.RunCuratedAsync(needs);

        Assert.Equal("defenseclaw skill show", Assert.Single(clipboard));
        Assert.False(confirmed);
        Assert.Contains("name", Assert.Single(toasts), StringComparison.Ordinal);
        Assert.Empty(_services!.Cli.Activity);
    }

    [Fact]
    public async Task A_curated_command_that_changes_state_is_reviewed_and_a_declined_review_runs_nothing()
    {
        var (actions, toasts, _) = Actions();
        var change = new CuratedCommand(new[] { "setup", "claude-code" }, "Setup", "Configure hooks.", string.Empty, Array.Empty<string>());
        CommandReview? shown = null;
        actions.Confirmer = review => { shown = review; return false; };

        await actions.RunCuratedAsync(change);

        Assert.NotNull(shown);
        Assert.NotEqual(CommandTier.ReadOnly, shown!.Tier);
        Assert.Equal(new[] { "setup", "claude-code" }, Assert.Single(shown.Steps).Argv);
        Assert.Empty(toasts);
        Assert.Empty(_services!.Cli.Activity);
    }

    [Fact]
    public async Task A_curated_command_with_a_secret_flag_is_refused_before_any_review()
    {
        var (actions, toasts, _) = Actions();
        var bad = new CuratedCommand(new[] { "setup", "llm", "--api-key" }, "Setup", "x", string.Empty, Array.Empty<string>());
        var confirmed = false;
        actions.Confirmer = _ => { confirmed = true; return true; };

        await actions.RunCuratedAsync(bad);

        Assert.False(confirmed);
        Assert.StartsWith("defenseclaw setup llm --api-key: Refused", Assert.Single(toasts), StringComparison.Ordinal);
        Assert.Empty(_services!.Cli.Activity);
    }

    [Fact]
    public void A_curated_row_has_the_detail_copy_and_a_run_that_copies_when_arguments_are_missing()
    {
        var (actions, _, clipboard) = Actions();
        var rows = ShellCommandRegistry.BuildCliCommands(
            new[] { new CuratedCommand(new[] { "skill", "show" }, "Enforce", "Show one skill.", string.Empty, new[] { "name" }) },
            actions);

        var item = new PaletteItem(Assert.Single(rows));
        Assert.True(item.IsCli);
        Assert.Equal("defenseclaw skill show", item.ArgvPreview);
        Assert.Equal("Copy to complete", item.RunLabel);
        Assert.Equal("Enforce", item.Category);

        var palette = new CommandPaletteViewModel();
        palette.Load(rows);
        Assert.Same(palette.Selected, palette.Detail);
        Assert.True(palette.CopySelected());
        Assert.Equal("defenseclaw skill show", Assert.Single(clipboard));
    }

    [Fact]
    public void The_palette_says_how_many_of_how_many_and_keeps_the_query_when_commands_arrive()
    {
        var (actions, _, _) = Actions();
        var app = ShellCommandRegistry.Build(_catalog!, actions, _ => { }, () => { });
        var palette = new CommandPaletteViewModel();
        palette.Load(app);
        Assert.Equal($"{app.Count} of {app.Count} commands", palette.CountLine);
        Assert.Null(palette.Detail);

        palette.Query = "scan";
        Assert.EndsWith($" of {app.Count} commands", palette.CountLine, StringComparison.Ordinal);

        var cli = new CuratedCommand(new[] { "agent", "discovery", "scan" }, "Scan", "Scan.", string.Empty, Array.Empty<string>());
        var more = ShellCommandRegistry.Build(_catalog!, actions, _ => { }, () => { }, curated: new[] { cli });
        palette.Reload(more);

        Assert.Equal("scan", palette.Query);
        Assert.Contains(palette.Results, r => r.Command.Id == "cli.agent.discovery.scan");
        Assert.EndsWith($" of {more.Count} commands", palette.CountLine, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ chords

    [Fact]
    public void The_new_chords_map_to_their_actions_exactly()
    {
        var cs = ModifierKeys.Control | ModifierKeys.Shift;

        Assert.Equal(ShellChordAction.HealthCheck, ShellShortcuts.ActionFor(Key.H, cs));
        Assert.Equal(ShellChordAction.ScanAi, ShellShortcuts.ActionFor(Key.A, cs));
        Assert.Equal(ShellChordAction.Diagnose, ShellShortcuts.ActionFor(Key.D, cs));
        Assert.Equal(ShellChordAction.CopyOutput, ShellShortcuts.ActionFor(Key.Y, cs));
        Assert.Equal(ShellChordAction.ExportOutput, ShellShortcuts.ActionFor(Key.E, cs));
        Assert.Equal(ShellChordAction.Refresh, ShellShortcuts.ActionFor(Key.R, ModifierKeys.Control));

        Assert.Null(ShellShortcuts.ActionFor(Key.H, ModifierKeys.Control));
        Assert.Null(ShellShortcuts.ActionFor(Key.H, cs | ModifierKeys.Alt));
        Assert.Null(ShellShortcuts.ActionFor(Key.R, cs));

        // Bare Ctrl+M / Ctrl+S / Ctrl+Y (and Ctrl+E, the Audit panel's) are not the shell's.
        foreach (var key in new[] { Key.M, Key.S, Key.Y, Key.E })
        {
            Assert.Null(ShellShortcuts.ActionFor(key, ModifierKeys.Control));
        }

        // Ctrl+Shift+M is the connector-scope chip's and Ctrl+Shift+L the theme's.
        Assert.Null(ShellShortcuts.ActionFor(Key.M, cs));
        Assert.Null(ShellShortcuts.ActionFor(Key.L, cs));
    }

    [Fact]
    public void No_two_shell_chords_collide_with_each_other_or_with_a_panel_number()
    {
        var chords = ShellShortcuts.ShellChords;
        Assert.Equal(chords.Count, chords.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        var panelChords = Enumerable.Range(0, ShellShortcuts.NumberedPanels).Select(i => ShellShortcuts.PanelChordText(i)!).ToArray();
        Assert.Empty(chords.Intersect(panelChords, StringComparer.OrdinalIgnoreCase));

        Assert.DoesNotContain("Ctrl+M", chords);
        Assert.DoesNotContain("Ctrl+S", chords);
        Assert.DoesNotContain("Ctrl+Y", chords);
        Assert.DoesNotContain("Ctrl+Shift+M", chords);
        Assert.DoesNotContain(ShellShortcuts.AuditExportText, chords);
    }

    [Fact]
    public void Every_shell_chord_is_in_the_shortcuts_overlay_and_so_are_the_panel_ones()
    {
        var (_, _, _) = Actions();
        var model = ShortcutCatalog.Build(_catalog!);
        var shown = model.Others.SelectMany(s => s.Rows).Concat(model.Panels.Rows).Select(r => r.Keys).ToArray();

        Assert.All(ShellShortcuts.ShellChords, chord => Assert.Contains(chord, shown));
        Assert.Contains(ShellShortcuts.AuditExportText, shown);
        Assert.Equal(shown.Length, shown.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    // ------------------------------------------------------------------ last output

    private static CliInvocation Invocation(string executable, string[] argv, DateTimeOffset at, params string[] lines)
    {
        var invocation = (CliInvocation)Activator.CreateInstance(
            typeof(CliInvocation),
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
            binder: null,
            args: new object[] { executable, argv, at, false },
            culture: null)!;
        var append = typeof(CliInvocation).GetMethod("Append", BindingFlags.Instance | BindingFlags.NonPublic)!;
        foreach (var line in lines)
        {
            _ = append.Invoke(invocation, new object[] { new CliOutputLine(at, CliStream.StandardOutput, line) });
        }

        return invocation;
    }

    [Fact]
    public void The_last_output_is_the_newest_runs_transcript_with_a_header_for_the_export()
    {
        var t0 = new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
        var older = Invocation("defenseclaw", new[] { "status" }, t0, "old");
        var newer = Invocation("defenseclaw", new[] { "doctor" }, t0.AddMinutes(1), "line one", "line two");

        var latest = LastCommandOutput.Latest(new[] { newer, older });

        Assert.Same(newer, latest);
        Assert.Equal("line one" + Environment.NewLine + "line two", LastCommandOutput.Text(newer));
        var export = LastCommandOutput.ExportText(newer);
        Assert.StartsWith("$ defenseclaw doctor", export, StringComparison.Ordinal);
        Assert.Contains("line two", export, StringComparison.Ordinal);
        Assert.Null(LastCommandOutput.Latest(Array.Empty<CliInvocation>()));
    }

    [Fact]
    public void The_suggested_export_name_names_the_command_and_is_a_valid_file_name()
    {
        var invocation = Invocation("defenseclaw", new[] { "agent", "discovery", "scan", "--x" }, DateTimeOffset.UtcNow);

        var name = LastCommandOutput.SuggestFileName(invocation);

        Assert.StartsWith("defenseclaw-agent-discovery-scan-", name, StringComparison.Ordinal);
        Assert.EndsWith(".log", name, StringComparison.Ordinal);
        Assert.Equal(-1, name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()));
    }
}
