using DefenseClaw.App.Services.Wizards;
using DefenseClaw.Core.Config;

namespace DefenseClaw.App.Tests.TestSupport;

/// <summary>
/// Click help screens shaped like the real 0.8.10 ones, and the same pipeline <c>WizardCatalog</c> runs on
/// them (parse, curate, Windows policy, secret routes) — minus the CLI probe, which is what makes it testable.
/// The screens are held as LF whatever the source file was checked out with (see <see cref="LineEndings"/>);
/// <see cref="LineEndings.With"/> turns one into the CRLF text the real CLI pipes.
/// </summary>
internal static class WizardSamples
{
    public static readonly string ClaudeCodeHelp = LineEndings.Normalize("""
        Usage: defenseclaw setup claude-code [OPTIONS]

          Configure DefenseClaw hooks for Claude Code.

          Writes the hook entries into Claude Code's settings.json and records the
          connector policy in config.yaml. Run with --yes for unattended setup.

        Options:
          --mode [observe|action]         Hook policy mode.  [default: observe]
          --fail-mode [open|closed]       What the hook does when the gateway is
                                          unreachable.
          --human-approval / --no-human-approval
                                          Ask for human approval on findings.
          --hilt-min-severity [LOW|MEDIUM|HIGH|CRITICAL]
                                          Lowest severity that needs approval.
          --block-message TEXT            Message shown to the agent when a call is
                                          blocked (pass "" to clear).
          --rule-pack TEXT                Rule pack for this connector.
          --rule-pack-dir DIRECTORY       Rule pack directory (empty = inherit).
          --enable-judge / --no-enable-judge
                                          Per-connector LLM judge override.
          --judge-hook-connectors TEXT    Connectors whose hooks call the judge
                                          (repeatable).
          --replace / --no-replace        Replace hooks already installed by other
                                          tools.  [default: no-replace]
          --workspace TEXT                Workspace-scoped settings file.
          --with-local-stack / --no-local-stack
                                          Also bring up the local Docker stack.
                                          [default: no-local-stack]
          --restart / --no-restart        Restart the gateway to pick up the change.
                                          [default: restart]
          -y, --yes                       Do not prompt.
          --help                          Show this message and exit.
        """);

    public static readonly string LlmHelp = LineEndings.Normalize("""
        Usage: defenseclaw setup llm [OPTIONS]

          Configure the unified LLM provider block.

        Options:
          --provider TEXT       Provider name.
          --model TEXT          Model id.
          --api-key TEXT        API key (stored by name, never on the command line).
          --api-key-env TEXT    Environment variable that holds the API key.
          --base-url TEXT       Provider base URL.
          --timeout INTEGER     Request timeout in seconds.
          --max-retries INTEGER
                                Retries on failure.
          --region TEXT         Cloud region.
          --non-interactive     Never prompt.
          --help                Show this message and exit.
        """);

    public static readonly string ObservabilityHelp = LineEndings.Normalize("""
        Usage: defenseclaw setup observability [OPTIONS] COMMAND [ARGS]...

          Manage telemetry destinations.

        Options:
          --help  Show this message and exit.

        Commands:
          add   Add a destination from a preset.
          list  List configured destinations.
        """);

    public static readonly string ObservabilityAddHelp = LineEndings.Normalize("""
        Usage: defenseclaw setup observability add [OPTIONS] <preset>

          Add a destination from a preset.

        Options:
          --token TEXT   Destination token.
          --name TEXT    Destination name.
          --help         Show this message and exit.
        """);

    /// <summary>The 0.8.10 <c>setup galileo --help</c> screen, verbatim (only the line endings are normalised).</summary>
    public static readonly string GalileoHelp = LineEndings.Normalize("""
        Usage: defenseclaw setup galileo [OPTIONS] [COMMAND] [ARGS]...

          Configure Galileo OTLP trace export.

          With no subcommand this runs the guided cloud/self-hosted setup. Galileo
          receives traces only; local-observability can remain enabled alongside it.

        Options:
          --deployment [cloud|self-hosted]
                                          [default: cloud]
          --project TEXT                  Galileo project name or ID
          --logstream TEXT                Galileo Log stream name or ID
          --console-url TEXT              Self-hosted Galileo console URL; the API
                                          trace endpoint is derived from it.
          --trace-endpoint TEXT           Exact Galileo OTLP HTTP traces endpoint
                                          (overrides URL derivation).
          --persist-api-key               Copy GALILEO_API_KEY from the environment
                                          into ~/.defenseclaw/.env.
          --disabled                      Write the destination disabled.
          --dry-run                       Preview config changes without writing.
          --non-interactive               Require all values through
                                          flags/environment.
          --help                          Show this message and exit.

        Commands:
          disable  Disable Galileo without deleting its configuration.
          enable   Enable the Galileo destination.
          remove   Remove the Galileo destination; the shared API key is preserved.
          status   Show the configured Galileo destination without secret values.
          test     Emit and acknowledge a content-free trace through Galileo.
        """);

