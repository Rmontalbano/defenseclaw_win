using System.Reflection;
using System.Runtime.CompilerServices;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.Tests.Wizards;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.Tests.Shell;

/// <summary>
/// CUST-311, the palette half. The rows are the connected runtime's own TUI registry entries for <c>setup local-observability</c> (six verbs
/// and the bare group, on Windows in both registries), they carry the tier their source earns, and the ones that run Docker Compose are
/// enabled only while the shared Docker look says they can run - with the look's reason on the row otherwise. Nothing here starts a process:
/// the Docker look is a fake, the runtime probe is not asked, and the runner is the isolated one with no CLI on its PATH, which is the one
/// thing that can stop a confirmed run here.
/// </summary>
public sealed class LocalObservabilityPaletteTests : IDisposable
{
    private const string Group = "setup local-observability";

    /// <summary>The registry's entries for the stack: the six verbs it lists (it has no <c>env</c> row) and the bare group.</summary>
    private static readonly string[] Names =
    {
        Group, Group + " up", Group + " down", Group + " reset", Group + " status", Group + " url", Group + " logs",
    };

    /// <summary>The rows that run Docker Compose: all of them but <c>url</c>, which prints constants. The bare group is the CLI's <c>up</c>.</summary>
    private static readonly string[] Gated = { Group, Group + " up", Group + " down", Group + " reset", Group + " status", Group + " logs" };

    private static readonly DockerStatus Ready = new(DockerState.Ready, "Docker is running.", Array.Empty<string>());

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

    private sealed record Scene(ShellActions Actions, AppServices Services, List<string> Toasts, List<CommandReview> Reviews);

    /// <summary>Actions over isolated services with the given Docker look; the review is recorded and answered with <paramref name="confirm"/>.</summary>
    private Scene Create(IDockerProbe probe, bool confirm = false)
    {
        var services = AppServices.CreateIsolated(
            TestServices.IsolatedPaths(_temp.Path),
            claudeSettingsPath: _temp.File("claude-settings.json"),
            dockerProbe: probe);
        _services.Add(services);

        var tray = (TrayIconService)RuntimeHelpers.GetUninitializedObject(typeof(TrayIconService));
        var toasts = new List<string>();
        var reviews = new List<CommandReview>();
        var actions = new ShellActions(services, new PanelCatalog(services), tray, () => null)
        {
            Toast = (title, message) => toasts.Add(title + ": " + message),
            ClipboardWriter = _ => { },
            Confirmer = review =>
            {
                reviews.Add(review);
                return confirm;
            },
        };
        return new Scene(actions, services, toasts, reviews);
    }

    private static TuiRegistryCatalogue Of(string runtime) => runtime == "baseline" ? TuiRegistryCatalogues.Baseline : TuiRegistryCatalogues.Extended;

    private static CuratedCommand Command(string name, string runtime = "baseline") =>
        CuratedCommand.FromRegistry(Of(runtime).Find(name) ?? throw new InvalidOperationException($"No entry '{name}'."));

    private static ShellCommand Row(IReadOnlyList<ShellCommand> rows, string name) => rows.Single(r => r.Cli!.TuiName == name);

    // ------------------------------------------------------------------ the registry's rows

