using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.FirstRun;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Policy;
using DefenseClaw.Core.Policy.Model;
using static DefenseClaw.App.Tests.TestSupport.PolicyModelTestSupport;

namespace DefenseClaw.App.Tests.Policies;

/// <summary>
/// The Cascade block of the Policies panel (runtime 1.0.10): lines from config.yaml with no command run, an Explain button that runs
/// <c>guardrail cascade explain [--connector C] --json</c> on demand only and through the panel's one read door, the matrix caveat while the cascade is
/// set, and a runtime without the cascade showing nothing new. The CLI is a function; no process starts.
/// </summary>
public sealed class GuardrailCascadePolicyTests : IDisposable
{
    private const string Owner =
        "guardrail:\n  enabled: true\n  connector: codex\n  connectors:\n    codex:\n      mode: observe\n  judge:\n    enabled: false\n";

    private const string SetConfig = Owner + "  cascade:\n    unknown: block\n    lanes:\n      alignment: alert\n";

    private const string ExplainJson = """
        {"cascade":{"connector":"codex","valid":true,
          "rules":{"mode":"observe","pack":"default","block_at":"HIGH","alert_at":"MEDIUM"},
          "system_one":{"runs":"off"},"judge":{"runs":"off","max_action":"block","no_verdict":"rules"},
          "unknown":{"setting":"pack","action":"alert"},"critical_floor":"alert","hook_fail_mode":"open"}}
        """;

    private readonly List<TempDirectory> _temps = new();
    private readonly List<AppServices> _services = new();

    public void Dispose()
    {
        foreach (var services in _services)
        {
            services.Dispose();
        }

        foreach (var temp in _temps)
        {
            temp.Dispose();
        }
    }

    private sealed class Script
    {
        public ScriptedCli Catalog { get; } = new(Connectors);

        public List<(IReadOnlyList<string> Argv, CliRunOptions Options)> Calls { get; } = new();

        public Func<IReadOnlyList<string>, CliInvocation>? Explain { get; set; }

        public IEnumerable<string> ExplainCalls => Calls.Where(c => c.Argv.Count > 2 && c.Argv[1] == "cascade").Select(c => string.Join(' ', c.Argv));

        public Task<CliInvocation> Run(IReadOnlyList<string> argv, CliRunOptions options)
        {
            Calls.Add((argv.ToArray(), options));
            if (argv.Count > 2 && argv[0] == "guardrail" && argv[1] == "cascade")
            {
                return Task.FromResult(Explain?.Invoke(argv) ?? throw new InvalidOperationException("nothing scripts the explain command"));
            }

            return Catalog.Run(argv, options);
        }
    }

    private async Task<(PolicyModelViewModel Vm, Script Script)> OpenAsync(string version, string config)
    {
        var temp = new TempDirectory();
        _temps.Add(temp);
        var services = TestServices.Create(temp, config, FirstRunFailModeTests.Versioned(version));
        _services.Add(services);
        _ = await services.Runtime.RefreshAsync();

        var script = new Script();
        var vm = new PolicyModelViewModel(services, SevenViewPolicyBackend.Instance, new FixtureData()) { RunCli = script.Run };
        await vm.InitializeAsync();
        return (vm, script);
    }

    private static string[] Lines(PolicyModelViewModel vm) => vm.CascadeLines.Select(l => l.Text).ToArray();

    // ---- what shows, and when it runs ------------------------------------------------------------------------------------------

    [Fact]
    public async Task On_a_1_0_10_runtime_the_block_shows_config_yamls_lines_and_runs_nothing_on_load()
    {
        var (vm, script) = await OpenAsync("1.0.10", Owner);

        Assert.Equal(PoliciesState.Loaded, vm.State);
        Assert.True(vm.ShowCascade);
        Assert.Equal(
            new[] { "Cascade: default (the rule pack decides; the effects and secret-flow lanes only measure, in shadow)" },
            Lines(vm));
        Assert.Empty(script.ExplainCalls);
    }

    [Fact]
    public async Task Refreshing_and_changing_scope_never_run_the_explain_command()
    {
        var (vm, script) = await OpenAsync("1.0.10", Owner);

        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedRow = vm.Rows.Single(r => r.Key == "codex");
        vm.SelectedScope = "claudecode";
        vm.SearchText = "x";

        Assert.Empty(script.ExplainCalls);
    }

