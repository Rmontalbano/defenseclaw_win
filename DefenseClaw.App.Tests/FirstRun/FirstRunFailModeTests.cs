using DefenseClaw.App.Services;
using DefenseClaw.App.Services.FirstRun;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels.FirstRun;
using DefenseClaw.Core.Install;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.Tests.FirstRun;

/// <summary>
/// The first-run hook fail mode by runtime (CUST-390, the Mac's v1.1.32 fec6f11). Checked against the runtime source: <c>cmd_init.py</c> at 0.8.10 (the
/// prompt defaults to open, the 0.8.10 TUI always passes the flag) and 1.0.0 to 1.0.4 (<c>--fail-mode</c> omitted keeps the current setting, closed on a
/// new install; the TUI sends it with the Action profile only and opens on closed), and <c>config.py</c> <c>hook_fail_mode = "closed"</c>. A per-user
/// 1.0.4 keeps the CUST-373 form exactly. The runtime answers from the synthetic probe fixtures; nothing runs.
/// </summary>
public sealed class FirstRunFailModeTests : IDisposable
{
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

    /// <summary>The runtimes the app supports, as their probe answers them: "unknown" never answers.</summary>
    public enum Runtime
    {
        Unknown,
        V0810,
        V100,
        V103,
        V104,
        V104Managed,
    }

    /// <summary>The pin's screens with another version: what a 1.0.3 or 1.0.4 runtime looks like to the probe (only the version decides these).</summary>
    internal static RuntimeProbeRunner Versioned(string version)
    {
        var pin = RuntimeFixtureRunner.For("95159fd");
        return (arguments, ct) => string.Join(' ', arguments) == "--version-json"
            ? Task.FromResult(RuntimeProbeOutput.Ok($"{{\"name\":\"defenseclaw-cli\",\"schema_version\":1,\"version\":\"{version}\"}}"))
            : pin(arguments, ct);
    }

    private async Task<AppServices> ServicesFor(Runtime runtime)
    {
        var runner = runtime switch
        {
            Runtime.Unknown => null,
            Runtime.V0810 => RuntimeFixtureRunner.For("0.8.10"),
            Runtime.V100 => RuntimeFixtureRunner.For("95159fd"),
            Runtime.V103 => Versioned("1.0.3"),
            _ => Versioned("1.0.4"),
        };

        var services = TestServices.Create(_temp, runtimeProbeRunner: runner);
        _services.Add(services);
        if (runner is not null)
        {
            _ = await services.Runtime.RefreshAsync();
        }

        if (runtime == Runtime.V104Managed)
        {
            services.Installation.Replace(TestInstallations.Managed(_temp.Path));
        }

        return services;
    }

    private static FirstRunViewModel Create(AppServices services) =>
        new(services, _ => Task.FromResult<IReadOnlyList<WizardDefinition>>(Array.Empty<WizardDefinition>()), new GatewaySnapshot
        {
            State = AppGatewayState.NotInitialized,
            Install = InstallState.InstalledNotInitialized,
            CliPath = @"C:\synthetic\defenseclaw.exe",
        });

    private static string Init(FirstRunViewModel vm) => string.Join(' ', vm.BuildPlan().Steps[0].Argv);

    // ---- the question each runtime gets ----

    [Theory]
    [InlineData(Runtime.Unknown, FirstRunFailModeQuestion.EveryProfile, "open")]
    [InlineData(Runtime.V0810, FirstRunFailModeQuestion.EveryProfile, "open")]
    [InlineData(Runtime.V100, FirstRunFailModeQuestion.ActionProfileOnly, "closed")]
    [InlineData(Runtime.V103, FirstRunFailModeQuestion.ActionProfileOnly, "closed")]
    [InlineData(Runtime.V104Managed, FirstRunFailModeQuestion.ActionProfileOnly, "closed")]
    [InlineData(Runtime.V104, FirstRunFailModeQuestion.FollowsPolicy, "closed")]
    public async Task Each_runtime_gets_its_own_question_and_starting_value(Runtime runtime, FirstRunFailModeQuestion question, string start)
    {
        using var vm = Create(await ServicesFor(runtime));

        Assert.Equal(question, vm.FailModeQuestion);
        Assert.Equal(start, vm.FailMode);
    }

