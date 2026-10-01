using System.Globalization;
using DefenseClaw.App.Services;
using DefenseClaw.Core.Gateway.Models;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// The rules of "What needs attention" that the Mac adds beyond the gateway/alert rows (CUST-213; <c>AppState.overviewNotices</c>): the agents
/// that were detected but are not configured, the guardrail that is off, what the doctor cache says once it is
/// reconciled with the live <c>/health</c>, a required key that is missing, connector drift and a connector that has seen no requests.
/// Everything here is derived from state the panel already holds (the snapshot, config.yaml, the doctor cache, the discovery file): no I/O and
/// nothing is run. The commands on the rows are text to copy.
/// </summary>
public sealed partial class OverviewPanelViewModel : IAcceptsNavigation
{
    // ---- The palette's "Run doctor" (a navigation payload: show the Doctor card's button, press nothing) ----

    /// <summary>Raised on the UI thread when something asked for the Doctor card's Run doctor button to be shown and focused.</summary>
    public event EventHandler? DoctorFocusRequested;

    /// <summary>Set by <see cref="Accept"/>, cleared by the view once it has moved focus (a first visit asks before the view is loaded).</summary>
    public bool DoctorFocusPending { get; set; }

    /// <summary>Takes an <see cref="OverviewFocus"/>; any other payload is ignored.</summary>
    public void Accept(object payload)
    {
        if (payload is OverviewFocus { Section: OverviewFocus.DoctorSection })
        {
            DoctorFocusPending = true;
            DoctorFocusRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>What the copy-only hint on a missing-key row says (the CLI's own filler for the keys doctor found missing; never run by the app).</summary>
    internal const string FillMissingKeysCommand = "defenseclaw keys fill-missing";

    /// <summary>The Mac's zero-requests notice waits this long after the gateway started before it speaks.</summary>
    internal static readonly TimeSpan ZeroRequestsAfter = TimeSpan.FromSeconds(60);

    /// <summary>The discovery file's agents that map to a hook connector (<see cref="OverviewDetectedConnectors"/>), kept so the live roster can be re-compared on every poll without reading the file again.</summary>
    private IReadOnlyList<string> _detectedDiscovered = Array.Empty<string>();

    /// <summary>
    /// True once the gateway has an installation behind it (the Mac's <c>installDetected</c>): anything but "not installed" and "installed but never
    /// initialized". A snapshot with no install state (before the first poll) is not an install either, so nothing here speaks before there is evidence.
    /// </summary>
    private static bool InstallDetected(GatewaySnapshot snapshot) =>
        snapshot.Install is { } install &&
        install != DefenseClaw.Core.Install.InstallState.NotInstalled &&
        install != DefenseClaw.Core.Install.InstallState.InstalledNotInitialized;

    /// <summary>Adds the Mac's remaining rules, in its emission order, to <paramref name="rows"/>.</summary>
    private void AppendParityAttention(List<AttentionRow> rows, GatewaySnapshot snapshot)
    {
        var health = snapshot.Health;
        var installDetected = InstallDetected(snapshot);

        // A standalone gateway ("gateway" subsystem disabled) explains itself in /health; the Mac passes that hint on.
        if (health?.FleetUplink is { IsDisabled: true } standalone &&
            (standalone.DetailString("hint") is { Length: > 0 } || standalone.DetailString("summary") is { Length: > 0 }))
        {
            rows.Add(new AttentionRow
            {
                Title = "The gateway runs standalone",
                Detail = standalone.DetailString("hint") is { Length: > 0 } hint ? hint : standalone.DetailString("summary")!,
                SeverityKey = "Info",
            });
        }

        var unconfigured = OverviewDetectedConnectors.Unconfigured(_detectedDiscovered, ManagedConnectorNames());
        if (unconfigured.Count > 0)
        {
            var names = string.Join(", ", unconfigured.Select(FriendlyConnectorName));
            rows.Add(new AttentionRow
            {
                Title = $"Detected but not configured: {names}",
                Detail = "DefenseClaw found these agents on this machine, but no connector is configured for them. Add from the Connectors card or Setup; " +
                         "discovery alone never changes enforcement.",
                SeverityKey = "High",
            });
        }

        if (installDetected && !Services.Config.Config.Guardrail.Enabled)
        {
            rows.Add(new AttentionRow
            {
                Title = "Guardrail not configured",
                Detail = "The LLM guardrail is off in config.yaml (guardrail.enabled). Set it up in Setup, under Guardrail.",
                SeverityKey = "High",
            });
        }

        AppendDoctorAttention(rows, health);

        // Only a reachable gateway has a live roster to compare with: with no /health these would freeze beside "the gateway is not answering".
        if (health is not null && installDetected)
        {
            AppendConnectorAttention(rows, health);
        }
    }

    private void AppendDoctorAttention(List<AttentionRow> rows, GatewayHealth? health)
    {
        if (_doctorSnapshot is not { } doctor || (doctor.Checks.Count == 0 && doctor.Passed + doctor.Failed + doctor.Warned + doctor.Skipped == 0))
        {
            return;
        }

        var contradicted = doctor.Checks.Where(c => DoctorReconciliation.LiveHealthContradicts(c, health)).ToList();
        var staleFailures = contradicted.Count(static c => c.Status == "fail");
        var effectiveFailed = Math.Max(doctor.Failed - staleFailures, 0);

        if (effectiveFailed > 0)
        {
            rows.Add(new AttentionRow
            {
                Title = $"Doctor found {effectiveFailed.ToString(CultureInfo.CurrentCulture)} failure{(effectiveFailed == 1 ? string.Empty : "s")}",
                Detail = "See the Doctor card, or run it again in a terminal to re-probe.",
                SeverityKey = "High",
                Command = "defenseclaw doctor",
            });
        }
        else if (contradicted.Count > 0)
        {
            rows.Add(new AttentionRow
            {
                Title = $"Doctor cache shows {contradicted.Count.ToString(CultureInfo.CurrentCulture)} stale failure{(contradicted.Count == 1 ? string.Empty : "s")}",
                Detail = "The live /health disagrees with them: those subsystems are running now. Run doctor to refresh the cache.",
                SeverityKey = "Info",
            });
        }
        else if (doctor.IsStale(DateTimeOffset.UtcNow, DoctorStaleAfter))
        {
            rows.Add(new AttentionRow
            {
                Title = "Doctor cache is stale",
                Detail = "The last doctor run is older than 15 minutes. Run doctor to re-probe.",
                SeverityKey = "Info",
            });
        }

        // One row per key, so each is a thing to fix; the same copy-only filler fixes all of them.
        foreach (var name in DoctorReconciliation.MissingRequiredCredentials(doctor))
        {
            rows.Add(new AttentionRow
            {
                Title = $"credential {name}",
                Detail = $"The required key {name} is not set. Doctor found it missing.",
                SeverityKey = "High",
                Command = FillMissingKeysCommand,
            });
        }
    }

    private void AppendConnectorAttention(List<AttentionRow> rows, GatewayHealth health)
    {
        // Drift compares claw.mode with the connector the gateway is actually routing for (the primary one).
        var live = health.Connector?.Name?.Trim() ?? string.Empty;
        var configured = Services.Config.Config.Claw.Mode?.Trim() ?? string.Empty;
        if (live.Length > 0 && configured.Length > 0 && !string.Equals(live, configured, StringComparison.OrdinalIgnoreCase))
        {
            rows.Add(new AttentionRow
            {
                Title = "Connector drift",
                Detail = $"claw.mode is {FriendlyConnectorName(configured)}, but the gateway is routing for {FriendlyConnectorName(live)}. " +
                         "Restart the gateway after editing claw.mode.",
                SeverityKey = "High",
            });
        }

        if (health.Uptime <= ZeroRequestsAfter)
        {
            return;
        }

        var connectors = health.Connectors.Count > 0
            ? health.Connectors
            : health.Connector is { } primary ? new[] { primary } : Array.Empty<ConnectorStatus>();
        foreach (var connector in connectors)
        {
            var name = connector.Name?.Trim() ?? string.Empty;
            var running = string.IsNullOrWhiteSpace(connector.State) || string.Equals(connector.State, "running", StringComparison.OrdinalIgnoreCase);
            if (name.Length == 0 || connector.Requests != 0 || !running)
            {
                continue;
            }

            rows.Add(new AttentionRow
            {
                Title = $"{FriendlyConnectorName(name)}: no requests yet",
                Detail = ZeroRequestsNotice(name, health.Uptime),
                SeverityKey = "Info",
            });
        }
    }

    /// <summary>The Mac's <c>zeroRequestsNotice</c> (the TUI's <c>zero_connector_requests_notice</c>): what zero traffic means for this kind of connector.</summary>
    internal static string ZeroRequestsNotice(string connector, TimeSpan uptime)
    {
        var name = FriendlyConnectorName(connector);
        var seconds = (long)uptime.TotalSeconds;
        var hours = seconds / 3600;
        var minutes = seconds % 3600 / 60;
        var formatted = hours > 0
            ? $"{hours}h {minutes}m"
            : minutes > 0 ? $"{minutes}m" : $"{seconds}s";

        return connector.Trim().ToLowerInvariant() switch
        {
            "codex" => $"{name} has seen 0 hook events after {formatted}. Normal until Codex emits a hook or notify event; verify the ~/.codex hooks if this persists.",
            "claudecode" => $"{name} has seen 0 hook events after {formatted}. Normal until Claude Code emits a hook event; verify the Claude Code hooks if this persists.",
            "omnigent" => $"{name} has seen 0 policy events after {formatted}. Normal until OmniGent emits a supported policy callback; verify the OmniGent policy setup if this persists.",
            "hermes" or "cursor" or "devin" or "geminicli" or "copilot" or "openhands" or "antigravity" or "opencode" or "amp" =>
                $"{name} has seen 0 hook events after {formatted}. Verify the connector's hook setup if this persists.",
            _ => $"{name} has seen 0 requests after {formatted}. Verify your agent is dialing the gateway port (gateway.port).",
        };
    }

    /// <summary>The Mac's <c>friendlyConnectorName</c>: the wire name of a connector as a person writes it.</summary>
    internal static string FriendlyConnectorName(string connector)
    {
        var trimmed = connector.Trim();
        return trimmed.ToLowerInvariant() switch
        {
            "openclaw" => "OpenClaw",
            "zeptoclaw" => "ZeptoClaw",
            "claudecode" => "Claude Code",
            "codex" => "Codex",
            "hermes" => "Hermes",
            "cursor" => "Cursor",
            "devin" => "Devin",
            "geminicli" => "Gemini CLI (deprecated; use Antigravity)",
            "copilot" => "GitHub Copilot CLI",
            "openhands" => "OpenHands",
            "antigravity" => "Antigravity",
            "opencode" => "OpenCode",
            "amp" => "Amp",
            "omnigent" => "OmniGent",
            { Length: > 0 } => char.ToUpperInvariant(trimmed[0]) + trimmed[1..],
            _ => "No connector",
        };
    }

    /// <summary>
    /// Remembers the agents the discovery file maps to a hook connector and re-derives the rows. Called from <see cref="SetDetectedConnectors"/>,
    /// so the attention row and the Connectors table's "not configured" rows come from the same read.
    /// </summary>
    private void SetDetectedForAttention(IReadOnlyList<string> discovered)
    {
        _detectedDiscovered = discovered;
        BuildAttention(_snapshot);
    }

    /// <summary>Every connector config.yaml names or the gateway reports (live roster, primary): discovery never offers one of these.</summary>
    private HashSet<string> ManagedConnectorNames()
    {
        var managed = new HashSet<string>(StringComparer.Ordinal);
        var config = Services.Config.Config;
        foreach (var name in config.Guardrail.Connectors.Keys)
        {
            managed.Add(OverviewDetectedConnectors.Normalize(name));
        }

        if (!string.IsNullOrWhiteSpace(config.Guardrail.Connector))
        {
            managed.Add(OverviewDetectedConnectors.Normalize(config.Guardrail.Connector));
        }

        foreach (var name in _snapshot.ActiveConnectors)
        {
            managed.Add(OverviewDetectedConnectors.Normalize(name));
        }

        if (_snapshot.Health is { } health)
        {
            foreach (var connector in health.Connectors)
            {
                managed.Add(OverviewDetectedConnectors.Normalize(connector.Name ?? string.Empty));
            }

            if (health.Connector?.Name is { Length: > 0 } primary)
            {
                managed.Add(OverviewDetectedConnectors.Normalize(primary));
            }
        }

        return managed;
    }
}
