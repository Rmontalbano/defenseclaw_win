using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels.Wizards;

namespace DefenseClaw.App.Tests.Wizards;

/// <summary>
/// The Splunk and Galileo guided first step: which cards, what each says and needs, how the flat flag list is regrouped per
/// pipeline, and that picking a card only drives the same flags (and the same argv) the plain wizard did. Synthetic help text and
/// a fake Docker probe throughout; nothing starts a process.
/// </summary>
public class WizardWalkthroughTests
{
    private static WizardStep Guide(WizardDefinition definition) =>
        definition.Steps.Single(s => s.Guide is not null);

    private static WizardValues Answers(WizardDefinition definition, params (string Id, string Value)[] answers)
    {
        var values = WizardSamples.StartingValues(definition);
        foreach (var (id, value) in answers)
        {
            values[id] = value;
        }

        return values;
    }

    // ------------------------------------------------------------------ the CLI's own words

    [Theory]
    [MemberData(nameof(LineEndings.Both), MemberType = typeof(LineEndings))]
    public void The_three_pipelines_are_read_out_of_the_help_text_whatever_the_line_ending(string eol)
    {
        var text = WizardWalkthroughs.PipelineText(LineEndings.With(WizardSamples.SplunkHelp, eol));

        Assert.Equal(new[] { "--enterprise", "--logs", "--o11y" }, text.Keys.Order().ToArray());
        Assert.Contains("Requires a Splunk access token", text["--o11y"], StringComparison.Ordinal);
        Assert.Contains("Requires Docker", text["--logs"], StringComparison.Ordinal);

        // "--enterprise" has its words on the following lines; they join into one run.
        Assert.StartsWith("Remote Splunk Enterprise HEC endpoint + token.", text["--enterprise"], StringComparison.Ordinal);
        Assert.Contains("unless --skip-test is set", text["--enterprise"], StringComparison.Ordinal);
    }

    [Fact]
    public void Option_lines_are_not_mistaken_for_pipelines()
    {
        var text = WizardWalkthroughs.PipelineText(WizardSamples.SplunkHelp);

        Assert.DoesNotContain("--realm", text.Keys);
        Assert.DoesNotContain("--index", text.Keys);
    }

