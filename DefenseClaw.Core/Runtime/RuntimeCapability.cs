namespace DefenseClaw.Core.Runtime;

/// <summary>
/// A feature the app shows only when the connected DefenseClaw runtime has it. Every one is something the installed 0.8.10
/// runtime does not have and the verified newer runtime (source commit <see cref="RuntimeCapabilityCatalog.VerifiedCommit"/>) does;
/// a feature 0.8.10 already has is never gated, so nothing here can hide what works today.
/// </summary>
public enum RuntimeCapability
{
    /// <summary>The seven-view policy model: opt-in protection packs, scope postures, rule families and sandbox packs.</summary>
    PolicyModel,

    /// <summary>The Agent Client Protocol guard: <c>acp setup</c>, <c>acp adopt</c> and friends.</summary>
    AcpGuard,

    /// <summary>The advanced redaction editor: <c>setup redaction</c> with its bucket, defaults, destination, profile and route operations.</summary>
    RedactionAdvanced,

    /// <summary>The OpenShell sandbox CLI and the gateway's <c>/api/v1/sandbox/*</c> routes (the runtime says it targets Linux and macOS).</summary>
    Sandbox,

    /// <summary>
    /// The canonical observability configuration (introduced with config schema 8): the app can read and edit it. It says nothing about the
    /// version of the config file on disk (that is <c>config_version</c> in config.yaml, 9 on a 1.0.x machine).
    /// </summary>
    CanonicalSchema8,

    /// <summary>
    /// The larger TUI command registry: the connectors and runtime planes added since 0.8.10 (253 entries at the pin, 232 of them run on
    /// Windows, against 0.8.10's 231 and 210). It is what chooses the command palette's catalogue (<see cref="Cli.TuiRegistryCatalogues.For"/>).
    /// </summary>
    TuiRegistry,

    /// <summary>
    /// The AI Discovery runtime planes: <c>agent discovery runtime</c> (status, scan, findings, permissions, enable, disable) and the
    /// gateway's <c>GET /api/v1/ai-usage/runtime</c>. 0.8.10 has neither the command group nor the route.
    /// </summary>
    AiRuntime,

    /// <summary>
    /// The gateway's read-only correlation ledger (<c>GET /api/v1/correlation/{graph,explain,timeline,conflicts}</c>) and agent directory
    /// (<c>GET /api/v1/agents/identities</c>), which the Agents panel reads. Judged from the runtime's version (1.0.0 or later, the source the panel's
    /// shapes were written from); the panel itself still treats a 404 or 405 from the gateway as "not supported", so a version label alone never
    /// makes it look populated.
    /// </summary>
    CorrelationApi,

    /// <summary>
    /// The gateway's read-only IDE plugin inventory (<c>GET /api/v1/ai-usage/ide-plugins</c>), which the AI BOM's IDE Plugins tab reads, next to the agent
    /// directory (<c>GET /api/v1/agents/identities</c>) its Agent identities tab reads. Both routes arrived in 1.0.0 and the gateway does not register
    /// either on a managed Secure Client host, so the version only decides whether to ask; a 404 or 405 is still "not available on this host".
    /// </summary>
    IdeInventory,

    /// <summary>
    /// The gateway's administrator view of the policy it applies: <c>GET /api/v1/guardrail/profiles/resolve</c> (the Overview's Effective policy card,
    /// CUST-375). Judged from the runtime's version like <see cref="CorrelationApi"/>; the card itself still treats a 404 or 405 as "not supported".
    /// </summary>
    EffectivePolicy,

    /// <summary>
    /// The runtime's canonical alert list: <c>defenseclaw alerts --json</c> (1.0.0 or later), which lists enforcement actions, connector hook
    /// blocks and health failures as alerts, leaves detection-only findings out, and names each alert's decision, rule and agent. The Alerts panel
    /// merges it into the audit-database queue; 0.8.10 has no <c>--json</c> and keeps reading the database alone.
    /// </summary>
    RuntimeAlerts,

    /// <summary>
    /// The runtime that removed the OpenHands and OmniGent connectors (1.0.4): <c>setup openhands</c> and <c>setup omnigent</c> are gone, the TUI registry
    /// lists neither, and <c>status --json</c> may carry <c>removed_connector_cleanup</c>. Judged from the runtime's version (1.0.4 or later); older
    /// runtimes, and one that has not answered, keep both connectors exactly as they were.
    /// </summary>
    ConnectorsRetired,

