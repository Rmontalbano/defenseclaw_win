using System.Text.Json;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.AiRuntime;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Gateway;
using DefenseClaw.Core.Paths;
using DefenseClaw.Core.Runtime;
using DefenseClaw.Core.Security;

namespace DefenseClaw.App.Tests.Runtime;

/// <summary>
/// The Runtime panel's view-model (CUST-309): every state it can be in, that nothing is ever shown as clean while a plane is not up or the
/// snapshot is stale, the findings list and its inspector, and the three reviewed actions. The gateway and the CLI are scripts handed to the
/// view-model (<see cref="AiRuntimePanelViewModel.ReadSnapshot"/>, <see cref="AiRuntimePanelViewModel.RunPermissionsRead"/>,
/// <see cref="DiscoverActionReview.RunStep"/>): no socket opens and no process starts. The snapshots are synthetic, built by hand from
/// <c>internal/gateway/ai_runtime_api.go</c> (see <c>docs/RUNTIME-COMPAT-95159fd.md</c>).
/// </summary>
public sealed class AiRuntimePanelTests : IDisposable
{
    private const string Disabled = "ai-usage-runtime.json";
    private const string Populated = "ai-usage-runtime.populated.synthetic.json";
    private const string Degraded = "ai-usage-runtime.degraded.synthetic.json";
    private const string PlanesAb = "ai-usage-runtime.planes-ab.synthetic.json";

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

    // ------------------------------------------------------------------ scripts

