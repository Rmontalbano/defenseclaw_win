using System.Text.Json;
using DefenseClaw.App.ViewModels.ConfigEditor;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.Tests.TestSupport;

/// <summary>
/// Synthetic dumps of the runtime's config catalog (CUST-391) in the shape the dump script prints (<see cref="RuntimeConfigCatalog.DumpScript"/>, the
/// Mac's <c>DynamicConfigCatalog</c> JSON): the 24 sections of 1.0.4's <c>build_setup_sections</c> in its order (<c>tui/panels/setup.py:1786-2021</c>),
/// each with a few of its real rows (labels, dotted keys, kinds, choices and hints as 1.0.4 writes them) and made-up values. Nothing here was produced
/// by running a runtime, and nothing in it is a real credential, host or account.
/// </summary>
internal static class RuntimeCatalogFixtures
{
    /// <summary>A "secret" the dump must never let through, put on a password row as a runtime that forgot to blank it would.</summary>
    public const string LeakedSecret = "sk-leaked-not-a-real-key-0000";

    /// <summary>The webhook row's secret path; the editor shows the host only.</summary>
    public const string WebhookSecretPath = "T000/B000/not-a-real-webhook-secret";

    /// <summary>The 1.0.4 section names, in the runtime's order.</summary>
    public static readonly string[] SectionNames =
    {
        "General", "Agent", "Notifications", "Claw", "Agent Hooks", "Connector Hooks", "Gateway", "Guardrail", "Scanners", "Asset Policy", "AI Discovery",
        "Gateway Watcher", "Gateway Watchdog", "Observability", "Webhooks", "Skill Admission", "MCP Admission", "Plugin Admission", "Watch", "OpenShell",
        "Inspect LLM (legacy - read-only)", "Cisco AI Defense", "Firewall", "Trusted Paths",
    };

    private static readonly string[] Severities = { string.Empty, "CRITICAL", "HIGH", "MEDIUM", "LOW", "INFO" };

    private static object Row(string label, string key = "", string kind = "string", string value = "", string[]? options = null, string hint = "") =>
        new { label, key, kind, value, options = options ?? Array.Empty<string>(), hint };

    private static object Separator(string name) => Row(".. " + name + " ..", kind: "header");

    private static object Section(string name, string summary, string help, params object[] fields) => new { name, summary, help, fields };

