using System.Reflection;
using System.Runtime.CompilerServices;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.Tests.Wizards;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Paths;

namespace DefenseClaw.App.Tests.Shell;

/// <summary>
/// CUST-317, the palette half. Neither runtime's TUI registry has a dashboards command, so the three rows (<c>plan</c>, <c>apply --yes</c>,
/// <c>destroy --yes</c>) are added beside the registry's. They are reviewed before they run, carry the tier their source earns, and are enabled
/// only while the shared Terraform look says Terraform can run them - with the look's reason on the row otherwise. Nothing here starts a
/// process: the Terraform look is a fake, the runtime probe is not asked, the token check is a seam, and the runner is the isolated one with
/// no CLI on its PATH, which is the one thing that can stop a confirmed run here.
/// </summary>
public sealed class SplunkDashboardsPaletteTests : IDisposable
{
    private const string Prefix = "setup splunk dashboards ";

    private static readonly string[] Names = { Prefix + "plan", Prefix + "apply", Prefix + "destroy" };

    private static readonly TerraformStatus Ready =
        new(TerraformState.Ready, "Terraform 1.9.5 is available.", "1.9.5", @"C:\synthetic\bin\terraform.exe");

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

    private sealed record Scene(ShellActions Actions, AppServices Services, PanelCatalog Catalog, List<string> Toasts, List<CommandReview> Reviews, List<string> TokenChecks);

    /// <summary>
    /// Actions over isolated services with the given Terraform look; the review is recorded and answered with <paramref name="confirm"/>. The
    /// installation is the usual writable one unless a synthetic one is given (<see cref="TestInstallations"/>, CUST-308).
    /// </summary>
    private Scene Create(ITerraformProbe probe, bool confirm = false, CredentialPresence token = CredentialPresence.InEnvironment, InstallationContext? installation = null)
    {
        var services = AppServices.CreateIsolated(
            TestServices.IsolatedPaths(_temp.Path, installation),
            claudeSettingsPath: _temp.File("claude-settings.json"),
            terraformProbe: probe);
        _services.Add(services);

        var catalog = new PanelCatalog(services);
        var tray = (TrayIconService)RuntimeHelpers.GetUninitializedObject(typeof(TrayIconService));
        var toasts = new List<string>();
        var reviews = new List<CommandReview>();
        var tokenChecks = new List<string>();
        var actions = new ShellActions(services, catalog, tray, () => null)
        {
            Toast = (title, message) => toasts.Add(title + ": " + message),
            ClipboardWriter = _ => { },
            Confirmer = review =>
            {
                reviews.Add(review);
                return confirm;
            },
            CredentialCheck = name =>
            {
                tokenChecks.Add(name);
                return token;
            },
        };
        return new Scene(actions, services, catalog, toasts, reviews, tokenChecks);
    }

    private static CuratedCommand Command(string verb) =>
        SplunkDashboardsCommands.Rows.Single(c => c.TuiName == Prefix + verb);

    private static ShellCommand Row(IReadOnlyList<ShellCommand> rows, string name) => rows.Single(r => r.Cli!.TuiName == name);

    private static IReadOnlyList<ShellCommand> DashboardRows(Scene scene) =>
        ShellCommandRegistry.BuildCliCommands(SplunkDashboardsCommands.Rows, scene.Actions);

    // ------------------------------------------------------------------ the rows

    [Fact]
    public void There_are_three_rows_in_the_order_of_the_verbs_with_the_exact_argv_the_review_will_show()
    {
        Assert.Equal(Names, SplunkDashboardsCommands.Rows.Select(r => r.TuiName).ToArray());
        Assert.Equal(new[] { "setup", "splunk", "dashboards", "plan" }, Command("plan").Argv);

        // apply and destroy ask the CLI's own question unless given --yes; a window with no console cannot answer it, and the review is that question.
        Assert.Equal(new[] { "setup", "splunk", "dashboards", "apply", "--yes" }, Command("apply").Argv);
        Assert.Equal(new[] { "setup", "splunk", "dashboards", "destroy", "--yes" }, Command("destroy").Argv);

        Assert.All(SplunkDashboardsCommands.Rows, r =>
        {
            Assert.Equal("Setup", r.Category);
            Assert.Equal(CommandReview.DefaultExecutable, r.Executable);
            Assert.False(r.NeedsArguments);
            Assert.False(r.NeedsTerminal);
            Assert.Null(r.Form);
            Assert.NotEmpty(r.Summary);
            Assert.DoesNotContain(r.Argv, a => a.StartsWith("--o11y", StringComparison.Ordinal) || a == "--terraform-bin");
        });
    }

