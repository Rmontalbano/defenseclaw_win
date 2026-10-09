namespace DefenseClaw.App.Services.Wizards;

/// <summary>
/// The goals of each wizard, as data. Wording follows the TUI's menus (<c>_llm_goals</c>, <c>_guardrail_goals</c>, <c>_connector_setup_goals</c>,
/// <c>_splunk_goals</c>, <c>_ai_discovery_goals</c> in <c>tui/panels/setup.py</c>, 0.8.10), bent to what the app has: Windows runs hook
/// connectors only, so there is no proxy-stack goal and no agent-LLM goal; adding, batching and removing connectors are the Setup hub's own
/// cards; and AI discovery's on / off is a different command (<c>agent discovery disable</c>), so its goals are the tuning ones.
/// <para>
/// Every flag and page named here is looked up in the live steps by <see cref="Apply"/>; the table says what a goal is for, the installed CLI
/// says whether it can be done. Two things differ from the TUI on purpose. Its judge goal presets <c>--inherit-from llm</c>, a value the CLI's
/// own choice list does not have (the sources are guardrail, guardrail.judge and the scanners), so it would not run; here the goal clears the
/// starting values and leaves Inherit From to be picked. And its cisco / judge / detection goals pin the scope to global, as here.
/// </para>
/// </summary>
public static partial class WizardGoals
{
    private static IReadOnlyDictionary<string, string> Set(params (string Key, string Value)[] presets) =>
        presets.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

    // ------------------------------------------------------------------ setup llm

    private static readonly IReadOnlyList<string> LlmProviderGroups = new[]
    {
        WizardPages.LlmBedrock, WizardPages.LlmVertex, WizardPages.LlmAzure, WizardPages.LlmTls,
    };

    private static readonly IReadOnlyList<WizardGoal> Llm = new[]
    {
        new WizardGoal
        {
            Id = "main",
            Label = "Set up or change my main model",
            Summary = "Pick the provider, model and API key variable for the unified LLM that the judge and the scanners share.",
            Presets = Set(("--role", "unified")),
            Flags = new[] { "--provider", "--model", "--api-key-env", "--api-key", "--base-url" },
            Pages = LlmProviderGroups,
            Needs = new[] { "--provider", "--model" },
        },
        new WizardGoal
        {
            Id = "judge",
            Label = "Add or change the judge LLM",
            Summary = "Give the guardrail judge a model of its own. Anything you leave blank is inherited from the main model.",

            // Blank starts: the judge block inherits whatever it does not set, so the main model's values are not its own.
            Presets = Set(
                ("--role", "judge"),
                ("--provider", string.Empty),
                ("--model", string.Empty),
                ("--api-key-env", string.Empty),
                ("--base-url", string.Empty),
                ("--timeout", string.Empty),
                ("--max-retries", string.Empty)),
            Flags = new[] { "--provider", "--model", "--api-key-env", "--api-key", "--base-url", "--inherit-from" },
            Pages = LlmProviderGroups,
            Needs = new[] { "--role" },
        },
        new WizardGoal
        {
            Id = "regional",
            Label = "Use a regional provider (Bedrock, Vertex AI or Azure)",
            Summary = "Switch to a cloud-region provider. Its sign-in settings appear when you pick it.",
            Flags = new[] { "--provider", "--model" },
            Pages = LlmProviderGroups,
            Needs = LlmProviderGroups,
        },
        new WizardGoal
        {
            Id = "instance",
            Label = "Connect a self-hosted or custom instance",
            Summary = "Point at an OpenAI-compatible endpoint you registered with provider add, with its own certificate if it needs one.",
            Flags = new[] { "--provider", "--instance-name", "--base-url", "--model" },
            Pages = new[] { WizardPages.LlmTls },
            Needs = new[] { "--instance-name" },
        },
        new WizardGoal
        {
            Id = "test",
            Label = "Test my LLM connection",
            Summary = "Save, then have the CLI send one tiny request to the provider to check it answers.",
            Presets = Set(("--ping", ToggleValues.On)),
            Flags = new[] { "--provider", "--model", "--api-key", "--ping" },
            Pages = LlmProviderGroups,
            Needs = new[] { "--ping" },
        },
    };

    // ------------------------------------------------------------------ setup guardrail

