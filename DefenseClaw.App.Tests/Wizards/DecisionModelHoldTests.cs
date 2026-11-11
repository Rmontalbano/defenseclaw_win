using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Paths;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.Tests.Wizards;

/// <summary>
/// The <c>setup decision-model</c> card (CUST-446, CUST-423 before it). DefenseClaw 1.0.6 added the command and the app held its card back; 1.0.7
/// settled it (<see cref="RuntimeCapability.DecisionModelCascade"/>) and 1.0.8 is identical, so the card is offered exactly on a runtime that
/// has the capability. A runtime without it (1.0.6 and earlier, one not probed yet, a catalog told nothing) gets what it always got: no card,
/// no probe, no search hit. Every other target, and the read-only config section and doctor row, are untouched either way.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class DecisionModelHoldTests
{
    /// <summary>A <c>setup --help</c> with the decision-model target among ordinary ones, a preview connector among the connectors.</summary>
    private static readonly string TopHelp = LineEndings.Normalize("""
        Usage: defenseclaw setup [OPTIONS] COMMAND [ARGS]...

          Configure DefenseClaw.

        Options:
          --help  Show this message and exit.

        Commands:
          amp             Configure DefenseClaw for Amp.
          claude-code     Configure DefenseClaw hooks for Claude Code.
          decision-model  Configure the unified decision model (decision_model:).
          llm             Configure the unified LLM provider block.
          opencode        OpenCode: preview on windows.
        """);

    /// <summary>
    /// A 1.0.7-shaped <c>setup decision-model --help</c>, written from the command's options in Click's layout (synthetic: no live output).
    /// 1.0.8 has the same options.
    /// </summary>
    private static readonly string DecisionModelHelp = LineEndings.Normalize("""
        Usage: defenseclaw setup decision-model [OPTIONS]

          Configure the unified decision model (decision_model:).

          A small, fast model that speaks the System One protocol. Off by default.

        Options:
          --show                          Print the current decision model settings and exit.
          --enable / --disable            Turn the decision model on or off.
          --model TEXT                    Model name the System One endpoint serves (sent in each request).
          --base-url TEXT                 Full endpoint URL, e.g. http://127.0.0.1:8791/v1/systemone. https anywhere, http only to loopback.
          --api-key-env TEXT              Env var holding the bearer token (default DEFENSECLAW_DECISION_MODEL_KEY; local servers need none).
          --timeout FLOAT                 Seconds one call may take (default 1; never more than a quarter of the judge's time).
          --allow-threshold FLOAT         Risk at or below this skips the judge (default 0.30).
          --block-threshold FLOAT         Risk at or above this is a confident block (default 0.75).
          --on-confident-block [escalate|decide]
                                          escalate (default): the judge still checks a confident block. decide: the block stands without the judge.
          --connectors TEXT               Connectors the decision model answers on its own where the LLM judge does not run: comma-separated names, or * for all. An empty string clears it.
          --ping / --no-ping              After saving, send one benign request to check the endpoint.
          --non-interactive, --accept-defaults  Use flags instead of prompts.
          --help                          Show this message and exit.
        """);

    private const string RuntimeHelp = "Usage: defenseclaw [OPTIONS] COMMAND [ARGS]...\n\nCommands:\n  setup  Configure.\n";

    private static readonly string[] OtherTargets = { "amp", "claude-code", "llm", "opencode" };

    /// <summary>What a probe of a runtime of <paramref name="version"/> says it can do (synthetic version screen, the same one the capability tests use).</summary>
    private static RuntimeCapabilities Runtime(string version) =>
        RuntimeProbe.Evaluate(
            new RuntimeProbeScreens("{\"name\":\"defenseclaw-cli\",\"version\":\"" + version + "\",\"schema_version\":1}", RuntimeHelp, RuntimeHelp, null, null, null, null, null),
            "test",
            "fp",
            DateTimeOffset.UnixEpoch).Capabilities;

    private static WizardCatalog CatalogOver(Func<RuntimeCapabilities?>? capabilities, Action<string>? onHelp = null)
    {
        var paths = new DefenseClawPaths(binDirectory: @"C:\fake\bin", searchPath: Array.Empty<string>(), fileExists: _ => true);
        return new WizardCatalog(
            new SetupHelpProbe(paths, diskCache: null, (_, path, _) =>
            {
                var key = string.Join(' ', path);
                onHelp?.Invoke(key);
                var text = key switch
                {
                    "" => TopHelp,
                    "claude-code" => WizardSamples.ClaudeCodeHelp,
                    "llm" => WizardSamples.LlmHelp,
                    "opencode" => PreviewStatusTests.OpenCodeHelp,
                    "decision-model" => DecisionModelHelp,
                    _ => null,
                };

                return Task.FromResult(text is null ? new HelpProbeResult(string.Empty, "defenseclaw exited 2.") : new HelpProbeResult(text, null));
            }),
            capabilities);
    }

    // ------------------------------------------------------------------ held: 1.0.6 and earlier, not probed, no source

    public static TheoryData<string> HeldRuntimes() => new() { "none", "unknown", "1.0.5", "1.0.6" };

    private static Func<RuntimeCapabilities?>? Source(string kind) => kind switch
    {
        "none" => null,
        "unknown" => () => RuntimeCapabilities.Unknown,
        _ => () => Runtime(kind),
    };

    [Theory]
    [MemberData(nameof(HeldRuntimes))]
    public async Task The_decision_model_card_is_not_in_the_catalog_and_the_others_are(string runtime)
    {
        var catalog = CatalogOver(Source(runtime));

        var roster = await catalog.LoadAsync();

        Assert.DoesNotContain(roster, d => d.Target == "decision-model");
        Assert.Null(catalog.Find("decision-model"));
        Assert.Equal(OtherTargets, roster.Select(d => d.Target).OrderBy(t => t, StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(HeldRuntimes))]
    public async Task The_held_target_is_never_probed_even_when_every_card_is_warmed(string runtime)
    {
        var probed = new List<string>();
        var catalog = CatalogOver(Source(runtime), path => { lock (probed) { probed.Add(path); } });

        _ = await catalog.LoadAsync();
        await catalog.RefreshAfterCliChangeAsync();
        _ = await catalog.EnsureDetailAsync("llm");

        lock (probed)
        {
            Assert.DoesNotContain("decision-model", probed);
        }
    }

    [Theory]
    [MemberData(nameof(HeldRuntimes))]
    public async Task The_hubs_search_and_cards_do_not_find_it_on_a_runtime_without_it(string runtime)
    {
        var catalog = CatalogOver(Source(runtime));
        var roster = await catalog.LoadAsync();
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp);

        UiThread.Run(() =>
        {
            var vm = new SetupPanelViewModel(services);
            vm.ShowCards(roster);

            vm.SearchText = "decision";
            Assert.Empty(vm.Groups.SelectMany(g => g.Cards));

            vm.SearchText = "llm";
            Assert.Contains(vm.Groups.SelectMany(g => g.Cards), c => c.Target == "llm");

            vm.SearchText = string.Empty;
            Assert.Equal(roster.Count, vm.Groups.SelectMany(g => g.Cards).Count());
        });
    }

    [Fact]
    public async Task A_preview_connector_beside_it_keeps_its_status_through_the_catalog()
    {
        var catalog = CatalogOver(Source("1.0.6"));

        var roster = await catalog.LoadAsync();
        Assert.Equal(PlatformStatus.Preview, roster.Single(d => d.Target == "opencode").PlatformStatus);

        var detail = await catalog.EnsureDetailAsync("opencode");
        Assert.Equal(PlatformStatus.Preview, detail.PlatformStatus);
        Assert.Equal(PreviewStatusTests.PreviewNote, detail.PlatformNote);
        Assert.True(detail.IsDetailLoaded);
        Assert.Null(detail.DetailError);
    }

    [Fact]
    public void The_rule_holds_the_card_unless_the_runtime_has_the_capability_and_nothing_else()
    {
        Assert.True(DecisionModelCard.IsHeld("decision-model", null));
        Assert.True(DecisionModelCard.IsHeld("decision-model", RuntimeCapabilities.Unknown));
        Assert.True(DecisionModelCard.IsHeld("decision-model", Runtime("1.0.6")));
        Assert.False(DecisionModelCard.IsHeld("decision-model", Runtime("1.0.7")));
        Assert.False(DecisionModelCard.IsHeld("decision-model", Runtime("1.0.8")));

        foreach (var target in new[] { "llm", "guardrail", "claude-code" })
        {
            Assert.False(DecisionModelCard.IsHeld(target, null));
            Assert.False(DecisionModelCard.IsHeld(target, Runtime("1.0.6")));
        }
    }

    [Fact]
    public void The_command_palettes_registry_has_no_decision_model_entry_so_it_needs_no_filter()
    {
        // The palette is fed by the generated TUI registry, not by the wizard catalog; DefenseClaw 1.0.6's own registry_data.py lists no
        // decision-model command either. A regeneration that adds one fails here, which is the moment to decide whether the palette holds it too.
        foreach (var catalogue in new[] { TuiRegistryCatalogues.Baseline, TuiRegistryCatalogues.Extended, TuiRegistryCatalogues.ExtendedWithoutRemovedConnectors })
        {
            Assert.DoesNotContain(catalogue.Entries, e => e.Argv.Contains("decision-model"));
        }
    }

    // ------------------------------------------------------------------ shown: 1.0.7 and 1.0.8

    [Theory]
    [InlineData("1.0.7")]
    [InlineData("1.0.8")]
    public async Task The_card_is_in_the_roster_under_guardrail_and_policy_with_its_title(string version)
    {
        var catalog = CatalogOver(Source(version));

        var roster = await catalog.LoadAsync();

        var card = roster.Single(d => d.Target == "decision-model");
        Assert.Equal(WizardGroups.GuardrailAndPolicy, card.Group);
        Assert.Equal("Decision model", card.Title);
        Assert.Same(card, catalog.Find("decision-model"));
        Assert.Equal(OtherTargets.Append("decision-model").Order(StringComparer.Ordinal), roster.Select(d => d.Target).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("1.0.7")]
    [InlineData("1.0.8")]
    public async Task The_detail_loads_the_curated_pages_with_every_flag_placed(string version)
    {
        var catalog = CatalogOver(Source(version));
        _ = await catalog.LoadAsync();

        var detail = await catalog.EnsureDetailAsync("decision-model");

        Assert.True(detail.IsDetailLoaded);
        Assert.Null(detail.DetailError);
        Assert.True(detail.IsCurated);
        Assert.Equal(
            new[] { "decision-model", "decision-model-connectors", "decision-model-thresholds", "decision-model-apply" },
            detail.Steps.Select(s => s.Id));

        // Nothing is left for a "More options" page, so --show and --non-interactive are placed where they are meant to be.
        var flags = detail.AllFields.Select(f => f.Flag).OfType<string>().Distinct().Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(
            new[]
            {
                "--allow-threshold", "--api-key-env", "--base-url", "--block-threshold", "--connectors", "--enable", "--model", "--non-interactive",
                "--on-confident-block", "--ping", "--show", "--timeout",
            },
            flags);
    }

    [Fact]
    public async Task The_hubs_search_finds_it_on_a_runtime_that_has_it()
    {
        var catalog = CatalogOver(Source("1.0.7"));
        var roster = await catalog.LoadAsync();
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp);

        UiThread.Run(() =>
        {
            var vm = new SetupPanelViewModel(services);
            vm.ShowCards(roster);

            vm.SearchText = "decision";
            Assert.Contains(vm.Groups.SelectMany(g => g.Cards), c => c.Target == "decision-model");
        });
    }

    // ------------------------------------------------------------------ the runtime changes under the catalog

    [Fact]
    public async Task A_probe_that_brings_the_capability_brings_the_card_in_and_one_that_takes_it_away_removes_it()
    {
        var current = Runtime("1.0.6");
        var topReads = 0;
        var catalog = CatalogOver(() => current, path => { if (path.Length == 0) { Interlocked.Increment(ref topReads); } });

        var roster = await catalog.LoadAsync();
        Assert.DoesNotContain(roster, d => d.Target == "decision-model");

        // Before anything changes, being told again is not a reason to read the CLI again.
        var reads = Volatile.Read(ref topReads);
        await catalog.ObserveRuntimeAsync();
        Assert.Equal(reads, Volatile.Read(ref topReads));

        current = Runtime("1.0.7");
        await catalog.ObserveRuntimeAsync();
        Assert.NotNull(catalog.Find("decision-model"));

        // 1.0.7 to 1.0.8 does not change the answer: no new read.
        reads = Volatile.Read(ref topReads);
        current = Runtime("1.0.8");
        await catalog.ObserveRuntimeAsync();
        Assert.Equal(reads, Volatile.Read(ref topReads));
        Assert.NotNull(catalog.Find("decision-model"));

        current = Runtime("1.0.6");
        await catalog.ObserveRuntimeAsync();
        Assert.Null(catalog.Find("decision-model"));
    }

    [Fact]
    public async Task A_runtime_change_before_the_first_load_reads_nothing()
    {
        var topReads = 0;
        var catalog = CatalogOver(() => Runtime("1.0.7"), path => { if (path.Length == 0) { Interlocked.Increment(ref topReads); } });

        await catalog.ObserveRuntimeAsync();

        Assert.Equal(0, Volatile.Read(ref topReads));
    }

    // ------------------------------------------------------------------ the command the wizard builds

    private static WizardDefinition Definition()
    {
        var help = SetupHelpParser.Parse(DecisionModelHelp);
        var (steps, curated) = WizardStepFactory.Build("decision-model", help);
        steps = WizardWindowsPolicy.Filter("decision-model", steps);
        steps = SecretRoutes.Annotate("decision-model", steps);

        return new WizardDefinition
        {
            Target = "decision-model",
            Title = "Decision model",
            Group = WizardGroups.GuardrailAndPolicy,
            Description = help.Summary,
            Steps = steps,
            PlatformStatus = help.PlatformStatus,
            IsCurated = curated,
            IsDetailLoaded = true,
        };
    }

    private static IReadOnlyList<string> Argv(Action<WizardValues>? answer = null)
    {
        var definition = Definition();
        var values = WizardSamples.StartingValues(definition);
        answer?.Invoke(values);
        return definition.BuildArgv(values);
    }

    [Fact]
    public void A_wizard_left_alone_sends_only_the_flag_that_stops_the_prompts()
    {
        Assert.Equal(new[] { "setup", "decision-model", "--non-interactive" }, Argv());
    }

    [Fact]
    public void Non_interactive_is_on_and_cannot_be_turned_off_in_the_window()
    {
        var field = Definition().AllFields.Single(f => f.Flag == "--non-interactive");

        Assert.Equal(ToggleValues.On, field.DefaultValue);
        Assert.NotEmpty(field.DisabledReason);

        // Whatever else is answered, it is there.
        Assert.Contains("--non-interactive", Argv(v => { v["connectors-mode"] = "clear"; v["enable"] = "false"; }));
    }

    [Fact]
    public void Leaving_the_connector_list_alone_sends_no_connectors_flag()
    {
        var argv = Argv(v => { v["connectors-mode"] = "keep"; v["connectors"] = "codex"; });

        Assert.DoesNotContain("--connectors", argv);
    }

    [Fact]
    public void All_connectors_sends_a_star()
    {
        Assert.Equal(new[] { "setup", "decision-model", "--connectors", "*", "--non-interactive" }, Argv(v => v["connectors-mode"] = "all"));
    }

    [Fact]
    public void Clearing_the_list_sends_the_flag_with_an_empty_value()
    {
        var argv = Argv(v => v["connectors-mode"] = "clear");

        Assert.Equal(new[] { "setup", "decision-model", "--connectors", string.Empty, "--non-interactive" }, argv);
    }

    [Fact]
    public void Naming_connectors_sends_the_names_and_naming_none_sends_nothing()
    {
        Assert.Equal(
            new[] { "setup", "decision-model", "--connectors", "claudecode,codex", "--non-interactive" },
            Argv(v => { v["connectors-mode"] = "only"; v["connectors"] = " claudecode,codex "; }));

        Assert.DoesNotContain("--connectors", Argv(v => v["connectors-mode"] = "only"));
    }

    [Fact]
    public void Only_the_chosen_answer_sends_the_flag_so_it_is_never_there_twice()
    {
        foreach (var mode in new[] { "keep", "all", "only", "clear" })
        {
            var argv = Argv(v => { v["connectors-mode"] = mode; v["connectors"] = "codex"; });
            Assert.True(argv.Count(a => a == "--connectors") <= 1, mode);
        }
    }

    [Fact]
    public void The_other_answers_become_their_flags()
    {
        var argv = Argv(v =>
        {
            v["enable"] = ToggleValues.On;
            v["base-url"] = "http://127.0.0.1:8791/v1/systemone";
            v["model"] = "small-1";
            v["api-key-env"] = "MY_DECISION_KEY";
            v["timeout"] = "0.5";
            v["allow-threshold"] = "0.2";
            v["block-threshold"] = "0.8";
            v["on-confident-block"] = "decide";
            v["ping"] = ToggleValues.On;
        });

        Assert.Equal(
            new[]
            {
                "setup", "decision-model", "--enable", "--base-url", "http://127.0.0.1:8791/v1/systemone", "--model", "small-1",
                "--api-key-env", "MY_DECISION_KEY", "--timeout", "0.5", "--allow-threshold", "0.2", "--block-threshold", "0.8",
                "--on-confident-block", "decide", "--ping", "--non-interactive",
            },
            argv);
    }

    [Fact]
    public void Ping_is_off_unless_it_is_turned_on_and_turning_the_model_off_sends_disable()
    {
        Assert.DoesNotContain("--ping", Argv());
        Assert.DoesNotContain("--no-ping", Argv());
        Assert.Contains("--disable", Argv(v => v["enable"] = ToggleValues.Off));
    }

    [Fact]
    public void An_empty_argument_survives_every_layer_that_shows_or_classifies_the_command()
    {
        var argv = Argv(v => v["connectors-mode"] = "clear");

        // Shown as two quotes, not dropped, so the operator reads what runs; and still a change, never a read.
        var shown = CommandReview.CommandLine("defenseclaw", argv);
        Assert.Contains("--connectors \"\" --non-interactive", shown, StringComparison.Ordinal);
        Assert.Contains("--connectors '' --non-interactive", CommandReview.ClipboardLine("defenseclaw", argv), StringComparison.Ordinal);
        Assert.Equal(CommandTier.StateChanging, CommandTiers.Classify(argv));
    }

    [Fact]
    public void The_pages_say_what_the_cascades_are_and_where_the_token_lives()
    {
        var definition = Definition();

        var connectors = definition.Steps.Single(s => s.Id == "decision-model-connectors");
        Assert.Contains("rules", connectors.Fields[0].Help, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LLM judge", connectors.Fields[0].Help, StringComparison.Ordinal);

        var token = definition.AllFields.Single(f => f.Flag == "--api-key-env");
        Assert.Equal(WizardFieldKind.EnvVarName, token.Kind);
        Assert.Contains("defenseclaw keys set DEFENSECLAW_DECISION_MODEL_KEY", token.Help, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ the review

    private static readonly string[] Setup = { "setup", "decision-model" };

    private static string[] Cmd(params string[] more) => Setup.Concat(more).ToArray();

    [Theory]
    [InlineData("*")]
    [InlineData("claudecode,codex")]
    public void A_connector_list_warns_about_going_back_to_a_release_that_refuses_the_key(string list)
    {
        var warnings = DecisionModelReview.Warnings(Cmd("--connectors", list, "--non-interactive"));

        var bar = Assert.Single(warnings);
        Assert.Equal("Rolling back to an older release", bar.Title);
        Assert.Contains("decision_model.hook_connectors", bar.Message, StringComparison.Ordinal);
        Assert.Contains("before 1.0.7", bar.Message, StringComparison.Ordinal);
        Assert.Contains("defenseclaw setup decision-model --connectors \"\" --non-interactive", bar.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Clearing_the_list_and_leaving_it_alone_carry_no_rollback_bar()
    {
        Assert.Empty(DecisionModelReview.Warnings(Cmd("--connectors", string.Empty, "--non-interactive")));
        Assert.Empty(DecisionModelReview.Warnings(Cmd("--non-interactive")));
        Assert.Empty(DecisionModelReview.Warnings(Cmd("--base-url", "http://127.0.0.1:8791/v1/systemone", "--non-interactive")));
    }

    [Fact]
    public void Ping_says_it_contacts_the_endpoint_and_that_the_settings_are_saved_either_way()
    {
        var bar = Assert.Single(DecisionModelReview.Warnings(Cmd("--ping", "--non-interactive")));

        Assert.Contains("endpoint", bar.Message, StringComparison.Ordinal);
        Assert.Contains("saved whether or not it answers", bar.Message, StringComparison.Ordinal);
        Assert.NotEqual("Rolling back to an older release", bar.Title);
    }

    [Fact]
    public void A_connector_list_and_a_ping_carry_both_bars()
    {
        var warnings = DecisionModelReview.Warnings(Cmd("--connectors", "*", "--ping", "--non-interactive"));

        Assert.Equal(2, warnings.Count);
        Assert.Contains(warnings, w => w.Title == "Rolling back to an older release");
        Assert.Contains(warnings, w => w.Title == DecisionModelReview.PingTitle);
    }

    [Fact]
    public void Nothing_is_said_about_any_other_command()
    {
        Assert.Empty(DecisionModelReview.Warnings(new[] { "setup", "llm", "--connectors", "*", "--ping" }));
        Assert.Empty(DecisionModelReview.Warnings(new[] { "setup", "guardrail", "--connectors", "claudecode" }));
        Assert.Empty(DecisionModelReview.Warnings(new[] { "decision-model", "--ping" }));
        Assert.Empty(DecisionModelReview.Warnings(Array.Empty<string>()));
    }
}
