using System;
using System.Collections.Generic;
using System.Linq;

namespace DefenseClaw.App.ViewModels.ConfigEditor;

/// <summary>One entry of a Choice field's list: the value written to config.yaml and the words the combo shows for it.</summary>
public sealed record ChoiceOption(string Value, string Label);

/// <summary>
/// What the catalogue knows about one config.yaml key: the TUI's one-line hint, the values a Choice offers (null: the key is not a
/// choice), and, for a key the TUI shows but does not let the operator edit, why.
/// </summary>
public sealed record ConfigFieldInfo(string Hint, IReadOnlyList<string>? Options = null, string? ReadOnlyReason = null)
{
    public bool IsChoice => Options is not null;
}

/// <summary>
/// The two facts that change a connector list: the operating system it is for, and which runtime's connector set. The installed 0.8.10
/// runtime is the default; the pinned newer source (commit 95159fd) has another set and another Windows table, and is chosen only when the
/// runtime shows <see cref="DefenseClaw.Core.Runtime.RuntimeCapability.TuiRegistry"/> (<c>setup --help</c> lists amp, devin and kiro).
/// </summary>
public sealed record ConfigChoiceProfile(string OsName, bool ExtendedConnectors)
{
    /// <summary>This machine's operating system, with the 0.8.10 connector set: what the editor uses until the runtime says otherwise.</summary>
    public static ConfigChoiceProfile ForHost(bool extendedConnectors = false) => new(HostOsName(), extendedConnectors);

    /// <summary>Windows with the 0.8.10 connector set: this app's own profile when no runtime has been probed.</summary>
    public static ConfigChoiceProfile Windows { get; } = new("windows", false);

    private static string HostOsName() =>
        OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "darwin" : "linux";
}

/// <summary>
/// The Config editor's curated choices and per-field hints: what the 0.8.10 TUI's Setup config panel offers for each key, as data.
/// <para>
/// <b>Sources</b> (all read from the installed 0.8.10, <c>site-packages\defenseclaw\tui</c>, never run): the option tuples of
/// <c>services/cli_choices.py</c> (<c>CONNECTORS</c> <c>:35-49</c>, <c>supported_connector_choices</c> <c>:58-65</c>, <c>LLM_PROVIDERS</c> <c>:98-115</c>,
/// <c>LLM_OVERRIDE_PROVIDERS</c> <c>:116</c>); the platform table behind <c>supported_connector_choices</c> (<c>defenseclaw/platform_support.py</c>,
/// <c>WINDOWS_CONNECTOR_SUPPORT</c>); and the fields of <c>panels/setup.py</c> <c>build_setup_sections</c> (<c>:1391-1625</c>) with its helpers
/// (<c>action_matrix_fields</c> <c>:1628-1670</c>, <c>_guardrail_section</c> <c>:5837-5959</c>, <c>_per_connector_guardrail_fields</c> <c>:5688-5834</c>,
/// <c>_scanners_section</c> <c>:5962-6018</c>, <c>_ai_discovery_section</c>, <c>_gateway_watcher_section</c>, <c>_watch_section</c>, <c>_openshell_section</c>,
/// <c>_asset_policy_fields</c> <c>:6146-6310</c>, <c>_agent_hook_fields</c> <c>:6313-6337</c>, <c>_llm_override_fields</c> <c>:6415-6429</c>,
/// <c>_cisco_ai_defense_fields</c>). Hints are the TUI's own words. Where a hint names the connector it is for, the key's own name stands in.
/// The key is the dotted path from the file's root; <c>*</c> in a pattern is one path segment.
/// </para>
/// <para>
/// <b>What this form does differently, on purpose.</b>
/// <list type="bullet">
/// <item><description>The TUI shows a connector's <i>effective</i> value (its override, or the inherited global); this form shows the value the file
/// holds, which can be blank. A blank, or any value the list does not have, is shown as the field's current value and kept
/// (<see cref="ChoiceItems"/>); picking another value is the only thing that changes it.</description></item>
/// <item><description><c>guardrail.hook_fail_mode</c> and <c>guardrail.connectors.*.hook_fail_mode</c> are read-only, as in the TUI
/// (<c>setup.py:5759-5771</c>, <c>:5842-5846</c>): a fail mode written to config.yaml alone leaves the installed hook registration behind, which is
/// the "fail-mode drift" the Overview warns about, so it is changed on the Setup panel (<c>defenseclaw guardrail fail-mode</c>). The pinned
/// source keeps it read-only too; the Mac's port (a 0.8.5 snapshot) still edits it as a list.</description></item>
/// <item><description><c>guardrail.connector</c> is filtered for the operating system like <c>claw.mode</c>; the TUI offers the whole list there
/// (<c>setup.py:5855</c>), which on Windows would offer connectors the platform table calls unsupported.</description></item>
/// <item><description>Not curated: <c>scanners.skill_scanner.policy</c> (the TUI stores its <c>none</c> as an empty string,
/// <c>setup_state.py:998</c>, a mapping this editor does not carry) and the tri-state booleans (<c>openshell.auto_pair</c>, <c>host_networking</c>,
/// <c>asset_policy.connectors.*.registry_required</c>): their YAML type is a boolean, which a choice of words would turn into text. They keep
/// their TUI hint.</description></item>
/// </list>
/// </para>
/// </summary>
public static class ConfigFieldCatalog
{
    private const string BlankLabel = "(blank)";