    /// <summary>
    /// The per-user hook rules of 1.0.4: a per-user host's gateway-outage fail mode follows the connector's policy tier (closed for a strict rule pack,
    /// open otherwise), a saved per-user <c>hook_fail_mode</c> is not applied (until 1.0.5, see <see cref="SavedFailModeWins"/>), and an <c>.install.lock</c> an interrupted install left behind keeps
    /// every per-user hook blocking (the hooks of 1.0.3 and earlier never read it). Judged from the runtime's version (1.0.4 or later).
    /// </summary>
    PerUserHookRules,

    /// <summary>
    /// <c>guardrail use-pack PACK --repin --yes --json</c> (1.0.4): replaces the pinned digest of a custom rule pack that was edited after it was
    /// pinned. 1.0.3 and earlier have neither flag. Judged from the runtime's version (1.0.4 or later); the CLI's own "No such option" is still
    /// treated as "not supported" by the review that runs it.
    /// </summary>
    GuardrailRepin,

    /// <summary>
    /// The gateway applies a config.yaml change in place for every key outside its restart set (<c>restartKeys</c>, config_writer.py and configwrite.go), so a saved
    /// change to most settings needs no restart. Present since 1.0.0 (judged from the version, like <see cref="CorrelationApi"/>); 0.8.10 reads config.yaml only
    /// at start, and an unknown runtime is treated the same, so the app keeps predicting a restart for every change there.
    /// </summary>
    GatewayConfigReload,

    /// <summary>
    /// The config keys 1.0.4 added or changed that the editor shows only for it: <c>scanners.mcp_scanner.unverified_server</c> (warn or block), <c>openshell.trusted_ca_bundle</c>,
    /// the read-only <c>local_stack</c> record on a local Splunk destination, and the notes that severities are read in any case and that profile assignments combine
    /// to the strictest. Judged from the version (1.0.4 or later).
    /// </summary>
    Config104Keys,

    /// <summary>
    /// The fail-mode rule of 1.0.5 (CUST-397): on a per-user host an explicitly saved <c>hook_fail_mode</c> (the connector's own, else the global one) decides what a
    /// hook does while the gateway is down, and only when nothing is saved does the policy tier decide (Go <c>HookOutageFailMode</c>,
    /// <c>config.py</c> <c>effective_hook_fail_mode</c>). Only <c>guardrail fail-mode</c>, <c>config set</c>, an explicit <c>--fail-mode</c> and a typed wizard answer
    /// save one; setup, init and adding a connector no longer do. Judged from the version (1.0.5 or later); 1.0.4 keeps <see cref="PerUserHookRules"/>' reading.
    /// </summary>
    SavedFailModeWins,

    /// <summary>
    /// The self-protection of 1.0.5: observe mode hard-blocks a proven change to DefenseClaw's own active state (<c>provenActiveStateMutation</c> in
    /// agent_hook.go; through 1.0.8 the permissive pack only reported it, and from 1.0.9 it blocks there too, <c>selfProtectionRules</c> in
    /// shell_protected_file_profile.go@1.0.9:69-77), and a call the gateway took but did not answer in time is blocked in every tier. So copy that
    /// says observe, or fail-mode open, "never blocks" is wrong from this release on. Judged from the runtime's version (1.0.5 or later).
    /// </summary>
    ObserveBlocksSelfChange,

    /// <summary>
    /// ACP guard health that can be believed (1.0.5): <c>acp status --json</c> and <c>acp verify</c> report a binding healthy only for a lock and agent the guard
    /// starts, and name the editor entries that start the guard with the lock (cmd_acp.py <c>_verify_binding</c>, <c>_pinned_entry_problem</c>). The ACP Guard
    /// window shows the read-only status on it; 1.0.4 and earlier keep the wizard alone. Judged from the version (1.0.5 or later).
    /// </summary>
    AcpGuardHealth,

    /// <summary>
    /// Codex setup refuses a <c>.codex</c> home that is a symbolic link or junction before it changes anything (1.0.5, <c>codexHomeLinkRefusal</c>), so the
    /// Codex wizard can say so before its review. Judged from the version (1.0.5 or later).
    /// </summary>
    CodexHomeLinkRefusal,

    /// <summary>
    /// The install-lock rule of 1.0.5: a per-user hook ignores an <c>.install.lock</c> whose installer is proven gone (<c>installLockStale</c>,
    /// hookexec/install_lock_state.go), and <c>doctor --fix</c> removes such a lock (<c>doctor.install.stale-lock.clear</c>). A lock an interrupted install
    /// keeps (a recovery record beside it) still waits for the installer. Judged from the runtime's version (1.0.5 or later).
    /// </summary>
    StaleInstallLockIgnored,

