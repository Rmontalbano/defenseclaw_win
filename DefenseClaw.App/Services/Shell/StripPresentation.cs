using System.Globalization;
using DefenseClaw.Core.Config;
using DefenseClaw.Core.Observability;

namespace DefenseClaw.App.Services;

/// <summary>
/// How the status strip's chips are worded and toned, as pure functions of what the strip is given, so each rule is a line a test can hold. The
/// words are the TUI's (<c>widgets/status_strip.py</c> of the 0.8.10 package) where it has them: <c>Keys</c> names the first two missing
/// credentials and counts the rest, <c>Redaction</c> carries the aggregate label as it is, <c>policy</c> is the mode, <c>stale</c> and
/// <c>running</c> are one word. The existing gateway, alerts, connector and version chips keep <see cref="GatewayPresentation"/>'s words.
/// <para>
/// The tone keys are the design system's (<c>Ok / Warn / Bad / Low / Neutral</c>, see Themes\DefenseClaw.xaml), chosen by <c>Tag</c>, so the
/// strip follows a live light/dark switch. A tone is never the only carrier of a meaning: every chip also says it in words, in its tooltip and in
/// its automation name.
/// </para>
/// </summary>
internal static class StripPresentation
{
    /// <summary>How many credential names the Keys chip spells out before it counts the rest (the TUI's <c>missing_keys[:2]</c>).</summary>
    public const int KeyNamesShown = 2;

    /// <summary>The TUI's tone for "all is well", "needs a look", "failed", "in progress" and "nothing to say".</summary>
    public const string Ok = "Ok";

    public const string Warn = "Warn";

    public const string Bad = "Bad";

    /// <summary>"In progress": the cyan tone, which is cyan in every look (Medium is yellow under Cisco, and would read as a warning).</summary>
    public const string Busy = "Low";

    public const string Neutral = "Neutral";

    /// <summary>The aggregate label when every route sends events unredacted, worded by the one function that words it (<see cref="ObservabilityRedaction.Aggregate"/>).</summary>
    private static readonly string UnredactedLabel = ObservabilityRedaction.Aggregate(new[] { ObservabilityRedaction.NoRedaction });

    // ---- Keys ---------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>Keys: missing OPENAI_API_KEY, CISCO_AI_DEFENSE_API_KEY (+2 more)</c>: the first two names, then how many more (the TUI's
    /// <c>missing {preview}{suffix}</c>). Names only - the Credentials card holds no value to hand over.
    /// </summary>
    public static string KeysText(IReadOnlyList<string> missing)
    {
        ArgumentNullException.ThrowIfNull(missing);

        var preview = string.Join(", ", missing.Take(KeyNamesShown));
        var more = missing.Count > KeyNamesShown ? $" (+{(missing.Count - KeyNamesShown).ToString(CultureInfo.InvariantCulture)} more)" : string.Empty;
        return $"Keys: missing {preview}{more}";
    }

    /// <summary>Every missing name, and where to set them.</summary>
    public static string KeysDetail(IReadOnlyList<string> missing)
    {
        ArgumentNullException.ThrowIfNull(missing);

        var count = missing.Count.ToString(CultureInfo.InvariantCulture);
        var lead = missing.Count == 1 ? "1 required credential is not set" : $"{count} required credentials are not set";
        return $"{lead}: {string.Join(", ", missing)}. Set each in Setup, Credentials: a console window asks for the value, so it is never typed or shown in this app.";
    }

    /// <summary>The sentence a screen reader says for the Keys chip: every name, without the instructions.</summary>
    public static string KeysName(IReadOnlyList<string> missing)
    {
        ArgumentNullException.ThrowIfNull(missing);

        var count = missing.Count.ToString(CultureInfo.InvariantCulture);
        return $"Keys: {count} required {(missing.Count == 1 ? "credential is" : "credentials are")} not set: {string.Join(", ", missing)}";
    }

    // ---- Watchdog and Guardrail ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// True for the state words that mean "as it should be" (the TUI's <c>is_healthy</c> set, plus the gateway's <c>healthy</c>). A chip in one of these
    /// states is just its name; any other state is spelled out after the name, so a tone is never what tells them apart.
    /// </summary>
    public static bool IsHealthyWord(string state) =>
        state.Trim().ToLowerInvariant() is "running" or "active" or "allowed" or "clean" or "enabled" or "healthy" or "ok";

    /// <summary><c>Guardrail</c> while it runs, <c>Guardrail: disabled</c> otherwise.</summary>
    public static string SubsystemText(string name, string state) => IsHealthyWord(state) ? name : $"{name}: {state}";

    /// <summary>The sentence a screen reader says for a subsystem chip: <c>Guardrail: running</c>, or - paused - <c>Guardrail: last seen running, monitoring paused</c>.</summary>
    public static string SubsystemName(string name, string state, bool paused) =>
        paused ? $"{name}: last seen {state}, monitoring paused" : $"{name}: {state}";