    /// <summary>
    /// The definition the catalog would build for <c>galileo</c>: the group pages, the Windows policy, the synthetic
    /// secrets, then the secret routes — the same order <c>WizardCatalog.LoadDetailAsync</c> runs them in.
    /// </summary>
    public static WizardDefinition Galileo(string? help = null)
    {
        var parsed = SetupHelpParser.Parse(help ?? GalileoHelp);
        var subcommands = parsed.Commands.ToDictionary(c => c.Name, c => new ParsedHelp { Summary = c.Summary });

        var steps = WizardStepFactory.BuildGroup(parsed, subcommands);
        steps = WizardWindowsPolicy.Filter("galileo", steps);
        steps = WizardSyntheticSecrets.Add("galileo", steps);
        steps = SecretRoutes.Annotate("galileo", steps);
        steps = WizardWalkthroughs.Apply("galileo", steps, help ?? GalileoHelp);

        return new WizardDefinition
        {
            Target = "galileo",
            Title = "Galileo",
            Group = WizardGroups.Observability,
            Description = parsed.Summary,
            Steps = steps,
            PlatformStatus = parsed.PlatformStatus,
            IsDetailLoaded = true,
        };
    }

    /// <summary>The 0.8.10 <c>setup splunk --help</c> screen, verbatim (only the line endings are normalised).</summary>
    public static readonly string SplunkHelp = LineEndings.Normalize("""
        Usage: defenseclaw setup splunk [OPTIONS] [COMMAND] [ARGS]...

          Configure Splunk integration for DefenseClaw.

          Three independent pipelines are available:

            --o11y   Splunk Observability Cloud (traces + metrics via OTLP HTTP)
                     No local infrastructure needed. Requires a Splunk access token.

            --logs   Local Splunk (Docker, HEC logs + dashboards)
                     Starts the bundled profile in Splunk Free mode from day 1.
                     Requires Docker.

            --enterprise
                     Remote Splunk Enterprise HEC endpoint + token.
                     No Docker, local bridge, or Splunk-side automation.
                     Sends one best-effort HEC probe unless --skip-test is set.

          Both can run simultaneously. Without flags, runs an interactive wizard.

        Options:
          --o11y                          Enable Splunk Observability Cloud (OTLP
                                          traces + metrics)
          --logs                          Enable local Splunk via Docker (HEC logs +
                                          dashboards, Free mode)
          --s3-export                     Enable local Splunk and start the optional
                                          S3 exporter sidecar
          --s3-bucket TEXT                S3 bucket for --s3-export (or set S3_BUCKET)
          --s3-prefix TEXT                S3 prefix for --s3-export (default:
                                          agentwatch/defenseclaw)
          --aws-region TEXT               AWS region for --s3-export (default: us-
                                          west-2)
          --enterprise                    Enable remote Splunk Enterprise via HEC
                                          endpoint + token
          --realm TEXT                    Splunk O11y realm (e.g. us1, us0, eu0)
          --access-token TEXT             Splunk O11y access token
          --hec-endpoint TEXT             Remote Splunk Enterprise HEC endpoint
          --hec-token TEXT                Remote Splunk Enterprise HEC token
          --app-name TEXT                 OTEL service name (default: defenseclaw)
          --index TEXT                    HEC index for --logs/--enterprise (default:
                                          defenseclaw_local for local, defenseclaw for
                                          enterprise)
          --source TEXT                   HEC source for --logs/--enterprise (default:
                                          defenseclaw)
          --sourcetype TEXT               HEC sourcetype for --logs/--enterprise
                                          (default: defenseclaw:json for local, _json
                                          for enterprise)
          --traces / --no-traces          Enable/disable trace export (O11y)
          --metrics / --no-metrics        Enable/disable metrics export (O11y)
          --logs-export / --no-logs-export
                                          Enable/disable logs export (O11y)
          --disable                       Disable Splunk integration(s)
          --accept-splunk-license         Acknowledge the Splunk General Terms for
                                          local Splunk enablement
          --skip-test                     Skip the live HEC probe after remote Splunk
                                          Enterprise setup
          --show-credentials              Show the generated HEC token and runtime-
                                          only Splunk bootstrap secret
          --refresh-bundle / --no-refresh-bundle
                                          Before starting local Splunk, refresh
                                          ~/.defenseclaw/splunk-bridge/ from the
                                          wheel/repo bundle.  [default: refresh-bundle]
          --non-interactive               Use flags instead of prompts
          --help                          Show this message and exit.

        Commands:
          dashboards  Create/update Splunk Observability Cloud dashboards.
        """);