    [Fact]
    public void A_description_is_the_prose_between_the_usage_line_and_the_options()
    {
        var text = WizardWalkthroughs.Description(WizardSamples.GalileoHelp);

        Assert.StartsWith("Configure Galileo OTLP trace export.", text, StringComparison.Ordinal);
        Assert.Contains("Galileo receives traces only", text, StringComparison.Ordinal);
        Assert.DoesNotContain("--deployment", text, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ splunk layout

    [Fact]
    public void Splunk_opens_on_a_guide_with_one_card_per_pipeline_each_driving_its_own_switch()
    {
        var definition = WizardSamples.Splunk();

        var guide = Guide(definition);
        Assert.Equal(definition.Steps[0], guide);
        Assert.Equal(new[] { "o11y", "logs", "enterprise" }, guide.Guide!.Cards.Select(c => c.FieldId).ToArray());
        Assert.Equal(new[] { "--o11y", "--logs", "--enterprise" }, guide.Fields.Select(f => f.Flag).ToArray());

        foreach (var card in guide.Guide.Cards)
        {
            Assert.NotEmpty(card.Needs);
            Assert.NotEmpty(card.Links);
            Assert.NotEmpty(card.WillDo);
            Assert.NotEmpty(card.CliSays); // quoted from the help, not curated
            Assert.All(card.Links, l => Assert.StartsWith("https://", l.Url, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void The_enterprise_card_says_it_sends_one_probe_and_how_to_skip_it_and_the_local_card_needs_docker()
    {
        var cards = Guide(WizardSamples.Splunk()).Guide!.Cards.ToDictionary(c => c.FieldId);

        Assert.Contains("one best-effort HEC probe", cards["enterprise"].WillDo, StringComparison.Ordinal);
        Assert.Contains("Skip test", cards["enterprise"].WillDo, StringComparison.Ordinal);
        Assert.Equal(WizardGuideRequirement.None, cards["enterprise"].Requires);
        Assert.Equal(WizardGuideRequirement.None, cards["o11y"].Requires);

        Assert.Equal(WizardGuideRequirement.Docker, cards["logs"].Requires);
        Assert.Contains(cards["logs"].Links, l => l.Url == WizardWalkthroughs.DockerInstallUrl);
        Assert.Contains(cards["logs"].Links, l => l.Url == WizardWalkthroughs.SplunkTermsUrl);
        Assert.Contains("Hyper-V", string.Join(' ', cards["logs"].Needs), StringComparison.Ordinal);
    }

    [Fact]
    public void Each_pipeline_has_a_page_of_its_own_gated_on_its_switch_and_the_shared_page_on_either_local_or_enterprise()
    {
        var definition = WizardSamples.Splunk();
        var page = definition.Steps.ToDictionary(s => s.Id);

        Assert.Equal("o11y", page["o11y"].VisibleWhenFieldId);
        Assert.Equal(new[] { "--realm", "--access-token", "--app-name", "--traces", "--metrics", "--logs-export" }, page["o11y"].Fields.Select(f => f.Flag).ToArray());

        Assert.Equal("logs", page["local"].VisibleWhenFieldId);
        Assert.Equal(new[] { "--accept-splunk-license", "--refresh-bundle" }, page["local"].Fields.Select(f => f.Flag).ToArray());

        Assert.Equal("enterprise", page["enterprise"].VisibleWhenFieldId);
        Assert.Equal(new[] { "--hec-endpoint", "--hec-token", "--skip-test" }, page["enterprise"].Fields.Select(f => f.Flag).ToArray());

        Assert.Equal("logs|enterprise", page["hec-destination"].VisibleWhenFieldId);
        Assert.Equal(new[] { "--index", "--source", "--sourcetype" }, page["hec-destination"].Fields.Select(f => f.Flag).ToArray());
    }

    [Fact]
    public void Every_flag_the_policy_leaves_is_still_offered_exactly_once_and_the_s3_and_credential_flags_stay_out()
    {
        var definition = WizardSamples.Splunk();
        var flags = definition.AllFields.Select(f => f.Flag).Where(f => f is not null).ToArray();

        Assert.Equal(flags.Length, flags.Distinct().Count());
        foreach (var kept in new[] { "--o11y", "--logs", "--enterprise", "--realm", "--access-token", "--hec-endpoint", "--hec-token", "--app-name", "--index", "--source", "--sourcetype", "--disable", "--skip-test", "--accept-splunk-license", "--refresh-bundle", "--non-interactive" })
        {
            Assert.Contains(kept, flags);
        }

        foreach (var gone in new[] { "--s3-export", "--s3-bucket", "--s3-prefix", "--aws-region", "--show-credentials" })
        {
            Assert.DoesNotContain(gone, flags);
        }

        // Whatever the layout did not place lands on a trailing page rather than vanishing.
        Assert.Contains(definition.Steps.Single(s => s.Id == "more-options").Fields, f => f.Flag == "--disable");
    }

    [Fact]
    public void Only_the_chosen_pipelines_fields_are_visible()
    {
        var definition = WizardSamples.Splunk();

        IEnumerable<string?> Visible(params (string, string)[] answers) =>
            definition.VisibleFields(Answers(definition, answers)).Select(f => f.Flag);

        var none = Visible().ToArray();
        Assert.DoesNotContain("--realm", none);
        Assert.DoesNotContain("--hec-endpoint", none);
        Assert.DoesNotContain("--accept-splunk-license", none);
        Assert.DoesNotContain("--index", none);

        var o11y = Visible(("o11y", ToggleValues.On)).ToArray();
        Assert.Contains("--realm", o11y);
        Assert.DoesNotContain("--hec-endpoint", o11y);
        Assert.DoesNotContain("--index", o11y);

        var local = Visible(("logs", ToggleValues.On)).ToArray();
        Assert.Contains("--accept-splunk-license", local);
        Assert.Contains("--index", local);
        Assert.DoesNotContain("--realm", local);
        Assert.DoesNotContain("--hec-token", local);

        var enterprise = Visible(("enterprise", ToggleValues.On)).ToArray();
        Assert.Contains("--hec-endpoint", enterprise);
        Assert.Contains("--index", enterprise); // shared with the local pipeline
        Assert.DoesNotContain("--accept-splunk-license", enterprise);
    }

    [Fact]
    public void Picking_cards_builds_the_same_argv_the_flags_always_did_and_never_a_secret()
    {
        var definition = WizardSamples.Splunk();
        var values = Answers(
            definition,
            ("o11y", ToggleValues.On),
            ("realm", "eu0"),
            ("logs", ToggleValues.On),
            ("accept-splunk-license", ToggleValues.On),
            ("enterprise", ToggleValues.On),
            ("hec-endpoint", "https://splunk.example.test:8088/services/collector/event"),
            ("skip-test", ToggleValues.On),
            ("index", "dc_test"));

        var argv = definition.BuildArgv(values);

        Assert.Equal(new[] { "setup", "splunk" }, argv.Take(2).ToArray());
        foreach (var expected in new[] { "--o11y", "--logs", "--enterprise", "--accept-splunk-license", "--skip-test" })
        {
            Assert.Contains(expected, argv);
        }

        Assert.Equal("eu0", argv[argv.ToList().IndexOf("--realm") + 1]);
        Assert.Equal("dc_test", argv[argv.ToList().IndexOf("--index") + 1]);
        Assert.DoesNotContain("--access-token", argv);
        Assert.DoesNotContain("--hec-token", argv);
        Assert.Equal(1, argv.Count(a => a == "--index"));
    }

    [Fact]
    public void The_secret_fields_keep_their_in_app_routes_after_the_regrouping()
    {
        var definition = WizardSamples.Splunk();

        var token = definition.AllFields.Single(f => f.Flag == "--access-token");
        var hec = definition.AllFields.Single(f => f.Flag == "--hec-token");

        Assert.Equal(WizardFieldKind.Secret, token.Kind);
        Assert.Equal("SPLUNK_ACCESS_TOKEN", token.Credential!.EnvName(new WizardValues()));
        Assert.Equal(WizardFieldKind.Secret, hec.Kind);
        Assert.NotNull(hec.Credential);
    }

    [Fact]
    public void A_cli_without_the_local_pipeline_gets_no_local_card_and_no_docker_check()
    {
        var steps = WizardWindowsPolicy.Filter(
            "splunk",
            new[]
            {
                new WizardStep
                {
                    Id = "page-1",
                    Title = "Guided",
                    Fields = new[]
                    {
                        new WizardField { Id = "o11y", Label = "O11y", Kind = WizardFieldKind.Switch, Flag = "--o11y" },
                        new WizardField { Id = "enterprise", Label = "Enterprise", Kind = WizardFieldKind.Switch, Flag = "--enterprise" },
                        new WizardField { Id = "realm", Label = "Realm", Kind = WizardFieldKind.Text, Flag = "--realm" },
                    },
                },
            });

        var laid = WizardWalkthroughs.Apply("splunk", steps, string.Empty);

        var cards = laid.Single(s => s.Guide is not null).Guide!.Cards;
        Assert.Equal(new[] { "o11y", "enterprise" }, cards.Select(c => c.FieldId).ToArray());
        Assert.DoesNotContain(cards, c => c.Requires == WizardGuideRequirement.Docker);
        Assert.All(cards, c => Assert.Equal(string.Empty, c.CliSays));
    }

    [Fact]
    public void A_cli_that_has_none_of_the_pipeline_switches_is_left_as_generated()
    {
        var steps = new[] { new WizardStep { Id = "page-1", Title = "x", Fields = new[] { new WizardField { Id = "a", Label = "a", Kind = WizardFieldKind.Text, Flag = "--a" } } } };

        Assert.Same(steps, WizardWalkthroughs.Apply("splunk", steps, string.Empty));
        Assert.Same(steps, WizardWalkthroughs.Apply("claude-code", steps, string.Empty));
    }

    [Fact]
    public void An_any_of_gate_is_open_when_one_of_its_fields_holds_a_value()
    {
        var values = new WizardValues();
        var values2 = new[] { ToggleValues.On };

        Assert.False(WizardDefinition.IsVisible("logs|enterprise", values2, values));

        values["enterprise"] = ToggleValues.On;
        Assert.True(WizardDefinition.IsVisible("logs|enterprise", values2, values));

        values["enterprise"] = ToggleValues.Off;
        values["logs"] = ToggleValues.On;
        Assert.True(WizardDefinition.IsVisible("logs|enterprise", values2, values));
    }

    // ------------------------------------------------------------------ galileo

    [Fact]
    public void Galileo_gets_a_what_you_need_page_first_and_keeps_its_key_route_and_ids()
    {
        var definition = WizardSamples.Galileo();

        var first = definition.Steps[0];
        Assert.Equal(WizardWalkthroughs.GuideStepId, first.Id);
        Assert.Empty(first.Fields);
        var card = Assert.Single(first.Guide!.Cards);
        Assert.False(card.IsChoice);
        Assert.Contains("GALILEO_API_KEY", string.Join(' ', card.Needs), StringComparison.Ordinal);
        Assert.Contains("never a command-line flag", string.Join(' ', card.Needs), StringComparison.Ordinal);
        Assert.Contains("Galileo receives traces only", card.CliSays, StringComparison.Ordinal);
        Assert.NotEmpty(card.Links);

        // The existing route: a flagless secret and the persist switch, untouched.
        Assert.Equal(WizardFieldKind.Secret, definition.AllFields.Single(f => f.Id == WizardSyntheticSecrets.GalileoKeyFieldId).Kind);
        Assert.Contains(definition.AllFields, f => f.Flag == "--persist-api-key");
    }

    [Fact]
    public void Galileo_project_and_log_stream_are_explained_and_the_flags_are_unchanged()
    {
        var definition = WizardSamples.Galileo();

        var project = definition.AllFields.Single(f => f.Id == "project");
        var stream = definition.AllFields.Single(f => f.Id == "logstream");
        var console = definition.AllFields.Single(f => f.Id == "console-url");

        Assert.Equal("--project", project.Flag);
        Assert.Contains("Galileo project the traces are filed under", project.Help, StringComparison.Ordinal);
        Assert.Equal("--logstream", stream.Flag);
        Assert.Contains("\"default\"", stream.Help, StringComparison.Ordinal);
        Assert.Contains("self-hosted", console.Label, StringComparison.OrdinalIgnoreCase);

        var values = Answers(definition, ("project", "p1"), ("logstream", "l1"));
        Assert.Equal(new[] { "setup", "galileo" }, definition.BuildArgv(values).Take(2).ToArray());
        Assert.Contains("--project", definition.BuildArgv(values));
    }
}

/// <summary>
/// The Docker probe: what a read-only look at <c>docker compose version</c> and <c>docker info</c> says, and that it never asks for anything
/// else.
/// </summary>
public class DockerProbeTests
{
    private const string LinuxKitInfo = "{\"OSType\":\"linux\",\"OperatingSystem\":\"Docker Desktop\",\"KernelVersion\":\"5.15.0-linuxkit\"}";

    private sealed class Rig
    {
        public string? Docker { get; set; } = @"C:\Program Files\Docker\Docker\resources\bin\docker.exe";

        /// <summary>The answer to <c>docker info</c>.</summary>
        public DockerProcessResult Result { get; set; } = new(0, LinuxKitInfo, string.Empty, false);

        /// <summary>The answer to <c>docker compose version</c>.</summary>
        public DockerProcessResult Compose { get; set; } = new(0, "Docker Compose version v2.29.1", string.Empty, false);

        public Exception? Throws { get; set; }

        public string Edition { get; set; } = "Enterprise";

        public bool? Wsl { get; set; } = false;

        public bool X64 { get; set; } = true;

        public List<string[]> Runs { get; } = new();

        public DockerProbe Probe => new(
            () => Task.FromResult(Docker),
            (exe, args, _, _) =>
            {
                Runs.Add(args.ToArray());
                var answer = args[0] == "compose" ? Compose : Result;
                return Throws is null ? Task.FromResult(answer) : Task.FromException<DockerProcessResult>(Throws);
            },
            () => Edition,
            () => Wsl,
            () => new[] { @"C:\Users\someone\AppData\Local", @"C:\Users\someone" },
            () => X64);
    }

    [Fact]
    public async Task No_docker_executable_is_not_installed_and_nothing_is_run()
    {
        var rig = new Rig { Docker = null };

        var status = await rig.Probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(DockerState.NotInstalled, status.State);
        Assert.False(status.AllowsLocalSplunk);
        Assert.Empty(rig.Runs);
    }

    [Fact]
    public async Task A_stopped_engine_is_engine_down_with_the_clis_words_and_still_only_asks_the_two_read_only_questions()
    {
        var rig = new Rig { Result = new DockerProcessResult(1, string.Empty, "failed to connect to the docker API at npipe\n", false) };

        var status = await rig.Probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(DockerState.EngineDown, status.State);
        Assert.False(status.AllowsLocalSplunk);
        Assert.False(status.AllowsLocalStack);
        Assert.Contains("not running", status.Summary, StringComparison.Ordinal);
        Assert.Contains("failed to connect", status.Summary, StringComparison.Ordinal);

        // The CLI's own preflight order: Compose v2 first (a client-side plugin, which answers with the engine stopped), then the engine.
        Assert.Equal(2, rig.Runs.Count);
        Assert.Equal(new[] { "compose", "version" }, rig.Runs[0]);
        Assert.Equal(new[] { "info", "--format", "{{json .}}" }, rig.Runs[1]);
    }

    [Fact]
    public async Task A_missing_compose_plugin_is_compose_missing_and_the_engine_is_never_asked()
    {
        var rig = new Rig { Compose = new DockerProcessResult(1, string.Empty, "docker: 'compose' is not a docker command.\nSee 'docker --help'\n", false) };

        var status = await rig.Probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(DockerState.ComposeMissing, status.State);
        Assert.False(status.AllowsLocalStack);
        Assert.False(status.AllowsLocalSplunk);
        Assert.Contains("Compose v2", status.Summary, StringComparison.Ordinal);
        Assert.Contains("is not a docker command", status.Summary, StringComparison.Ordinal);
        Assert.Empty(status.Warnings);
        Assert.Equal(new[] { "compose", "version" }, Assert.Single(rig.Runs));
    }

    [Fact]
    public async Task A_compose_plugin_that_does_not_answer_is_compose_missing_too()
    {
        var rig = new Rig { Compose = new DockerProcessResult(-1, string.Empty, string.Empty, true) };

        var status = await rig.Probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(DockerState.ComposeMissing, status.State);
        Assert.Contains("did not answer", status.Summary, StringComparison.Ordinal);
        Assert.Single(rig.Runs);
    }

    [Fact]
    public async Task Only_two_read_only_commands_are_ever_run()
    {
        var rig = new Rig();

        _ = await rig.Probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(
            new[] { "compose version", "info --format {{json .}}" },
            rig.Runs.Select(r => string.Join(' ', r)).ToArray());
    }

    [Fact]
    public async Task A_client_only_answer_without_a_server_is_engine_down()
    {
        var rig = new Rig { Result = new DockerProcessResult(0, "{\"ClientInfo\":{}}", string.Empty, false) };

        Assert.Equal(DockerState.EngineDown, (await rig.Probe.ProbeAsync(CancellationToken.None)).State);
    }

    [Fact]
    public async Task A_timeout_is_engine_down()
    {
        var rig = new Rig { Result = new DockerProcessResult(-1, string.Empty, string.Empty, true) };

        var status = await rig.Probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(DockerState.EngineDown, status.State);
        Assert.Contains("did not answer", status.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_running_hyper_v_docker_desktop_is_ready_with_nothing_to_warn_about()
    {
        var status = await new Rig().Probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(DockerState.Ready, status.State);
        Assert.True(status.AllowsLocalSplunk);
        Assert.Empty(status.Warnings);
    }

    [Fact]
    public async Task Everything_the_clis_certified_path_refuses_is_a_warning_not_a_block()
    {
        var rig = new Rig
        {
            Docker = @"C:\Users\someone\AppData\Local\Docker\docker.exe",
            Result = new DockerProcessResult(0, "{\"OSType\":\"windows\",\"OperatingSystem\":\"Moby\",\"KernelVersion\":\"5.15.153.1-microsoft-standard-WSL2\"}", string.Empty, false),
            Edition = "Core",
            Wsl = true,
            X64 = false,
        };

        var status = await rig.Probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(DockerState.Ready, status.State);
        Assert.True(status.AllowsLocalSplunk);
        var all = string.Join('\n', status.Warnings);
        Assert.Contains("Windows containers", all, StringComparison.Ordinal);
        Assert.Contains("x64", all, StringComparison.Ordinal);
        Assert.Contains("Core", all, StringComparison.Ordinal);
        Assert.Contains("per-user", all, StringComparison.Ordinal);
        Assert.Contains("WSL 2", all, StringComparison.Ordinal);
        Assert.Contains("Docker Desktop", all, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unreadable_backend_setting_is_a_warning_unless_the_kernel_says_linuxkit()
    {
        var rig = new Rig { Wsl = null, Result = new DockerProcessResult(0, "{\"OSType\":\"linux\",\"OperatingSystem\":\"Docker Desktop\",\"KernelVersion\":\"6.1.0\"}", string.Empty, false) };
        Assert.Contains("Hyper-V backend could not be verified", string.Join(' ', (await rig.Probe.ProbeAsync(CancellationToken.None)).Warnings), StringComparison.Ordinal);

        rig = new Rig { Wsl = null };
        Assert.Empty((await rig.Probe.ProbeAsync(CancellationToken.None)).Warnings);
    }

    [Fact]
    public async Task A_probe_that_blows_up_says_unknown_and_does_not_block()
    {
        var rig = new Rig { Throws = new InvalidOperationException("boom") };

        var status = await rig.Probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(DockerState.Unknown, status.State);
        Assert.True(status.AllowsLocalSplunk);
        Assert.Contains("boom", status.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_is_not_swallowed()
    {
        var rig = new Rig { Throws = new OperationCanceledException() };

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Probe.ProbeAsync(CancellationToken.None));
    }
}

/// <summary>The wizard window's view-model around the guide: disabled-with-reason, the caution, and driving the flags.</summary>
[Collection(UiCollection.Name)]
public class WizardGuideViewModelTests
{
    private sealed class FakeProbe : IDockerProbe
    {
        private TaskCompletionSource<DockerStatus> _pending = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Calls { get; private set; }

        public Task<DockerStatus> ProbeAsync(CancellationToken cancellationToken)
        {
            Calls++;
            _pending = new TaskCompletionSource<DockerStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
            return _pending.Task;
        }

        public void Answer(DockerStatus status) => _pending.TrySetResult(status);
    }

    private sealed class Scene : IDisposable
    {
        private readonly TempDirectory _temp = new();
        private readonly AppServices _services;

        public Scene(FakeProbe probe)
        {
            Probe = probe;
            _services = TestServices.Create(_temp);
            ViewModel = UiThread.Run(() => new WizardViewModel(_services, WizardSamples.Splunk(), probe));
        }

        public FakeProbe Probe { get; }

        public WizardViewModel ViewModel { get; }

        public WizardGuideCardViewModel Card(string id) =>
            ViewModel.Steps.Single(s => s.Guide is not null).Guide!.Cards.Single(c => c.Card.FieldId == id);

        public WizardFieldViewModel Field(string id) => ViewModel.Steps.SelectMany(s => s.Fields).Single(f => f.Id == id);

        public void Dispose()
        {
            UiThread.Run(() => ViewModel.Dispose());
            _services.Dispose();
            _temp.Dispose();
        }
    }

    private static readonly DockerStatus Ready = new(DockerState.Ready, "Docker is running.", Array.Empty<string>());

    [Fact]
    public void The_local_card_is_disabled_while_docker_is_being_checked_and_the_others_never_are()
    {
        using var scene = new Scene(new FakeProbe());

        UiThread.Run(() =>
        {
            var logs = scene.Card("logs");
            Assert.False(logs.IsAvailable);
            Assert.True(logs.IsChecking);
            Assert.Contains("Checking", logs.UnavailableReason, StringComparison.Ordinal);

            Assert.True(scene.Card("o11y").IsAvailable);
            Assert.True(scene.Card("enterprise").IsAvailable);
            Assert.Equal(1, scene.Probe.Calls);
        });
    }

    [Fact]
    public void No_docker_keeps_the_local_card_off_with_the_reason_and_a_pick_cannot_turn_it_on()
    {
        using var scene = new Scene(new FakeProbe());
        scene.Probe.Answer(new DockerStatus(DockerState.NotInstalled, "Docker was not found on this machine's PATH.", Array.Empty<string>()));

        UiThread.WaitFor(() => !scene.Card("logs").IsChecking, "the probe answer");
        UiThread.Run(() =>
        {
            var logs = scene.Card("logs");
            Assert.False(logs.IsAvailable);
            Assert.True(logs.HasUnavailableReason);
            Assert.Contains("not found", logs.UnavailableReason, StringComparison.Ordinal);
            Assert.Contains("install or start Docker Desktop", logs.UnavailableReason, StringComparison.Ordinal);

            logs.IsSelected = true;

            Assert.False(logs.IsSelected);
            Assert.False(scene.Field("logs").IsOn);
        });
    }

    [Fact]
    public void A_missing_compose_plugin_keeps_the_local_card_off_with_the_probes_reason_like_a_missing_engine_does()
    {
        using var scene = new Scene(new FakeProbe());
        scene.Probe.Answer(new DockerStatus(
            DockerState.ComposeMissing,
            "Docker Compose v2 is not available (docker compose version failed). Install or enable the Compose plugin; Docker Desktop includes it.",
            Array.Empty<string>()));

        UiThread.WaitFor(() => !scene.Card("logs").IsChecking, "the probe answer");
        UiThread.Run(() =>
        {
            var logs = scene.Card("logs");
            Assert.False(logs.IsAvailable);
            Assert.StartsWith("Docker Compose v2 is not available", logs.UnavailableReason, StringComparison.Ordinal);

            logs.IsSelected = true;

            Assert.False(logs.IsSelected);
            Assert.False(scene.Field("logs").IsOn);
        });
    }

    [Fact]
    public void A_running_engine_enables_the_card_with_the_container_caution_and_its_warnings()
    {
        using var scene = new Scene(new FakeProbe());
        scene.Probe.Answer(new DockerStatus(DockerState.Ready, "Docker is running.", new[] { "Docker Desktop is using the WSL 2 backend." }));

        UiThread.WaitFor(() => scene.Card("logs").IsAvailable, "the probe answer");
        UiThread.Run(() =>
        {
            var logs = scene.Card("logs");
            Assert.True(logs.HasCaution);
            Assert.Contains("starts containers", logs.Caution, StringComparison.Ordinal);
            Assert.Contains("Splunk in Free mode", logs.Caution, StringComparison.Ordinal);
            Assert.Contains("Hyper-V", logs.Caution, StringComparison.Ordinal);
            Assert.Contains("WSL 2 backend", logs.Caution, StringComparison.Ordinal);

            logs.IsSelected = true;
            Assert.True(scene.Field("logs").IsOn);
        });
    }

    [Fact]
    public void Checking_again_after_docker_was_started_turns_the_card_on_and_a_stopped_engine_turns_a_chosen_card_off()
    {
        using var scene = new Scene(new FakeProbe());
        scene.Probe.Answer(new DockerStatus(DockerState.EngineDown, "Docker is installed but its engine is not running.", Array.Empty<string>()));
        UiThread.WaitFor(() => !scene.Card("logs").IsChecking, "first answer");

        UiThread.Run(() =>
        {
            Assert.False(scene.Card("logs").IsAvailable);
            scene.Card("logs").CheckAgainCommand.Execute(null);
            Assert.Equal(2, scene.Probe.Calls);
        });

        scene.Probe.Answer(Ready);
        UiThread.WaitFor(() => scene.Card("logs").IsAvailable, "second answer");

        UiThread.Run(() =>
        {
            scene.Card("logs").IsSelected = true;
            Assert.True(scene.Field("logs").IsOn);
            scene.Card("logs").CheckAgainCommand.Execute(null);
        });

        scene.Probe.Answer(new DockerStatus(DockerState.EngineDown, "Docker is installed but its engine is not running.", Array.Empty<string>()));
        UiThread.WaitFor(() => !scene.Card("logs").IsAvailable && !scene.Card("logs").IsChecking, "third answer");
        UiThread.Run(() => Assert.False(scene.Field("logs").IsOn));
    }

    [Fact]
    public void A_probe_that_could_not_run_does_not_lock_the_option()
    {
        using var scene = new Scene(new FakeProbe());
        scene.Probe.Answer(new DockerStatus(DockerState.Unknown, "Docker could not be checked from here (x).", Array.Empty<string>()));

        UiThread.WaitFor(() => scene.Card("logs").IsAvailable, "the probe answer");
    }

    [Fact]
    public void Choosing_nothing_stops_the_guide_page_and_choosing_a_card_adds_only_its_pages()
    {
        using var scene = new Scene(new FakeProbe());

        UiThread.Run(() =>
        {
            var vm = scene.ViewModel;
            Assert.Equal("Choose what to send", vm.CurrentStep!.Title);

            vm.Next();
            Assert.True(vm.HasValidationSummary);
            Assert.Contains("Choose at least one", vm.ValidationSummary, StringComparison.Ordinal);
            Assert.Equal(0, vm.PageIndex);

            scene.Card("enterprise").IsSelected = true;
            vm.Next();
            Assert.False(vm.HasValidationSummary, vm.ValidationSummary);
            Assert.Equal("Splunk Enterprise (HEC)", vm.CurrentStep!.Title);

            // Only the enterprise pages: no observability or local page in between.
            var titles = new List<string> { vm.CurrentStep.Title };
            while (true)
            {
                vm.Next();
                if (vm.IsReview || vm.HasValidationSummary)
                {
                    break;
                }

                titles.Add(vm.CurrentStep!.Title);
            }

            Assert.DoesNotContain("Splunk Observability Cloud", titles);
            Assert.DoesNotContain("Local Splunk (Docker)", titles);
            Assert.Contains("HEC index, source and sourcetype", titles);
        });
    }

    [Fact]
    public void The_last_page_reports_what_a_chosen_pipeline_is_missing_and_the_review_refuses_to_run()
    {
        using var scene = new Scene(new FakeProbe());

        UiThread.Run(() =>
        {
            var vm = scene.ViewModel;
            scene.Card("enterprise").IsSelected = true;

            while (!vm.IsReview && !vm.HasValidationSummary)
            {
                vm.Next();
            }

            // No HEC endpoint typed: the problem is raised leaving the last page, naming the field.
            Assert.True(vm.HasValidationSummary);
            Assert.Contains("HEC endpoint", vm.ValidationSummary, StringComparison.Ordinal);

            scene.Field("hec-endpoint").Value = "https://splunk.example.test:8088/services/collector/event";
            vm.Next();
            Assert.True(vm.IsReview, vm.ValidationSummary);
            Assert.False(vm.HasReviewProblem);
            Assert.Equal("setup", vm.CommandReview!.Steps[0].Argv[0]);
            Assert.Contains("--enterprise", vm.CommandReview.Steps[0].Argv);
            Assert.DoesNotContain("--hec-token", vm.CommandReview.Steps[0].Argv);
        });
    }
}
