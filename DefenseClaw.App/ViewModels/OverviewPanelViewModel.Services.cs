using System.Globalization;
using System.Text.Json;
using DefenseClaw.App.Services;
using DefenseClaw.Core.Gateway.Models;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// The Services card (CUST-313): the nine service cards of the DefenseClaw TUI's Overview, in its order and under its names - Gateway, Agent,
/// Watchdog, Guardrail, API, Sinks, Telemetry, AI Discovery, Sandbox (<c>OverviewPanelModel.service_cards</c> in 0.8.10's
/// <c>tui/services/overview_state.py</c>; the Mac's <c>AppState.services</c> mirrors it). Each card is a state word read from one <c>/health</c>
/// block, then the detail line the TUI prints after it. Re-derived from the snapshot on every poll; no I/O.
/// <para>
/// <b>By presence, never by version.</b> Every card reads the block it is named for and nothing else, so the same code serves 0.8.10 and the
/// newer runtime: a block that is not there is <c>unknown</c>, not an error, and no card asks which runtime answered. (Neither runtime emits
/// <c>sinks</c> today; the TUI's reader and this card are ready for a gateway that does.)
/// </para>
/// <para>
/// <b>Gateway.</b> The TUI's Gateway row reads <c>/health</c>'s <c>gateway</c> block, which is the optional OpenClaw fleet uplink: <c>disabled</c>
/// on every install without an OpenClaw fleet, and OpenClaw does not run on Windows, so always. The TUI's own status strip says so (it reports the
/// sidecar's availability, "not the optional fleet uplink") and so does this card: the state word is the gateway's availability as this app measures
/// it (the poll that produced the snapshot), and the uplink moves to the detail line (<c>no OpenClaw fleet configured (standalone mode)</c>). An
/// uplink that is in use (any state but <c>disabled</c>) speaks for itself.
/// </para>
/// <para>
/// <b>Agent</b> is the roll-up of the connectors <c>/health</c> lists: <c>running</c> only when every one is up, <c>degraded</c> when some are, the
/// first one's own state when none is, <c>disabled</c> when the gateway lists none because every connector of the roster is switched off (it drops
/// disabled connectors, so an empty list is not a stopped gateway) and <c>unknown</c> when there is nothing to roll up. Which connectors are switched
/// off comes from <c>status --json</c> (<c>connectors[].enabled</c>), the same answer the TUI takes from <c>guardrail.effective_enabled</c>.
/// </para>
/// <para>
/// <b>Sandbox</b> is a platform fact, not a reading: OpenShell sandboxes run on Linux and macOS only (the runtime refuses them on Windows and WSL2),
/// so the card says so whatever <c>/health</c> holds, and nothing probes or runs anything for it.
/// </para>
/// <para>
/// <b>What moved.</b> The card used to list every block of <c>/health</c>. The TUI has no row for three of them, so they left this card:
/// the fleet uplink is the Gateway card's detail line (above), and <c>config</c> (the reload state) and <c>application_protection</c> are
/// configuration, so they are rows of the Configuration card (<see cref="AddApplicationProtectionRow"/>, <see cref="AddConfigReloadRow"/>).
/// </para>
/// </summary>
public sealed partial class OverviewPanelViewModel
{
    /// <summary>The Sandbox card's state word: the platform fact. Lower case like every other state word of the card.</summary>
    internal const string SandboxStateText = "not supported on Windows";

    /// <summary>What the Sandbox card says under its state: the runtime's own reason (<c>UNSUPPORTED_PLATFORM_MESSAGE</c> of its <c>sandbox</c> group).</summary>
    internal const string SandboxDetailText = "OpenShell sandboxes run on Linux and macOS only";

    /// <summary>What the Sinks card says while the gateway answers without a <c>sinks</c> block, which is what both runtimes do today.</summary>
    internal const string SinksNotReportedText = "not reported by this gateway";

    /// <summary>The cards' keys (the TUI's), in the order it lists them.</summary>
    internal static readonly string[] ServiceKeys =
    {
        "gateway", "agent", "watcher", "guardrail", "api", "sinks", "telemetry", "ai_discovery", "sandbox",
    };

    /// <summary>The states that count as a connector being up, the TUI's <c>_RUNNING_STATES</c>.</summary>
    private static readonly string[] UpStates = { "running", "active", "enabled" };