    /// <summary>
    /// The protected-file watch of 1.0.6 (internal/protectedfiles, internal/gateway/protected_files.go): a per-user gateway raises a HIGH
    /// <c>protected-file-changed</c> alert for a change to a short list of files while an agent tool call runs, and <c>guardrail.protected_files</c> is in
    /// its config schema. A release before 1.0.6 refuses a config that sets that key. A managed gateway has no watch, whatever its version, so this answers
    /// "does this runtime know the setting and the alert", not "is something watching". Judged from the runtime's version (1.0.6 or later).
    /// </summary>
    ProtectedFileWatch,

    /// <summary>
    /// OpenCode V2 is the only OpenCode DefenseClaw guards (1.0.6, <c>RequireOpenCodeV2</c> in internal/gateway/connector/opencode_v2.go): setup and the gateway refuse
    /// V1 and any other major, and doctor fails the OpenCode hooks row while the account runs V1. A release before 1.0.6 guards V1 only, loads no V2 plugin, and still
    /// reports OpenCode protected. Judged from the runtime's version (1.0.6 or later).
    /// </summary>
    OpenCodeV2Only,

    /// <summary>
    /// The decision model can be the only AI check for chosen hook connectors (1.0.7): <c>setup decision-model --connectors</c> and <c>decision_model.hook_connectors</c>. A release before 1.0.7 refuses the key. Judged from the runtime's version (1.0.7 or later).
    /// </summary>
    DecisionModelCascade,

    /// <summary>
    /// Retention warns when a completed pass leaves the audit store above its size cap (1.0.7): <c>retention_state</c> <c>warning</c> with the used bytes, the cap and a reason in /health. Judged from the runtime's version (1.0.7 or later).
    /// </summary>
    AuditStoreCapWarning,

    /// <summary>
    /// The gateway names the connectors it did not start and why (1.0.7): <c>connectors_not_started</c> and <c>connectors_refused_by_agent_settings</c> in the guardrail details of /health. Judged from the runtime's version (1.0.7 or later).
    /// </summary>
    AgentNotInstalledSkip,

    /// <summary>
    /// A call the hook refused on its own, with no gateway decision, is written to the audit trail as a block row (1.0.7). A release before 1.0.7 records none. Judged from the runtime's version (1.0.7 or later).
    /// </summary>
    HookFailureBlockRows,

    /// <summary>
    /// Under <c>regex_judge</c> a CRITICAL detection-only finding goes to the LLM judge, and a missing verdict follows <c>hook_fail_mode</c> (1.0.7, finding <c>JUDGE-UNAVAILABLE-ESCALATED</c>). Judged from the runtime's version (1.0.7 or later).
    /// </summary>
    JudgeEscalation,

    /// <summary>
    /// The per-user <c>defenseclaw.exe</c> is a native launcher for the bundled CLI (1.0.7); a damaged install or a linked home folder now fails with the launcher's own message. Judged from the runtime's version (1.0.7 or later).
    /// </summary>
    PerUserCliLauncher,

    /// <summary>
    /// Claude Cowork is protected as a surface of the Claude Code connector (1.0.8): <c>setup cowork</c>, the <c>cowork</c> object of <c>status --json</c> and <c>connectors[].surfaces.cowork</c> in /health. Judged from the runtime's version (1.0.8 or later).
    /// </summary>
    CoworkProtection,

    /// <summary>
    /// AI Discovery reports Claude Desktop and Cowork (1.0.8), including Claude Desktop on third-party inference. Judged from the runtime's version (1.0.8 or later).
    /// </summary>
    ClaudeDesktopDiscovery,

    /// <summary>
    /// <c>guardrail.judge.extra_body</c> passes extra request fields to an OpenAI-compatible judge (1.0.8). A release before 1.0.8 refuses a config that carries the key. Judged from the runtime's version (1.0.8 or later).
    /// </summary>
    JudgeExtraBody,

    /// <summary>
    /// <c>openshell.middleware.enabled</c> is retired (1.0.8): a config that carries it still loads, and <c>config set</c> refuses it. Judged from the runtime's version (1.0.8 or later).
    /// </summary>
    OpenShellMiddlewareRetired,

