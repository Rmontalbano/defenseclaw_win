using System.Text.Json;
using System.Text.Json.Nodes;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Gateway.Models;

namespace DefenseClaw.App.Tests.Overview;

/// <summary>
/// The Services card's nine service cards (CUST-313): Gateway, Agent, Watchdog, Guardrail, API, Sinks, Telemetry, AI Discovery and Sandbox, in
/// the DefenseClaw TUI's order, each read from one <c>/health</c> block by presence. The state words are checked against two payloads: the 0.8.10 one
/// (<c>Fixtures/runtime-0.8.10/rest/health.sinks.synthetic.json</c>, a live 0.8.10 capture plus the <c>sinks</c> block it lacks) and the newer runtime's
/// (<c>Fixtures/runtime-95159fd/rest/health.json</c>). No test names a version: the same code reads both.
/// </summary>
public sealed class OverviewServiceCardsTests
{
    private const string Fixture0810 = "runtime-0.8.10/rest/health.sinks.synthetic.json";
    private const string FixturePinned = "runtime-95159fd/rest/health.json";

    private static string Read(string relative) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", relative.Replace('/', Path.DirectorySeparatorChar)));

    private static GatewayHealth Health(string json) =>
        JsonSerializer.Deserialize<GatewayHealth>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private static string Without(string json, string property)
    {
        var node = JsonNode.Parse(json)!.AsObject();
        _ = node.Remove(property);
        return node.ToJsonString();
    }

    private static string With(string json, string property, string valueJson)
    {
        var node = JsonNode.Parse(json)!.AsObject();
        node[property] = JsonNode.Parse(valueJson);
        return node.ToJsonString();
    }

    private static GatewaySnapshot Snapshot(GatewayHealth? health, AppGatewayState state = AppGatewayState.Running) => new()
    {
        State = state,
        ApiPort = 18970,
        Health = health,
        PolledAt = DateTimeOffset.UtcNow,
    };

    private static IReadOnlyList<ServiceRow> Cards(
        GatewayHealth? health,
        AppGatewayState state = AppGatewayState.Running,
        string[]? roster = null,
        string[]? disabled = null,
        string? claw = null) =>
        OverviewPanelViewModel.BuildServiceCards(
            Snapshot(health, state),
            health,
            roster ?? Array.Empty<string>(),
            new HashSet<string>(disabled ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase),
            claw);

    private static ServiceRow Card(IReadOnlyList<ServiceRow> cards, string key) => cards.Single(c => c.Key == key);

    private static string Connector(string name, string state, long requests = 0, long toolBlocks = 0, long subprocessBlocks = 0, string mode = "both") =>
        $"{{\"name\":\"{name}\",\"state\":\"{state}\",\"source\":\"manual\",\"requests\":{requests},\"errors\":0,\"tool_inspection_mode\":\"{mode}\"," +
        $"\"tool_blocks\":{toolBlocks},\"subprocess_blocks\":{subprocessBlocks}}}";

    private static GatewayHealth Connectors(params string[] connectors) =>
        Health("{\"uptime_ms\":600000,\"api\":{\"state\":\"running\"},\"connectors\":[" + string.Join(",", connectors) + "]}");

    // ---- The nine cards ----

    [Fact]
    public void The_services_card_is_the_tuis_nine_cards_in_its_order_and_under_its_names()
    {
        var cards = Cards(Health(Read(Fixture0810)));

        Assert.Equal(OverviewPanelViewModel.ServiceKeys, cards.Select(c => c.Key));
        Assert.Equal(
            new[] { "Gateway", "Agent", "Watchdog", "Guardrail", "API", "Sinks", "Telemetry", "AI Discovery", "Sandbox" },
            cards.Select(c => c.Name));
    }

    [Fact]
    public void Nothing_answering_leaves_the_nine_cards_unknown_except_the_gateway_and_the_sandbox_which_know_better()
    {
        var cards = Cards(null, AppGatewayState.GatewayStopped);

        Assert.Equal(9, cards.Count);
        Assert.All(cards.Where(c => c.Key is not ("gateway" or "sandbox")), c =>
        {
            Assert.Equal("unknown", c.StateText);
            Assert.Equal("Neutral", c.StateKey);
        });

        var gateway = Card(cards, "gateway");
        Assert.Equal("stopped", gateway.StateText);
        Assert.Equal("Bad", gateway.StateKey);
        Assert.Equal("not answering on port 18970", gateway.Detail);
        Assert.Equal(OverviewPanelViewModel.SandboxStateText, Card(cards, "sandbox").StateText);
    }