    // ---- the box: where it shows, and what it says ----

    [Theory]
    [InlineData(Runtime.Unknown, true, true, false)]
    [InlineData(Runtime.V0810, true, true, false)]
    [InlineData(Runtime.V100, false, true, true)]
    [InlineData(Runtime.V103, false, true, true)]
    [InlineData(Runtime.V104Managed, false, true, true)]
    [InlineData(Runtime.V104, false, false, false)]
    public async Task The_box_shows_for_every_profile_on_0_8_x_for_action_only_on_1_0_x_and_never_on_a_per_user_1_0_4(
        Runtime runtime, bool inObserve, bool inAction, bool hint)
    {
        using var vm = Create(await ServicesFor(runtime));

        vm.Profile = "observe";
        Assert.Equal(inObserve, vm.AsksFailMode);
        Assert.False(vm.HasFailModeHint);

        vm.Profile = "action";
        Assert.Equal(inAction, vm.AsksFailMode);
        Assert.Equal(hint, vm.HasFailModeHint);
        Assert.Equal(hint ? FirstRunFailMode.ActionOnlyHint : string.Empty, vm.FailModeHint);
        Assert.Equal(runtime == Runtime.V104, vm.OutageFollowsPolicy);
    }

    [Fact]
    public void The_hint_is_the_tuis_own_sentence()
    {
        // tui/panels/first_run.py at 1.0.0 to 1.0.4, the "Hook Fail Mode" field.
        Assert.Equal(
            "Action profile only. If a check can't finish: closed = block where supported, open = allow and log.",
            FirstRunFailMode.ActionOnlyHint);
    }

    // ---- the command, as a golden per runtime and profile ----

    private const string ObserveHead = "init --non-interactive --yes --json-summary --connector codex --profile observe --scanner-mode local --no-judge";
    private const string ActionHead = "init --non-interactive --yes --json-summary --connector codex --profile action --scanner-mode local --no-judge";
    private const string Tail = "--no-start-gateway --verify";

    [Theory]
    [InlineData(Runtime.Unknown, "observe", ObserveHead + " --fail-mode open " + Tail)]
    [InlineData(Runtime.Unknown, "action", ActionHead + " --fail-mode open --no-human-approval " + Tail)]
    [InlineData(Runtime.V0810, "observe", ObserveHead + " --fail-mode open " + Tail)]
    [InlineData(Runtime.V0810, "action", ActionHead + " --fail-mode open --no-human-approval " + Tail)]
    [InlineData(Runtime.V100, "observe", ObserveHead + " " + Tail)]
    [InlineData(Runtime.V100, "action", ActionHead + " --fail-mode closed --no-human-approval " + Tail)]
    [InlineData(Runtime.V103, "observe", ObserveHead + " " + Tail)]
    [InlineData(Runtime.V103, "action", ActionHead + " --fail-mode closed --no-human-approval " + Tail)]
    [InlineData(Runtime.V104Managed, "observe", ObserveHead + " " + Tail)]
    [InlineData(Runtime.V104Managed, "action", ActionHead + " --fail-mode closed --no-human-approval " + Tail)]
    [InlineData(Runtime.V104, "observe", ObserveHead + " " + Tail)]
    [InlineData(Runtime.V104, "action", ActionHead + " --no-human-approval " + Tail)]
    public async Task The_init_command_for_an_untouched_form_is_the_golden_one(Runtime runtime, string profile, string expected)
    {
        using var vm = Create(await ServicesFor(runtime));
        vm.Profile = profile;

        Assert.Equal(expected, Init(vm));
    }

    [Theory]
    [InlineData(Runtime.V0810, "observe", "closed")]
    [InlineData(Runtime.V0810, "action", "closed")]
    [InlineData(Runtime.V103, "action", "open")]
    [InlineData(Runtime.V104Managed, "action", "open")]
    public async Task A_picked_value_is_sent_where_the_runtime_takes_one(Runtime runtime, string profile, string picked)
    {
        using var vm = Create(await ServicesFor(runtime));
        vm.Profile = profile;
        vm.FailMode = picked;

        var argv = vm.BuildPlan().Steps[0].Argv.ToList();

        Assert.Equal(picked, argv[argv.IndexOf("--fail-mode") + 1]);
    }