    /// <summary>
    /// <c>doctor</c> fails a Windows PowerShell EncodedCommand hook bridge written by an earlier release (1.0.8) and names the <c>setup</c> command that rewrites it. Judged from the runtime's version (1.0.8 or later).
    /// </summary>
    PowerShellBridgeFailClosed,

    /// <summary>
    /// OTLP records are kept through a collector outage and replayed, and /health counts rejections per destination (1.0.8). Judged from the runtime's version (1.0.8 or later).
    /// </summary>
    OtlpReplay,

    /// <summary>
    /// A late or missing LLM judge verdict is written to the hook's audit row as <c>judge_fallback</c> (1.0.8; 1.0.7 counted it in health only). Judged from the runtime's version (1.0.8 or later).
    /// </summary>
    JudgeFallbackOnHookRows,

    /// <summary>
    /// The LLM provider list includes <c>openai-compatible</c> and <c>custom-openai</c> (1.0.8); both need a base URL. Judged from the runtime's version (1.0.8 or later).
    /// </summary>
    OpenAiCompatibleProviders,

    /// <summary>
    /// The gateway skips a gateway token that is an OpenClaw secret reference (<c>{"source": ...}</c> text 1.0.5/1.0.6 could save) instead of using the
    /// text as the bearer, and <c>doctor --fix</c> drops it (1.0.7; config.go and cmd_doctor.py). Judged from the runtime's version (1.0.7 or later).
    /// </summary>
    SecretRefGatewayTokenSkipped,

    /// <summary>
    /// The Overview shows the gateway's runtime-monitoring degraded reasons as information (the app shows them from 1.0.7 on; older runtimes keep the
    /// Overview they had). Judged from the runtime's version (1.0.7 or later).
    /// </summary>
    RuntimeMonitoringDegradedReasons,

    /// <summary>
    /// <c>defenseclaw-gateway connector recover-config</c> finishes an interrupted agent config change (1.0.9); before it, the only way past a stuck receipt was to move it aside. Judged from the runtime's version (1.0.9 or later).
    /// </summary>
    ConnectorRecoverConfig,

    /// <summary>
    /// <c>status --json</c> reports <c>cowork_plugin_stale</c> for a Cowork plugin an older release built (1.0.9); the plugin has to be built and uploaded again. Judged from the runtime's version (1.0.9 or later).
    /// </summary>
    CoworkPluginStale,

    /// <summary>
    /// A Cowork plugin built by 1.0.9 lets Cowork calls run after DefenseClaw's data folder is removed; a plugin an older release built may block them until it is removed in Claude Desktop. Judged from the runtime's version (1.0.9 or later).
    /// </summary>
    CoworkPluginRunsAfterRemoval,

    /// <summary>
    /// A shell command that writes a protected file is at least an alert in every rule pack, the permissive one included, and DefenseClaw's own files block with every pack (1.0.9). Judged from the runtime's version (1.0.9 or later).
    /// </summary>
    ShellProtectedFileFloor,

    /// <summary>
    /// <c>guardrail.judge.max_parallel</c> defaults to 1 for a local judge (1.0.9), so the hint to set it is moot there. Judged from the runtime's version (1.0.9 or later).
    /// </summary>
    JudgeLocalParallelDefault,

    /// <summary>
    /// One dropped host sensor event keeps the host sensor degraded until the gateway restarts (1.0.9); before it, the state cleared by itself. Judged from the runtime's version (1.0.9 or later).
    /// </summary>
    HostSensorDropLatch,

    /// <summary>
    /// <c>setup -c</c>, <c>--detected</c> and <c>--all</c> add or update the connectors they name and keep the rest of the roster (1.0.10). Before it, the same command replaced the whole roster. Judged from the runtime's version (1.0.10 or later).
    /// </summary>
    SetupKeepsRoster,

    /// <summary>
    /// OpenClaw runs on native Windows in native hook mode (1.0.10): <c>setup openclaw</c> is offered there and /health reports <c>openclaw_plugin</c>. Before it, OpenClaw was unsupported on Windows. Judged from the runtime's version (1.0.10 or later).
    /// </summary>
    OpenClawNativeWindows,

    /// <summary>
    /// The OpenClaw gateway token is never the DefenseClaw gateway token (1.0.10): a <c>token_env</c> that names it, or a value equal to it, is skipped. Judged from the runtime's version (1.0.10 or later).
    /// </summary>
    OpenClawTokenSeparate,

