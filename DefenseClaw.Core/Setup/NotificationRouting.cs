using DefenseClaw.Core.Config;

namespace DefenseClaw.Core.Setup;

/// <summary>Where a notification slot sits in config.yaml's <c>notifications:</c> block: an event type, or the subsystem the event came from.</summary>
public enum NotificationSlotKind
{
    /// <summary>An event type (<c>block_enforced</c>, <c>block_would_block</c>, <c>hitl_approval</c>).</summary>
    Category,

    /// <summary>A subsystem of origin (<c>sources.hook</c>, <c>sources.guardrail</c>, <c>sources.asset_policy</c>).</summary>
    Source,
}

/// <summary>
/// One toggle of <c>defenseclaw setup notifications-set &lt;slot&gt; on|off</c>.
/// </summary>
/// <param name="Id">The slot as the CLI's help lists it, and as the TUI's wizard sends it: <c>sources.hook</c>, never the short <c>hook</c> (both work).</param>
/// <param name="Kind">An event type or a source.</param>
/// <param name="Label">The TUI wizard's label for it.</param>
/// <param name="Description">What it controls, in the CLI's own words where it has them.</param>
/// <param name="DefaultOn">What the runtime does when config.yaml does not name the slot (<c>NotificationsConfig</c>'s defaults).</param>
public sealed record NotificationSlot(string Id, NotificationSlotKind Kind, string Label, string Description, bool DefaultOn);

/// <summary>What config.yaml says about the runtime's own desktop notifications: the master switch and each slot.</summary>
/// <param name="MasterEnabled"><c>notifications.enabled</c>, or the runtime's default when config.yaml does not name it (on, on Windows).</param>
/// <param name="MasterIsExplicit">True when config.yaml names the master switch.</param>
/// <param name="Values">Every slot of <see cref="NotificationRouting.Slots"/>, by id: what config.yaml says, or the slot's default.</param>
public sealed record NotificationRoutingState(bool MasterEnabled, bool MasterIsExplicit, IReadOnlyDictionary<string, bool> Values)
{
    /// <summary>The slot's current value; false for an id that is not a slot.</summary>
    public bool this[string slot] => Values.TryGetValue(slot, out var on) && on;
}

/// <summary>One slot that is to change.</summary>
public sealed record NotificationChange(NotificationSlot Slot, bool From, bool To)
{
    /// <summary>The CLI's word for the new value: <c>on</c> or <c>off</c>.</summary>
    public string Value => To ? "on" : "off";

    /// <summary>"Block (enforced): off to on" - for the review's list of what changes.</summary>
    public string Describe() => $"{Slot.Label}: {(From ? "on" : "off")} to {Value}";
}

/// <summary>
/// The Notifications Routing wizard of the 0.8.10 TUI (<c>tui/panels/setup.py</c>: <c>NOTIFICATION_ROUTING_SLOTS</c>,
/// <c>notifications_routing_wizard_fields</c>, <c>notifications_routing_intents</c>) as data and argv: six toggles seeded from config.yaml, one
/// <c>setup notifications-set</c> invocation per toggle that changed, and "nothing to apply" when none did. Pure: nothing here runs a command.
/// <para>
/// <b>What a slot is.</b> <c>notifications-set</c> flips one key of the <c>notifications:</c> block (<c>cmd_setup.py: setup_notifications_set</c>)
/// and leaves the master switch alone (<c>setup notifications on|off</c> is that one). The gateway's dispatcher reads the block once, at boot, so
/// the command restarts the gateway unless it is given <c>--no-restart</c>. A value already in place prints "nothing to change" and exits 0
/// without restarting - which is why the plan is built from a diff, never from the six toggles.
/// </para>
/// <para>
/// <b>One restart, not N.</b> The TUI sends the operator's restart choice with every slot, so two changes restart the gateway twice. The
/// Mac's builder (<c>notificationCommands</c>) lets only the last one restart; so does this (<see cref="Argvs"/>): every step but the last
/// carries <c>--no-restart</c>, and the config is saved by each before the one restart that reads all of it.
/// </para>
/// </summary>
public static class NotificationRouting
{
    /// <summary>The runtime's own master-switch default on this platform (<c>_default_notifications_enabled</c>: on for macOS and Windows).</summary>
    public const bool MasterDefaultOn = true;