    /// <summary>
    /// The definition the catalog would build for <c>splunk</c> with the 0.8.10 help: group pages, Windows policy, synthetic
    /// secrets, secret routes, then the walkthrough — the order <c>WizardCatalog.LoadDetailAsync</c> runs them in.
    /// </summary>
    public static WizardDefinition Splunk()
    {
        var parsed = SetupHelpParser.Parse(SplunkHelp);
        var subcommands = parsed.Commands.ToDictionary(c => c.Name, c => new ParsedHelp { Summary = c.Summary });

        var steps = WizardStepFactory.BuildGroup(parsed, subcommands);
        steps = WizardWindowsPolicy.Filter("splunk", steps);
        steps = WizardSyntheticSecrets.Add("splunk", steps);
        steps = SecretRoutes.Annotate("splunk", steps);
        steps = WizardWalkthroughs.Apply("splunk", steps, SplunkHelp);

        return new WizardDefinition
        {
            Target = "splunk",
            Title = "Splunk",
            Group = WizardGroups.Observability,
            Description = parsed.Summary,
            Steps = steps,
            PlatformStatus = parsed.PlatformStatus,
            IsDetailLoaded = true,
            HelpText = SplunkHelp,
            CrossValidator = WizardWindowsPolicy.CrossValidatorFor("splunk"),
        };
    }

    /// <summary>The 0.8.10 <c>setup local-observability --help</c> screen, verbatim (only the line endings are normalised).</summary>
    public static readonly string LocalObservabilityHelp = LineEndings.Normalize("""
        Usage: defenseclaw setup local-observability [OPTIONS] [COMMAND] [ARGS]...

          Drive the bundled local observability stack.

          Provides a one-command path to the same compose stack that historically
          lived under ``deploy/observability/``. Subcommands:

            up       Start the stack, wait for readiness, wire config.yaml
            down     Stop containers, keep volumes
            reset    Stop + wipe all metric / log / trace data volumes
            status   Show compose ps + per-service readiness probes
            logs     Tail logs for one or all services
            url      Print the Grafana / Prometheus / Tempo / Loki URLs

          Bare invocation is an alias for ``up`` so ``defenseclaw setup local-
          observability`` matches the ergonomics of ``setup splunk --logs``.

        Options:
          --help  Show this message and exit.

        Commands:
          down    Stop the stack (volumes preserved).
          env     Print environment values that point a gateway at the local...
          logs    Tail logs from the running stack.
          reset   Stop the stack and drop all persisted metric / log / trace...
          status  Show compose ps and per-service readiness probes.
          up      Start the stack, wait for readiness, and wire the gateway config.
          url     Print the Grafana / Prometheus / Tempo / Loki URLs.
        """);

