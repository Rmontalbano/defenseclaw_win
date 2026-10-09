namespace DefenseClaw.App.Services.Wizards;

/// <summary>
/// Builds a wizard's pages from one parsed help screen.
/// <para>
/// Two paths, and the difference is only how well the pages read:
/// </para>
/// <list type="bullet">
///   <item><b>Curated</b> — the flows an operator actually runs (hook connectors, guardrail,
///     the two scanners) get hand-ordered pages with the load-bearing decisions first.
///     Curated layouts name flags, but every named flag is looked up in the live
///     <c>--help</c>: a flag the installed CLI no longer has is silently dropped rather than
///     shipped into argv, so a curated layout cannot drift into an invalid command.</item>
///   <item><b>Generated</b> — everything else is chunked out of the parsed option list, with
///     booleans as toggles, <c>[a|b]</c> metavars as combos, and the rest as text.</item>
/// </list>
/// </summary>
public static partial class WizardStepFactory
{
    /// <summary>Generated pages hold at most this many fields before a new page starts.</summary>
    private const int FieldsPerGeneratedStep = 7;

    /// <summary>Builds the pages for a plain (non-group) target.</summary>
    public static (IReadOnlyList<WizardStep> Steps, bool IsCurated) Build(string target, ParsedHelp help)
    {
        ArgumentNullException.ThrowIfNull(help);

        var curated = Curate(target, help);
        return curated is not null
            ? (WithRemainder(curated, help, RemainderGate(target, help)), true)
            : (Generate(help), false);
    }

    /// <summary>
    /// Pages for a target whose real surface is its subcommands (<c>observability</c>,
    /// <c>webhook</c>, <c>provider</c>, <c>trusted-paths</c>, …). Page one picks the
    /// subcommand; every later page is gated on that pick, so the review screen only ever
    /// builds a command out of flags that subcommand actually accepts.
    /// </summary>
    /// <param name="target">
    /// The setup noun when the group has a curated subcommand (<c>provider</c>'s <c>add</c>); null builds every subcommand's pages from its help.
    /// </param>
    public static IReadOnlyList<WizardStep> BuildGroup(
        ParsedHelp help,
        IReadOnlyDictionary<string, ParsedHelp> subcommands,
        string? target = null)
    {
        ArgumentNullException.ThrowIfNull(help);
        ArgumentNullException.ThrowIfNull(subcommands);

        var steps = new List<WizardStep>();

        var choices = new List<WizardChoice>();
        if (help.SubcommandOptional)
        {
            choices.Add(new WizardChoice(string.Empty, "(no subcommand — run the guided default)"));
        }

        foreach (var command in help.Commands)
        {
            var label = command.Summary.Length > 0 ? $"{command.Name} — {command.Summary}" : command.Name;
            choices.Add(new WizardChoice(command.Name, label));
        }

        var commandField = new WizardField
        {
            Id = "subcommand",
            Label = "Command",
            Kind = WizardFieldKind.Choice,
            IsPositional = true,
            PositionalOrder = 0,
            IsRequired = !help.SubcommandOptional,
            Choices = choices,
            DefaultValue = help.SubcommandOptional ? string.Empty : help.Commands.FirstOrDefault()?.Name ?? string.Empty,
            Help = "Which subcommand of this setup group to run. The pages after this one change to match.",
        };

        steps.Add(new WizardStep
        {
            Id = "command",
            Title = "Command",
            Subtitle = "This setup target is a group; pick the subcommand to configure.",
            Fields = new[] { commandField },
        });

        // A group that also accepts its own flags (galileo, splunk, local-observability) only
        // accepts them when no subcommand follows, so those pages are gated on the empty pick.
        if (help.SubcommandOptional && help.Options.Count > 0)
        {
            steps.AddRange(Generate(help, "Guided setup", "subcommand", new[] { string.Empty }));
        }

        foreach (var (name, subHelp) in subcommands.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var gateValues = new[] { name };
            var prefix = name + ":";
            var order = 1;

            // `provider add` is the one subcommand with a curated layout: the base provider type picks which cloud's settings appear.
            if (CuratedSubcommand(target, name, subHelp, prefix, gateValues) is { Count: > 0 } curatedPages)
            {
                steps.AddRange(curatedPages);
                continue;
            }

            var fields = new List<WizardField>();
            foreach (var positional in subHelp.Positionals)
            {
                fields.Add(WizardFieldBuilder.FromPositional(positional, order++, prefix, "subcommand", gateValues));
            }

            foreach (var option in subHelp.Options)
            {
                var field = WizardFieldBuilder.From(option, "subcommand", gateValues, prefix);
                if (field is not null)
                {
                    fields.Add(field);
                }
            }

            if (fields.Count == 0)
            {
                steps.Add(new WizardStep
                {
                    Id = prefix + "empty",
                    Title = name,
                    Subtitle = subHelp.Summary.Length > 0 ? subHelp.Summary : "This subcommand takes no options.",
                    VisibleWhenFieldId = "subcommand",
                    VisibleWhenValues = gateValues,
                });
                continue;
            }

            steps.AddRange(Chunk(fields, name, subHelp.Summary, "subcommand", gateValues, prefix));
        }

        return steps;
    }