    // ---- State mapping from the fixtures ----

    [Fact]
    public void The_0_8_10_payload_with_a_sinks_block_gives_each_card_its_state_and_detail()
    {
        var cards = Cards(Health(Read(Fixture0810)), roster: new[] { "claudecode" });

        // The fleet uplink (/health "gateway") is disabled on a standalone box; the Gateway card is the gateway's own availability, and says why.
        var gateway = Card(cards, "gateway");
        Assert.Equal(("running", "Ok"), (gateway.StateText, gateway.StateKey));
        Assert.Equal("up 14m · no OpenClaw fleet configured (standalone mode)", gateway.Detail);
        Assert.StartsWith("since ", gateway.SinceText, StringComparison.Ordinal);

        var agent = Card(cards, "agent");
        Assert.Equal(("running", "Ok"), (agent.StateText, agent.StateKey));
        Assert.Equal("Claude Code - both - 14 req", agent.Detail);

        var watchdog = Card(cards, "watcher");
        Assert.Equal(("running", "Ok"), (watchdog.StateText, watchdog.StateKey));
        Assert.Equal("2 skill dirs, 2 plugin dirs", watchdog.Detail);

        // Liveness is the state word; the posture says it is observing, which is configuration and not a fault.
        var guardrail = Card(cards, "guardrail");
        Assert.Equal(("running", "Ok"), (guardrail.StateText, guardrail.StateKey));
        Assert.Equal("observability-only (no proxy binding)", guardrail.Detail);
        Assert.Equal("observe · observing (enforcement off) · agent_lifecycle_hooks", guardrail.Posture);

        var api = Card(cards, "api");
        Assert.Equal(("running", "Ok"), (api.StateText, api.StateKey));
        Assert.Equal("127.0.0.1:18970", api.Detail);

        var sinks = Card(cards, "sinks");
        Assert.Equal(("running", "Ok"), (sinks.StateText, sinks.StateKey));
        Assert.Equal("1 of 1 enabled", sinks.Detail);

        var telemetry = Card(cards, "telemetry");
        Assert.Equal(("running", "Ok"), (telemetry.StateText, telemetry.StateKey));
        Assert.Equal("1 destination: local-sqlite (healthy)", telemetry.Detail);
        Assert.Equal("retention 90 days", telemetry.Posture);

        var discovery = Card(cards, "ai_discovery");
        Assert.Equal(("running", "Ok"), (discovery.StateText, discovery.StateKey));
        Assert.Equal("96 active, 0 new, enhanced", discovery.Detail);

        var sandbox = Card(cards, "sandbox");
        Assert.Equal((OverviewPanelViewModel.SandboxStateText, "Neutral"), (sandbox.StateText, sandbox.StateKey));
    }

    [Fact]
    public void The_same_payload_without_its_sinks_block_changes_the_sinks_card_and_nothing_else()
    {
        var json = Read(Fixture0810);
        var with = Cards(Health(json), roster: new[] { "claudecode" });
        var without = Cards(Health(Without(json, "sinks")), roster: new[] { "claudecode" });

        var sinks = Card(without, "sinks");
        Assert.Equal(("unknown", "Neutral"), (sinks.StateText, sinks.StateKey));
        Assert.Equal(OverviewPanelViewModel.SinksNotReportedText, sinks.Detail);

        Assert.Equal(with.Where(c => c.Key != "sinks"), without.Where(c => c.Key != "sinks"));
    }

