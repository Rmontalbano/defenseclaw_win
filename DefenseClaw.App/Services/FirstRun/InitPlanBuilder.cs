using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Setup;

namespace DefenseClaw.App.Services.FirstRun;

/// <summary>What the first-run form decided.</summary>
public sealed record InitPlanOptions
{
    /// <summary>Connectors the detection found (normalized ids), in display order. Empty when nothing was detected.</summary>
    public IReadOnlyList<string> Detected { get; init; } = Array.Empty<string>();

    /// <summary>The detected connectors the operator left checked.</summary>
    public IReadOnlySet<string> Registered { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>The connector to configure when nothing was detected (the form's fallback picker).</summary>
    public string FallbackConnector { get; init; } = "codex";

    /// <summary>The registered connectors that enforce (Action profile only).</summary>
    public IReadOnlySet<string> Action { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary><c>observe</c> or <c>action</c>.</summary>
    public string Profile { get; init; } = "observe";

    /// <summary><c>local</c>, <c>remote</c> or <c>both</c>.</summary>
    public string ScannerMode { get; init; } = "local";

    public bool LlmJudge { get; init; }

    /// <summary><c>open</c> or <c>closed</c>.</summary>
    public string FailMode { get; init; } = "open";

    /// <summary>Human-in-the-loop approval; only meaningful (and only sent) for the Action profile.</summary>
    public bool HumanApproval { get; init; }

    /// <summary>CRITICAL, HIGH, MEDIUM or LOW; sent only with <see cref="HumanApproval"/>.</summary>
    public string HiltSeverity { get; init; } = "HIGH";

    /// <summary>Start the gateway as the plan's last step.</summary>
    public bool StartGateway { get; init; } = true;

    /// <summary><c>--verify</c>: the readiness checks that make the report worth reading.</summary>
    public bool Verify { get; init; } = true;
}

/// <summary>The multi-step review first run shows: what to run, in order, and what to say next to it.</summary>
public sealed record InitPlan
{
    public required IReadOnlyList<DiscoverStep> Steps { get; init; }

    /// <summary>The connectors the plan configures, normalized, in the order they are configured.</summary>
    public required IReadOnlyList<string> Connectors { get; init; }

    public string Title { get; init; } = "Set up DefenseClaw?";

    public string Summary { get; init; } = string.Empty;
}

/// <summary>
/// Builds the commands first-run setup runs. Pure: the same options always give the same argv, and nothing is run, read or probed here.
/// <para>
/// <b>Backbone: <c>init</c>, not <c>quickstart</c>.</b> Both end in the same backend (<c>bootstrap.run_first_run</c>) and print the same
/// report, but <c>quickstart</c> (cmd_quickstart.py) configures exactly one connector and exits 2 when several are detected, has no
/// <c>--verify</c>/<c>--no-start-gateway</c> pair (it verifies always and starts the gateway unless <c>--skip-gateway</c>), no
/// <c>--observe-all</c>/<c>--action-connectors</c> and no <c>--rescan-agents</c>. The form needs those: a subset of the detected connectors,
/// some enforcing, HITL, and a gateway start that is a separate, reviewed step. <c>init --non-interactive --yes --json-summary</c> has all of
/// them (cmd_init.py options, confirmed against <c>defenseclaw init --help</c> 0.8.10).
/// </para>
/// <para>
/// <b>Shape</b> (the Mac's <c>ConnectorOnboarding.initializationPlan</c>, with the gateway start moved out so it is reviewed on its own):
/// one <c>init</c> step that always carries <c>--no-start-gateway</c>; for a strict subset of several detected connectors (which one init
/// call cannot express) an additive <c>setup &lt;alias&gt; --yes --mode … --no-restart</c> per extra connector; then, if wanted,
/// <c>defenseclaw-gateway start</c>. <b>Secrets never reach argv</b>: the plan never emits <c>--llm-api-key</c> or <c>--cisco-api-key</c>
/// (the CLI reads <c>DEFENSECLAW_LLM_KEY</c> / <c>CISCO_AI_DEFENSE_API_KEY</c> from the environment by default; the <c>*-env</c> forms only
/// ever carry a variable name).
/// </para>
/// </summary>
public static class InitPlanBuilder
{
    public static InitPlan Build(InitPlanOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var profile = options.Profile == "action" ? "action" : "observe";
        var detected = options.Detected.Select(ConnectorOnboarding.Normalize).Where(static c => c.Length > 0 && !ConnectorOnboarding.IsProxy(c)).Distinct().ToList();
        var registered = new HashSet<string>(options.Registered.Select(ConnectorOnboarding.Normalize), StringComparer.Ordinal);

        var selected = detected.Where(registered.Contains).ToList();

        // The form requires at least one; a caller that passes none anyway gets every detected connector rather than half a setup.
        if (selected.Count == 0)
        {
            selected = detected.ToList();
        }

        var action = new HashSet<string>(options.Action.Select(ConnectorOnboarding.Normalize), StringComparer.Ordinal);
        action.IntersectWith(selected);

        var head = new List<string> { "init", "--non-interactive", "--yes", "--json-summary" };
        var followUps = new List<IReadOnlyList<string>>();
        var connectors = new List<string>();

        if (detected.Count == 0)
        {
            var fallback = ConnectorOnboarding.Normalize(options.FallbackConnector);
            if (fallback.Length == 0 || ConnectorOnboarding.IsProxy(fallback))
            {
                fallback = "codex";
            }

            head.AddRange(new[] { "--connector", fallback, "--profile", profile });
            connectors.Add(fallback);
        }
        else if (selected.Count == detected.Count)
        {
            head.Add("--observe-all");
            if (profile == "action")
            {
                var enforced = selected.Where(action.Contains).ToList();
                if (enforced.Count > 0)
                {
                    head.AddRange(new[] { "--action-connectors", string.Join(',', enforced) });
                }
            }

            // The profile stays explicit for the non-interactive contract; the multi-connector flags decide each peer's mode.
            head.AddRange(new[] { "--profile", profile });
            connectors.AddRange(selected);
        }
        else
        {
            // A strict subset: init configures one connector and the rest are added alongside with `setup`, which the CLI documents as
            // add-alongside-peers. Lead with an enforcing connector so init carries the global enforcement options (human approval).
            var first = (profile == "action" ? selected.FirstOrDefault(action.Contains) : null) ?? selected[0];
            var firstProfile = profile == "action" && action.Contains(first) ? "action" : "observe";
            head.AddRange(new[] { "--connector", first, "--profile", firstProfile });
            connectors.Add(first);

            foreach (var name in selected.Where(c => c != first))
            {
                var mode = profile == "action" && action.Contains(name) ? "action" : "observe";
                followUps.Add(new[] { "setup", ConnectorOnboarding.SetupAlias(name), "--yes", "--mode", mode, "--no-restart" });
                connectors.Add(name);
            }
        }

        var scanner = options.ScannerMode is "remote" or "both" ? options.ScannerMode : "local";
        head.AddRange(new[] { "--scanner-mode", scanner, options.LlmJudge ? "--with-judge" : "--no-judge", "--fail-mode", options.FailMode == "closed" ? "closed" : "open" });

        if (profile == "action")
        {
            head.Add(options.HumanApproval ? "--human-approval" : "--no-human-approval");
            if (options.HumanApproval)
            {
                head.AddRange(new[] { "--hilt-min-severity", NormalizeSeverity(options.HiltSeverity) });
            }
        }

        // The gateway start is its own reviewed step, so init never starts it.
        head.Add("--no-start-gateway");
        head.Add(options.Verify ? "--verify" : "--no-verify");

        var steps = new List<DiscoverStep>
        {
            new(
                head,
                "Create the DefenseClaw configuration, install the scanners and configure " + Describe(connectors) + ". Prints a JSON report that is checked before anything after it runs.",
                CommandTier.StateChanging,
                CliRunner.LongRunningTimeout,
                Verify: VerifyReport,
                RetainFullOutput: true),
        };

        foreach (var followUp in followUps)
        {
            steps.Add(new DiscoverStep(
                followUp,
                $"Add {ConnectorOnboarding.Label(followUp[1] == "claude-code" ? "claudecode" : followUp[1])} alongside the others.",
                CommandTier.StateChanging,
                CliRunner.LongRunningTimeout));
        }

        if (options.StartGateway)
        {
            steps.Add(new DiscoverStep(
                GatewayControl.Argv(GatewayAction.Start),
                "Start the gateway as a background daemon so the configured hooks have something to talk to.",
                CommandTier.StateChanging,
                Executable: GatewayControl.Executable));
        }

        return new InitPlan
        {
            Steps = steps,
            Connectors = connectors,
            Summary =
                "Creates ~/.defenseclaw, configures " + Describe(connectors) + " in " + profile + " mode" +
                (options.StartGateway ? " and starts the gateway" : ", leaving the gateway stopped") +
                ". Each command runs only if the one before it succeeded, and setup counts as failed unless its own report says it finished.",
        };
    }

    /// <summary>
    /// The <c>init</c> step's check: its exit code is not its result (<see cref="InitReportValidator"/>). Returns null when the report is
    /// accepted, otherwise a sentence plus the failing steps, for the result box.
    /// </summary>
    internal static string? VerifyReport(CliInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        var result = InitReportValidator.Validate(DiscoverCli.Stdout(invocation));
        if (result.IsAccepted)
        {
            return null;
        }

        var lines = new List<string> { result.Message };
        foreach (var step in result.Attention.Where(static s => s.Status == "fail"))
        {
            lines.Add($"  {step.Name}: {(step.Detail.Length > 0 ? step.Detail : "failed")}" + (step.NextCommand.Length > 0 ? $" (try: {step.NextCommand})" : string.Empty));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string NormalizeSeverity(string? severity)
    {
        var value = (severity ?? string.Empty).Trim().ToUpperInvariant();
        return value is "CRITICAL" or "HIGH" or "MEDIUM" or "LOW" ? value : "HIGH";
    }

    private static string Describe(IReadOnlyList<string> connectors) =>
        connectors.Count == 1 ? ConnectorOnboarding.Label(connectors[0]) : string.Join(", ", connectors.Select(ConnectorOnboarding.Label));
}
