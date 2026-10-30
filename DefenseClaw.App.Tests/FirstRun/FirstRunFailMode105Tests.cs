using DefenseClaw.App.Services;
using DefenseClaw.App.Services.FirstRun;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels.FirstRun;
using DefenseClaw.Core.Install;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.Tests.FirstRun;

/// <summary>
/// The first-run hook fail mode on a per-user 1.0.5 (CUST-397). There a sent <c>--fail-mode</c> is saved and wins over the policy tier (<c>cmd_init.py</c>
/// <c>--fail-mode</c> help at tag 1.0.5: "A saved choice wins over the policy tier. Omitted, nothing is saved"), so the form asks it as the 1.0.5 TUI does
/// (<c>tui/panels/first_run.py</c>: policy, open or closed, Action profile only, starting on policy) and sends the flag only for a typed open or closed.
/// A managed 1.0.5 keeps the 1.0.x form, and the 1.0.4 form is untouched (<see cref="FirstRunFailModeTests"/>).
/// </summary>
public sealed class FirstRunFailMode105Tests : IDisposable
{
    private const string ActionHead = "init --non-interactive --yes --json-summary --connector codex --profile action --scanner-mode local --no-judge";
    private const string ObserveHead = "init --non-interactive --yes --json-summary --connector codex --profile observe --scanner-mode local --no-judge";
    private const string Tail = "--no-start-gateway --verify";

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

    private async Task<FirstRunViewModel> CreateAsync(string version = "1.0.5", bool managed = false)
    {
        var services = TestServices.Create(_temp, runtimeProbeRunner: FirstRunFailModeTests.Versioned(version));
        _services.Add(services);
        _ = await services.Runtime.RefreshAsync();
        if (managed)
        {
            services.Installation.Replace(TestInstallations.Managed(_temp.Path));
        }

        return new FirstRunViewModel(services, _ => Task.FromResult<IReadOnlyList<WizardDefinition>>(Array.Empty<WizardDefinition>()), new GatewaySnapshot
        {
            State = AppGatewayState.NotInitialized,
            Install = InstallState.InstalledNotInitialized,
            CliPath = @"C:\synthetic\defenseclaw.exe",
        });
    }

    private static string Init(FirstRunViewModel vm) => string.Join(' ', vm.BuildPlan().Steps[0].Argv);

    [Theory]
    [InlineData("1.0.5", false, FirstRunFailModeQuestion.SavedOrPolicy, "policy")]
    [InlineData("1.0.5", true, FirstRunFailModeQuestion.ActionProfileOnly, "closed")]
    [InlineData("1.0.4", false, FirstRunFailModeQuestion.FollowsPolicy, "closed")]
    [InlineData("1.0.4", true, FirstRunFailModeQuestion.ActionProfileOnly, "closed")]
    [InlineData("1.0.3", false, FirstRunFailModeQuestion.ActionProfileOnly, "closed")]
    public async Task Each_release_and_installation_gets_its_question_and_starting_value(string version, bool managed, FirstRunFailModeQuestion question, string start)
    {
        using var vm = await CreateAsync(version, managed);

        Assert.Equal(question, vm.FailModeQuestion);
        Assert.Equal(start, vm.FailMode);
        Assert.Contains(vm.FailModes, c => c.Value == start);
    }