    /// <summary>The six slots, in the TUI wizard's order: the event types, then the sources.</summary>
    public static IReadOnlyList<NotificationSlot> Slots { get; } = new NotificationSlot[]
    {
        new("block_enforced", NotificationSlotKind.Category, "Block (enforced)",
            "A tool call or prompt was actually denied.", true),
        new("block_would_block", NotificationSlotKind.Category, "Block (would-block / observe)",
            "Observe mode: a verdict that would have blocked, or would have asked, but let the call through.", false),
        new("hitl_approval", NotificationSlotKind.Category, "HITL approval",
            "A human-in-the-loop approval prompt is waiting for an answer.", true),
        new("sources.hook", NotificationSlotKind.Source, "Source: hooks",
            "Verdicts from the per-tool hooks of the connectors (Claude Code, Codex, ...).", true),
        new("sources.guardrail", NotificationSlotKind.Source, "Source: guardrail",
            "Guardrail verdicts.", true),
        new("sources.asset_policy", NotificationSlotKind.Source, "Source: asset policy",
            "Skill and MCP allow-list blocks.", true),
    };

    /// <summary>The slot called <paramref name="id"/>, or null.</summary>
    public static NotificationSlot? Find(string? id) =>
        Slots.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));

    /// <summary>What config.yaml (loaded as <paramref name="document"/>) says; a missing document, file or key reads as the runtime's default.</summary>
    public static NotificationRoutingState Read(ConfigDocument? document) => FromYaml(document?.RawText);

    /// <summary>What config.yaml's text says. Never throws: text that is not YAML says nothing, and every slot is then its default.</summary>
    public static NotificationRoutingState FromYaml(string? yaml)
    {
        var reader = ConfigYamlReader.Parse(yaml);
        var master = reader.Bool("notifications", "enabled");

        var values = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var slot in Slots)
        {
            values[slot.Id] = reader.Bool(Path(slot)) ?? slot.DefaultOn;
        }

        return new NotificationRoutingState(master ?? MasterDefaultOn, master is not null, values);
    }

    /// <summary>The slots whose wanted value differs from the current one, in slot order. A slot <paramref name="wanted"/> does not name is left alone.</summary>
    public static IReadOnlyList<NotificationChange> Diff(NotificationRoutingState current, IReadOnlyDictionary<string, bool> wanted)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(wanted);

        var changes = new List<NotificationChange>();
        foreach (var slot in Slots)
        {
            if (wanted.TryGetValue(slot.Id, out var to) && to != current[slot.Id])
            {
                changes.Add(new NotificationChange(slot, current[slot.Id], to));
            }
        }

        return changes;
    }

    /// <summary>
    /// <c>setup notifications-set SLOT on|off</c>, with <c>--no-restart</c> when asked. The slot is the dotted path the help lists; both
    /// positionals come before the option, as in the CLI's own examples.
    /// </summary>
    public static IReadOnlyList<string> Argv(NotificationChange change, bool noRestart)
    {
        ArgumentNullException.ThrowIfNull(change);

        return noRestart
            ? new[] { "setup", "notifications-set", change.Slot.Id, change.Value, "--no-restart" }
            : new[] { "setup", "notifications-set", change.Slot.Id, change.Value };
    }

    /// <summary>
    /// One invocation per change, in slot order. With <paramref name="restartAfter"/> only the last restarts the gateway (the earlier ones
    /// are told <c>--no-restart</c>), without it none does. <paramref name="canSkipRestart"/> false means the installed CLI's help has no
    /// <c>--no-restart</c> on this command: nothing can be suppressed, so each invocation restarts by itself and none is given the flag.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>> Argvs(IReadOnlyList<NotificationChange> changes, bool restartAfter, bool canSkipRestart = true)
    {
        ArgumentNullException.ThrowIfNull(changes);

        var argvs = new List<IReadOnlyList<string>>(changes.Count);
        for (var i = 0; i < changes.Count; i++)
        {
            var last = i == changes.Count - 1;
            argvs.Add(Argv(changes[i], noRestart: canSkipRestart && (!restartAfter || !last)));
        }

        return argvs;
    }

    /// <summary>The key path of a slot in config.yaml: <c>notifications.block_enforced</c>, <c>notifications.sources.hook</c>.</summary>
    private static string[] Path(NotificationSlot slot) =>
        new[] { "notifications" }.Concat(slot.Id.Split('.')).ToArray();
}