    /// <summary>The 1.0.4 catalog as the dump prints it, sentinel and all.</summary>
    public static string Dump104()
    {
        var sections = new object[]
        {
            Section(
                "General",
                "Global paths, environment label, and the shared LLM key fallback.",
                "Config Version is read-only; edit unified LLM fields here instead of legacy inspect_llm.",
                Row("Config Version", "config_version", "header", "9"),
                Separator("Paths"),
                Row("Data Dir", "data_dir", value: @"C:\Users\operator\.defenseclaw", hint: "Root directory for DefenseClaw state."),
                Row("Environment", "environment", value: "windows", hint: "Free-form deployment label."),
                Separator("Unified LLM (for scanners + guardrail)"),
                Row("Provider", "llm.provider", "choice", "openai", new[] { "anthropic", "openai", "azure", "ollama" }, "LLM provider family."),
                Row("Model", "llm.model", value: "gpt-4o", hint: "Model identifier."),
                Row("API Key Env", "llm.api_key_env", value: "OPENAI_API_KEY", hint: "Env var NAME holding the unified key."),
                Row("API Key (redacted)", "llm.api_key", "password", LeakedSecret, hint: "Inline key; prefer API Key Env."),
                Row("Base URL", "llm.base_url", value: "https://operator:pw@proxy.example.test/v1?key=abc", hint: "Override provider base URL."),
                Row("Timeout (s)", "llm.timeout", "int", "30", hint: "Per-request timeout in seconds.")),
            Section(
                "Agent",
                "Logical agent identity used for aggregation, webhooks, and enterprise reporting.",
                string.Empty,
                Row("Agent ID", "agent.id", hint: "Stable lower-kebab-case identity."),
                Row("Agent Name", "agent.name", hint: "Human-readable display name.")),
            Section(
                "Notifications",
                "Native Windows desktop/toast notifications are unsupported; delivery is inactive.",
                "Restart the gateway after editing; the dispatcher snapshots config at boot.",
                Row("Enabled (native desktop unsupported on Windows)", "notifications.enabled", "header", "false", hint: "Read-only legacy setting; Windows toast delivery is inactive."),
                Separator("Categories"),
                Row("Block (enforced)", "notifications.block_enforced", "bool", "true", hint: "Toast when a request is actually denied.")),
            Section(
                "Claw",
                "Legacy single-agent setting (claw.mode).",
                string.Empty,
                Row("Mode", "claw.mode", "choice", "codex", new[] { "codex", "claudecode" }, "Legacy single-agent setting; set up each connector in Setup tasks instead.")),
            Section(
                "Agent Hooks",
                "Dedicated agent hook policy: when scans run, fail behavior, and watched paths.",
                string.Empty,
                Row("Claude Code", "claude_code", "header", "read-only", hint: "Set by defenseclaw setup claude-code")),
            Section(
                "Connector Hooks",
                "Advanced connector_hooks map for configured and future agent connectors.",
                string.Empty,
                Row("Codex", "connector_hooks.codex", "header", "read-only", hint: "Set by defenseclaw setup codex")),
            Section(
                "Gateway",
                "Sidecar WebSocket gateway: connection settings, TLS/auth, API bind, reconnect tuning.",
                string.Empty,
                Row("Host", "gateway.host", value: "127.0.0.1", hint: "Where clients reach the gateway."),
                Row("Port", "gateway.port", "int", "18789", hint: "WebSocket port."),
                Row("API Port", "gateway.api_port", "int", "18970", hint: "REST sidecar port."),
                Row("TLS", "gateway.tls", "bool", "false", hint: "Force wss:// and cert validation."),
                Row("Token Env", "gateway.token_env", value: "DEFENSECLAW_GATEWAY_TOKEN", hint: "Env var NAME holding gateway auth token."),
                Row("Token (redacted)", "gateway.token", "password", LeakedSecret, hint: "Inline gateway token.")),
            Section(
                "Guardrail",
                "LLM guardrail proxy: mode, detection strategy, judge and per-connector overrides.",
                string.Empty,
                Separator("Core"),
                Row("Enabled", "guardrail.enabled", "bool", "true", hint: "Master guardrail switch."),
                Row("Mode", "guardrail.mode", "choice", "observe", new[] { "observe", "action" }, "observe=log only; action=block."),
                Row("Hook Fail Mode", "guardrail.hook_fail_mode", "header", "open")),
            Section(
                "Scanners",
                "Skill/MCP/Plugin scanner binaries and behavior flags.",
                string.Empty,
                Separator("Skill Scanner"),
                Row("Block At", "scanners.skill_scanner.fail_on_severity", "choice", string.Empty, Severities, "Findings at or above this severity block (empty: HIGH)."),
                Row("Review From", "scanners.skill_scanner.review_queue_min", "choice", string.Empty, Severities, "Findings from here up to Block At go to review (empty: MEDIUM)."),
                Row("Judge Source", "scanners.skill_scanner.judge_source", "choice", string.Empty, new[] { string.Empty, "inherit", "override" }, "inherit: the top-level llm block; override: the LLM override below."),
                Row("Use OSV", "scanners.skill_scanner.analyzers.osv.enabled", "bool", "false", hint: "Optional: check dependencies on OSV.dev.")),
            Section("Asset Policy", "Registry requirements and default allow/deny behavior.", string.Empty, Row("Enabled", "asset_policy.enabled", "bool", "true", hint: "Master asset admission switch.")),
            Section("AI Discovery", "Continuous local discovery for supported and shadow AI usage.", string.Empty, Row("Enabled", "ai_discovery.enabled", "bool", "true", hint: "Run AI discovery service.")),
            Section("Gateway Watcher", "Filesystem watcher that auto-scans assets as they appear.", string.Empty, Row("Enabled", "gateway.watcher.enabled", "bool", "true", hint: "Master switch for all watchers.")),
            Section("Gateway Watchdog", "Health-check loop that restarts the gateway process when it becomes unresponsive.", string.Empty, Row("Enabled", "gateway.watchdog.enabled", "bool", "false", hint: "Turn the watchdog on/off.")),
            Section(
                "Observability",
                "Telemetry collection, retention, routing, and per-route redaction policy.",
                "Read-only effective plan; press E to manage destinations through setup observability.",
                Row("Status", "observability.status", "header", RuntimeConfigCatalog.ObservabilityNote),
                Row("How to edit", "observability.hint", "header", "press E to manage destinations; collection, routes, redaction, and retention live in config.yaml")),
            Section(
                "Webhooks",
                "Read-only notifier webhook summary.",
                string.Empty,
                Row("ops-alerts", "webhooks.0", "header", @"\[enabled] https://hooks.example.test/services/" + WebhookSecretPath),
                Row("How to edit", "webhooks.hint", "header", "press [E] for interactive editor, or run defenseclaw setup webhook ...")),
            Section("Skill Admission", "Skill admission action per severity.", string.Empty, Row("CRITICAL install", "skill_actions.critical.install", "choice", "block", new[] { "none", "block", "allow" }, "On CRITICAL: block rejects installs; allow permits; none defers.")),
            Section("MCP Admission", "MCP admission action per severity.", string.Empty, Row("CRITICAL install", "mcp_actions.critical.install", "choice", "block", new[] { "none", "block", "allow" })),
            Section("Plugin Admission", "Plugin admission action per severity.", string.Empty, Row("CRITICAL install", "plugin_actions.critical.install", "choice", "block", new[] { "none", "block", "allow" })),
            Section("Watch", "File watch debounce and rescan behavior.", string.Empty, Row("Debounce (ms)", "watch.debounce_ms", "int", "500", hint: "Milliseconds to wait for edits to settle.")),
            Section("OpenShell", "OpenShell sandbox runtime.", string.Empty, Row("Mode", "openshell.mode", "choice", string.Empty, new[] { string.Empty, "docker", "standalone" }, "docker, standalone, or blank auto-detect.")),
            Section("Inspect LLM (legacy - read-only)", "Deprecated v4 block. Edit the Unified LLM section instead.", string.Empty, Row("Provider", kind: "header", value: "(unset)")),
            Section(
                "Cisco AI Defense",
                "Cloud-hosted prompt/response moderation.",
                string.Empty,
                Row("Endpoint", "cisco_ai_defense.endpoint", value: "https://aidefense.example.test", hint: "Cisco AI Defense API endpoint."),
                Row("API Key (redacted)", "cisco_ai_defense.api_key", "password", LeakedSecret, hint: "Inline Cisco key.")),
            Section("Firewall", "Host firewall anchor paths. Read-only in the TUI.", string.Empty, Row("Config File", "firewall.config_file", "header", @"C:\Users\operator\.defenseclaw\firewall.yaml")),
            Section(
                "Trusted Paths",
                "Binary locations trusted for connector discovery. Read-only here; manage via 'defenseclaw setup trusted-paths'.",
                string.Empty,
                Row("Built-in defaults", "trusted_paths.defaults", "header", "12 default prefixes, 3 present on this host")),
        };

        return "a Python warning the editor must skip\n" + RuntimeConfigCatalog.Sentinel + JsonSerializer.Serialize(sections) + "\n";
    }

