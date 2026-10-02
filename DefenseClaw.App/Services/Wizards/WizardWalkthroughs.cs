using System.Text;
using System.Text.RegularExpressions;

namespace DefenseClaw.App.Services.Wizards;

/// <summary>
/// The guided first step of the Splunk and Galileo wizards, and the way Splunk's one long flag list is regrouped
/// per pipeline.
/// <para>
/// Two layers, kept apart on purpose. The <b>CLI layer</b> is parsed from the installed <c>--help</c> text
/// (<see cref="PipelineText"/>, <see cref="Description"/>): what each pipeline is, in the CLI's own words, so a CLI that
/// changes its mind is quoted as it now says. The <b>curated layer</b> is what the help does not say — what to have in
/// hand, where to get it, what running the command touches — and lives here as plain data. Every flag the layout names is
/// looked up in the live steps: one the installed CLI no longer has is dropped, never shipped into argv.
/// </para>
/// <para>
/// Nothing here runs anything. The walkthrough only chooses which flags are turned on and which fields are shown; the
/// review page still prints the exact argv, secrets still never reach it, and nothing runs without the review.
/// </para>
/// </summary>
public static partial class WizardWalkthroughs
{
    /// <summary>The id the guide page carries.</summary>
    public const string GuideStepId = "guide";

    /// <summary>The terms the CLI's own error message points at for local Splunk.</summary>
    public const string SplunkTermsUrl = "https://www.splunk.com/en_us/legal/splunk-general-terms.html";

    /// <summary>Docker Desktop for Windows: install guide (covers the Hyper-V and WSL 2 backends).</summary>
    public const string DockerInstallUrl = "https://docs.docker.com/desktop/setup/install/windows-install/";

    private const string O11yPipelineId = "o11y";
    private const string LogsPipelineId = "logs";
    private const string EnterprisePipelineId = "enterprise";

    /// <summary>Adds the walkthrough for <paramref name="target"/>; every other target comes back unchanged.</summary>
    public static IReadOnlyList<WizardStep> Apply(string target, IReadOnlyList<WizardStep> steps, string helpText)
    {
        ArgumentNullException.ThrowIfNull(steps);

        var help = CliText.NormalizeLineEndings(helpText ?? string.Empty);
        return target switch
        {
            "splunk" => Splunk(steps, help),
            "galileo" => Galileo(steps, help),
            _ => steps,
        };
    }

    // ------------------------------------------------------------------ the CLI's own words

    /// <summary>
    /// The per-flag paragraphs of a command description — Click's <c>\b</c> blocks, rendered as
    /// <c>  --o11y   Splunk Observability Cloud (…)</c> followed by indented continuation lines — keyed by flag. Reads only
    /// the prose above the <c>Options:</c> block, and joins each entry into one sentence run.
    /// </summary>
    public static IReadOnlyDictionary<string, string> PipelineText(string helpText)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        string? current = null;
        var builder = new StringBuilder();

        void Flush()
        {
            if (current is not null)
            {
                result[current] = builder.ToString().Trim();
            }

            current = null;
            builder.Clear();
        }

        foreach (var raw in CliText.NormalizeLineEndings(helpText ?? string.Empty).Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("Options:", StringComparison.Ordinal))
            {
                break;
            }

            if (line.Trim().Length == 0)
            {
                Flush();
                continue;
            }

            if (PipelineLine().Match(line) is { Success: true } match)
            {
                Flush();
                current = match.Groups["flag"].Value;
                _ = builder.Append(match.Groups["text"].Value.Trim());
                continue;
            }