    /// <summary>Chunks every parsed option of a flat target into pages.</summary>
    private static IReadOnlyList<WizardStep> Generate(
        ParsedHelp help,
        string title = "Options",
        string? gateFieldId = null,
        IReadOnlyList<string>? gateValues = null)
    {
        var fields = new List<WizardField>();
        var order = 1;

        foreach (var positional in help.Positionals)
        {
            fields.Add(WizardFieldBuilder.FromPositional(positional, order++, null, gateFieldId, gateValues));
        }

        foreach (var option in help.Options)
        {
            var field = WizardFieldBuilder.From(option, gateFieldId, gateValues);
            if (field is not null)
            {
                fields.Add(field);
            }
        }

        return fields.Count == 0
            ? new[]
            {
                new WizardStep
                {
                    Id = "no-options",
                    Title = title,
                    Subtitle = "This command takes no options — review and run it.",
                    VisibleWhenFieldId = gateFieldId,
                    VisibleWhenValues = gateValues ?? Array.Empty<string>(),
                },
            }
            : Chunk(fields, title, help.Summary, gateFieldId, gateValues, string.Empty);
    }

    private static IReadOnlyList<WizardStep> Chunk(
        IReadOnlyList<WizardField> fields,
        string title,
        string subtitle,
        string? gateFieldId,
        IReadOnlyList<string>? gateValues,
        string idPrefix)
    {
        var steps = new List<WizardStep>();
        var pages = (fields.Count + FieldsPerGeneratedStep - 1) / FieldsPerGeneratedStep;

        for (var page = 0; page < pages; page++)
        {
            var slice = fields.Skip(page * FieldsPerGeneratedStep).Take(FieldsPerGeneratedStep).ToArray();
            steps.Add(new WizardStep
            {
                Id = $"{idPrefix}page-{page + 1}",
                Title = pages == 1 ? title : $"{title} ({page + 1}/{pages})",
                Subtitle = page == 0 ? subtitle : string.Empty,
                Fields = slice,
                VisibleWhenFieldId = gateFieldId,
                VisibleWhenValues = gateValues ?? Array.Empty<string>(),
            });
        }

        return steps;
    }

    /// <summary>Returns hand-ordered pages for the flows worth curating, or null.</summary>
    private static IReadOnlyList<WizardStep>? Curate(string target, ParsedHelp help)
    {
        switch (target)
        {
            case "guardrail":
                return GuardrailSteps(help);

            case "llm":
                return LlmSteps(help);

            case "skill-scanner":
                return SkillScannerSteps(help);

            case "mcp-scanner":
                return McpScannerSteps(help);

            default:
                // Shape, not name: every hook/lifecycle connector shares this option set, so
                // claude-code, codex, cursor and every future connector get the same layout
                // without this file having to learn their names.
                return IsHookConnector(help) ? HookConnectorSteps(help) : null;
        }
    }

    private static bool IsHookConnector(ParsedHelp help) =>
        help.Option("--mode") is not null &&
        help.Option("--fail-mode") is not null &&
        help.Option("--rule-pack") is not null;