    [Fact]
    public void Every_argv_passes_the_catalogues_own_check_so_a_token_flag_could_never_be_added_to_one()
    {
        Assert.All(SplunkDashboardsCommands.Rows, r =>
        {
            Assert.False(CuratedCommandCatalog.Refuses(r.Argv));
            Assert.Null(TuiRegistryCatalogues.ArgvProblem(r.Argv));
        });

        Assert.True(CuratedCommandCatalog.Refuses(Command("apply").Argv.Append("--o11y-api-token").ToArray()));
        Assert.True(CuratedCommandCatalog.Refuses(Command("plan").Argv.Concat(new[] { "--terraform-bin", @"C:\x\terraform.exe" }).ToArray()));
    }

    [Fact]
    public void Each_row_carries_the_tier_its_source_earns_and_none_runs_without_a_review()
    {
        Assert.Equal(CommandTier.StateChanging, Command("plan").Tier);
        Assert.Equal(CommandTier.StateChanging, Command("apply").Tier);
        Assert.Equal(CommandTier.Destructive, Command("destroy").Tier);

        Assert.All(SplunkDashboardsCommands.Rows, r =>
        {
            Assert.False(r.RunsWithoutReview, r.TuiName);
            Assert.False(CommandReview.MayRunUnreviewed(r.Argv), r.TuiName);
            Assert.False(CommandTiers.IsUnreviewedRead(r.Argv), r.TuiName);
        });
    }

    [Fact]
    public void Neither_registry_has_a_dashboards_entry_so_the_rows_are_added_beside_the_registrys_and_only_with_them()
    {
        foreach (var catalogue in new[] { TuiRegistryCatalogues.Baseline, TuiRegistryCatalogues.Extended })
        {
            Assert.DoesNotContain(catalogue.OnWindows, e => e.Argv.Count >= 3 && e.Argv[2] == "dashboards");
            Assert.DoesNotContain(catalogue.HiddenOnWindows, e => e.Argv.Count >= 3 && e.Argv[2] == "dashboards");
            Assert.All(Names, n => Assert.Null(catalogue.Find(n)));
        }

        var scene = Create(new FixedTerraformProbe(Ready));
        var registry = CuratedCommandCatalog.For(null).Commands;

        // The registry's own rows are exactly what they were: no dashboards row among them.
        Assert.DoesNotContain(ShellCommandRegistry.BuildCliCommands(registry, scene.Actions), r => r.Id.Contains("dashboards", StringComparison.Ordinal));

        // The whole palette lists the registry's rows and then the three.
        var all = ShellCommandRegistry.Build(scene.Catalog, scene.Actions, _ => { }, () => { }, curated: registry);
        Assert.Equal(
            Names.Select(n => "cli." + n.Replace(' ', '.')).ToArray(),
            all.Where(c => c.Id.StartsWith("cli.setup.splunk.dashboards.", StringComparison.Ordinal)).Select(c => c.Id).ToArray());
        Assert.Equal(registry.Count + 3, all.Count(c => c.Cli is not null));

        // Wherever the registry's rows are not listed (DefenseClaw not installed, a test with none), neither are these.
        var without = ShellCommandRegistry.Build(scene.Catalog, scene.Actions, _ => { }, () => { });
        Assert.DoesNotContain(without, c => c.Cli is not null);
        Assert.DoesNotContain(
            ShellCommandRegistry.Build(scene.Catalog, scene.Actions, _ => { }, () => { }, curated: Array.Empty<CuratedCommand>()),
            c => c.Cli is not null);
    }