    [Fact]
    public async Task On_a_per_user_1_0_5_the_box_is_the_action_profiles_with_save_nothing_first_and_no_strict_pack_toggle()
    {
        using var vm = await CreateAsync();

        Assert.Equal(new[] { "policy", "open", "closed" }, vm.FailModes.Select(c => c.Value).ToArray());
        Assert.False(vm.OutageFollowsPolicy);

        vm.Profile = "observe";
        Assert.False(vm.AsksFailMode);

        vm.Profile = "action";
        Assert.True(vm.AsksFailMode);
        Assert.True(vm.HasFailModeHint);
        Assert.Equal(FirstRunFailMode.SavedOrPolicyHint, vm.FailModeHint);
        Assert.Contains("policy = save nothing", vm.FailModeHint, StringComparison.Ordinal);
        Assert.Contains("A saved choice wins over the policy tier", vm.FailModeHint, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("action", "policy", ActionHead + " --no-human-approval " + Tail)]
    [InlineData("action", "open", ActionHead + " --fail-mode open --no-human-approval " + Tail)]
    [InlineData("action", "closed", ActionHead + " --fail-mode closed --no-human-approval " + Tail)]
    [InlineData("observe", "policy", ObserveHead + " " + Tail)]
    [InlineData("observe", "closed", ObserveHead + " " + Tail)]
    [InlineData("observe", "open", ObserveHead + " " + Tail)]
    public async Task Only_a_typed_open_or_closed_with_the_action_profile_reaches_argv(string profile, string picked, string expected)
    {
        using var vm = await CreateAsync();
        vm.Profile = "action";
        vm.FailMode = picked;
        vm.Profile = profile;

        Assert.Equal(expected, Init(vm));
        Assert.DoesNotContain(vm.BuildPlan().Steps, s => s.Argv.Contains("use-pack"));
    }

    [Theory]
    [InlineData("1.0.5", true)]
    [InlineData("1.0.4", false)]
    [InlineData("1.0.3", false)]
    public async Task Observe_is_never_blocks_only_before_1_0_5(string version, bool selfProtection)
    {
        using var vm = await CreateAsync(version);
        vm.IsInstalled = true;

        Assert.Equal(selfProtection, vm.ObserveBlocksSelfChanges);
        foreach (var text in new[] { vm.Subtitle, vm.ProfileCaption })
        {
            Assert.Equal(selfProtection, text.Contains("never blocks on policy findings (DefenseClaw still blocks changes to its own files)", StringComparison.Ordinal));
            Assert.Equal(!selfProtection, text.Contains("never blocks;", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task An_untouched_form_saves_nothing_in_either_profile()
    {
        using var vm = await CreateAsync();

        foreach (var profile in new[] { "observe", "action" })
        {
            vm.Profile = profile;
            Assert.DoesNotContain("--fail-mode", vm.BuildPlan().Steps.SelectMany(s => s.Argv));
        }
    }

    [Fact]
    public void The_builder_never_sends_policy_or_anything_unknown_when_only_a_chosen_value_counts()
    {
        foreach (var failMode in new[] { "policy", string.Empty, "OPEN", "&calc", "Closed" })
        {
            var argv = InitPlanBuilder.Build(new InitPlanOptions
            {
                Profile = "action",
                FailMode = failMode,
                FailModeActionOnly = true,
                FailModeOnlyWhenChosen = true,
            }).Steps.SelectMany(s => s.Argv).ToList();

            Assert.DoesNotContain("--fail-mode", argv);
        }

        var closed = InitPlanBuilder.Build(new InitPlanOptions { Profile = "action", FailMode = "closed", FailModeActionOnly = true, FailModeOnlyWhenChosen = true })
            .Steps[0].Argv.ToList();
        Assert.Equal("closed", closed[closed.IndexOf("--fail-mode") + 1]);
    }

    [Fact]
    public async Task A_policy_pick_does_not_survive_a_runtime_that_turns_out_older()
    {
        var services = TestServices.Create(_temp, runtimeProbeRunner: FirstRunFailModeTests.Versioned("1.0.5"));
        _services.Add(services);
        _ = await services.Runtime.RefreshAsync();
        using var vm = new FirstRunViewModel(services, _ => Task.FromResult<IReadOnlyList<WizardDefinition>>(Array.Empty<WizardDefinition>()), new GatewaySnapshot
        {
            State = AppGatewayState.NotInitialized,
            Install = InstallState.InstalledNotInitialized,
            CliPath = @"C:\synthetic\defenseclaw.exe",
        });
        vm.FailMode = "closed";
        vm.FailMode = "policy";

        // A managed install reads as the 1.0.x form, which has no policy choice: the pick goes back to that form's starting value.
        services.Installation.Replace(TestInstallations.Managed(_temp.Path));
        vm.Profile = "action";

        Assert.Equal(FirstRunFailModeQuestion.ActionProfileOnly, vm.FailModeQuestion);
        Assert.Equal("closed", vm.FailMode);
        Assert.DoesNotContain(vm.FailModes, c => c.Value == "policy");
    }
}