    /// <summary>
    /// When an agent updates itself in place the gateway keeps running and leaves only that connector unstarted (1.0.10). Before it, the gateway stopped for every connector. Judged from the runtime's version (1.0.10 or later).
    /// </summary>
    AgentSelfUpdateKeepsGateway,

    /// <summary>
    /// <c>defenseclaw routing</c>, the routing details of /health and the <c>/api/v1/routing</c> reads exist (1.0.10). Judged from the runtime's version (1.0.10 or later).
    /// </summary>
    RoutingCli,

    /// <summary>
    /// Only nine <c>routing</c> keys need a gateway restart; the rest reload in place (1.0.10). Before it, every routing change needed a restart. Judged from the runtime's version (1.0.10 or later).
    /// </summary>
    RoutingHotReload,

    /// <summary>
    /// The gateway can record what each model call it carries used and cost and serves it at <c>/usage</c> (1.0.10); off until set, and only for calls DefenseClaw carries. Judged from the runtime's version (1.0.10 or later).
    /// </summary>
    Tokenomics,

    /// <summary>
    /// <c>setup decision-model</c> takes <c>--protocol</c> (System One or Gemini decisions) and the <c>decide</c> choice of <c>--on-confident-block</c> is retired (1.0.10: still accepted, and ignored; cmd_setup.py@1.0.10:1471-1479). Judged from the runtime's version (1.0.10 or later).
    /// </summary>
    DecisionModelProtocol,

    /// <summary>
    /// <c>guardrail.cascade</c> sets what each guardrail layer does, with an opt-in alignment lane (1.0.10). A proven CRITICAL finding ends at least at alert, <c>ACTION-COMPOUND-LIMIT</c> is the shared over-limit finding and <c>ACTION-OPERAND-BOUND</c> is retired. Judged from the runtime's version (1.0.10 or later).
    /// </summary>
    GuardrailCascade,
}

/// <summary>Names, the verified commit and the one sentence a hidden feature carries.</summary>
public static class RuntimeCapabilityCatalog
{
    /// <summary>The source commit the markers were derived from and checked against.</summary>
    public const string VerifiedCommit = "95159fd";

    /// <summary>
    /// What an unavailable feature says, wherever it says anything: a disabled palette row, a panel's empty state, a Setup tile.
    /// </summary>
    public const string UnsupportedMessage =
        "Requires a compatible DefenseClaw runtime (verified against source commit " + VerifiedCommit + ")";

    /// <summary>Every capability, in the order About lists them.</summary>
    public static IReadOnlyList<RuntimeCapability> All { get; } = Enum.GetValues<RuntimeCapability>();

