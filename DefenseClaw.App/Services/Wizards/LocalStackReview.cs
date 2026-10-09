using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.Services.Wizards;

/// <summary>
/// What a review of a <c>setup local-observability</c> command says beyond its tier and its argv, for both surfaces that review one
/// (the wizard's last page and the palette's confirmation), so they cannot tell different stories. Every sentence comes from what the
/// installed CLI's source does (<c>commands/cmd_setup_local_observability.py</c>, <c>observability/local_stack.py</c>, and the
/// <c>setup</c> group's own result callback in <c>commands/cmd_setup.py</c>; see <see cref="WizardWindowsPolicy.LocalObservabilityTarget"/>).
/// </summary>
public static class LocalStackReview
{
    /// <summary>The heading of the bar that says the stack's own files are refreshed from the bundled copy first.</summary>
    public const string RefreshesFilesTitle = "Refreshes the stack's files";

    /// <summary>The heading of the bar that says the gateway keeps exporting to a stack that is no longer running.</summary>
    public const string ExportingContinuesTitle = "Exporting continues";

    /// <summary>The heading of the bar that says the stack's data is about to be deleted.</summary>
    public const string DeletesDataTitle = "Deletes the stack's data";

    /// <summary>The heading of the bar that carries the Docker look's warnings.</summary>
    public const string DockerMayRefuseTitle = "Docker may refuse this";

    /// <summary>
    /// True for a command under <c>setup local-observability</c> (the verb after it is <paramref name="verb"/>). The group run bare, with
    /// no verb, is its <c>up</c>: the CLI's group callback invokes <c>up</c> when no subcommand is named
    /// (<c>commands/cmd_setup_local_observability.py</c>), whatever a one-line description of the bare command says, so it is judged as one.
    /// </summary>
    public static bool IsStackCommand(IReadOnlyList<string> argv, out string verb)
    {
        ArgumentNullException.ThrowIfNull(argv);

        verb = string.Empty;
        if (argv.Count < 2 ||
            !string.Equals(argv[0], "setup", StringComparison.Ordinal) ||
            !string.Equals(argv[1], WizardWindowsPolicy.LocalObservabilityTarget, StringComparison.Ordinal))
        {
            return false;
        }

        verb = argv.Count == 2 ? "up" : argv[2];
        return true;
    }

    /// <summary>
    /// Whether running <paramref name="argv"/> restarts the live gateway, for a command of the stack; null when this has no opinion (not a
    /// command of the stack, or a verb a newer CLI adds), and the general rule for <c>setup</c> commands applies.
    /// <para>
    /// The verbs themselves never restart anything. What does is the <c>setup</c> group's result callback
    /// (<c>_auto_restart_sidecar_after_setup</c>, <c>cmd_setup.py</c>): after any <c>setup</c> subcommand, nested groups included, that
    /// changed config.yaml it restarts a running gateway (and says "Gateway is not running" when there is none). So it is exactly the verbs
    /// that rewrite config.yaml: <c>up</c>, which writes the destination unless given <c>--no-config</c> (or <c>--no-wait</c>, which skips the
    /// readiness proof the write waits for), and <c>down --disable-config</c>. <c>reset</c>, plain <c>down</c> and the four reads never
    /// touch it. Like the general rule this errs toward "yes": a run that finds config.yaml unchanged restarts nothing.
    /// </para>
    /// </summary>
    public static bool? RestartsGateway(IReadOnlyList<string> argv)
    {
        ArgumentNullException.ThrowIfNull(argv);

        if (!IsStackCommand(argv, out var verb))
        {
            return null;
        }

        return verb switch
        {
            "up" => !Has(argv, "--no-config") && !Has(argv, "--no-wait"),
            "down" => Has(argv, "--disable-config"),
            "reset" or "status" or "logs" or "url" or "env" => false,
            _ => null,
        };
    }

    /// <summary>
    /// What one verb does, in a sentence, for the line a wizard's review shows where it says what changed from the current configuration:
    /// the stack's verbs are not edits of a setting, so "nothing was changed, running this re-applies it" would be wrong for a reset. Empty
    /// for a command that is not the stack's or a verb this app does not know (the review then says what it says for any wizard).
    /// </summary>
    public static string Summary(IReadOnlyList<string> argv)
    {
        ArgumentNullException.ThrowIfNull(argv);

        if (!IsStackCommand(argv, out var verb))
        {
            return string.Empty;
        }

        return verb switch
        {
            "up" => "Starts the stack's containers (Prometheus, Loki, Tempo, Grafana and the OpenTelemetry collector) on this machine.",
            "down" => "Stops and removes the stack's containers and network. Their data volumes are kept.",
            "reset" => "Stops the stack's containers and deletes their data volumes.",
            "status" => "Only reads: shows the stack's containers and whether each of its ports answers.",
            "logs" => "Only reads: prints the recent log lines of the stack's containers.",
            "url" => "Only reads: prints the addresses of the stack's Grafana, Prometheus, Tempo, Loki and OTLP ports.",
            "env" => "Only reads: prints the environment variables that point a process at the stack's OTLP collector.",
            _ => string.Empty,
        };
    }

