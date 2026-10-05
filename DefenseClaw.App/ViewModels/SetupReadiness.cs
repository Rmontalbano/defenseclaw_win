using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Config;

namespace DefenseClaw.App.ViewModels;

/// <summary>How a readiness row reads; the TUI's <c>pass | warn | fail</c>.</summary>
public enum ReadinessStatus
{
    Pass,
    Warn,
    Fail,
}

/// <summary>What pressing a row's Fix does.</summary>
public enum ReadinessFixKind
{
    /// <summary>Opens the shared command review with <see cref="ReadinessFix.Steps"/>; runs them in order once confirmed.</summary>
    Review,

    /// <summary>
    /// The command prompts for a secret with a hidden console prompt (<c>getpass</c> reads the console, never a pipe), so it cannot run
    /// from here: the fix opens a console window running the exact command (<see cref="CredentialTerminal"/>).
    /// </summary>
    Terminal,

    /// <summary>
    /// The TUI's fix is an interactive wizard (<c>setup llm</c>, <c>setup guardrail</c>: bare, they prompt on stdin, which this
    /// app has none of). The fix opens the matching Setup wizard, which ends on a review of the exact flags it will pass.
    /// </summary>
    Wizard,
}

/// <summary>One command of a fix: the program, its arguments (no executable, no secret) and what it is for.</summary>
public sealed record ReadinessFixStep(string Executable, IReadOnlyList<string> Argv, string Purpose);

/// <summary>
/// What a failing row offers. <see cref="Steps"/> is the exact argv the 0.8.10 TUI's readiness intent uses (<c>setup_state._intent</c>),
/// so a Fix and the TUI agree, whichever way the app has to run it (<see cref="Kind"/>).
/// </summary>
/// <param name="Kind">How the fix is carried out.</param>
/// <param name="Label">The TUI's intent label (<c>keys fill-missing</c>, <c>restart</c>, …).</param>
/// <param name="Steps">The commands in run order; a step only runs if the one before it exited 0.</param>
/// <param name="WizardTarget">For <see cref="ReadinessFixKind.Wizard"/>: the Setup target to open (<c>llm</c>).</param>
/// <param name="RestartsGateway">True when running it bounces the live gateway.</param>
public sealed record ReadinessFix(
    ReadinessFixKind Kind,
    string Label,
    IReadOnlyList<ReadinessFixStep> Steps,
    string? WizardTarget = null,
    bool RestartsGateway = false)
{
    /// <summary>The commands as one line each, the way the review shows them.</summary>
    public string CommandText => string.Join("  then  ", Steps.Select(s => s.Executable + " " + string.Join(' ', s.Argv)));
}

/// <summary>One row of the readiness checklist (the TUI's <c>ReadinessCheck</c>).</summary>
public sealed record ReadinessCheck(string Title, string Detail, ReadinessStatus Status, ReadinessFix? Fix = null);

/// <summary>
/// What the checklist is built from: values the Setup panel already holds (the loaded config.yaml, the monitor's snapshot, the
/// credential read), so building it is pure and costs nothing. Nothing here is fetched for the checklist.
/// </summary>
public sealed record ReadinessInputs
{
    /// <summary>The roster the gateway reports, falling back to config.yaml's; empty means "no connector configured".</summary>
    public IReadOnlyList<string> ActiveConnectors { get; init; } = Array.Empty<string>();

    /// <summary>Where the monitor says the gateway is; decides which gateway row and which fix.</summary>
    public AppGatewayStateKind Gateway { get; init; } = AppGatewayStateKind.Unknown;

    /// <summary>The monitor's own sentence for <see cref="Gateway"/>, used when there is no fix to offer.</summary>
    public string GatewayDetail { get; init; } = string.Empty;

    /// <summary><c>/health</c> <c>gateway.state</c> (the fleet uplink; <c>disabled</c> on a standalone install); empty when absent.</summary>
    public string GatewaySubsystemState { get; init; } = string.Empty;

    /// <summary><c>/health</c> <c>api.state</c>; empty when absent.</summary>
    public string ApiState { get; init; } = string.Empty;

    public bool GuardrailEnabled { get; init; }

    public string GuardrailMode { get; init; } = string.Empty;

    /// <summary>The last <c>keys list --json</c> read; empty until one has landed.</summary>
    public IReadOnlyList<CredentialRow> Credentials { get; init; } = Array.Empty<CredentialRow>();

    /// <summary>Required keys the doctor cache found missing; used (as the TUI does) only while the key list shows none.</summary>
    public IReadOnlyList<string> DoctorMissingCredentials { get; init; } = Array.Empty<string>();