    [Fact]
    public void The_newer_runtimes_payload_maps_by_presence_with_no_sinks_connector_or_sandbox_in_it()
    {
        var cards = Cards(Health(Read(FixturePinned)));

        Assert.Equal(9, cards.Count);

        var gateway = Card(cards, "gateway");
        Assert.Equal(("running", "Ok"), (gateway.StateText, gateway.StateKey));
        Assert.Equal("up 1m · no OpenClaw fleet configured (standalone mode)", gateway.Detail);

        // A fresh install: no connector configured and none running, so there is nothing to roll up.
        var agent = Card(cards, "agent");
        Assert.Equal(("unknown", "Neutral"), (agent.StateText, agent.StateKey));
        Assert.Equal(string.Empty, agent.Detail);

        var watchdog = Card(cards, "watcher");
        Assert.Equal("running", watchdog.StateText);
        Assert.Equal("0 skill dirs, 0 plugin dirs", watchdog.Detail);

        // Disabled is not broken: neutral, with the gateway's own sentence, and no posture to claim.
        var guardrail = Card(cards, "guardrail");
        Assert.Equal(("disabled", "Neutral"), (guardrail.StateText, guardrail.StateKey));
        Assert.Equal("no connector configured; run defenseclaw setup for a connector", guardrail.Detail);
        Assert.Equal(string.Empty, guardrail.Posture);

        Assert.Equal("0.0.0.0:18970", Card(cards, "api").Detail);

        var sinks = Card(cards, "sinks");
        Assert.Equal(("unknown", "Neutral"), (sinks.StateText, sinks.StateKey));
        Assert.Equal(OverviewPanelViewModel.SinksNotReportedText, sinks.Detail);

        var telemetry = Card(cards, "telemetry");
        Assert.Equal("running", telemetry.StateText);
        Assert.Equal("2 destinations: local-sqlite (healthy), example-otlp (healthy)", telemetry.Detail);
        Assert.Equal("retention 7 days", telemetry.Posture);

        var discovery = Card(cards, "ai_discovery");
        Assert.Equal(("disabled", "Neutral"), (discovery.StateText, discovery.StateKey));
        Assert.Equal(string.Empty, discovery.Detail);

        Assert.Equal(OverviewPanelViewModel.SandboxStateText, Card(cards, "sandbox").StateText);
    }

    [Fact]
    public void A_sinks_block_the_gateway_does_report_is_read_wherever_it_comes_from()
    {
        var json = With(
            Read(FixturePinned),
            "sinks",
            "{\"state\":\"error\",\"last_error\":\"audit-file: permission denied\",\"details\":{\"sinks\":[{\"name\":\"audit-file\",\"state\":\"error\"},{\"name\":\"splunk\"}]}}");

        var sinks = Card(Cards(Health(json)), "sinks");

        Assert.Equal(("error", "Bad"), (sinks.StateText, sinks.StateKey));
        Assert.Equal("2 sinks: audit-file (error), splunk", sinks.Detail);
    }

    [Theory]
    [InlineData("{\"state\":5}")]
    [InlineData("{\"since\":\"not-a-date\"}")]
    [InlineData("[1,2,3]")]
    [InlineData("\"text\"")]
    [InlineData("null")]
    public void A_sinks_block_of_the_wrong_shape_is_a_card_that_is_not_reported_not_an_exception(string sinks)
    {
        var cards = Cards(Health(With(Read(Fixture0810), "sinks", sinks)));

        var card = Card(cards, "sinks");
        Assert.Equal("unknown", card.StateText);
        Assert.Equal(OverviewPanelViewModel.SinksNotReportedText, card.Detail);
        Assert.Equal(9, cards.Count);
    }

    [Theory]
    [InlineData("running", "Ok")]
    [InlineData("healthy", "Ok")]
    [InlineData("enabled", "Ok")]
    [InlineData("active", "Ok")]
    [InlineData("disabled", "Neutral")]
    [InlineData("degraded", "Warn")]
    [InlineData("starting", "Warn")]
    [InlineData("error", "Bad")]
    [InlineData("stopped", "Bad")]
    [InlineData("something-new", "Neutral")]
    public void A_block_is_drawn_in_the_tone_of_its_state(string state, string tone)
    {
        var card = Card(Cards(Health("{\"watcher\":{\"state\":\"" + state + "\"}}")), "watcher");

        Assert.Equal(state, card.StateText);
        Assert.Equal(tone, card.StateKey);
    }

