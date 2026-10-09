namespace DefenseClaw.App.Services.Wizards;

/// <summary>
/// What the Setup hub and the wizards deliberately do <b>not</b> offer on Windows, and why.
/// <para>
/// All of it comes from the command catalog's "not relevant on native Windows" section and the
/// installed CLI's own gates, not from this app's taste. Nothing here is silent: a flag that is
/// suppressed simply is not rendered (the CLI's default applies), and a whole target that cannot
/// work is shown in the hub's "not available" group with its reason and where to go instead.
/// </para>
/// </summary>
public static class WizardWindowsPolicy
{
    /// <summary>
    /// <c>--with-local-stack</c> on the hook connectors: brings up the Docker Prom/Loki/Tempo/Grafana
    /// stack after saving, and on 0.8.10 the command raises an uncaught TypeError <i>after</i> the config
    /// is saved and the gateway restarted (catalog §4). Default is <c>--no-local-stack</c>, so
    /// leaving the flag off is exactly right.
    /// </summary>
    private static readonly IReadOnlySet<string> SuppressedEverywhere = new HashSet<string>(StringComparer.Ordinal)
    {
        "--with-local-stack",
    };

    /// <summary>
    /// <c>setup splunk</c>. The installed CLI has a native Windows controller for the local pipeline
    /// (<c>--logs</c>, <c>local_splunk.py</c>: argv-only Docker Compose, no bash or WSL), so <c>--logs</c>, its
    /// license acceptance and <c>--refresh-bundle</c> are <b>offered, gated</b> on a read-only Docker probe
    /// (<see cref="IDockerProbe"/>) rather than hidden. Still suppressed: the S3 exporter (its AWS credentials are read
    /// from the environment with no in-app route, so the wizard could not supply them), <c>--show-credentials</c>
    /// (prints the generated HEC token and a bootstrap secret into the output), and <c>dashboards</c> (Terraform plus a
    /// Splunk O11y API token, and not a Docker question, so it cannot be gated the same way).
    /// </summary>
    private static readonly IReadOnlySet<string> SplunkSuppressed = new HashSet<string>(StringComparer.Ordinal)
    {
        "--s3-export",
        "--s3-bucket",
        "--s3-prefix",
        "--aws-region",
        "--show-credentials",
    };