    [Fact]
    public async Task A_set_cascade_lists_its_values_and_a_connector_the_alignment_check_cannot_reach()
    {
        var (vm, _) = await OpenAsync("1.0.10", SetConfig);
        vm.SelectedRow = vm.Rows.Single(r => r.Key == "codex");

        var lines = Lines(vm);

        Assert.Equal("Cascade: set in config.yaml", lines[0]);
        Assert.Contains("unknown: block", lines);
        Assert.Contains("lanes.alignment: alert", lines);
        Assert.Contains(
            "The alignment check (lanes.alignment alert) does nothing for codex: the LLM judge does not run for it (guardrail.judge.enabled, guardrail.judge.hook_connectors).",
            lines);
    }

    // ---- Explain -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Explain_for_the_global_scope_runs_the_exact_argv_as_a_background_read_with_a_35_second_limit()
    {
        var (vm, script) = await OpenAsync("1.0.10", Owner);
        script.Explain = argv => Result(0, argv, ExplainJson);

        await vm.ExplainCascadeCommand.ExecuteAsync(null);

        var call = Assert.Single(script.Calls, c => c.Argv.Count > 1 && c.Argv[1] == "cascade");
        Assert.Equal(new[] { "guardrail", "cascade", "explain", "--json" }, call.Argv);
        Assert.True(call.Options.Background);
        Assert.Equal(TimeSpan.FromSeconds(35), call.Options.Timeout);
        Assert.Equal(CommandTier.ReadOnly, CommandTiers.Classify(call.Argv));
        Assert.True(PolicyActionGuard.IsAllowedRead(call.Argv));
    }

