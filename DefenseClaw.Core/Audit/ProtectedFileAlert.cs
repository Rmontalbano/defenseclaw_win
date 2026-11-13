using System.Text;

namespace DefenseClaw.Core.Audit;

/// <summary>
/// What a <c>protected-file-changed</c> alert says, read from the audit row's own text. The row never holds the file's content, and nothing here (or anywhere
/// in the app) opens the watched file: the only inputs are strings the gateway already wrote.
/// </summary>
/// <param name="File">The file, as the row's target names it (the gateway's absolute path).</param>
/// <param name="Class">The gateway's class: <c>authorized_keys</c>, <c>shell_rc</c>, <c>autostart</c> or <c>other</c>; empty when the row does not say.</param>
/// <param name="Before">The state before: <c>sha256:</c> and 12 hex characters, <c>absent</c>, or another word the watch uses (<c>dir</c>, <c>unreadable</c> ...). Empty when the row does not say.</param>
/// <param name="After">The state after, in the same words.</param>
/// <param name="Connector">The agent's connector; empty when the row does not say.</param>
/// <param name="Session">The agent session; empty when the row does not say.</param>
/// <param name="ToolCall">The tool call that was running; empty when the row does not say.</param>
/// <param name="AgentIdentity">The agent identity; empty when the row does not say.</param>
/// <param name="Restored">True when restore mode put the content back, false when it tried and could not, null when it did not try.</param>
/// <param name="RestoreError">Why a restore failed; empty otherwise.</param>
public sealed record ProtectedFileChange(
    string File,
    string Class,
    string Before,
    string After,
    string Connector,
    string Session,
    string ToolCall,
    string AgentIdentity,
    bool? Restored,
    string RestoreError);

/// <summary>
/// The runtime's contract for this alert (<c>emitProtectedFileChange</c> and <c>protectedFileChangeDetails</c>, internal/gateway/protected_files.go at tag
/// 1.0.6): action <c>protected-file-changed</c>, severity HIGH, target the file's path, and a details text of space-separated <c>key=value</c> pairs
/// (<c>rule class before after connector session tool_invocation agent_identity restored restore_error</c>, the last one Go-quoted).
/// <para>
/// <b>How it reaches the app.</b> The row is filed under bucket <c>security.finding</c> with event name <c>legacy.audit.protected.file.changed</c>
/// (internal/observability/zz_generated_telemetry_producers.go), so the database queue's two ordinary arms do not hold it. <c>alerts --json</c> lists it
/// (alert_semantics.py <c>ALERT_LEGACY_FINDING_ACTIONS</c>, db.py <c>compat_finding</c>), and the queue gains <see cref="QueueArm"/> on a runtime with the
/// capability, so the signal does not wait for or depend on a CLI run. <see cref="RuntimeAlertMerge"/> keeps a row both hold as one row.
/// </para>
/// </summary>
public static class ProtectedFileAlert
{
    /// <summary>The audit action (audit_actions.py <c>ACTION_PROTECTED_FILE_CHANGED</c>).</summary>
    public const string Action = "protected-file-changed";

    /// <summary>The finding's rule (<c>protectedFileRuleID</c>).</summary>
    public const string RuleId = "persistence.protected_file_changed";

    /// <summary>
    /// The queue's extra arm. It is searched by <c>idx_audit_action</c> (these rows are few); the unary plus keeps the planner off the bucket index, which
    /// would walk every finding for the sake of the sort. The event-name test keeps a row out that <c>finding.observed</c> already lists.
    /// </summary>
    internal const string QueueArm = "e.action = '" + Action + "' AND +e.bucket = 'security.finding' AND +e.event_name <> 'finding.observed'";

    /// <summary>The longest value kept from the row; a details text is the gateway's, but it is drawn, so it stays bounded.</summary>
    private const int ValueLimit = 512;