    /// <summary>
    /// The bars a review of <paramref name="argv"/> carries <i>besides</i> the gateway restart (which the surface adds from
    /// <see cref="RestartsGateway"/>): nothing for a command that is not the local stack's, otherwise only what that verb with those flags
    /// really does. <paramref name="docker"/> is the last Docker look (null when there is none); its warnings are what the CLI's own Docker
    /// check would refuse on this machine, shown for the verbs that reach Docker.
    /// </summary>
    public static IReadOnlyList<CommandReviewWarning> Warnings(IReadOnlyList<string> argv, DockerStatus? docker)
    {
        ArgumentNullException.ThrowIfNull(argv);

        if (!IsStackCommand(argv, out var verb))
        {
            return Array.Empty<CommandReviewWarning>();
        }

        var warnings = new List<CommandReviewWarning>();
        switch (verb)
        {
            case "up":
                // The bundle refresh is on by default; --no-refresh-bundle turns all of it off, --no-refresh-config keeps the operator's edits.
                if (!Has(argv, "--no-refresh-bundle"))
                {
                    var overwrites = !Has(argv, "--no-refresh-config");
                    warnings.Add(new CommandReviewWarning(
                        RefreshesFilesTitle,
                        "First refreshes ~/.defenseclaw/observability-stack/ from the copy bundled with DefenseClaw. " +
                        (overwrites
                            ? "That overwrites any edits you made to its Grafana dashboards, Prometheus rules and Loki, Tempo and collector configs (--no-refresh-config keeps them). "
                            : "Your edits to its dashboards, rules and configs are kept (--no-refresh-config). ") +
                        "A stack that is already running is stopped, refreshed and started again."));
                }

                break;

            case "down":
                if (!Has(argv, "--disable-config"))
                {
                    warnings.Add(StillExporting(" (--disable-config does that)."));
                }

                break;

            case "reset":
                warnings.Add(new CommandReviewWarning(
                    DeletesDataTitle,
                    "Stops the stack and drops its Prometheus, Loki, Tempo and Grafana data volumes: every stored metric, log and trace, and " +
                    "Grafana's own data. This cannot be undone." +
                    (Has(argv, "--yes")
                        ? " This review stands in for the CLI's own \"Continue?\" question, which is why --yes is on the command."
                        : string.Empty)));
                warnings.Add(StillExporting("."));
                break;
        }

        if (docker is { Warnings.Count: > 0 } && WizardWindowsPolicy.CommandNeedsDocker(argv))
        {
            warnings.Add(new CommandReviewWarning(
                DockerMayRefuseTitle,
                "The CLI checks Docker itself before it changes anything, and from here it looks like it would refuse: " +
                string.Join(" ", docker.Warnings)));
        }

        return warnings;
    }

    /// <summary>
    /// Stopping the stack leaves its destination enabled in config.yaml (only <c>down --disable-config</c> turns it off), so the gateway goes on
    /// trying to export to an endpoint nothing answers on.
    /// </summary>
    private static CommandReviewWarning StillExporting(string ending) =>
        new(
            ExportingContinuesTitle,
            "The local-observability destination stays enabled in config.yaml, so the gateway keeps trying to export to the stack " +
            "until it is started again or the destination is disabled" + ending);

    /// <summary>
    /// True when <paramref name="flag"/> is on the command as an option. Only what comes before a <c>--</c> can be one, and a token that is the
    /// value of the option before it does not count: <c>up --service-name --no-config</c> names the service "--no-config" and still writes
    /// config.yaml (the same rule <see cref="CommandTiers.IsStandaloneFlag"/> applies to the preview flags).
    /// </summary>
    private static bool Has(IReadOnlyList<string> argv, string flag)
    {
        var options = argv.TakeWhile(a => !string.Equals(a, "--", StringComparison.Ordinal)).ToArray();
        for (var i = 0; i < options.Length; i++)
        {
            if (string.Equals(options[i], flag, StringComparison.Ordinal) && CommandTiers.IsStandaloneFlag(options, i))
            {
                return true;
            }
        }

        return false;
    }
}