    [Fact]
    public async Task Explain_for_a_connector_scope_adds_the_connector_and_replaces_the_lines_with_the_runtimes_own()
    {
        var (vm, script) = await OpenAsync("1.0.10", Owner);
        script.Explain = argv => Result(0, argv, ExplainJson);
        vm.SelectedRow = vm.Rows.Single(r => r.Key == "codex");

        await vm.ExplainCascadeCommand.ExecuteAsync(null);

        Assert.Equal(
            new[] { "guardrail", "cascade", "explain", "--connector", "codex", "--json" },
            script.Calls.Single(c => c.Argv.Count > 1 && c.Argv[1] == "cascade").Argv);
        var lines = Lines(vm);
        Assert.Equal("Scope: codex", lines[0]);
        Assert.Contains("Rules: default pack, observe mode, block at HIGH, alert at MEDIUM", lines);
        Assert.Contains("System One: off", lines);
        Assert.Contains("LLM judge: off", lines);
        Assert.Contains("Unknown tier: alert (pack)", lines);
        Assert.Contains("CRITICAL floor: alert", lines);
        Assert.DoesNotContain(lines, l => l.StartsWith("Cascade: default", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Moving_to_another_scope_goes_back_to_config_lines_and_runs_nothing()
    {
        var (vm, script) = await OpenAsync("1.0.10", Owner);
        script.Explain = argv => Result(0, argv, ExplainJson);
        vm.SelectedRow = vm.Rows.Single(r => r.Key == "codex");
        await vm.ExplainCascadeCommand.ExecuteAsync(null);

        vm.SelectedRow = vm.Rows.Single(r => r.Key == "claudecode");

        Assert.StartsWith("Cascade: default", Lines(vm)[0], StringComparison.Ordinal);
        Assert.Single(script.ExplainCalls);
    }

    [Fact]
    public async Task A_command_that_fails_shows_its_first_error_line_beside_the_config_lines()
    {
        var (vm, script) = await OpenAsync("1.0.10", Owner);
        script.Explain = argv => Result(1, argv, stderr: "defenseclaw-gateway was not found\nsecond line\n");

        await vm.ExplainCascadeCommand.ExecuteAsync(null);

        var lines = Lines(vm);
        Assert.Equal("Could not run defenseclaw guardrail cascade explain: defenseclaw-gateway was not found", lines[^1]);
        Assert.StartsWith("Cascade: default", lines[0], StringComparison.Ordinal);
        Assert.DoesNotContain(lines, l => l.Contains("second line", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_command_that_did_not_finish_says_why()
    {
        var (vm, script) = await OpenAsync("1.0.10", Owner);
        script.Explain = argv => Result(null, argv, failure: "timed out after 35 s");

        await vm.ExplainCascadeCommand.ExecuteAsync(null);

        Assert.Equal("Could not run defenseclaw guardrail cascade explain: timed out after 35 s", Lines(vm)[^1]);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"cascade\":{\"valid\":\"maybe\"}}")]
    [InlineData("")]
    public async Task Output_that_is_not_the_expected_shape_falls_back_to_the_config_lines_and_says_so(string output)
    {
        var (vm, script) = await OpenAsync("1.0.10", SetConfig);
        script.Explain = argv => Result(0, argv, output);

        await vm.ExplainCascadeCommand.ExecuteAsync(null);

        var lines = Lines(vm);
        Assert.Equal("Cascade: set in config.yaml", lines[0]);
        Assert.Equal(GuardrailCascadeExplain.UnreadableLine, lines[^1]);
    }

    [Fact]
    public async Task Output_over_the_bound_is_never_shown()
    {
        var (vm, script) = await OpenAsync("1.0.10", Owner);
        script.Explain = argv => Result(0, argv, "{\"cascade\":{\"valid\":true,\"rules\":{},\"connector\":\"" + new string('x', 300_000) + "\"}}");

        await vm.ExplainCascadeCommand.ExecuteAsync(null);

        Assert.Equal(GuardrailCascadeExplain.UnreadableLine, Lines(vm)[^1]);
        Assert.All(Lines(vm), l => Assert.True(l.Length < 400));
    }

    [Fact]
    public async Task The_panels_door_refuses_a_cascade_argv_of_another_shape()
    {
        var (vm, script) = await OpenAsync("1.0.10", Owner);

        await Assert.ThrowsAsync<InvalidOperationException>(() => vm.RunReadAsync(new[] { "guardrail", "cascade", "explain", "--input", "x", "--json" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => vm.RunReadAsync(new[] { "guardrail", "cascade", "set", "unknown", "block" }));

        Assert.Empty(script.ExplainCalls);
    }

    // ---- the severity matrix ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_matrix_caveat_shows_only_while_the_cascade_is_set()
    {
        var (set, _) = await OpenAsync("1.0.10", SetConfig);
        var (plain, _) = await OpenAsync("1.0.10", Owner);
        set.SelectedRow = set.Rows.Single(r => r.Key == "global");
        plain.SelectedRow = plain.Rows.Single(r => r.Key == "global");

        Assert.Contains(set.InspectorLines, l => l.Text == PostureCascade.MatrixCaveat);
        Assert.DoesNotContain(plain.InspectorLines, l => l.Text == PostureCascade.MatrixCaveat);
        Assert.Equal(
            "guardrail.cascade is set: it can change the outcome these pack levels predict for this scope; see Cascade.",
            PostureCascade.MatrixCaveat);
    }

    [Fact]
    public async Task The_caveat_sits_under_the_matrix()
    {
        var (vm, _) = await OpenAsync("1.0.10", SetConfig);
        vm.SelectedRow = vm.Rows.Single(r => r.Key == "global");

        var texts = vm.InspectorLines.Select(l => l.Text).ToList();

        var low = texts.FindIndex(t => t.StartsWith("  LOW", StringComparison.Ordinal));
        Assert.True(low >= 0);
        Assert.Equal(PostureCascade.MatrixCaveat, texts[low + 1]);
    }

    // ---- a runtime without the cascade ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_1_0_9_runtime_shows_no_block_runs_nothing_and_its_matrix_text_is_unchanged()
    {
        var (withCascade, script) = await OpenAsync("1.0.9", SetConfig);
        var (without, _) = await OpenAsync("1.0.9", Owner);
        withCascade.SelectedRow = withCascade.Rows.Single(r => r.Key == "global");
        without.SelectedRow = without.Rows.Single(r => r.Key == "global");

        Assert.False(withCascade.CascadeAvailable);
        Assert.False(withCascade.ShowCascade);
        Assert.False(withCascade.CanExplainCascade);
        Assert.Empty(withCascade.CascadeLines);
        Assert.Equal(without.InspectorLines.Select(l => l.Text), withCascade.InspectorLines.Select(l => l.Text));
        Assert.DoesNotContain(withCascade.InspectorLines, l => l.Text.Contains("cascade", StringComparison.OrdinalIgnoreCase));

        await withCascade.ExplainCascadeCommand.ExecuteAsync(null);

        Assert.Empty(script.ExplainCalls);
        Assert.Empty(withCascade.CascadeLines);
    }

    [Fact]
    public async Task The_block_shows_on_the_posture_view_only()
    {
        var (vm, _) = await OpenAsync("1.0.10", Owner);

        vm.SelectedNav = vm.Nav.Single(n => n.View == "packs");

        Assert.False(vm.ShowCascade);
    }
}