    /// <summary>True for this alert's action, in any case (the runtime's own comparison folds case).</summary>
    public static bool IsAction(string? action) => string.Equals(action?.Trim(), Action, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads a row's text. Whatever is missing stays empty and whatever is unknown is ignored; a details text that is not <c>key=value</c> pairs gives the
    /// file alone. Never throws, and touches no file.
    /// </summary>
    /// <param name="target">The row's target.</param>
    /// <param name="details">The row's details.</param>
    /// <param name="connector">The row's own connector column, used when the details do not name one.</param>
    /// <param name="session">The row's own session column, or the runtime's <c>session_id</c> fact, used when the details do not name one.</param>
    /// <param name="agentIdentity">The runtime's <c>agent_identity_id</c> fact, used when the details do not name one.</param>
    public static ProtectedFileChange Parse(string? target, string? details, string? connector = null, string? session = null, string? agentIdentity = null)
    {
        var pairs = Pairs(details);
        var restored = Value(pairs, "restored");
        return new ProtectedFileChange(
            target?.Trim() ?? string.Empty,
            Value(pairs, "class"),
            Value(pairs, "before"),
            Value(pairs, "after"),
            FirstOf(Value(pairs, "connector"), connector),
            FirstOf(Value(pairs, "session"), session),
            Value(pairs, "tool_invocation"),
            FirstOf(Value(pairs, "agent_identity"), agentIdentity),
            restored switch
            {
                "true" => true,
                "false" => false,
                _ => null,
            },
            Value(pairs, "restore_error"));
    }

    /// <summary>
    /// A state as a person reads it: the hash prefix without its <c>sha256:</c>, "absent (no file)", a link's first 12 hex characters of its target's hash
    /// (the watch does not shorten those), and any other word as the row has it. "not recorded" when the row does not say.
    /// </summary>
    public static string DigestLabel(string? digest)
    {
        var text = digest?.Trim() ?? string.Empty;
        const string Sha = "sha256:";
        const string Link = "link:sha256:";
        if (text.Length == 0)
        {
            return "not recorded";
        }

        if (text.StartsWith(Sha, StringComparison.Ordinal))
        {
            return Prefix(text[Sha.Length..]);
        }

        if (text.StartsWith(Link, StringComparison.Ordinal))
        {
            return "link " + Prefix(text[Link.Length..]);
        }

        return text == "absent" ? "absent (no file)" : text;

        static string Prefix(string hex) => hex.Length > 12 ? hex[..12] : hex;
    }

    /// <summary>The file for one line: the home folder as <c>~</c> when the path is under <paramref name="home"/>; the path as it is otherwise, or "a protected file" when the row names none.</summary>
    public static string FileLabel(string? file, string? home = null)
    {
        var path = file?.Trim() ?? string.Empty;
        if (path.Length == 0)
        {
            return "a protected file";
        }

        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var root = home.TrimEnd('\\', '/');
        return root.Length > 0 && path.Length > root.Length + 1 && path.StartsWith(root, StringComparison.OrdinalIgnoreCase) && path[root.Length] is '\\' or '/'
            ? "~" + path[root.Length..]
            : path;
    }

    private static string FirstOf(string value, string? fallback) => value.Length > 0 ? value : Bounded(fallback?.Trim() ?? string.Empty);

    private static string Value(Dictionary<string, string> pairs, string key) => pairs.TryGetValue(key, out var value) ? value : string.Empty;

    private static string Bounded(string value) => value.Length <= ValueLimit ? value : value[..ValueLimit];

    /// <summary>The <c>key=value</c> pairs of a details text; the first of a repeated key wins. A value is one word, or a double-quoted string with Go's escapes.</summary>
    private static Dictionary<string, string> Pairs(string? details)
    {
        var pairs = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(details))
        {
            return pairs;
        }

        var i = 0;
        while (i < details.Length)
        {
            while (i < details.Length && char.IsWhiteSpace(details[i]))
            {
                i++;
            }

            var keyStart = i;
            while (i < details.Length && details[i] != '=' && !char.IsWhiteSpace(details[i]))
            {
                i++;
            }

            if (i >= details.Length || details[i] != '=')
            {
                // A word with no value: not a pair.
                continue;
            }

            var key = details[keyStart..i];
            i++;
            var value = i < details.Length && details[i] == '"' ? Quoted(details, ref i) : Word(details, ref i);
            if (key.Length > 0)
            {
                _ = pairs.TryAdd(key, Bounded(value));
            }
        }

        return pairs;
    }

    private static string Word(string text, ref int i)
    {
        var start = i;
        while (i < text.Length && !char.IsWhiteSpace(text[i]))
        {
            i++;
        }

        return text[start..i];
    }

