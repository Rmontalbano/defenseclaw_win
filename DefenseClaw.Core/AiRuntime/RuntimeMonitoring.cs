using System.Text.Json;
using DefenseClaw.Core.Config;
using DefenseClaw.Core.Gateway.Models;

namespace DefenseClaw.Core.AiRuntime;

/// <summary>Whether the gateway runs with an administrator token. Read from the administrator-mode helper; <see cref="Unknown"/> until that exists.</summary>
public enum GatewayElevation
{
    /// <summary>Nothing says. Today every reading, since this app cannot start an elevated gateway yet.</summary>
    Unknown,

    /// <summary>The gateway runs with the user's ordinary (filtered) token.</summary>
    NotElevated,

    /// <summary>The gateway runs with an elevated administrator token.</summary>
    Elevated,
}

/// <summary>Where one thing a plane needs stands. "Cannot check" is its own state and is never drawn as a pass or a fault.</summary>
public enum RuntimeRequirementState
{
    /// <summary>In place.</summary>
    Met,

    /// <summary>Not in place; the text says what to change.</summary>
    NotMet,

    /// <summary>This app cannot tell without administrator rights (or the gateway has not said). Nothing is guessed.</summary>
    CannotCheck,

    /// <summary>Something the app offers no way to do yet (run the gateway as administrator); drawn greyed.</summary>
    Unavailable,

    /// <summary>A fact to know, not a pass or a fault (a gap that is always there).</summary>
    Note,
}

/// <summary>One line of a plane's requirements.</summary>
public sealed record RuntimeRequirement(string Text, RuntimeRequirementState State);

/// <summary>What the gateway says about one plane, from <c>/health</c> <c>ai_runtime.details.planes</c>.</summary>
/// <param name="Available">The platform can observe it.</param>
/// <param name="Running">It is observing now.</param>
/// <param name="Mechanism">How, when it is running.</param>
/// <param name="Reason">Why not, or the limit it states.</param>
public sealed record RuntimeHealthPlane(bool Available, bool Running, string Mechanism, string Reason);