    [Theory]
    [InlineData("baseline")]
    [InlineData("extended")]
    public void Both_registries_offer_the_stack_on_windows_as_six_verbs_and_the_bare_group_and_hide_none_of_it(string runtime)
    {
        var catalogue = Of(runtime);

        var offered = catalogue.OnWindows
            .Where(e => e.Argv.Count >= 2 && e.Argv[0] == "setup" && e.Argv[1] == "local-observability")
            .Select(e => e.Name)
            .Order(StringComparer.Ordinal);
        Assert.Equal(Names.Order(StringComparer.Ordinal), offered);
        Assert.DoesNotContain(catalogue.HiddenOnWindows, e => e.Name.StartsWith(Group, StringComparison.Ordinal));

        // The TUI has no row for `env` (it prints constants; the wizard's command page still offers it).
        Assert.Null(catalogue.Find(Group + " env"));

        // The exact argv the TUI itself runs: the verbs plain, `reset` with the --yes that stands in for its "Continue?" question.
        Assert.Equal(new[] { "setup", "local-observability" }, catalogue.Find(Group)!.Argv);
        foreach (var verb in new[] { "up", "down", "status", "url", "logs" })
        {
            Assert.Equal(new[] { "setup", "local-observability", verb }, catalogue.Find(Group + " " + verb)!.Argv);
        }

        Assert.Equal(new[] { "setup", "local-observability", "reset", "--yes" }, catalogue.Find(Group + " reset")!.Argv);
        Assert.Null(TuiRegistryCatalogues.ArgvProblem(catalogue.Find(Group + " reset")!.Argv));
    }

    [Theory]
    [InlineData("baseline")]
    [InlineData("extended")]
    public void Each_row_carries_the_tier_its_source_earns_and_only_the_three_reads_run_without_a_review(string runtime)
    {
        foreach (var name in new[] { Group + " status", Group + " logs", Group + " url" })
        {
            var command = Command(name, runtime);
            Assert.Equal(CommandTier.ReadOnly, command.Tier);
            Assert.True(command.RunsWithoutReview, name);
        }

        // `up` and `down` change state; the bare group is the CLI's `up`, so it is at least that.
        foreach (var name in new[] { Group, Group + " up", Group + " down" })
        {
            var command = Command(name, runtime);
            Assert.Equal(CommandTier.StateChanging, command.Tier);
            Assert.False(command.RunsWithoutReview, name);
        }

        var reset = Command(Group + " reset", runtime);
        Assert.Equal(CommandTier.Destructive, reset.Tier);
        Assert.False(reset.RunsWithoutReview);
    }

    [Fact]
    public void The_palette_says_so_on_each_row_and_the_destructive_one_is_reviewed()
    {
        var scene = Create(new FixedDockerProbe(Ready));
        var rows = ShellCommandRegistry.BuildCliCommands(CuratedCommandCatalog.For(null).Commands, scene.Actions);

        PaletteItem Item(string name) => new(Row(rows, name));

        Assert.Equal("Read-only", Item(Group + " status").TierLabel);
        Assert.Contains("runs straight away", Item(Group + " status").RunNote, StringComparison.Ordinal);
        Assert.Equal("Changes state", Item(Group + " up").TierLabel);
        Assert.Equal("You review the exact command before it runs.", Item(Group + " up").RunNote);
        Assert.Equal("Destructive", Item(Group + " reset").TierLabel);
        Assert.Equal("You review the exact command before it runs.", Item(Group + " reset").RunNote);
        Assert.Equal("defenseclaw setup local-observability reset --yes", Item(Group + " reset").ArgvPreview);
        Assert.Equal("cli.setup.local-observability.reset", Row(rows, Group + " reset").Id);
    }

    // ------------------------------------------------------------------ the gate

    [Fact]
    public void Before_the_first_look_the_rows_that_run_compose_say_checking_and_url_is_open()
    {
        var scene = Create(new ManualDockerProbe());

        var rows = ShellCommandRegistry.BuildCliCommands(CuratedCommandCatalog.For(null).Commands, scene.Actions);

        foreach (var name in Gated)
        {
            var row = Row(rows, name);
            Assert.False(row.IsEnabled, name);
            Assert.Equal(LocalStackAvailability.CheckingReason, row.DisabledReason);
        }

        var url = Row(rows, Group + " url");
        Assert.True(url.IsEnabled);
        Assert.Null(url.DisabledReason);
    }

