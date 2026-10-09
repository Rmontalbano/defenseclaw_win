using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Panels;
using DefenseClaw.Core.AiRuntime;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Gateway;
using System.Windows.Controls;

namespace DefenseClaw.App.Tests.Runtime;

/// <summary>
/// The Plane C readiness card on the Runtime panel (CUST-324): shown only with the developer flag, one row per check with whose token it
/// describes, "could not read" for every probe, and, above all, nothing started. The process seams (the review's step runner and the
/// permissions read) fail the test if they are called; the probes are fakes; the sources of the card are scanned for anything that could start
/// a process. A PNG is written when <c>DC_RENDER_DIR</c> names a folder.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class PlaneCReadinessPanelTests : IDisposable
{
    private const string Populated = "ai-usage-runtime.populated.synthetic.json";
    private const string Degraded = "ai-usage-runtime.degraded.synthetic.json";

    private readonly TempDirectory _temp = new();
    private AppServices? _services;

    public void Dispose()
    {
        _services?.Dispose();
        _temp.Dispose();
    }

    private sealed class FakeProbes : IPlaneCProbes
    {
        public Func<ProbeReading<TokenElevation>> Elevation { get; set; } = () => ProbeReading<TokenElevation>.Ok(TokenElevation.Limited);

        public Func<ProbeReading<bool>> Readers { get; set; } = () => ProbeReading<bool>.Ok(false);

        public Func<ProbeReading<bool>> Channel { get; set; } = () => ProbeReading<bool>.Ok(false);

        public Func<ProbeReading<int?>> CommandLine { get; set; } = () => ProbeReading<int?>.Ok(1);

        public int Reads { get; private set; }

        public ProbeReading<TokenElevation> ReadElevation() { Reads++; return Elevation(); }

        public ProbeReading<bool> ReadEventLogReadersMembership() { Reads++; return Readers(); }

        public ProbeReading<bool> ReadSecurityChannelOpens() { Reads++; return Channel(); }

        public ProbeReading<int?> ReadCommandLineAudit() { Reads++; return CommandLine(); }
    }

    private static string Rest(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "runtime-95159fd", "rest", name));

    private static string PermissionsJson() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "runtime-95159fd", "cli", "agent-discovery-runtime-permissions.windows.synthetic.json"));

    private static CliInvocation Done(IReadOnlyList<string> argv, string output)
    {
        var invocation = InvocationFactory.Create(retainFullOutput: true, argv.ToArray());
        InvocationFactory.Append(invocation, output);
        InvocationFactory.Finish(invocation, 0);
        return invocation;
    }

    /// <summary>A panel whose every process seam fails the test, with the developer flag as asked.</summary>
    private async Task<(AiRuntimePanelViewModel Vm, FakeProbes Probes, List<string> Copied, List<string> Started)> OpenAsync(bool developer, string snapshot = Populated)
    {
        _services ??= AppServices.CreateIsolated(
            TestServices.IsolatedPaths(_temp.Path),
            claudeSettingsPath: _temp.File("claude-settings.json"),
            runtimeProbeRunner: RuntimeFixtureRunner.For("95159fd"));
        _ = await _services.Runtime.RefreshAsync();
        Assert.True(_services.Settings.Update(s => s with { Developer = s.Developer with { Enabled = developer } }));

        var probes = new FakeProbes();
        var copied = new List<string>();
        var started = new List<string>();
        var vm = new AiRuntimePanelViewModel(_services)
        {
            ReadSnapshot = _ => Task.FromResult(GatewayResult<JsonDocument>.Ok(JsonDocument.Parse(Rest(snapshot)))),
            PlaneCProbes = probes,
            ClipboardWriter = text => { copied.Add(text); return true; },
            RunPermissionsRead = (argv, _) =>
            {
                started.Add("permissions " + string.Join(' ', argv));
                return Task.FromResult(Done(argv, PermissionsJson()));
            },
        };
        vm.Review.RunStep = (executable, argv, _) =>
        {
            started.Add(executable + " " + string.Join(' ', argv));
            throw new InvalidOperationException("The Plane C card must never start a process: " + executable);
        };
        return (vm, probes, copied, started);
    }

    [Fact]
    public async Task Without_the_developer_flag_there_is_no_card_and_the_machine_is_not_read()
    {
        var (vm, probes, _, _) = await OpenAsync(developer: false);

        await vm.RefreshCommand.ExecuteAsync(null);
        await vm.ReadPlaneCAsync();
        await vm.CheckPlaneCCommand.ExecuteAsync(null);

        Assert.False(vm.ShowPlaneC);
        Assert.Empty(vm.PlaneCChecks);
        Assert.Equal(0, probes.Reads);
    }

    [Fact]
    public async Task With_the_developer_flag_the_card_lists_six_checks_and_plane_c_follows_the_snapshot()
    {
        var (vm, probes, _, _) = await OpenAsync(developer: true, Degraded);

        await vm.RefreshCommand.ExecuteAsync(null);
        await vm.ReadPlaneCAsync();

        Assert.True(vm.ShowPlaneC);
        Assert.Equal(4, probes.Reads);
        Assert.Equal(6, vm.PlaneCChecks.Count);
        var planeC = vm.Planes.Single(p => p.Plane.Id == "c").Plane;
        var line = vm.PlaneCChecks.Single(c => c.Id == "plane-c");
        Assert.Equal(PlaneCSubject.Gateway, line.Subject);
        Assert.Equal(planeC.Badge, line.Value);

        // Another poll moves only the gateway's line; the machine is not read again.
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(4, probes.Reads);
    }

    [Fact]
    public async Task Every_probe_failing_leaves_the_four_app_checks_could_not_read_and_the_card_standing()
    {
        var (vm, probes, _, _) = await OpenAsync(developer: true);
        probes.Elevation = () => throw new InvalidOperationException();
        probes.Readers = () => ProbeReading<bool>.Failed("lookup failed");
        probes.Channel = () => throw new UnauthorizedAccessException();
        probes.CommandLine = () => ProbeReading<int?>.Failed("not a DWORD");

        await vm.RefreshCommand.ExecuteAsync(null);
        await vm.ReadPlaneCAsync();

        Assert.Equal(
            new[] { "could not read", "could not read", "could not read", "could not read" },
            vm.PlaneCChecks.Where(c => c.Subject == PlaneCSubject.App).Select(c => c.StateText).ToArray());
    }

    [Fact]
    public async Task Reading_checking_again_and_copying_never_start_a_process()
    {
        var (vm, _, copied, started) = await OpenAsync(developer: true);
        await vm.RefreshCommand.ExecuteAsync(null);

        started.Clear();
        await vm.ReadPlaneCAsync();
        await vm.CheckPlaneCCommand.ExecuteAsync(null);
        foreach (var block in new[] { "verify", "grant", "revert", "readers", "side-by-side", "nothing" })
        {
            vm.CopyPlaneCCommand.Execute(block);
        }

        // Nothing went to the review's step runner or to the CLI; the copy writes text and nothing else.
        Assert.Empty(started);
        Assert.False(vm.Review.IsOpen);
        Assert.False(vm.Review.IsRunning);
        Assert.Equal(4, copied.Count); // verify, revert, readers, side-by-side; the grant lines exist only after the Prerequisites read
        Assert.Contains(copied, text => text.Contains("auditpol /get /subcategory:\"Process Creation\"", StringComparison.Ordinal));
        Assert.Contains(copied, text => text.Contains("restart", StringComparison.Ordinal) && text.Contains("defenseclaw-gateway.exe", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_grant_block_is_the_runtimes_own_lines_once_the_prerequisites_are_read()
    {
        var (vm, _, copied, started) = await OpenAsync(developer: true);
        await vm.InitializeAsync();

        Assert.True(vm.HasPlaneCGrant);
        vm.CopyPlaneCCommand.Execute("grant");

        // The one run is the existing read-only permissions read, never --grant.
        Assert.All(started, s => Assert.DoesNotContain("--grant", s, StringComparison.Ordinal));
        Assert.Single(copied);
        Assert.Contains("auditpol /set", copied[0], StringComparison.Ordinal);
    }

    [Fact]
    public void The_runtime_panels_wording_says_security_event_log_where_it_described_plane_c_as_kernel_or_etw()
    {
        foreach (var text in new[] { AiRuntimePanelViewModel.EnableExplanation, AiRuntimePanelViewModel.PlaneCWarning, AiRuntimePanelViewModel.PollExplanation, AiRuntimePanelViewModel.WindowsNote })
        {
            Assert.DoesNotContain("ETW", text, StringComparison.Ordinal);
            Assert.DoesNotContain("kernel", text, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("Security event log", AiRuntimePanelViewModel.EnableExplanation, StringComparison.Ordinal);
        Assert.Contains("Security event log", AiRuntimePanelViewModel.PlaneCWarning, StringComparison.Ordinal);
    }

    [Fact]
    public void The_cards_sources_have_no_way_to_start_a_process()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "DefenseClaw.Win.sln")))
        {
            root = Path.GetDirectoryName(root);
        }

        Assert.NotNull(root);
        var files = new[]
        {
            @"DefenseClaw.Core\AiRuntime\PlaneCReadiness.cs",
            @"DefenseClaw.App\Services\WindowsPlaneCProbes.cs",
            @"DefenseClaw.App\ViewModels\AiRuntimePanelViewModel.PlaneC.cs",
        };
        var forbidden = new Regex(@"\bProcess\s*\.|ProcessStartInfo|ShellExecute|CliRunner|Services\s*\.\s*Cli\b|RunAsync\s*\(|\bReview\b|RunStep|RunPermissionsRead|\brunas\b", RegexOptions.None);
        var offenders = new List<string>();
        foreach (var relative in files)
        {
            var lines = File.ReadAllLines(Path.Combine(root!, relative));
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].TrimStart();
                if (line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith("///", StringComparison.Ordinal))
                {
                    continue;
                }

                if (forbidden.IsMatch(line))
                {
                    offenders.Add($"{relative}:{i + 1}: {line}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public async Task The_card_renders_offscreen_with_its_checks_and_copy_only_blocks()
    {
        var (vm, _, _, _) = await OpenAsync(developer: true, Degraded);
        await vm.InitializeAsync();

        var (view, host) = UiThread.Run(() =>
        {
            var panel = new AiRuntimePanel { DataContext = vm };
            return (panel, new OffscreenHost(panel, 1100, 2600));
        });
        try
        {
            UiThread.Run(() =>
            {
                host.Relayout();
                var names = VisualTree.Descendants<DependencyObject>(view).Select(AutomationProperties.GetName).Where(n => n.Length > 0).ToArray();
                Assert.Contains("Plane C readiness checks", names);
                Assert.Contains(names, n => n.StartsWith("This app's token: Token elevation", StringComparison.Ordinal));
                Assert.Contains(names, n => n.StartsWith("The gateway (runtime snapshot): Plane C", StringComparison.Ordinal));
                Assert.Contains("Verify commands to copy (this app does not run them)", names);
                Assert.Contains("Revert commands to copy (this app does not run them)", names);

                if (Environment.GetEnvironmentVariable("DC_RENDER_DIR") is { Length: > 0 } dir)
                {
                    host.RenderPng(Path.Combine(dir, "plane-c-readiness.png"));
                }
            });
        }
        finally
        {
            UiThread.Run(host.Dispose);
        }
    }
}