/// <summary>The <c>ai_runtime</c> block of <c>/health</c>, in the parts this card uses.</summary>
/// <param name="Reported">The block was in the reply at all (0.8.10 has none; a gateway that did not answer has no reply).</param>
/// <param name="State"><c>disabled</c>, <c>starting</c>, <c>running</c>, <c>error</c>, <c>stopped</c>; empty when not reported.</param>
/// <param name="LastError">The gateway's own error text, when it has one.</param>
/// <param name="Degraded">The gateway's <c>details.degraded</c>.</param>
/// <param name="Planes">The plane letter (<c>a</c>, <c>b</c>, <c>c</c>) to its entry; a plane the gateway did not list is absent.</param>
/// <param name="DegradedReasons">The gateway's <c>details.degraded_reasons</c> (sent only while it is degraded): each a trimmed string of at most 300 characters, at most 8; null when it sent none.</param>
public sealed record RuntimeHealthView(
    bool Reported,
    string State,
    string LastError,
    bool Degraded,
    IReadOnlyDictionary<string, RuntimeHealthPlane> Planes,
    IReadOnlyList<string>? DegradedReasons = null)
{
    /// <summary>The reasons the gateway gives for being degraded; empty when it gives none.</summary>
    public IReadOnlyList<string> Reasons => DegradedReasons ?? Array.Empty<string>();

    private const int MaxReasons = 8;
    private const int MaxReasonLength = 300;

    /// <summary>No reply, or no block.</summary>
    public static RuntimeHealthView NotReported { get; } = new(false, string.Empty, string.Empty, false, new Dictionary<string, RuntimeHealthPlane>());

    public bool IsDisabled => State.Equals("disabled", StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads the block; <see cref="NotReported"/> when the gateway sent none. Display text is trimmed, never interpreted.</summary>
    public static RuntimeHealthView From(GatewayHealth? health)
    {
        var block = health?.AiRuntime;
        if (block is null)
        {
            return NotReported;
        }

        var planes = new Dictionary<string, RuntimeHealthPlane>(StringComparer.OrdinalIgnoreCase);
        if (block.Details is { ValueKind: JsonValueKind.Object } details &&
            details.TryGetProperty("planes", out var map) && map.ValueKind == JsonValueKind.Object)
        {
            foreach (var plane in map.EnumerateObject())
            {
                if (plane.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                planes[plane.Name.Trim()] = new RuntimeHealthPlane(
                    Bool(plane.Value, "available"),
                    Bool(plane.Value, "running"),
                    Text(plane.Value, "mechanism"),
                    Text(plane.Value, "reason"));
            }
        }

        return new RuntimeHealthView(
            true,
            block.State?.Trim() ?? string.Empty,
            block.LastError?.Trim() ?? string.Empty,
            block.DetailBool("degraded") == true,
            planes,
            ReadReasons(block.Details));
    }

    private static List<string>? ReadReasons(JsonElement? details)
    {
        if (details is not { ValueKind: JsonValueKind.Object } d ||
            !d.TryGetProperty("degraded_reasons", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var reasons = new List<string>();
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || (item.GetString()?.Trim() ?? string.Empty) is not { Length: > 0 } text)
            {
                continue;
            }

            text = DefenseClaw.Core.Text.DisplayNames.Visible(text);
            reasons.Add(text.Length > MaxReasonLength ? text[..MaxReasonLength] : text);
            if (reasons.Count == MaxReasons)
            {
                break;
            }
        }

        return reasons;
    }

    private static bool Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() ?? string.Empty : string.Empty;
}

/// <summary>
/// <c>ai_discovery.runtime</c> as config.yaml says it (config schema 9; schema 8 files without the block read as "off"). Read from the file's text,
/// not the typed model, so "the file does not say" stays distinct from "false". The rules are the gateway's (<c>EffectivePlanes</c> in
/// <c>internal/config/config.go</c>): an empty <c>planes</c> list means A and B, plus C when <c>enable_host_plane</c> is true; a list that names C
/// still drops it without <c>enable_host_plane</c>.
/// </summary>
public sealed record RuntimeMonitoringConfig(
    bool Enabled,
    IReadOnlyList<string> Planes,
    bool EnableHostPlane,
    bool DnsCapture,
    string Acquisition,
    int? ConfigVersion)
{
    public static RuntimeMonitoringConfig Off { get; } = new(false, Array.Empty<string>(), false, false, string.Empty, null);

    public static RuntimeMonitoringConfig From(ConfigDocument? document) => From(ConfigYamlReader.From(document));

    public static RuntimeMonitoringConfig From(ConfigYamlReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return new RuntimeMonitoringConfig(
            reader.Bool("ai_discovery", "runtime", "enabled") ?? false,
            (reader.List("ai_discovery", "runtime", "planes") ?? Array.Empty<string>())
                .Select(static p => p.Trim().ToLowerInvariant()).Where(static p => p.Length > 0).ToArray(),
            reader.Bool("ai_discovery", "runtime", "enable_host_plane") ?? false,
            reader.Bool("ai_discovery", "runtime", "dns_capture") ?? false,
            reader.Text("ai_discovery", "runtime", "acquisition") ?? string.Empty,
            reader.Integer("config_version") is { } v and >= 0 and <= int.MaxValue ? (int)v : null);
    }

    /// <summary>The planes the gateway would run if monitoring is on (its <c>EffectivePlanes</c>).</summary>
    public IReadOnlyList<string> EffectivePlanes
    {
        get
        {
            if (Planes.Count == 0)
            {
                return EnableHostPlane ? new[] { "a", "b", "c" } : new[] { "a", "b" };
            }

            return new[] { "a", "b", "c" }
                .Where(id => Planes.Contains(id, StringComparer.Ordinal) && (id != "c" || EnableHostPlane))
                .ToArray();
        }
    }

    public bool PlaneSelected(string id) => EffectivePlanes.Contains(id, StringComparer.Ordinal);

    /// <summary>Plane C is listed in <c>planes</c> with <c>enable_host_plane</c> false: asked for in two places and granted in neither.</summary>
    public bool HostPlaneListedWithoutOptIn => !EnableHostPlane && Planes.Contains("c", StringComparer.Ordinal);
}

/// <summary>One plane's row on the card.</summary>
public sealed record RuntimeMonitoringPlane(
    string Id,
    string Name,
    string Records,
    string Privacy,
    bool SelectedInConfig,
    string ConfigText,
    RuntimePlaneRunState Run,
    string RunText,
    IReadOnlyList<RuntimeRequirement> Requirements)
{
    public string DisplayName => $"{Id.ToUpperInvariant()} · {Name}";
}

/// <summary>Whether the gateway is running one plane, as <c>/health</c> says.</summary>
public enum RuntimePlaneRunState
{
    /// <summary>No answer: the gateway is not reachable, or does not report the block (0.8.10).</summary>
    Unknown,

    Running,

    /// <summary>The gateway answered and the plane is not running (off, blind, or waiting).</summary>
    NotRunning,
}

/// <summary>The whole card: monitoring on or off in config, what the gateway runs, and three plane rows.</summary>
public sealed record RuntimeMonitoringState(
    RuntimeMonitoringConfig Config,
    RuntimeHealthView Health,
    string Headline,
    string ApplyNote,
    IReadOnlyList<RuntimeMonitoringPlane> Planes)
{
    public const string RecordsA =
        "Process names, CPU use per poll and resident memory, to spot sustained inference. Also reads process command lines, which the inventory scan does not. " +
        "Uses Toolhelp32, GetProcessTimes and GetProcessMemoryInfo.";

    public const string RecordsB =
        "Which process holds which TCP or UDP connection, matched to known AI providers. " +
        "With DNS capture on, it also reads the DNS Client operational log, event 3008, to name the peer. Otherwise peers are named by reverse DNS.";

    public const string RecordsC =
        "Process creation, privileged logons and account events from the Windows Security event log. Signals are raised only when an AI agent is in the process lineage. " +
        "File read and write events need an audit rule on each file and are not covered.";

    public const string PrivacyAB =
        "Findings go to the local audit store and the observability destinations you configured. Command lines are classified as content and pass through each " +
        "destination's redaction profile. Environment variable values are never read.";

    public const string PrivacyC =
        "Reads other programs' process creation records, including command lines when Windows includes them. " +
        "The gateway reads the Security log on this machine and handles it like the other planes' data. Environment variable values are never read.";

    public const string PrivacyDns =
        "DNS capture records the domains this machine resolved, locally, to name egress peers.";

    /// <summary>The compact state for the Overview: <c>on · A running · B running · C off</c>.</summary>
    public string Summary
    {
        get
        {
            var parts = Planes.Select(p => $"{p.Id.ToUpperInvariant()} {SummaryWord(p)}");
            return (Config.Enabled ? "on" : "off") + " · " + string.Join(" · ", parts);
        }
    }

    private static string SummaryWord(RuntimeMonitoringPlane plane) => plane.Run switch
    {
        RuntimePlaneRunState.Running => "running",
        RuntimePlaneRunState.NotRunning => plane.SelectedInConfig ? "not running" : "off",
        _ => plane.SelectedInConfig ? "selected" : "off",
    };

    /// <summary>Builds the card from what config.yaml, the gateway and (when it exists) the administrator-mode helper say.</summary>
    /// <param name="config">config.yaml's <c>ai_discovery.runtime</c>.</param>
    /// <param name="health"><c>/health</c>'s <c>ai_runtime</c>; <see cref="RuntimeHealthView.NotReported"/> when the gateway did not answer or is 0.8.10.</param>
    /// <param name="gatewayAnswered">The gateway answered the last poll (so a missing block means "not there" and not "unreachable").</param>
    /// <param name="elevation">Whether the gateway runs elevated.</param>
    /// <param name="commandLineAudit">The registry reading of <c>ProcessCreationIncludeCmdLine_Enabled</c> (readable without elevation); null reading when it could not be read.</param>
    public static RuntimeMonitoringState Build(
        RuntimeMonitoringConfig config,
        RuntimeHealthView health,
        bool gatewayAnswered,
        GatewayElevation elevation,
        ProbeReading<int?>? commandLineAudit)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(health);

        var rows = new[]
        {
            Row("a", "inference heartbeat", RecordsA, PrivacyAB, config, health, gatewayAnswered, elevation, commandLineAudit),
            Row("b", "shadow egress", RecordsB, PrivacyAB + " " + PrivacyDns, config, health, gatewayAnswered, elevation, commandLineAudit),
            Row("c", "agent actions", RecordsC, PrivacyC, config, health, gatewayAnswered, elevation, commandLineAudit),
        };

        var headline = config.Enabled
            ? "Runtime monitoring is on in config.yaml."
            : "Runtime monitoring is off in config.yaml. Nothing watches processes or connections for AI use.";

        return new RuntimeMonitoringState(config, health, headline, ApplyNoteFor(config, health, gatewayAnswered), rows);
    }

    /// <summary>The sentence about config and gateway disagreeing (a saved change the running gateway has not picked up), or empty.</summary>
    private static string ApplyNoteFor(RuntimeMonitoringConfig config, RuntimeHealthView health, bool gatewayAnswered)
    {
        if (!gatewayAnswered)
        {
            return "The gateway did not answer. The settings above are from config.yaml.";
        }

        if (!health.Reported)
        {
            return "This gateway does not report runtime monitoring.";
        }

        if (config.Enabled && health.IsDisabled)
        {
            return "config.yaml turns monitoring on but the gateway reports it disabled. Restart the gateway to apply it.";
        }

        if (!config.Enabled && !health.IsDisabled && health.State.Length > 0)
        {
            return "config.yaml turns monitoring off but the gateway still reports it " + health.State + ". Restart the gateway to stop it.";
        }

        if (health.State.Equals("error", StringComparison.OrdinalIgnoreCase))
        {
            return "The gateway reports a runtime monitoring error" + (health.LastError.Length > 0 ? ": " + health.LastError : ".");
        }

        return string.Empty;
    }

    private static RuntimeMonitoringPlane Row(
        string id,
        string name,
        string records,
        string privacy,
        RuntimeMonitoringConfig config,
        RuntimeHealthView health,
        bool gatewayAnswered,
        GatewayElevation elevation,
        ProbeReading<int?>? commandLineAudit)
    {
        var selected = config.Enabled && config.PlaneSelected(id);
        string configText;
        if (!config.Enabled)
        {
            configText = "Off: runtime monitoring is off.";
        }
        else if (id == "c" && !config.PlaneSelected("c"))
        {
            configText = config.HostPlaneListedWithoutOptIn
                ? "Off: C is listed in planes but enable_host_plane is false. The opt-in is the second gate."
                : "Off: Plane C is an explicit opt-in (enable_host_plane) and has not been enabled.";
        }
        else
        {
            configText = selected ? "On in config.yaml." : "Off: not selected in ai_discovery.runtime.planes.";
        }

        RuntimePlaneRunState run;
        string runText;
        if (!gatewayAnswered)
        {
            run = RuntimePlaneRunState.Unknown;
            runText = "Not known: the gateway did not answer.";
        }
        else if (!health.Reported)
        {
            run = RuntimePlaneRunState.Unknown;
            runText = "Not reported by this gateway.";
        }
        else if (health.Planes.TryGetValue(id, out var plane))
        {
            run = plane.Running ? RuntimePlaneRunState.Running : RuntimePlaneRunState.NotRunning;
            runText = plane.Running
                ? "Running" + (plane.Mechanism.Length > 0 ? " via " + plane.Mechanism : string.Empty) + (plane.Reason.Length > 0 ? ". Stated limit: " + plane.Reason : ".")
                : "Not running" + (plane.Reason.Length > 0 ? ": " + plane.Reason : plane.Available ? ": available but not started." : ".");
        }
        else
        {
            run = RuntimePlaneRunState.NotRunning;
            runText = health.IsDisabled
                ? "Not running: the gateway reports runtime monitoring disabled."
                : $"Not running: the gateway lists no entry for plane {id.ToUpperInvariant()} yet" + (health.State.Equals("starting", StringComparison.OrdinalIgnoreCase) ? " (it is starting)." : ".");
        }

        return new RuntimeMonitoringPlane(id, name, records, privacy, selected, configText, run, runText, Requirements(id, config, health, elevation, commandLineAudit));
    }

    /// <summary>What each plane needs on Windows, per the 1.0 sensor source. Nothing here is probed that needs administrator rights.</summary>
    internal static IReadOnlyList<RuntimeRequirement> Requirements(
        string id,
        RuntimeMonitoringConfig config,
        RuntimeHealthView health,
        GatewayElevation elevation,
        ProbeReading<int?>? commandLineAudit)
    {
        var rows = new List<RuntimeRequirement>();
        switch (id)
        {
            case "a":
                rows.Add(new("Works without administrator rights for your own processes.", RuntimeRequirementState.Met));
                rows.Add(new(
                    "Gap without an elevated gateway: other users' processes and elevated processes are only partly visible. They count as reduced coverage.",
                    RuntimeRequirementState.Note));
                break;

            case "b":
                rows.Add(new("Works without administrator rights. Windows gives the owning process id for every socket.", RuntimeRequirementState.Met));
                rows.Add(new(
                    "Gap without an elevated gateway: image path and owner can be refused for other users' or elevated processes, so some connections are attributed by address only.",
                    RuntimeRequirementState.Note));
                rows.Add(DnsRequirement(config, health));
                break;

            default:
                var optedIn = config.PlaneSelected("c");
                rows.Add(new(
                    optedIn
                        ? "Opted in: planes lists c and enable_host_plane is true."
                        : "Not opted in. Needs c in planes and enable_host_plane true. Turning Plane C on here sets both.",
                    optedIn ? RuntimeRequirementState.Met : RuntimeRequirementState.NotMet));
                rows.Add(elevation switch
                {
                    GatewayElevation.Elevated => new("The gateway runs as administrator.", RuntimeRequirementState.Met),
                    GatewayElevation.NotElevated => new("Needs Run gateway as administrator: the gateway must hold an elevated token to read the Security log.", RuntimeRequirementState.NotMet),
                    _ => new("Needs Run gateway as administrator. This app cannot start an elevated gateway yet, so this is not available here.", RuntimeRequirementState.Unavailable),
                });
                rows.Add(new(
                    "Advanced Audit Policy, Detailed Tracking, Audit Process Creation (success): can't check without admin. Reading it needs an elevated prompt with auditpol.",
                    RuntimeRequirementState.CannotCheck));
                rows.Add(CommandLineRequirement(commandLineAudit));
                rows.Add(new(
                    "Not covered by this plane: file read and write events need an audit rule on each file, which turning the plane on does not create.",
                    RuntimeRequirementState.Note));
                break;
        }

        return rows;
    }

    private static RuntimeRequirement DnsRequirement(RuntimeMonitoringConfig config, RuntimeHealthView health)
    {
        if (!config.DnsCapture)
        {
            return new("DNS capture is off: peers are named by reverse DNS, which is less direct.", RuntimeRequirementState.Note);
        }

        var mechanism = health.Planes.TryGetValue("b", out var b) ? b.Mechanism : string.Empty;
        if (mechanism.Contains("dns capture unavailable", StringComparison.OrdinalIgnoreCase))
        {
            return new(
                "DNS capture is on but unavailable. Enable the DNS Client log in an elevated prompt with wevtutil sl Microsoft-Windows-DNS-Client/Operational /e:true",
                RuntimeRequirementState.NotMet);
        }

        if (mechanism.Contains("DNS capture (", StringComparison.Ordinal))
        {
            return new("DNS capture is on and the gateway is reading the DNS Client operational log.", RuntimeRequirementState.Met);
        }

        return new(
            "DNS capture is on. It needs the DNS Client operational log, which is off by default. The gateway reports its state once plane B runs.",
            RuntimeRequirementState.CannotCheck);
    }

    private static RuntimeRequirement CommandLineRequirement(ProbeReading<int?>? reading)
    {
        const string Subject = "Command lines in process creation events (Include command line in process creation events): ";
        if (reading is null || !reading.Read)
        {
            return new(Subject + "could not be read" + (reading is { Problem.Length: > 0 } ? " (" + reading.Problem + ")" : string.Empty) + ". Lineage still works. Argument-based detections do not.", RuntimeRequirementState.CannotCheck);
        }

        return reading.Value is { } value and not 0
            ? new(Subject + "on.", RuntimeRequirementState.Met)
            : new(Subject + "off or not set. Lineage still works. Argument-based detections do not.", RuntimeRequirementState.NotMet);
    }
}

/// <summary>
/// What the Runtime monitoring card is asked to change, and the one command each choice becomes. Every argv is
/// <see cref="AiRuntimeCommands"/> (<c>agent discovery runtime enable | disable</c>), which the 1.0 CLI implements as: edit <c>ai_discovery.runtime</c> in
/// config.yaml, then restart the gateway unless <c>--no-restart</c>.
/// <para>
/// The CLI has no switch for Plane A or B alone. <c>enable</c> turns on A and B together (<c>planes</c> becomes <c>[a, b]</c>), and with
/// <c>--enable-host-plane</c> A, B and C (<c>planes</c> <c>[a, b, c]</c>, <c>enable_host_plane</c> true). <c>--no-enable-host-plane</c> drops C again. <c>disable</c> turns the whole
/// runtime off (<c>enabled</c> false) and keeps recorded findings. So the card offers: monitoring on or off, Plane C on or off, DNS capture on or off.
/// </para>
/// </summary>
public static class RuntimeMonitoringPlan
{
    /// <summary>What the operator wants the three switches to be.</summary>
    public sealed record Wanted(bool Enabled, bool HostPlane, bool DnsCapture);

    /// <summary>What one change is, in the words the review and the dialog's list use.</summary>
    public sealed record Change(string Description);

    /// <summary>The wanted state that equals what config.yaml says now (so the diff is empty).</summary>
    public static Wanted Current(RuntimeMonitoringConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return new Wanted(config.Enabled, config.PlaneSelected("c"), config.DnsCapture);
    }

    /// <summary>The changes between what config.yaml says and what is wanted. Plane C and DNS capture only count while monitoring is (to be) on.</summary>
    public static IReadOnlyList<Change> Diff(RuntimeMonitoringConfig config, Wanted wanted)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(wanted);

        var changes = new List<Change>();
        if (wanted.Enabled != config.Enabled)
        {
            changes.Add(new(wanted.Enabled
                ? "Runtime monitoring: off → on (planes A and B)."
                : "Runtime monitoring: on → off. Every plane stops and recorded findings are kept."));
        }

        if (wanted.Enabled)
        {
            var currentC = config.PlaneSelected("c");
            if (wanted.HostPlane != currentC)
            {
                changes.Add(new(wanted.HostPlane
                    ? "Plane C (agent actions): off → on. Needs an elevated gateway and the audit policy. Until then it reports no coverage."
                    : "Plane C (agent actions): on → off."));
            }

            if (wanted.DnsCapture != config.DnsCapture)
            {
                changes.Add(new(wanted.DnsCapture
                    ? "DNS capture: off → on (peers named from the DNS Client log)."
                    : "DNS capture: on → off (peers named by reverse DNS)."));
            }
        }

        return changes;
    }

    /// <summary>
    /// The command for the change, or null when there is none. <b>Off</b>: <c>disable --yes</c>. <b>On</b>: <c>enable --yes</c> with the Plane C flag spelled out
    /// every time (so the review shows what C becomes, and the CLI's own "keep a prior opt-in" rule never decides it) and the DNS flag only when it moved.
    /// <c>--no-restart</c> when the gateway is not to be restarted.
    /// </summary>
    public static IReadOnlyList<string>? Argv(RuntimeMonitoringConfig config, Wanted wanted, bool restart)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(wanted);

        if (Diff(config, wanted).Count == 0)
        {
            return null;
        }

        if (!wanted.Enabled)
        {
            return AiRuntimeCommands.Disable(restart);
        }

        return AiRuntimeCommands.Enable(new AiRuntimeEnableOptions(
            HostPlane: wanted.HostPlane,
            DnsCapture: wanted.DnsCapture != config.DnsCapture ? wanted.DnsCapture : null,
            Restart: restart));
    }
}