    [Fact]
    public void A_disabled_blocks_last_error_is_never_shown_but_a_failing_ones_is()
    {
        var disabled = Card(Cards(Health("{\"api\":{\"state\":\"disabled\",\"last_error\":\"api disabled\"}}")), "api");
        var failing = Card(Cards(Health("{\"api\":{\"state\":\"error\",\"last_error\":\"address already in use\"}}")), "api");

        Assert.Equal(string.Empty, disabled.Detail);
        Assert.Equal("address already in use", failing.Detail);
    }

    [Fact]
    public void The_guardrail_says_enforcing_when_it_is_and_says_nothing_it_does_not_know()
    {
        var enforcing = Card(
            Cards(Health("{\"guardrail\":{\"state\":\"running\",\"details\":{\"policy_mode\":\"action\",\"enforcement_enabled\":true,\"enforcement_surface\":\"proxy\"}}}")),
            "guardrail");
        var modeOnly = Card(Cards(Health("{\"guardrail\":{\"state\":\"running\",\"details\":{\"mode\":\"observe\"}}}")), "guardrail");
        var bare = Card(Cards(Health("{\"guardrail\":{\"state\":\"running\"}}")), "guardrail");

        Assert.Equal("action · enforcing · proxy", enforcing.Posture);
        Assert.Equal("observe", modeOnly.Posture);
        Assert.Equal(string.Empty, bare.Posture);
    }

    [Fact]
    public void Telemetry_that_only_counts_its_destinations_says_how_many()
    {
        var card = Card(Cards(Health("{\"telemetry\":{\"state\":\"running\",\"details\":{\"destination_count\":3,\"retention_days\":30}}}")), "telemetry");

        Assert.Equal("3 destinations", card.Detail);
        Assert.Equal("retention 30 days", card.Posture);
    }

    [Fact]
    public void Telemetry_leaves_out_a_destination_that_is_switched_off()
    {
        var card = Card(
            Cards(Health("{\"telemetry\":{\"state\":\"running\",\"details\":{\"destinations\":[{\"name\":\"a\",\"enabled\":true,\"state\":\"healthy\"},{\"name\":\"b\",\"enabled\":false,\"state\":\"disabled\"}]}}}")),
            "telemetry");

        Assert.Equal("1 destination: a (healthy)", card.Detail);
    }

    // ---- Gateway ----

    [Fact]
    public void A_disabled_fleet_uplink_does_not_speak_for_the_gateway_and_an_uplink_in_use_does()
    {
        var standalone = Card(Cards(Health("{\"uptime_ms\":3600000,\"gateway\":{\"state\":\"disabled\",\"details\":{\"summary\":\"standalone\"}}}")), "gateway");
        var failing = Card(
            Cards(Health("{\"uptime_ms\":3600000,\"gateway\":{\"state\":\"error\",\"last_error\":\"fleet unreachable\"}}")),
            "gateway");
        var degraded = Card(Cards(Health("{\"gateway\":{\"state\":\"degraded\"}}")), "gateway");

        Assert.Equal(("running", "Ok", "up 1h 0m · standalone"), (standalone.StateText, standalone.StateKey, standalone.Detail));
        Assert.Equal(("error", "Bad", "up 1h 0m · fleet unreachable"), (failing.StateText, failing.StateKey, failing.Detail));
        Assert.Equal(("degraded", "Warn"), (degraded.StateText, degraded.StateKey));
    }

    [Theory]
    [InlineData(AppGatewayState.Running, "running", "Ok")]
    [InlineData(AppGatewayState.Degraded, "degraded", "Warn")]
    [InlineData(AppGatewayState.WslGatewayDetected, "wsl gateway", "Warn")]
    [InlineData(AppGatewayState.GatewayStopped, "stopped", "Bad")]
    [InlineData(AppGatewayState.NotInstalled, "not installed", "Neutral")]
    [InlineData(AppGatewayState.NotInitialized, "not initialized", "Neutral")]
    [InlineData(AppGatewayState.Unknown, "unknown", "Neutral")]
    public void With_the_fleet_uplink_off_the_gateway_card_is_the_availability_the_poll_found(AppGatewayState state, string word, string tone)
    {
        var health = state == AppGatewayState.GatewayStopped ? null : Health("{\"gateway\":{\"state\":\"disabled\"}}");

        var card = Card(Cards(health, state), "gateway");

        Assert.Equal((word, tone), (card.StateText, card.StateKey));
    }