    [Fact]
    public void The_palette_says_so_on_each_row_and_the_destructive_one_is_reviewed()
    {
        var scene = Create(new FixedTerraformProbe(Ready));
        var rows = DashboardRows(scene);

        PaletteItem Item(string verb) => new(Row(rows, Prefix + verb));

        Assert.Equal("Changes state", Item("plan").TierLabel);
        Assert.Equal("Changes state", Item("apply").TierLabel);
        Assert.Equal("Destructive", Item("destroy").TierLabel);
        Assert.All(new[] { "plan", "apply", "destroy" }, v => Assert.Equal("You review the exact command before it runs.", Item(v).RunNote));
        Assert.All(new[] { "plan", "apply", "destroy" }, v => Assert.Equal("Run", Item(v).RunLabel));
        Assert.Equal("defenseclaw setup splunk dashboards destroy --yes", Item("destroy").ArgvPreview);
        Assert.Equal("cli.setup.splunk.dashboards.apply", Row(rows, Prefix + "apply").Id);
        Assert.Contains("terraform", Row(rows, Prefix + "plan").Keywords, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ the gate

    [Fact]
    public void Before_the_first_look_the_rows_say_checking()
    {
        var scene = Create(new ManualTerraformProbe());

        var rows = DashboardRows(scene);

        Assert.Equal(3, rows.Count);
        foreach (var row in rows)
        {
            Assert.False(row.IsEnabled, row.Title);
            Assert.Equal(TerraformAvailability.CheckingReason, row.DisabledReason);
        }
    }

    [Theory]
    [InlineData(TerraformState.Ready, true)]
    [InlineData(TerraformState.Unknown, true)]
    [InlineData(TerraformState.NotInstalled, false)]
    [InlineData(TerraformState.TooOld, false)]
    [InlineData(TerraformState.NotWorking, false)]
    public async Task The_rows_follow_the_terraform_look_and_no_other_row_does(TerraformState state, bool open)
    {
        var registry = CuratedCommandCatalog.For(null).Commands;
        var scene = Create(new FixedTerraformProbe(new TerraformStatus(state, "The probe's own sentence.", string.Empty, string.Empty)));
        await scene.Services.Terraform.RefreshAsync();
        var reference = Create(new FixedTerraformProbe(Ready));
        await reference.Services.Terraform.RefreshAsync();

        var rows = DashboardRows(scene);

        foreach (var row in rows)
        {
            Assert.Equal(open, row.IsEnabled);
            var item = new PaletteItem(row);
            if (open)
            {
                Assert.Null(row.DisabledReason);
                Assert.Equal(row.Description, item.DetailLine);
            }
            else
            {
                // The row says why, in the look's own words and how to put it right, instead of its description; a screen reader hears it.
                Assert.StartsWith("The probe's own sentence.", row.DisabledReason, StringComparison.Ordinal);
                Assert.Contains("TERRAFORM_BIN", row.DisabledReason, StringComparison.Ordinal);
                Assert.Equal(row.DisabledReason, item.DetailLine);
                Assert.Contains("unavailable", item.AutomationName, StringComparison.Ordinal);
            }
        }

        // Whatever Terraform says changes nothing about a row that does not run it: the same as with a healthy Terraform.
        var registryRows = ShellCommandRegistry.BuildCliCommands(registry, scene.Actions);
        var baseline = ShellCommandRegistry.BuildCliCommands(registry, reference.Actions);
        Assert.Equal(baseline.Count, registryRows.Count);
        for (var i = 0; i < registryRows.Count; i++)
        {
            Assert.Equal(baseline[i].IsEnabled, registryRows[i].IsEnabled);
            Assert.Equal(baseline[i].DisabledReason, registryRows[i].DisabledReason);
        }
    }

    [Fact]
    public async Task Docker_and_terraform_gate_their_own_rows_independently()
    {
        var dockerOnly = Create(new FixedTerraformProbe(Ready));
        await dockerOnly.Services.Terraform.RefreshAsync();
        var registry = CuratedCommandCatalog.For(null).Commands;

        // Docker is "not installed" in an isolated composition: the stack's rows are closed, the dashboards' are open.
        var stack = ShellCommandRegistry.BuildCliCommands(registry, dockerOnly.Actions).Where(r => WizardWindowsPolicy.CommandNeedsDocker(r.Cli!.Argv)).ToArray();
        Assert.NotEmpty(stack);
        Assert.All(stack, r => Assert.False(r.IsEnabled));
        Assert.All(DashboardRows(dockerOnly), r => Assert.True(r.IsEnabled));
    }

    [Fact]
    public async Task A_closed_row_is_skipped_by_the_arrow_keys_and_cannot_be_chosen()
    {
        var scene = Create(new FixedTerraformProbe(new TerraformStatus(TerraformState.NotInstalled, "Terraform was not found on this machine's PATH.", string.Empty, "terraform")));
        await scene.Services.Terraform.RefreshAsync();

        var palette = new CommandPaletteViewModel();
        palette.Load(DashboardRows(scene));
        var chosen = new List<ShellCommand>();
        palette.CommandChosen += (_, command) => chosen.Add(command);

        var plan = palette.Results.Single(r => r.Title == Prefix + "plan");
        Assert.False(plan.IsEnabled);
        Assert.False(palette.Choose(plan));
        Assert.Empty(chosen);
        Assert.True(palette.Selected is null || palette.Selected.IsEnabled);
    }

    [Fact]
    public async Task The_look_is_asked_for_when_the_palette_opens_and_not_again_while_it_is_fresh()
    {
        var probe = new ManualTerraformProbe();
        var scene = Create(probe);

        // Nothing has asked yet: building the rows is only a reading of what is known.
        _ = DashboardRows(scene);
        _ = ShellCommandRegistry.Build(scene.Catalog, scene.Actions, _ => { }, () => { }, curated: scene.Actions.CliCommands);
        Assert.Equal(0, probe.Calls);

        var look = scene.Actions.CheckTerraform();
        await probe.WaitForCallsAsync(1);
        probe.Answer(Ready);
        await look;
        Assert.Equal(1, probe.Calls);

        await scene.Actions.CheckTerraform();
        Assert.Equal(1, probe.Calls);
    }

    // ------------------------------------------------------------------ the runs

    [Fact]
    public async Task Plan_is_reviewed_as_a_change_that_restarts_nothing_and_a_declined_review_runs_nothing()
    {
        var scene = Create(new FixedTerraformProbe(Ready));

        await scene.Actions.RunCuratedAsync(Command("plan"));

        var shown = Assert.Single(scene.Reviews);
        Assert.Equal("Run defenseclaw setup splunk dashboards plan?", shown.Title);
        Assert.Equal(CommandTier.StateChanging, shown.Tier);
        Assert.Equal(new[] { "setup", "splunk", "dashboards", "plan" }, Assert.Single(shown.Steps).Argv);
        Assert.Equal(SplunkDashboardsReview.Summary(Command("plan").Argv), shown.Summary);

        // It writes Terraform's files, never config.yaml, so there is no gateway restart for the general `setup` rule to announce.
        Assert.False(shown.RestartsGateway);
        Assert.Empty(shown.Warnings);
        Assert.Empty(scene.Toasts);
        Assert.Empty(scene.Services.Cli.Activity);
    }

    [Fact]
    public async Task Apply_is_reviewed_with_its_exact_command_and_says_it_goes_on_after_the_plan()
    {
        var scene = Create(new FixedTerraformProbe(Ready));

        await scene.Actions.RunCuratedAsync(Command("apply"));

        var shown = Assert.Single(scene.Reviews);
        Assert.Equal("Run defenseclaw setup splunk dashboards apply --yes?", shown.Title);
        Assert.Equal("defenseclaw setup splunk dashboards apply --yes", shown.CommandText);
        Assert.Equal(CommandTier.StateChanging, shown.Tier);
        Assert.False(shown.IsDestructive);
        Assert.Equal("Run command", shown.ConfirmLabel);
        Assert.False(shown.RestartsGateway);
        Assert.Equal(
            new[] { SplunkDashboardsReview.AppliesNowTitle, SplunkDashboardsReview.RemovesDetectorsTitle },
            shown.Warnings.Select(w => w.Title).ToArray());
        Assert.Empty(scene.Services.Cli.Activity);
    }

    [Fact]
    public async Task Destroy_is_reviewed_as_destructive_with_the_yes_on_the_command_and_what_it_deletes()
    {
        var scene = Create(new FixedTerraformProbe(Ready));

        await scene.Actions.RunCuratedAsync(Command("destroy"));

        var shown = Assert.Single(scene.Reviews);
        Assert.Equal(CommandTier.Destructive, shown.Tier);
        Assert.True(shown.IsDestructive);
        Assert.Equal("Run destructive command", shown.ConfirmLabel);
        Assert.Equal("defenseclaw setup splunk dashboards destroy --yes", shown.CommandText);
        Assert.Equal(new[] { "setup", "splunk", "dashboards", "destroy", "--yes" }, Assert.Single(shown.Steps).Argv);
        Assert.Equal(SplunkDashboardsReview.Summary(Command("destroy").Argv), shown.Summary);

        // The review is the question the CLI would have asked, and says so; it also says what goes.
        var deletes = Assert.Single(shown.Warnings, w => w.Title == SplunkDashboardsReview.DeletesTitle);
        Assert.Contains("which is why --yes is on the command", deletes.Message, StringComparison.Ordinal);
        Assert.False(shown.RestartsGateway);
        Assert.Empty(scene.Toasts);
        Assert.Empty(scene.Services.Cli.Activity);
    }

    [Theory]
    [InlineData("plan")]
    [InlineData("apply")]
    [InlineData("destroy")]
    public async Task A_row_cannot_ask_for_a_token_so_its_review_says_when_there_is_none(string verb)
    {
        var withoutToken = Create(new FixedTerraformProbe(Ready), token: CredentialPresence.NotSet);

        await withoutToken.Actions.RunCuratedAsync(Command(verb));

        var shown = Assert.Single(withoutToken.Reviews);
        var bar = Assert.Single(shown.Warnings, w => w.Title == SplunkDashboardsReview.NoTokenTitle);
        Assert.Contains("SFX_AUTH_TOKEN", bar.Message, StringComparison.Ordinal);
        Assert.Contains("Setup page", bar.Message, StringComparison.Ordinal);
        Assert.Equal(new[] { "SFX_AUTH_TOKEN" }, withoutToken.TokenChecks);

        foreach (var present in new[] { CredentialPresence.InEnvironment, CredentialPresence.InDotEnv, CredentialPresence.Unknown })
        {
            var scene = Create(new FixedTerraformProbe(Ready), token: present);
            await scene.Actions.RunCuratedAsync(Command(verb));
            Assert.DoesNotContain(Assert.Single(scene.Reviews).Warnings, w => w.Title == SplunkDashboardsReview.NoTokenTitle);
        }
    }

    [Fact]
    public async Task The_token_is_only_looked_for_by_name_for_a_dashboards_command_and_never_read()
    {
        var scene = Create(new FixedTerraformProbe(Ready));

        await scene.Actions.RunCuratedAsync(CuratedCommand.FromRegistry(TuiRegistryCatalogues.Baseline.Find("setup codex")!));

        Assert.Empty(scene.TokenChecks);
        Assert.Single(scene.Reviews);
    }

    [Fact]
    public async Task A_confirmed_review_goes_to_the_runner_which_here_has_no_cli_and_so_starts_nothing()
    {
        var scene = Create(new FixedTerraformProbe(Ready), confirm: true);

        await scene.Actions.RunCuratedAsync(Command("destroy"));

        _ = Assert.Single(scene.Reviews);
        Assert.Contains("Could not run it", Assert.Single(scene.Toasts), StringComparison.Ordinal);
        Assert.Empty(scene.Services.Cli.Activity);
    }

    [Fact]
    public async Task A_row_is_refused_before_any_review_if_a_credential_flag_were_ever_added_to_it()
    {
        var scene = Create(new FixedTerraformProbe(Ready), confirm: true);
        var bad = new CuratedCommand(
            Command("apply").Argv.Append("--o11y-api-token").ToArray(),
            "Setup",
            "x",
            string.Empty,
            Array.Empty<string>(),
            TuiName: "bad");

        await scene.Actions.RunCuratedAsync(bad);

        Assert.Empty(scene.Reviews);
        Assert.StartsWith("bad: Refused", Assert.Single(scene.Toasts), StringComparison.Ordinal);
        Assert.Empty(scene.Services.Cli.Activity);
    }

    // ------------------------------------------------------------------ a read-only installation (CUST-308)

    private static readonly TerraformStatus NoTerraform =
        new(TerraformState.NotInstalled, "Terraform was not found on this machine's PATH.", string.Empty, "terraform");

    [Theory]
    [InlineData("checking")]
    [InlineData("no")]
    [InlineData("yes")]
    public async Task On_a_read_only_installation_the_rows_are_off_with_the_installations_sentence_whatever_terraform_says(string look)
    {
        var probe = new ManualTerraformProbe();
        var scene = Create(probe, installation: TestInstallations.ManagedAt(_temp.Path));
        if (look != "checking")
        {
            var asking = scene.Services.Terraform.RefreshAsync();
            await probe.WaitForCallsAsync(1);
            probe.Answer(look == "yes" ? Ready : NoTerraform);
            await asking;
        }

        var rows = DashboardRows(scene);

        // One sentence per row, and it is the installation's: not "Checking for Terraform...", not "not found", and not an open row, which is
        // what a yes would have given on a writable installation.
        Assert.Equal(3, rows.Count);
        foreach (var row in rows)
        {
            Assert.False(row.IsEnabled, row.Title);
            Assert.Equal(TestInstallations.ManagedReason, row.DisabledReason);

            var item = new PaletteItem(row);
            Assert.Equal(TestInstallations.ManagedReason, item.DetailLine);
            Assert.Contains("unavailable", item.AutomationName, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task On_a_writable_installation_the_same_rows_say_terraforms_reason_and_never_the_installations()
    {
        var scene = Create(new FixedTerraformProbe(NoTerraform));
        await scene.Services.Terraform.RefreshAsync();

        foreach (var row in DashboardRows(scene))
        {
            Assert.False(row.IsEnabled, row.Title);
            Assert.StartsWith(NoTerraform.Summary, row.DisabledReason, StringComparison.Ordinal);
            Assert.DoesNotContain(TestInstallations.ManagedReason, row.DisabledReason, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task The_whole_palette_on_a_read_only_installation_turns_the_dashboards_rows_off_with_the_same_sentence_as_the_rest()
    {
        var scene = Create(new FixedTerraformProbe(Ready), installation: TestInstallations.ManagedAt(_temp.Path));
        await scene.Services.Terraform.RefreshAsync();

        // The whole palette: a change anywhere in it is off with the one sentence, the dashboards' three included.
        var all = ShellCommandRegistry.Build(scene.Catalog, scene.Actions, _ => { }, () => { }, curated: CuratedCommandCatalog.For(null).Commands);
        var dashboards = all.Where(c => c.Cli is { } cli && SplunkDashboards.IsCommand(cli.Argv, out _)).ToArray();

        Assert.Equal(3, dashboards.Length);
        Assert.All(dashboards, row =>
        {
            Assert.False(row.IsEnabled, row.Title);
            Assert.Equal(TestInstallations.ManagedReason, row.DisabledReason);
        });
        Assert.Equal(TestInstallations.ManagedReason, Assert.Single(all, c => c.Id == "gateway.restart").DisabledReason);
    }

    [Theory]
    [InlineData("plan")]
    [InlineData("apply")]
    [InlineData("destroy")]
    public async Task Running_a_row_on_a_read_only_installation_says_the_sentence_and_shows_no_review_and_starts_nothing(string verb)
    {
        var scene = Create(new FixedTerraformProbe(Ready), confirm: true, installation: TestInstallations.ManagedAt(_temp.Path));
        await scene.Services.Terraform.RefreshAsync();

        // The row is off; a Run that arrived another way (the detail pane, a remembered command) ends the same way.
        await scene.Actions.RunCuratedAsync(Command(verb));

        Assert.Equal(Command(verb).Title + ": " + TestInstallations.ManagedReason, Assert.Single(scene.Toasts));
        Assert.Empty(scene.Reviews);
        Assert.Empty(scene.TokenChecks);
        Assert.Empty(scene.Services.Cli.Activity);
    }

    [Fact]
    public async Task The_runner_refuses_a_dashboards_command_on_a_read_only_installation_whatever_drew_the_row_and_records_it()
    {
        var scene = Create(new FixedTerraformProbe(Ready), installation: TestInstallations.ManagedAt(_temp.Path));
        var argv = Command("destroy").Argv;

        // The runner is the guard whatever drew the row: it records the refusal and starts nothing.
        var refused = await scene.Services.Cli.RunAsync(argv);

        Assert.StartsWith(CliRunner.RefusedPrefix + " — ", refused.FailureReason, StringComparison.Ordinal);
        Assert.Contains(TestInstallations.ManagedReason, refused.FailureReason, StringComparison.Ordinal);
        Assert.Null(refused.ExitCode);
        Assert.Equal(argv, refused.Argv);
        Assert.Same(refused, Assert.Single(scene.Services.Cli.Activity));
    }

    [Fact]
    public void Copy_puts_the_exact_command_on_the_clipboard_and_nothing_secret_can_be_in_it()
    {
        var scene = Create(new FixedTerraformProbe(Ready));
        var copied = new List<string>();
        scene.Actions.ClipboardWriter = copied.Add;

        foreach (var row in SplunkDashboardsCommands.Rows)
        {
            scene.Actions.CopyCurated(row);
        }

        Assert.Equal(SplunkDashboardsCommands.Rows.Select(r => r.ClipboardText).ToArray(), copied);
        Assert.All(copied, text =>
        {
            Assert.StartsWith("defenseclaw setup splunk dashboards ", text, StringComparison.Ordinal);
            Assert.DoesNotContain("token", text, StringComparison.OrdinalIgnoreCase);
        });
    }
}

/// <summary>
/// The dashboard window and the dashboards' palette rows: it asks for the Terraform look when the palette opens, swaps the rows when the look
/// answers, and lets go of the look when it really closes. No process is started: the look is a fake that waits for the test to answer.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class SplunkDashboardsPaletteWindowTests : IDisposable
{
    private const string Apply = "setup splunk dashboards apply";

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

    private AppServices Create(ITerraformProbe probe, InstallationContext? installation = null)
    {
        var services = AppServices.CreateIsolated(
            TestServices.IsolatedPaths(_temp.Path, installation),
            claudeSettingsPath: _temp.File("claude-settings.json"),
            terraformProbe: probe);
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
        var probe = new ManualTerraformProbe();
        var services = Create(probe);
        MainWindow? window = null;
        CommandPaletteViewModel? palette = null;

        UiThread.Run(() =>
        {
            window = new MainWindow(services, new PanelCatalog(services), UnbuiltTray());
            palette = PaletteOf(window);

            // The way a panel's menu asks for the palette. Nobody has looked at Terraform yet, so the rows that need it say so.
            Assert.Equal(0, probe.Calls);
            services.Navigation.RequestPalette();
            var apply = Item(palette, Apply);
            Assert.False(apply.IsEnabled);
            Assert.Equal(TerraformAvailability.CheckingReason, apply.Command.DisabledReason);
        });

        try
        {
            // Opening the palette is what asked, once; the answer arrives on another thread and the open palette is rebuilt in place.
            await probe.WaitForCallsAsync(1);
            probe.Answer(new TerraformStatus(TerraformState.Ready, "Terraform 1.9.5 is available.", "1.9.5", @"C:\synthetic\bin\terraform.exe"));

            UiThread.WaitFor(() => Item(palette!, Apply).IsEnabled, "the dashboards' rows, enabled by the look");
            UiThread.Run(() =>
            {
                Assert.True(Item(palette!, "setup splunk dashboards plan").IsEnabled);
                Assert.True(Item(palette!, "setup splunk dashboards destroy").IsEnabled);
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
        var probe = new ManualTerraformProbe();
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
            probe.Answer(new TerraformStatus(TerraformState.NotInstalled, "Terraform was not found on this machine's PATH.", string.Empty, "terraform"));

            UiThread.WaitFor(() => Item(palette!, Apply).Command.DisabledReason?.StartsWith("Terraform was not found", StringComparison.Ordinal) == true, "the look's reason on the row");
            UiThread.Run(() =>
            {
                Assert.False(Item(palette!, Apply).IsEnabled);
                Assert.False(Item(palette!, "setup splunk dashboards plan").IsEnabled);

                // A row that does not run Terraform is not touched by it.
                Assert.True(Item(palette!, "doctor").IsEnabled);
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
    public async Task A_palette_opened_on_a_read_only_installation_before_terraform_has_answered_says_the_installations_sentence_not_checking()
    {
        var probe = new ManualTerraformProbe();
        var services = Create(probe, TestInstallations.ManagedAt(_temp.Path));
        MainWindow? window = null;

        UiThread.Run(() =>
        {
            window = new MainWindow(services, new PanelCatalog(services), UnbuiltTray());
            var palette = PaletteOf(window);
            services.Navigation.RequestPalette();

            // Nobody has looked at Terraform yet, and the rows do not say "Checking for Terraform...": the installation comes first.
            foreach (var verb in new[] { "plan", "apply", "destroy" })
            {
                var item = Item(palette, "setup splunk dashboards " + verb);
                Assert.False(item.IsEnabled, verb);
                Assert.Equal(TestInstallations.ManagedReason, item.Command.DisabledReason);
            }
        });

        try
        {
            // Opening the palette still asked for the look (the Setup card shares it); let it finish so nothing is left waiting.
            await probe.WaitForCallsAsync(1);
            probe.Answer(new TerraformStatus(TerraformState.Ready, "Terraform 1.9.5 is available.", "1.9.5", @"C:\synthetic\bin\terraform.exe"));
            await services.Terraform.EnsureFreshAsync();
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
    public async Task A_palette_opened_on_a_read_only_installation_after_terraform_said_yes_still_has_its_rows_off()
    {
        var services = Create(new FixedTerraformProbe(new TerraformStatus(TerraformState.Ready, "Terraform 1.9.5 is available.", "1.9.5", @"C:\synthetic\bin\terraform.exe")), TestInstallations.ManagedAt(_temp.Path));
        await services.Terraform.RefreshAsync();
        Assert.True(services.Terraform.Decision.IsAvailable);
        MainWindow? window = null;

        try
        {
            UiThread.Run(() =>
            {
                window = new MainWindow(services, new PanelCatalog(services), UnbuiltTray());
                var palette = PaletteOf(window);
                services.Navigation.RequestPalette();

                // Terraform is fine, and what the row would run is a change on a read-only installation: it says that, and is off.
                foreach (var verb in new[] { "plan", "apply", "destroy" })
                {
                    var item = Item(palette, "setup splunk dashboards " + verb);
                    Assert.False(item.IsEnabled, verb);
                    Assert.Equal(TestInstallations.ManagedReason, item.Command.DisabledReason);
                }
            });
        }
        finally
        {
            UiThread.Run(() =>
            {
                window?.AllowClose();
                window?.Close();
            });
        }
    }

    [Fact]
    public void A_window_listens_to_the_terraform_look_while_it_lives_and_stops_when_it_really_closes()
    {
        var services = Create(new ManualTerraformProbe());

        UiThread.Run(() =>
        {
            var before = Subscribers(services.Terraform, "Changed");
            var window = new MainWindow(services, new PanelCatalog(services), UnbuiltTray());
            Assert.Equal(before + 1, Subscribers(services.Terraform, "Changed"));

            window.AllowClose();
            window.Close();
            Assert.Equal(before, Subscribers(services.Terraform, "Changed"));
        });
    }
}
