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

    /// <summary>Command words that name something Windows does not run: sandboxes, OpenClaw, ZeptoClaw and the Docker stacks.</summary>
    private static readonly string[] HiddenWords = { "sandbox", "openclaw", "zeptoclaw", "docker", "local-observability", "local-stack" };

    /// <summary>
    /// True when the command palette must not list the command at <paramref name="path"/> (its nouns, without the executable):
    /// a noun that names a sandbox / OpenClaw / ZeptoClaw / Docker feature, or a summary that says it needs one. The same
    /// things the Setup hub puts in "not available", for the same reasons.
    /// </summary>
    /// <param name="path">The command's nouns, e.g. <c>setup local-observability up</c>.</param>
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

    /// <summary>Why a hub card cannot be launched here, or null when it can.</summary>
    public static string? UnavailableReason(string target, PlatformStatus status)
    {
        switch (target)
        {
            case "registry":
                return "An interactive wizard that needs a real terminal — with none attached it exits before changing anything. " +
                       "Add, sync, approve and remove registries from the Registries panel (Discover) instead.";

            case "local-observability":
                return "Runs the bundled Prometheus/Loki/Tempo/Grafana stack in Docker, which needs Docker Desktop with the Hyper-V " +
                       "backend. Per-user Windows installs cannot provide that.";

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
        if (target != "splunk" || field.Id != "subcommand" || !field.Choices.Any(c => c.Value == "dashboards"))
        {
            return field;
        }

        return new WizardField
        {
            Id = field.Id,
            Label = field.Label,
            Kind = field.Kind,
            Flag = field.Flag,
            NegativeFlag = field.NegativeFlag,
            Help = field.Help,
            Choices = field.Choices.Where(c => c.Value != "dashboards").ToArray(),
            DefaultValue = field.DefaultValue,
            BaselineValue = field.BaselineValue,
            BaselineSource = field.BaselineSource,
            AllowEmptyWhenChanged = field.AllowEmptyWhenChanged,
            Credential = field.Credential,
            IsPositional = field.IsPositional,
            PositionalOrder = field.PositionalOrder,
            IsRequired = field.IsRequired,
            Placeholder = field.Placeholder,
            VisibleWhenFieldId = field.VisibleWhenFieldId,
            VisibleWhenValues = field.VisibleWhenValues,
        };
    }

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