    private static readonly IReadOnlyList<WizardGoal> Guardrail = new[]
    {
        new WizardGoal
        {
            Id = "mode",
            Label = "Switch enforcement mode (observe or action)",
            Summary = "Log what the guardrail would block (observe) or block it (action).",
            Flags = new[] { "--mode", "--restart" },
            Pages = new[] { WizardPages.GuardrailScope },
            Needs = new[] { "--mode" },
        },
        new WizardGoal
        {
            Id = "judge",
            Label = "Set up or change the LLM judge",
            Summary = "Global judge settings, shared by every active connector. The judge pages appear once the strategy uses it.",
            Presets = Set(("scope", GuardrailScopes.Global), ("--detection-strategy", "regex_judge")),
            Flags = new[]
            {
                "--detection-strategy", "--judge-provider", "--judge-model", "--judge-api-key-env", "--judge-api-base",
                "--judge-instance-name", "--llm-role", "--inherit-from", "--inherit-llm", "--judge-hook-connectors", "--restart",
            },
            Pages = new[]
            {
                WizardPages.GuardrailScope, WizardPages.GuardrailJudge, WizardPages.GuardrailJudgeBedrock, WizardPages.GuardrailJudgeVertex,
                WizardPages.GuardrailJudgeAzure, WizardPages.GuardrailJudgeTls,
            },
            Needs = new[] { "--judge-model" },
        },
        new WizardGoal
        {
            Id = "cisco",
            Label = "Connect Cisco AI Defense",
            Summary = "Global Cisco AI Defense settings, shared by every active connector. The API key itself stays in ~/.defenseclaw/.env.",
            Presets = Set(("scope", GuardrailScopes.Global)),
            Flags = new[] { "--scanner-mode", "--cisco-endpoint", "--cisco-api-key-env", "--cisco-timeout-ms", "--restart" },
            Pages = new[] { WizardPages.GuardrailScope },
            Needs = new[] { "--cisco-endpoint" },
        },
        new WizardGoal
        {
            Id = "hitl",
            Label = "Require human approval (HITL)",
            Summary = "Ask a person before the guardrail lets a high-risk action through.",
            Presets = Set(("--human-approval", ToggleValues.On)),
            Flags = new[] { "--human-approval", "--hilt-min-severity", "--restart" },
            Pages = new[] { WizardPages.GuardrailScope },
            Needs = new[] { "--human-approval" },
        },
        new WizardGoal
        {
            Id = "detection",
            Label = "Tune global detection",
            Summary = "Scanner mode, port and detection strategy for every active connector.",
            Presets = Set(("scope", GuardrailScopes.Global)),
            Flags = new[]
            {
                "--scanner-mode", "--port", "--detection-strategy", "--detection-strategy-prompt", "--detection-strategy-completion",
                "--detection-strategy-tool-call", "--restart",
            },
            Pages = new[] { WizardPages.GuardrailScope },
            Needs = new[] { "--detection-strategy", "--scanner-mode" },
        },
        new WizardGoal
        {
            Id = "rule-pack",
            Label = "Change one connector's rule pack",
            Summary = "Only that connector's rule pack changes; its peers keep theirs.",
            Presets = Set(("scope", GuardrailScopes.Connector)),
            Flags = new[] { "--rule-pack", "--rule-pack-dir", "--block-message", "--restart" },
            Pages = new[] { WizardPages.GuardrailScope },
            Needs = new[] { "--rule-pack", "--rule-pack-dir" },
        },
        new WizardGoal
        {
            Id = "disable",
            Label = "Turn the guardrail off",
            Summary = "Disable it for one connector, or for every connector, and let the gateway tear the hooks down.",
            Presets = Set(("--disable", ToggleValues.On)),
            Flags = new[] { "--restart" },
            Pages = new[] { WizardPages.GuardrailScope },
            Needs = new[] { "--disable" },
        },
    };

    // ------------------------------------------------------------------ setup <hook connector>