    /// <summary>The tooltip of a subsystem chip: its state, what the Overview's Services card says about it, and - paused - that this is the last reading.</summary>
    public static string SubsystemDetail(string name, string state, string summary, bool paused)
    {
        var parts = new List<string> { $"{name}: {state}." };
        if (summary.Length > 0)
        {
            parts.Add(summary + ".");
        }

        parts.Add(paused
            ? "Monitoring is paused, so this is the last reading, not the current state."
            : "From the gateway's /health, the same reading as the Overview's Services card.");
        return string.Join(' ', parts);
    }

    // ---- Policy -------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The policy mode (<c>observe</c>, <c>action</c>): the gateway's own <c>guardrail.details.policy_mode</c> from <c>/health</c> when it answered,
    /// else config.yaml's mode for the connector in scope (or the primary one) - the order the Overview's Guardrail tile uses. Null when neither
    /// says, and then the strip shows no policy chip (the TUI shows none without a mode either).
    /// </summary>
    public static (string Mode, bool FromGateway)? PolicyMode(GatewaySnapshot snapshot, DefenseClawConfig config, string? scope)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(config);

        if (snapshot.Health?.Guardrail?.DetailString("policy_mode") is { Length: > 0 } live && live.Trim().Length > 0)
        {
            return (live.Trim().ToLowerInvariant(), true);
        }