    /// <summary>
    /// <c>setup local-observability</c>, the bundled Prometheus / Loki / Tempo / Grafana stack, is <b>offered, gated</b> on the same
    /// read-only Docker probe (<see cref="IDockerProbe"/>, through <see cref="LocalStackAvailability"/>): the Setup card and the palette's
    /// rows for <c>up | down | status | logs | reset</c> (and the bare group, which the CLI answers with <c>up</c>) are available while
    /// Compose v2 is there and the engine answers, and otherwise show the probe's reason; <c>url</c> never reaches Docker, so its row is
    /// always available. The palette's rows are the connected runtime's own TUI registry entries, which list the stack on Windows for the
    /// reason below. It used to be listed here as "needs Docker Desktop with the Hyper-V backend; per-user Windows
    /// installs cannot provide that" — a true description of what the CLI's preflight enforces on some machines, but wrong as a static
    /// hide: the CLI checks it at run time and refuses only where it applies (the probe mirrors it as warnings, below), and a machine with a
    /// machine-wide Hyper-V Docker Desktop runs the stack. Evidence (DefenseClaw 0.8.10, read from the install):
    /// <list type="bullet">
    ///   <item><c>defenseclaw/platform_support.py</c> (lines 219-223): <c>local_observability_stack_supported()</c> is true for
    ///     <c>windows</c>, <c>darwin</c> and <c>linux</c>; it is what hides the command in the TUI (<c>tui/registry.py</c>,
    ///     <c>tui/panels/setup.py</c>) and the destination in <c>setup observability</c>, so on Windows the TUI offers it too.</item>
    ///   <item><c>commands/cmd_setup_local_observability.py</c> (line 17: "Native Docker Compose lifecycle for the bundled local OTel
    ///     stack"): the verbs that touch Docker (<c>up</c>, <c>down</c>, <c>reset</c>, <c>status</c>, <c>logs</c>) go through
    ///     <c>observability/local_stack.py</c>'s <c>LocalStackController</c>, which runs
    ///     <c>docker compose --project-name defenseclaw-observability</c> as an argument vector — no bash, no WSL — after
    ///     <c>validate_native_docker_preflight</c>: Compose v2, then a daemon that answers (<c>docker info</c>), Linux containers, and
    ///     the Windows certification (x64, Pro/Enterprise/Education, Docker Desktop, machine-wide install, Hyper-V rather than WSL 2:
    ///     <c>_validate_windows_docker_certification</c>, <c>local_stack.py</c> lines 373-427). The probe reports the first two as the gate
    ///     and the rest as warnings, because the CLI is
    ///     the authority and refuses before it changes anything — the same arrangement as local Splunk's <c>--logs</c>
    ///     (<c>local_splunk.py</c>, same preflight). <c>url</c> and <c>env</c> build no controller: they print constants.</item>
    ///   <item>What each verb touches, from that source: <c>url</c> and <c>env</c> never reach Docker;
    ///     <c>status</c> runs <c>docker compose ps</c> and probes the loopback ports, <c>logs</c> runs <c>docker compose logs --tail 200</c>,
    ///     and neither changes anything (<see cref="DefenseClaw.Core.Cli.CommandTiers.IsReadOnlyLeaf"/>); <c>up</c> starts containers, refreshes <c>~/.defenseclaw/observability-stack/</c>
    ///     and writes the <c>local-observability</c> destination into config.yaml; <c>down</c> stops and removes them
    ///     (<c>--disable-config</c> also edits config.yaml); <c>reset</c> drops the four data volumes and asks "Continue?" unless given
    ///     <c>--yes</c>. The verbs restart nothing themselves, but the <c>setup</c> group's result callback
    ///     (<c>cmd_setup.py</c>, <c>_auto_restart_sidecar_after_setup</c>) restarts a running gateway after any subcommand that changed
    ///     config.yaml: that is <c>up</c> (unless <c>--no-config</c> / <c>--no-wait</c>) and <c>down --disable-config</c>
    ///     (<see cref="LocalStackReview.RestartsGateway"/>).</item>
    /// </list>
    /// </summary>
    public const string LocalObservabilityTarget = "local-observability";

    /// <summary>
    /// Command words that name something Windows does not run: sandboxes, OpenClaw, ZeptoClaw and anything the CLI's own summary says needs
    /// Docker or the <c>--with-local-stack</c> flow. <c>local-observability</c> is not here: see <see cref="LocalObservabilityTarget"/>.
    /// </summary>
    private static readonly string[] HiddenWords = { "sandbox", "openclaw", "zeptoclaw", "docker", "local-stack" };