    private const string HookFailModeReason =
        "Hook fail mode is not edited here: change it on the Setup panel (Guardrail) or with defenseclaw guardrail fail-mode, which update the " +
        "installed hook registration as well as config.yaml.";

    // ------------------------------------------------------------------ option lists (cli_choices.py)

    /// <summary><c>CONNECTORS</c> in 0.8.10 (<c>cli_choices.py:35-49</c>): the order drives every connector picker.</summary>
    private static readonly string[] Connectors0810 =
    {
        "openclaw", "zeptoclaw", "codex", "claudecode", "hermes", "cursor", "windsurf", "geminicli", "copilot", "openhands", "antigravity", "opencode", "omnigent",
    };

    /// <summary><c>CONNECTORS</c> at the pinned source (95159fd): windsurf and geminicli are gone, devin, amp and kiro are new.</summary>
    private static readonly string[] ConnectorsPin =
    {
        "openclaw", "zeptoclaw", "codex", "claudecode", "hermes", "cursor", "devin", "copilot", "openhands", "antigravity", "opencode", "amp", "omnigent", "kiro",
    };

    /// <summary>
    /// The connectors <c>WINDOWS_CONNECTOR_SUPPORT</c> calls <c>supported</c> or <c>preview</c> in 0.8.10 (<c>platform_support.py</c>); every other
    /// name there is <c>not_certified</c> (cursor, windsurf, geminicli, copilot, antigravity, opencode, hermes) or <c>unsupported</c> (openhands,
    /// omnigent, openclaw, zeptoclaw) and <c>connector_supported_on_os</c> hides both kinds from a picker.
    /// </summary>
    private static readonly HashSet<string> WindowsAvailable0810 = new(StringComparer.Ordinal) { "codex", "claudecode" };

    /// <summary>The same set at the pinned source: everything but openhands, openclaw and zeptoclaw is <c>supported</c> on native Windows there.</summary>
    private static readonly HashSet<string> WindowsAvailablePin = new(StringComparer.Ordinal)
    {
        "kiro", "codex", "claudecode", "cursor", "devin", "copilot", "antigravity", "opencode", "amp", "hermes", "omnigent",
    };

    /// <summary><c>LLM_PROVIDERS</c> (<c>cli_choices.py:98-115</c>): the providers the Setup config panel's Provider field cycles through.</summary>
    public static IReadOnlyList<string> LlmProviders { get; } = new[]
    {
        "anthropic", "openai", "openrouter", "azure", "gemini", "gemini-openai", "groq", "mistral", "cohere", "deepseek", "xai", "bedrock", "vertex_ai", "ollama", "vllm", "lm_studio",
    };

