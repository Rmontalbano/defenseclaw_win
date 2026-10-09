using System.Reflection;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;

namespace DefenseClaw.App.Tests.Shell;

/// <summary>
/// The shell view-model hands the strip what it needs and gives it back (CUST-273): each snapshot the monitor publishes reaches the chips, a guardrail
/// that changes state does too (it is not part of what <c>/health</c> comparisons used to notice), and a closed window leaves nothing listening.
/// Synthetic data only.
/// </summary>
public sealed class StatusStripShellTests : IDisposable
{
    private const string Config = "gateway:\n  api_port: 18970\nguardrail:\n  connector: claudecode\n  connectors:\n    claudecode:\n      mode: observe\n";

    private readonly TempDirectory _temp = new();
    private readonly AppServices _services;

    public StatusStripShellTests()
    {
        _services = TestServices.Create(_temp, Config);
    }

    public void Dispose()
    {
        _services.Dispose();
        _temp.Dispose();
    }

    /// <summary>Makes <paramref name="snapshot"/> the monitor's current one and raises its events, as a poll does (its publish step is private).</summary>
    private void Publish(GatewaySnapshot snapshot) =>
        _ = typeof(GatewayMonitor).GetMethod("Publish", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(_services.Monitor, new object[] { snapshot });

    [Fact]
    public void The_shell_hands_every_snapshot_to_the_strip_and_the_strip_starts_from_the_one_it_was_built_after()
    {
        Publish(StripScene.Snapshot(new[] { "claudecode" }, version: "0.8.10"));
        using var shell = new MainWindowViewModel(_services);

        // Built after the first poll: it did not have to wait for the next one.
        Assert.Equal("DefenseClaw 0.8.10", shell.Strip.Chip(StripChipKey.Version).Text);
        Assert.Equal("Connector: claudecode", shell.Strip.Chip(StripChipKey.Connector).Text);

        Publish(StripScene.Snapshot(new[] { "claudecode" }, version: "0.8.11"));
        Assert.Equal("DefenseClaw 0.8.11", shell.Strip.Chip(StripChipKey.Version).Text);

        shell.Apply(StripScene.Snapshot(new[] { "claudecode" }, version: "0.9.0"));
        Assert.Equal("DefenseClaw 0.9.0", shell.Strip.Chip(StripChipKey.Version).Text);
    }

    [Fact]
    public void The_pill_keeps_its_properties_and_the_chips_that_moved_are_the_strips_now()
    {
        using var shell = new MainWindowViewModel(_services);

        shell.Apply(StripScene.Snapshot());

        Assert.Equal("Running", shell.StateLabel);
        Assert.Equal("Ok", shell.StateTone);
        Assert.Equal("Gateway status: Running", shell.StateAutomationName);
        Assert.Equal(shell.StateDetail, shell.Strip.Chip(StripChipKey.Detail).Text);
    }

    [Fact]
    public void A_guardrail_that_changes_state_alone_is_announced_by_the_monitor_so_an_always_alive_strip_follows_it()
    {
        using var shell = new MainWindowViewModel(_services);
        Publish(StripScene.Snapshot(guardrail: "running"));
        Assert.Equal("Guardrail", shell.Strip.Chip(StripChipKey.Guardrail).Text);

        // Nothing else differs between the two snapshots: before the strip, health as a whole was not compared, so this was never raised.
        Publish(StripScene.Snapshot(guardrail: "error"));

        Assert.Equal("Guardrail: error", shell.Strip.Chip(StripChipKey.Guardrail).Text);
        Assert.Equal("Bad", shell.Strip.Chip(StripChipKey.Guardrail).Tone);

        Publish(StripScene.Snapshot(guardrail: "error", watcher: "stopped"));
        Assert.Equal("Watchdog: stopped", shell.Strip.Chip(StripChipKey.Watchdog).Text);

        Publish(StripScene.Snapshot(guardrail: "error", watcher: "stopped", policyMode: "action"));
        Assert.Equal("Policy: action", shell.Strip.Chip(StripChipKey.Policy).Text);
    }

    [Fact]
    public void The_subsystem_reading_is_part_of_what_the_monitor_compares_and_the_rest_of_health_still_is_not()
    {
        var running = StripScene.Snapshot();

        // A different /health - another uptime, other connector counters, more destinations: the ones that move on nearly every poll of a busy box - is
        // still not a change while the two subsystems read the same.
        var later = running with { Health = OverviewScene.Health(twoConnectors: true) };
        Assert.True(running.RendersSameAs(later));

        Assert.False(running.RendersSameAs(StripScene.Snapshot(guardrail: "error")));
        Assert.False(running.RendersSameAs(StripScene.Snapshot(watcher: "degraded")));
        Assert.False(running.RendersSameAs(StripScene.Snapshot(policyMode: "action")));
        Assert.False(running.RendersSameAs(StripScene.Snapshot(enforcement: true)));
        Assert.True(running.RendersSameAs(StripScene.Snapshot()));
    }

    [Fact]
    public void Disposing_the_shell_disposes_the_strip_and_a_rebuilt_window_leaves_nothing_behind()
    {
        var shell = new MainWindowViewModel(_services);
        var strip = shell.Strip;
        strip.SetActive(true);

        shell.Dispose();

        Assert.False(strip.Chip(StripChipKey.Keys).IsShown);
        _services.StatusFacts.PublishMissingKeys(new[] { "OPENAI_API_KEY" });
        Assert.False(strip.Chip(StripChipKey.Keys).IsShown);

        // A window abandoned and built again, a few times over: each strip gives back every subscription it took.
        for (var i = 0; i < 5; i++)
        {
            new MainWindowViewModel(_services).Dispose();
        }

        // Nothing built since is listening: publishing reaches no one (and so throws nothing).
        _services.StatusFacts.PublishRedaction("per-route · unredacted");
        _services.StatusFacts.PublishRedaction("per-route · sensitive");
    }

    [Fact]
    public async Task The_strip_follows_the_facts_the_panels_hand_over_through_the_shell()
    {
        using var shell = new MainWindowViewModel(_services);

        _services.StatusFacts.PublishMissingKeys(new[] { "OPENAI_API_KEY", "GALILEO_API_KEY", "SPLUNK_HEC_TOKEN" });
        _services.StatusFacts.PublishRedaction("per-route · unredacted");

        // The strip hears of a fact on whatever thread published it and draws it on the UI thread: in place when that is this one, queued when a
        // dispatcher is running elsewhere (the UI suites share one per process).
        await WaitUntilAsync(() => shell.Strip.Chip(StripChipKey.Redaction).IsShown && shell.Strip.Chip(StripChipKey.Keys).IsShown, "the strip to draw the facts");

        Assert.Equal("Keys: missing OPENAI_API_KEY, GALILEO_API_KEY (+1 more)", shell.Strip.Chip(StripChipKey.Keys).Text);
        Assert.Equal("Redaction: per-route · unredacted", shell.Strip.Chip(StripChipKey.Redaction).Text);
        Assert.Equal("Warn", shell.Strip.Chip(StripChipKey.Redaction).Tone);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = Environment.TickCount64 + 120_000; // a wait, not a bound: CI runners have run up to ~25x slower than a desktop
        while (!condition())
        {
            Assert.True(Environment.TickCount64 < deadline, "Timed out waiting for: " + what);
            await Task.Delay(10);
        }
    }
}