    private static IReadOnlyList<WizardStep> HookConnectorSteps(ParsedHelp help) => Compact(
        Step(
            "enforcement",
            "Enforcement",
            "Observe records only. Action returns a deny verdict from the agent's own permission flow.",
            help,
            "--mode",
            "--fail-mode",
            "--human-approval",
            "--hilt-min-severity",
            "--block-message"),
        Step(
            "rules",
            "Rules and judge",
            "Per-connector overrides. Leave a field unchanged to inherit the global setting.",
            help,
            "--rule-pack",
            "--rule-pack-dir",
            "--enable-judge",
            "--judge-hook-connectors"),
        Step(
            "apply",
            "Scope and apply",
            "How the change is written and whether the gateway is bounced to pick it up.",
            help,
            "--replace",
            "--workspace",
            // --with-local-stack is deliberately absent: WizardWindowsPolicy removes it (the CLI crashes
            // after saving when it is used on Windows) and its default is already "off".
            "--restart",
            "--yes"));

    private static IReadOnlyList<WizardStep> SkillScannerSteps(ParsedHelp help) => Compact(
        Step(
            "analyzers",
            "Analyzers",
            "Which passes skill-scanner runs over a skill.",
            help,
            "--use-llm",
            "--use-behavioral",
            "--enable-meta",
            "--use-trigger",
            "--use-virustotal",
            "--use-aidefense"),
        Step(
            "llm",
            "LLM analyzer",
            "Shared with the MCP, plugin and guardrail scanners through the unified llm: block.",
            help,
            "--llm-provider",
            "--llm-model",
            "--llm-consensus-runs"),
        Step(
            "policy",
            "Policy and apply",
            "How findings are graded, and whether connectivity is verified afterwards.",
            help,
            "--policy",
            "--lenient",
            "--verify",
            "--non-interactive"));

    private static IReadOnlyList<WizardStep> McpScannerSteps(ParsedHelp help) => Compact(
        Step(
            "analyzers",
            "Analyzers and scope",
            "Which analyzers run, and which parts of an MCP server they read.",
            help,
            "--analyzers",
            "--scan-prompts",
            "--scan-resources",
            "--scan-instructions"),
        Step(
            "llm",
            "LLM analyzer",
            "Shared with the skill and plugin scanners through the unified llm: block.",
            help,
            "--llm-provider",
            "--llm-model"),
        Step(
            "apply",
            "Apply",
            "Whether connectivity is verified after the change is written.",
            help,
            "--verify",
            "--non-interactive"));

    /// <summary>
    /// Builds one curated page. Flags absent from the installed CLI's help are dropped, so a
    /// curated layout degrades to fewer fields instead of emitting a flag the binary would
    /// reject.
    /// </summary>
    private static WizardStep Step(string id, string title, string subtitle, ParsedHelp help, params string[] flags)
    {
        var fields = new List<WizardField>();
        foreach (var flag in flags)
        {
            if (help.Option(flag) is { } option && WizardFieldBuilder.From(option) is { } field)
            {
                fields.Add(field);
            }
        }

        return new WizardStep
        {
            Id = id,
            Title = title,
            Subtitle = subtitle,
            Fields = fields,
        };
    }

    /// <summary>Drops pages the installed CLI left with nothing to show.</summary>
    private static IReadOnlyList<WizardStep> Compact(params WizardStep[] steps) =>
        steps.Where(s => s.Fields.Count > 0).ToArray();

    /// <summary>
    /// Curated pages plus a trailing "More options" page holding every flag the curation did
    /// not place. Keeps the hand-ordered flows readable without ever hiding CLI surface.
    /// </summary>
    private static IReadOnlyList<WizardStep> WithRemainder(
        IReadOnlyList<WizardStep> curated,
        ParsedHelp help,
        (string? FieldId, IReadOnlyList<string> Values) gate = default)
    {
        ArgumentNullException.ThrowIfNull(curated);
        ArgumentNullException.ThrowIfNull(help);

        var placed = curated
            .SelectMany(s => s.Fields)
            .Select(f => f.Flag)
            .Where(f => f is { Length: > 0 })
            .Select(f => f!)
            .ToHashSet(StringComparer.Ordinal);

        var remaining = new List<WizardField>();
        foreach (var option in help.Options)
        {
            if (placed.Contains(option.Flag))
            {
                continue;
            }

            if (WizardFieldBuilder.From(option) is { } field)
            {
                remaining.Add(field);
            }
        }

        if (remaining.Count == 0)
        {
            return curated;
        }

        var steps = new List<WizardStep>(curated);
        steps.AddRange(Chunk(
            remaining,
            "More options",
            "Everything else this command accepts, straight from its help screen.",
            gate.FieldId,
            gate.Values,
            "more-"));

        return steps;
    }
}