    /// <summary><c>LLM_OVERRIDE_PROVIDERS</c> (<c>cli_choices.py:116</c>): the same, with a leading blank for "inherit the unified LLM".</summary>
    public static IReadOnlyList<string> LlmOverrideProviders { get; } = new[] { string.Empty }.Concat(LlmProviders).ToArray();

    /// <summary>Every connector name the profile's runtime knows, in the order its pickers show them (<c>CONNECTORS</c>).</summary>
    public static IReadOnlyList<string> AllConnectors(ConfigChoiceProfile profile) =>
        profile.ExtendedConnectors ? ConnectorsPin : Connectors0810;

    /// <summary>
    /// <c>supported_connector_choices(os_name)</c> (<c>cli_choices.py:58-65</c>): <see cref="AllConnectors"/> without the ones <c>os</c> cannot run. On
    /// Windows that keeps only the connectors the platform table calls supported or preview, which is why <c>claw.mode</c> never offers
    /// openclaw or zeptoclaw there (and, on 0.8.10, none of the not-certified ones either). Everywhere else every connector stays.
    /// </summary>
    public static IReadOnlyList<string> SupportedConnectors(ConfigChoiceProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var all = AllConnectors(profile);
        if (!IsWindows(profile.OsName))
        {
            return all;
        }

        var available = profile.ExtendedConnectors ? WindowsAvailablePin : WindowsAvailable0810;
        return all.Where(available.Contains).ToArray();
    }

    // ------------------------------------------------------------------ the combo's items

    /// <summary>The words the combo shows for a value: the value itself, or <c>(blank)</c> for the empty string.</summary>
    public static string LabelFor(string value) => value.Length == 0 ? BlankLabel : value;

    /// <summary>
    /// The items of a Choice field: <paramref name="options"/> in their order, and, when <paramref name="current"/> is not one of them, the
    /// current value as one more item. A value the list does not know (a connector another platform offers, a provider the wizard has and the
    /// editor's list does not, a blank override) is therefore selectable and selected, never replaced by the first option.
    /// </summary>
    public static IReadOnlyList<ChoiceOption> ChoiceItems(IReadOnlyList<string> options, string current)
    {
        ArgumentNullException.ThrowIfNull(options);

        var items = options.Select(o => new ChoiceOption(o, LabelFor(o))).ToList();
        if (!options.Contains(current, StringComparer.Ordinal))
        {
            items.Add(new ChoiceOption(current, LabelFor(current)));
        }

        return items;
    }

    // ------------------------------------------------------------------ lookup

    /// <summary>
    /// What the catalogue says about <paramref name="path"/> (the dotted path from the file's root), or null for a key the TUI's panel does not
    /// list. <paramref name="profile"/> decides the connector lists; null is this machine with the 0.8.10 set.
    /// </summary>
    public static ConfigFieldInfo? Resolve(string path, ConfigChoiceProfile? profile = null)
    {
        ArgumentNullException.ThrowIfNull(path);

        profile ??= ConfigChoiceProfile.ForHost();
        if (ExactEntries.TryGetValue(path, out var entry))
        {
            return entry.Resolve(profile, Array.Empty<string>());
        }

        var segments = path.Split('.');
        foreach (var pattern in PatternEntries)
        {
            if (pattern.Segments.Length == segments.Length && pattern.TryMatch(segments, out var captures))
            {
                return pattern.Entry.Resolve(profile, captures);
            }
        }

        return null;
    }

    /// <summary>Every exact key in the catalogue (the patterns are not expanded): for the tests that pin its shape.</summary>
    internal static IReadOnlyCollection<string> ExactPaths => ExactEntries.Keys;