    /// <summary>The label About shows.</summary>
    public static string DisplayName(RuntimeCapability capability) => capability switch
    {
        RuntimeCapability.PolicyModel => "Policy model (seven views)",
        RuntimeCapability.AcpGuard => "ACP guard",
        RuntimeCapability.RedactionAdvanced => "Advanced redaction",
        RuntimeCapability.Sandbox => "Sandboxes (Linux/macOS only)",
        RuntimeCapability.CanonicalSchema8 => "Canonical observability config editing",
        RuntimeCapability.TuiRegistry => "Extended command registry",
        RuntimeCapability.AiRuntime => "AI Discovery runtime planes",
        RuntimeCapability.CorrelationApi => "Agent view (correlation ledger)",
        RuntimeCapability.IdeInventory => "AI BOM IDE plugins and agent identities",
        RuntimeCapability.EffectivePolicy => "Effective policy (gateway profile resolution)",
        RuntimeCapability.RuntimeAlerts => "Runtime alert list (alerts --json)",
        RuntimeCapability.ConnectorsRetired => "OpenHands and OmniGent removed",
        RuntimeCapability.PerUserHookRules => "Per-user hook rules of 1.0.4",
        RuntimeCapability.GuardrailRepin => "Re-pin an edited guardrail pack",
        RuntimeCapability.GatewayConfigReload => "Gateway hot config reload",
        RuntimeCapability.Config104Keys => "Config keys of 1.0.4",
        RuntimeCapability.SavedFailModeWins => "A saved per-user fail mode wins (1.0.5)",
        RuntimeCapability.ObserveBlocksSelfChange => "Observe blocks changes to DefenseClaw's own files (1.0.5)",
        RuntimeCapability.AcpGuardHealth => "ACP guard status and verify of 1.0.5",
        RuntimeCapability.CodexHomeLinkRefusal => "Codex setup refuses a linked .codex home",
        RuntimeCapability.StaleInstallLockIgnored => "Stale install lock ignored (1.0.5)",
        RuntimeCapability.ProtectedFileWatch => "Protected-file watch (1.0.6)",
        RuntimeCapability.OpenCodeV2Only => "OpenCode V2 guarded, V1 refused (1.0.6)",
        RuntimeCapability.DecisionModelCascade => "Decision model for chosen connectors (1.0.7)",
        RuntimeCapability.AuditStoreCapWarning => "Audit store size-cap warning (1.0.7)",
        RuntimeCapability.AgentNotInstalledSkip => "Connector skipped when its agent is not installed (1.0.7)",
        RuntimeCapability.HookFailureBlockRows => "Hook self-block rows in the audit trail (1.0.7)",
        RuntimeCapability.JudgeEscalation => "CRITICAL pattern findings go to the LLM judge (1.0.7)",
        RuntimeCapability.PerUserCliLauncher => "Native per-user CLI launcher (1.0.7)",
        RuntimeCapability.CoworkProtection => "Claude Cowork protection (1.0.8)",
        RuntimeCapability.ClaudeDesktopDiscovery => "Claude Desktop and Cowork in AI Discovery (1.0.8)",
        RuntimeCapability.JudgeExtraBody => "Extra request fields for the LLM judge (1.0.8)",
        RuntimeCapability.OpenShellMiddlewareRetired => "openshell.middleware.enabled retired (1.0.8)",
        RuntimeCapability.PowerShellBridgeFailClosed => "Doctor fails old PowerShell hook bridges (1.0.8)",
        RuntimeCapability.OtlpReplay => "OTLP records kept through an outage (1.0.8)",
        RuntimeCapability.JudgeFallbackOnHookRows => "judge_fallback on hook audit rows (1.0.8)",
        RuntimeCapability.OpenAiCompatibleProviders => "openai-compatible and custom-openai providers (1.0.8)",
        RuntimeCapability.SecretRefGatewayTokenSkipped => "Secret-reference gateway token skipped (1.0.7)",
        RuntimeCapability.RuntimeMonitoringDegradedReasons => "Runtime monitoring degraded reasons (1.0.7)",
        RuntimeCapability.ConnectorRecoverConfig => "Recover an interrupted agent config change (1.0.9)",
        RuntimeCapability.CoworkPluginStale => "Cowork plugin from an older release reads stale (1.0.9)",
        RuntimeCapability.CoworkPluginRunsAfterRemoval => "Cowork plugin lets calls run after DefenseClaw is removed (1.0.9)",
        RuntimeCapability.ShellProtectedFileFloor => "Shell writes to protected files always alert (1.0.9)",
        RuntimeCapability.JudgeLocalParallelDefault => "A local LLM judge runs one call at a time by default (1.0.9)",
        RuntimeCapability.HostSensorDropLatch => "A dropped host sensor event stays degraded until restart (1.0.9)",
        RuntimeCapability.SetupKeepsRoster => "Connector setup keeps the other connectors (1.0.10)",
        RuntimeCapability.OpenClawNativeWindows => "OpenClaw on native Windows (1.0.10)",
        RuntimeCapability.OpenClawTokenSeparate => "The OpenClaw token is not the DefenseClaw token (1.0.10)",
        RuntimeCapability.AgentSelfUpdateKeepsGateway => "An agent that updated itself no longer stops the gateway (1.0.10)",
        RuntimeCapability.RoutingCli => "Model routing commands and state (1.0.10)",
        RuntimeCapability.RoutingHotReload => "Most routing settings reload without a restart (1.0.10)",
        RuntimeCapability.Tokenomics => "Tokenomics: usage and cost of model calls (1.0.10)",
        RuntimeCapability.DecisionModelProtocol => "Decision model protocol choice (1.0.10)",
        RuntimeCapability.GuardrailCascade => "Configurable guardrail cascade (1.0.10)",
        _ => capability.ToString(),
    };

    /// <summary>
    /// Whether the user can use the feature on this machine. The runtime's sandbox commands exist on Windows but the runtime itself says it
    /// targets Linux and macOS, so a summary of "what you have here" leaves them out.
    /// </summary>
    public static bool IsUsableHere(RuntimeCapability capability) =>
        !(capability == RuntimeCapability.Sandbox && OperatingSystem.IsWindows());