    /// <summary>Re-derives the nine cards; the roster, the switched-off connectors and the claw mode are what the panel already holds.</summary>
    private void BuildServices(GatewayHealth? health)
    {
        var disabled = new HashSet<string>(
            _status.Connectors.Where(static c => c.Enabled == false).Select(static c => c.Name),
            StringComparer.OrdinalIgnoreCase);

        SyncByEquality(
            ServiceRows,
            BuildServiceCards(_snapshot, health, Roster(), disabled, Services.Config.Config.Claw.Mode),
            static row => row.Key);
    }

    /// <summary>
    /// The nine cards for one poll. Pure, so the tests can feed it a payload from a fixture.
    /// </summary>
    /// <param name="snapshot">What the poll found: its state is the gateway's availability, its port names the one that did not answer.</param>
    /// <param name="health"><c>/health</c>, or null when nothing answered (every block is then <c>unknown</c>, as in the TUI).</param>
    /// <param name="roster">The connectors of this install (config.yaml's, the gateway's, <c>status --json</c>'s).</param>
    /// <param name="disabledConnectors">The ones <c>status --json</c> says are switched off (<c>enabled: false</c>), compared without case.</param>
    /// <param name="configuredConnector">config.yaml's <c>claw.mode</c>, named when the roster has nothing to name.</param>
    internal static IReadOnlyList<ServiceRow> BuildServiceCards(
        GatewaySnapshot snapshot,
        GatewayHealth? health,
        IReadOnlyList<string> roster,
        IReadOnlySet<string> disabledConnectors,
        string? configuredConnector)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(disabledConnectors);