    private static readonly IReadOnlyList<WizardGoal> Connector = new[]
    {
        new WizardGoal
        {
            Id = "setup",
            Label = "Set up or re-run this connector",
            Summary = "Install its hooks and choose how it enforces, observing or blocking.",
            Flags = new[] { "--mode", "--fail-mode", "--replace", "--workspace", "--restart" },
            Needs = new[] { "--mode" },
        },
        new WizardGoal
        {
            Id = "mode",
            Label = "Switch enforcement mode (observe or action)",
            Summary = "Record what would be blocked (observe) or block it through the agent's own permission flow (action).",
            Flags = new[] { "--mode", "--fail-mode", "--restart" },
            Needs = new[] { "--mode" },
        },
        new WizardGoal
        {
            Id = "hitl",
            Label = "Require human approval (HITL)",
            Summary = "Ask a person before this connector lets a high-risk action through.",
            Presets = Set(("--human-approval", ToggleValues.On)),
            Flags = new[] { "--human-approval", "--hilt-min-severity", "--restart" },
            Needs = new[] { "--human-approval" },
        },
        new WizardGoal
        {
            Id = "rules",
            Label = "Change the rule pack or the block message",
            Summary = "Per-connector overrides. Leave a field unchanged to keep inheriting the global setting.",
            Flags = new[] { "--rule-pack", "--rule-pack-dir", "--block-message", "--restart" },
            Needs = new[] { "--rule-pack", "--rule-pack-dir" },
        },
        new WizardGoal
        {
            Id = "judge",
            Label = "Use the LLM judge for this connector",
            Summary = "Turn the judge on for this connector's hooks. The judge's model is set under the guardrail or the LLM wizard.",
            Presets = Set(("--enable-judge", ToggleValues.On)),
            Flags = new[] { "--enable-judge", "--judge-hook-connectors", "--restart" },
            Needs = new[] { "--enable-judge" },
        },
    };

    // ------------------------------------------------------------------ setup splunk

    private static readonly IReadOnlyList<WizardGoal> Splunk = new[]
    {
        new WizardGoal
        {
            Id = "o11y",
            Label = "Send to Splunk Observability Cloud",
            Summary = "Stream DefenseClaw's traces and metrics to Splunk Observability Cloud over OTLP.",
            Presets = Set(("--o11y", ToggleValues.On)),
            Pages = new[] { WizardPages.SplunkO11y },
            Needs = new[] { "--o11y" },
        },
        new WizardGoal
        {
            Id = "local-docker",
            Label = "Run a local Splunk (Docker)",
            Summary = "Start Splunk in Docker on this machine and send the logs to it.",
            Presets = Set(("--logs", ToggleValues.On)),
            Pages = new[] { WizardPages.SplunkLocal, WizardPages.SplunkHec },
            Needs = new[] { "--logs" },
            Requires = WizardGuideRequirement.Docker,
        },
        new WizardGoal
        {
            Id = "enterprise",
            Label = "Send to Splunk Enterprise (HEC)",
            Summary = "Forward the logs to a Splunk Enterprise or Splunk Cloud HTTP Event Collector you already run.",
            Presets = Set(("--enterprise", ToggleValues.On)),
            Pages = new[] { WizardPages.SplunkEnterprise, WizardPages.SplunkHec },
            Needs = new[] { "--enterprise" },
        },
    };

    // ------------------------------------------------------------------ agent discovery enable

    private static readonly IReadOnlyList<WizardGoal> AiDiscovery = new[]
    {
        new WizardGoal
        {
            Id = "cadence",
            Label = "Set the scan cadence",
            Summary = "Choose passive or enhanced discovery and how often it scans.",
            Flags = new[] { "--mode", "--scan-interval-min", "--process-interval-s" },
            Needs = new[] { "--scan-interval-min", "--process-interval-s" },
        },
        new WizardGoal
        {
            Id = "scope",
            Label = "Set where it scans",
            Summary = "Choose the folders it looks in and how much of each it reads.",
            Flags = new[] { "--scan-roots", "--max-files-per-scan", "--max-file-bytes" },
            Needs = new[] { "--scan-roots", "--max-files-per-scan", "--max-file-bytes" },
        },
        new WizardGoal
        {
            Id = "sources",
            Label = "Choose what it looks at",
            Summary = "Shell history, package manifests, environment variable names and network domains.",
            Flags = new[]
            {
                "--include-shell-history", "--include-package-manifests", "--include-env-var-names", "--include-network-domains",
                "--allow-workspace-signatures", "--store-raw-local-paths",
            },
        },
    };
}