            if (current is not null)
            {
                _ = builder.Append(' ').Append(line.Trim());
            }
        }

        Flush();
        return result;
    }

    /// <summary>The command's own description prose (between the usage line and <c>Options:</c>), one paragraph per blank line.</summary>
    public static string Description(string helpText)
    {
        var paragraphs = new List<string>();
        var builder = new StringBuilder();
        var afterUsage = false;

        foreach (var raw in CliText.NormalizeLineEndings(helpText ?? string.Empty).Split('\n'))
        {
            if (!afterUsage)
            {
                afterUsage = raw.StartsWith("Usage:", StringComparison.Ordinal);
                continue;
            }

            if (raw.StartsWith("Options:", StringComparison.Ordinal) || raw.StartsWith("Commands:", StringComparison.Ordinal))
            {
                break;
            }

            var line = raw.Trim();
            if (line.Length == 0)
            {
                if (builder.Length > 0)
                {
                    paragraphs.Add(builder.ToString());
                    builder.Clear();
                }

                continue;
            }

            _ = builder.Append(builder.Length > 0 ? " " : string.Empty).Append(line);
        }

        if (builder.Length > 0)
        {
            paragraphs.Add(builder.ToString());
        }

        return string.Join("\n", paragraphs);
    }

    [GeneratedRegex(@"^\s{1,8}(?<flag>--[a-z0-9][a-z0-9-]*)(?:\s{2,}(?<text>.*))?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex PipelineLine();

    // ------------------------------------------------------------------ splunk

    private static IReadOnlyList<WizardStep> Splunk(IReadOnlyList<WizardStep> steps, string help)
    {
        var all = steps.SelectMany(s => s.Fields).ToList();
        WizardField? ByFlag(string flag) => all.FirstOrDefault(f => string.Equals(f.Flag, flag, StringComparison.Ordinal));

        var o11y = ByFlag("--o11y");
        var logs = ByFlag("--logs");
        var enterprise = ByFlag("--enterprise");

        // A CLI that no longer has the pipeline switches is not the one this layout describes: leave the generated pages alone.
        if (o11y is null && logs is null && enterprise is null)
        {
            return steps;
        }

        var cli = PipelineText(help);
        string Says(string flag) => cli.TryGetValue(flag, out var text) ? text : string.Empty;

        var cards = new List<WizardGuideCard>();
        if (o11y is not null)
        {
            cards.Add(new WizardGuideCard
            {
                FieldId = o11y.Id,
                Title = "Splunk Observability Cloud",
                Summary = "Sends DefenseClaw's traces and metrics (and, if you choose, logs) to Splunk Observability Cloud over OTLP HTTP. Nothing runs on this machine.",
                CliSays = Says("--o11y"),
                Needs = new[]
                {
                    "A Splunk Observability Cloud access token with ingest permission. You type it on the next page; it never goes on the command line.",
                    "Your realm — the short region code in your Observability Cloud URL, such as us0, us1 or eu0.",
                },
                Links = new[]
                {
                    new WizardGuideLink("Create an access token", "https://help.splunk.com/en/splunk-observability-cloud/administer/authentication-and-security/authentication-tokens/org-access-tokens"),
                    new WizardGuideLink("Find your realm", "https://dev.splunk.com/observability/docs/realms_in_endpoints/"),
                },
                WillDo = "Writes a Splunk O11y destination to DefenseClaw's configuration and stores the token in ~/.defenseclaw/.env. It does not contact Splunk during setup.",
            });
        }

        if (logs is not null)
        {
            cards.Add(new WizardGuideCard
            {
                FieldId = logs.Id,
                Title = "Local Splunk (Docker)",
                Summary = "Runs a Splunk instance on this machine in Docker (Splunk Free mode) and sends DefenseClaw's logs to it over HEC, with dashboards you open at http://127.0.0.1:8000.",
                CliSays = Says("--logs"),
                Needs = new[]
                {
                    "Docker Desktop running with Linux containers. The CLI's Windows path certifies Pro, Enterprise or Education with the Hyper-V backend, and refuses the WSL 2 backend.",
                    "Your acceptance of the Splunk General Terms (a switch on the page after this one).",
                },
                Links = new[]
                {
                    new WizardGuideLink("Install Docker Desktop", DockerInstallUrl),
                    new WizardGuideLink("Splunk General Terms", SplunkTermsUrl),
                },
                WillDo = "Pulls and starts Splunk containers (and their volumes) on this machine, then saves the destination and reloads the gateway, which restarts it if it is running (the review says so). " +
                         "It is the one pipeline that starts something here, and it can take a few minutes the first time.",
                Requires = WizardGuideRequirement.Docker,
            });
        }

        if (enterprise is not null)
        {
            cards.Add(new WizardGuideCard
            {
                FieldId = enterprise.Id,
                Title = "Splunk Enterprise (HEC)",
                Summary = "Sends DefenseClaw's logs to a Splunk Enterprise or Splunk Cloud Platform HTTP Event Collector you already run. No Docker and nothing local.",
                CliSays = Says("--enterprise"),
                Needs = new[]
                {
                    "The HEC endpoint URL, for example https://splunk.example.com:8088/services/collector/event.",
                    "A HEC token with access to your index. You type it on the next page; it never goes on the command line.",
                },
                Links = new[]
                {
                    new WizardGuideLink("Set up the HTTP Event Collector", "https://help.splunk.com/en/splunk-enterprise/get-started/get-data-in/10.6/get-data-with-http-event-collector/set-up-and-use-http-event-collector-in-splunk-web"),
                },
                WillDo = "Writes a Splunk Enterprise destination and stores the token in ~/.defenseclaw/.env, then sends one best-effort HEC probe to your endpoint — " +
                         "turn on \"Skip test\" on that pipeline's page to skip the probe.",
            });
        }

        var guide = new WizardGuide
        {
            Intro = "Pick one or more pipelines. They are independent and can run together. Each one you pick adds its own page of fields, " +
                    "and the last page shows the exact command before anything runs.",
            Cards = cards,
        };

        var (gateField, gateValues) = GateOf(steps);
        var placed = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<WizardStep>();

        // A group that still has real subcommands keeps its own "pick a command" page first.
        result.AddRange(steps.Where(s => s.Fields.Any(f => f.IsPositional) && s.Fields.All(f => f.IsPositional)));

        var pipelineFields = new[] { o11y, logs, enterprise }.Where(f => f is not null).Select(f => f!).ToArray();
        foreach (var f in pipelineFields)
        {
            _ = placed.Add(f.Id);
        }

        result.Add(new WizardStep
        {
            Id = GuideStepId,
            Title = "Choose what to send",
            Subtitle = "Splunk has three independent pipelines. Choose the ones you want.",
            Fields = pipelineFields,
            VisibleWhenFieldId = gateField,
            VisibleWhenValues = gateValues,
            Guide = guide,
        });

        void Page(string id, string title, string subtitle, string? pipeline, string pipelineGate, params string[] flags)
        {
            var fields = new List<WizardField>();
            foreach (var flag in flags)
            {
                if (ByFlag(flag) is { } field && placed.Add(field.Id))
                {
                    fields.Add(field);
                }
            }

            if (fields.Count == 0)
            {
                return;
            }

            result.Add(new WizardStep
            {
                Id = id,
                Title = title,
                Subtitle = subtitle,
                Fields = fields,
                VisibleWhenFieldId = pipeline,
                VisibleWhenValues = pipeline is null ? Array.Empty<string>() : new[] { pipelineGate },
            });
        }

        Page(
            "o11y",
            "Splunk Observability Cloud",
            "Where to send traces and metrics. The access token is typed in this app and never reaches the command line.",
            o11y is null ? null : o11y.Id,
            ToggleValues.On,
            "--realm",
            "--access-token",
            "--app-name",
            "--traces",
            "--metrics",
            "--logs-export");

        Page(
            "local",
            "Local Splunk (Docker)",
            "Starts the bundled Splunk profile in Docker. Splunk's license terms must be accepted for the command to run.",
            logs is null ? null : logs.Id,
            ToggleValues.On,
            "--accept-splunk-license",
            "--refresh-bundle");

        Page(
            "enterprise",
            "Splunk Enterprise (HEC)",
            "Your remote HTTP Event Collector. The token is typed in this app and never reaches the command line.",
            enterprise is null ? null : enterprise.Id,
            ToggleValues.On,
            "--hec-endpoint",
            "--hec-token",
            "--skip-test");

        // Index, source and sourcetype are shared by the two HEC pipelines; one page, shown when either is chosen.
        if (logs is not null && enterprise is not null)
        {
            Page(
                "hec-destination",
                "HEC index, source and sourcetype",
                "Where the events land in Splunk. The defaults differ for local and enterprise; leave a field alone to use the CLI's default.",
                logs.Id + "|" + enterprise.Id,
                ToggleValues.On,
                "--index",
                "--source",
                "--sourcetype");
        }
        else if (logs is not null || enterprise is not null)
        {
            Page(
                "hec-destination",
                "HEC index, source and sourcetype",
                "Where the events land in Splunk. Leave a field alone to use the CLI's default.",
                (logs ?? enterprise)!.Id,
                ToggleValues.On,
                "--index",
                "--source",
                "--sourcetype");
        }

        // Whatever the installed CLI has beyond this layout (--disable, --non-interactive, anything added later) is still there.
        var rest = all.Where(f => !placed.Contains(f.Id) && !f.IsPositional).ToArray();
        if (rest.Length > 0)
        {
            result.Add(new WizardStep
            {
                Id = "more-options",
                Title = "More options",
                Subtitle = "Everything else this command accepts, straight from its help screen.",
                Fields = rest,
                VisibleWhenFieldId = gateField,
                VisibleWhenValues = gateValues,
            });
        }

        return result;
    }

    /// <summary>The gate a group's guided pages carry (<c>subcommand</c> = ""), so the new pages hide when another subcommand is picked.</summary>
    private static (string? Field, IReadOnlyList<string> Values) GateOf(IReadOnlyList<WizardStep> steps)
    {
        var gated = steps.FirstOrDefault(s => s.VisibleWhenFieldId is { Length: > 0 } && s.Fields.Any(f => !f.IsPositional));
        return gated is null ? (null, Array.Empty<string>()) : (gated.VisibleWhenFieldId, gated.VisibleWhenValues);
    }

    // ------------------------------------------------------------------ galileo

    private static IReadOnlyList<WizardStep> Galileo(IReadOnlyList<WizardStep> steps, string help)
    {
        if (steps.Count == 0)
        {
            return steps;
        }

        var cliSays = Description(help);

        // The key page exists only while the installed CLI still advertises GALILEO_API_KEY (see WizardSyntheticSecrets); without
        // it the guide must not promise a place to type one.
        var hasKeyPage = steps.SelectMany(s => s.Fields).Any(f => f.Id == WizardSyntheticSecrets.GalileoKeyFieldId);
        var keyNeed = hasKeyPage
            ? "A Galileo API key. You type it on the API key page of this wizard (or store it once with defenseclaw keys set GALILEO_API_KEY); it is never a command-line flag."
            : "A Galileo API key, supplied the way the CLI's own help (below) describes. It is never a command-line flag.";

        var guide = new WizardGuide
        {
            Intro = "Galileo is an AI evaluation and observability service. This setup sends DefenseClaw's OpenTelemetry traces to a Galileo project, " +
                    "so you can see each agent run there. Galileo receives traces only.",
            Cards = new[]
            {
                new WizardGuideCard
                {
                    Title = "Galileo trace export",
                    Summary = "A Galileo account, a project and Log stream to send into, and an API key.",
                    CliSays = cliSays,
                    Needs = new[]
                    {
                        keyNeed,
                        "The project name or ID, and the Log stream name or ID, the traces should land in. The CLI offers \"default\" for the Log stream when you run it by hand.",
                        "For a self-hosted Galileo: your console URL (the API address is worked out from it), or the exact OTLP traces endpoint if it cannot be.",
                    },
                    Links = new[]
                    {
                        new WizardGuideLink("Galileo documentation (API keys, projects, Log streams)", "https://docs.galileo.ai/"),
                    },
                    WillDo = "Writes a Galileo destination (traces only) to DefenseClaw's configuration. Keeping the key in ~/.defenseclaw/.env is the \"Save the key\" choice on the API key page; " +
                             "Preview (on the last page) shows what would be written without writing it. Setup itself only writes configuration; the test command, picked on the Command page, sends a content-free trace to confirm Galileo accepts it.",
                },
            },
        };

        var guideStep = new WizardStep
        {
            Id = GuideStepId,
            Title = "About Galileo",
            Subtitle = "What this sets up and what to have ready.",
            Guide = guide,
        };

        // The wording of the three fields that need explaining. Ids are the ones the generated pages use; one the
        // installed CLI no longer has is simply not touched.
        var reworded = new Dictionary<string, (string Label, string Help)>(StringComparer.Ordinal)
        {
            ["deployment"] = ("Deployment", "Cloud sends to Galileo's hosted service. Self-hosted sends to your own Galileo installation, which needs the console URL (or the exact trace endpoint) below."),
            ["project"] = ("Project", "The Galileo project the traces are filed under — its name or its ID, as the Galileo console shows it. Required."),
            ["logstream"] = ("Log stream", "The Log stream inside that project. A name or an ID. Required; \"default\" is the stream a new project starts with."),
            ["console-url"] = ("Console URL (self-hosted only)", "Your self-hosted Galileo console address, https:// only. The API endpoint is derived from it (console. becomes api.). Leave blank for Galileo Cloud."),
            ["trace-endpoint"] = ("Trace endpoint (self-hosted only)", "The exact OTLP HTTP traces endpoint, https:// without a query. Only needed when it cannot be derived from the console URL. Overrides the console URL."),
        };

        var result = new List<WizardStep> { guideStep };
        foreach (var step in steps)
        {
            if (step.Fields.Count == 0 || !step.Fields.Any(f => reworded.ContainsKey(f.Id)))
            {
                result.Add(step);
                continue;
            }

            result.Add(new WizardStep
            {
                Id = step.Id,
                Title = step.Title,
                Subtitle = step.Subtitle,
                Fields = step.Fields.Select(f => reworded.TryGetValue(f.Id, out var words) ? f.WithWording(words.Label, words.Help) : f).ToArray(),
                VisibleWhenFieldId = step.VisibleWhenFieldId,
                VisibleWhenValues = step.VisibleWhenValues,
                Guide = step.Guide,
            });
        }

        return result;
    }
}