        return new[]
        {
            GatewayCard(snapshot, health),
            AgentCard(health, roster, disabledConnectors, configuredConnector),
            Subsystem("watcher", "Watchdog", health?.Watcher, WatchdogDetail),
            Subsystem("guardrail", "Guardrail", health?.Guardrail, OwnWords, GuardrailPosture),
            Subsystem("api", "API", health?.Api, static api => DetailText(api, "addr") ?? string.Empty),
            SinksCard(health),
            Subsystem("telemetry", "Telemetry", health?.Telemetry, TelemetryDetail, TelemetryPosture),
            Subsystem("ai_discovery", "AI Discovery", health?.AiDiscovery, AiDiscoveryDetail),
            SandboxCard(),
        };
    }

    // ---- Gateway ----------------------------------------------------------------------------------------------------------------------

    private static ServiceRow GatewayCard(GatewaySnapshot snapshot, GatewayHealth? health)
    {
        // The fleet uplink is "in use" when it reports anything but disabled; a disabled one is the normal standalone state and says nothing
        // about whether the gateway itself is up.
        var uplink = health?.FleetUplink;
        var uplinkState = uplink?.State?.Trim() ?? string.Empty;
        var uplinkInUse = uplink is not null && uplinkState.Length > 0 && !uplink.IsDisabled;

        var (availability, availabilityTone, availabilityNote) = Availability(snapshot);

        var detail = new List<string>();
        if (availabilityNote.Length > 0)
        {
            detail.Add(availabilityNote);
        }

        if (health is { UptimeMs: > 0 })
        {
            detail.Add(HealthTrustPresentation.Mark("up " + FormatDuration(health.Uptime), snapshot));
        }

        if (uplink is not null)
        {
            var words = uplink.IsDisabled
                ? DetailText(uplink, "summary") ?? DetailText(uplink, "hint") ?? string.Empty
                : OwnWords(uplink);
            if (words.Length > 0)
            {
                detail.Add(words);
            }
        }

        return new ServiceRow
        {
            Key = "gateway",
            Name = "Gateway",
            StateText = uplinkInUse ? uplinkState : availability,
            StateKey = uplinkInUse ? ServiceTone(uplinkState) : availabilityTone,
            Detail = string.Join(" · ", detail),
            SinceText = uplink is not null ? SinceOf(uplink) : health?.StartedAt is { } started ? FormatSince(started) : string.Empty,
        };
    }

    /// <summary>The gateway's availability as the poll found it: the state word, its tone and, when the gateway did not answer, a note.</summary>
    private static (string State, string Tone, string Note) Availability(GatewaySnapshot snapshot) => snapshot.State switch
    {
        AppGatewayState.Running => ("running", "Ok", string.Empty),
        AppGatewayState.Degraded => ("degraded", "Warn", string.Empty),
        AppGatewayState.WslGatewayDetected => ("wsl gateway", "Warn", "answering from WSL, not the native install"),
        AppGatewayState.GatewayStopped => (
            "stopped",
            "Bad",
            snapshot.ApiPort > 0 ? $"not answering on port {snapshot.ApiPort.ToString(CultureInfo.InvariantCulture)}" : "not running"),
        AppGatewayState.NotInstalled => ("not installed", "Neutral", string.Empty),
        AppGatewayState.NotInitialized => ("not initialized", "Neutral", string.Empty),
        _ => ("unknown", "Neutral", string.Empty),
    };

    // ---- Agent ------------------------------------------------------------------------------------------------------------------------

    private static ServiceRow AgentCard(
        GatewayHealth? health,
        IReadOnlyList<string> roster,
        IReadOnlySet<string> disabledConnectors,
        string? configuredConnector)
    {
        var rostered = roster.Where(static n => !string.IsNullOrWhiteSpace(n)).ToList();
        var disabledCount = rostered.Count(name => disabledConnectors.Any(off => string.Equals(off, name, StringComparison.OrdinalIgnoreCase)));
        var allDisabled = rostered.Count > 0 && disabledCount == rostered.Count;

        // /health lists one entry per live connector. A gateway that predates the array reports only the primary one.
        var live = health is null
            ? new List<ConnectorStatus>()
            : health.Connectors.Where(static c => !string.IsNullOrWhiteSpace(c.Name)).ToList();
        var primary = health?.Connector is { Name: { Length: > 0 } } reportedPrimary ? reportedPrimary : live.FirstOrDefault();
        if (live.Count == 0 && primary is not null)
        {
            live.Add(primary);
        }

        string state;
        if (health is null)
        {
            state = "unknown";
        }
        else if (live.Count > 0)
        {
            state = RollUp(live);
        }
        else
        {
            // The gateway drops a connector that is switched off, so "nothing listed" is "disabled" when that is why, and unknown otherwise.
            state = allDisabled ? "disabled" : "unknown";
        }

        return new ServiceRow
        {
            Key = "agent",
            Name = "Agent",
            StateText = state,
            StateKey = ServiceTone(state),
            Detail = AgentDetail(rostered, disabledCount, allDisabled, live, primary, configuredConnector),
        };
    }

    /// <summary>
    /// The TUI's <c>_aggregate_connector_state</c>: <c>running</c> only when every connector is up, <c>degraded</c> when some are, otherwise the
    /// first connector's own state (a connector that is down says how).
    /// </summary>
    private static string RollUp(IReadOnlyList<ConnectorStatus> connectors)
    {
        var states = connectors.Select(static c => (c.State ?? string.Empty).Trim().ToLowerInvariant()).ToList();
        var up = states.Count(static s => UpStates.Contains(s));

        if (up == states.Count)
        {
            return "running";
        }

        if (up > 0)
        {
            return "degraded";
        }

        return states[0].Length > 0 ? states[0] : "unknown";
    }

    private static bool IsUp(ConnectorStatus connector) => UpStates.Contains((connector.State ?? string.Empty).Trim().ToLowerInvariant());

    /// <summary>The TUI's <c>agent_detail</c>: a head count for several connectors, the one connector's own counters for a single one.</summary>
    private static string AgentDetail(
        IReadOnlyList<string> rostered,
        int disabledCount,
        bool allDisabled,
        IReadOnlyList<ConnectorStatus> live,
        ConnectorStatus? primary,
        string? configuredConnector)
    {
        if (rostered.Count > 1)
        {
            var total = rostered.Count;
            var enabledTotal = Math.Max(total - disabledCount, 0);
            var running = live.Count(IsUp);

            if (disabledCount == 0)
            {
                return live.Count == 0
                    ? $"{total} connectors configured"
                    : running == total ? $"{total} connectors active" : $"{running}/{total} connectors running";
            }

            // Switched-off connectors stay in the roster but enforce nothing, so they are counted apart and left out of "active".
            var suffix = $" · {disabledCount} disabled";
            if (enabledTotal == 0)
            {
                return "0 active" + suffix;
            }

            return live.Count > 0 && running < enabledTotal ? $"{running}/{enabledTotal} running{suffix}" : $"{enabledTotal} active{suffix}";
        }

        if (primary is null)
        {
            var name = rostered.FirstOrDefault() ?? configuredConnector;
            if (string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }

            return allDisabled ? $"{FriendlyConnectorName(name)} (disabled)" : $"{FriendlyConnectorName(name)} (configured, not connected)";
        }

        var parts = new List<string> { FriendlyConnectorName(primary.Name ?? string.Empty) };
        if (!string.IsNullOrWhiteSpace(primary.ToolInspectionMode))
        {
            parts.Add(primary.ToolInspectionMode);
        }

        if (primary.Requests != 0)
        {
            parts.Add($"{primary.Requests.ToString(CultureInfo.InvariantCulture)} req");
        }

        if (primary.ToolBlocks != 0)
        {
            parts.Add($"{primary.ToolBlocks.ToString(CultureInfo.InvariantCulture)} tool blocks");
        }

        if (primary.SubprocessBlocks != 0)
        {
            parts.Add($"{primary.SubprocessBlocks.ToString(CultureInfo.InvariantCulture)} subprocess blocks");
        }

        return string.Join(" - ", parts);
    }

    // ---- The cards that read one block ------------------------------------------------------------------------------------------------

    /// <summary>
    /// One card from one block: the state word, then the card's own detail, or the block's own words when it has none. A block that is not there
    /// is <c>unknown</c>, which is what the TUI prints for an empty one.
    /// </summary>
    private static ServiceRow Subsystem(
        string key,
        string name,
        ServiceState? block,
        Func<ServiceState, string> detailOf,
        Func<ServiceState, string>? postureOf = null)
    {
        if (block is null)
        {
            return Unreported(key, name, string.Empty);
        }

        var state = StateWord(block);
        var detail = detailOf(block);
        return new ServiceRow
        {
            Key = key,
            Name = name,
            StateText = state,
            StateKey = ServiceTone(state),
            Detail = detail.Length > 0 ? detail : OwnWords(block),
            Posture = postureOf?.Invoke(block) ?? string.Empty,
            SinceText = SinceOf(block),
        };
    }

    private static ServiceRow Unreported(string key, string name, string detail) => new()
    {
        Key = key,
        Name = name,
        StateText = "unknown",
        StateKey = "Neutral",
        Detail = detail,
    };

    private static string StateWord(ServiceState block) => string.IsNullOrWhiteSpace(block.State) ? "unknown" : block.State.Trim();

    /// <summary>The tone of a state word. The TUI draws <c>enabled</c> green with <c>running</c> and <c>active</c>; <see cref="ClassifyText"/> leaves it neutral.</summary>
    private static string ServiceTone(string state) =>
        string.Equals(state.Trim(), "enabled", StringComparison.OrdinalIgnoreCase) ? "Ok" : ClassifyText(state);

    /// <summary>
    /// What the block says about itself when the card has nothing better: its summary, its hint and, unless it is simply disabled, its last
    /// error. (A disabled subsystem writes its own name into <c>last_error</c> on a healthy standalone box, so that is never shown for it.)
    /// </summary>
    private static string OwnWords(ServiceState block)
    {
        if (DetailText(block, "summary") is { Length: > 0 } summary && !string.IsNullOrWhiteSpace(summary))
        {
            return summary;
        }

        if (DetailText(block, "hint") is { Length: > 0 } hint && !string.IsNullOrWhiteSpace(hint))
        {
            return hint;
        }

        return block.LastError is { Length: > 0 } error && !block.IsDisabled ? error : string.Empty;
    }

    private static string SinceOf(ServiceState block) => block.Since is { } since ? FormatSince(since) : string.Empty;

    private static string FormatSince(DateTimeOffset since) =>
        $"since {since.ToLocalTime().ToString("MMM d HH:mm", CultureInfo.CurrentCulture)}";

    /// <summary>A string or number out of <c>details</c> as text (a number as the gateway wrote it); null when absent or any other type.</summary>
    private static string? DetailText(ServiceState block, string key)
    {
        if (block.Details is not { ValueKind: JsonValueKind.Object } details || !details.TryGetProperty(key, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }

    /// <summary>Watchdog: <c>2 skill dirs, 2 plugin dirs</c>.</summary>
    private static string WatchdogDetail(ServiceState watcher)
    {
        var parts = new List<string>();
        if (DetailText(watcher, "skill_dirs") is { } skills)
        {
            parts.Add($"{skills} skill dirs");
        }

        if (DetailText(watcher, "plugin_dirs") is { } plugins)
        {
            parts.Add($"{plugins} plugin dirs");
        }

        return string.Join(", ", parts);
    }

    /// <summary>AI Discovery: <c>96 active, 0 new, enhanced</c>.</summary>
    private static string AiDiscoveryDetail(ServiceState discovery)
    {
        var parts = new List<string>();
        if (DetailText(discovery, "active_signals") is { } active)
        {
            parts.Add($"{active} active");
        }

        if (DetailText(discovery, "new_signals") is { } added)
        {
            parts.Add($"{added} new");
        }

        if (DetailText(discovery, "mode") is { Length: > 0 } mode)
        {
            parts.Add(mode);
        }

        return string.Join(", ", parts);
    }

    /// <summary>
    /// Enforcement posture, kept apart from liveness on purpose: <c>guardrail</c> runs with <c>enforcement_enabled: false</c> in observe mode,
    /// and one word for both would paint a correctly configured box as broken. Said only when the block carries a posture at all (a guardrail
    /// with no connector has none).
    /// </summary>
    private static string GuardrailPosture(ServiceState guardrail)
    {
        var mode = DetailText(guardrail, "policy_mode") ?? DetailText(guardrail, "mode");
        var enforcing = guardrail.DetailBool("enforcement_enabled");
        if (mode is null && enforcing is null)
        {
            return string.Empty;
        }

        var parts = new List<string> { mode ?? "unknown" };
        if (enforcing is { } enforced)
        {
            parts.Add(enforced ? "enforcing" : "observing (enforcement off)");
        }

        if (DetailText(guardrail, "enforcement_surface") is { Length: > 0 } surface)
        {
            parts.Add(surface);
        }

        return string.Join(" · ", parts);
    }

    // ---- Sinks, Telemetry, Sandbox ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>sinks</c> is a block the health model does not name, so it stays untyped (a block of the wrong shape must not fail the whole
    /// <c>/health</c> parse) and is read through <see cref="Service"/>, which treats one it cannot read as absent. Absent is <c>unknown</c>, as in the
    /// 0.8.10 TUI; the card says why. (The newer TUI hides the row instead; this one keeps the nine.)
    /// </summary>
    private static ServiceRow SinksCard(GatewayHealth? health)
    {
        if (health is null)
        {
            return Unreported("sinks", "Sinks", string.Empty);
        }

        var block = Service(health, "sinks");
        return block is null
            ? Unreported("sinks", "Sinks", SinksNotReportedText)
            : Subsystem("sinks", "Sinks", block, SinksDetail);
    }

    /// <summary>Sinks: the gateway's own summary (<c>1 of 1 enabled</c>), else <c>1 sink: audit-file (healthy)</c> from <c>details.sinks[]</c>.</summary>
    private static string SinksDetail(ServiceState sinks)
    {
        if (DetailText(sinks, "summary") is { Length: > 0 } summary && !string.IsNullOrWhiteSpace(summary))
        {
            return summary;
        }

        if (sinks.Details is not { ValueKind: JsonValueKind.Object } details)
        {
            return string.Empty;
        }

        var labels = Items(details, "sinks")
            .Select(static s => (Name: Text(s, "name"), State: Text(s, "state")))
            .Where(static s => s.Name.Length > 0)
            .Select(static s => s.State.Length > 0 ? $"{s.Name} ({s.State})" : s.Name)
            .ToList();

        return labels.Count == 0 ? string.Empty : Counted(labels.Count, "sink", labels);
    }

    /// <summary>
    /// Telemetry, as the TUI prints it: how many destinations are on and what state each is in (<c>2 destinations: local-sqlite (healthy),
    /// example-otlp (healthy)</c>). <c>/health</c> lists them under <c>telemetry.details.destinations[]</c>; a gateway that only counts them gets the count.
    /// </summary>
    private static string TelemetryDetail(ServiceState telemetry)
    {
        if (telemetry.Details is not { ValueKind: JsonValueKind.Object } details)
        {
            return string.Empty;
        }

        if (details.TryGetProperty("destinations", out var listed) && listed.ValueKind == JsonValueKind.Array)
        {
            var labels = Items(details, "destinations")
                .Where(static d => !(d.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.False))
                .Select(static d => (Name: Text(d, "name"), State: Text(d, "state")))
                .Where(static d => d.Name.Length > 0)
                .Select(static d => d.State.Length > 0 ? $"{d.Name} ({d.State})" : d.Name)
                .ToList();

            return Counted(labels.Count, "destination", labels);
        }

        return DetailNumber(telemetry, "destination_count") is { } count ? Counted((int)Math.Clamp(count, 0, int.MaxValue), "destination", Array.Empty<string>()) : string.Empty;
    }

    /// <summary>Telemetry's retention, which this app shows and the TUI's card does not: <c>retention 90 days</c>.</summary>
    private static string TelemetryPosture(ServiceState telemetry) =>
        DetailNumber(telemetry, "retention_days") is { } days ? $"retention {days.ToString(CultureInfo.InvariantCulture)} days" : string.Empty;

    /// <summary><c>2 destinations: a (healthy), b (healthy)</c>; <c>1 sink: x</c>; <c>0 destinations</c>.</summary>
    private static string Counted(int count, string noun, IReadOnlyList<string> labels) =>
        $"{count.ToString(CultureInfo.InvariantCulture)} {noun}{(count == 1 ? string.Empty : "s")}" + (labels.Count > 0 ? ": " + string.Join(", ", labels) : string.Empty);

    private static ServiceRow SandboxCard() => new()
    {
        Key = "sandbox",
        Name = "Sandbox",
        StateText = SandboxStateText,
        StateKey = "Neutral",
        Detail = SandboxDetailText,
    };

    // ---- What moved to the Configuration card -----------------------------------------------------------------------------------------

    /// <summary>
    /// The Application protection row: the live <c>/health</c> block when the gateway has one (it is read every poll), else what
    /// <c>status --json</c> last said. It is configuration (is it on, and in which asset policy mode), not a service, which is why the TUI has no
    /// row for it among the nine.
    /// </summary>
    private void AddApplicationProtectionRow(List<(string Label, string Value, string Tone)> rows, string suffix)
    {
        var label = "Application protection" + suffix;

        if (_snapshot.Health?.ApplicationProtection is { } block)
        {
            var state = StateWord(block);
            var enabled = block.DetailBool("enabled") ?? block.IsRunning;
            var value = enabled
                ? "on" + (block.IsRunning ? string.Empty : $" ({state})") + $" · asset policy {DetailText(block, "asset_policy_mode") ?? "?"}"
                : $"off ({state})";
            rows.Add((label, value, enabled && !block.IsRunning && ServiceTone(state) != "Ok" ? "Warn" : "Neutral"));
            return;
        }

        if (_status.ApplicationProtectionEnabled is { } protection)
        {
            rows.Add((label, protection ? "on" : $"off ({_status.ApplicationProtectionState ?? "disabled"})", "Neutral"));
        }
    }

    /// <summary>
    /// The Config reload row, from <c>/health</c>'s <c>config</c> block: whether the gateway has picked up config.yaml (its state, the generation it
    /// is on, why it last reloaded and what that changed) and, on a runtime that says so, which changes still need a restart. It sits beside the Config
    /// file row; the TUI has no row for it.
    /// </summary>
    private void AddConfigReloadRow(List<(string Label, string Value, string Tone)> rows, string suffix)
    {
        if (_snapshot.Health?.Config is not { } block)
        {
            return;
        }

        var parts = new List<string> { StateWord(block) };
        if (DetailText(block, "generation") is { } generation)
        {
            parts.Add($"generation {generation}");
        }

        if (DetailText(block, "reason") is { Length: > 0 } reason)
        {
            parts.Add($"last reload: {reason}");
        }

        var changed = StringItems(block, "changed");
        if (changed.Count > 0)
        {
            parts.Add("changed: " + string.Join(", ", changed));
        }

        var restart = StringItems(block, "restart_required");
        if (restart.Count > 0)
        {
            parts.Add("restart required: " + string.Join(", ", restart));
        }

        var attention = restart.Count > 0 || ServiceTone(StateWord(block)) is "Warn" or "Bad";
        rows.Add(("Config reload" + suffix, string.Join(" · ", parts), attention ? "Warn" : "Neutral"));
    }

    private static List<string> StringItems(ServiceState block, string key)
    {
        var items = new List<string>();
        if (block.Details is { ValueKind: JsonValueKind.Object } details && details.TryGetProperty(key, out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text)
                {
                    items.Add(text);
                }
            }
        }

        return items;
    }
}