    /// <summary>
    /// True when the command palette must not list the command at <paramref name="path"/> (its nouns, without the executable):
    /// a noun that names a sandbox / OpenClaw / ZeptoClaw / Docker feature, or a summary that says it needs one. The same
    /// things the Setup hub puts in "not available", for the same reasons.
    /// </summary>
    /// <param name="path">The command's nouns, e.g. <c>sandbox list</c>.</param>
    /// <param name="summary">Its one-line help summary; may be empty.</param>
    public static bool HidesCommand(IReadOnlyList<string> path, string summary)
    {
        ArgumentNullException.ThrowIfNull(path);

        foreach (var word in HiddenWords)
        {
            if (path.Any(noun => noun.Contains(word, StringComparison.OrdinalIgnoreCase)) ||
                (summary ?? string.Empty).Contains(word, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return path.Count == 2 && string.Equals(path[0], "setup", StringComparison.Ordinal) &&
               UnavailableReason(path[1], PlatformStatus.Certified) is not null;
    }

    /// <summary>
    /// True for a setup target whose card is offered only while Docker can run it: the Setup hub asks the shared
    /// <see cref="LocalStackAvailability"/> (the live probe) and shows its reason, instead of a fixed one from
    /// <see cref="UnavailableReason"/>. Only the observability stack today; local Splunk is one pipeline of a wizard that has other
    /// pipelines, so its gate is the wizard's own page (<see cref="WizardGuideRequirement.Docker"/>).
    /// </summary>
    public static bool NeedsDocker(string target) =>
        string.Equals(target, LocalObservabilityTarget, StringComparison.Ordinal);

    /// <summary>
    /// True for a palette command that runs Docker Compose and so is enabled only while the probe says it can: every verb of
    /// <c>setup local-observability</c> except <c>url</c> and <c>env</c>, which print constants and never reach Docker
    /// (<c>commands/cmd_setup_local_observability.py</c>). A verb a newer CLI adds under it is assumed to need Docker, and so is the group
    /// run bare, which the CLI answers with its <c>up</c>.
    /// </summary>
    /// <param name="argv">The command's nouns, e.g. <c>setup local-observability up</c>; the registry's <c>reset</c> row also carries its <c>--yes</c>.</param>
    public static bool CommandNeedsDocker(IReadOnlyList<string> argv)
    {
        ArgumentNullException.ThrowIfNull(argv);

        if (argv.Count < 2 || !string.Equals(argv[0], "setup", StringComparison.Ordinal) || !NeedsDocker(argv[1]))
        {
            return false;
        }

        return argv.Count == 2 ||
               (!argv[2].Equals("url", StringComparison.Ordinal) && !argv[2].Equals("env", StringComparison.Ordinal));
    }

    /// <summary>
    /// Why a hub card cannot be launched here, or null when it can. A target that needs Docker (<see cref="NeedsDocker"/>) is not
    /// decided here: whether Docker is ready is a fact about this machine right now, not about the platform.
    /// </summary>
    public static string? UnavailableReason(string target, PlatformStatus status)
    {
        switch (target)
        {
            case "registry":
                return "An interactive wizard that needs a real terminal — with none attached it exits before changing anything. " +
                       "Add, sync, approve and remove registries from the Registries panel (Discover) instead.";

            case "gateway":
                return "Configures the OpenClaw fleet uplink (host, port, token). OpenClaw is unsupported on Windows, and a " +
                       "standalone install has no fleet gateway to configure.";
        }

        return status == PlatformStatus.Unsupported
            ? "The CLI reports this integration as unsupported on Windows and refuses to run it before writing anything."
            : null;
    }

    /// <summary>
    /// Removes the fields (and, for a group, the subcommand pages) this platform must not offer, then drops
    /// any page left empty. A page that was empty to begin with (a subcommand with no options) is kept.
    /// </summary>
    public static IReadOnlyList<WizardStep> Filter(string target, IReadOnlyList<WizardStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);

        var result = new List<WizardStep>(steps.Count);
        foreach (var step in steps)
        {
            // A group's subcommand pages are gated on its "subcommand" field; a suppressed subcommand's
            // pages go with it.
            if (target == "splunk" && step.VisibleWhenValues.Contains("dashboards", StringComparer.Ordinal))
            {
                continue;
            }

            var kept = step.Fields.Where(f => !IsSuppressed(target, f)).ToArray();
            var choicesTrimmed = kept.Select(f => TrimChoices(target, f)).ToArray();

            if (choicesTrimmed.Length == 0 && step.Fields.Count > 0)
            {
                continue;
            }

            // With "dashboards" gone, splunk's "pick a subcommand" page has a single choice — the guided
            // default. A page that asks nothing is not a page; the field's gate still reads as "".
            if (target == "splunk" && choicesTrimmed.Length == 1 && choicesTrimmed[0].Id == "subcommand" &&
                choicesTrimmed[0].Choices.Count <= 1)
            {
                continue;
            }

            result.Add(kept.Length == step.Fields.Count && choicesTrimmed.SequenceEqual(kept)
                ? step
                : new WizardStep
                {
                    Id = step.Id,
                    Title = step.Title,
                    Subtitle = step.Subtitle,
                    Fields = choicesTrimmed,
                    VisibleWhenFieldId = step.VisibleWhenFieldId,
                    VisibleWhenValues = step.VisibleWhenValues,
                    Guide = step.Guide,
                });
        }

        return result;
    }

    private static bool IsSuppressed(string target, WizardField field)
    {
        if (field.Flag is not { Length: > 0 } flag)
        {
            return false;
        }

        return SuppressedEverywhere.Contains(flag) ||
               (target == "splunk" && SplunkSuppressed.Contains(flag));
    }

    private static WizardField TrimChoices(string target, WizardField field)
    {
        if (target == LocalObservabilityTarget && field is { Id: "subcommand", Kind: WizardFieldKind.Choice })
        {
            return ShapeLocalObservabilityCommand(field);
        }

        if (target != "splunk" || field.Id != "subcommand" || !field.Choices.Any(c => c.Value == "dashboards"))
        {
            return field;
        }

        return Copy(field, field.Choices.Where(c => c.Value != "dashboards").ToArray(), field.DefaultValue, field.IsRequired);
    }

    /// <summary>The order the stack's verbs are offered in: look first, then act, with the destructive one last.</summary>
    private static readonly string[] LocalObservabilityOrder = { "status", "up", "down", "logs", "url", "env", "reset" };

    /// <summary>
    /// The "Command" page of the local observability wizard. The CLI lets <c>setup local-observability</c> run bare, as an alias for
    /// <c>up</c>; offered as a choice that would put <c>defenseclaw setup local-observability</c> on the review, hiding that it
    /// starts containers and writes config.yaml. So the page offers the real verbs only — every one of them named in the command that
    /// runs — starting on <c>status</c>, which changes nothing (the TUI's Local OTel wizard starts there too), with <c>reset</c> last.
    /// A verb a newer CLI adds keeps its place after the known ones.
    /// </summary>
    private static WizardField ShapeLocalObservabilityCommand(WizardField field)
    {
        var verbs = field.Choices.Where(c => c.Value.Length > 0).ToArray();
        if (verbs.Length == 0)
        {
            return field;
        }

        var ordered = verbs.OrderBy(c => Rank(c.Value)).ToArray();
        var start = ordered.Any(c => c.Value == "status") ? "status" : ordered[0].Value;

        return field.Choices.Count == ordered.Length && field.IsRequired &&
               string.Equals(field.DefaultValue, start, StringComparison.Ordinal) &&
               field.Choices.Select(c => c.Value).SequenceEqual(ordered.Select(c => c.Value), StringComparer.Ordinal)
            ? field
            : Copy(field, ordered, start, isRequired: true);

        static int Rank(string verb)
        {
            var index = Array.IndexOf(LocalObservabilityOrder, verb);
            return index < 0 ? int.MaxValue : index;
        }
    }

    private static WizardField Copy(WizardField field, IReadOnlyList<WizardChoice> choices, string defaultValue, bool isRequired) => new()
    {
        Id = field.Id,
        Label = field.Label,
        Kind = field.Kind,
        Flag = field.Flag,
        NegativeFlag = field.NegativeFlag,
        Help = field.Help,
        Choices = choices,
        DefaultValue = defaultValue,
        BaselineValue = field.BaselineValue,
        BaselineSource = field.BaselineSource,
        AllowEmptyWhenChanged = field.AllowEmptyWhenChanged,
        Credential = field.Credential,
        IsPositional = field.IsPositional,
        PositionalOrder = field.PositionalOrder,
        IsRequired = isRequired,
        Placeholder = field.Placeholder,
        VisibleWhenFieldId = field.VisibleWhenFieldId,
        VisibleWhenValues = field.VisibleWhenValues,
    };

    /// <summary>Cross-field checks for targets whose CLI would otherwise fall back to a prompt this app cannot answer.</summary>
    public static Func<WizardValues, string?>? CrossValidatorFor(string target) => target switch
    {
        "splunk" => ValidateSplunk,
        _ => null,
    };

    private static string? ValidateSplunk(WizardValues values)
    {
        // A subcommand (dashboards is removed, but a future one may appear) has its own pages.
        if (values["subcommand"].Length > 0)
        {
            return null;
        }

        var o11y = IsOn(values["o11y"]);
        var logs = IsOn(values["logs"]);
        var enterprise = IsOn(values["enterprise"]);

        if (!o11y && !logs && !enterprise)
        {
            return "Choose at least one pipeline: Splunk Observability Cloud, Local Splunk (Docker) or Splunk Enterprise (HEC). " +
                   "With none, the command opens an interactive wizard, which needs a real terminal.";
        }

        if (enterprise && values["hec-endpoint"].Trim().Length == 0)
        {
            return "HEC endpoint is required for Splunk Enterprise.";
        }

        // --non-interactive local Splunk refuses to start without the explicit acceptance, and consent is never assumed.
        if (logs && !IsOn(values["accept-splunk-license"]))
        {
            return "Accept the Splunk General Terms (the switch on the Local Splunk page) to enable local Splunk. " +
                   "The command refuses to run without --accept-splunk-license.";
        }

        return null;
    }

    private static bool IsOn(string value) =>
        string.Equals(value, ToggleValues.On, StringComparison.OrdinalIgnoreCase);
}