    [Theory]
    [InlineData(DockerState.Ready, true)]
    [InlineData(DockerState.Unknown, true)]
    [InlineData(DockerState.NotInstalled, false)]
    [InlineData(DockerState.EngineDown, false)]
    [InlineData(DockerState.ComposeMissing, false)]
    public async Task The_rows_that_run_compose_follow_the_docker_look_and_url_and_every_other_row_never_do(DockerState state, bool open)
    {
        var commands = CuratedCommandCatalog.For(null).Commands;
        var scene = Create(new FixedDockerProbe(new DockerStatus(state, "The probe's own sentence.", Array.Empty<string>())));
        await scene.Services.LocalStack.RefreshAsync();
        var reference = Create(new FixedDockerProbe(Ready));
        await reference.Services.LocalStack.RefreshAsync();

        var rows = ShellCommandRegistry.BuildCliCommands(commands, scene.Actions);
        var baseline = ShellCommandRegistry.BuildCliCommands(commands, reference.Actions);

        Assert.Equal(baseline.Count, rows.Count);
        var gated = new List<string>();
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (!WizardWindowsPolicy.CommandNeedsDocker(row.Cli!.Argv))
            {
                // Whatever Docker says changes nothing about a row that does not run it: the same as with a healthy Docker.
                Assert.Equal(baseline[i].IsEnabled, row.IsEnabled);
                Assert.Equal(baseline[i].DisabledReason, row.DisabledReason);
                continue;
            }

            gated.Add(row.Cli.TuiName!);
            Assert.Equal(open, row.IsEnabled);
            var item = new PaletteItem(row);
            if (open)
            {
                Assert.Null(row.DisabledReason);
                Assert.Equal(row.Description, item.DetailLine);
            }
            else
            {
                // The row says why, in the look's own words, instead of its description; a screen reader hears that it is unavailable.
                Assert.StartsWith("The probe's own sentence.", row.DisabledReason, StringComparison.Ordinal);
                Assert.Equal(row.DisabledReason, item.DetailLine);
                Assert.Contains("unavailable", item.AutomationName, StringComparison.Ordinal);
            }
        }