    [Theory]
    [InlineData(Runtime.V100)]
    [InlineData(Runtime.V103)]
    [InlineData(Runtime.V104Managed)]
    public async Task On_1_0_x_observe_leaves_the_flag_off_whatever_was_picked_so_the_cli_keeps_the_saved_setting(Runtime runtime)
    {
        using var vm = Create(await ServicesFor(runtime));
        vm.Profile = "action";
        vm.FailMode = "open";
        vm.Profile = "observe";

        Assert.DoesNotContain("--fail-mode", vm.BuildPlan().Steps[0].Argv);
    }

    [Fact]
    public async Task A_per_user_1_0_4_plan_is_what_it_was_with_the_strict_pack_as_its_own_step()
    {
        using var vm = Create(await ServicesFor(Runtime.V104));
        vm.Profile = "action";
        vm.FailMode = "open";
        vm.StrictOutage = true;

        var plan = vm.BuildPlan();

        Assert.DoesNotContain("--fail-mode", plan.Steps[0].Argv);
        Assert.Equal(new[] { "guardrail", "use-pack", "strict" }, plan.Steps[1].Argv.ToArray());
    }

    // ---- never weaker than before ----

    [Fact]
    public void No_option_sends_open_unless_open_was_asked_for_and_anything_unknown_is_closed()
    {
        foreach (var actionOnly in new[] { false, true })
        {
            foreach (var follows in new[] { false, true })
            {
                foreach (var profile in new[] { "observe", "action" })
                {
                    foreach (var failMode in new[] { "open", "closed", "OPEN", string.Empty, "&calc" })
                    {
                        var argv = InitPlanBuilder.Build(new InitPlanOptions
                        {
                            Profile = profile,
                            FailMode = failMode,
                            FailModeActionOnly = actionOnly,
                            OutageFollowsPolicy = follows,
                        }).Steps.SelectMany(s => s.Argv).ToList();

                        var at = argv.IndexOf("--fail-mode");
                        if (at < 0)
                        {
                            continue;
                        }

                        Assert.Equal(failMode == "open" ? "open" : "closed", argv[at + 1]);
                    }
                }
            }
        }
    }

    [Fact]
    public void The_options_on_their_own_start_closed()
    {
        var argv = InitPlanBuilder.Build(new InitPlanOptions()).Steps[0].Argv.ToList();

        Assert.Equal("closed", argv[argv.IndexOf("--fail-mode") + 1]);
    }

    // ---- a runtime that answers after the window opened ----

    [Fact]
    public async Task A_1_0_x_runtime_that_answers_late_moves_an_untouched_box_to_closed_and_to_the_action_profile()
    {
        var services = TestServices.Create(_temp, runtimeProbeRunner: Versioned("1.0.3"));
        _services.Add(services);
        using var vm = Create(services);
        Assert.Equal("open", vm.FailMode);
        Assert.True(vm.AsksFailMode);

        _ = await services.Runtime.RefreshAsync();
        DrainUi();

        Assert.Equal(FirstRunFailModeQuestion.ActionProfileOnly, vm.FailModeQuestion);
        Assert.Equal("closed", vm.FailMode);
        Assert.False(vm.AsksFailMode);
        Assert.DoesNotContain("--fail-mode", vm.BuildPlan().Steps[0].Argv);
    }

    [Fact]
    public async Task A_value_the_operator_picked_is_kept_when_the_runtime_answers()
    {
        var services = TestServices.Create(_temp, runtimeProbeRunner: Versioned("1.0.3"));
        _services.Add(services);
        using var vm = Create(services);
        vm.FailMode = "closed";
        vm.FailMode = "open";

        _ = await services.Runtime.RefreshAsync();
        DrainUi();

        Assert.Equal("open", vm.FailMode);
    }

    /// <summary>RuntimeService posts Changed to the shared UI thread when it exists; let that post run before asserting.</summary>
    private static void DrainUi() =>
        System.Windows.Application.Current?.Dispatcher.Invoke(static () => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
}
