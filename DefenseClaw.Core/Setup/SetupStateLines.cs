using DefenseClaw.Core.Config;
using DefenseClaw.Core.Text;

namespace DefenseClaw.Core.Setup;

/// <summary>
/// What a Setup card says about the thing it configures, right now.
/// </summary>
/// <param name="Text">The line on the tile: short enough for one line of a 233-pixel tile.</param>
/// <param name="Detail">The whole sentence, for the tooltip - the TUI's own line where it has one.</param>
public sealed record CardState(string Text, string Detail);

/// <summary>
/// The one-line "here is what is configured today" of the 0.8.10 TUI's Setup goal menu (<c>tui/panels/setup.py: wizard_state_summary</c>), for the
/// cards of the Setup hub: the LLM, the guardrail and the connector roster have one there; the notification switch and its categories are added
/// because this app routes them through its own dialog. Each is read from the text of config.yaml the app already holds (nothing is opened, no
/// command runs), with the runtime's default for a key the file does not name - the TUI reads the loaded configuration, which has them all.
/// <para>
/// The tile's <see cref="CardState.Text"/> is the short form; <see cref="CardState.Detail"/> is the TUI's line as it prints it
/// (<c>Guardrail: on  ·  Mode: observe  ·  Strategy: regex_only</c>), so an operator who knows the TUI finds the same words.
/// </para>
/// </summary>
public static class SetupStateLines
{
    private const string NotSet = "not set";

    /// <summary>Connectors whose LLM role is judge and agent (<c>connector_llm_role</c>): the proxy connectors, which Windows does not run.</summary>
    private static readonly string[] ProxyConnectors = { "openclaw", "zeptoclaw" };

    /// <summary>The LLM card: <c>Main: openai/gpt-4o  ·  Judge: not set  ·  Connectors: codex (judge only)</c>.</summary>
    public static CardState Llm(ConfigDocument? document)
    {
        var facts = OverviewConfigFacts.FromConfig(document);
        var reader = ConfigYamlReader.From(document);

        var provider = facts.LlmProvider;
        var model = facts.LlmModel;
        var main = provider.Length > 0 && model.Length > 0 ? $"{provider}/{model}" : model.Length > 0 ? model : provider.Length > 0 ? provider : NotSet;
        var judge = reader.Text("guardrail", "judge", "model") is { Length: > 0 } j ? Shown(j) : NotSet;
        var active = ActiveConnectors(reader);
        var role = active.Any(a => ProxyConnectors.Contains(a, StringComparer.Ordinal)) ? "judge+agent available" : "judge only";

        return new CardState(
            $"Main: {main} · Judge: {judge}",
            $"Main: {main}  ·  Judge: {judge}  ·  Connectors: {(active.Count == 0 ? "none" : string.Join(", ", active.Select(Shown)))} ({role})");
    }

    /// <summary>The guardrail card: <c>Guardrail: on  ·  Mode: observe  ·  Strategy: regex_only</c>.</summary>
    public static CardState Guardrail(ConfigDocument? document)
    {
        var reader = ConfigYamlReader.From(document);
        var enabled = reader.Bool("guardrail", "enabled") ?? false;
        var mode = Shown(Or(reader.Text("guardrail", "mode"), "observe"));
        var strategy = Shown(Or(reader.Text("guardrail", "detection_strategy"), "regex_only"));
        var on = enabled ? "on" : "off";

        return new CardState(
            $"{(enabled ? "On" : "Off")} · {mode} · {strategy}",
            $"Guardrail: {on}  ·  Mode: {mode}  ·  Strategy: {strategy}");
    }

    /// <summary>The roster of connectors configured today: <c>Active connectors: codex, claudecode</c>, or <c>not set</c>.</summary>
    public static CardState Roster(ConfigDocument? document)
    {
        var active = ActiveConnectors(ConfigYamlReader.From(document));
        return active.Count == 0
            ? new CardState("No connector configured", "Active connectors: not set")
            : new CardState("Active: " + string.Join(", ", active.Select(Shown)), "Active connectors: " + string.Join(", ", active.Select(Shown)));
    }

