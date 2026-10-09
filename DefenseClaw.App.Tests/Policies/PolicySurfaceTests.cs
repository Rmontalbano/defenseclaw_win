using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Policy;
using static DefenseClaw.App.Tests.TestSupport.PolicyModelTestSupport;

namespace DefenseClaw.App.Tests.Policies;

/// <summary>
/// Which Policies surface the panel is (CUST-293): the table of named policies of CUST-281 on 0.8.10 and on any runtime that is not known to
/// have the policy model, the model panel when the runtime probe finds <see cref="DefenseClaw.Core.Runtime.RuntimeCapability.PolicyModel"/>,
/// and that the choice follows the probe. The runtime probe answers from the help-screen fixtures of 0.8.10 and of the pinned source commit
/// (95159fd), and the CLI is a script: no process starts.
/// </summary>
public sealed class PolicySurfaceTests : IDisposable
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

    /// <param name="source">What the runtime probe answers.</param>
    /// <param name="started">
    /// True starts the runtime service, as the app does at launch: the first probe is then on its way, and the panel's first visit waits for it.
    /// False leaves it idle, as a tool or a test does: nothing will ever probe, and the runtime reads as unknown.
    /// </param>
    private AppServices Create(RuntimeProbeRunnerSource source, bool started = true)
    {
        var services = TestServices.Create(_temp, runtimeProbeRunner: source.Runner);
        _services.Add(services);
        if (started)
        {
            services.Runtime.Start();
        }

        return services;
    }

    /// <summary>A runtime whose fixture set a test can change between probes (an upgrade).</summary>
    private sealed class RuntimeProbeRunnerSource
    {
        public RuntimeProbeRunnerSource(string set)
        {
            Set = set;
            Runner = ProbeRunner(() => Set);
        }

        public string Set { get; set; }

        public DefenseClaw.Core.Runtime.RuntimeProbeRunner Runner { get; }
    }

    /// <summary>Answers both surfaces' reads and records every command asked: <c>policy list</c> is 0.8.10's, <c>policy list --json</c> the model's.</summary>
    private static Func<IReadOnlyList<string>, CliRunOptions, Task<CliInvocation>> Script(List<string> calls)
    {
        var model = new ScriptedCli(Fresh);
        return (argv, options) =>
        {
            var text = string.Join(' ', argv);
            lock (calls)
            {
                calls.Add(text);
            }

            if (text == "policy list")
            {
                return Task.FromResult(Result(0, argv, PolicyFixtures.ListReport));
            }

            if (text.StartsWith("policy show ", StringComparison.Ordinal) && argv.Count == 3)
            {
                return Task.FromResult(Result(0, argv, PolicyFixtures.Show(argv[2])));
            }

            return model.Run(argv, options);
        };
    }

    // ------------------------------------------------------------------ the choice

    [Fact]
    public async Task On_0810_the_panel_is_the_table_of_named_policies_and_nothing_of_the_model_is_read()
    {
        var services = Create(new RuntimeProbeRunnerSource(Installed));
        var calls = new List<string>();
        var vm = new PoliciesPanelViewModel(services) { RunCli = Script(calls) };

        await vm.InitializeAsync();

        Assert.True(vm.UsesClassic);
        Assert.False(vm.UsesModel);
        Assert.Null(vm.Model);
        Assert.Equal(PoliciesState.Loaded, vm.State);
        Assert.Equal(4, vm.Rows.Count);
        Assert.Equal(new[] { "policy list" }, calls);
        Assert.Equal("Security policies for skills, MCP servers and plugins: which are built in, which you made, and which one is active.", vm.Description);
    }

    [Fact]
    public async Task A_runtime_whose_probe_failed_is_treated_as_0810_and_nothing_newer_is_offered_on_a_guess()
    {
        var services = Create(new RuntimeProbeRunnerSource("no-such-set"));
        var calls = new List<string>();
        var vm = new PoliciesPanelViewModel(services) { RunCli = Script(calls) };

        await vm.InitializeAsync();

        Assert.False(services.Runtime.Capabilities.IsKnown);
        Assert.False(ReferenceEquals(services.Runtime.Current, DefenseClaw.Core.Runtime.RuntimeSnapshot.NotProbed));
        Assert.True(vm.UsesClassic);
        Assert.Null(vm.Model);
        Assert.Equal(new[] { "policy list" }, calls);
    }

    [Fact]
    public async Task A_runtime_nothing_has_probed_is_the_0810_surface_and_the_first_visit_does_not_wait_for_a_probe_that_will_not_come()
    {
        var services = Create(new RuntimeProbeRunnerSource(Pinned), started: false);
        var calls = new List<string>();
        var vm = new PoliciesPanelViewModel(services) { RunCli = Script(calls) };

        var init = vm.InitializeAsync();

        Assert.True(init.IsCompleted);
        await init;
        Assert.True(ReferenceEquals(services.Runtime.Current, DefenseClaw.Core.Runtime.RuntimeSnapshot.NotProbed));
        Assert.True(vm.UsesClassic);
        Assert.Equal(new[] { "policy list" }, calls);
    }

    [Fact]
    public async Task On_the_pinned_runtime_the_panel_is_the_model_and_the_first_visit_waits_for_the_probe_to_say_so()
    {
        var services = Create(new RuntimeProbeRunnerSource(Pinned));
        var calls = new List<string>();
        var vm = new PoliciesPanelViewModel(services) { RunCli = Script(calls) };

        // The first visit asks the runtime what it is (waiting for the app's own first probe if it has not answered) before it reads anything.
        await vm.InitializeAsync();

        Assert.True(services.Runtime.Capabilities.Has(DefenseClaw.Core.Runtime.RuntimeCapability.PolicyModel));
        Assert.True(vm.UsesModel);
        Assert.False(vm.UsesClassic);
        Assert.NotNull(vm.Model);
        Assert.Equal(PoliciesState.Loaded, vm.Model!.State);
        Assert.DoesNotContain("policy list", calls);
        Assert.Contains("policy list --json", calls);
        Assert.Equal(vm.Model.Description, vm.Description);
        Assert.Equal(new[] { "posture", "optin", "chains", "families", "policies", "packs" }, vm.Model.Nav.Select(n => n.View).ToArray());
    }

    [Fact]
    public async Task A_first_visit_that_starts_before_the_probe_has_answered_reads_nothing_until_it_knows_what_the_runtime_is()
    {
        // The shell activates the panel while its first initialization is still waiting for the app's own first probe.
        var gate = new TaskCompletionSource();
        var answers = ProbeRunner(Pinned);
        var services = TestServices.Create(
            _temp,
            runtimeProbeRunner: async (arguments, token) =>
            {
                await gate.Task;
                return await answers(arguments, token);
            });
        _services.Add(services);
        services.Runtime.Start();
        var calls = new List<string>();
        var vm = new PoliciesPanelViewModel(services) { RunCli = Script(calls) };

        vm.SetActive(true);
        var init = vm.InitializeAsync();

        Assert.False(init.IsCompleted);
        Assert.Empty(calls);
        Assert.True(vm.UsesClassic);

        gate.SetResult();
        await init;

        Assert.True(vm.UsesModel);
        Assert.True(vm.Model!.IsActive);
        Assert.DoesNotContain("policy list", calls);
        Assert.Equal(1, calls.Count(c => c == "policy list --json"));
        vm.SetActive(false);
    }

    [Fact]
    public async Task A_first_visit_that_starts_before_the_probe_has_answered_reads_the_table_once_when_the_runtime_is_0810()
    {
        var gate = new TaskCompletionSource();
        var answers = ProbeRunner(Installed);
        var services = TestServices.Create(
            _temp,
            runtimeProbeRunner: async (arguments, token) =>
            {
                await gate.Task;
                return await answers(arguments, token);
            });
        _services.Add(services);
        services.Runtime.Start();
        var calls = new List<string>();
        var vm = new PoliciesPanelViewModel(services) { RunCli = Script(calls) };

        vm.SetActive(true);
        var init = vm.InitializeAsync();
        Assert.Empty(calls);

        gate.SetResult();
        await init;

        Assert.True(vm.UsesClassic);
        Assert.Equal(new[] { "policy list" }, calls);
        vm.SetActive(false);
    }

    [Fact]
    public async Task A_runtime_probed_before_the_panel_is_built_gives_the_model_at_once()
    {
        var services = Create(new RuntimeProbeRunnerSource(Pinned));
        _ = await services.Runtime.RefreshAsync();

        var vm = new PoliciesPanelViewModel(services) { RunCli = Script(new List<string>()) };

        Assert.True(vm.UsesModel);
        Assert.NotNull(vm.Model);
    }

    [Fact]
    public async Task A_backend_handed_in_fixes_the_surface_whatever_the_runtime_is()
    {
        var pinned = Create(new RuntimeProbeRunnerSource(Pinned));
        _ = await pinned.Runtime.RefreshAsync();

        Assert.True(new PoliciesPanelViewModel(pinned, Release0810PolicyBackend.Instance).UsesClassic);
        Assert.True(new PoliciesPanelViewModel(Create(new RuntimeProbeRunnerSource(Installed)), SevenViewPolicyBackend.Instance).UsesModel);
    }

    // ------------------------------------------------------------------ the choice follows the probe

    [Fact]
    public async Task The_surface_follows_the_probe_when_the_runtime_changes_while_the_panel_is_on_screen()
    {
        var runtime = new RuntimeProbeRunnerSource(Installed);
        var services = Create(runtime);
        var calls = new List<string>();
        var vm = new PoliciesPanelViewModel(services) { RunCli = Script(calls) };
        await vm.InitializeAsync();
        vm.SetActive(true);
        Assert.True(vm.UsesClassic);
        Assert.Equal(new[] { "policy list" }, calls);

        // An upgrade to the newer runtime: the panel becomes the model and reads it.
        runtime.Set = Pinned;
        _ = await services.Runtime.RefreshAsync(force: true);
        await WaitAsync(() => vm.Model is { State: PoliciesState.Loaded });

        Assert.True(vm.UsesModel);
        Assert.True(vm.Model!.IsActive);
        Assert.Contains("policy list --json", calls);

        // And back (a downgrade, or the developer runtime selector): the model is dropped and the table is read again.
        calls.Clear();
        runtime.Set = Installed;
        _ = await services.Runtime.RefreshAsync(force: true);
        await WaitAsync(() => vm.UsesClassic && calls.Contains("policy list") && !vm.IsBusy && vm.State == PoliciesState.Loaded);

        Assert.Null(vm.Model);
        Assert.Equal(4, vm.Rows.Count);
        vm.SetActive(false);
    }

    [Fact]
    public async Task A_change_of_runtime_while_the_panel_was_away_is_applied_when_it_comes_back()
    {
        var runtime = new RuntimeProbeRunnerSource(Installed);
        var services = Create(runtime);
        var vm = new PoliciesPanelViewModel(services) { RunCli = Script(new List<string>()) };
        await vm.InitializeAsync();
        Assert.True(vm.UsesClassic);

        runtime.Set = Pinned;
        _ = await services.Runtime.RefreshAsync(force: true);
        Assert.True(vm.UsesClassic);

        vm.SetActive(true);
        await WaitAsync(() => vm.Model is { State: PoliciesState.Loaded });

        Assert.True(vm.UsesModel);
        vm.SetActive(false);
    }

    // ------------------------------------------------------------------ the shell's keys reach the model

    [Fact]
    public async Task F5_and_Escape_go_to_the_model_when_the_panel_is_the_model()
    {
        var services = Create(new RuntimeProbeRunnerSource(Pinned));
        var calls = new List<string>();
        var vm = new PoliciesPanelViewModel(services) { RunCli = Script(calls) };
        await vm.InitializeAsync();
        var model = vm.Model!;

        // The shell finds RefreshCommand by name on the panel's view-model.
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(2, calls.Count(c => c == "policy list --json"));

        model.SelectedNav = model.Nav.Single(n => n.View == "policies");
        model.SelectedRow = model.Rows.First();
        Assert.True(vm.HandleEscape());
        Assert.Null(model.SelectedRow);
        Assert.False(vm.HandleEscape());
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        for (var i = 0; i < 2000 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "the condition did not become true in time");
    }
}