    /// <summary>A runner that answers every start with <paramref name="stdout"/> and exit 0, and remembers the launches it was given. No process starts.</summary>
    public static (RuntimePythonRunner Runner, List<RuntimePythonLaunch> Launches) Answering(string stdout)
    {
        var launches = new List<RuntimePythonLaunch>();
        RuntimePythonRunner runner = (launch, _) =>
        {
            lock (launches)
            {
                launches.Add(launch);
            }

            return Task.FromResult(new RuntimePythonOutput(0, stdout, null));
        };
        return (runner, launches);
    }

    /// <summary>A runner whose every start ends as <paramref name="output"/> (a timeout, a non-zero exit).</summary>
    public static RuntimePythonRunner Ending(RuntimePythonOutput output) => (_, _) => Task.FromResult(output);

    /// <summary>
    /// Puts a <c>defenseclaw.exe</c> and a <c>python.exe</c> (empty files, not programs) in the bin folder an isolated composition looks in, so the CLI is
    /// found and the interpreter beside it is the catalog's (the venv layout). Nothing ever starts either: the catalog runner is a fixture.
    /// </summary>
    public static void PlantCliAndPython(string dataDirectory)
    {
        var bin = Path.Combine(dataDirectory, "no-such-bin");
        _ = Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "defenseclaw.exe"), "this is not a program");
        File.WriteAllText(Path.Combine(bin, "python.exe"), "this is not a program");
    }

    /// <summary>
    /// A probe runner that says "1.0.4" to <c>--version-json</c> and answers the help screens from the pinned-source fixtures (a runtime needs a command
    /// list to be known), so the label carries a version.
    /// </summary>
    public static RuntimeProbeRunner Version104 { get; } = (arguments, token) =>
        arguments is ["--version-json"]
            ? Task.FromResult(RuntimeProbeOutput.Ok("{\"name\":\"defenseclaw-cli\",\"schema_version\":1,\"version\":\"1.0.4\"}"))
            : RuntimeFixtureRunner.For("95159fd")(arguments, token);
}