    public ReadinessConfig Config { get; init; } = ReadinessConfig.Empty;

    /// <summary>Why a gateway restart is queued; empty when none is.</summary>
    public string RestartReason { get; init; } = string.Empty;
}

/// <summary>The gateway states the checklist tells apart (a narrowing of <c>AppGatewayState</c> that the model does not need to import).</summary>
public enum AppGatewayStateKind
{
    /// <summary>Before the first poll, or a state with nothing to say.</summary>
    Unknown,

    /// <summary><c>/health</c> answered.</summary>
    Reachable,

    /// <summary>Initialized, but nothing answers on the API port: <c>defenseclaw-gateway start</c> would help.</summary>
    Stopped,

    /// <summary>Not installed, not initialized, or a WSL gateway owns the port: starting the native one is not the fix.</summary>
    NotRunnableHere,
}

/// <summary>
/// The Setup readiness checklist: <c>services/setup_state.py::build_readiness_checks</c> of the 0.8.10 TUI, row for row and in its order, with
/// the same pass/warn/fail rules and the same fix argv. Differences are deliberate and listed on the rows they touch.
/// </summary>
public static class SetupReadiness
{
    private const string Cli = "defenseclaw";
    private const string Gateway = "defenseclaw-gateway";

    public static IReadOnlyList<ReadinessCheck> Build(ReadinessInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        var checks = new List<ReadinessCheck>();
        var config = inputs.Config;

        // Active connectors. The TUI's fix here is `setup openclaw --yes`; OpenClaw is not supported on Windows (the Setup hub lists it
        // under "not available"), so the row says what is missing and the connector cards above are where it gets added.
        if (inputs.ActiveConnectors.Count > 0)
        {
            foreach (var connector in inputs.ActiveConnectors)
            {
                checks.Add(new ReadinessCheck($"Active Connector: {connector}", "configured", ReadinessStatus.Pass));
            }
        }
        else
        {
            checks.Add(new ReadinessCheck(
                "Active Connector",
                "No connector mode is configured. Add one from the setup cards below (Claude Code and Codex are the Windows connectors).",
                ReadinessStatus.Fail));
        }

        checks.Add(GatewayRow(inputs));

        checks.Add(inputs.GuardrailEnabled
            ? new ReadinessCheck("Guardrail", $"enabled in {(inputs.GuardrailMode.Length > 0 ? inputs.GuardrailMode : "observe")} mode", ReadinessStatus.Pass)
            : new ReadinessCheck(
                "Guardrail",
                "Guardrail is disabled or config is unavailable.",
                ReadinessStatus.Warn,
                Wizard("setup guardrail", "guardrail", "setup", "guardrail")));

        var missing = inputs.Credentials.Count(c => c.IsMissingRequired);
        if (missing == 0)
        {
            missing = inputs.DoctorMissingCredentials.Count;
        }

        checks.Add(missing > 0
            ? new ReadinessCheck(
                "Required Credentials",
                $"{missing} required credential(s) missing",
                ReadinessStatus.Fail,
                new ReadinessFix(
                    ReadinessFixKind.Terminal,
                    "keys fill-missing",
                    new[] { new ReadinessFixStep(Cli, new[] { "keys", "fill-missing", "--yes" }, "Prompts for each missing required key with a hidden console prompt.") }))
            : new ReadinessCheck("Required Credentials", "No missing required credentials detected.", ReadinessStatus.Pass));

        var provider = config.LlmProvider;
        if (provider.Length > 0 && (config.LlmModel.Length > 0 || config.LlmInstanceName.Length > 0))
        {
            var detail = config.LlmModel.Length > 0 ? $"{provider}/{config.LlmModel}" : $"{provider} (via instance {config.LlmInstanceName})";
            checks.Add(new ReadinessCheck("LLM Config", detail, ReadinessStatus.Pass));
        }
        else
        {
            checks.Add(new ReadinessCheck(
                "LLM Config",
                "Unified llm.provider/model is incomplete.",
                ReadinessStatus.Warn,
                Wizard("setup llm", "llm", "setup", "llm")));
        }

        if (ReadinessConfig.RegionalProviders.Contains(provider))
        {
            var region = config.LlmRegion;
            var endpoint = config.AzureEndpoint;
            var azure = provider == "azure";
            if (region.Length > 0 || (azure && endpoint.Length > 0))
            {
                var where = azure && region.Length == 0 ? endpoint : region;
                checks.Add(new ReadinessCheck("Regional Provider", $"{provider} ({where})", ReadinessStatus.Pass));
            }
            else
            {
                checks.Add(new ReadinessCheck(
                    "Regional Provider",
                    $"{provider} selected but no {(azure ? "endpoint" : "region")} configured.",
                    ReadinessStatus.Warn,
                    Wizard("setup llm", "llm", "setup", "llm")));
            }
        }

        if (config.LlmInstanceName.Length > 0)
        {
            checks.Add(new ReadinessCheck("Custom-provider Overlay", $"instance '{config.LlmInstanceName}' bound", ReadinessStatus.Pass));
        }

        checks.Add(config.ScannerConfigured
            ? new ReadinessCheck("Scanner Availability", "Scanner config present.", ReadinessStatus.Pass)
            : new ReadinessCheck(
                "Scanner Availability",
                "Scanner binaries are not configured.",
                ReadinessStatus.Warn,
                // Two-step consent, as in the TUI: the dry run lists what the fixers would do, then the apply step does it.
                new ReadinessFix(
                    ReadinessFixKind.Review,
                    "doctor --fix",
                    new[]
                    {
                        new ReadinessFixStep(Cli, new[] { "doctor", "--fix", "--dry-run" }, "Previews the repairs doctor would make; changes nothing."),
                        new ReadinessFixStep(Cli, new[] { "doctor", "--fix", "--yes" }, "Applies the repairs."),
                    })));

        checks.Add(new ReadinessCheck(
            "Observability v8",
            "Canonical routing is active; local SQLite collection is mandatory.",
            ReadinessStatus.Pass));

        checks.Add(config.AssetPolicyEnabled && config.RegistryRequiredButEmpty
            ? new ReadinessCheck(
                "Registry / Asset Policy",
                "Registry-required asset policy has no promoted registry entries.",
                ReadinessStatus.Warn,
                new ReadinessFix(
                    ReadinessFixKind.Review,
                    "registry sync --all",
                    new[] { new ReadinessFixStep(Cli, new[] { "registry", "sync", "--all" }, "Syncs every registry source so entries can be promoted.") }))
            : new ReadinessCheck("Registry / Asset Policy", "Registry policy is ready or not required.", ReadinessStatus.Pass));

        checks.Add(inputs.RestartReason.Length > 0
            ? new ReadinessCheck("Restart Pending", inputs.RestartReason, ReadinessStatus.Warn, GatewayRestart())
            : new ReadinessCheck("Restart Pending", "No queued restart.", ReadinessStatus.Pass));

        return checks;
    }