    /// <summary>The 0.8.10 <c>--help</c> screen of each <c>setup local-observability</c> subcommand, verbatim, by verb.</summary>
    public static readonly IReadOnlyDictionary<string, string> LocalObservabilitySubcommandHelp = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["up"] = LineEndings.Normalize("""
            Usage: defenseclaw setup local-observability up [OPTIONS]

              Start the stack, wait for readiness, and wire the gateway config.

            Options:
              --timeout INTEGER               Readiness wait budget (seconds) for the
                                              stack's OTLP + Grafana ports.  [default:
                                              180]
              --no-wait                       Skip the readiness wait (container ps only).
              --no-config                     Do not write config.yaml. Useful for 'just
                                              start the containers' flows where a
                                              different canonical destination already owns
                                              routing.
              --endpoint TEXT                 Override the OTLP endpoint stamped into
                                              config.yaml (default: native stack
                                              contract).
              --signals TEXT                  Comma-separated canonical signals to enable
                                              (traces,metrics,logs).  [default:
                                              traces,metrics,logs]
              --service-name TEXT             Value to stamp into observability.resource.a
                                              ttributes.service.name.  [default:
                                              defenseclaw]
              --refresh-bundle / --no-refresh-bundle
                                              Before starting the stack, refresh
                                              ~/.defenseclaw/observability-stack/ from the
                                              wheel/repo bundle so newly-shipped
                                              controller / compose changes take effect.
                                              Operator-editable surfaces (Grafana
                                              dashboards, Prometheus rules,
                                              Loki/Tempo/OTel-Collector configs) are
                                              refreshed by default; pass --no-refresh-
                                              config to preserve local edits. If the stack
                                              is already running, it will be stopped,
                                              refreshed, and restarted automatically.
                                              [default: refresh-bundle]
              --refresh-config / --no-refresh-config
                                              When refreshing the bundle, overwrite
                                              operator-editable surfaces (grafana/,
                                              prometheus/, loki/, tempo/, otel-collector/)
                                              with the bundled versions. Pass --no-
                                              refresh-config to preserve local dashboard /
                                              rule / config edits.  [default: refresh-
                                              config]
              --help                          Show this message and exit.
            """),
        ["down"] = LineEndings.Normalize("""
            Usage: defenseclaw setup local-observability down [OPTIONS]

              Stop the stack (volumes preserved).

            Options:
              --disable-config  Also disable the canonical local-observability
                                destination.
              --help            Show this message and exit.
            """),
        ["reset"] = LineEndings.Normalize("""
            Usage: defenseclaw setup local-observability reset [OPTIONS]

              Stop the stack and drop all persisted metric / log / trace volumes.

            Options:
              --yes   Skip the destructive-action confirmation prompt.
              --help  Show this message and exit.
            """),
        ["status"] = LineEndings.Normalize("""
            Usage: defenseclaw setup local-observability status [OPTIONS]

              Show compose ps and per-service readiness probes.

            Options:
              --help  Show this message and exit.
            """),
        ["logs"] = LineEndings.Normalize("""
            Usage: defenseclaw setup local-observability logs [OPTIONS]

              Tail logs from the running stack.

            Options:
              --service TEXT          Compose service to target (default: all).
              --follow / --no-follow  Stream logs until Ctrl+C.
              --help                  Show this message and exit.
            """),
        ["url"] = LineEndings.Normalize("""
            Usage: defenseclaw setup local-observability url [OPTIONS]

              Print the Grafana / Prometheus / Tempo / Loki URLs.

            Options:
              --json  Emit machine-readable JSON.
              --help  Show this message and exit.
            """),
        ["env"] = LineEndings.Normalize("""
            Usage: defenseclaw setup local-observability env [OPTIONS]

              Print environment values that point a gateway at the local collector.

            Options:
              --json  Emit machine-readable JSON.
              --help  Show this message and exit.
            """),
    };

    /// <summary>
    /// The definition the catalog would build for <c>local-observability</c> from the 0.8.10 help: the group pages over the real
    /// subcommand screens, then the Windows policy, secret routes and walkthroughs, in the order <c>WizardCatalog.LoadDetailAsync</c> runs them.
    /// </summary>
    public static WizardDefinition LocalObservability()
    {
        var parsed = SetupHelpParser.Parse(LocalObservabilityHelp);
        var subcommands = parsed.Commands.ToDictionary(
            c => c.Name,
            c => SetupHelpParser.Parse(LocalObservabilitySubcommandHelp[c.Name], commandDepth: 2));

        var steps = WizardStepFactory.BuildGroup(parsed, subcommands);
        steps = WizardWindowsPolicy.Filter("local-observability", steps);
        steps = WizardSyntheticSecrets.Add("local-observability", steps);
        steps = SecretRoutes.Annotate("local-observability", steps);
        steps = WizardWalkthroughs.Apply("local-observability", steps, LocalObservabilityHelp);

        return new WizardDefinition
        {
            Target = "local-observability",
            Title = "Local observability stack",
            Group = WizardGroups.Observability,
            Description = parsed.Summary,
            Steps = steps,
            PlatformStatus = parsed.PlatformStatus,
            IsDetailLoaded = true,
            HelpText = LocalObservabilityHelp,
        };
    }

    /// <summary>
    /// The 0.8.10 <c>setup splunk dashboards --help</c> screen: the group's docstring and its three commands, in the alphabetical order Click lists
    /// them (the wizard has to reorder them). Laid out as Click prints it, built from the option definitions in
    /// <c>commands/cmd_setup_splunk_o11y_dashboards.py</c>.
    /// </summary>
    public static readonly string DashboardsGroupHelp = LineEndings.Normalize("""
        Usage: defenseclaw setup splunk dashboards [OPTIONS] COMMAND [ARGS]...

          Create or update DefenseClaw Splunk Observability Cloud dashboards.

          The command uses the Terraform bundle shipped with DefenseClaw and stores
          Terraform working files under ``~/.defenseclaw/splunk-o11y-dashboards`` by
          default. Re-running from the same state path updates the same O11y objects.

        Options:
          --help  Show this message and exit.

        Commands:
          apply    Create or update the O11y dashboards.
          destroy  Destroy O11y objects managed by the selected Terraform state.
          plan     Show Terraform changes for the O11y dashboard bundle.
        """);

    /// <summary>The options <c>plan</c>, <c>apply</c> and <c>destroy</c> share (<c>_dashboard_options</c>), up to where <c>apply</c> and <c>destroy</c> add <c>--yes</c>.</summary>
    private const string DashboardsOptions =
        "  --api-url TEXT                  Splunk O11y API URL. Defaults from SFX_API_URL\n" +
        "                                  or the configured OTLP ingest realm.\n" +
        "  --o11y-api-token TEXT           Splunk O11y API access token. Prefer the\n" +
        "                                  SFX_AUTH_TOKEN environment variable so the\n" +
        "                                  secret never appears in shell history or\n" +
        "                                  process listings.\n" +
        "  --name-prefix TEXT              Label dashboard groups, dashboards, and\n" +
        "                                  detectors. Useful for smoke tests.\n" +
        "  --with-detectors / --dashboards-only\n" +
        "                                  Create Splunk detectors in addition to\n" +
        "                                  dashboards.  [default: dashboards-only]\n" +
        "  --enable-detectors              Create detector rules enabled. By default\n" +
        "                                  created detectors are disabled.\n" +
        "  --detector-notification TEXT    Detector notification target, e.g.\n" +
        "                                  \"Email,secops@example.com\". Repeatable.\n" +
        "  --work-dir DIRECTORY            Terraform working directory. Defaults under\n" +
        "                                  the DefenseClaw data directory.\n" +
        "  --state FILE                    Terraform state file path. Defaults under the\n" +
        "                                  DefenseClaw data directory.\n" +
        "  --terraform-bin TEXT            Terraform executable to run.  [default:\n" +
        "                                  terraform]\n" +
        "  --plugin-dir DIRECTORY          Optional Terraform provider plugin directory\n" +
        "                                  for offline/cached provider installs.\n" +
        "  --skip-init                     Skip `terraform init` in the working\n" +
        "                                  directory.\n" +
        "  --skip-validate                 Skip `terraform validate` after init.\n" +
        "  --timeout INTEGER               Timeout in seconds for each Terraform\n" +
        "                                  subprocess.  [default: 900]\n";

    private static string DashboardsVerbScreen(string verb, string summary, string extraOptions) =>
        LineEndings.Normalize(
            $"Usage: defenseclaw setup splunk dashboards {verb} [OPTIONS]\n\n  {summary}\n\nOptions:\n{DashboardsOptions}{extraOptions}" +
            "  --help                          Show this message and exit.\n");

    /// <summary>The <c>--help</c> screen of each <c>setup splunk dashboards</c> command, by verb. Only <c>apply</c> and <c>destroy</c> have <c>--yes</c>.</summary>
    public static readonly IReadOnlyDictionary<string, string> DashboardsVerbHelp = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["plan"] = DashboardsVerbScreen("plan", "Show Terraform changes for the O11y dashboard bundle.", string.Empty),
        ["apply"] = DashboardsVerbScreen(
            "apply",
            "Create or update the O11y dashboards.",
            "  --yes                           Apply without an additional confirmation\n                                  prompt.\n"),
        ["destroy"] = DashboardsVerbScreen(
            "destroy",
            "Destroy O11y objects managed by the selected Terraform state.",
            "  --yes                           Destroy without an additional confirmation\n                                  prompt.\n"),
    };

    /// <summary>
    /// The definition the catalog would build for <c>splunk dashboards</c> from the 0.8.10 help: the group pages over the three verbs' real
    /// screens, then the Windows policy, secret routes and walkthroughs, in the order <c>WizardCatalog.LoadDetailAsync</c> runs them.
    /// </summary>
    /// <param name="editHelp">Edits every screen before it is parsed (a CLI that words something differently); null leaves them as they are.</param>
    /// <param name="extraVerbs">Verbs a newer CLI adds to the group, each with its <c>--help</c> screen; null for none.</param>
    public static WizardDefinition Dashboards(Func<string, string>? editHelp = null, IReadOnlyDictionary<string, string>? extraVerbs = null)
    {
        string Edit(string text) => editHelp is null ? text : editHelp(text);

        var groupHelp = Edit(DashboardsGroupHelp) +
                        string.Concat((extraVerbs ?? new Dictionary<string, string>()).Keys.Select(v => $"\n  {v,-8} A verb a newer CLI adds."));
        var screens = new Dictionary<string, string>(DashboardsVerbHelp, StringComparer.Ordinal);
        foreach (var (verb, screen) in extraVerbs ?? new Dictionary<string, string>())
        {
            screens[verb] = screen;
        }

        var group = SetupHelpParser.Parse(groupHelp, commandDepth: 2);
        var verbs = group.Commands.ToDictionary(c => c.Name, c => SetupHelpParser.Parse(Edit(screens[c.Name]), commandDepth: 3));

        var steps = WizardStepFactory.BuildGroup(group, verbs);
        steps = WizardWindowsPolicy.Filter(SplunkDashboards.Target, steps);
        steps = WizardSyntheticSecrets.Add(SplunkDashboards.Target, steps);
        steps = SecretRoutes.Annotate(SplunkDashboards.Target, steps);
        steps = WizardWalkthroughs.Apply(SplunkDashboards.Target, steps, groupHelp);

        return new WizardDefinition
        {
            Target = SplunkDashboards.Target,
            Title = SplunkDashboards.Title,
            Group = WizardGroups.Observability,
            Description = SplunkDashboards.Description,
            Steps = steps,
            PlatformStatus = group.PlatformStatus,
            IsDetailLoaded = true,
            HelpText = groupHelp,
        };
    }

    /// <summary>The curated + filtered + annotated definition the catalog would build for claude-code.</summary>
    public static WizardDefinition ClaudeCode()
    {
        var help = SetupHelpParser.Parse(ClaudeCodeHelp);
        var (steps, curated) = WizardStepFactory.Build("claude-code", help);
        steps = WizardWindowsPolicy.Filter("claude-code", steps);
        steps = SecretRoutes.Annotate("claude-code", steps);

        return new WizardDefinition
        {
            Target = "claude-code",
            Title = "Claude Code",
            Group = WizardGroups.Connectors,
            Description = help.Summary,
            Steps = steps,
            PlatformStatus = help.PlatformStatus,
            PlatformNote = help.PlatformNote,
            IsCurated = curated,
            IsDetailLoaded = true,
        };
    }

    public static WizardDefinition Llm()
    {
        var help = SetupHelpParser.Parse(LlmHelp);
        var (steps, curated) = WizardStepFactory.Build("llm", help);
        steps = WizardWindowsPolicy.Filter("llm", steps);
        steps = SecretRoutes.Annotate("llm", steps);

        return new WizardDefinition
        {
            Target = "llm",
            Title = "LLM providers",
            Group = WizardGroups.Credentials,
            Steps = steps,
            PlatformStatus = help.PlatformStatus,
            IsCurated = curated,
            IsDetailLoaded = true,
        };
    }

    /// <summary>A wizard's answers as the window starts them: every field at its default.</summary>
    public static WizardValues StartingValues(WizardDefinition definition)
    {
        var values = new WizardValues();
        foreach (var field in definition.AllFields)
        {
            values[field.Id] = field.DefaultValue;
        }

        return values;
    }

    public static ConfigDocument Config(string yaml) => ConfigStore.Parse(yaml);

    public static WizardField Find(WizardDefinition definition, string flag) =>
        definition.AllFields.Single(f => f.Flag == flag);
}
