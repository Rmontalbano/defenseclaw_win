using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Gateway.Models;
using DefenseClaw.Core.Observability;

namespace DefenseClaw.App.Tests.Shell;

/// <summary>
/// What each chip of the status strip says, in what tone and with what tooltip and automation name, for every state it can be in (CUST-273). The
/// words are the TUI's (<c>widgets/status_strip.py</c>): Keys names the first two missing credentials and counts the rest, Redaction carries the
/// Overview's aggregate label as it is, the policy chip is the mode, the others are one word. Everything is derived from state the app already
/// holds; these tests feed it by hand. Synthetic data only.
/// </summary>
public sealed class StatusStripChipTests : IDisposable
{
    private readonly StripScene _scene = new();

    public void Dispose() => _scene.Dispose();

    private StripChip Chip(StripChipKey key) => _scene.Chip(key);

    private void Poll(GatewaySnapshot snapshot) => _scene.Strip.Apply(snapshot);

    // ---- The chips and their order ---------------------------------------------------------------------------------------------------

    [Fact]
    public void The_chips_are_the_TUIs_in_its_order_with_the_detail_sentence_first_and_the_overflow_chip_last()
    {
        Assert.Equal(
            new[]
            {
                StripChipKey.Detail, StripChipKey.Watchdog, StripChipKey.Guardrail, StripChipKey.Keys, StripChipKey.Alerts, StripChipKey.Connector,
                StripChipKey.Redaction, StripChipKey.Policy, StripChipKey.Running, StripChipKey.Stale, StripChipKey.Version, StripChipKey.Overflow,
            },
            _scene.Strip.Chips.Select(static c => c.Key));
    }

    [Fact]
    public void The_collapse_order_is_the_issues_with_the_watchdog_after_the_guardrail_and_the_running_chip_before_stale()
    {
        // gateway (the pill, outside the strip, never collapses), guardrail, keys, alerts, connector, redaction, policy, stale, version - lower stays longer.
        var byPriority = _scene.Strip.Chips.Where(static c => c.Priority > 0).OrderBy(static c => c.Priority).Select(static c => c.Key).ToArray();

        Assert.Equal(
            new[]
            {
                StripChipKey.Guardrail, StripChipKey.Watchdog, StripChipKey.Keys, StripChipKey.Alerts, StripChipKey.Connector,
                StripChipKey.Redaction, StripChipKey.Policy, StripChipKey.Running, StripChipKey.Stale, StripChipKey.Version,
            },
            byPriority);

        // The sentence and the +N chip are not chips in the order: the panel treats them by role.
        Assert.Equal(0, Chip(StripChipKey.Detail).Priority);
        Assert.Equal(0, Chip(StripChipKey.Overflow).Priority);
        Assert.True(Chip(StripChipKey.Detail).IsFlexible);
        Assert.True(Chip(StripChipKey.Overflow).IsOverflowSlot);
    }

    [Fact]
    public void Before_the_first_poll_only_the_sentence_and_the_alert_and_connector_placeholders_show_and_nothing_claims_a_state()
    {
        // The Initial snapshot is what a monitor holds before it has polled.
        Assert.True(Chip(StripChipKey.Detail).IsShown);
        Assert.Equal("Waiting for the first gateway poll…", Chip(StripChipKey.Detail).Text);
        Assert.False(Chip(StripChipKey.Watchdog).IsShown);
        Assert.False(Chip(StripChipKey.Guardrail).IsShown);
        Assert.Equal("Alerts: —", Chip(StripChipKey.Alerts).Text);
        Assert.Equal("No connector", Chip(StripChipKey.Connector).Text);
        Assert.False(Chip(StripChipKey.Keys).IsShown);
        Assert.False(Chip(StripChipKey.Redaction).IsShown);
        Assert.False(Chip(StripChipKey.Running).IsShown);
        Assert.False(Chip(StripChipKey.Stale).IsShown);
        Assert.False(Chip(StripChipKey.Version).IsShown);
    }