        var guardrail = config.Guardrail;
        var connector = !string.IsNullOrWhiteSpace(scope) ? scope : !string.IsNullOrWhiteSpace(guardrail.Connector) ? guardrail.Connector : config.Claw.Mode;
        return !string.IsNullOrWhiteSpace(connector) &&
               guardrail.Connectors.TryGetValue(connector, out var settings) &&
               !string.IsNullOrWhiteSpace(settings.Mode)
            ? (settings.Mode.Trim().ToLowerInvariant(), false)
            : null;
    }

    /// <summary><c>Policy: observe</c>.</summary>
    public static string PolicyText(string mode) => $"Policy: {mode}";

    /// <summary>What the mode is, whether enforcement is on (when the gateway says), and where it was read.</summary>
    public static string PolicyDetail(string mode, bool fromGateway, bool? enforcing)
    {
        var parts = new List<string> { $"Policy posture: {mode}." };
        if (enforcing is { } enforced)
        {
            parts.Add(enforced ? "Enforcement is on." : "Enforcement is off: the guardrail observes only.");
        }

        parts.Add(fromGateway
            ? "Reported by the gateway (/health, guardrail policy_mode)."
            : "From config.yaml: the gateway did not report a mode, so this is what was asked for, not necessarily what is running.");
        return string.Join(' ', parts);
    }

    // ---- Redaction ----------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>Redaction: per-route · unredacted</c>: the Overview's aggregate label, as it is. Version 8 has no global redaction switch - each route carries
    /// a profile - which is why the label says <c>per-route</c> and lists what is in use.
    /// </summary>
    public static string RedactionText(string label) => $"Redaction: {label}";

    /// <summary>
    /// Warn when events leave unredacted - every route does (the label says <c>unredacted</c>), or one of several profiles in use is <c>none</c> -
    /// a privacy posture to see at all times, which the TUI's strip leaves green; Ok when every route redacts; Neutral while the plan has not
    /// been read or could not be (nothing is known, so nothing is claimed).
    /// </summary>
    public static string RedactionTone(string label) =>
        IsRedactionUnknown(label) ? Neutral
        : SendsUnredacted(label) ? Warn
        : Ok;

    private static bool IsRedactionUnknown(string label) =>
        string.Equals(label, ObservabilityRedaction.Loading, StringComparison.Ordinal) ||
        string.Equals(label, ObservabilityRedaction.Unavailable, StringComparison.Ordinal);

    private const string RoutePrefix = "per-route · ";

    /// <summary>
    /// True when some route sends events as recorded: the label is <see cref="UnredactedLabel"/>, or it lists the built-in profile <c>none</c> among
    /// others (<c>per-route · none,sensitive,strict</c>). The profiles are read back out of the label the Overview words; nothing is computed afresh.
    /// </summary>
    private static bool SendsUnredacted(string label)
    {
        if (string.Equals(label, UnredactedLabel, StringComparison.Ordinal))
        {
            return true;
        }

        return label.StartsWith(RoutePrefix, StringComparison.Ordinal) &&
               label[RoutePrefix.Length..].Split(',').Contains(ObservabilityRedaction.NoRedaction, StringComparer.Ordinal);
    }

    /// <summary>What the label means, in a sentence.</summary>
    public static string RedactionDetail(string label)
    {
        const string Source = "From the Overview's Observability card (defenseclaw observability plan).";
        if (string.Equals(label, ObservabilityRedaction.Loading, StringComparison.Ordinal))
        {
            return $"The observability plan has not been read yet, so the redaction posture is not known. {Source}";
        }

        if (string.Equals(label, ObservabilityRedaction.Unavailable, StringComparison.Ordinal))
        {
            return $"The observability plan could not be read, so the redaction posture is not known. The Overview says why. {Source}";
        }

        if (string.Equals(label, UnredactedLabel, StringComparison.Ordinal))
        {
            return $"Every route that sends events sends them unredacted (profile none). {Source}";
        }

        var profiles = label.StartsWith(RoutePrefix, StringComparison.Ordinal) ? label[RoutePrefix.Length..] : label;
        var warning = SendsUnredacted(label) ? " At least one route sends events unredacted (profile none)." : string.Empty;
        return $"Redaction is set per route, and the routes of this install use: {profiles}.{warning} {Source}";
    }

    // ---- Connector ----------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The connector chip. One connector or none keeps its words (<c>Connector: claudecode</c>, <c>No connector</c>). With several, it says which view
    /// the whole app has: <c>All connectors (3)</c>, or - narrowed with the shared filter - <c>codex (filtered)</c> (the TUI's wording).
    /// </summary>
    public static string ConnectorText(GatewaySnapshot snapshot, string? scope)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var roster = RosterOf(snapshot);
        if (roster.Count <= 1)
        {
            return GatewayPresentation.ConnectorText(snapshot);
        }

        return string.IsNullOrWhiteSpace(scope) ? $"All connectors ({roster.Count.ToString(CultureInfo.InvariantCulture)})" : $"{scope} (filtered)";
    }

    /// <summary>The connector chip's tooltip.</summary>
    public static string ConnectorDetail(GatewaySnapshot snapshot, string? scope)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var roster = RosterOf(snapshot);
        if (roster.Count <= 1)
        {
            return GatewayPresentation.ConnectorText(snapshot);
        }

        return string.IsNullOrWhiteSpace(scope)
            ? $"All {roster.Count.ToString(CultureInfo.InvariantCulture)} connectors: {string.Join(", ", roster)}. Every screen shows all of them; Ctrl+Shift+M narrows the whole app to one."
            : $"The whole app is narrowed to {scope} (of {string.Join(", ", roster)}). Ctrl+Shift+M steps to the next connector, and then back to all of them.";
    }

    /// <summary>The connectors of this install, without blanks or repeats (without case): what <see cref="ConnectorScope"/> offers.</summary>
    public static IReadOnlyList<string> RosterOf(GatewaySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var roster = new List<string>(snapshot.ActiveConnectors.Count);
        foreach (var name in snapshot.ActiveConnectors)
        {
            if (!string.IsNullOrWhiteSpace(name) && !roster.Contains(name.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                roster.Add(name.Trim());
            }
        }

        return roster;
    }

    // ---- Alerts and version (as they always were) ---------------------------------------------------------------------------------------

    /// <summary>The sentence a screen reader says for the alerts chip.</summary>
    public static string AlertsName(GatewaySnapshot snapshot) => "Alerts: " + GatewayPresentation.AlertDetail(snapshot);

    /// <summary>The version chip's tooltip.</summary>
    public static string VersionDetail(GatewaySnapshot snapshot) =>
        $"The running gateway reports DefenseClaw version {snapshot.BinaryVersion}.";

    // ---- Running and stale --------------------------------------------------------------------------------------------------------------

    /// <summary><c>Running</c> for one command, <c>Running 3</c> for several.</summary>
    public static string RunningText(int count) => count <= 1 ? "Running" : $"Running {count.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>The sentence for the running chip, its tooltip and its automation name.</summary>
    public static string RunningDetail(int count) =>
        count == 1
            ? "1 command is running. Its output is in Activity."
            : $"{count.ToString(CultureInfo.InvariantCulture)} commands are running. Their output is in Activity.";

    /// <summary>What the Stale chip says it means: how long since the last good poll, against the interval it is counted in.</summary>
    public static string StaleDetail(TimeSpan since, TimeSpan cadence) =>
        $"No gateway poll has finished cleanly for {Span(since)}, more than {PollFreshness.IntervalsBeforeStale.ToString(CultureInfo.InvariantCulture)} times the {Span(cadence)} check interval. " +
        "What this strip shows may be out of date. Refresh polls the gateway now.";

    /// <summary><c>12 s</c>, <c>2 min 5 s</c>, <c>1 h 3 min</c>.</summary>
    public static string Span(TimeSpan span)
    {
        var total = (long)Math.Round(span.TotalSeconds);
        if (total < 120)
        {
            return $"{total.ToString(CultureInfo.InvariantCulture)} s";
        }

        if (total < 7200)
        {
            var minutes = total / 60;
            var seconds = total % 60;
            return seconds == 0
                ? $"{minutes.ToString(CultureInfo.InvariantCulture)} min"
                : $"{minutes.ToString(CultureInfo.InvariantCulture)} min {seconds.ToString(CultureInfo.InvariantCulture)} s";
        }

        return $"{(total / 3600).ToString(CultureInfo.InvariantCulture)} h {(total % 3600 / 60).ToString(CultureInfo.InvariantCulture)} min";
    }
}
