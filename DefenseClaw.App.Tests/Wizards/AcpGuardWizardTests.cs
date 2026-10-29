using System.Text.Json;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.ViewModels.Wizards;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Gateway;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.Tests.Wizards;

/// <summary>
/// CUST-294: the ACP Guard wizard. One argv builder for a standard setup and a managed enrollment (golden argv in the Mac's order), a token named only by
/// its path, the tile gated on the runtime's ACP guard and opening the wizard, the editor / agent / profile lists read from the CLI's help and the gateway
/// (synthetic 1.0.4 fixtures) with a quiet fallback when the gateway does not have them, and a review that runs nothing until Execute.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class AcpGuardWizardTests
{
    private const string RuntimeDir = @"C:\Users\operator\.defenseclaw";

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "runtime-1.0.4", "acp", name));

    private static HelpProbeResult Help(string eol = LineEndings.Lf) => new(LineEndings.With(Fixture("acp-setup-help.synthetic.txt"), eol), null);

    private static GatewayResult<JsonDocument> Json(string name) => GatewayResult<JsonDocument>.Ok(JsonDocument.Parse(Fixture(name)));

    private static GatewayResult<JsonDocument> NotFound() => GatewayResult<JsonDocument>.Error("gateway returned HTTP 404", 404);

    private static AcpGuardContext Full(string eol = LineEndings.Lf) => AcpGuardDiscovery.From(
        Help(eol),
        AcpGuardReader.Catalog(Json("acp-catalog.synthetic.json")),
        AcpGuardReader.Profiles(Json("acp-profiles.synthetic.json")));

    private static WizardField Field(WizardDefinition definition, string id) => definition.AllFields.Single(f => f.Id == id);

    // ------------------------------------------------------------------ the one argv builder

    public static IEnumerable<object[]> Golden() => new[]
    {
        new object[]
        {
            new AcpGuardRequest("zed", "kiro", "default", Activate: false),
            new[] { "acp", "setup", "--client", "zed", "--agent", "kiro", "--profile", "default" },
        },
        new object[]
        {
            new AcpGuardRequest("jetbrains", "copilot", "locked", Activate: true),
            new[] { "acp", "setup", "--client", "jetbrains", "--agent", "copilot", "--profile", "locked", "--activate" },
        },
        new object[]
        {
            new AcpGuardRequest("zed", "devin", "watch", Activate: false, Managed: true, RuntimeDir, RuntimeDir + @"\acp\zed-devin.token"),
            new[] { "acp", "setup", "--client", "zed", "--agent", "devin", "--profile", "watch", "--managed", "--runtime-data-dir", RuntimeDir, "--token-file", RuntimeDir + @"\acp\zed-devin.token" },
        },
        new object[]
        {
            new AcpGuardRequest("zed", "kiro", "locked", Activate: true, Managed: true, RuntimeDir, RuntimeDir + @"\acp\zed-kiro.token"),
            new[] { "acp", "setup", "--client", "zed", "--agent", "kiro", "--profile", "locked", "--activate", "--managed", "--runtime-data-dir", RuntimeDir, "--token-file", RuntimeDir + @"\acp\zed-kiro.token" },
        },
    };

    [Theory]
    [MemberData(nameof(Golden))]
    public void Standard_and_managed_observe_and_action_share_one_argv_in_the_Macs_order(AcpGuardRequest request, string[] expected)
    {
        Assert.Equal(expected, AcpGuardArgv.Build(request));
        Assert.Null(AcpGuardArgv.Problem(request, managedAllowed: true));

        // The wizard's command is the same builder over the answers.
        var definition = AcpGuardWizard.Build(Full(), managedInstallation: true);
        var values = new WizardValues();
        values[AcpGuardWizard.ClientField] = request.Client;
        values[AcpGuardWizard.AgentField] = request.Agent;
        values[AcpGuardWizard.ProfileField] = request.Profile is "default" or "locked" ? request.Profile : AcpGuardWizard.AnotherProfile;
        values[AcpGuardWizard.NewProfileField] = request.Profile;
        values[AcpGuardWizard.ActivateField] = request.Activate ? ToggleValues.On : ToggleValues.Off;
        values[AcpGuardWizard.ManagedField] = request.Managed ? ToggleValues.On : ToggleValues.Off;
        values[AcpGuardWizard.RuntimeDataDirField] = request.RuntimeDataDirectory;
        values[AcpGuardWizard.TokenFileField] = request.TokenFile;
        Assert.Equal(expected, definition.BuildArgv(values));
        Assert.Null(definition.CrossCheck(values));
    }

    [Fact]
    public void The_profile_is_always_named_and_a_blank_one_is_the_CLIs_default()
    {
        Assert.Equal(
            new[] { "acp", "setup", "--client", "zed", "--agent", "kiro", "--profile", "default" },
            AcpGuardArgv.Build(new AcpGuardRequest(" zed ", "kiro", "  ", Activate: false)));

        // Paths typed for a managed enrollment are not sent while the switch is off.
        var values = new WizardValues();
        values[AcpGuardWizard.ClientField] = "zed";
        values[AcpGuardWizard.AgentField] = "kiro";
        values[AcpGuardWizard.ProfileField] = "default";
        values[AcpGuardWizard.ManagedField] = ToggleValues.Off;
        values[AcpGuardWizard.RuntimeDataDirField] = RuntimeDir;
        values[AcpGuardWizard.TokenFileField] = RuntimeDir + @"\acp\zed-kiro.token";
        Assert.DoesNotContain("--runtime-data-dir", AcpGuardWizard.Build(Full(), managedInstallation: true).BuildArgv(values));
    }

    // ------------------------------------------------------------------ the token is a path, never a value

    [Theory]
    [InlineData("synthetic-token-0123456789abcdefABCDEF_-")]
    [InlineData("acp\\zed-kiro.token")]
    [InlineData("")]
    public void A_managed_enrollment_takes_the_token_file_by_its_full_path_and_refuses_anything_else(string tokenFile)
    {
        var request = new AcpGuardRequest("zed", "kiro", "default", Activate: false, Managed: true, RuntimeDir, tokenFile);

        var problem = AcpGuardArgv.Problem(request, managedAllowed: true);
        Assert.NotNull(problem);
        Assert.Contains("never the token itself", problem, StringComparison.Ordinal);

        // The wizard will not take it to the review's Execute either.
        var definition = AcpGuardWizard.Build(Full(), managedInstallation: true);
        var values = Answers(managed: true, tokenFile: tokenFile);
        Assert.Equal(problem, definition.CrossCheck(values));
    }

    [Fact]
    public void The_token_file_must_be_where_the_CLI_requires_it_and_its_contents_are_never_read_or_sent()
    {
        using var temp = new TempDirectory();
        var dir = temp.Path;
        var token = Path.Combine(dir, "acp", "zed-kiro.token");
        _ = Directory.CreateDirectory(Path.GetDirectoryName(token)!);
        File.WriteAllText(token, "synthetic-token-value-never-on-argv");

        var wrong = new AcpGuardRequest("zed", "kiro", "default", Activate: false, Managed: true, dir, Path.Combine(dir, "acp", "zed-devin.token"));
        Assert.Equal(
            "The CLI accepts the managed token only at " + token + " (<runtime-data-dir>\\acp\\<client>-<agent>.token).",
            AcpGuardArgv.Problem(wrong, managedAllowed: true));

        var right = wrong with { TokenFile = token.ToUpperInvariant() }; // Windows paths compare without case
        Assert.Null(AcpGuardArgv.Problem(right, managedAllowed: true));
        var argv = AcpGuardArgv.Build(right);
        Assert.Contains(token.ToUpperInvariant(), argv);
        Assert.DoesNotContain(argv, a => a.Contains("synthetic-token-value", StringComparison.Ordinal));
        Assert.Equal(Path.Combine(dir, "acp", "zed-kiro.token"), AcpGuardArgv.ExpectedTokenFile(dir, "zed", "kiro"));

        // The request has nowhere to put a token value: its only text members are ids and paths.
        Assert.Equal(
            new[] { "Agent", "Client", "Profile", "RuntimeDataDirectory", "TokenFile" },
            typeof(AcpGuardRequest).GetProperties().Where(p => p.PropertyType == typeof(string)).Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void On_a_per_user_installation_managed_enrollment_keeps_its_place_disabled_with_the_reason()
    {
        var definition = AcpGuardWizard.Build(Full(), managedInstallation: false);
        var managed = Field(definition, AcpGuardWizard.ManagedField);
        Assert.Equal(AcpGuardArgv.ManagedNeedsManagedInstallation, managed.DisabledReason);
        Assert.Equal(ToggleValues.Off, managed.DefaultValue);

        // Even forced on, the command is refused before the review can run it: the CLI would refuse --managed here.
        Assert.Equal(AcpGuardArgv.ManagedNeedsManagedInstallation, definition.CrossCheck(Answers(managed: true, tokenFile: RuntimeDir + @"\acp\zed-kiro.token")));

        // On a managed installation it is offered, and starts on.
        var onManaged = Field(AcpGuardWizard.Build(Full(), managedInstallation: true), AcpGuardWizard.ManagedField);
        Assert.Equal(string.Empty, onManaged.DisabledReason);
        Assert.Equal(ToggleValues.On, onManaged.DefaultValue);

        // A CLI whose acp setup has no --managed cannot enroll, wherever it runs.
        var older = AcpGuardDiscovery.From(
            new HelpProbeResult(LineEndings.Normalize(Fixture("acp-setup-help.synthetic.txt")).Replace("  --managed ", "  --unmanaged ", StringComparison.Ordinal), null),
            AcpRead<AcpCatalog>.Unsupported,
            AcpRead<AcpProfiles>.Unsupported);
        Assert.False(older.HasManagedFlags);
        Assert.Contains("no --managed", Field(AcpGuardWizard.Build(older, managedInstallation: true), AcpGuardWizard.ManagedField).DisabledReason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Default")]
    [InlineData("--activate")]
    [InlineData("a b")]
    public void A_profile_name_the_runtime_would_refuse_is_caught_before_the_review(string profile)
    {
        var problem = AcpGuardArgv.Problem(new AcpGuardRequest("zed", "kiro", profile, Activate: false), managedAllowed: false);
        Assert.NotNull(problem);
        Assert.Contains("profile name", problem, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ what the wizard offers, and where it came from

    [Theory]
    [MemberData(nameof(LineEndings.Both), MemberType = typeof(LineEndings))]
    public void With_the_CLI_and_the_gateway_the_choices_are_what_acp_setup_accepts_named_by_the_catalog(string eol)
    {
        var context = Full(eol);

        Assert.Equal(new[] { "jetbrains", "zed" }, context.Clients.Select(c => c.Value));
        Assert.Equal("Zed (zed) · tested", context.Clients[1].Label);
        Assert.Equal(new[] { "copilot", "cursor", "devin", "hermes", "kiro", "opencode" }, context.Agents.Select(c => c.Value));
        Assert.Equal("Kiro (kiro) · tested · runs kiro-cli", context.Agents.Single(a => a.Value == "kiro").Label);
        Assert.StartsWith("Editors and agents: what this CLI's acp setup accepts, named and rated", context.ChoiceNote, StringComparison.Ordinal);
        Assert.True(context.HasManagedFlags);
        Assert.True(context.HasProfileChoices);
        Assert.Equal("default", context.StartingProfile);

        var definition = AcpGuardWizard.Build(context, managedInstallation: false);
        var profile = Field(definition, AcpGuardWizard.ProfileField);
        Assert.Equal(WizardFieldKind.Choice, profile.Kind);
        Assert.Equal(new[] { "default", "locked", AcpGuardWizard.AnotherProfile }, profile.Choices.Select(c => c.Value));
        Assert.Equal("locked — action, fails closed (editors: zed; agents: devin)", profile.Choices[1].Label);
        Assert.Equal("default", profile.DefaultValue);
        Assert.Equal(WizardFieldKind.Choice, Field(definition, AcpGuardWizard.ClientField).Kind);
        Assert.Equal(string.Empty, Field(definition, AcpGuardWizard.ClientField).DefaultValue); // nothing chosen for the operator
        Assert.Equal(string.Empty, Field(definition, AcpGuardWizard.AgentField).DefaultValue);
        Assert.Equal(context.HelpText, definition.HelpText);
        Assert.Equal("acp setup", definition.DisplayCommand);
    }

    [Fact]
    public void A_gateway_without_the_routes_falls_back_to_the_CLIs_choices_and_a_typed_profile_with_no_error()
    {
        var context = AcpGuardDiscovery.From(Help(), AcpGuardReader.Catalog(NotFound()), AcpGuardReader.Profiles(GatewayResult<JsonDocument>.Error("method not allowed", 405)));

        Assert.Equal(new[] { "jetbrains", "zed" }, context.Clients.Select(c => c.Label));
        Assert.Null(context.Profiles);
        Assert.Contains("this gateway does not list them", context.ProfileNote, StringComparison.Ordinal);
        Assert.Equal("Editors and agents: what this CLI's acp setup accepts.", context.ChoiceNote);

        var definition = AcpGuardWizard.Build(context, managedInstallation: false);
        var profile = Field(definition, AcpGuardWizard.ProfileField);
        Assert.Equal(WizardFieldKind.Text, profile.Kind);
        Assert.Equal("default", profile.DefaultValue);
        Assert.Equal(string.Empty, definition.BaselineWarning); // the "could not be read" bar stays shut
        Assert.DoesNotContain("error", definition.BaselineNote, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Without_the_help_the_gateways_catalog_names_the_choices_and_without_either_they_are_typed()
    {
        var catalogOnly = AcpGuardDiscovery.From(
            new HelpProbeResult(string.Empty, "defenseclaw is not on PATH or in the installer's bin directory."),
            AcpGuardReader.Catalog(Json("acp-catalog.synthetic.json")),
            AcpGuardReader.Profiles(GatewayResult<JsonDocument>.Unreachable()));
        Assert.Equal(new[] { "zed", "jetbrains" }, catalogOnly.Clients.Select(c => c.Value));
        Assert.Equal(string.Empty, catalogOnly.HelpText);
        Assert.Contains("not read from the gateway (The gateway is not running)", catalogOnly.ProfileNote, StringComparison.Ordinal);
        Assert.Contains("the gateway's catalog (the CLI's help could not be read)", catalogOnly.ChoiceNote, StringComparison.Ordinal);

        var neither = AcpGuardDiscovery.From(new HelpProbeResult(string.Empty, "exit 2"), AcpRead<AcpCatalog>.Unsupported, AcpRead<AcpProfiles>.Unsupported);
        var definition = AcpGuardWizard.Build(neither, managedInstallation: false);
        Assert.Equal(WizardFieldKind.Text, Field(definition, AcpGuardWizard.ClientField).Kind);
        Assert.Equal(WizardFieldKind.Text, Field(definition, AcpGuardWizard.AgentField).Kind);
        Assert.True(Field(definition, AcpGuardWizard.ClientField).IsRequired);
        Assert.Equal("Choose the editor (client) that runs the agent.", definition.CrossCheck(new WizardValues()));
    }

    [Fact]
    public void A_catalog_entry_this_CLI_does_not_accept_is_named_and_not_offered()
    {
        var catalog = AcpGuardReader.Catalog(GatewayResult<JsonDocument>.Ok(JsonDocument.Parse(
            """{"clients":[{"id":"zed","name":"Zed","support":"tested"},{"id":"neovim","name":"Neovim","support":"cataloged"}],"agents":[{"id":"kiro","name":"Kiro"}]}""")));
        var context = AcpGuardDiscovery.From(Help(), catalog, AcpRead<AcpProfiles>.Unsupported);

        Assert.DoesNotContain(context.Clients, c => c.Value == "neovim");
        Assert.Contains("also lists the editor neovim", context.ChoiceNote, StringComparison.Ordinal);
        Assert.DoesNotContain("agent ", context.ChoiceNote.Split("also lists")[1], StringComparison.Ordinal);
        Assert.Equal("jetbrains", context.Clients[0].Label); // the catalog had no name for it: the id
    }

    [Fact]
    public async Task Discovery_reads_one_help_screen_and_two_gateway_routes_and_nothing_else()
    {
        var helps = new List<string>();
        var routes = new List<string>();
        var context = await AcpGuardDiscovery.ReadAsync(
            (path, _) =>
            {
                lock (helps)
                {
                    helps.Add(string.Join(' ', path));
                }

                return Task.FromResult(Help());
            },
            (route, max, _) =>
            {
                lock (routes)
                {
                    routes.Add(route);
                }

                Assert.Equal(AcpGuardReader.MaxBytes, max);
                return Task.FromResult(route == AcpGuardReader.CatalogRoute ? Json("acp-catalog.synthetic.json") : Json("acp-profiles.synthetic.json"));
            });

        Assert.Equal(new[] { "acp setup" }, helps);
        Assert.Equal(new[] { "v1/acp/catalog", "v1/acp/profiles" }, routes.Order(StringComparer.Ordinal));
        Assert.True(context.HasProfileChoices);
    }

    // ------------------------------------------------------------------ the tile

    [Fact]
    public async Task The_Setup_tile_opens_the_wizard_on_a_runtime_with_the_guard_and_not_on_0_8_10()
    {
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp, runtimeProbeRunner: RuntimeFixtureRunner.For("95159fd"));
        _ = await services.Runtime.RefreshAsync(force: true);
        var opened = 0;
        var vm = UiThread.Run(() =>
        {
            var hub = new SetupPanelViewModel(services);
            hub.ShowCards(Array.Empty<WizardDefinition>());
            hub.AcpGuardOpener = () =>
            {
                opened++;
                return Task.CompletedTask;
            };
            return hub;
        });

        var tile = vm.Areas.Single(a => a.Id == "acp-guard");
        Assert.True(tile.CanOpen);
        await UiThread.Run(() => vm.OpenAreaCommand.ExecuteAsync(tile));
        Assert.Equal(1, opened);

        // 0.8.10 has no acp command: the tile keeps its slot, says why, and pressing it does nothing.
        using var oldTemp = new TempDirectory();
        using var old = TestServices.Create(oldTemp, runtimeProbeRunner: RuntimeFixtureRunner.For("0.8.10"));
        _ = await old.Runtime.RefreshAsync(force: true);
        Assert.False(old.Runtime.Capabilities.Has(RuntimeCapability.AcpGuard));
        var oldHub = UiThread.Run(() =>
        {
            var hub = new SetupPanelViewModel(old);
            hub.ShowCards(Array.Empty<WizardDefinition>());
            hub.AcpGuardOpener = () =>
            {
                opened++;
                return Task.CompletedTask;
            };
            return hub;
        });
        var closed = oldHub.Areas.Single(a => a.Id == "acp-guard");
        Assert.False(closed.CanOpen);
        Assert.False(closed.IsComing);
        Assert.Equal(old.Runtime.Check(RuntimeCapability.AcpGuard).Reason ?? RuntimeCapabilityCatalog.UnsupportedMessage, closed.Reason);
        await UiThread.Run(() => oldHub.OpenAreaCommand.ExecuteAsync(closed));
        Assert.Equal(1, opened);
    }

    [Fact]
    public async Task On_a_managed_installation_the_tile_still_opens_and_says_the_review_runs_nothing()
    {
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp, runtimeProbeRunner: RuntimeFixtureRunner.For("95159fd"), installation: TestInstallations.Managed());
        _ = await services.Runtime.RefreshAsync(force: true);
        var vm = UiThread.Run(() =>
        {
            var hub = new SetupPanelViewModel(services);
            hub.ShowCards(Array.Empty<WizardDefinition>());
            return hub;
        });

        var tile = vm.Areas.Single(a => a.Id == "acp-guard");
        Assert.True(tile.CanOpen);
        Assert.Contains(SetupAreas.AcpGuardReadOnlyNote, tile.TileToolTip, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ the review

    [Fact]
    public void Opening_the_wizard_and_cancelling_its_review_runs_nothing()
    {
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp);
        var started = 0;
        services.Cli.InvocationStarted += (_, _) => Interlocked.Increment(ref started);

        UiThread.Run(() =>
        {
            using var vm = new WizardViewModel(services, AcpGuardWizard.Build(Full(), services.Installation.Context.IsManaged));
            Assert.Equal("acp setup", vm.CommandName);
            Assert.Single(vm.Steps);

            // Next without a pair: the page says what is missing and stays.
            vm.Next();
            Assert.False(vm.IsReview);
            Assert.True(vm.HasValidationSummary);

            Set(vm, AcpGuardWizard.ClientField, "zed");
            Set(vm, AcpGuardWizard.AgentField, "kiro");
            vm.Next();
            Assert.True(vm.IsReview);
            Assert.Equal(
                new[] { "acp", "setup", "--client", "zed", "--agent", "kiro", "--profile", "default" },
                vm.CommandReview!.Steps.Single().Argv);
            Assert.Equal(CommandTier.StateChanging, vm.CommandReview.Tier);
            Assert.False(vm.CommandReview.RestartsGateway);
            Assert.True(vm.CanExecute);
            Assert.DoesNotContain(vm.CommandReview.Warnings, w => w.Title == AcpGuardReview.ActionModeTitle);
            Assert.Contains(vm.CommandReview.Warnings, w => w.Title == AcpGuardReview.EditorFileTitle && w.Message.Contains(@"%APPDATA%\Zed\settings.json", StringComparison.Ordinal));
            Assert.StartsWith("Routes this editor and agent through the ACP guard in observe mode", vm.ChangeSummary, StringComparison.Ordinal);

            var closed = false;
            vm.CloseRequested += (_, _) => closed = true;
            vm.CancelCommand.Execute(null);
            Assert.True(closed);
        });

        Assert.Equal(0, Volatile.Read(ref started));
    }

    [Fact]
    public void Action_mode_carries_the_blocking_caution_and_a_managed_installation_reviews_without_running()
    {
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp, installation: TestInstallations.Managed());
        var started = 0;
        services.Cli.InvocationStarted += (_, _) => Interlocked.Increment(ref started);

        UiThread.Run(() =>
        {
            using var vm = new WizardViewModel(services, AcpGuardWizard.Build(Full(), services.Installation.Context.IsManaged));
            Set(vm, AcpGuardWizard.ClientField, "zed");
            Set(vm, AcpGuardWizard.AgentField, "kiro");
            Set(vm, AcpGuardWizard.ProfileField, "locked");
            Set(vm, AcpGuardWizard.ActivateField, ToggleValues.On);
            Set(vm, AcpGuardWizard.RuntimeDataDirField, RuntimeDir);
            Set(vm, AcpGuardWizard.TokenFileField, RuntimeDir + @"\acp\zed-kiro.token");
            vm.Next();

            Assert.True(vm.IsReview);
            Assert.False(vm.HasReviewProblem, vm.ReviewProblem);
            var review = vm.CommandReview!;
            Assert.Contains("--managed", review.Steps.Single().Argv);
            Assert.Contains(review.Warnings, w => w.Title == AcpGuardReview.ActionModeTitle && w.Message.Contains("blocks", StringComparison.Ordinal));
            Assert.Contains(review.Warnings, w => w.Title == AcpGuardReview.ManagedTitle);
            Assert.True(review.IsBlocked);
            Assert.Equal(TestInstallations.ManagedReason, vm.InstallationBlockedReason);
            Assert.False(vm.CanExecute);

            // Execute is a no-op when it cannot run.
            vm.ExecuteCommand.Execute(null);
            Assert.False(vm.HasRun);
        });

        Assert.Equal(0, Volatile.Read(ref started));
    }

    [Fact]
    public void The_cautions_are_judged_on_the_argv_and_say_nothing_about_other_commands()
    {
        Assert.Empty(AcpGuardReview.Warnings(new[] { "setup", "acp", "--activate" })); // setup acp is acp adopt, not this wizard's command
        Assert.Null(AcpGuardReview.Summary(new[] { "setup", "llm" }));

        // --activate as the value of --profile is a profile name, not action mode.
        var oddProfile = AcpGuardReview.Warnings(new[] { "acp", "setup", "--client", "jetbrains", "--agent", "kiro", "--profile", "--activate" });
        Assert.DoesNotContain(oddProfile, w => w.Title == AcpGuardReview.ActionModeTitle);
        Assert.Contains(oddProfile, w => w.Message.Contains(@"%USERPROFILE%\.jetbrains\acp.json", StringComparison.Ordinal));

        // The wizard framework's own bar list carries them.
        Assert.Contains(WizardCautions.For(new[] { "acp", "setup", "--client", "zed", "--agent", "kiro", "--profile", "default", "--activate" }), w => w.Title == AcpGuardReview.ActionModeTitle);
    }

    private static WizardValues Answers(bool managed, string tokenFile)
    {
        var values = new WizardValues();
        values[AcpGuardWizard.ClientField] = "zed";
        values[AcpGuardWizard.AgentField] = "kiro";
        values[AcpGuardWizard.ProfileField] = "default";
        values[AcpGuardWizard.ActivateField] = ToggleValues.Off;
        values[AcpGuardWizard.ManagedField] = managed ? ToggleValues.On : ToggleValues.Off;
        values[AcpGuardWizard.RuntimeDataDirField] = RuntimeDir;
        values[AcpGuardWizard.TokenFileField] = tokenFile;
        return values;
    }

    private static void Set(WizardViewModel vm, string id, string value) => vm.Steps.SelectMany(s => s.Fields).Single(f => f.Id == id).Value = value;
}