    // ---- Agent: the roll-up ----

    [Theory]
    [InlineData("running", "running")]
    [InlineData("active", "enabled")]
    public void The_agent_is_running_only_when_every_connector_is_up(string first, string second)
    {
        var card = Card(
            Cards(Connectors(Connector("claudecode", first, 3), Connector("hermes", second, 4)), roster: new[] { "claudecode", "hermes" }),
            "agent");

        Assert.Equal(("running", "Ok", "2 connectors active"), (card.StateText, card.StateKey, card.Detail));
    }

    [Fact]
    public void The_agent_is_degraded_when_some_connectors_are_up()
    {
        var card = Card(
            Cards(Connectors(Connector("claudecode", "running"), Connector("hermes", "stopped")), roster: new[] { "claudecode", "hermes" }),
            "agent");

        Assert.Equal(("degraded", "Warn", "1/2 connectors running"), (card.StateText, card.StateKey, card.Detail));
    }

    [Fact]
    public void With_no_connector_up_the_agent_says_how_the_first_one_is_down()
    {
        var card = Card(
            Cards(Connectors(Connector("claudecode", "error"), Connector("hermes", "stopped")), roster: new[] { "claudecode", "hermes" }),
            "agent");

        Assert.Equal(("error", "Bad", "0/2 connectors running"), (card.StateText, card.StateKey, card.Detail));
    }

    [Fact]
    public void The_agent_is_disabled_when_every_connector_of_the_roster_is_switched_off_and_the_gateway_therefore_lists_none()
    {
        var card = Card(
            Cards(Connectors(), roster: new[] { "claudecode", "hermes" }, disabled: new[] { "Claudecode", "hermes" }),
            "agent");

        Assert.Equal(("disabled", "Neutral", "0 active · 2 disabled"), (card.StateText, card.StateKey, card.Detail));
    }

    [Fact]
    public void A_single_connector_that_is_switched_off_is_a_disabled_agent_too()
    {
        var card = Card(Cards(Connectors(), roster: new[] { "claudecode" }, disabled: new[] { "claudecode" }), "agent");

        Assert.Equal(("disabled", "Neutral", "Claude Code (disabled)"), (card.StateText, card.StateKey, card.Detail));
    }

    [Fact]
    public void Connectors_that_all_report_disabled_roll_up_to_disabled()
    {
        var card = Card(
            Cards(Connectors(Connector("claudecode", "disabled"), Connector("hermes", "disabled")), roster: new[] { "claudecode", "hermes" }),
            "agent");

        Assert.Equal(("disabled", "Neutral"), (card.StateText, card.StateKey));
    }

    [Fact]
    public void A_switched_off_connector_is_counted_apart_and_does_not_make_the_rest_degraded()
    {
        var running = Card(
            Cards(Connectors(Connector("claudecode", "running")), roster: new[] { "claudecode", "hermes" }, disabled: new[] { "hermes" }),
            "agent");
        var oneDown = Card(
            Cards(Connectors(Connector("claudecode", "running"), Connector("codex", "error")), roster: new[] { "claudecode", "codex", "hermes" }, disabled: new[] { "hermes" }),
            "agent");

        Assert.Equal(("running", "1 active · 1 disabled"), (running.StateText, running.Detail));
        Assert.Equal(("degraded", "1/2 running · 1 disabled"), (oneDown.StateText, oneDown.Detail));
    }