    [Fact]
    public void Every_chip_that_shows_has_a_tooltip_and_an_automation_name()
    {
        Poll(StripScene.Snapshot(new[] { "claudecode", "codex" }));
        _scene.Services.StatusFacts.PublishMissingKeys(new[] { "OPENAI_API_KEY", "CISCO_AI_DEFENSE_API_KEY", "SPLUNK_HEC_TOKEN" });
        _scene.Services.StatusFacts.PublishRedaction("per-route · unredacted");
        _scene.Commands.OnStarted(null, StripScene.Running("skill", "list"));
        _scene.Freshness.SinceLastGoodPoll = TimeSpan.FromSeconds(40);
        _scene.Strip.EvaluateFreshness();
        _scene.Strip.ApplyCollapsed(new[] { StripChipKey.Version, StripChipKey.Stale });

        var shown = _scene.Strip.Chips.Where(static c => c.IsShown).ToList();
        Assert.True(shown.Count >= 11, "all eleven chips and the +N chip should be showing in this scene");
        foreach (var chip in shown)
        {
            Assert.False(string.IsNullOrWhiteSpace(chip.AutomationName), $"{chip.Key} has no automation name");
            Assert.False(string.IsNullOrWhiteSpace(chip.ToolTip), $"{chip.Key} has no tooltip");
            Assert.False(string.IsNullOrWhiteSpace(chip.Summary), $"{chip.Key} has no one-line summary");
            Assert.Equal(chip.AutomationName, chip.ToString());
        }
    }