    /// <summary>
    /// One connector's card: whether <c>guardrail.connectors</c> has it and in which mode (<c>configured · observe · fail-open</c>). The target
    /// is the setup subcommand (<c>claude-code</c>), the config key the same name without the hyphen.
    /// </summary>
    public static CardState Connector(ConfigDocument? document, string target)
    {
        ArgumentNullException.ThrowIfNull(target);

        var reader = ConfigYamlReader.From(document);
        var id = Id(target);
        var key = reader.Keys("guardrail", "connectors").FirstOrDefault(k => string.Equals(Id(k), id, StringComparison.Ordinal));
        var configured = key is not null || ActiveConnectors(reader).Contains(id, StringComparer.Ordinal);
        if (!configured)
        {
            return new CardState("Not configured", $"{Shown(id)}: not in guardrail.connectors, so DefenseClaw does not watch it yet.");
        }

        var mode = key is null ? null : reader.Text("guardrail", "connectors", key, "mode");
        var fail = key is null ? null : reader.Text("guardrail", "connectors", key, "hook_fail_mode");
        var parts = new List<string> { "Configured" };
        if (!string.IsNullOrWhiteSpace(mode))
        {
            parts.Add(Shown(mode.Trim().ToLowerInvariant()));
        }

        if (!string.IsNullOrWhiteSpace(fail))
        {
            parts.Add("fail-" + Shown(fail.Trim().ToLowerInvariant()));
        }

        var detail = $"{Shown(id)}: configured" +
                     (parts.Count > 1 ? " (" + string.Join(", ", parts.Skip(1)) + ")" : string.Empty) +
                     ". The wizard starts from these values.";
        return new CardState(string.Join(" · ", parts), detail);
    }

    /// <summary>The master switch: <c>Desktop notifications: on</c>. Says when the file does not name it and the runtime's default applies.</summary>
    public static CardState NotificationsSwitch(ConfigDocument? document)
    {
        var state = NotificationRouting.Read(document);
        var on = state.MasterEnabled ? "on" : "off";
        return new CardState(
            $"Desktop notifications {on}",
            $"Desktop notifications: {on}" + (state.MasterIsExplicit ? string.Empty : " (config.yaml does not say; the runtime's default on Windows)") +
            ". These are the gateway's own notifications, not the tray alerts of this app.");
    }

    /// <summary>The categories and sources: <c>5 of 6 on</c>, and which are off.</summary>
    public static CardState NotificationCategories(ConfigDocument? document)
    {
        var state = NotificationRouting.Read(document);
        var slots = NotificationRouting.Slots;
        var off = slots.Where(s => !state[s.Id]).Select(s => s.Label).ToArray();
        var on = slots.Count - off.Length;
        var text = $"{on} of {slots.Count} on" + (state.MasterEnabled ? string.Empty : " · switch off");
        var detail = off.Length == 0
            ? "Every category and source raises a desktop notification."
            : "Off: " + string.Join(", ", off) + ".";
        return new CardState(text, state.MasterEnabled ? detail : detail + " The master switch is off, so none of them raises one.");
    }

    /// <summary>
    /// The connectors the TUI calls active (<c>_active_connector_names_for_setup</c>): the keys of <c>guardrail.connectors</c>, normalised and
    /// sorted; failing that, the single connector (<c>claw.mode</c>); failing that, none.
    /// </summary>
    public static IReadOnlyList<string> ActiveConnectors(ConfigYamlReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var keys = reader.Keys("guardrail", "connectors")
            .Select(Id)
            .Where(static k => k.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static k => k, StringComparer.Ordinal)
            .ToArray();
        if (keys.Length > 0)
        {
            return keys;
        }

        var single = Or(reader.Text("claw", "mode"), string.Empty);
        return single.Length > 0 ? new[] { Id(single) } : Array.Empty<string>();
    }

    /// <summary>A connector's name as the runtime keys it, without the hyphens or underscores a setup subcommand or an old file puts in.</summary>
    public static string Id(string? name) =>
        (name ?? string.Empty).Trim().ToLowerInvariant().Replace("-", string.Empty, StringComparison.Ordinal).Replace("_", string.Empty, StringComparison.Ordinal);

    private static string Or(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    /// <summary>A value out of config.yaml as it is drawn: control and bidirectional characters written out, and cut to a name's length.</summary>
    private static string Shown(string value)
    {
        var visible = DisplayNames.Visible(value);
        return visible.Length <= 64 ? visible : visible[..64];
    }
}