    [Fact]
    public void The_agent_is_unknown_when_there_is_nothing_to_roll_up()
    {
        // No answer at all.
        var silent = Card(Cards(null, AppGatewayState.GatewayStopped, roster: new[] { "claudecode" }, claw: "claudecode"), "agent");
        Assert.Equal(("unknown", "Neutral", "Claude Code (configured, not connected)"), (silent.StateText, silent.StateKey, silent.Detail));

        // An answer that lists no connector, with the roster not switched off: not "disabled".
        var none = Card(Cards(Connectors(), roster: new[] { "claudecode", "hermes" }), "agent");
        Assert.Equal(("unknown", "2 connectors configured"), (none.StateText, none.Detail));

        // A connector that names no state.
        var blank = Card(Cards(Connectors(Connector("claudecode", "")), roster: new[] { "claudecode" }), "agent");
        Assert.Equal("unknown", blank.StateText);

        // A fresh install: nothing configured, nothing running.
        var fresh = Card(Cards(Health(Read(FixturePinned))), "agent");
        Assert.Equal(("unknown", string.Empty), (fresh.StateText, fresh.Detail));
    }

    [Fact]
    public void One_connector_is_named_with_its_counters_and_a_gateway_without_the_array_still_reports_its_primary()
    {
        var withArray = Card(
            Cards(Connectors(Connector("claudecode", "running", requests: 3707, toolBlocks: 2, subprocessBlocks: 1)), roster: new[] { "claudecode" }),
            "agent");
        Assert.Equal(("running", "Claude Code - both - 3707 req - 2 tool blocks - 1 subprocess blocks"), (withArray.StateText, withArray.Detail));

        var primaryOnly = Health("{\"connector\":" + Connector("claudecode", "degraded", requests: 5) + "}");
        var legacy = Card(Cards(primaryOnly, roster: new[] { "claudecode" }), "agent");
        Assert.Equal(("degraded", "Warn", "Claude Code - both - 5 req"), (legacy.StateText, legacy.StateKey, legacy.Detail));
    }

    // ---- Sandbox ----

    [Fact]
    public void The_sandbox_card_is_the_platform_fact_whatever_the_gateway_says()
    {
        var running = Health(With(Read(Fixture0810), "sandbox", "{\"state\":\"running\",\"details\":{\"summary\":\"sandbox up\"}}"));

        foreach (var cards in new[] { Cards(running), Cards(Health(Read(FixturePinned))), Cards(null, AppGatewayState.GatewayStopped) })
        {
            var sandbox = Card(cards, "sandbox");

            Assert.Equal(("Sandbox", "not supported on Windows", "Neutral"), (sandbox.Name, sandbox.StateText, sandbox.StateKey));
            Assert.Equal("OpenShell sandboxes run on Linux and macOS only", sandbox.Detail);
            Assert.Equal(string.Empty, sandbox.SinceText);
            Assert.Equal("sandbox", cards[^1].Key);
        }
    }

    [Fact]
    public void A_row_reads_as_a_sentence_to_a_screen_reader()
    {
        var sandbox = Card(Cards(null, AppGatewayState.GatewayStopped), "sandbox");

        Assert.Equal("Sandbox. not supported on Windows. OpenShell sandboxes run on Linux and macOS only", sandbox.ToString());
    }
}

