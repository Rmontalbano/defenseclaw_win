using System.Globalization;
using System.IO;
using DefenseClaw.App.Services;
using DefenseClaw.Core.Audit;
using DefenseClaw.Core.Gateway.Models;
using DefenseClaw.Core.Text;
using Microsoft.Data.Sqlite;

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

    // ---- Silent bypass (CUST-218) ----

    private NetworkEgressReader? _egressReader;
    private int _silentBypass;
    private int _bypassReading;

    /// <summary>The egress reader behind the silent-bypass row (a test aims it at its own database).</summary>
    internal NetworkEgressReader EgressReader
    {
        get => _egressReader ??= new NetworkEgressReader(Services.Paths.AuditDatabasePath);
        set => _egressReader = value;
    }

    /// <summary>Allowed, LLM-shaped egress events in the last 5 minutes, as of the last read (the Mac's <c>silentBypassCount</c>).</summary>
    internal int SilentBypassCount => _silentBypass;

    /// <summary>
    /// Re-counts the silent-bypass events and, when the number changed, rebuilds "What needs attention". Runs with the metrics read
    /// (<see cref="RefreshMetricsAsync"/>, so on its 15 s cadence and while the panel is active), one at a time, and never throws: a
    /// database that cannot answer keeps the last number rather than inventing a quiet one.
    /// </summary>
    internal async Task RefreshSilentBypassAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _bypassReading, 1) == 1)
        {
            return;
        }

        try
        {
            var count = await EgressReader.CountSilentBypassAsync(DateTimeOffset.UtcNow, timeout: Services.ReaderTimeouts.HookTotals, cancellationToken: cancellationToken).ConfigureAwait(true);
            if (count != _silentBypass)
            {
                _silentBypass = count;
                BuildAttention(_snapshot);
            }
        }
        catch (OperationCanceledException)
        {
            // The panel went away mid-read; the next activation reads again.
        }
#pragma warning disable CA1031 // A locked or older database leaves the row as it was; it must not fault the panel.
        catch (Exception ex) when (ex is SqliteException or IOException or TimeoutException or InvalidOperationException)