    // ---- Watchdog and Guardrail -------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("running", "Watchdog", "Ok", "Watchdog: running")]
    [InlineData("stopped", "Watchdog: stopped", "Bad", "Watchdog: stopped")]
    [InlineData("error", "Watchdog: error", "Bad", "Watchdog: error")]
    [InlineData("degraded", "Watchdog: degraded", "Warn", "Watchdog: degraded")]
    [InlineData("starting", "Watchdog: starting", "Warn", "Watchdog: starting")]
    [InlineData("disabled", "Watchdog: disabled", "Neutral", "Watchdog: disabled")]
    [InlineData("something-new", "Watchdog: something-new", "Neutral", "Watchdog: something-new")]
    public void The_watchdog_chip_is_its_name_while_it_runs_and_its_state_in_words_otherwise(string state, string text, string tone, string name)
    {
        Poll(StripScene.Snapshot(watcher: state));

        var chip = Chip(StripChipKey.Watchdog);
        Assert.True(chip.IsShown);
        Assert.Equal(text, chip.Text);
        Assert.Equal(tone, chip.Tone);
        Assert.Equal(name, chip.AutomationName);
        Assert.Equal(StripChipKind.State, chip.Kind);
        Assert.Contains("Overview", chip.ToolTip, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("running", "Guardrail", "Ok", "Guardrail: running")]
    [InlineData("enabled", "Guardrail", "Ok", "Guardrail: enabled")]
    [InlineData("disabled", "Guardrail: disabled", "Neutral", "Guardrail: disabled")]
    [InlineData("error", "Guardrail: error", "Bad", "Guardrail: error")]
    [InlineData("degraded", "Guardrail: degraded", "Warn", "Guardrail: degraded")]
    [InlineData("starting", "Guardrail: starting", "Warn", "Guardrail: starting")]
    public void The_guardrail_chip_is_the_live_state_and_nothing_else(string state, string text, string tone, string name)
    {
        // The TUI's strip reports the live subsystem state, not a missing credential laid over it: a guardrail can run with a key missing.
        _scene.Services.StatusFacts.PublishMissingKeys(new[] { "OPENCLAW_GATEWAY_TOKEN" });
        Poll(StripScene.Snapshot(guardrail: state));

        var chip = Chip(StripChipKey.Guardrail);
        Assert.Equal(text, chip.Text);
        Assert.Equal(tone, chip.Tone);
        Assert.Equal(name, chip.AutomationName);
        Assert.True(Chip(StripChipKey.Keys).IsShown, "the missing key has its own chip");
    }

    [Fact]
    public void A_gateway_that_does_not_answer_leaves_both_subsystems_unknown_not_stopped()
    {
        Poll(StripScene.Snapshot(running: false));

        Assert.Equal("Guardrail: unknown", Chip(StripChipKey.Guardrail).Text);
        Assert.Equal("Neutral", Chip(StripChipKey.Guardrail).Tone);
        Assert.Equal("Watchdog: unknown", Chip(StripChipKey.Watchdog).Text);
        Assert.Equal("Neutral", Chip(StripChipKey.Watchdog).Tone);
    }

    [Theory]
    [InlineData(AppGatewayState.NotInstalled)]
    [InlineData(AppGatewayState.NotInitialized)]
    public void Where_there_is_no_gateway_to_ask_the_subsystem_chips_are_not_drawn_the_banner_says_why(AppGatewayState state)
    {
        Poll(StripScene.Snapshot(running: false) with { State = state });

        Assert.False(Chip(StripChipKey.Watchdog).IsShown);
        Assert.False(Chip(StripChipKey.Guardrail).IsShown);

        // A gateway that is installed and stopped is a different thing: it is there, and its subsystems are unknown.
        Poll(StripScene.Snapshot(running: false));
        Assert.True(Chip(StripChipKey.Watchdog).IsShown);
        Assert.True(Chip(StripChipKey.Guardrail).IsShown);
    }

    [Fact]
    public void While_monitoring_is_paused_the_subsystem_chips_are_neutral_and_say_it_is_the_last_reading()
    {
        Poll(StripScene.Snapshot(guardrail: "running", paused: true));

        var chip = Chip(StripChipKey.Guardrail);
        Assert.Equal("Neutral", chip.Tone);
        Assert.Equal("Guardrail", chip.Text);
        Assert.Equal("Guardrail: last seen running, monitoring paused", chip.AutomationName);
        Assert.Contains("last reading", chip.ToolTip, StringComparison.Ordinal);
    }

    [Fact]
    public void The_subsystem_chips_use_the_same_words_and_tones_as_the_Overviews_Services_cards()
    {
        var snapshot = StripScene.Snapshot(watcher: "degraded", guardrail: "error");
        Poll(snapshot);

        var cards = OverviewPanelViewModel.BuildServiceCards(snapshot, snapshot.Health, snapshot.ActiveConnectors, new HashSet<string>(), null);
        var watcher = cards.Single(static c => c.Key == "watcher");
        var guardrail = cards.Single(static c => c.Key == "guardrail");

        Assert.Equal(watcher.StateKey, Chip(StripChipKey.Watchdog).Tone);
        Assert.Equal(guardrail.StateKey, Chip(StripChipKey.Guardrail).Tone);
        Assert.Contains(watcher.StateText, Chip(StripChipKey.Watchdog).Text, StringComparison.Ordinal);
        Assert.Contains(guardrail.StateText, Chip(StripChipKey.Guardrail).Text, StringComparison.Ordinal);
    }

    // ---- Keys ---------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Keys_shows_nothing_until_a_credential_list_has_been_read_and_nothing_when_none_is_missing()
    {
        Poll(StripScene.Snapshot());
        Assert.False(Chip(StripChipKey.Keys).IsShown);

        _scene.Services.StatusFacts.PublishMissingKeys(Array.Empty<string>());

        Assert.False(Chip(StripChipKey.Keys).IsShown);
    }

    [Theory]
    [InlineData(1, "Keys: missing OPENAI_API_KEY")]
    [InlineData(2, "Keys: missing OPENAI_API_KEY, CISCO_AI_DEFENSE_API_KEY")]
    [InlineData(3, "Keys: missing OPENAI_API_KEY, CISCO_AI_DEFENSE_API_KEY (+1 more)")]
    [InlineData(5, "Keys: missing OPENAI_API_KEY, CISCO_AI_DEFENSE_API_KEY (+3 more)")]
    public void Keys_names_the_first_two_missing_credentials_and_counts_the_rest(int missing, string text)
    {
        var names = new[] { "OPENAI_API_KEY", "CISCO_AI_DEFENSE_API_KEY", "SPLUNK_HEC_TOKEN", "GALILEO_API_KEY", "OTEL_TOKEN" };
        Poll(StripScene.Snapshot());

        _scene.Services.StatusFacts.PublishMissingKeys(names.Take(missing));

        var chip = Chip(StripChipKey.Keys);
        Assert.True(chip.IsShown);
        Assert.Equal(text, chip.Text);
        Assert.Equal("Bad", chip.Tone);
        foreach (var name in names.Take(missing))
        {
            Assert.Contains(name, chip.ToolTip, StringComparison.Ordinal);
            Assert.Contains(name, chip.AutomationName, StringComparison.Ordinal);
        }

        foreach (var name in names.Skip(missing))
        {
            Assert.DoesNotContain(name, chip.ToolTip, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Keys_goes_when_the_last_missing_credential_is_set_and_comes_back_when_one_is_lost()
    {
        _scene.Services.StatusFacts.PublishMissingKeys(new[] { "OPENAI_API_KEY" });
        Assert.True(Chip(StripChipKey.Keys).IsShown);

        _scene.Services.StatusFacts.PublishMissingKeys(Array.Empty<string>());
        Assert.False(Chip(StripChipKey.Keys).IsShown);

        _scene.Services.StatusFacts.PublishMissingKeys(new[] { "GALILEO_API_KEY" });
        Assert.Equal("Keys: missing GALILEO_API_KEY", Chip(StripChipKey.Keys).Text);
    }

    [Fact]
    public void The_keys_tooltip_says_where_to_set_them_and_that_no_value_is_ever_shown()
    {
        _scene.Services.StatusFacts.PublishMissingKeys(new[] { "OPENAI_API_KEY", "GALILEO_API_KEY" });

        var tip = Chip(StripChipKey.Keys).ToolTip;
        Assert.StartsWith("2 required credentials are not set: OPENAI_API_KEY, GALILEO_API_KEY.", tip, StringComparison.Ordinal);
        Assert.Contains("Setup", tip, StringComparison.Ordinal);
        Assert.Contains("never typed or shown in this app", tip, StringComparison.Ordinal);
    }

    // ---- Alerts and version: the words they have always had -------------------------------------------------------------------------------

    [Fact]
    public void The_alerts_chip_keeps_the_words_the_tone_and_the_tooltip_it_always_had()
    {
        var snapshot = StripScene.Snapshot() with
        {
            AlertCount = 3,
            CriticalAlertCount = 1,
            RecentAlerts = new[] { Alert("a"), Alert("b", "CRITICAL"), Alert("c") },
        };
        Poll(snapshot);

        var chip = Chip(StripChipKey.Alerts);
        Assert.Equal(GatewayPresentation.AlertText(snapshot), chip.Text);
        Assert.Equal("3 recent alerts · 1 critical", chip.Text);
        Assert.Equal("Critical", chip.Tone);
        Assert.Equal(GatewayPresentation.AlertDetail(snapshot), chip.ToolTip);
        Assert.Equal("Alerts: " + GatewayPresentation.AlertDetail(snapshot), chip.AutomationName);
    }

    [Fact]
    public void An_unavailable_alert_list_is_neutral_and_says_why()
    {
        var snapshot = StripScene.Snapshot() with { AlertsUnavailable = "The alert subsystem is not connected on this install." };
        Poll(snapshot);

        var chip = Chip(StripChipKey.Alerts);
        Assert.Equal("Alerts: unavailable", chip.Text);
        Assert.Equal("Neutral", chip.Tone);
        Assert.Equal("The alert subsystem is not connected on this install.", chip.ToolTip);
    }

    [Fact]
    public void The_version_chip_reads_DefenseClaw_and_the_version_and_shows_once_the_gateway_has_reported_one()
    {
        Poll(StripScene.Snapshot(version: null));
        Assert.False(Chip(StripChipKey.Version).IsShown);

        Poll(StripScene.Snapshot(version: "0.8.10"));

        var chip = Chip(StripChipKey.Version);
        Assert.True(chip.IsShown);
        Assert.Equal("DefenseClaw 0.8.10", chip.Text);
        Assert.Equal("Neutral", chip.Tone);
        Assert.Equal(StripChipKind.Chip, chip.Kind);
        Assert.Contains("0.8.10", chip.ToolTip, StringComparison.Ordinal);
    }

    [Fact]
    public void The_detail_sentence_is_the_gateways_own_and_its_own_tooltip()
    {
        Poll(StripScene.Snapshot(detail: "Gateway responding on 127.0.0.1:18970."));

        var chip = Chip(StripChipKey.Detail);
        Assert.Equal("Gateway responding on 127.0.0.1:18970.", chip.Text);
        Assert.Equal("Gateway responding on 127.0.0.1:18970.", chip.ToolTip);
        Assert.Equal(StripChipKind.Sentence, chip.Kind);

        Poll(StripScene.Snapshot(detail: string.Empty));
        Assert.False(chip.IsShown);
    }

    // ---- Connector ------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void One_connector_or_none_keeps_the_words_it_always_had()
    {
        Poll(StripScene.Snapshot(new[] { "claudecode" }));
        Assert.Equal("Connector: claudecode", Chip(StripChipKey.Connector).Text);

        Poll(StripScene.Snapshot(Array.Empty<string>()));
        Assert.Equal("No connector", Chip(StripChipKey.Connector).Text);
    }

    [Fact]
    public void Several_connectors_say_all_connectors_and_how_many_until_the_shared_filter_narrows_the_app_to_one()
    {
        var snapshot = StripScene.Snapshot(new[] { "claudecode", "codex", "hermes" });
        _scene.Services.ConnectorScope.UpdateRoster(snapshot.ActiveConnectors);
        Poll(snapshot);

        var chip = Chip(StripChipKey.Connector);
        Assert.Equal("All connectors (3)", chip.Text);
        Assert.Contains("claudecode, codex, hermes", chip.ToolTip, StringComparison.Ordinal);
        Assert.Contains("Ctrl+Shift+M", chip.ToolTip, StringComparison.Ordinal);

        Assert.True(_scene.Services.ConnectorScope.Set("codex"));
        Assert.Equal("codex (filtered)", chip.Text);
        Assert.Contains("narrowed to codex", chip.ToolTip, StringComparison.Ordinal);
        Assert.Equal(chip.ToolTip, chip.AutomationName);

        Assert.True(_scene.Services.ConnectorScope.Set(null));
        Assert.Equal("All connectors (3)", chip.Text);
    }

    [Fact]
    public void A_filter_whose_connector_leaves_the_roster_falls_back_to_all_connectors()
    {
        var snapshot = StripScene.Snapshot(new[] { "claudecode", "codex" });
        _scene.Services.ConnectorScope.UpdateRoster(snapshot.ActiveConnectors);
        Poll(snapshot);
        Assert.True(_scene.Services.ConnectorScope.Set("codex"));
        Assert.Equal("codex (filtered)", Chip(StripChipKey.Connector).Text);

        // The roster shrinks to one connector: the scope resets by itself, and the chip is the single connector's again.
        _scene.Services.ConnectorScope.UpdateRoster(new[] { "claudecode" });
        Poll(StripScene.Snapshot(new[] { "claudecode" }));

        Assert.Equal("Connector: claudecode", Chip(StripChipKey.Connector).Text);
    }

    // ---- Redaction ------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Redaction_shows_nothing_until_the_overview_has_worded_its_label()
    {
        Poll(StripScene.Snapshot());

        Assert.False(Chip(StripChipKey.Redaction).IsShown);
    }

    [Theory]
    [InlineData("per-route · unredacted", "Warn")]
    [InlineData("per-route · whole-content", "Ok")]
    [InlineData("per-route · sensitive", "Ok")]
    [InlineData("per-route · sensitive,strict", "Ok")]
    [InlineData("per-route · none,sensitive,strict", "Warn")]
    [InlineData("per-route (loading)", "Neutral")]
    [InlineData("per-route (unavailable)", "Neutral")]
    public void Redaction_carries_the_overviews_label_as_it_is_in_a_warning_tone_when_events_leave_unredacted(string label, string tone)
    {
        _scene.Services.StatusFacts.PublishRedaction(label);

        var chip = Chip(StripChipKey.Redaction);
        Assert.True(chip.IsShown);
        Assert.Equal("Redaction: " + label, chip.Text);
        Assert.Equal(tone, chip.Tone);
        Assert.Equal(chip.Text, chip.AutomationName);
        Assert.False(string.IsNullOrWhiteSpace(chip.ToolTip));
    }

    [Fact]
    public void The_label_is_the_core_aggregate_not_a_second_computation_of_it()
    {
        // The Overview publishes ObservabilityPlan.RedactionSummary; the chip shows exactly that string.
        var plan = new ObservabilityPlan(
            "canonical_go_compiled_routes",
            8,
            "digest",
            14,
            new[]
            {
                new PlanDestination("local-sqlite", "sqlite", true, new[] { "logs" }, 14, new[] { "none" }, false, null),
                new PlanDestination("example-otlp", "otlp", true, new[] { "logs" }, 14, new[] { "sensitive" }, false, null),
            },
            0);

        _scene.Services.StatusFacts.PublishRedaction(plan.RedactionSummary);

        Assert.Equal("Redaction: per-route · none,sensitive", Chip(StripChipKey.Redaction).Text);
        Assert.Equal("Warn", Chip(StripChipKey.Redaction).Tone);
        Assert.Contains("profile none", Chip(StripChipKey.Redaction).ToolTip, StringComparison.Ordinal);
    }

    [Fact]
    public void The_unredacted_tooltip_says_what_it_means_and_where_it_comes_from()
    {
        _scene.Services.StatusFacts.PublishRedaction("per-route · unredacted");

        var tip = Chip(StripChipKey.Redaction).ToolTip;
        Assert.Contains("unredacted", tip, StringComparison.Ordinal);
        Assert.Contains("Observability card", tip, StringComparison.Ordinal);
    }

    // ---- Policy posture -------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("observe", false, "Policy: observe")]
    [InlineData("action", true, "Policy: action")]
    [InlineData("Observe", null, "Policy: observe")]
    public void Policy_is_the_mode_the_gateway_reports(string mode, bool? enforcing, string text)
    {
        Poll(StripScene.Snapshot(policyMode: mode, enforcement: enforcing));

        var chip = Chip(StripChipKey.Policy);
        Assert.True(chip.IsShown);
        Assert.Equal(text, chip.Text);
        Assert.Equal("Neutral", chip.Tone);
        Assert.Equal(StripChipKind.Chip, chip.Kind);
        Assert.Contains("Reported by the gateway", chip.ToolTip, StringComparison.Ordinal);
        if (enforcing is { } value)
        {
            Assert.Contains(value ? "Enforcement is on" : "Enforcement is off", chip.ToolTip, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Policy_falls_back_to_config_yamls_mode_when_the_gateway_reports_none_and_says_so()
    {
        Poll(StripScene.Snapshot(running: false));

        var chip = Chip(StripChipKey.Policy);
        Assert.Equal("Policy: observe", chip.Text);
        Assert.Contains("From config.yaml", chip.ToolTip, StringComparison.Ordinal);
        Assert.Contains("not necessarily what is running", chip.ToolTip, StringComparison.Ordinal);
    }

    [Fact]
    public void Policy_for_a_scoped_connector_reads_that_connectors_config_when_the_gateway_is_silent()
    {
        using var scene = new StripScene("guardrail:\n  connector: claudecode\n  connectors:\n    claudecode:\n      mode: observe\n    codex:\n      mode: action\n");
        var snapshot = StripScene.Snapshot(new[] { "claudecode", "codex" }, running: false);
        scene.Services.ConnectorScope.UpdateRoster(snapshot.ActiveConnectors);
        scene.Strip.Apply(snapshot);
        Assert.Equal("Policy: observe", scene.Chip(StripChipKey.Policy).Text);

        Assert.True(scene.Services.ConnectorScope.Set("codex"));

        Assert.Equal("Policy: action", scene.Chip(StripChipKey.Policy).Text);
    }

    [Fact]
    public void No_policy_chip_when_neither_the_gateway_nor_config_names_a_mode()
    {
        using var scene = new StripScene("gateway:\n  api_port: 18970\n");
        scene.Strip.Apply(StripScene.Snapshot(policyMode: null, enforcement: null));

        Assert.False(scene.Chip(StripChipKey.Policy).IsShown);
    }

    // ---- Running ---------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Running_shows_while_any_command_of_this_app_is_in_flight_and_counts_them()
    {
        Assert.False(Chip(StripChipKey.Running).IsShown);

        var doctor = StripScene.Running("doctor");
        _scene.Commands.OnStarted(null, doctor);

        var chip = Chip(StripChipKey.Running);
        Assert.True(chip.IsShown);
        Assert.Equal("Running", chip.Text);
        Assert.Equal("Low", chip.Tone);
        Assert.Equal("1 command is running. Its output is in Activity.", chip.ToolTip);
        Assert.Equal(chip.ToolTip, chip.AutomationName);

        var status = StripScene.Running("status", "--json");
        _scene.Commands.OnStarted(null, status);
        Assert.Equal("Running 2", chip.Text);
        Assert.Equal("2 commands are running. Their output is in Activity.", chip.ToolTip);

        InvocationFactory.Finish(doctor);
        _scene.Commands.OnCompleted(null, doctor);
        Assert.Equal("Running", chip.Text);

        InvocationFactory.Finish(status);
        _scene.Commands.OnCompleted(null, status);
        Assert.False(chip.IsShown);
    }

    [Fact]
    public void A_command_that_fails_or_is_stopped_ends_the_running_chip_too()
    {
        var run = StripScene.Running("gateway", "status");
        _scene.Commands.OnStarted(null, run);
        Assert.True(Chip(StripChipKey.Running).IsShown);

        InvocationFactory.Fail(run, "timed out after 120 s — process tree killed");
        _scene.Commands.OnCompleted(null, run);

        Assert.False(Chip(StripChipKey.Running).IsShown);
    }

    [Fact]
    public void A_hand_off_or_a_refusal_born_finished_never_shows_as_running()
    {
        var handOff = StripScene.Running("keys", "set", "OPENAI_API_KEY");
        InvocationFactory.Finish(handOff);

        _scene.Commands.OnStarted(null, handOff);
        _scene.Commands.OnCompleted(null, handOff);

        Assert.False(Chip(StripChipKey.Running).IsShown);
    }

    // ---- Stale ------------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, 5, false)]
    [InlineData(14, 5, false)]
    [InlineData(15, 5, false)]
    [InlineData(16, 5, true)]
    [InlineData(40, 5, true)]
    [InlineData(6, 2, false)]
    [InlineData(7, 2, true)]
    [InlineData(179, 60, false)]
    [InlineData(181, 60, true)]
    [InlineData(80, 30, false)]
    [InlineData(91, 30, true)]
    public void Stale_is_on_when_the_last_good_poll_is_more_than_three_intervals_old(int sinceSeconds, int cadenceSeconds, bool stale)
    {
        _scene.Freshness.SinceLastGoodPoll = TimeSpan.FromSeconds(sinceSeconds);
        _scene.Freshness.Cadence = TimeSpan.FromSeconds(cadenceSeconds);

        _scene.Strip.EvaluateFreshness();

        var chip = Chip(StripChipKey.Stale);
        Assert.Equal(stale, chip.IsShown);
        if (stale)
        {
            Assert.Equal("Stale", chip.Text);
            Assert.Equal("Warn", chip.Tone);
            Assert.Contains(StripPresentation.Span(TimeSpan.FromSeconds(sinceSeconds)), chip.ToolTip, StringComparison.Ordinal);
            Assert.Contains($"{StripPresentation.Span(TimeSpan.FromSeconds(cadenceSeconds))} check interval", chip.ToolTip, StringComparison.Ordinal);
            Assert.Contains("3 times", chip.ToolTip, StringComparison.Ordinal);
            Assert.StartsWith("Stale data: the last good gateway poll finished", chip.AutomationName, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Nothing_is_stale_before_a_first_good_poll_or_while_monitoring_is_paused()
    {
        _scene.Freshness.SinceLastGoodPoll = null;
        _scene.Strip.EvaluateFreshness();
        Assert.False(Chip(StripChipKey.Stale).IsShown);

        _scene.Freshness.SinceLastGoodPoll = TimeSpan.FromMinutes(10);
        _scene.Freshness.IsPaused = true;
        _scene.Strip.EvaluateFreshness();
        Assert.False(Chip(StripChipKey.Stale).IsShown);

        _scene.Freshness.IsPaused = false;
        _scene.Strip.EvaluateFreshness();
        Assert.True(Chip(StripChipKey.Stale).IsShown);
    }

    [Theory]
    [InlineData(12, "12 s")]
    [InlineData(119, "119 s")]
    [InlineData(120, "2 min")]
    [InlineData(125, "2 min 5 s")]
    [InlineData(3600, "60 min")]
    [InlineData(7260, "2 h 1 min")]
    public void Spans_are_worded_the_way_a_person_reads_them(int seconds, string words) =>
        Assert.Equal(words, StripPresentation.Span(TimeSpan.FromSeconds(seconds)));

    // ---- The +N chip -------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_overflow_chip_is_empty_until_the_panel_hides_something()
    {
        Poll(StripScene.Snapshot());

        var chip = Chip(StripChipKey.Overflow);
        Assert.True(chip.IsShown, "present for the panel to place: the panel decides whether it has room");
        Assert.Equal(string.Empty, chip.Text);
        Assert.Equal(string.Empty, chip.AutomationName);
    }

    [Fact]
    public void The_overflow_chip_counts_the_hidden_chips_lists_them_and_takes_the_worst_tone()
    {
        Poll(StripScene.Snapshot());
        _scene.Services.StatusFacts.PublishRedaction("per-route · unredacted");

        _scene.Strip.ApplyCollapsed(new[] { StripChipKey.Redaction, StripChipKey.Policy, StripChipKey.Version });

        var chip = Chip(StripChipKey.Overflow);
        Assert.Equal("+3", chip.Text);
        Assert.Equal("Warn", chip.Tone);
        Assert.Contains("3 status items are hidden at this window width", chip.ToolTip, StringComparison.Ordinal);
        Assert.Contains("• Redaction: per-route · unredacted", chip.ToolTip, StringComparison.Ordinal);
        Assert.Contains("• Policy: observe", chip.ToolTip, StringComparison.Ordinal);
        Assert.Contains("• DefenseClaw 0.8.10", chip.ToolTip, StringComparison.Ordinal);
        Assert.StartsWith("3 more status items hidden at this window width: Redaction: per-route · unredacted; Policy: observe; DefenseClaw 0.8.10", chip.AutomationName, StringComparison.Ordinal);
    }

    [Fact]
    public void A_hidden_chip_that_changes_tone_recolours_the_overflow_chip_so_a_warning_with_no_room_is_not_silent()
    {
        Poll(StripScene.Snapshot());
        _scene.Services.StatusFacts.PublishRedaction("per-route · sensitive");
        _scene.Strip.ApplyCollapsed(new[] { StripChipKey.Redaction, StripChipKey.Version });
        Assert.Equal("Neutral", Chip(StripChipKey.Overflow).Tone);

        _scene.Services.StatusFacts.PublishRedaction("per-route · unredacted");
        Assert.Equal("Warn", Chip(StripChipKey.Overflow).Tone);

        _scene.Services.StatusFacts.PublishMissingKeys(new[] { "OPENAI_API_KEY" });
        _scene.Strip.ApplyCollapsed(new[] { StripChipKey.Redaction, StripChipKey.Keys });
        Assert.Equal("Bad", Chip(StripChipKey.Overflow).Tone);
    }

    [Fact]
    public void A_hidden_chip_that_stops_showing_leaves_the_overflow_chip_and_it_empties_with_the_last()
    {
        Poll(StripScene.Snapshot());
        _scene.Services.StatusFacts.PublishMissingKeys(new[] { "OPENAI_API_KEY" });
        _scene.Strip.ApplyCollapsed(new[] { StripChipKey.Keys });
        Assert.Equal("+1", Chip(StripChipKey.Overflow).Text);
        Assert.Contains("1 status item is hidden", Chip(StripChipKey.Overflow).ToolTip, StringComparison.Ordinal);

        _scene.Services.StatusFacts.PublishMissingKeys(Array.Empty<string>());

        Assert.Equal(string.Empty, Chip(StripChipKey.Overflow).Text);
    }

    [Fact]
    public void The_command_the_panel_runs_takes_the_hidden_chips_themselves()
    {
        Poll(StripScene.Snapshot());
        var hidden = new object?[] { Chip(StripChipKey.Version), Chip(StripChipKey.Policy), null, "not a chip" };

        _scene.Strip.ApplyHiddenCommand.Execute(hidden);

        Assert.Equal("+2", Chip(StripChipKey.Overflow).Text);

        _scene.Strip.ApplyHiddenCommand.Execute(Array.Empty<object?>());
        Assert.Equal(string.Empty, Chip(StripChipKey.Overflow).Text);
    }

    // ---- Lifetime ----------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_disposed_strip_hears_nothing_more()
    {
        _scene.Strip.Dispose();

        _scene.Services.StatusFacts.PublishRedaction("per-route · unredacted");
        _scene.Services.StatusFacts.PublishMissingKeys(new[] { "OPENAI_API_KEY" });
        _scene.Commands.OnStarted(null, StripScene.Running("doctor"));

        Assert.False(Chip(StripChipKey.Redaction).IsShown);
        Assert.False(Chip(StripChipKey.Keys).IsShown);
        Assert.False(Chip(StripChipKey.Running).IsShown);
    }

    private static GatewayAlert Alert(string id, string severity = "HIGH") =>
        new()
        {
            Id = id,
            Severity = severity,
            Timestamp = DateTimeOffset.UtcNow,
            Action = "block",
            Target = "synthetic-target",
        };
}