/// <summary>
/// The panel's wiring of the Services card (CUST-313): the cards follow the snapshot and <c>status --json</c>, an unchanged one keeps its row, and
/// what the card used to list that the TUI has no row for - the fleet uplink, <c>config</c> and <c>application_protection</c> - is still shown: the first
/// as the Gateway card's detail, the other two as rows of the Configuration card.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class OverviewServicesPanelTests : IDisposable
{
    private readonly OverviewScene _scene = OverviewScene.Create(twoConnectors: false, seedAudit: false, seedAgents: false);

    public void Dispose() => _scene.Dispose();

    private static string Read(string relative) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", relative.Replace('/', Path.DirectorySeparatorChar)));

    private static GatewayHealth Health(string json) =>
        JsonSerializer.Deserialize<GatewayHealth>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private static GatewaySnapshot Snapshot(string healthJson) => OverviewScene.Snapshot(twoConnectors: false, health: Health(healthJson));

    private OverviewPanelViewModel Panel(string healthJson)
    {
        var vm = new OverviewPanelViewModel(_scene.Services);
        var snapshot = Snapshot(healthJson);
        _scene.Publish(snapshot);
        vm.Apply(snapshot);
        return vm;
    }

    private static ConfigRow Row(OverviewPanelViewModel vm, string label)
    {
        if (!vm.ConfigurationExpanded && vm.HasConfigurationOverflow)
        {
            vm.ToggleConfigurationCommand.Execute(null);
        }

        return vm.ConfigurationRows.Single(r => r.Label == label);
    }

    private static ServiceRow Card(OverviewPanelViewModel vm, string key) => vm.ServiceRows.Single(r => r.Key == key);

    [Fact]
    public void A_poll_fills_the_nine_rows_in_the_tuis_order()
    {
        var vm = Panel(Read("runtime-0.8.10/rest/health.sinks.synthetic.json"));

        Assert.Equal(OverviewPanelViewModel.ServiceKeys, vm.ServiceRows.Select(r => r.Key));
        Assert.Equal("running", Card(vm, "agent").StateText);
        Assert.Equal("Claude Code - both - 14 req", Card(vm, "agent").Detail);
    }

    [Fact]
    public void Before_the_first_poll_the_card_is_nine_unknowns_and_the_sandbox_fact_not_an_empty_box()
    {
        using var scene = OverviewScene.Create(seedAudit: false, seedAgents: false);
        var vm = new OverviewPanelViewModel(scene.Services);

        Assert.Equal(9, vm.ServiceRows.Count);
        Assert.Equal("unknown", Card(vm, "gateway").StateText);
        Assert.Equal(OverviewPanelViewModel.SandboxStateText, Card(vm, "sandbox").StateText);
    }

    [Fact]
    public void A_poll_that_changes_nothing_keeps_every_row_and_one_that_changes_a_state_replaces_only_that_row()
    {
        var json = Read("runtime-0.8.10/rest/health.sinks.synthetic.json");
        var vm = Panel(json);
        var before = vm.ServiceRows.ToList();

        vm.Apply(Snapshot(json));
        Assert.All(before.Zip(vm.ServiceRows), pair => Assert.Same(pair.First, pair.Second));

        var node = JsonNode.Parse(json)!.AsObject();
        node["watcher"]!["state"] = "error";
        vm.Apply(Snapshot(node.ToJsonString()));

        Assert.Equal("error", Card(vm, "watcher").StateText);
        foreach (var (was, now) in before.Zip(vm.ServiceRows))
        {
            Assert.Equal(was.Key != "watcher", ReferenceEquals(was, now));
        }
    }

    [Fact]
    public void Status_json_that_switches_every_connector_off_turns_the_agent_card_disabled()
    {
        var vm = Panel("{\"uptime_ms\":600000,\"api\":{\"state\":\"running\"}}");
        Assert.Equal("unknown", Card(vm, "agent").StateText);

        vm.ApplyStatus(DefenseClawStatusReader.Parse(
            "{\"connectors\":[{\"name\":\"claudecode\",\"friendly\":\"Claude Code\",\"enabled\":false,\"source\":\"manual\"}]}"));

        var agent = Card(vm, "agent");
        Assert.Equal(("disabled", "Neutral", "Claude Code (disabled)"), (agent.StateText, agent.StateKey, agent.Detail));
    }

    [Fact]
    public void The_card_no_longer_lists_the_blocks_the_tui_has_no_row_for_and_the_gateway_card_carries_the_fleet_uplink()
    {
        var vm = Panel(Read("runtime-0.8.10/rest/health.sinks.synthetic.json"));

        Assert.Equal(9, vm.ServiceRows.Count);
        Assert.DoesNotContain(vm.ServiceRows, r => r.Key is "config" or "application_protection");
        Assert.DoesNotContain(vm.ServiceRows, r => r.Name.Contains("fleet", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(vm.ServiceRows, r => r.Name.Contains("protection", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(vm.ServiceRows, r => r.Name.Contains("config", StringComparison.OrdinalIgnoreCase));

        // The fleet uplink is the Gateway card's detail line.
        Assert.Contains("no OpenClaw fleet configured (standalone mode)", Card(vm, "gateway").Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Application_protection_and_the_config_reload_are_rows_of_the_configuration_card()
    {
        var vm = Panel(Read("runtime-0.8.10/rest/health.sinks.synthetic.json"));

        Assert.Equal("off (disabled)", Row(vm, "Application protection").Value);
        Assert.Equal("Neutral", Row(vm, "Application protection").ToneKey);
        Assert.Equal("running · generation 0 · last reload: startup_reconcile", Row(vm, "Config reload").Value);
        Assert.Equal("Neutral", Row(vm, "Config reload").ToneKey);

        // The reload row sits with the config file's.
        var labels = vm.ConfigurationRows.Select(r => r.Label).ToList();
        Assert.Equal(labels.IndexOf("Config file") + 1, labels.IndexOf("Config reload"));
    }

    [Fact]
    public void The_newer_runtimes_config_block_adds_what_it_changed_and_a_restart_it_needs_is_a_warning()
    {
        var calm = Panel(Read("runtime-95159fd/rest/health.json"));
        Assert.Equal("running · generation 2 · last reload: fsnotify:config · changed: observability", Row(calm, "Config reload").Value);
        Assert.Equal("Neutral", Row(calm, "Config reload").ToneKey);
        Assert.Equal("off (disabled)", Row(calm, "Application protection").Value);

        var node = JsonNode.Parse(Read("runtime-95159fd/rest/health.json"))!.AsObject();
        node["config"]!["details"]!["restart_required"] = JsonNode.Parse("[\"gateway.port\"]");
        var vm = Panel(node.ToJsonString());

        var restart = Row(vm, "Config reload");
        Assert.EndsWith("restart required: gateway.port", restart.Value, StringComparison.Ordinal);
        Assert.Equal("Warn", restart.ToneKey);
    }

    [Fact]
    public void A_gateway_without_a_config_or_application_protection_block_adds_neither_row()
    {
        var vm = Panel("{\"uptime_ms\":600000,\"api\":{\"state\":\"running\"}}");

        Assert.DoesNotContain(vm.ConfigurationRows, r => r.Label == "Config reload");
        Assert.DoesNotContain(vm.ConfigurationRows, r => r.Label == "Application protection");
        Assert.DoesNotContain(vm.ServiceRows, r => r.Key is "config" or "application_protection");
    }

    [Fact]
    public void The_live_application_protection_block_outranks_a_status_json_that_may_be_minutes_old()
    {
        var vm = Panel("{\"uptime_ms\":600000,\"application_protection\":{\"state\":\"running\",\"details\":{\"enabled\":true,\"asset_policy_mode\":\"enforce\"}}}");
        vm.ApplyStatus(DefenseClawStatusReader.Parse("{\"application_protection\":{\"enabled\":false,\"health_state\":\"disabled\"}}"));

        Assert.Equal("on · asset policy enforce", Row(vm, "Application protection").Value);

        // Without a block in /health the last status --json is all there is.
        var gone = Panel("{\"uptime_ms\":600000}");
        gone.ApplyStatus(DefenseClawStatusReader.Parse("{\"application_protection\":{\"enabled\":false,\"health_state\":\"disabled\"}}"));
        Assert.Equal("off (disabled)", Row(gone, "Application protection").Value);
    }

    [Fact]
    public void Application_protection_that_is_on_but_not_running_says_so_in_a_warning()
    {
        var vm = Panel("{\"uptime_ms\":600000,\"application_protection\":{\"state\":\"degraded\",\"details\":{\"enabled\":true,\"asset_policy_mode\":\"observe\"}}}");

        var row = Row(vm, "Application protection");
        Assert.Equal("on (degraded) · asset policy observe", row.Value);
        Assert.Equal("Warn", row.ToneKey);
    }

    [Fact]
    public void Scoped_to_a_connector_the_two_rows_are_marked_global_with_the_rest()
    {
        using var two = OverviewScene.Create(seedAudit: false, seedAgents: false);
        var vm = new OverviewPanelViewModel(two.Services);
        var snapshot = OverviewScene.Snapshot(health: Health(Read("runtime-0.8.10/rest/health.sinks.synthetic.json")));
        two.Publish(snapshot);
        vm.Apply(snapshot);
        _ = two.Services.ConnectorScope.Set("hermes");
        vm.ApplyScope();

        Assert.Equal("off (disabled)", Row(vm, "Application protection (global)").Value);
        Assert.StartsWith("running", Row(vm, "Config reload (global)").Value, StringComparison.Ordinal);
    }
}
