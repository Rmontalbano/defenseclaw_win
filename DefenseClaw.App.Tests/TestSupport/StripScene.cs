using System.Text.Json;
using DefenseClaw.App.Services;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Gateway.Models;
using DefenseClaw.Core.Install;

namespace DefenseClaw.App.Tests.TestSupport;

/// <summary>
/// What the status strip is told about the monitor's freshness, set by hand: the last good poll's age, the cadence and whether monitoring is paused.
/// </summary>
internal sealed class FakeFreshness : IPollFreshness
{
    public TimeSpan? SinceLastGoodPoll { get; set; }

    public TimeSpan Cadence { get; set; } = TimeSpan.FromSeconds(5);

    public bool IsPaused { get; set; }
}

/// <summary>
/// A <see cref="StatusStripViewModel"/> over an isolated composition (a scratch data directory, no CLI, no gateway) with everything it hears of in the
/// test's hands: the monitor's freshness (<see cref="Freshness"/>), the clock its stale look is timed on (<see cref="Clock"/>), the commands in flight
/// (<see cref="Commands"/>, fed by hand), the connector roster, and what the panels hand over (<c>Services.StatusFacts</c>). Updates run in place.
/// Synthetic data only.
/// </summary>
internal sealed class StripScene : IDisposable
{
    private const string DefaultConfig = "gateway:\n  api_port: 18970\nguardrail:\n  connector: claudecode\n  enabled: true\n  connectors:\n    claudecode:\n      mode: observe\n";

    private readonly TempDirectory _temp = new();

    public StripScene(string? configYaml = DefaultConfig)
    {
        Services = TestServices.Create(_temp, configYaml);
        Clock = new TickingClock();
        Freshness = new FakeFreshness { SinceLastGoodPoll = TimeSpan.Zero };
        Commands = new CommandActivity(Services.Cli, action => action());
        Strip = new StatusStripViewModel(Services, Freshness, Clock, action => action(), Commands);
    }

    public AppServices Services { get; }

    public TickingClock Clock { get; }

    public FakeFreshness Freshness { get; }

    public CommandActivity Commands { get; }

    public StatusStripViewModel Strip { get; }

    public StripChip Chip(StripChipKey key) => Strip.Chip(key);

    /// <summary>A running gateway as the monitor would have published it: the connectors, the version, and a <c>/health</c> with the given subsystem states.</summary>
    public static GatewaySnapshot Snapshot(
        string[]? connectors = null,
        string watcher = "running",
        string guardrail = "running",
        string? policyMode = "observe",
        bool? enforcement = false,
        bool running = true,
        bool paused = false,
        string detail = "Gateway responding on 127.0.0.1:18970.",
        string? version = "0.8.10") => new()
    {
        State = running ? AppGatewayState.Running : AppGatewayState.GatewayStopped,
        Install = running ? InstallState.Running : InstallState.GatewayStopped,
        Detail = detail,
        Health = running ? Health(watcher, guardrail, policyMode, enforcement) : null,
        BinaryVersion = version,
        ApiPort = 18970,
        ActiveConnectors = connectors ?? new[] { "claudecode" },
        PolledAt = DateTimeOffset.UtcNow,
        IsPaused = paused,
    };

    /// <summary>The two blocks the strip reads out of <c>/health</c>, in the shape 0.8.10 prints them.</summary>
    public static GatewayHealth Health(string watcher, string guardrail, string? policyMode, bool? enforcement)
    {
        var posture = policyMode is null && enforcement is null
            ? string.Empty
            : ",\"details\":{" + string.Join(
                ',',
                new[]
                {
                    policyMode is null ? null : $"\"policy_mode\":\"{policyMode}\"",
                    enforcement is null ? null : $"\"enforcement_enabled\":{(enforcement.Value ? "true" : "false")}",
                }.Where(static part => part is not null)) + "}";

        var json = "{\"uptime_ms\":1000,\"watcher\":{\"state\":\"" + watcher + "\",\"details\":{\"skill_dirs\":2,\"plugin_dirs\":2}}," +
                   "\"guardrail\":{\"state\":\"" + guardrail + "\"" + posture + "}," +
                   "\"provenance\":{\"binary_version\":\"0.8.10\"}}";
        return JsonSerializer.Deserialize<GatewayHealth>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    /// <summary>A command the runner is running: a real <see cref="CliInvocation"/>, built without a process.</summary>
    public static CliInvocation Running(params string[] argv) => InvocationFactory.Create(false, argv);

    public void Dispose()
    {
        Strip.Dispose();
        Commands.Dispose();
        Services.Dispose();
        _temp.Dispose();
    }
}