    private static string Rest(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "runtime-95159fd", "rest", name));

    private static string PermissionsJson() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "runtime-95159fd", "cli", "agent-discovery-runtime-permissions.windows.synthetic.json"));

    private static GatewayResult<JsonDocument> Doc(string json) => GatewayResult<JsonDocument>.Ok(JsonDocument.Parse(json));

    /// <summary>What the gateway says next; counts the reads.</summary>
    private sealed class Gateway
    {
        public Func<GatewayResult<JsonDocument>> Answer { get; set; } = () => Doc(Rest(Populated));

        public int Reads { get; private set; }

        public Task<GatewayResult<JsonDocument>> Read(CancellationToken _)
        {
            Reads++;
            return Task.FromResult(Answer());
        }
    }

    private sealed record Scene(AiRuntimePanelViewModel Vm, Gateway Gateway, List<string> Ran, List<IReadOnlyList<string>> PermissionCalls, AppServices Services);

    private static CliInvocation Done(IReadOnlyList<string> argv, int exit, string output)
    {
        var invocation = InvocationFactory.Create(retainFullOutput: true, argv.ToArray());
        if (output.Length > 0)
        {
            InvocationFactory.Append(invocation, output);
        }

        InvocationFactory.Finish(invocation, exit);
        return invocation;
    }

    private async Task<AppServices> ServicesAsync(string runtimeSet = "95159fd", DefenseClawPaths? paths = null)
    {
        var services = AppServices.CreateIsolated(
            paths ?? TestServices.IsolatedPaths(_temp.Path),
            claudeSettingsPath: _temp.File("claude-settings.json"),
            runtimeProbeRunner: RuntimeFixtureRunner.For(runtimeSet));
        _services.Add(services);
        _ = await services.Runtime.RefreshAsync();
        return services;
    }

    private async Task<Scene> OpenAsync(string snapshot = Populated, string runtimeSet = "95159fd", DefenseClawPaths? paths = null, bool initialize = true)
    {
        var services = await ServicesAsync(runtimeSet, paths);
        var gateway = new Gateway { Answer = () => Doc(Rest(snapshot)) };
        var ran = new List<string>();
        var permissionCalls = new List<IReadOnlyList<string>>();
        var vm = new AiRuntimePanelViewModel(services)
        {
            ReadSnapshot = gateway.Read,
            RunPermissionsRead = (argv, _) =>
            {
                permissionCalls.Add(argv.ToArray());
                return Task.FromResult(Done(argv, 0, PermissionsJson()));
            },
        };
        vm.Review.RunStep = (executable, argv, _) =>
        {
            ran.Add(executable + " " + string.Join(' ', argv));
            return Task.FromResult(Done(argv, 0, "ok"));
        };

        if (initialize)
        {
            await vm.InitializeAsync();
        }

        return new Scene(vm, gateway, ran, permissionCalls, services);
    }

    private static async Task RefreshAsync(Scene scene) => await scene.Vm.RefreshCommand.ExecuteAsync(null);

    // ------------------------------------------------------------------ the states

    [Fact]
    public async Task A_disabled_runtime_gets_a_card_of_its_own_and_offers_enable_and_nothing_else()
    {
        var scene = await OpenAsync(Disabled);
        var vm = scene.Vm;

        Assert.Equal(AiRuntimeState.Disabled, vm.State);
        Assert.Equal(AiRuntimePanelViewModel.DisabledTitle, vm.StateTitle);
        Assert.Contains("defenseclaw agent discovery runtime enable", vm.StateDetail, StringComparison.Ordinal);
        Assert.True(vm.ShowStateCard);
        Assert.False(vm.ShowCoverage);
        Assert.False(vm.ShowFindings);
        Assert.True(vm.ShowEnableInState);
        Assert.Equal("Disabled", vm.CaptionText);

        Assert.True(vm.CanEnable);
        Assert.False(vm.CanPollNow);
        Assert.False(vm.CanDisable);
        Assert.Contains("disabled", vm.PollBlockedReason, StringComparison.Ordinal);
        Assert.Contains("disabled", vm.DisableBlockedReason, StringComparison.Ordinal);
        Assert.Equal("Enable…", vm.EnableButtonText);

        // Two buttons share the reason: it is said once, for both.
        Assert.Equal("Poll now and Disable are off: The runtime planes are disabled; enable them first.", vm.ActionsNote);
    }

    [Fact]
    public async Task A_populated_snapshot_shows_coverage_first_then_the_findings_worst_first()
    {
        var scene = await OpenAsync(Populated);
        var vm = scene.Vm;

        Assert.Equal(AiRuntimeState.Ready, vm.State);
        Assert.False(vm.ShowStateCard);
        Assert.True(vm.ShowCoverage);
        Assert.True(vm.ShowFindings);
        Assert.True(vm.ShowFindingsTable);
        Assert.False(vm.IsStale);

        Assert.Equal(new[] { "up", "up", "up" }, vm.Planes.Select(p => p.Badge).ToArray());
        Assert.All(vm.Planes, p => Assert.False(p.HasReason));
        Assert.Equal("All planes up", vm.CoverageBadgeText);
        Assert.Equal("Ok", vm.CoverageTone);

        Assert.Equal(new[] { 100, 60, 55, 44, 35, 18 }, vm.Rows.Select(r => r.Score).ToArray());
        Assert.Equal("6 findings", vm.FindingsCaption);
        // The verdict follows the count at once: a narrow window cuts the caption short and must not cut that.
        Assert.Equal(
            "6 findings · all planes up · polled " + AiRuntimePanelViewModel.Time(new DateTimeOffset(2030, 1, 15, 10, 4, 30, TimeSpan.Zero)) +
            " · 212 processes, 3 not fully readable, 148 connections, 6 with no owner",
            vm.CaptionText);
        Assert.Equal("host plane: 57 kernel events classified, 41 excluded outside AI-agent lineage", vm.HostPlaneText);

        Assert.True(vm.CanPollNow);
        Assert.True(vm.CanEnable);
        Assert.True(vm.CanDisable);
        Assert.Equal("Change settings…", vm.EnableButtonText);
        Assert.Equal(string.Empty, vm.ActionsNote);
    }

    [Fact]
    public async Task A_degraded_snapshot_puts_every_plane_that_is_not_up_on_the_page_with_its_whole_reason()
    {
        var scene = await OpenAsync(Degraded);
        var vm = scene.Vm;

        Assert.Equal("Partial coverage", vm.CoverageBadgeText);
        Assert.Equal("Warn", vm.CoverageTone);
        Assert.Equal("Partial coverage: 1 of 3 planes fully reporting.", vm.CoverageHeadline);
        Assert.Contains("DEGRADED", vm.CaptionText, StringComparison.Ordinal);

        var egress = vm.Planes.Single(p => p.Plane.Id == "b");
        var actions = vm.Planes.Single(p => p.Plane.Id == "c");
        Assert.Equal(("partial", "Warn", true), (egress.Badge, egress.ToneKey, egress.HasReason));
        Assert.Equal("egress attribution is limited to this process's own sockets; run the gateway elevated for machine-wide coverage", egress.Reason);
        Assert.Equal(("blind", "Bad", true), (actions.Badge, actions.ToneKey, actions.HasReason));
        Assert.Equal(
            "plane: Security event log unreadable: Access is denied. (the gateway needs an elevated token to read the Security channel)",
            actions.Reason);
        Assert.False(vm.Planes.Single(p => p.Plane.Id == "a").HasReason);

        Assert.True(vm.HasUnattributedWarning);
        Assert.StartsWith("54% of connections could not be attributed to a process.", vm.UnattributedWarning, StringComparison.Ordinal);
        Assert.False(vm.HasOtherGaps); // both degraded lines only restate a plane

        // Findings still list: the coverage above them is what says they are not the whole story.
        Assert.Equal(2, vm.Rows.Count);
    }

    [Fact]
    public async Task Planes_a_and_b_up_with_c_off_and_nothing_found_is_no_findings_beside_the_caveat_not_a_clean_host()
    {
        var scene = await OpenAsync(PlanesAb);
        var vm = scene.Vm;

        Assert.Equal(AiRuntimeState.Ready, vm.State);
        Assert.True(vm.ShowNoFindings);
        Assert.False(vm.ShowFindingsTable);
        Assert.False(vm.ShowNoMatch);
        Assert.Equal("Partial coverage", vm.CoverageBadgeText);
        Assert.Equal("Warn", vm.CoverageTone);

        var off = vm.Planes.Single(p => p.Plane.Id == "c");
        Assert.Equal(("off", "Neutral", true), (off.Badge, off.ToneKey, off.HasReason));
        Assert.Equal("not selected in ai_discovery.runtime.planes", off.Reason);

        Assert.StartsWith(AiRuntimeCoverage.NoFindingsCaveat, vm.NoFindingsDetail, StringComparison.Ordinal);
        Assert.EndsWith("Partial coverage: 2 of 3 planes fully reporting.", vm.NoFindingsDetail, StringComparison.Ordinal);
        Assert.DoesNotContain("all planes up", vm.CaptionText, StringComparison.Ordinal);
        Assert.StartsWith("0 findings · partial coverage · polled ", vm.CaptionText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Enabled_but_never_polled_says_no_poll_yet_and_still_shows_what_the_planes_report()
    {
        var scene = await OpenAsync(PlanesAb);
        const string neverPolled = """{"enabled":true,"findings":[],"planes":[{"plane":"a","name":"inference heartbeat","available":true,"running":false,"reason":"not started"}]}""";
        scene.Gateway.Answer = () => Doc(neverPolled);

        await RefreshAsync(scene);
        var vm = scene.Vm;

        Assert.Equal(AiRuntimeState.NoPoll, vm.State);
        Assert.Equal(AiRuntimePanelViewModel.NoPollTitle, vm.StateTitle);
        Assert.Contains("not a clean-host result", vm.StateDetail, StringComparison.Ordinal);
        Assert.True(vm.ShowCoverage);
        Assert.False(vm.ShowFindings);
        Assert.Equal("idle", vm.Planes.Single().Badge);
        Assert.Equal("not started", vm.Planes.Single().Reason);
        Assert.Equal("No poll yet", vm.CaptionText);
        Assert.True(vm.CanPollNow); // the way out of "no poll yet" is a poll
    }

    [Theory]
    [InlineData(404)]
    [InlineData(405)]
    public async Task A_gateway_without_the_route_is_unsupported_and_clears_what_was_shown(int status)
    {
        var scene = await OpenAsync(Populated);
        Assert.Equal(6, scene.Vm.Rows.Count);
        scene.Gateway.Answer = () => GatewayResult<JsonDocument>.Error("gateway returned HTTP " + status, status);

        await RefreshAsync(scene);
        var vm = scene.Vm;

        Assert.Equal(AiRuntimeState.Unsupported, vm.State);
        Assert.Equal(AiRuntimePanelViewModel.UnsupportedTitle, vm.StateTitle);
        Assert.Contains("limit of the version", vm.StateDetail, StringComparison.Ordinal);
        Assert.False(vm.IsStale);
        Assert.Empty(vm.Rows);
        Assert.Empty(vm.Planes);
        Assert.False(vm.ShowCoverage);
        Assert.False(vm.CanPollNow || vm.CanEnable || vm.CanDisable);
        Assert.Equal("This gateway does not serve the runtime planes.", vm.PollBlockedReason);
        Assert.Equal("Not supported by this gateway", vm.CaptionText);
    }

    [Fact]
    public async Task A_first_read_that_fails_is_unavailable_and_says_it_is_not_a_clean_host_result()
    {
        var services = await ServicesAsync();
        var gateway = new Gateway { Answer = () => GatewayResult<JsonDocument>.Unreachable("gateway is not listening (connection refused)") };
        var vm = new AiRuntimePanelViewModel(services) { ReadSnapshot = gateway.Read, RunPermissionsRead = (a, _) => Task.FromResult(Done(a, 0, PermissionsJson())) };

        await vm.InitializeAsync();

        Assert.Equal(AiRuntimeState.Unavailable, vm.State);
        Assert.Equal(AiRuntimePanelViewModel.UnavailableTitle, vm.StateTitle);
        Assert.Contains("The gateway is not answering.", vm.StateDetail, StringComparison.Ordinal);
        Assert.Contains("not a clean-host result", vm.StateDetail, StringComparison.Ordinal);
        Assert.False(vm.IsStale); // there is nothing older to call stale
        Assert.False(vm.ShowCoverage);
        Assert.False(vm.CanPollNow || vm.CanEnable || vm.CanDisable);
    }

    [Fact]
    public async Task A_first_answer_that_is_malformed_is_unavailable_never_an_empty_list()
    {
        var services = await ServicesAsync();
        var gateway = new Gateway { Answer = () => Doc("""{"enabled":true,"planes":[]}""") };
        var vm = new AiRuntimePanelViewModel(services) { ReadSnapshot = gateway.Read, RunPermissionsRead = (a, _) => Task.FromResult(Done(a, 0, PermissionsJson())) };

        await vm.InitializeAsync();

        Assert.Equal(AiRuntimeState.Unavailable, vm.State);
        Assert.Contains("incomplete", vm.StateDetail, StringComparison.Ordinal);
        Assert.False(vm.ShowNoFindings);
        Assert.False(vm.ShowFindings);
    }

    [Fact]
    public async Task While_the_first_read_is_on_its_way_nothing_is_claimed()
    {
        var scene = await OpenAsync(Populated, initialize: false);

        Assert.Equal(AiRuntimeState.Loading, scene.Vm.State);
        Assert.Equal(AiRuntimePanelViewModel.LoadingTitle, scene.Vm.StateTitle);
        Assert.True(scene.Vm.ShowStateCard);
        Assert.False(scene.Vm.CanPollNow || scene.Vm.CanEnable || scene.Vm.CanDisable);
        Assert.Equal("Reading runtime coverage…", scene.Vm.CaptionText);
    }

    // ------------------------------------------------------------------ stale

    [Theory]
    [InlineData("unreachable")]
    [InlineData("unauthorized")]
    [InlineData("not-connected")]
    [InlineData("server-error")]
    [InlineData("malformed")]
    [InlineData("parse-failure")]
    public async Task A_failed_refresh_keeps_the_last_good_snapshot_marked_stale_and_switches_the_actions_off(string failure)
    {
        var scene = await OpenAsync(Populated);
        var vm = scene.Vm;
        scene.Gateway.Answer = failure switch
        {
            "unreachable" => () => GatewayResult<JsonDocument>.Unreachable("gateway is not listening (connection refused)"),
            "unauthorized" => () => GatewayResult<JsonDocument>.Unauthorized(),
            "not-connected" => () => GatewayResult<JsonDocument>.NotConnected(),
            "server-error" => () => GatewayResult<JsonDocument>.Error("boom", 500),
            "malformed" => () => Doc("""{"enabled":true}"""),
            _ => () => GatewayResult<JsonDocument>.Error("could not parse gateway response", 200),
        };

        await RefreshAsync(scene);

        // What was on screen is still on screen, and says it is not current.
        Assert.True(vm.IsStale);
        Assert.Equal(AiRuntimeState.Ready, vm.State);
        Assert.Equal(6, vm.Rows.Count);
        Assert.Equal(3, vm.Planes.Count);
        Assert.StartsWith("STALE", vm.StaleBanner, StringComparison.Ordinal);
        Assert.Contains("polled", vm.StaleBanner, StringComparison.Ordinal);
        Assert.StartsWith("STALE · ", vm.CaptionText, StringComparison.Ordinal);
        Assert.NotEmpty(vm.StaleReason);

        // And the calm version of the coverage is gone: a stale "all planes up" is not a statement about now.
        Assert.Equal("Stale", vm.CoverageBadgeText);
        Assert.Equal("Warn", vm.CoverageTone);
        Assert.StartsWith("As of the last successful poll", vm.CoverageHeadline, StringComparison.Ordinal);

        // The chips lose their green too: a stale "up" says nothing about now.
        Assert.All(vm.Planes, plane => Assert.Equal("Neutral", plane.ToneKey));
        Assert.All(vm.Planes, plane => Assert.EndsWith("(last poll)", plane.ChipText, StringComparison.Ordinal));

        Assert.False(vm.CanPollNow || vm.CanEnable || vm.CanDisable);
        Assert.Contains("stale", vm.PollBlockedReason, StringComparison.Ordinal);
        Assert.Contains("stale", vm.ActionsNote, StringComparison.Ordinal);
        Assert.False(vm.PollNowCommand.CanExecute(null));
        Assert.False(vm.EnablePlanesCommand.CanExecute(null));
        Assert.False(vm.DisablePlanesCommand.CanExecute(null));

        // A read that works clears it and gives the buttons back.
        scene.Gateway.Answer = () => Doc(Rest(Populated));
        await RefreshAsync(scene);

        Assert.False(vm.IsStale);
        Assert.Equal(string.Empty, vm.StaleBanner);
        Assert.Equal("All planes up", vm.CoverageBadgeText);
        Assert.All(vm.Planes, plane => Assert.Equal("Ok", plane.ToneKey));
        Assert.True(vm.CanPollNow && vm.CanEnable && vm.CanDisable);
    }

    [Fact]
    public async Task A_plane_that_was_down_keeps_the_colour_of_its_problem_while_the_snapshot_is_stale()
    {
        var scene = await OpenAsync(Degraded);
        scene.Gateway.Answer = () => GatewayResult<JsonDocument>.Unreachable();

        await RefreshAsync(scene);

        Assert.Equal(new[] { "Neutral", "Warn", "Bad" }, scene.Vm.Planes.Select(p => p.ToneKey).ToArray());
    }

    [Fact]
    public async Task The_gateway_stopping_marks_the_snapshot_stale_and_its_return_reads_it_again()
    {
        var scene = await OpenAsync(Populated);
        var vm = scene.Vm;
        var reads = scene.Gateway.Reads;

        vm.NoteGatewayState(AppGatewayState.GatewayStopped);
        Assert.True(vm.IsStale);
        Assert.Contains("gateway is not running", vm.StaleReason, StringComparison.Ordinal);
        Assert.Equal(reads, scene.Gateway.Reads); // marking stale reads nothing
        Assert.False(vm.CanPollNow);

        vm.NoteGatewayState(AppGatewayState.Running);
        // Wait for the condition, not for a time: a loaded CI runner is much slower than this machine.
        for (var i = 0; i < 2000 && vm.IsStale; i++)
        {
            await Task.Delay(10);
        }

        Assert.False(vm.IsStale);
        Assert.Equal(reads + 1, scene.Gateway.Reads);
        Assert.True(vm.CanPollNow);
    }

    [Theory]
    [InlineData(AppGatewayState.Unknown)]
    [InlineData(AppGatewayState.Running)]
    [InlineData(AppGatewayState.Degraded)]
    public async Task A_gateway_that_answers_even_badly_is_asked_not_assumed_down(AppGatewayState state)
    {
        var scene = await OpenAsync(Populated);

        scene.Vm.NoteGatewayState(state);

        Assert.False(scene.Vm.IsStale);
    }

    [Fact]
    public async Task A_read_asked_for_while_one_is_running_is_served_after_it_not_lost()
    {
        var services = await ServicesAsync();
        var gate = new TaskCompletionSource<GatewayResult<JsonDocument>>();
        var reads = 0;
        var vm = new AiRuntimePanelViewModel(services)
        {
            ReadSnapshot = _ =>
            {
                reads++;
                return reads == 1 ? gate.Task : Task.FromResult(Doc(Rest(Populated)));
            },
        };

        var first = vm.RefreshCommand.ExecuteAsync(null);
        await vm.RefreshCommand.ExecuteAsync(null); // returns at once: one is already running
        Assert.Equal(1, reads);

        gate.SetResult(Doc(Rest(PlanesAb)));
        await first;

        Assert.Equal(2, reads);
        Assert.Equal(6, vm.Rows.Count); // the second read's answer is what stands
    }

    // ------------------------------------------------------------------ nothing is clean

    [Fact]
    public async Task No_state_of_the_panel_says_clean_except_to_deny_it_and_the_calm_badge_needs_three_planes_up_and_a_fresh_snapshot()
    {
        var denials = new[]
        {
            AiRuntimeCoverage.NoFindingsCaveat,
            AiRuntimePanelViewModel.UnavailableDetail,
            AiRuntimePanelViewModel.NoPollDetail,
        };

        var seen = new List<(string Name, AiRuntimePanelViewModel Vm)>();
        foreach (var file in new[] { Disabled, Populated, Degraded, PlanesAb })
        {
            seen.Add((file, (await OpenAsync(file)).Vm));
        }

        var stale = await OpenAsync(Populated);
        stale.Gateway.Answer = () => GatewayResult<JsonDocument>.Unreachable();
        await RefreshAsync(stale);
        seen.Add(("stale", stale.Vm));

        var unsupported = await OpenAsync(Populated);
        unsupported.Gateway.Answer = () => GatewayResult<JsonDocument>.Error("x", 404);
        await RefreshAsync(unsupported);
        seen.Add(("unsupported", unsupported.Vm));

        foreach (var (name, vm) in seen)
        {
            var text = string.Join(
                '\n',
                new[]
                {
                    vm.CaptionText, vm.StateTitle, vm.StateDetail, vm.CoverageBadgeText, vm.CoverageHeadline, vm.NoFindingsDetail, vm.StaleBanner,
                    vm.FindingsCaption, vm.CountsText, vm.HostPlaneText, vm.ReadText, vm.UnattributedWarning, vm.ActionsNote,
                }
                .Concat(vm.Planes.SelectMany(p => new[] { p.ChipText, p.Reason, p.AutomationName }))
                .Concat(vm.OtherGaps));
            foreach (var denial in denials)
            {
                text = text.Replace(denial, string.Empty, StringComparison.Ordinal);
            }

            Assert.DoesNotContain("clean", text, StringComparison.OrdinalIgnoreCase);

            // The calm badge is earned by three planes fully up and a snapshot that is current, and by nothing else.
            var allUp = vm.Planes.Count == 3 && vm.Planes.All(p => p.Plane.IsFullyUp);
            var earned = vm.State == AiRuntimeState.Ready && allUp && !vm.IsStale && !(vm.HasUnattributedWarning || vm.HasOtherGaps);
            Assert.Equal(earned, vm.CoverageTone == "Ok");
        }

        Assert.Equal("Ok", seen.Single(s => s.Name == Populated).Vm.CoverageTone);
        Assert.Equal("Warn", seen.Single(s => s.Name == Degraded).Vm.CoverageTone);
        Assert.Equal("Warn", seen.Single(s => s.Name == PlanesAb).Vm.CoverageTone);
        Assert.Equal("Warn", seen.Single(s => s.Name == "stale").Vm.CoverageTone);
    }

    // ------------------------------------------------------------------ findings: filter, selection, escape

    [Fact]
    public async Task The_filter_matches_the_fields_the_mac_searches_and_a_filter_that_hides_everything_says_so()
    {
        var scene = await OpenAsync(Populated);
        var vm = scene.Vm;

        void Expect(string filter, params int[] pids)
        {
            vm.FilterText = filter;
            Assert.Equal(pids.Order().ToArray(), vm.Rows.Select(r => r.Pid).Order().ToArray());
        }

        Expect("ollama", 9912);
        Expect("http.server", 3320);                    // command line
        Expect("claude-code", 4120);                    // agent
        Expect("critical", 4120);                       // severity
        Expect("provider-a", 7788);                     // provider
        Expect("unobserved", 9912, 3320);               // inventory verdict
        Expect("node accounted", 7788, 5504);           // every word
        Expect("operator", 4120, 7788, 9912, 3320, 5504, 6001);

        vm.FilterText = "no-such-process";
        Assert.Empty(vm.Rows);
        Assert.True(vm.ShowNoMatch);
        Assert.False(vm.ShowNoFindings);
        Assert.False(vm.ShowFindingsTable);
        Assert.Equal("0 of 6 findings", vm.FindingsCaption);

        vm.ClearFilterCommand.Execute(null);
        Assert.Equal(6, vm.Rows.Count);
        Assert.False(vm.ShowNoMatch);
    }

    [Fact]
    public async Task The_selection_follows_a_finding_across_a_refresh_and_an_unchanged_row_keeps_its_identity()
    {
        var scene = await OpenAsync(Populated);
        var vm = scene.Vm;
        var chosen = vm.Rows.Single(r => r.Pid == 7788);
        var before = vm.Rows.ToArray();
        vm.SelectedFinding = chosen;
        Assert.True(vm.HasSelection);

        await RefreshAsync(scene); // the same answer

        Assert.Same(chosen, vm.SelectedFinding);
        Assert.True(vm.Rows.Zip(before).All(pair => ReferenceEquals(pair.First, pair.Second)));

        // A change to that finding replaces its row; the selection is its id and moves to the new row.
        scene.Gateway.Answer = () => Doc(Rest(Populated).Replace("\"score\": 55", "\"score\": 56", StringComparison.Ordinal));
        await RefreshAsync(scene);

        Assert.NotSame(chosen, vm.SelectedFinding);
        Assert.Equal(chosen.Id, vm.SelectedFinding!.Id);
        Assert.Equal("56", vm.SelectedFinding.ScoreText);
        Assert.Equal(7788, vm.SelectedFinding.Pid);
    }

    [Fact]
    public async Task A_selection_whose_finding_is_gone_or_filtered_out_clears_so_the_inspector_never_describes_an_exited_process()
    {
        var scene = await OpenAsync(Populated);
        var vm = scene.Vm;

        vm.SelectedFinding = vm.Rows.Single(r => r.Pid == 9912);
        vm.FilterText = "node"; // the selected process is not among those
        Assert.Null(vm.SelectedFinding);
        Assert.False(vm.HasSelection);

        vm.FilterText = string.Empty;
        vm.SelectedFinding = vm.Rows.Single(r => r.Pid == 9912);
        scene.Gateway.Answer = () => Doc(Rest(PlanesAb)); // no findings at all now
        await RefreshAsync(scene);

        Assert.Null(vm.SelectedFinding);
        Assert.Empty(vm.Rows);
    }

    [Fact]
    public async Task The_inspector_has_everything_about_the_selected_finding_including_an_unobserved_verdict()
    {
        var scene = await OpenAsync(Populated);
        var chain = scene.Vm.Rows[0];

        Assert.Equal("Critical, score 100", chain.Headline);
        Assert.True(chain.HasChain);
        Assert.Equal("credential_access -> identity_creation -> exfiltration", chain.Chain);
        Assert.True(chain.HasCmdline);
        Assert.DoesNotContain("synthetic-synthetic", chain.Cmdline, StringComparison.Ordinal);
        Assert.Equal(new[] { "+28", "+42", "+33", "+25" }, chain.SignalRows.Select(s => s.WeightText).ToArray());
        Assert.Equal("unaccounted", chain.Verdict);
        Assert.Equal("Warn", chain.VerdictTone);

        var unobserved = scene.Vm.Rows.Single(r => r.Pid == 9912);
        Assert.Equal("unobserved", unobserved.Verdict);
        Assert.Equal("Neutral", unobserved.VerdictTone);
        Assert.True(unobserved.HasVerdictReason);

        var egress = scene.Vm.Rows.Single(r => r.Pid == 7788);
        Assert.Equal(2, egress.ProviderRows.Count);
        Assert.Equal("api.provider-a.example:443", egress.ProviderRows[0].Display);
        Assert.Equal("api.provider-a.example:8443", egress.ProviderRows[1].Display);
        Assert.Equal("00000000-0000-4000-8000-000000000a01", egress.VerdictMatches);
        Assert.Equal("coding_assistant", egress.VerdictCategories);
        Assert.Equal("—", scene.Vm.Rows.Single(r => r.Pid == 9912).Providers);
        Assert.Equal("—", scene.Vm.Rows.Single(r => r.Pid == 9912).Agent);
    }

    [Fact]
    public async Task Escape_closes_the_review_first_then_the_inspector_then_does_nothing()
    {
        var scene = await OpenAsync(Populated);
        var vm = scene.Vm;
        vm.SelectedFinding = vm.Rows[0];
        vm.PollNowCommand.Execute(null);
        Assert.True(vm.Review.IsOpen);

        Assert.True(vm.HandleEscape());
        Assert.False(vm.Review.IsOpen);
        Assert.True(vm.HasSelection);

        Assert.True(vm.HandleEscape());
        Assert.False(vm.HasSelection);

        Assert.False(vm.HandleEscape());
    }

    [Fact]
    public async Task More_findings_than_the_panel_lists_are_counted_not_silently_dropped()
    {
        var scene = await OpenAsync(Populated);
        var extra = string.Join(',', Enumerable.Range(0, 1100).Select(i => $$"""{"finding_id":"x{{i}}","pid":{{i + 10}},"process":"p{{i}}","score":40,"severity":"medium"}"""));
        scene.Gateway.Answer = () => Doc($$"""{"enabled":true,"scanned_at":"2030-01-15T10:04:30Z","planes":[],"findings":[{{extra}}]}""");

        await RefreshAsync(scene);

        Assert.Equal(AiRuntimeReader.MaxFindings, scene.Vm.Rows.Count);
        Assert.Equal(
            "1,000 findings; 100 more were not kept (the worst 1,000 are listed)",
            scene.Vm.FindingsCaption);
    }

    // ------------------------------------------------------------------ actions

    [Fact]
    public async Task Poll_now_shows_the_exact_command_runs_nothing_until_confirmed_and_reads_the_snapshot_again_after()
    {
        var scene = await OpenAsync(Populated);
        var vm = scene.Vm;
        var reads = scene.Gateway.Reads;

        vm.PollNowCommand.Execute(null);

        Assert.True(vm.Review.IsOpen);
        var review = vm.Review.CommandReview!;
        Assert.Equal("defenseclaw agent discovery runtime scan", review.CommandText);
        var step = Assert.Single(review.Steps);
        Assert.Equal(CommandTier.StateChanging, step.Tier);
        Assert.Equal(new[] { "agent", "discovery", "runtime", "scan" }, step.Argv.ToArray());
        Assert.Empty(scene.Ran);
        Assert.Equal(reads, scene.Gateway.Reads);

        await vm.Review.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "defenseclaw agent discovery runtime scan" }, scene.Ran);
        Assert.Equal(reads + 1, scene.Gateway.Reads);
        Assert.Contains("finished at", vm.LastRunSummary, StringComparison.Ordinal);
        Assert.Contains("(exit 0)", vm.LastRunSummary, StringComparison.Ordinal);
        Assert.True(vm.HasLastRun);
    }

    [Fact]
    public async Task Cancelling_a_review_runs_nothing()
    {
        var scene = await OpenAsync(Populated);

        scene.Vm.PollNowCommand.Execute(null);
        scene.Vm.Review.DismissCommand.Execute(null);

        Assert.False(scene.Vm.Review.IsOpen);
        Assert.Empty(scene.Ran);
    }

    [Fact]
    public async Task A_run_that_fails_is_reported_on_the_panel_and_the_snapshot_is_still_read_again()
    {
        var scene = await OpenAsync(Populated);
        var vm = scene.Vm;
        var reads = scene.Gateway.Reads;
        vm.Review.RunStep = (executable, argv, _) => Task.FromResult(Done(argv, 1, "the runtime planes are disabled in config"));

        vm.PollNowCommand.Execute(null);
        await vm.Review.ConfirmCommand.ExecuteAsync(null);

        Assert.Contains("(exit 1)", vm.LastRunSummary, StringComparison.Ordinal);
        Assert.Equal(reads + 1, scene.Gateway.Reads);
        Assert.True(vm.Review.IsFinished);
    }

    [Fact]
    public async Task Enable_carries_exactly_the_chosen_flags_and_says_what_each_one_does_before_the_command_does()
    {
        var scene = await OpenAsync(Disabled);
        var vm = scene.Vm;
        vm.HostPlaneIndex = 1;
        vm.DnsCaptureIndex = 2;
        vm.PollIntervalText = " 30 ";
        vm.MinRiskText = "45";
        vm.RestartOnChange = false;

        vm.EnablePlanesCommand.Execute(null);

        Assert.True(vm.Review.IsOpen);
        var review = vm.Review.CommandReview!;
        Assert.Equal(
            "defenseclaw agent discovery runtime enable --yes --enable-host-plane --no-dns-capture --poll-interval-s 30 --min-risk-to-report 45 --no-restart",
            review.CommandText);
        Assert.Equal("Turn on the runtime planes?", review.Title);
        Assert.Contains("Plane C (agent actions): turned on.", review.Summary, StringComparison.Ordinal);
        Assert.Contains("DNS capture: turned off", review.Summary, StringComparison.Ordinal);
        Assert.Contains("Poll interval: 30 seconds.", review.Summary, StringComparison.Ordinal);
        Assert.Contains("findings need a score of 45 or more", review.Summary, StringComparison.Ordinal);
        Assert.Contains("--no-restart", review.Summary, StringComparison.Ordinal);
        Assert.Contains(review.Warnings, w => w.Message == AiRuntimePanelViewModel.PlaneCWarning);
        Assert.DoesNotContain(review.Warnings, w => w.Title == "Gateway restart");
        Assert.False(review.RestartsGateway);
        Assert.Empty(scene.Ran);

        await vm.Review.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal(
            new[] { "defenseclaw agent discovery runtime enable --yes --enable-host-plane --no-dns-capture --poll-interval-s 30 --min-risk-to-report 45 --no-restart" },
            scene.Ran);
    }

    [Fact]
    public async Task Enable_with_nothing_chosen_changes_nothing_but_the_restart_is_a_warning_on_the_review()
    {
        var scene = await OpenAsync(Disabled);

        scene.Vm.EnablePlanesCommand.Execute(null);

        var review = scene.Vm.Review.CommandReview!;
        Assert.Equal("defenseclaw agent discovery runtime enable --yes", review.CommandText);
        Assert.True(review.RestartsGateway);
        Assert.Contains(review.Warnings, w => w.Title == "Gateway restart");
        Assert.DoesNotContain(review.Warnings, w => w.Message == AiRuntimePanelViewModel.PlaneCWarning);
        Assert.Contains("left as it is", review.Summary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("abc", "", "poll interval must be a whole number")]
    [InlineData("10 seconds", "", "poll interval must be a whole number")]
    [InlineData("-5", "", "poll interval must be a whole number")]
    [InlineData("4", "", "between 5 and 3600 seconds")]
    [InlineData("3601", "", "between 5 and 3600 seconds")]
    [InlineData("", "0", "between 1 and 100")]
    [InlineData("", "101", "between 1 and 100")]
    [InlineData("", "high", "reporting floor must be a whole number")]
    public async Task A_number_the_cli_would_refuse_is_refused_here_with_the_range_and_no_review_opens(string interval, string floor, string expected)
    {
        var scene = await OpenAsync(Disabled);
        scene.Vm.PollIntervalText = interval;
        scene.Vm.MinRiskText = floor;

        scene.Vm.EnablePlanesCommand.Execute(null);

        Assert.False(scene.Vm.Review.IsOpen);
        Assert.True(scene.Vm.HasOptionsError);
        Assert.Contains(expected, scene.Vm.OptionsError, StringComparison.Ordinal);
        Assert.Empty(scene.Ran);
    }

    [Fact]
    public async Task While_the_planes_are_on_enable_becomes_change_settings_and_the_error_clears_when_the_form_is_right()
    {
        var scene = await OpenAsync(Populated);
        var vm = scene.Vm;
        Assert.Equal("Change settings…", vm.EnableButtonText);

        vm.PollIntervalText = "2";
        vm.EnablePlanesCommand.Execute(null);
        Assert.True(vm.HasOptionsError);
        Assert.False(vm.Review.IsOpen);

        vm.PollIntervalText = "120";
        vm.EnablePlanesCommand.Execute(null);

        Assert.False(vm.HasOptionsError);
        Assert.Equal("Apply these runtime plane settings?", vm.Review.CommandReview!.Title);
        Assert.Equal("defenseclaw agent discovery runtime enable --yes --poll-interval-s 120", vm.Review.CommandReview.CommandText);
    }

    [Theory]
    [InlineData(true, "defenseclaw agent discovery runtime disable --yes")]
    [InlineData(false, "defenseclaw agent discovery runtime disable --yes --no-restart")]
    public async Task Disable_is_reviewed_and_carries_no_restart_only_when_unticked(bool restart, string expected)
    {
        var scene = await OpenAsync(Populated);
        scene.Vm.RestartOnChange = restart;

        scene.Vm.DisablePlanesCommand.Execute(null);

        var review = scene.Vm.Review.CommandReview!;
        Assert.Equal(expected, review.CommandText);
        Assert.Equal(restart, review.RestartsGateway);
        Assert.Equal(CommandTier.StateChanging, review.Tier);
        Assert.Empty(scene.Ran);
    }

    [Fact]
    public async Task One_change_at_a_time_while_a_review_is_open_every_button_is_off()
    {
        var scene = await OpenAsync(Populated);

        scene.Vm.PollNowCommand.Execute(null);

        Assert.True(scene.Vm.Review.IsOpen);
        Assert.False(scene.Vm.CanPollNow || scene.Vm.CanEnable || scene.Vm.CanDisable);
        Assert.Contains("Another change is open", scene.Vm.ActionsNote, StringComparison.Ordinal);

        scene.Vm.Review.DismissCommand.Execute(null);
        Assert.True(scene.Vm.CanPollNow);
    }

    [Fact]
    public async Task A_runtime_without_the_subcommand_gets_that_button_off_whatever_the_snapshot_says()
    {
        var scene = await OpenAsync(Populated, runtimeSet: "0.8.10");

        Assert.Equal(AiRuntimeState.Ready, scene.Vm.State); // the panel can still read what a gateway serves
        Assert.False(scene.Vm.CanPollNow);
        Assert.False(scene.Vm.CanEnable);
        Assert.False(scene.Vm.CanDisable);
        Assert.Equal("This DefenseClaw runtime does not list 'agent discovery runtime scan'.", scene.Vm.PollBlockedReason);
        Assert.Equal("This DefenseClaw runtime does not list 'agent discovery runtime enable'.", scene.Vm.EnableBlockedReason);
        Assert.False(scene.Vm.PollNowCommand.CanExecute(null));
        scene.Vm.PollNowCommand.Execute(null);
        Assert.False(scene.Vm.Review.IsOpen);
    }

    [Fact]
    public async Task A_read_only_data_folder_switches_every_change_off_with_its_reason()
    {
        var paths = new DefenseClawPaths(
            dataDirectory: _temp.Path,
            binDirectory: _temp.File("no-such-bin"),
            searchPath: Array.Empty<string>(),
            runtime: RuntimeSelection.ForContainer("synthetic-container", "http://127.0.0.1:18971", _temp.Path));
        var scene = await OpenAsync(Populated, paths: paths);

        Assert.True(paths.DataDirectoryReadOnly);
        Assert.False(scene.Vm.CanPollNow || scene.Vm.CanEnable || scene.Vm.CanDisable);
        Assert.Contains("read-only copy", scene.Vm.PollBlockedReason, StringComparison.Ordinal);
        Assert.Equal(scene.Vm.PollBlockedReason, scene.Vm.ActionsNote); // one shared reason is said once
        Assert.Equal(AiRuntimeState.Ready, scene.Vm.State);             // reading is not changing
    }

    // ------------------------------------------------------------------ argv: no --grant, no secret, nowhere

    [Fact]
    public async Task Whatever_the_panel_runs_never_has_grant_or_revert_or_a_secret_on_its_command_line()
    {
        var scene = await OpenAsync(Disabled);
        var vm = scene.Vm;

        // enable (every option), then poll, then disable, each confirmed; plus the prerequisites check twice.
        vm.HostPlaneIndex = 1;
        vm.DnsCaptureIndex = 1;
        vm.PollIntervalText = "60";
        vm.MinRiskText = "30";
        vm.EnablePlanesCommand.Execute(null);
        await vm.Review.ConfirmCommand.ExecuteAsync(null);
        vm.Review.DismissCommand.Execute(null);

        scene.Gateway.Answer = () => Doc(Rest(Populated));
        await RefreshAsync(scene);
        vm.PollNowCommand.Execute(null);
        await vm.Review.ConfirmCommand.ExecuteAsync(null);
        vm.Review.DismissCommand.Execute(null);
        vm.DisablePlanesCommand.Execute(null);
        await vm.Review.ConfirmCommand.ExecuteAsync(null);
        vm.Review.DismissCommand.Execute(null);
        await vm.CheckPrerequisitesCommand.ExecuteAsync(null);

        Assert.Equal(3, scene.Ran.Count);
        Assert.True(scene.PermissionCalls.Count >= 2);
        foreach (var line in scene.Ran)
        {
            Assert.DoesNotContain("--grant", line, StringComparison.Ordinal);
            Assert.DoesNotContain("--revert", line, StringComparison.Ordinal);
            Assert.StartsWith("defenseclaw agent discovery runtime ", line, StringComparison.Ordinal);
            Assert.All(line.Split(' '), token => Assert.False(SecretHeuristics.LooksSecret(token), token));
        }

        // Every permissions read is the one read-only argv, with nothing added.
        Assert.All(scene.PermissionCalls, argv => Assert.True(AiRuntimeCommands.IsPermissionsRead(argv), string.Join(' ', argv)));
        Assert.All(scene.PermissionCalls, argv => Assert.DoesNotContain("--grant", argv));
    }

    // ------------------------------------------------------------------ the prerequisites card

    [Fact]
    public async Task The_prerequisites_card_reads_permissions_json_and_lists_every_grant_with_its_state()
    {
        var scene = await OpenAsync(Populated);
        var vm = scene.Vm;

        Assert.Equal(AiRuntimePrerequisitesState.Ready, vm.PrerequisitesState);
        var call = Assert.Single(scene.PermissionCalls);
        Assert.Equal(new[] { "agent", "discovery", "runtime", "permissions", "--json" }, call.ToArray());

        Assert.Equal(5, vm.Grants.Count);
        Assert.Equal(new[] { "missing", "missing", "unknown", "missing", "unknown" }, vm.Grants.Select(g => g.StateText).ToArray());
        Assert.Equal(new[] { "Bad", "Bad", "Warn", "Bad", "Warn" }, vm.Grants.Select(g => g.ToneKey).ToArray());
        Assert.Equal(
            "3 missing, 2 could not be verified from here. An unknown is not a failure: it is a grant this process cannot verify, not one that is absent.",
            vm.PrerequisitesSummary);
        Assert.True(vm.ShowGrants);
        Assert.False(vm.ShowPrerequisitesMessage);
        Assert.False(vm.HasGuidanceOs);
        Assert.Contains("work without elevation for your own processes", AiRuntimePanelViewModel.WindowsNote, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_copy_only_lines_are_the_auditpol_and_reg_commands_and_copying_puts_them_on_the_clipboard_without_running_anything()
    {
        var scene = await OpenAsync(Populated);
        var vm = scene.Vm;
        string? copied = null;
        vm.ClipboardWriter = text =>
        {
            copied = text;
            return true;
        };

        Assert.True(vm.HasCommands);
        var lines = vm.CommandsText.Split(Environment.NewLine);
        Assert.Equal(4, lines.Length);
        Assert.StartsWith("auditpol /set /subcategory:\"Process Creation\"", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("auditpol /set /subcategory:\"User Account Management\"", lines[1], StringComparison.Ordinal);
        Assert.StartsWith("auditpol /set /subcategory:\"Sensitive Privilege Use\"", lines[2], StringComparison.Ordinal);
        Assert.StartsWith("reg add HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Policies\\System\\Audit /v ProcessCreationIncludeCmdLine_Enabled", lines[3], StringComparison.Ordinal);

        vm.CopyCommandsCommand.Execute(null);

        Assert.Equal(vm.CommandsText, copied);
        Assert.StartsWith("Copied.", vm.CopyNote, StringComparison.Ordinal);
        Assert.True(vm.HasCopyNote);
        Assert.Empty(scene.Ran);
        Assert.Contains("never runs 'permissions --grant'", AiRuntimePanelViewModel.CopyOnlyNote, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_copy_that_fails_says_so_and_a_card_with_no_commands_copies_nothing()
    {
        var scene = await OpenAsync(Populated);
        scene.Vm.ClipboardWriter = _ => false;

        scene.Vm.CopyCommandsCommand.Execute(null);
        Assert.Equal(DefenseClaw.App.Views.Controls.DcClipboard.FailureText, scene.Vm.CopyNote);

        var wrote = false;
        scene.Vm.ClipboardWriter = _ => wrote = true;
        scene.Vm.RunPermissionsRead = (argv, _) => Task.FromResult(Done(argv, 0, """{"os":"windows","grants":[]}"""));
        await scene.Vm.CheckPrerequisitesCommand.ExecuteAsync(null);
        Assert.False(scene.Vm.HasCommands);
        scene.Vm.CopyCommandsCommand.Execute(null);
        Assert.False(wrote);
    }

    [Fact]
    public async Task Guidance_for_another_os_says_whose_it_is()
    {
        var scene = await OpenAsync(Populated);
        scene.Vm.RunPermissionsRead = (argv, _) => Task.FromResult(Done(
            argv, 0, """{"os":"linux","checked_this_host":true,"grants":[{"plane":"shadow egress (B)","needs":"root","why":"w","how":"run the gateway as root","granted":false,"probe":"cap_dac"}]}"""));

        await scene.Vm.CheckPrerequisitesCommand.ExecuteAsync(null);

        Assert.True(scene.Vm.HasGuidanceOs);
        Assert.Equal("This guidance is for linux, where the runtime runs, not for this PC.", scene.Vm.GuidanceOsNote);
        Assert.False(scene.Vm.HasCommands);
    }

    [Theory]
    [InlineData("exit")]
    [InlineData("garbage")]
    [InlineData("not-found")]
    [InlineData("throws")]
    [InlineData("failure-reason")]
    public async Task A_prerequisites_check_that_does_not_complete_assumes_nothing_is_granted(string failure)
    {
        var scene = await OpenAsync(Populated);
        scene.Vm.RunPermissionsRead = (argv, _) => failure switch
        {
            "exit" => Task.FromResult(Done(argv, 2, "usage error")),
            "garbage" => Task.FromResult(Done(argv, 0, "<html>no</html>")),
            "not-found" => throw new CliNotFoundException("defenseclaw", new[] { "PATH" }),
            "throws" => throw new InvalidOperationException("boom"),
            _ => Task.FromResult(WithFailure(argv)),
        };

        await scene.Vm.CheckPrerequisitesCommand.ExecuteAsync(null);

        Assert.Equal(AiRuntimePrerequisitesState.Unavailable, scene.Vm.PrerequisitesState);
        Assert.Empty(scene.Vm.Grants);
        Assert.False(scene.Vm.ShowGrants);
        Assert.True(scene.Vm.ShowPrerequisitesMessage);
        Assert.StartsWith("The permissions check did not complete", scene.Vm.PrerequisitesMessage, StringComparison.Ordinal);
        Assert.EndsWith("Nothing is assumed to be granted.", scene.Vm.PrerequisitesMessage, StringComparison.Ordinal);
        Assert.False(scene.Vm.HasCommands);

        static CliInvocation WithFailure(IReadOnlyList<string> argv)
        {
            var invocation = Done(argv, 0, string.Empty);
            typeof(CliInvocation).GetProperty(nameof(CliInvocation.FailureReason))!.SetValue(invocation, "timed out after 60 s");
            return invocation;
        }
    }

    [Fact]
    public async Task A_runtime_without_the_permissions_command_has_nothing_to_check_and_asks_nothing()
    {
        var scene = await OpenAsync(Populated, runtimeSet: "0.8.10");

        Assert.Equal(AiRuntimePrerequisitesState.NotSupported, scene.Vm.PrerequisitesState);
        Assert.Empty(scene.PermissionCalls);
        Assert.Contains("no 'agent discovery runtime permissions' command", scene.Vm.PrerequisitesMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_again_asks_again_and_a_grant_that_is_now_in_place_drops_its_copy_lines()
    {
        var scene = await OpenAsync(Populated);
        Assert.True(scene.Vm.HasCommands);
        scene.Vm.RunPermissionsRead = (argv, _) =>
        {
            scene.PermissionCalls.Add(argv.ToArray());
            return Task.FromResult(Done(
                argv,
                0,
                """
                {"os":"windows","checked_this_host":true,"grants":[
                  {"plane":"agent actions (C), process and identity events","needs":"n","why":"w","how":"auditpol /set /subcategory:\"Process Creation\" /success:enable /failure:enable   (also User Account Management, Sensitive Privilege Use)","granted":true,"probe":"win_audit_proc"},
                  {"plane":"agent actions (C), command lines","needs":"n","why":"w","how":"reg add HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Policies\\System\\Audit /v ProcessCreationIncludeCmdLine_Enabled /t REG_DWORD /d 1 /f","granted":false,"probe":"win_cmdline"}]}
                """));
        };

        await scene.Vm.CheckPrerequisitesCommand.ExecuteAsync(null);

        Assert.Equal(2, scene.PermissionCalls.Count);
        Assert.Equal(new[] { "granted", "missing" }, scene.Vm.Grants.Select(g => g.StateText).ToArray());
        var only = Assert.Single(scene.Vm.CommandsText.Split(Environment.NewLine));
        Assert.StartsWith("reg add ", only, StringComparison.Ordinal);
        Assert.Equal(string.Empty, scene.Vm.Grants[0].How); // a grant in place shows no "how"
    }

    // ------------------------------------------------------------------ the catalog

    [Fact]
    public async Task The_catalog_builds_this_view_model_for_the_ai_runtime_panel_and_it_requires_the_capability()
    {
        var services = await ServicesAsync();
        var catalog = new PanelCatalog(services);

        var panel = catalog.ById("ai-runtime");

        Assert.NotNull(panel);
        Assert.Equal("Runtime", panel.Title);
        Assert.Equal("Discover", panel.Group);
        Assert.Equal(RuntimeCapability.AiRuntime, panel.Requires);
        Assert.Equal(typeof(DefenseClaw.App.Views.Panels.AiRuntimePanel), panel.ViewType);
        Assert.IsType<AiRuntimePanelViewModel>(panel.ViewModelFactory(services));
        Assert.Equal("ai-discovery", catalog.InGroup("Discover").ElementAt(1).Id);
        Assert.Equal("ai-runtime", catalog.InGroup("Discover").ElementAt(2).Id);
    }
}