    private static bool IsWindows(string osName) => osName.Trim().StartsWith("win", StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ the table

    private sealed record Entry(
        Func<string[], string> Hint,
        Func<ConfigChoiceProfile, IReadOnlyList<string>>? Options = null,
        string? ReadOnlyReason = null)
    {
        public ConfigFieldInfo Resolve(ConfigChoiceProfile profile, string[] captures) =>
            new(Hint(captures), Options?.Invoke(profile), ReadOnlyReason);
    }

    private sealed record Pattern(string[] Segments, Entry Entry)
    {
        public bool TryMatch(string[] path, out string[] captures)
        {
            var found = new List<string>();
            for (var i = 0; i < Segments.Length; i++)
            {
                if (Segments[i] == "*")
                {
                    found.Add(path[i]);
                }
                else if (!string.Equals(Segments[i], path[i], StringComparison.Ordinal))
                {
                    captures = Array.Empty<string>();
                    return false;
                }
            }

            captures = found.ToArray();
            return true;
        }
    }

    private static readonly Lazy<(Dictionary<string, Entry> Exact, List<Pattern> Patterns)> Table = new(BuildTable);

    private static Dictionary<string, Entry> ExactEntries => Table.Value.Exact;

    private static List<Pattern> PatternEntries => Table.Value.Patterns;

    private static (Dictionary<string, Entry> Exact, List<Pattern> Patterns) BuildTable()
    {
        var exact = new Dictionary<string, Entry>(StringComparer.Ordinal);
        var patterns = new List<Pattern>();

        void Add(string path, Entry entry)
        {
            if (path.Contains('*', StringComparison.Ordinal))
            {
                patterns.Add(new Pattern(path.Split('.'), entry));
            }
            else
            {
                exact[path] = entry;
            }
        }

        void Hint(string path, string hint) => Add(path, new Entry(_ => hint));

        void Choice(string path, string hint, params string[] options) => Add(path, new Entry(_ => hint, _ => options));

        void ChoiceOf(string path, string hint, IReadOnlyList<string> options) => Add(path, new Entry(_ => hint, _ => options));

        // ---- build_setup_sections: General, Agent, Notifications, Claw, Gateway, Gateway Watchdog (setup.py:1406-1585)
        Hint("data_dir", "Root directory for DefenseClaw state.");
        Hint("audit_db", "SQLite file path for the audit log.");
        Hint("quarantine_dir", "Where quarantined assets are moved.");
        Hint("plugin_dir", "Directory DefenseClaw scans for installed plugins.");
        Hint("policy_dir", "Root of policy packs.");
        Hint("environment", "Free-form deployment label.");
        ChoiceOf("llm.provider", "LLM provider family.", LlmProviders);
        Hint("llm.model", "Model identifier.");
        Hint("llm.api_key_env", "Env var NAME holding the unified key.");
        Hint("llm.api_key", "Inline key; prefer API Key Env.");
        Hint("llm.base_url", "Override provider base URL.");
        Hint("llm.timeout", "Per-request timeout in seconds.");
        Hint("llm.max_retries", "Retries with exponential backoff.");
        Hint("agent.id", "Stable lower-kebab-case identity.");
        Hint("agent.name", "Human-readable display name.");
        Hint("notifications.enabled", "Master desktop notification switch.");
        Hint("notifications.block_enforced", "Toast when a request is actually denied.");
        Hint("notifications.block_would_block", "Toast for observe-mode would-block verdicts.");
        Hint("notifications.hitl_approval", "Toast when a HITL approval prompt is pending.");
        Hint("notifications.sources.hook", "Allow hook notifications.");
        Hint("notifications.sources.guardrail", "Allow guardrail notifications.");
        Hint("notifications.sources.asset_policy", "Allow asset-policy notifications.");
        Hint("notifications.dedup_window", "Duration string like 30s, 1m, or 500ms.");
        Hint("notifications.max_per_minute", "Global notification rate cap.");
        Add("claw.mode", new Entry(_ => "Active agent framework.", SupportedConnectors));
        Hint("claw.home_dir", "Override for connector home directory.");
        Hint("claw.config_file", "Connector primary config file.");
        Hint("gateway.host", "Where clients reach the gateway.");
        Hint("gateway.port", "WebSocket port.");
        Hint("gateway.api_port", "REST sidecar port.");
        Hint("gateway.api_bind", "Bind address for API Port.");
        Hint("gateway.auto_approve_safe", "Auto-approve CLEAN scans.");
        Hint("gateway.tls", "Force wss:// and cert validation.");
        Hint("gateway.tls_skip_verify", "Skip cert verification.");
        Hint("gateway.reconnect_ms", "Initial reconnect backoff.");
        Hint("gateway.max_reconnect_ms", "Reconnect backoff ceiling.");
        Hint("gateway.approval_timeout_s", "Operator approval wait budget.");
        Hint("gateway.token_env", "Env var NAME holding gateway auth token.");
        Hint("gateway.token", "Inline gateway token.");
        Hint("gateway.device_key_file", "Path to per-machine private key.");
        Hint("gateway.watchdog.enabled", "Turn the watchdog on/off.");
        Hint("gateway.watchdog.interval", "Seconds between health checks.");
        Hint("gateway.watchdog.debounce", "Consecutive failures before restart.");

        // ---- Agent Hooks (claude_code, codex) and Connector Hooks (connector_hooks.<name>): _agent_hook_fields, setup.py:6313-6337
        void AgentHooks(string prefix, Func<string[], string> enabledHint)
        {
            Add(prefix + ".enabled", new Entry(enabledHint));
            Choice(prefix + ".mode", "Blank inherits connector defaults.", string.Empty, "observe", "action");
            Choice(prefix + ".fail_mode", "Legacy policy-layer hint.", string.Empty, "open", "closed");
            Hint(prefix + ".scan_on_session_start", "Run checks when session begins.");
            Hint(prefix + ".scan_on_stop", "Run checks when session stops.");
            Hint(prefix + ".scan_paths", "CSV extra paths scanned by hooks.");
            Hint(prefix + ".component_scan_interval_minutes", "Minimum minutes between repeated scans.");
        }

        AgentHooks("claude_code", _ => "Claude Code hooks master switch.");
        AgentHooks("codex", _ => "Codex hooks master switch.");
        AgentHooks("connector_hooks.*", c => $"{c[0]} hooks master switch.");

        // ---- Guardrail: _guardrail_section, setup.py:5837-5959
        Hint("guardrail.enabled", "Master guardrail switch.");
        Choice("guardrail.mode", "observe=log only; action=block.", "observe", "action");
        Add("guardrail.hook_fail_mode", new Entry(_ => string.Empty, ReadOnlyReason: HookFailModeReason));
        Choice("guardrail.scanner_mode", "local=regex/judge; remote=Cisco AI Defense; both=chained.", "local", "remote", "both");
        Add("guardrail.connector", new Entry(_ => "Blank follows claw.mode.", p => new[] { string.Empty }.Concat(SupportedConnectors(p)).ToArray()));
        Hint("guardrail.allow_empty_providers", "Let sidecar boot with no upstream providers.");
        Hint("guardrail.allow_unknown_llm_domains", "Permit unknown LLM-looking hosts.");
        Hint("guardrail.hilt.enabled", "Ask before supported high-risk actions.");
        Choice("guardrail.hilt.min_severity", "Minimum severity for approval prompts.", "HIGH", "MEDIUM", "LOW", "CRITICAL");
        Hint("guardrail.host", "Proxy bind address.");
        Hint("guardrail.port", "Proxy listen port.");
        Hint("guardrail.model", "Legacy upstream model identifier.");
        Hint("guardrail.model_name", "Display name shown to agents.");
        Hint("guardrail.original_model", "Client-visible original model.");
        Hint("guardrail.api_key_env", "Legacy upstream API key env name.");
        Hint("guardrail.api_base", "Legacy upstream API URL.");
        Hint("guardrail.block_message", "Response text returned when blocked.");
        Hint("guardrail.stream_buffer_bytes", "Chunk size for streaming inspection.");
        Hint("guardrail.retain_judge_bodies", "Persist raw judge verdicts locally.");
        Choice("guardrail.detection_strategy", "Global detection strategy.", "regex_only", "regex_judge", "judge_first");
        Choice("guardrail.detection_strategy_prompt", "Prompt override; blank=inherit.", string.Empty, "regex_only", "regex_judge", "judge_first");
        Choice("guardrail.detection_strategy_completion", "Completion override; blank=inherit.", string.Empty, "regex_only", "regex_judge", "judge_first");
        Choice("guardrail.detection_strategy_tool_call", "Tool-call override; blank=inherit.", string.Empty, "regex_only", "regex_judge", "judge_first");
        Hint("guardrail.rule_pack_dir", "Path to active rule pack.");
        Hint("guardrail.judge_sweep", "Judge all requests in regex_only mode.");
        Hint("guardrail.judge.enabled", "Enable LLM-as-judge scanner.");
        Hint("guardrail.judge.model", "Legacy judge model id.");
        Hint("guardrail.judge.api_key_env", "Legacy judge API key env.");
        Hint("guardrail.judge.api_base", "Legacy judge API base URL.");
        Hint("guardrail.judge.timeout", "Seconds to wait for one judge call.");
        Hint("guardrail.judge.adjudication_timeout", "Total judge fallback budget.");
        Hint("guardrail.judge.fallbacks", "CSV of backup judge models.");
        Hint("guardrail.judge.injection", "Detect prompt injection.");
        Hint("guardrail.judge.exfil", "Detect data exfiltration attempts.");
        Hint("guardrail.judge.pii", "Master PII toggle.");
        Hint("guardrail.judge.pii_prompt", "Flag PII on inbound prompts.");
        Hint("guardrail.judge.pii_completion", "Flag PII on completions.");
        Hint("guardrail.judge.tool_injection", "Detect payloads in tool-call args.");

        // ---- Guardrail, one connector's overrides: _per_connector_guardrail_fields, setup.py:5688-5834
        Add("guardrail.connectors.*.mode", new Entry(c => $"Per-connector mode for {c[0]} (blank inherits the global mode).", _ => new[] { "observe", "action" }));
        Add("guardrail.connectors.*.rule_pack_dir", new Entry(c => $"Per-connector rule pack for {c[0]} (blank inherits the global pack)."));
        Add("guardrail.connectors.*.enabled", new Entry(c => $"Per-connector guardrail switch for {c[0]} (off tears down its hooks; on by default)."));
        Add("guardrail.connectors.*.hook_fail_mode", new Entry(_ => string.Empty, ReadOnlyReason: HookFailModeReason));
        Add("guardrail.connectors.*.hilt.enabled", new Entry(c => $"Ask before supported high-risk actions for {c[0]}."));
        Add("guardrail.connectors.*.hilt.min_severity", new Entry(c => $"Minimum severity for {c[0]} approval prompts.", _ => new[] { "HIGH", "MEDIUM", "LOW", "CRITICAL" }));
        Add("guardrail.connectors.*.block_message", new Entry(c => $"Per-connector block message for {c[0]} (blank inherits the global message)."));

        // ---- The LLM override block of a component: _llm_override_fields, setup.py:6415-6429
        foreach (var prefix in new[] { "guardrail.llm", "guardrail.judge.llm", "scanners.skill_scanner.llm", "scanners.mcp_scanner.llm", "scanners.plugin_llm" })
        {
            ChoiceOf(prefix + ".provider", "Blank inherits Unified LLM.", LlmOverrideProviders);
            Hint(prefix + ".model", "Blank inherits Unified LLM model.");
            Hint(prefix + ".api_key_env", "Env var NAME for this component.");
            Hint(prefix + ".api_key", "Inline component key.");
            Hint(prefix + ".base_url", "Optional local/proxy endpoint.");
            Hint(prefix + ".timeout", "Per-request timeout.");
            Hint(prefix + ".max_retries", "Retry count.");
        }

        // ---- Scanners: _scanners_section, setup.py:5962-6018 (skill_scanner.policy is not curated: see the class remarks)
        Hint("scanners.skill_scanner.binary", "Path/name of skill-scanner executable.");
        Hint("scanners.skill_scanner.policy", "Skill scanner policy.");
        Hint("scanners.skill_scanner.lenient", "Downgrade findings by one severity.");
        Hint("scanners.skill_scanner.use_llm", "Enable LLM-assisted classification.");
        Hint("scanners.skill_scanner.llm_consensus_runs", "Number of LLM votes.");
        Hint("scanners.skill_scanner.use_behavioral", "Run behavioral analysis.");
        Hint("scanners.skill_scanner.enable_meta", "Scan skill metadata.");
        Hint("scanners.skill_scanner.use_trigger", "Enable trigger-word heuristics.");
        Hint("scanners.skill_scanner.use_virustotal", "Submit artifact hashes.");
        Hint("scanners.skill_scanner.virustotal_api_key_env", "Env var NAME for VirusTotal key.");
        Hint("scanners.skill_scanner.virustotal_api_key", "Inline VirusTotal key.");
        Hint("scanners.skill_scanner.use_aidefense", "Chain Cisco AI Defense scan.");
        Hint("scanners.mcp_scanner.binary", "Path/name of mcp-scanner executable.");
        Hint("scanners.mcp_scanner.analyzers", "CSV of analyzer IDs.");
        Hint("scanners.mcp_scanner.scan_prompts", "Scan MCP prompt templates.");
        Hint("scanners.mcp_scanner.scan_resources", "Scan MCP resource contents.");
        Hint("scanners.mcp_scanner.scan_instructions", "Scan server instructions.");
        Hint("scanners.plugin_scanner", "Command to scan connector plugins.");
        Hint("scanners.codeguard", "Command for CodeGuard skill.");

        // ---- AI Discovery, watchers, Watch, OpenShell, Cisco AI Defense (setup.py:6021-6143, 6523-6530)
        Hint("ai_discovery.enabled", "Run AI discovery service.");
        Hint("ai_discovery.mode", "passive or enhanced.");
        Hint("ai_discovery.scan_interval_min", "Minutes between full scans.");
        Hint("ai_discovery.process_interval_s", "Seconds between process scans.");
        Hint("ai_discovery.scan_roots", "CSV roots for artifact scans.");
        Hint("ai_discovery.signature_packs", "CSV custom signature packs.");
        Hint("ai_discovery.allow_workspace_signatures", "Allow workspace signatures.");
        Hint("ai_discovery.disabled_signature_ids", "CSV signature IDs to suppress.");
        Hint("ai_discovery.include_shell_history", "Match known AI command patterns.");
        Hint("ai_discovery.include_package_manifests", "Detect AI SDK dependencies.");
        Hint("ai_discovery.include_env_var_names", "Detect env var names only.");
        Hint("ai_discovery.include_network_domains", "Detect provider domains.");
        Hint("ai_discovery.max_files_per_scan", "Max files per scan.");
        Hint("ai_discovery.max_file_bytes", "Skip larger files.");
        Hint("ai_discovery.store_raw_local_paths", "Store raw paths locally only.");
        Hint("gateway.watcher.enabled", "Master switch for all watchers.");
        Hint("gateway.watcher.skill.enabled", "Watch skill directories.");
        Hint("gateway.watcher.skill.take_action", "Re-apply enforcement on changes.");
        Hint("gateway.watcher.skill.dirs", "CSV extra skill directories.");
        Hint("gateway.watcher.plugin.enabled", "Watch plugin_dir.");
        Hint("gateway.watcher.plugin.take_action", "Re-apply enforcement.");
        Hint("gateway.watcher.plugin.dirs", "CSV extra plugin directories.");
        Hint("gateway.watcher.mcp.take_action", "Re-apply enforcement on MCP config changes.");
        Hint("watch.debounce_ms", "Milliseconds to wait for edits to settle.");
        Hint("watch.auto_block", "Block high findings automatically.");
        Hint("watch.allow_list_bypass_scan", "Skip allow-listed rescans.");
        Hint("watch.rescan_enabled", "Periodically re-scan installed artifacts.");
        Hint("watch.rescan_interval_min", "Minutes between rescans.");
        Hint("openshell.binary", "Path to openshell executable.");
        Hint("openshell.policy_dir", "OpenShell policy YAML directory.");
        Choice("openshell.mode", "docker, standalone, or blank auto-detect.", string.Empty, "docker", "standalone");
        Hint("openshell.version", "Pinned OpenShell version.");
        Hint("openshell.sandbox_home", "Root of per-sandbox state.");
        Hint("openshell.auto_pair", "Blank=default true.");
        Hint("openshell.host_networking", "Blank=default false.");
        Hint("cisco_ai_defense.endpoint", "Cisco AI Defense API endpoint.");
        Hint("cisco_ai_defense.api_key", "Inline Cisco key.");
        Hint("cisco_ai_defense.api_key_env", "Env var NAME holding Cisco key.");
        Hint("cisco_ai_defense.timeout_ms", "HTTP timeout for probes.");
        Hint("cisco_ai_defense.enabled_rules", "CSV cloud rules.");

        // ---- Asset policy: _asset_policy_fields, setup.py:6146-6310
        Hint("asset_policy.enabled", "Master asset admission switch.");
        Choice("asset_policy.mode", "observe=log; action=block.", "observe", "action");
        foreach (var asset in new[] { "skill", "mcp", "plugin" })
        {
            Choice($"asset_policy.{asset}.default", "Fallback action.", "allow", "deny");
            Hint($"asset_policy.{asset}.registry_required", "Require approved registry entry.");
            Choice($"asset_policy.{asset}.registry_empty_action", "Behavior when registry required but empty.", "deny", "allow");
        }

        Hint("asset_policy.mcp.runtime_detection.enabled", "Detect runtime MCP usage.");
        Hint("asset_policy.mcp.runtime_detection.terminal_commands", "Inspect terminal command surfaces.");
        Choice("asset_policy.mcp.runtime_detection.unknown_terminal_mcp", "Unknown MCP posture.", "observe", "action");

        Add("asset_policy.connectors.*.mode", new Entry(
            c => $"Per-connector asset-policy mode for {c[0]}; blank inherits the global mode.",
            _ => new[] { string.Empty, "observe", "action" }));

        static string AssetLabel(string asset) => asset == "mcp" ? "MCP" : char.ToUpperInvariant(asset[0]) + asset[1..];
        Add("asset_policy.connectors.*.*.default", new Entry(
            c => $"{AssetLabel(c[1])} override for {c[0]}; blank inherits the global {c[1]} policy.",
            _ => new[] { string.Empty, "allow", "deny" }));
        Add("asset_policy.connectors.*.*.registry_required", new Entry(
            c => $"{AssetLabel(c[1])} override for {c[0]}; blank inherits the global {c[1]} policy."));
        Add("asset_policy.connectors.*.*.registry_empty_action", new Entry(
            c => $"{AssetLabel(c[1])} override for {c[0]}; blank inherits the global {c[1]} policy.",
            _ => new[] { string.Empty, "deny", "warn", "allow", "block" }));

        // ---- The severity matrices: action_matrix_fields, setup.py:1628-1670
        foreach (var matrix in new[] { "skill_actions", "mcp_actions", "plugin_actions" })
        {
            foreach (var severity in new[] { "critical", "high", "medium", "low", "info" })
            {
                var upper = severity.ToUpperInvariant();
                Choice($"{matrix}.{severity}.file", $"On {upper}: quarantine moves the artifact; none leaves it in place.", "none", "quarantine");
                Choice($"{matrix}.{severity}.runtime", $"On {upper}: disable stops runtime invocation; enable keeps it live.", "enable", "disable");
                Choice($"{matrix}.{severity}.install", $"On {upper}: block rejects installs; allow permits; none defers.", "none", "block", "allow");
            }
        }

        return (exact, patterns);
    }
}