#pragma warning restore CA1031
        {
            System.Diagnostics.Trace.TraceWarning($"overview: silent bypass could not be counted: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _ = Interlocked.Exchange(ref _bypassReading, 0);
        }
    }

    /// <summary>What the copy-only hint on a missing-key row says (the CLI's own filler for the keys doctor found missing; never run by the app).</summary>
    internal const string FillMissingKeysCommand = "defenseclaw keys fill-missing";

    /// <summary>What the last rebuild of the list drew for the scanner: so a lookup that lands later can tell whether the list needs another.</summary>
    private bool _scannerNoticeBuilt;

    /// <summary>
    /// True when the skill scanner is known to be missing <i>and</i> the DefenseClaw CLI is installed: the same lookup the Scanners card shows (never
    /// "missing" while the first lookup is still out), and not on a machine that has no DefenseClaw at all, where "DefenseClaw was not found" is the
    /// row and a scanner hint would only be noise. (The TUI runs from the installed CLI, so it has no such case.)
    /// </summary>
    private bool SkillScannerNoticeWanted(GatewaySnapshot snapshot) =>
        _scannerPathsResolved && _skillScannerPath is null && !string.IsNullOrEmpty(snapshot.CliPath);

    /// <summary>The scanner lookup answered (or changed its answer): the list is rebuilt when that changes whether its row is there.</summary>
    private void RefreshScannerNotice()
    {
        if (SkillScannerNoticeWanted(_snapshot) != _scannerNoticeBuilt)
        {
            BuildAttention(_snapshot);
        }
    }

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

    /// <summary>
    /// What the "skill-scanner not on PATH" row offers to copy. The TUI prints <c>pip install skill-scanner</c>, which names the executable; the
    /// distribution that provides it is <c>cisco-ai-skill-scanner</c> (the CLI's own error says so: <c>scanner/skill.py</c>, and 0.8.10 ships
    /// <c>cisco_ai_skill_scanner</c>), so that is what an operator should be handed - not the name of a different package. Copy only: the app never runs it.
    /// </summary>
    internal const string InstallSkillScannerCommand = "pip install cisco-ai-skill-scanner";

    /// <summary>
    /// The states of a subsystem of <c>/health</c> that the TUI reads as "up" (<c>_gateway_state_from_snapshot</c>): an empty one (no block, or no
    /// state) counts, so a gateway that does not report a subsystem is not "starting" for that.
    /// </summary>
    private static readonly string[] UpWords = { string.Empty, "running", "ready", "healthy", "ok" };

    private static readonly string[] DownWords = { "stopped", "offline", "down" };

    /// <summary>
    /// True when the gateway answered <c>/health</c> but is not up yet: the TUI's <c>starting</c> verdict (<c>_gateway_state_from_snapshot</c>, shown
    /// as <c>Gateway is starting - health checks will retry automatically</c>). The API block is up when it says running, ready, healthy, ok or
    /// nothing; the <c>gateway</c> block (the fleet uplink) is up on those or <c>disabled</c>, the normal standalone state. A gateway that is down
    /// (the API stopped, offline or down, or the gateway block) or failed (the API in error or failed) is not "starting" - those are the TUI's
    /// offline and error verdicts, and the "not answering" row speaks for the first. Anything else that is not up - <c>starting</c>,
    /// <c>reconnecting</c>, a state this does not know - is starting, as in the TUI. <paramref name="detail"/> names what is not up yet.
    /// </summary>
    internal static bool GatewayIsStarting(GatewayHealth? health, out string detail)
    {
        detail = string.Empty;
        if (health is null)
        {
            return false;
        }

        var api = (health.Api?.State ?? string.Empty).Trim().ToLowerInvariant();
        var gateway = (health.FleetUplink?.State ?? string.Empty).Trim().ToLowerInvariant();
        var apiUp = UpWords.Contains(api, StringComparer.Ordinal);
        var gatewayUp = UpWords.Contains(gateway, StringComparer.Ordinal) || gateway == "disabled";
        if (apiUp && gatewayUp)
        {
            return false;
        }

        if (DownWords.Contains(api, StringComparer.Ordinal) || api is "error" or "failed" || DownWords.Contains(gateway, StringComparer.Ordinal))
        {
            return false;
        }

        detail = apiUp
            ? $"The gateway subsystem of /health reports {SafeStateWord(gateway)}."
            : $"The API subsystem of /health reports {SafeStateWord(api)}.";
        return true;
    }

    /// <summary>A state word the gateway wrote, made fit to draw: control and bidirectional characters written out, and cut short.</summary>
    private static string SafeStateWord(string word) => DisplayNames.Visible(word.Length > 40 ? word[..40] : word);

    /// <summary>Adds the Mac's remaining rules, in its emission order, to <paramref name="rows"/>.</summary>
    private void AppendParityAttention(List<AttentionRow> rows, GatewaySnapshot snapshot)
    {
        var health = snapshot.Health;
        var installDetected = InstallDetected(snapshot);

        // The TUI's gateway notice is one of: starting (it answered but is not up yet), or a standalone gateway explaining itself.
        if (GatewayIsStarting(health, out var starting))
        {
            rows.Add(new AttentionRow
            {
                Title = "The gateway is starting",
                Detail = starting + " Health checks will retry automatically; this clears once it reports running.",
                SeverityKey = "Info",
            });
        }
        else if (health?.FleetUplink is { IsDisabled: true } standalone &&
                 (standalone.DetailString("hint") is { Length: > 0 } || standalone.DetailString("summary") is { Length: > 0 }))
        {
            // A standalone gateway ("gateway" subsystem disabled) explains itself in /health; the Mac passes that hint on.
            rows.Add(new AttentionRow
            {
                Title = "The gateway runs standalone",
                Detail = standalone.DetailString("hint") is { Length: > 0 } hint ? hint : standalone.DetailString("summary")!,
                SeverityKey = "Info",
            });
        }

        // The TUI's "Connector roster degraded" (an error): the connector map in config.yaml has a name the runtime rejects, so the roster it builds
        // is not the one the file lists. The typed model folds names that differ only by case into one, so this card shows fewer than the file has.
        if (OverviewFacts().RosterProblem is { Length: > 0 } rosterProblem)
        {
            rows.Add(new AttentionRow
            {
                Title = "Connector roster degraded",
                Detail = rosterProblem + " - showing a reduced view; check your connector config",
                SeverityKey = "Critical",
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

        // The TUI's "skill-scanner not on PATH - run: pip install skill-scanner": the same lookup the Scanners card shows, with the fix as text to copy.
        _scannerNoticeBuilt = SkillScannerNoticeWanted(snapshot);
        if (_scannerNoticeBuilt)
        {
            rows.Add(new AttentionRow
            {
                Title = "skill-scanner not on PATH",
                Detail = "The skill scanner is neither on PATH nor in the installer's bin directory, so skill scans cannot run. " +
                         "Install it with the command below, then press Refresh. The app never runs it for you.",
                SeverityKey = "High",
                Command = InstallSkillScannerCommand,
            });
        }

        if (_silentBypass > 0)
        {
            rows.Add(new AttentionRow
            {
                Title = $"Silent bypass: {_silentBypass.ToString(CultureInfo.CurrentCulture)} allowed LLM-shaped egress in the last 5 min",
                Detail = "Traffic that looks like an LLM call left this machine without the guardrail's say. Open Alerts and choose the Egress kind to see where it went.",
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