        Assert.Equal(Gated.Order(StringComparer.Ordinal), gated.Order(StringComparer.Ordinal));
        Assert.True(Row(rows, Group + " url").IsEnabled);
    }

    [Fact]
    public async Task A_closed_row_is_skipped_by_the_arrow_keys_and_cannot_be_chosen()
    {
        var scene = Create(new FixedDockerProbe(new DockerStatus(DockerState.NotInstalled, "Docker was not found on this machine's PATH.", Array.Empty<string>())));
        await scene.Services.LocalStack.RefreshAsync();

        var palette = new CommandPaletteViewModel();
        palette.Load(ShellCommandRegistry.BuildCliCommands(CuratedCommandCatalog.For(null).Commands, scene.Actions));
        palette.Query = "local-observability";
        var chosen = new List<ShellCommand>();
        palette.CommandChosen += (_, command) => chosen.Add(command);

        var status = palette.Results.Single(r => r.Title == Group + " status");
        Assert.False(status.IsEnabled);
        Assert.False(palette.Choose(status));
        Assert.Empty(chosen);

        // The selection rests on a row Enter would honour (url), never on one it would refuse.
        Assert.Equal(Group + " url", palette.Selected!.Title);
        Assert.True(palette.Selected.IsEnabled);
    }

    [Fact]
    public async Task The_look_is_asked_for_when_the_palette_lists_the_stack_and_not_again_while_it_is_fresh()
    {
        var probe = new ManualDockerProbe();
        var scene = Create(probe);

        // Nothing has asked yet: building the rows is only a reading of what is known.
        _ = ShellCommandRegistry.BuildCliCommands(scene.Actions.CliCommands, scene.Actions);
        Assert.Equal(0, probe.Calls);

        var look = scene.Actions.CheckLocalStack();
        await probe.WaitForCallsAsync(1);
        probe.Answer(Ready);
        await look;
        Assert.Equal(1, probe.Calls);

        await scene.Actions.CheckLocalStack();
        Assert.Equal(1, probe.Calls);
    }

    // ------------------------------------------------------------------ the runs

    [Fact]
    public async Task Up_is_reviewed_as_a_change_that_restarts_the_gateway_and_a_declined_review_runs_nothing()
    {
        var scene = Create(new FixedDockerProbe(Ready));

        await scene.Actions.RunCuratedAsync(Command(Group + " up"));

        var shown = Assert.Single(scene.Reviews);
        Assert.Equal("Run defenseclaw setup local-observability up?", shown.Title);
        Assert.Equal(CommandTier.StateChanging, shown.Tier);
        Assert.Equal(new[] { "setup", "local-observability", "up" }, Assert.Single(shown.Steps).Argv);
        Assert.Equal(LocalStackReview.Summary(new[] { "setup", "local-observability", "up" }), shown.Summary);

        // Bare `up` rewrites config.yaml, so `setup`'s result callback restarts a running gateway: the standard bar, then the stack's own.
        Assert.True(shown.RestartsGateway);
        Assert.Equal(new[] { "Gateway restart", LocalStackReview.RefreshesFilesTitle }, shown.Warnings.Select(w => w.Title).ToArray());
        Assert.Empty(scene.Toasts);
        Assert.Empty(scene.Services.Cli.Activity);
    }

    [Fact]
    public async Task The_bare_group_row_is_reviewed_as_the_up_it_is_whatever_the_registrys_one_line_says()
    {
        var scene = Create(new FixedDockerProbe(Ready));
        var bare = Command(Group);
        Assert.Equal("Show local observability commands", bare.Summary);

        await scene.Actions.RunCuratedAsync(bare);

        var shown = Assert.Single(scene.Reviews);
        Assert.Equal("Run defenseclaw setup local-observability?", shown.Title);
        Assert.Equal(CommandTier.StateChanging, shown.Tier);
        Assert.Equal(new[] { "setup", "local-observability" }, Assert.Single(shown.Steps).Argv);

        // It starts containers and rewrites config.yaml, and the review says that instead of the registry's description.
        Assert.StartsWith("Starts the stack's containers", shown.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("Show local observability commands", shown.Summary, StringComparison.Ordinal);
        Assert.True(shown.RestartsGateway);
        Assert.Equal(new[] { "Gateway restart", LocalStackReview.RefreshesFilesTitle }, shown.Warnings.Select(w => w.Title).ToArray());
    }

    [Fact]
    public async Task Down_restarts_nothing_and_says_the_destination_stays_enabled()
    {
        var scene = Create(new FixedDockerProbe(Ready));

        await scene.Actions.RunCuratedAsync(Command(Group + " down"));

        var shown = Assert.Single(scene.Reviews);
        Assert.Equal(CommandTier.StateChanging, shown.Tier);
        Assert.False(shown.RestartsGateway);
        Assert.Equal(new[] { LocalStackReview.ExportingContinuesTitle }, shown.Warnings.Select(w => w.Title).ToArray());
        Assert.Empty(scene.Toasts);
        Assert.Empty(scene.Services.Cli.Activity);
    }

    [Fact]
    public async Task Reset_is_reviewed_as_destructive_with_the_registrys_yes_on_the_command_and_what_it_deletes()
    {
        var scene = Create(new FixedDockerProbe(Ready));

        await scene.Actions.RunCuratedAsync(Command(Group + " reset"));

        var shown = Assert.Single(scene.Reviews);
        Assert.Equal(CommandTier.Destructive, shown.Tier);
        Assert.True(shown.IsDestructive);
        Assert.Equal("Run destructive command", shown.ConfirmLabel);
        Assert.Equal("defenseclaw setup local-observability reset --yes", shown.CommandText);
        Assert.Equal(new[] { "setup", "local-observability", "reset", "--yes" }, Assert.Single(shown.Steps).Argv);

        // The review is the question the CLI would have asked, and says so; it also says what goes.
        var deletes = Assert.Single(shown.Warnings, w => w.Title == LocalStackReview.DeletesDataTitle);
        Assert.Contains("stands in for the CLI's own \"Continue?\"", deletes.Message, StringComparison.Ordinal);
        Assert.False(shown.RestartsGateway);
        Assert.Empty(scene.Toasts);
        Assert.Empty(scene.Services.Cli.Activity);
    }

    [Fact]
    public async Task A_confirmed_review_goes_to_the_runner_which_here_has_no_cli_and_so_starts_nothing()
    {
        var scene = Create(new FixedDockerProbe(Ready), confirm: true);

        await scene.Actions.RunCuratedAsync(Command(Group + " reset"));

        _ = Assert.Single(scene.Reviews);
        Assert.Contains("Could not run it", Assert.Single(scene.Toasts), StringComparison.Ordinal);
        Assert.Empty(scene.Services.Cli.Activity);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("logs")]
    [InlineData("url")]
    public async Task The_three_reads_run_straight_away_with_no_review(string verb)
    {
        var scene = Create(new FixedDockerProbe(Ready), confirm: true);

        await scene.Actions.RunCuratedAsync(Command(Group + " " + verb));

        Assert.Empty(scene.Reviews);
        Assert.Contains("Could not run it", Assert.Single(scene.Toasts), StringComparison.Ordinal);
    }

    [Fact]
    public async Task What_the_clis_own_docker_check_would_refuse_is_on_the_review_of_a_verb_that_reaches_docker()
    {
        var warned = new DockerStatus(DockerState.Ready, "Docker is running.", new[] { "Docker Desktop is using the WSL 2 backend." });
        var scene = Create(new FixedDockerProbe(warned));
        await scene.Services.LocalStack.RefreshAsync();

        await scene.Actions.RunCuratedAsync(Command(Group + " down"));

        var bar = Assert.Single(Assert.Single(scene.Reviews).Warnings, w => w.Title == LocalStackReview.DockerMayRefuseTitle);
        Assert.Contains("WSL 2 backend", bar.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_row_for_a_command_that_is_not_the_stacks_is_reviewed_exactly_as_before()
    {
        var scene = Create(new FixedDockerProbe(Ready));
        await scene.Services.LocalStack.RefreshAsync();

        await scene.Actions.RunCuratedAsync(Command("setup codex"));

        var shown = Assert.Single(scene.Reviews);
        Assert.True(shown.RestartsGateway);
        Assert.Equal("Gateway restart", Assert.Single(shown.Warnings).Title);
        Assert.Equal("defenseclaw setup codex --yes", Assert.Single(shown.Steps).CommandText);
        Assert.DoesNotContain("stack", shown.Summary, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// The dashboard window and the stack's palette rows: it asks for the Docker look when the palette opens, swaps the rows when the look
/// answers, and lets go of the look when it really closes. No process is started: the look is a fake that waits for the test to answer.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class LocalObservabilityPaletteWindowTests : IDisposable
{
    private const string Up = "setup local-observability up";

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

    private AppServices Create(IDockerProbe probe)
    {
        var services = AppServices.CreateIsolated(
            TestServices.IsolatedPaths(_temp.Path),
            claudeSettingsPath: _temp.File("claude-settings.json"),
            dockerProbe: probe);
        _services.Add(services);
        return services;
    }

    private static TrayIconService UnbuiltTray() =>
        (TrayIconService)RuntimeHelpers.GetUninitializedObject(typeof(TrayIconService));

    private static CommandPaletteViewModel PaletteOf(MainWindow window) =>
        (CommandPaletteViewModel)typeof(MainWindow).GetField("_paletteViewModel", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;

    private static PaletteItem Item(CommandPaletteViewModel palette, string title) => palette.Results.Single(i => i.Title == title);

    private static int Subscribers(object owner, string eventName)
    {
        var field = owner.GetType().GetField(eventName, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"{owner.GetType().Name}.{eventName} is not a field-like event any more; update this helper.");
        return (field.GetValue(owner) as Delegate)?.GetInvocationList().Length ?? 0;
    }

    [Fact]
    public async Task Opening_the_palette_asks_for_the_look_and_an_open_palette_swaps_the_rows_when_it_answers()
    {
        var probe = new ManualDockerProbe();
        var services = Create(probe);
        MainWindow? window = null;
        CommandPaletteViewModel? palette = null;

        UiThread.Run(() =>
        {
            window = new MainWindow(services, new PanelCatalog(services), UnbuiltTray());
            palette = PaletteOf(window);

            // The way a panel's menu asks for the palette. Nobody has looked at Docker yet, so the rows that need it say so.
            Assert.Equal(0, probe.Calls);
            services.Navigation.RequestPalette();
            var up = Item(palette, Up);
            Assert.False(up.IsEnabled);
            Assert.Equal(LocalStackAvailability.CheckingReason, up.Command.DisabledReason);
            Assert.True(Item(palette, "setup local-observability url").IsEnabled);
        });

        try
        {
            // Opening the palette is what asked, once; the answer arrives on another thread and the open palette is rebuilt in place.
            await probe.WaitForCallsAsync(1);
            probe.Answer(new DockerStatus(DockerState.Ready, "Docker is running.", Array.Empty<string>()));

            UiThread.WaitFor(() => Item(palette!, Up).IsEnabled, "the stack's rows, enabled by the look");
            UiThread.Run(() =>
            {
                Assert.True(Item(palette!, "setup local-observability status").IsEnabled);
                Assert.True(Item(palette!, "setup local-observability reset").IsEnabled);
                Assert.True(Item(palette!, "setup local-observability").IsEnabled);
                Assert.Equal(1, probe.Calls);
            });
        }
        finally
        {
            UiThread.Run(() =>
            {
                window!.AllowClose();
                window.Close();
            });
        }
    }

    [Fact]
    public async Task A_no_from_the_look_greys_the_rows_out_with_its_reason_in_the_open_palette()
    {
        var probe = new ManualDockerProbe();
        var services = Create(probe);
        MainWindow? window = null;
        CommandPaletteViewModel? palette = null;

        UiThread.Run(() =>
        {
            window = new MainWindow(services, new PanelCatalog(services), UnbuiltTray());
            palette = PaletteOf(window);
            services.Navigation.RequestPalette();
        });

        try
        {
            await probe.WaitForCallsAsync(1);
            probe.Answer(new DockerStatus(DockerState.EngineDown, "Docker is installed but its engine is not answering.", Array.Empty<string>()));

            UiThread.WaitFor(() => Item(palette!, Up).Command.DisabledReason?.StartsWith("Docker is installed", StringComparison.Ordinal) == true, "the look's reason on the row");
            UiThread.Run(() =>
            {
                Assert.False(Item(palette!, Up).IsEnabled);
                Assert.True(Item(palette!, "setup local-observability url").IsEnabled);
            });
        }
        finally
        {
            UiThread.Run(() =>
            {
                window!.AllowClose();
                window.Close();
            });
        }
    }

    [Fact]
    public void A_window_listens_to_the_docker_look_while_it_lives_and_stops_when_it_really_closes()
    {
        var services = Create(new ManualDockerProbe());

        UiThread.Run(() =>
        {
            var before = Subscribers(services.LocalStack, "Changed");
            var window = new MainWindow(services, new PanelCatalog(services), UnbuiltTray());
            Assert.Equal(before + 1, Subscribers(services.LocalStack, "Changed"));

            window.AllowClose();
            window.Close();
            Assert.Equal(before, Subscribers(services.LocalStack, "Changed"));
        });
    }
}