    /// <summary>The marker the probe looks for, in a sentence (About's tooltip and the docs).</summary>
    public static string Marker(RuntimeCapability capability) => capability switch
    {
        RuntimeCapability.PolicyModel => "'guardrail --help' lists 'protection'",
        RuntimeCapability.AcpGuard => "'acp --help' lists 'setup' and 'adopt'",
        RuntimeCapability.RedactionAdvanced => "'setup --help' lists 'redaction'; 'setup redaction --help' lists bucket, defaults, destination, profile and route",
        RuntimeCapability.Sandbox => "'sandbox --help' lists 'pack', 'approvals' and 'doctor'",
        RuntimeCapability.CanonicalSchema8 => "1.0.0 or later: 'config --help' lists 'get'",
        RuntimeCapability.TuiRegistry => "'setup --help' lists 'amp', 'devin' and 'kiro'",
        RuntimeCapability.AiRuntime => "'agent discovery --help' lists 'runtime'; 'agent discovery runtime --help' lists status, scan, findings and permissions",
        RuntimeCapability.CorrelationApi => "1.0.0 or later: the gateway serves /api/v1/correlation/* and /api/v1/agents/identities",
        RuntimeCapability.IdeInventory => "1.0.0 or later: the gateway serves /api/v1/ai-usage/ide-plugins and /api/v1/agents/identities. Not on a managed host.",
        RuntimeCapability.EffectivePolicy => "1.0.0 or later: the gateway serves /api/v1/guardrail/profiles/resolve",
        RuntimeCapability.RuntimeAlerts => "1.0.0 or later: 'alerts --json' exists",
        RuntimeCapability.ConnectorsRetired => "1.0.4 or later: setup openhands and setup omnigent are gone",
        RuntimeCapability.PerUserHookRules => "1.0.4 or later: a saved per-user hook_fail_mode is not applied, and a leftover install lock blocks hooks",
        RuntimeCapability.GuardrailRepin => "1.0.4 or later: 'guardrail use-pack' takes --repin and --yes",
        RuntimeCapability.GatewayConfigReload => "1.0.0 or later: the gateway applies a config.yaml change outside its restart keys in place",
        RuntimeCapability.Config104Keys => "1.0.4 or later: the schema has mcp_scanner.unverified_server, openshell.trusted_ca_bundle and the Splunk local_stack record",
        RuntimeCapability.SavedFailModeWins => "1.0.5 or later: a saved per-user hook_fail_mode wins over the policy tier",
        RuntimeCapability.ObserveBlocksSelfChange => "1.0.5 or later: observe mode blocks a proven change to DefenseClaw's own active state",
        RuntimeCapability.AcpGuardHealth => "1.0.5 or later: 'acp status --json' and 'acp verify' report healthy only for a lock and agent the guard starts",
        RuntimeCapability.CodexHomeLinkRefusal => "1.0.5 or later: setup codex refuses a .codex home that is a link or junction",
        RuntimeCapability.StaleInstallLockIgnored => "1.0.5 or later: hooks ignore an install lock whose installer is gone, and doctor --fix removes it",
        RuntimeCapability.ProtectedFileWatch => "1.0.6 or later: a per-user gateway alerts on an agent's change to a protected file, and guardrail.protected_files is in the schema",
        RuntimeCapability.OpenCodeV2Only => "1.0.6 or later: the gateway and setup guard OpenCode V2 and refuse OpenCode V1. Earlier releases guard V1 only.",
        RuntimeCapability.DecisionModelCascade => "1.0.7 or later: 'setup decision-model' takes --connectors and config accepts decision_model.hook_connectors",
        RuntimeCapability.AuditStoreCapWarning => "1.0.7 or later: /health telemetry reports retention_state 'warning' when the store stays above observability.local.max_store_mb",
        RuntimeCapability.AgentNotInstalledSkip => "1.0.7 or later: /health lists connectors_not_started and connectors_refused_by_agent_settings",
        RuntimeCapability.HookFailureBlockRows => "1.0.7 or later: a call the hook refused during an outage is a 'guardrail-block' row from hook-failures.jsonl",
        RuntimeCapability.JudgeEscalation => "1.0.7 or later: under regex_judge a CRITICAL finding the rules cannot enforce waits for the LLM judge. A missing verdict follows hook_fail_mode with finding JUDGE-UNAVAILABLE-ESCALATED.",
        RuntimeCapability.PerUserCliLauncher => "1.0.7 or later: the per-user defenseclaw.exe is a native launcher that prints its own errors when the install is damaged",
        RuntimeCapability.CoworkProtection => "1.0.8 or later: 'setup cowork' exists, 'status --json' carries a cowork object and /health lists a cowork surface under claudecode",
        RuntimeCapability.ClaudeDesktopDiscovery => "1.0.8 or later: AI Discovery reports Claude Desktop with its deployment (first or third party), Cowork and its plugins",
        RuntimeCapability.JudgeExtraBody => "1.0.8 or later: config accepts guardrail.judge.extra_body",
        RuntimeCapability.OpenShellMiddlewareRetired => "1.0.8 or later: 'config set' refuses openshell.middleware.enabled, and a config that still has it loads and ignores it",
        RuntimeCapability.PowerShellBridgeFailClosed => "1.0.8 or later: doctor fails a hook bridge written as a Windows PowerShell EncodedCommand and names the setup command that rewrites it",
        RuntimeCapability.OtlpReplay => "1.0.8 or later: OTLP records are kept through a collector outage and replayed, and /health counts rejections per destination (counters_scope)",
        RuntimeCapability.JudgeFallbackOnHookRows => "1.0.8 or later: a late or missing LLM judge verdict is written to the hook's audit row as judge_fallback. 1.0.7 counted it in health only.",
        RuntimeCapability.OpenAiCompatibleProviders => "1.0.8 or later: the LLM provider list has openai-compatible and custom-openai, which need a base URL",
        RuntimeCapability.SecretRefGatewayTokenSkipped => "1.0.7 or later: the gateway skips a gateway token that is a secret reference, and doctor --fix drops it",
        RuntimeCapability.RuntimeMonitoringDegradedReasons => "1.0.7 or later: the Overview shows the gateway's runtime-monitoring degraded reasons",
        RuntimeCapability.ConnectorRecoverConfig => "1.0.9 or later: 'connector recover-config' finishes an interrupted agent config change. Do not remove or replay its receipts.",
        RuntimeCapability.CoworkPluginStale => "1.0.9 or later: 'status --json' can report the Cowork reason code cowork_plugin_stale for a plugin an older release built",
        RuntimeCapability.CoworkPluginRunsAfterRemoval => "1.0.9 or later: a Cowork plugin built by this release lets Cowork calls run after the data folder is removed. An older plugin may block them.",
        RuntimeCapability.ShellProtectedFileFloor => "1.0.9 or later: a shell write to SSH authorized keys, a shell startup file or an active Git hook is at least an alert in every rule pack. DefenseClaw's own files block in every pack.",
        RuntimeCapability.JudgeLocalParallelDefault => "1.0.9 or later: guardrail.judge.max_parallel defaults to 1 for a local judge",
        RuntimeCapability.HostSensorDropLatch => "1.0.9 or later: one dropped host sensor event keeps the host sensor degraded until the gateway restarts",
        RuntimeCapability.SetupKeepsRoster => "1.0.10 or later: 'setup -c', '--detected' and '--all' add or update the named connectors and keep the others. Before 1.0.10 they replaced the roster.",
        RuntimeCapability.OpenClawNativeWindows => "1.0.10 or later: OpenClaw runs on native Windows in native hook mode, 'setup openclaw' is offered and /health carries openclaw_plugin",
        RuntimeCapability.OpenClawTokenSeparate => "1.0.10 or later: the gateway skips a token_env that names the OpenClaw gateway token and uses DEFENSECLAW_GATEWAY_TOKEN",
        RuntimeCapability.AgentSelfUpdateKeepsGateway => "1.0.10 or later: when an agent updates itself the gateway keeps running and does not start that connector until setup is run again",
        RuntimeCapability.RoutingCli => "1.0.10 or later: 'defenseclaw routing' exists, /health reports routing and the gateway serves /api/v1/routing/*",
        RuntimeCapability.RoutingHotReload => "1.0.10 or later: only nine routing keys need a gateway restart. The rest, tokenomics and pricing included, apply in place.",
        RuntimeCapability.Tokenomics => "1.0.10 or later: the gateway can record usage and cost of each model call and serves it at /usage. Off until set.",
        RuntimeCapability.DecisionModelProtocol => "1.0.10 or later: 'setup decision-model' takes --protocol system-one or gemini-decisions. '--on-confident-block decide' is retired, accepted and ignored.",
        RuntimeCapability.GuardrailCascade => "1.0.10 or later: guardrail.cascade sets what each guardrail layer does, a proven CRITICAL finding ends at least at alert, and ACTION-COMPOUND-LIMIT is the shared over-limit finding",
        _ => string.Empty,
    };
}