    /// <summary>A value Go's <c>%q</c> wrote; an escape this does not know keeps its character, and an unclosed quote takes the rest of the text.</summary>
    private static string Quoted(string text, ref int i)
    {
        var value = new StringBuilder();
        i++;
        while (i < text.Length && text[i] != '"')
        {
            if (text[i] == '\\' && i + 1 < text.Length)
            {
                i++;
                _ = value.Append(text[i] switch
                {
                    'n' or 'r' or 't' => ' ',
                    var other => other,
                });
            }
            else
            {
                _ = value.Append(text[i]);
            }

            i++;
        }

        i = Math.Min(i + 1, text.Length);
        return value.ToString();
    }
}

/// <summary>
/// The sentences the app says about the watch, in one place, so the Alerts inspector, the Overview row and the setting cannot differ
/// (docs-site/content/docs/guardrail/protected-files.mdx at tag 1.0.6). Each one is a limit of the runtime the UI must not hide.
/// </summary>
public static class ProtectedFileWatchFacts
{
    /// <summary>The config key of the setting.</summary>
    public const string OnChangeKey = "guardrail.protected_files.on_change";

    /// <summary>The attribution limit.</summary>
    public const string Attribution =
        "Attribution is by time: a change you make by hand while an agent call runs is reported as the agent's, and a background program an agent left running is reported as yours.";

    /// <summary>What the watch does not do.</summary>
    public const string BlocksNothing = "The watch blocks nothing: it reports a change after it happened.";

    /// <summary>What the alert holds of the file.</summary>
    public const string NoContent = "The alert holds the first 12 hex characters of the SHA-256 before and after, never the file's content.";

    /// <summary>What <c>restore</c> does, shown in its review before it runs.</summary>
    public const string RestoreWarning =
        "Restore puts back the last accepted content after an agent's change, and a file the agent created is removed. It is meant for strict setups only and is off " +
        "in every pack. Configuration tools that rewrite these files during an agent session are undone: leave it off on those hosts. It still blocks nothing.";

    /// <summary>What a rollback does with the setting, shown where it is changed.</summary>
    public const string RollbackWarning =
        "defenseclaw rollback to 1.0.5 or older puts back the config from before the upgrade. A 1.0.5 or older release started on a config that sets " +
        "guardrail.protected_files refuses it, so remove the block first if you move the config by hand.";

    /// <summary>When a change to the setting takes effect.</summary>
    public const string RestartNote = "Saved in config.yaml. The gateway reads this setting when it starts, so it applies at the next gateway restart.";

    /// <summary>What Windows does not get, whatever the docs' table says for other systems.</summary>
    public const string WindowsCoverage = "Shell startup files and autostart entries are watched on Linux and macOS only.";

    /// <summary>
    /// What 1.0.9 adds that has no Windows gate and is a separate rule from the file watch: a shell command's write to SSH authorized keys, a shell startup file or
    /// a Git hook is at least an alert in every pack (internal/gateway/shell_protected_file_profile.go@1.0.9:28-33,:90-106; shell commands only, not the native
    /// write tools); DefenseClaw's own state files block in every pack (:75-77,:99-100); an agent edit that turns the DefenseClaw hooks off in the agent's settings
    /// (tamper.agent_hook_config_disable) blocks in action mode, a shell write to those settings (tamper.agent_hook_config_write) is an alert and blocks in the
    /// strict pack (agent_hook_config_guard.go@1.0.9; cognitive.yaml@1.0.9:112-135). Observe mode records the two hook-settings rules and does not block them.
    /// Shown only on a runtime with <see cref="DefenseClaw.Core.Runtime.RuntimeCapability.ShellProtectedFileFloor"/>.
    /// </summary>
    public const string ShellFloorCoverage =
        "On Windows too, a shell command the agent runs that writes ~\\.ssh\\authorized_keys, a shell startup file or a Git hook is at least an alert in every rule pack, " +
        "permissive included, and changes to DefenseClaw's own files are blocked in every pack. In action mode an agent edit that turns DefenseClaw's hooks off in its " +
        "settings is blocked; a shell command that writes those settings is an alert (the strict pack blocks it).";
}