    private static ReadinessCheck GatewayRow(ReadinessInputs inputs)
    {
        const string Title = "Gateway / API Health";
        switch (inputs.Gateway)
        {
            case AppGatewayStateKind.Stopped:
                return new ReadinessCheck(Title, "Gateway health endpoint is offline.", ReadinessStatus.Fail, GatewayStart());

            case AppGatewayStateKind.NotRunnableHere:
                return new ReadinessCheck(
                    Title,
                    inputs.GatewayDetail.Length > 0 ? inputs.GatewayDetail : "The native gateway is not available on this machine.",
                    ReadinessStatus.Fail);

            case AppGatewayStateKind.Unknown:
                return new ReadinessCheck(Title, "Waiting for the first gateway poll.", ReadinessStatus.Warn);
        }

        var gateway = inputs.GatewaySubsystemState.Trim().ToLowerInvariant();
        var api = inputs.ApiState.Trim().ToLowerInvariant();
        return IsHealthy(gateway, orDisabled: true) && IsHealthy(api, orDisabled: false)
            ? new ReadinessCheck(Title, "Gateway and API are healthy.", ReadinessStatus.Pass)
            : new ReadinessCheck(Title, $"gateway={gateway} api={api}", ReadinessStatus.Warn, GatewayRestart());
    }

    private static bool IsHealthy(string state, bool orDisabled) =>
        state is "running" or "ok" or "healthy" or "ready" || (orDisabled && state == "disabled");

    private static ReadinessFix GatewayStart() => new(
        ReadinessFixKind.Review,
        "start",
        new[] { new ReadinessFixStep(Gateway, new[] { "start" }, "Starts the DefenseClaw gateway.") });

    private static ReadinessFix GatewayRestart() => new(
        ReadinessFixKind.Review,
        "restart",
        new[] { new ReadinessFixStep(Gateway, new[] { "restart" }, "Restarts the DefenseClaw gateway.") },
        RestartsGateway: true);

    private static ReadinessFix Wizard(string label, string target, params string[] argv) => new(
        ReadinessFixKind.Wizard,
        label,
        new[] { new ReadinessFixStep(Cli, argv, "The TUI runs this wizard; here it opens as a form that ends on a review of the exact flags.") },
        WizardTarget: target);
}
